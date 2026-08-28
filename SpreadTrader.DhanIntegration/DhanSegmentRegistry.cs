using System.Collections.Concurrent;
using SpreadTrader.Core.Brokers;

namespace SpreadTrader.DhanIntegration;

/// <summary>
/// Remembers which Dhan exchange segment a given securityId belongs to, for tokens the instrument
/// master cannot answer for.
///
/// WHY THIS EXISTS. Kite's instrument_token is globally unique, so its ticker subscribes with the
/// bare number and can stream anything the account holds. Dhan's securityId is only unique WITHIN a
/// segment, so every subscription must name the segment too - and DhanMarketFeedClient resolves
/// that from the instrument master, which this app deliberately narrows to FUTURES ONLY as it
/// parses (a full scrip master is 100k+ rows, almost all option strikes - see
/// DhanInstrumentMasterService).
///
/// That is fine for spread legs, which are futures by definition. It is not fine for POSITIONS.
/// PositionsCache anchors a tick subscription for every open position so the Positions and PNL page
/// can show a live exit price and live PNL, and an account can legitimately hold something that is
/// not a future in this app's futures master - an option leg placed by hand in the Dhan app, most
/// obviously. On Kite such a position streams normally; on Dhan its subscription was silently
/// skipped for want of a segment, leaving that row with no LastPrice, no ExitPrice and no ExitPnl,
/// and its PNL frozen at whatever the last REST snapshot said. This closes that gap.
///
/// Dhan's positions response names the segment on every row, so the positions service simply
/// records what it already saw and the feed client consults it as a fallback. Nothing is inferred
/// or guessed: a token nobody has reported a segment for is still skipped rather than subscribed
/// under an assumed segment, which on Dhan could stream a completely different contract that
/// happens to share the id in another segment.
///
/// Bounded by the number of distinct instruments the account has actually held while this process
/// has been running, which is the same order as the position count - so it needs no eviction. CDV
/// (see CdvHostedService) is the second caller of Remember, for the same underlying reason: its
/// option contracts are resolved via IOptionChainSource, not the futures-only instrument master
/// either, so without this their tokens would be silently unresolvable here too.
/// </summary>
public sealed class DhanSegmentRegistry : IInstrumentSegmentRegistry
{
    private readonly ConcurrentDictionary<uint, string> _segments = new();

    /// <summary>Records the segment a securityId was observed under. Ignores blanks so a response
    /// with a missing field can't poison the map with an empty segment.</summary>
    public void Remember(uint securityId, string? dhanSegment)
    {
        if (securityId == 0 || string.IsNullOrWhiteSpace(dhanSegment)) return;
        _segments[securityId] = dhanSegment;
    }

    public bool TryGet(uint securityId, out string dhanSegment) =>
        _segments.TryGetValue(securityId, out dhanSegment!);

    public int Count => _segments.Count;
}
