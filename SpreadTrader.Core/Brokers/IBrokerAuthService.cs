namespace SpreadTrader.Core.Brokers;

/// <summary>Outcome of completing a broker login handshake - see
/// <see cref="IBrokerAuthService.CompleteLoginAsync"/>.</summary>
/// <param name="Success">True only when an access token was actually obtained AND stored on the
/// session. Anything else (user cancelled at the broker, exchange call rejected, network failure)
/// is false with <paramref name="Error"/> set.</param>
/// <param name="AccessToken">The freshly obtained token, already written to IBrokerSession.</param>
/// <param name="ClientId">The broker account id that came back with the token, where the platform
/// returns one (Dhan does; Kite does not).</param>
/// <param name="ExpiresUtc">When this token stops being valid, as best the platform can say. Handed
/// to the dashboard so its client-side cache expires the token at the right moment instead of
/// assuming one broker's schedule - Kite invalidates daily at ~6 AM IST; every Dhan token
/// (portal-generated, consent-flow, or TOTP-minted) currently runs 24 hours from issue.</param>
public sealed record BrokerLoginResult(
    bool Success,
    string? AccessToken,
    string? ClientId,
    DateTimeOffset? ExpiresUtc,
    string? Error);

/// <summary>
/// The broker login handshake, reduced to the three things the app actually needs: where to send
/// the browser, how to turn whatever the broker redirects back with into an access token, and how
/// to check whether a token it already has is still honoured.
///
/// The two supported platforms differ enough that none of this can be a shared implementation:
///  - Kite: a fixed login URL built from the api_key, redirected back with <c>request_token</c>,
///    exchanged for an access token via a SHA-256 checksum of api_key+request_token+api_secret.
///  - Dhan: the login URL itself requires a prior server-to-server call to mint a consent id, the
///    browser is redirected back with <c>tokenId</c>, and that is consumed for the access token.
///    Dhan also supports skipping the flow entirely with a token generated in the web portal.
///
/// AuthController is written against this interface only and passes the callback query through
/// untouched, so neither the controller nor the dashboard has to know which parameter name the
/// live platform redirects with.
/// </summary>
public interface IBrokerAuthService
{
    /// <summary>Human-readable name of the live platform ("Zerodha Kite", "Dhan"), surfaced by
    /// GET /api/auth/broker so the dashboard can label its login button and status pill for
    /// whichever broker is actually configured instead of hardcoding one.</summary>
    string PlatformName { get; }

    /// <summary>False when this platform is configured in a way that has no browser login flow at
    /// all - specifically Dhan with a static access token from the web portal and no app
    /// id/secret. The dashboard uses this to hide the login button and explain that the token
    /// comes from configuration instead of leaving a button that can only ever fail.</summary>
    bool SupportsInteractiveLogin { get; }

    /// <summary>Builds the URL the browser should be sent to in order to log in. Async because
    /// Dhan requires a server-to-server consent call first to obtain the id the login URL is keyed
    /// by; Kite's is a pure string format and completes synchronously.</summary>
    /// <exception cref="InvalidOperationException">The platform has no interactive login flow, or
    /// the credentials needed to start one are not configured.</exception>
    Task<string> BuildLoginUrlAsync(CancellationToken cancellationToken);

    /// <summary>Completes the handshake from whatever the broker redirected back to
    /// /api/auth/callback with, and on success stores the token on IBrokerSession. The raw query
    /// string is passed straight through (keys are matched case-insensitively) precisely so this
    /// interface doesn't have to name a parameter that differs per platform.</summary>
    Task<BrokerLoginResult> CompleteLoginAsync(
        IReadOnlyDictionary<string, string?> callbackQuery,
        CancellationToken cancellationToken);

    /// <summary>Cheapest possible "does the broker still honour this token" check - one lightweight
    /// authenticated GET (Kite's /user/profile, Dhan's /v2/profile). Used by the restore path,
    /// where no other API call is happening yet that would naturally reveal a dead token. Returns
    /// false on any failure, including network errors: an unverifiable token must never be treated
    /// as live.</summary>
    Task<bool> VerifyAccessTokenAsync(string accessToken, CancellationToken cancellationToken);

    /// <summary>When a token obtained now would stop being valid, per this platform's own schedule.
    /// Returned to the dashboard alongside the token so its cache can expire at the right time -
    /// see BrokerLoginResult.ExpiresUtc.</summary>
    DateTimeOffset GetTokenExpiryUtc(DateTimeOffset issuedAtUtc);
}
