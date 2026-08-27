import { getToken, clearSession } from './auth'

const jsonHeaders = { 'Content-Type': 'application/json' }

// Merges the Authorization: Bearer <jwt> header (when logged in) into whatever headers a call
// already sets. Every API call now goes through fetch wrappers below that spread this in, so the
// token rides along on all of them - the backend rejects anything without it.
function authHeaders(extra) {
  const token = getToken()
  return token ? { ...extra, Authorization: `Bearer ${token}` } : { ...extra }
}

// Wraps window.fetch to attach the token and to treat a 401 as "session gone" - clears the stored
// token so App.jsx flips back to the login screen instead of leaving the user staring at a dead UI
// (this is exactly what happens when a token hits its 3-day expiry mid-use).
async function authFetch(url, options = {}) {
  const response = await fetch(url, { ...options, headers: authHeaders(options.headers) })
  if (response.status === 401) {
    clearSession()
    throw new Error('Your session has expired. Please log in again.')
  }
  return response
}

async function handle(response) {
  if (!response.ok) {
    const text = await response.text().catch(() => '')
    throw new Error(text || `Request failed: ${response.status}`)
  }
  if (response.status === 204) return null
  return response.json()
}

// Exchanges email/password for a JWT. The only call that doesn't need an existing token; on success
// the caller stores the returned session via auth.setSession. Uses plain fetch (not authFetch) so a
// wrong password surfaces its own 401 message instead of being swallowed as "session expired".
export async function login(email, password) {
  const response = await fetch('/api/auth/token', {
    method: 'POST',
    headers: jsonHeaders,
    body: JSON.stringify({ email, password }),
  })
  if (!response.ok) {
    let msg = 'Invalid email or password.'
    try {
      const body = await response.json()
      if (body?.error) msg = body.error
    } catch { /* keep default message */ }
    throw new Error(msg)
  }
  return response.json() // { token, expiresUtc, email }
}

