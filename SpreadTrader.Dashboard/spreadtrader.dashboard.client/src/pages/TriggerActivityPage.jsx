import { useTriggerEvents, clearTriggerEvents } from '../hooks/spreadHub'
import TriggerFeed from '../components/TriggerFeed'

// Live feed of trigger fires only - split out from the Dashboard so the "what's moving"
// ranking view and the "what fired" activity log don't have to compete for space on one page.
export default function TriggerActivityPage() {
  const triggerEvents = useTriggerEvents()
  return <TriggerFeed events={triggerEvents} onClear={clearTriggerEvents} />
}
