import { useCallback, useEffect, useState } from 'react'
import { Card, Button, Typography, Space } from 'antd'
import { DatabaseOutlined, SyncOutlined } from '@ant-design/icons'
import { api } from '../api'
import UniversePanel from '../components/UniversePanel'

// Setup/refresh page: sync the raw futures symbol list from Kite, then build the tradeable
// calendar-spread universe from it. Nothing here is "live" - re-visit this page whenever you
// want to pick up newly-added F&O stocks or roll to a new expiry.
export default function SymbolsPage({ onSpreadsChanged }) {
  const [futures, setFutures] = useState([])
  const [syncStatus, setSyncStatus] = useState(null)
  const [syncing, setSyncing] = useState(false)

  const loadFutures = useCallback(() => {
    api.getAllFutures().then(setFutures).catch(() => {})
  }, [])

  useEffect(() => {
    loadFutures() // shows whatever was synced in a previous session, if any
  }, [loadFutures])

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
      <Card title={<Space><DatabaseOutlined style={{ color: '#4da3ff' }} /> Symbols</Space>} style={{ marginBottom: 16 }}>
        <Space direction="vertical" size="middle" style={{ width: '100%' }}>
          <Typography.Text type="secondary">
            Fetches every futures contract currently listed on NFO (options and equities are
            excluded) and saves them to a CSV file on the server, so the universe builder below
            doesn't need to hit Kite every time.
          </Typography.Text>
          <Button icon={<SyncOutlined />} onClick={syncSymbols} loading={syncing}>
            Sync symbols from Kite
          </Button>
          {syncStatus && <Typography.Text type="secondary">{syncStatus}</Typography.Text>}
          {futures.length > 0 && <Typography.Text type="secondary">{futures.length} futures contracts currently loaded.</Typography.Text>}
        </Space>
      </Card>

      <UniversePanel onSpreadsChanged={onSpreadsChanged} />
    </>
  )
}
