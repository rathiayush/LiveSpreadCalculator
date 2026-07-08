import { useSyncExternalStore } from 'react'
import * as signalR from '@microsoft/signalr'
import { api } from '../api'
import { getToken, clearSession } from '../auth'

// SignalR doesn't surface an expired/invalid JWT as a typed error - an unauthorized negotiate or
// WebSocket handshake just comes back as a generic Error whose message happens to mention the
// status. Used below so a token dying while someone is just sitting on a live page (no REST call
// to trip authFetch's 401 handling - see api.js) still flips the whole app back to the login
// screen immediately, instead of only on the next button click that happens to hit the API.
function isAuthError(err) {
  const msg = String(err?.message ?? err ?? '')
  return /\b401\b/.test(msg) || /unauthorized/i.test(msg)
}

// Why this file exists (replaces the old useSpreadHub.js hook):
//
// The previous version put spreads/ranking/triggerEvents/connectionState in a useState inside
// AppShell (the top of the component tree, always mounted). spreadUpdate pushes arrive roughly
// every 250-300ms for the whole universe (see the comment on connection.on('spreadUpdate', ...)
// below) - so every single tick called setState on AppShell, which re-rendered AppShell *and
// whatever page was currently mounted*, even pages that never touch spread data (Symbols,
// Triggers, Trigger Activity). That's the main source of the reported lag: the UI was doing full
// React work several times a second no matter which page you were looking at.
//
// This version keeps the exact same data (and the exact same SignalR event names/shapes) but
// stores it in plain module-level variables instead of React state, and exposes each slice via
// its own useSyncExternalStore-backed hook. The connection itself still lives for the whole app
// session (nothing conceptually changes there - AppShell was always mounted for the app's whole
// lifetime anyway), but now a tick only causes a re-render in components that are both (a)
// currently mounted and (b) actually calling the corresponding hook. Navigate off Live Spreads
// and SpreadGrid unmounts, so nobody calls useLiveSpreads() any more, so those same ticks become
// a cheap object write with zero React/DOM work anywhere - which is the "take benefit of not
// being on Live Spreads" the perf review asked for.

let spreadsMap = {}
let rankingList = []
let triggerEventsList = []
let connectionState = 'connecting'
// Live open positions/PNL - see LivePositionsHostedService on the backend, which recomputes and
// pushes this roughly every 750ms while any position is open (reacting instantly to order
// postbacks in between). [] means "none open" just as much as "not loaded yet" - same as
// spreadsMap/rankingList, there's no separate loading flag for this slice either.
let positionsList = []
// Last known error fetching positions (seed or an explicit refreshPositions() call) - null once
// a fetch/push succeeds. Kept separate from positionsList so a transient fetch failure doesn't
// have to also decide whether to blank out data that's still perfectly good to show.
let positionsError = null
// Non-null once the backend has told us (via "kiteAuthInvalidated" - see
// KiteAuthFailureHandler/SignalRKiteAuthNotifier) that a real Kite API call failed with a
// TokenException mid-session. Distinct from the one-time GET /api/auth/status check App.jsx
// does on load - this is how the app finds out *live*, without waiting for a page reload, that
// the token died partway through the day.
let kiteAuthInvalidReason = null

const spreadsListeners = new Set()
const rankingListeners = new Set()
const triggerListeners = new Set()
const connectionListeners = new Set()
const kiteAuthListeners = new Set()
const positionsListeners = new Set()
const positionsErrorListeners = new Set()

function notify(listeners) {
  for (const listener of listeners) listener()
}

function subscribe(listeners) {
  return (onStoreChange) => {
    // Lazily open the hub the first time any component actually subscribes. Those components only
    // mount once the user is logged in (App.jsx gates the whole app behind auth), so by the time
    // this runs there's a JWT for accessTokenFactory to send - which is why the old module-load
    // ensureConnection() call was removed (it would have fired before login and been rejected).
    ensureConnection()
    listeners.add(onStoreChange)
    return () => listeners.delete(onStoreChange)
  }
}

