using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Brokers;
using SpreadTrader.Core.Models;

namespace SpreadTrader.KotakIntegration.Ticker;

/// <summary>
/// Owns the live connection to Kotak Neo's market feed (wss://mlhsm.kotaksecurities.com by
/// default), subscribes the configured instruments, and raises TickReceived for each update -
/// the Kotak counterpart of KiteTickerClient/DhanMarketFeedClient. Reconnects with exponential
/// backoff and re-subscribes everything on every reconnect, same as both other integrations.
///
/// Closer to Kite's shape than Dhan's: ONE socket, JSON in both directions (Kotak's reference SDK
/// showed no binary tick format the way Kite/Dhan use - see the class doc comment's confidence
/// caveat below), subscriptions keyed by "exchange_segment|token" strings joined with '&amp;'.
///
/// AUTH HANDSHAKE (per the reference SDK): immediately after connecting, send
/// <c>{"type":"cn","Authorization":{editToken},"Sid":{editSid}}</c>. A periodic heartbeat
/// <c>{"type":"hb"}</c> keeps the connection alive (Kotak's own SDK sends this every 29s).
///
/// TICK FIELD MAPPING IS NOW CONFIRMED against Kotak's own "Field mapping in the websocket
/// response" table (tk=token, ltp/ltq, bp/bq=best bid, sp/bs=best ask - note "bs" is the ask
/// QUANTITY despite the b-prefix, per that table - op/h/lo/c=OHLC, oi=open interest). Merge tries
/// the confirmed name first for every field; the older guessed alternates (bp1/sp1/o/l, from before
/// a real field-mapping table was available) are kept only as a fallback in case a future message
/// shape ever omits the confirmed one.
///
/// ORDER/POSITION UPDATES: a SECOND, SEPARATE socket, confirmed by Kotak's own Order & Position
/// Streaming docs - wss://{dataCenter}.kotaksecurities.com/realtime (KotakSession.DataCenter, from
/// the login response - NOT the same host as WsUrl/RestBaseUrl). Its auth handshake is a raw
/// string, deliberately NOT JSON ("Do NOT use JSON.stringify" per the docs):
/// <c>{type:cn,Authorization:{editToken},Sid:{editSid},src:WEB}</c>. Every message on this socket
/// that carries <c>"type":"order"</c> or <c>"type":"position"</c> just raises OrderUpdateReceived
/// (deliberately parameterless - see IMarketDataFeed's doc comment) rather than parsing the full
/// order/position payload, since PositionsCache (the only subscriber) only needs "something
/// changed, re-fetch now" - it already does its own REST fetch either way. Runs as its own
/// reconnect/backoff loop alongside (not gating) the HSM tick socket above: RunOrderStreamAsync
/// simply waits for DataCenter/AccessToken/Sid to exist and retries on any disconnect, so a slow or
/// failed login never blocks ticks, and losing this socket never stops ticks either - PositionsCache
/// still re-polls on its own fixed timer regardless, same "still refreshes even without a push"
/// safety net DhanMarketFeedClient's doc comment describes for its own order-socket outages.
/// </summary>
public sealed class KotakMarketFeedClient : IMarketDataFeed
{
    /// <summary>No documented per-message subscription limit was found for Kotak's feed; this
    /// mirrors Dhan's conservative 100-per-batch assumption rather than sending an unbounded list.</summary>
    private const int SubscriptionBatchSize = 100;

    private static readonly TimeSpan InterBatchDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(29);

    private readonly KotakOptions _options;
    private readonly KotakSession _session;
    private readonly IBrokerInstrumentMaster _instrumentMaster;
    private readonly ILogger<KotakMarketFeedClient> _logger;

    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>Detects a connection that has silently stopped delivering - see WatchForStallAsync.
    /// Only the tick socket gets one; the order stream is legitimately idle for long stretches and
    /// PositionsCache re-polls on its own timer regardless.</summary>
    private readonly FeedStallDetector _stallDetector = new();
    private readonly HashSet<uint> _subscribedTokens = new();
    private readonly object _tokensLock = new();
    private readonly ConcurrentDictionary<uint, Tick> _lastTick = new();

    private ClientWebSocket? _socket;
    private ClientWebSocket? _orderStreamSocket;

