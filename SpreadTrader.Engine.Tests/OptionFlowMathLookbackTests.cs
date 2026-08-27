using SpreadTrader.Core.OptionsFlow;

namespace SpreadTrader.Engine.Tests;

public class OptionFlowMathLookbackTests
{
    [Fact]
    public void DoublingOverFiveBarsReports100Percent()
    {
        // base value 10 bars back from "now" is index [count-1-5]; wrap in leading padding so
        // the lookback index lands cleanly.
        var raw = new decimal[] { 0, 0, 0, 100m, 1, 1, 1, 200m };
        // last = 7, lookback 4 -> base = raw[3] = 100, current = raw[7] = 200 -> +100%
        var pct = OptionFlowMath.LookbackChangePercent(raw, 4);
        Assert.Equal(100m, pct);
    }

    [Fact]
    public void HalvingReportsNegative50Percent()
    {
        var raw = new decimal[] { 200m, 1, 1, 100m };
        var pct = OptionFlowMath.LookbackChangePercent(raw, 3);
        Assert.Equal(-50m, pct);
    }

    [Fact]
    public void SwingThroughZeroUsesAbsoluteBaseAsDenominator()
    {
        // base -50, current +50: (50 - (-50)) / abs(-50) * 100 = 200%
        var raw = new decimal[] { -50m, 0, 0, 50m };
        var pct = OptionFlowMath.LookbackChangePercent(raw, 3);
        Assert.Equal(200m, pct);
    }

    [Fact]
    public void ZeroBaseValueIsUndefinedNotInfinite()
    {
        var raw = new decimal[] { 0m, 5m, 10m, 15m };
        var pct = OptionFlowMath.LookbackChangePercent(raw, 3);
        Assert.Null(pct);
    }

    [Fact]
    public void NotEnoughHistoryForTheLookbackReturnsNull()
    {
        var raw = new decimal[] { 10m, 20m };
        var pct = OptionFlowMath.LookbackChangePercent(raw, 5);
        Assert.Null(pct);
    }

    [Fact]
    public void EmptySeriesReturnsNull()
    {
        Assert.Null(OptionFlowMath.LookbackChangePercent(Array.Empty<decimal>(), 5));
    }

    [Fact]
    public void ZeroOrNegativeLookbackReturnsNull()
    {
        var raw = new decimal[] { 10m, 20m, 30m };
        Assert.Null(OptionFlowMath.LookbackChangePercent(raw, 0));
        Assert.Null(OptionFlowMath.LookbackChangePercent(raw, -1));
    }

    [Fact]
    public void SmallMoveBelowHeadlineMagnitudeIsStillReportedAccurately()
    {
        // Not every caller wants the 100% alert threshold applied here - the math itself should
        // report the true % change regardless of what OptionsFlowOptions.LookbackAlertThresholdPercent
        // later filters on.
        var raw = new decimal[] { 100m, 0, 0, 110m };
        var pct = OptionFlowMath.LookbackChangePercent(raw, 3);
        Assert.Equal(10m, pct);
    }
}
