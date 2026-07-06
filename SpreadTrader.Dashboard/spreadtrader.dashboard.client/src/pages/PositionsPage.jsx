import { useEffect, useState } from 'react'
import { Alert } from 'antd'
import { usePositions, usePositionsError, refreshPositions, joinPositionsView, leavePositionsView } from '../hooks/spreadHub'
import PositionsTable from '../components/PositionsTable'
import { api } from '../api'

// Live positions/PNL - fed by the "positionsUpdate" push (see hooks/spreadHub.js and the
// backend's LivePositionsHostedService), which recomputes and pushes roughly every 750ms while
// any position is open, reacting within about that same window to a fill anywhere on the
// account (this app's own orders, or one placed directly in the Kite app/web - see
// KiteTickerClient.OrderUpdateReceived). No polling loop here any more - the manual refresh
// button just forces an immediate REST re-fetch into the same shared store instead of waiting
// for the next push.
export default function PositionsPage() {
  const positions = usePositions()
  const error = usePositionsError()
  const [refreshing, setRefreshing] = useState(false)

  // Tells the backend this tab is actually looking at Positions & PNL, so
  // LivePositionsHostedService resumes its Kite REST resync + live PNL push for as long as this
  // page stays mounted - see PositionsViewerTracker/SpreadHub.JoinPositions. Without this, the
  // backend would keep polling Kite and pushing SignalR updates for this data even while every
  // browser tab was off looking at some other page entirely.
  useEffect(() => {
    joinPositionsView()
    return () => leavePositionsView()
  }, [])
  // Fetched once here (not by PositionsTable itself) so "Add trigger" can be gated to only the
  // rows that are actually a leg of some configured spread - an exit trigger needs a near/far
  // spread to evaluate a threshold against, so offering the button for a bare position with no
  // matching SpreadDefinition would just lead to the dead-end warning AddPositionExitTriggerPage
  // used to show after the fact. Failing quietly (empty array) just hides the buttons rather than
  // breaking the whole Positions page over it.
  const [spreads, setSpreads] = useState([])

  useEffect(() => {
    api.getSpreads().then(setSpreads).catch(() => setSpreads([]))
  }, [])

  const handleRefresh = async () => {
    setRefreshing(true)
    try {
      await refreshPositions()
    } finally {
      setRefreshing(false)
    }
  }

  return (
    <>
      {error && <Alert type="warning" message={error} style={{ marginBottom: 16 }} showIcon />}
      <PositionsTable positions={positions} spreads={spreads} onChanged={refreshPositions} refreshing={refreshing} onRefresh={handleRefresh} />
    </>
  )
}
