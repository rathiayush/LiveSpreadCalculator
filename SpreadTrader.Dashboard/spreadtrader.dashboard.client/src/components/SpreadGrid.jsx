import { Card, Table, Typography } from 'antd'

export default function SpreadGrid({ definitions, liveValues }) {
  if (definitions.length === 0) {
    return (
      <Card title="Spreads" style={{ marginBottom: 16 }}>
        <Typography.Text type="secondary">No spreads configured yet - add one on the Trigger Config page.</Typography.Text>
      </Card>
    )
  }

  const dataSource = definitions.map((d) => ({ key: d.id, ...d, live: liveValues[d.id] }))

  const DIRECTION_LABELS = {
    'BuyNear/SellFar': 'Buy near / Sell far',
    'SellNear/BuyFar': 'Sell near / Buy far',
    Flat: 'Flat',
  }

  const columns = [
    { title: 'Underlying', dataIndex: 'underlying', key: 'underlying', fixed: 'left' },
    { title: 'Near leg', key: 'near', render: (_, r) => r.nearLeg.tradingSymbol },
    { title: 'Far leg', key: 'far', render: (_, r) => r.farLeg.tradingSymbol },
    {
      title: 'Near price',
      key: 'nearPrice',
      render: (_, r) => r.live ? `${r.live.nearPrice.toFixed(2)} (${r.live.direction === 'SellNear/BuyFar' ? 'bid' : 'ask'})` : '—',
    },
    {
      title: 'Far price',
      key: 'farPrice',
      render: (_, r) => r.live ? `${r.live.farPrice.toFixed(2)} (${r.live.direction === 'BuyNear/SellFar' ? 'bid' : 'ask'})` : '—',
    },
    {
      title: 'Spread',
      key: 'spread',
      render: (_, r) => r.live ? (
        <span style={{ color: r.live.spreadValue >= 0 ? '#3ddc84' : '#ff5c5c', fontWeight: 600 }}>
          {r.live.spreadValue.toFixed(2)}
        </span>
      ) : '—',
    },
    { title: 'Direction', key: 'direction', render: (_, r) => r.live ? (DIRECTION_LABELS[r.live.direction] ?? r.live.direction) : '—' },
    { title: 'Updated', key: 'updated', render: (_, r) => r.live ? new Date(r.live.updatedAt).toLocaleTimeString() : '—' },
  ]

  return (
    <Card title={`Spreads (${definitions.length})`} style={{ marginBottom: 16 }}>
      <Table dataSource={dataSource} columns={columns} size="small" pagination={false} scroll={{ x: 'max-content' }} />
    </Card>
  )
}
