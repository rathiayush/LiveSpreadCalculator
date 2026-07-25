using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Brokers;
using SpreadTrader.Core.Models;
using SpreadTrader.KiteIntegration.Auth;

namespace SpreadTrader.KiteIntegration.Orders;

/// <summary>
/// Calls Kite's basket margin API (POST /margins/basket?consider_positions=true&amp;mode=compact)
/// to find out what a basket of order legs would actually cost in margin, with the exchange's
/// hedge/spread benefit applied when the legs offset each other (e.g. a near/far calendar spread
/// on the same underlying) - "final" is that combined, benefit-applied figure; "initial" is what
/// it would cost with no offsetting positions considered.
/// https://kite.trade/docs/connect/v3/margins/#basket-margins
///
/// consider_positions=true makes Kite factor in the account's EXISTING open positions/orders, so
/// "final" is the incremental margin the basket needs on top of what's already there - the correct
/// figure to compare against available margin (which is likewise already net of existing blocks).
///
/// When EngineOptions.VerifyMarginBeforeDispatch is on, OrderDispatchHostedService.CheckMarginAsync
/// DOES block dispatch on this: a confirmed-insufficient result stops the basket entirely (nothing
/// is placed), so one leg can't go through while the other is rejected for margin. A failed *check*
/// (network/API hiccup), on the other hand, is not treated as fatal - Kite's own order entry still
/// enforces margin regardless - so dispatch proceeds in that case.
/// </summary>
public sealed class KiteMarginService : IMarginService
{
    private readonly HttpClient _httpClient;
    private readonly KiteOptions _options;
    private readonly KiteSession _session;
    private readonly KiteAuthFailureHandler _authFailureHandler;
    private readonly ILogger<KiteMarginService> _logger;

    public KiteMarginService(
        HttpClient httpClient,
        IOptions<KiteOptions> options,
        KiteSession session,
        KiteAuthFailureHandler authFailureHandler,
        ILogger<KiteMarginService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _session = session;
        _authFailureHandler = authFailureHandler;
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
                await _authFailureHandler.HandleIfTokenExceptionAsync(root, "checking basket margin", cancellationToken);
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

    /// <summary>Calls GET /user/margins/{segment} for the account's actual usable margin right
    /// now (cash + collateral - what's already blocked by open positions/orders) - "net" in
    /// Kite's response, which is the same figure Kite Web shows as "Margin available". F&amp;O
    /// (NFO) instruments settle against the "equity" segment's margin, not "commodity" (that's
    /// MCX only), so the default here is what OrderDispatchHostedService's pre-trade check
    /// actually needs. https://kite.trade/docs/connect/v3/user/#funds-and-margins
    /// </summary>
    public async Task<AvailableMarginResult> GetAvailableMarginAsync(string segment = "equity", CancellationToken cancellationToken = default)
    {
        var accessToken = _session.AccessToken;
        if (string.IsNullOrEmpty(accessToken))
            return new AvailableMarginResult(false, null, "No Kite access token - complete the login flow first.");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_options.RestBaseUrl}/user/margins/{segment}");
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
                await _authFailureHandler.HandleIfTokenExceptionAsync(root, "checking available margin", cancellationToken);
                return new AvailableMarginResult(false, null, message);
            }

            var data = root.GetProperty("data");
            var net = data.TryGetProperty("net", out var netElement) && netElement.TryGetDecimal(out var netValue)
                ? netValue
                : (decimal?)null;
            return new AvailableMarginResult(net is not null, net, net is null ? "Response had no 'net' margin figure" : null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Available margin check failed for segment {Segment}", segment);
            return new AvailableMarginResult(false, null, ex.Message);
        }
    }
}
