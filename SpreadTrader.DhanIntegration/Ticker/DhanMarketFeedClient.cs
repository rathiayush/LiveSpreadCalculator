using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Brokers;
using SpreadTrader.Core.Models;
using SpreadTrader.DhanIntegration.Auth;

namespace SpreadTrader.DhanIntegration.Ticker;

/// <summary>
/// Owns the live connection to Dhan's market feed (wss://api-feed.dhan.co), subscribes the
/// configured instruments, decodes the binary packets, and raises TickReceived for each update.
/// Reconnects with exponential backoff and re-subscribes everything on every reconnect.
///
/// Four things differ from Kite's ticker and are worth knowing before changing anything here:
///
///  1. TWO SOCKETS, ONE INTERFACE. Kite multiplexes order postbacks onto the ticker connection as
///     text frames. Dhan has a separate order-update socket (wss://api-order-update.dhan.co) with
///     its own JSON login handshake. RunAsync therefore drives both connections concurrently and
///     surfaces them through the single IMarketDataFeed contract, so PositionsCache still just
///     subscribes to OrderUpdateReceived and knows nothing about the difference.
///
///  2. SUBSCRIPTIONS ARE JSON, DATA IS BINARY. Requests go up as JSON; everything that comes back
///     is binary (see DhanTickPacketParser). Dhan caps a subscription message at 100 instruments,
///     so a universe of a few hundred legs must be batched - sending one oversized message
///     silently subscribes nothing.
///
///  3. SUBSCRIPTIONS ARE KEYED BY (SEGMENT, SECURITY ID), NOT A GLOBAL TOKEN. Kite's
///     instrument_token is globally unique, so its ticker only ever needs the number. Dhan needs
///     the exchange segment alongside the security id, so this resolves each token's segment from
///     the instrument master. IMarketDataFeed deliberately still takes plain tokens: pushing the
///     segment up into the interface would have forced every call site (including
///     PositionsCache.GetAnchoredTokens, which genuinely only has tokens) to carry broker-shaped
///     data around.
///
///  4. THE SERVER ANNOUNCES WHY IT DISCONNECTS. Dhan sends a disconnect packet (response code 50)
///     carrying a reason before closing - notably 807 for an expired token, which is routed to
///     DhanAuthFailureHandler so the dashboard is told to log in again instead of watching the
///     feed reconnect-loop forever against a dead token.
///
/// IMPORTANT: this must run as a single instance - see IMarketDataFeed's doc comment.
/// </summary>
public sealed class DhanMarketFeedClient : IMarketDataFeed
{
    // Dhan's documented maximum instruments per subscription message.
    private const int SubscriptionBatchSize = 100;

    /// <summary>Dhan's documented ceiling on instruments carried by a single feed connection. This
    /// client uses one connection, so this is the hard limit on what can stream at once. The full
    /// NFO futures universe is ~658 contracts and MCX adds ~150, so ordinary use is nowhere near
    /// it - but going over silently is the bad case, since Dhan answers an oversized subscription
    /// with error 804 and the surplus instruments simply never tick, which looks like a handful of
    /// spreads being mysteriously stale rather than a limit being hit.</summary>
    private const int MaxInstrumentsPerConnection = 5000;

    // Small pause between consecutive subscription messages. Sending several hundred instruments
    // as back-to-back batches with no gap is exactly the pattern that caused instruments to end up
    // silently without depth on the Kite side (see TickIngestionHostedService.SubscribeTokensAsync),
    // and costs nothing to avoid here.
    private static readonly TimeSpan InterBatchDelay = TimeSpan.FromMilliseconds(50);

    // Dhan feed request codes, per Dhan's published annexure.
    private const int RequestCodeTicker = 15;
    private const int RequestCodeUnsubscribeTicker = 16;
    private const int RequestCodeQuote = 17;
    private const int RequestCodeUnsubscribeQuote = 18;
    private const int RequestCodeFull = 21;
    private const int RequestCodeUnsubscribeFull = 22;
    private const int RequestCodeDisconnect = 12;

    private readonly DhanOptions _options;
    private readonly DhanSession _session;
    private readonly IBrokerInstrumentMaster _instrumentMaster;
    private readonly DhanSegmentRegistry _segmentRegistry;
    private readonly DhanAuthFailureHandler _authFailureHandler;
    private readonly ILogger<DhanMarketFeedClient> _logger;

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly HashSet<uint> _subscribedTokens = new();
    private readonly object _tokensLock = new();

