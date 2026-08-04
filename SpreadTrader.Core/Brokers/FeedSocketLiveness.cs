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

/// <summary>
/// Decides whether a feed that WAS delivering data has gone silent, so the socket can be aborted
/// and the normal reconnect path taken. Pure state + clock arithmetic, so it is unit tested rather
/// than reasoned about - see SpreadTrader.Engine.Tests.FeedStallDetectorTests.
///
/// THE SUBTLETY, and the reason this is not just "no data for N seconds": outside market hours a
/// perfectly healthy feed legitimately sends nothing for many hours. A naive timeout would decide
/// that was a stall and reconnect on a loop all night - hundreds of pointless reconnects, each
/// re-subscribing the entire universe, risking the broker's own rate limits, and turning a
/// non-problem into an outage.
///
/// So a connection that has NEVER delivered a message can never be judged stalled: silence there is
/// indistinguishable from a closed market, and the safe reading is to leave it alone. Only once at
/// least one message has arrived - proving data does flow on this connection right now - does
/// continued silence mean something is wrong. Overnight the detector therefore stays permanently
/// inert (nothing arrives, so nothing is ever judged), and during the session it has the evidence
/// it needs.
/// </summary>
public sealed class FeedStallDetector
{
    private DateTimeOffset? _lastMessageAt;

    /// <summary>Call on every (re)connect. Clears the evidence from the previous connection, so a
    /// fresh socket starts out unjudgeable rather than inheriting a timestamp that would make it
    /// look instantly stalled.</summary>
    public void OnConnected() => _lastMessageAt = null;

    /// <summary>Call for every message received, of any kind - a tick, a market-status frame,
    /// anything. It is liveness that is being tracked, not usefulness, so a non-tick frame counts
    /// just as much: it still proves the connection is delivering.</summary>
    public void OnMessage(DateTimeOffset now) => _lastMessageAt = now;

    /// <summary>True only when this connection has delivered at least one message AND has since
    /// been silent for longer than <paramref name="timeout"/>.</summary>
    public bool IsStalled(DateTimeOffset now, TimeSpan timeout) =>
        _lastMessageAt is { } last && now - last > timeout;
}
