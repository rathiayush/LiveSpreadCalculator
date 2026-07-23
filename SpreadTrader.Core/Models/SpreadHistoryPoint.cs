namespace SpreadTrader.Core.Models;

/// <summary>
/// One hourly snapshot of a spread persisted to the history database (see ISpreadHistoryStore /
/// SpreadHistoryCaptureHostedService). Unlike the live SpreadSnapshot (which is in-memory only and
/// overwritten every tick), these are the point-in-time rows the "Live Spread" popup plots as a
/// datetime-vs-spread chart, and the rows that get flushed when a leg expires.
///
/// Both executable directions are stored every capture - BuySpreadValue (BuyNear/SellFar) and
/// SellSpreadValue (SellNear/BuyFar) plus their percents - so the chart can show either or both
/// lines without needing another capture pass. NearSymbol/FarSymbol/NearExpiry/FarExpiry are copied
/// off the spread definition's legs at capture time so the purge-on-expiry job can find and delete a
/// leg's rows even after the definition itself has rolled to the next month.
/// </summary>
public readonly record struct SpreadHistoryPoint(
    string SpreadId,
    string Underlying,
    string NearSymbol,
    string FarSymbol,
    DateOnly? NearExpiry,
    DateOnly? FarExpiry,
    DateTimeOffset CapturedAt,
    decimal NearPrice,
    decimal FarPrice,
    string Direction,
    decimal BuySpreadValue,
    decimal SellSpreadValue,
    decimal BuySpreadPercent,
    decimal SellSpreadPercent);
