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
  const data = useMemo(() => {
    const byTime = new Map()
    for (const p of points) {
      const time = Math.floor(new Date(p.capturedAt).getTime() / 1000)
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
      timeScale: { borderColor: BORDER_COLOR, timeVisible: true, secondsVisible: false },
      crosshair: { mode: CrosshairMode.Normal },
      handleScroll: true,
      handleScale: true,
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
            Scroll or pinch to zoom, drag to pan.
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
      <Typography.Text style={{ fontSize: 12 }}>{new Date(hover.time * 1000).toLocaleString()}</Typography.Text>
      {showBuy && hover.buy != null && (
        <Typography.Text style={{ fontSize: 12, color: BUY_COLOR }}>Buy: {Number(hover.buy).toFixed(2)}</Typography.Text>
      )}
      {showSell && hover.sell != null && (
        <Typography.Text style={{ fontSize: 12, color: SELL_COLOR }}>Sell: {Number(hover.sell).toFixed(2)}</Typography.Text>
      )}
    </Space>
  )
}
