using SpreadTrader.Core.Models;

namespace SpreadTrader.Core.Abstractions;

/// <summary>Publishes what actually happened when a basket was dispatched (or blocked) for a
/// fired trigger - see BasketExecutionEvent and OrderDispatchHostedService.</summary>
public interface IOrderExecutionSink
{
    Task PublishAsync(BasketExecutionEvent executionEvent, CancellationToken cancellationToken);
}
