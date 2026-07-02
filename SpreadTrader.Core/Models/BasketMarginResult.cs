namespace SpreadTrader.Core.Models;

/// <summary>Result of a POST /margins/basket call for one basket's legs - see
/// KiteMarginService. FinalTotal is what Kite actually blocks once the hedge/spread benefit
/// between the legs (e.g. a near/far calendar spread) is taken into account; InitialTotal is
/// what it would be if the same account had no existing offsetting positions. The difference
/// between them is the margin benefit the basket gets from being placed together.</summary>
public sealed record BasketMarginResult(
    bool Success,
    decimal? InitialTotal,
    decimal? FinalTotal,
    string? ErrorMessage);
