import { useEffect, useMemo, useRef, useState } from 'react'
import { Modal, Spin, Empty, Alert, Segmented, Space, Typography, Button } from 'antd'
import { ZoomOutOutlined } from '@ant-design/icons'
import { createChart, LineSeries, LineStyle, CrosshairMode } from 'lightweight-charts'
import { api } from '../api'

// "Live Spread" history popup: datetime (x) vs spread value (y), plotted from the hourly snapshots
// persisted server-side (see EfSpreadHistoryStore / GET /api/spreads/{id}/history). Rendered with
// TradingView's lightweight-charts, which gives mouse-wheel/pinch zoom and drag-to-pan over the
// full series out of the box. Both executable directions are stored each capture, so this can show
// the Buy spread line, the Sell spread line, or both.
//
// Fully responsive: the modal width tracks the viewport (near full-width on phones), the chart
// height and axis density shrink on small screens, and the header controls wrap - so it's usable on
// mobile as well as desktop.

const BUY_COLOR = '#3ddc84'
const SELL_COLOR = '#ff5c5c'
const AXIS_COLOR = '#9aa4b2'
const GRID_COLOR = '#ffffff14'
const BORDER_COLOR = '#ffffff22'

// lightweight-charts has no timezone option: it always renders a UNIX timestamp as UTC. Snapshots
// are captured during IST market hours (09:29-15:29), so plotting the raw epoch labelled every
// point 5.5h early - the 09:29 capture drew at 03:59.
//
// The library's documented workaround is to feed it timestamps pre-shifted into the target zone, so
// its UTC rendering lands on IST wall-clock. Every timestamp from `data` onwards is therefore a
// DISPLAY time (true epoch + 5.5h), including the `time` handed back by crosshair events; read it
// with UTC getters to recover the IST wall-clock, or subtract IST_OFFSET_SECONDS for the true
// instant. Shifting rather than relying on the browser's own zone is what makes the axis read IST
// for a viewer sitting anywhere in the world.
const IST_OFFSET_SECONDS = 5.5 * 60 * 60

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']
const pad2 = (n) => String(n).padStart(2, '0')

// Formats a shifted display timestamp. UTC getters are correct here precisely because the value was
// shifted: reading it as UTC yields the IST wall-clock we want to show.
function formatIst(displayTimeSeconds, { withDate = true, withTime = true } = {}) {
  const d = new Date(displayTimeSeconds * 1000)
  const date = `${pad2(d.getUTCDate())} ${MONTHS[d.getUTCMonth()]}`
  const time = `${pad2(d.getUTCHours())}:${pad2(d.getUTCMinutes())}`
  if (withDate && withTime) return `${date} ${time}`
  return withDate ? date : time
}

// TickMarkType values, inlined rather than imported so the axis keeps formatting correctly even if
// the library stops exporting the enum as a runtime value: 0 Year, 1 Month, 2 DayOfMonth,
// 3 Time, 4 TimeWithSeconds. Day-level ticks show the date, intraday ticks show HH:mm.
const TICK_DATE_TYPES = new Set([0, 1, 2])

// Tracks the viewport width so the modal/chart can size to the device (updates on rotate/resize).
function useViewportWidth() {
  const [width, setWidth] = useState(typeof window !== 'undefined' ? window.innerWidth : 1024)
  useEffect(() => {
    const onResize = () => setWidth(window.innerWidth)
    window.addEventListener('resize', onResize)
    window.addEventListener('orientationchange', onResize)
    return () => {
      window.removeEventListener('resize', onResize)
      window.removeEventListener('orientationchange', onResize)
    }
  }, [])
  return width
}

