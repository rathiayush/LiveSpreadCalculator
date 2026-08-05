using SpreadTrader.Core.Brokers;

namespace SpreadTrader.Engine.Tests;

/// <summary>
/// Pins the rule that decides whether a live market-data socket has silently died.
///
/// Both directions of getting this wrong are costly, which is why it is tested rather than
/// eyeballed:
///  - too eager, and a healthy but quiet connection is torn down and the whole universe
///    re-subscribed, potentially in a loop all night against the broker's rate limits;
///  - too reluctant, and a half-open socket sits there forever with prices frozen while the app
///    looks perfectly healthy - the failure this class exists to end.
/// </summary>
public class FeedStallDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    [Fact]
    public void AConnectionThatHasNeverDeliveredIsNeverJudgedStalled()
    {
        // THE off-hours guard. Overnight a healthy feed sends nothing at all, and that is
        // indistinguishable from a dead one - so the safe answer is to leave it alone rather than
        // reconnect on a loop until morning.
        var detector = new FeedStallDetector();
        detector.OnConnected();

        Assert.False(detector.IsStalled(T0.AddHours(9), Timeout));
    }

    [Fact]
    public void SilenceAfterDataHasFlowedIsAStall()
    {
        // Data was flowing a moment ago and has now stopped dead - the half-open socket case.
        var detector = new FeedStallDetector();
        detector.OnConnected();
        detector.OnMessage(T0);

        Assert.True(detector.IsStalled(T0.Add(Timeout).AddSeconds(1), Timeout));
    }

    [Fact]
    public void AGapShorterThanTheTimeoutIsNotAStall()
    {
        // An illiquid contract going quiet for a minute mid-session must not cost a reconnect.
        var detector = new FeedStallDetector();
        detector.OnConnected();
        detector.OnMessage(T0);

        Assert.False(detector.IsStalled(T0.AddSeconds(119), Timeout));
    }

    [Fact]
    public void TheTimeoutBoundaryItselfIsNotYetAStall()
    {
        var detector = new FeedStallDetector();
        detector.OnConnected();
        detector.OnMessage(T0);

        Assert.False(detector.IsStalled(T0.Add(Timeout), Timeout));
    }

    [Fact]
    public void EveryMessageResetsTheClock()
    {
        // A steadily ticking feed must never be judged stalled, however long it stays connected.
        var detector = new FeedStallDetector();
        detector.OnConnected();

        var now = T0;
        for (var i = 0; i < 50; i++)
        {
            now = now.AddSeconds(60);
            detector.OnMessage(now);
            Assert.False(detector.IsStalled(now.AddSeconds(30), Timeout));
        }
    }

    [Fact]
    public void ReconnectingClearsTheEvidenceFromThePreviousConnection()
    {
        // Without this a fresh socket would inherit the old timestamp and be declared stalled
        // immediately - reconnect, instantly "stall", reconnect again, forever.
        var detector = new FeedStallDetector();
        detector.OnConnected();
        detector.OnMessage(T0);

        detector.OnConnected();

        Assert.False(detector.IsStalled(T0.AddHours(1), Timeout));
    }

    [Fact]
    public void AStalledConnectionStaysStalledUntilItReconnects()
    {
        // The watchdog polls, so the verdict must be stable between checks rather than one-shot.
        var detector = new FeedStallDetector();
        detector.OnConnected();
        detector.OnMessage(T0);

        Assert.True(detector.IsStalled(T0.AddSeconds(121), Timeout));
        Assert.True(detector.IsStalled(T0.AddSeconds(200), Timeout));
    }

    [Fact]
    public void DataResumingClearsTheStall()
    {
        // If a late frame arrives before the watchdog's next poll, nothing should be torn down.
        var detector = new FeedStallDetector();
        detector.OnConnected();
        detector.OnMessage(T0);
        Assert.True(detector.IsStalled(T0.AddSeconds(121), Timeout));

        detector.OnMessage(T0.AddSeconds(122));

        Assert.False(detector.IsStalled(T0.AddSeconds(123), Timeout));
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
