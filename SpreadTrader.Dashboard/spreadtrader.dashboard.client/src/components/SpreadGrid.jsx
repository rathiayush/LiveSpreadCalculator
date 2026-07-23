import { useMemo, useState } from 'react'
import { Card, Table, Typography, Input, Space, Tooltip, Button } from 'antd'
import { LineChartOutlined, AreaChartOutlined } from '@ant-design/icons'
import DirectionTag from './DirectionTag'
import FlashValue from './FlashValue'
import SpreadHistoryModal from './SpreadHistoryModal'

// Live per-spread grid with a name search (underlying / near / far tradingsymbol) and real
// pagination - lives on its own "Live Spreads" page since with a 150-200+ symbol universe a
// single unpaginated table is unusable.
export default function SpreadGrid({ definitions, liveValues }) {
  const [search, setSearch] = useState('')
  // Controlled pagination. This table re-renders ~4x/second from the live tick feed; with antd's
  // default (uncontrolled) pagination the current page lives in antd's own internal state, and the
  // constant re-render with a fresh `pagination` object each tick let it get lost / snap back to
  // page 1, which read as "pagination not working". Owning current/pageSize here makes the page
  // deterministic and immune to the live re-renders.
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(25)
  // The spread whose stored hourly history is open in the chart popup (null = closed).
  const [historySpread, setHistorySpread] = useState(null)

  // Reset to the first page whenever the search term changes, so filtering can't strand you on a
  // page number that no longer exists in the smaller result set.
  const onSearch = (value) => {
    setSearch(value)
    setPage(1)
  }

  const filtered = useMemo(() => {
    const term = search.trim().toLowerCase()
    if (!term) return definitions
    return definitions.filter((d) =>
      d.underlying.toLowerCase().includes(term) ||
      d.id.toLowerCase().includes(term) ||
      d.nearLeg.tradingSymbol.toLowerCase().includes(term) ||
      d.farLeg.tradingSymbol.toLowerCase().includes(term))
  }, [definitions, search])

  const dataSource = filtered.map((d) => ({ key: d.id, ...d, live: liveValues[d.id] }))

  const columns = [
    {
      // Opens the datetime-vs-spread history chart (hourly snapshots persisted server-side) for
      // this spread - the "click on live spread -> popup datetime vs spread" view.
      title: '',
      key: 'history',
      fixed: 'left',
      width: 44,
      render: (_, r) => (
        <Tooltip title="View spread history chart">
          <Button
            type="text"
            size="small"
            icon={<AreaChartOutlined style={{ color: '#4da3ff' }} />}
            onClick={() => setHistorySpread(r)}
          />
        </Tooltip>
      ),
    },
    {
      // width is required (not just cosmetic) on a fixed column - without it, antd can't
      // reliably compute the sticky offset for the columns after it before layout settles,
      // especially on a narrow/mobile viewport, causing this column's text to render
      // overlapping the next column instead of pushing it aside.
      title: 'Underlying',
      dataIndex: 'underlying',
      key: 'underlying',
      fixed: 'left',
      width: 140,
      sorter: (a, b) => a.underlying.localeCompare(b.underlying),
      render: (v) => <strong>{v}</strong>,
    },
    {
      // Same |spread %| the Dashboard ranks by (spreadValue as a percentage of the near leg
      // price) - computed here rather than read off the live snapshot since the per-tick
      // spreadUpdate payload doesn't carry a percent field, only the ranking snapshot does.
      // Leads the non-fixed columns (right after Underlying) since this - along with the
      // rupee Spread next to it - is the number people actually scan this grid for; legs and
      // per-leg prices are supporting detail, not the headline.
      title: 'Spread %',
      key: 'spreadPercent',
      sorter: (a, b) => {
        const pctA = a.live?.nearPrice ? (a.live.spreadValue / a.live.nearPrice) * 100 : 0
        const pctB = b.live?.nearPrice ? (b.live.spreadValue / b.live.nearPrice) * 100 : 0
        return pctA - pctB
      },
      // Default view: most negative spread % first, ascending up to most positive - still fully
      // re-sortable/reversible by clicking the header.
      defaultSortOrder: 'ascend',
      render: (_, r) => {
        if (!r.live || !r.live.nearPrice) return '—'
        const pct = (r.live.spreadValue / r.live.nearPrice) * 100
        return (
          <FlashValue value={pct}>
            <span style={{ color: pct >= 0 ? '#3ddc84' : '#ff5c5c', fontWeight: 600 }}>
              {pct.toFixed(2)}%
            </span>
          </FlashValue>
        )
      },
    },
    {
      title: 'Spread',
      key: 'spread',
      sorter: (a, b) => (a.live?.spreadValue ?? 0) - (b.live?.spreadValue ?? 0),
      render: (_, r) => r.live ? (
        <FlashValue value={r.live.spreadValue}>
          <span style={{ color: r.live.spreadValue >= 0 ? '#3ddc84' : '#ff5c5c', fontWeight: 600 }}>
            {r.live.spreadValue.toFixed(2)}
          </span>
        </FlashValue>
      ) : '—',
    },
    {
      // Always the BuyNear(ask)/SellFar(bid) price, regardless of which direction the Spread/
      // Spread % columns currently favor - see SpreadSnapshot.BuySpreadValue. Shown alongside
      // Sell spread as a two-way live quote on the spread itself, so you can see what going
      // either direction costs right now instead of only the auto-picked one.
      title: (
        <Tooltip title="What Buy Near / Sell Far would cost right now (near leg at ask, far leg at bid) - always shown, regardless of which direction the Spread column is currently favoring.">
          Buy spread
        </Tooltip>
      ),
      key: 'buySpread',
      sorter: (a, b) => (a.live?.buySpreadValue ?? 0) - (b.live?.buySpreadValue ?? 0),
      render: (_, r) => !r.live ? '—' : (
        <FlashValue value={r.live.buySpreadValue}>
          <span style={{ color: r.live.buySpreadValue >= 0 ? '#3ddc84' : '#ff5c5c' }}>
            {r.live.buySpreadValue.toFixed(2)}
          </span>
        </FlashValue>
      ),
    },
    {
      // Always the SellNear(bid)/BuyFar(ask) price - the other side of the two-way quote
      // alongside Buy spread. See SpreadSnapshot.SellSpreadValue.
      title: (
        <Tooltip title="What Sell Near / Buy Far would net right now (near leg at bid, far leg at ask) - always shown, regardless of which direction the Spread column is currently favoring.">
          Sell spread
        </Tooltip>
      ),
      key: 'sellSpread',
      sorter: (a, b) => (a.live?.sellSpreadValue ?? 0) - (b.live?.sellSpreadValue ?? 0),
      render: (_, r) => !r.live ? '—' : (
        <FlashValue value={r.live.sellSpreadValue}>
          <span style={{ color: r.live.sellSpreadValue >= 0 ? '#3ddc84' : '#ff5c5c' }}>
            {r.live.sellSpreadValue.toFixed(2)}
          </span>
        </FlashValue>
      ),
    },
    {
      // Plain gap between the two-way quote above - Sell spread minus Buy spread, no percentage
      // (a raw rupee/point difference, same units as the two columns it's derived from).
      title: (
        <Tooltip title="Sell spread minus Buy spread - the gap between the two directions' executable prices right now.">
          Spread difference
        </Tooltip>
      ),
      key: 'spreadDifference',
      sorter: (a, b) => {
        const da = a.live ? a.live.sellSpreadValue - a.live.buySpreadValue : 0
        const db = b.live ? b.live.sellSpreadValue - b.live.buySpreadValue : 0
        return da - db
      },
      render: (_, r) => {
        if (!r.live) return '—'
        const diff = r.live.sellSpreadValue - r.live.buySpreadValue
        return (
          <FlashValue value={diff}>
            <Typography.Text type="secondary">{diff.toFixed(2)}</Typography.Text>
          </FlashValue>
        )
      },
    },
    {
      title: 'Near price',
      key: 'nearPrice',
      render: (_, r) => {
        if (!r.live) return '—'
        const usesBid = r.live.direction === 'SellNear/BuyFar'
        return <FlashValue value={r.live.nearPrice}>{`${r.live.nearPrice.toFixed(2)} (${usesBid ? 'bid' : 'ask'})`}</FlashValue>
      },
    },
    {
      title: 'Far price',
      key: 'farPrice',
      render: (_, r) => {
        if (!r.live) return '—'
        const usesBid = r.live.direction === 'BuyNear/SellFar'
        return <FlashValue value={r.live.farPrice}>{`${r.live.farPrice.toFixed(2)} (${usesBid ? 'bid' : 'ask'})`}</FlashValue>
      },
    },
    { title: 'Direction', key: 'direction', render: (_, r) => r.live ? <DirectionTag direction={r.live.direction} /> : '—' },
    // Near/Far leg (the raw tradingsymbols) are supporting detail, not the headline - pushed to
    // the end, right before Updated, rather than up near Underlying/Spread %/Spread.
    { title: 'Near leg', key: 'near', render: (_, r) => r.nearLeg.tradingSymbol },
    { title: 'Far leg', key: 'far', render: (_, r) => r.farLeg.tradingSymbol },
    { title: 'Updated', key: 'updated', render: (_, r) => r.live ? new Date(r.live.updatedAt).toLocaleTimeString() : '—' },
  ]

  return (
    <Card
      title={
        <Space>
          <LineChartOutlined style={{ color: '#4da3ff' }} />
          Spreads ({filtered.length}{filtered.length !== definitions.length ? ` of ${definitions.length}` : ''})
        </Space>
      }
      style={{ marginBottom: 16 }}
      extra={
        <Input.Search
          placeholder="Search by symbol..."
          allowClear
          value={search}
          onChange={(e) => onSearch(e.target.value)}
          style={{ width: 220 }}
        />
      }
    >
      {definitions.length === 0 ? (
        <Typography.Text type="secondary">No spreads configured yet - build the universe on the Symbols page.</Typography.Text>
      ) : (
        <Table
          dataSource={dataSource}
          columns={columns}
          size="small"
          pagination={{
            current: page,
            pageSize,
            showSizeChanger: true,
            pageSizeOptions: [25, 50, 100, 200],
            position: ['bottomCenter'],
            showTotal: (total) => `${total} spread${total === 1 ? '' : 's'}`,
            onChange: (nextPage, nextPageSize) => {
              setPage(nextPage)
              setPageSize(nextPageSize)
            },
          }}
          scroll={{ x: 'max-content' }}
        />
      )}
      <SpreadHistoryModal
        spread={historySpread}
        open={historySpread != null}
        onClose={() => setHistorySpread(null)}
      />
    </Card>
  )
}
