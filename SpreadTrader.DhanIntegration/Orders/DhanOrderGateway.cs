using System.Text.Json;
using Microsoft.Extensions.Logging;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Brokers;
using SpreadTrader.Core.Models;

namespace SpreadTrader.DhanIntegration.Orders;

/// <summary>
/// Places orders via Dhan's REST order API (POST /v2/orders). Paced by a RateLimiter to stay under
/// Dhan's order-placement ceiling, which matters when a trigger fires a multi-leg basket, since
/// legs are dispatched one call at a time.
///
/// Behaviourally identical to KiteOrderGateway from the engine's point of view - including polling
/// the order's real status after placement rather than treating acceptance as a fill - but the
/// wire format differs in three ways that this class is responsible for hiding:
///
///  1. ORDERS ARE ADDRESSED BY securityId, NOT tradingsymbol. Dhan's payload has no symbol field
///     at all. The leg's numeric token is used when the caller already resolved one, falling back
///     to a lookup by symbol in the instrument master. A leg that resolves to neither is refused
///     outright rather than sent - there is no safe guess for "which contract did they mean".
///
///  2. VOCABULARY. Products, order types and statuses all differ (NRML vs MARGIN, COMPLETE vs
///     TRADED, ...). Everything crossing this boundary is translated by DhanSymbology, so
///     OrderResult.Status comes back in the same canonical vocabulary the engine already branches
///     on and OrderDispatchHostedService needs no Dhan-specific handling.
///
///  3. THERE IS NO "VARIETY". Kite routes placement and cancellation through /orders/:variety;
///     Dhan has a single /orders collection. The variety argument on CancelOrderAsync is accepted
///     to satisfy IOrderGateway and deliberately ignored.
/// </summary>
public sealed class DhanOrderGateway : IOrderGateway
{
    /// <summary>How many times to poll GET /orders/{id} after placement to find out whether the
    /// order actually filled, and how long to wait between attempts. IOC orders resolve to a
    /// terminal status almost immediately, so this is a short poll. An order still resting when the
    /// poll gives up is reported as Status="OPEN"/Success=false - the honest answer rather than
    /// guessing either way.</summary>
    private const int StatusPollAttempts = 4;
    private static readonly TimeSpan StatusPollDelay = TimeSpan.FromMilliseconds(300);

    private readonly DhanApiClient _api;
    private readonly IBrokerInstrumentMaster _instrumentMaster;
    private readonly RateLimiter _rateLimiter;
    private readonly ILogger<DhanOrderGateway> _logger;

