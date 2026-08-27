using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Brokers;
using SpreadTrader.Core.Models;
using SpreadTrader.KiteIntegration;

namespace SpreadTrader.KiteIntegration.Ticker;

/// <summary>
/// Owns a single WebSocket connection to Kite's ticker (wss://ws.kite.trade), subscribes to
/// the configured instrument tokens, parses incoming binary frames, and raises TickReceived
/// for each one. Reconnects with exponential backoff and re-subscribes on every reconnect.
///
/// IMPORTANT: this must run as a singleton, single instance in your deployment. Kite caps a
/// single API key at 3 WebSocket connections, and running more than one instance of this
/// service would mean duplicate ticks (harmless) but, more importantly, duplicate trigger
/// evaluation and duplicate orders if wired to the same trigger engine. Do not scale this
/// service out horizontally.
/// </summary>
public sealed class KiteTickerClient : IMarketDataFeed
{
    private readonly KiteOptions _options;
    private readonly KiteSession _session;
    private readonly ILogger<KiteTickerClient> _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly HashSet<uint> _subscribedTokens = new();
    private readonly object _tokensLock = new();

    /// <summary>Detects a connection that has silently stopped delivering - see WatchForStallAsync.</summary>
    private readonly FeedStallDetector _stallDetector = new();
    private ClientWebSocket? _socket;

    public event Action<Tick>? TickReceived;

    /// <summary>Fired whenever Kite pushes an order postback over this same WebSocket connection
    /// (text frame, `{"type":"order","data":{...}}` - see
    /// https://kite.trade/docs/connect/v3/websocket/#postbacks-and-non-binary-updates). Kite's
    /// docs specifically recommend this over the separate HTTP postback webhook for individual-
    /// developer apps: it covers every order for the logged-in user regardless of which client
    /// placed it (this app's own triggered baskets, manual square-off, or even a trade placed
    /// directly in the Kite app/web), all without a public postback_url to register/host.
    /// Deliberately parameterless - consumers (see LivePositionsHostedService) don't need to know
    /// which order changed, only that SOME order did, as the cue to re-fetch REST positions.</summary>
    public event Action? OrderUpdateReceived;

    public KiteTickerClient(IOptions<KiteOptions> options, KiteSession session, ILogger<KiteTickerClient> logger)
    {
        _options = options.Value;
        _session = session;
        _logger = logger;
    }

    /// <summary>
    /// Adds instrument tokens to the live subscription set. Safe to call at any time, including
    /// after new spreads are configured through the API while the ticker is already running -
    /// it subscribes immediately if connected, and the tokens are remembered for resubscription
    /// after any reconnect.
    /// </summary>
    public async Task SubscribeAsync(IEnumerable<uint> instrumentTokens, CancellationToken cancellationToken)
    {
        uint[] newTokens;
        lock (_tokensLock)
        {
            newTokens = instrumentTokens.Where(t => _subscribedTokens.Add(t)).ToArray();
        }

        if (newTokens.Length == 0 || _socket is not { State: WebSocketState.Open })
            return;

        await SendAsync(new { a = "subscribe", v = newTokens }, cancellationToken);
        await SendAsync(new { a = "mode", v = new object[] { _options.TickerMode, newTokens } }, cancellationToken);
    }

    /// <summary>Removes instrument tokens from the live subscription set - the counterpart
    /// SubscribeAsync never had, which is why nothing in this app used to shrink it: once
    /// subscribed, a token (and the InMemoryTickStore entry it feeds) stayed forever, even after
    /// the spread/position that needed it was long gone. Drops each token from the remembered set
    /// first (so a reconnect never re-subscribes it), then tells Kite over the wire if currently
    /// connected - same "always update local state, best-effort over the wire" shape as
    /// SubscribeAsync. See TickIngestionHostedService's reconciliation loop, the only caller.</summary>
    public async Task UnsubscribeAsync(IEnumerable<uint> instrumentTokens, CancellationToken cancellationToken)
    {
        uint[] removedTokens;
        lock (_tokensLock)
        {
            removedTokens = instrumentTokens.Where(t => _subscribedTokens.Remove(t)).ToArray();
        }

        if (removedTokens.Length == 0 || _socket is not { State: WebSocketState.Open })
            return;

        await SendAsync(new { a = "unsubscribe", v = removedTokens }, cancellationToken);
    }

    /// <summary>Every instrument token currently remembered as subscribed (survives reconnects -
    /// see ConnectAndSubscribeAsync) - read by TickIngestionHostedService's reconciliation loop to
    /// find tokens nothing needs any more.</summary>
    public IReadOnlyCollection<uint> GetSubscribedTokens()
    {
        lock (_tokensLock)
        {
            return _subscribedTokens.ToArray();
        }
    }

