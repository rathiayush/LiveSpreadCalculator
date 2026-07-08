using System.Threading.Channels;
using SpreadTrader.Core.Models;

namespace SpreadTrader.Engine;

/// <summary>
/// Hands fired triggers from spread calculation off to order dispatch without letting a slow
/// or failing order call block the tick/spread processing loop.
///
/// Bounded (rather than unbounded) at <see cref="Capacity"/>: if OrderDispatchHostedService's
/// consumer ever stalls (a hanging Kite call, an exception loop, etc.) faster than triggers
/// fire, an unbounded channel would grow without limit for as long as that stall lasts. Bounded
/// + FullMode.Wait means the writer (SpreadCalculationHostedService, which already awaits
/// WriteAsync) simply applies backpressure once the queue is full, instead of the queue itself
/// becoming an unbounded memory sink. 1000 is comfortably above any realistic fire burst (fires
/// are already rate-limited per rule via EngineOptions.MinFireIntervalPerRuleMs) while still
/// bounding worst-case memory if dispatch ever truly wedges.
/// </summary>
public sealed class TriggerEventChannel
{
    private const int Capacity = 1000;

    private readonly Channel<TriggerEvent> _channel = Channel.CreateBounded<TriggerEvent>(
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.Wait });

    public ChannelWriter<TriggerEvent> Writer => _channel.Writer;
    public ChannelReader<TriggerEvent> Reader => _channel.Reader;
}
