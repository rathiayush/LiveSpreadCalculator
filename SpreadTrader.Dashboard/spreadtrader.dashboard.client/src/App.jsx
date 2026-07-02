import { useCallback, useEffect, useMemo, useState } from 'react'
import { BrowserRouter, Routes, Route, Link, useLocation } from 'react-router-dom'
import { ConfigProvider, Layout, Menu, Space, Tag, Button, Typography, message, theme } from 'antd'
import {
  DashboardOutlined,
  LineChartOutlined,
  CloudSyncOutlined,
  UnorderedListOutlined,
  ThunderboltOutlined,
  RiseOutlined,
  WifiOutlined,
  HistoryOutlined,
} from '@ant-design/icons'
import { api } from './api'
import { useSpreadHub } from './hooks/useSpreadHub'
import DashboardPage from './pages/DashboardPage'
import SpreadsPage from './pages/SpreadsPage'
import SymbolsPage from './pages/SymbolsPage'
import ConfiguredSpreadsPage from './pages/ConfiguredSpreadsPage'
import TriggersPage from './pages/TriggersPage'
import TriggerActivityPage from './pages/TriggerActivityPage'

const { Header, Content, Sider } = Layout

const NAV_ITEMS = [
  { key: '/', label: 'Dashboard', icon: <DashboardOutlined />, subtitle: 'Ranked movers across the whole universe' },
  { key: '/spreads', label: 'Live Spreads', icon: <LineChartOutlined />, subtitle: 'Search and drill into any configured spread' },
  { key: '/symbols', label: 'Symbols', icon: <CloudSyncOutlined />, subtitle: 'Sync the F&O symbol list and build the tradeable universe' },
  { key: '/configured-spreads', label: 'Configured Spreads', icon: <UnorderedListOutlined />, subtitle: 'Every spread definition currently streaming' },
  { key: '/triggers', label: 'Triggers', icon: <ThunderboltOutlined />, subtitle: 'Rules that fire baskets automatically' },
  { key: '/trigger-activity', label: 'Trigger Activity', icon: <HistoryOutlined />, subtitle: 'Live feed of trigger fires' },
]

function useActiveNavItem() {
  const location = useLocation()
  return (
    NAV_ITEMS.find((item) => item.key !== '/' && location.pathname.startsWith(item.key)) ??
    NAV_ITEMS[0]
  )
}

function Sidebar() {
  const active = useActiveNavItem()

  return (
    <Sider
      width={232}
      style={{
        background: '#0d0f14',
        borderRight: '1px solid rgba(255,255,255,0.06)',
        position: 'sticky',
        top: 0,
        height: '100vh',
        overflow: 'auto',
      }}
    >
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 10,
          padding: '20px 20px 16px',
        }}
      >
        <div
          style={{
            width: 32,
            height: 32,
            borderRadius: 8,
            background: 'linear-gradient(135deg, #4da3ff, #2f6fd6)',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            flexShrink: 0,
          }}
        >
          <RiseOutlined style={{ color: '#fff', fontSize: 16 }} />
        </div>
        <div style={{ lineHeight: 1.15 }}>
          <div style={{ color: '#fff', fontWeight: 700, fontSize: 16, letterSpacing: '-0.01em' }}>SpreadTrader</div>
          <div style={{ color: 'rgba(255,255,255,0.4)', fontSize: 11 }}>Calendar spread desk</div>
        </div>
      </div>

      <Menu
        theme="dark"
        mode="inline"
        selectedKeys={[active.key]}
        style={{ background: 'transparent', borderInlineEnd: 'none', padding: '4px 8px' }}
        items={NAV_ITEMS.map((item) => ({
          key: item.key,
          icon: item.icon,
          label: <Link to={item.key}>{item.label}</Link>,
        }))}
      />
    </Sider>
  )
}

function TopBar({ connectionState, authStatus, onLogin }) {
  const active = useActiveNavItem()

  return (
    <Header
      style={{
        background: 'rgba(13,15,20,0.85)',
        backdropFilter: 'blur(6px)',
        borderBottom: '1px solid rgba(255,255,255,0.06)',
        position: 'sticky',
        top: 0,
        zIndex: 10,
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'space-between',
        gap: 16,
        paddingInline: 24,
        height: 'auto',
        paddingBlock: 14,
        flexWrap: 'wrap',
      }}
    >
      <div>
        <Typography.Title level={4} style={{ margin: 0, color: '#f3f4f6' }}>{active.label}</Typography.Title>
        <Typography.Text type="secondary" style={{ fontSize: 12.5 }}>{active.subtitle}</Typography.Text>
      </div>

      <Space wrap size="small">
        <Tag icon={<WifiOutlined />} color={connectionState === 'connected' ? 'success' : 'warning'} style={{ borderRadius: 999, paddingInline: 10 }}>
          Live feed: {connectionState}
        </Tag>
        <Tag color={authStatus?.isAuthenticated ? 'success' : 'warning'} style={{ borderRadius: 999, paddingInline: 10 }}>
          Kite: {authStatus?.isAuthenticated ? 'authenticated' : 'not logged in'}
        </Tag>
        {authStatus && !authStatus.isAuthenticated && (
          <Button size="small" type="primary" onClick={onLogin}>Login to Kite</Button>
        )}
      </Space>
    </Header>
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
      <Sidebar />
      <Layout>
        <TopBar connectionState={connectionState} authStatus={authStatus} onLogin={handleLogin} />
        <Content style={{ padding: 24, maxWidth: 1560, width: '100%', margin: '0 auto' }}>
          <Routes>
            <Route
              path="/"
              element={<DashboardPage ranking={ranking} />}
            />
            <Route
              path="/spreads"
              element={<SpreadsPage spreads={spreads} liveValues={liveValues} />}
            />
            <Route
              path="/symbols"
              element={<SymbolsPage onSpreadsChanged={loadSpreads} />}
            />
            <Route
              path="/configured-spreads"
              element={<ConfiguredSpreadsPage spreads={spreads} onSpreadsChanged={loadSpreads} />}
            />
            <Route
              path="/triggers"
              element={<TriggersPage spreads={spreads} />}
            />
            <Route
              path="/trigger-activity"
              element={<TriggerActivityPage triggerEvents={triggerEvents} />}
            />
          </Routes>
        </Content>
      </Layout>
    </Layout>
  )
}

function App() {
  const themeConfig = useMemo(() => ({
    algorithm: theme.darkAlgorithm,
    token: {
      colorPrimary: '#4da3ff',
      colorBgBase: '#0b0d12',
      colorBgContainer: '#12141b',
      colorBgLayout: '#0b0d12',
      colorBorderSecondary: 'rgba(255,255,255,0.06)',
      borderRadius: 8,
      fontFamily: "system-ui, 'Segoe UI', Roboto, sans-serif",
    },
    components: {
      Card: {
        headerFontSize: 15,
        paddingLG: 20,
      },
      Layout: {
        headerBg: 'transparent',
        bodyBg: '#0b0d12',
      },
      Table: {
        headerBg: '#171a22',
      },
    },
  }), [])

  return (
    <ConfigProvider theme={themeConfig}>
      <BrowserRouter>
        <AppShell />
      </BrowserRouter>
    </ConfigProvider>
  )
}

export default App
