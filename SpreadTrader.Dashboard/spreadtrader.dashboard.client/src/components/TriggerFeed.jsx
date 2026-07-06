import { Card, List, Typography, Space, Button, Popconfirm, Tag, Tooltip, Alert } from 'antd'
import {
  ThunderboltOutlined,
  ClearOutlined,
  CheckCircleFilled,
  CloseCircleFilled,
  LoadingOutlined,
  StopOutlined,
  SwapOutlined,
  AppstoreOutlined,
} from '@ant-design/icons'
import DirectionTag from './DirectionTag'

const ORDER_KIND_COLORS = {
  Basket: 'geekblue',
  'Single leg': 'cyan',
  None: 'default',
  Unknown: 'default',
}

// Always visible regardless of outcome (success, reject, or blocked) - shows whether this fire
// resolved to a real multi-leg basket, a single-leg order, or neither, so the feed never shows a
// fire with no context about what kind of order it was. Shows "pending" until the matching
// "basketExecuted" push tells us which one it was - see OrderDispatchHostedService.DescribeOrderKind.
function OrderKindTag({ orderKind }) {
  if (!orderKind) return <Tag icon={<LoadingOutlined />}>pending</Tag>
  return <Tag color={ORDER_KIND_COLORS[orderKind] ?? 'default'} icon={<AppstoreOutlined />}>{orderKind}</Tag>
}

// One order-leg outcome - a colored pill showing BUY/SELL, underlying name, trading symbol, qty,
// and success/reject, with the rejection reason (if any) in a tooltip so the row stays scannable
// at a glance. Always shows the full bought/sold detail whether the leg succeeded or was
// rejected - a failed leg is exactly the case where you most need to see what it tried to do.
// isSquareOff legs are the compensating orders OrderDispatchHostedService fires automatically
// when only one side of a basket fills - see EngineOptions.AutoSquareOffOnPartialFill.
function LegPill({ leg, underlying }) {
  const isBuy = leg.transactionType === 'BUY'
  // filledQuantity can be less than quantity (a genuine partial fill within this one leg, e.g.
  // an IOC order that only partly filled before the rest was cancelled) - only called out when
  // it actually differs, so the common fully-filled case still reads exactly as before.
  const isPartial = leg.success && leg.filledQuantity > 0 && leg.filledQuantity < leg.quantity
  const qtyLabel = isPartial ? `x${leg.filledQuantity}/${leg.quantity}` : `x${leg.quantity}`
  const label = underlying
    ? `${leg.transactionType} ${underlying} · ${leg.tradingSymbol} ${qtyLabel}`
    : `${leg.transactionType} ${leg.tradingSymbol} ${qtyLabel}`
  const pill = (
    <Tag
      color={leg.success ? (isPartial ? 'gold' : isBuy ? 'blue' : 'purple') : 'error'}
      icon={leg.success ? <CheckCircleFilled /> : <CloseCircleFilled />}
      style={{ marginInlineEnd: 0 }}
    >
      {leg.isSquareOff && <SwapOutlined style={{ marginInlineEnd: 4 }} />}
      {label}
    </Tag>
  )
  const tooltipText = leg.isSquareOff
    ? `Auto square-off (partial fill cleanup)${leg.success ? ` - order ${leg.orderId}` : ` - FAILED: ${leg.errorMessage}`}`
    : leg.success
      ? isPartial
        ? `Partially filled: ${leg.filledQuantity} of ${leg.quantity} - order ${leg.orderId}`
        : `Filled - order ${leg.orderId}`
      : `Not filled: ${leg.errorMessage}`
  return <Tooltip title={tooltipText}>{pill}</Tooltip>
}

// What to show in place of leg pills before/instead of a real execution result. Every branch
// always renders something - never blank - so success, rejection, and margin-block all leave a
// visible trail of what was (or would have been) bought/sold.
function ExecutionStatus({ e }) {
  if (e.marginBlocked) {
    return (
      <Tooltip title={e.blockedReason || 'Insufficient margin'}>
        <Tag color="error" icon={<StopOutlined />}>Blocked - nothing placed</Tag>
      </Tooltip>
    )
  }
  if (e.legs === null) {
    return <Tag icon={<LoadingOutlined />}>placing order...</Tag>
  }
  if (e.legs.length === 0) {
    return <Typography.Text type="secondary" style={{ fontSize: 12.5 }}>no leg actions configured</Typography.Text>
  }
  return (
    <Space wrap size={[6, 4]}>
      {e.legs.map((leg, i) => <LegPill key={i} leg={leg} underlying={e.underlying} />)}
    </Space>
  )
}

export default function TriggerFeed({ events, onClear, clearError }) {
  return (
    <Card
      title={<Space><ThunderboltOutlined style={{ color: '#f5a623' }} /> Recent trigger fires ({events.length})</Space>}
      style={{ marginBottom: 16 }}
      extra={
        onClear && (
          <Popconfirm
            title="Clear trigger activity?"
            description="Removes the history from the server too (not just this browser's view) - anyone else looking at this feed, or a fresh refresh/new tab, won't see these entries again. Cannot be undone."
            onConfirm={onClear}
            okText="Clear"
            okButtonProps={{ danger: true }}
            disabled={events.length === 0}
          >
            <Button size="small" icon={<ClearOutlined />} disabled={events.length === 0}>Clear</Button>
          </Popconfirm>
        )
      }
    >
      {clearError && (
        <Alert
          type="error"
          showIcon
          closable
          message={`Couldn't clear server-side history: ${clearError}. Your local view was cleared anyway, but it may reappear on refresh.`}
          style={{ marginBottom: 12 }}
        />
      )}
      {events.length === 0 ? (
        <Typography.Text type="secondary">No triggers fired yet.</Typography.Text>
      ) : (
        <List
          size="small"
          dataSource={events}
          style={{ maxHeight: 460, overflowY: 'auto' }}
          renderItem={(e, i) => (
            <List.Item key={`${e.fireId ?? e.ruleId}-${e.triggeredAt}-${i}`}>
              <Space direction="vertical" size={4} style={{ width: '100%' }}>
                <Space wrap size="small">
                  <ThunderboltOutlined style={{ color: '#f5a623' }} />
                  <strong>{e.ruleId}</strong>
                  <Typography.Text type="secondary">on</Typography.Text>
                  <strong>{e.underlying || e.spreadId}</strong>
                  <OrderKindTag orderKind={e.orderKind} />
                  <Typography.Text type="secondary">spread at</Typography.Text>
                  <span>{e.spreadValueAtTrigger.toFixed(2)}</span>
                  {e.direction && <DirectionTag direction={e.direction} />}
                  <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                    {new Date(e.triggeredAt).toLocaleTimeString()}
                  </Typography.Text>
                </Space>
                <div style={{ paddingInlineStart: 22 }}>
                  <ExecutionStatus e={e} />
                </div>
              </Space>
            </List.Item>
          )}
        />
      )}
    </Card>
  )
}
