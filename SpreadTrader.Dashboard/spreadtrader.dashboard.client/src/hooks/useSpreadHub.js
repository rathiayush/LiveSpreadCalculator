import { useEffect, useRef, useState } from 'react'
import * as signalR from '@microsoft/signalr'
import { api } from '../api'

// Connects to SpreadHub and keeps three pieces of live state:
//  - spreads: spreadId -> latest SpreadSnapshot (each incoming "spreadUpdate" message is
//    already a batch of everything that changed in one recalculation cycle - see
//    Engine:SpreadRecalculationIntervalMs / SpreadCalculationHostedService - so there is no
//    need to throttle again on the client).
//  - ranking: the full spread list sorted by |spread %| descending, refreshed roughly every
//    Engine:RankingIntervalMinutes (see SpreadRankingHostedService) via "rankingUpdate".
//  - triggerEvents: a rolling feed of the most recent fired triggers.
// Both spreads and ranking are seeded once from REST on mount so the dashboard isn't empty
// while waiting for the first push.
export function useSpreadHub() {
  const [spreads, setSpreads] = useState({})
  const [ranking, setRanking] = useState([])
  const [triggerEvents, setTriggerEvents] = useState([])
  const [connectionState, setConnectionState] = useState('connecting')
  const connectionRef = useRef(null)

  useEffect(() => {
    let disposed = false

    api.getSpreadSnapshot()
      .then((initial) => {
        if (disposed || !initial) return
        setSpreads((prev) => {
          const next = { ...prev }
          for (const snapshot of initial) next[snapshot.spreadId] = snapshot
          return next
        })
      })
      .catch(() => {})

    api.getSpreadRanking()
      .then((initial) => !disposed && initial && setRanking(initial))
      .catch(() => {})

    const connection = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/spreads')
      .withAutomaticReconnect()
      .build()

    connection.on('spreadUpdate', (changed) => {
      setSpreads((prev) => {
        const next = { ...prev }
        for (const snapshot of changed) {
          next[snapshot.spreadId] = snapshot
        }
        return next
      })
    })

    connection.on('rankingUpdate', (rankedList) => {
      setRanking(rankedList)
    })

    connection.on('triggerFired', (evt) => {
      setTriggerEvents((prev) => [evt, ...prev].slice(0, 50))
    })

    connection.onreconnecting(() => setConnectionState('reconnecting'))
    connection.onreconnected(() => setConnectionState('connected'))
    connection.onclose(() => setConnectionState('disconnected'))

    connection
      .start()
      .then(() => !disposed && setConnectionState('connected'))
      .catch(() => !disposed && setConnectionState('disconnected'))

    connectionRef.current = connection
    return () => {
      disposed = true
      connection.stop()
    }
  }, [])

  return { spreads, ranking, triggerEvents, connectionState }
}
