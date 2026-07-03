import { useState } from 'react'
import { Card, Form, Input, Button, Typography, Alert, Space } from 'antd'
import { RiseOutlined, LockOutlined, MailOutlined } from '@ant-design/icons'
import { login } from '../api'
import { setSession } from '../auth'

// The app's front door. Everything behind it - every API call and the live SignalR feed - now
// requires a JWT, so until the user authenticates here App.jsx renders this and nothing else.
// On success we stash the returned token/email/expiry via auth.setSession, which flips App over to
// the real dashboard.
export default function LoginPage() {
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState(null)

  const onFinish = async ({ email, password }) => {
    setSubmitting(true)
    setError(null)
    try {
      const result = await login(email, password) // { token, expiresUtc, email }
      setSession(result)
    } catch (err) {
      setError(err.message || 'Login failed.')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <div
      style={{
        minHeight: '100vh',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        padding: 24,
        background: 'linear-gradient(180deg, #10121a 0%, #0d0f16 55%, #0b0c12 100%)',
      }}
    >
      <Card style={{ width: '100%', maxWidth: 380 }} styles={{ body: { padding: 28 } }}>
        <Space direction="vertical" size={6} style={{ width: '100%', marginBottom: 20, textAlign: 'center' }}>
          <div
            style={{
              width: 44,
              height: 44,
              borderRadius: 11,
              margin: '0 auto',
              background: 'linear-gradient(135deg, #63b3ff, #4da3ff 45%, #7c6df0)',
              boxShadow: '0 4px 14px rgba(77,163,255,0.35)',
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
            }}
          >
            <RiseOutlined style={{ color: '#fff', fontSize: 20 }} />
          </div>
          <Typography.Title level={4} style={{ margin: 0 }}>SpreadTrader</Typography.Title>
          <Typography.Text type="secondary">Sign in to continue</Typography.Text>
        </Space>

        {error && <Alert type="error" showIcon message={error} style={{ marginBottom: 16 }} />}

        <Form layout="vertical" onFinish={onFinish} requiredMark={false} disabled={submitting}>
          <Form.Item
            name="email"
            label="Email"
            rules={[{ required: true, message: 'Enter your email' }]}
          >
            <Input prefix={<MailOutlined />} placeholder="you@example.com" autoComplete="username" autoFocus />
          </Form.Item>
          <Form.Item
            name="password"
            label="Password"
            rules={[{ required: true, message: 'Enter your password' }]}
          >
            <Input.Password prefix={<LockOutlined />} placeholder="Password" autoComplete="current-password" />
          </Form.Item>
          <Form.Item style={{ marginBottom: 0, marginTop: 8 }}>
            <Button type="primary" htmlType="submit" block loading={submitting}>
              Sign in
            </Button>
          </Form.Item>
        </Form>
      </Card>
    </div>
  )
}
