namespace SpreadTrader.Core.Brokers;

/// <summary>
/// Mutable, in-memory holder for the current broker access token. Registered as a singleton so a
/// token obtained through the login flow (AuthController) is immediately visible to the market
/// data feed, order gateway and every other caller without a restart. Lost on process restart by
/// design - the dashboard caches it client-side and offers it back on the next boot (see
/// POST /api/auth/broker-restore).
/// </summary>
public interface IBrokerSession
{
    /// <summary>The bearer/access token for the live broker session, or null when logged out.
    /// Setting it to null is how a detected auth failure logs the process out (see each broker's
    /// auth-failure handler).</summary>
    string? AccessToken { get; set; }

    /// <summary>The broker account identifier that must accompany the token on every request, for
    /// platforms that require one. Dhan sends it as the <c>client-id</c> header and as
    /// <c>dhanClientId</c> in order payloads; Kite has no equivalent (the api_key plays that role
    /// and comes from configuration), so its implementation returns the configured api key.</summary>
    string? ClientId { get; }

    /// <summary>True when a token is currently held. Note this is a purely local check - it says
    /// nothing about whether the broker still honours that token, which is only ever learned from
    /// a real API call failing (see the auth-failure handlers) or from an explicit verification
    /// call (see IBrokerAuthService.VerifyAccessTokenAsync).</summary>
    bool IsAuthenticated { get; }
}