    /// <summary>Runs until cancellationToken is cancelled, reconnecting on any failure.</summary>
    public async Task RunAsync(IReadOnlyCollection<uint> instrumentTokens, CancellationToken cancellationToken)
    {
        lock (_tokensLock)
        {
            foreach (var token in instrumentTokens) _subscribedTokens.Add(token);
        }

        var backoff = TimeSpan.FromSeconds(1);
        var loggedAwaitingSession = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            // Waiting for login is not a connection failure - polling at a short FIXED interval
            // keeps it from inflating the reconnect backoff to its 30s ceiling, which would then
            // leave the ticker idle for that long after the token finally arrived. See
            // FeedSocketLiveness.AwaitingSessionPollInterval.
            if (string.IsNullOrWhiteSpace(_session.AccessToken))
            {
                if (!loggedAwaitingSession)
                {
                    _logger.LogInformation("Kite ticker is waiting for login to complete before connecting.");
                    loggedAwaitingSession = true;
                }

                try { await Task.Delay(FeedSocketLiveness.AwaitingSessionPollInterval, cancellationToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            loggedAwaitingSession = false;

            try
            {
                await ConnectAndSubscribeAsync(cancellationToken);
                var connectedAt = DateTimeOffset.UtcNow;

                // Watchdog for a silently half-open socket - see WatchForStallAsync.
                using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var watchdog = WatchForStallAsync(_socket!, stallCts.Token);
                string endedReason;
                try
                {
                    endedReason = await ReceiveLoopAsync(cancellationToken);
                }
                finally
                {
                    stallCts.Cancel();
                    await SafeAwaitAsync(watchdog);
                }

                // The receive loop ended without throwing - a close frame, or the socket no longer
                // Open. This used to fall through and reconnect silently: no log, no delay. See
                // DhanMarketFeedClient.RunMarketFeedAsync for the full reasoning; the shape is the
                // same here.
                var uptime = DateTimeOffset.UtcNow - connectedAt;
                _logger.LogWarning(
                    "Kite ticker disconnected after {Uptime} - {Reason}. Reconnecting in {Seconds}s.",
                    uptime.TotalSeconds < 90 ? $"{uptime.TotalSeconds:F0}s" : $"{uptime.TotalMinutes:F1}m",
                    endedReason, backoff.TotalSeconds);

                backoff = uptime >= FeedSocketLiveness.StableConnectionThreshold
                    ? TimeSpan.FromSeconds(1)
                    : TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));

                try { await Task.Delay(backoff, cancellationToken); }
                catch (OperationCanceledException) { break; }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kite ticker connection lost, retrying in {Seconds}s", backoff.TotalSeconds);
                try
                {
                    await Task.Delay(backoff, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
            }
        }
    }

    /// <summary>Aborts the socket when the feed goes silent after having been live, so the blocked
    /// ReceiveAsync throws and the reconnect loop above runs. See FeedStallDetector for why a
    /// connection that never delivered anything is deliberately never judged stalled.</summary>
    private async Task WatchForStallAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await Task.Delay(FeedSocketLiveness.StallCheckInterval, cancellationToken); }
            catch (OperationCanceledException) { return; }

            if (socket.State != WebSocketState.Open) return;

            uint[] tokens;
            lock (_tokensLock) { tokens = _subscribedTokens.ToArray(); }

            switch (_stallDetector.Decide(DateTimeOffset.UtcNow, FeedSocketLiveness.StallTimeout, FeedSocketLiveness.InitialDataTimeout, tokens.Length > 0))
            {
                case FeedRecoveryAction.Resubscribe:
                    _logger.LogWarning(
                        "Kite ticker has sent nothing for over {Seconds}s with {Count} instrument(s) subscribed - " +
                        "re-sending the subscriptions on the same connection.",
                        FeedSocketLiveness.StallTimeout.TotalSeconds, tokens.Length);

                    try
                    {
                        await SendAsync(new { a = "subscribe", v = tokens }, cancellationToken);
                        await SendAsync(new { a = "mode", v = new object[] { _options.TickerMode, tokens } }, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Kite ticker re-subscribe failed - the connection is no longer usable.");
                        try { socket.Abort(); } catch { /* the receive loop's exception is what matters */ }
                        return;
                    }
                    break;

                case FeedRecoveryAction.Reconnect:
                    _logger.LogWarning(
                        "Kite ticker still silent after a re-subscribe, having previously been live - treating it as a " +
                        "silently dropped connection and reconnecting.");

                    try { socket.Abort(); } catch { /* the receive loop's exception is what matters */ }
                    return;
            }
        }
    }

