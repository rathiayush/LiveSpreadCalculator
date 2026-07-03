import { useState } from 'react'
import { Card, Row, Col, Input, InputNumber, Select, Switch, Button, Alert, Typography, Space } from 'antd'
import { PlusCircleOutlined } from '@ant-design/icons'
import { api } from '../api'

const LEG_ACTIONS = ['None', 'Buy', 'Sell']
const OPERATORS = ['GreaterThanOrEqual', 'LessThanOrEqual']

const emptyForm = {
  id: '',
  spreadId: '',
  operator: 'GreaterThanOrEqual',
  threshold: '',
  thresholdIsPercent: false,
  autoDirectionFromSpreadSign: true,
  nearLegAction: 'None',
  farLegAction: 'None',
  lots: '',
  product: 'NRML',
  orderType: 'MARKET',
  oneShot: true,
  rearmBuffer: '0',
}

export default function TriggerForm({ spreads, onCreated }) {
  const [form, setForm] = useState(emptyForm)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState(null)

  const set = (field) => (value) => setForm((f) => ({ ...f, [field]: value }))
  const setFromEvent = (field) => (e) => set(field)(e.target.value)

  // Exchange lot size for whichever spread is selected - both legs of a calendar spread share
  // the same underlying, so near/far always have the same lot size once resolved; used only to
  // show "= N shares" below the Lots input, the actual lots -> quantity conversion happens
  // server-side (see TriggersController.Create) since that's the source of truth for lot size.
  const selectedSpread = spreads.find((s) => s.id === form.spreadId)
  const lotSize = selectedSpread?.nearLeg?.lotSize || selectedSpread?.farLeg?.lotSize || 0

  const submit = async (e) => {
    e.preventDefault()
    if (!form.autoDirectionFromSpreadSign && form.nearLegAction === 'None' && form.farLegAction === 'None') {
      setError('Pick a Buy/Sell action for at least one leg, or turn auto-direction back on.')
      return
    }
    setBusy(true)
    setError(null)
    try {
      await api.createTrigger({
        ...form,
        threshold: Number(form.threshold),
        lots: Number(form.lots),
        rearmBuffer: Number(form.rearmBuffer || 0),
      })
      setForm({ ...emptyForm, spreadId: form.spreadId })
      onCreated()
    } catch (err) {
      setError(err.message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <Card title={<Space><PlusCircleOutlined style={{ color: '#3ddc84' }} /> Add trigger</Space>} style={{ marginBottom: 16 }}>
      <form onSubmit={submit}>
        <Space direction="vertical" size="middle" style={{ width: '100%' }}>
          <Typography.Text type="secondary">
            Evaluated against each leg's best ask price, recomputed live - see the Dashboard page.
          </Typography.Text>

          <Row gutter={[12, 12]}>
            <Col xs={24} sm={12}>
              <Input placeholder="Rule id" value={form.id} onChange={setFromEvent('id')} />
            </Col>
            <Col xs={24} sm={12}>
              <Select
                showSearch
                placeholder="Select spread"
                value={form.spreadId || undefined}
                onChange={set('spreadId')}
                style={{ width: '100%' }}
                optionFilterProp="label"
                filterOption={(input, option) => (option?.label ?? '').toLowerCase().includes(input.toLowerCase())}
                options={spreads.map((s) => ({ value: s.id, label: `${s.underlying} — ${s.id}` }))}
              />
            </Col>
          </Row>

          <Row gutter={[12, 12]}>
            <Col xs={24} sm={12}>
              <Select
                value={form.operator}
                onChange={set('operator')}
                style={{ width: '100%' }}
                options={OPERATORS.map((o) => ({ value: o, label: o === 'GreaterThanOrEqual' ? 'spread >=' : 'spread <=' }))}
              />
            </Col>
            <Col xs={24} sm={12}>
              <InputNumber
                step={0.05}
                placeholder={form.thresholdIsPercent ? 'Spread % threshold' : 'Ask-price spread threshold'}
                value={form.threshold === '' ? null : form.threshold}
                onChange={(v) => set('threshold')(v ?? '')}
                style={{ width: '100%' }}
              />
            </Col>
          </Row>

          <Space>
            <Switch checked={form.thresholdIsPercent} onChange={set('thresholdIsPercent')} />
            <Typography.Text>Threshold is a % of the near leg's price (instead of a rupee amount)</Typography.Text>
          </Space>

          <Space>
            <Switch checked={form.autoDirectionFromSpreadSign} onChange={set('autoDirectionFromSpreadSign')} />
            <Typography.Text>Auto direction from spread sign</Typography.Text>
          </Space>

          {form.autoDirectionFromSpreadSign ? (
            <Typography.Text type="secondary">
              Classic calendar-spread arbitrage: when the near (current month) leg is trading at a
              premium to the far leg, the engine sells near and buys far. When near is at a
              discount, it does the opposite - buys near, sells far.
            </Typography.Text>
          ) : (
            <Row gutter={[12, 12]}>
              <Col xs={24} sm={12}>
                <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 4 }}>Near leg</Typography.Text>
                <Select
                  value={form.nearLegAction}
                  onChange={set('nearLegAction')}
                  style={{ width: '100%' }}
                  options={LEG_ACTIONS.map((a) => ({ value: a, label: a }))}
                />
              </Col>
              <Col xs={24} sm={12}>
                <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 4 }}>Far leg</Typography.Text>
                <Select
                  value={form.farLegAction}
                  onChange={set('farLegAction')}
                  style={{ width: '100%' }}
                  options={LEG_ACTIONS.map((a) => ({ value: a, label: a }))}
                />
              </Col>
            </Row>
          )}

          <Row gutter={[12, 12]}>
            <Col xs={24} sm={8}>
              <InputNumber
                min={1}
                placeholder="Lots"
                addonBefore="Lots"
                value={form.lots === '' ? null : form.lots}
                onChange={(v) => set('lots')(v ?? '')}
                style={{ width: '100%' }}
              />
              <Typography.Text type="secondary" style={{ fontSize: 12, display: 'block', marginTop: 4 }}>
                {lotSize > 0
                  ? `Lot size ${lotSize}${form.lots ? ` — ${Number(form.lots) * lotSize} shares/contracts per leg` : ''}`
                  : 'Select a spread to see its lot size'}
              </Typography.Text>
            </Col>
            <Col xs={24} sm={8}>
              <Select
                value={form.product}
                onChange={set('product')}
                style={{ width: '100%' }}
                options={[{ value: 'NRML', label: 'NRML' }, { value: 'MIS', label: 'MIS' }]}
              />
            </Col>
            <Col xs={24} sm={8}>
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
                step={0.05}
                placeholder="Re-arm buffer"
                value={form.rearmBuffer === '' ? null : form.rearmBuffer}
                onChange={(v) => set('rearmBuffer')(v ?? '')}
                disabled={form.oneShot}
                style={{ width: '100%' }}
              />
            </Col>
          </Row>

          {error && <Alert type="error" message={error} showIcon />}
          <Button type="primary" htmlType="submit" loading={busy}>Add trigger</Button>
        </Space>
      </form>
    </Card>
  )
}