    /// <summary>Last full tick per instrument, so a packet that carries only part of the picture
    /// (an OI or previous-close update, or a Quote when depth arrived separately) folds into what
    /// is already known instead of publishing a tick with the depth zeroed out. Without this, a
    /// single OI packet would blank the best bid/ask that spread evaluation runs on.</summary>
    private readonly ConcurrentDictionary<uint, Tick> _lastTick = new();

    private ClientWebSocket? _socket;

    public event Action<Tick>? TickReceived;
    public event Action? OrderUpdateReceived;

    public DhanMarketFeedClient(
        IOptions<DhanOptions> options,
        DhanSession session,
        IBrokerInstrumentMaster instrumentMaster,
        DhanSegmentRegistry segmentRegistry,
        DhanAuthFailureHandler authFailureHandler,
        ILogger<DhanMarketFeedClient> logger)
    {
        _options = options.Value;
        _session = session;
        _instrumentMaster = instrumentMaster;
        _segmentRegistry = segmentRegistry;
        _authFailureHandler = authFailureHandler;
        _logger = logger;
    }

    /// <summary>Subscribe request code for the configured ticker mode. Only Full (21) carries
    /// market depth, which is what spread triggers are evaluated against - see DhanOptions.TickerMode.</summary>
    private int SubscribeRequestCode => _options.TickerMode?.ToLowerInvariant() switch
    {
        "quote" => RequestCodeQuote,
        "ltp" or "ticker" => RequestCodeTicker,
        _ => RequestCodeFull,
    };

    /// <summary>The unsubscribe code paired with the current mode's subscribe code. Dhan's codes
    /// happen to run subscribe/unsubscribe in adjacent pairs, but this maps them explicitly rather
    /// than computing "subscribe + 1" - an arithmetic relationship the protocol never promises,
    /// and one that would silently send a wrong request code if Dhan ever adds a mode that breaks
    /// the pattern.</summary>
    private int UnsubscribeRequestCode => SubscribeRequestCode switch
    {
        RequestCodeQuote => RequestCodeUnsubscribeQuote,
        RequestCodeTicker => RequestCodeUnsubscribeTicker,
        _ => RequestCodeUnsubscribeFull,
    };

    public async Task SubscribeAsync(IEnumerable<uint> instrumentTokens, CancellationToken cancellationToken)
    {
        uint[] newTokens;
        lock (_tokensLock)
        {
            newTokens = instrumentTokens.Where(t => _subscribedTokens.Add(t)).ToArray();
        }

        if (newTokens.Length == 0 || _socket is not { State: WebSocketState.Open })
            return;

        await SendSubscriptionAsync(newTokens, SubscribeRequestCode, cancellationToken);
    }

    public async Task UnsubscribeAsync(IEnumerable<uint> instrumentTokens, CancellationToken cancellationToken)
    {
        uint[] removedTokens;
        lock (_tokensLock)
        {
            removedTokens = instrumentTokens.Where(t => _subscribedTokens.Remove(t)).ToArray();
        }

        if (removedTokens.Length == 0) return;

        // Drop the cached ticks too - otherwise the merge cache would keep a slot for every
        // instrument this process ever subscribed to, which is the same unbounded leak the
        // reconciliation loop exists to prevent on the subscription itself.
        foreach (var token in removedTokens) _lastTick.TryRemove(token, out _);

        if (_socket is not { State: WebSocketState.Open }) return;

        await SendSubscriptionAsync(removedTokens, UnsubscribeRequestCode, cancellationToken);
    }

    public IReadOnlyCollection<uint> GetSubscribedTokens()
    {
        lock (_tokensLock)
        {
            return _subscribedTokens.ToArray();
        }
    }

    /// <summary>Runs both the market feed and the order-update socket until cancelled. Each
    /// reconnects independently, so an order-socket outage never interrupts the tick stream that
    /// spread evaluation depends on, and vice versa.</summary>
    public async Task RunAsync(IReadOnlyCollection<uint> instrumentTokens, CancellationToken cancellationToken)
    {
        lock (_tokensLock)
        {
            foreach (var token in instrumentTokens) _subscribedTokens.Add(token);
        }

        await Task.WhenAll(
            RunMarketFeedAsync(cancellationToken),
            RunOrderUpdateFeedAsync(cancellationToken));
    }

