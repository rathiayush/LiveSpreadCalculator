namespace SpreadTrader.Core.Abstractions;

/// <summary>
/// Pushed to whenever the app discovers - mid-session, from an actual broker API call failing -
/// that the current access token is no longer valid (Kite's TokenException, Dhan's DH-901
/// invalid-authentication error or an 807 feed disconnect). Lets the UI flip its "authenticated"
/// state and prompt for re-login immediately, rather than only on the next full page load - see
/// each broker's auth-failure handler (the only things that call this) and SignalRBrokerAuthNotifier
/// (the real-time implementation).
/// </summary>
public interface IBrokerAuthNotifier
{
    Task NotifyAuthInvalidAsync(string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Pushed the moment a background auto-login (currently: Dhan TOTP) succeeds after the session
    /// was NOT already authenticated - a fresh boot's startup login, or a reactive re-login
    /// following NotifyAuthInvalidAsync. There is no browser redirect to carry this news for an
    /// automatic login the way there is for Kite's or Dhan's consent flow (both tell the dashboard
    /// via the ?brokerLogin= query param on redirect), so without this push a dashboard tab open
    /// from before the login finished would sit showing "not authenticated" until manually
    /// refreshed, even though the backend is trading fine. See
    /// DhanTotpAutoRenewalHostedService (the only caller) and SignalRBrokerAuthNotifier.
    /// </summary>
    Task NotifyAuthRestoredAsync(CancellationToken cancellationToken);
}
