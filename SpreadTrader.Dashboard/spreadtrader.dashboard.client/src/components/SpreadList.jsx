import { Card, Table, Button, Popconfirm, Typography } from 'antd'
import { api } from '../api'

export default function SpreadList({ spreads, onChanged }) {
  const remove = async (id) => {
    await api.deleteSpread(id)
    onChanged()
  }

  const columns = [
    { title: 'Id', dataIndex: 'id', key: 'id', fixed: 'left' },
    { title: 'Underlying', dataIndex: 'underlying', key: 'underlying' },
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
          <Button type="link" danger size="small">Remove</Button>
        </Popconfirm>
      ),
    },
  ]

  return (
    <Card title={`Configured spreads (${spreads.length})`} style={{ marginBottom: 16 }}>
      {spreads.length === 0 ? (
        <Typography.Text type="secondary">None yet - add one below.</Typography.Text>
      ) : (
        <Table
          dataSource={spreads.map((s) => ({ key: s.id, ...s }))}
          columns={columns}
          size="small"
          pagination={{ pageSize: 10 }}
          scroll={{ x: 'max-content' }}
        />
      )}
    </Card>
  )
}