const subscribeSpreads = subscribe(spreadsListeners)
const subscribeRanking = subscribe(rankingListeners)
const subscribeTriggers = subscribe(triggerListeners)
const subscribeConnection = subscribe(connectionListeners)
const subscribeKiteAuth = subscribe(kiteAuthListeners)
const subscribePositions = subscribe(positionsListeners)
const subscribePositionsError = subscribe(positionsErrorListeners)

const getSpreadsSnapshot = () => spreadsMap
const getRankingSnapshot = () => rankingList
const getTriggersSnapshot = () => triggerEventsList
const getConnectionSnapshot = () => connectionState
const getKiteAuthSnapshot = () => kiteAuthInvalidReason
const getPositionsSnapshot = () => positionsList
const getPositionsErrorSnapshot = () => positionsError

let connection = null

// Whether the Positions & PNL page currently wants live updates - set by joinPositionsView/
// leavePositionsView (called from PositionsPage's mount/unmount). Kept as its own flag, separate
// from just invoking the hub method once, because a SignalR reconnect gets a brand new connection
// on the server (see SpreadHub.JoinPositions) - this flag is what lets connection.start()/
// onreconnected below re-join automatically so the backend's LivePositionsHostedService doesn't
// silently stop pushing PNL after a network blip just because the server-side "who's watching"
// state reset.
let positionsViewerWanted = false

// Best-effort: only actually invokes when the hub is currently connected. If it isn't (still
// starting up, or mid-reconnect), the connection.start()/onreconnected handlers below re-check
// positionsViewerWanted once they complete and invoke JoinPositions again then - so a join
// requested before the connection is ready is never silently lost.
function invokeIfConnected(methodName) {
  if (connection && connection.state === signalR.HubConnectionState.Connected) {
    connection.invoke(methodName).catch((err) => console.error(`[spreadHub] ${methodName} failed:`, err))
  }
}

