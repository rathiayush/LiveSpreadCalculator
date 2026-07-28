using Microsoft.Extensions.Options;
using SpreadTrader.Core.Models;

namespace SpreadTrader.Engine.Services;

/// <summary>
/// The engine's own liveness signal, as opposed to the web host's.
///
/// Why this exists: <c>/healthz</c> answers "did the HTTP pipeline respond", which this process can
/// keep doing perfectly while the thing it actually exists to do is dead - the broker WebSocket can
/// have dropped hours ago, or TickIngestionHostedService's loop can have faulted, and every page
/// still loads, every REST call still returns, and the dashboard still reconnects. Whoever is
/// watching the deployment sees green through exactly the outage that matters, because a stalled
/// engine means live spreads stop updating and, far worse, exit triggers on OPEN POSITIONS stop
/// firing (see OrderDispatchHostedService).
///
/// So this tracks the two facts that actually distinguish "running" from "up": whether the
/// ingestion loop is still running, and how long ago the last tick arrived. Those are exposed at
/// <c>GET /healthz/engine</c>, which returns 503 when the engine is not doing its job so an
/// external uptime monitor can alert on it.
///
/// Staleness is only judged while ticks are genuinely expected - inside market hours, with at least
/// one instrument subscribed. Outside that window a silent feed is correct behaviour, not an
/// outage, and alerting on it every night trains everyone to ignore the alert.
///
/// Written to from the socket receive loop (see RecordTick), so the write path is two interlocked
/// operations and nothing else.
/// </summary>
public sealed class EngineHealthState
{
    /// <summary>Ingestion has been constructed but its loop hasn't reached the point of running the
    /// feed yet (instrument master still loading, legs still resolving).</summary>
    public const string FeedStarting = "starting";

    /// <summary>The feed loop is running. Note this says nothing about the socket being connected
    /// right now - the feed reconnects internally with backoff and never returns on a drop, which is
    /// precisely why last-tick age, not this flag, is the real signal.</summary>
    public const string FeedRunning = "running";

    /// <summary>The loop exited with an exception. The process is up; the engine is not.</summary>
    public const string FeedFaulted = "faulted";

    /// <summary>The loop exited cleanly - only expected during shutdown.</summary>
    public const string FeedStopped = "stopped";

    private readonly EngineOptions _options;

    private long _lastTickUtcTicks;   // 0 = no tick received yet this process lifetime
    private long _tickCount;
    private volatile string _feedState = FeedStarting;
    private volatile string? _feedError;

    public EngineHealthState(IOptions<EngineOptions> options)
    {
        _options = options.Value;
        ProcessStartedUtc = DateTimeOffset.UtcNow;
    }

    public DateTimeOffset ProcessStartedUtc { get; }

    /// <summary>Attached to IMarketDataFeed.TickReceived alongside ITickStore.Update, so it sees
    /// every tick without the feed implementations needing to know health tracking exists. Runs on
    /// the socket's receive loop - keep it to these two interlocked writes.</summary>
    public void RecordTick(Tick tick)
    {
        Interlocked.Exchange(ref _lastTickUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        Interlocked.Increment(ref _tickCount);
    }

    public void MarkFeedRunning()
    {
        _feedState = FeedRunning;
        _feedError = null;
    }

    public void MarkFeedFaulted(Exception ex)
    {
        _feedState = FeedFaulted;
        _feedError = ex.Message;
    }

    public void MarkFeedStopped()
    {
        // A faulted loop also runs through the finally block that calls this; don't let the clean
        // "stopped" overwrite the more informative failure that just happened.
        if (_feedState != FeedFaulted) _feedState = FeedStopped;
    }

    /// <summary>Builds the current report. <paramref name="subscribedTokenCount"/> comes from the
    /// live feed (IMarketDataFeed.GetSubscribedTokens) - when it's zero nothing is expected to tick,
    /// so silence is not a fault (a fresh process with no spreads configured yet, for instance).</summary>
    public EngineHealthReport Build(int subscribedTokenCount)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var nowIst = IstClock.ToIst(nowUtc);

        var lastTickTicks = Interlocked.Read(ref _lastTickUtcTicks);
        DateTimeOffset? lastTickUtc = lastTickTicks == 0
            ? null
            : new DateTimeOffset(lastTickTicks, TimeSpan.Zero);
        var tickAge = lastTickUtc is null ? (TimeSpan?)null : nowUtc - lastTickUtc.Value;

        var marketOpen = IsWithinMarketHours(nowIst, out var openIst, out var closeIst);
        var ticksExpected = subscribedTokenCount > 0 && (!_options.HealthCheckMarketHoursOnly || marketOpen);

        var feedState = _feedState;
        var maxAge = TimeSpan.FromSeconds(Math.Max(1, _options.HealthMaxTickAgeSeconds));

        string status;
        string? reason;

        if (feedState is FeedFaulted or FeedStopped)
        {
            status = EngineHealthReport.Unhealthy;
            reason = $"Tick ingestion loop is '{feedState}'"
                     + (_feedError is null ? "." : $": {_feedError}");
        }
        else if (!ticksExpected)
        {
            status = EngineHealthReport.Ok;
            reason = null;
        }
        else if (lastTickUtc is null)
        {
            status = EngineHealthReport.Unhealthy;
            reason = $"No tick has ever been received while {subscribedTokenCount} instrument(s) " +
                     "are subscribed and ticks are expected.";
        }
        else if (tickAge > maxAge)
        {
            status = EngineHealthReport.Degraded;
            reason = $"Last tick was {tickAge.Value.TotalSeconds:F0}s ago (limit " +
                     $"{maxAge.TotalSeconds:F0}s) with {subscribedTokenCount} instrument(s) subscribed.";
        }
        else
        {
            status = EngineHealthReport.Ok;
            reason = null;
        }

        return new EngineHealthReport(
            Status: status,
            Reason: reason,
            CheckedAtUtc: nowUtc,
            CheckedAtIst: nowIst.ToString("yyyy-MM-dd HH:mm:ss"),
            ProcessStartedUtc: ProcessStartedUtc,
            Uptime: (nowUtc - ProcessStartedUtc).ToString(@"d\.hh\:mm\:ss"),
            Feed: new EngineFeedHealth(
                State: feedState,
                Error: _feedError,
                SubscribedTokens: subscribedTokenCount,
                LastTickUtc: lastTickUtc,
                LastTickAgeSeconds: tickAge is null ? null : Math.Round(tickAge.Value.TotalSeconds, 1),
                TicksReceived: Interlocked.Read(ref _tickCount)),
            Market: new EngineMarketHealth(
                OpenIst: openIst,
                CloseIst: closeIst,
                IsOpen: marketOpen,
                TicksExpected: ticksExpected));
    }

