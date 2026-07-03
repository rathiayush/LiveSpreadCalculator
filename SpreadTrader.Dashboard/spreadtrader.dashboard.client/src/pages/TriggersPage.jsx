import { useCallback, useEffect, useState } from 'react'
import { api } from '../api'
import BulkTriggerForm from '../components/BulkTriggerForm'
import TriggerList from '../components/TriggerList'
import TriggerForm from '../components/TriggerForm'

// Everything to do with trigger rules: applying one threshold across many spreads at once,
// the list of existing rules, and the form to add a new one.
export default function TriggersPage({ spreads }) {
  const [triggers, setTriggers] = useState([])

  const loadTriggers = useCallback(() => {
    api.getTriggers().then(setTriggers).catch(() => {})
  }, [])

  useEffect(() => {
    loadTriggers()
  }, [loadTriggers])

  return (
    <>
      <TriggerList triggers={triggers} onChanged={loadTriggers} spreads={spreads} />
      <TriggerForm spreads={spreads} onCreated={loadTriggers} />
      <BulkTriggerForm spreadCount={spreads.length} onCreated={loadTriggers} />
    </>
  )
}
