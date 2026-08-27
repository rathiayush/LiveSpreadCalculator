using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;

namespace SpreadTrader.Engine.Services;

/// <summary>
/// Evaluates every enabled PnlAlertRule against the configured spread's live combined Exit PNL
/// (both legs netted - see PositionSnapshot.ExitPnl / PositionExitPricing.Compute) and sends a
/// Telegram notification the moment a rule's target is crossed.
///
/// Reads from PositionsCache.GetProjected() - the same live-tick-projected snapshot
/// LivePositionsHostedService pushes to the browser - instead of calling IPositionsService
/// itself. PositionsCache already keeps itself in sync (event-driven off the order-update
/// socket), so this makes zero broker REST calls of its own; adding a second independent poller
/// here would have duplicated that work against the same rate-limited endpoint for no benefit.
/// Runs on its own, much longer, cadence purely because a Telegram notification doesn't need
/// sub-second latency - and the projected PNL it evaluates is tick-fresh regardless, since
/// GetProjected() recomputes from the live tick store on every read.
/// </summary>
public sealed class PnlAlertHostedService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    // How often the SAME non-one-shot rule may fire again while it stays past its target - without
    // this, a combined Exit PNL sitting right on the target would send a fresh Telegram message
    // every single poll. One-shot rules (the common case) never hit this at all - they disable
    // themselves on the first fire, see below.
    private static readonly TimeSpan MinRefireInterval = TimeSpan.FromMinutes(15);

    private readonly PositionsCache _positionsCache;
    private readonly ISpreadDefinitionStore _spreadStore;
    private readonly IPnlAlertStore _alertStore;
    private readonly IPnlAlertNotifier _notifier;
    private readonly ILogger<PnlAlertHostedService> _logger;

    public PnlAlertHostedService(
        PositionsCache positionsCache,
        ISpreadDefinitionStore spreadStore,
        IPnlAlertStore alertStore,
        IPnlAlertNotifier notifier,
        ILogger<PnlAlertHostedService> logger)
    {
        _positionsCache = positionsCache;
        _spreadStore = spreadStore;
        _alertStore = alertStore;
        _notifier = notifier;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EvaluateAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PnlAlertHostedService: evaluation cycle failed - will retry next poll.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        var rules = _alertStore.GetAll().Where(r => r.Enabled).ToList();
        if (rules.Count == 0)
        {
            // Deliberately NOT logged. Having no PNL alerts configured is the normal state, and
            // this runs every 20 seconds - at Information it emitted ~4,300 identical lines a day,
            // which buried the feed/order lines that actually matter when something is wrong. The
            // "nothing configured" case is visible from the alerts page, not worth a log line.
            return; // nothing to check - skip reading the cache entirely
        }

        var positions = _positionsCache.GetProjected();
        _logger.LogInformation(
            "PnlAlertHostedService: evaluating {RuleCount} enabled alert(s) against {PositionCount} cached position(s).",
            rules.Count, positions.Count);

        if (positions.Count == 0)
        {
            _logger.LogWarning(
                "PnlAlertHostedService: PositionsCache has zero positions - either nothing is open, the broker " +
                "session/access token isn't valid, or the cache hasn't completed its first resync yet. No alert " +
                "can evaluate until this is non-empty.");
            return;
        }

        foreach (var rule in rules)
        {
            var spread = _spreadStore.Get(rule.SpreadId);
            if (spread is null)
            {
                _logger.LogWarning(
                    "PnlAlertHostedService: alert {RuleId} references spread {SpreadId}, which no longer exists " +
                    "in ISpreadDefinitionStore - skipping (this alert can never fire until the spread is re-added " +
                    "with the same id, or the alert is cancelled).", rule.Id, rule.SpreadId);
                continue;
            }

            var nearPos = positions.FirstOrDefault(p => SameInstrument(spread.NearLeg, p));
            var farPos = positions.FirstOrDefault(p => SameInstrument(spread.FarLeg, p));
            var legs = new[] { nearPos, farPos }.Where(p => p is not null).Cast<PositionSnapshot>().ToList();

            // Nothing open on either leg any more, or a leg is open but doesn't have a live quote
            // yet - same "only net once every leg has a real price" rule PositionsTable.jsx's
            // GroupCard uses, so this never fires on a silently-incomplete combined figure.
            if (legs.Count == 0)
            {
                _logger.LogInformation(
                    "PnlAlertHostedService: alert {RuleId} ({SpreadId}) - neither {Near} nor {Far} found in the " +
                    "current position list (nothing open on this spread right now, or a symbol/exchange mismatch).",
                    rule.Id, rule.SpreadId, spread.NearLeg.TradingSymbol, spread.FarLeg.TradingSymbol);
                continue;
            }

            if (legs.Any(p => p.ExitPrice == 0))
            {
                _logger.LogInformation(
                    "PnlAlertHostedService: alert {RuleId} ({SpreadId}) - {Count} leg(s) open but at least one has " +
                    "no live quote yet (ExitPrice=0) - waiting for a tick before this can net a combined figure.",
                    rule.Id, rule.SpreadId, legs.Count);
                continue;
            }

            var combinedExitPnl = legs.Sum(p => p.ExitPnl);
            var crossed = rule.FireWhenGreaterOrEqual ? combinedExitPnl >= rule.TargetExitPnl : combinedExitPnl <= rule.TargetExitPnl;
            _logger.LogInformation(
                "PnlAlertHostedService: alert {RuleId} ({SpreadId}) - combined Exit PNL {ExitPnl:0.##} vs target " +
                "{Target:0.##} ({Direction}) -> {Crossed}.",
                rule.Id, rule.SpreadId, combinedExitPnl, rule.TargetExitPnl,
                rule.FireWhenGreaterOrEqual ? ">=" : "<=", crossed ? "CROSSED" : "not yet");

            if (!crossed) continue;

            if (rule.LastFiredAt is { } last && DateTimeOffset.UtcNow - last < MinRefireInterval)
            {
                _logger.LogInformation(
                    "PnlAlertHostedService: alert {RuleId} crossed but fired {Ago} ago (< {MinInterval} refire " +
                    "interval) - suppressing a duplicate send.", rule.Id, DateTimeOffset.UtcNow - last, MinRefireInterval);
                continue;
            }

            var direction = rule.FireWhenGreaterOrEqual ? "risen to" : "fallen to";
            var text =
                $"\U0001F514 <b>PNL Alert - {Esc(rule.Underlying)}</b>\n" +
                $"Combined Exit PNL has {direction} your target.\n" +
                $"Exit PNL: <b>{(combinedExitPnl >= 0 ? "+" : "")}{combinedExitPnl:0.##}</b>  (target {rule.TargetExitPnl:0.##})";

            await _notifier.SendAsync(text, cancellationToken);
            _logger.LogInformation(
                "PnlAlertHostedService: fired alert {RuleId} for spread {SpreadId} - Exit PNL {ExitPnl} vs target {Target}.",
                rule.Id, rule.SpreadId, combinedExitPnl, rule.TargetExitPnl);

            rule.LastFiredAt = DateTimeOffset.UtcNow;
            if (rule.OneShot) rule.Enabled = false;
            _alertStore.Upsert(rule);
        }
    }

    private static bool SameInstrument(InstrumentConfig leg, PositionSnapshot p) =>
        string.Equals(leg.TradingSymbol, p.TradingSymbol, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(leg.Exchange, p.Exchange, StringComparison.OrdinalIgnoreCase);

    private static string Esc(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");
}
