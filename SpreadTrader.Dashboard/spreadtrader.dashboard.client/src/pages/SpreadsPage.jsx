import { useLiveSpreads } from '../hooks/spreadHub'
import SpreadGrid from '../components/SpreadGrid'

// The full live per-spread grid - search by symbol, real pagination (see SpreadGrid). Separate
// from the Dashboard page since that one is meant to be a full-picture "everything at once"
// glance, while this one is for looking up/drilling into a specific stock's spread.
//
// Reads the live (per-tick, ~every 250-300ms) spread map directly from the shared store (see
// hooks/spreadHub.js) rather than via a prop - this is the single highest-frequency piece of
// data in the app, so keeping the subscription here means it only costs anything while this
// page is actually the one on screen.
export default function SpreadsPage({ spreads }) {
  const liveValues = useLiveSpreads()
  return <SpreadGrid definitions={spreads} liveValues={liveValues} />
}
