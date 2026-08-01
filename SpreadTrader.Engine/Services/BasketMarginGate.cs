using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Brokers;
using SpreadTrader.Core.Models;

namespace SpreadTrader.Engine.Services;

/// <summary>
/// The pre-trade margin decision, extracted from OrderDispatchHostedService so that class is left
/// with the order choreography alone. Behaviour is unchanged - see
/// SpreadTrader.Engine.Tests.OrderDispatchBusinessRuleTests, which pins both outcomes this makes
/// (a confirmed shortfall blocking the whole basket, a FAILED check not blocking it) and was green
/// before and after the move.
///
/// Checks the basket's combined margin requirement - with the near/far hedge benefit applied, see
/// IMarginService.GetBasketMarginAsync - against the account's actual available margin. Returns
/// null if dispatch should proceed, or a human-readable reason if it should be blocked entirely.
///
/// The asymmetry between the two failure modes is deliberate and load-bearing:
///  - A failed CHECK (network error, broker API hiccup) is NOT fatal. The broker's own order entry
///    still rejects a genuinely under-margined order, so a flaky diagnostic call must not be able
///    to halt trading.
///  - A confirmed INSUFFICIENT margin blocks everything. Letting one leg place and the other get
///    rejected for lack of margin is exactly the naked-position scenario the whole basket approach
///    exists to avoid.
/// </summary>
public sealed class BasketMarginGate
{
    private readonly IMarginService _marginService;
    private readonly EngineOptions _options;
    private readonly McxOptions _mcx;
    private readonly ILogger<BasketMarginGate> _logger;

    public BasketMarginGate(
        IMarginService marginService,
        IOptions<EngineOptions> options,
        McxOptions mcx,
        ILogger<BasketMarginGate> logger)
    {
        _marginService = marginService;
        _options = options.Value;
        _mcx = mcx;
        _logger = logger;
    }

    /// <summary>Null to proceed, or the reason dispatch should be blocked. Callers only invoke this
    /// when EngineOptions.VerifyMarginBeforeDispatch is on - with it off, no margin round trips are
    /// made at all and the broker's own order entry is the only gate.</summary>
    public async Task<string?> CheckAsync(string ruleId, IReadOnlyList<OrderLegRequest> legs, CancellationToken cancellationToken)
    {
        var margin = await _marginService.GetBasketMarginAsync(legs, cancellationToken);
        if (!margin.Success)
        {
            _logger.LogWarning("Basket margin check failed for rule {RuleId}: {Error} - proceeding anyway", ruleId, margin.ErrorMessage);
            return null;
        }

        _logger.LogInformation(
            "Basket margin for rule {RuleId}: final={Final} (would be {Initial} without the spread hedge benefit)",
            ruleId, margin.FinalTotal, margin.InitialTotal);

        if (margin.FinalTotal is not { } required)
            return null;

        // NFO (equity F&O) settles against the "equity" margin segment; MCX commodities against
        // "commodity". Picked per-basket from the legs' own exchange so an MCX basket's pre-trade
        // check reads the right pool of available funds. With the MCX add-on off no basket ever
        // carries an MCX leg, so this always resolves to the original "equity" default.
        var marginSegment = _mcx.Enabled && legs.Any(l => string.Equals(l.Exchange, _mcx.Exchange, StringComparison.OrdinalIgnoreCase))
            ? _mcx.MarginSegment
            : "equity";

        var available = await _marginService.GetAvailableMarginAsync(marginSegment, cancellationToken);
        if (!available.Success || available.Net is not { } net)
        {
            _logger.LogWarning("Available margin check failed for rule {RuleId}: {Error} - proceeding anyway", ruleId, available.ErrorMessage);
            return null;
        }

        var buffered = required * (1 + _options.MarginSafetyBufferPercent / 100m);
        if (net < buffered)
        {
            var reason = $"Insufficient margin: needs {required:F2} (+{_options.MarginSafetyBufferPercent}% safety buffer = {buffered:F2}), only {net:F2} available.";
            _logger.LogError(
                "Rule {RuleId} basket NOT placed - {Reason} Placing only some legs would leave a naked, unhedged position, so nothing was sent.",
                ruleId, reason);
            return reason;
        }

        _logger.LogInformation("Margin check passed for rule {RuleId}: {Available:F2} available covers {Required:F2} + buffer.", ruleId, net, required);
        return null;
    }
}
