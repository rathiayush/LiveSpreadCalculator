using System.Net;
using Microsoft.Extensions.Logging;
using SpreadTrader.Core.Abstractions;

namespace SpreadTrader.KotakIntegration.Auth;

/// <summary>
/// Centralizes detecting "the Kotak session is dead" across every call site that hits Kotak, so an
/// expired/invalidated session clears the in-memory session and tells connected dashboards
/// immediately - same role as DhanAuthFailureHandler/KiteAuthFailureHandler.
///
/// UNLIKE Dhan's DH-901 or Kite's TokenException, this integration has no confirmed Kotak error
/// code for "session invalid" to key off (no live account to observe one against yet - see
/// KotakOptions' doc comment). Detection is therefore a best-effort heuristic: an HTTP 401/403, or
/// an error message that mentions session/token/auth invalidity by name. This will both under- and
/// over-fire compared to a real error-code match until it can be tightened against actual Kotak
/// responses - flagged here rather than silently assumed correct.
/// </summary>
public sealed class KotakAuthFailureHandler
{
    private readonly KotakSession _session;
    private readonly IBrokerAuthNotifier _notifier;
    private readonly ILogger<KotakAuthFailureHandler> _logger;

    public KotakAuthFailureHandler(KotakSession session, IBrokerAuthNotifier notifier, ILogger<KotakAuthFailureHandler> logger)
    {
        _session = session;
        _notifier = notifier;
        _logger = logger;
    }

    public Task<bool> HandleIfAuthFailureAsync(
        HttpStatusCode statusCode, string? errorMessage, string context, CancellationToken cancellationToken)
    {
        var isAuthFailure = statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            || LooksLikeSessionError(errorMessage);

        return isAuthFailure
            ? InvalidateAsync($"{context} (HTTP {(int)statusCode}{(errorMessage is null ? "" : $", {errorMessage}")})", cancellationToken)
            : Task.FromResult(false);
    }

    /// <summary>Best-effort keyword match - see the class doc comment. Looks for "session"/"token"/
    /// "auth" combined with a negative word, so a message like "session expired", "invalid token"
    /// or "authentication failed" triggers it without also catching an unrelated message that
    /// happens to contain one of the words alone (e.g. an order-tag validation error).</summary>
    private static bool LooksLikeSessionError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var lower = message.ToLowerInvariant();

        var mentionsCredential = lower.Contains("session") || lower.Contains("token") || lower.Contains("auth");
        var mentionsProblem = lower.Contains("expired") || lower.Contains("invalid") || lower.Contains("failed") || lower.Contains("unauthor");

        return mentionsCredential && mentionsProblem;
    }

    private async Task<bool> InvalidateAsync(string context, CancellationToken cancellationToken)
    {
        var alreadyCleared = string.IsNullOrEmpty(_session.AccessToken);
        _session.ClearSession();

        _logger.LogError(
            "Kotak session invalidated while {Context} - the session is no longer valid. " +
            "Cleared the in-memory session{AlreadyCleared} and notified connected clients to log in again.",
            context, alreadyCleared ? " (was already empty)" : "");

        try
        {
            await _notifier.NotifyAuthInvalidAsync(context, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push the Kotak auth-invalidated notification - clients will still pick this up next time they poll GET /api/auth/status.");
        }

        return true;
    }
}
