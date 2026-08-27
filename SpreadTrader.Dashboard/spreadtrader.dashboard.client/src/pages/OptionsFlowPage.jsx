import { useEffect, useMemo, useRef, useState } from 'react'
import { Card, Segmented, Space, Table, Tag, Typography, Alert, Statistic, Row, Col, Empty, Switch } from 'antd'
import { createChart, LineSeries, LineStyle, CrosshairMode } from 'lightweight-charts'
import { api } from '../api'
import { useOptionsFlow, seedOptionsFlow } from '../hooks/spreadHub'

// Matches the reference TradingView indicator's colours: calls green, puts red.
const CE_COLOR = '#26de81'
const PE_COLOR = '#ff5c5c'
const GRID_COLOR = '#ffffff12'
const BORDER_COLOR = '#ffffff24'
const AXIS_COLOR = '#9aa4b2'

const IST_OFFSET_SECONDS = 5.5 * 3600

// lightweight-charts renders every timestamp in UTC. Shifting by IST's fixed +5:30 makes the axis
// read as IST wall-clock without the library needing a timezone - the same trick SpreadHistoryModal
// already uses, kept identical so both charts label time the same way.
function toChartTime(iso) {
  return Math.floor(new Date(iso).getTime() / 1000) + IST_OFFSET_SECONDS
}

function formatIst(seconds) {
  const d = new Date((seconds - IST_OFFSET_SECONDS) * 1000 + IST_OFFSET_SECONDS * 1000)
  return d.toISOString().slice(11, 16)
}

// Big numbers are the norm here (volume delta across 16 strikes runs into millions), so the axis
// and tables read in K/M rather than showing nine digits.
function formatCompact(value) {
  if (value === null || value === undefined || Number.isNaN(value)) return '-'
  const abs = Math.abs(value)
  if (abs >= 1_000_000) return `${(value / 1_000_000).toFixed(2)}M`
  if (abs >= 1_000) return `${(value / 1_000).toFixed(2)}K`
  return value.toFixed(1)
}

/**
 * Index-option flow: aggregated CE vs PE cumulative volume delta (CDV) and on-balance volume (OBV)
 * across an ATM-centred strike window, plus the crossover signals derived from them.
 *
 * The series is computed entirely on the backend (see OptionsFlowEngine) and pushed whole on every
 * refresh rather than appended to - an ATM shift rebuilds the entire day from the session open, so
 * there is no stable "just add the newest point" contract for this page to rely on.
 */
