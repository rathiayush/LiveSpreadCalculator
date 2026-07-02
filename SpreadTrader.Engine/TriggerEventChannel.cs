using System.Threading.Channels;
using SpreadTrader.Core.Models;

namespace SpreadTrader.Engine;

/// <summary>
/// Hands fired triggers from spread calculation off to order dispatch without letting a slow
/// or failing order call block the tick/spread processing loop.
/// </summary>
public sealed class TriggerEventChannel
{
    private readonly Channel<TriggerEvent> _channel = Channel.CreateUnbounded<TriggerEvent>();

    public ChannelWriter<TriggerEvent> Writer => _channel.Writer;
    public ChannelReader<TriggerEvent> Reader => _channel.Reader;
}
