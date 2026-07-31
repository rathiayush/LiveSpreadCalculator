import { useCallback, useEffect, useMemo, useState } from 'react'
import { BrowserRouter, Routes, Route, Link, useLocation } from 'react-router-dom'
import { ConfigProvider, Layout, Menu, Space, Tag, Button, Typography, Drawer, Grid, message, theme, Tooltip, Modal, Input } from 'antd'
import {
  DashboardOutlined,
  LineChartOutlined,
  CloudSyncOutlined,
  UnorderedListOutlined,
  ThunderboltOutlined,
  RiseOutlined,
  WifiOutlined,
  HistoryOutlined,
  ScissorOutlined,
  SunOutlined,
  MoonOutlined,
  MenuOutlined,
  SettingOutlined,
  LogoutOutlined,
  UserOutlined,
  ApiOutlined,
} from '@ant-design/icons'
import { api } from './api'
import { useAuth, clearSession } from './auth'
import { getCachedBrokerToken, saveBrokerToken, clearCachedBrokerToken } from './brokerAuth'
import { useSpreadHubConnectionState, useBrokerAuthInvalidated, clearBrokerAuthInvalidated, useBrokerAuthRestored, stopConnection, pruneSpreads } from './hooks/spreadHub'
import LoginPage from './pages/LoginPage'
import DashboardPage from './pages/DashboardPage'
import SpreadsPage from './pages/SpreadsPage'
import SymbolsPage from './pages/SymbolsPage'
import ConfiguredSpreadsPage from './pages/ConfiguredSpreadsPage'
import TriggersPage from './pages/TriggersPage'
import TriggerActivityPage from './pages/TriggerActivityPage'
import PositionsPage from './pages/PositionsPage'
import BrokerPlatformPage from './pages/BrokerPlatformPage'
import ProfileModal from './components/ProfileModal'

const { Header, Content, Sider } = Layout

const THEME_STORAGE_KEY = 'spreadtrader-theme-mode'

// Per-mode token overrides layered on top of antd's dark/default algorithm. The algorithm does
// the heavy lifting (text colors, hover states, etc. across every component); these just pin the
// surface colors to the same palette used in index.css's CSS variables so the hand-styled chrome
// (Sidebar/TopBar/cards) and antd's own components agree on what "background" and "border" mean.
const THEME_TOKENS = {
  dark: {
    colorBgBase: '#0b0d12',
    colorBgContainer: '#12141b',
    colorBgElevated: '#171a24',
    colorBorderSecondary: 'rgba(255,255,255,0.07)',
  },
  light: {
    colorBgBase: '#f4f6f9',
    colorBgContainer: '#ffffff',
    colorBgElevated: '#ffffff',
    colorBorderSecondary: 'rgba(15,23,42,0.08)',
  },
}

// `group: 'admin'` items are administrative/setup screens (symbol sync, spread definitions) that
// most users only touch occasionally - they're tucked under the "Admin" submenu in NavMenu below
// instead of sitting as top-level rail items next to the daily-use Triggers/Live Spreads pages.
// This flat list stays the single source of truth for routing/TopBar lookups either way - only
// NavMenu's rendering groups them differently.
const NAV_ITEMS = [
  { key: '/', label: 'Dashboard', icon: <DashboardOutlined />, subtitle: 'Ranked movers across the whole universe' },
  { key: '/spreads', label: 'Live Spreads', icon: <LineChartOutlined />, subtitle: 'Search and drill into any configured spread' },
  { key: '/triggers', label: 'Triggers', icon: <ThunderboltOutlined />, subtitle: 'Rules that fire baskets automatically' },
  { key: '/trigger-activity', label: 'Trigger Activity', icon: <HistoryOutlined />, subtitle: 'Live feed of trigger fires' },
  { key: '/positions', label: 'Positions & PNL', icon: <ScissorOutlined />, subtitle: 'Open positions, live PNL, and manual square-off' },
  { key: '/symbols', label: 'Symbols', icon: <CloudSyncOutlined />, subtitle: 'Sync the F&O symbol list and build the tradeable universe', group: 'admin' },
  { key: '/configured-spreads', label: 'Configured Spreads', icon: <UnorderedListOutlined />, subtitle: 'Every spread definition currently streaming', group: 'admin' },
  { key: '/broker-platform', label: 'Broker Platform', icon: <ApiOutlined />, subtitle: 'Switch which broker the app trades through, instantly - no restart', group: 'admin' },
]

