import { Tag, Tooltip } from 'antd'
import { ArrowUpOutlined, ArrowDownOutlined, MinusOutlined } from '@ant-design/icons'

const DIRECTION_META = {
  'BuyNear/SellFar': { label: 'Buy near / Sell far', color: 'green', icon: <ArrowUpOutlined /> },
  'SellNear/BuyFar': { label: 'Sell near / Buy far', color: 'red', icon: <ArrowDownOutlined /> },
  Flat: { label: 'Flat', color: 'default', icon: <MinusOutlined /> },
}

// direction is either a plain "BuyNear/SellFar" / "SellNear/BuyFar" / "Flat", or one of the
// first two with " (bid pending)" appended - the backend falls back to that leg's ask when the
// bid it would need to price a sell hasn't arrived yet, rather than hiding the row entirely.
// Used by RankingPanel, SpreadGrid, and TriggerFeed so the same colors/labels show everywhere.
export default function DirectionTag({ direction }) {
  if (!direction) return null

  const pending = direction.endsWith('(bid pending)')
  const base = pending ? direction.replace(' (bid pending)', '') : direction
  const meta = DIRECTION_META[base] ?? { label: base, color: 'default', icon: null }

  const tag = (
    <Tag color={meta.color} icon={meta.icon} style={{ opacity: pending ? 0.7 : 1 }}>
      {meta.label}{pending ? ' *' : ''}
    </Tag>
  )

  return pending ? <Tooltip title="Bid not received yet for the sell leg - using its ask as a provisional value">{tag}</Tooltip> : tag
}
