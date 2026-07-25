import { useCallback, useEffect, useState } from 'react'
import { Card, Radio, Button, Typography, Space, Alert, Modal } from 'antd'
import { ApiOutlined } from '@ant-design/icons'
import { api } from '../api'

const PLATFORMS = [
  { value: 'Kite', label: 'Zerodha Kite' },
  { value: 'Dhan', label: 'Dhan' },
]

// Admin-only switch: which broker the whole app is wired to. Not a per-user preference - it
// changes what every trading feature in this process actually does. Takes effect immediately, in
// process - both platforms' services are always running (see BrokerRegistration), so switching is
// just flipping which one is "active" (see BrokerSwitchState), not a restart. Still a deliberate,
// confirmed action rather than a casual toggle: it clears every live tick and spread definition
// currently streaming (trigger rules survive - they're saved to disk and keyed by a broker-neutral
// id), and the tradeable universe needs rebuilding afterwards since Kite and Dhan use different
// instrument ids.
export default function BrokerPlatformPage() {
  const [currentPlatform, setCurrentPlatform] = useState(null)
  const [selected, setSelected] = useState(null)
  const [loading, setLoading] = useState(true)
  const [switching, setSwitching] = useState(false)
  const [error, setError] = useState(null)
  const [notice, setNotice] = useState(null)

  const load = useCallback(() => {
    api.getBrokerPlatform()
      .then((info) => {
        setCurrentPlatform(info.platform)
        setSelected(info.platform)
        setError(null)
      })
      .catch((err) => setError(err.message))
      .finally(() => setLoading(false))
  }, [])

  useEffect(() => {
    load()
  }, [load])

  const confirmSwitch = () => {
    Modal.confirm({
      title: `Switch to ${PLATFORMS.find((p) => p.value === selected)?.label}?`,
      content: (
        <Typography.Text>
          Takes effect immediately - no restart. Every live tick and spread definition currently
          streaming is cleared (trigger rules are not - those are saved to disk). You'll need to
          rebuild the tradeable universe from the Symbols page afterwards, since Kite and Dhan use
          different instrument ids.
        </Typography.Text>
      ),
      okText: 'Switch now',
      okButtonProps: { danger: true },
      onOk: doSwitch,
    })
  }

  const doSwitch = async () => {
    setSwitching(true)
    setError(null)
    setNotice(null)
    try {
      const result = await api.switchBrokerPlatform(selected)
      setNotice(result.message)
      if (result.switched) {
        // The BACKEND switch is already done and doesn't need a restart, but the FRONTEND still
        // does - the spread cache (hooks/spreadHub.js), AppShell's own `spreads`/`brokerInfo`/
        // `authStatus` state, and the SignalR connection's one-time REST seed are all seeded once
        // per browser session and have no way to invalidate themselves on a switch. Without a
        // reload here, the Spreads/Positions pages and the header's broker label would keep
        // showing the OUTGOING platform's data until the tab was refreshed by hand. A brief delay
        // just lets the success message actually render before the reload wipes it.
        setTimeout(() => window.location.reload(), 800)
      } else {
        setCurrentPlatform(result.platform)
        setSelected(result.platform)
        setSwitching(false)
      }
    } catch (err) {
      setError(err.message)
      setSwitching(false)
    }
  }

  return (
    <Card title={<Space><ApiOutlined style={{ color: '#4da3ff' }} /> Broker Platform</Space>}>
      <Space direction="vertical" size="middle" style={{ width: '100%' }}>
        <Typography.Text type="secondary">
          Which broker the whole app trades through. Switching takes effect immediately - both
          platforms' services always run in the background, so there's no restart and no downtime.
        </Typography.Text>

        {error && <Alert type="error" showIcon message={error} closable onClose={() => setError(null)} />}
        {notice && <Alert type="success" showIcon message={notice} closable onClose={() => setNotice(null)} />}

        <Radio.Group
          value={selected}
          onChange={(e) => setSelected(e.target.value)}
          disabled={loading || switching}
          optionType="button"
          options={PLATFORMS.map((p) => ({ label: p.label, value: p.value }))}
        />
        {currentPlatform && (
          <Typography.Text type="secondary">
            Currently running on <strong>{currentPlatform}</strong>.
          </Typography.Text>
        )}
        <Button
          type="primary"
          loading={switching}
          disabled={loading || !selected || selected === currentPlatform}
          onClick={confirmSwitch}
        >
          Switch platform
        </Button>
      </Space>
    </Card>
  )
}
