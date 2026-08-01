using SpreadTrader.Engine.Services;

namespace SpreadTrader.Engine.Tests;

/// <summary>
/// Covers the two pure decisions that determine whether a partially-filled basket can leave the
/// account holding an UNHEDGED position:
///
///   - <see cref="OrderDispatchHostedService.ComputeRetryTargets"/> - how far each leg is chased
///     on retry, and
///   - <see cref="OrderDispatchHostedService.ComputeExcessQuantities"/> - how much of each leg is
///     unwound afterwards so the basket ends up matched.
///
/// The invariant these exist to guarantee, asserted directly in
/// <see cref="EveryFillCombination_EndsUpBalanced"/>: after the excess is unwound, every leg of a
/// multi-leg basket holds the SAME quantity - either a real hedged spread or nothing at all, never
/// a lopsided position.
///
/// The regression these guard is a real one that shipped: a hedge leg filling 1 of 2 lots counted
/// as "hedge in place" (OrderResult.Success only means at least one lot filled), which released a
/// FULL-size short against it, and the old cleanup only recognised the all-or-nothing case - so the
/// leftover naked lot was silently kept. See PartialHedgeFill_* below.
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

    // ---- ComputeRetryTargets: how far each leg is chased ---------------------------------------

    [Fact]
    public void SingleLeg_IsChasedToItsFullRequestedQuantity()
    {
        Assert.Equal(new[] { 2 }, OrderDispatchHostedService.ComputeRetryTargets(new[] { 2 }, new[] { 0 }));
        Assert.Equal(new[] { 2 }, OrderDispatchHostedService.ComputeRetryTargets(new[] { 2 }, new[] { 1 }));
    }

    [Fact]
    public void RejectedLeg_IsChasedUpToItsFilledPartner()
    {
        // Partner got the full 2, so the rejected leg is chased to 2 - completing the spread is
        // better than unwinding the leg that did fill.
        Assert.Equal(new[] { 2, 2 }, OrderDispatchHostedService.ComputeRetryTargets(new[] { 2, 2 }, new[] { 2, 0 }));
    }

    [Fact]
    public void PartiallyFilledPair_SettlesAtTheMatchedQuantityInsteadOfChasingFullSize()
    {
        // Both at 1 of 2: already balanced. Chasing either back to 2 would just re-open an
        // imbalance, so both targets are the matched 1 and no retry is triggered.
        Assert.Equal(new[] { 1, 1 }, OrderDispatchHostedService.ComputeRetryTargets(new[] { 2, 2 }, new[] { 1, 1 }));
    }

    [Fact]
    public void PartialHedgeWithRejectedPartner_ChasesOnlyToTheHedgedQuantity()
    {
        // Hedge got 1 of 2, partner got nothing: chase the partner to 1 (matching the hedge), NOT
        // to 2 - which is exactly what would strand a naked lot.
        Assert.Equal(new[] { 1, 1 }, OrderDispatchHostedService.ComputeRetryTargets(new[] { 2, 2 }, new[] { 1, 0 }));
    }

    [Fact]
    public void NothingFilledAnywhere_NothingIsChased()
    {
        Assert.Equal(new[] { 0, 0 }, OrderDispatchHostedService.ComputeRetryTargets(new[] { 2, 2 }, new[] { 0, 0 }));
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

    [Fact]
    public void RetryTargetsNeverExceedRequestedQuantity()
    {
        // A retry must never chase a leg beyond what the rule actually asked for, whatever the
        // partner did - otherwise a retry could overfill the basket past the user's intended size.
        for (var legA = 0; legA <= 5; legA++)
        {
            for (var legB = 0; legB <= 5; legB++)
            {
                var targets = OrderDispatchHostedService.ComputeRetryTargets(new[] { 3, 3 }, new[] { legA, legB });
                Assert.All(targets, t => Assert.True(t <= 3, $"target {t} exceeded the requested 3 for fills {legA}/{legB}"));
            }
        }
    }
}
