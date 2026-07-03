import { useSyncExternalStore } from 'react'

// Central app-login state. The backend now requires a JWT on every /api call and on the SignalR
// hub (see the .NET Program.cs fallback policy), so the dashboard must obtain one via
// POST /api/auth/token and attach it to everything. This module is the single source of truth for
// that token: api.js reads it for the Authorization header, spreadHub.js reads it for SignalR's
// accessTokenFactory, and App.jsx subscribes to it to decide between the login screen and the app.

const STORAGE_KEY = 'spreadtrader-auth'

let session = loadFromStorage()
const listeners = new Set()

function loadFromStorage() {
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY)
    if (!raw) return null
    const parsed = JSON.parse(raw)
    // Drop an already-expired token so we don't start the app only to have every call 401.
    if (parsed?.expiresUtc && new Date(parsed.expiresUtc).getTime() <= Date.now()) return null
    return parsed?.token ? parsed : null
  } catch {
    return null
  }
}

function notify() {
  for (const listener of listeners) listener()
}

export function getToken() {
  return session?.token ?? null
}

export function getEmail() {
  return session?.email ?? null
}

export function isAuthenticated() {
  return !!session?.token
}

export function setSession(next) {
  session = next
  try {
    window.localStorage.setItem(STORAGE_KEY, JSON.stringify(next))
  } catch { /* ignore quota/availability errors - in-memory session still works for this tab */ }
  notify()
}

export function clearSession() {
  session = null
  try {
    window.localStorage.removeItem(STORAGE_KEY)
  } catch { /* ignore */ }
  notify()
}

function subscribe(onStoreChange) {
  listeners.add(onStoreChange)
  return () => listeners.delete(onStoreChange)
}

// Re-renders the subscribing component whenever login state changes. Snapshot is the token string
// (or null), which is a stable primitive so useSyncExternalStore won't loop.
export function useAuth() {
  const token = useSyncExternalStore(subscribe, getToken)
  return { token, email: getEmail(), isAuthenticated: !!token }
}
