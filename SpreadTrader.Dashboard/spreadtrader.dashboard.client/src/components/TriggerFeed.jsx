import { Card, List, Typography } from 'antd'

export default function TriggerFeed({ events }) {
  return (
    <Card title="Recent trigger fires" style={{ marginBottom: 16 }}>
      {events.length === 0 ? (
        <Typography.Text type="secondary">No triggers fired yet.</Typography.Text>
      ) : (
        <List
          size="small"
          dataSource={events}
          style={{ maxHeight: 280, overflowY: 'auto' }}
          renderItem={(e, i) => (
            <List.Item key={`${e.ruleId}-${e.triggeredAt}-${i}`}>
              <Typography.Text>
                <strong>{e.ruleId}</strong> on {e.spreadId} at {e.spreadValueAtTrigger.toFixed(2)}
                {' — '}
                <Typography.Text type="secondary">{new Date(e.triggeredAt).toLocaleTimeString()}</Typography.Text>
              </Typography.Text>
            </List.Item>
          )}
        />
      )}
    </Card>
  )
}
