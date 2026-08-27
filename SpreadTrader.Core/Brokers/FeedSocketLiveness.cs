using System.Net.WebSockets;

namespace SpreadTrader.Core.Brokers;

/// <summary>
/// Shared liveness settings and stall detection for every broker's market-data WebSocket.
///
/// THE PROBLEM THIS SOLVES. A WebSocket can go "half open": the peer (or something between us and
/// it - a load balancer, a NAT gateway, an idle timeout) stops delivering data without ever sending
/// a close frame or resetting the TCP connection. The socket's State stays Open, so a receive loop
/// written as
///
///     while (socket.State == WebSocketState.Open) { await socket.ReceiveAsync(...); }
///
/// simply awaits forever. No exception is thrown, so the surrounding reconnect/backoff loop never
/// runs, and the feed is silently dead until the process is restarted - the "sometimes it just
/// stops updating and nothing in the logs says why" failure. It is worse than a visible disconnect
/// precisely because it is invisible: prices quietly stop moving while the app still looks healthy.
///
/// .NET 9 solves this directly with ClientWebSocketOptions.KeepAliveTimeout (ping, and abort if no
/// pong). This app targets net8.0, where that property does not exist - KeepAliveInterval there
/// sends keep-alive frames but never aborts on a missing reply - so the pong-based approach is not
/// available and the stall has to be detected at the application level instead. See
/// FeedStallDetector for how, and why it cannot simply be "no data for N seconds".
/// </summary>
public static class FeedSocketLiveness
{
    /// <summary>How often to send a keep-alive frame on an otherwise idle connection. Comfortably
    /// under the ~60s idle timeout typical of cloud load balancers, so a quiet connection is not
    /// culled for inactivity. On net8.0 this alone cannot detect a half-open peer (there is no pong
    /// timeout) but it does surface a genuinely dead TCP connection, because writing to one
    /// eventually fails - which the reconnect loop then handles.</summary>
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(20);

    /// <summary>How long a connection that HAS been delivering data may then go silent before it is
    /// treated as stalled. Long enough not to trip on an ordinary mid-session lull in an illiquid
    /// contract, short enough that a dead feed is noticed in seconds rather than never.</summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(120);

    /// <summary>How often to test for a stall. Cheap - it is one timestamp comparison.</summary>
    public static readonly TimeSpan StallCheckInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a freshly connected feed may deliver NOTHING before its subscriptions are re-sent.
    ///
    /// Much shorter than StallTimeout on purpose. Once a subscription is live, data starts within
    /// seconds during market hours, so prolonged silence right after connecting means the
    /// subscription very likely never took effect - and on Dhan that is a documented possibility,
    /// since authorisation there completes asynchronously AFTER the socket connects and anything
    /// sent before it lands can simply be ignored. Waiting the full stall timeout to try again
    /// would mean minutes of a dead feed for something a single re-send fixes.
    ///
    /// Only the FIRST attempt is this eager; subsequent ones fall back to StallTimeout so a
    /// genuinely closed market settles into a quiet retry rather than chattering.
    /// </summary>
    public static readonly TimeSpan InitialDataTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a connection must survive before it counts as "healthy", allowing the reconnect
    /// backoff to reset to its floor.
    ///
    /// Without this the backoff reset the instant a connect SUCCEEDED, which is not the same thing
    /// as the connection working: a server that accepts and then immediately closes would be
    /// retried every second forever, hammering the broker and inviting a rate-limit block that
    /// turns a blip into a sustained outage. Escalating on a flap and resetting only on a
    /// connection that actually held is the difference between backing off and busy-waiting.
    /// </summary>
    public static readonly TimeSpan StableConnectionThreshold = TimeSpan.FromSeconds(60);

    /// <summary>How long to wait between checks while the broker login hasn't completed yet.
    ///
    /// Deliberately short and FIXED, never exponential: waiting for a token is a normal startup (and
    /// token-renewal) state, not a connection failure, so it must not inflate the reconnect backoff.
    /// Letting it do so meant the backoff had already doubled its way to the 30s ceiling by the time
    /// login finished, and the feed then sat idle for a further 30 seconds before even trying again -
    /// dead air with no live prices on every startup and every token renewal.</summary>
    public static readonly TimeSpan AwaitingSessionPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>Applies the keep-alive interval. Call on every ClientWebSocket before connecting -
    /// the options are read at connect time and cannot be changed afterwards.</summary>
    public static void Configure(ClientWebSocket socket)
    {
        socket.Options.KeepAliveInterval = KeepAliveInterval;
    }
}

/// <summary>What to do about a feed that has gone quiet.</summary>
public enum FeedRecoveryAction
{
    /// <summary>Nothing is wrong, or nothing can be concluded yet.</summary>
    None,

    /// <summary>Send the subscriptions again on the SAME connection. Always tried first: it is
    /// cheap, it repairs the most likely cause (a subscription the broker never acted on), and
    /// unlike reconnecting it opens no new socket - so it cannot contribute to the connection-count
    /// limit that Dhan answers with 805.</summary>
    Resubscribe,

    /// <summary>Tear the socket down and reconnect. Only for a connection that HAD been delivering
    /// and then went silent even after a re-subscribe - i.e. the connection itself is the problem.</summary>
    Reconnect,
}

