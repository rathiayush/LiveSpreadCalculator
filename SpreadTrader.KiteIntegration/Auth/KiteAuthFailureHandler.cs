using System.Text.Json;
using Microsoft.Extensions.Logging;
using SpreadTrader.Core.Abstractions;

namespace SpreadTrader.KiteIntegration.Auth;

/// <summary>
/// Centralizes detecting Kite's TokenException - the specific error_type Kite's REST API
/// returns when the access token is invalid, expired, or was revoked
/// (https://kite.trade/docs/connect/v3/exceptions/) - across every call site that hits Kite
/// (KiteOrderGateway, KiteMarginService). Previously every one of those just logged a generic
/// rejection/error and moved on: an expired token looked identical to any other order rejection
/// or transient API hiccup, KiteSession.AccessToken was never actually cleared until a full
/// process restart, and GET /api/auth/status kept reporting "authenticated" (it only checks
/// whether the in-memory string is non-empty, not whether Kite still honors it) for however
/// long the process happened to stay up. Calling HandleIfTokenExceptionAsync at every place a
/// Kite response is parsed fixes both: the session is cleared the instant Kite itself says the
/// token is dead, and connected dashboards are told immediately via IKiteAuthNotifier instead of
/// only finding out next time they reload the page.
/// </summary>
public sealed class KiteAuthFailureHandler
{
    private readonly KiteSession _session;
    private readonly IKiteAuthNotifier _notifier;
    private readonly ILogger<KiteAuthFailureHandler> _logger;

    public KiteAuthFailureHandler(KiteSession session, IKiteAuthNotifier notifier, ILogger<KiteAuthFailureHandler> logger)
    {
        _session = session;
        _notifier = notifier;
        _logger = logger;
    }

    /// <summary>Inspects an already-parsed Kite error response for error_type=="TokenException".
    /// If found: clears the in-memory session (so /api/auth/status - and the ticker's next
    /// reconnect attempt, which checks the same field - both immediately see it as logged out)
    /// and pushes a real-time notification. Returns true if this was a token exception (callers
    /// use this purely for their own logging - the session-clearing/notification already
    /// happened here either way). Safe to call on every non-success response; it's a no-op for
    /// any other error_type (OrderException, InputException, NetworkException, etc.) - those are
    /// genuine order/request problems, not an auth problem, and don't warrant logging the user
    /// out.</summary>
    public async Task<bool> HandleIfTokenExceptionAsync(JsonElement responseRoot, string context, CancellationToken cancellationToken)
    {
        var errorType = responseRoot.TryGetProperty("error_type", out var et) ? et.GetString() : null;
        if (!string.Equals(errorType, "TokenException", StringComparison.Ordinal))
            return false;

        var alreadyCleared = string.IsNullOrEmpty(_session.AccessToken);
        _session.AccessToken = null;

        _logger.LogError(
            "Kite session invalidated (TokenException) while {Context} - the access token is no longer valid. " +
            "Cleared the in-memory session{AlreadyCleared} and notified connected clients to log in again.",
            context, alreadyCleared ? " (was already empty)" : "");

        // Best-effort: a dashboard being briefly disconnected shouldn't turn "your token expired"
        // into an unhandled exception on top of it.
        try
        {
            await _notifier.NotifyAuthInvalidAsync(context, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push the Kite auth-invalidated notification - clients will still pick this up next time they poll GET /api/auth/status.");
        }

        return true;
    }
}
