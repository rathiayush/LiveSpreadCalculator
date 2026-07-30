using System.Text.Json;
using Microsoft.Extensions.Logging;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Brokers;
using SpreadTrader.Core.Models;

namespace SpreadTrader.KotakIntegration.Orders;

/// <summary>
/// Places orders via Kotak Neo's REST order API (POST quick/order/rule/ms/place). Behaviourally
/// identical to KiteOrderGateway/DhanOrderGateway from the engine's point of view - including
/// polling the order's real status after placement rather than treating acceptance as a fill - but
/// the wire format differs in ways this class hides:
///
///  1. FORM-URLENCODED BODY WITH ABBREVIATED FIELD NAMES, not JSON. Reverse-engineered from the
///     Kotak-Neo/Kotak-neo-api-v2 Python SDK's order_api.py: es=exchange_segment, pc=product,
///     pr=price, pt=order_type ("price type"), qt=quantity, rt=validity, ts=trading_symbol,
///     tt=transaction_type. These eight are the ones confirmed from that source; optional fields
///     the SDK's place_order() also accepts (AMO, disclosed quantity, market protection, trigger
///     price, tag) are deliberately NOT sent here since their abbreviated JSON keys were not found
///     in the available source excerpts - see KotakOptions' doc comment on this integration's
///     overall confidence level. This is fine for the one order shape this app actually sends
///     (MarketProtectionService always converts a MARKET leg into a protected IOC LIMIT before it
///     reaches any gateway), but SL/SL-M/AMO order types would need those fields wired in and
///     confirmed first.
///
///  2. ORDERS ARE ADDRESSED BY tradingSymbol, per the SDK - not a numeric token the way Dhan
///     addresses by securityId. leg.TradingSymbol is sent directly.
///
///  3. RESPONSE FIELD NAMES ARE BEST-EFFORT. No confirmed Kotak order-placement/order-status
///     response was available while building this (see KotakApiClient's doc comment) - several
///     plausible field-name candidates are tried for the order id, status and fill quantity,
///     falling back to treating an unrecognised response as "accepted, fill status unknown" rather
///     than guessing wrong in either direction. Tighten ReadOrderId/ReadStatus/ReadFilledQuantity
///     once a real response has been seen.
/// </summary>
public sealed class KotakOrderGateway : IOrderGateway
{
    private const int StatusPollAttempts = 4;
    private static readonly TimeSpan StatusPollDelay = TimeSpan.FromMilliseconds(300);

    private readonly KotakApiClient _api;
    private readonly RateLimiter _rateLimiter;
    private readonly ILogger<KotakOrderGateway> _logger;

    public KotakOrderGateway(KotakApiClient api, RateLimiter rateLimiter, ILogger<KotakOrderGateway> logger)
    {
        _api = api;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    public async Task<OrderResult> PlaceOrderAsync(OrderLegRequest leg, CancellationToken cancellationToken)
    {
        await _rateLimiter.WaitAsync(cancellationToken);

        if (!_api.HasSession)
            return new OrderResult { Success = false, ErrorMessage = "No Kotak session - complete the login flow first." };

        var fields = new Dictionary<string, string>
        {
            ["es"] = KotakSymbology.ToKotakSegment(leg.Exchange),
            ["pc"] = KotakSymbology.ToKotakProduct(leg.Product),
            ["pr"] = (leg.Price ?? 0m).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["pt"] = KotakSymbology.ToKotakOrderType(leg.OrderType),
            ["qt"] = leg.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["rt"] = KotakSymbology.ToKotakValidity(leg.Validity),
            ["ts"] = leg.TradingSymbol,
            ["tt"] = KotakSymbology.ToKotakTransactionType(leg.TransactionType),
        };

        string? orderId;
        using (var response = await _api.SendSessionAsync(
            HttpMethod.Post, "quick/order/rule/ms/place", fields, $"placing order for {leg.TradingSymbol}", cancellationToken))
        {
            if (!response.Success)
            {
                _logger.LogWarning("Order rejected: {Symbol} {Side} x{Qty} - {Message}",
                    leg.TradingSymbol, leg.TransactionType, leg.Quantity, response.ErrorMessage);
                return new OrderResult { Success = false, ErrorMessage = response.ErrorMessage, Status = "REJECTED" };
            }

            orderId = ReadOrderId(response.Data);
            _logger.LogInformation("Order accepted: {Symbol} {Side} x{Qty} -> {OrderId}, checking fill status...",
                leg.TradingSymbol, leg.TransactionType, leg.Quantity, orderId);
        }

        return await PollOrderStatusAsync(orderId, leg, cancellationToken);
    }

    private async Task<OrderResult> PollOrderStatusAsync(string? orderId, OrderLegRequest leg, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(orderId))
            return new OrderResult { Success = false, ErrorMessage = "Order accepted but no order id was returned - cannot verify fill status." };

        for (var attempt = 1; attempt <= StatusPollAttempts; attempt++)
        {
            await Task.Delay(StatusPollDelay, cancellationToken);
            await _rateLimiter.WaitAsync(cancellationToken);

            var fields = new Dictionary<string, string> { ["nOrdNo"] = orderId };
            using var response = await _api.SendSessionAsync(
                HttpMethod.Get, "quick/order/history", fields, $"polling status of order {orderId}", cancellationToken);

            if (!response.Success)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) break;
                continue;
            }

