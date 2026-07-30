using System.Text.Json;
using Microsoft.Extensions.Logging;
using SpreadTrader.Core.Brokers;
using SpreadTrader.Core.Models;

namespace SpreadTrader.KotakIntegration.Orders;

/// <summary>
/// Pre-trade margin checks against Kotak Neo - BASKET MARGIN via POST quick/user/check-margin,
/// AVAILABLE MARGIN via GET quick/user/limits.
///
/// Like KiteMarginService/DhanMarginService, a failed margin CHECK is reported as failure rather
/// than a pessimistic estimate - OrderDispatchHostedService treats that as non-fatal and proceeds
/// (Kotak's own order entry still enforces margin), which is the safer of the two wrong answers
/// when the check itself can't be trusted.
///
/// RESPONSE FIELD NAMES ARE BEST-EFFORT - see KotakApiClient's doc comment on why this whole
/// integration is unverified against a live account. Several plausible field-name candidates are
/// tried for the margin figures; a response with none of them is treated as a failed check (see
/// GetBasketMarginAsync/GetAvailableMarginAsync), never as "zero margin required/available".
/// </summary>
public sealed class KotakMarginService : IMarginService
{
    private readonly KotakApiClient _api;
    private readonly ILogger<KotakMarginService> _logger;

    public KotakMarginService(KotakApiClient api, ILogger<KotakMarginService> logger)
    {
        _api = api;
        _logger = logger;
    }

    public async Task<BasketMarginResult> GetBasketMarginAsync(IReadOnlyList<OrderLegRequest> legs, CancellationToken cancellationToken)
    {
        if (!_api.HasSession)
            return new BasketMarginResult(false, null, null, "No Kotak session - complete the login flow first.");

        // Kotak's check-margin endpoint, per the reference SDK's form-encoded convention, takes one
        // leg's fields per call rather than a documented multi-leg basket shape - unlike Kite's
        // /margins/basket or Dhan's /margincalculator/multi, no confirmed single-call "whole
        // basket with hedge benefit" endpoint was found while building this (see the class doc
        // comment). Summed per-leg margins therefore do NOT include any hedge/spread benefit
        // between offsetting legs, so this will over-report what a calendar spread actually needs
        // until a real basket endpoint is confirmed - flagged rather than silently assumed correct.
        decimal total = 0m;
        foreach (var leg in legs)
        {
            var fields = new Dictionary<string, string>
            {
                ["es"] = KotakSymbology.ToKotakSegment(leg.Exchange),
                ["pc"] = KotakSymbology.ToKotakProduct(leg.Product),
                ["pr"] = (leg.Price ?? 0m).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["pt"] = KotakSymbology.ToKotakOrderType(leg.OrderType),
                ["qt"] = leg.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["tt"] = KotakSymbology.ToKotakTransactionType(leg.TransactionType),
                ["ts"] = leg.TradingSymbol,
            };

            using var response = await _api.SendSessionAsync(
                HttpMethod.Post, "quick/user/check-margin", fields, $"checking margin for {leg.TradingSymbol}", cancellationToken);

            if (!response.Success)
            {
                _logger.LogWarning("Margin check failed for {Symbol}: {Message}", leg.TradingSymbol, response.ErrorMessage);
                return new BasketMarginResult(false, null, null, response.ErrorMessage);
            }

            var legMargin = ReadDecimal(response.Data, "marginUsed") ?? ReadDecimal(response.Data, "totalMargin") ?? ReadDecimal(response.Data, "margin");
            if (legMargin is null)
            {
                const string message = "Kotak's margin check response carried no recognisable margin figure - treating the check as failed rather than as 'no margin required'.";
                _logger.LogWarning("{Message} Response: {Response}", message, Truncate(response.Data.ToString()));
                return new BasketMarginResult(false, null, null, message);
            }

            total += legMargin.Value;
        }

        // No hedge benefit is known here (see the comment above), so initial and final report the
        // same un-benefited sum rather than fabricating a benefit figure.
        return new BasketMarginResult(true, total, total, null);
    }

    public async Task<AvailableMarginResult> GetAvailableMarginAsync(string segment = "equity", CancellationToken cancellationToken = default)
    {
        if (!_api.HasSession)
            return new AvailableMarginResult(false, null, "No Kotak session - complete the login flow first.");

        using var response = await _api.SendSessionAsync(
            HttpMethod.Get, "quick/user/limits", null, "checking available margin", cancellationToken);

        if (!response.Success)
        {
            _logger.LogWarning("Available margin check failed: {Message}", response.ErrorMessage);
            return new AvailableMarginResult(false, null, response.ErrorMessage);
        }

        var available = ReadDecimal(response.Data, "Net") ?? ReadDecimal(response.Data, "netAvailableMargin")
            ?? ReadDecimal(response.Data, "availableMargin") ?? ReadDecimal(response.Data, "cash");

        return new AvailableMarginResult(
            available is not null,
            available,
            available is null ? "Kotak's limits response had no recognisable available-margin figure." : null);
    }

    private static decimal? ReadDecimal(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static string Truncate(string value) => value.Length <= 300 ? value : value[..300] + "...";
}
