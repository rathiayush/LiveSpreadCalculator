using System.Collections.Concurrent;
using System.Net.WebSockets;
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
/// ORDER UPDATES ARE NOT WIRED. No confirmed Kotak order-update push channel (WebSocket message
/// type or separate socket) was available either - OrderUpdateReceived exists to satisfy
/// IMarketDataFeed but never actually fires from this client. This is not silently broken:
/// PositionsCache (the only subscriber) also re-polls positions on its own fixed timer regardless
/// of this event ever firing, the same "still refreshes even without a push" safety net
/// DhanMarketFeedClient's doc comment describes for its own order-socket outages - it just means a
/// Kotak-driven position change is only ever noticed on the next scheduled poll, never instantly.
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
    private readonly HashSet<uint> _subscribedTokens = new();
    private readonly object _tokensLock = new();
    private readonly ConcurrentDictionary<uint, Tick> _lastTick = new();

    private ClientWebSocket? _socket;

    public event Action<Tick>? TickReceived;

    // Never raised - see the class doc comment's "ORDER UPDATES ARE NOT WIRED" section. Kept to
    // satisfy IMarketDataFeed; suppressed rather than left as a compiler warning since the reason
    // is deliberate and documented, not an oversight.
#pragma warning disable CS0067
    public event Action? OrderUpdateReceived;
#pragma warning restore CS0067

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

        var backoff = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndSubscribeAsync(cancellationToken);
                backoff = TimeSpan.FromSeconds(1);

                using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var heartbeatTask = RunHeartbeatAsync(heartbeatCts.Token);
                try
                {
                    await ReceiveLoopAsync(cancellationToken);
                }
                finally
                {
                    heartbeatCts.Cancel();
                    await SafeAwaitAsync(heartbeatTask);
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

    private async Task ConnectAndSubscribeAsync(CancellationToken cancellationToken)
    {
        _socket?.Dispose();
        _socket = new ClientWebSocket();

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
        _sendLock.Dispose();
    }
}