            var order = UnwrapOrder(response.Data);
            var status = KotakSymbology.ToCanonicalOrderStatus(ReadStatus(order));

            if (!KotakSymbology.IsTerminalStatus(status))
                continue;

            var filledQuantity = ReadFilledQuantity(order);
            var averageFillPrice = ReadAverageFillPrice(order);
            var rejectionReason = ReadRejectionReason(order);

            var success = filledQuantity > 0;
            _logger.LogInformation(
                "Order {OrderId} ({Symbol} {Side}) reached {Status}: {Filled}/{Requested} filled{AvgPrice}",
                orderId, leg.TradingSymbol, leg.TransactionType, status, filledQuantity, leg.Quantity,
                averageFillPrice is { } avg ? $" @ avg {avg}" : "");

            return new OrderResult
            {
                Success = success,
                OrderId = orderId,
                FilledQuantity = filledQuantity,
                Status = status,
                ErrorMessage = success ? null : (rejectionReason ?? $"Order ended as {status} with no fill."),
                AverageFillPrice = averageFillPrice,
            };
        }

        _logger.LogWarning(
            "Order {OrderId} ({Symbol} {Side}) status still not terminal after {Max} checks - treating as unconfirmed, may still be open on Kotak.",
            orderId, leg.TradingSymbol, leg.TransactionType, StatusPollAttempts);

        return new OrderResult
        {
            Success = false,
            OrderId = orderId,
            Status = "OPEN",
            ErrorMessage = "Could not confirm fill status in time - order may still be open on Kotak; check manually.",
        };
    }

    public async Task<bool> CancelOrderAsync(string orderId, string variety, CancellationToken cancellationToken)
    {
        await _rateLimiter.WaitAsync(cancellationToken);

        if (!_api.HasSession)
        {
            _logger.LogWarning("Cannot cancel order {OrderId} - no Kotak session.", orderId);
            return false;
        }

        var fields = new Dictionary<string, string> { ["nOrdNo"] = orderId };
        using var response = await _api.SendSessionAsync(
            HttpMethod.Post, "quick/order/cancel", fields, $"cancelling order {orderId}", cancellationToken);

        if (response.Success)
            _logger.LogWarning("Cancelled stray open order {OrderId} (status couldn't be confirmed as filled/rejected in time).", orderId);
        else
            _logger.LogError("Failed to cancel order {OrderId}: {Message}", orderId, response.ErrorMessage);

        return response.Success;
    }

    // ---- Best-effort response field readers - see the class doc comment ------------------------

    private static string? ReadOrderId(JsonElement element)
    {
        foreach (var field in new[] { "nOrdNo", "orderId", "OrderId", "order_id", "orderNumber" })
        {
            if (TryReadString(element, field, out var value)) return value;
        }
        return null;
    }

    private static string? ReadStatus(JsonElement element)
    {
        foreach (var field in new[] { "ordSt", "orderStatus", "status", "Status" })
        {
            if (TryReadString(element, field, out var value)) return value;
        }
        return null;
    }

    private static int ReadFilledQuantity(JsonElement element)
    {
        foreach (var field in new[] { "fldQty", "filledQty", "filledQuantity", "flQty" })
        {
            if (TryReadInt(element, field, out var value)) return value;
        }
        return 0;
    }

    private static decimal? ReadAverageFillPrice(JsonElement element)
    {
        foreach (var field in new[] { "avgPrc", "averagePrice", "avgPrice", "avgTradedPrice" })
        {
            if (TryReadDecimal(element, field, out var value) && value > 0) return value;
        }
        return null;
    }

    private static string? ReadRejectionReason(JsonElement element)
    {
        foreach (var field in new[] { "rejRsn", "rejectionReason", "reason", "errMsg" })
        {
            if (TryReadString(element, field, out var value)) return value;
        }
        return null;
    }

    /// <summary>Some Kotak endpoints wrap a single order in a one-element array; accept both shapes,
    /// same defensive pattern as DhanOrderGateway.UnwrapOrder.</summary>
    private static JsonElement UnwrapOrder(JsonElement root) =>
        root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[root.GetArrayLength() - 1] : root;

    private static bool TryReadString(JsonElement element, string property, out string value)
    {
        value = "";
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var v)) return false;
        var s = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
        if (string.IsNullOrWhiteSpace(s)) return false;
        value = s;
        return true;
    }

    private static bool TryReadInt(JsonElement element, string property, out int value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out value) => true,
            JsonValueKind.String when int.TryParse(v.GetString(), out value) => true,
            _ => false,
        };
    }

    private static bool TryReadDecimal(JsonElement element, string property, out decimal value)
    {
        value = 0m;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetDecimal(out value) => true,
            JsonValueKind.String when decimal.TryParse(v.GetString(), out value) => true,
            _ => false,
        };
    }
}