export default function SpreadHistoryModal({ spread, open, onClose }) {
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState(null)
  const [points, setPoints] = useState([])
  const [series, setSeries] = useState('Both')
  const [hover, setHover] = useState(null)

  const containerRef = useRef(null)
  const chartRef = useRef(null)
  const buySeriesRef = useRef(null)
  const sellSeriesRef = useRef(null)

  const vw = useViewportWidth()
  const isMobile = vw < 576
  // Full-width-ish on phones (leave a small gutter), capped at 820 on desktop.
  const modalWidth = Math.min(820, vw - (isMobile ? 16 : 24))
  const chartHeight = isMobile ? 300 : 380

  const spreadId = spread?.id

  useEffect(() => {
    if (!open || !spreadId) return
    let cancelled = false
    setLoading(true)
    setError(null)
    // Explicitly fetch only the last 30 days (the backend also defaults to this, but pinning it
    // client-side keeps the payload bounded regardless of any server-side default change).
    const from = new Date(Date.now() - 30 * 24 * 60 * 60 * 1000).toISOString()
    api.getSpreadHistory(spreadId, from)
      .then((data) => { if (!cancelled) setPoints(Array.isArray(data) ? data : []) })
      .catch((e) => { if (!cancelled) setError(e.message || 'Failed to load history') })
      .finally(() => { if (!cancelled) setLoading(false) })
    return () => { cancelled = true }
  }, [open, spreadId])

  // lightweight-charts wants unix seconds and strictly ascending, de-duplicated time points.
  // capturedAt carries its own offset (+05:30), so Date parses the correct instant regardless of
  // where the browser is; the +IST_OFFSET_SECONDS shift is purely so the chart's UTC rendering
  // prints IST wall-clock. See the note beside IST_OFFSET_SECONDS.
  const data = useMemo(() => {
    const byTime = new Map()
    for (const p of points) {
      const time = Math.floor(new Date(p.capturedAt).getTime() / 1000) + IST_OFFSET_SECONDS
      byTime.set(time, { time, buy: p.buySpreadValue, sell: p.sellSpreadValue })
    }
    return [...byTime.values()].sort((a, b) => a.time - b.time)
  }, [points])

  const showBuy = series === 'Both' || series === 'Buy'
  const showSell = series === 'Both' || series === 'Sell'

  // Create the chart once per dataset and tear it down on unmount/refresh.
  useEffect(() => {
    if (!containerRef.current || data.length === 0) return

    const chart = createChart(containerRef.current, {
      width: containerRef.current.clientWidth,
      height: chartHeight,
      layout: { background: { color: 'transparent' }, textColor: AXIS_COLOR, fontSize: isMobile ? 9 : 11 },
      grid: { vertLines: { color: GRID_COLOR }, horzLines: { color: GRID_COLOR } },
      rightPriceScale: { borderColor: BORDER_COLOR },
      // Both formatters read the shifted timestamps as IST wall-clock, so the axis ticks and the
      // crosshair's time label agree with each other and with the HoverReadout below.
      localization: { timeFormatter: (time) => `${formatIst(time)} IST` },
      timeScale: {
        borderColor: BORDER_COLOR,
        timeVisible: true,
        secondsVisible: false,
        tickMarkFormatter: (time, tickMarkType) =>
          formatIst(time, { withDate: TICK_DATE_TYPES.has(tickMarkType), withTime: !TICK_DATE_TYPES.has(tickMarkType) }),
      },
      crosshair: { mode: CrosshairMode.Normal },
      // Pass full option objects rather than `true` shorthand - the library's option merge
      // overwrites the whole sub-object when given a boolean, which silently disables every
      // individual flag (pressedMouseMove, mouseWheel, touch drag, pinch, etc).
      handleScroll: { mouseWheel: true, pressedMouseMove: true, horzTouchDrag: true, vertTouchDrag: true },
      handleScale: { mouseWheel: true, pinch: true, axisPressedMouseMove: true, axisDoubleClickReset: true },
    })
    chartRef.current = chart

    const buySeries = chart.addSeries(LineSeries, {
      color: BUY_COLOR, lineWidth: 2, priceLineVisible: false, lastValueVisible: true, visible: showBuy,
    })
    const sellSeries = chart.addSeries(LineSeries, {
      color: SELL_COLOR, lineWidth: 2, priceLineVisible: false, lastValueVisible: true, visible: showSell,
    })
    buySeriesRef.current = buySeries
    sellSeriesRef.current = sellSeries

    buySeries.createPriceLine({
      price: 0, color: '#ffffff40', lineStyle: LineStyle.Dashed, lineWidth: 1, axisLabelVisible: false,
    })

    buySeries.setData(data.map((d) => ({ time: d.time, value: d.buy })))
    sellSeries.setData(data.map((d) => ({ time: d.time, value: d.sell })))
    chart.timeScale().fitContent()

    chart.subscribeCrosshairMove((param) => {
      if (!param.time || !param.point) { setHover(null); return }
      setHover({
        time: param.time,
        buy: param.seriesData.get(buySeries)?.value,
        sell: param.seriesData.get(sellSeries)?.value,
      })
    })

    const onResize = () => {
      if (containerRef.current) chart.applyOptions({ width: containerRef.current.clientWidth })
    }
    window.addEventListener('resize', onResize)

    return () => {
      window.removeEventListener('resize', onResize)
      chart.remove()
      chartRef.current = null
      buySeriesRef.current = null
      sellSeriesRef.current = null
      setHover(null)
    }
    // Series toggling is handled by the effect below without recreating the chart; only rebuild
    // when the underlying dataset or sizing actually changes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [data, chartHeight, isMobile])

  // Toggle Buy/Sell visibility in place, no need to rebuild the chart.
  useEffect(() => {
    buySeriesRef.current?.applyOptions({ visible: showBuy })
    sellSeriesRef.current?.applyOptions({ visible: showSell })
  }, [showBuy, showSell])

  const resetZoom = () => chartRef.current?.timeScale().fitContent()

  return (
    <Modal
      open={open}
      onCancel={onClose}
      footer={null}
      width={modalWidth}
      centered={isMobile}
      style={{ maxWidth: '100vw', top: isMobile ? undefined : 40 }}
      styles={{ body: { paddingTop: 8 } }}
      title={spread ? `Spread history — ${spread.underlying}${isMobile ? '' : ` (${spread.id})`}` : 'Spread history'}
      destroyOnClose
    >
      {loading ? (
        <div style={{ textAlign: 'center', padding: 48 }}><Spin /></div>
      ) : error ? (
        <Alert type="error" showIcon message="Could not load history" description={error} />
      ) : points.length === 0 ? (
        <Empty description="No history captured yet. Snapshots are recorded hourly during market hours." />
      ) : (
        <Space direction="vertical" size={12} style={{ width: '100%' }}>
          <Space wrap size={[8, 8]} style={{ width: '100%', justifyContent: 'space-between' }}>
            <Space wrap size={[8, 8]}>
              <Segmented value={series} onChange={setSeries} options={['Both', 'Buy', 'Sell']} size={isMobile ? 'small' : 'middle'} />
              <Typography.Text type="secondary">{points.length} snapshot{points.length === 1 ? '' : 's'}</Typography.Text>
            </Space>
            <Button size="small" icon={<ZoomOutOutlined />} onClick={resetZoom}>Reset zoom</Button>
          </Space>

          <Legend showBuy={showBuy} showSell={showSell} isMobile={isMobile} />

          <div ref={containerRef} style={{ width: '100%', height: chartHeight }} />

          <HoverReadout hover={hover} showBuy={showBuy} showSell={showSell} />

          <Typography.Text type="secondary" style={{ fontSize: 11 }}>
            Scroll or pinch to zoom, drag to pan. All times are IST.
          </Typography.Text>
        </Space>
      )}
    </Modal>
  )
}

function Legend({ showBuy, showSell, isMobile }) {
  return (
    <Space size={16} wrap>
      {showBuy && (
        <Space size={6}>
          <span style={{ display: 'inline-block', width: 10, height: 10, borderRadius: 2, background: BUY_COLOR }} />
          <Typography.Text style={{ fontSize: 12 }}>{isMobile ? 'Buy' : 'Buy spread (BuyNear / SellFar)'}</Typography.Text>
        </Space>
      )}
      {showSell && (
        <Space size={6}>
          <span style={{ display: 'inline-block', width: 10, height: 10, borderRadius: 2, background: SELL_COLOR }} />
          <Typography.Text style={{ fontSize: 12 }}>{isMobile ? 'Sell' : 'Sell spread (SellNear / BuyFar)'}</Typography.Text>
        </Space>
      )}
    </Space>
  )
}

function HoverReadout({ hover, showBuy, showSell }) {
  if (!hover) {
    return <Typography.Text type="secondary" style={{ fontSize: 12, opacity: 0.6 }}>Hover the chart for values</Typography.Text>
  }
  return (
    <Space size={16} wrap>
      <Typography.Text style={{ fontSize: 12 }}>{formatIst(hover.time)} IST</Typography.Text>
      {showBuy && hover.buy != null && (
        <Typography.Text style={{ fontSize: 12, color: BUY_COLOR }}>Buy: {Number(hover.buy).toFixed(2)}</Typography.Text>
      )}
      {showSell && hover.sell != null && (
        <Typography.Text style={{ fontSize: 12, color: SELL_COLOR }}>Sell: {Number(hover.sell).toFixed(2)}</Typography.Text>
      )}
    </Space>
  )
}
