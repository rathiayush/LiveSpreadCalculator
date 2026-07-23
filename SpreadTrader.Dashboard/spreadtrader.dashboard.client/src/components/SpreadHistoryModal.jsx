import { useEffect, useMemo, useState } from 'react'
import { Modal, Spin, Empty, Alert, Segmented, Space, Typography } from 'antd'
import {
  ResponsiveContainer, LineChart, Line, XAxis, YAxis, CartesianGrid,
  Tooltip, Legend, ReferenceLine,
} from 'recharts'
import { api } from '../api'

// "Live Spread" history popup: datetime (x) vs spread value (y), plotted from the hourly snapshots
// persisted server-side (see EfSpreadHistoryStore / GET /api/spreads/{id}/history). Rendered with
// Recharts. Both executable directions are stored each capture, so this can show the Buy spread
// line, the Sell spread line, or both.
//
// Fully responsive: the modal width tracks the viewport (near full-width on phones), the chart
// height and axis density shrink on small screens, and the header controls wrap - so it's usable on
// mobile as well as desktop.

const BUY_COLOR = '#3ddc84'
const SELL_COLOR = '#ff5c5c'
const AXIS_COLOR = '#9aa4b2'

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

  // Recharts wants a flat data array; keep a numeric timestamp for correct time spacing on the axis.
  const data = useMemo(() => points.map((p) => ({
    t: new Date(p.capturedAt).getTime(),
    buy: p.buySpreadValue,
    sell: p.sellSpreadValue,
  })), [points])

  const showBuy = series === 'Both' || series === 'Buy'
  const showSell = series === 'Both' || series === 'Sell'

  // When there are many snapshots, widen the chart so points don't cram together and let the
  // container scroll horizontally instead. When there are few, it just fills the modal (no scroll).
  const pxPerPoint = isMobile ? 22 : 30
  const minChartWidth = Math.max(280, modalWidth - (isMobile ? 24 : 48))
  const chartPixelWidth = Math.max(minChartWidth, data.length * pxPerPoint)

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
          <Space wrap size={[8, 8]}>
            <Segmented value={series} onChange={setSeries} options={['Both', 'Buy', 'Sell']} size={isMobile ? 'small' : 'middle'} />
            <Typography.Text type="secondary">{points.length} snapshot{points.length === 1 ? '' : 's'}</Typography.Text>
          </Space>

          <div style={{ width: '100%', height: chartHeight, overflowX: 'auto', overflowY: 'hidden' }}>
            <div style={{ width: chartPixelWidth, height: '100%' }}>
            <ResponsiveContainer width="100%" height="100%">
              <LineChart data={data} margin={{ top: 10, right: isMobile ? 8 : 20, bottom: 8, left: isMobile ? -8 : 4 }}>
                <CartesianGrid strokeDasharray="3 3" stroke="#ffffff14" />
                <XAxis
                  dataKey="t"
                  type="number"
                  scale="time"
                  domain={['dataMin', 'dataMax']}
                  tickFormatter={(t) => formatAxisTime(t, isMobile)}
                  tick={{ fill: AXIS_COLOR, fontSize: isMobile ? 9 : 11 }}
                  stroke={AXIS_COLOR}
                  minTickGap={isMobile ? 56 : 40}
                />
                <YAxis
                  tickFormatter={(v) => Number(v).toFixed(isMobile ? 1 : 2)}
                  tick={{ fill: AXIS_COLOR, fontSize: isMobile ? 9 : 11 }}
                  stroke={AXIS_COLOR}
                  width={isMobile ? 40 : 56}
                  domain={['auto', 'auto']}
                />
                <ReferenceLine y={0} stroke="#ffffff40" strokeDasharray="4 4" />
                <Tooltip
                  contentStyle={{ background: '#1f2430', border: '1px solid #ffffff22', borderRadius: 6, fontSize: 12 }}
                  labelStyle={{ color: '#c9d1d9' }}
                  labelFormatter={(t) => new Date(t).toLocaleString()}
                  formatter={(value, name) => [Number(value).toFixed(2), name === 'buy' ? 'Buy spread' : 'Sell spread']}
                />
                <Legend
                  formatter={(v) => (v === 'buy'
                    ? (isMobile ? 'Buy' : 'Buy spread (BuyNear / SellFar)')
                    : (isMobile ? 'Sell' : 'Sell spread (SellNear / BuyFar)'))}
                  wrapperStyle={{ fontSize: 12 }}
                />
                {showBuy && <Line type="monotone" dataKey="buy" stroke={BUY_COLOR} strokeWidth={1.75} dot={{ r: isMobile ? 1.5 : 2 }} activeDot={{ r: 4 }} isAnimationActive={false} />}
                {showSell && <Line type="monotone" dataKey="sell" stroke={SELL_COLOR} strokeWidth={1.75} dot={{ r: isMobile ? 1.5 : 2 }} activeDot={{ r: 4 }} isAnimationActive={false} />}
              </LineChart>
            </ResponsiveContainer>
            </div>
          </div>
        </Space>
      )}
    </Modal>
  )
}

function formatAxisTime(t, isMobile) {
  const d = new Date(t)
  const hm = `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`
  const dm = `${d.getDate()}/${d.getMonth() + 1}`
  // Single-line label (SVG axis ticks don't wrap). Fewer ticks on mobile via minTickGap keeps it
  // readable; show only the time at midday-ish ticks would need custom logic, so keep D/M HH:mm.
  return isMobile ? `${dm} ${hm}` : `${dm} ${hm}`
}
