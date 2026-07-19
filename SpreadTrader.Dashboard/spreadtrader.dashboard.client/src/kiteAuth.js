// Client-side cache of the Kite (Zerodha) access token, separate from the app's own JWT session
// (see auth.js). WHY THIS EXISTS: Kite Connect access tokens are valid until roughly 6:00 AM IST
// the NEXT day regardless of how long this app's own backend process has been running - but the
// backend only ever kept that token in memory (KiteSession - see its doc comment), so restarting
// the .NET process (a deploy, a crash, anything) always threw the still-valid token away and forced
// a brand new Kite login. This module lets the frontend hold onto the token itself and hand it back
// to the backend after a restart (POST /api/auth/kite-restore) instead of always prompting a fresh
// login - the "Login to Kite" button only needs to show when the token has ACTUALLY expired (or
// Kite itself rejects it), not just because the server process happened to restart.
//
// SECURITY NOTE: this puts a broker-level access token in localStorage, readable by any JS running
// on this origin (same trade-off this app already makes for its own JWT in auth.js). Only worth it
// because this is a single-operator trading tool, not a multi-tenant app with untrusted users.
//
// Kite's daily cutoff is a fixed wall-clock time in India Standard Time (UTC+5:30, no DST), which
// converts to a fixed UTC time: 6:00 AM IST == 00:30 UTC the same calendar date. Expiry is computed
// once at cache-write time as "the next occurrence of 00:30 UTC after now" - a plain UTC instant,
// so later comparisons never have to re-derive IST wall-clock time at all.

const STORAGE_KEY = 'spreadtrader-kite-token'

/// Next UTC instant that corresponds to 6:00 AM IST - either today's or tomorrow's, whichever is
/// still ahead of `now`. 6:00 IST - 5:30 UTC offset = 00:30 UTC same calendar date.
function nextKiteCutoffUtc(now = new Date()) {
  const todayCutoff = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate(), 0, 30, 0))
  return now.getTime() < todayCutoff.getTime()
    ? todayCutoff
    : new Date(todayCutoff.getTime() + 24 * 60 * 60 * 1000)
}

/// Caches a freshly-obtained Kite access token (see GET /api/auth/kite-token, called right after a
/// successful login) with its computed expiry - the next 6 AM IST cutoff from the moment of caching.
export function saveKiteToken(accessToken) {
  if (!accessToken) return
  try {
    window.localStorage.setItem(
      STORAGE_KEY,
      JSON.stringify({ accessToken, expiresUtc: nextKiteCutoffUtc().toISOString() }),
    )
  } catch { /* ignore quota/availability errors - restore just won't have anything to offer */ }
}

/// Returns the cached access token if present and not past its computed 6 AM IST expiry, else
/// null. Does NOT mean the token is actually still honored by Kite (only Kite knows that for
/// certain) - just that it's still within the window it's expected to be valid for. A token that
/// turns out to be dead earlier than expected (e.g. a fresh login elsewhere invalidated it) is
/// caught the normal way: the next real Kite API call fails with TokenException and
/// clearCachedKiteToken() is called from the "kiteAuthInvalidated" handler - see App.jsx.
export function getCachedKiteToken() {
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY)
    if (!raw) return null
    const parsed = JSON.parse(raw)
    if (!parsed?.accessToken || !parsed?.expiresUtc) return null
    if (new Date(parsed.expiresUtc).getTime() <= Date.now()) return null
    return parsed.accessToken
  } catch {
    return null
  }
}

/// Drops the cached token - called once the backend confirms (via "kiteAuthInvalidated") that a
/// real Kite call rejected it, so a dead token isn't offered again on the next restart, and once
/// the user completes a fresh Kite login manually (the new token from that login replaces it via
/// saveKiteToken anyway, but clearing first avoids ever briefly holding two candidates).
export function clearCachedKiteToken() {
  try {
    window.localStorage.removeItem(STORAGE_KEY)
  } catch { /* ignore */ }
}
