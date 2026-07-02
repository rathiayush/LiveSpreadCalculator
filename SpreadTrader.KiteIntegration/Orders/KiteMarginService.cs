using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Models;

namespace SpreadTrader.KiteIntegration.Orders;

/// <summary>
/// Calls Kite's basket margin API (POST /margins/basket?consider_positions=true&amp;mode=compact)
/// to find out what a basket of order legs would actually cost in margin, with the exchange's
/// hedge/spread benefit applied when the legs offset each other (e.g. a near/far calendar spread
/// on the same underlying) - "final" is that combined, benefit-applied figure; "initial" is what
/// it would cost with no offsetting positions considered.
/// https://kite.trade/docs/connect/v3/margins/#basket-margins
///
/// This is informational only - see OrderDispatchHostedService, which logs the result but does
/// not block order placement on it (Kite's own order entry will reject an order if margin is
/// genuinely insufficient, so a failed/short margin check here is not treated as fatal).
/// </summary>
public sealed class KiteMarginService
{
    private readonly HttpClient _httpClient;
    private readonly KiteOptions _options;
    private readonly KiteSession _session;
    private readonly ILogger<KiteMarginService> _logger;

    public KiteMarginService(HttpClient httpClient, IOptions<KiteOptions> options, KiteSession session, ILogger<KiteMarginService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _session = session;
        _logger = logger;
    }

    public async Task<BasketMarginResult> GetBasketMarginAsync(IReadOnlyList<OrderLegRequest> legs, CancellationToken cancellationToken)
    {
        var accessToken = _session.AccessToken;
        if (string.IsNullOrEmpty(accessToken))
            return new BasketMarginResult(false, null, null, "No Kite access token - complete the login flow first.");

        var payload = legs.Select(leg => new
        {
            exchange = leg.Exchange,
            tradingsymbol = leg.TradingSymbol,
            transaction_type = leg.TransactionType,
            variety = leg.Variety,
            product = leg.Product,
            order_type = leg.OrderType,
            quantity = leg.Quantity,
            price = leg.Price ?? 0m,
        }).ToArray();

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{_options.RestBaseUrl}/margins/basket?consider_positions=true&mode=compact")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Add("X-Kite-Version", "3");
        request.Headers.Authorization = new AuthenticationHeaderValue("token", $"{_options.ApiKey}:{accessToken}");

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!string.Equals(root.GetProperty("status").GetString(), "success", StringComparison.Ordinal))
            {
                var message = root.TryGetProperty("message", out var m) ? m.GetString() : body;
                return new BasketMarginResult(false, null, null, message);
            }

            var data = root.GetProperty("data");
            var initial = ReadTotal(data, "initial");
            var final = ReadTotal(data, "final");
            return new BasketMarginResult(true, initial, final, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Basket margin check failed for {LegCount} leg(s)", legs.Count);
            return new BasketMarginResult(false, null, null, ex.Message);
        }
    }

    private static decimal? ReadTotal(JsonElement data, string section)
    {
        if (!data.TryGetProperty(section, out var sectionElement)) return null;
        if (!sectionElement.TryGetProperty("total", out var totalElement)) return null;
        return totalElement.TryGetDecimal(out var value) ? value : null;
    }
}
