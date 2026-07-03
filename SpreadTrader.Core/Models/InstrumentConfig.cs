namespace SpreadTrader.Core.Models;

/// <summary>
/// One tradeable instrument (e.g. a specific futures contract) that the engine tracks.
/// InstrumentToken is resolved at startup from the Kite instrument master by TradingSymbol+Exchange.
/// </summary>
public sealed class InstrumentConfig
{
    public required string TradingSymbol { get; set; } // e.g. "RELIANCE26JULFUT"
    public required string Exchange { get; set; } // e.g. "NFO"
    public uint InstrumentToken { get; set; } // resolved from instrument master, 0 until resolved
    public string? Underlying { get; set; } // e.g. "RELIANCE"
    public DateOnly? Expiry { get; set; }

    /// <summary>Exchange lot size for this contract, resolved alongside InstrumentToken. Order
    /// quantities must be whole multiples of this - used to convert a "lots per trade" bulk
    /// trigger template into an actual order quantity per stock (lot sizes vary a lot across the
    /// F&amp;O universe, so a single flat quantity across 100 different stocks isn't meaningful).</summary>
    public int LotSize { get; set; }

    /// <summary>Minimum price increment for this contract, resolved alongside InstrumentToken.
    /// LIMIT order prices must be an exact multiple of this or Kite rejects the order ("Tick
    /// size for this script is X") - commonly 0.05 for NSE F&amp;O but genuinely varies by
    /// instrument, so it's tracked per-contract rather than assumed. 0 until resolved - see
    /// OrderDispatchHostedService.ApplyMarketProtection for the fallback used in that case.</summary>
    public decimal TickSize { get; set; }
}
