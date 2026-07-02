import { Card, Table, Button, Popconfirm, Tag, Typography, Space } from 'antd'
import {
  ThunderboltOutlined,
  CheckCircleOutlined,
  ClockCircleOutlined,
  StopOutlined,
  PoweroffOutlined,
  DeleteOutlined,
  AimOutlined,
} from '@ant-design/icons'
import { api } from '../api'

export default function TriggerList({ triggers, onChanged }) {
  const toggle = async (rule) => {
    if (rule.enabled) await api.disableTrigger(rule.id)
    else await api.enableTrigger(rule.id)
    onChanged()
  }

  const remove = async (id) => {
    await api.deleteTrigger(id)
    onChanged()
  }

  const removeAll = async () => {
    await api.deleteAllTriggers()
    onChanged()
  }

  const columns = [
    { title: 'Id', dataIndex: 'id', key: 'id', fixed: 'left' },
    { title: 'Spread', dataIndex: 'spreadId', key: 'spreadId' },
    {
      title: 'Condition',
      key: 'condition',
      render: (_, t) => `${t.operator === 'GreaterThanOrEqual' ? '>=' : '<='} ${t.threshold}${t.thresholdIsPercent ? '%' : ''}`,
    },
    {
      title: 'Legs',
      key: 'legs',
      render: (_, t) => t.autoDirectionFromSpreadSign ? (
        <Tag icon={<AimOutlined />} color="blue">auto (by spread sign)</Tag>
      ) : (
        <>
          {t.nearLegAction !== 'None' && `near ${t.nearLegAction}`}
          {t.nearLegAction !== 'None' && t.farLegAction !== 'None' && ', '}
          {t.farLegAction !== 'None' && `far ${t.farLegAction}`}
        </>
      ),
    },
    { title: 'Qty', dataIndex: 'quantity', key: 'quantity' },
    {
      title: 'Status',
      key: 'status',
      render: (_, t) => {
        if (!t.enabled) return <Tag icon={<StopOutlined />}>disabled</Tag>
        return t.isArmed
          ? <Tag icon={<CheckCircleOutlined />} color="success">armed</Tag>
          : <Tag icon={<ClockCircleOutlined />} color="warning">fired</Tag>
      },
    },
    {
      title: '',
      key: 'actions',
      render: (_, t) => (
        <Space size="small">
          <Button type="link" size="small" icon={<PoweroffOutlined />} onClick={() => toggle(t)}>
            {t.enabled ? 'Disable' : 'Enable'}
          </Button>
          <Popconfirm title={`Remove trigger "${t.id}"?`} onConfirm={() => remove(t.id)} okText="Remove" okButtonProps={{ danger: true }}>
            <Button type="link" danger size="small" icon={<DeleteOutlined />}>Remove</Button>
          </Popconfirm>
        </Space>
      ),
    },
  ]

  return (
    <Card
      title={<Space><ThunderboltOutlined style={{ color: '#f5a623' }} /> Configured triggers ({triggers.length})</Space>}
      extra={triggers.length > 0 && (
        <Popconfirm
          title={`Delete all ${triggers.length} triggers?`}
          description="This removes every trigger rule, not just the ones shown on this page. Cannot be undone."
          onConfirm={removeAll}
          okText="Delete all"
          okButtonProps={{ danger: true }}
        >
          <Button danger size="small" icon={<DeleteOutlined />}>Delete all</Button>
        </Popconfirm>
      )}
      style={{ marginBottom: 16 }}
    >
      {triggers.length === 0 ? (
        <Typography.Text type="secondary">
          None yet - add one below. Saved to a CSV file on the server, so these survive a restart.
        </Typography.Text>
      ) : (
        <Table
          dataSource={triggers.map((t) => ({ key: t.id, ...t }))}
          columns={columns}
          size="small"
          pagination={{ pageSize: 10 }}
          scroll={{ x: 'max-content' }}
        />
      )}
    </Card>
  )
}
