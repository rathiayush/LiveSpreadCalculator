using Microsoft.Extensions.Logging;
using SpreadTrader.Core.Brokers;
using SpreadTrader.Core.Models;

namespace SpreadTrader.DhanIntegration.Orders;

/// <summary>
/// Pre-trade margin checks against Dhan.
///
/// BASKET MARGIN uses POST /v2/margincalculator/multi, Dhan's multi-leg calculator, with
/// includePosition and includeOrders both on. That combination is what makes the returned figure
/// the INCREMENTAL margin the basket needs on top of what the account's existing positions and
/// open orders already block - the correct number to compare against available balance, and the
/// one that has the exchange's hedge benefit applied between the offsetting legs of a calendar
/// spread. The single-leg /margincalculator endpoint deliberately isn't used as a substitute:
/// summing per-leg margins would ignore the hedge benefit entirely and report several times the
/// real requirement, which would block baskets the account can comfortably afford.
///
/// When the multi-leg call fails outright this reports failure rather than falling back to a
/// pessimistic per-leg sum. That is deliberate: OrderDispatchHostedService treats a failed margin
/// CHECK as non-fatal and proceeds (Dhan's own order entry still enforces margin), whereas an
/// inflated estimate would silently block valid trades - so failing the check is the safer of the
/// two wrong answers.
/// </summary>
public sealed class DhanMarginService : IMarginService
{
    private readonly DhanApiClient _api;
    private readonly IBrokerInstrumentMaster _instrumentMaster;
    private readonly ILogger<DhanMarginService> _logger;

    public DhanMarginService(
        DhanApiClient api,
        IBrokerInstrumentMaster instrumentMaster,
        ILogger<DhanMarginService> logger)
    {
        _api = api;
        _instrumentMaster = instrumentMaster;
        _logger = logger;
    }

    public async Task<BasketMarginResult> GetBasketMarginAsync(IReadOnlyList<OrderLegRequest> legs, CancellationToken cancellationToken)
    {
        if (!_api.HasSession)
            return new BasketMarginResult(false, null, null, "No Dhan access token - complete the login flow first.");

        var scripts = new List<object>(legs.Count);
        foreach (var leg in legs)
        {
            var securityId = ResolveSecurityId(leg);
            if (securityId is null)
                return new BasketMarginResult(false, null, null,
                    $"Could not resolve {leg.TradingSymbol} on {leg.Exchange} to a Dhan securityId for the margin check.");

            scripts.Add(new
            {
                exchangeSegment = DhanSymbology.ToDhanSegment(leg.Exchange),
                transactionType = leg.TransactionType.ToUpperInvariant(),
                quantity = leg.Quantity,
                productType = DhanSymbology.ToDhanProduct(leg.Product),
                securityId,
                price = leg.Price ?? 0m,
                triggerPrice = 0m,
            });
        }

        // Field names here are the multi-leg endpoint's own, which are NOT the single-leg
        // endpoint's: the leg array is "scripts" (not "scripList") and the open-orders flag is
        // "includeOrders" (not "includeOrder"). Both were previously wrong, so Dhan saw a request
        // with no legs in it at all.
        var payload = new
        {
            includePosition = true,
            includeOrders = true,
            scripts,
        };

        using var response = await _api.SendAsync(
            HttpMethod.Post, "/margincalculator/multi", payload, "checking basket margin", cancellationToken);

        if (!response.Success)
        {
            _logger.LogWarning("Basket margin check failed for {LegCount} leg(s): {Message}", legs.Count, response.ErrorMessage);
            return new BasketMarginResult(false, null, null, response.ErrorMessage);
        }

        var root = response.Root;

        // The multi-leg endpoint answers in snake_case with the figures as STRINGS
        // ("total_margin": "150000.00"), unlike the single-leg endpoint's camelCase numbers. The
        // camelCase spelling is still accepted as a fallback in case Dhan ever aligns the two.
        var final = ReadDecimal(root, "total_margin") ?? ReadDecimal(root, "totalMargin");
        if (final is null)
        {
            // Returning "success with no figure" would be worse than an outright failure: the
            // caller treats an absent total as "nothing to check" and dispatches the basket
            // unguarded, which is exactly the pre-trade gate it asked for being silently skipped.
            const string message = "Dhan's basket margin response carried no total_margin figure - treating the check as failed rather than as 'no margin required'.";
            _logger.LogWarning("{Message} Response: {Response}", message, Truncate(root.ToString()));
            return new BasketMarginResult(false, null, null, message);
        }

        // Dhan reports the hedge benefit directly, so the un-benefited figure is the total plus
        // whatever the offsetting legs saved. It is blank on baskets that earn no benefit, in
        // which case initial and final are legitimately the same number.
        var hedgeBenefit = ReadDecimal(root, "hedge_benefit") ?? 0m;
        var initial = final + hedgeBenefit;

        return new BasketMarginResult(true, initial, final, null);
    }

    private static string Truncate(string value) => value.Length <= 300 ? value : value[..300] + "...";

    private string? ResolveSecurityId(OrderLegRequest leg)
    {
        if (leg.InstrumentToken != 0) return leg.InstrumentToken.ToString();
        var instrument = _instrumentMaster.Find(leg.TradingSymbol, leg.Exchange);
        return instrument?.InstrumentToken.ToString();
    }

    /// <summary>The account's usable balance. Note Dhan's fund limit is ACCOUNT-WIDE, not split by
    /// settlement segment the way Kite's /user/margins/{segment} is - so <paramref name="segment"/>
    /// is accepted for interface compatibility and ignored. That is not a loss of accuracy for the
    /// pre-dispatch check: the figure returned already nets off everything blocked across every
    /// segment, so comparing an NFO or MCX basket against it is still correct.</summary>
    public async Task<AvailableMarginResult> GetAvailableMarginAsync(string segment = "equity", CancellationToken cancellationToken = default)
    {
        if (!_api.HasSession)
            return new AvailableMarginResult(false, null, "No Dhan access token - complete the login flow first.");

        using var response = await _api.SendAsync(
            HttpMethod.Get, "/fundlimit", null, "checking available margin", cancellationToken);

        if (!response.Success)
        {
            _logger.LogWarning("Available margin check failed: {Message}", response.ErrorMessage);
            return new AvailableMarginResult(false, null, response.ErrorMessage);
        }

        // "availabelBalance" is Dhan's own spelling of the field in their API responses; the
        // correctly-spelled variant is accepted too in case it is ever fixed.
        var available = ReadDecimal(response.Root, "availabelBalance")
                        ?? ReadDecimal(response.Root, "availableBalance")
                        ?? ReadDecimal(response.Root, "withdrawableBalance");

        return new AvailableMarginResult(
            available is not null,
            available,
            available is null ? "Dhan's fund limit response had no available-balance figure." : null);
    }

    private static decimal? ReadDecimal(System.Text.Json.JsonElement element, string property)
    {
        if (element.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        if (!element.TryGetProperty(property, out var value)) return null;

        return value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            System.Text.Json.JsonValueKind.String when decimal.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }
}
