namespace SpreadTrader.Core.Brokers;

/// <summary>
/// Which broker the engine talks to. Everything downstream of this - ticks, orders, margin,
/// positions, instrument resolution and the login flow - is resolved from a single set of
/// interfaces (see the rest of this folder), so adding/switching a platform is a composition-root
/// concern only: no engine service, controller, or store knows which broker is live.
/// </summary>
public enum BrokerPlatform
{
    /// <summary>Zerodha Kite Connect v3 - the platform this app was originally built against.</summary>
    Kite = 0,

    /// <summary>DhanHQ API v2.</summary>
    Dhan = 1,
}

/// <summary>
/// The one switch that decides which broker implementation gets wired into the DI container at
/// startup (see BrokerRegistration in the Api project). Set it in appsettings.json:
///
///   "Broker": { "Platform": "Dhan" }
///
/// or via the <c>Broker__Platform</c> environment variable in Docker/Azure. Changing it requires
/// a restart - the choice is made once while the container is being built, deliberately, so there
/// is never a half-Kite/half-Dhan state at runtime and no per-call branching in the hot tick path.
///
/// IMPORTANT - the two platforms use different instrument identifiers and different tradingsymbol
/// formats (Kite: "RELIANCE26JULFUT" + instrument_token; Dhan: "RELIANCE-Jul2026-FUT" + securityId).
/// Spread definitions are in-memory only and the universe is rebuilt on demand, so switching
/// platforms means rebuilding the universe (POST /api/universe/build) once after the restart -
/// see DhanInstrumentMasterService's doc comment.
/// </summary>
public sealed class BrokerOptions
{
    public const string SectionName = "Broker";

    /// <summary>Defaults to Kite so an existing deployment that has never heard of this setting
    /// keeps behaving exactly as it did before this abstraction existed.</summary>
    public BrokerPlatform Platform { get; set; } = BrokerPlatform.Kite;
}
