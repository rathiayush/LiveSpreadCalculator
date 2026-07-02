import { useMemo, useState } from 'react'
import { Card, Table, Typography, Input, Space } from 'antd'
import { LineChartOutlined } from '@ant-design/icons'
import DirectionTag from './DirectionTag'

// Live per-spread grid with a name search (underlying / near / far tradingsymbol) and real
// pagination - lives on its own "Live Spreads" page since with a 150-200+ symbol universe a
// single unpaginated table is unusable.
export default function SpreadGrid({ definitions, liveValues }) {
  const [search, setSearch] = useState('')

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
      title: 'Underlying',
      dataIndex: 'underlying',
      key: 'underlying',
      fixed: 'left',
      sorter: (a, b) => a.underlying.localeCompare(b.underlying),
      render: (v) => <strong>{v}</strong>,
    },
    { title: 'Near leg', key: 'near', render: (_, r) => r.nearLeg.tradingSymbol },
    { title: 'Far leg', key: 'far', render: (_, r) => r.farLeg.tradingSymbol },
    {
      title: 'Near price',
      key: 'nearPrice',
      render: (_, r) => {
        if (!r.live) return '—'
        const usesBid = r.live.direction === 'SellNear/BuyFar'
        return `${r.live.nearPrice.toFixed(2)} (${usesBid ? 'bid' : 'ask'})`
      },
    },
    {
      title: 'Far price',
      key: 'farPrice',
      render: (_, r) => {
        if (!r.live) return '—'
        const usesBid = r.live.direction === 'BuyNear/SellFar'
        return `${r.live.farPrice.toFixed(2)} (${usesBid ? 'bid' : 'ask'})`
      },
    },
    {
      title: 'Spread',
      key: 'spread',
      sorter: (a, b) => (a.live?.spreadValue ?? 0) - (b.live?.spreadValue ?? 0),
      render: (_, r) => r.live ? (
        <span style={{ color: r.live.spreadValue >= 0 ? '#3ddc84' : '#ff5c5c', fontWeight: 600 }}>
          {r.live.spreadValue.toFixed(2)}
        </span>
      ) : '—',
    },
    { title: 'Direction', key: 'direction', render: (_, r) => r.live ? <DirectionTag direction={r.live.direction} /> : '—' },
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
          onChange={(e) => setSearch(e.target.value)}
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
          pagination={{ pageSize: 25, showSizeChanger: true, pageSizeOptions: [25, 50, 100, 200] }}
          scroll={{ x: 'max-content' }}
        />
      )}
    </Card>
  )
}
