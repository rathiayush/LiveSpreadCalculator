import { useEffect, useMemo, useRef, useState } from 'react'
import { Card, Segmented, Space, Typography, Table, Tag, Empty, Statistic, Row, Col } from 'antd'
import { createChart, LineSeries, CrosshairMode } from 'lightweight-charts'
import { useCdvSnapshots, useCdvSeries } from '../hooks/spreadHub'

// Live CE/PE Cumulative Delta Volume (CDV) chart + tables for an index option chain's order flow -
// see CdvHostedService on the backend for the full computation (a port of the "delta auto with
// Delta-Weighted CDV" Pine Script indicator, adapted to this app's real tick data). One line each
// for CE (calls) and PE (puts), updated once per bar (Cdv:TimeframeSeconds, default 60s) via the
// "cdvUpdate" SignalR push.

const CE_COLOR = '#3ddc84'
const PE_COLOR = '#ff5c5c'
const AXIS_COLOR = '#9aa4b2'
const GRID_COLOR = '#ffffff14'
const BORDER_COLOR = '#ffffff22'

function pctColor(v) {
  if (v == null) return undefined
  return v > 0 ? CE_COLOR : v < 0 ? PE_COLOR : undefined
}

function fmtPct(v) {
  return v == null ? '—' : `${v >= 0 ? '+' : ''}${Number(v).toFixed(2)}%`
}

function fmtNum(v) {
  return v == null ? '—' : Number(v).toLocaleString(undefined, { maximumFractionDigits: 0 })
}

function CdvChart({ series }) {
  const containerRef = useRef(null)
  const chartRef = useRef(null)
  const ceRef = useRef(null)
  const peRef = useRef(null)

  useEffect(() => {
    if (!containerRef.current) return

    const chart = createChart(containerRef.current, {
      width: containerRef.current.clientWidth,
      height: 360,
      layout: { background: { color: 'transparent' }, textColor: AXIS_COLOR, fontSize: 11 },
      grid: { vertLines: { color: GRID_COLOR }, horzLines: { color: GRID_COLOR } },
      rightPriceScale: { borderColor: BORDER_COLOR },
      timeScale: { borderColor: BORDER_COLOR, timeVisible: true, secondsVisible: false },
      crosshair: { mode: CrosshairMode.Normal },
      handleScroll: { mouseWheel: true, pressedMouseMove: true, horzTouchDrag: true, vertTouchDrag: true },
      handleScale: { mouseWheel: true, pinch: true, axisPressedMouseMove: true, axisDoubleClickReset: true },
    })
    chartRef.current = chart

    ceRef.current = chart.addSeries(LineSeries, { color: CE_COLOR, lineWidth: 2, title: 'CE', priceLineVisible: false })
    peRef.current = chart.addSeries(LineSeries, { color: PE_COLOR, lineWidth: 2, title: 'PE', priceLineVisible: false })

    const onResize = () => {
      if (containerRef.current) chart.applyOptions({ width: containerRef.current.clientWidth })
    }
    window.addEventListener('resize', onResize)

    return () => {
      window.removeEventListener('resize', onResize)
      chart.remove()
      chartRef.current = null
      ceRef.current = null
      peRef.current = null
    }
  }, [])

  useEffect(() => {
    if (!ceRef.current || !peRef.current) return
    ceRef.current.setData(series.map((p) => ({ time: p.time, value: p.ce })))
    peRef.current.setData(series.map((p) => ({ time: p.time, value: p.pe })))
    chartRef.current?.timeScale().fitContent()
  }, [series])

  return <div ref={containerRef} style={{ width: '100%', height: 360 }} />
}

function LookbackTable({ multiLookback }) {
  const columns = [
    { title: 'Lookback', dataIndex: 'bars', key: 'bars', render: (b) => `±${b} bars` },
    {
      title: 'CE % change',
      dataIndex: 'cePercent',
      key: 'ce',
      render: (v, row) => (
        <Tag color={row.ceHit ? (v >= 0 ? 'green' : 'red') : undefined} style={{ color: row.ceHit ? undefined : pctColor(v) }}>
          {fmtPct(v)}
        </Tag>
      ),
    },
    {
      title: 'PE % change',
      dataIndex: 'pePercent',
      key: 'pe',
      render: (v, row) => (
        <Tag color={row.peHit ? (v >= 0 ? 'green' : 'red') : undefined} style={{ color: row.peHit ? undefined : pctColor(v) }}>
          {fmtPct(v)}
        </Tag>
      ),
    },
  ]
  return (
    <Table
      size="small"
      pagination={false}
      columns={columns}
      dataSource={(multiLookback || []).map((r) => ({ ...r, key: r.bars }))}
    />
  )
}

