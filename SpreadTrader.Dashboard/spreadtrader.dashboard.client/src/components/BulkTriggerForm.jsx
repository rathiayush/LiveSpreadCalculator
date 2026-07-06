import { useState } from 'react'
import { Card, Row, Col, InputNumber, Select, Radio, Switch, Button, Alert, Typography, Space } from 'antd'
import { ThunderboltOutlined } from '@ant-design/icons'
import { api } from '../api'

const LEG_ACTIONS = ['None', 'Buy', 'Sell']

const emptyForm = {
  targetPercent: -2,
  operator: 'lte',
  nearLegAction: 'Buy',
  farLegAction: 'Sell',
  lotsPerTrade: 1,
  product: 'NRML',
  orderType: 'MARKET',
  oneShot: true,
  rearmBufferPercent: 0.1,
}

// Configures ONE rule for however many spreads are currently defined (e.g. the whole built
// universe) instead of adding rules by hand one at a time - a single signed target %, an explicit
// comparison, and a FIXED Buy/Sell/None leg pair applied identically across every spread in the
// batch. Deliberately no auto-direction: a single template can't know in advance which leg is
// actually rich/cheap for each different underlying, so the same fixed legs fire everywhere a
// spread crosses the target, regardless of which side is actually pricier for that particular
// stock at that moment. See BulkTriggerTemplateService.
export default function BulkTriggerForm({ spreadCount, onCreated }) {
  const [form, setForm] = useState(emptyForm)
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState(null)
  const [error, setError] = useState(null)

  const set = (field) => (value) => setForm((f) => ({ ...f, [field]: value }))

  const submit = async (e) => {
    e.preventDefault()
    if (form.nearLegAction === 'None' && form.farLegAction === 'None') {
      setError('Pick a Buy/Sell action for at least one leg.')
      return
    }
    setBusy(true)
    setError(null)
    setResult(null)
    try {
      const res = await api.applyBulkTemplate({
        targetPercent: Number(form.targetPercent),
        fireWhenGreaterOrEqual: form.operator === 'gte',
        nearLegAction: form.nearLegAction,
        farLegAction: form.farLegAction,
        lotsPerTrade: Number(form.lotsPerTrade),
        product: form.product,
        orderType: form.orderType,
        oneShot: form.oneShot,
        rearmBufferPercent: Number(form.rearmBufferPercent || 0),
      })
      setResult(res)
      onCreated()
    } catch (err) {
      setError(err.message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <Card title={<Space><ThunderboltOutlined style={{ color: '#f5a623' }} /> Bulk trigger template</Space>} style={{ marginBottom: 16 }}>
      <form onSubmit={submit}>
        <Space direction="vertical" size="middle" style={{ width: '100%' }}>
          <Typography.Text type="secondary">
            Applies to all {spreadCount} currently configured spread{spreadCount === 1 ? '' : 's'} -
            one rule each, same target/comparison/legs everywhere. Quantity is lots × each stock's
            own exchange lot size, not a flat number.
          </Typography.Text>

          <Row gutter={[12, 12]}>
            <Col xs={24}>
              <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 4 }}>Target spread % (signed)</Typography.Text>
              <InputNumber step={0.05} value={form.targetPercent} onChange={set('targetPercent')} style={{ width: '100%' }} />
              <Typography.Text type="secondary" style={{ fontSize: 12, display: 'block', marginTop: 4 }}>
                Enter the actual spread % level (e.g. -2), not a deviation magnitude - applied
                identically to every spread in the batch regardless of its own price level.
              </Typography.Text>
            </Col>
          </Row>

          <Row gutter={[12, 12]}>
            <Col xs={24}>
              <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 4 }}>
                Fire when the spread is...
              </Typography.Text>
              <Radio.Group
                block
                optionType="button"
                buttonStyle="solid"
                value={form.operator}
                onChange={(e) => set('operator')(e.target.value)}
                options={[
                  { label: `Rises to ${form.targetPercent}% or higher`, value: 'gte' },
                  { label: `Falls to ${form.targetPercent}% or lower`, value: 'lte' },
                ]}
              />
            </Col>
          </Row>

          <Row gutter={[12, 12]}>
            <Col xs={24} sm={12}>
              <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 4 }}>Near leg action (every spread)</Typography.Text>
              <Select
                value={form.nearLegAction}
                onChange={set('nearLegAction')}
                style={{ width: '100%' }}
                options={LEG_ACTIONS.map((a) => ({ value: a, label: a }))}
              />
            </Col>
            <Col xs={24} sm={12}>
              <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 4 }}>Far leg action (every spread)</Typography.Text>
              <Select
                value={form.farLegAction}
                onChange={set('farLegAction')}
                style={{ width: '100%' }}
                options={LEG_ACTIONS.map((a) => ({ value: a, label: a }))}
              />
            </Col>
          </Row>

          {/* Plain-English restatement of exactly what this template will do, using the actual
              choices made above - e.g. "once a stock's near-month vs far-month difference falls
              to -2% or lower: its near-month contract will be BOUGHT, its far-month contract
              will be SOLD - on all 120 spreads". */}
          {(form.nearLegAction !== 'None' || form.farLegAction !== 'None') && (
            <Alert
              type="info"
              showIcon
              message={
                <>
                  Once a stock's <b>near-month vs far-month difference</b> <b>{form.operator === 'gte' ? `rises to ${form.targetPercent}% or higher` : `falls to ${form.targetPercent}% or lower`}</b>:
                  {form.nearLegAction !== 'None' && <> its <b>near-month</b> contract will be <b>{form.nearLegAction === 'Buy' ? 'BOUGHT' : 'SOLD'}</b></>}
                  {form.nearLegAction !== 'None' && form.farLegAction !== 'None' && ', and'}
                  {form.farLegAction !== 'None' && <> its <b>far-month</b> contract will be <b>{form.farLegAction === 'Buy' ? 'BOUGHT' : 'SOLD'}</b></>}
                  {' '}- the same way on all {spreadCount} spread{spreadCount === 1 ? '' : 's'} in the batch, regardless of which contract is actually pricier for that particular stock.
                </>
              }
            />
          )}

          <Row gutter={[12, 12]}>
            <Col xs={24} sm={12}>
              <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 4 }}>Lots per trade</Typography.Text>
              <InputNumber min={1} value={form.lotsPerTrade} onChange={set('lotsPerTrade')} style={{ width: '100%' }} />
            </Col>
            <Col xs={24} sm={12}>
              <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 4 }}>Product</Typography.Text>
              <Select
                value={form.product}
                onChange={set('product')}
                style={{ width: '100%' }}
                options={[{ value: 'NRML', label: 'NRML' }, { value: 'MIS', label: 'MIS' }]}
              />
            </Col>
          </Row>

          <Row gutter={[12, 12]}>
            <Col xs={24}>
              <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 4 }}>Order type</Typography.Text>
              <Select
                value={form.orderType}
                onChange={set('orderType')}
                style={{ width: '100%' }}
                options={[{ value: 'MARKET', label: 'MARKET' }, { value: 'LIMIT', label: 'LIMIT' }]}
              />
            </Col>
          </Row>

          <Row gutter={[12, 12]} align="middle">
            <Col xs={24} sm={12}>
              <Space>
                <Switch checked={form.oneShot} onChange={set('oneShot')} />
                <Typography.Text>One-shot (disable after firing)</Typography.Text>
              </Space>
            </Col>
            <Col xs={24} sm={12}>
              <InputNumber
                min={0}
                step={0.05}
                addonBefore="Re-arm %"
                value={form.rearmBufferPercent}
                onChange={set('rearmBufferPercent')}
                disabled={form.oneShot}
                style={{ width: '100%' }}
              />
            </Col>
          </Row>

          {error && <Alert type="error" message={error} showIcon />}
          <Button type="primary" icon={<ThunderboltOutlined />} htmlType="submit" loading={busy} disabled={spreadCount === 0}>
            Apply to all {spreadCount} spreads
          </Button>
          {result && (
            <Alert
              type="success"
              showIcon
              message={`${result.rulesCreated} rules created across ${result.spreadCount} spreads.`}
              description={result.skipped.length > 0
                ? `${result.skipped.length} skipped: ${result.skipped.slice(0, 5).join('; ')}${result.skipped.length > 5 ? '…' : ''}`
                : undefined}
            />
          )}
        </Space>
      </form>
    </Card>
  )
}