const ADMIN_GROUP_KEY = 'admin-group'

function useActiveNavItem() {
  const location = useLocation()
  return (
    NAV_ITEMS.find((item) => item.key !== '/' && location.pathname.startsWith(item.key)) ??
    NAV_ITEMS[0]
  )
}

// Logo + product name, shared by the desktop rail and the mobile drawer so the two navs stay
// visually identical instead of drifting apart as separate copies.
function BrandLockup() {
  return (
    <div
      style={{
        display: 'flex',
        alignItems: 'center',
        gap: 10,
        padding: '20px 20px 16px',
        borderBottom: '1px solid rgba(255,255,255,0.06)',
        marginBottom: 8,
      }}
    >
      <div
        style={{
          width: 34,
          height: 34,
          borderRadius: 9,
          background: 'linear-gradient(135deg, #63b3ff, #4da3ff 45%, #7c6df0)',
          boxShadow: '0 4px 14px rgba(77,163,255,0.35)',
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
  )
}

// `onNavigate` fires on every menu click - used to close the mobile drawer after picking a page
// (the desktop rail just passes a no-op since there's nothing to close).
function NavMenu({ onNavigate }) {
  const active = useActiveNavItem()
  // Controlled openKeys (rather than defaultOpenKeys) because the active item - and therefore
  // whether "Admin" should be expanded - changes on every navigation, not just on first mount.
  const [openKeys, setOpenKeys] = useState(active.group === 'admin' ? [ADMIN_GROUP_KEY] : [])

  useEffect(() => {
    if (active.group === 'admin') {
      setOpenKeys((keys) => (keys.includes(ADMIN_GROUP_KEY) ? keys : [...keys, ADMIN_GROUP_KEY]))
    }
  }, [active])

  const topLevelItems = NAV_ITEMS.filter((item) => !item.group).map((item) => ({
    key: item.key,
    icon: item.icon,
    label: <Link to={item.key}>{item.label}</Link>,
  }))

  const adminItems = NAV_ITEMS.filter((item) => item.group === 'admin').map((item) => ({
    key: item.key,
    icon: item.icon,
    label: <Link to={item.key}>{item.label}</Link>,
  }))

  return (
    <Menu
      theme="dark"
      mode="inline"
      selectedKeys={[active.key]}
      openKeys={openKeys}
      onOpenChange={setOpenKeys}
      onClick={onNavigate}
      style={{ background: 'transparent', borderInlineEnd: 'none', padding: '4px 10px' }}
      items={[
        ...topLevelItems,
        { key: ADMIN_GROUP_KEY, icon: <SettingOutlined />, label: 'Admin', children: adminItems },
      ]}
    />
  )
}

// Desktop/tablet (lg+ breakpoint, >=992px): a permanent 232px rail down the left side.
function Sidebar() {
  return (
    <Sider
      width={232}
      style={{
        background: 'linear-gradient(180deg, #10121a 0%, #0d0f16 55%, #0b0c12 100%)',
        borderRight: '1px solid rgba(255,255,255,0.07)',
        boxShadow: '4px 0 24px rgba(0,0,0,0.35)',
        position: 'sticky',
        top: 0,
        height: '100vh',
        overflow: 'auto',
        zIndex: 5,
      }}
    >
      <BrandLockup />
      <NavMenu onNavigate={() => {}} />
    </Sider>
  )
}

// Below lg (phones and narrow tablets): the rail would eat 60%+ of a 375px-wide screen, so
// instead nothing is docked and this drawer - opened via the hamburger button in TopBar - covers
// the same brand+menu content on demand. Closes itself as soon as a nav item is picked.
function MobileNavDrawer({ open, onClose }) {
  return (
    <Drawer
      placement="left"
      open={open}
      onClose={onClose}
      closable={false}
      width={264}
      styles={{
        body: { padding: 0 },
        content: { background: 'linear-gradient(180deg, #10121a 0%, #0d0f16 55%, #0b0c12 100%)' },
        header: { display: 'none' },
      }}
    >
      <BrandLockup />
      <NavMenu onNavigate={onClose} />
    </Drawer>
  )
}

function TopBar({ authStatus, brokerName, supportsInteractiveLogin, supportsManualToken, onLogin, onEnterToken, mode, onToggleMode, onMenuClick, showMenuButton, userEmail, onLogout, onProfile }) {
  const active = useActiveNavItem()
  const isLight = mode === 'light'
  // Only this hook re-renders on connection state flips (rare - connect/reconnect/disconnect),
  // not on every spread tick, so it's read directly here rather than threaded down from AppShell.
  const connectionState = useSpreadHubConnectionState()

  // showMenuButton is only true below the lg breakpoint (see AppShell) - reused here as the
  // "compact" signal so the header doesn't need its own separate breakpoint check. On mobile this
  // drops the subtitle line and shrinks the status tags to icon+tooltip instead of icon+sentence,
  // which is what was previously forcing three stacked rows (title+subtitle, tags, login/toggle)
  // and eating a huge chunk of a phone's vertical space before any real content showed.
  const compact = showMenuButton

  return (
    <Header
      style={{
        background: isLight ? 'rgba(255,255,255,0.75)' : 'rgba(15,17,23,0.72)',
        backdropFilter: 'blur(10px) saturate(140%)',
        WebkitBackdropFilter: 'blur(10px) saturate(140%)',
        borderBottom: isLight ? '1px solid rgba(15,23,42,0.08)' : '1px solid rgba(255,255,255,0.07)',
        boxShadow: isLight
          ? '0 1px 0 rgba(255,255,255,0.6), 0 6px 20px rgba(15,23,42,0.06)'
          : '0 1px 0 rgba(255,255,255,0.03), 0 6px 20px rgba(0,0,0,0.25)',
        position: 'sticky',
        top: 0,
        zIndex: 10,
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'space-between',
        gap: compact ? 8 : 16,
        paddingInline: compact ? 10 : 24,
        height: 'auto',
        paddingBlock: compact ? 8 : 14,
        flexWrap: 'wrap',
        rowGap: 6,
      }}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: 10, minWidth: 0 }}>
        {showMenuButton && (
          <Button
            shape="circle"
            size={compact ? 'middle' : 'large'}
            icon={<MenuOutlined />}
            onClick={onMenuClick}
            aria-label="Open navigation"
            style={{ flexShrink: 0 }}
          />
        )}
        <div style={{ minWidth: 0 }}>
          <Typography.Title level={compact ? 5 : 4} style={{ margin: 0 }}>{active.label}</Typography.Title>
          {!compact && (
            <Typography.Text
              type="secondary"
              style={{ fontSize: 12.5, display: 'block', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}
            >
              {active.subtitle}
            </Typography.Text>
          )}
        </div>
      </div>

      <Space wrap size="small">
        {compact ? (
          <Tooltip title={`Live feed: ${connectionState}`}>
            <Tag icon={<WifiOutlined />} color={connectionState === 'connected' ? 'success' : 'warning'} style={{ borderRadius: 999, marginInlineEnd: 0 }} />
          </Tooltip>
        ) : (
          <Tag icon={<WifiOutlined />} color={connectionState === 'connected' ? 'success' : 'warning'} style={{ borderRadius: 999, paddingInline: 10 }}>
            Live feed: {connectionState}
          </Tag>
        )}
        {compact ? (
          <Tooltip title={`${brokerName}: ${authStatus?.isAuthenticated ? 'authenticated' : 'not logged in'}`}>
            <Tag color={authStatus?.isAuthenticated ? 'success' : 'warning'} style={{ borderRadius: 999, marginInlineEnd: 0 }}>
              {brokerName.charAt(0)}
            </Tag>
          </Tooltip>
        ) : (
          <Tag color={authStatus?.isAuthenticated ? 'success' : 'warning'} style={{ borderRadius: 999, paddingInline: 10 }}>
            {brokerName}: {authStatus?.isAuthenticated ? 'authenticated' : 'not logged in'}
          </Tag>
        )}
        {/* Three auth models, so up to three actions. Kite has a mandatory browser login; Dhan on
            a portal-generated token has none, so instead of a button that could only ever fail it
            offers a place to paste a freshly generated token; Kotak has neither - its token is
            minted automatically server-side and no portal ever hands one to a human to paste - so
            no button at all, just the status pill above (already showing "not logged in"). */}
        {authStatus && !authStatus.isAuthenticated && (
          supportsInteractiveLogin ? (
            <Button size="small" type="primary" onClick={onLogin}>Login to {brokerName}</Button>
          ) : supportsManualToken ? (
            <Button size="small" type="primary" onClick={onEnterToken}>Enter {brokerName} token</Button>
          ) : null
        )}
        <Button
          size="small"
          shape="circle"
          icon={isLight ? <MoonOutlined /> : <SunOutlined />}
          onClick={onToggleMode}
          title={isLight ? 'Switch to dark theme' : 'Switch to light theme'}
        />
        <Tooltip title={userEmail ? `${userEmail} - change password` : 'Change password'}>
          <Button
            size="small"
            shape="circle"
            icon={<UserOutlined />}
            onClick={onProfile}
            aria-label="Profile and password"
          />
        </Tooltip>
        {compact ? (
          <Tooltip title={userEmail ? `Signed in as ${userEmail} - log out` : 'Log out'}>
            <Button size="small" shape="circle" icon={<LogoutOutlined />} onClick={onLogout} aria-label="Log out" />
          </Tooltip>
        ) : (
          <Button size="small" icon={<LogoutOutlined />} onClick={onLogout} title={userEmail ? `Signed in as ${userEmail}` : undefined}>
            Log out
          </Button>
        )}
      </Space>
    </Header>
  )
}

function AppShell({ mode, onToggleMode, userEmail, onLogout }) {
  const [spreads, setSpreads] = useState([])
  const [authStatus, setAuthStatus] = useState(null)
  // Which broker this backend is wired to - see GET /api/auth/broker. Seeded with a neutral label
  // so the header never names the wrong platform in the moment before the real value lands.
  const [brokerInfo, setBrokerInfo] = useState({ platform: 'Broker', supportsInteractiveLogin: true, supportsManualToken: true, notAuthenticatedGuidance: '' })
  // Manual access-token entry - only reachable on a platform with no browser login flow, see
  // handleSubmitToken and AuthController.SetBrokerToken.
  const [tokenModalOpen, setTokenModalOpen] = useState(false)
  const [tokenValue, setTokenValue] = useState('')
  const [tokenSubmitting, setTokenSubmitting] = useState(false)
  const [drawerOpen, setDrawerOpen] = useState(false)
  const [profileOpen, setProfileOpen] = useState(false)
  // Note: live spread ticks, ranking, and trigger events are deliberately *not* read here - see
  // hooks/spreadHub.js. Each page/component that needs one of those slices calls its own hook,
  // so AppShell (and unrelated pages) no longer re-render on every ~250-300ms spread tick.

  // antd's own responsive breakpoints (lg = 992px). Below that the permanent 232px rail would
  // take up well over half of a phone's width, so it's swapped for a drawer opened from TopBar.
  const screens = Grid.useBreakpoint()
  const isDesktopNav = screens.lg ?? true

  const loadSpreads = useCallback(() => {
    api.getSpreads()
      .then((list) => {
        setSpreads(list)
        // Whenever we get a fresh definition list (mount, brokerLogin redirect, universe rebuild,
        // or a spread being removed on Configured Spreads), drop any live-tick entries for ids
        // that no longer exist - see pruneSpreads' doc comment in hooks/spreadHub.js.
        pruneSpreads(list.map((s) => s.id))
      })
      .catch(() => {})
  }, [])

  // Which broker is live, loaded once on mount. Falls back to a neutral label rather than naming
  // a platform, so a failed/pending load can never mislabel the UI as the wrong broker.
  const loadBrokerInfo = useCallback(() => {
    api.getBrokerInfo()
      .then((info) => setBrokerInfo({
        platform: info.platform || 'Broker',
        supportsInteractiveLogin: info.supportsInteractiveLogin !== false,
        supportsManualToken: info.supportsManualToken !== false,
        notAuthenticatedGuidance: info.notAuthenticatedGuidance || '',
      }))
      .catch((err) => console.error('Failed to load broker info:', err))
  }, [])

  const loadAuthStatus = useCallback(() => {
    // If this fails (backend not reachable yet, etc.) we still want the Login button to show
    // rather than silently disappearing - so fall back to an explicit "not authenticated" state
    // instead of leaving authStatus null.
    api.getAuthStatus()
      .then(async (status) => {
        if (status.isAuthenticated) {
          setAuthStatus(status)
          return
        }
        // Backend has no session - most likely a fresh process restart, not an actual logout.
        // Offer back whatever broker token this browser cached from an earlier login, as long as
        // it's not past the expiry the backend reported for it - see brokerAuth.js. This is what
        // avoids showing the login button (and prompting a real re-login) purely because the
        // server restarted, while the existing token is still genuinely valid.
        const cachedToken = getCachedBrokerToken()
        if (!cachedToken) {
          setAuthStatus(status)
          return
        }
        try {
          const restored = await api.restoreBrokerToken(cachedToken)
          setAuthStatus(restored)
          // Backend only ever fills an EMPTY session, and verifies the offered token against the
          // broker before accepting it (see AuthController.RestoreBrokerToken), so
          // isAuthenticated:false here means the token was empty/malformed or the broker rejected
          // it outright. Either way there's nothing useful left to retry with - drop it.
          if (!restored.isAuthenticated) clearCachedBrokerToken()
        } catch (err) {
          console.error('Failed to restore cached broker token:', err)
          setAuthStatus(status)
        }
      })
      .catch((err) => {
        console.error('Failed to load broker auth status:', err)
        setAuthStatus({ isAuthenticated: false })
      })
  }, [])

  useEffect(() => {
    loadSpreads()
    loadAuthStatus()
    loadBrokerInfo()

    // The broker redirects back here with ?brokerLogin=success|failed after AuthController/callback.
    const params = new URLSearchParams(window.location.search)
    if (params.get('brokerLogin')) {
      window.history.replaceState({}, '', window.location.pathname)
      loadAuthStatus()
      // A fresh login means any earlier "your token died mid-session" notice no longer
      // applies - clear it so it doesn't keep showing after the problem's already fixed.
      clearBrokerAuthInvalidated()
      // Cache the just-obtained token, with the expiry the backend computed for this platform, so
      // a future backend restart can restore this session instead of forcing another real login -
      // see brokerAuth.js / loadAuthStatus above.
      if (params.get('brokerLogin') === 'success') {
        api.getBrokerToken()
          .then((res) => saveBrokerToken(res.accessToken, res.expiresUtc))
          .catch((err) => console.error('Failed to cache broker token after login:', err))
      }
    }
  }, [loadSpreads, loadAuthStatus, loadBrokerInfo])

  // Pushed live (see hooks/spreadHub.js's "brokerAuthInvalidated" handler) the instant a real
  // broker API call reveals a dead token - independent of, and much faster than, the one-time
  // check above. The backend has already cleared its own session by the time this arrives; this
  // just makes sure the UI doesn't sit there showing a stale "authenticated" tag (and silently
  // failing orders) until the next full page reload.
  const brokerAuthInvalidReason = useBrokerAuthInvalidated()
  useEffect(() => {
    if (!brokerAuthInvalidReason) return
    setAuthStatus({ isAuthenticated: false })
    // The cached token (if any) has now been proven dead by an actual broker API call - drop it so
    // a future restart doesn't try to restore this same dead token again. See brokerAuth.js.
    clearCachedBrokerToken()
    message.warning(`${brokerInfo.platform} session ended (${brokerAuthInvalidReason}) - please log in again.`, 8)
  }, [brokerAuthInvalidReason, brokerInfo.platform])

  // Pushed live (see hooks/spreadHub.js's "brokerAuthRestored" handler) the moment a background
  // auto-login (Dhan TOTP) actually succeeds - the missing counterpart to brokerAuthInvalidReason
  // above. loadAuthStatus only ever runs once, at mount; a tab opened while
  // DhanTotpAutoRenewalHostedService was still mid-retry would otherwise capture
  // isAuthenticated:false once and never learn it later succeeded, even though the backend was
  // trading fine by then.
  const brokerAuthRestoredCount = useBrokerAuthRestored()
  useEffect(() => {
    if (brokerAuthRestoredCount === 0) return
    loadAuthStatus()
  }, [brokerAuthRestoredCount, loadAuthStatus])

  const handleLogin = async () => {
    try {
      const { url } = await api.getLoginUrl()
      if (!url) throw new Error('Backend returned an empty login URL - check the broker credentials in appsettings.json.')
      window.location.href = url
    } catch (err) {
      // Previously this failed silently (unhandled promise rejection) so clicking the button
      // looked like it did nothing. Now it's surfaced both in the console and to the user - the
      // backend returns a 400 with the actual reason (missing api key / partner credentials).
      console.error('Failed to get broker login URL:', err)
      message.error(`Could not start ${brokerInfo.platform} login: ${err.message}`)
    }
  }

  // Installs a hand-pasted access token. The backend verifies it against the broker before
  // accepting, so an expired or mistyped token is rejected here rather than silently breaking the
  // next order - which is exactly why the error is surfaced instead of being swallowed.
  const handleSubmitToken = async () => {
    const token = tokenValue.trim()
    if (!token) return
    setTokenSubmitting(true)
    try {
      const result = await api.setBrokerToken(token)
      setAuthStatus({ isAuthenticated: true })
      // Cache it like any other successfully obtained token, so a later backend restart can
      // restore this session instead of asking for it again - see brokerAuth.js.
      saveBrokerToken(token, result.expiresUtc)
      clearBrokerAuthInvalidated()
      setTokenModalOpen(false)
      setTokenValue('')
      message.success(`${brokerInfo.platform} access token accepted.`)
    } catch (err) {
      message.error(err.message || 'That access token was rejected.')
    } finally {
      setTokenSubmitting(false)
    }
  }

  return (
    <Layout style={{ minHeight: '100vh' }}>
      {isDesktopNav && <Sidebar />}
      <Layout>
        <TopBar
          authStatus={authStatus}
          brokerName={brokerInfo.platform}
          supportsInteractiveLogin={brokerInfo.supportsInteractiveLogin}
          supportsManualToken={brokerInfo.supportsManualToken}
          onLogin={handleLogin}
          onEnterToken={() => setTokenModalOpen(true)}
          mode={mode}
          onToggleMode={onToggleMode}
          showMenuButton={!isDesktopNav}
          onMenuClick={() => setDrawerOpen(true)}
          userEmail={userEmail}
          onLogout={onLogout}
          onProfile={() => setProfileOpen(true)}
        />
        <Content
          className="app-content"
          style={{ maxWidth: 1560, width: '100%', margin: '0 auto' }}
        >
          <Routes>
            <Route
              path="/"
              element={<DashboardPage />}
            />
            <Route
              path="/spreads"
              element={<SpreadsPage spreads={spreads} />}
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
              element={<TriggerActivityPage />}
            />
            <Route
              path="/positions"
              element={<PositionsPage />}
            />
            <Route
              path="/broker-platform"
              element={<BrokerPlatformPage />}
            />
          </Routes>
        </Content>
      </Layout>
      {!isDesktopNav && <MobileNavDrawer open={drawerOpen} onClose={() => setDrawerOpen(false)} />}
      <ProfileModal
        open={profileOpen}
        onClose={() => setProfileOpen(false)}
        email={userEmail}
        onChanged={() => {
          setProfileOpen(false)
          onLogout() // password changed -> old token is dead server-side; drop the session locally
        }}
      />
      {/* Only reachable on a platform without a browser login flow (Dhan on a portal-generated
          token). Without it, an expired token would need a config edit and a restart of a live
          process - see AuthController.SetBrokerToken. */}
      <Modal
        open={tokenModalOpen}
        title={`Enter ${brokerInfo.platform} access token`}
        okText="Save token"
        confirmLoading={tokenSubmitting}
        onOk={handleSubmitToken}
        onCancel={() => { setTokenModalOpen(false); setTokenValue('') }}
        destroyOnClose
      >
        <Typography.Paragraph type="secondary" style={{ marginBottom: 12, whiteSpace: 'pre-line' }}>
          {brokerInfo.notAuthenticatedGuidance ||
            `${brokerInfo.platform} doesn't use a browser login here - generate an access token in the ${brokerInfo.platform} web portal and paste it below. It's verified against ${brokerInfo.platform} before being accepted, and takes effect immediately without a restart.`}
        </Typography.Paragraph>
        <Input.Password
          value={tokenValue}
          onChange={(e) => setTokenValue(e.target.value)}
          onPressEnter={handleSubmitToken}
          placeholder="Access token"
          autoComplete="off"
        />
      </Modal>
    </Layout>
  )
}

function App() {
  const [mode, setMode] = useState(() => {
    // Light is the default for anyone who hasn't picked a theme yet (no THEME_STORAGE_KEY in
    // localStorage) - previously defaulted to dark. Once a user has toggled it either way, their
    // explicit choice (stored below) always wins over this default.
    if (typeof window === 'undefined') return 'light'
    return window.localStorage.getItem(THEME_STORAGE_KEY) === 'dark' ? 'dark' : 'light'
  })

  // Mirrors the mode onto <html data-theme="..."> so index.css can flip the hand-written CSS
  // variables (page background, card fill, table stripes, scrollbar) that live outside antd's
  // token system - ConfigProvider only reaches antd's own components.
  useEffect(() => {
    document.documentElement.setAttribute('data-theme', mode)
    window.localStorage.setItem(THEME_STORAGE_KEY, mode)
  }, [mode])

  const toggleMode = useCallback(() => {
    setMode((m) => (m === 'dark' ? 'light' : 'dark'))
  }, [])

  // App-login gate. Until there's a valid JWT the whole dashboard is replaced by the login screen -
  // there's no point mounting AppShell (and opening the SignalR hub) since every call would 401.
  const { isAuthenticated, email } = useAuth()

  const handleLogout = useCallback(() => {
    stopConnection() // drop the hub authenticated as this user before the token goes away
    clearSession()
  }, [])

  const themeConfig = useMemo(() => ({
    algorithm: mode === 'dark' ? theme.darkAlgorithm : theme.defaultAlgorithm,
    token: {
      colorPrimary: '#4da3ff',
      ...THEME_TOKENS[mode],
      // Transparent so the body's layered gradient (see index.css) shows through the main
      // content area instead of being painted over by a flat Layout background. Sider/Header
      // still set their own opaque/blurred backgrounds inline, so hierarchy is unaffected.
      colorBgLayout: 'transparent',
      borderRadius: 10,
      fontFamily: "system-ui, 'Segoe UI', Roboto, sans-serif",
    },
    components: {
      Card: {
        headerFontSize: 15,
        paddingLG: 20,
      },
      Layout: {
        headerBg: 'transparent',
        bodyBg: 'transparent',
      },
      Table: {
        headerBg: mode === 'dark' ? '#171a22' : '#f4f6f9',
        headerColor: mode === 'dark' ? 'rgba(255,255,255,0.75)' : 'rgba(15,23,42,0.75)',
      },
    },
  }), [mode])

  return (
    <ConfigProvider theme={themeConfig}>
      {isAuthenticated ? (
        <BrowserRouter>
          <AppShell mode={mode} onToggleMode={toggleMode} userEmail={email} onLogout={handleLogout} />
        </BrowserRouter>
      ) : (
        <LoginPage />
      )}
    </ConfigProvider>
  )
}

export default App
