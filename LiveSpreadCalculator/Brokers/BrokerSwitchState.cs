using Microsoft.Extensions.Logging;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Brokers;

namespace LiveSpreadCalculator.Brokers;

/// <summary>
/// The one mutable "which broker is live right now" flag for the whole process - what makes an
/// in-process broker switch possible with no restart. Both platforms' services are always
/// constructed (see BrokerRegistration); every Switching* wrapper reads <see cref="Current"/> on
/// every call to decide which concrete instance actually handles it, and <see cref="Changed"/> is
/// what tells SwitchingMarketDataFeed to stop the outgoing platform's live socket and start the
/// new one's, and PositionsCache to re-fetch against the new platform.
///
/// Deliberately has no direct dependency on PositionsCache (or anything else downstream of the
/// Switching* wrappers) - PositionsCache depends on IPositionsService, which resolves to a
/// SwitchingPositionsService that itself depends on THIS class, so reaching the other way (this
/// class holding a PositionsCache reference to call RefreshAsync directly) would be a circular
/// dependency the DI container can't construct. Subscribing to Changed instead - the same shape
/// PositionsCache already uses for OrderUpdateReceived - avoids that entirely.
///
/// SwitchToAsync is the ONLY way Current should ever change - it does the choreography that makes
/// switching safe, in this order:
///   1. Clear ITickStore. Kite's instrument_token and Dhan's securityId are two unrelated
///      numbering systems for the same real-world contract - the same numeric value can mean a
///      completely different instrument on the other platform. Leaving old ticks in place could
///      have a stale Kite tick misread as live data for whatever Dhan happens to key the same
///      number under, once the universe is rebuilt.
///   2. Clear ISpreadDefinitionStore. Every leg's InstrumentToken/TradingSymbol/Exchange is
///      resolved against the OLD platform's instrument master - meaningless on the new one. This
///      is the same "rebuild the universe after switching" requirement a restart already has;
///      nothing new. Trigger rules and PNL alerts are untouched - they're keyed by a broker-neutral
///      SpreadId (see SpreadDefinition.Id) and simply go inert until a rebuild reintroduces a
///      matching one.
///   3. Flip Current and raise Changed. SwitchingMarketDataFeed reacts by cancelling the outgoing
///      platform's RunAsync and starting the new platform's; PositionsCache reacts by resyncing
///      against the new platform's positions, exactly as it would for an order-update event.
///   4. Persist the choice to disk (BrokerPlatformOverrideStore) so a FUTURE literal process
///      restart (a redeploy, a crash) also boots into this platform, purely as a convenience - the
///      in-process switch above already took effect with no restart involved.
/// </summary>
public sealed class BrokerSwitchState : IBrokerSwitchNotifier
{
    private readonly object _lock = new();
    private readonly ITickStore _tickStore;
    private readonly ISpreadDefinitionStore _spreadStore;
    private readonly string _overridePath;
    private readonly ILogger<BrokerSwitchState> _logger;

    public BrokerSwitchState(
        BrokerPlatform initial,
        ITickStore tickStore,
        ISpreadDefinitionStore spreadStore,
        string overridePath,
        ILogger<BrokerSwitchState> logger)
    {
        Current = initial;
        _tickStore = tickStore;
        _spreadStore = spreadStore;
        _overridePath = overridePath;
        _logger = logger;
    }

    public BrokerPlatform Current { get; private set; }

    /// <summary>Raised AFTER Current has already flipped - subscribers see a consistent new world,
    /// never a transition in progress. SwitchingMarketDataFeed and PositionsCache are the current
    /// subscribers.</summary>
    public event Action<BrokerPlatform>? Changed;

    public bool SwitchTo(BrokerPlatform platform)
    {
        lock (_lock)
        {
            if (Current == platform) return false;
        }

        _logger.LogWarning(
            "Switching broker platform from {Old} to {New} - clearing live ticks and spread definitions, " +
            "the universe will need rebuilding on the new platform.", Current, platform);

        foreach (var tick in _tickStore.Snapshot())
            _tickStore.Remove(tick.InstrumentToken);

        foreach (var spread in _spreadStore.GetAll())
            _spreadStore.Remove(spread.Id);

        lock (_lock)
        {
            Current = platform;
        }
        Changed?.Invoke(platform);

        BrokerPlatformOverrideStore.Write(_overridePath, platform);

        return true;
    }
}
