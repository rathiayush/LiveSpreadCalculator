namespace SpreadTrader.KotakIntegration;

/// <summary>
/// Everything needed to talk to the Kotak Neo Trade API. Bound from the "Kotak" section of
/// appsettings.json, or the equivalent <c>Kotak__*</c> environment variables (which is how the
/// secrets are meant to be supplied - never commit real values here, same discipline as
/// Kite/Dhan).
///
/// KOTAK'S LOGIN IS A TWO-STEP TOTP HANDSHAKE, DIFFERENT FROM BOTH KITE AND DHAN:
///   1. totp_login: POST {SessionBaseUrl}/login/1.0/tradeApiLogin with MobileNumber+Ucc+Totp
///      (a fresh 6-digit RFC 6238 code from TotpSecret - see TotpGenerator, shared with Dhan's
///      identical algorithm) - returns a short-lived "view token" + session id.
///   2. totp_validate: POST {SessionBaseUrl}/login/1.0/tradeApiValidate with Mpin, authenticated
///      by the view token from step 1 - returns the actual "edit token" + "edit sid" that every
///      other call (orders, positions, quotes, ...) authenticates with.
/// There is no browser/OAuth flow at all - SupportsInteractiveLogin is always false (see
/// KotakAuthService) and KotakSessionAutoRenewalHostedService drives this exactly like Dhan's TOTP
/// auto-renewal drives Dhan's single-step login, just with the extra validate call.
///
/// IMPORTANT - BASE URLS ARE BEST-EFFORT. Kotak Neo's REST API is documented to span several
/// different hostnames per environment (a session/login host, a trading-API gateway host, a
/// scrip-master host, ...), reverse-engineered here from the public Kotak-Neo/Kotak-neo-api-v2
/// Python SDK's source rather than from live-tested access (this deployment has no Kotak Neo
/// account yet - see KotakOptions' constructor comment). <see cref="RestBaseUrl"/> is deliberately
/// a SINGLE configurable base used for every endpoint category (session, orders, positions,
/// margin, scrip master) rather than guessing at which of Kotak's several hostnames fronts which
/// category - override it once real Kotak Neo API access is available and the true routing is
/// confirmed. Every literal path below (login/1.0/tradeApiLogin, quick/order/rule/ms/place, ...)
/// comes directly from that SDK's PROD_URL table, so those should be correct even if the base host
/// needs adjusting.
/// </summary>
public sealed class KotakOptions
{
    public const string SectionName = "Kotak";

    /// <summary>The API consumer key from Kotak Neo's API Dashboard (Invest -&gt; Trade API -&gt;
    /// API Dashboard -&gt; Generate API access token). Sent as the raw value of the
    /// <c>Authorization</c> header on every call - NOT a Bearer token, and NOT the same thing as
    /// the session token obtained from login (see <see cref="KotakSession"/>).</summary>
    public string ConsumerKey { get; set; } = "";

    /// <summary>The mobile number registered on the Kotak Neo trading account, used in step 1 of
    /// login (totp_login). Digits only, no country code prefix, matching the SDK's own examples.</summary>
    public string MobileNumber { get; set; } = "";

    /// <summary>The Kotak Neo client code (UCC - "Unique Client Code"), used alongside
    /// MobileNumber in step 1 of login. This is Kotak's equivalent of Dhan's ClientId.</summary>
    public string Ucc { get; set; } = "";

    /// <summary>The account's trading MPIN, used in step 2 of login (totp_validate) to exchange
    /// the view token for the actual session token. A login credential, not a token - see
    /// <see cref="TotpSecret"/>'s doc comment for the handling this implies; the same discipline
    /// applies here.</summary>
    public string? Mpin { get; set; }

    /// <summary>The base32 TOTP seed registered via Kotak Neo's TOTP setup page
    /// (https://www.kotakneo.com/platform/kotak-neo-trade-api/totp-registration/) - the same
    /// string/QR an authenticator app would use. Required for step 1 of login alongside
    /// <see cref="MobileNumber"/>/<see cref="Ucc"/> - see TotpGenerator (RFC 6238, identical
    /// algorithm to Dhan's).
    ///
    /// TREAT THIS AS A LOGIN CREDENTIAL, NOT A TOKEN - see DhanOptions.TotpSecret's doc comment for
    /// the exact same reasoning: paired with the MPIN, this lets whoever holds both mint a fresh
    /// valid session at will, indefinitely, until TOTP is re-registered. Never commit a real value;
    /// set it only via an environment variable or user-secrets.</summary>
    public string? TotpSecret { get; set; }

    /// <summary>"prod" or "uat". Selects which of Kotak's environments <see cref="RestBaseUrl"/>/
    /// <see cref="WsUrl"/> default to when left unset. Case-insensitive; anything other than "uat"
    /// is treated as "prod".</summary>
    public string Environment { get; set; } = "prod";

    private string? _restBaseUrl;

    /// <summary>Base URL every REST call is built against - see the class doc comment's
    /// "BASE URLS ARE BEST-EFFORT" note. Defaults by <see cref="Environment"/> when not explicitly
    /// set: Kotak's trading-API gateway host for prod, the UAT gateway host otherwise.</summary>
    public string RestBaseUrl
    {
        get => _restBaseUrl ?? (IsUat ? "https://nsbxapi-gw.kotaksecurities.com" : "https://gw-napi.kotaksecurities.com");
        set => _restBaseUrl = value;
    }

    private string? _wsUrl;

    /// <summary>Live market-data WebSocket. Kotak's quotes/subscription socket - see
    /// KotakMarketFeedClient.</summary>
    public string WsUrl
    {
        get => _wsUrl ?? "wss://mlhsm.kotaksecurities.com";
        set => _wsUrl = value;
    }

    /// <summary>Fixed per-environment constant Kotak's own SDK sends as the <c>neo-fin-key</c>
    /// header on login calls - not a secret (it's the same public value for every UAT caller, and
    /// "neotradeapi" for prod), just an API-versioning marker Kotak's gateway expects.</summary>
    public string NeoFinKey => IsUat ? "bQJNkL5z8m4aGcRgjDvXhHfSx7VpZnE" : "neotradeapi";

    public bool IsUat => string.Equals(Environment, "uat", StringComparison.OrdinalIgnoreCase);

    /// <summary>ltp, quote, or full - see DhanOptions.TickerMode's identical doc comment. "full" is
    /// required for spread triggers, which are evaluated against best bid/ask.</summary>
    public string TickerMode { get; set; } = "full";

    /// <summary>Order placement pacing. Kotak documents up to 10 orders/sec; the default leaves
    /// headroom under that, same reasoning as DhanOptions.OrdersPerSecond.</summary>
    public int OrdersPerSecond { get; set; } = 8;

    /// <summary>True when everything the TOTP+MPIN login needs is actually configured. Checked
    /// before KotakSessionAutoRenewalHostedService does anything, rather than failing partway
    /// through with a confusing error from Kotak's own API.</summary>
    public bool HasLoginCredentials =>
        !string.IsNullOrWhiteSpace(MobileNumber) && !string.IsNullOrWhiteSpace(Ucc) &&
        !string.IsNullOrWhiteSpace(Mpin) && !string.IsNullOrWhiteSpace(TotpSecret) &&
        !string.IsNullOrWhiteSpace(ConsumerKey);
}