    public DhanOrderGateway(
        DhanApiClient api,
        IBrokerInstrumentMaster instrumentMaster,
        RateLimiter rateLimiter,
        ILogger<DhanOrderGateway> logger)
    {
        _api = api;
        _instrumentMaster = instrumentMaster;
        // Injected as a singleton (see BrokerRegistration) rather than constructed here, so pacing
        // state is shared even if this gateway is resolved as a fresh instance per call.
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    public async Task<OrderResult> PlaceOrderAsync(OrderLegRequest leg, CancellationToken cancellationToken)
    {
        await _rateLimiter.WaitAsync(cancellationToken);

        if (!_api.HasSession)
            return new OrderResult { Success = false, ErrorMessage = "No Dhan access token - complete the login flow first." };

        var securityId = ResolveSecurityId(leg);
        if (securityId is null)
        {
            var message = $"Could not resolve {leg.TradingSymbol} on {leg.Exchange} to a Dhan securityId - the instrument master may not be loaded.";
            _logger.LogError("Order not placed: {Message}", message);
            return new OrderResult { Success = false, ErrorMessage = message, Status = "REJECTED" };
        }

        var payload = new Dictionary<string, object?>
        {
            ["transactionType"] = leg.TransactionType.ToUpperInvariant(),
            ["exchangeSegment"] = DhanSymbology.ToDhanSegment(leg.Exchange),
            ["productType"] = DhanSymbology.ToDhanProduct(leg.Product),
            ["orderType"] = DhanSymbology.ToDhanOrderType(leg.OrderType),
            ["validity"] = leg.Validity.ToUpperInvariant(),
            ["securityId"] = securityId,
            ["quantity"] = leg.Quantity,
            ["disclosedQuantity"] = 0,
            // Dhan requires the field even for MARKET orders, where it is ignored.
            ["price"] = leg.Price ?? 0m,
            ["triggerPrice"] = 0m,
            ["afterMarketOrder"] = false,
        };

        if (!string.IsNullOrEmpty(leg.Tag))
            payload["correlationId"] = leg.Tag;

        string? orderId;
        using (var response = await _api.SendAsync(
            HttpMethod.Post, "/orders", payload, $"placing order for {leg.TradingSymbol}", cancellationToken))
        {
            if (!response.Success)
            {
                _logger.LogWarning("Order rejected: {Symbol} {Side} x{Qty} - {Message}",
                    leg.TradingSymbol, leg.TransactionType, leg.Quantity, response.ErrorMessage);
                return new OrderResult { Success = false, ErrorMessage = response.ErrorMessage, Status = "REJECTED" };
            }

            orderId = response.Root.TryGetProperty("orderId", out var id) ? id.GetString() : null;

            // Dhan reports the order's initial state on the placement response. A synchronous
            // rejection is terminal - there is nothing to poll for, so return it immediately
            // rather than spending four status calls rediscovering it.
            var placementStatus = DhanSymbology.ToCanonicalOrderStatus(
                response.Root.TryGetProperty("orderStatus", out var st) ? st.GetString() : null);

            if (placementStatus == "REJECTED")
            {
                _logger.LogWarning("Order rejected on placement: {Symbol} {Side} x{Qty} (order {OrderId})",
                    leg.TradingSymbol, leg.TransactionType, leg.Quantity, orderId);
                return new OrderResult
                {
                    Success = false,
                    OrderId = orderId,
                    Status = "REJECTED",
                    ErrorMessage = "Dhan rejected the order on placement.",
                };
            }

            _logger.LogInformation("Order accepted: {Symbol} {Side} x{Qty} -> {OrderId}, checking fill status...",
                leg.TradingSymbol, leg.TransactionType, leg.Quantity, orderId);
        }

        // Acceptance only means Dhan queued it - it says nothing about whether it filled. Poll for
        // the real outcome, which is what lets AutoSquareOffOnPartialFill react to "accepted but
        // never filled" instead of only to outright rejections.
        return await PollOrderStatusAsync(orderId, leg, cancellationToken);
    }

    /// <summary>Prefers the token the caller already resolved (every engine-dispatched leg carries
    /// one - see OrderDispatchHostedService.LegPlan), falling back to a symbol lookup for callers
    /// that only have a symbol, such as manual square-off.</summary>
    private string? ResolveSecurityId(OrderLegRequest leg)
    {
        if (leg.InstrumentToken != 0)
            return leg.InstrumentToken.ToString();

        var instrument = _instrumentMaster.Find(leg.TradingSymbol, leg.Exchange);
        return instrument is null ? null : instrument.InstrumentToken.ToString();
    }

    private async Task<OrderResult> PollOrderStatusAsync(string? orderId, OrderLegRequest leg, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(orderId))
            return new OrderResult { Success = false, ErrorMessage = "Order accepted but no orderId was returned - cannot verify fill status." };

        for (var attempt = 1; attempt <= StatusPollAttempts; attempt++)
        {
            await Task.Delay(StatusPollDelay, cancellationToken);
            await _rateLimiter.WaitAsync(cancellationToken);

            using var response = await _api.SendAsync(
                HttpMethod.Get, $"/orders/{orderId}", null, $"polling status of order {orderId}", cancellationToken);

            if (!response.Success)
            {
                // A dead token will fail identically on every retry - stop early. Anything else is
                // treated as a transient hiccup on the status check itself and retried.
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) break;
                continue;
            }

            var order = UnwrapOrder(response.Root);
            var status = DhanSymbology.ToCanonicalOrderStatus(
                order.TryGetProperty("orderStatus", out var s) ? s.GetString() : null);

            if (!DhanSymbology.IsTerminalStatus(status))
                continue; // TRANSIT/PENDING/PART_TRADED - keep polling until attempts run out

            var filledQuantity = ReadInt(order, "filledQty");
            var averageFillPrice = ReadDecimal(order, "averageTradedPrice");
            var rejectionReason = order.TryGetProperty("omsErrorDescription", out var oms) ? oms.GetString() : null;

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

        // Gave up before seeing a terminal status - report the honest "don't actually know if this
        // filled" state rather than assuming success. Callers treat this the same as a failure for
        // square-off purposes, but the order may still be live.
        _logger.LogWarning(
            "Order {OrderId} ({Symbol} {Side}) status still not terminal after {Max} checks - treating as unconfirmed, may still be open on Dhan.",
            orderId, leg.TradingSymbol, leg.TransactionType, StatusPollAttempts);

        return new OrderResult
        {
            Success = false,
            OrderId = orderId,
            Status = "OPEN",
            ErrorMessage = "Could not confirm fill status in time - order may still be open on Dhan; check manually.",
        };
    }

    /// <summary>GET /orders/{id} returns the order object directly, but some Dhan responses wrap a
    /// single order in an array. Accept both so a response-shape change can't silently turn every
    /// fill into "unconfirmed".</summary>
    private static JsonElement UnwrapOrder(JsonElement root) =>
        root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
            ? root[root.GetArrayLength() - 1]
            : root;

    private static int ReadInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => 0,
        };
    }

    /// <summary>Returns null for a missing or zero value - Dhan reports 0 for an order with no fill
    /// yet, and 0 is not a meaningful price.</summary>
    private static decimal? ReadDecimal(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;

        var parsed = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(value.GetString(), out var fromString) => fromString,
            _ => 0m,
        };

        return parsed > 0 ? parsed : null;
    }

    /// <summary>Cancels a still-open order. <paramref name="variety"/> is part of IOrderGateway for
    /// Kite's sake and has no Dhan equivalent - Dhan cancels by order id alone.</summary>
    public async Task<bool> CancelOrderAsync(string orderId, string variety, CancellationToken cancellationToken)
    {
        await _rateLimiter.WaitAsync(cancellationToken);

        if (!_api.HasSession)
        {
            _logger.LogWarning("Cannot cancel order {OrderId} - no Dhan access token.", orderId);
            return false;
        }

        using var response = await _api.SendAsync(
            HttpMethod.Delete, $"/orders/{orderId}", null, $"cancelling order {orderId}", cancellationToken);

        if (response.Success)
            _logger.LogWarning("Cancelled stray open order {OrderId} (status couldn't be confirmed as filled/rejected in time).", orderId);
        else
            _logger.LogError("Failed to cancel order {OrderId}: {Message}", orderId, response.ErrorMessage);

        return response.Success;
    }
}
