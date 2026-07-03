import { useSyncExternalStore } from 'react'
import * as signalR from '@microsoft/signalr'
import { api } from '../api'

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

function notify(listeners) {
  for (const listener of listeners) listener()
}

function subscribe(listeners) {
  return (onStoreChange) => {
    listeners.add(onStoreChange)
    return () => listeners.delete(onStoreChange)
  }
}

const subscribeSpreads = subscribe(spreadsListeners)
const subscribeRanking = subscribe(rankingListeners)
const subscribeTriggers = subscribe(triggerListeners)
const subscribeConnection = subscribe(connectionListeners)
const subscribeKiteAuth = subscribe(kiteAuthListeners)

const getSpreadsSnapshot = () => spreadsMap
const getRankingSnapshot = () => rankingList
const getTriggersSnapshot = () => triggerEventsList
const getConnectionSnapshot = () => connectionState
const getKiteAuthSnapshot = () => kiteAuthInvalidReason

let connection = null

// Runs exactly once, the first time this module is imported anywhere (ES modules are singletons)
// - not tied to any component's mount/unmount, so there's no StrictMode double-connect concern
// and no risk of a page re-render tearing the connection down.
function ensureConnection() {
  if (connection) return connection

  connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/spreads')
    .withAutomaticReconnect()
    .build()

  // Seeds spreads/ranking once from REST so pages aren't empty while waiting for the first push.
  api.getSpreadSnapshot()
    .then((initial) => {
      if (!initial) return
      const next = { ...spreadsMap }
      for (const snapshot of initial) next[snapshot.spreadId] = snapshot
      spreadsMap = next
      notify(spreadsListeners)
    })
    .catch(() => {})

  api.getSpreadRanking()
    .then((initial) => {
      if (!initial) return
      rankingList = initial
      notify(rankingListeners)
    })
    .catch(() => {})

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
    .catch(() => {})

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
  })
  connection.onclose(() => {
    connectionState = 'disconnected'
    notify(connectionListeners)
  })

  connection
    .start()
    .then(() => {
      connectionState = 'connected'
      notify(connectionListeners)
    })
    .catch(() => {
      connectionState = 'disconnected'
      notify(connectionListeners)
    })

  return connection
}

ensureConnection()

// spreadId -> latest SpreadSnapshot. High frequency - only call this from a component that's
// actually showing live per-spread values (currently just SpreadGrid/SpreadsPage).
export function useLiveSpreads() {
  return useSyncExternalStore(subscribeSpreads, getSpreadsSnapshot)
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
