import { useMemo } from 'react'
import { Row, Col } from 'antd'
import { AppstoreOutlined, FireOutlined, PercentageOutlined, SwapOutlined } from '@ant-design/icons'
import StatCard from '../components/StatCard'
import RankingPanel from '../components/RankingPanel'

// At-a-glance landing page: a colored KPI strip plus the full ranking (no pagination - it's the
// "what's moving" view, you want to see everything at once). Recent trigger fires live on their
// own "Trigger Activity" page (see TriggerActivityPage) and the per-spread live grid with
// search/pagination lives on its own page too (see SpreadsPage), since neither fits the same
// "glance at everything" purpose once the universe is 150-200+ symbols.
export default function DashboardPage({ ranking }) {
  const stats = useMemo(() => {
    if (ranking.length === 0) return null
    const top = ranking[0]
    const avgAbsPercent = ranking.reduce((sum, r) => sum + Math.abs(r.spreadPercent), 0) / ranking.length
    const positive = ranking.filter((r) => r.spreadValue >= 0).length
    return { top, avgAbsPercent, positive, negative: ranking.length - positive }
  }, [ranking])

  return (
    <>
      {stats && (
        <Row gutter={[16, 16]} style={{ marginBottom: 16 }}>
          <Col xs={24} sm={12} lg={6}>
            <StatCard
              icon={<AppstoreOutlined />}
              color="#4da3ff"
              title="Spreads tracked"
              value={ranking.length}
            />
          </Col>
          <Col xs={24} sm={12} lg={6}>
            <StatCard
              icon={<FireOutlined />}
              color="#f5a623"
              title="Biggest mover"
              value={stats.top.underlying}
              suffix={`${stats.top.spreadPercent >= 0 ? '+' : ''}${stats.top.spreadPercent.toFixed(2)}%`}
            />
          </Col>
          <Col xs={24} sm={12} lg={6}>
            <StatCard
              icon={<PercentageOutlined />}
              color="#a78bfa"
              title="Avg |spread %|"
              value={`${stats.avgAbsPercent.toFixed(2)}%`}
            />
          </Col>
          <Col xs={24} sm={12} lg={6}>
            <StatCard
              icon={<SwapOutlined />}
              color="#3ddc84"
              title="Premium / Discount"
              value={`${stats.positive} / ${stats.negative}`}
            />
          </Col>
        </Row>
      )}

      <RankingPanel ranking={ranking} />
    </>
  )
}
