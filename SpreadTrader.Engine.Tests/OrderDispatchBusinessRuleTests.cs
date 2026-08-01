using SpreadTrader.Core.Models;

namespace SpreadTrader.Engine.Tests;

/// <summary>
/// Characterization tests for OrderDispatchHostedService: they assert the BUSINESS RULES this
/// trading system relies on, driving the real service end to end through DispatchHarness.
///
/// Their job is to be the safety net for refactoring. Anything that changes an outcome asserted
/// here is a behaviour change, not a refactor - which is exactly the distinction that is otherwise
/// impossible to make by eye in an ~850-line orchestration class.
/// </summary>
public class OrderDispatchBusinessRuleTests
{
    // ---- Hedge-first sequencing ---------------------------------------------------------------

    [Fact]
    public async Task BuyLegIsAlwaysPlacedBeforeSellLeg()
    {
        // The short must be evaluated by the broker WITH the hedge benefit already in place,
        // otherwise it can be rejected for full naked-short margin.
        var h = new DispatchHarness();
        h.SeedCalendarSpread();

        await h.DispatchAsync();

        Assert.Equal("BUY", h.Gateway.Placed[0].TransactionType);
        Assert.Equal("SELL", h.Gateway.Placed[1].TransactionType);
    }

    [Fact]
    public async Task SellLegIsNeverPlacedWhenTheHedgeDidNotFill()
    {
        // Placing the short with no hedge behind it is precisely the naked-short case hedge-first
        // ordering exists to prevent.
        var h = new DispatchHarness();
        h.SeedCalendarSpread();
        h.Gateway.Script("TEST26AUGFUT", new OrderResult { Success = false, FilledQuantity = 0, Status = "REJECTED", ErrorMessage = "rejected" });

        var result = await h.DispatchAsync();

        Assert.DoesNotContain(h.Gateway.Placed, p => p.TransactionType == "SELL");
        Assert.Contains(result.Legs, l => l.Status == "SKIPPED");
    }

    [Fact]
    public async Task ShortIsSizedDownToAPartiallyFilledHedge()
    {
        // Hedge fills 1 of 2 -> the short may only be 1, never the full 2, or the difference is
        // naked. This is the regression that shipped.
        var h = new DispatchHarness();
        h.SeedCalendarSpread(quantity: 2);
        h.Gateway.Script("TEST26AUGFUT", new OrderResult { Success = true, OrderId = "b1", FilledQuantity = 1, Status = "COMPLETE" });

        await h.DispatchAsync();

        var firstSell = h.Gateway.Placed.First(p => p.TransactionType == "SELL");
        Assert.Equal(1, firstSell.Quantity);
    }

    // ---- Always-fill retry --------------------------------------------------------------------

    [Fact]
    public async Task PartiallyFilledLegIsRetriedForOnlyTheOutstandingQuantity()
    {
        var h = new DispatchHarness();
        h.SeedCalendarSpread(quantity: 2);
        // Hedge takes two goes to complete; the short fills whatever it is asked for.
        h.Gateway.Script("TEST26AUGFUT",
            new OrderResult { Success = true, OrderId = "b1", FilledQuantity = 1, Status = "COMPLETE" },
            new OrderResult { Success = true, OrderId = "b2", FilledQuantity = 1, Status = "COMPLETE" });

        var result = await h.DispatchAsync();

        var buyRetry = h.Gateway.Placed.Where(p => p.TransactionType == "BUY").Skip(1).First();
        Assert.Equal(1, buyRetry.Quantity); // the outstanding 1, not the full 2 again

        var buyLeg = result.Legs.First(l => l.TransactionType == "BUY" && !l.IsSquareOff);
        Assert.Equal(2, buyLeg.FilledQuantity);  // fills accumulate across attempts
        Assert.Equal(2, buyLeg.Quantity);        // still reported against the original requested size
    }

