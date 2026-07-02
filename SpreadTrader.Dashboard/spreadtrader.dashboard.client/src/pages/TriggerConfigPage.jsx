import { useCallback, useEffect, useState } from 'react'
import { Card, Button, Typography, Space } from 'antd'
import { api } from '../api'
import UniversePanel from '../components/UniversePanel'
import SpreadList from '../components/SpreadList'
import AddSpreadForm from '../components/AddSpreadForm'
import BulkTriggerForm from '../components/BulkTriggerForm'
import TriggerList from '../components/TriggerList'
import TriggerForm from '../components/TriggerForm'

// Everything to do with configuration lives here: syncing the futures symbol list from Kite,
// adding/removing spreads, and adding/enabling/disabling/removing trigger rules. The live
// dashboard (DashboardPage) is read-only and deliberately doesn't touch any of this.
export default function TriggerConfigPage({ spreads, onSpreadsChanged }) {
  const [triggers, setTriggers] = useState([])
  const [futures, setFutures] = useState([])
  const [syncStatus, setSyncStatus] = useState(null)
  const [syncing, setSyncing] = useState(false)

  const loadTriggers = useCallback(() => {
    api.getTriggers().then(setTriggers).catch(() => {})
  }, [])

  const loadFutures = useCallback(() => {
    api.getAllFutures().then(setFutures).catch(() => {})
  }, [])

  useEffect(() => {
    loadTriggers()
    loadFutures() // shows whatever was synced in a previous session, if any
  }, [loadTriggers, loadFutures])

  const syncSymbols = async () => {
    setSyncing(true)
    setSyncStatus(null)
    try {
      const result = await api.fetchAllFutures('NFO')
      setSyncStatus(`Synced ${result.count} futures contracts from Kite.`)
      loadFutures()
    } catch (err) {
      setSyncStatus(`Sync failed: ${err.message}`)
    } finally {
      setSyncing(false)
    }
  }

  return (
    <>
      <Card title="Symbols" style={{ marginBottom: 16 }}>
        <Space direction="vertical" size="middle" style={{ width: '100%' }}>
          <Typography.Text type="secondary">
            Fetches every futures contract currently listed on NFO (options and equities are
            excluded) and saves them to a CSV file on the server, so the pickers below don't need
            to hit Kite every time.
          </Typography.Text>
          <Button onClick={syncSymbols} loading={syncing}>
            Sync symbols from Kite
          </Button>
          {syncStatus && <Typography.Text type="secondary">{syncStatus}</Typography.Text>}
          {futures.length > 0 && <Typography.Text type="secondary">{futures.length} futures contracts currently loaded.</Typography.Text>}
        </Space>
      </Card>

      <UniversePanel onSpreadsChanged={onSpreadsChanged} />

      <SpreadList spreads={spreads} onChanged={onSpreadsChanged} />
      <AddSpreadForm futures={futures} onCreated={onSpreadsChanged} />

      <BulkTriggerForm spreadCount={spreads.length} onCreated={loadTriggers} />
      <TriggerList triggers={triggers} onChanged={loadTriggers} />
      <TriggerForm spreads={spreads} onCreated={loadTriggers} />
    </>
  )
}
