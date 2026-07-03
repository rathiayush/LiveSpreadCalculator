import { useEffect, useRef, useState } from 'react'

// Wraps a live numeric value (price, spread, spread %) and briefly flashes green when it ticks
// up or red when it ticks down, so an update is visually noticeable instead of silently
// swapping text on every refresh. children is whatever you want rendered (usually the
// already-formatted string/JSX) - value is the raw number this flash decision is based on.
//
// The inner span is remounted (via key={tick}) on every value change rather than just toggling
// a class - a plain class toggle wouldn't restart the CSS animation if the same direction fires
// twice in a row before the previous flash finishes playing (React bails out of re-rendering an
// unchanged className), which matters here since ticks can arrive every ~250-300ms.
export default function FlashValue({ value, children }) {
  const prevRef = useRef(value)
  const [tick, setTick] = useState(0)
  const [flash, setFlash] = useState(null) // 'up' | 'down' | null

  useEffect(() => {
    const prev = prevRef.current
    if (typeof prev === 'number' && typeof value === 'number' && value !== prev) {
      setFlash(value > prev ? 'up' : 'down')
      setTick((t) => t + 1)
    }
    prevRef.current = value
  }, [value])

  const className = `flash-value${flash ? ` flash-${flash}` : ''}`
  return <span key={tick} className={className}>{children}</span>
}
