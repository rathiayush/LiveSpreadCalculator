namespace SpreadTrader.Core.Abstractions;

/// <summary>Plain "send this text somewhere" abstraction for PnlAlertHostedService (in
/// SpreadTrader.Engine) - mirrors why IOrderExecutionSink exists instead of OrderDispatchHostedService
/// depending on TelegramOrderExecutionSink concretely: SpreadTrader.Engine doesn't (and shouldn't)
/// reference the LiveSpreadCalculator web project, so the actual Telegram implementation (see
/// LiveSpreadCalculator.Notifications.TelegramNotifier) is wired in only at the DI-registration
/// site in Program.cs.</summary>
public interface IPnlAlertNotifier
{
    Task SendAsync(string text, CancellationToken cancellationToken);
}
