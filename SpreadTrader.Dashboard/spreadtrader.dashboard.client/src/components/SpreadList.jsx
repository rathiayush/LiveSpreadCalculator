import { useMemo, useState } from 'react'
import { Card, Table, Button, Popconfirm, Typography, Space, Input, message } from 'antd'
import { UnorderedListOutlined, DeleteOutlined } from '@ant-design/icons'
import { api } from '../api'

export default function SpreadList({ spreads, onChanged }) {
  const [search, setSearch] = useState('')
  // Controlled pagination (see the same note in SpreadGrid) - keeps the current page stable and
  // resets to page 1 on search so filtering can't leave you stranded on a now-empty page.
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(15)

  const onSearch = (value) => {
    setSearch(value)
    setPage(1)
  }

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
    try {
      await api.deleteSpread(id)
      onChanged()
    } catch (err) {
      message.error(err.message || 'Failed to remove spread.')
    }
  }

  const columns = [
    // width is required (not just cosmetic) on a fixed column - without it, antd can't reliably
    // compute the sticky offset for the columns after it before layout settles, especially on a
    // narrow/mobile viewport, causing this column's text to render overlapping the next column
    // instead of pushing it aside.
    { title: 'Id', dataIndex: 'id', key: 'id', fixed: 'left', width: 170 },
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
            onChange={(e) => onSearch(e.target.value)}
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
          pagination={{
            current: page,
            pageSize,
            showSizeChanger: true,
            pageSizeOptions: [15, 30, 50, 100],
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
    </Card>
  )
}
