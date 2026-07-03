namespace SpreadTrader.Core.Abstractions;

/// <summary>
/// Pushed to whenever the app discovers - mid-session, from an actual Kite API call failing -
/// that the current access token is no longer valid (Kite's TokenException). Lets the UI flip
/// its "Kite: authenticated" state and prompt for re-login immediately, rather than only on the
/// next full page load - see KiteAuthFailureHandler (the only thing that calls this) and
/// SignalRKiteAuthNotifier (the real-time implementation).
/// </summary>
public interface IKiteAuthNotifier
{
    Task NotifyAuthInvalidAsync(string reason, CancellationToken cancellationToken);
}
