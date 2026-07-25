using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SpreadTrader.DhanIntegration.Auth;

/// <summary>
/// Keeps DhanSession authenticated with zero human involvement, for deployments that set
/// DhanOptions.AuthMode to "Totp" instead of pasting a token. Only registered by
/// BrokerRegistration when <see cref="DhanOptions.UseTotpAuth"/> is true - in Static mode (the
/// default) this class does not run at all, and the session is seeded from
/// DhanOptions.AccessToken exactly as it always was.
///
/// A token is minted (via DhanAuthService.GenerateAccessTokenViaTotpAsync) in exactly three
/// situations, mirroring the event-driven shape PositionsCache uses for its own resync - a timer
/// alone would mean waiting up to the full renewal window after a token dies for no reason, and
/// polling on a fast timer would mint tokens nobody asked for:
///
///  1. STARTUP - so the app is authenticated within seconds of boot, the same promise Static mode
///     makes (just achieved by a live call instead of a config value). Retried with backoff
///     (2s doubling to 30s) until it succeeds - Dhan's auth host being briefly unreachable at
///     boot must not leave this loop giving up and sitting logged out forever.
///
///  2. PROACTIVELY, an hour before the current token's own expiry. Dhan's tokens run 24 hours;
///     renewing with an hour of margin means an ordinary trading request essentially never
///     actually meets an expired token in normal operation - DhanAuthFailureHandler's reactive
///     path (case 3 below) exists for the abnormal case, not the routine one.
///
///  3. REACTIVELY, the instant DhanSession.SessionInvalidated fires - Dhan revoking the token
///     early, a login from elsewhere, or any REST/feed call surfacing a dead session before the
///     proactive renewal was due. This is what actually makes TOTP mode self-healing: a dead
///     token is a momentary blip (one HTTP round trip) rather than a login prompt nobody is
///     there to answer.
///
/// A failed renewal attempt (case 1 or 3) is retried with the same backoff until it succeeds -
/// unlike a login BUTTON, there is no "wait for the operator to try again" fallback here, so
/// giving up is not an option while the process is still running.
/// </summary>
public sealed class DhanTotpAutoRenewalHostedService : BackgroundService
{
    private static readonly TimeSpan RetryBackoffInitial = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryBackoffMax = TimeSpan.FromSeconds(30);

    /// <summary>How long before a token's own expiry to proactively replace it. Large enough that
    /// a slow network hiccup or a few failed retries at renewal time still finish well before the
    /// old token actually dies.</summary>
    private static readonly TimeSpan RenewalMargin = TimeSpan.FromHours(1);

    private readonly DhanOptions _options;
    private readonly DhanSession _session;
    private readonly DhanAuthService _authService;
    private readonly ILogger<DhanTotpAutoRenewalHostedService> _logger;

    // Same flag+semaphore coalescing shape as PositionsCache.OnOrderUpdate/_resyncSignal: the
    // first invalidation while idle wakes the loop; any that arrive while a renewal is already in
    // flight or already armed are absorbed into that same round rather than queuing up separately.
    private readonly SemaphoreSlim _invalidatedSignal = new(0, 1);
    private int _invalidatedPending;

    public DhanTotpAutoRenewalHostedService(
        IOptions<DhanOptions> options,
        DhanSession session,
        DhanAuthService authService,
        ILogger<DhanTotpAutoRenewalHostedService> logger)
    {
        _options = options.Value;
        _session = session;
        _authService = authService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.HasTotpCredentials)
        {
            _logger.LogError(
                "Dhan:AuthMode is 'Totp' but Dhan:ClientId / Dhan:Pin / Dhan:TotpSecret are not all configured - " +
                "TOTP auto-login cannot run, and nothing will authenticate this session. " +
                "Set all three, or switch AuthMode back to 'Static' and supply Dhan:AccessToken instead.");
            return;
        }

        _session.SessionInvalidated += OnInvalidated;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var expiresUtc = await RenewUntilSuccessAsync(stoppingToken);
                if (expiresUtc is null) return; // cancelled while retrying - shutting down

                await WaitForInvalidationOrDeadlineAsync(expiresUtc.Value - RenewalMargin, stoppingToken);
            }
        }
        finally
        {
            _session.SessionInvalidated -= OnInvalidated;
        }
    }

    /// <summary>Mints a token, retrying with backoff until it succeeds or the service is
    /// stopping. Returns the new token's expiry, or null if cancelled before a renewal
    /// succeeded.</summary>
    private async Task<DateTimeOffset?> RenewUntilSuccessAsync(CancellationToken stoppingToken)
    {
        var backoff = RetryBackoffInitial;
        while (!stoppingToken.IsCancellationRequested)
        {
            var result = await _authService.GenerateAccessTokenViaTotpAsync(stoppingToken);
            if (result.Success)
            {
                _logger.LogInformation(
                    "Dhan TOTP auto-login succeeded for client {ClientId}; token valid until {ExpiresUtc:u}.",
                    result.ClientId, result.ExpiresUtc);
                // Fall back to a conservative estimate if Dhan's response ever omits expiryTime,
                // so a renewal still gets scheduled rather than this loop stalling indefinitely.
                return result.ExpiresUtc ?? DateTimeOffset.UtcNow.AddHours(23);
            }

            _logger.LogWarning(
                "Dhan TOTP auto-login failed: {Error} - retrying in {Seconds}s.",
                result.Error, backoff.TotalSeconds);

            if (!await DelayAsync(backoff, stoppingToken)) return null;
            backoff = Min(backoff * 2, RetryBackoffMax);
        }

        return null;
    }

    /// <summary>Blocks until either an invalidation is signalled or <paramref name="deadlineUtc"/>
    /// arrives, whichever is first - the "renew now" race between cases 2 and 3 in the class doc
    /// comment. Returns once either condition is met; the caller always renews next regardless of
    /// which one fired, so there is nothing to report back.</summary>
    private async Task WaitForInvalidationOrDeadlineAsync(DateTimeOffset deadlineUtc, CancellationToken stoppingToken)
    {
        var delay = deadlineUtc - DateTimeOffset.UtcNow;
        if (delay <= TimeSpan.Zero)
        {
            // The proactive deadline is already in the past - a token issued with less than
            // RenewalMargin of validity, or enough clock skew to have the same effect. Still
            // renew promptly (a real expiry may be close), but through RetryBackoffInitial rather
            // than a zero-length wait, so a degenerate expiry can never turn into a busy loop
            // hammering Dhan's auth endpoint on every iteration.
            Interlocked.Exchange(ref _invalidatedPending, 0);
            await DelayAsync(RetryBackoffInitial, stoppingToken);
            return;
        }

        using var deadlineCts = new CancellationTokenSource(delay);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, deadlineCts.Token);
        try
        {
            await _invalidatedSignal.WaitAsync(linked.Token);
            Interlocked.Exchange(ref _invalidatedPending, 0);
        }
        catch (OperationCanceledException)
        {
            // Either the proactive deadline arrived or the service is stopping - the outer loop
            // checks stoppingToken itself before renewing again, so no extra handling is needed
            // here either way.
        }
    }

    /// <summary>Called from wherever DhanSession.SessionInvalidated is raised - must stay
    /// non-blocking. See PositionsCache.OnOrderUpdate for the identical coalescing shape.</summary>
    private void OnInvalidated()
    {
        if (Interlocked.CompareExchange(ref _invalidatedPending, 1, 0) == 0)
        {
            try { _invalidatedSignal.Release(); }
            catch (SemaphoreFullException) { /* already armed - coalesced */ }
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
