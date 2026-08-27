namespace SpreadTrader.Core.Brokers;

/// <summary>
/// Matches a FUTURES underlying to the name its CASH/INDEX instrument is listed under, so
/// IBrokerInstrumentMaster.FindSpot can find the spot price for a calendar spread's underlying.
///
/// For a stock this is a non-problem: RELIANCE futures are written on an equity also called
/// RELIANCE, so an exact name match resolves it and nothing here is needed.
///
/// Indices are the exception, and the reason this exists. An index future's underlying name is a
/// compact code (NIFTY, BANKNIFTY), while the index itself is published under a longer display
/// name that differs per broker - Kite lists NIFTY's spot as "NIFTY 50" in its INDICES segment,
/// while Dhan and Kotak may carry the short form directly. So callers try, in order:
///
///   1. the underlying name exactly as-is        (every stock; brokers using the short index form)
///   2. each alias below                         (brokers using the long display form)
///
/// UNRESOLVED IS NOT AN ERROR. An underlying with no matching spot instrument simply gets no spot
/// price and no basis - the spread itself is unaffected. That is deliberate and is the safe
/// direction to fail in: a missing spot column is obvious and harmless, whereas silently matching
/// the WRONG instrument would show a plausible-looking but meaningless basis %, which on a trading
/// screen is far worse than showing nothing. For that reason nothing here does fuzzy or prefix
/// matching - only exact, known names.
/// </summary>
public static class SpotSymbology
{
    /// <summary>
    /// Index futures underlying -> the display name(s) its spot is listed under. Ordered by
    /// likelihood; the first that matches the broker's master wins.
    ///
    /// Kept deliberately small and explicit. These are the NSE index futures that actually trade,
    /// and their spot names come from NSE's own index naming, which is what all three brokers
    /// ultimately republish.
    /// </summary>
    private static readonly Dictionary<string, string[]> IndexAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NIFTY"] = new[] { "NIFTY 50", "NIFTY50" },
        ["BANKNIFTY"] = new[] { "NIFTY BANK", "BANKNIFTY" },
        ["FINNIFTY"] = new[] { "NIFTY FIN SERVICE", "NIFTY FINANCIAL SERVICES", "FINNIFTY" },
        ["MIDCPNIFTY"] = new[] { "NIFTY MID SELECT", "NIFTY MIDCAP SELECT", "MIDCPNIFTY" },
        ["NIFTYNXT50"] = new[] { "NIFTY NEXT 50", "NIFTYNEXT50" },
    };

    /// <summary>
    /// Every name worth trying for this underlying, most likely first: the underlying itself, then
    /// any known index aliases. Callers walk this in order and take the first hit in their own
    /// master, so a broker that lists indices under the short code resolves at step 1 and one that
    /// uses the long display name resolves at step 2 - without either needing its own lookup table.
    /// </summary>
    public static IReadOnlyList<string> CandidateSpotNames(string underlyingName)
    {
        if (string.IsNullOrWhiteSpace(underlyingName)) return Array.Empty<string>();

        var trimmed = underlyingName.Trim();
        if (!IndexAliases.TryGetValue(trimmed, out var aliases)) return new[] { trimmed };

        var candidates = new List<string>(aliases.Length + 1) { trimmed };
        foreach (var alias in aliases)
        {
            if (!candidates.Contains(alias, StringComparer.OrdinalIgnoreCase))
                candidates.Add(alias);
        }

        return candidates;
    }

    /// <summary>True if this underlying is one of the known INDEX futures - i.e. its spot is an
    /// index level rather than a tradeable equity. Callers use this to widen their search to an
    /// index segment (Kite's "INDICES", Dhan's IDX_I) that a stock lookup would never touch.</summary>
    public static bool IsIndex(string underlyingName) =>
        !string.IsNullOrWhiteSpace(underlyingName) && IndexAliases.ContainsKey(underlyingName.Trim());
}
