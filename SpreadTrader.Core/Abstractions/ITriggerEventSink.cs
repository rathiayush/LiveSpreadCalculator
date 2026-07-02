using SpreadTrader.Core.Models;

namespace SpreadTrader.Core.Abstractions;

public interface ITriggerEventSink
{
    Task PublishAsync(TriggerEvent triggerEvent, CancellationToken cancellationToken);
}
