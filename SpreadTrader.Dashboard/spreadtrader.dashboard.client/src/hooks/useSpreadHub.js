// Superseded by hooks/spreadHub.js - a module-level external store (useSyncExternalStore)
// instead of lifting spreads/ranking/triggerEvents/connectionState into AppShell's own useState.
// That change is what let pages stop re-rendering on every ~250-300ms spread tick when they're
// not the one on screen (see the comment at the top of spreadHub.js for the full story). Kept as
// a harmless unused file rather than deleted (no delete tool available) - safe to remove by hand.
export {};