// Runs exactly once, the first time this module is imported anywhere (ES modules are singletons)
// - not tied to any component's mount/unmount, so there's no StrictMode double-connect concern
// and no risk of a page re-render tearing the connection down.
function ensureConnection() {
  if (connection) return connection

  connection = new signalR.HubConnectionBuilder()
    // accessTokenFactory supplies the JWT for the hub. SignalR appends it as ?access_token=... on
    // the negotiate/WebSocket URL (a browser can't set an Authorization header on a WS handshake),
    // which the backend's JwtBearer OnMessageReceived reads for /hubs paths - see Program.cs.
    .withUrl('/hubs/spreads', { accessTokenFactory: () => getToken() ?? '' })
    .withAutomaticReconnect()
    .build()

  // Seeds spreads/ranking once from REST so pages aren't empty while waiting for the first push.
  // These are logged (not silently swallowed) specifically so "why is the page empty?" is
  // answerable from the browser console: a genuinely-empty backend (no configured spreads yet,
  // or no ranking computed yet) resolves fine with an empty array/null and logs nothing; a real
  // fetch failure (expired JWT -> 401, network error, 500) lands here and prints exactly why.
  api.getSpreadSnapshot()
    .then((initial) => {
      if (!initial) return
      const next = { ...spreadsMap }
      for (const snapshot of initial) next[snapshot.spreadId] = snapshot
      spreadsMap = next
      notify(spreadsListeners)
    })
    .catch((err) => console.error('[spreadHub] failed to seed spread snapshot:', err))

  api.getSpreadRanking()
    .then((initial) => {
      if (!initial) return
      rankingList = initial
      notify(rankingListeners)
    })
    .catch((err) => console.error('[spreadHub] failed to seed ranking:', err))

  // Seeds the Trigger Activity feed from the server's in-memory history (see
  // ITriggerActivityStore) so a browser refresh no longer loses everything since the page
  // loaded - previously this list only ever grew from live "triggerFired"/"basketExecuted"
  // pushes and started from empty on every reload, even though the fires themselves were real
  // and already resolved server-side. Merges rather than overwrites, newest first: anything
  // already here arrived via a live push in the brief window between connection.start() and
  // this request resolving, so it's newer than the snapshot and kept in front; duplicates (the
  // same fireId in both) are dropped from the seeded side, not the live one.
  api.getTriggerActivity()
    .then((initial) => {
      if (!initial) return
      const existingIds = new Set(triggerEventsList.map((e) => e.fireId))
      const seeded = initial.filter((e) => !existingIds.has(e.fireId))
      triggerEventsList = [...triggerEventsList, ...seeded].slice(0, 50)
      notify(triggerListeners)
    })
    .catch((err) => console.error('[spreadHub] failed to seed trigger activity:', err))

  // Seeds Positions & PNL from a plain REST call so the page isn't empty while waiting for the
  // first "positionsUpdate" push (see LivePositionsHostedService on the backend).
  api.getPositions()
    .then((result) => {
      positionsList = result.success ? result.positions : []
      positionsError = result.success ? null : (result.errorMessage || 'Could not load positions.')
      notify(positionsListeners)
      notify(positionsErrorListeners)
    })
    .catch((err) => {
      positionsError = err.message
      notify(positionsErrorListeners)
      console.error('[spreadHub] failed to seed positions:', err)
    })

  // Each incoming message is already a full batch of everything that changed in one
  // recalculation cycle (see Engine:SpreadRecalculationIntervalMs / SpreadCalculationHostedService
  // on the backend) - no need to throttle again here.
  connection.on('spreadUpdate', (changed) => {
    const next = { ...spreadsMap }
    for (const snapshot of changed) next[snapshot.spreadId] = snapshot
    spreadsMap = next
    notify(spreadsListeners)
  })

  // Refreshed roughly every Engine:RankingIntervalMinutes (see SpreadRankingHostedService).
  connection.on('rankingUpdate', (rankedList) => {
    rankingList = rankedList
    notify(rankingListeners)
  })

  // Each fired trigger gets `legs: null` until the matching "basketExecuted" push (below)
  // arrives - see TriggerFeed, which renders that as "placing order...". The two pushes come
  // from different backend services at different times (this one the instant the rule crosses
  // its threshold, the other only after OrderDispatchHostedService's margin check and order
  // placement round-trips finish), correlated by fireId.
  connection.on('triggerFired', (evt) => {
    triggerEventsList = [
      { ...evt, legs: null, marginBlocked: false, blockedReason: null, executedAt: null, underlying: null, orderKind: null },
      ...triggerEventsList,
    ].slice(0, 50)
    notify(triggerListeners)
  })

  // Reports what was actually bought/sold for a fired trigger - underlying, order kind
  // (Basket/Single leg/None/Unknown), which leg(s) succeeded, which were rejected and why, or
  // that nothing was placed at all because the pre-trade margin check came up short
  // (marginBlocked) - see OrderDispatchHostedService.DispatchAsync on the backend. Always sent
  // regardless of outcome, so the feed never gets stuck on "placing order...".
  connection.on('basketExecuted', (exec) => {
    triggerEventsList = triggerEventsList.map((e) => (e.fireId === exec.fireId
      ? {
        ...e,
        legs: exec.legs,
        marginBlocked: exec.marginBlocked,
        blockedReason: exec.blockedReason,
        executedAt: exec.executedAt,
        underlying: exec.underlying,
        orderKind: exec.orderKind,
      }
      : e))
    notify(triggerListeners)
  })

  // Pushed roughly every 750ms while any position is open (see LivePositionsHostedService on
  // the backend) - already a full replacement list, same idea as spreadUpdate/rankingUpdate, so
  // this just overwrites rather than merging.
  connection.on('positionsUpdate', (positions) => {
    positionsList = positions
    positionsError = null
    notify(positionsListeners)
    notify(positionsErrorListeners)
  })

  // Pushed the instant KiteAuthFailureHandler sees a TokenException from any real Kite call
  // (order placement, margin check, order status/cancel) - see that class on the backend. The
  // session is already cleared server-side by the time this arrives, so GET /api/auth/status
  // would report the same thing on next load anyway; this just means the UI doesn't have to
  // wait for that reload to tell you your session died.
  connection.on('kiteAuthInvalidated', (payload) => {
    kiteAuthInvalidReason = payload?.reason || 'Your Kite session is no longer valid.'
    notify(kiteAuthListeners)
  })

  connection.onreconnecting(() => {
    connectionState = 'reconnecting'
    notify(connectionListeners)
  })
  connection.onreconnected(() => {
    connectionState = 'connected'
    notify(connectionListeners)
    // A reconnect gets a brand new connection (and Context.Items) on the server, so whatever
    // this connection had joined before is gone server-side - re-join now if the Positions & PNL
    // page is still open, otherwise LivePositionsHostedService would silently stay paused for
    // this viewer after a network blip even though the page never actually left.
    if (positionsViewerWanted) invokeIfConnected('JoinPositions')
  })
  connection.onclose((err) => {
    connectionState = 'disconnected'
    notify(connectionListeners)
    // Automatic reconnect gave up (it retries a handful of times, then closes for good) - if
    // that's because the token is no longer valid, clearSession() notifies every useAuth()
    // subscriber (see auth.js), which is what flips App.jsx from the dashboard back to the login
    // screen app-wide, the same way authFetch's 401 handling does for REST calls.
    if (isAuthError(err)) clearSession()
  })

  connection
    .start()
    .then(() => {
      connectionState = 'connected'
      notify(connectionListeners)
      // Covers the case where joinPositionsView() was called (PositionsPage mounted) before this
      // initial connect finished - invokeIfConnected() inside it would have been a no-op then.
      if (positionsViewerWanted) invokeIfConnected('JoinPositions')
    })
    .catch((err) => {
      console.error('[spreadHub] hub connection failed to start:', err)
      connectionState = 'disconnected'
      notify(connectionListeners)
      if (isAuthError(err)) clearSession()
    })

  return connection
}

