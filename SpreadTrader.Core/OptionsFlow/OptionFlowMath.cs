namespace SpreadTrader.Core.OptionsFlow;

/// <summary>
/// The per-bar flow maths, ported deliberately verbatim from the reference TradingView Pine
/// script so the dashboard's numbers line up with what the same strikes show there.
///
/// Two things about this port are worth stating plainly, because both look like bugs otherwise:
///
///  1. THE DELTA IS A CRUDE APPROXIMATION, NOT BLACK-SCHOLES. It has no volatility, no time to
///     expiry and no rate - it is a logistic curve over moneyness alone, exactly as the Pine
///     script defines it. A real option delta would give different weights, especially near
///     expiry. It is kept as-is on purpose: the point is to reproduce the reference indicator, not
///     to improve on it. Swapping in a proper pricing model would silently change every plotted
///     value relative to the chart this was built to match.
///
///  2. CDV'S BUY/SELL SPLIT IS AN ESTIMATE FROM THE BAR SHAPE. Neither broker's feed says which
///     side of the book a trade hit, so the split is inferred from where the bar closed inside its
///     own range: a bar closing at its high is treated as all buying, at its low as all selling.
///     This is the standard approximation for volume delta without tick-by-tick trade direction.
/// </summary>
public static class OptionFlowMath
{
    /// <summary>
    /// Approximate option delta from moneyness alone. Returns a positive value for calls and a
    /// negative one for puts, both bounded to (-1, 1) - the sign convention of the source script.
    /// Callers weight by the ABSOLUTE value (see <see cref="DeltaWeight"/>); the sign is retained
    /// here only to stay faithful to the original.
    /// </summary>
    public static double ApproximateDelta(decimal strike, bool isCall, decimal spot)
    {
        if (spot <= 0) return isCall ? 0.5 : -0.5;

        var moneyness = (double)((strike - spot) / spot);

        return isCall
            ? 1.0 / (1.0 + Math.Exp(-1.7 * -moneyness * 5.0))
            : -1.0 / (1.0 + Math.Exp(-1.7 * moneyness * 5.0));
    }

    /// <summary>The multiplier applied to a bar's volume before it is summed across strikes. 1.0
    /// when delta weighting is off, matching the reference script's toggle.</summary>
    public static decimal DeltaWeight(decimal strike, bool isCall, decimal spot, bool useDeltaWeighting) =>
        useDeltaWeighting ? (decimal)Math.Abs(ApproximateDelta(strike, isCall, spot)) : 1m;

    /// <summary>
    /// One bar's volume delta: estimated buy volume minus estimated sell volume, split by where the
    /// close sits within the bar's range, then delta-weighted.
    ///
    /// A zero-range bar (every trade at one price, common on an illiquid far strike) contributes
    /// nothing rather than being forced to one side - the source script's <c>hl &gt; 0.0001</c>
    /// guard, kept because dividing by a near-zero range would otherwise amplify noise into a
    /// spike large enough to dominate the whole aggregate.
    /// </summary>
    public static decimal BarVolumeDelta(in OptionBar bar, decimal weight)
    {
        var range = bar.High - bar.Low;
        if (range <= 0.0001m || bar.Volume <= 0) return 0m;

        var buyVolume = (bar.Close - bar.Low) / range * bar.Volume;
        var sellVolume = (bar.High - bar.Close) / range * bar.Volume;

        return (buyVolume - sellVolume) * weight;
    }

    /// <summary>
    /// One bar's OBV contribution: the whole bar volume signed by the direction of the close
    /// against the previous bar's close, then delta-weighted. An unchanged close contributes
    /// nothing, per the standard definition and the source script's <c>math.sign</c>.
    /// </summary>
    public static decimal BarObv(in OptionBar bar, decimal previousClose, decimal weight)
    {
        if (bar.Volume <= 0) return 0m;

        var direction = Math.Sign(bar.Close - previousClose);
        return direction == 0 ? 0m : direction * bar.Volume * weight;
    }

    /// <summary>
    /// Exponential moving average over a series, matching Pine's <c>ta.ema</c>: seeded with the
    /// first value (not with an SMA of the first n) so a short series still produces a curve from
    /// its very first point rather than a run of blanks.
    /// </summary>
    public static decimal[] Ema(IReadOnlyList<decimal> values, int length)
    {
        var result = new decimal[values.Count];
        if (values.Count == 0) return result;

        var alpha = 2m / (Math.Max(1, length) + 1);
        result[0] = values[0];

        for (var i = 1; i < values.Count; i++)
            result[i] = alpha * values[i] + (1 - alpha) * result[i - 1];

        return result;
    }

    /// <summary>
    /// % change from <paramref name="lookback"/> bars ago to the latest value in
    /// <paramref name="raw"/>, for the "N-bar % change" alert (the reference Pine scripts surface
    /// this as a 5/10/15-bar readout on the raw cumulative CDV/OBV series).
    ///
    /// The denominator is ABS(base) rather than base itself, so a series that swings through zero
    /// (a real possibility for CDV/OBV, unlike price) doesn't flip the sign of the result.
    ///
    /// Returns null - not a huge or infinite number - when there isn't enough history for the
    /// requested lookback yet, or the base value is exactly zero: a % change from a zero baseline
    /// is undefined, and reporting it as some enormous swing would be misleading rather than
    /// informative.
    /// </summary>
    public static decimal? LookbackChangePercent(IReadOnlyList<decimal> raw, int lookback)
    {
        if (lookback <= 0 || raw.Count == 0) return null;

        var last = raw.Count - 1;
        if (last - lookback < 0) return null;

        var baseValue = raw[last - lookback];
        if (baseValue == 0) return null;

        return (raw[last] - baseValue) / Math.Abs(baseValue) * 100m;
    }
}
