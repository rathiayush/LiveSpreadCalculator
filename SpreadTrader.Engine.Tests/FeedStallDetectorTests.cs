using SpreadTrader.Core.Brokers;

namespace SpreadTrader.Engine.Tests;

/// <summary>
/// Pins what a live market-data socket does when it goes quiet.
///
/// Both directions of getting this wrong are costly, which is why it is tested rather than
/// eyeballed:
///  - too eager, and a healthy but quiet connection is torn down and the whole universe
///    re-subscribed, potentially in a loop all night against the broker's rate limits;
///  - too reluctant, and a connection sits there forever delivering nothing while the app looks
///    perfectly healthy - connected, authenticated, no errors, prices frozen. That is the failure
///    that actually shipped, and the reason a re-subscribe now happens before any reconnect.
/// </summary>
public class FeedStallDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan Initial = TimeSpan.FromSeconds(30);

    private static FeedRecoveryAction Decide(FeedStallDetector d, DateTimeOffset now, bool subscribed = true) =>
        d.Decide(now, Quiet, Initial, subscribed);

    private static FeedStallDetector Connected()
    {
        var d = new FeedStallDetector();
        d.OnConnected(T0);
        return d;
    }

    // ---- A connection that never delivers ------------------------------------------------------

    [Fact]
    public void AConnectionThatNeverDeliversIsResubscribedNotReconnected()
    {
        // THE bug this fixes. A socket whose subscription never took effect - on Dhan, because
        // authorisation completes asynchronously after connect - sat connected and silent forever,
        // because "never delivered" was deliberately unjudgeable. Re-subscribing repairs exactly
        // that, and unlike a reconnect it opens no new socket.
        var d = Connected();

        Assert.Equal(FeedRecoveryAction.Resubscribe, Decide(d, T0.Add(Initial).AddSeconds(1)));
    }

    [Fact]
    public void ItNeverEscalatesToAReconnectWhileNothingHasEverArrived()
    {
        // The off-hours guard, preserved. Overnight a healthy feed delivers nothing all night, and
        // reconnecting on a loop until morning - re-subscribing the whole universe each time, into
        // the very connection limits that get an account blocked - would be far worse than the
        // problem. So this case re-subscribes indefinitely and never tears anything down.
        var d = Connected();
        var now = T0;

        for (var i = 0; i < 40; i++)
        {
            now = now.AddSeconds(200);
            Assert.NotEqual(FeedRecoveryAction.Reconnect, Decide(d, now));
        }
    }

    [Fact]
    public void TheFirstAttemptIsEagerAndLaterOnesAreNot()
    {
        // Fast recovery when a subscription was simply missed, without chattering all night.
        var d = Connected();

        Assert.Equal(FeedRecoveryAction.None, Decide(d, T0.AddSeconds(29)));
        Assert.Equal(FeedRecoveryAction.Resubscribe, Decide(d, T0.AddSeconds(31)));

        // Second attempt waits the full quiet window, not another 30s.
        Assert.Equal(FeedRecoveryAction.None, Decide(d, T0.AddSeconds(31 + 119)));
        Assert.Equal(FeedRecoveryAction.Resubscribe, Decide(d, T0.AddSeconds(31 + 121)));
    }

    // ---- A connection that delivered and then stopped ------------------------------------------

    [Fact]
    public void SilenceAfterDataHasFlowedResubscribesFirstThenReconnects()
    {
        // A re-subscribe is tried first because it is cheap; if the feed is still silent after it
        // has had a full window to take effect, the connection itself is the problem.
        var d = Connected();
        d.OnMessage(T0);

        Assert.Equal(FeedRecoveryAction.Resubscribe, Decide(d, T0.Add(Quiet).AddSeconds(1)));
        Assert.Equal(FeedRecoveryAction.Reconnect, Decide(d, T0.Add(Quiet).Add(Quiet).AddSeconds(2)));
    }

    [Fact]
    public void DataResumingCancelsTheEscalation()
    {
        // If the re-subscribe worked, nothing should be torn down.
        var d = Connected();
        d.OnMessage(T0);
        Assert.Equal(FeedRecoveryAction.Resubscribe, Decide(d, T0.Add(Quiet).AddSeconds(1)));

        d.OnMessage(T0.Add(Quiet).AddSeconds(5));

        Assert.Equal(FeedRecoveryAction.None, Decide(d, T0.Add(Quiet).AddSeconds(10)));
    }

    [Fact]
    public void AGapShorterThanTheTimeoutIsNotActedOn()
    {
        // An illiquid contract going quiet for a minute mid-session must not cost anything.
        var d = Connected();
        d.OnMessage(T0);

        Assert.Equal(FeedRecoveryAction.None, Decide(d, T0.AddSeconds(119)));
    }

    [Fact]
    public void EveryMessageResetsTheClock()
    {
        var d = Connected();
        var now = T0;

        for (var i = 0; i < 50; i++)
        {
            now = now.AddSeconds(60);
            d.OnMessage(now);
            Assert.Equal(FeedRecoveryAction.None, Decide(d, now.AddSeconds(30)));
        }
    }

    // ---- Guards --------------------------------------------------------------------------------

    [Fact]
    public void NothingHappensWhenNothingIsSubscribed()
    {
        // With no subscriptions there is no data to expect, so silence means nothing at all.
        var d = Connected();

        Assert.Equal(FeedRecoveryAction.None, Decide(d, T0.AddHours(9), subscribed: false));
    }

    [Fact]
    public void ReconnectingClearsTheEvidenceFromThePreviousConnection()
    {
        // Without this a fresh socket would inherit the old state and be judged immediately.
        var d = Connected();
        d.OnMessage(T0);
        Assert.Equal(FeedRecoveryAction.Resubscribe, Decide(d, T0.Add(Quiet).AddSeconds(1)));

        d.OnConnected(T0.AddHours(1));

        Assert.Equal(FeedRecoveryAction.None, Decide(d, T0.AddHours(1).AddSeconds(10)));
    }

    [Fact]
    public void TheFirstMessageIsReportedOnlyOnce()
    {
        // The caller logs on this, and one line per connection is the point - not one per tick.
        var d = Connected();

        Assert.True(d.OnMessage(T0));
        Assert.False(d.OnMessage(T0.AddSeconds(1)));
        Assert.False(d.OnMessage(T0.AddSeconds(2)));

        d.OnConnected(T0.AddMinutes(5));
        Assert.True(d.OnMessage(T0.AddMinutes(6)));
    }

    // ---- Reconnect backoff after a disconnect --------------------------------------------------

    [Fact]
    public void AConnectionThatHeldResetsTheBackoffToTheFloor()
    {
        // The feed ran fine for an hour and then dropped - that is a blip on a healthy endpoint,
        // so the next attempt should be immediate rather than inheriting an old penalty.
        var next = DhanBackoff(TimeSpan.FromSeconds(16), uptime: TimeSpan.FromHours(1));

        Assert.Equal(TimeSpan.FromSeconds(1), next);
    }

    [Fact]
    public void AConnectionThatDroppedImmediatelyEscalatesInstead()
    {
        // Connect, closed straight away, repeat. Resetting here would reconnect every second
        // forever - hammering the broker and inviting a rate-limit block that turns a blip into a
        // sustained outage.
        var next = DhanBackoff(TimeSpan.FromSeconds(2), uptime: TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.FromSeconds(4), next);
    }

    [Fact]
    public void EscalationIsCappedSoAFlapNeverBacksOffForever()
    {
        var next = DhanBackoff(TimeSpan.FromSeconds(30), uptime: TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.FromSeconds(30), next);
    }

    [Fact]
    public void RepeatedFlappingClimbsToTheCapAndStops()
    {
        // The whole point is that a connect/close loop cannot busy-wait. Walk it and check it
        // settles at the ceiling rather than oscillating back to the floor.
        var backoff = TimeSpan.FromSeconds(1);
        for (var i = 0; i < 12; i++)
            backoff = DhanBackoff(backoff, uptime: TimeSpan.FromSeconds(2));

        Assert.Equal(TimeSpan.FromSeconds(30), backoff);

        // ...and one healthy connection is enough to release it again.
        Assert.Equal(TimeSpan.FromSeconds(1), DhanBackoff(backoff, uptime: TimeSpan.FromMinutes(5)));
    }

    // ---- Dhan disconnect-code cooldowns --------------------------------------------------------

    [Fact]
    public void TooManyConnectionsGetsALongCooldownNotAFastRetry()
    {
        // 805 is Dhan's "too many requests or connections. Further requests may result in the user
        // being blocked". Reconnecting promptly into that is what escalates a temporary refusal
        // into an account-level block, so it must wait far longer than the ordinary backoff ceiling.
        var cooldown = SpreadTrader.DhanIntegration.Ticker.DhanMarketFeedClient.CooldownFor(805);

        Assert.NotNull(cooldown);
        Assert.True(cooldown > TimeSpan.FromSeconds(30), "805 must outwait the ordinary 30s backoff ceiling");
    }

    [Fact]
    public void AnOversizedSubscriptionAlsoWaitsRatherThanRetryingIdentically()
    {
        // 804 - retrying the exact same oversized subscription immediately cannot succeed.
        Assert.NotNull(SpreadTrader.DhanIntegration.Ticker.DhanMarketFeedClient.CooldownFor(804));
    }

    [Theory]
    [InlineData(0)]    // no disconnect packet was seen at all
    [InlineData(800)]  // transient server-side error
    [InlineData(807)]  // expired token - handled by the auth failure path, not by waiting
    public void OrdinaryDisconnectReasonsUseTheNormalBackoff(int code)
    {
        // Only the codes that specifically punish fast retries get a cooldown; everything else must
        // keep the fast-recovery behaviour, or a routine blip would cost minutes of missing prices.
        Assert.Null(SpreadTrader.DhanIntegration.Ticker.DhanMarketFeedClient.CooldownFor(code));
    }

    [Fact]
    public void TheStabilityThresholdIsLongerThanAnOrdinaryReconnectCycle()
    {
        // If "stable" were shorter than the longest backoff, a connection could be judged healthy
        // purely because the previous wait was long - which would defeat the escalation entirely.
        Assert.True(FeedSocketLiveness.StableConnectionThreshold >= TimeSpan.FromSeconds(30));
    }

    /// <summary>The real backoff rule from DhanMarketFeedClient - Kite and Kotak inline the same
    /// expression, so pinning it once pins the behaviour all three rely on.</summary>
    private static TimeSpan DhanBackoff(TimeSpan current, TimeSpan uptime) =>
        SpreadTrader.DhanIntegration.Ticker.DhanMarketFeedClient.NextBackoff(current, uptime);

    [Fact]
    public void TheConfiguredDefaultsAreSaneRelativeToEachOther()
    {
        // The watchdog has to poll several times within the timeout window, or detection latency
        // would be dominated by the poll interval rather than the timeout itself.
        Assert.True(FeedSocketLiveness.StallCheckInterval < FeedSocketLiveness.StallTimeout);
        Assert.True(FeedSocketLiveness.StallTimeout / FeedSocketLiveness.StallCheckInterval >= 4);

        // Waiting for login must be re-checked far more often than a reconnect backs off, since
        // that wait is the thing that used to cost 30 seconds of dead air.
        Assert.True(FeedSocketLiveness.AwaitingSessionPollInterval <= TimeSpan.FromSeconds(2));

        // Keep-alive must be well inside the ~60s idle cull typical of cloud load balancers.
        Assert.True(FeedSocketLiveness.KeepAliveInterval < TimeSpan.FromSeconds(60));
    }
}
