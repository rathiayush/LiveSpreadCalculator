using SpreadTrader.DhanIntegration;

namespace SpreadTrader.Tests;

/// <summary>
/// Pins the Kite-vocabulary-to-Dhan translation table. These mappings are the difference between
/// an order reaching the intended contract and product or a different one, and a wrong mapping
/// fails silently - the order is accepted, just not the order that was meant. Hence tests rather
/// than trust, particularly for the product mapping.
/// </summary>
public class DhanSymbologyTests
{
    [Theory]
    [InlineData("NFO", "NSE_FNO")]
    [InlineData("MCX", "MCX_COMM")]
    [InlineData("NSE", "NSE_EQ")]
    [InlineData("BFO", "BSE_FNO")]
    public void CanonicalExchange_MapsToDhanSegment(string canonical, string expected) =>
        Assert.Equal(expected, DhanSymbology.ToDhanSegment(canonical));

    [Theory]
    [InlineData("NSE_FNO", "NFO")]
    [InlineData("MCX_COMM", "MCX")]
    public void DhanSegment_MapsBackToCanonicalExchange(string segment, string expected) =>
        Assert.Equal(expected, DhanSymbology.ToCanonicalExchange(segment));

    [Fact]
    public void ExchangeMapping_RoundTrips()
    {
        // Positions come back tagged with a Dhan segment and are filtered against canonical
        // exchange codes, so these two directions must agree exactly or open positions silently
        // vanish from the dashboard.
        foreach (var canonical in new[] { "NFO", "MCX", "NSE", "BSE", "BFO" })
            Assert.Equal(canonical, DhanSymbology.ToCanonicalExchange(DhanSymbology.ToDhanSegment(canonical)));
    }

    [Fact]
    public void UnknownSegment_PassesThroughUnchanged()
    {
        // A segment Dhan adds later must surface as itself in logs and positions rather than being
        // silently mapped onto the wrong exchange.
        Assert.Equal("SOME_NEW_SEGMENT", DhanSymbology.ToCanonicalExchange("SOME_NEW_SEGMENT"));
    }

    [Fact]
    public void Nrml_MapsToMargin_NotIntradayOrCnc()
    {
        // The single most consequential mapping here. NRML is the carry-forward F&O product; Dhan
        // calls it MARGIN. INTRADAY would have the broker auto-square-off every leg at session
        // close - a materially different trade for a calendar spread held across days - and CNC is
        // a delivery-equity product that is simply invalid for a futures contract.
        Assert.Equal("MARGIN", DhanSymbology.ToDhanProduct("NRML"));
        Assert.Equal("INTRADAY", DhanSymbology.ToDhanProduct("MIS"));
    }

    [Fact]
    public void ProductMapping_RoundTrips()
    {
        // Positions are netted per product, so the product reported back from Dhan has to match
        // what the square-off form will send down, or the exit won't net against the position.
        foreach (var canonical in new[] { "NRML", "MIS", "CNC", "MTF" })
            Assert.Equal(canonical, DhanSymbology.ToCanonicalProduct(DhanSymbology.ToDhanProduct(canonical)));
    }

    [Theory]
    [InlineData("MARKET", "MARKET")]
    [InlineData("LIMIT", "LIMIT")]
    [InlineData("SL", "STOP_LOSS")]
    [InlineData("SL-M", "STOP_LOSS_MARKET")]
    public void OrderTypes_MapToDhanNames(string canonical, string expected) =>
        Assert.Equal(expected, DhanSymbology.ToDhanOrderType(canonical));

    [Theory]
    [InlineData("TRADED", "COMPLETE")]
    [InlineData("REJECTED", "REJECTED")]
    [InlineData("CANCELLED", "CANCELLED")]
    [InlineData("EXPIRED", "CANCELLED")]
    public void TerminalStatuses_MapToTheEnginesVocabulary(string dhanStatus, string expected)
    {
        var mapped = DhanSymbology.ToCanonicalOrderStatus(dhanStatus);
        Assert.Equal(expected, mapped);
        Assert.True(DhanSymbology.IsTerminalStatus(mapped));
    }

    [Theory]
    [InlineData("TRANSIT")]
    [InlineData("PENDING")]
    [InlineData("PART_TRADED")]
    [InlineData(null)]
    public void InFlightStatuses_MapToOpenAndAreNotTerminal(string? dhanStatus)
    {
        // OrderDispatchHostedService branches on exactly this: "OPEN" means keep polling and, if it
        // stays that way, cancel the stray order. Mapping an in-flight status to a terminal one
        // would make a resting order look resolved and leave it live and unmanaged.
        var mapped = DhanSymbology.ToCanonicalOrderStatus(dhanStatus);
        Assert.Equal("OPEN", mapped);
        Assert.False(DhanSymbology.IsTerminalStatus(mapped));
    }

    [Theory]
    [InlineData("FUTSTK", true)]
    [InlineData("FUTIDX", true)]
    [InlineData("FUTCOM", true)]
    [InlineData("OPTSTK", false)]
    [InlineData("EQUITY", false)]
    [InlineData(null, false)]
    public void OnlyFuturesAreRetainedFromTheScripMaster(string? instrumentName, bool expected) =>
        Assert.Equal(expected, DhanSymbology.IsFuture(instrumentName));

    [Theory]
    [InlineData((byte)2, "NFO")]
    [InlineData((byte)5, "MCX")]
    [InlineData((byte)1, "NSE")]
    public void FeedSegmentBytes_MapToCanonicalExchanges(byte feedSegment, string expected) =>
        Assert.Equal(expected, DhanSymbology.FeedSegmentToExchange(feedSegment));
}
