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

  // Builds the futures universe from Kite's live instrument master - every stock currently
  // listed for NFO futures, not a hand-maintained list. Resolves each symbol's near/far month
  // contract, writes App_Data/universe_futures.csv, and subscribes the live ticker to all of
  // them in one call. See UniverseBuilderService. maxSymbols caps the build to the first N
  // symbols (alphabetically); omit it to build every futures-listed symbol on the exchange
  // (roughly 200-220 as of mid-2026 - that's the whole exchange, not a limit in this code).
  buildUniverse: (maxSymbols) =>
    authFetch(`/api/universe/build${maxSymbols ? `?maxSymbols=${encodeURIComponent(maxSymbols)}` : ''}`, { method: 'POST' }).then(handle),

  getFutures: (underlying, exchange = 'NFO') =>
    authFetch(`/api/instruments/futures?underlying=${encodeURIComponent(underlying)}&exchange=${encodeURIComponent(exchange)}`).then(handle),
  // Downloads the full instrument master and writes every futures contract (no options) to
  // the futures CSV file on the server - see Storage:FuturesCsvPath in appsettings.json.
  fetchAllFutures: (exchange) =>
    authFetch(`/api/instruments/futures/fetch-all${exchange ? `?exchange=${encodeURIComponent(exchange)}` : ''}`, { method: 'POST' }).then(handle),
  getAllFutures: (exchange) =>
    authFetch(`/api/instruments/futures/all${exchange ? `?exchange=${encodeURIComponent(exchange)}` : ''}`).then(handle),

  getAuthStatus: () => authFetch('/api/auth/status').then(handle),
  getLoginUrl: () => authFetch('/api/auth/login-url').then(handle),

  // Live open positions + PNL (GET /portfolio/positions under the hood) - see
  // KitePositionsService. { success, positions: [...], errorMessage }.
  getPositions: () => authFetch('/api/positions').then(handle),
  // Exits `lots` lots of an open position (product must match - Kite nets down by product).
  // Backend derives BUY vs SELL itself from the position's actual current sign.
  squareOffPosition: (position, lots) =>
    authFetch('/api/positions/square-off', {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify({
        tradingSymbol: position.tradingSymbol,
        exchange: position.exchange,
        product: position.product,
        lots,
      }),
    }).then(handle),
}
