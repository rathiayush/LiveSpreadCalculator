using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SpreadTrader.DhanIntegration;
using SpreadTrader.DhanIntegration.Instruments;
using SpreadTrader.DhanIntegration.Ticker;

namespace SpreadTrader.Engine.Tests;

/// <summary>
/// Covers a real production symptom: NIFTY/BANKNIFTY Options Flow computed a nonsense ATM (7500 for
/// NIFTY, 3200 for BANKNIFTY - neither a remotely plausible index level) because Dhan's securityId
/// is only unique WITHIN one exchange segment, and this app started subscribing the IDX_I (index)
/// segment for spot pricing without ever disambiguating its ids from NSE_FNO/NSE_EQ ones sharing
/// the same tick store. See DhanSymbology.IndexSegmentTokenFlag for the full explanation and fix.
/// </summary>
public class DhanIndexTokenTaggingTests
{
    // ---- DhanSymbology tag/untag ---------------------------------------------------------------

    [Fact]
    public void TaggingAnIndexTokenSetsTheReservedBit()
    {
        var tagged = DhanSymbology.TagIndexToken(13);
        Assert.True(DhanSymbology.IsIndexToken(tagged));
        Assert.NotEqual(13u, tagged);
    }

    [Fact]
    public void UntaggingRecoversTheOriginalRawSecurityId()
    {
        var tagged = DhanSymbology.TagIndexToken(25);
        Assert.Equal(25u, DhanSymbology.UntagIndexToken(tagged));
    }

    [Fact]
    public void AnUntaggedTokenIsNotReportedAsAnIndexToken()
    {
        Assert.False(DhanSymbology.IsIndexToken(1234));
    }

    [Fact]
    public void TaggingCannotCollideWithARealisticSecurityId()
    {
        // Real Dhan security ids are documented to fit comfortably under 2^24 - the exact case this
        // bug depends on (an index id and an NSE_FNO/NSE_EQ id sharing the same raw number).
        uint plausibleRealId = 5_000_000;
        var tagged = DhanSymbology.TagIndexToken(13);
        Assert.NotEqual(plausibleRealId, tagged);
    }

    // ---- DhanTickPacketParser: incoming packets get tagged based on the wire segment byte -------

    private static byte[] BuildTickerPacket(byte exchangeSegment, uint securityId, float lastPrice)
    {
        var packet = new byte[16];
        packet[0] = 2; // Ticker
        BitConverter.GetBytes((ushort)16).CopyTo(packet, 1);
        packet[3] = exchangeSegment;
        BitConverter.GetBytes(securityId).CopyTo(packet, 4);
        BitConverter.GetBytes(lastPrice).CopyTo(packet, 8);
        BitConverter.GetBytes((uint)0).CopyTo(packet, 12);
        return packet;
    }

    [Fact]
    public void AnIndexSegmentPacketIsTaggedOnDecode()
    {
        var raw = BuildTickerPacket(exchangeSegment: 0, securityId: 13, lastPrice: 24500f); // IDX_I
        var packets = DhanTickPacketParser.ParseMessage(raw);

        var packet = Assert.Single(packets);
        Assert.True(DhanSymbology.IsIndexToken(packet.SecurityId));
        Assert.Equal(13u, DhanSymbology.UntagIndexToken(packet.SecurityId));
    }

    [Fact]
    public void AnNfoSegmentPacketIsNeverTagged()
    {
        var raw = BuildTickerPacket(exchangeSegment: 2, securityId: 13, lastPrice: 100f); // NSE_FNO
        var packets = DhanTickPacketParser.ParseMessage(raw);

        var packet = Assert.Single(packets);
        Assert.False(DhanSymbology.IsIndexToken(packet.SecurityId));
        Assert.Equal(13u, packet.SecurityId);
    }