    public event Action<Tick>? TickReceived;

    // Raised from the separate order/position stream socket - see the class doc comment's
    // "ORDER/POSITION UPDATES" section.
    public event Action? OrderUpdateReceived;

    public KotakMarketFeedClient(
        IOptions<KotakOptions> options,
        KotakSession session,
        IBrokerInstrumentMaster instrumentMaster,
        ILogger<KotakMarketFeedClient> logger)
    {
        _options = options.Value;
        _session = session;
        _instrumentMaster = instrumentMaster;
        _logger = logger;
    }

    public async Task SubscribeAsync(IEnumerable<uint> instrumentTokens, CancellationToken cancellationToken)
    {
        uint[] newTokens;
        lock (_tokensLock) { newTokens = instrumentTokens.Where(t => _subscribedTokens.Add(t)).ToArray(); }

        if (newTokens.Length == 0 || _socket is not { State: WebSocketState.Open }) return;
        await SendSubscriptionAsync(newTokens, subscribe: true, cancellationToken);
    }

    public async Task UnsubscribeAsync(IEnumerable<uint> instrumentTokens, CancellationToken cancellationToken)
    {
        uint[] removedTokens;
        lock (_tokensLock) { removedTokens = instrumentTokens.Where(t => _subscribedTokens.Remove(t)).ToArray(); }

        if (removedTokens.Length == 0) return;
        foreach (var token in removedTokens) _lastTick.TryRemove(token, out _);

        if (_socket is not { State: WebSocketState.Open }) return;
        await SendSubscriptionAsync(removedTokens, subscribe: false, cancellationToken);
    }

    public IReadOnlyCollection<uint> GetSubscribedTokens()
    {
        lock (_tokensLock) { return _subscribedTokens.ToArray(); }
    }