    private static async Task SafeAwaitAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { /* expected when the receive loop ends first */ }
        catch { /* the watchdog must never be the thing that takes the feed down */ }
    }

    private async Task ConnectAndSubscribeAsync(CancellationToken cancellationToken)
    {
        _socket?.Dispose();
        _socket = new ClientWebSocket();
        FeedSocketLiveness.Configure(_socket);
        _stallDetector.OnConnected(DateTimeOffset.UtcNow);

        var accessToken = _session.AccessToken
            ?? throw new InvalidOperationException("No Kite access token available - complete the login flow first.");

        var uri = new Uri(
            $"{_options.WsUrl}?api_key={Uri.EscapeDataString(_options.ApiKey)}&access_token={Uri.EscapeDataString(accessToken)}");

        await _socket.ConnectAsync(uri, cancellationToken);

        uint[] tokens;
        lock (_tokensLock)
        {
            tokens = _subscribedTokens.ToArray();
        }

        _logger.LogInformation("Connected to Kite ticker ({Count} instruments, mode={Mode})", tokens.Length, _options.TickerMode);

        if (tokens.Length == 0)
            return;

        await SendAsync(new { a = "subscribe", v = tokens }, cancellationToken);
        await SendAsync(new { a = "mode", v = new object[] { _options.TickerMode, tokens } }, cancellationToken);
    }

    private async Task SendAsync(object message, CancellationToken cancellationToken)
    {
        if (_socket is null) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Receives until the connection ends, and reports why - both exits here are ordinary
    /// returns rather than exceptions, so the caller has no other way to tell a server close from a
    /// socket that quietly stopped being Open.</summary>
    private async Task<string> ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var messageStream = new MemoryStream();

        while (_socket!.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            messageStream.SetLength(0);
            WebSocketReceiveResult result;

            do
            {
                result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                // Liveness, not usefulness - Kite's own periodic heartbeat frame counts just as
                // much as a tick, since either proves the connection is still delivering.
                if (_stallDetector.OnMessage(DateTimeOffset.UtcNow))
                {
                    // First frame on this connection. Logged once because its ABSENCE is the single
                    // most useful fact when a feed looks stuck: if this line never appears, the
                    // socket is connected but delivering nothing, and the problem is the
                    // subscription or the broker - not anything downstream of here.
                    _logger.LogInformation(
                        "Kite ticker delivered its first data {Seconds:F1}s after connecting.",
                        _stallDetector.TimeSinceConnected(DateTimeOffset.UtcNow).TotalSeconds);
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    var status = result.CloseStatus?.ToString() ?? "no status";
                    var description = string.IsNullOrWhiteSpace(result.CloseStatusDescription)
                        ? "no description"
                        : result.CloseStatusDescription;

                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "server closed", cancellationToken);
                    return $"the server closed the connection ({status}: {description})";
                }
                messageStream.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Text)
            {
                // JsonDocument.Parse has no ReadOnlySpan<byte> overload (only ReadOnlyMemory<byte>,
                // ReadOnlySequence<byte>, Stream, or string) - AsMemory instead of AsSpan here, and
                // HandleTextMessage takes ReadOnlyMemory<byte> to match.
                HandleTextMessage(messageStream.GetBuffer().AsMemory(0, (int)messageStream.Length));
                continue;
            }

            if (result.MessageType != WebSocketMessageType.Binary || TickReceived is null)
                continue; // heartbeats (and text messages, handled above) have nothing left to do here

            var ticks = KiteTickPacketParser.ParseMessage(messageStream.GetBuffer().AsSpan(0, (int)messageStream.Length));
            foreach (var tick in ticks)
                TickReceived.Invoke(tick);
        }

        return cancellationToken.IsCancellationRequested
            ? "shutting down"
            : $"the socket stopped being open (state {_socket!.State})";
    }

    /// <summary>Parses a text frame for `{"type":"order",...}` (see OrderUpdateReceived's doc
    /// comment) and raises the event if it matches. "error"/"message" frames (also documented as
    /// possible `type` values - connection-level errors and broker alerts, not order updates) are
    /// deliberately ignored here; nothing currently needs them. Any frame that isn't valid JSON,
    /// or doesn't have the shape expected, is swallowed - a malformed/unexpected text frame from
    /// Kite should never take down tick processing on the same connection.</summary>
    private void HandleTextMessage(ReadOnlyMemory<byte> bytes)
    {
        if (OrderUpdateReceived is null) return;

        try
        {
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.TryGetProperty("type", out var typeElement) &&
                string.Equals(typeElement.GetString(), "order", StringComparison.Ordinal))
            {
                OrderUpdateReceived.Invoke();
            }
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Ignoring unparseable Kite ticker text frame.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket is { State: WebSocketState.Open })
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "disposing", CancellationToken.None);
            }
            catch
            {
                // best-effort close
            }
        }

        _socket?.Dispose();
    }
}
