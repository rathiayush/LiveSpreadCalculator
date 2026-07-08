namespace SpreadTrader.Core.Models;

/// <summary>
/// A standing "notify me" watch on a configured spread's combined Exit PNL (both legs netted
/// together via PositionExitPricing.Compute - see PositionSnapshot.ExitPnl) - deliberately NOT a
/// TriggerRule: it never places an order, only sends a Telegram message once the live combined
/// Exit PNL crosses TargetExitPnl. Scoped to a spread (not a single leg) because Exit PNL only
/// really means something as the netted pair - see PositionsTable.jsx's GroupCard, which is where
/// this is created from.
///
/// Evaluated by PnlAlertHostedService, which runs unconditionally (unlike LivePositionsHostedService,
/// gated on PositionsViewerTracker.HasViewers) - an alert exists specifically so you find out about a
/// PNL move WITHOUT having the page open, so it can't depend on the page being open.
/// </summary>
public sealed class PnlAlertRule
{
    public required string Id { get; set; }
    public required string SpreadId { get; set; }

    /// <summary>Denormalized off SpreadDefinition.Underlying at creation time purely so the Telegram
    /// message and the "active alerts" list don't need a live spread lookup to render a human name -
    /// if the spread is later renamed/removed this just goes stale as a label, it's never
    /// re-resolved for evaluation (SpreadId is what's actually matched against).</summary>
    public required string Underlying { get; set; }

    /// <summary>The combined Exit PNL level that fires this alert - a plain rupee amount (not a
    /// percent), since Exit PNL itself isn't expressed as one either.</summary>
    public decimal TargetExitPnl { get; set; }

    /// <summary>true: fires once combined Exit PNL rises to TargetExitPnl or higher. false: fires
    /// once it falls to TargetExitPnl or lower. Derived once at creation time from target vs. the
    /// live value then (same convention as AddPositionExitTriggerPage's fireWhenGreaterOrEqual),
    /// not re-derived on every evaluation.</summary>
    public bool FireWhenGreaterOrEqual { get; set; }

    /// <summary>true (the common case): auto-disables itself the moment it fires - a one-time "let
    /// me know when I've hit my number" ping. false: stays enabled and can fire again, but never
    /// more often than MinRefireInterval (see PnlAlertHostedService) apart, so a PNL sitting right
    /// on the target doesn't spam the same message every evaluation cycle.</summary>
    public bool OneShot { get; set; } = true;

    public bool Enabled { get; set; } = true;

    public DateTimeOffset? LastFiredAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
