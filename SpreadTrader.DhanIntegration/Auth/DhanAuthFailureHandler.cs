using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SpreadTrader.Core.Abstractions;

namespace SpreadTrader.DhanIntegration.Auth;

/// <summary>
/// Centralizes detecting "the Dhan access token is dead" across every call site that hits Dhan
/// (orders, margin, positions, and the market-data feed's disconnect frames), so an expired token
/// clears the in-memory session and tells connected dashboards immediately instead of looking like
/// any other rejection until someone reloads the page. Same role as KiteAuthFailureHandler, but
/// Dhan signals it differently:
///
///  - REST: HTTP 401, and/or an error body carrying <c>errorCode</c> "DH-901"
///    (errorType <c>Invalid_Authentication</c>) - "Client ID or user generated access token is
///    invalid or expired".
///  - Market feed: the server sends a disconnect packet (response code 50) whose reason code says
///    the credentials are dead - 807 (token expired), 808 (authentication failed), 809 (token
///    invalid) or 810 (client id invalid). See DhanMarketFeedClient and
///    DhanSymbology.IsSessionInvalidCode.
///
/// Deliberately NOT treated as an auth failure:
///  - DH-902 (<c>Invalid_Access</c>): the token is fine, the account just isn't subscribed to the
///    Data APIs, or the endpoint isn't entitled. Logging the user out would be wrong and would
///    send them round a login loop that cannot fix it.
///  - DH-905 (<c>Invalid_Request</c>) and friends, including the "invalid IP" rejection that order
///    placement returns when the deploy's egress IP isn't whitelisted with Dhan. Also a real
///    problem, also not a dead token.
/// Both are surfaced by the caller's own error logging instead.
/// </summary>
public sealed class DhanAuthFailureHandler
{
    /// <summary>Dhan's error code for an invalid/expired access token or client id.</summary>
    private const string InvalidAuthenticationCode = "DH-901";

    /// <summary>The market feed's disconnect reason code for an expired token. Kept as a named
    /// constant because it is the most common of the dead-session codes; the full set this reacts
    /// to lives in DhanSymbology.IsSessionInvalidCode.</summary>
    public const int FeedTokenExpiredCode = 807;

    private readonly DhanSession _session;
    private readonly IBrokerAuthNotifier _notifier;
    private readonly ILogger<DhanAuthFailureHandler> _logger;

    public DhanAuthFailureHandler(DhanSession session, IBrokerAuthNotifier notifier, ILogger<DhanAuthFailureHandler> logger)
    {
        _session = session;
        _notifier = notifier;
        _logger = logger;
    }

    /// <summary>Inspects an already-parsed Dhan error response. If it is an authentication failure,
    /// clears the in-memory session (so GET /api/auth/status - and the feed's next reconnect
    /// attempt, which checks the same field - both immediately see it as logged out) and pushes a
    /// real-time notification. Returns true if this was an auth failure; callers use that purely
    /// for their own logging, since the session clearing already happened here either way. Safe to
    /// call on every non-success response.</summary>
    public Task<bool> HandleIfAuthFailureAsync(
        HttpStatusCode statusCode,
        string? errorCode,
        string context,
        CancellationToken cancellationToken)
    {
        var isAuthFailure =
            statusCode is HttpStatusCode.Unauthorized ||
            string.Equals(errorCode, InvalidAuthenticationCode, StringComparison.OrdinalIgnoreCase);

        return isAuthFailure
            ? InvalidateAsync($"{context} (HTTP {(int)statusCode}{(errorCode is null ? "" : $", {errorCode}")})", cancellationToken)
            : Task.FromResult(false);
    }

    /// <summary>The market-feed counterpart: called with the reason code from a Dhan disconnect
    /// packet. Invalidates the session for every code that means the credentials themselves are
    /// dead - 807 (token expired), 808 (authentication failed), 809 (token invalid) and 810
    /// (client id invalid). 805 (too many connections) and 806 (not subscribed to Data APIs) are
    /// real problems but not dead tokens, and logging out over them would hide the actual cause.
    ///
    /// Previously only 807 was handled, so a feed rejected with 809 or 810 reconnected on its
    /// backoff loop indefinitely while the dashboard still showed a healthy session - the ticks
    /// simply stopped, with nothing pointing at the token as the cause.</summary>
    public Task<bool> HandleIfFeedTokenExpiredAsync(int disconnectCode, CancellationToken cancellationToken) =>
        DhanSymbology.IsSessionInvalidCode(disconnectCode)
            ? InvalidateAsync(
                $"the market data feed reported that {DhanSymbology.DescribeFeedErrorCode(disconnectCode)} (code {disconnectCode})",
                cancellationToken)
            : Task.FromResult(false);

    private async Task<bool> InvalidateAsync(string context, CancellationToken cancellationToken)
    {
        var alreadyCleared = string.IsNullOrEmpty(_session.AccessToken);
        _session.AccessToken = null;

        _logger.LogError(
            "Dhan session invalidated while {Context} - the access token is no longer valid. " +
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
            _logger.LogWarning(ex, "Failed to push the Dhan auth-invalidated notification - clients will still pick this up next time they poll GET /api/auth/status.");
        }

        return true;
    }
}