export default function CdvPage() {
  const snapshots = useCdvSnapshots()
  const underlyings = useMemo(() => Object.keys(snapshots).sort(), [snapshots])
  // Manual pick from the Segmented control; falls back to the first available underlying whenever
  // nothing has been picked yet (or the pick is no longer in the list) - derived during render
  // rather than synced via an effect, so there's no extra render/setState round-trip.
  const [manualSelected, setManualSelected] = useState(null)
  const selected = manualSelected && underlyings.includes(manualSelected) ? manualSelected : (underlyings[0] ?? null)

  const snapshot = selected ? snapshots[selected] : null
  const series = useCdvSeries(selected)

  if (underlyings.length === 0) {
    return (
      <div style={{ padding: 24 }}>
        <Empty description="No CDV data yet - waiting for the first bar from the backend (Cdv:Enabled must be true and Dhan must be the active broker platform)." />
      </div>
    )
  }

  return (
    <Space direction="vertical" size={16} style={{ width: '100%', padding: '16px 0' }}>
      <Space wrap size={12} style={{ justifyContent: 'space-between', width: '100%' }}>
        <Segmented value={selected} onChange={setManualSelected} options={underlyings} />
        {snapshot && (
          <Typography.Text type="secondary">
            ATM {snapshot.atmStrike} · Expiry {snapshot.expiry} · Spot {fmtNum(snapshot.spotPrice)} · Bar #{snapshot.barCount}
          </Typography.Text>
        )}
      </Space>

      {snapshot && (
        <Row gutter={[12, 12]}>
          <Col xs={12} md={6}>
            <Card size="small"><Statistic title="CE (EMA)" value={snapshot.ceEma} precision={0} valueStyle={{ color: CE_COLOR }} /></Card>
          </Col>
          <Col xs={12} md={6}>
            <Card size="small"><Statistic title="PE (EMA)" value={snapshot.peEma} precision={0} valueStyle={{ color: PE_COLOR }} /></Card>
          </Col>
          <Col xs={12} md={6}>
            <Card size="small">
              <Statistic
                title="Lookback % (CE)"
                value={fmtPct(snapshot.ceLookbackPercent)}
                valueStyle={{ color: pctColor(snapshot.ceLookbackPercent) }}
              />
            </Card>
          </Col>
          <Col xs={12} md={6}>
            <Card size="small">
              <Statistic
                title="Lookback % (PE)"
                value={fmtPct(snapshot.peLookbackPercent)}
                valueStyle={{ color: pctColor(snapshot.peLookbackPercent) }}
              />
            </Card>
          </Col>
        </Row>
      )}

      <Card size="small" title="CE / PE Cumulative Delta Volume (EMA)">
        {series.length > 0 ? <CdvChart series={series} /> : <Empty description="Waiting for enough bars to plot" />}
      </Card>

      <Row gutter={[12, 12]}>
        <Col xs={24} md={12}>
          <Card size="small" title="Multi-lookback % change">
            <LookbackTable multiLookback={snapshot?.multiLookback} />
          </Card>
        </Col>
        <Col xs={24} md={12}>
          <Card size="small" title="Frozen-bar % change">
            {snapshot?.ceFrozenValue == null ? (
              <Typography.Text type="secondary">Frozen bar not captured yet today.</Typography.Text>
            ) : (
              <Table
                size="small"
                pagination={false}
                columns={[
                  { title: '', dataIndex: 'label', key: 'label' },
                  {
                    title: 'CE',
                    dataIndex: 'ce',
                    key: 'ce',
                    render: (v) => <span style={{ color: pctColor(v) }}>{fmtPct(v)}</span>,
                  },
                  {
                    title: 'PE',
                    dataIndex: 'pe',
                    key: 'pe',
                    render: (v) => <span style={{ color: pctColor(v) }}>{fmtPct(v)}</span>,
                  },
                ]}
                dataSource={[{ key: 'chg', label: '% change vs frozen bar', ce: snapshot.ceFrozenPercent, pe: snapshot.peFrozenPercent }]}
              />
            )}
          </Card>
        </Col>
      </Row>
    </Space>
  )
}
