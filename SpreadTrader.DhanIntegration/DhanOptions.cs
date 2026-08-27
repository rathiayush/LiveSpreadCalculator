namespace SpreadTrader.DhanIntegration;

/// <summary>
/// Everything needed to talk to DhanHQ API v2. Bound from the "Dhan" section of appsettings.json,
/// or the equivalent <c>Dhan__*</c> environment variables in Docker/Azure (which is how the
/// secrets are meant to be supplied - never commit ClientId/AccessToken/Pin/TotpSecret/AppSecret).
///
/// Dhan offers three ways to obtain an access token; this app supports all three, chosen by
/// <see cref="AuthMode"/> - see DhanAuthService and DhanTotpAutoRenewalHostedService:
///
///  1. STATIC TOKEN (AuthMode "Static", the default). Generate an access token in the Dhan web
///     portal (Profile -> DhanHQ Trading APIs) and put it in <see cref="AccessToken"/> along with
///     your <see cref="ClientId"/>. There is no browser login flow and no code running on your
///     behalf; the app is authenticated the moment it boots. Portal tokens are currently fixed at
///     24-hour validity (Dhan removed the longer-lived option this app originally supported), so
///     this needs a fresh token pasted into the dashboard's "Enter Dhan token" box roughly once a
///     day - the one manual step this mode still carries.
///
///  2. TOTP (AuthMode "Totp") - fully automatic, no daily paste, no browser ever. Set
///     <see cref="Pin"/> and <see cref="TotpSecret"/> alongside <see cref="ClientId"/>, leave
///     <see cref="AccessToken"/> empty. DhanTotpAutoRenewalHostedService computes a fresh 6-digit
///     TOTP code from the secret whenever a token is needed (RFC 6238 - the same algorithm an
///     authenticator app runs, computed here instead of read off a phone) and calls Dhan's
///     generateAccessToken endpoint with it, both at startup and again shortly before every 24h
///     token expires. See <see cref="TotpSecret"/>'s doc comment for what this trades away for the
///     convenience.
///
///  3. CONSENT / OAuth FLOW (for a registered Dhan partner app). Set <see cref="AppId"/> and
///     <see cref="AppSecret"/>; the app then mints a consent, sends the browser to Dhan's login
///     page, and consumes the tokenId Dhan redirects back with. Tokens from this flow are valid
///     for 24 hours. Independent of <see cref="AuthMode"/> - it only controls whether the
///     dashboard's login button is offered, and can be layered on top of either mode above as a
///     manual fallback.
/// </summary>
public sealed class DhanOptions
{
    public const string SectionName = "Dhan";

    /// <summary>Which login method this deployment is meant to use:
    /// "Static" (default - a token you paste in, valid 24h, no code runs on your behalf),
    /// "Totp" (fully automatic - see <see cref="UseTotpAuth"/>), or
    /// "Consent" (browser OAuth-style login via <see cref="AppId"/>/<see cref="AppSecret"/> - see
    /// <see cref="UseConsentAuth"/> and DhanAuthService's consent flow). Case-insensitive. An
    /// unrecognised value is treated as "Static" rather than failing startup, so a typo degrades to
    /// "you'll need to paste a token" instead of refusing to boot.
    ///
    /// Setting this to "Consent" is purely declarative/self-documenting - the login button is
    /// actually gated on <see cref="HasConsentCredentials"/> regardless of this value (so an
    /// existing deployment that already has AppId/AppSecret set keeps working unchanged even if
    /// AuthMode is still "Static" or "Totp"). What changes when AuthMode IS "Consent" is the
    /// guidance text shown while AppId/AppSecret are missing - see
    /// DhanAuthService.NotAuthenticatedGuidance - so a misconfigured Consent deployment points you
    /// at the right fix instead of the Static-mode message.</summary>
    public string AuthMode { get; set; } = "Static";

    /// <summary>The Dhan account id (a numeric string, e.g. "1100123456"). Sent as the
    /// <c>client-id</c> header on every REST call, as <c>dhanClientId</c> in order payloads, and
    /// as the <c>clientId</c> query parameter on the market-feed WebSocket. Required in every auth
    /// mode.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>A pre-generated access token from the Dhan web portal. Only meaningful in
    /// AuthMode "Static" - leave empty when using Totp or Consent, where the token is obtained
    /// automatically and held in memory only. When set, it seeds the session at startup so no
    /// login is needed until it expires (currently 24 hours after issue).</summary>
    public string? AccessToken { get; set; }

    /// <summary>Your Dhan account PIN. Required for AuthMode "Totp" - Dhan's generateAccessToken
    /// endpoint takes the PIN alongside the TOTP code on every call, so this app can mint its own
    /// tokens with no human involved. Not used by any other auth mode. SECRET - see
    /// <see cref="TotpSecret"/>'s doc comment; the same handling applies here.</summary>
    public string? Pin { get; set; }

