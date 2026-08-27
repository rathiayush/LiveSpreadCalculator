namespace SpreadTrader.Core.OptionsFlow;

/// <summary>
/// Decides which OptionFlowSignals from a fresh snapshot are newly worth alerting on, and de-dupes
/// so the same event never fires twice. One instance is kept per tracked index (see
/// OptionsFlowEngine.IndexFlowState.AlertGate) - crossings on NIFTY and BANKNIFTY are independent.
///
/// Deliberately separate from OptionsFlowEngine and free of any broker/DI dependency, so this - the
/// part that actually decides "does a notification go out right now" - can be unit tested with
/// plain OptionFlowSignal values instead of standing up the whole engine.
///
/// Only three signal Kinds are alert-worthy: "CePeCross" (CE crossing PE on CDV/OBV),
/// "PriceEmaCross" (the 9/21 EMA crossover on the index's own spot price chart), and
/// "LookbackChange" (a 5/10/15-bar % move past threshold). The others OptionsFlowEngine computes
/// - the fast/slow EMA cross within one side's CDV/OBV - are informational only, shown in the
/// dashboard's signal table but never alerted; see OptionsFlowEngine.DispatchAlertsAsync.
/// </summary>
public sealed class OptionsFlowAlertGate
{
    private static readonly HashSet<string> AlertableKinds =
        new(StringComparer.Ordinal) { "CePeCross", "PriceEmaCross", "LookbackChange" };

    private readonly Dictionary<string, DateTimeOffset> _lastAlertedAt = new(StringComparer.Ordinal);
    private bool _primed;

    /// <summary>
    /// Filters a snapshot's full signal list down to the ones newly worth alerting on, and records
    /// them as alerted so a repeat call with the same signals returns nothing.
    ///
    /// THE FIRST CALL EVER MADE ON A GATE NEVER RETURNS ANYTHING - it only remembers where every
    /// alert-worthy signal currently stands. Without this, enabling the feature (or restarting the
    /// process) mid-session would replay every crossing and threshold-breach already backfilled
    /// from the session open as a fresh alert, firing a burst of stale notifications the instant
    /// the feature turns on rather than only alerting on what actually changes from here.
    /// </summary>
    public IReadOnlyList<OptionFlowSignal> SelectNewAlerts(IReadOnlyList<OptionFlowSignal> signals)
    {
        if (!_primed)
        {
            foreach (var signal in signals)
            {
                if (AlertableKinds.Contains(signal.Kind) && signal.AtUtc is { } atUtc)
                    _lastAlertedAt[Key(signal)] = atUtc;
            }

            _primed = true;
            return Array.Empty<OptionFlowSignal>();
        }

        List<OptionFlowSignal>? result = null;

        foreach (var signal in signals)
        {
            if (!AlertableKinds.Contains(signal.Kind)) continue;
            if (signal.AtUtc is not { } atUtc) continue;

            var key = Key(signal);
            // Same bar this signal last alerted on (or, for LookbackChange, the same bar the
            // threshold was already reported as crossed on) - nothing new to say.
            if (_lastAlertedAt.TryGetValue(key, out var last) && last == atUtc) continue;

            _lastAlertedAt[key] = atUtc;
            (result ??= new List<OptionFlowSignal>()).Add(signal);
        }

        return (IReadOnlyList<OptionFlowSignal>?)result ?? Array.Empty<OptionFlowSignal>();
    }

    private static string Key(OptionFlowSignal signal) => $"{signal.Kind}|{signal.Metric}|{signal.Scope}";
}
