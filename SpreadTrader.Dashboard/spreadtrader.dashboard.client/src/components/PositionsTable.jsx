import { useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Card, Table, Tag, Typography, Space, InputNumber, Button, Modal, Switch, message, Alert, Tooltip, Grid, Checkbox, Divider } from 'antd'
import { CloseCircleOutlined, ScissorOutlined, ReloadOutlined, ThunderboltOutlined, BellOutlined, ArrowDownOutlined, ArrowUpOutlined, ExclamationCircleOutlined } from '@ant-design/icons'
import { api } from '../api'

// Stable row identity - symbol|exchange|product - used both as the antd rowKey for leg rows and
// as the key into modalLots below, since positions are re-fetched wholesale on every poll (see
// PositionsPage) and object identity can't be relied on across refreshes.
function rowKey(p) {
  return `${p.tradingSymbol}|${p.exchange}|${p.product}`
}

// A PNL alert id is an internal identifier only, generated client-side rather than collected as
// a form field - same pattern TriggerForm.jsx uses for a trigger rule's id.
function generateAlertId() {
  return (typeof crypto !== 'undefined' && crypto.randomUUID)
    ? crypto.randomUUID()
    : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`
}

// Kept local rather than shared since it's a one-line comparison, not worth a shared module for.
function sameInstrument(a, tradingSymbol, exchange) {
  return a && a.tradingSymbol?.toUpperCase() === tradingSymbol?.toUpperCase() && a.exchange?.toUpperCase() === exchange?.toUpperCase()
}

// Live open positions + PNL, grouped by configured spread (near/far legs together, with combined
// running PNL and combined Exit PNL on the group's own row), plus the ability to square off some
// or all of the lots on a leg, a whole spread, or a multi-row selection - the manual "cut position
// via the dashboard" flow requested instead of needing the Kite app (which would kill this app's
// own session - see AuthController/KiteAuthService).
export default function PositionsTable({ positions, spreads, onChanged, refreshing, onRefresh }) {
  const navigate = useNavigate()
  const [selectedKeys, setSelectedKeys] = useState([])
  const [error, setError] = useState(null)

  // Below the `md` breakpoint (~768px), the fixed-column Table with scroll:{x:'max-content'}
  // means constant horizontal scrolling to reach the numbers that matter (PNL/Exit PNL) - swap
  // to a stacked card list instead (see LegLine/GroupCard below), same grouping/actions, just
  // laid out vertically. screens.md is `undefined` for one tick before antd's resize observer measures
  // the viewport, which would flash the desktop table first on a phone - defaulting to `true`
  // (mobile) whenever it's still unknown avoids that flash in the far more common "opened on a
  // phone" case, at the cost of a one-tick flash the other way on desktop, which is much less
  // noticeable on a wide screen.
  const screens = Grid.useBreakpoint()
  const isMobile = screens.md === undefined ? true : !screens.md

  const totalPnl = useMemo(() => positions.reduce((sum, p) => sum + (p.pnl ?? 0), 0), [positions])

  const openLots = (p) => (p.lotSize > 0 ? Math.abs(p.quantity) / p.lotSize : null)

  // Group each configured spread's near/far legs together, with the combined (both-legs-netted)
  // Exit PNL shown on the group's own row - that combined number, not either leg in isolation, is
  // what actually tells you whether closing the whole spread right now is worth it, net of both
  // legs' bid/ask spread (see PositionExitPricing.Compute). Positions that aren't part of any
  // configured spread (or whose spread was removed/renamed after the position was opened) fall
  // through as plain standalone/naked rows, same as before grouping existed - there's nothing to
  // net them against. Rendered via antd Table's built-in tree support (a `children` array on a
  // row automatically gets an expand/collapse toggle), so no separate grouping library is needed.
  const grouped = useMemo(() => {
    const rows = []
    const used = new Set()

    for (const s of spreads ?? []) {
      const nearPos = positions.find((p) => sameInstrument(s.nearLeg, p.tradingSymbol, p.exchange))
      const farPos = positions.find((p) => sameInstrument(s.farLeg, p.tradingSymbol, p.exchange))
      const legs = [nearPos, farPos].filter(Boolean)
      if (legs.length === 0) continue // neither leg currently open - nothing to group

      legs.forEach((p) => used.add(rowKey(p)))

      // Only netted once every open leg has a live quote to price off of - a "combined" figure
      // silently missing a leg would understate what closing the spread actually costs/pays,
      // which is worse than just showing nothing for a moment.
      const combinedExitPnl = legs.every((p) => p.exitPrice)
        ? legs.reduce((sum, p) => sum + (p.exitPnl ?? 0), 0)
        : null

      // The spread VALUE (not PNL) at entry and right now, in the same Far-minus-Near convention
      // SpreadDefinition/SpreadSnapshot use everywhere else - lets you see how far the spread
      // itself has actually moved since you got in, independent of quantity/lot size. Both need
      // the FULL pair (near AND far) to mean anything; a naked single leg has no "spread" to
      // measure, so these stay null/blank for a lone leg the same way combinedExitPnl does.
      //   - Average entered at: each leg's own AveragePrice (Kite's fill-weighted average).
      //   - Exit running spread: each leg's own ExitPrice (bid if long/selling-to-close, ask if
      //     short/buying-to-close - see PositionExitPricing.Compute) - what the spread would
      //     actually print if you closed both legs this instant, as opposed to the live-streamed
      //     SpreadSnapshot value (which is priced for a fresh ENTRY, not for unwinding this
      //     specific held position).
      const averageEnteredAt = (nearPos && farPos)
        ? farPos.averagePrice - nearPos.averagePrice
        : null
      const exitRunningSpread = (nearPos && farPos && nearPos.exitPrice && farPos.exitPrice)
        ? farPos.exitPrice - nearPos.exitPrice
        : null

      rows.push({
        key: `group|${s.id}`,
        isGroup: true,
        spreadId: s.id,
        underlying: s.underlying,
        legCount: legs.length,
        hasFullPair: legs.length === 2,
        combinedPnl: legs.reduce((sum, p) => sum + (p.pnl ?? 0), 0),
        combinedExitPnl,
        averageEnteredAt,
        exitRunningSpread,
        children: legs,
      })
    }

    for (const p of positions) {
      if (!used.has(rowKey(p))) rows.push(p)
    }

    return rows
  }, [positions, spreads])

  // Groups start expanded and stay that way through every live poll/push refresh, without
  // fighting a manual collapse. antd's own `defaultExpandAllRows` only applies once, at mount -
  // useless here since `positions` (and therefore `grouped`) is very likely still empty on that
  // first render while the initial fetch is in flight, so there's nothing yet to expand. Instead,
  // expandedKeys is owned here and a group's key is added to it exactly once, the first time that
  // group is ever seen - after that, collapsing it is a deliberate user action that later
  // refreshes (which just rebuild `grouped` with fresh row objects but the same stable keys) won't
  // silently undo.
  const [expandedKeys, setExpandedKeys] = useState([])
  const seenGroupKeys = useRef(new Set())
  useEffect(() => {
    const newKeys = grouped.filter((r) => r.isGroup && !seenGroupKeys.current.has(r.key)).map((r) => r.key)
    if (newKeys.length === 0) return
    newKeys.forEach((k) => seenGroupKeys.current.add(k))
    setExpandedKeys((prev) => [...prev, ...newKeys])
  }, [grouped])

  // Square-off confirmation popup - asks for lots per leg (defaulted to the full amount open, but
  // editable down to a partial exit) and one slippage % applied to every leg in this action, then
  // executes exactly those numbers, nothing more. Shared by a single leg's Exit button, a group's
  // "Square off spread" button, and the multi-row "Square off selected" button - `target` is just
  // { legs: [...positions to close], title }.
  const [target, setTarget] = useState(null)
  const [modalLots, setModalLots] = useState({}) // rowKey -> lots typed in the popup
  const [modalSlippage, setModalSlippage] = useState(0.10)
  const [modalBusy, setModalBusy] = useState(false)

  const openSquareOff = (legs, title) => {
    const initialLots = {}
    legs.forEach((p) => { initialLots[rowKey(p)] = openLots(p) ?? '' })
    setModalLots(initialLots)
    setModalSlippage(0.10)
    setTarget({ legs, title })
  }

  const squareOff = async (p, lots, slippagePercent) => {
    try {
      const result = await api.squareOffPosition(p, lots, slippagePercent)
      if (!result.success) {
        message.error(`Square-off failed for ${p.tradingSymbol}: ${result.errorMessage || result.status || 'rejected'}`)
      } else {
        message.success(`Exited ${lots} lot(s) of ${p.tradingSymbol} (${result.status})`)
        setSelectedKeys((keys) => keys.filter((k) => k !== rowKey(p)))
      }
      onChanged()
    } catch (err) {
      setError(err.message)
    }
  }

  // Validates the lots/slippage typed into the square-off modal, then hands off to a SECOND,
  // explicit "are you sure" (see runSquareOff) before anything is actually sent to the exchange.
  // The first modal is really a form - easy to hit Enter or click through without re-reading the
  // numbers you just typed - so nothing gets placed until this separate confirm is accepted.
  const confirmSquareOff = () => {
    if (!target) return
    setError(null)

    const slip = Number(modalSlippage)
    if (!modalSlippage || slip <= 0) {
      message.error('Enter a slippage % greater than 0.')
      return
    }
    for (const p of target.legs) {
      const lots = Number(modalLots[rowKey(p)])
      if (!lots || lots <= 0) {
        message.error(`Enter lots to exit for ${p.tradingSymbol}.`)
        return
      }
      const max = openLots(p)
      if (max !== null && lots > max) {
        message.error(`${p.tradingSymbol}: only ${max} lot(s) open.`)
        return
      }
    }

    Modal.confirm({
      title: 'Confirm square-off',
      icon: <ExclamationCircleOutlined style={{ color: '#cf1322' }} />,
      okText: 'Yes, square off',
      okButtonProps: { danger: true },
      cancelText: 'Cancel',
      content: (
        <div>
          {target.legs.map((p) => {
            const key = rowKey(p)
            return (
              <div key={key} style={{ marginBottom: 4 }}>
                {p.quantity > 0 ? 'SELL' : 'BUY'} <strong>{Number(modalLots[key])}</strong> lot(s) of <strong>{p.tradingSymbol}</strong>
              </div>
            )
          })}
          <div style={{ marginTop: 8 }}>Slippage: {slip}%</div>
          <div style={{ marginTop: 8 }}>
            <Typography.Text type="secondary">This places real order(s) immediately and cannot be undone.</Typography.Text>
          </div>
        </div>
      ),
      onOk: () => runSquareOff(slip),
    })
  }

  const runSquareOff = async (slip) => {
    setModalBusy(true)
    try {
      // Exit every SELL-side leg (closing a long position) before any BUY-side leg (covering a
      // short) - keeps longs already cut and margin/exposure reduced before any short gets
      // covered, rather than the two happening in whatever order the legs were listed in.
      const sells = target.legs.filter((p) => p.quantity > 0)
      const buys = target.legs.filter((p) => p.quantity < 0)
      for (const p of [...sells, ...buys]) {
        await squareOff(p, Number(modalLots[rowKey(p)]), slip)
      }
      setTarget(null)
    } finally {
      setModalBusy(false)
    }
  }

  // Standing "notify me on Telegram" watches on a spread's combined Exit PNL - see PnlAlertRule /
  // PnlAlertHostedService (backend). Loaded once on mount and refetched after any create/cancel
  // from this component - an alert can also be auto-disabled server-side after it fires, which
  // this page won't notice until its next reload/create/cancel, same staleness tradeoff as
  // everything else here that isn't itself pushed over SignalR.
  const [alerts, setAlerts] = useState([])
  const loadAlerts = () => { api.getPnlAlerts().then(setAlerts).catch(() => {}) }
  useEffect(() => { loadAlerts() }, [])

  const alertForSpread = (spreadId) => alerts.find((a) => a.spreadId === spreadId && a.enabled)

  const [alertTarget, setAlertTarget] = useState(null) // { spreadId, underlying, currentExitPnl }
  const [alertValue, setAlertValue] = useState('')
  // 'fall' | 'rise' - EXPLICIT, not silently inferred from comparing the target against whatever
  // combinedExitPnl happened to be in the row at the instant the bell icon was clicked. That
  // inference is a real footgun: Exit PNL can already have crossed the target by the time the
  // modal is actually confirmed (a few seconds of typing is plenty at F&O speeds), which silently
  // flips the direction to "wait for it to recover back up to the target" instead of "keep
  // watching it fall past" - the alert then never fires again while PNL keeps moving the same way
  // it already was, with no error anywhere to say so. Auto-suggested below (see the effect) so the
  // common case still needs no thought, but always visible and overridable before you confirm.
  const [alertDirection, setAlertDirection] = useState('fall')
  const [alertDirectionTouched, setAlertDirectionTouched] = useState(false)
  const [alertOneShot, setAlertOneShot] = useState(true)
  const [alertBusy, setAlertBusy] = useState(false)

  const openPnlAlert = (r) => {
    setAlertValue('')
    setAlertDirection('fall')
    setAlertDirectionTouched(false)
    setAlertOneShot(true)
    setAlertTarget({ spreadId: r.spreadId, underlying: r.underlying, currentExitPnl: r.combinedExitPnl })
  }

  // Auto-suggests a direction as soon as there's a target to compare against the Exit PNL that was
  // current when the modal opened - purely a convenience default. Stops re-suggesting the moment
  // the user touches the Segmented control themselves, so it can't fight a deliberate choice.
  useEffect(() => {
    if (!alertTarget || alertValue === '' || alertDirectionTouched) return
    const current = alertTarget.currentExitPnl ?? 0
    setAlertDirection(Number(alertValue) >= current ? 'rise' : 'fall')
  }, [alertValue, alertTarget, alertDirectionTouched])

  const confirmPnlAlert = async () => {
    if (!alertTarget) return
    if (alertValue === '' || Number.isNaN(Number(alertValue))) {
      message.error('Enter a target Exit PNL.')
      return
    }

    setAlertBusy(true)
    try {
      await api.createPnlAlert({
        id: generateAlertId(),
        spreadId: alertTarget.spreadId,
        targetExitPnl: Number(alertValue),
        // Whatever the Segmented control is currently showing - the confirmed, visible choice,
        // not a value silently recomputed from a possibly-stale snapshot (see alertDirection above).
        fireWhenGreaterOrEqual: alertDirection === 'rise',
        oneShot: alertOneShot,
      })
      message.success(`PNL alert set for ${alertTarget.underlying}`)
      setAlertTarget(null)
      loadAlerts()
    } catch (err) {
      message.error(err.message)
    } finally {
      setAlertBusy(false)
    }
  }

  const cancelPnlAlert = async (alert) => {
    try {
      await api.deletePnlAlert(alert.id)
      message.success('PNL alert cancelled')
      loadAlerts()
    } catch (err) {
      message.error(err.message)
    }
  }

  // Bell button for a group row/card - "PNL alert" when none is set for this spread, or "Alert @
  // <target>" (click to cancel) when one already is. Shared by the desktop Exit column and the
  // mobile GroupCard so the two stay in sync without duplicating the lookup/render logic.
  const PnlAlertButton = ({ r }) => {
    const alert = alertForSpread(r.spreadId)
    return alert ? (
      <Tooltip title="Click to cancel this alert">
        <Button size="small" danger icon={<BellOutlined />} onClick={() => cancelPnlAlert(alert)}>
          Alert @ {alert.targetExitPnl >= 0 ? '+' : ''}{alert.targetExitPnl}
        </Button>
      </Tooltip>
    ) : (
      <Tooltip title="Get a Telegram message once this spread's combined Exit PNL reaches a value you pick">
        <Button size="small" icon={<BellOutlined />} onClick={() => openPnlAlert(r)}>
          PNL alert
        </Button>
      </Tooltip>
    )
  }

  // A handful of columns (Exchange/Product/Avg price/LTP) don't mean anything combined across two
  // different instruments, so group rows render blank there - the group row's job is just to
  // carry Underlying, combined PNL, and combined Exit PNL; the two leg rows underneath (expanded
  // by default) carry every per-instrument detail.
  const columns = [
    // width is required (not just cosmetic) on a fixed column - without it, antd can't reliably
    // compute the sticky offset for the columns after it before layout settles, especially on a
    // narrow/mobile viewport, which is what caused this column's text to render overlapping the
    // Exchange column next to it instead of pushing it aside.
    {
      title: 'Symbol',
      key: 'tradingSymbol',
      fixed: 'left',
      width: 200,
      render: (_, r) => r.isGroup ? (
        <Space size={4}>
          <strong>{r.underlying}</strong>
          <Tag color="blue">spread</Tag>
          {!r.hasFullPair && <Tag color="orange">1 leg</Tag>}
        </Space>
      ) : r.tradingSymbol,
    },
    { title: 'Exchange', key: 'exchange', render: (_, r) => r.isGroup ? '' : r.exchange },
    { title: 'Product', key: 'product', render: (_, r) => r.isGroup ? '' : r.product },
    {
      title: 'Side',
      key: 'side',
      render: (_, r) => r.isGroup ? '' : (r.quantity > 0 ? <Tag color="green">LONG</Tag> : <Tag color="red">SHORT</Tag>),
    },
    {
      // What squaring this leg off RIGHT NOW would actually cost/pay - a LONG position exits by
      // SELLING (priced at the live BID, what you'd receive), a SHORT position exits by BUYING
      // (priced at the live ASK, what you'd pay). Deliberately separate from PNL below, which is
      // Kite's own LTP-anchored figure - LTP can sit on either side of the book or be stale
      // between trades, so it isn't necessarily what you'd really get exiting this instant.
      // Doesn't combine across a group's two legs (they're different instruments/prices), so a
      // group row just notes how many legs are underneath instead. Leads the numeric columns
      // (ahead of Qty/Avg/LTP/PNL) since this - along with Exit PNL next to it - is what people
      // actually scan this table for; the rest is supporting detail.
      title: (
        <Tooltip title="Live price this leg would exit at right now - bid if you're long (selling to close), ask if you're short (buying to close). Different from LTP, which can sit on either side of the book.">
          Exit price
        </Tooltip>
      ),
      key: 'exitPrice',
      render: (_, r) => {
        if (r.isGroup) return <Typography.Text type="secondary" style={{ fontSize: 12 }}>{r.legCount} leg{r.legCount === 1 ? '' : 's'}</Typography.Text>
        return r.exitPrice
          ? <span>{r.exitPrice.toFixed(2)} <Typography.Text type="secondary" style={{ fontSize: 11 }}>({r.quantity > 0 ? 'bid' : 'ask'})</Typography.Text></span>
          : '—'
      },
    },
    {
      // The headline number for a group row: both legs' Exit PNL netted together, priced off live
      // bid/ask rather than LTP - what actually tells you whether closing this spread right now is
      // worth it, net of both legs' bid/ask spread (see PositionExitPricing.Compute). For a
      // standalone/naked leg it's just that one leg's own Exit PNL, same as before grouping.
      title: (
        <Tooltip title="For a spread group: both legs' Exit PNL netted together - what closing the whole spread right now would actually net you. For a single leg: that leg's own Exit PNL.">
          Exit PNL
        </Tooltip>
      ),
      key: 'exitPnl',
      render: (_, r) => {
        const v = r.isGroup ? r.combinedExitPnl : r.exitPnl
        const missing = r.isGroup ? v === null : !r.exitPrice
        if (missing) return '—'
        return (
          <Typography.Text strong style={{ color: v >= 0 ? '#3f8600' : '#cf1322', fontSize: r.isGroup ? 15 : undefined }}>
            {v >= 0 ? '+' : ''}{v.toFixed(2)}
          </Typography.Text>
        )
      },
      sorter: (a, b) => {
        const va = a.isGroup ? (a.combinedExitPnl ?? 0) : (a.exitPnl ?? 0)
        const vb = b.isGroup ? (b.combinedExitPnl ?? 0) : (b.exitPnl ?? 0)
        return va - vb
      },
    },
    {
      // Group-only (see averageEnteredAt above) - a naked single leg has no spread value to show
      // here, same as the blank cells combinedPnl/combinedExitPnl already leave for one.
      title: (
        <Tooltip title="Spread value (Far minus Near) at entry, from each leg's own average fill price - the same figure Add Exit Trigger shows as 'Entered at'.">
          Entered at
        </Tooltip>
      ),
      key: 'averageEnteredAt',
      render: (_, r) => (r.isGroup && r.averageEnteredAt !== null) ? r.averageEnteredAt.toFixed(2) : (r.isGroup ? '—' : ''),
      sorter: (a, b) => (a.isGroup ? (a.averageEnteredAt ?? 0) : 0) - (b.isGroup ? (b.averageEnteredAt ?? 0) : 0),
    },
    {
      title: (
        <Tooltip title="Spread value (Far minus Near) if you closed both legs right now, priced off each leg's own Exit price (bid if long, ask if short) - not the live-streamed spread value, which is priced for a fresh entry, not for unwinding this held position.">
          Running spread
        </Tooltip>
      ),
      key: 'exitRunningSpread',
      render: (_, r) => {
        if (!r.isGroup) return ''
        return r.exitRunningSpread === null
          ? '—'
          : <Typography.Text strong>{r.exitRunningSpread.toFixed(2)}</Typography.Text>
      },
      sorter: (a, b) => (a.isGroup ? (a.exitRunningSpread ?? 0) : 0) - (b.isGroup ? (b.exitRunningSpread ?? 0) : 0),
    },
    {
      title: 'Qty (lots)',
      key: 'quantity',
      render: (_, r) => {
        if (r.isGroup) return <Typography.Text type="secondary">{r.legCount} leg{r.legCount === 1 ? '' : 's'}</Typography.Text>
        const lots = openLots(r)
        return (
          <span>
            {Math.abs(r.quantity)}
            {lots !== null && <Typography.Text type="secondary"> ({lots} lot{lots === 1 ? '' : 's'})</Typography.Text>}
          </span>
        )
      },
    },
    {
      title: 'Avg price',
      key: 'averagePrice',
      render: (_, r) => r.isGroup ? '' : r.averagePrice?.toFixed(2),
    },
    {
      title: 'LTP',
      key: 'lastPrice',
      render: (_, r) => r.isGroup ? '' : r.lastPrice?.toFixed(2),
    },
    {
      // For a group row this is BOTH legs' Kite pnl netted together - the running combined PNL
      // for the spread as a whole, same convention as Total PNL up in the card header but scoped
      // to just this one spread instead of the whole account.
      title: 'PNL',
      key: 'pnl',
      render: (_, r) => {
        const v = r.isGroup ? r.combinedPnl : r.pnl
        if (v === undefined || v === null) return '—'
        return (
          <Typography.Text strong style={{ color: v >= 0 ? '#3f8600' : '#cf1322' }}>
            {v >= 0 ? '+' : ''}{v.toFixed(2)}
          </Typography.Text>
        )
      },
      sorter: (a, b) => (a.isGroup ? a.combinedPnl : (a.pnl ?? 0)) - (b.isGroup ? b.combinedPnl : (b.pnl ?? 0)),
    },
    {
      title: 'Exit',
      key: 'exit',
      render: (_, r) => {
        if (r.isGroup) {
          // "Add trigger" lives here now, once per spread, instead of duplicated on each leg row -
          // an exit trigger is evaluated against the near/far spread as a whole anyway, so it only
          // ever made sense at the group level. r.spreadId (computed in the `grouped` memo above)
          // goes straight to the Triggers page's Exit Trigger tab, which preselects it - see
          // TriggersPage/TriggerForm's initialSpreadId prop.
          return (
            <Space size="small">
              {r.hasFullPair && (
                <Button
                  size="small"
                  danger
                  icon={<CloseCircleOutlined />}
                  onClick={() => openSquareOff(r.children, `Square off both legs of ${r.underlying}?`)}
                >
                  Square off spread
                </Button>
              )}
              <Tooltip title="Build an exit trigger for this spread (fires automatically, deleted if you square off manually)">
                <Button
                  size="small"
                  icon={<ThunderboltOutlined />}
                  onClick={() => navigate('/triggers', { state: { tab: 'exit-trigger', spreadId: r.spreadId } })}
                >
                  Add trigger
                </Button>
              </Tooltip>
              <PnlAlertButton r={r} />
            </Space>
          )
        }
        const p = r
        return (
          <Button
            size="small"
            danger
            icon={<ScissorOutlined />}
            onClick={() => openSquareOff([p], `Square off ${p.tradingSymbol}?`)}
          >
            Exit
          </Button>
        )
      },
    },
  ]

  // One leg's detail line for the mobile card layout - the same fields the desktop columns show
  // (Side, Qty/Avg/LTP, PNL, Exit price/PNL) just stacked instead of columned, plus its own Exit
  // button and a selection checkbox so "Square off selected" still works the same way it does via
  // the Table's rowSelection on desktop. Used both inside a spread's GroupCard and for a naked
  // leg's own card below.
  const LegLine = ({ p }) => {
    const key = rowKey(p)
    const long = p.quantity > 0
    const lots = openLots(p)
    return (
      <div style={{ padding: '8px 0' }}>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 8 }}>
          <Space size={6}>
            <Checkbox
              checked={selectedKeys.includes(key)}
              onChange={(e) => setSelectedKeys((keys) => e.target.checked ? [...keys, key] : keys.filter((k) => k !== key))}
            />
            <Typography.Text strong>{p.tradingSymbol}</Typography.Text>
            <Tag color={long ? 'green' : 'red'}>{long ? 'LONG' : 'SHORT'}</Tag>
          </Space>
          <Button size="small" danger icon={<ScissorOutlined />} onClick={() => openSquareOff([p], `Square off ${p.tradingSymbol}?`)}>
            Exit
          </Button>
        </div>
        <Typography.Text type="secondary" style={{ fontSize: 12, display: 'block', marginTop: 2 }}>
          Qty {Math.abs(p.quantity)}{lots !== null ? ` (${lots} lot${lots === 1 ? '' : 's'})` : ''} · Avg {p.averagePrice?.toFixed(2)} · LTP {p.lastPrice?.toFixed(2)}
        </Typography.Text>
        <div style={{ marginTop: 4, display: 'flex', gap: 16, flexWrap: 'wrap' }}>
          <span>
            PNL{' '}
            <Typography.Text strong style={{ color: p.pnl >= 0 ? '#3f8600' : '#cf1322' }}>
              {p.pnl >= 0 ? '+' : ''}{p.pnl?.toFixed(2)}
            </Typography.Text>
          </span>
          <span>
            Exit{' '}
            {p.exitPrice ? (
              <>
                {p.exitPrice.toFixed(2)} <Typography.Text type="secondary" style={{ fontSize: 11 }}>({long ? 'bid' : 'ask'})</Typography.Text>
                {' · '}
                <Typography.Text strong style={{ color: p.exitPnl >= 0 ? '#3f8600' : '#cf1322' }}>
                  {p.exitPnl >= 0 ? '+' : ''}{p.exitPnl?.toFixed(2)}
                </Typography.Text>
              </>
            ) : '—'}
          </span>
        </div>
      </div>
    )
  }

  // A spread's group card - combined PNL/Exit PNL headline up top (the number that tells you
  // whether closing the whole spread now is worth it), each leg's own LegLine underneath, and the
  // same group-level actions (Square off spread / Add trigger) as the desktop Exit column.
  const GroupCard = ({ r }) => (
    <Card
      size="small"
      title={
        <Space size={4}>
          <strong>{r.underlying}</strong>
          <Tag color="blue">spread</Tag>
          {!r.hasFullPair && <Tag color="orange">1 leg</Tag>}
        </Space>
      }
    >
      <div style={{ display: 'flex', justifyContent: 'space-between' }}>
        <Typography.Text type="secondary">PNL</Typography.Text>
        <Typography.Text strong style={{ color: r.combinedPnl >= 0 ? '#3f8600' : '#cf1322' }}>
          {r.combinedPnl >= 0 ? '+' : ''}{r.combinedPnl.toFixed(2)}
        </Typography.Text>
      </div>
      <div style={{ display: 'flex', justifyContent: 'space-between', marginTop: 2 }}>
        <Typography.Text type="secondary">Exit PNL</Typography.Text>
        {r.combinedExitPnl === null ? (
          <Typography.Text type="secondary">—</Typography.Text>
        ) : (
          <Typography.Text strong style={{ fontSize: 16, color: r.combinedExitPnl >= 0 ? '#3f8600' : '#cf1322' }}>
            {r.combinedExitPnl >= 0 ? '+' : ''}{r.combinedExitPnl.toFixed(2)}
          </Typography.Text>
        )}
      </div>
      <div style={{ display: 'flex', justifyContent: 'space-between', marginTop: 2 }}>
        <Typography.Text type="secondary">Entered at</Typography.Text>
        <Typography.Text>{r.averageEnteredAt === null ? '—' : r.averageEnteredAt.toFixed(2)}</Typography.Text>
      </div>
      <div style={{ display: 'flex', justifyContent: 'space-between', marginTop: 2 }}>
        <Typography.Text type="secondary">Running spread</Typography.Text>
        <Typography.Text strong>{r.exitRunningSpread === null ? '—' : r.exitRunningSpread.toFixed(2)}</Typography.Text>
      </div>

      <Divider style={{ margin: '10px 0' }} />

      {r.children.map((p) => <LegLine key={rowKey(p)} p={p} />)}

      <Space style={{ marginTop: 8 }} wrap>
        {r.hasFullPair && (
          <Button
            size="small"
            danger
            icon={<CloseCircleOutlined />}
            onClick={() => openSquareOff(r.children, `Square off both legs of ${r.underlying}?`)}
          >
            Square off spread
          </Button>
        )}
        <Tooltip title="Build an exit trigger for this spread (fires automatically, deleted if you square off manually)">
          <Button
            size="small"
            icon={<ThunderboltOutlined />}
            onClick={() => navigate('/triggers', { state: { tab: 'exit-trigger', spreadId: r.spreadId } })}
          >
            Add trigger
          </Button>
        </Tooltip>
        <PnlAlertButton r={r} />
      </Space>
    </Card>
  )

  return (
    <Card
      // Just the short count here - Total PNL used to live in this same Space, but antd's Card
      // header title has overflow:hidden/white-space:nowrap by default, so on a narrow/mobile
      // viewport the "extra" buttons on the right squeezed the title down until Total PNL (the
      // second item) got clipped off entirely rather than wrapping - it never actually
      // disappeared, it just ran out of header width to render in. Moved below, as its own
      // block-level line in the card body, where it can't be truncated by anything.
      title={`Open positions (${positions.length})`}
      extra={
        <Space size="small">
          {selectedKeys.length > 0 && (
            <Button
              danger
              size="small"
              icon={<CloseCircleOutlined />}
              onClick={() => openSquareOff(
                positions.filter((p) => selectedKeys.includes(rowKey(p))),
                `Square off ${selectedKeys.length} selected position(s)?`)}
            >
              Square off selected ({selectedKeys.length})
            </Button>
          )}
          <Tooltip title="Refresh now (also auto-refreshes every 60s while this page is open)">
            <Button
              size="small"
              icon={<ReloadOutlined spin={!!refreshing} />}
              onClick={onRefresh}
              aria-label="Refresh positions"
            />
          </Tooltip>
        </Space>
      }
    >
      <Typography.Text strong style={{ display: 'block', marginBottom: 12, fontSize: 15 }}>
        Total PNL:{' '}
        <span style={{ color: totalPnl >= 0 ? '#3f8600' : '#cf1322' }}>
          {totalPnl >= 0 ? '+' : ''}{totalPnl.toFixed(2)}
        </span>
      </Typography.Text>
      {error && <Alert type="error" message={error} closable onClose={() => setError(null)} style={{ marginBottom: 12 }} />}
      {positions.length === 0 ? (
        <Typography.Text type="secondary">No open positions right now.</Typography.Text>
      ) : isMobile ? (
        <Space direction="vertical" style={{ width: '100%' }} size="middle">
          {grouped.map((r) => r.isGroup
            ? <GroupCard key={r.key} r={r} />
            : <Card key={rowKey(r)} size="small"><LegLine p={r} /></Card>)}
        </Space>
      ) : (
        <Table
          rowKey={(r) => r.key ?? rowKey(r)}
          dataSource={grouped}
          columns={columns}
          size="small"
          pagination={false}
          scroll={{ x: 'max-content' }}
          expandable={{
            expandedRowKeys: expandedKeys,
            onExpandedRowsChange: (keys) => setExpandedKeys(keys),
            rowExpandable: (r) => !!r.children?.length,
          }}
          rowSelection={{
            selectedRowKeys: selectedKeys,
            onChange: (keys) => setSelectedKeys(keys),
            // Square-off-selected acts on individual legs - a group row itself has its own
            // "Square off spread" button instead, so it isn't checkable.
            getCheckboxProps: (r) => ({ disabled: !!r.isGroup }),
          }}
        />
      )}

      <Modal
        title={target?.title}
        open={!!target}
        onCancel={() => setTarget(null)}
        onOk={confirmSquareOff}
        confirmLoading={modalBusy}
        okText="Square off"
        okButtonProps={{ danger: true }}
      >
        {target?.legs.map((p) => {
          const key = rowKey(p)
          const max = openLots(p)
          return (
            <div key={key} style={{ marginBottom: 12 }}>
              <Typography.Text strong>{p.tradingSymbol}</Typography.Text>{' '}
              <Typography.Text type="secondary">
                ({p.quantity > 0 ? 'SELL to close' : 'BUY to close'}, {max ?? '—'} lot{max === 1 ? '' : 's'} open)
              </Typography.Text>
              <InputNumber
                min={1}
                max={max ?? undefined}
                addonBefore="Lots"
                value={modalLots[key] === '' ? null : modalLots[key]}
                onChange={(v) => setModalLots((m) => ({ ...m, [key]: v ?? '' }))}
                style={{ width: '100%', marginTop: 4 }}
                disabled={max === null}
              />
            </div>
          )
        })}
        <div>
          <Typography.Text strong>Slippage</Typography.Text>
          <InputNumber
            min={0.05}
            step={0.05}
            addonAfter="%"
            value={modalSlippage === '' ? null : modalSlippage}
            onChange={(v) => setModalSlippage(v ?? '')}
            style={{ width: '100%', marginTop: 4 }}
          />
          <Typography.Text type="secondary" style={{ fontSize: 12, display: 'block', marginTop: 4 }}>
            How far past the current bid/ask each leg's MARKET order may fill at, so the exit goes
            through right away. Applied to every leg above.
          </Typography.Text>
        </div>
      </Modal>

      <Modal
        title={`PNL alert - ${alertTarget?.underlying}`}
        open={!!alertTarget}
        onCancel={() => setAlertTarget(null)}
        onOk={confirmPnlAlert}
        confirmLoading={alertBusy}
        okText="Set alert"
      >
        <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 8 }}>
          Current combined Exit PNL:{' '}
          {alertTarget?.currentExitPnl === null || alertTarget?.currentExitPnl === undefined
            ? 'not available yet'
            : `${alertTarget.currentExitPnl >= 0 ? '+' : ''}${alertTarget.currentExitPnl.toFixed(2)}`}
        </Typography.Text>
        <InputNumber
          placeholder="Target Exit PNL (signed)"
          value={alertValue === '' ? null : alertValue}
          onChange={(v) => setAlertValue(v ?? '')}
          style={{ width: '100%' }}
        />
        <div style={{ marginTop: 10 }}>
          {/* Two plain Buttons instead of antd's Segmented - Segmented's "selected" pill is styled
              off theme tokens (colorBgElevated etc.), which weren't customized for it here and so
              rendered with basically no visible contrast against this app's dark surface, making
              it impossible to tell which direction was actually picked. A solid fill (same
              red/green used for PNL everywhere else in this table) is unambiguous regardless of
              theme and doesn't depend on any token being set correctly. */}
          <Space.Compact block>
            <Button
              block
              type="primary"
              danger={alertDirection === 'fall'}
              ghost={alertDirection !== 'fall'}
              icon={<ArrowDownOutlined />}
              onClick={() => { setAlertDirection('fall'); setAlertDirectionTouched(true) }}
            >
              Falls to
            </Button>
            <Button
              block
              type="primary"
              ghost={alertDirection !== 'rise'}
              icon={<ArrowUpOutlined />}
              style={alertDirection === 'rise' ? { background: '#3f8600', borderColor: '#3f8600' } : { color: '#3f8600', borderColor: '#3f8600' }}
              onClick={() => { setAlertDirection('rise'); setAlertDirectionTouched(true) }}
            >
              Rises to
            </Button>
          </Space.Compact>
          <Typography.Text type="secondary" style={{ fontSize: 12, display: 'block', marginTop: 4 }}>
            {alertDirection === 'fall'
              ? 'Fires once combined Exit PNL drops to your target or below.'
              : 'Fires once combined Exit PNL climbs to your target or above.'}
            {' '}Auto-picked from the current Exit PNL above vs. your target - check it before
            confirming, especially if PNL has been moving fast, since it won't re-check itself
            once you've touched this control.
          </Typography.Text>
        </div>
        <Space style={{ marginTop: 12 }}>
          <Switch checked={alertOneShot} onChange={setAlertOneShot} />
          <Typography.Text>Notify once (auto-cancels after firing)</Typography.Text>
        </Space>
      </Modal>
    </Card>
  )
}
