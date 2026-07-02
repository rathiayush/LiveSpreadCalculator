import SpreadList from '../components/SpreadList'

// Read-only(-ish) view of every configured spread definition, with a remove action - no "add
// spread" form here. Spreads are populated in bulk from the Symbols page's universe builder now,
// not added one at a time by hand.
export default function ConfiguredSpreadsPage({ spreads, onSpreadsChanged }) {
  return <SpreadList spreads={spreads} onChanged={onSpreadsChanged} />
}
