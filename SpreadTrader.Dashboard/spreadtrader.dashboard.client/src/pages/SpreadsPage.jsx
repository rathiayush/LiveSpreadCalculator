import SpreadGrid from '../components/SpreadGrid'

// The full live per-spread grid - search by symbol, real pagination (see SpreadGrid). Separate
// from the Dashboard page since that one is meant to be a full-picture "everything at once"
// glance, while this one is for looking up/drilling into a specific stock's spread.
export default function SpreadsPage({ spreads, liveValues }) {
  return <SpreadGrid definitions={spreads} liveValues={liveValues} />
}
