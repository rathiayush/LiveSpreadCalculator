using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Abstractions;

namespace SpreadTrader.KotakIntegration.Auth;

/// <summary>
/// Keeps KotakSession authenticated with zero human involvement, exactly the way
/// DhanTotpAutoRenewalHostedService does for Dhan's TOTP mode - see that class's doc comment for
/// the full reasoning behind the three triggers (startup, proactive-before-expiry, reactive-on-
/// invalidation). The only structural difference is what gets called: Kotak's login is the two-step
/// TOTP+MPIN handshake in KotakAuthService.GenerateAccessTokenViaTotpLoginAsync, versus Dhan's
/// single TOTP call.
///
/// Always registered (unlike Dhan's equivalent, which only runs when AuthMode is "Totp") - Kotak
/// has no static-token mode at all, so this is the only way a Kotak session is ever obtained.
/// </summary>
public sealed class KotakSessionAutoRenewalHostedService : BackgroundService
{
    private static readonly TimeSpan RetryBackoffInitial = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryBackoffMax = TimeSpan.FromSeconds(30);

    /// <summary>See KotakAuthService.AssumedSessionValidity's doc comment - Kotak's real session
    /// lifetime is unconfirmed, so this margin is deliberately generous relative to the assumed
    /// 12h validity.</summary>
    private static readonly TimeSpan RenewalMargin = TimeSpan.FromHours(2);

    private readonly KotakOptions _options;
    private readonly KotakSession _session;
    private readonly KotakAuthService _authService;
    private readonly IBrokerAuthNotifier _notifier;
    private readonly ILogger<KotakSessionAutoRenewalHostedService> _logger;

    private readonly SemaphoreSlim _invalidatedSignal = new(0, 1);
    private int _invalidatedPending;

    public KotakSessionAutoRenewalHostedService(
        IOptions<KotakOptions> options,
        KotakSession session,
        KotakAuthService authService,
        IBrokerAuthNotifier notifier,
        ILogger<KotakSessionAutoRenewalHostedService> logger)
    {
        _options = options.Value;
        _session = session;
        _authService = authService;
        _notifier = notifier;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.HasLoginCredentials)
        {
            _logger.LogError(
                "Kotak:ConsumerKey / Kotak:MobileNumber / Kotak:Ucc / Kotak:Mpin / Kotak:TotpSecret are not all configured - " +
                "TOTP auto-login cannot run, and nothing will authenticate this session. Set all five.");
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

    private async Task<DateTimeOffset?> RenewUntilSuccessAsync(CancellationToken stoppingToken)
    {
        var wasAuthenticated = _session.IsAuthenticated;

        var backoff = RetryBackoffInitial;
        while (!stoppingToken.IsCancellationRequested)
        {
            var result = await _authService.GenerateAccessTokenViaTotpLoginAsync(stoppingToken);
            if (result.Success)
            {
                _logger.LogInformation(
                    "Kotak TOTP auto-login succeeded for UCC {ClientId}; session valid until {ExpiresUtc:u}.",
                    result.ClientId, result.ExpiresUtc);

                if (!wasAuthenticated)
                {
                    try
                    {
                        await _notifier.NotifyAuthRestoredAsync(stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to push the Kotak auth-restored notification - clients will still pick this up next time they poll GET /api/auth/status.");
                    }
                }

                return result.ExpiresUtc ?? DateTimeOffset.UtcNow.AddHours(10);
            }

            _logger.LogWarning("Kotak TOTP login failed: {Error} - retrying in {Seconds}s.", result.Error, backoff.TotalSeconds);

            if (!await DelayAsync(backoff, stoppingToken)) return null;
            backoff = Min(backoff * 2, RetryBackoffMax);
        }

        return null;
    }

    private async Task WaitForInvalidationOrDeadlineAsync(DateTimeOffset deadlineUtc, CancellationToken stoppingToken)
    {
        var delay = deadlineUtc - DateTimeOffset.UtcNow;
        if (delay <= TimeSpan.Zero)
        {
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
            // Either the proactive deadline arrived or the service is stopping.
        }
    }

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
