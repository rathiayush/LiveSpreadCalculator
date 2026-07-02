import SpreadGrid from '../components/SpreadGrid'
import RankingPanel from '../components/RankingPanel'
import TriggerFeed from '../components/TriggerFeed'

// Read-only live view - configuration happens on the Trigger Config page.
export default function DashboardPage({ spreads, liveValues, ranking, triggerEvents }) {
  return (
    <>
      <RankingPanel ranking={ranking} />
      <SpreadGrid definitions={spreads} liveValues={liveValues} />
      <TriggerFeed events={triggerEvents} />
    </>
  )
}
