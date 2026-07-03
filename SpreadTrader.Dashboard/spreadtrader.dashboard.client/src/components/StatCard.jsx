import { Card, Typography } from 'antd'

// A KPI card with a colored icon badge - used on the Dashboard stats strip. `color` drives both
// the icon badge background/glow and a thin gradient accent bar along the top of the card, so
// the four cards read as distinct, "designed" tiles at a glance instead of blending into one
// flat grey row.
export default function StatCard({ icon, color, title, value, suffix }) {
  return (
    <Card
      size="small"
      style={{
        height: '100%',
        position: 'relative',
        overflow: 'hidden',
        transition: 'transform 160ms ease, box-shadow 160ms ease',
      }}
      styles={{ body: { display: 'flex', alignItems: 'center', gap: 14, padding: '16px 18px' } }}
      className="stat-card"
    >
      <div
        aria-hidden
        style={{
          position: 'absolute',
          inset: '0 0 auto 0',
          height: 3,
          background: `linear-gradient(90deg, ${color}, ${color}00 85%)`,
        }}
      />
      <div
        style={{
          width: 42,
          height: 42,
          borderRadius: 11,
          background: `linear-gradient(155deg, ${color}33, ${color}14)`,
          border: `1px solid ${color}3d`,
          boxShadow: `0 4px 16px ${color}26`,
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
          <span style={{ fontSize: 22, fontWeight: 700, color: 'var(--text-strong)', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
            {value}
          </span>
          {suffix && <span style={{ fontSize: 13, color: 'var(--text-soft)' }}>{suffix}</span>}
        </div>
      </div>
    </Card>
  )
}
