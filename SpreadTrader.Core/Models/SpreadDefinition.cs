namespace SpreadTrader.Core.Models;

/// <summary>
/// A calendar spread between two legs of the same underlying (e.g. July fut vs August fut).
/// Spread value is always computed as FarLeg.LastPrice - NearLeg.LastPrice.
/// </summary>
public sealed class SpreadDefinition
{
    public required string Id { get; set; } // stable id, e.g. "RELIANCE-JUL-AUG"
    public required string Underlying { get; set; } // e.g. "RELIANCE"
    public required InstrumentConfig NearLeg { get; set; } // e.g. July future
    public required InstrumentConfig FarLeg { get; set; } // e.g. August future

    /// <summary>
    /// The underlying's CASH/INDEX instrument (e.g. the RELIANCE equity, or the NIFTY 50 index),
    /// resolved from the broker's master so the live spot price can be shown alongside the two
    /// futures legs and the near leg's basis computed against it - see SpreadSnapshot.BasisPercent.
    ///
    /// NOT a tradeable leg: nothing is ever ordered against it, it exists purely to anchor a tick
    /// subscription for the spot price. Null whenever spot could not be resolved (no matching cash
    /// instrument in the broker's master, an index whose alias isn't known - see SpotSymbology - or
    /// an MCX contract, which has no cash equivalent at all). A null here is not an error: the
    /// spread keeps working exactly as before and the UI simply shows no spot.
    /// </summary>
    public InstrumentConfig? SpotLeg { get; set; }

    public bool Enabled { get; set; } = true;
}
