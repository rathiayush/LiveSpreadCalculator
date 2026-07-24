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
}
