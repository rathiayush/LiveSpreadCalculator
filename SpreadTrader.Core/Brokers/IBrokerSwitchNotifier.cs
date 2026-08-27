namespace SpreadTrader.Core.Brokers;

/// <summary>
/// Fired when the operator switches which broker platform is active (see BrokerSwitchState in the
/// composition root, LiveSpreadCalculator/Brokers). Lets an Engine-level singleton like
/// PositionsCache react - re-fetch against the new platform - without Engine ever referencing a
/// broker-specific project or the composition root itself; BrokerSwitchState is the only
/// implementation, registered against this interface so Engine only ever sees the abstraction.
///
/// Optional everywhere it's consumed (a nullable constructor parameter) - a host that never wires
/// up broker switching at all still works identically to before this existed.
/// </summary>
public interface IBrokerSwitchNotifier
{
    event Action<BrokerPlatform>? Changed;

    /// <summary>The platform currently active. Exposed alongside the event so a consumer that only
    /// needs to know "which broker is live right now" - rather than to react to a change - doesn't
    /// have to subscribe and cache it itself. Used by the Options Flow add-on, which is
    /// Dhan-specific and must stand down while another platform is active.</summary>
    BrokerPlatform Current { get; }
}
