import { Card, List, Typography, Space } from 'antd'
import { ThunderboltOutlined } from '@ant-design/icons'
import DirectionTag from './DirectionTag'

export default function TriggerFeed({ events }) {
  return (
    <Card title={<Space><ThunderboltOutlined style={{ color: '#f5a623' }} /> Recent trigger fires</Space>} style={{ marginBottom: 16 }}>
      {events.length === 0 ? (
        <Typography.Text type="secondary">No triggers fired yet.</Typography.Text>
      ) : (
        <List
          size="small"
          dataSource={events}
          style={{ maxHeight: 320, overflowY: 'auto' }}
          renderItem={(e, i) => (
            <List.Item key={`${e.ruleId}-${e.triggeredAt}-${i}`}>
              <Space wrap size="small">
                <ThunderboltOutlined style={{ color: '#f5a623' }} />
                <strong>{e.ruleId}</strong>
                <Typography.Text type="secondary">on</Typography.Text>
                <span>{e.spreadId}</span>
                <Typography.Text type="secondary">at</Typography.Text>
                <span>{e.spreadValueAtTrigger.toFixed(2)}</span>
                {e.direction && <DirectionTag direction={e.direction} />}
                <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                  {new Date(e.triggeredAt).toLocaleTimeString()}
                </Typography.Text>
              </Space>
            </List.Item>
          )}
        />
      )}
    </Card>
  )
}
