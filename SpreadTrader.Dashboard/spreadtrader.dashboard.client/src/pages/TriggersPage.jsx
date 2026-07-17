import { useCallback, useEffect, useMemo, useState } from 'react'
import { Tabs } from 'antd'
import { ThunderboltOutlined, ScissorOutlined } from '@ant-design/icons'
import { api } from '../api'
import BulkTriggerForm from '../components/BulkTriggerForm'
import TriggerList from '../components/TriggerList'
import TriggerForm from '../components/TriggerForm'

// Everything to do with trigger rules: applying one threshold across many spreads at once,
// the list of existing rules, and the form to add a new one. Split into two tabs by
// TriggerRule.IsExitTrigger - "Trigger" for rules that open a new position, "Exit Trigger" for
// rules that close one - both tabs share the exact same form/list components (see
// TriggerForm/TriggerList's isExitTrigger/title props), just filtered and tagged differently.
// Bulk template application (BulkTriggerForm) only applies to entry triggers - there's no bulk
// "exit" template today.
export default function TriggersPage({ spreads }) {
  const [triggers, setTriggers] = useState([])

  const loadTriggers = useCallback(() => {
    api.getTriggers().then(setTriggers).catch(() => {})
  }, [])

  useEffect(() => {
    loadTriggers()
  }, [loadTriggers])

  const entryTriggers = useMemo(() => triggers.filter((t) => !t.isExitTrigger), [triggers])
  const exitTriggers = useMemo(() => triggers.filter((t) => t.isExitTrigger), [triggers])

  return (
    <Tabs
      items={[
        {
          key: 'trigger',
          label: <span><ThunderboltOutlined /> Trigger</span>,
          children: (
            <>
              <TriggerList triggers={entryTriggers} onChanged={loadTriggers} spreads={spreads} />
              <TriggerForm spreads={spreads} onCreated={loadTriggers} />
              <BulkTriggerForm spreadCount={spreads.length} onCreated={loadTriggers} />
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
                onChanged={loadTriggers}
                spreads={spreads}
                title="Configured exit triggers"
              />
              <TriggerForm spreads={spreads} onCreated={loadTriggers} isExitTrigger />
            </>
          ),
        },
      ]}
    />
  )
}
