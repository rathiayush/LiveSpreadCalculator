namespace SpreadTrader.KiteIntegration;

/// <summary>
/// Simple fixed-spacing pacer (not a bursty token bucket) used to keep order placement calls
/// under Kite's rate limits. Good enough for a basket dispatcher that fires a handful of legs
/// per trigger; not intended for high-frequency use.
/// </summary>
public sealed class RateLimiter
{
    private readonly TimeSpan _minInterval;
    private DateTimeOffset _nextSlot = DateTimeOffset.MinValue;
    private readonly object _lock = new();

    public RateLimiter(int maxOperationsPerSecond)
    {
        _minInterval = TimeSpan.FromSeconds(1.0 / Math.Max(1, maxOperationsPerSecond));
    }

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        TimeSpan delay;
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            var slot = _nextSlot > now ? _nextSlot : now;
            delay = slot - now;
            _nextSlot = slot + _minInterval;
        }

        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken);
    }
}
