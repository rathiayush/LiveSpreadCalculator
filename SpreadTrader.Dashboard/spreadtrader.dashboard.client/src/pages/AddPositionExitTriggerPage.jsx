import { useEffect, useMemo, useState } from 'react'
import { useLocation, useNavigate, Link } from 'react-router-dom'
import { Card, Row, Col, InputNumber, Select, Switch, Button, Alert, Typography, Space, Spin } from 'antd'
import { ScissorOutlined, ArrowLeftOutlined } from '@ant-design/icons'
import { api } from '../api'
import { useLiveSpreads } from '../hooks/spreadHub'

const LEG_ACTIONS = ['None', 'Buy', 'Sell']

// Mirrors TriggerForm's identical helper - rule id is an internal identifier only, generated
// client-side rather than collected as a form field.
const generateRuleId = () =>
  (typeof crypto !== 'undefined' && crypto.randomUUID)
    ? crypto.randomUUID()
    : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`

function sameInstrument(a, tradingSymbol, exchange) {
  return a && a.tradingSymbol?.toUpperCase() === tradingSymbol?.toUpperCase() && a.exchange?.toUpperCase() === exchange?.toUpperCase()
}

function fmt(v) {
  return v === null || v === undefined ? '—' : v.toFixed(2)
}

// "Opposite" of TriggerForm's normal Add Trigger flow: that page opens a fresh spread position
// when the near/far spread value crosses a threshold. This page instead builds an EXIT rule for
// a position you already hold, anchored to an absolute TARGET spread level rather than a
// "deviation magnitude" - e.g. entered at spread -15, exit once it's recovered to -7. That's
// deliberately NOT the same threshold UI as the ordinary Add Trigger page: a plain magnitude +
// gte/lte picker can't represent "fire when spread RISES to a NEGATIVE target" (sign-inference
// on the backend would otherwise always read a negative threshold as "falls to" - see
// CreateTriggerRuleRequest.FireWhenGreaterOrEqual, which this page sends explicitly to avoid
// that trap). Near/far leg actions default to whatever CLOSES what's currently open (long ->
// Sell, short -> Buy). The created rule is linked to this exact position (see TriggerRule.
// LinkedPositionTradingSymbol) so PositionsController.SquareOff deletes it automatically the
// moment you manually square off - an exit rule left armed against a position you already closed
// by hand no longer makes sense.
export default function AddPositionExitTriggerPage() {
  const location = useLocation()
  const navigate = useNavigate()
  const position = location.state?.position ?? null
  const liveSpreads = useLiveSpreads()

  // A real, always-in-the-same-place Button rather than relying only on an inline text Link
  // buried in an Alert's description, or the form's Cancel button at the very bottom of a long
  // page - reaching this page directly (a refresh, a bookmark, browser back/forward) loses the
  // position that was only ever passed via router state (see `position` above), so every branch
  // below needs an equally obvious way back to Positions & PNL, not just the happy path.
  const BackToPositions = () => (
    <Button
      icon={<ArrowLeftOutlined />}
      onClick={() => navigate('/positions')}
      style={{ marginBottom: 16 }}
    >
      Back to Positions & PNL
    </Button>
  )

  const [spreads, setSpreads] = useState(null)
  const [positions, setPositions] = useState(null)
  const [loadError, setLoadError] = useState(null)

  const [target, setTarget] = useState('')
  const [thresholdIsPercent, setThresholdIsPercent] = useState(false)
  const [nearLegAction, setNearLegAction] = useState('None')
  const [farLegAction, setFarLegAction] = useState('None')
  const [lots, setLots] = useState('')
  const [orderType, setOrderType] = useState('MARKET')
  const [slippagePercent, setSlippagePercent] = useState(0.10)
  const [oneShot, setOneShot] = useState(true)
  const [rearmBuffer, setRearmBuffer] = useState('0')
  const [busy, setBusy] = useState(false)
  const [submitError, setSubmitError] = useState(null)

  useEffect(() => {
    if (!position) return
    Promise.all([api.getSpreads(), api.getPositions()])
      .then(([spreadList, positionsResult]) => {
        setSpreads(spreadList)
        setPositions(positionsResult.success ? positionsResult.positions : [])
      })
      .catch((err) => setLoadError(err.message))
  }, [position])

  const matchedSpread = useMemo(() => {
    if (!spreads || !position) return null
    return spreads.find((s) =>
      sameInstrument(s.nearLeg, position.tradingSymbol, position.exchange) ||
      sameInstrument(s.farLeg, position.tradingSymbol, position.exchange))
  }, [spreads, position])

  // Which live position (if any) is currently open on each leg of the matched spread - used both
  // to default the leg actions to whatever closes them, and to default Lots to what's actually
  // safe to exit (never more than what's really open on both legs at once).
  const nearPosition = useMemo(
    () => (matchedSpread && positions ? positions.find((p) => sameInstrument(matchedSpread.nearLeg, p.tradingSymbol, p.exchange)) : null),
    [matchedSpread, positions])
  const farPosition = useMemo(
    () => (matchedSpread && positions ? positions.find((p) => sameInstrument(matchedSpread.farLeg, p.tradingSymbol, p.exchange)) : null),
    [matchedSpread, positions])

  const lotSize = matchedSpread?.nearLeg?.lotSize || matchedSpread?.farLeg?.lotSize || 0

  // The spread value/percent at the time you entered, derived from each leg's average_price -
  // matches SpreadDefinition's own convention (FarLeg price minus NearLeg price, percent of the
  // near leg). Only meaningful once both legs actually have an open position.
  const entrySpreadValue = (nearPosition && farPosition) ? farPosition.averagePrice - nearPosition.averagePrice : null
  const entrySpreadPercent = (entrySpreadValue !== null && nearPosition?.averagePrice)
    ? (entrySpreadValue / nearPosition.averagePrice) * 100
    : null

  // The live spread value right now (see hooks/spreadHub.js) - falls back to the entry figure
  // above if the ticker hasn't produced a value for this spread yet (e.g. it was only just
  // configured, or the market is closed).
  const liveSnapshot = matchedSpread ? liveSpreads[matchedSpread.id] : null
  const currentValue = liveSnapshot?.spreadValue ?? entrySpreadValue
  const currentPercent = liveSnapshot?.spreadPercent ?? entrySpreadPercent
  const currentReference = thresholdIsPercent ? currentPercent : currentValue

  // Pre-fill leg actions (closing whatever's actually held) and a safe default Lots figure, once
  // per successful match - re-runs only when the match/positions actually change, not on every
  // keystroke in the form below.
  useEffect(() => {
    if (!matchedSpread) return
    setNearLegAction(nearPosition && nearPosition.quantity !== 0 ? (nearPosition.quantity > 0 ? 'Sell' : 'Buy') : 'None')
    setFarLegAction(farPosition && farPosition.quantity !== 0 ? (farPosition.quantity > 0 ? 'Sell' : 'Buy') : 'None')

    if (lotSize > 0) {
      const nearOpenLots = nearPosition ? Math.abs(nearPosition.quantity) / lotSize : null
      const farOpenLots = farPosition ? Math.abs(farPosition.quantity) / lotSize : null
      const candidates = [nearOpenLots, farOpenLots].filter((v) => v !== null && v > 0)
      if (candidates.length > 0) setLots(Math.min(...candidates))
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [matchedSpread, nearPosition, farPosition, lotSize])

  // No manual gte/lte picker - Near/Far leg action here are always fixed to whatever CLOSES the
  // position regardless of which direction the spread approaches the target from, so unlike the
  // ordinary Add Trigger page (where picking an operator also picks the leg actions), there's
  // nothing for the user to actually decide here. The fire direction is derived automatically,
  // every render, from target vs. the current live reference - e.g. entry -15, target -7: -7 is
  // above -15, so the spread needs to rise to get there. Only null before a target's been typed
  // or before any reference value (entry or live) is known yet.
  const fireWhenGreaterOrEqual = (target === '' || currentReference === null || currentReference === undefined)
    ? null
    : Number(target) >= currentReference

  const submit = async (e) => {
    e.preventDefault()
    if (nearLegAction === 'None' && farLegAction === 'None') {
      setSubmitError('Pick a Buy/Sell action for at least one leg.')
      return
    }
    if (target === '') {
      setSubmitError('Enter a target spread value.')
      return
    }
    if (slippagePercent === '' || Number(slippagePercent) <= 0) {
      setSubmitError('Enter a slippage % greater than 0.')
      return
    }
    if (fireWhenGreaterOrEqual === null) {
      setSubmitError('Waiting for a live spread value to determine fire direction - try again in a moment.')
      return
    }
    setBusy(true)
    setSubmitError(null)
    try {
      await api.createTrigger({
        id: generateRuleId(),
        spreadId: matchedSpread.id,
        // The raw signed target itself - NOT a magnitude. fireWhenGreaterOrEqual (derived above,
        // automatically, from target vs. currentReference) carries the direction explicitly
        // instead of relying on this value's sign, since a negative target that the spread needs
        // to RISE to is exactly the case sign-inference gets wrong.
        threshold: Number(target),
        fireWhenGreaterOrEqual,
        thresholdIsPercent,
        watchBothSides: false,
        autoDirectionFromSpreadSign: false,
        nearLegAction,
        farLegAction,
        lots: Number(lots),
        product: position.product,
        orderType,
        slippagePercent: Number(slippagePercent),
        oneShot,
        rearmBuffer: Number(rearmBuffer || 0),
        linkedPositionTradingSymbol: position.tradingSymbol,
        linkedPositionExchange: position.exchange,
        linkedPositionProduct: position.product,
      })
      navigate('/positions')
    } catch (err) {
      setSubmitError(err.message)
    } finally {
      setBusy(false)
    }
  }

  if (!position) {
    return (
      <>
        <BackToPositions />
        <Alert
          type="warning"
          showIcon
          message="No position selected"
          description={<>Go back to <Link to="/positions">Positions & PNL</Link> and use "Add trigger" on a row - this page needs to know which position it's building an exit for.</>}
        />
      </>
    )
  }

  if (loadError) return <><BackToPositions /><Alert type="error" message={loadError} showIcon /></>
  if (!spreads || !positions) return <><BackToPositions /><Spin /></>

  if (!matchedSpread) {
    return (
      <>
        <BackToPositions />
        <Alert
          type="warning"
          showIcon
          message={`${position.tradingSymbol} isn't part of any configured spread`}
          description={
            <>
              An exit trigger needs a near/far calendar spread to evaluate a threshold against.
              Add {position.tradingSymbol} under <Link to="/configured-spreads">Configured Spreads</Link> first, then come back here.
            </>
          }
        />
      </>
    )
  }

  const unit = thresholdIsPercent ? '%' : ''

  return (
    <>
      <BackToPositions />
      <Card
        title={<Space><ScissorOutlined style={{ color: '#f5a623' }} /> Add exit trigger - {position.tradingSymbol}</Space>}
        style={{ marginBottom: 16 }}
      >
      <form onSubmit={submit}>
        <Space direction="vertical" size="middle" style={{ width: '100%' }}>
          <Typography.Text type="secondary">
            Spread: <b>{matchedSpread.underlying}</b> — {matchedSpread.id}. Near/far leg actions
            below default to whatever CLOSES your current position on each leg - edit them if you
            want something else.
          </Typography.Text>

          <Space size="large" wrap>
            <Typography.Text>
              Entered at: <b>{thresholdIsPercent ? `${fmt(entrySpreadPercent)}%` : fmt(entrySpreadValue)}</b>
            </Typography.Text>
            <Typography.Text>
              Live now: <b>{liveSnapshot ? (thresholdIsPercent ? `${fmt(currentPercent)}%` : fmt(currentValue)) : 'not ticking yet'}</b>
            </Typography.Text>
          </Space>

          <Row gutter={[12, 12]}>
            <Col xs={24}>
              <InputNumber
                step={0.05}
                placeholder={thresholdIsPercent ? 'Target spread % (signed)' : 'Target spread value (signed)'}
                value={target === '' ? null : target}
                onChange={(v) => setTarget(v ?? '')}
                style={{ width: '100%' }}
              />
              <Typography.Text type="secondary" style={{ fontSize: 12, display: 'block', marginTop: 4 }}>
                Enter the actual spread level you want to exit at (e.g. -7), not a deviation
                magnitude - this fires once the spread reaches this exact value, from whichever
                direction it's currently coming from.
              </Typography.Text>
            </Col>
          </Row>

          <Space>
            <Switch checked={thresholdIsPercent} onChange={setThresholdIsPercent} />
            <Typography.Text>Target is a % of the near leg's price (instead of a rupee amount)</Typography.Text>
          </Space>

          <Row gutter={[12, 12]}>
            <Col xs={24}>
              {/* Informational only - no picker. Near/Far leg action below are always fixed to
                  whatever CLOSES the position no matter which way the spread gets there, so
                  (unlike the ordinary Add Trigger page) there's nothing to actually choose here -
                  this just tells you what firing this rule will mean, derived automatically from
                  target vs. the current live reference. Plain English throughout (rises/falls to
                  X or higher/lower, named after the two actual contracts) rather than raw >=/<=
                  symbols or the abstract word "spread" - matches TriggerForm.jsx's wording. */}
              <Typography.Text type="secondary" style={{ display: 'block' }}>
                {target === '' || currentReference === null || currentReference === undefined ? (
                  'Fire when the spread reaches your target - enter it above.'
                ) : (
                  <>
                    Once <b>{matchedSpread.nearLeg.tradingSymbol}</b> vs <b>{matchedSpread.farLeg.tradingSymbol}</b> difference{' '}
                    <b>{fireWhenGreaterOrEqual ? `rises to ${target}${unit} or higher` : `falls to ${target}${unit} or lower`}</b>
                    {' '}(currently {thresholdIsPercent ? `${fmt(currentReference)}%` : fmt(currentReference)}):
                    {nearLegAction !== 'None' && <> <b>{matchedSpread.nearLeg.tradingSymbol}</b> will be <b>{nearLegAction === 'Buy' ? 'BOUGHT' : 'SOLD'}</b></>}
                    {nearLegAction !== 'None' && farLegAction !== 'None' && ', and'}
                    {farLegAction !== 'None' && <> <b>{matchedSpread.farLeg.tradingSymbol}</b> will be <b>{farLegAction === 'Buy' ? 'BOUGHT' : 'SOLD'}</b></>}
                    .
                  </>
                )}
              </Typography.Text>
            </Col>
          </Row>

          <Row gutter={[12, 12]}>
            <Col xs={24} sm={12}>
              <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 4 }}>
                Near leg action {nearPosition && nearPosition.quantity !== 0 && <Typography.Text type="secondary">(currently {nearPosition.quantity > 0 ? 'LONG' : 'SHORT'})</Typography.Text>}
              </Typography.Text>
              <Select
                value={nearLegAction}
                onChange={setNearLegAction}
                style={{ width: '100%' }}
                options={LEG_ACTIONS.map((a) => ({ value: a, label: a }))}
              />
            </Col>
            <Col xs={24} sm={12}>
              <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 4 }}>
                Far leg action {farPosition && farPosition.quantity !== 0 && <Typography.Text type="secondary">(currently {farPosition.quantity > 0 ? 'LONG' : 'SHORT'})</Typography.Text>}
              </Typography.Text>
              <Select
                value={farLegAction}
                onChange={setFarLegAction}
                style={{ width: '100%' }}
                options={LEG_ACTIONS.map((a) => ({ value: a, label: a }))}
              />
            </Col>
          </Row>

          <Row gutter={[12, 12]}>
            <Col xs={24} sm={6}>
              <InputNumber
                min={1}
                max={lotSize > 0 ? Math.floor(Math.abs(position.quantity) / lotSize) || undefined : undefined}
                placeholder="Lots"
                addonBefore="Lots"
                value={lots === '' ? null : lots}
                onChange={(v) => setLots(v ?? '')}
                style={{ width: '100%' }}
              />
              <Typography.Text type="secondary" style={{ fontSize: 12, display: 'block', marginTop: 4 }}>
                {lotSize > 0
                  ? `Lot size ${lotSize}${lots ? ` — ${Number(lots) * lotSize} shares/contracts per leg` : ''}`
                  : 'Lot size not resolved yet for this spread'}
              </Typography.Text>
            </Col>
            <Col xs={24} sm={6}>
              <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 4 }}>Product</Typography.Text>
              <Select value={position.product} disabled style={{ width: '100%' }} options={[{ value: position.product, label: position.product }]} />
            </Col>
            <Col xs={24} sm={6}>
              <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 4 }}>Order type</Typography.Text>
              <Select
                value={orderType}
                onChange={setOrderType}
                style={{ width: '100%' }}
                options={[{ value: 'MARKET', label: 'MARKET' }, { value: 'LIMIT', label: 'LIMIT' }]}
              />
            </Col>
            <Col xs={24} sm={6}>
              <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 4 }}>Slippage</Typography.Text>
              <InputNumber
                min={0.05}
                step={0.05}
                addonAfter="%"
                placeholder="Slippage"
                value={slippagePercent === '' ? null : slippagePercent}
                onChange={(v) => setSlippagePercent(v ?? '')}
                style={{ width: '100%' }}
              />
              <Typography.Text type="secondary" style={{ fontSize: 12, display: 'block', marginTop: 4 }}>
                How far past the current price each leg may fill at, so the exit goes through right away. Steps in 0.05% increments - type any value.
              </Typography.Text>
            </Col>
          </Row>

          <Row gutter={[12, 12]} align="middle">
            <Col xs={24} sm={12}>
              <Space>
                <Switch checked={oneShot} onChange={setOneShot} />
                <Typography.Text>One-shot (disable after firing)</Typography.Text>
              </Space>
            </Col>
            <Col xs={24} sm={12}>
              <InputNumber
                step={0.05}
                placeholder="Re-arm buffer"
                value={rearmBuffer === '' ? null : rearmBuffer}
                onChange={(v) => setRearmBuffer(v ?? '')}
                disabled={oneShot}
                style={{ width: '100%' }}
              />
            </Col>
          </Row>

          {submitError && <Alert type="error" message={submitError} showIcon />}
          <Space>
            <Button type="primary" htmlType="submit" loading={busy}>Add exit trigger</Button>
            <Button onClick={() => navigate('/positions')}>Cancel</Button>
          </Space>
        </Space>
      </form>
      </Card>
    </>
  )
}
