using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;

namespace LiveSpreadCalculator.Notifications;

/// <summary>Fans a single BasketExecutionEvent out to several sinks (the SignalR dashboard push and
/// the Telegram notifier) so OrderDispatchHostedService still depends on just one IOrderExecutionSink
/// and knows nothing about how many places the outcome goes.
///
/// Each inner sink is awaited inside its own try/catch: a failure in one (e.g. Telegram is
/// unreachable) must not stop the others from firing, and must never propagate back into order
/// dispatch. The dashboard push in particular should always happen even if the outbound
/// notification fails.</summary>
public sealed class CompositeOrderExecutionSink : IOrderExecutionSink
{
    private readonly IReadOnlyList<IOrderExecutionSink> _sinks;
    private readonly ILogger<CompositeOrderExecutionSink> _logger;

    public CompositeOrderExecutionSink(IEnumerable<IOrderExecutionSink> sinks, ILogger<CompositeOrderExecutionSink> logger)
    {
        _sinks = sinks.ToList();
        _logger = logger;
    }

    public async Task PublishAsync(BasketExecutionEvent executionEvent, CancellationToken cancellationToken)
    {
        foreach (var sink in _sinks)
        {
            try
            {
                await sink.PublishAsync(executionEvent, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Order-execution sink {Sink} failed", sink.GetType().Name);
            }
        }
    }
}
