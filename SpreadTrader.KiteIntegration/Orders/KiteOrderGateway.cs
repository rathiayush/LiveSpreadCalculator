using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;
using SpreadTrader.KiteIntegration;
using SpreadTrader.KiteIntegration.Auth;

namespace SpreadTrader.KiteIntegration.Orders;

/// <summary>
/// Places orders via Kite's REST order API (POST /orders/:variety). Paced by a RateLimiter to
/// stay under Kite's documented ~3-5 req/sec order placement ceiling - this matters when a
/// trigger fires a multi-leg basket, since legs are dispatched one call at a time.
/// https://kite.trade/docs/connect/v3/orders/#placing-orders
/// </summary>
public sealed class KiteOrderGateway : IOrderGateway
{
    private readonly HttpClient _httpClient;
    private readonly KiteOptions _options;
    private readonly KiteSession _session;
    private readonly RateLimiter _rateLimiter;
    private readonly KiteAuthFailureHandler _authFailureHandler;
    private readonly ILogger<KiteOrderGateway> _logger;

    public KiteOrderGateway(
        HttpClient httpClient,
        IOptions<KiteOptions> options,
        KiteSession session,
        RateLimiter rateLimiter,
        KiteAuthFailureHandler authFailureHandler,
        ILogger<KiteOrderGateway> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _session = session;
        _logger = logger;
        _authFailureHandler = authFailureHandler;
        // Injected as a singleton (see Program.cs) rather than constructed here, so pacing
        // state is shared even if this gateway is resolved as a fresh instance per call.
        _rateLimiter = rateLimiter;
    }

    /// <summary>How many times to poll GET /orders/{order_id} after placement to find out
    /// whether it actually filled, and how long to wait between attempts. IOC orders (see
    /// OrderLegRequest.Validity) resolve to a terminal status almost immediately - Kite/the
    /// exchange either fills what it can and cancels the rest, or rejects it outright - so this
    /// is a short poll, not a long one. A DAY order that's still resting when the poll gives up
    /// is reported back as Status="OPEN"/Success=false (not yet a confirmed fill), which is the
    /// honest answer rather than guessing either way.</summary>
    private const int StatusPollAttempts = 4;
    private static readonly TimeSpan StatusPollDelay = TimeSpan.FromMilliseconds(300);

