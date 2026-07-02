using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;
using SpreadTrader.Engine;
using SpreadTrader.KiteIntegration.Orders;

namespace SpreadTrader.Engine.Services;

/// <summary>
/// Consumes fired triggers and turns each into a basket of order legs, dispatched sequentially
/// through IOrderGateway (which paces itself under Kite's rate limits). Runs as its own
/// consumer loop off a channel so a slow or failing order call never blocks tick/spread
/// processing upstream.
/// </summary>
public sealed class OrderDispatchHostedService : BackgroundService
{
    private readonly TriggerEventChannel _triggerChannel;
    private readonly ITriggerRuleStore _ruleStore;
    private readonly ISpreadDefinitionStore _spreadStore;
    private readonly IOrderGateway _orderGateway;
    private readonly KiteMarginService _marginService;
    private readonly ILogger<OrderDispatchHostedService> _logger;

    public OrderDispatchHostedService(
        TriggerEventChannel triggerChannel,
        ITriggerRuleStore ruleStore,
        ISpreadDefinitionStore spreadStore,
        IOrderGateway orderGateway,
        KiteMarginService marginService,
        ILogger<OrderDispatchHostedService> logger)
    {
        _triggerChannel = triggerChannel;
        _ruleStore = ruleStore;
        _spreadStore = spreadStore;
        _orderGateway = orderGateway;
        _marginService = marginService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var triggerEvent in _triggerChannel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await DispatchAsync(triggerEvent, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to dispatch basket for trigger {RuleId}", triggerEvent.RuleId);
            }
        }
    }

    private async Task DispatchAsync(TriggerEvent triggerEvent, CancellationToken stoppingToken)
    {
        var rule = _ruleStore.Get(triggerEvent.RuleId);
        var spread = _spreadStore.Get(triggerEvent.SpreadId);
        if (rule is null || spread is null)
        {
            _logger.LogWarning("Trigger fired for a rule/spread that no longer exists: {RuleId}/{SpreadId}",
                triggerEvent.RuleId, triggerEvent.SpreadId);
            return;
        }

        var legs = BuildLegs(rule, spread, triggerEvent.Direction);
        if (legs.Count == 0)
        {
            _logger.LogWarning("Rule {RuleId} fired but has no leg actions configured - nothing to place", rule.Id);
            return;
        }

        await LogBasketMarginAsync(rule.Id, legs, stoppingToken);

        foreach (var leg in legs)
        {
            var result = await _orderGateway.PlaceOrderAsync(leg, stoppingToken);
            if (!result.Success)
            {
                _logger.LogError("Basket leg failed for rule {RuleId}: {Symbol} {Side} - {Error}",
                    rule.Id, leg.TradingSymbol, leg.TransactionType, result.ErrorMessage);
            }
        }
    }

    /// <summary>Logs the margin Kite would actually block for this basket, benefit from the
    /// near/far hedge included (see KiteMarginService) - informational only, never blocks
    /// dispatch. If your available margin is short, Kite's own order entry will reject the
    /// order and that rejection is what gets logged/surfaced, not this check.</summary>
    private async Task LogBasketMarginAsync(string ruleId, List<OrderLegRequest> legs, CancellationToken cancellationToken)
    {
        try
        {
            var margin = await _marginService.GetBasketMarginAsync(legs, cancellationToken);
            if (margin.Success)
            {
                _logger.LogInformation(
                    "Basket margin for rule {RuleId}: final={Final} (would be {Initial} without the spread hedge benefit)",
                    ruleId, margin.FinalTotal, margin.InitialTotal);
            }
            else
            {
                _logger.LogWarning("Basket margin check failed for rule {RuleId}: {Error}", ruleId, margin.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Basket margin check threw for rule {RuleId}", ruleId);
        }
    }

    private static List<OrderLegRequest> BuildLegs(TriggerRule rule, SpreadDefinition spread, string direction)
    {
        var (nearAction, farAction) = rule.AutoDirectionFromSpreadSign
            ? ResolveAutoDirection(direction)
            : (rule.NearLegAction, rule.FarLegAction);

        var legs = new List<OrderLegRequest>();
        var tag = BuildTag(rule.Id);

        if (nearAction != TriggerLegAction.None)
        {
            legs.Add(new OrderLegRequest
            {
                TradingSymbol = spread.NearLeg.TradingSymbol,
                Exchange = spread.NearLeg.Exchange,
                TransactionType = nearAction == TriggerLegAction.Buy ? "BUY" : "SELL",
                Quantity = rule.Quantity,
                Product = rule.Product,
                OrderType = rule.OrderType,
                Tag = tag,
            });
        }

        if (farAction != TriggerLegAction.None)
        {
            legs.Add(new OrderLegRequest
            {
                TradingSymbol = spread.FarLeg.TradingSymbol,
                Exchange = spread.FarLeg.Exchange,
                TransactionType = farAction == TriggerLegAction.Buy ? "BUY" : "SELL",
                Quantity = rule.Quantity,
                Product = rule.Product,
                OrderType = rule.OrderType,
                Tag = tag,
            });
        }

        return legs;
    }

    /// <summary>
    /// The calendar-spread arbitrage rule, decided by SpreadCalculationHostedService and carried
    /// here verbatim via TriggerEvent.Direction (never re-derived from SpreadValueAtTrigger's
    /// sign - see the comment on TriggerEvent for why that would be unsafe): if the current
    /// (near) month is in premium, sell it and buy the next (far) month; if it's at a discount,
    /// buy it and sell the far month. "Flat" (or a direction string this switch doesn't
    /// recognize) is treated as "nothing to do" since there's no direction to take a position in.
    /// </summary>
    private static (TriggerLegAction Near, TriggerLegAction Far) ResolveAutoDirection(string direction) =>
        direction switch
        {
            "BuyNear/SellFar" or "BuyNear/SellFar (bid pending)" => (TriggerLegAction.Buy, TriggerLegAction.Sell),
            "SellNear/BuyFar" or "SellNear/BuyFar (bid pending)" => (TriggerLegAction.Sell, TriggerLegAction.Buy),
            _ => (TriggerLegAction.None, TriggerLegAction.None),
        };

    private static string BuildTag(string ruleId)
    {
        // Kite tags must be alphanumeric and <= 20 chars.
        var raw = "str" + new string(ruleId.Where(char.IsLetterOrDigit).ToArray());
        return raw.Length > 20 ? raw[..20] : raw;
    }
}
