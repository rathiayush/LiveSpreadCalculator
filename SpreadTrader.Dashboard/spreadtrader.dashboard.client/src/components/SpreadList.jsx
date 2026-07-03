import { useMemo, useState } from 'react'
import { Card, Table, Button, Popconfirm, Typography, Space, Input } from 'antd'
import { UnorderedListOutlined, DeleteOutlined } from '@ant-design/icons'
import { api } from '../api'

export default function SpreadList({ spreads, onChanged }) {
  const [search, setSearch] = useState('')

  const filtered = useMemo(() => {
    const term = search.trim().toLowerCase()
    if (!term) return spreads
    return spreads.filter((s) =>
      s.underlying.toLowerCase().includes(term) ||
      s.id.toLowerCase().includes(term) ||
      s.nearLeg.tradingSymbol.toLowerCase().includes(term) ||
      s.farLeg.tradingSymbol.toLowerCase().includes(term))
  }, [spreads, search])

  const remove = async (id) => {
    await api.deleteSpread(id)
    onChanged()
  }

  const columns = [
    { title: 'Id', dataIndex: 'id', key: 'id', fixed: 'left' },
    { title: 'Underlying', dataIndex: 'underlying', key: 'underlying', render: (v) => <strong>{v}</strong> },
    { title: 'Near', key: 'near', render: (_, s) => s.nearLeg.tradingSymbol },
    { title: 'Far', key: 'far', render: (_, s) => s.farLeg.tradingSymbol },
    {
      title: '',
      key: 'actions',
      render: (_, s) => (
        <Popconfirm
          title={`Remove spread "${s.id}"?`}
          description="Any triggers pointing at it will stop firing."
          onConfirm={() => remove(s.id)}
          okText="Remove"
          okButtonProps={{ danger: true }}
        >
          <Button type="link" danger size="small" icon={<DeleteOutlined />}>Remove</Button>
        </Popconfirm>
      ),
    },
  ]

  return (
    <Card
      title={
        <Space>
          <UnorderedListOutlined style={{ color: '#4da3ff' }} />
          Configured spreads ({filtered.length}{filtered.length !== spreads.length ? ` of ${spreads.length}` : ''})
        </Space>
      }
      style={{ marginBottom: 16 }}
      extra={
        spreads.length > 0 && (
          <Input.Search
            placeholder="Search by symbol..."
            allowClear
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            style={{ width: 220 }}
          />
        )
      }
    >
      {spreads.length === 0 ? (
        <Typography.Text type="secondary">None yet - build the universe on the Symbols page to populate this list.</Typography.Text>
      ) : (
        <Table
          dataSource={filtered.map((s) => ({ key: s.id, ...s }))}
          columns={columns}
          size="small"
          pagination={{ pageSize: 15, showSizeChanger: true, pageSizeOptions: [15, 30, 50, 100] }}
          scroll={{ x: 'max-content' }}
        />
      )}
    </Card>
  )
}