    /// <summary>The base32 TOTP seed from Dhan Web -&gt; Profile -&gt; DhanHQ Trading APIs -&gt;
    /// Setup TOTP (the same string, or QR code, you would otherwise scan into an authenticator
    /// app). Required for AuthMode "Totp" alongside <see cref="Pin"/> - see TotpGenerator.
    ///
    /// TREAT THIS AS A LOGIN CREDENTIAL, NOT A TOKEN. A leaked access token is bad but
    /// self-limiting - it expires within 24 hours regardless of what anyone does with it. This
    /// seed does not expire: paired with the PIN, it lets whoever holds both mint a fresh valid
    /// token at will, indefinitely, until you disable TOTP in the Dhan portal and set up a new
    /// one. Set it only via an environment variable or user-secrets, never in a committed file,
    /// and rotate it (Dhan Web -&gt; Setup TOTP again) if you ever suspect it leaked - the same
    /// discipline as a password, not a bearer token.</summary>
    public string? TotpSecret { get; set; }

    /// <summary>Partner app credentials for the consent login flow. Both must be set for the
    /// browser login to be offered; leave both empty to run purely off <see cref="AuthMode"/>'s
    /// automatic path. Independent of AuthMode - see the class doc comment.</summary>
    public string? AppId { get; set; }
    public string? AppSecret { get; set; }

    public string RestBaseUrl { get; set; } = "https://api.dhan.co/v2";

    /// <summary>Host for the consent/login endpoints - a different host from the trading API.</summary>
    public string AuthBaseUrl { get; set; } = "https://auth.dhan.co";

    /// <summary>Live market data feed. The v2 handshake authenticates via query string
    /// (<c>?version=2&amp;token=...&amp;clientId=...&amp;authType=2</c>) rather than the binary
    /// authorisation packet v1 required.</summary>
    public string WsUrl { get; set; } = "wss://api-feed.dhan.co";

    /// <summary>Order updates arrive on their own socket on Dhan (unlike Kite, which multiplexes
    /// them onto the ticker connection). DhanMarketFeedClient runs this alongside the feed and
    /// surfaces both through one IMarketDataFeed - see its doc comment.</summary>
    public string OrderUpdateWsUrl { get; set; } = "wss://api-order-update.dhan.co";

    /// <summary>The instrument dump ("security id list"). Must be the COMPACT csv: it is the only
    /// one of Dhan's two files with a per-contract trading symbol column (SEM_TRADING_SYMBOL), and
    /// it carries tick and lot size just as the detailed file does. The detailed csv
    /// (api-scrip-master-detailed.csv) has no usable trading symbol column, so pointing this at it
    /// loads zero instruments - see DhanInstrumentMasterService.ParseCsv.</summary>
    public string ScripMasterUrl { get; set; } = "https://images.dhan.co/api-data/api-scrip-master.csv";

    /// <summary>ltp, quote, or full. Spread triggers are evaluated against best bid/ask, which only
    /// "full" mode carries (Dhan request code 21 - the Full packet is the only one with market
    /// depth). "quote" (17) has OHLC/volume but no depth, "ltp" (15) is last price only.</summary>
    public string TickerMode { get; set; } = "full";

    /// <summary>Order placement pacing. Dhan's published rate limit for Order APIs is 10 requests
    /// per second (and 250/minute, 1,000/hour, 7,000/day); the default leaves headroom under that
    /// rather than riding the ceiling, since a rejected-for-rate-limit leg of a two-leg basket is
    /// far more expensive than a few extra milliseconds. Note the limit covers order READS as well
    /// as writes, and every placement here is followed by up to four status polls - so the
    /// effective request rate per order is several times this number.</summary>
    public int OrdersPerSecond { get; set; } = 8;

    /// <summary>How long a token obtained from the Dhan web portal stays valid, in days. Only used
    /// for static-token mode, to tell the dashboard when to stop offering its cached copy back.
    /// Dhan currently issues portal tokens fixed at 24-hour validity - this defaults to 1
    /// accordingly. Kept configurable (rather than hardcoded) in case Dhan changes it again;
    /// Totp and Consent tokens ignore this and use Dhan's own fixed 24-hour validity for those
    /// flows directly.</summary>
    public int AccessTokenValidityDays { get; set; } = 1;

    /// <summary>True when partner credentials for the browser consent flow are configured.</summary>
    public bool HasConsentCredentials =>
        !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(AppSecret);

    /// <summary>True when AuthMode selects the TOTP auto-login path.</summary>
    public bool UseTotpAuth => string.Equals(AuthMode, "Totp", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when AuthMode explicitly declares intent to use the browser Consent/OAuth
    /// flow. Does NOT gate whether the login button actually appears - see
    /// <see cref="HasConsentCredentials"/> for that, which still works standalone (AppId/AppSecret
    /// set with AuthMode left at "Static"/"Totp") for backward compatibility. This flag only drives
    /// which guidance message DhanAuthService shows when consent credentials are absent.</summary>
    public bool UseConsentAuth => string.Equals(AuthMode, "Consent", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when everything TOTP auto-login needs is actually configured. Checked before
    /// DhanTotpAutoRenewalHostedService does anything, rather than failing partway through with a
    /// confusing "pin missing" error from Dhan's own API.</summary>
    public bool HasTotpCredentials =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(Pin) && !string.IsNullOrWhiteSpace(TotpSecret);
}
