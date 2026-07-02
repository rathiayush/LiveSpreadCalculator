import { useCallback, useEffect, useState } from 'react'
import { BrowserRouter, Routes, Route, Link, useLocation } from 'react-router-dom'
import { ConfigProvider, Layout, Menu, Space, Tag, Button, message, theme } from 'antd'
import { api } from './api'
import { useSpreadHub } from './hooks/useSpreadHub'
import DashboardPage from './pages/DashboardPage'
import TriggerConfigPage from './pages/TriggerConfigPage'

const { Header, Content } = Layout

function TopNav() {
  const location = useLocation()
  const selectedKey = location.pathname.startsWith('/triggers') ? '/triggers' : '/'

  return (
    <Menu
      theme="dark"
      mode="horizontal"
      selectedKeys={[selectedKey]}
      style={{ flex: 1, minWidth: 0 }}
      items={[
        { key: '/', label: <Link to="/">Dashboard</Link> },
        { key: '/triggers', label: <Link to="/triggers">Trigger Config</Link> },
      ]}
    />
  )
}

function AppShell() {
  const [spreads, setSpreads] = useState([])
  const [authStatus, setAuthStatus] = useState(null)
  const { spreads: liveValues, ranking, triggerEvents, connectionState } = useSpreadHub()

  const loadSpreads = useCallback(() => {
    api.getSpreads().then(setSpreads).catch(() => {})
  }, [])

  const loadAuthStatus = useCallback(() => {
    // If this fails (backend not reachable yet, etc.) we still want the Login button to show
    // rather than silently disappearing - so fall back to an explicit "not authenticated" state
    // instead of leaving authStatus null.
    api.getAuthStatus()
      .then(setAuthStatus)
      .catch((err) => {
        console.error('Failed to load Kite auth status:', err)
        setAuthStatus({ isAuthenticated: false })
      })
  }, [])

  useEffect(() => {
    loadSpreads()
    loadAuthStatus()

    // Kite redirects back here with ?kiteLogin=success|failed after AuthController/callback.
    const params = new URLSearchParams(window.location.search)
    if (params.get('kiteLogin')) {
      window.history.replaceState({}, '', window.location.pathname)
      loadAuthStatus()
    }
  }, [loadSpreads, loadAuthStatus])

  const handleLogin = async () => {
    try {
      const { url } = await api.getLoginUrl()
      if (!url) throw new Error('Backend returned an empty login URL - is Kite:ApiKey set in appsettings.json?')
      window.location.href = url
    } catch (err) {
      // Previously this failed silently (unhandled promise rejection) so clicking the button
      // looked like it did nothing. Now it's surfaced both in the console and to the user.
      console.error('Failed to get Kite login URL:', err)
      message.error(`Could not start Kite login: ${err.message}`)
    }
  }

  return (
    <Layout style={{ minHeight: '100vh' }}>
      <Header style={{ display: 'flex', alignItems: 'center', gap: 16, paddingInline: 16, flexWrap: 'wrap', height: 'auto', paddingBlock: 8 }}>
        <span style={{ color: '#fff', fontWeight: 700, fontSize: 18, whiteSpace: 'nowrap' }}>SpreadTrader</span>
        <TopNav />
        <Space wrap size="small">
          <Tag color={connectionState === 'connected' ? 'success' : 'warning'}>
            Live feed: {connectionState}
          </Tag>
          <Tag color={authStatus?.isAuthenticated ? 'success' : 'warning'}>
            Kite: {authStatus?.isAuthenticated ? 'authenticated' : 'not logged in'}
          </Tag>
          {authStatus && !authStatus.isAuthenticated && (
            <Button size="small" onClick={handleLogin}>Login to Kite</Button>
          )}
        </Space>
      </Header>

      <Content style={{ padding: '16px', maxWidth: 1400, width: '100%', margin: '0 auto' }}>
        <Routes>
          <Route
            path="/"
            element={<DashboardPage spreads={spreads} liveValues={liveValues} ranking={ranking} triggerEvents={triggerEvents} />}
          />
          <Route
            path="/triggers"
            element={<TriggerConfigPage spreads={spreads} onSpreadsChanged={loadSpreads} />}
          />
        </Routes>
      </Content>
    </Layout>
  )
}

function App() {
  return (
    <ConfigProvider theme={{ algorithm: theme.darkAlgorithm, token: { colorPrimary: '#4da3ff' } }}>
      <BrowserRouter>
        <AppShell />
      </BrowserRouter>
    </ConfigProvider>
  )
}

export default App
