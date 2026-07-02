using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;
using SpreadTrader.KiteIntegration.Instruments;

namespace SpreadTrader.Engine.Services;

/// <summary>
/// On demand (via POST /api/universe/build), turns every stock Kite currently lists futures
/// for on NFO into a live-streamed calendar spread universe in one call - no hand-maintained
/// symbol list required:
///   1. asks the Kite instrument master for every distinct underlying with an NFO futures
///      contract right now (see InstrumentMasterService.GetAllFuturesUnderlyings), optionally
///      capped to the first maxSymbols of them,
///   2. resolves each symbol's near/far month futures contract,
///   3. writes the resolved set to a CSV (App_Data/universe_futures.csv) - this is the
///      "master list" of what's actually tradeable right now,
///   4. upserts a SpreadDefinition for every resolved row and subscribes the live ticker to it,
///      so all of them start streaming immediately without configuring each one by hand.
/// Symbols that only have one futures contract listed right now (near month about to expire
/// with no far month yet rolled) are skipped rather than failing the whole batch - see
/// UniverseBuildResult.Skipped. Re-run this after every monthly expiry roll, or whenever you
/// want to pick up newly-added F&O stocks - nothing here re-runs automatically.
/// </summary>
public sealed class UniverseBuilderService
{
    private readonly InstrumentMasterService _instrumentMaster;
    private readonly ISpreadDefinitionStore _spreadStore;
    private readonly TickIngestionHostedService _tickIngestion;
    private readonly ILogger<UniverseBuilderService> _logger;
    private readonly string _outputCsvPath;

    public UniverseBuilderService(
        InstrumentMasterService instrumentMaster,
        ISpreadDefinitionStore spreadStore,
        TickIngestionHostedService tickIngestion,
        ILogger<UniverseBuilderService> logger,
        string outputCsvPath)
    {
        _instrumentMaster = instrumentMaster;
        _spreadStore = spreadStore;
        _tickIngestion = tickIngestion;
        _logger = logger;
        _outputCsvPath = outputCsvPath;
    }

    /// <param name="maxSymbols">Optional cap on how many symbols (alphabetically, from Kite's
    /// full NFO futures list) to build. Null/omitted = every stock currently listed for futures
    /// on NFO - roughly 200-220 as of mid-2026, since that's the entire exchange's F&O universe,
    /// not an artificial limit in this code.</param>
    public async Task<UniverseBuildResult> BuildAsync(int? maxSymbols, CancellationToken cancellationToken)
    {
        await _instrumentMaster.EnsureLoadedAsync(cancellationToken);

        var symbols = _instrumentMaster.GetAllFuturesUnderlyings("NFO");
        if (maxSymbols is > 0 && maxSymbols < symbols.Count)
            symbols = symbols.Take(maxSymbols.Value).ToArray();

        var included = new List<UniverseBuildRow>();
        var skipped = new List<string>();
        // Collected across every spread and subscribed in one batch at the end - see
        // TickIngestionHostedService.SubscribeTokensAsync for why per-spread subscribing is
        // unreliable at this scale.
        var allTokens = new HashSet<uint>();

        foreach (var symbol in symbols)
        {
            var contracts = _instrumentMaster.FindFutures(symbol, "NFO");
            if (contracts.Count < 2)
            {
                skipped.Add($"{symbol} (only {contracts.Count} futures contract(s) currently listed - needs a near and a far month)");
                continue;
            }

            var near = contracts[0];
            var far = contracts[1];

            var spreadId = $"{symbol}-{MonthAbbrev(near.Expiry)}-{MonthAbbrev(far.Expiry)}";
            var spread = new SpreadDefinition
            {
                Id = spreadId,
                Underlying = symbol,
                NearLeg = new InstrumentConfig
                {
                    TradingSymbol = near.TradingSymbol,
                    Exchange = near.Exchange,
                    InstrumentToken = near.InstrumentToken,
                    Underlying = symbol,
                    Expiry = near.Expiry,
                    LotSize = near.LotSize,
                },
                FarLeg = new InstrumentConfig
                {
                    TradingSymbol = far.TradingSymbol,
                    Exchange = far.Exchange,
                    InstrumentToken = far.InstrumentToken,
                    Underlying = symbol,
                    Expiry = far.Expiry,
                    LotSize = far.LotSize,
                },
                Enabled = true,
            };

            _spreadStore.Upsert(spread);
            allTokens.Add(near.InstrumentToken);
            allTokens.Add(far.InstrumentToken);

            included.Add(new UniverseBuildRow(
                symbol, spreadId,
                near.TradingSymbol, near.Expiry, near.LotSize,
                far.TradingSymbol, far.Expiry, far.LotSize));
        }

        try
        {
            await _tickIngestion.SubscribeTokensAsync(allTokens, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to subscribe ticker for {Count} universe tokens", allTokens.Count);
        }

        await WriteOutputCsvAsync(included, cancellationToken);

        _logger.LogInformation(
            "Universe build: {Included}/{Requested} symbols resolved to a tradeable calendar spread, {Skipped} skipped, {Tokens} instrument tokens subscribed in one batch",
            included.Count, symbols.Count, skipped.Count, allTokens.Count);

        return new UniverseBuildResult(symbols.Count, included, skipped);
    }

    private async Task WriteOutputCsvAsync(IReadOnlyList<UniverseBuildRow> rows, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(_outputCsvPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var sb = new StringBuilder();
        sb.AppendLine("Underlying,SpreadId,NearTradingSymbol,NearExpiry,NearLotSize,FarTradingSymbol,FarExpiry,FarLotSize");
        foreach (var r in rows)
        {
            sb.AppendLine(string.Join(",",
                r.Underlying, r.SpreadId,
                r.NearTradingSymbol, r.NearExpiry?.ToString("yyyy-MM-dd") ?? "", r.NearLotSize,
                r.FarTradingSymbol, r.FarExpiry?.ToString("yyyy-MM-dd") ?? "", r.FarLotSize));
        }

        var tempPath = _outputCsvPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, sb.ToString(), cancellationToken);
        File.Move(tempPath, _outputCsvPath, overwrite: true);
    }

    private static string MonthAbbrev(DateOnly? expiry) =>
        expiry is { } d ? d.ToString("MMM", CultureInfo.InvariantCulture).ToUpperInvariant() : "XXX";
}
