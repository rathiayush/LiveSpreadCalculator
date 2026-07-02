namespace SpreadTrader.Core.Models;

/// <summary>One row of the resolved futures universe - a stock from the constituents CSV that
/// actually has a near and far month futures contract listed right now.</summary>
public sealed record UniverseBuildRow(
    string Underlying,
    string SpreadId,
    string NearTradingSymbol,
    DateOnly? NearExpiry,
    int NearLotSize,
    string FarTradingSymbol,
    DateOnly? FarExpiry,
    int FarLotSize);

/// <summary>Result of building the futures universe from the constituents CSV - see
/// UniverseBuilderService. Skipped entries are symbols in the constituents list that don't
/// currently have two listed futures contracts (delisted from F&amp;O, just IPO'd, etc.) rather
/// than being a hard failure. RequestedCount reflects however many symbols were actually asked
/// for (the full constituents list, or fewer if maxSymbols was passed to the build call).</summary>
public sealed record UniverseBuildResult(
    int RequestedCount,
    IReadOnlyList<UniverseBuildRow> Included,
    IReadOnlyList<string> Skipped);
