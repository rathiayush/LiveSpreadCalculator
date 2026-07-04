import { useState } from 'react'
import { Card, Table, Button, Popconfirm, Tag, Typography, Space, Modal, InputNumber, Switch, Radio, Alert } from 'antd'
import {
  ThunderboltOutlined,
  CheckCircleOutlined,
  ClockCircleOutlined,
  StopOutlined,
  PoweroffOutlined,
  DeleteOutlined,
  AimOutlined,
  EditOutlined,
} from '@ant-design/icons'
import { api } from '../api'

const emptyEdit = { lots: '', threshold: '', thresholdIsPercent: false, operator: 'gte' }

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
      // Threshold is always shown as a plain positive magnitude now - the operator picker below
      // carries the direction instead of the user having to read/preserve a sign (matches Add
      // trigger's UX). AbsoluteGreaterThanOrEqual rules (watch-both-sides) already stored the
      // threshold unsigned, so Math.abs is a no-op for those.
      threshold: Math.abs(rule.threshold),
      thresholdIsPercent: rule.thresholdIsPercent,
      operator: rule.operator === 'LessThanOrEqual' ? 'lte' : 'gte',
    })
    setEditError(null)
  }

  const closeEdit = () => {
    if (editBusy) return
    setEditing(null)
  }

  const saveEdit = async () => {
    if (!editing) return
    setEditBusy(true)
    setEditError(null)
    try {
      const magnitude = Math.abs(Number(editForm.threshold))
      // AbsoluteGreaterThanOrEqual (watch-both-sides) rules ignore sign entirely - Update can't
      // switch a rule out of that mode, so just send the magnitude. Otherwise the operator
      // picker below supplies the sign that TriggersController.Update re-derives Operator from.
      const signedThreshold = editing.operator === 'AbsoluteGreaterThanOrEqual'
        ? magnitude
        : (editForm.operator === 'lte' ? -magnitude : magnitude)
      await api.updateTrigger(editing.id, {
        lots: Number(editForm.lots),
        threshold: signedThreshold,
        thresholdIsPercent: editForm.thresholdIsPercent,
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
    { title: 'Id', dataIndex: 'id', key: 'id', fixed: 'left' },
    { title: 'Spread', dataIndex: 'spreadId', key: 'spreadId' },
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
      render: (_, t) => t.autoDirectionFromSpreadSign ? (
        <Tag icon={<AimOutlined />} color="blue">auto (by spread sign)</Tag>
      ) : (
        <>
          {t.nearLegAction !== 'None' && `near ${t.nearLegAction}`}
          {t.nearLegAction !== 'None' && t.farLegAction !== 'None' && ', '}
          {t.farLegAction !== 'None' && `far ${t.farLegAction}`}
        </>
      ),
    },
    { title: 'Qty', dataIndex: 'quantity', key: 'quantity' },
    {
      title: 'Status',
      key: 'status',
      render: (_, t) => {
        if (!t.enabled) return <Tag icon={<StopOutlined />}>disabled</Tag>
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
                Threshold {editForm.thresholdIsPercent ? "(% of the near leg's price)" : '(rupee spread amount)'}
              </Typography.Text>
              <InputNumber
                step={0.05}
                min={0.01}
                value={editForm.threshold === '' ? null : editForm.threshold}
                onChange={(v) => setEditForm((f) => ({ ...f, threshold: v ?? '' }))}
                style={{ width: '100%' }}
              />
              <Typography.Text type="secondary" style={{ fontSize: 12, display: 'block', marginTop: 4 }}>
                {editing?.operator === 'AbsoluteGreaterThanOrEqual'
                  ? "This rule watches both sides (direction decided automatically, live) - enter a plain positive number, sign is ignored either way."
                  : "Enter a plain positive number - pick the direction below instead of a sign to remember."}
              </Typography.Text>
            </div>

            {editing?.operator !== 'AbsoluteGreaterThanOrEqual' && (
              <div>
                <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 4 }}>
                  Fire when the spread...
                </Typography.Text>
                <Radio.Group
                  block
                  optionType="button"
                  buttonStyle="solid"
                  value={editForm.operator}
                  onChange={(e) => setEditForm((f) => ({ ...f, operator: e.target.value }))}
                  options={[
                    { label: 'Rises to/above (>=)', value: 'gte' },
                    { label: 'Falls to/below (<=)', value: 'lte' },
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

            {editError && <Alert type="error" message={editError} showIcon />}
          </Space>
        )}
      </Modal>
    </>
  )
}
