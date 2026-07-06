import { useState } from 'react'
import { useTriggerEvents, clearTriggerEvents } from '../hooks/spreadHub'
import { api } from '../api'
import TriggerFeed from '../components/TriggerFeed'

// Live feed of trigger fires only - split out from the Dashboard so the "what's moving"
// ranking view and the "what fired" activity log don't have to compete for space on one page.
export default function TriggerActivityPage() {
  const triggerEvents = useTriggerEvents()
  const [clearError, setClearError] = useState(null)

  // Clears both sides: the server-side history (so it doesn't just reappear on refresh or in
  // another tab - see InMemoryTriggerActivityStore/DELETE /api/triggers/activity) and this
  // browser's own local list (clearTriggerEvents), which would otherwise keep showing the same
  // entries until the next push even after the server-side history is gone.
  const handleClear = async () => {
    try {
      await api.clearTriggerActivity()
      setClearError(null)
    } catch (err) {
      setClearError(err.message)
    }
    clearTriggerEvents()
  }

  return <TriggerFeed events={triggerEvents} onClear={handleClear} clearError={clearError} />
}
