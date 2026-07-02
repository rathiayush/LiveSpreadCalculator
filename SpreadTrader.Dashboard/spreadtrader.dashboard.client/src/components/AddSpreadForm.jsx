import { useMemo, useState } from 'react'
import { Card, Row, Col, Input, Select, Button, Alert, Typography, Space } from 'antd'
import { api } from '../api'

const emptyManual = { id: '', underlying: '', nearSymbol: '', nearExchange: 'NFO', farSymbol: '', farExchange: 'NFO' }

function monthAbbrev(expiry) {
  if (!expiry) return ''
  const d = new Date(expiry)
  return d.toLocaleString('en-US', { month: 'short' }).toUpperCase()
}

// `futures` is the list fetched from GET /api/instruments/futures/all (populated after the
// user clicks "Sync symbols from Kite" on this page) - grouped by underlying so near/far
// month can be picked from a dropdown instead of typing exact tradingsymbols.
export default function AddSpreadForm({ futures, onCreated }) {
  const [manualMode, setManualMode] = useState(futures.length === 0)
  const [manual, setManual] = useState(emptyManual)

  const [underlying, setUnderlying] = useState('')
  const [nearToken, setNearToken] = useState('')
  const [farToken, setFarToken] = useState('')
  const [spreadId, setSpreadId] = useState('')

  const [busy, setBusy] = useState(false)
  const [error, setError] = useState(null)

  const byUnderlying = useMemo(() => {
    const map = new Map()
    for (const f of futures) {
      if (!map.has(f.name)) map.set(f.name, [])
      map.get(f.name).push(f)
    }
    for (const list of map.values()) list.sort((a, b) => (a.expiry ?? '').localeCompare(b.expiry ?? ''))
    return map
  }, [futures])

  const underlyings = useMemo(() => [...byUnderlying.keys()].sort(), [byUnderlying])
  const contracts = byUnderlying.get(underlying) ?? []

  const selectUnderlying = (name) => {
    setUnderlying(name)
    const list = byUnderlying.get(name) ?? []
    const near = list[0]
    const far = list[1]
    setNearToken(near ? String(near.instrumentToken) : '')
    setFarToken(far ? String(far.instrumentToken) : '')
    if (near && far) setSpreadId(`${name}-${monthAbbrev(near.expiry)}-${monthAbbrev(far.expiry)}`)
  }

  const updateManual = (field) => (e) => setManual((f) => ({ ...f, [field]: e.target.value }))

  const submit = async (e) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      if (manualMode) {
        if (!manual.id || !manual.underlying || !manual.nearSymbol || !manual.farSymbol) {
          throw new Error('Fill in all fields.')
        }
        await api.createSpread({
          id: manual.id,
          underlying: manual.underlying,
          nearLeg: { tradingSymbol: manual.nearSymbol, exchange: manual.nearExchange },
          farLeg: { tradingSymbol: manual.farSymbol, exchange: manual.farExchange },
        })
        setManual(emptyManual)
      } else {
        const near = contracts.find((c) => String(c.instrumentToken) === nearToken)
        const far = contracts.find((c) => String(c.instrumentToken) === farToken)
        if (!near || !far) throw new Error('Pick both a near and a far contract.')
        if (near.instrumentToken === far.instrumentToken) throw new Error('Near and far must be different contracts.')

        await api.createSpread({
          id: spreadId,
          underlying,
          nearLeg: { tradingSymbol: near.tradingSymbol, exchange: near.exchange },
          farLeg: { tradingSymbol: far.tradingSymbol, exchange: far.exchange },
        })
        setUnderlying('')
        setNearToken('')
        setFarToken('')
        setSpreadId('')
      }
      onCreated()
    } catch (err) {
      setError(err.message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <Card
      title="Add spread"
      style={{ marginBottom: 16 }}
      extra={futures.length > 0 && (
        <Button type="link" size="small" onClick={() => setManualMode((m) => !m)}>
          {manualMode ? 'Pick from synced symbols' : 'Enter symbols manually'}
        </Button>
      )}
    >
      <form onSubmit={submit}>
        <Space direction="vertical" size="middle" style={{ width: '100%' }}>
          {manualMode ? (
            <>
              {futures.length === 0 && (
                <Typography.Text type="secondary">
                  No symbols synced yet - click "Sync symbols from Kite" above, or enter tradingsymbols manually below.
                </Typography.Text>
              )}
              <Row gutter={[12, 12]}>
                <Col xs={24} sm={12}>
                  <Input placeholder="Spread id (e.g. RELIANCE-JUL-AUG)" value={manual.id} onChange={updateManual('id')} />
                </Col>
                <Col xs={24} sm={12}>
                  <Input placeholder="Underlying (e.g. RELIANCE)" value={manual.underlying} onChange={updateManual('underlying')} />
                </Col>
              </Row>
              <Row gutter={[12, 12]}>
                <Col xs={24} sm={16}>
                  <Input placeholder="Near tradingsymbol (e.g. RELIANCE26JULFUT)" value={manual.nearSymbol} onChange={updateManual('nearSymbol')} />
                </Col>
                <Col xs={24} sm={8}>
                  <Input placeholder="Exchange" value={manual.nearExchange} onChange={updateManual('nearExchange')} />
                </Col>
              </Row>
              <Row gutter={[12, 12]}>
                <Col xs={24} sm={16}>
                  <Input placeholder="Far tradingsymbol (e.g. RELIANCE26AUGFUT)" value={manual.farSymbol} onChange={updateManual('farSymbol')} />
                </Col>
                <Col xs={24} sm={8}>
                  <Input placeholder="Exchange" value={manual.farExchange} onChange={updateManual('farExchange')} />
                </Col>
              </Row>
            </>
          ) : (
            <>
              <Select
                placeholder={`Select underlying (${underlyings.length} available)`}
                value={underlying || undefined}
                onChange={selectUnderlying}
                showSearch
                style={{ width: '100%' }}
                options={underlyings.map((u) => ({ value: u, label: u }))}
              />
              {underlying && (
                <>
                  <Row gutter={[12, 12]}>
                    <Col xs={24} sm={12}>
                      <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 4 }}>Near leg</Typography.Text>
                      <Select
                        value={nearToken || undefined}
                        onChange={setNearToken}
                        style={{ width: '100%' }}
                        options={contracts.map((c) => ({ value: String(c.instrumentToken), label: c.tradingSymbol }))}
                      />
                    </Col>
                    <Col xs={24} sm={12}>
                      <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 4 }}>Far leg</Typography.Text>
                      <Select
                        value={farToken || undefined}
                        onChange={setFarToken}
                        style={{ width: '100%' }}
                        options={contracts.map((c) => ({ value: String(c.instrumentToken), label: c.tradingSymbol }))}
                      />
                    </Col>
                  </Row>
                  <Input placeholder="Spread id" value={spreadId} onChange={(e) => setSpreadId(e.target.value)} />
                </>
              )}
            </>
          )}

          {error && <Alert type="error" message={error} showIcon />}
          <Button type="primary" htmlType="submit" loading={busy}>Add spread</Button>
        </Space>
      </form>
    </Card>
  )
}
