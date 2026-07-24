namespace SpreadTrader.DhanIntegration;

/// <summary>
/// Everything needed to talk to DhanHQ API v2. Bound from the "Dhan" section of appsettings.json,
/// or the equivalent <c>Dhan__*</c> environment variables in Docker/Azure (which is how the
/// secrets are meant to be supplied - never commit ClientId/AccessToken/AppSecret).
///
/// Dhan offers two ways to obtain an access token, and this app supports both - see
/// DhanAuthService:
///
///  1. STATIC TOKEN (the common case for an individual trader). Generate an access token in the
///     Dhan web portal (Profile -> DhanHQ Trading APIs) and put it in <see cref="AccessToken"/>
///     along with your <see cref="ClientId"/>. There is no browser login flow at all; the app is
///     authenticated the moment it boots. Portal tokens are issued with a validity you choose (up
///     to 30 days), so set <see cref="AccessTokenValidityDays"/> to match.
///
///  2. CONSENT / OAuth FLOW (for a registered Dhan partner app). Set <see cref="AppId"/> and
///     <see cref="AppSecret"/>; the app then mints a consent, sends the browser to Dhan's login
///     page, and consumes the tokenId Dhan redirects back with. Tokens from this flow are valid
///     for 24 hours.
///
/// If AppId/AppSecret are set, the consent flow is offered; otherwise the app runs purely off the
/// static token.
/// </summary>
public sealed class DhanOptions
{
    public const string SectionName = "Dhan";

    /// <summary>The Dhan account id (a numeric string, e.g. "1100123456"). Sent as the
    /// <c>client-id</c> header on every REST call, as <c>dhanClientId</c> in order payloads, and
    /// as the <c>clientId</c> query parameter on the market-feed WebSocket. Required in both auth
    /// modes.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>A pre-generated access token from the Dhan web portal. Optional - leave empty when
    /// using the consent flow, in which case the token is obtained at login and held in memory
    /// only. When set, it seeds the session at startup so no login is needed at all.</summary>
    public string? AccessToken { get; set; }

    /// <summary>Partner app credentials for the consent login flow. Both must be set for the
    /// browser login to be offered; leave both empty to run purely off <see cref="AccessToken"/>.
    /// </summary>
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

    /// <summary>The instrument dump ("security id list"). The DETAILED csv is the default because
    /// it is the only one that carries tick size, which order pricing needs - see
    /// DhanInstrumentMasterService.</summary>
    public string ScripMasterUrl { get; set; } = "https://images.dhan.co/api-data/api-scrip-master-detailed.csv";

    /// <summary>ltp, quote, or full. Spread triggers are evaluated against best bid/ask, which only
    /// "full" mode carries (Dhan request code 21 - the Full packet is the only one with market
    /// depth). "quote" (17) has OHLC/volume but no depth, "ltp" (15) is last price only.</summary>
    public string TickerMode { get; set; } = "full";

    /// <summary>Order placement pacing. Dhan documents 25 orders/sec; the default leaves headroom
    /// under that rather than riding the ceiling, since a rejected-for-rate-limit leg of a
    /// two-leg basket is far more expensive than a few extra milliseconds.</summary>
    public int OrdersPerSecond { get; set; } = 15;

    /// <summary>How long a token obtained from the Dhan web portal stays valid, in days. Only used
    /// for static-token mode, to tell the dashboard when to stop offering its cached copy back.
    /// Dhan lets you choose up to 30 days when generating one; set this to whatever you chose.
    /// Consent-flow tokens ignore this and use Dhan's fixed 24-hour validity.</summary>
    public int AccessTokenValidityDays { get; set; } = 30;

    /// <summary>True when partner credentials for the browser consent flow are configured.</summary>
    public bool HasConsentCredentials =>
        !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(AppSecret);
}
