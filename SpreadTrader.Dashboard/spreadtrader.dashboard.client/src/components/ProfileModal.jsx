import { useState } from 'react'
import { Modal, Form, Input, Typography, Alert, message } from 'antd'
import { LockOutlined } from '@ant-design/icons'
import { api } from '../api'

// "Profile > Change password" dialog. On success the backend has already invalidated every token
// this user holds (the security-stamp bump - see the .NET AuthController/Program.cs), so there's no
// staying logged in with the old token: we surface a success notice and then call onChanged, which
// App.jsx wires to the same handleLogout used by the Log out button. The user re-signs in with the
// new password on whichever device(s) they use.
export default function ProfileModal({ open, onClose, email, onChanged }) {
  const [form] = Form.useForm()
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState(null)

  const close = () => {
    if (submitting) return
    form.resetFields()
    setError(null)
    onClose()
  }

  const onFinish = async ({ currentPassword, newPassword }) => {
    setSubmitting(true)
    setError(null)
    try {
      await api.changePassword(currentPassword, newPassword)
      message.success('Password updated. Please sign in again.')
      form.resetFields()
      onChanged() // clears the session -> App.jsx flips back to the login screen
    } catch (err) {
      setError(err.message || 'Could not change password.')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Modal
      title="Change password"
      open={open}
      onCancel={close}
      onOk={() => form.submit()}
      okText="Update password"
      confirmLoading={submitting}
      destroyOnClose
      maskClosable={!submitting}
    >
      <Typography.Paragraph type="secondary" style={{ marginTop: 4 }}>
        Signed in as <strong>{email}</strong>. Changing your password signs you out on every device.
      </Typography.Paragraph>

      {error && <Alert type="error" showIcon message={error} style={{ marginBottom: 16 }} />}

      <Form form={form} layout="vertical" onFinish={onFinish} requiredMark={false} disabled={submitting}>
        <Form.Item
          name="currentPassword"
          label="Current password"
          rules={[{ required: true, message: 'Enter your current password' }]}
        >
          <Input.Password prefix={<LockOutlined />} placeholder="Current password" autoComplete="current-password" />
        </Form.Item>
        <Form.Item
          name="newPassword"
          label="New password"
          rules={[
            { required: true, message: 'Enter a new password' },
            { min: 6, message: 'At least 6 characters' },
          ]}
          hasFeedback
        >
          <Input.Password prefix={<LockOutlined />} placeholder="New password" autoComplete="new-password" />
        </Form.Item>
        <Form.Item
          name="confirmPassword"
          label="Confirm new password"
          dependencies={['newPassword']}
          hasFeedback
          rules={[
            { required: true, message: 'Re-enter the new password' },
            ({ getFieldValue }) => ({
              validator(_, value) {
                if (!value || getFieldValue('newPassword') === value) return Promise.resolve()
                return Promise.reject(new Error('The two passwords do not match'))
              },
            }),
          ]}
        >
          <Input.Password prefix={<LockOutlined />} placeholder="Confirm new password" autoComplete="new-password" />
        </Form.Item>
      </Form>
    </Modal>
  )
}
