import { Card, Typography } from 'antd'

// A KPI card with a colored icon badge - used on the Dashboard stats strip. `color` drives both
// the icon badge background and a thin accent bar along the top of the card, so the four cards
// read as distinct at a glance instead of blending into one grey row.
export default function StatCard({ icon, color, title, value, suffix }) {
  return (
    <Card
      size="small"
      style={{ borderTop: `2px solid ${color}`, height: '100%' }}
      styles={{ body: { display: 'flex', alignItems: 'center', gap: 14, padding: '16px 18px' } }}
    >
      <div
        style={{
          width: 40,
          height: 40,
          borderRadius: 10,
          background: `${color}22`,
          color,
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          fontSize: 18,
          flexShrink: 0,
        }}
      >
        {icon}
      </div>
      <div style={{ minWidth: 0 }}>
        <Typography.Text type="secondary" style={{ fontSize: 12, display: 'block' }}>{title}</Typography.Text>
        <div style={{ display: 'flex', alignItems: 'baseline', gap: 6 }}>
          <span style={{ fontSize: 22, fontWeight: 700, color: '#f3f4f6', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
            {value}
          </span>
          {suffix && <span style={{ fontSize: 13, color: 'rgba(255,255,255,0.5)' }}>{suffix}</span>}
        </div>
      </div>
    </Card>
  )
}