// Tears the hub down on logout so a connection authenticated as the previous user doesn't linger,
// and clears the seeded slices so the next user doesn't briefly see the old one's data. A fresh
// ensureConnection() (with the new token) runs automatically the next time a hook subscribes.
export function stopConnection() {
  if (connection) {
    const closing = connection
    connection = null
    closing.stop().catch(() => {})
  }
  spreadsMap = {}
  rankingList = []
  triggerEventsList = []
  kiteAuthInvalidReason = null
  positionsList = []
  positionsError = null
  // Not carried over to the next login - if Positions & PNL happens to still be mounted across a
  // logout/login, its own effect re-runs joinPositionsView() on the fresh connection anyway.
  positionsViewerWanted = false
  connectionState = 'disconnected'
  notify(spreadsListeners)
  notify(rankingListeners)
  notify(triggerListeners)
  notify(connectionListeners)
  notify(positionsListeners)
  notify(positionsErrorListeners)
}

// spreadId -> latest SpreadSnapshot. High frequency - only call this from a component that's
// actually showing live per-spread values (currently just SpreadGrid/SpreadsPage).
export function useLiveSpreads() {
  return useSyncExternalStore(subscribeSpreads, getSpreadsSnapshot)
}

// Drops any spreadsMap entry whose id is no longer in the current spread definition list.
// spreadsMap only ever gets entries added (from "spreadUpdate" pushes) - nothing removes one
// when the underlying spread is deleted via Configured Spreads, so without this it grows a stale
// key per deleted-then-recreated spread for the lifetime of the tab (bounded by universe size in
// practice, but unbounded over a long enough session of add/delete churn). Mirrors the backend's
// own self-prune of _lastValues in SpreadCalculationHostedService. Call this any time the current
// spread id list is freshly known - see AppShell's loadSpreads().
export function pruneSpreads(currentIds) {
  const idSet = currentIds instanceof Set ? currentIds : new Set(currentIds)
  const staleKeys = Object.keys(spreadsMap).filter((id) => !idSet.has(id))
  if (staleKeys.length === 0) return

  const next = { ...spreadsMap }
  for (const id of staleKeys) delete next[id]
  spreadsMap = next
  notify(spreadsListeners)
}

