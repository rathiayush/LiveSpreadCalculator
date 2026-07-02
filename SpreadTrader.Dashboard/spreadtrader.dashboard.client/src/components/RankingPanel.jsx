import { Card, Table, Typography } from 'antd'

// Shows the last computed ranking (spreads sorted by |spread %| descending) - refreshed on the
// server roughly every Engine:RankingIntervalMinutes (default 3), not on every tick.
export default function RankingPanel({ ranking }) {
  if (ranking.length === 0) {
    return (
      <Card title="Ranked by spread %" style={{ marginBottom: 16 }}>
        <Typography.Text type="secondary">No ranking yet - it fills in once the engine has ask prices for at least one spread.</Typography.Text>
      </Card>
    )
  }

  const dataSource = ranking.map((r) => ({ key: r.spreadId, ...r }))

  const DIRECTION_LABELS = {
    'BuyNear/SellFar': 'Buy near / Sell far',
    'SellNear/BuyFar': 'Sell near / Buy far',
    Flat: 'Flat',
  }

  const columns = [
    { title: '#', dataIndex: 'rank', key: 'rank', fixed: 'left', width: 48 },
    { title: 'Underlying', dataIndex: 'underlying', key: 'underlying' },
    { title: 'Spread', dataIndex: 'spreadId', key: 'spreadId' },
    { title: 'Near price', dataIndex: 'nearPrice', key: 'nearPrice', render: (v) => v.toFixed(2) },
    { title: 'Far price', dataIndex: 'farPrice', key: 'farPrice', render: (v) => v.toFixed(2) },
    {
      title: 'Spread',
      dataIndex: 'spreadValue',
      key: 'spreadValue',
      render: (v) => <span style={{ color: v >= 0 ? '#3ddc84' : '#ff5c5c', fontWeight: 600 }}>{v.toFixed(2)}</span>,
    },
    {
      title: 'Spread %',
      dataIndex: 'spreadPercent',
      key: 'spreadPercent',
      render: (v) => <span style={{ color: v >= 0 ? '#3ddc84' : '#ff5c5c', fontWeight: 600 }}>{v.toFixed(2)}%</span>,
    },
    {
      title: 'Direction',
      dataIndex: 'direction',
      key: 'direction',
      render: (v) => {
        const pending = v.endsWith('(bid pending)')
        const base = pending ? v.replace(' (bid pending)', '') : v
        const label = DIRECTION_LABELS[base] ?? base
        return pending ? `${label} (bid pending, using ask)` : label
      },
    },
    { title: 'Updated', dataIndex: 'updatedAt', key: 'updatedAt', render: (v) => new Date(v).toLocaleTimeString() },
  ]

  return (
    <Card
      title={`Ranked by spread % (top ${ranking.length})`}
      style={{ marginBottom: 16 }}
      extra={<Typography.Text type="secondary" style={{ fontSize: 12 }}>Re-sorted every Engine:RankingIntervalMinutes</Typography.Text>}
    >
      <Table dataSource={dataSource} columns={columns} size="small" pagination={{ pageSize: 20 }} scroll={{ x: 'max-content' }} />
    </Card>
  )
}