    /// <summary>Market-hours test against the IST wall clock. Deliberately does NOT know about
    /// exchange holidays - the cost of being wrong is only that a holiday reports "ticks expected"
    /// and the endpoint goes 503 on a day nobody is trading, so keep an eye on that if you alert on
    /// it; the alternative (a holiday calendar to maintain) is worse for what this is.</summary>
    private bool IsWithinMarketHours(DateTimeOffset nowIst, out string openIst, out string closeIst)
    {
        var open = ParseOrDefault(_options.HealthMarketOpenIst, new TimeSpan(9, 15, 0));
        var close = ParseOrDefault(_options.HealthMarketCloseIst, new TimeSpan(15, 30, 0));

        openIst = open.ToString(@"hh\:mm");
        closeIst = close.ToString(@"hh\:mm");

        if (_options.HealthWeekdaysOnly &&
            nowIst.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;

        var timeOfDay = nowIst.TimeOfDay;

        // A close earlier than the open means the session runs past midnight; treat it as such
        // rather than as an empty window (nothing configures that today, but silently reporting
        // "market closed" all day would be a nasty way to find that out).
        return close >= open
            ? timeOfDay >= open && timeOfDay <= close
            : timeOfDay >= open || timeOfDay <= close;
    }

    private static TimeSpan ParseOrDefault(string? value, TimeSpan fallback) =>
        TimeSpan.TryParseExact(value?.Trim(), "hh\\:mm", null, out var parsed) ? parsed : fallback;
}

/// <summary>Serialized as the body of GET /healthz/engine.</summary>
public sealed record EngineHealthReport(
    string Status,
    string? Reason,
    DateTimeOffset CheckedAtUtc,
    string CheckedAtIst,
    DateTimeOffset ProcessStartedUtc,
    string Uptime,
    EngineFeedHealth Feed,
    EngineMarketHealth Market)
{
    /// <summary>Engine is doing its job (or is legitimately idle outside market hours).</summary>
    public const string Ok = "ok";

    /// <summary>Ingestion is running but ticks have gone stale while they were expected.</summary>
    public const string Degraded = "degraded";

    /// <summary>Ingestion isn't running at all, or has never produced a tick when it should have.</summary>
    public const string Unhealthy = "unhealthy";

    /// <summary>False for anything an operator should be paged about - drives the endpoint's
    /// 200-vs-503 so uptime monitors alert without having to parse the body.</summary>
    public bool IsHealthy => Status == Ok;
}

public sealed record EngineFeedHealth(
    string State,
    string? Error,
    int SubscribedTokens,
    DateTimeOffset? LastTickUtc,
    double? LastTickAgeSeconds,
    long TicksReceived);

public sealed record EngineMarketHealth(
    string OpenIst,
    string CloseIst,
    bool IsOpen,
    bool TicksExpected);
