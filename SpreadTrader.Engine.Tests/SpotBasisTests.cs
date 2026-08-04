using SpreadTrader.Core.Brokers;
using SpreadTrader.Core.Models;
using SpreadTrader.Engine.Services;

namespace SpreadTrader.Engine.Tests;

/// <summary>
/// Pins the spot/basis maths and the index alias resolution behind it.
///
/// The basis is a number people read off a trading screen and act on, so the two things that matter
/// most here are that the SIGN convention never silently flips (a premium must never render as a
/// discount) and that "no spot available" stays clearly distinct from "trading exactly at cash" -
/// both would be invisible in a screenshot but badly misleading.
/// </summary>
public class SpotBasisTests
{
    private static Tick TickWith(decimal last = 0m, decimal bid = 0m, decimal ask = 0m) =>
        new(1, last, 0, 0, 0, 0, 0, 0, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, BestBidPrice: bid, BestAskPrice: ask);

    // ---- Basis maths --------------------------------------------------------------------------

    [Fact]
    public void FutureAboveSpotIsAPositivePremium()
    {
        // Contango - the normal state for a future, given cost of carry.
        var basis = SpreadCalculationHostedService.ComputeBasisPercent(nearPrice: 101m, spotPrice: 100m);

        Assert.Equal(1m, basis);
    }

    [Fact]
    public void FutureBelowSpotIsANegativeDiscount()
    {
        // Backwardation. The sign is the whole signal here, so it gets its own test.
        var basis = SpreadCalculationHostedService.ComputeBasisPercent(nearPrice: 99m, spotPrice: 100m);

        Assert.Equal(-1m, basis);
    }

    [Fact]
    public void BasisIsAPercentageOfSpotNotOfTheFuture()
    {
        // (110 - 100) / 100 = 10%, NOT (110 - 100) / 110 = 9.09%. Market convention is percent of
        // spot, and the two only diverge visibly once the basis is wide.
        var basis = SpreadCalculationHostedService.ComputeBasisPercent(nearPrice: 110m, spotPrice: 100m);

        Assert.Equal(10m, basis);
    }

    [Theory]
    [InlineData(0)]      // spot never resolved, or hasn't ticked
    [InlineData(-5)]     // nonsense feed value
    public void MissingSpotYieldsZeroRatherThanThrowingOrFakingAZeroBasis(decimal spot)
    {
        // 0 is the agreed "no basis available" marker, which the UI renders as a dash - the guard
        // exists so an absent spot can never divide by zero, and never reads as a real 0.00%
        // (a future trading exactly at cash), which would be a plausible-looking lie.
        var basis = SpreadCalculationHostedService.ComputeBasisPercent(nearPrice: 101m, spotPrice: spot);

        Assert.Equal(0m, basis);
    }

    [Fact]
    public void MissingNearPriceAlsoYieldsZero()
    {
        var basis = SpreadCalculationHostedService.ComputeBasisPercent(nearPrice: 0m, spotPrice: 100m);

        Assert.Equal(0m, basis);
    }

    // ---- Which price is read off a tick -------------------------------------------------------

    [Fact]
    public void SpotPrefersLastTradedPrice()
    {
        // Spot is deliberately LTP-based - an INDEX has no bid/ask at all, so depth is not an
        // option for NIFTY/BANKNIFTY. See SpotPriceFromTick's doc comment.
        var price = SpreadCalculationHostedService.SpotPriceFromTick(TickWith(last: 100m, bid: 98m, ask: 102m));

        Assert.Equal(100m, price);
    }

    [Fact]
    public void SpotFallsBackToTheBookWhenNoTradeHasPrinted()
    {
        // An equity that has quotes but no trade yet (pre-open, or an illiquid name) still shows a
        // spot rather than a dash.
        Assert.Equal(102m, SpreadCalculationHostedService.SpotPriceFromTick(TickWith(ask: 102m)));
        Assert.Equal(98m, SpreadCalculationHostedService.SpotPriceFromTick(TickWith(bid: 98m)));
    }

    [Fact]
    public void SpotIsZeroWhenTheTickCarriesNoPriceAtAll()
    {
        Assert.Equal(0m, SpreadCalculationHostedService.SpotPriceFromTick(TickWith()));
    }

    [Fact]
    public void NearLegUsesLastTradedPriceForAnApplesToApplesComparison()
    {
        // Both sides of the ratio should be LTP where possible; comparing the future's ASK against
        // spot's LTP would build the spread between them into the basis.
        Assert.Equal(100m, SpreadCalculationHostedService.NearPriceForBasis(TickWith(last: 100m, ask: 102m)));
        Assert.Equal(102m, SpreadCalculationHostedService.NearPriceForBasis(TickWith(ask: 102m)));
    }

    // ---- Spot symbology -----------------------------------------------------------------------

    [Fact]
    public void AStockResolvesToItselfWithNoAliasNeeded()
    {
        var candidates = SpotSymbology.CandidateSpotNames("RELIANCE");

        Assert.Equal(new[] { "RELIANCE" }, candidates);
    }

    [Fact]
    public void AnIndexOffersItsOwnNameFirstThenTheLongDisplayNames()
    {
        // Order matters: a broker that lists indices under the short code must resolve at step 1
        // without ever reaching the alias table.
        var candidates = SpotSymbology.CandidateSpotNames("NIFTY");

        Assert.Equal("NIFTY", candidates[0]);
        Assert.Contains("NIFTY 50", candidates);
    }

    [Theory]
    [InlineData("BANKNIFTY", "NIFTY BANK")]
    [InlineData("FINNIFTY", "NIFTY FIN SERVICE")]
    [InlineData("MIDCPNIFTY", "NIFTY MID SELECT")]
    [InlineData("NIFTYNXT50", "NIFTY NEXT 50")]
    public void EveryTradedIndexHasItsNseDisplayNameAvailable(string underlying, string expectedAlias)
    {
        Assert.Contains(expectedAlias, SpotSymbology.CandidateSpotNames(underlying));
    }

    [Fact]
    public void IndexLookupIsCaseInsensitiveAndTrimmed()
    {
        // Underlying names arrive from three different broker dumps with no shared casing rule.
        Assert.Contains("NIFTY BANK", SpotSymbology.CandidateSpotNames(" banknifty "));
        Assert.True(SpotSymbology.IsIndex("nifty"));
    }

    [Fact]
    public void AStockIsNotTreatedAsAnIndex()
    {
        Assert.False(SpotSymbology.IsIndex("RELIANCE"));
    }

    [Fact]
    public void AnEmptyUnderlyingYieldsNoCandidatesRatherThanABlankMatch()
    {
        // Guards against a blank name matching a blank field somewhere in a broker dump and
        // silently pairing a spread with an arbitrary instrument.
        Assert.Empty(SpotSymbology.CandidateSpotNames(""));
        Assert.Empty(SpotSymbology.CandidateSpotNames("   "));
    }
}
