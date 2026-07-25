// Client-side cache of the BROKER access token (Zerodha Kite or Dhan, whichever this deployment is
// configured for - see Broker:Platform), separate from the app's own JWT session (see auth.js).
//
// WHY THIS EXISTS: broker access tokens outlive this app's backend process. Kite's are valid until
// roughly 6:00 AM IST the next day; Dhan's for 24 hours, or up to 30 days for one generated in the
// web portal. But the backend only ever keeps the token in memory (see IBrokerSession), so
// restarting the .NET process - a deploy, a crash, anything - used to throw a perfectly good token
// away and force a brand new broker login. This module lets the frontend hold onto the token itself
// and hand it back after a restart (POST /api/auth/broker-restore), so the login button only shows
// when the token has ACTUALLY expired (or the broker rejects it), not merely because the server
// process restarted.
//
// SECURITY NOTE: this puts a broker-level access token in localStorage, readable by any JS running
// on this origin (the same trade-off this app already makes for its own JWT in auth.js). Only worth
// it because this is a single-operator trading tool, not a multi-tenant app with untrusted users.
//
// EXPIRY IS SERVER-COMPUTED. This used to derive Kite's fixed 6 AM IST cutoff here in the browser,
// which is simply wrong for Dhan - it has no daily wall-clock cutoff, its tokens expire a fixed
// duration after they were issued. So the backend now returns the expiry alongside the token (see
// GET /api/auth/broker-token -> IBrokerAuthService.GetTokenExpiryUtc) and this module just stores
// and honours whatever it is told. Nothing here knows which broker is live.

const STORAGE_KEY = 'spreadtrader-broker-token'

// Used only if the backend somehow returns a token with no expiry - short enough that a stale token
// gets re-verified rather than trusted indefinitely, long enough to still be useful.
const FALLBACK_LIFETIME_MS = 24 * 60 * 60 * 1000

/// Caches a freshly-obtained broker access token together with the expiry the backend reported for
/// it. Call right after a successful login (brokerLogin=success) - see App.jsx.
export function saveBrokerToken(accessToken, expiresUtc) {
  if (!accessToken) return
  try {
    const expiry = expiresUtc ?? new Date(Date.now() + FALLBACK_LIFETIME_MS).toISOString()
    window.localStorage.setItem(STORAGE_KEY, JSON.stringify({ accessToken, expiresUtc: expiry }))
  } catch { /* ignore quota/availability errors - restore just won't have anything to offer */ }
}

/// Returns the cached access token if present and not past its expiry, else null. Does NOT mean the
/// token is actually still honored by the broker (only the broker knows that for certain) - just
/// that it's still inside the window it was issued for. A token that turns out to be dead earlier
/// than expected (e.g. a fresh login elsewhere invalidated it) is caught the normal way: the next
/// real broker API call fails and clearCachedBrokerToken() is called from the
/// "brokerAuthInvalidated" handler - see App.jsx. The backend also re-verifies any token offered
/// back to it before trusting it (see AuthController.RestoreBrokerToken).
export function getCachedBrokerToken() {
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

/// Drops the cached token - called once the backend confirms (via "brokerAuthInvalidated") that a
/// real broker call rejected it, so a dead token isn't offered again on the next restart, and once
/// the user completes a fresh login manually (the new token replaces it via saveBrokerToken anyway,
/// but clearing first avoids ever briefly holding two candidates).
export function clearCachedBrokerToken() {
  try {
    window.localStorage.removeItem(STORAGE_KEY)
  } catch { /* ignore */ }
}