    public async Task RunAsync(IReadOnlyCollection<uint> instrumentTokens, CancellationToken cancellationToken)
    {
        lock (_tokensLock) { foreach (var token in instrumentTokens) _subscribedTokens.Add(token); }

        // Runs its own independent reconnect loop for the process lifetime - deliberately not
        // awaited here, and its failures never propagate to the tick socket loop below (see the
        // class doc comment's "ORDER/POSITION UPDATES" section on why the two are decoupled).
        _ = RunOrderStreamSupervisorAsync(cancellationToken);

        var backoff = TimeSpan.FromSeconds(1);
        var loggedAwaitingSession = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            // Waiting for login is not a connection failure - polling at a short FIXED interval
            // keeps it from inflating the reconnect backoff to its 30s ceiling, which would then
            // leave the feed idle for that long after the session finally arrived. See
            // FeedSocketLiveness.AwaitingSessionPollInterval.
            if (string.IsNullOrWhiteSpace(_session.AccessToken) || string.IsNullOrWhiteSpace(_session.Sid))
            {
                if (!loggedAwaitingSession)
                {
                    _logger.LogInformation("Kotak market feed is waiting for login to complete before connecting.");
                    loggedAwaitingSession = true;
                }

                if (!await DelayAsync(FeedSocketLiveness.AwaitingSessionPollInterval, cancellationToken)) break;
                continue;
            }

            loggedAwaitingSession = false;

            try
            {
                await ConnectAndSubscribeAsync(cancellationToken);
                backoff = TimeSpan.FromSeconds(1);

                using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var heartbeatTask = RunHeartbeatAsync(heartbeatCts.Token);
                // Watchdog for a silently half-open socket - see WatchForStallAsync. Distinct from
                // the heartbeat above, which only SENDS; this notices that nothing is coming back.
                var watchdog = WatchForStallAsync(_socket!, heartbeatCts.Token);
                try
                {
                    await ReceiveLoopAsync(cancellationToken);
                }
                finally
                {
                    heartbeatCts.Cancel();
                    await SafeAwaitAsync(heartbeatTask);
                    await SafeAwaitAsync(watchdog);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kotak market feed connection lost, retrying in {Seconds}s", backoff.TotalSeconds);
                if (!await DelayAsync(backoff, cancellationToken)) break;
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
            if (!await DelayAsync(FeedSocketLiveness.StallCheckInterval, cancellationToken)) return;
            if (socket.State != WebSocketState.Open) return;
            if (!_stallDetector.IsStalled(DateTimeOffset.UtcNow, FeedSocketLiveness.StallTimeout)) continue;

            _logger.LogWarning(
                "Kotak market feed has sent nothing for over {Seconds}s despite having been live - treating it as a " +
                "silently dropped connection and reconnecting.", FeedSocketLiveness.StallTimeout.TotalSeconds);

            try { socket.Abort(); } catch { /* the receive loop's exception is what matters */ }
            return;
        }
    }

    private async Task ConnectAndSubscribeAsync(CancellationToken cancellationToken)
    {
        _socket?.Dispose();
        _socket = new ClientWebSocket();
        FeedSocketLiveness.Configure(_socket);
        _stallDetector.OnConnected();

        var accessToken = _session.AccessToken
            ?? throw new InvalidOperationException("No Kotak session available - complete the login flow first.");
        var sid = _session.Sid
            ?? throw new InvalidOperationException("No Kotak session id available - complete the login flow first.");

        await _socket.ConnectAsync(new Uri(_options.WsUrl), cancellationToken);

        await SendAsync(new { type = "cn", Authorization = accessToken, Sid = sid }, cancellationToken);

        uint[] tokens;
        lock (_tokensLock) { tokens = _subscribedTokens.ToArray(); }

        _logger.LogInformation("Connected to Kotak market feed ({Count} instruments, mode={Mode})", tokens.Length, _options.TickerMode);

        if (tokens.Length > 0)
            await SendSubscriptionAsync(tokens, subscribe: true, cancellationToken);
    }

    private async Task SendSubscriptionAsync(IReadOnlyCollection<uint> tokens, bool subscribe, CancellationToken cancellationToken)
    {
        var entries = ResolveSubscriptionEntries(tokens);
        if (entries.Count == 0) return;

        var isFirst = true;
        foreach (var batch in entries.Chunk(SubscriptionBatchSize))
        {
            if (!isFirst && !await DelayAsync(InterBatchDelay, cancellationToken)) return;
            isFirst = false;

            await SendAsync(new
            {
                type = subscribe ? "mws" : "umws", // "market watch subscribe"/"unsubscribe" - see the class doc comment's confidence caveat
                scrips = string.Join("&", batch),
                channelnum = "1",
            }, cancellationToken);
        }
    }

    private readonly record struct SubscriptionEntry(string Segment, string Token)
    {
        public override string ToString() => $"{Segment}|{Token}";
    }

    private List<SubscriptionEntry> ResolveSubscriptionEntries(IReadOnlyCollection<uint> tokens)
    {
        var lookup = new Dictionary<uint, string>();
        foreach (var instrument in _instrumentMaster.GetAllFutures())
            lookup[instrument.InstrumentToken] = instrument.Segment;

        // Cash/index instruments too, so a spread's spot leg actually streams - without a known
        // segment a spot token is skipped as "unresolved" below and its price never arrives.
        // Futures are added first so they always win a (theoretical) id collision.
        foreach (var instrument in _instrumentMaster.GetAllSpot())
            lookup.TryAdd(instrument.InstrumentToken, instrument.Segment);

        var entries = new List<SubscriptionEntry>(tokens.Count);
        var unresolved = new List<uint>();

        foreach (var token in tokens)
        {
            if (lookup.TryGetValue(token, out var segment))
                entries.Add(new SubscriptionEntry(segment, token.ToString()));
            else
                unresolved.Add(token);
        }

        if (unresolved.Count > 0)
        {
            _logger.LogWarning(
                "Skipped {Count} instrument token(s) with no entry in the Kotak scrip master, so their exchange segment is unknown: {Tokens}.",
                unresolved.Count, string.Join(", ", unresolved.Take(20)));
        }

        return entries;
    }

    private async Task RunHeartbeatAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!await DelayAsync(HeartbeatInterval, cancellationToken)) return;
            try { await SendAsync(new { type = "hb" }, cancellationToken); }
            catch { /* connection loss surfaces via the receive loop instead */ }
        }
    }

    // ---- Order/position stream (wss://{dataCenter}.kotaksecurities.com/realtime) - see the class
    // doc comment's "ORDER/POSITION UPDATES" section. Entirely independent of the tick socket
    // above: its own supervisor loop, its own backoff, its own ClientWebSocket instance.

    private async Task RunOrderStreamSupervisorAsync(CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (await RunOrderStreamOnceAsync(cancellationToken))
                    backoff = TimeSpan.FromSeconds(1); // a real connection was made and later dropped - reset backoff
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kotak order/position stream connection lost, retrying in {Seconds}s", backoff.TotalSeconds);
            }

            if (!await DelayAsync(backoff, cancellationToken)) break;
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
        }
    }

    /// <summary>Connects, authenticates, and receives until the socket closes or the session isn't
    /// ready yet. Returns true if a connection was actually established (so the supervisor knows to
    /// reset its backoff), false if it never got that far (e.g. DataCenter/session not set yet -
    /// login hasn't completed) so the supervisor keeps its current backoff instead of hot-looping.</summary>
    private async Task<bool> RunOrderStreamOnceAsync(CancellationToken cancellationToken)
    {
        var dataCenter = _session.DataCenter;
        var accessToken = _session.AccessToken;
        var sid = _session.Sid;
        if (string.IsNullOrWhiteSpace(dataCenter) || string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(sid))
            return false; // no session yet - KotakSessionAutoRenewalHostedService hasn't logged in (or this response had no dataCenter)

        _orderStreamSocket?.Dispose();
        var socket = new ClientWebSocket();
        // Keep-alive only, no stall watchdog: this socket is legitimately silent for long stretches
        // (no order activity is the normal state), so silence here means nothing is wrong.
        FeedSocketLiveness.Configure(socket);
        _orderStreamSocket = socket;

        var uri = new Uri($"wss://{dataCenter.ToLowerInvariant()}.kotaksecurities.com/realtime");
        await socket.ConnectAsync(uri, cancellationToken);

        // Deliberately a raw string, NOT JSON.stringify'd - confirmed by Kotak's own docs ("This is
        // NOT JSON. Do NOT use JSON.stringify. It must be sent as a single raw string.").
        var authPayload = $"{{type:cn,Authorization:{accessToken},Sid:{sid},src:WEB}}";
        var authBytes = Encoding.UTF8.GetBytes(authPayload);
        await socket.SendAsync(new ArraySegment<byte>(authBytes), WebSocketMessageType.Text, true, cancellationToken);

        _logger.LogInformation("Connected to Kotak order/position stream ({Host})", uri.Host);

        var buffer = new byte[16 * 1024];
        using var messageStream = new MemoryStream();

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            messageStream.SetLength(0);
            WebSocketReceiveResult result;

            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "server closed", cancellationToken);
                    return true;
                }
                messageStream.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Text)
                HandleOrderStreamMessage(messageStream.GetBuffer().AsSpan(0, (int)messageStream.Length));
        }

        return true;
    }

    /// <summary>Deliberately doesn't parse the order/position payload itself - see the class doc
    /// comment on why a bare OrderUpdateReceived is enough for PositionsCache. The connection
    /// acknowledgement (<c>{"ak":"ok","type":"cn",...}</c>) also lands here and is silently ignored
    /// since its type is "cn", not "order"/"position".</summary>
    private void HandleOrderStreamMessage(ReadOnlySpan<byte> bytes)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(bytes.ToArray()); }
        catch (JsonException) { return; }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type is "order" or "position")
                OrderUpdateReceived?.Invoke();
        }
    }

    private async Task SendAsync(object message, CancellationToken cancellationToken)
    {
        if (_socket is not { State: WebSocketState.Open }) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);

        await _sendLock.WaitAsync(cancellationToken);
        try { await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken); }
        finally { _sendLock.Release(); }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var messageStream = new MemoryStream();

        while (_socket!.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            messageStream.SetLength(0);
            WebSocketReceiveResult result;

            do
            {
                result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                // Liveness, not usefulness - any frame proves the connection is still delivering.
                _stallDetector.OnMessage(DateTimeOffset.UtcNow);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "server closed", cancellationToken);
                    return;
                }
                messageStream.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text) continue;

            HandleMessage(messageStream.GetBuffer().AsSpan(0, (int)messageStream.Length));
        }
    }

    private void HandleMessage(ReadOnlySpan<byte> bytes)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(bytes.ToArray()); }
        catch (JsonException) { return; }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in root.EnumerateArray())
                    TryHandleTick(element);
                return;
            }

            TryHandleTick(root);
        }
    }

    private void TryHandleTick(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return;

        var token = ReadUInt(element, "tk") ?? ReadUInt(element, "token") ?? ReadUInt(element, "instrumentToken");
        if (token is not { } instrumentToken || instrumentToken == 0) return;

        var handler = TickReceived;
        if (handler is null) return;

        bool stillSubscribed;
        lock (_tokensLock) { stillSubscribed = _subscribedTokens.Contains(instrumentToken); }

        var tick = Merge(instrumentToken, element);
        if (stillSubscribed) _lastTick[instrumentToken] = tick;
        else _lastTick.TryRemove(instrumentToken, out _);

        handler.Invoke(tick);
    }

    /// <summary>Folds a tick message into the last known state for that instrument - same
    /// never-blank-out-what-we-already-know approach as DhanMarketFeedClient.Merge, given a Kotak
    /// message may only carry a subset of fields (see the class doc comment's caveat on unconfirmed
    /// field names).</summary>
    private Tick Merge(uint token, JsonElement element)
    {
        var previous = _lastTick.TryGetValue(token, out var existing) ? existing : default;
        var now = DateTimeOffset.UtcNow;

        var lastPrice = ReadDecimal(element, "ltp") ?? ReadDecimal(element, "lastPrice") ?? ReadDecimal(element, "c");
        var bestBid = ReadDecimal(element, "bp") ?? ReadDecimal(element, "bp1") ?? ReadDecimal(element, "bestBidPrice");
        var bestAsk = ReadDecimal(element, "sp") ?? ReadDecimal(element, "sp1") ?? ReadDecimal(element, "bestAskPrice");

        return new Tick(
            InstrumentToken: token,
            LastPrice: lastPrice is > 0 ? lastPrice.Value : previous.LastPrice,
            LastTradedQuantity: ReadLong(element, "ltq") ?? previous.LastTradedQuantity,
            Volume: ReadLong(element, "v") ?? ReadLong(element, "vol") ?? previous.Volume,
            Open: ReadDecimal(element, "op") ?? ReadDecimal(element, "o") ?? previous.Open,
            High: ReadDecimal(element, "h") ?? previous.High,
            Low: ReadDecimal(element, "lo") ?? ReadDecimal(element, "l") ?? previous.Low,
            Close: ReadDecimal(element, "c") ?? previous.Close,
            OpenInterest: ReadLong(element, "oi") ?? previous.OpenInterest,
            ExchangeTimestamp: now,
            ReceivedAt: now,
            BestBidPrice: bestBid is > 0 ? bestBid.Value : previous.BestBidPrice,
            BestAskPrice: bestAsk is > 0 ? bestAsk.Value : previous.BestAskPrice);
    }

    private static uint? ReadUInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetUInt32(out var number) => number,
            JsonValueKind.String when uint.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static decimal? ReadDecimal(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static long? ReadLong(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try { await Task.Delay(delay, cancellationToken); return true; }
        catch (OperationCanceledException) { return false; }
    }

    private static async Task SafeAwaitAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { /* expected on cancellation */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket is { State: WebSocketState.Open })
        {
            try { await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "disposing", CancellationToken.None); }
            catch { /* best-effort close */ }
        }
        _socket?.Dispose();

        if (_orderStreamSocket is { State: WebSocketState.Open })
        {
            try { await _orderStreamSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "disposing", CancellationToken.None); }
            catch { /* best-effort close */ }
        }
        _orderStreamSocket?.Dispose();

        // _sendLock is deliberately NOT disposed - see DhanMarketFeedClient.DisposeAsync for the
        // full reasoning. In short: this is a DI singleton that SwitchingMarketDataFeed disposes on
        // every broker switch and re-runs if the user switches back, so disposing the semaphore
        // here would make that second run throw ObjectDisposedException on its first send. Both
        // sockets above are the real resources and are still closed and disposed.
    }
}
