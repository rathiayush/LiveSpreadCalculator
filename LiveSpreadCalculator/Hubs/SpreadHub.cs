using Microsoft.AspNetCore.SignalR;

namespace LiveSpreadCalculator.Hubs;

/// <summary>
/// Thin push-only hub - the server broadcasts "spreadUpdate", "triggerFired", "basketExecuted",
/// and "kiteAuthInvalidated" events to every connected client (see SignalRSpreadPublisher /
/// SignalRTriggerEventSink / SignalROrderExecutionSink / SignalRKiteAuthNotifier). No
/// client-invoked methods are needed yet; all configuration goes through the REST controllers.
/// </summary>
public sealed class SpreadHub : Hub
{
}
