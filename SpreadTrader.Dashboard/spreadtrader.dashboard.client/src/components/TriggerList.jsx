import { useState } from 'react'
import { Card, Table, Button, Popconfirm, Tag, Typography, Space, Modal, InputNumber, Switch, Radio, Alert, Tooltip } from 'antd'
import {
  ThunderboltOutlined,
  CheckCircleOutlined,
  ClockCircleOutlined,
  StopOutlined,
  PoweroffOutlined,
  DeleteOutlined,
  EditOutlined,
} from '@ant-design/icons'
import { api } from '../api'

const emptyEdit = { lots: '', threshold: '', thresholdIsPercent: false, operator: 'gte', slippagePercent: 0.10, oneShot: true, maxFireCount: 3 }

export default function TriggerList({ triggers, onChanged, spreads }) {
  // The rule currently open in the edit modal, or null - available regardless of enabled/armed/
  // fired status, since a rule that already fired (one-shot, now disabled) is just as valid to
  // retune for next time as one that's still armed.
  const [editing, setEditing] = useState(null)
  const [editForm, setEditForm] = useState(emptyEdit)
  const [editBusy, setEditBusy] = useState(false)
  const [editError, setEditError] = useState(null)

  // Controlled pagination (see the note in SpreadGrid) so the page stays put across the frequent
  // re-renders when triggers fire and their status updates.
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(10)

  // Both legs of a calendar spread share the same underlying, so near/far always have the same
  // lot size once resolved - see TriggerForm's identical helper for why either leg works.
  const getLotSize = (spreadId) => {
    const spread = spreads?.find((s) => s.id === spreadId)
    return spread?.nearLeg?.lotSize || spread?.farLeg?.lotSize || 0
  }

  const toggle = async (rule) => {
    if (rule.enabled) await api.disableTrigger(rule.id)
    else await api.enableTrigger(rule.id)
    onChanged()
  }

  const remove = async (id) => {
    await api.deleteTrigger(id)
    onChanged()
  }

  const removeAll = async () => {
    await api.deleteAllTriggers()
    onChanged()
  }

  const openEdit = (rule) => {
    setEditing(rule)
    const lotSize = getLotSize(rule.spreadId)
    setEditForm({
      // rule.quantity is always the real, already-converted share/contract quantity (see
      // TriggersController.Create/Update) - back it out to lots for the input using the
      // spread's resolved lot size, so the edit modal matches what "Add trigger" collects.
      lots: lotSize > 0 ? Math.round(rule.quantity / lotSize) : '',
      // Every rule (ordinary or exit) now stores Threshold as a raw signed absolute spread
      // target, not a deviation magnitude - see TriggerForm.jsx/AddPositionExitTriggerPage.jsx,
      // both of which send it exactly as typed. Show it as-is (no Math.abs): the >=/<= operator
      // below can legitimately pair with EITHER sign (e.g. GreaterThanOrEqual with a negative
      // target, an exit rule waiting for the spread to RISE to -7 from -15) - forcing it through
      // a magnitude+sign-flip here would silently corrupt that on save. Only
      // AbsoluteGreaterThanOrEqual (watch-both-sides) rules genuinely store an unsigned
      // magnitude, so Math.abs is still correct (and a no-op) for those.
      threshold: rule.operator === 'AbsoluteGreaterThanOrEqual' ? Math.abs(rule.threshold) : rule.threshold,
      thresholdIsPercent: rule.thresholdIsPercent,
      operator: rule.operator === 'LessThanOrEqual' ? 'lte' : 'gte',
      slippagePercent: rule.slippagePercent,
      oneShot: rule.oneShot,
      // Existing rules created before this field existed have maxFireCount 0 (unlimited/unused) -
      // show 3 as a sensible starting point rather than 0, same default as Add trigger, in case
      // they flip oneShot off here and want a shot count already filled in.
      maxFireCount: rule.maxFireCount > 0 ? rule.maxFireCount : 3,
    })
    setEditError(null)
  }

  const closeEdit = () => {
    if (editBusy) return
    setEditing(null)
  }

  const saveEdit = async () => {
    if (!editing) return
    if (editForm.slippagePercent === '' || Number(editForm.slippagePercent) <= 0) {
      setEditError('Enter a slippage % greater than 0.')
      return
    }
    setEditBusy(true)
    setEditError(null)
    try {
      let threshold
      let fireWhenGreaterOrEqual
      if (editing.operator === 'AbsoluteGreaterThanOrEqual') {
        // Watch-both-sides ignores sign entirely - Update can't switch a rule out of that mode,
        // so just send the magnitude; fireWhenGreaterOrEqual is meaningless here and left unset.
        threshold = Math.abs(Number(editForm.threshold))
      } else {
        // Raw signed target, direction sent explicitly - same convention Create uses now for
        // every rule (see TriggerForm.jsx), so a negative target the spread needs to RISE to
        // (e.g. -8 with >=) round-trips correctly instead of being forced through a magnitude+
        // sign-flip that can't represent every valid sign/operator combination.
        threshold = Number(editForm.threshold)
        fireWhenGreaterOrEqual = editForm.operator === 'gte'
      }
      await api.updateTrigger(editing.id, {
        lots: Number(editForm.lots),
        threshold,
        fireWhenGreaterOrEqual,
        thresholdIsPercent: editForm.thresholdIsPercent,
        slippagePercent: Number(editForm.slippagePercent),
        oneShot: editForm.oneShot,
        maxFireCount: editForm.oneShot ? 0 : Number(editForm.maxFireCount || 0),
      })
      setEditing(null)
      onChanged()
    } catch (err) {
      setEditError(err.message)
    } finally {
      setEditBusy(false)
    }
  }

  const columns = [
    {
      // The id is an internal UUID the user never types or reasons about, so show only a short
      // monospace stub (full value on hover) - the full 36-char UUID was overrunning its column
      // and visually colliding with the Spread column. Copyable so it can still be grabbed if needed.
      title: 'Id',
      dataIndex: 'id',
      key: 'id',
      width: 120,
      render: (v) => (
        <Tooltip title={v}>
          <Typography.Text copyable={{ text: v }} style={{ fontFamily: 'monospace', fontSize: 12 }}>
            {String(v).slice(0, 8)}…
          </Typography.Text>
        </Tooltip>
      ),
    },
    { title: 'Spread', dataIndex: 'spreadId', key: 'spreadId', ellipsis: { showTitle: true } },
    {
      title: 'Condition',
      key: 'condition',
      render: (_, t) => {
        const unit = t.thresholdIsPercent ? '%' : ''
        if (t.operator === 'AbsoluteGreaterThanOrEqual') return `|spread| >= ${t.threshold}${unit}`
        return `${t.operator === 'GreaterThanOrEqual' ? '>=' : '<='} ${t.threshold}${unit}`
      },
    },
    {
      title: 'Legs',
      key: 'legs',
      render: (_, t) => (
        <>
          {t.nearLegAction !== 'None' && `near ${t.nearLegAction}`}
          {t.nearLegAction !== 'None' && t.farLegAction !== 'None' && ', '}
          {t.farLegAction !== 'None' && `far ${t.farLegAction}`}
        </>
      ),
    },
    { title: 'Qty', dataIndex: 'quantity', key: 'quantity' },
    {
      title: 'Slippage',
      dataIndex: 'slippagePercent',
      key: 'slippagePercent',
      render: (v) => `${Number(v ?? 0).toFixed(2)}%`,
    },
    {
      title: 'Status',
      key: 'status',
      render: (_, t) => {
        if (!t.enabled) return <Tag icon={<StopOutlined />}>disabled</Tag>
        // Bounded multi-shot mode (see TriggerRule.MaxFireCount) doesn't use IsArmed/re-arm at
        // all, so "armed"/"fired" wouldn't reflect anything real for it - show shot progress
        // instead.
        if (!t.oneShot && t.maxFireCount > 0) {
          return (
            <Tag icon={<ThunderboltOutlined />} color="processing">
              {t.fireCount ?? 0}/{t.maxFireCount} fired
            </Tag>
          )
        }
        return t.isArmed
          ? <Tag icon={<CheckCircleOutlined />} color="success">armed</Tag>
          : <Tag icon={<ClockCircleOutlined />} color="warning">fired</Tag>
      },
    },
    {
      title: '',
      key: 'actions',
      render: (_, t) => (
        <Space size="small">
          <Button type="link" size="small" icon={<EditOutlined />} onClick={() => openEdit(t)}>
            Edit
          </Button>
          <Button type="link" size="small" icon={<PoweroffOutlined />} onClick={() => toggle(t)}>
            {t.enabled ? 'Disable' : 'Enable'}
          </Button>
          <Popconfirm title={`Remove trigger "${t.id}"?`} onConfirm={() => remove(t.id)} okText="Remove" okButtonProps={{ danger: true }}>
            <Button type="link" danger size="small" icon={<DeleteOutlined />}>Remove</Button>
          </Popconfirm>
        </Space>
      ),
    },
  ]

  return (
    <>
      <Card
        title={<Space><ThunderboltOutlined style={{ color: '#f5a623' }} /> Configured triggers ({triggers.length})</Space>}
        extra={triggers.length > 0 && (
          <Popconfirm
            title={`Delete all ${triggers.length} triggers?`}
            description="This removes every trigger rule, not just the ones shown on this page. Cannot be undone."
            onConfirm={removeAll}
            okText="Delete all"
            okButtonProps={{ danger: true }}
          >
            <Button danger size="small" icon={<DeleteOutlined />}>Delete all</Button>
          </Popconfirm>
        )}
        style={{ marginBottom: 16 }}
      >
        {triggers.length === 0 ? (
          <Typography.Text type="secondary">
            None yet - add one below. Saved to a CSV file on the server, so these survive a restart.
          </Typography.Text>
        ) : (
          <Table
            dataSource={triggers.map((t) => ({ key: t.id, ...t }))}
            columns={columns}
            size="small"
            pagination={{
              current: page,
              pageSize,
              showSizeChanger: true,
              pageSizeOptions: [10, 20, 50, 100],
              position: ['bottomCenter'],
              showTotal: (total) => `${total} trigger${total === 1 ? '' : 's'}`,
              onChange: (nextPage, nextPageSize) => {
                setPage(nextPage)
                setPageSize(nextPageSize)
              },
            }}
            scroll={{ x: 'max-content' }}
          />
        )}
      </Card>

      <Modal
        title={editing ? `Edit trigger "${editing.id}"` : 'Edit trigger'}
        open={!!editing}
        onCancel={closeEdit}
        onOk={saveEdit}
        confirmLoading={editBusy}
        okText="Save"
        destroyOnHidden
      >
        {editing && (
          <Space direction="vertical" size="middle" style={{ width: '100%' }}>
            <Typography.Text type="secondary">
              Works whether this rule is armed, already fired, or disabled - a fired one-shot
              trigger just re-arms with the new numbers next time you enable it.
            </Typography.Text>

            <div>
              <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 4 }}>Lots</Typography.Text>
              <InputNumber
                min={1}
                addonBefore="Lots"
                value={editForm.lots === '' ? null : editForm.lots}
                onChange={(v) => setEditForm((f) => ({ ...f, lots: v ?? '' }))}
                style={{ width: '100%' }}
              />
              <Typography.Text type="secondary" style={{ fontSize: 12, display: 'block', marginTop: 4 }}>
                {(() => {
                  const lotSize = editing ? getLotSize(editing.spreadId) : 0
                  return lotSize > 0
                    ? `Lot size ${lotSize}${editForm.lots ? ` — ${Number(editForm.lots) * lotSize} shares/contracts per leg` : ''}`
                    : 'Lot size not resolved yet for this spread'
                })()}
              </Typography.Text>
            </div>

            <div>
              <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 4 }}>
                {editing?.operator === 'AbsoluteGreaterThanOrEqual'
                  ? `Deviation magnitude ${editForm.thresholdIsPercent ? '(%)' : '(rupee spread amount)'}`
                  : `Target spread ${editForm.thresholdIsPercent ? '% (signed)' : 'value (signed)'}`}
              </Typography.Text>
              <InputNumber
                step={0.05}
                min={editing?.operator === 'AbsoluteGreaterThanOrEqual' ? 0.01 : undefined}
                value={editForm.threshold === '' ? null : editForm.threshold}
                onChange={(v) => setEditForm((f) => ({ ...f, threshold: v ?? '' }))}
                style={{ width: '100%' }}
              />
              <Typography.Text type="secondary" style={{ fontSize: 12, display: 'block', marginTop: 4 }}>
                {editing?.operator === 'AbsoluteGreaterThanOrEqual'
                  ? "This rule watches both sides (direction decided automatically, live) - enter a plain positive number, sign is ignored either way."
                  : 'Enter the actual signed spread level (e.g. -8), not a magnitude - pick the comparison below.'}
              </Typography.Text>
            </div>

            {editing?.operator !== 'AbsoluteGreaterThanOrEqual' && (
              <div>
                <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 4 }}>
                  Fire when the spread is...
                </Typography.Text>
                <Radio.Group
                  block
                  optionType="button"
                  buttonStyle="solid"
                  value={editForm.operator}
                  onChange={(e) => setEditForm((f) => ({ ...f, operator: e.target.value }))}
                  options={[
                    { label: `>= ${editForm.threshold === '' ? 'this' : editForm.threshold}${editForm.thresholdIsPercent ? '%' : ''}`, value: 'gte' },
                    { label: `<= ${editForm.threshold === '' ? 'this' : editForm.threshold}${editForm.thresholdIsPercent ? '%' : ''}`, value: 'lte' },
                  ]}
                />
              </div>
            )}

            <Space>
              <Switch
                checked={editForm.thresholdIsPercent}
                onChange={(v) => setEditForm((f) => ({ ...f, thresholdIsPercent: v }))}
              />
              <Typography.Text>Threshold is a % of the near leg's price (instead of a rupee amount)</Typography.Text>
            </Space>

            <Space>
              <Switch
                checked={editForm.oneShot}
                onChange={(v) => setEditForm((f) => ({ ...f, oneShot: v }))}
              />
              <Typography.Text>One-shot (disable after firing)</Typography.Text>
            </Space>

            {!editForm.oneShot && (
              <div>
                <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 4 }}>
                  Number of shots
                </Typography.Text>
                <InputNumber
                  min={1}
                  max={15}
                  step={1}
                  addonBefore="Shots"
                  value={editForm.maxFireCount === '' ? null : editForm.maxFireCount}
                  onChange={(v) => setEditForm((f) => ({ ...f, maxFireCount: v ?? '' }))}
                  style={{ width: '100%' }}
                />
                <Typography.Text type="secondary" style={{ fontSize: 12, display: 'block', marginTop: 4 }}>
                  Fires this many times total, one right after another resolves, for as long as the condition keeps being true. Raising this on a rule that already used up its shots just adds more - it doesn't restart the count.
                </Typography.Text>
              </div>
            )}

            <div>
              <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 4 }}>Slippage</Typography.Text>
              <InputNumber
                min={0.05}
                step={0.05}
                addonAfter="%"
                placeholder="Slippage"
                value={editForm.slippagePercent === '' ? null : editForm.slippagePercent}
                onChange={(v) => setEditForm((f) => ({ ...f, slippagePercent: v ?? '' }))}
                style={{ width: '100%' }}
              />
              <Typography.Text type="secondary" style={{ fontSize: 12, display: 'block', marginTop: 4 }}>
                How far past the current price both legs are allowed to fill at when this rule fires. Steps in 0.05% increments - type any value.
              </Typography.Text>
            </div>

            {editError && <Alert type="error" message={editError} showIcon />}
          </Space>
        )}
      </Modal>
    </>
  )
}
