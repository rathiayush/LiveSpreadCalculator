import { useState } from 'react'
import { Card, Button, Alert, Typography, Space, InputNumber, Divider } from 'antd'
import { GlobalOutlined, ThunderboltOutlined, GoldOutlined } from '@ant-design/icons'
import { api } from '../api'

// One button that builds the futures universe straight from Kite's live instrument master -
// every stock currently listed for NFO futures, not a hand-maintained list: resolves near/far
// month contracts, writes App_Data/universe_futures.csv, and subscribes the live ticker to every
// one of them - no per-stock setup needed. Re-run this after every monthly expiry roll, or
// whenever NSE adds new F&O stocks. How many symbols get built is configurable below (leave
// blank to build every futures-listed symbol on the exchange - currently ~200-220, since that's
// the entire exchange's F&O universe, not a limit in this code).
export default function UniversePanel({ onSpreadsChanged }) {
  const [maxSymbols, setMaxSymbols] = useState(null)
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState(null)
  const [error, setError] = useState(null)

  // Separate state for the MCX commodity build so its outcome/errors don't clobber the NFO panel.
  const [mcxBusy, setMcxBusy] = useState(false)
  const [mcxResult, setMcxResult] = useState(null)
  const [mcxError, setMcxError] = useState(null)

  const build = async () => {
    setBusy(true)
    setError(null)
    setResult(null)
    try {
      const res = await api.buildUniverse(maxSymbols || undefined)
      setResult(res)
      onSpreadsChanged()
    } catch (err) {
      setError(err.message)
    } finally {
      setBusy(false)
    }
  }

  const buildMcx = async () => {
    setMcxBusy(true)
    setMcxError(null)
    setMcxResult(null)
    try {
      const res = await api.buildMcxUniverse()
      setMcxResult(res)
      onSpreadsChanged()
    } catch (err) {
      // While Mcx:Enabled is false the server returns 409 - surface it as-is so it's obvious the
      // add-on just needs turning on in config rather than looking like a failure.
      setMcxError(err.message)
    } finally {
      setMcxBusy(false)
    }
  }

  return (
    <Card title={<Space><GlobalOutlined style={{ color: '#4da3ff' }} /> Futures universe builder</Space>} style={{ marginBottom: 16 }}>
      <Space direction="vertical" size="middle" style={{ width: '100%' }}>
        <Typography.Text type="secondary">
          Fetches every stock Kite currently lists NFO futures for (no manual list to maintain),
          resolves each one's current and next month futures contract, saves the resolved set to
          App_Data/universe_futures.csv, and starts streaming all of them immediately.
        </Typography.Text>
        <Space wrap>
          <InputNumber
            min={1}
            placeholder="All available"
            value={maxSymbols}
            onChange={setMaxSymbols}
            addonBefore="Limit to first"
            addonAfter="symbols"
          />
          <Button type="primary" icon={<ThunderboltOutlined />} onClick={build} loading={busy}>
            Build universe from Kite
          </Button>
        </Space>
        {error && <Alert type="error" message={error} showIcon />}
        {result && (
          <Alert
            type="success"
            showIcon
            message={`${result.included.length} of ${result.requestedCount} symbols resolved and are now streaming.`}
            description={result.skipped.length > 0
              ? `${result.skipped.length} skipped: ${result.skipped.slice(0, 5).join('; ')}${result.skipped.length > 5 ? '…' : ''}`
              : undefined}
          />
        )}

        <Divider style={{ margin: '4px 0' }} />

        {/* MCX commodity arbitrage build. Gated server-side by Mcx:Enabled - until that flag is on,
            this button reports the add-on is disabled instead of building anything. Once built, the
            commodity spreads stream into the same Live Spreads grid / ranking as everything else. */}
        <Typography.Text type="secondary">
          <GoldOutlined style={{ color: '#d4af37', marginRight: 6 }} />
          Builds MCX commodity spreads (silver, gold, crude, natural gas): near/far calendar spreads
          plus same-expiry cross-contract pairs like SILVER vs SILVERM. Disabled until
          <Typography.Text code>Mcx:Enabled</Typography.Text> is turned on in config.
        </Typography.Text>
        <Space wrap>
          <Button icon={<GoldOutlined />} onClick={buildMcx} loading={mcxBusy}>
            Build MCX commodity universe
          </Button>
        </Space>
        {mcxError && <Alert type="warning" message={mcxError} showIcon />}
        {mcxResult && (
          <Alert
            type="success"
            showIcon
            message={`${mcxResult.included.length} MCX commodity spreads built and now streaming.`}
            description={mcxResult.skipped.length > 0
              ? `${mcxResult.skipped.length} skipped: ${mcxResult.skipped.slice(0, 5).join('; ')}${mcxResult.skipped.length > 5 ? '…' : ''}`
              : undefined}
          />
        )}
      </Space>
    </Card>
  )
}
