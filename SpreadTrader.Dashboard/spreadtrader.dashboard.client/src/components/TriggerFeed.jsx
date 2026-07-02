import { Card, List, Typography } from 'antd'

const DIRECTION_LABELS = {
  'BuyNear/SellFar': 'Buy near / Sell far',
  'SellNear/BuyFar': 'Sell near / Buy far',
  Flat: 'Flat',
}

function describeDirection(direction) {
  if (!direction) return null
  const pending = direction.endsWith('(bid pending)')
  const base = pending ? direction.replace(' (bid pending)', '') : direction
  const label = DIRECTION_LABELS[base] ?? base
  return pending ? `${label} (bid pending, used ask)` : label
}

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
                {e.direction && <> ({describeDirection(e.direction)})</>}
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