    private async Task RunMarketFeedAsync(CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(1);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndSubscribeAsync(cancellationToken);
                backoff = TimeSpan.FromSeconds(1);
                await ReceiveLoopAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Dhan market feed connection lost, retrying in {Seconds}s", backoff.TotalSeconds);
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
            ?? throw new InvalidOperationException("No Dhan access token available - complete the login flow first.");
        var clientId = _session.ClientId
            ?? throw new InvalidOperationException("No Dhan client id available - set Dhan:ClientId.");

        // v2 authenticates entirely through the query string; the binary authorisation packet that
        // v1 required is not sent.
        var uri = new Uri(
            $"{_options.WsUrl}?version=2&token={Uri.EscapeDataString(accessToken)}" +
            $"&clientId={Uri.EscapeDataString(clientId)}&authType=2");

        await _socket.ConnectAsync(uri, cancellationToken);

        uint[] tokens;
        lock (_tokensLock)
        {
            tokens = _subscribedTokens.ToArray();
        }

        _logger.LogInformation(
            "Connected to Dhan market feed ({Count} instruments, mode={Mode})", tokens.Length, _options.TickerMode);

        if (tokens.Length > 0)
            await SendSubscriptionAsync(tokens, SubscribeRequestCode, cancellationToken);
    }

    /// <summary>Sends one subscribe/unsubscribe message per batch of 100, resolving each token's
    /// exchange segment from the instrument master along the way.</summary>
    private async Task SendSubscriptionAsync(IReadOnlyCollection<uint> tokens, int requestCode, CancellationToken cancellationToken)
    {
        var entries = ResolveSubscriptionEntries(tokens);
        if (entries.Count == 0) return;

        WarnIfOverConnectionLimit(requestCode);

        var isFirst = true;
        foreach (var batch in entries.Chunk(SubscriptionBatchSize))
        {
            if (!isFirst && !await DelayAsync(InterBatchDelay, cancellationToken)) return;
            isFirst = false;

            await SendAsync(new
            {
                RequestCode = requestCode,
                InstrumentCount = batch.Length,
                InstrumentList = batch.Select(e => new { e.ExchangeSegment, e.SecurityId }).ToArray(),
            }, cancellationToken);
        }
    }

    /// <summary>Warns once per subscribe attempt when the total subscribed set has grown past what
    /// one Dhan connection can carry. Deliberately a warning rather than a truncation: silently
    /// dropping instruments would be indistinguishable, from the engine's side, from a contract
    /// that simply isn't trading, whereas Dhan's own 804 rejection at least surfaces in the
    /// disconnect log next to this message. Only checked on subscribe - an unsubscribe can only
    /// take the count down.</summary>
    private void WarnIfOverConnectionLimit(int requestCode)
    {
        if (requestCode is RequestCodeUnsubscribeTicker or RequestCodeUnsubscribeQuote or RequestCodeUnsubscribeFull)
            return;

        int total;
        lock (_tokensLock)
        {
            total = _subscribedTokens.Count;
        }

        if (total <= MaxInstrumentsPerConnection) return;

        _logger.LogError(
            "Subscribed instrument count ({Count}) exceeds Dhan's per-connection limit of {Limit}. " +
            "Dhan will reject the surplus (error 804) and those instruments will never tick, so any spread using them will sit stale. " +
            "Reduce the streamed universe, or shard across additional feed connections.",
            total, MaxInstrumentsPerConnection);
    }

    private readonly record struct SubscriptionEntry(string ExchangeSegment, string SecurityId);

    /// <summary>Maps instrument tokens to the (segment, securityId) pairs Dhan's subscription
    /// message needs. A token the instrument master doesn't know is skipped rather than guessed
    /// at - subscribing it under the wrong segment would either fail silently or, worse, stream a
    /// completely different contract that happens to share the id in another segment.</summary>
    private List<SubscriptionEntry> ResolveSubscriptionEntries(IReadOnlyCollection<uint> tokens)
    {
        var segments = BuildSegmentLookup();
        var entries = new List<SubscriptionEntry>(tokens.Count);
        var unresolved = new List<uint>();

        foreach (var token in tokens)
        {
            if (segments.TryGetValue(token, out var segment) || _segmentRegistry.TryGet(token, out segment))
                entries.Add(new SubscriptionEntry(segment, token.ToString()));
            else
                unresolved.Add(token);
        }

        if (unresolved.Count > 0)
        {
            _logger.LogWarning(
                "Skipped {Count} instrument token(s) with no entry in the Dhan scrip master, so their exchange segment is unknown: {Tokens}. " +
                "They will not stream until the instrument master is refreshed.",
                unresolved.Count, string.Join(", ", unresolved.Take(20)));
        }

        return entries;
    }

