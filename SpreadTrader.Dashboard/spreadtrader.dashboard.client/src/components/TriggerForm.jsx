import { useState } from 'react'
import { Card, Row, Col, InputNumber, Select, Switch, Radio, Button, Alert, Typography, Space, Tooltip } from 'antd'
import { PlusCircleOutlined } from '@ant-design/icons'
import { api } from '../api'
import { useLiveSpreads } from '../hooks/spreadHub'
import FlashValue from './FlashValue'

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

// Picking an operator pre-fills the classic calendar-spread arb legs (far pricier -> buy near/
// sell far, near pricier -> sell near/buy far) purely as a convenience starting point - both
// Selects below stay fully editable, and nothing here is sent to the backend as-is (the actual
// nearLegAction/farLegAction fields are).
const OPERATOR_LEG_DEFAULTS = {
  gte: { nearLegAction: 'Buy', farLegAction: 'Sell' },
  lte: { nearLegAction: 'Sell', farLegAction: 'Buy' },
}

const emptyForm = {
  spreadId: '',
  target: '',
  thresholdIsPercent: false,
  operator: 'gte',
  ...OPERATOR_LEG_DEFAULTS.gte,
  lots: '',
  product: 'NRML',
  orderType: 'MARKET',
  slippagePercent: 0.10,
  oneShot: true,
  maxFireCount: 3,
}

// The two-way live quote (same buySpreadValue/sellSpreadValue SpreadGrid's "Buy spread"/"Sell
// spread" columns show - see SpreadSnapshot.BuySpreadValue) for whichever spread is currently
// selected, shown right under the picker so there's a live number to anchor a target against
// without leaving this page. Both sides shown regardless of thresholdIsPercent/leg actions
// picked below, since which one this rule actually gets evaluated against depends on Near/Far
// leg action (see SpreadCalculationHostedService.ResolveMeasuredValue) - showing only one would
// silently guess wrong for whichever direction isn't picked yet.
function fmtLive(v, isPercent) {
  return v === null || v === undefined ? '—' : `${v.toFixed(2)}${isPercent ? '%' : ''}`
}
function LiveSpreadPrice({ snapshot, thresholdIsPercent }) {
  if (!snapshot) {
    return (
      <Typography.Text type="secondary" style={{ fontSize: 12, display: 'block', marginTop: 4 }}>
        Live now: not ticking yet
      </Typography.Text>
    )
  }
  const buy = thresholdIsPercent ? snapshot.buySpreadPercent : snapshot.buySpreadValue
  const sell = thresholdIsPercent ? snapshot.sellSpreadPercent : snapshot.sellSpreadValue
  return (
    <Typography.Text type="secondary" style={{ fontSize: 12, display: 'block', marginTop: 4 }}>
      Live now —{' '}
      <Tooltip title="What Buy Near / Sell Far would cost right now (near leg at ask, far leg at bid).">
        Buy spread:{' '}
        <FlashValue value={buy}>
          <span style={{ color: buy >= 0 ? '#3ddc84' : '#ff5c5c', fontWeight: 600 }}>{fmtLive(buy, thresholdIsPercent)}</span>
        </FlashValue>
      </Tooltip>
      {'  ·  '}
      <Tooltip title="What Sell Near / Buy Far would net right now (near leg at bid, far leg at ask).">
        Sell spread:{' '}
        <FlashValue value={sell}>
          <span style={{ color: sell >= 0 ? '#3ddc84' : '#ff5c5c', fontWeight: 600 }}>{fmtLive(sell, thresholdIsPercent)}</span>
        </FlashValue>
      </Tooltip>
    </Typography.Text>
  )
}

