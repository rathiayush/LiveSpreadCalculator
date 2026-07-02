using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
public sealed class KiteTickerClient : IAsyncDisposable
{
    private readonly KiteOptions _options;
    private readonly KiteSession _session;
    private readonly ILogger<KiteTickerClient> _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly HashSet<uint> _subscribedTokens = new();
    private readonly object _tokensLock = new();
    private ClientWebSocket? _socket;

    public event Action<Tick>? TickReceived;

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

    /// <summary>Runs until cancellationToken is cancelled, reconnecting on any failure.</summary>
    public async Task RunAsync(IReadOnlyCollection<uint> instrumentTokens, CancellationToken cancellationToken)
    {
        lock (_tokensLock)
        {
            foreach (var token in instrumentTokens) _subscribedTokens.Add(token);
        }

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

    private async Task ConnectAndSubscribeAsync(CancellationToken cancellationToken)
    {
        _socket?.Dispose();
        _socket = new ClientWebSocket();

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

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
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
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "server closed", cancellationToken);
                    return;
                }
                messageStream.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Binary || TickReceived is null)
                continue; // heartbeats and text postbacks (order updates/alerts) are not handled here

            var ticks = KiteTickPacketParser.ParseMessage(messageStream.GetBuffer().AsSpan(0, (int)messageStream.Length));
            foreach (var tick in ticks)
                TickReceived.Invoke(tick);
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