export default function OptionsFlowPage() {
  const flow = useOptionsFlow()
  const [status, setStatus] = useState(null)
  const [index, setIndex] = useState(null)
  const [metric, setMetric] = useState('CDV')
  const [showCe, setShowCe] = useState(true)
  const [showPe, setShowPe] = useState(true)
  const [error, setError] = useState(null)

  const containerRef = useRef(null)
  const chartRef = useRef(null)
  const ceSeriesRef = useRef(null)
  const peSeriesRef = useRef(null)

  // Feature status decides whether there is anything to show at all; the snapshot seed fills the
  // chart immediately rather than waiting up to a second for the first push.
  useEffect(() => {
    let cancelled = false
    api.getOptionsFlowStatus()
      .then((s) => {
        if (cancelled) return
        setStatus(s)
        if (s?.indices?.length) setIndex((current) => current ?? s.indices[0])
      })
      .catch((err) => !cancelled && setError(err.message))

    api.getOptionsFlowSnapshots()
      .then((snapshots) => !cancelled && seedOptionsFlow(snapshots))
      .catch(() => { /* the SignalR push will fill this in shortly */ })

    return () => { cancelled = true }
  }, [])

  const snapshot = index ? flow[index] : null

  const series = useMemo(() => {
    if (!snapshot) return { ce: [], pe: [] }
    const pick = (p) => (metric === 'CDV' ? p.cdv : p.obv)
    return {
      ce: (snapshot.call ?? []).map((p) => ({ time: toChartTime(p.minuteUtc), value: pick(p) })),
      pe: (snapshot.put ?? []).map((p) => ({ time: toChartTime(p.minuteUtc), value: pick(p) })),
    }
  }, [snapshot, metric])

  // Build the chart once, then feed it new data on every push. Recreating it per update would
  // reset the user's zoom/pan on every single second.
  useEffect(() => {
    if (!containerRef.current || chartRef.current) return

    const chart = createChart(containerRef.current, {
      width: containerRef.current.clientWidth,
      height: 380,
      layout: { background: { color: 'transparent' }, textColor: AXIS_COLOR, fontSize: 11 },
      grid: { vertLines: { color: GRID_COLOR }, horzLines: { color: GRID_COLOR } },
      rightPriceScale: { borderColor: BORDER_COLOR },
      localization: { timeFormatter: (t) => `${formatIst(t)} IST`, priceFormatter: formatCompact },
      timeScale: { borderColor: BORDER_COLOR, timeVisible: true, secondsVisible: false },
      crosshair: { mode: CrosshairMode.Normal },
      handleScroll: { mouseWheel: true, pressedMouseMove: true, horzTouchDrag: true, vertTouchDrag: true },
      handleScale: { mouseWheel: true, pinch: true, axisPressedMouseMove: true, axisDoubleClickReset: true },
    })

    ceSeriesRef.current = chart.addSeries(LineSeries, { color: CE_COLOR, lineWidth: 2, priceLineVisible: false, title: 'CE' })
    peSeriesRef.current = chart.addSeries(LineSeries, { color: PE_COLOR, lineWidth: 2, priceLineVisible: false, title: 'PE' })

    // The zero line is the reference the whole reading hangs off - both series start the session there.
    ceSeriesRef.current.createPriceLine({
      price: 0, color: '#ffffff40', lineStyle: LineStyle.Dashed, lineWidth: 1, axisLabelVisible: false,
    })

    chartRef.current = chart

    const onResize = () => containerRef.current && chart.applyOptions({ width: containerRef.current.clientWidth })
    window.addEventListener('resize', onResize)

    return () => {
      window.removeEventListener('resize', onResize)
      chart.remove()
      chartRef.current = null
      ceSeriesRef.current = null
      peSeriesRef.current = null
    }
  }, [])

  const fittedRef = useRef(false)
  useEffect(() => { fittedRef.current = false }, [index, metric])

  useEffect(() => {
    if (!ceSeriesRef.current || !peSeriesRef.current) return
    ceSeriesRef.current.setData(series.ce)
    peSeriesRef.current.setData(series.pe)
    ceSeriesRef.current.applyOptions({ visible: showCe })
    peSeriesRef.current.applyOptions({ visible: showPe })

    // Fit once per index/metric change, then leave the viewport alone so a live update every second
    // doesn't yank the chart back while you're inspecting an earlier part of the day.
    if (!fittedRef.current && (series.ce.length || series.pe.length)) {
      chartRef.current?.timeScale().fitContent()
      fittedRef.current = true
    }
  }, [series, showCe, showPe])

  const signalRows = useMemo(() => {
    if (!snapshot?.signals) return []
    return snapshot.signals
      .filter((s) => s.metric === metric)
      .map((s, i) => ({ key: `${s.kind}-${s.scope}-${i}`, ...s }))
  }, [snapshot, metric])

  const signalColumns = useMemo(() => [
    {
      title: 'Signal',
      dataIndex: 'kind',
      render: (kind, row) => (kind === 'CePeCross' ? 'CE / PE cross' : `${row.scope} EMA ${status?.signalFast ?? 9}/${status?.signalSlow ?? 21}`),
    },
    { title: 'On', dataIndex: 'scope' },
    {
      title: 'State',
      dataIndex: 'bullish',
      render: (bullish, row) => (
        <Tag color={bullish ? 'green' : 'red'}>
          {row.kind === 'CePeCross'
            ? (bullish ? 'CE above PE' : 'PE above CE')
            : (bullish ? 'Bullish' : 'Bearish')}
        </Tag>
      ),
    },
    {
      title: 'Crossed',
      dataIndex: 'atUtc',
      render: (atUtc, row) =>
        row.barsAgo < 0
          ? <Typography.Text type="secondary">no cross today</Typography.Text>
          : `${new Date(atUtc).toLocaleTimeString('en-IN', { timeZone: 'Asia/Kolkata', hour: '2-digit', minute: '2-digit' })} IST`,
    },
    {
      title: 'Bars ago',
      dataIndex: 'barsAgo',
      render: (barsAgo) => (barsAgo < 0 ? '-' : barsAgo),
    },
    { title: 'Fast / CE', dataIndex: 'fast', render: formatCompact },
    { title: 'Slow / PE', dataIndex: 'slow', render: formatCompact },
  ], [status])

  if (error) return <Alert type="error" showIcon message="Could not load Options Flow" description={error} />

  if (status && !status.enabled) {
    return (
      <Alert
        type="info"
        showIcon
        message="Options Flow is disabled"
        description="Set OptionsFlow:Enabled to true (and run on Dhan) to enable the index-option CDV/OBV page."
      />
    )
  }

  return (
    <Space direction="vertical" size="middle" style={{ width: '100%' }}>
      <Card
        size="small"
        title={
          <Space wrap>
            <Segmented
              value={index ?? undefined}
              onChange={setIndex}
              options={status?.indices ?? []}
            />
            <Segmented value={metric} onChange={setMetric} options={['CDV', 'OBV']} />
          </Space>
        }
        extra={
          <Space size="middle">
            <Space size="small"><Switch size="small" checked={showCe} onChange={setShowCe} /><Tag color="green">CE</Tag></Space>
            <Space size="small"><Switch size="small" checked={showPe} onChange={setShowPe} /><Tag color="red">PE</Tag></Space>
          </Space>
        }
      >
        {snapshot ? (
          <Row gutter={[16, 8]} style={{ marginBottom: 12 }}>
            <Col><Statistic title="Spot" value={snapshot.spot} precision={2} /></Col>
            <Col><Statistic title="ATM" value={snapshot.atmStrike} precision={0} /></Col>
            <Col><Statistic title="Strikes / side" value={snapshot.strikeCount} /></Col>
            <Col><Statistic title="Expiry" value={snapshot.expiry ?? '-'} /></Col>
            <Col>
              <Statistic
                title="Series from"
                valueRender={() => (
                  <Tag color={snapshot.backfilled ? 'blue' : 'orange'}>
                    {snapshot.backfilled ? 'session open (backfilled)' : 'process start'}
                  </Tag>
                )}
              />
            </Col>
            <Col>
              <Statistic
                title="Delta weighting"
                valueRender={() => <Tag color={snapshot.deltaWeighted ? 'green' : 'default'}>{snapshot.deltaWeighted ? 'ON' : 'OFF'}</Tag>}
              />
            </Col>
          </Row>
        ) : (
          <Empty description="Waiting for the first Options Flow update - the strike window may still be resolving." />
        )}

        <div ref={containerRef} style={{ width: '100%' }} />
      </Card>

      <Card size="small" title={`Crossover signals (${metric})`}>
        <Table
          size="small"
          pagination={false}
          dataSource={signalRows}
          columns={signalColumns}
          locale={{ emptyText: 'No signals yet - needs at least a few minutes of data.' }}
        />
      </Card>
    </Space>
  )
}