    private static readonly HashSet<string> TerminalStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "COMPLETE", "REJECTED", "CANCELLED",
    };

    public async Task<OrderResult> PlaceOrderAsync(OrderLegRequest leg, CancellationToken cancellationToken)
    {
        await _rateLimiter.WaitAsync(cancellationToken);

        var accessToken = _session.AccessToken;
        if (string.IsNullOrEmpty(accessToken))
            return new OrderResult { Success = false, ErrorMessage = "No Kite access token - complete the login flow first." };

        var form = new Dictionary<string, string>
        {
            ["tradingsymbol"] = leg.TradingSymbol,
            ["exchange"] = leg.Exchange,
            ["transaction_type"] = leg.TransactionType,
            ["order_type"] = leg.OrderType,
            ["quantity"] = leg.Quantity.ToString(CultureInfo.InvariantCulture),
            ["product"] = leg.Product,
            ["validity"] = leg.Validity,
        };
        if (leg.Price is { } price)
            form["price"] = price.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrEmpty(leg.Tag))
            form["tag"] = leg.Tag;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.RestBaseUrl}/orders/{leg.Variety}")
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Add("X-Kite-Version", "3");
        request.Headers.Authorization = new AuthenticationHeaderValue("token", $"{_options.ApiKey}:{accessToken}");

        string? orderId;
        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!string.Equals(root.GetProperty("status").GetString(), "success", StringComparison.Ordinal))
            {
                var message = root.TryGetProperty("message", out var m) ? m.GetString() : body;
                await _authFailureHandler.HandleIfTokenExceptionAsync(root, $"placing order for {leg.TradingSymbol}", cancellationToken);
                _logger.LogWarning("Order rejected: {Symbol} {Side} x{Qty} - {Message}",
                    leg.TradingSymbol, leg.TransactionType, leg.Quantity, message);
                return new OrderResult { Success = false, ErrorMessage = message, Status = "REJECTED" };
            }

            orderId = root.GetProperty("data").GetProperty("order_id").GetString();
            _logger.LogInformation("Order accepted: {Symbol} {Side} x{Qty} -> {OrderId}, checking fill status...",
                leg.TradingSymbol, leg.TransactionType, leg.Quantity, orderId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Order placement failed: {Symbol} {Side} x{Qty}", leg.TradingSymbol, leg.TransactionType, leg.Quantity);
            return new OrderResult { Success = false, ErrorMessage = ex.Message };
        }

        // Acceptance just means Kite queued it for processing - it says nothing about whether
        // it actually filled (a resting DAY order could sit open for hours; even an IOC order's
        // outcome isn't in the placement response). Poll its status/history briefly to find out
        // for real - this is what lets AutoSquareOffOnPartialFill react to "accepted but never
        // filled" instead of only to outright rejections.
        return await PollOrderStatusAsync(orderId, leg, cancellationToken);
    }

    private async Task<OrderResult> PollOrderStatusAsync(string? orderId, OrderLegRequest leg, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(orderId))
            return new OrderResult { Success = false, ErrorMessage = "Order accepted but no order_id was returned - cannot verify fill status." };

        var accessToken = _session.AccessToken;
        for (var attempt = 1; attempt <= StatusPollAttempts; attempt++)
        {
            await Task.Delay(StatusPollDelay, cancellationToken);
            await _rateLimiter.WaitAsync(cancellationToken);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{_options.RestBaseUrl}/orders/{orderId}");
                request.Headers.Add("X-Kite-Version", "3");
                request.Headers.Authorization = new AuthenticationHeaderValue("token", $"{_options.ApiKey}:{accessToken}");

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (!string.Equals(root.GetProperty("status").GetString(), "success", StringComparison.Ordinal))
                {
                    if (await _authFailureHandler.HandleIfTokenExceptionAsync(root, $"polling status of order {orderId}", cancellationToken))
                        break; // token's dead - further polls would just fail the same way, stop early
                    continue; // otherwise a transient API hiccup on the status check itself - retry
                }

                // /orders/{order_id} returns the full status history for that order, oldest
                // first - the last entry is its current/latest known status.
                var updates = root.GetProperty("data");
                var count = updates.GetArrayLength();
                if (count == 0) continue;

                var latest = updates[count - 1];
                var status = latest.TryGetProperty("status", out var s) ? s.GetString() : null;
                var filledQuantity = latest.TryGetProperty("filled_quantity", out var fq) && fq.TryGetInt32(out var fqv) ? fqv : 0;
                var rejectionReason = latest.TryGetProperty("status_message", out var sm) ? sm.GetString() : null;

                if (status is not null && TerminalStatuses.Contains(status))
                {
                    var success = filledQuantity > 0;
                    _logger.LogInformation(
                        "Order {OrderId} ({Symbol} {Side}) reached {Status}: {Filled}/{Requested} filled",
                        orderId, leg.TradingSymbol, leg.TransactionType, status, filledQuantity, leg.Quantity);
                    return new OrderResult
                    {
                        Success = success,
                        OrderId = orderId,
                        FilledQuantity = filledQuantity,
                        Status = status,
                        ErrorMessage = success ? null : (rejectionReason ?? $"Order ended as {status} with no fill."),
                    };
                }
                // Still OPEN/pending (or TRIGGER PENDING etc.) - keep polling until attempts run out.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Order status check failed for {OrderId} (attempt {Attempt}/{Max}) - retrying",
                    orderId, attempt, StatusPollAttempts);
            }
        }

        // Gave up before seeing a terminal status - report the honest "don't actually know if
        // this filled" state rather than assuming success. Callers should treat this the same
        // as a failure for square-off purposes (nothing confirmed filled to flatten) but the
        // order may still be live - see the ErrorMessage.
        _logger.LogWarning(
            "Order {OrderId} ({Symbol} {Side}) status still not terminal after {Max} checks - treating as unconfirmed, may still be open on Kite.",
            orderId, leg.TradingSymbol, leg.TransactionType, StatusPollAttempts);
        return new OrderResult
        {
            Success = false,
            OrderId = orderId,
            Status = "OPEN",
            ErrorMessage = "Could not confirm fill status in time - order may still be open on Kite; check manually.",
        };
    }

    public async Task<bool> CancelOrderAsync(string orderId, string variety, CancellationToken cancellationToken)
    {
        await _rateLimiter.WaitAsync(cancellationToken);

        var accessToken = _session.AccessToken;
        if (string.IsNullOrEmpty(accessToken))
        {
            _logger.LogWarning("Cannot cancel order {OrderId} - no Kite access token.", orderId);
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"{_options.RestBaseUrl}/orders/{variety}/{orderId}");
            request.Headers.Add("X-Kite-Version", "3");
            request.Headers.Authorization = new AuthenticationHeaderValue("token", $"{_options.ApiKey}:{accessToken}");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var success = string.Equals(root.GetProperty("status").GetString(), "success", StringComparison.Ordinal);

            if (success)
                _logger.LogWarning("Cancelled stray open order {OrderId} (status couldn't be confirmed as filled/rejected in time).", orderId);
            else
            {
                await _authFailureHandler.HandleIfTokenExceptionAsync(root, $"cancelling order {orderId}", cancellationToken);
                _logger.LogError("Failed to cancel order {OrderId}: {Body}", orderId, body);
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel order {OrderId}", orderId);
            return false;
        }
    }
}