    [Fact]
    public async Task BasketIsDrivenToFullSizeAndEndsMatched()
    {
        // The always-fill promise: both legs finish at the requested quantity.
        var h = new DispatchHarness();
        h.SeedCalendarSpread(quantity: 2);
        h.Gateway.Script("TEST26AUGFUT",
            new OrderResult { Success = true, OrderId = "b1", FilledQuantity = 1, Status = "COMPLETE" },
            new OrderResult { Success = true, OrderId = "b2", FilledQuantity = 1, Status = "COMPLETE" });
        h.Gateway.Script("TEST26SEPFUT",
            new OrderResult { Success = true, OrderId = "s1", FilledQuantity = 1, Status = "COMPLETE" },
            new OrderResult { Success = true, OrderId = "s2", FilledQuantity = 1, Status = "COMPLETE" });

        var result = await h.DispatchAsync();

        var buy = result.Legs.First(l => l.TransactionType == "BUY" && !l.IsSquareOff);
        var sell = result.Legs.First(l => l.TransactionType == "SELL" && !l.IsSquareOff);
        Assert.Equal(2, buy.FilledQuantity);
        Assert.Equal(2, sell.FilledQuantity);
    }

    [Fact]
    public async Task NothingIsUnwoundUnderTheAlwaysFillPolicy()
    {
        // AutoSquareOffOnPartialFill defaults to false: a leg that cannot be filled is left live,
        // never flattened. No square-off order may be placed.
        var h = new DispatchHarness();
        h.SeedCalendarSpread(quantity: 2);
        h.Gateway.Script("TEST26SEPFUT",
            new OrderResult { Success = false, FilledQuantity = 0, Status = "REJECTED", ErrorMessage = "no fill" },
            new OrderResult { Success = false, FilledQuantity = 0, Status = "REJECTED", ErrorMessage = "no fill" },
            new OrderResult { Success = false, FilledQuantity = 0, Status = "REJECTED", ErrorMessage = "no fill" },
            new OrderResult { Success = false, FilledQuantity = 0, Status = "REJECTED", ErrorMessage = "no fill" },
            new OrderResult { Success = false, FilledQuantity = 0, Status = "REJECTED", ErrorMessage = "no fill" });

        var result = await h.DispatchAsync();

        Assert.DoesNotContain(result.Legs, l => l.IsSquareOff);
    }

    [Fact]
    public async Task SquareOffStillUnwindsWhenExplicitlyOptedIn()
    {
        // The opt-in fallback must still work for anyone who would rather be flat than exposed.
        var h = new DispatchHarness();
        h.Options.AutoSquareOffOnPartialFill = true;
        h.Options.RetryUnfilledLegOnPartialFill = false; // isolate the square-off decision
        h.SeedCalendarSpread(quantity: 2);
        h.Gateway.Script("TEST26SEPFUT", new OrderResult { Success = false, FilledQuantity = 0, Status = "REJECTED", ErrorMessage = "no fill" });

        var result = await h.DispatchAsync();

        var squareOff = result.Legs.Single(l => l.IsSquareOff);
        Assert.Equal("SELL", squareOff.TransactionType); // unwinds the filled BUY
        Assert.Equal(2, squareOff.Quantity);
    }

    // ---- Margin gating ------------------------------------------------------------------------

    [Fact]
    public async Task InsufficientMarginBlocksTheEntireBasket()
    {
        // Placing only some legs because margin ran out mid-basket is the exact naked-position
        // scenario the pre-trade check exists to prevent - so nothing may be sent at all.
        var h = new DispatchHarness();
        h.Options.VerifyMarginBeforeDispatch = true;
        h.Margin.BasketRequired = 100_000m;
        h.Margin.Available = 1_000m;
        h.SeedCalendarSpread();

        var result = await h.DispatchAsync();

        Assert.Empty(h.Gateway.Placed);
        Assert.True(result.MarginBlocked);
        Assert.Contains("Insufficient margin", result.BlockedReason);
    }

    [Fact]
    public async Task AFailedMarginCheckDoesNotBlockDispatch()
    {
        // A flaky diagnostic call must not stop trading - the broker still enforces margin itself.
        var h = new DispatchHarness();
        h.Options.VerifyMarginBeforeDispatch = true;
        h.Margin.BasketCheckSucceeds = false;
        h.SeedCalendarSpread();

        var result = await h.DispatchAsync();

        Assert.NotEmpty(h.Gateway.Placed);
        Assert.False(result.MarginBlocked);
    }

    // ---- Rule bookkeeping ---------------------------------------------------------------------

