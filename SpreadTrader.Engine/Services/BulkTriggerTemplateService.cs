using Microsoft.Extensions.Logging;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;

namespace SpreadTrader.Engine.Services;

/// <summary>
/// Applies one trigger template across many spreads at once - built for the "configure a
/// trigger for all 100 stocks" case, where retyping the same threshold/quantity/product/legs 100
/// times one at a time isn't practical. For every target spread it creates ONE TriggerRule with
/// a fixed signed target %, explicit comparison, and fixed Buy/Sell/None leg actions - all
/// applied identically across the whole batch (see BulkTriggerTemplateRequest). Deliberately no
/// auto-direction: a single template can't know in advance which leg is actually rich/cheap for
/// each different underlying, so the same fixed legs fire everywhere a spread crosses the target,
/// regardless of which side of that stock's own spread is actually pricier at that moment.
/// Quantity is computed per stock as lotsPerTrade * that stock's exchange lot size (from
/// InstrumentConfig.LotSize, resolved by the instrument master) rather than a single flat
/// quantity, since lot sizes vary widely across the F&amp;O universe.
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
        decimal targetPercent,
        bool fireWhenGreaterOrEqual,
        TriggerLegAction nearLegAction,
        TriggerLegAction farLegAction,
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

            // Pre-refactor bulk applications created two auto-direction rules per spread under
            // these ids (see the old BULK-UP/BULK-DOWN pair, described in this class's doc
            // comment history) - remove any left over from before this change so re-applying the
            // template doesn't leave a stale auto-direction rule sitting alongside (and
            // double-firing orders next to) the new single fixed-leg rule below. No-ops if
            // they were never created (Remove returns false, ignored).
            _ruleStore.Remove($"{spread.Id}-BULK-UP");
            _ruleStore.Remove($"{spread.Id}-BULK-DOWN");

            _ruleStore.Upsert(new TriggerRule
            {
                Id = $"{spread.Id}-BULK",
                SpreadId = spread.Id,
                Operator = fireWhenGreaterOrEqual ? TriggerOperator.GreaterThanOrEqual : TriggerOperator.LessThanOrEqual,
                Threshold = targetPercent,
                ThresholdIsPercent = true,
                AutoDirectionFromSpreadSign = false,
                NearLegAction = nearLegAction,
                FarLegAction = farLegAction,
                Quantity = quantity,
                Product = product,
                OrderType = orderType,
                OneShot = oneShot,
                RearmBuffer = rearmBufferPercent,
            });

            rulesCreated += 1;
        }

        _logger.LogInformation(
            "Bulk trigger template applied: {RulesCreated} rules across {SpreadCount} spreads, {Skipped} skipped",
            rulesCreated, targets.Length, skipped.Count);

        return new BulkTriggerTemplateResult(targets.Length, rulesCreated, skipped);
    }
}
