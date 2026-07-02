import { Card, Table, Typography, Space } from 'antd'
import { TrophyOutlined } from '@ant-design/icons'
import DirectionTag from './DirectionTag'

const MEDAL_COLORS = { 1: '#ffd700', 2: '#c0c0c0', 3: '#cd7f32' }

// Shows the last computed ranking (spreads sorted by |spread %| descending) - refreshed on the
// server roughly every Engine:RankingIntervalMinutes (default 3), not on every tick. Shown in
// full with no pagination - this is the "everything at a glance" view.
export default function RankingPanel({ ranking }) {
  if (ranking.length === 0) {
    return (
      <Card
        title={<Space><TrophyOutlined style={{ color: '#f5a623' }} /> Ranked by spread %</Space>}
        style={{ marginBottom: 16 }}
      >
        <Typography.Text type="secondary">No ranking yet - it fills in once the engine has ask prices for at least one spread.</Typography.Text>
      </Card>
    )
  }

  const dataSource = ranking.map((r) => ({ key: r.spreadId, ...r }))

  const columns = [
    {
      title: '#',
      dataIndex: 'rank',
      key: 'rank',
      fixed: 'left',
      width: 56,
      render: (v) => {
        const medal = MEDAL_COLORS[v]
        return medal ? (
          <span style={{
            display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
            width: 24, height: 24, borderRadius: '50%', background: `${medal}33`,
            color: medal, fontWeight: 700, fontSize: 12,
          }}>{v}</span>
        ) : <span style={{ color: 'rgba(255,255,255,0.55)' }}>{v}</span>
      },
    },
    { title: 'Underlying', dataIndex: 'underlying', key: 'underlying', render: (v) => <strong>{v}</strong> },
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
    { title: 'Direction', dataIndex: 'direction', key: 'direction', render: (v) => <DirectionTag direction={v} /> },
    { title: 'Updated', dataIndex: 'updatedAt', key: 'updatedAt', render: (v) => new Date(v).toLocaleTimeString() },
  ]

  return (
    <Card
      title={<Space><TrophyOutlined style={{ color: '#f5a623' }} /> Ranked by spread % ({ranking.length})</Space>}
      style={{ marginBottom: 16 }}
      extra={<Typography.Text type="secondary" style={{ fontSize: 12 }}>Re-sorted every Engine:RankingIntervalMinutes</Typography.Text>}
    >
      <Table dataSource={dataSource} columns={columns} size="small" pagination={false} scroll={{ x: 'max-content' }} />
    </Card>
  )
}
