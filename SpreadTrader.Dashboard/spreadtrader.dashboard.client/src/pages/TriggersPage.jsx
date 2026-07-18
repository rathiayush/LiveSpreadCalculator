import { useCallback, useEffect, useState } from 'react'
import { useLocation } from 'react-router-dom'
import { Tabs } from 'antd'
import { ThunderboltOutlined, ScissorOutlined } from '@ant-design/icons'
import { api } from '../api'
import BulkTriggerForm from '../components/BulkTriggerForm'
import TriggerList from '../components/TriggerList'
import TriggerForm from '../components/TriggerForm'

// Everything to do with trigger rules: applying one threshold across many spreads at once,
// the list of existing rules, and the form to add a new one. Split into two tabs backed by two
// completely separate stores (see IExitTriggerRuleStore's doc comment for why) - "Trigger" for
// rules that open a new position (GET/POST /api/triggers), "Exit Trigger" for rules that close
// one (GET/POST /api/exit-triggers). Both tabs share the exact same form/list components (see
// TriggerForm/TriggerList's isExitTrigger prop, which just points them at the matching set of
// api.* functions), just fetched and posted through different endpoints. Bulk template
// application (BulkTriggerForm) only applies to entry triggers - there's no bulk "exit" template
// today.
//
// "Add trigger" on a Positions & PNL row (see PositionsTable.jsx) lands here with router state
// ({ tab: 'exit-trigger', spreadId }) instead of the old standalone AddPositionExitTriggerPage -
// read once on mount (same lazy-initializer pattern as TriggerForm's own initialSpreadId) to open
// straight to the Exit Trigger tab with that spread already selected in the form below.
export default function TriggersPage({ spreads }) {
  const location = useLocation()
  const initialTab = location.state?.tab === 'exit-trigger' ? 'exit-trigger' : 'trigger'
  const initialExitSpreadId = location.state?.spreadId ?? ''

  const [entryTriggers, setEntryTriggers] = useState([])
  const [exitTriggers, setExitTriggers] = useState([])

  const loadEntryTriggers = useCallback(() => {
    api.getTriggers().then(setEntryTriggers).catch(() => {})
  }, [])
  const loadExitTriggers = useCallback(() => {
    api.getExitTriggers().then(setExitTriggers).catch(() => {})
  }, [])

  useEffect(() => {
    loadEntryTriggers()
    loadExitTriggers()
  }, [loadEntryTriggers, loadExitTriggers])

  return (
    <Tabs
      defaultActiveKey={initialTab}
      items={[
        {
          key: 'trigger',
          label: <span><ThunderboltOutlined /> Trigger</span>,
          children: (
            <>
              <TriggerList triggers={entryTriggers} onChanged={loadEntryTriggers} spreads={spreads} />
              <TriggerForm spreads={spreads} onCreated={loadEntryTriggers} />
              <BulkTriggerForm spreadCount={spreads.length} onCreated={loadEntryTriggers} />
            </>
          ),
        },
        {
          key: 'exit-trigger',
          label: <span><ScissorOutlined /> Exit Trigger</span>,
          children: (
            <>
              <TriggerList
                triggers={exitTriggers}
                onChanged={loadExitTriggers}
                spreads={spreads}
                title="Configured exit triggers"
                isExitTrigger
              />
              <TriggerForm
                spreads={spreads}
                onCreated={loadExitTriggers}
                isExitTrigger
                initialSpreadId={initialExitSpreadId}
              />
            </>
          ),
        },
      ]}
    />
  )
}