// All calls use relative paths - see vite.config.js for the dev proxy to the Api project.
export const api = {
  getSpreads: () => authFetch('/api/spreads').then(handle),
  // Latest known value for every spread (seeds the dashboard before the first SignalR push).
  getSpreadSnapshot: () => authFetch('/api/spreads/snapshot').then(handle),
  // Most recent ranking (spreads sorted by |spread %| descending) - see
  // Engine:RankingIntervalMinutes and SpreadRankingHostedService.
  getSpreadRanking: () => authFetch('/api/spreads/ranking').then(handle),
  createSpread: (spread) =>
    authFetch('/api/spreads', { method: 'POST', headers: jsonHeaders, body: JSON.stringify(spread) }).then(handle),
  deleteSpread: (id) => authFetch(`/api/spreads/${encodeURIComponent(id)}`, { method: 'DELETE' }).then(handle),
  // Stored hourly spread history (datetime + buy/sell spread values), oldest first - powers the
  // "Live Spread" chart popup. Backend defaults to the last 30 days; pass ISO strings to override.
  getSpreadHistory: (id, from, to) => {
    const params = new URLSearchParams()
    if (from) params.set('from', from)
    if (to) params.set('to', to)
    const qs = params.toString()
    return authFetch(`/api/spreads/${encodeURIComponent(id)}/history${qs ? `?${qs}` : ''}`).then(handle)
  },

  getTriggers: (spreadId) =>
    authFetch(`/api/triggers${spreadId ? `?spreadId=${encodeURIComponent(spreadId)}` : ''}`).then(handle),
  // Recent fired triggers + execution outcomes, newest first - seeds the Trigger Activity feed
  // before the first SignalR push, same idea as getSpreadSnapshot for Live Spreads. Held in
  // memory server-side (see ITriggerActivityStore) until the backend restarts.
  getTriggerActivity: () => authFetch('/api/triggers/activity').then(handle),
  // Wipes the server-side Trigger Activity history (not just this browser's view) - see
  // TriggerActivityPage's Clear button.
  clearTriggerActivity: () => authFetch('/api/triggers/activity', { method: 'DELETE' }).then(handle),
  createTrigger: (rule) =>
    authFetch('/api/triggers', { method: 'POST', headers: jsonHeaders, body: JSON.stringify(rule) }).then(handle),
  // Patches an existing rule in place (quantity/threshold/thresholdIsPercent etc.) rather than
  // delete+recreate, so its id, enabled/armed state, and fire history are untouched - works on a
  // trigger whether it's currently armed, fired, or disabled. Needs a matching
  // PUT /api/triggers/{id} on the backend - see TriggerList's "Edit" action.
  updateTrigger: (id, patch) =>
    authFetch(`/api/triggers/${encodeURIComponent(id)}`, { method: 'PUT', headers: jsonHeaders, body: JSON.stringify(patch) }).then(handle),
  deleteTrigger: (id) => authFetch(`/api/triggers/${encodeURIComponent(id)}`, { method: 'DELETE' }).then(handle),
  deleteAllTriggers: () => authFetch('/api/triggers', { method: 'DELETE' }).then(handle),
  enableTrigger: (id) => authFetch(`/api/triggers/${encodeURIComponent(id)}/enable`, { method: 'POST' }).then(handle),
  disableTrigger: (id) => authFetch(`/api/triggers/${encodeURIComponent(id)}/disable`, { method: 'POST' }).then(handle),
  // Applies one threshold/quantity/product template across many spreads at once (e.g. the whole
  // built universe) instead of configuring each one by hand - see BulkTriggerTemplateService.
  applyBulkTemplate: (template) =>
    authFetch('/api/triggers/bulk', { method: 'POST', headers: jsonHeaders, body: JSON.stringify(template) }).then(handle),

  // Exit triggers (rules that CLOSE an existing position) - a completely separate backend store/
  // CSV file from the entry triggers above (see IExitTriggerRuleStore), reached at /api/exit-
  // triggers instead of /api/triggers. Same request/response shapes throughout - only the base
  // path differs - so these mirror the entry-trigger functions one for one.
  getExitTriggers: (spreadId) =>
    authFetch(`/api/exit-triggers${spreadId ? `?spreadId=${encodeURIComponent(spreadId)}` : ''}`).then(handle),
  createExitTrigger: (rule) =>
    authFetch('/api/exit-triggers', { method: 'POST', headers: jsonHeaders, body: JSON.stringify(rule) }).then(handle),
  updateExitTrigger: (id, patch) =>
    authFetch(`/api/exit-triggers/${encodeURIComponent(id)}`, { method: 'PUT', headers: jsonHeaders, body: JSON.stringify(patch) }).then(handle),
  deleteExitTrigger: (id) => authFetch(`/api/exit-triggers/${encodeURIComponent(id)}`, { method: 'DELETE' }).then(handle),
  enableExitTrigger: (id) => authFetch(`/api/exit-triggers/${encodeURIComponent(id)}/enable`, { method: 'POST' }).then(handle),
  disableExitTrigger: (id) => authFetch(`/api/exit-triggers/${encodeURIComponent(id)}/disable`, { method: 'POST' }).then(handle),

  // Builds the futures universe from Kite's live instrument master - every stock currently
  // listed for NFO futures, not a hand-maintained list. Resolves each symbol's near/far month
  // contract, writes App_Data/universe_futures.csv, and subscribes the live ticker to all of
  // them in one call. See UniverseBuilderService. maxSymbols caps the build to the first N
  // symbols (alphabetically); omit it to build every futures-listed symbol on the exchange
  // (roughly 200-220 as of mid-2026 - that's the whole exchange, not a limit in this code).
  // expiryOffset: 0 (default) = nearest two listed expiries (e.g. Jul/Aug); 1 = skip to the next
  // pair (Aug/Sep), etc. - see UniverseController.Build.
  buildUniverse: (maxSymbols, expiryOffset) => {
    const params = new URLSearchParams()
    if (maxSymbols) params.set('maxSymbols', maxSymbols)
    if (expiryOffset) params.set('expiryOffset', expiryOffset)
    const qs = params.toString()
    return authFetch(`/api/universe/build${qs ? `?${qs}` : ''}`, { method: 'POST' }).then(handle)
  },

  // Builds the MCX commodity arbitrage universe (silver/gold/crude/natgas calendar + cross-
  // contract spreads like SILVER vs SILVERM) from Kite's live MCX instrument master - see
  // UniverseBuilderService.BuildMcxAsync. Gated server-side by Mcx:Enabled: returns 409 (surfaced
  // as an error here) while the add-on is switched off, so this button is a no-op until the flag
  // is flipped in config.
  buildMcxUniverse: () =>
    authFetch('/api/universe/build-mcx', { method: 'POST' }).then(handle),

  getFutures: (underlying, exchange = 'NFO') =>
    authFetch(`/api/instruments/futures?underlying=${encodeURIComponent(underlying)}&exchange=${encodeURIComponent(exchange)}`).then(handle),
  // Downloads the full instrument master and writes every futures contract (no options) to
  // the futures CSV file on the server - see Storage:FuturesCsvPath in appsettings.json.
  fetchAllFutures: (exchange) =>
    authFetch(`/api/instruments/futures/fetch-all${exchange ? `?exchange=${encodeURIComponent(exchange)}` : ''}`, { method: 'POST' }).then(handle),
  getAllFutures: (exchange) =>
    authFetch(`/api/instruments/futures/all${exchange ? `?exchange=${encodeURIComponent(exchange)}` : ''}`).then(handle),

  // Options Flow add-on (index-option CDV/OBV). status tells the dashboard whether the feature is
  // enabled at all, so the nav entry can be hidden entirely when it isn't - see OptionsFlowController.
  getOptionsFlowStatus: () => authFetch('/api/options-flow/status').then(handle),
  // Every tracked index's latest CE/PE series, for seeding the page on load before the first
  // SignalR push arrives.
  getOptionsFlowSnapshots: () => authFetch('/api/options-flow/snapshot').then(handle),

  getAuthStatus: () => authFetch('/api/auth/status').then(handle),
  getLoginUrl: () => authFetch('/api/auth/login-url').then(handle),
  // Which broker this deployment is wired to (see Broker:Platform) and whether it has a browser
  // login flow at all - lets the UI label itself for the live platform instead of hardcoding one.
  // { platform, supportsInteractiveLogin, isAuthenticated }.
  getBrokerInfo: () => authFetch('/api/auth/broker').then(handle),
  // Fetches the current broker access token, and the expiry the backend computed for it, so it can
  // be cached client-side (see brokerAuth.js) - call right after a successful login
  // (brokerLogin=success). { accessToken, expiresUtc }.
  getBrokerToken: () => authFetch('/api/auth/broker-token').then(handle),
  // Installs an access token by hand, verified against the broker before it's accepted. The
  // recovery path for a platform with no browser login (Dhan on a portal-generated token), where
  // an expired token would otherwise need a config edit + restart. { isAuthenticated, expiresUtc }.
  setBrokerToken: (accessToken) =>
    authFetch('/api/auth/broker-token', { method: 'POST', headers: jsonHeaders, body: JSON.stringify({ accessToken }) }).then(handle),
  // Offers a client-cached broker token back to the backend after a restart - only takes effect if
  // the backend's own in-memory session is currently empty (never overwrites a live one), and only
  // after the backend re-verifies it against the broker. See AuthController.RestoreBrokerToken.
  restoreBrokerToken: (accessToken) =>
    authFetch('/api/auth/broker-restore', { method: 'POST', headers: jsonHeaders, body: JSON.stringify({ accessToken }) }).then(handle),

  // Which platform is actually live right now - see BrokerPlatformController.Get.
  getBrokerPlatform: () => authFetch('/api/settings/broker-platform').then(handle),
  // Switches broker platforms immediately, in-process - no restart. See
  // BrokerPlatformController.Switch. { platform, switched, message }.
  switchBrokerPlatform: (platform) =>
    authFetch('/api/settings/broker-platform', { method: 'POST', headers: jsonHeaders, body: JSON.stringify({ platform }) }).then(handle),

  // Changes the signed-in user's own password. On success the backend invalidates every existing
  // token for this user (security-stamp bump), so the caller must clear the local session and send
  // the user back to the login screen - see ProfileModal.jsx. Uses plain fetch instead of authFetch
  // so a "current password is wrong" 400 surfaces its own message; a genuine 401 (already-dead
  // token) still throws below.
  changePassword: async (currentPassword, newPassword) => {
    const response = await fetch('/api/auth/change-password', {
      method: 'POST',
      headers: authHeaders(jsonHeaders),
      body: JSON.stringify({ currentPassword, newPassword }),
    })
    if (!response.ok) {
      let msg = 'Could not change password.'
      try {
        const body = await response.json()
        if (body?.error) msg = body.error
      } catch { /* keep default */ }
      throw new Error(msg)
    }
    return response.json() // { message }
  },

  // Live open positions + PNL (GET /portfolio/positions under the hood) - see
  // KitePositionsService. { success, positions: [...], errorMessage }.
  getPositions: () => authFetch('/api/positions').then(handle),
  // Exits `lots` lots of an open position (product must match - Kite nets down by product).
  // Backend derives BUY vs SELL itself from the position's actual current sign. slippagePercent
  // is whatever the user entered on the square-off confirmation popup (see PositionsTable.jsx) -
  // required by the backend now (SquareOffPositionRequest.SlippagePercent), no more silent
  // fallback to the app-wide EngineOptions default.
  squareOffPosition: (position, lots, slippagePercent) =>
    authFetch('/api/positions/square-off', {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify({
        tradingSymbol: position.tradingSymbol,
        exchange: position.exchange,
        product: position.product,
        lots,
        slippagePercent,
      }),
    }).then(handle),

  // Standing "notify me on Telegram" watches on a spread's combined Exit PNL - see PnlAlertRule /
  // PnlAlertHostedService. No Update endpoint - PositionsTable.jsx deletes + recreates to change one.
  getPnlAlerts: () => authFetch('/api/pnl-alerts').then(handle),
  createPnlAlert: (alert) =>
    authFetch('/api/pnl-alerts', { method: 'POST', headers: jsonHeaders, body: JSON.stringify(alert) }).then(handle),
  deletePnlAlert: (id) => authFetch(`/api/pnl-alerts/${encodeURIComponent(id)}`, { method: 'DELETE' }).then(handle),
}
