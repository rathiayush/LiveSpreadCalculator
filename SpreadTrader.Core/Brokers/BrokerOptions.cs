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

    /// <summary>Kotak Neo Trade API. NOT offered in the dashboard's broker-switch UI
    /// (BrokerPlatformPage.jsx) by design - switch to it only via the backend, e.g.
    /// POST /api/settings/broker-platform or the Broker__Platform environment variable. Also has no
    /// MCX support - see SpreadTrader.KotakIntegration.KotakSymbology's doc comment.</summary>
    Kotak = 2,
}

/// <summary>
/// The STARTUP DEFAULT for which broker platform is live, read once at boot to seed
/// BrokerSwitchState's initial value (see BrokerRegistration.AddBrokerIntegration in the Api
/// project). Set it in appsettings.json:
///
///   "Broker": { "Platform": "Dhan" }
///
/// or via the <c>Broker__Platform</c> environment variable in Docker/Azure. This is NOT the only
/// way the live platform changes: every platform's full stack is always constructed regardless of
/// this value (see BrokerRegistration's own doc comment), and BrokerSwitchState.SwitchTo lets the
/// live platform change in-process, with no restart, via POST /api/settings/broker-platform - this
/// setting only decides which one a fresh boot starts on before any such switch happens, and is
/// itself overridden by whatever a prior runtime switch persisted to disk (see
/// BrokerPlatformOverrideStore).
///
/// IMPORTANT - every platform uses different instrument identifiers and different tradingsymbol
/// formats (Kite: "RELIANCE26JULFUT" + instrument_token; Dhan: "RELIANCE-Jul2026-FUT" + securityId).
/// Spread definitions are in-memory only and the universe is rebuilt on demand, so switching
/// platforms means rebuilding the universe (POST /api/universe/build) once after the switch - see
/// DhanInstrumentMasterService's doc comment.
/// </summary>
public sealed class BrokerOptions
{
    public const string SectionName = "Broker";

    /// <summary>Defaults to Kite so an existing deployment that has never heard of this setting
    /// keeps behaving exactly as it did before this abstraction existed.</summary>
    public BrokerPlatform Platform { get; set; } = BrokerPlatform.Kite;
}