    [Fact]
    public async Task RuleIsReArmedWhenTheAttemptFilledNothing()
    {
        // A fire that placed nothing must not cost the rule its shot, or a margin blip silently
        // consumes a one-shot trigger.
        var h = new DispatchHarness();
        h.Options.VerifyMarginBeforeDispatch = true;
        h.Margin.BasketRequired = 100_000m;
        h.Margin.Available = 1_000m;
        h.SeedCalendarSpread(oneShot: true);

        await h.DispatchAsync();

        var rule = h.Rules.Get(DispatchHarness.RuleId)!;
        Assert.True(rule.IsArmed);
        Assert.True(rule.Enabled);
        Assert.Equal(1, rule.UnfilledAttempts);
    }

    [Fact]
    public async Task RuleIsNotReArmedOnceSomethingActuallyFilled()
    {
        // Real position was opened, so the fire legitimately counts and the shot is consumed.
        var h = new DispatchHarness();
        h.SeedCalendarSpread(oneShot: true);

        await h.DispatchAsync();

        var rule = h.Rules.Get(DispatchHarness.RuleId)!;
        Assert.False(rule.IsArmed);
    }

    [Fact]
    public async Task RuleAutoDisarmsAfterTooManyConsecutiveEmptyFires()
    {
        // A condition that can never fill must stop retrying forever and wait for a human.
        var h = new DispatchHarness();
        h.Options.VerifyMarginBeforeDispatch = true;
        h.Options.MaxUnfilledDispatchAttempts = 3;
        h.Margin.BasketRequired = 100_000m;
        h.Margin.Available = 1_000m;
        var rule = h.SeedCalendarSpread();
        rule.UnfilledAttempts = 2; // one away from the cap

        await h.DispatchAsync();

        var after = h.Rules.Get(DispatchHarness.RuleId)!;
        Assert.False(after.IsArmed);
        Assert.False(after.Enabled);
    }

    [Fact]
    public async Task ExitTriggersResolveAgainstTheExitRuleStore()
    {
        // Entry and exit rules live in physically separate stores; dispatching an exit trigger
        // against the entry store would silently do nothing.
        var h = new DispatchHarness();
        var rule = h.SeedCalendarSpread();
        h.Rules.Remove(rule.Id);
        h.ExitRules.Upsert(rule);

        var result = await h.DispatchAsync(isExitTrigger: true);

        Assert.False(result.MarginBlocked);
        Assert.NotEmpty(h.Gateway.Placed);
    }

    // ---- Stray order hygiene ------------------------------------------------------------------

    [Fact]
    public async Task UnconfirmedOrderIsCancelledRatherThanLeftResting()
    {
        // An order stuck at OPEN could still fill later and create an untracked position.
        var h = new DispatchHarness();
        h.SeedCalendarSpread();
        h.Gateway.Script("TEST26AUGFUT", new OrderResult { Success = false, OrderId = "stuck-1", FilledQuantity = 0, Status = "OPEN" });

        await h.DispatchAsync();

        Assert.Contains("stuck-1", h.Gateway.Cancelled);
    }

    // ---- Order shape --------------------------------------------------------------------------

    [Fact]
    public async Task MarketLegsAreSentAsProtectedLimitOrders()
    {
        // Kite rejects raw MARKET orders for F&O outright, so every leg must carry a real price.
        var h = new DispatchHarness();
        h.SeedCalendarSpread();

        await h.DispatchAsync();

        Assert.All(h.Gateway.Placed, p => Assert.NotNull(p.Price));

        var buy = h.Gateway.Placed.First(p => p.TransactionType == "BUY");
        var sell = h.Gateway.Placed.First(p => p.TransactionType == "SELL");
        Assert.True(buy.Price > 101m, "a BUY must be priced through the best ask");
        Assert.True(sell.Price < 99m, "a SELL must be priced through the best bid");
    }

    [Fact]
    public async Task ADeletedRuleIsReportedAsBlockedAndPlacesNothing()
    {
        var h = new DispatchHarness();
        var rule = h.SeedCalendarSpread();
        h.Rules.Remove(rule.Id);

        var result = await h.DispatchAsync();

        Assert.Empty(h.Gateway.Placed);
        Assert.True(result.MarginBlocked);
    }
}
