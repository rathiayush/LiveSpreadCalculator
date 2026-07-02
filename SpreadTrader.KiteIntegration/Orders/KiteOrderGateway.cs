using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;
using SpreadTrader.KiteIntegration;

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
    private readonly ILogger<KiteOrderGateway> _logger;

    public KiteOrderGateway(HttpClient httpClient, IOptions<KiteOptions> options, KiteSession session, RateLimiter rateLimiter, ILogger<KiteOrderGateway> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _session = session;
        _logger = logger;
        // Injected as a singleton (see Program.cs) rather than constructed here, so pacing
        // state is shared even if this gateway is resolved as a fresh instance per call.
        _rateLimiter = rateLimiter;
    }

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
            ["validity"] = "DAY",
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

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (string.Equals(root.GetProperty("status").GetString(), "success", StringComparison.Ordinal))
            {
                var orderId = root.GetProperty("data").GetProperty("order_id").GetString();
                _logger.LogInformation("Order placed: {Symbol} {Side} x{Qty} -> {OrderId}",
                    leg.TradingSymbol, leg.TransactionType, leg.Quantity, orderId);
                return new OrderResult { Success = true, OrderId = orderId };
            }

            var message = root.TryGetProperty("message", out var m) ? m.GetString() : body;
            _logger.LogWarning("Order rejected: {Symbol} {Side} x{Qty} - {Message}",
                leg.TradingSymbol, leg.TransactionType, leg.Quantity, message);
            return new OrderResult { Success = false, ErrorMessage = message };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Order placement failed: {Symbol} {Side} x{Qty}", leg.TradingSymbol, leg.TransactionType, leg.Quantity);
            return new OrderResult { Success = false, ErrorMessage = ex.Message };
        }
    }
}