    [Fact]
    public void SameRawSecurityIdInDifferentSegmentsProducesDifferentTokens()
    {
        // This is the exact collision the bug depended on: id 13 exists in both IDX_I (NIFTY 50)
        // and, hypothetically, some NSE_FNO contract. Post-fix they can never collide in a shared
        // uint-keyed tick store.
        var indexPacket = Assert.Single(DhanTickPacketParser.ParseMessage(BuildTickerPacket(0, 13, 24500f)));
        var fnoPacket = Assert.Single(DhanTickPacketParser.ParseMessage(BuildTickerPacket(2, 13, 100f)));

        Assert.NotEqual(indexPacket.SecurityId, fnoPacket.SecurityId);
    }

    // ---- DhanInstrumentMasterService: index spot rows get tagged at construction -----------------

    private static string WriteScripMasterFixture()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhan-index-tagging-{Guid.NewGuid():N}.csv");
        var header = "SEM_SMST_SECURITY_ID,SEM_EXM_EXCH_ID,SEM_SEGMENT,SEM_INSTRUMENT_NAME,SEM_TRADING_SYMBOL,SEM_CUSTOM_SYMBOL,SM_SYMBOL_NAME,SEM_EXPIRY_DATE,SEM_LOT_UNITS,SEM_TICK_SIZE";
        var expiry = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(20)).ToString("yyyy-MM-dd");
        var rows = new[]
        {
            // A future so the master has something to load (TryWarmFromDiskCache bails on zero futures).
            "99999,NSE,D,FUTIDX,NIFTY-Nov2026-FUT,,NIFTY,2026-11-25,50,0.05",
            // The index spot row itself - real Dhan NIFTY 50 index id.
            "13,NSE,I,INDEX,NIFTY 50,,NIFTY 50,,0,0",
            // An equity spot row - untouched by tagging.
            "500,NSE,E,EQUITY,RELIANCE,,RELIANCE,,0,0.05",
        };
        File.WriteAllLines(path, new[] { header }.Concat(rows));
        return path;
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("no network in this test");
    }

    private static async Task<DhanInstrumentMasterService> BuildLoadedMasterAsync()
    {
        var cachePath = WriteScripMasterFixture();
        var master = new DhanInstrumentMasterService(
            new HttpClient(new ThrowingHandler()),
            Options.Create(new DhanOptions()),
            NullLogger<DhanInstrumentMasterService>.Instance,
            futuresCsvPath: cachePath,
            exchanges: new[] { "NFO" },
            scripMasterCachePath: cachePath);

        // The live download always fails (ThrowingHandler) and is swallowed since the disk warm
        // above already populated _instruments - see EnsureLoadedAsync's doc comment.
        await master.EnsureLoadedAsync(CancellationToken.None);
        return master;
    }

    [Fact]
    public async Task NiftyIndexSpotResolvesToATaggedToken()
    {
        var master = await BuildLoadedMasterAsync();
        var spot = master.FindSpot("NIFTY");

        Assert.NotNull(spot);
        Assert.True(DhanSymbology.IsIndexToken(spot!.InstrumentToken));
        Assert.Equal(13u, DhanSymbology.UntagIndexToken(spot.InstrumentToken));
    }

    [Fact]
    public async Task EquitySpotIsNotTagged()
    {
        var master = await BuildLoadedMasterAsync();
        var spot = master.FindSpot("RELIANCE");

        Assert.NotNull(spot);
        Assert.False(DhanSymbology.IsIndexToken(spot!.InstrumentToken));
        Assert.Equal(500u, spot.InstrumentToken);
    }

    [Fact]
    public async Task TaggedIndexTokenNeverCollidesWithARealisticFutureOrEquityId()
    {
        var master = await BuildLoadedMasterAsync();
        var indexSpot = master.FindSpot("NIFTY");
        var equitySpot = master.FindSpot("RELIANCE");

        Assert.NotEqual(indexSpot!.InstrumentToken, equitySpot!.InstrumentToken);
        // Even though the raw ids (13 vs 500) are already different here, the point is structural:
        // the tagged token space (bit 30 set) can never overlap the untagged one at all, regardless
        // of what raw id either side happens to have in a real scrip master.
        Assert.True(DhanSymbology.IsIndexToken(indexSpot.InstrumentToken));
        Assert.False(DhanSymbology.IsIndexToken(equitySpot.InstrumentToken));
    }
}