/// <summary>
/// Decides whether a feed has gone quiet and what to do about it. Pure state + clock arithmetic, so
/// it is unit tested rather than reasoned about - see SpreadTrader.Engine.Tests.FeedStallDetectorTests.
///
/// TWO DIFFERENT FAILURES, WHICH NEED DIFFERENT ANSWERS:
///
///  1. A connection that WAS delivering and stopped. The socket is likely half-open, so the fix is
///     ultimately to reconnect - but a re-subscribe is tried first, because it is far cheaper and
///     sometimes enough.
///
///  2. A connection that has delivered NOTHING since it was established, while subscriptions are
///     outstanding. Reconnecting is the wrong answer here and was previously the reason nothing
///     happened at all: an earlier version deliberately never judged such a connection, because
///     overnight a healthy feed legitimately delivers nothing and reconnecting on a loop all night
///     would be far worse than the problem. But that also meant a socket whose SUBSCRIPTION never
///     took effect sat there connected, silent, and unexamined - which on Dhan is a real scenario,
///     because authorisation there completes asynchronously AFTER the socket connects, so
///     subscriptions sent immediately on connect can be received before the connection is
///     authorised and simply never acted upon. The feed then looks perfectly healthy - connected,
///     authenticated, no errors - and never sends a single tick.
///
///     Re-subscribing fixes exactly that, and is safe to do repeatedly: it is a few small messages,
///     it opens no new connection, and if the market is simply closed it achieves nothing and costs
///     nothing. So this case re-subscribes indefinitely and NEVER escalates to a reconnect.
/// </summary>
public sealed class FeedStallDetector
{
    private DateTimeOffset _connectedAt;
    private DateTimeOffset? _lastMessageAt;
    private DateTimeOffset? _lastRecoveryAt;
    private bool _everDelivered;
    private bool _resubscribedSinceLastMessage;

    /// <summary>Call on every (re)connect, before the subscriptions go out. Clears the previous
    /// connection's state so a fresh socket is judged on its own behaviour.</summary>
    public void OnConnected(DateTimeOffset now)
    {
        _connectedAt = now;
        _lastMessageAt = null;
        _lastRecoveryAt = null;
        _everDelivered = false;
        _resubscribedSinceLastMessage = false;
    }

    /// <summary>Call for every message received, of any kind - a tick, a market-status frame,
    /// anything. It is liveness being tracked, not usefulness: any frame proves the connection is
    /// delivering. (Note WebSocket ping/pong control frames do NOT surface through ReceiveAsync, so
    /// they correctly never count.)</summary>
    /// <returns>True the FIRST time this connection delivers anything, so the caller can log it.
    /// That one line is what distinguishes "the socket is not delivering" from "the socket is fine
    /// and the problem is downstream" - the single most useful fact when a feed looks stuck, and
    /// otherwise entirely invisible.</returns>
    public bool OnMessage(DateTimeOffset now)
    {
        var isFirst = !_everDelivered;
        _lastMessageAt = now;
        _everDelivered = true;
        _resubscribedSinceLastMessage = false;
        return isFirst;
    }

    /// <summary>How long this connection has been established - for logging alongside the first
    /// delivery, so "connected but silent for 4 minutes" is visible at a glance.</summary>
    public TimeSpan TimeSinceConnected(DateTimeOffset now) => now - _connectedAt;

    /// <summary>What the caller should do right now.</summary>
    /// <param name="quietTimeout">How long a connection that HAS delivered may go silent.</param>
    /// <param name="initialDataTimeout">How long a connection that has delivered NOTHING may stay
    /// silent before its subscriptions are re-sent - deliberately shorter, see the field's docs.</param>
    /// <param name="hasSubscriptions">Whether anything is actually subscribed. With nothing
    /// subscribed there is no data to expect, so silence means nothing.</param>
    public FeedRecoveryAction Decide(
        DateTimeOffset now, TimeSpan quietTimeout, TimeSpan initialDataTimeout, bool hasSubscriptions)
    {
        if (!hasSubscriptions) return FeedRecoveryAction.None;

        // A connection that has never delivered gets ONE eager attempt, then settles to the normal
        // cadence - fast recovery when a subscription was missed, without chattering all night when
        // the market is simply closed.
        var timeout = !_everDelivered && _lastRecoveryAt is null ? initialDataTimeout : quietTimeout;

        // Quiet is measured from the last thing that happened - a message, or the last recovery
        // attempt, or failing both the moment we connected. Counting from the recovery attempt is
        // what gives a re-subscribe a full window to take effect before anything escalates.
        var since = Max(_lastMessageAt ?? _connectedAt, _lastRecoveryAt ?? _connectedAt);
        if (now - since <= timeout) return FeedRecoveryAction.None;

        // Delivered before, and a re-subscribe has already been tried without bringing it back:
        // the connection itself is the problem.
        if (_everDelivered && _resubscribedSinceLastMessage)
        {
            _lastRecoveryAt = now;
            return FeedRecoveryAction.Reconnect;
        }

        _lastRecoveryAt = now;
        _resubscribedSinceLastMessage = true;
        return FeedRecoveryAction.Resubscribe;
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
