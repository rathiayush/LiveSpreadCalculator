import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Card, Table, Tag, Typography, Space, InputNumber, Button, Popconfirm, message, Alert, Tooltip } from 'antd'
import { CloseCircleOutlined, ScissorOutlined, ReloadOutlined, ThunderboltOutlined } from '@ant-design/icons'
import { api } from '../api'

// Per-row "lots to exit" state lives keyed by a stable row key (symbol|exchange|product) rather
// than in the row data itself, since positions are re-fetched wholesale on every poll (see
// PositionsPage) and we don't want a freshly-fetched row to wipe out what the user was mid-typing.
function rowKey(p) {
  return `${p.tradingSymbol}|${p.exchange}|${p.product}`
}

// Mirrors AddPositionExitTriggerPage's identical helper - kept local rather than shared since
// it's a one-line comparison, not worth a shared module for.
function sameInstrument(a, tradingSymbol, exchange) {
  return a && a.tradingSymbol?.toUpperCase() === tradingSymbol?.toUpperCase() && a.exchange?.toUpperCase() === exchange?.toUpperCase()
}

// Live open positions + PNL, with the ability to select rows and exit some or all of the lots on
// each - the manual "cut position via the dashboard" flow requested instead of needing the Kite
// app (which would kill this app's own session - see AuthController/KiteAuthService).
export default function PositionsTable({ positions, spreads, onChanged, refreshing, onRefresh }) {
  const navigate = useNavigate()
  const [exitLots, setExitLots] = useState({}) // rowKey -> lots requested
  const [selectedKeys, setSelectedKeys] = useState([])
  const [busyKeys, setBusyKeys] = useState({}) // rowKey -> true while a square-off is in flight
  const [error, setError] = useState(null)

  const totalPnl = useMemo(() => positions.reduce((sum, p) => sum + (p.pnl ?? 0), 0), [positions])

  const openLots = (p) => (p.lotSize > 0 ? Math.abs(p.quantity) / p.lotSize : null)

  // Only a position that's a near or far leg of some configured spread can get an exit trigger -
  // there's no near/far pair to evaluate a threshold against otherwise. Gates the "Add trigger"
  // button itself instead of only warning after navigating to the exit-trigger page.
  const hasMatchedSpread = (p) =>
    (spreads ?? []).some((s) =>
      sameInstrument(s.nearLeg, p.tradingSymbol, p.exchange) || sameInstrument(s.farLeg, p.tradingSymbol, p.exchange))

  const lotsFor = (p) => {
    const key = rowKey(p)
    if (exitLots[key] !== undefined) return exitLots[key]
    const lots = openLots(p)
    return lots ?? ''
  }

  const setLotsFor = (p, value) => setExitLots((m) => ({ ...m, [rowKey(p)]: value }))

  const squareOff = async (p, lots) => {
    const key = rowKey(p)
    setError(null)
    setBusyKeys((m) => ({ ...m, [key]: true }))
    try {
      const result = await api.squareOffPosition(p, lots)
      if (!result.success) {
        message.error(`Square-off failed for ${p.tradingSymbol}: ${result.errorMessage || result.status || 'rejected'}`)
      } else {
        message.success(`Exited ${lots} lot(s) of ${p.tradingSymbol} (${result.status})`)
        setSelectedKeys((keys) => keys.filter((k) => k !== key))
      }
      onChanged()
    } catch (err) {
      setError(err.message)
    } finally {
      setBusyKeys((m) => ({ ...m, [key]: false }))
    }
  }

  const squareOffSelected = async () => {
    const rows = positions.filter((p) => selectedKeys.includes(rowKey(p)))
    // Exit every SELL-side leg (closing a long position) before any BUY-side leg (covering a
    // short) - requested so longs are already cut and margin/exposure reduced before any short
    // gets covered, rather than the two happening in whatever order they were selected/rendered.
    const sells = rows.filter((p) => p.quantity > 0)
    const buys = rows.filter((p) => p.quantity < 0)
    for (const p of [...sells, ...buys]) {
      const lots = lotsFor(p)
      if (lots > 0) await squareOff(p, lots)
    }
  }

  const columns = [
    { title: 'Symbol', dataIndex: 'tradingSymbol', key: 'tradingSymbol', fixed: 'left' },
    { title: 'Exchange', dataIndex: 'exchange', key: 'exchange' },
    { title: 'Product', dataIndex: 'product', key: 'product' },
    {
      title: 'Side',
      key: 'side',
      render: (_, p) => (p.quantity > 0 ? <Tag color="green">LONG</Tag> : <Tag color="red">SHORT</Tag>),
    },
    {
      title: 'Qty (lots)',
      key: 'quantity',
      render: (_, p) => {
        const lots = openLots(p)
        return (
          <span>
            {Math.abs(p.quantity)}
            {lots !== null && <Typography.Text type="secondary"> ({lots} lot{lots === 1 ? '' : 's'})</Typography.Text>}
          </span>
        )
      },
    },
    {
      title: 'Avg price',
      dataIndex: 'averagePrice',
      key: 'averagePrice',
      render: (v) => v?.toFixed(2),
    },
    {
      title: 'LTP',
      dataIndex: 'lastPrice',
      key: 'lastPrice',
      render: (v) => v?.toFixed(2),
    },
    {
      title: 'PNL',
      dataIndex: 'pnl',
      key: 'pnl',
      render: (v) => (
        <Typography.Text strong style={{ color: v >= 0 ? '#3f8600' : '#cf1322' }}>
          {v >= 0 ? '+' : ''}{v?.toFixed(2)}
        </Typography.Text>
      ),
      sorter: (a, b) => a.pnl - b.pnl,
    },
    {
      title: 'Exit',
      key: 'exit',
      render: (_, p) => {
        const key = rowKey(p)
        const lots = openLots(p)
        return (
          <Space size="small">
            <InputNumber
              min={1}
              max={lots ?? undefined}
              value={lotsFor(p) === '' ? null : lotsFor(p)}
              onChange={(v) => setLotsFor(p, v ?? '')}
              size="small"
              style={{ width: 72 }}
              disabled={lots === null}
            />
            <Popconfirm
              title={`Square off ${p.tradingSymbol}?`}
              description={`Places a MARKET ${p.quantity > 0 ? 'SELL' : 'BUY'} for ${lotsFor(p) || 0} lot(s).`}
              onConfirm={() => squareOff(p, Number(lotsFor(p)))}
              okText="Square off"
              okButtonProps={{ danger: true }}
              disabled={!lotsFor(p) || Number(lotsFor(p)) <= 0}
            >
              <Button
                size="small"
                danger
                icon={<ScissorOutlined />}
                loading={!!busyKeys[key]}
                disabled={lots === null || !lotsFor(p) || Number(lotsFor(p)) <= 0}
              >
                Exit
              </Button>
            </Popconfirm>
            <Tooltip title={hasMatchedSpread(p)
              ? 'Build an exit trigger for this position (fires automatically, deleted if you square off manually)'
              : `${p.tradingSymbol} isn't part of any configured spread - add it under Configured Spreads first`}
            >
              <Button
                size="small"
                icon={<ThunderboltOutlined />}
                disabled={!hasMatchedSpread(p)}
                onClick={() => navigate('/positions/exit-trigger', { state: { position: p } })}
              >
                Add trigger
              </Button>
            </Tooltip>
          </Space>
        )
      },
    },
  ]

  return (
    <Card
      title={
        <Space>
          Open positions ({positions.length})
          <Typography.Text style={{ color: totalPnl >= 0 ? '#3f8600' : '#cf1322' }}>
            Total PNL: {totalPnl >= 0 ? '+' : ''}{totalPnl.toFixed(2)}
          </Typography.Text>
        </Space>
      }
      extra={
        <Space size="small">
          {selectedKeys.length > 0 && (
            <Popconfirm
              title={`Square off ${selectedKeys.length} selected position(s)?`}
              description="Each row exits whatever lot count is currently set in its Exit box."
              onConfirm={squareOffSelected}
              okText="Square off selected"
              okButtonProps={{ danger: true }}
            >
              <Button danger size="small" icon={<CloseCircleOutlined />}>
                Square off selected ({selectedKeys.length})
              </Button>
            </Popconfirm>
          )}
          <Tooltip title="Refresh now (also auto-refreshes every 60s while this page is open)">
            <Button
              size="small"
              icon={<ReloadOutlined spin={!!refreshing} />}
              onClick={onRefresh}
              aria-label="Refresh positions"
            />
          </Tooltip>
        </Space>
      }
    >
      {error && <Alert type="error" message={error} closable onClose={() => setError(null)} style={{ marginBottom: 12 }} />}
      {positions.length === 0 ? (
        <Typography.Text type="secondary">No open positions right now.</Typography.Text>
      ) : (
        <Table
          rowKey={rowKey}
          dataSource={positions}
          columns={columns}
          size="small"
          pagination={false}
          scroll={{ x: 'max-content' }}
          rowSelection={{
            selectedRowKeys: selectedKeys,
            onChange: (keys) => setSelectedKeys(keys),
          }}
        />
      )}
    </Card>
  )
}
