import { useMemo, useState } from 'react'
import { Tabs } from 'antd'
import { ThunderboltOutlined, ScissorOutlined } from '@ant-design/icons'
import { useTriggerEvents, clearTriggerEvents } from '../hooks/spreadHub'
import { api } from '../api'
import TriggerFeed from '../components/TriggerFeed'

// Live feed of trigger fires only - split out from the Dashboard so the "what's moving"
// ranking view and the "what fired" activity log don't have to compete for space on one page.
// Split into two tabs by TriggerEvent.IsExitTrigger (carried through from the rule that fired -
// see TriggerRule.IsExitTrigger), matching the Trigger/Exit Trigger split on the Triggers page.
export default function TriggerActivityPage() {
  const triggerEvents = useTriggerEvents()
  const [clearError, setClearError] = useState(null)

  // Clears both sides: the server-side history (so it doesn't just reappear on refresh or in
  // another tab - see InMemoryTriggerActivityStore/DELETE /api/triggers/activity) and this
  // browser's own local list (clearTriggerEvents), which would otherwise keep showing the same
  // entries until the next push even after the server-side history is gone. Shared across both
  // tabs - there's only one server-side history, so Clear on either tab wipes both.
  const handleClear = async () => {
    try {
      await api.clearTriggerActivity()
      setClearError(null)
    } catch (err) {
      setClearError(err.message)
    }
    clearTriggerEvents()
  }

  const entryEvents = useMemo(() => triggerEvents.filter((e) => !e.isExitTrigger), [triggerEvents])
  const exitEvents = useMemo(() => triggerEvents.filter((e) => e.isExitTrigger), [triggerEvents])

  return (
    <Tabs
      items={[
        {
          key: 'trigger-activity',
          label: <span><ThunderboltOutlined /> Trigger Activity</span>,
          children: <TriggerFeed events={entryEvents} onClear={handleClear} clearError={clearError} />,
        },
        {
          key: 'exit-trigger-activity',
          label: <span><ScissorOutlined /> Exit Trigger Activity</span>,
          children: <TriggerFeed events={exitEvents} onClear={handleClear} clearError={clearError} />,
        },
      ]}
    />
  )
}