// Every trigger created here is now the same simple shape: a plain SIGNED absolute spread target
// (e.g. -8, not a deviation magnitude away from zero), an explicit >= / <= comparison against it,
// and manual Buy/Sell/None leg actions you set yourself - no "watch both sides"/auto-direction
// mode where the engine decides live which leg to buy/sell from whichever side happened to cross.
// The backend/engine already treat Threshold as a literal comparison value (see
// SpreadCalculationHostedService.EvaluateTriggersAsync - `measured >= rule.Threshold` /
// `measured <= rule.Threshold`), so this is a frontend simplification only - no API contract
// changes were needed for single-spread rules.
// isExitTrigger picks which backend store the created rule lands in - POST /api/exit-triggers
// (a completely separate store/CSV file from /api/triggers - see IExitTriggerRuleStore) instead
// of the ordinary one, so exit rules never mix into the same table as entry rules. Everything
// else - the form, validation, what's sent in the request body - is identical either way.
// initialSpreadId seeds the spread picker once on mount (e.g. "Add trigger" on a Positions & PNL
// row navigates here with the matching spread already known - see PositionsTable.jsx/
// TriggersPage.jsx) - only read on the very first render (React's lazy useState initializer),
// same as every other field in emptyForm; changing the prop after mount does nothing, since this
// is a one-time default, not a controlled value.
export default function TriggerForm({ spreads, onCreated, isExitTrigger = false, initialSpreadId = '' }) {
  const [form, setForm] = useState(() => ({ ...emptyForm, spreadId: initialSpreadId }))
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState(null)

  // Live spreadId -> SpreadSnapshot map, same source SpreadGrid/Live Spreads uses - shown right
  // under the spread picker below so you can see where the spread is trading right now while
  // typing a target, without leaving this page to go check the Live Spreads grid.
  const liveSpreads = useLiveSpreads()

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
    if (form.nearLegAction === 'None' && form.farLegAction === 'None') {
      setError('Pick a Buy/Sell action for at least one leg.')
      return
    }
    if (!form.spreadId) {
      setError('Select a spread.')
      return
    }
    if (form.target === '') {
      setError('Enter a target spread value.')
      return
    }
    if (form.slippagePercent === '' || Number(form.slippagePercent) <= 0) {
      setError('Enter a slippage % greater than 0.')
      return
    }
    if (!form.oneShot && (form.maxFireCount === '' || Number(form.maxFireCount) <= 0)) {
      setError('Enter a number of shots (1-15).')
      return
    }
    setBusy(true)
    setError(null)
    try {
      const create = isExitTrigger ? api.createExitTrigger : api.createTrigger
      await create({
        id: generateRuleId(),
        spreadId: form.spreadId,
        // The raw signed target itself - NOT a magnitude. fireWhenGreaterOrEqual carries the
        // direction explicitly instead of relying on this value's sign, so a negative target
        // (e.g. -8) works identically whichever way you picked below.
        threshold: Number(form.target),
        fireWhenGreaterOrEqual: form.operator === 'gte',
        thresholdIsPercent: form.thresholdIsPercent,
        nearLegAction: form.nearLegAction,
        farLegAction: form.farLegAction,
        lots: Number(form.lots),
        product: form.product,
        orderType: form.orderType,
        oneShot: form.oneShot,
        maxFireCount: Number(form.maxFireCount || 0),
        slippagePercent: Number(form.slippagePercent),
      })
      setForm({ ...emptyForm, spreadId: form.spreadId })
      onCreated()
    } catch (err) {
      setError(err.message)
    } finally {
      setBusy(false)
    }
  }

  const unit = form.thresholdIsPercent ? '%' : ''
  const liveSnapshot = form.spreadId ? liveSpreads[form.spreadId] : null

  return (
    <Card title={<Space><PlusCircleOutlined style={{ color: '#3ddc84' }} /> {isExitTrigger ? 'Add exit trigger' : 'Add trigger'}</Space>} style={{ marginBottom: 16 }}>
      <form onSubmit={submit}>
        <Space direction="vertical" size="middle" style={{ width: '100%' }}>
          <Typography.Text type="secondary">
            {isExitTrigger
              ? 'Same mechanism as an ordinary trigger, just tagged as an exit - set the leg actions to whatever CLOSES your position (opposite of however you entered it).'
              : "Evaluated against each leg's best ask price, recomputed live - see the Dashboard page."}
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
              {form.spreadId && <LiveSpreadPrice snapshot={liveSnapshot} thresholdIsPercent={form.thresholdIsPercent} />}
            </Col>
          </Row>

          <Row gutter={[12, 12]}>
            <Col xs={24}>
              <InputNumber
                step={0.05}
                placeholder={form.thresholdIsPercent ? 'Target spread % (signed)' : 'Target spread value (signed)'}
                value={form.target === '' ? null : form.target}
                onChange={(v) => set('target')(v ?? '')}
                style={{ width: '100%' }}
              />
              <Typography.Text type="secondary" style={{ fontSize: 12, display: 'block', marginTop: 4 }}>
                Enter the actual spread level (e.g. -8), not a deviation magnitude - pick the
                comparison below, and which leg to buy/sell further down.
              </Typography.Text>
            </Col>
          </Row>

          <Space>
            <Switch checked={form.thresholdIsPercent} onChange={set('thresholdIsPercent')} />
            <Typography.Text>Target is a % of the near leg's price (instead of a rupee amount)</Typography.Text>
          </Space>

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
                onChange={(e) => setOperator(e.target.value)}
                options={[
                  { label: `Rises to ${form.target === '' ? 'this' : form.target}${unit} or higher`, value: 'gte' },
                  { label: `Falls to ${form.target === '' ? 'this' : form.target}${unit} or lower`, value: 'lte' },
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

          {/* Plain-English restatement of exactly what this rule will do, using the actual
              contract names and choices made above - e.g. "once KPITTECH26JULFUT vs
              KPITTECH26AUGFUT difference rises to -8 or higher: KPITTECH26JULFUT will be
              BOUGHT, KPITTECH26AUGFUT will be SOLD" - named after the two actual contracts
              instead of the abstract word "spread" or raw comparison symbols. */}
          {selectedSpread && form.target !== '' && (form.nearLegAction !== 'None' || form.farLegAction !== 'None') && (
            <Alert
              type="info"
              showIcon
              message={
                <>
                  Once <b>{selectedSpread.nearLeg.tradingSymbol}</b> vs <b>{selectedSpread.farLeg.tradingSymbol}</b> difference <b>{form.operator === 'gte' ? `rises to ${form.target}${unit} or higher` : `falls to ${form.target}${unit} or lower`}</b>:
                  {form.nearLegAction !== 'None' && <> <b>{selectedSpread.nearLeg.tradingSymbol}</b> will be <b>{form.nearLegAction === 'Buy' ? 'BOUGHT' : 'SOLD'}</b></>}
                  {form.nearLegAction !== 'None' && form.farLegAction !== 'None' && ', and'}
                  {form.farLegAction !== 'None' && <> <b>{selectedSpread.farLeg.tradingSymbol}</b> will be <b>{form.farLegAction === 'Buy' ? 'BOUGHT' : 'SOLD'}</b></>}
                  .
                </>
              }
            />
          )}

          <Row gutter={[12, 12]}>
            <Col xs={24} sm={6}>
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
            <Col xs={24} sm={6}>
              <Select
                value={form.product}
                onChange={set('product')}
                style={{ width: '100%' }}
                options={[{ value: 'NRML', label: 'NRML' }, { value: 'MIS', label: 'MIS' }]}
              />
            </Col>
            <Col xs={24} sm={6}>
              <Select
                value={form.orderType}
                onChange={set('orderType')}
                style={{ width: '100%' }}
                options={[{ value: 'MARKET', label: 'MARKET' }, { value: 'LIMIT', label: 'LIMIT' }]}
              />
            </Col>
            <Col xs={24} sm={6}>
              <InputNumber
                min={0.05}
                step={0.05}
                addonAfter="%"
                placeholder="Slippage"
                value={form.slippagePercent === '' ? null : form.slippagePercent}
                onChange={(v) => set('slippagePercent')(v ?? '')}
                style={{ width: '100%' }}
              />
              <Typography.Text type="secondary" style={{ fontSize: 12, display: 'block', marginTop: 4 }}>
                How far past the current price we're allowed to fill at, on both legs, so the order goes through right away instead of possibly sitting unfilled. Steps in 0.05% increments - type any value.
              </Typography.Text>
            </Col>
          </Row>

          <Row gutter={[12, 12]} align="middle">
            <Col xs={24}>
              <Space>
                <Switch checked={form.oneShot} onChange={set('oneShot')} />
                <Typography.Text>One-shot (disable after firing)</Typography.Text>
              </Space>
              <Typography.Text type="secondary" style={{ fontSize: 12, display: 'block', marginTop: 4 }}>
                {form.oneShot
                  ? `Fires once when the spread first crosses ${form.target === '' ? 'your target' : form.target}${unit}, then disables itself - it won't fire again until you re-enable it. Turn this off to fire more than once (see Number of shots below).`
                  : `Fires up to ${form.maxFireCount || 'N'} time(s) total. Each shot fires as soon as the spread crosses ${form.target === '' ? 'your target' : form.target}${unit} again AND the previous shot's order has actually filled or been rejected - no need for the spread to retreat first. If it's briefly not crossed when checked, that's just skipped - the count picks up again next time it crosses, it isn't lost. Disables itself automatically after the last shot.`}
              </Typography.Text>
            </Col>
          </Row>

          {!form.oneShot && (
            <Row gutter={[12, 12]} align="middle">
              <Col xs={24} sm={12}>
                <InputNumber
                  min={1}
                  max={15}
                  step={1}
                  addonBefore="Shots"
                  value={form.maxFireCount === '' ? null : form.maxFireCount}
                  onChange={(v) => set('maxFireCount')(v ?? '')}
                  style={{ width: '100%' }}
                />
                <Typography.Text type="secondary" style={{ fontSize: 12, display: 'block', marginTop: 4 }}>
                  How many times this rule fires in total (1-15), one right after another completes.
                </Typography.Text>
              </Col>
            </Row>
          )}

          {error && <Alert type="error" message={error} showIcon />}
          <Button type="primary" htmlType="submit" loading={busy}>{isExitTrigger ? 'Add exit trigger' : 'Add trigger'}</Button>
        </Space>
      </form>
    </Card>
  )
}
