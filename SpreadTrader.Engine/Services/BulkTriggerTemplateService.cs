using Microsoft.Extensions.Logging;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;

namespace SpreadTrader.Engine.Services;

/// <summary>
/// Applies one trigger template across many spreads at once - built for the "configure a
/// trigger for all 100 stocks" case, where retyping the same threshold/quantity/product 100
/// times one at a time isn't practical. For every target spread it creates two TriggerRules,
/// both percent-threshold and auto-direction:
///   - fires when spread % &gt;= +thresholdPercent (spread far above near - sell near, buy far)
///   - fires when spread % &lt;= -thresholdPercent (spread far below near - buy near, sell far)
/// so the strategy trades either side of the deviation without needing two separate manual
/// configuration passes. Quantity is computed per stock as lotsPerTrade * that stock's exchange
/// lot size (from InstrumentConfig.LotSize, resolved by the instrument master) rather than a
/// single flat quantity, since lot sizes vary widely across the F&amp;O universe.
/// </summary>
public sealed class BulkTriggerTemplateService
{
    private readonly ISpreadDefinitionStore _spreadStore;
    private readonly ITriggerRuleStore _ruleStore;
    private readonly ILogger<BulkTriggerTemplateService> _logger;

    public BulkTriggerTemplateService(
        ISpreadDefinitionStore spreadStore,
        ITriggerRuleStore ruleStore,
        ILogger<BulkTriggerTemplateService> logger)
    {
        _spreadStore = spreadStore;
        _ruleStore = ruleStore;
        _logger = logger;
    }

    public BulkTriggerTemplateResult Apply(
        IReadOnlyList<string>? spreadIds,
        decimal thresholdPercent,
        int lotsPerTrade,
        string product,
        string orderType,
        bool oneShot,
        decimal rearmBufferPercent)
    {
        var targets = spreadIds is { Count: > 0 }
            ? spreadIds.Select(_spreadStore.Get).Where(s => s is not null).Select(s => s!).ToArray()
            : _spreadStore.GetAll().Where(s => s.Enabled).ToArray();

        var skipped = new List<string>();
        var rulesCreated = 0;

        foreach (var spread in targets)
        {
            var lotSize = spread.NearLeg.LotSize;
            if (lotSize <= 0)
            {
                skipped.Add($"{spread.Id} (lot size not resolved yet - build/resolve the universe first)");
                continue;
            }

            var quantity = lotsPerTrade * lotSize;

            _ruleStore.Upsert(new TriggerRule
            {
                Id = $"{spread.Id}-BULK-UP",
                SpreadId = spread.Id,
                Operator = TriggerOperator.GreaterThanOrEqual,
                Threshold = thresholdPercent,
                ThresholdIsPercent = true,
                AutoDirectionFromSpreadSign = true,
                Quantity = quantity,
                Product = product,
                OrderType = orderType,
                OneShot = oneShot,
                RearmBuffer = rearmBufferPercent,
            });

            _ruleStore.Upsert(new TriggerRule
            {
                Id = $"{spread.Id}-BULK-DOWN",
                SpreadId = spread.Id,
                Operator = TriggerOperator.LessThanOrEqual,
                Threshold = -thresholdPercent,
                ThresholdIsPercent = true,
                AutoDirectionFromSpreadSign = true,
                Quantity = quantity,
                Product = product,
                OrderType = orderType,
                OneShot = oneShot,
                RearmBuffer = rearmBufferPercent,
            });

            rulesCreated += 2;
        }

        _logger.LogInformation(
            "Bulk trigger template applied: {RulesCreated} rules across {SpreadCount} spreads, {Skipped} skipped",
            rulesCreated, targets.Length, skipped.Count);

        return new BulkTriggerTemplateResult(targets.Length, rulesCreated, skipped);
    }
}
