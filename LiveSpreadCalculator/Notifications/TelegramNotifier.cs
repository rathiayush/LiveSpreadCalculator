using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Abstractions;

namespace LiveSpreadCalculator.Notifications;

/// <summary>
/// Plain "send this text to the configured Telegram chat" primitive - reads the same TelegramOptions
/// section as TelegramOrderExecutionSink, but isn't coupled to BasketExecutionEvent's shape, so any
/// future notification that isn't an order-fill (starting with PnlAlertHostedService) can use it
/// directly instead of duplicating the HTTP POST. Deliberately NOT merged into
/// TelegramOrderExecutionSink itself - that class's public contract is IOrderExecutionSink.PublishAsync
/// (BasketExecutionEvent), and reshaping it to also expose a generic text-send risked touching the
/// order-notification path this app already depends on working correctly.
///
/// Best-effort by design, same as TelegramOrderExecutionSink: any failure talking to Telegram is
/// logged and swallowed, never thrown - a notification problem must never take down whatever
/// triggered it.
/// </summary>
public sealed class TelegramNotifier : IPnlAlertNotifier
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramNotifier> _logger;

    public TelegramNotifier(IHttpClientFactory httpClientFactory, IOptions<TelegramOptions> options, ILogger<TelegramNotifier> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendAsync(string text, CancellationToken cancellationToken)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.BotToken) || string.IsNullOrWhiteSpace(_options.ChatId))
        {
            _logger.LogDebug("TelegramNotifier: skipped - Telegram disabled or BotToken/ChatId not configured.");
            return;
        }

        var url = $"{_options.ApiBaseUrl.TrimEnd('/')}/bot{_options.BotToken}/sendMessage";
        try
        {
            var client = _httpClientFactory.CreateClient(nameof(TelegramNotifier));
            using var response = await client.PostAsJsonAsync(
                url,
                new { chat_id = _options.ChatId, parse_mode = "HTML", text },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("TelegramNotifier: sendMessage failed ({Status}): {Body}", (int)response.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TelegramNotifier: sendMessage threw");
        }
    }
}