    /// <summary>Token -> Dhan exchange segment for everything currently subscribable. The futures
    /// instrument master answers for every spread leg; DhanSegmentRegistry fills in tokens it
    /// cannot know about - notably an open position in something that is not a future in that
    /// master, which PositionsCache still anchors a subscription for. See DhanSegmentRegistry for
    /// why the master alone is not enough.</summary>
    private Dictionary<uint, string> BuildSegmentLookup()
    {
        var lookup = new Dictionary<uint, string>();
        foreach (var instrument in _instrumentMaster.GetAllFutures())
            lookup[instrument.InstrumentToken] = instrument.Segment;

        // Cash/index instruments too, so a spread's spot leg actually streams. Without these a
        // spot token has no known segment and is dropped by the caller as unsubscribable - the
        // price would simply never arrive. Added after the futures so a futures token always wins
        // a (theoretical) id collision, since futures are what actually get traded.
        foreach (var instrument in _instrumentMaster.GetAllSpot())
            lookup.TryAdd(instrument.InstrumentToken, instrument.Segment);

        return lookup;
    }

    private async Task SendAsync(object message, CancellationToken cancellationToken)
    {
        if (_socket is not { State: WebSocketState.Open }) return;
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

            if (result.MessageType != WebSocketMessageType.Binary)
                continue; // v2 sends market data as binary only; anything else is not a tick

            var packets = DhanTickPacketParser.ParseMessage(
                messageStream.GetBuffer().AsSpan(0, (int)messageStream.Length));

            foreach (var packet in packets)
                await HandlePacketAsync(packet, cancellationToken);
        }
    }

    private async Task HandlePacketAsync(DhanPacket packet, CancellationToken cancellationToken)
    {
        if (packet.Kind == DhanPacketKind.Disconnect)
        {
            LogDisconnect(packet.DisconnectCode);
            await _authFailureHandler.HandleIfFeedTokenExpiredAsync(packet.DisconnectCode, cancellationToken);
            return;
        }

        if (packet.Kind is DhanPacketKind.MarketStatus or DhanPacketKind.Unknown)
            return;

        var handler = TickReceived;
        if (handler is null) return;

        // Only cache instruments we still want. Without this check the merge cache grows without
        // bound over a long-running process: UnsubscribeAsync drops an instrument's entry, but a
        // packet already in flight for it re-adds one moments later, and nothing ever removes that
        // second entry - it belongs to a token no future unsubscribe will name. Across months of
        // expiry rolls and universe rebuilds (this process is meant to run for weeks - see
        // ARCHITECTURE.md §8) that is a slow, permanent leak of one Tick per retired contract.
        bool stillSubscribed;
        lock (_tokensLock)
        {
            stillSubscribed = _subscribedTokens.Contains(packet.SecurityId);
        }

        var tick = Merge(packet);
        if (stillSubscribed) _lastTick[packet.SecurityId] = tick;
        else _lastTick.TryRemove(packet.SecurityId, out _);

        handler.Invoke(tick);
    }

    /// <summary>Folds a packet into the last known tick for that instrument, so each packet kind
    /// contributes only what it actually carries. This is what lets a Full packet supply depth
    /// while a later OI or previous-close packet updates its own field without blanking the bid/ask
    /// that spread evaluation reads.</summary>
    private Tick Merge(DhanPacket packet)
    {
        var previous = _lastTick.TryGetValue(packet.SecurityId, out var existing) ? existing : default;
        var now = DateTimeOffset.UtcNow;

        return new Tick(
            InstrumentToken: packet.SecurityId,
            LastPrice: packet.LastPrice > 0 ? packet.LastPrice : previous.LastPrice,
            LastTradedQuantity: packet.LastTradedQuantity > 0 ? packet.LastTradedQuantity : previous.LastTradedQuantity,
            Volume: packet.Volume > 0 ? packet.Volume : previous.Volume,
            Open: packet.Open > 0 ? packet.Open : previous.Open,
            High: packet.High > 0 ? packet.High : previous.High,
            Low: packet.Low > 0 ? packet.Low : previous.Low,
            Close: packet.Close > 0 ? packet.Close : previous.Close,
            OpenInterest: packet.OpenInterest > 0 ? packet.OpenInterest : previous.OpenInterest,
            ExchangeTimestamp: packet.ExchangeTimestamp > DateTimeOffset.UnixEpoch ? packet.ExchangeTimestamp : now,
            ReceivedAt: now,
            // Depth only ever arrives on Full and Depth packets; carry the previous values forward
            // on every other kind rather than resetting to zero.
            BestBidPrice: packet.BestBidPrice > 0 ? packet.BestBidPrice : previous.BestBidPrice,
            BestAskPrice: packet.BestAskPrice > 0 ? packet.BestAskPrice : previous.BestAskPrice);
    }

    private void LogDisconnect(int code) =>
        _logger.LogWarning(
            "Dhan market feed sent a disconnect packet (code {Code}): {Reason}.",
            code, DhanSymbology.DescribeFeedErrorCode(code));

    // ---- Order updates --------------------------------------------------------------------------

    /// <summary>Dhan's order-update socket: a JSON login frame, then a JSON message per order state
    /// change. Only the fact that SOMETHING changed is surfaced - see
    /// IMarketDataFeed.OrderUpdateReceived. Failures here are logged and retried but never
    /// propagate: a missed order update costs PositionsCache one early refresh, since it also
    /// polls on its own timer regardless.</summary>
    private async Task RunOrderUpdateFeedAsync(CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(2);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ReceiveOrderUpdatesAsync(cancellationToken);
                backoff = TimeSpan.FromSeconds(2);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Dhan order-update socket disconnected, retrying in {Seconds}s (positions still refresh on their own timer meanwhile)",
                    backoff.TotalSeconds);
                if (!await DelayAsync(backoff, cancellationToken)) break;
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
            }
        }
    }

    private async Task ReceiveOrderUpdatesAsync(CancellationToken cancellationToken)
    {
        var accessToken = _session.AccessToken;
        var clientId = _session.ClientId;
        if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(clientId))
        {
            // Not logged in yet (or logged out). Wait rather than hammering a connection that
            // cannot authenticate - the market feed loop will surface the auth problem itself.
            await DelayAsync(TimeSpan.FromSeconds(5), cancellationToken);
            return;
        }

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(_options.OrderUpdateWsUrl), cancellationToken);

        var login = JsonSerializer.SerializeToUtf8Bytes(new
        {
            LoginReq = new { MsgCode = 42, ClientId = clientId, Token = accessToken },
            UserType = "SELF",
        });
        await socket.SendAsync(new ArraySegment<byte>(login), WebSocketMessageType.Text, true, cancellationToken);

        _logger.LogInformation("Connected to the Dhan order-update socket.");

        var buffer = new byte[8192];
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
                    return;
                }
                messageStream.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Text)
                HandleOrderUpdateMessage(messageStream.GetBuffer().AsMemory(0, (int)messageStream.Length));
        }
    }

    private void HandleOrderUpdateMessage(ReadOnlyMemory<byte> bytes)
    {
        var handler = OrderUpdateReceived;
        if (handler is null) return;

        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.TryGetProperty("Type", out var type) &&
                string.Equals(type.GetString(), "order_alert", StringComparison.OrdinalIgnoreCase))
            {
                handler.Invoke();
            }
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Ignoring unparseable Dhan order-update frame.");
        }
    }

    /// <summary>Cancellable delay that reports whether it completed rather than throwing, so the
    /// reconnect loops can exit cleanly on shutdown.</summary>
    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket is { State: WebSocketState.Open })
        {
            try
            {
                // Tell Dhan we're going away before dropping the socket, so the connection is
                // released against the account's connection cap immediately rather than lingering.
                await SendAsync(new { RequestCode = RequestCodeDisconnect }, CancellationToken.None);
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "disposing", CancellationToken.None);
            }
            catch
            {
                // best-effort close
            }
        }

        _socket?.Dispose();

        // _sendLock is deliberately NOT disposed. This client is a DI singleton that gets
        // STOPPED AND RESTARTED rather than thrown away: SwitchingMarketDataFeed disposes the
        // outgoing platform's feed on every broker switch and then calls RunAsync on that same
        // instance again if the user ever switches back (see its RunAsync loop). Disposing the
        // semaphore here made that second run throw ObjectDisposedException on its first send,
        // silently killing the feed for the rest of the process. The socket above is the real
        // unmanaged resource and is still closed and disposed; ConnectAndSubscribeAsync creates a
        // fresh one on the next run, so nothing leaks by leaving this alone.
    }
}
