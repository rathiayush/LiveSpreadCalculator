namespace SpreadTrader.Core.Models;

/// <summary>
/// Direction is carried through from the SpreadSnapshot that crossed the threshold (see
/// SpreadCalculationHostedService) rather than re-derived from SpreadValueAtTrigger's sign at
/// dispatch time - deliberately, since the direction decision and the executable spread value
/// use different price sources (ask-vs-ask for direction, bid/ask-per-leg for the value) and
/// aren't guaranteed to agree at the margins when bid/ask spreads are wide. Re-deriving from
/// sign would risk placing an order in the opposite direction from the one actually implied by
/// the premium/discount call - carrying the explicit Direction avoids that entirely.
///
/// FireId correlates this event to the BasketExecutionEvent published later once
/// OrderDispatchHostedService actually finishes placing (or blocking) the basket - the two are
/// published from different services at different times (this one the instant the rule crosses
/// its threshold, the other only after the margin check and order placement round-trips
/// complete), so the dashboard needs a shared id to line up "a trigger fired" with "here's what
/// it actually bought/sold and whether it succeeded".
/// </summary>
public readonly record struct TriggerEvent(
    Guid FireId,
    string RuleId,
    string SpreadId,
    decimal SpreadValueAtTrigger,
    string Direction,
    DateTimeOffset TriggeredAt,
    // Stamped by SpreadCalculationHostedService based on which store (ITriggerRuleStore vs
    // IExitTriggerRuleStore) it evaluated this rule out of - not a field on TriggerRule itself,
    // since entry and exit rules live in physically separate stores (see
    // IExitTriggerRuleStore's doc comment). Lets the Trigger Activity feed split entry vs exit
    // fires onto separate tabs, and OrderDispatchHostedService look the rule back up in the
    // correct store, without re-deriving which store a rule came from at dispatch time.
    bool IsExitTrigger = false);
