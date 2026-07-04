import { useState } from 'react'
import { Card, Row, Col, InputNumber, Select, Switch, Radio, Button, Alert, Typography, Space } from 'antd'
import { PlusCircleOutlined } from '@ant-design/icons'
import { api } from '../api'

const LEG_ACTIONS = ['None', 'Buy', 'Sell']

// Rule id is an internal identifier only (used for CSV persistence, the Edit modal, and the
// trigger-activity feed) - the user never types or reasons about it, so it's generated here
// instead of collected as a form field. crypto.randomUUID() needs a secure context (https or
// localhost), which is what this app already runs under (Kite's own OAuth redirect requires
// https in production) - the Math.random fallback only matters for an odd plain-http dev setup.
const generateRuleId = () =>
  (typeof crypto !== 'undefined' && crypto.randomUUID)
    ? crypto.randomUUID()
    : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`

// Direction is now fully determined by watchBothSides - there's no separate "auto direction"
// toggle to get out of sync with it:
//   - watchBothSides ON  -> the engine always decides live which leg to buy/sell from the
//     spread's actual direction the instant it crosses the threshold (classic calendar-spread
//     arbitrage - sell whichever leg is rich, buy whichever is cheap). This is the only mode
//     that makes sense here, since a fixed manual leg action can't correctly handle a deviation
//     that might fire from either side.
//   - watchBothSides OFF -> fully manual: an explicit operator (fires when the spread rises
//     to/above, or falls to/below, the threshold) plus a fixed Buy/Sell/None per leg. Threshold
//     is always typed as a plain positive magnitude - the operator picker carries the direction
//     instead of the user having to remember "type a negative number for <=".
// Matches the classic calendar-spread arbitrage convention (same one auto-direction/"watch both
// sides" applies live): far leg pricier -> near is cheap -> buy near, sell far. Near leg pricier
// -> near is rich -> sell near, buy far. See OPERATOR_LEG_DEFAULTS below - picking an operator
// pre-fills the matching leg actions so the common case needs no extra clicks, while still
// leaving both selects free to override for anything non-standard.
const OPERATOR_LEG_DEFAULTS = {
  gte: { nearLegAction: 'Buy', farLegAction: 'Sell' },
  lte: { nearLegAction: 'Sell', farLegAction: 'Buy' },
}

const emptyForm = {
  spreadId: '',
  threshold: '',
  thresholdIsPercent: false,
  watchBothSides: false,
  operator: 'gte',
  ...OPERATOR_LEG_DEFAULTS.gte,
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

  // Picking an operator re-applies its matching leg defaults (see OPERATOR_LEG_DEFAULTS) so the
  // common case ("far pricier -> buy near/sell far", "near pricier -> sell near/buy far") is set
  // automatically - the two Selects below stay fully editable if you want something else.
  const setOperator = (value) => setForm((f) => ({ ...f, operator: value, ...OPERATOR_LEG_DEFAULTS[value] }))

  // Exchange lot size for whichever spread is selected - both legs of a calendar spread share
  // the same underlying, so near/far always have the same lot size once resolved; used only to
  // show "= N shares" below the Lots input, the actual lots -> quantity conversion happens
  // server-side (see TriggersController.Create) since that's the source of truth for lot size.
  const selectedSpread = spreads.find((s) => s.id === form.spreadId)
  const lotSize = selectedSpread?.nearLeg?.lotSize || selectedSpread?.farLeg?.lotSize || 0

  const submit = async (e) => {
    e.preventDefault()
    if (!form.watchBothSides && form.nearLegAction === 'None' && form.farLegAction === 'None') {
      setError('Pick a Buy/Sell action for at least one leg.')
      return
    }
    if (Number(form.threshold) === 0) {
      setError('Threshold must be non-zero.')
      return
    }
    setBusy(true)
    setError(null)
    try {
      const magnitude = Math.abs(Number(form.threshold))
      // Operator only carries a sign when watchBothSides is off - watching both sides ignores
      // sign entirely (see TriggersController.Create), so always send a plain positive number
      // there regardless of what the (hidden, in that mode) operator field happens to hold.
      const signedThreshold = (!form.watchBothSides && form.operator === 'lte') ? -magnitude : magnitude
      await api.createTrigger({
        // form.operator is a frontend-only field (not part of the request contract) - harmless
        // to send since unrecognized JSON properties are just ignored by ASP.NET Core's model
        // binder, but autoDirectionFromSpreadSign below is the one the backend actually reads.
        ...form,
        autoDirectionFromSpreadSign: form.watchBothSides,
        id: generateRuleId(),
        threshold: signedThreshold,
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
            <Col xs={24}>
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
            <Col xs={24}>
              <InputNumber
                step={0.05}
                min={0.01}
                placeholder={form.thresholdIsPercent ? 'Spread % deviation magnitude' : 'Ask-price spread deviation magnitude'}
                value={form.threshold === '' ? null : form.threshold}
                onChange={(v) => set('threshold')(v ?? '')}
                style={{ width: '100%' }}
              />
              <Typography.Text type="secondary" style={{ fontSize: 12, display: 'block', marginTop: 4 }}>
                {form.watchBothSides
                  ? "Enter a plain positive number - fires when the spread deviates at least this much in EITHER direction; the engine decides live which leg to buy/sell from the actual direction at that moment."
                  : "Enter a plain positive number - pick the direction below (instead of a sign to remember) and which leg to buy/sell further down."}
              </Typography.Text>
            </Col>
          </Row>

          <Space>
            <Switch checked={form.thresholdIsPercent} onChange={set('thresholdIsPercent')} />
            <Typography.Text>Threshold is a % of the near leg's price (instead of a rupee amount)</Typography.Text>
          </Space>

          <Space>
            <Switch checked={form.watchBothSides} onChange={set('watchBothSides')} />
            <Typography.Text>Watch both sides (direction decided automatically, live)</Typography.Text>
          </Space>

          {form.watchBothSides ? (
            <Typography.Text type="secondary">
              Classic calendar-spread arbitrage, handled automatically: when the near (current
              month) leg is trading at a premium to the far leg, the engine sells near and buys
              far. When near is at a discount, it does the opposite - buys near, sells far. No
              manual operator or leg selection needed here, since which side actually crossed the
              threshold decides this live.
            </Typography.Text>
          ) : (
            <>
              <Row gutter={[12, 12]}>
                <Col xs={24}>
                  <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 4 }}>
                    Fire when...
                  </Typography.Text>
                  {/* Named after which leg gets pricier, not "rises/falls" - which direction
                      counts as "up" depends on which leg you're subtracting from which, so
                      naming the leg directly instead of >=/<= removes that ambiguity. */}
                  <Radio.Group
                    block
                    optionType="button"
                    buttonStyle="solid"
                    value={form.operator}
                    onChange={(e) => setOperator(e.target.value)}
                    options={[
                      { label: 'Far leg gets pricier (near at discount)', value: 'gte' },
                      { label: 'Near leg gets pricier (near at premium)', value: 'lte' },
                    ]}
                  />
                </Col>
              </Row>

              <Row gutter={[12, 12]}>
                <Col xs={24} sm={12}>
                  <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 4 }}>Near leg action</Typography.Text>
                  <Select
                    value={form.nearLegAction}
                    onChange={set('nearLegAction')}
                    style={{ width: '100%' }}
                    options={LEG_ACTIONS.map((a) => ({ value: a, label: a }))}
                  />
                </Col>
                <Col xs={24} sm={12}>
                  <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 4 }}>Far leg action</Typography.Text>
                  <Select
                    value={form.farLegAction}
                    onChange={set('farLegAction')}
                    style={{ width: '100%' }}
                    options={LEG_ACTIONS.map((a) => ({ value: a, label: a }))}
                  />
                </Col>
              </Row>
            </>
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
