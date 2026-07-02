const jsonHeaders = { 'Content-Type': 'application/json' }

async function handle(response) {
  if (!response.ok) {
    const text = await response.text().catch(() => '')
    throw new Error(text || `Request failed: ${response.status}`)
  }
  if (response.status === 204) return null
  return response.json()
}

// All calls use relative paths - see vite.config.js for the dev proxy to the Api project.
export const api = {
  getSpreads: () => fetch('/api/spreads').then(handle),
  // Latest known value for every spread (seeds the dashboard before the first SignalR push).
  getSpreadSnapshot: () => fetch('/api/spreads/snapshot').then(handle),
  // Most recent ranking (spreads sorted by |spread %| descending) - see
  // Engine:RankingIntervalMinutes and SpreadRankingHostedService.
  getSpreadRanking: () => fetch('/api/spreads/ranking').then(handle),
  createSpread: (spread) =>
    fetch('/api/spreads', { method: 'POST', headers: jsonHeaders, body: JSON.stringify(spread) }).then(handle),
  deleteSpread: (id) => fetch(`/api/spreads/${encodeURIComponent(id)}`, { method: 'DELETE' }).then(handle),

  getTriggers: (spreadId) =>
    fetch(`/api/triggers${spreadId ? `?spreadId=${encodeURIComponent(spreadId)}` : ''}`).then(handle),
  createTrigger: (rule) =>
    fetch('/api/triggers', { method: 'POST', headers: jsonHeaders, body: JSON.stringify(rule) }).then(handle),
  deleteTrigger: (id) => fetch(`/api/triggers/${encodeURIComponent(id)}`, { method: 'DELETE' }).then(handle),
  deleteAllTriggers: () => fetch('/api/triggers', { method: 'DELETE' }).then(handle),
  enableTrigger: (id) => fetch(`/api/triggers/${encodeURIComponent(id)}/enable`, { method: 'POST' }).then(handle),
  disableTrigger: (id) => fetch(`/api/triggers/${encodeURIComponent(id)}/disable`, { method: 'POST' }).then(handle),
  // Applies one threshold/quantity/product template across many spreads at once (e.g. the whole
  // built universe) instead of configuring each one by hand - see BulkTriggerTemplateService.
  applyBulkTemplate: (template) =>
    fetch('/api/triggers/bulk', { method: 'POST', headers: jsonHeaders, body: JSON.stringify(template) }).then(handle),

  // Builds the futures universe from Kite's live instrument master - every stock currently
  // listed for NFO futures, not a hand-maintained list. Resolves each symbol's near/far month
  // contract, writes App_Data/universe_futures.csv, and subscribes the live ticker to all of
  // them in one call. See UniverseBuilderService. maxSymbols caps the build to the first N
  // symbols (alphabetically); omit it to build every futures-listed symbol on the exchange
  // (roughly 200-220 as of mid-2026 - that's the whole exchange, not a limit in this code).
  buildUniverse: (maxSymbols) =>
    fetch(`/api/universe/build${maxSymbols ? `?maxSymbols=${encodeURIComponent(maxSymbols)}` : ''}`, { method: 'POST' }).then(handle),

  getFutures: (underlying, exchange = 'NFO') =>
    fetch(`/api/instruments/futures?underlying=${encodeURIComponent(underlying)}&exchange=${encodeURIComponent(exchange)}`).then(handle),
  // Downloads the full instrument master and writes every futures contract (no options) to
  // the futures CSV file on the server - see Storage:FuturesCsvPath in appsettings.json.
  fetchAllFutures: (exchange) =>
    fetch(`/api/instruments/futures/fetch-all${exchange ? `?exchange=${encodeURIComponent(exchange)}` : ''}`, { method: 'POST' }).then(handle),
  getAllFutures: (exchange) =>
    fetch(`/api/instruments/futures/all${exchange ? `?exchange=${encodeURIComponent(exchange)}` : ''}`).then(handle),

  getAuthStatus: () => fetch('/api/auth/status').then(handle),
  getLoginUrl: () => fetch('/api/auth/login-url').then(handle),
}