// Full spread list sorted by |spread %| descending. Low frequency (every few minutes).
export function useSpreadRanking() {
  return useSyncExternalStore(subscribeRanking, getRankingSnapshot)
}

// Rolling feed of the most recent fired triggers (client-side only, capped at 50).
export function useTriggerEvents() {
  return useSyncExternalStore(subscribeTriggers, getTriggersSnapshot)
}

export function clearTriggerEvents() {
  triggerEventsList = []
  notify(triggerListeners)
}

export function useSpreadHubConnectionState() {
  return useSyncExternalStore(subscribeConnection, getConnectionSnapshot)
}

// Null when the session is fine (or hasn't been checked yet); a reason string once the backend
// has pushed "kiteAuthInvalidated". App.jsx uses this to flip the auth tag and prompt re-login
// immediately, without waiting for a page reload.
export function useKiteAuthInvalidated() {
  return useSyncExternalStore(subscribeKiteAuth, getKiteAuthSnapshot)
}

// Called once the user re-completes the Kite login flow (see App.jsx's kiteLogin=success
// handling), so a stale invalidation notice doesn't keep showing after they've already fixed it.
export function clearKiteAuthInvalidated() {
  kiteAuthInvalidReason = null
  notify(kiteAuthListeners)
}

// Live open positions/PNL - see LivePositionsHostedService on the backend. Updates roughly every
// 750ms while any position is open; [] both before the first load and once the account is flat.
export function usePositions() {
  return useSyncExternalStore(subscribePositions, getPositionsSnapshot)
}

// Last known error loading positions (seed or an explicit refreshPositions() call), or null.
export function usePositionsError() {
  return useSyncExternalStore(subscribePositionsError, getPositionsErrorSnapshot)
}

// Forces an immediate REST re-fetch instead of waiting for the next live push - wired to the
// Positions & PNL page's manual refresh button. Updates the same shared store, so every
// usePositions()/usePositionsError() subscriber sees the result, not just the caller.
export function refreshPositions() {
  return api.getPositions()
    .then((result) => {
      positionsList = result.success ? result.positions : positionsList
      positionsError = result.success ? null : (result.errorMessage || 'Could not load positions.')
      notify(positionsListeners)
      notify(positionsErrorListeners)
      return result
    })
    .catch((err) => {
      positionsError = err.message
      notify(positionsErrorListeners)
      throw err
    })
}

// Tells the backend a browser tab now has the Positions & PNL page open, so
// LivePositionsHostedService resumes its Kite REST resync + live PNL push for as long as at
// least one tab has called this - see PositionsViewerTracker/SpreadHub.JoinPositions on the
// backend. Call from that page's mount; call leavePositionsView() on its unmount. Safe to call
// even before the SignalR connection has finished starting (invokeIfConnected no-ops until
// then, and positionsViewerWanted makes connection.start()/onreconnected pick it up once ready).
export function joinPositionsView() {
  positionsViewerWanted = true
  ensureConnection()
  invokeIfConnected('JoinPositions')
}

// Counterpart to joinPositionsView() - call on the Positions & PNL page's unmount. Does NOT stop
// the shared SignalR connection itself (other pages/hooks may still be using it) - only tells the
// backend this tab no longer needs live position pushes.
export function leavePositionsView() {
  positionsViewerWanted = false
  invokeIfConnected('LeavePositions')
}
