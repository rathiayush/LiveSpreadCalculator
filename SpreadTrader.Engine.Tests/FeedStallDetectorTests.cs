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
