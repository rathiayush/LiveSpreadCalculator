import { useCallback, useEffect, useRef, useState } from 'react'
import { Card, Radio, Button, Typography, Space, Alert, Modal, Spin } from 'antd'
import { ApiOutlined } from '@ant-design/icons'
import { api } from '../api'

const PLATFORMS = [
  { value: 'Kite', label: 'Zerodha Kite' },
  { value: 'Dhan', label: 'Dhan' },
]

// How long to keep polling GET /api/settings/broker-platform for the new value after a switch,
// before giving up and just telling the operator to check back manually. The backend's own restart
// isn't guaranteed to be automatic (see BrokerPlatformController's doc comment) - this is a generous
// window for a normal container/process restart, not a promise that one will happen.
const POLL_TIMEOUT_MS = 90_000
const POLL_INTERVAL_MS = 2_000

// Admin-only switch: which broker the whole app is wired to. Not a per-user preference - it
// changes what every trading feature in this process actually does, and applying it requires a
// full process restart (see BrokerRegistration - exactly one platform's services get wired up,
// once, at startup). Restarting drops every in-memory spread definition and live tick, so this is
// a deliberate, confirmed action, not a casual toggle.
export default function BrokerPlatformPage() {
  const [currentPlatform, setCurrentPlatform] = useState(null)
  const [selected, setSelected] = useState(null)
  const [loading, setLoading] = useState(true)
  const [switching, setSwitching] = useState(false)
  const [error, setError] = useState(null)
  const pollTimer = useRef(null)

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
    return () => clearTimeout(pollTimer.current)
  }, [load])

  // After a switch, the OLD process is about to go down - poll until a process (hopefully the
  // freshly restarted one) answers with the platform we asked for, then reload the whole page so
  // every other piece of state (auth status, spreads, positions) starts clean against it too. A
  // plain async loop (rather than a self-scheduling callback) so there's no function referencing
  // its own not-yet-assigned binding.
  const pollForRestart = useCallback(async (targetPlatform, deadline) => {
    while (Date.now() <= deadline) {
      try {
        const info = await api.getBrokerPlatform()
        if (info.platform === targetPlatform) {
          window.location.reload()
          return
        }
      } catch {
        // Expected mid-restart - the old process is shutting down or the new one isn't answering
        // requests yet. Keep polling silently until the deadline.
      }
      await new Promise((resolve) => { pollTimer.current = setTimeout(resolve, POLL_INTERVAL_MS) })
    }

    setSwitching(false)
    setError(
      `Still waiting on ${targetPlatform} to come up. The process may not restart itself on this ` +
      'deployment - check the server / container logs, and reload this page once it is back.'
    )
  }, [])

  const confirmSwitch = () => {
    Modal.confirm({
      title: `Switch to ${PLATFORMS.find((p) => p.value === selected)?.label}?`,
      content: (
        <Space direction="vertical">
          <Typography.Text>
            This restarts the whole app to wire up the new broker. Every live tick and spread
            definition currently streaming is lost (trigger rules are not - those are saved to
            disk). You'll need to rebuild the tradeable universe from the Symbols page afterwards,
            since Kite and Dhan use different instrument ids.
          </Typography.Text>
          <Typography.Text type="secondary">
            If this deployment doesn't restart itself automatically, the choice is still saved -
            starting the process again (manually, or on the next deploy) will pick it up.
          </Typography.Text>
        </Space>
      ),
      okText: 'Switch and restart',
      okButtonProps: { danger: true },
      onOk: doSwitch,
    })
  }

  const doSwitch = async () => {
    setSwitching(true)
    setError(null)
    try {
      const result = await api.switchBrokerPlatform(selected)
      if (!result.restartTriggered) {
        // Already on that platform - nothing actually changed, so no restart/poll needed.
        setSwitching(false)
        return
      }
      pollForRestart(result.platform, Date.now() + POLL_TIMEOUT_MS)
    } catch (err) {
      setSwitching(false)
      setError(err.message)
    }
  }

  return (
    <Card title={<Space><ApiOutlined style={{ color: '#4da3ff' }} /> Broker Platform</Space>}>
      <Space direction="vertical" size="middle" style={{ width: '100%' }}>
        <Typography.Text type="secondary">
          Which broker the whole app trades through. Changing this restarts the process, so pick
          carefully rather than as a quick experiment.
        </Typography.Text>

        {error && <Alert type="error" showIcon message={error} closable onClose={() => setError(null)} />}

        {switching ? (
          <Space direction="vertical" align="center" style={{ width: '100%', padding: '24px 0' }}>
            <Spin size="large" />
            <Typography.Text type="secondary">
              Restarting - this page will reload automatically once the new platform is up.
            </Typography.Text>
          </Space>
        ) : (
          <>
            <Radio.Group
              value={selected}
              onChange={(e) => setSelected(e.target.value)}
              disabled={loading}
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
              disabled={loading || !selected || selected === currentPlatform}
              onClick={confirmSwitch}
            >
              Switch platform
            </Button>
          </>
        )}
      </Space>
    </Card>
  )
}
