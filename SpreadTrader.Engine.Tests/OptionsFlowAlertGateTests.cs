using SpreadTrader.Core.OptionsFlow;

namespace SpreadTrader.Engine.Tests;

public class OptionsFlowAlertGateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 27, 9, 15, 0, TimeSpan.Zero);

    private static OptionFlowSignal CePeCross(DateTimeOffset? atUtc, int barsAgo = 0) =>
        new("CePeCross", "CDV", "CE/PE", true, atUtc, barsAgo, 10, 5);

    private static OptionFlowSignal PriceEmaCross(DateTimeOffset? atUtc) =>
        new("PriceEmaCross", "Price", "Index", true, atUtc, 0, 10, 5);

    private static OptionFlowSignal LookbackChange(DateTimeOffset? atUtc, int lookback = 5) =>
        new("LookbackChange", "OBV", "CE", true, atUtc, lookback, 20, 10, lookback, 150m);

    private static OptionFlowSignal InformationalEmaCross(DateTimeOffset? atUtc) =>
        // "EmaCross" on CDV/OBV is display-only - never alert-worthy, even with a fresh AtUtc.
        new("EmaCross", "CDV", "CE", true, atUtc, 0, 10, 5);

    [Fact]
    public void FirstCallEverNeverAlertsEvenWithFreshCrossings()
    {
        var gate = new OptionsFlowAlertGate();
        var signals = new[] { CePeCross(T0), PriceEmaCross(T0), LookbackChange(T0) };

        var result = gate.SelectNewAlerts(signals);

        Assert.Empty(result);
    }

    [Fact]
    public void ANewCrossAfterPrimingDoesAlert()
    {
        var gate = new OptionsFlowAlertGate();
        gate.SelectNewAlerts(new[] { CePeCross(T0) }); // primes

        var fresh = CePeCross(T0.AddMinutes(1));
        var result = gate.SelectNewAlerts(new[] { fresh });

        Assert.Single(result);
        Assert.Same(fresh, result[0]);
    }

    [Fact]
    public void TheSameCrossRepeatedNextCycleDoesNotAlertAgain()
    {
        var gate = new OptionsFlowAlertGate();
        gate.SelectNewAlerts(new[] { CePeCross(T0) }); // primes

        var fresh = CePeCross(T0.AddMinutes(1));
        gate.SelectNewAlerts(new[] { fresh }); // alerts once

        var repeated = gate.SelectNewAlerts(new[] { CePeCross(T0.AddMinutes(1)) });

        Assert.Empty(repeated);
    }

    [Fact]
    public void NoCrossTodaySignalsAreNeverAlertWorthy()
    {
        var gate = new OptionsFlowAlertGate();
        gate.SelectNewAlerts(new[] { CePeCross(null, barsAgo: -1) }); // primes on a "no cross" signal

        var result = gate.SelectNewAlerts(new[] { CePeCross(null, barsAgo: -1) });

        Assert.Empty(result);
    }

    [Fact]
    public void InformationalEmaCrossKindIsNeverAlertWorthy()
    {
        var gate = new OptionsFlowAlertGate();
        gate.SelectNewAlerts(new[] { InformationalEmaCross(T0) }); // primes

        var result = gate.SelectNewAlerts(new[] { InformationalEmaCross(T0.AddMinutes(1)) });

        Assert.Empty(result);
    }

    [Fact]
    public void DifferentSignalKeysAreTrackedIndependently()
    {
        var gate = new OptionsFlowAlertGate();
        gate.SelectNewAlerts(new[] { CePeCross(T0), PriceEmaCross(T0) }); // primes both

        // Only the CePeCross moves to a new bar - PriceEmaCross should not spuriously re-alert.
        var result = gate.SelectNewAlerts(new[] { CePeCross(T0.AddMinutes(1)), PriceEmaCross(T0) });

        var alert = Assert.Single(result);
        Assert.Equal("CePeCross", alert.Kind);
    }

    [Fact]
    public void LookbackChangeAlertsOnANewBarWhileStillPastThreshold()
    {
        var gate = new OptionsFlowAlertGate();
        gate.SelectNewAlerts(new[] { LookbackChange(T0) }); // primes

        // Condition is still true a minute later, on a new bar - that is a legitimate fresh alert,
        // not a repeat of the same event (unlike a crossing, "still past threshold" is meaningful).
        var result = gate.SelectNewAlerts(new[] { LookbackChange(T0.AddMinutes(1)) });

        Assert.Single(result);
    }

    [Fact]
    public void MultipleFreshAlertsInOneCycleAreAllReturned()
    {
        var gate = new OptionsFlowAlertGate();
        gate.SelectNewAlerts(new[] { CePeCross(T0), PriceEmaCross(T0) }); // primes

        var result = gate.SelectNewAlerts(new[]
        {
            CePeCross(T0.AddMinutes(1)),
            PriceEmaCross(T0.AddMinutes(1)),
        });

        Assert.Equal(2, result.Count);
    }
}
