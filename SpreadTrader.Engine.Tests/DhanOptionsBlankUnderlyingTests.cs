using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SpreadTrader.DhanIntegration;
using SpreadTrader.DhanIntegration.OptionsFlow;

namespace SpreadTrader.Engine.Tests;

/// <summary>
/// Covers a real production symptom: NIFTY/BANKNIFTY Options Flow stayed permanently on "waiting
/// for the first update" because Dhan's scrip master leaves SM_SYMBOL_NAME blank on some rows -
/// DhanInstrumentMasterService already has a documented fallback for this on the futures side
/// (DeriveUnderlying), but DhanOptionsInstrumentSource's parser had none, so every option row with
/// a blank symbol name was silently dropped and the chain parsed to zero contracts.
/// </summary>
public class DhanOptionsBlankUnderlyingTests
{
    private const uint NiftyCallToken = 555001;
    private const uint NiftyPutToken = 555002;

    private static string WriteFixtureCsv(bool blankSymbolName)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhan-options-blank-underlying-{Guid.NewGuid():N}.csv");
        var header = "SEM_SMST_SECURITY_ID,SEM_EXM_EXCH_ID,SEM_INSTRUMENT_NAME,SEM_TRADING_SYMBOL,SM_SYMBOL_NAME,SEM_EXPIRY_DATE,SEM_LOT_UNITS,SEM_STRIKE_PRICE,SEM_OPTION_TYPE";
        var expiry = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(14)).ToString("yyyy-MM-dd");
        var symbolName = blankSymbolName ? "" : "NIFTY";

        var rows = new[]
        {
            $"{NiftyCallToken},NSE,OPTIDX,NIFTY-Nov2026-24000-CE,{symbolName},{expiry},50,24000,CE",
            $"{NiftyPutToken},NSE,OPTIDX,NIFTY-Nov2026-24000-PE,{symbolName},{expiry},50,24000,PE",
        };
        File.WriteAllLines(path, new[] { header }.Concat(rows));
        return path;
    }

    private static DhanOptionsInstrumentSource BuildSource(bool blankSymbolName) => new(
        new HttpClient(),
        Options.Create(new DhanOptions()),
        Options.Create(new SpreadTrader.Core.OptionsFlow.OptionsFlowOptions { Indices = new List<string> { "NIFTY" } }),
        WriteFixtureCsv(blankSymbolName),
        new DhanSegmentRegistry(),
        NullLogger<DhanOptionsInstrumentSource>.Instance);

    [Fact]
    public async Task BlankSymbolNameColumnStillResolvesTheChainFromTheTradingSymbol()
    {
        var source = BuildSource(blankSymbolName: true);
        await source.EnsureLoadedAsync(CancellationToken.None);

        var expiry = source.GetNearestExpiry("NIFTY");
        Assert.NotNull(expiry);

        var chain = source.GetChain("NIFTY", expiry!.Value);
        Assert.Equal(2, chain.Count);
        Assert.Contains(chain, c => c.InstrumentToken == NiftyCallToken && c.Underlying == "NIFTY");
        Assert.Contains(chain, c => c.InstrumentToken == NiftyPutToken && c.Underlying == "NIFTY");
    }

    [Fact]
    public async Task APopulatedSymbolNameColumnIsUsedDirectlyAndUnaffected()
    {
        var source = BuildSource(blankSymbolName: false);
        await source.EnsureLoadedAsync(CancellationToken.None);

        var expiry = source.GetNearestExpiry("NIFTY");
        Assert.NotNull(expiry);

        var chain = source.GetChain("NIFTY", expiry!.Value);
        Assert.Equal(2, chain.Count);
    }
}
