import { useState } from 'react'
import { Card, Row, Col, InputNumber, Select, Switch, Button, Alert, Typography, Space } from 'antd'
import { api } from '../api'

const emptyForm = {
  thresholdPercent: 0.5,
  lotsPerTrade: 1,
  product: 'NRML',
  orderType: 'MARKET',
  oneShot: true,
  rearmBufferPercent: 0.1,
}

// Configures one trigger for however many spreads are currently defined (e.g. the whole
// top-150 universe after a build) instead of adding 150+ rules by hand. Creates two rules per
// spread - fires when the spread % deviates thresholdPercent above OR below zero - both with
// auto-direction, so the calendar-spread carry rule (buy current/sell next when negative, sell
// current/buy next when positive) applies uniformly. See BulkTriggerTemplateService.
export default function BulkTriggerForm({ spreadCount, onCreated }) {
  const [form, setForm] = useState(emptyForm)
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState(null)
  const [error, setError] = useState(null)

  const set = (field) => (value) => setForm((f) => ({ ...f, [field]: value }))

  const submit = async (e) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    setResult(null)
    try {
      const res = await api.applyBulkTemplate({
        thresholdPercent: Number(form.thresholdPercent),
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
    <Card title="Bulk trigger template" style={{ marginBottom: 16 }}>
      <form onSubmit={submit}>
        <Space direction="vertical" size="middle" style={{ width: '100%' }}>
          <Typography.Text type="secondary">
            Applies to all {spreadCount} currently configured spread{spreadCount === 1 ? '' : 's'} -
            two auto-direction rules each (one per side of the deviation). Quantity is lots × each
            stock's own exchange lot size, not a flat number.
          </Typography.Text>

          <Row gutter={[12, 12]}>
            <Col xs={24} sm={12}>
              <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 4 }}>Threshold (spread % deviation)</Typography.Text>
              <InputNumber min={0.01} step={0.05} value={form.thresholdPercent} onChange={set('thresholdPercent')} style={{ width: '100%' }} />
            </Col>
            <Col xs={24} sm={12}>
              <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 4 }}>Lots per trade</Typography.Text>
              <InputNumber min={1} value={form.lotsPerTrade} onChange={set('lotsPerTrade')} style={{ width: '100%' }} />
            </Col>
          </Row>

          <Row gutter={[12, 12]}>
            <Col xs={24} sm={12}>
              <Select
                value={form.product}
                onChange={set('product')}
                style={{ width: '100%' }}
                options={[{ value: 'NRML', label: 'NRML' }, { value: 'MIS', label: 'MIS' }]}
              />
            </Col>
            <Col xs={24} sm={12}>
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
          <Button type="primary" htmlType="submit" loading={busy} disabled={spreadCount === 0}>
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
