using SpreadTrader.Engine.Services;

namespace SpreadTrader.Engine.Tests;

/// <summary>
/// Covers the two pure decisions that determine whether a partially-filled basket can leave the
/// account holding an UNHEDGED position:
///
///   - <see cref="OrderDispatchHostedService.ComputeLegTargets"/> - how far each leg is chased as
///     the basket is filled in (the always-fill policy), and
///   - <see cref="OrderDispatchHostedService.ComputeExcessQuantities"/> - how much of each leg
///     would be unwound, used by the opt-in square-off fallback and by the Critical alert that
///     reports anything the retries could not fill.
///
/// Two properties are asserted, one per decision:
///
///   - <see cref="ShortIsNeverChasedBeyondTheHedge_ForAnyFillCombination"/>: while filling, the
///     short's target never exceeds the hedge that actually exists, so the account is never
///     transiently naked on the way to a complete basket.
///   - <see cref="EveryFillCombination_EndsUpBalanced"/>: the excess calculation always resolves to
///     equal quantities across the legs, and never gives back a matched lot.
///
/// The regression these guard is a real one that shipped: a hedge leg filling 1 of 2 lots counted
/// as "hedge in place" (OrderResult.Success only means at least one lot filled), which released a
/// FULL-size short against it, and the old cleanup only recognised the all-or-nothing case - so the
/// leftover naked lot was silently kept.
/// </summary>
public class PartialFillBalancingTests
{
    // ---- ComputeExcessQuantities: what has to be unwound -------------------------------------

    [Fact]
    public void BothLegsFullyFilled_NothingIsUnwound()
    {
        Assert.Equal(new[] { 0, 0 }, OrderDispatchHostedService.ComputeExcessQuantities(new[] { 2, 2 }));
    }

    [Fact]
    public void BothLegsEquallyPartiallyFilled_KeptAsASmallerHedgedSpread()
    {
        // 1 lot each is a genuine (smaller) hedged spread - unwinding it would be strictly worse.
        Assert.Equal(new[] { 0, 0 }, OrderDispatchHostedService.ComputeExcessQuantities(new[] { 1, 1 }));
    }

    [Fact]
    public void OneLegFilledOtherRejected_EntireFilledLegIsUnwound()
    {
        // The all-or-nothing case the original implementation handled - preserved exactly.
        Assert.Equal(new[] { 2, 0 }, OrderDispatchHostedService.ComputeExcessQuantities(new[] { 2, 0 }));
        Assert.Equal(new[] { 0, 2 }, OrderDispatchHostedService.ComputeExcessQuantities(new[] { 0, 2 }));
    }

    [Fact]
    public void PartialHedgeFill_OnlyTheUnmatchedLotIsUnwound()
    {
        // THE REGRESSION: legs at 2 and 1 used to be treated as "everything filled, nothing to do",
        // silently leaving 1 lot naked. Now exactly that 1 lot is given back, keeping the matched 1.
        Assert.Equal(new[] { 1, 0 }, OrderDispatchHostedService.ComputeExcessQuantities(new[] { 2, 1 }));
        Assert.Equal(new[] { 0, 1 }, OrderDispatchHostedService.ComputeExcessQuantities(new[] { 1, 2 }));
    }

    [Fact]
    public void NothingFilled_NothingToUnwind()
    {
        Assert.Equal(new[] { 0, 0 }, OrderDispatchHostedService.ComputeExcessQuantities(new[] { 0, 0 }));
    }

    [Fact]
    public void SingleLegRule_IsNeverUnwound()
    {
        // A single-leg rule has no partner to be unbalanced against - a partial fill there is a
        // legitimate smaller position, not an unhedged one.
        Assert.Equal(new[] { 0 }, OrderDispatchHostedService.ComputeExcessQuantities(new[] { 1 }));
    }

    // ---- ComputeLegTargets: how far each leg is chased (always-fill policy) -------------------

    private static readonly bool[] BuyThenSell = { false, true };

    [Fact]
    public void HedgeLeg_IsAlwaysChasedToTheFullRequestedQuantity()
    {
        // Always-fill: the aim is to COMPLETE the spread the rule asked for, never to settle for a
        // smaller one, so the hedge is chased to full size regardless of what the short has done.
        var targets = OrderDispatchHostedService.ComputeLegTargets(new[] { 2, 2 }, new[] { 1, 0 }, BuyThenSell);
        Assert.Equal(2, targets[0]);
    }

