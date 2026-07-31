using System.Text.Json;
using Microsoft.Extensions.Logging;
using SpreadTrader.Core.Brokers;
using SpreadTrader.Core.Models;

namespace SpreadTrader.KotakIntegration.Orders;

/// <summary>
/// Pre-trade margin checks against Kotak Neo - BASKET MARGIN via POST quick/user/check-margin,
/// AVAILABLE MARGIN via POST quick/user/limits. Both endpoints and their jData/response shapes are
/// now CONFIRMED against Kotak's own Trade API docs (Margins / Limits pages) - previously these
/// were reverse-engineered guesses; several things were actually wrong (see git history for this
/// file): check-margin's field names are NOT the same abbreviations the order-placement endpoint
/// uses (it wants brkName/brnchId/exSeg/prc/prcTp/prod/qty/tok/trnsTp, a different vocabulary from
/// es/pc/pr/pt/qt/tt/ts), and limits is a POST with a jData body, not a bodyless GET.
///
/// Like KiteMarginService/DhanMarginService, a failed margin CHECK is reported as failure rather
/// than a pessimistic estimate - OrderDispatchHostedService treats that as non-fatal and proceeds
/// (Kotak's own order entry still enforces margin), which is the safer of the two wrong answers
/// when the check itself can't be trusted.
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
            // Field names/values confirmed against Kotak's Margins doc - a different abbreviation
            // vocabulary from the order-placement endpoint (brkName/brnchId are fixed constants
            // every caller sends verbatim; tok is the numeric instrument token, not tradingSymbol).
            var fields = new Dictionary<string, string>
            {
                ["brkName"] = "KOTAK",
                ["brnchId"] = "ONLINE",
                ["exSeg"] = KotakSymbology.ToKotakSegment(leg.Exchange),
                ["prc"] = (leg.Price ?? 0m).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["prcTp"] = KotakSymbology.ToKotakOrderType(leg.OrderType),
                ["prod"] = KotakSymbology.ToKotakProduct(leg.Product),
                ["qty"] = leg.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["tok"] = leg.InstrumentToken.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["trnsTp"] = KotakSymbology.ToKotakTransactionType(leg.TransactionType),
            };

            using var response = await _api.SendSessionAsync(
                HttpMethod.Post, "quick/user/check-margin", fields, $"checking margin for {leg.TradingSymbol}", cancellationToken);

            if (!response.Success)
            {
                _logger.LogWarning("Margin check failed for {Symbol}: {Message}", leg.TradingSymbol, response.ErrorMessage);
                return new BasketMarginResult(false, null, null, response.ErrorMessage);
            }

            // reqdMrgn ("Net required margin for the order") and ordMrgn ("Margin required for the
            // order") are both confirmed response fields - reqdMrgn is preferred since it's the net
            // figure after any margin benefit, same as every other broker's margin check here uses.
            var legMargin = ReadDecimal(response.Data, "reqdMrgn") ?? ReadDecimal(response.Data, "ordMrgn");
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

        // Confirmed by Kotak's Limits doc: POST (NOT a bodyless GET, which is what this sent before
        // being fixed) with jData={"seg":"ALL","exch":"ALL","prod":"ALL"} for a consolidated report
        // across every segment/exchange/product.
        var fields = new Dictionary<string, string> { ["seg"] = "ALL", ["exch"] = "ALL", ["prod"] = "ALL" };
        using var response = await _api.SendSessionAsync(
            HttpMethod.Post, "quick/user/limits", fields, "checking available margin", cancellationToken);

        if (!response.Success)
        {
            _logger.LogWarning("Available margin check failed: {Message}", response.ErrorMessage);
            return new AvailableMarginResult(false, null, response.ErrorMessage);
        }

        // "Net" ("Net available margin/cash") is the confirmed field for this - the others are kept
        // as a fallback only in case a given account/segment response ever omits it.
        var available = ReadDecimal(response.Data, "Net") ?? ReadDecimal(response.Data, "CollateralValue");

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
