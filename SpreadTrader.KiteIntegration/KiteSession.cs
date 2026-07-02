namespace SpreadTrader.KiteIntegration;

/// <summary>
/// Mutable, in-memory holder for the current Kite access token. Registered as a singleton so
/// the token obtained via the login flow (AuthController) is immediately visible to the ticker
/// and order gateway without a restart. Lost on process restart by design (no persistence).
/// </summary>
public sealed class KiteSession
{
    public string? AccessToken { get; set; }
}