    [Fact]
    public void ShortLeg_IsCappedAtWhatTheHedgeCurrentlyHolds()
    {
        // Hedge has only 1 of 2 filled so far, so the short may be topped up to 1 - never to 2,
        // which would overshoot the long protecting it mid-fill.
        var targets = OrderDispatchHostedService.ComputeLegTargets(new[] { 2, 2 }, new[] { 1, 0 }, BuyThenSell);
        Assert.Equal(1, targets[1]);
    }

    [Fact]
    public void ShortLegCapRisesAsTheHedgeFillsIn()
    {
        // Once the hedge reaches full size the cap lifts and the short is chased to full size too -
        // this is what lets successive retry rounds converge on a complete, matched basket.
        var targets = OrderDispatchHostedService.ComputeLegTargets(new[] { 2, 2 }, new[] { 2, 1 }, BuyThenSell);
        Assert.Equal(new[] { 2, 2 }, targets);
    }

    [Fact]
    public void UnfilledHedge_LeavesTheShortCappedAtZero()
    {
        // No hedge at all means no short may be placed - the cap is 0, so nothing is chased.
        var targets = OrderDispatchHostedService.ComputeLegTargets(new[] { 2, 2 }, new[] { 0, 0 }, BuyThenSell);
        Assert.Equal(0, targets[1]);
    }

    [Fact]
    public void SellOnlyRule_HasNoHedgeToCapAgainstAndGoesToFullSize()
    {
        var targets = OrderDispatchHostedService.ComputeLegTargets(new[] { 2 }, new[] { 0 }, new[] { true });
        Assert.Equal(new[] { 2 }, targets);
    }

    [Fact]
    public void SingleBuyLeg_IsChasedToItsFullRequestedQuantity()
    {
        var targets = OrderDispatchHostedService.ComputeLegTargets(new[] { 2 }, new[] { 1 }, new[] { false });
        Assert.Equal(new[] { 2 }, targets);
    }

    [Fact]
    public void ShortIsNeverChasedBeyondTheHedge_ForAnyFillCombination()
    {
        // The safety property of the always-fill policy: at no point during filling may the short's
        // target exceed the hedge that actually exists, so the account is never transiently naked.
        for (var hedgeFilled = 0; hedgeFilled <= 5; hedgeFilled++)
        {
            for (var shortFilled = 0; shortFilled <= 5; shortFilled++)
            {
                var targets = OrderDispatchHostedService.ComputeLegTargets(
                    new[] { 5, 5 }, new[] { hedgeFilled, shortFilled }, BuyThenSell);

                Assert.Equal(5, targets[0]); // hedge always chased to full size
                Assert.True(targets[1] <= hedgeFilled,
                    $"short target {targets[1]} exceeded the hedge's {hedgeFilled} filled lot(s)");
            }
        }
    }

    // ---- The invariant itself -----------------------------------------------------------------

    [Fact]
    public void EveryFillCombination_EndsUpBalanced()
    {
        // Exhaustive over every way a 2-leg basket can fill, including the lopsided ones. After the
        // computed excess is unwound, the legs must hold identical quantities - a hedged spread or
        // nothing, never a naked remainder.
        for (var legA = 0; legA <= 5; legA++)
        {
            for (var legB = 0; legB <= 5; legB++)
            {
                var filled = new[] { legA, legB };
                var excess = OrderDispatchHostedService.ComputeExcessQuantities(filled);

                var afterA = filled[0] - excess[0];
                var afterB = filled[1] - excess[1];

                Assert.Equal(afterA, afterB);
                Assert.True(excess[0] >= 0 && excess[1] >= 0, $"excess must never be negative (would mean BUYING more to 'unwind'): {legA}/{legB}");
                Assert.True(afterA >= 0, $"cannot unwind more than was filled: {legA}/{legB}");
                // Never unwind a matched lot - the retained quantity must be the full matched amount.
                Assert.Equal(Math.Min(legA, legB), afterA);
            }
        }
    }

}
