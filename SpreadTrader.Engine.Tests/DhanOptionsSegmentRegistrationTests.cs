using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SpreadTrader.DhanIntegration;
using SpreadTrader.DhanIntegration.OptionsFlow;

namespace SpreadTrader.Engine.Tests;

/// <summary>
/// Regression test for the reported "Options Flow is empty" bug: the option chain loaded and
/// EnsureLoadedAsync reported contracts, but not a single tick ever arrived.
///
/// THE BUG. Dhan's securityId is only unique WITHIN a segment (see DhanSegmentRegistry's doc
/// comment), so DhanMarketFeedClient refuses to subscribe a token unless something has told it
/// which segment that token lives in. DhanOptionsInstrumentSource builds every OptionContract with
/// Segment="NSE_FNO" hardcoded - it KNOWS the answer - but never told the shared
/// DhanSegmentRegistry, the exact same registry DhanPositionsService already feeds for the
/// identical reason (a manually-placed option position). So every option token the chain resolved
/// was silently dropped by the feed as "unknown segment", and the chart stayed empty forever with
/// no error anywhere - the chain and the subscription request both looked completely healthy.
///
/// This drives the REAL DhanOptionsInstrumentSource.EnsureLoadedAsync against a disk-cache CSV
/// fixture (no network needed - the disk-cache path is a real, exercised code path, not a stand-in
/// for the download path), then asserts against the real DhanSegmentRegistry - the same instance
/// DhanMarketFeedClient consults - rather than asserting on GetChain() alone, which would have
/// passed both before and after the fix and proven nothing about whether the feed can actually
/// subscribe these tokens.
/// </summary>
public class DhanOptionsSegmentRegistrationTests
{
    private const uint NiftyCallToken = 987654;
    private const uint NiftyPutToken = 987655;

    private static string WriteFixtureCsv()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhan-options-fixture-{Guid.NewGuid():N}.csv");
        var header = "SEM_SMST_SECURITY_ID,SEM_EXM_EXCH_ID,SEM_INSTRUMENT_NAME,SEM_TRADING_SYMBOL,SM_SYMBOL_NAME,SEM_EXPIRY_DATE,SEM_LOT_UNITS,SEM_STRIKE_PRICE,SEM_OPTION_TYPE";
        var expiry = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(14)).ToString("yyyy-MM-dd");
        var rows = new[]
        {
            $"{NiftyCallToken},NSE,OPTIDX,NIFTY{expiry:yyMMdd}24000CE,NIFTY,{expiry},50,24000,CE",
            $"{NiftyPutToken},NSE,OPTIDX,NIFTY{expiry:yyMMdd}24000PE,NIFTY,{expiry},50,24000,PE",
            // A non-option row of the kind the real dump is mostly full of - must be ignored, not
            // registered, and must not stop the option rows above from being processed.
            "111,NSE,EQUITY,RELIANCE,RELIANCE,,1,0,",
        };
        File.WriteAllLines(path, new[] { header }.Concat(rows));
        return path;
    }

    private static (DhanOptionsInstrumentSource Source, DhanSegmentRegistry Registry) BuildRig()
    {
        var registry = new DhanSegmentRegistry();
        var source = new DhanOptionsInstrumentSource(
            new HttpClient(),
            Options.Create(new DhanOptions()),
            Options.Create(new SpreadTrader.Core.OptionsFlow.OptionsFlowOptions { Indices = new List<string> { "NIFTY" } }),
            WriteFixtureCsv(),
            registry,
            NullLogger<DhanOptionsInstrumentSource>.Instance);
        return (source, registry);
    }

    [Fact]
    public async Task LoadingTheChainRegistersEveryContractsSegment()
    {
        // THE regression. Before the fix this registry stayed empty however many contracts the
        // chain reported - the exact state that shipped.
        var (source, registry) = BuildRig();

        await source.EnsureLoadedAsync(CancellationToken.None);

        Assert.True(registry.TryGet(NiftyCallToken, out var callSegment));
        Assert.Equal("NSE_FNO", callSegment);
        Assert.True(registry.TryGet(NiftyPutToken, out var putSegment));
        Assert.Equal("NSE_FNO", putSegment);
    }

    [Fact]
    public async Task TheChainItselfIsUnaffectedByTheFix()
    {
        // Guards against "fixing" this by changing what gets loaded rather than by registering it.
        var (source, _) = BuildRig();

        await source.EnsureLoadedAsync(CancellationToken.None);
        var expiry = source.GetNearestExpiry("NIFTY");

        Assert.NotNull(expiry);
        var chain = source.GetChain("NIFTY", expiry!.Value);
        Assert.Equal(2, chain.Count);
        Assert.Contains(chain, c => c.InstrumentToken == NiftyCallToken);
        Assert.Contains(chain, c => c.InstrumentToken == NiftyPutToken);
    }

    [Fact]
    public async Task NonOptionRowsAreNeverRegistered()
    {
        // The equity row in the fixture must not pollute the registry - only option tokens this
        // source itself resolved should ever come from here.
        var (source, registry) = BuildRig();

        await source.EnsureLoadedAsync(CancellationToken.None);

        Assert.False(registry.TryGet(111, out _));
    }
}
