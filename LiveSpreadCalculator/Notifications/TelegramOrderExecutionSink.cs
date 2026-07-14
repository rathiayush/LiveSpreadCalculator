using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;

namespace LiveSpreadCalculator.Notifications;

/// <summary>Sends a Telegram message for every basket execution so the desk sees, in the chat, what
/// actually happened the moment a trigger fires an order: whether both legs filled, only one did
/// (partial), everything was rejected, or the basket was blocked on margin before anything was
/// placed. Formats the same BasketExecutionEvent the dashboard's Trigger Activity feed uses, so the
/// two never disagree.
///
/// Best-effort by design: any failure talking to Telegram (network, bad token, chat not found) is
/// logged and swallowed - a notification problem must never affect order dispatch or the SignalR
/// feed (the CompositeOrderExecutionSink also isolates each sink from the others).</summary>
public sealed class TelegramOrderExecutionSink : IOrderExecutionSink
{
    // IST - this is an Indian F&O desk, so timestamps read best in exchange-local time rather than UTC.
    private static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TelegramOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<TelegramOrderExecutionSink> _logger;

    public TelegramOrderExecutionSink(
        IHttpClientFactory httpClientFactory,
        IOptions<TelegramOptions> options,
        IHostEnvironment environment,
        ILogger<TelegramOrderExecutionSink> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>Fire-and-forget: the message is formatted synchronously (cheap) and the HTTP POST is
    /// launched on a background task that this method does NOT await, so order dispatch returns
    /// immediately and never waits on - or is slowed by - the Telegram API. The send uses
    /// CancellationToken.None (not the dispatch token) so a request completing doesn't cancel an
    /// in-flight notification, and is bounded instead by the HttpClient timeout configured in
    /// Program.cs. Any failure is logged, never thrown.</summary>
    public Task PublishAsync(BasketExecutionEvent executionEvent, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.BotToken) || string.IsNullOrWhiteSpace(_options.ChatId))
        {
            _logger.LogWarning("Telegram notification skipped - BotToken/ChatId not configured.");
            return Task.CompletedTask;
        }

        var text = BuildMessage(executionEvent);
        _ = SendAsync(text, executionEvent.RuleId);
        return Task.CompletedTask;
    }

    private async Task SendAsync(string text, string ruleId)
    {
        // Prepend which deployed instance (Production / pradeep-1 / pradeep-2 / Development) this
        // came from - see TelegramEnvTag - so an order fill on pradeep-1 can't be mistaken for one
        // on Production when they share the same Telegram chat.
        var taggedText = $"{TelegramEnvTag.Build(_environment)}{text}";

        var url = $"{_options.ApiBaseUrl.TrimEnd('/')}/bot{_options.BotToken}/sendMessage";
        try
        {
            var client = _httpClientFactory.CreateClient(nameof(TelegramOrderExecutionSink));
            using var response = await client.PostAsJsonAsync(
                url,
                new { chat_id = _options.ChatId, parse_mode = "HTML", text = taggedText },
                CancellationToken.None);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(CancellationToken.None);
                _logger.LogError("Telegram sendMessage failed ({Status}): {Body}", (int)response.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telegram sendMessage threw for rule {RuleId}", ruleId);
        }
    }

    /// <summary>Turns the execution outcome into an HTML Telegram message: a one-line overall
    /// verdict (all filled / partial / rejected / blocked) followed by a per-leg breakdown.</summary>
    private static string BuildMessage(BasketExecutionEvent e)
    {
        var sb = new StringBuilder();

        // Margin-blocked: nothing was placed at all, so there are no legs to report.
        if (e.MarginBlocked)
        {
            sb.Append("<b>⛔ ORDER BLOCKED</b>\n");
            sb.Append($"<b>{Esc(e.Underlying)}</b> | {Esc(e.OrderKind)}\n");
            sb.Append($"<b>Rule:</b> {Esc(e.RuleId)}\n");
            sb.Append($"<b>Spread:</b> {Esc(e.SpreadId)}\n");
            sb.Append($"<b>Reason:</b> {Esc(e.BlockedReason ?? "Unknown")}\n");
            sb.Append($"<b>Time:</b> {FormatTime(e.ExecutedAt)}");
            return sb.ToString();
        }

        var primary = e.Legs.Where(l => !l.IsSquareOff).ToList();
        var squareOffs = e.Legs.Where(l => l.IsSquareOff).ToList();

        var filledCount = primary.Count(l => IsFullyFilled(l));
        var anyFill = primary.Any(l => l.FilledQuantity > 0);
        var (headEmoji, headText) =
            primary.Count == 0 ? ("ℹ️", "NO LEGS PLACED")
            : filledCount == primary.Count ? ("✅", "ALL LEGS FILLED")
            : !anyFill ? ("❌", "NOT FILLED")
            : ("⚠️", "PARTIALLY FILLED");

        sb.Append($"<b>{headEmoji} {headText}</b>\n");
        sb.Append($"<b>{Esc(e.Underlying)}</b> | {Esc(e.OrderKind)}\n");
        sb.Append($"<b>Rule:</b> {Esc(e.RuleId)}  |  <b>Spread:</b> {Esc(e.SpreadId)}\n");
        sb.Append($"<b>Filled:</b> {filledCount}/{primary.Count} leg(s)\n");

        sb.Append("\n<b>Legs</b>\n");
        foreach (var leg in primary)
            sb.Append(FormatLeg(leg)).Append('\n');

        if (squareOffs.Count > 0)
        {
            sb.Append("\n<b>♻️ Auto square-off (partial fill cleanup)</b>\n");
            foreach (var leg in squareOffs)
                sb.Append(FormatLeg(leg)).Append('\n');
        }

        sb.Append($"\n<b>Time:</b> {FormatTime(e.ExecutedAt)}");
        return sb.ToString();
    }

    private static bool IsFullyFilled(OrderLegResult l) => l.Success && l.Quantity > 0 && l.FilledQuantity >= l.Quantity;

    private static string FormatLeg(OrderLegResult l)
    {
        var sideEmoji = string.Equals(l.TransactionType, "BUY", StringComparison.OrdinalIgnoreCase) ? "\U0001F7E2" : "\U0001F534";

        // The price the order was actually placed at: the protection-band LIMIT price for a
        // MARKET-converted leg, or "MKT" when it went as a raw market order with no price reference.
        var priceText = l.Price is { } p ? $"@ {p:0.##}" : "@ MKT";

        string status;
        if (!l.Success)
            status = $"❌ Rejected{(string.IsNullOrWhiteSpace(l.ErrorMessage) ? "" : $" - {Esc(l.ErrorMessage!)}")}";
        else if (l.Quantity > 0 && l.FilledQuantity >= l.Quantity)
            status = $"✅ Filled {l.FilledQuantity}/{l.Quantity}";
        else if (l.FilledQuantity > 0)
            status = $"⚠️ Partial {l.FilledQuantity}/{l.Quantity}";
        else
            status = $"⭕ Not filled 0/{l.Quantity}";

        // e.g. "🟢 NIFTY25JULFUT BUY ×75 @ 1190.35 (LIMIT) - ✅ Filled 75/75  [COMPLETE]  #2507...".
        var orderType = string.IsNullOrWhiteSpace(l.OrderType) ? "" : $" ({Esc(l.OrderType!)})";
        var line = $"{sideEmoji} <b>{Esc(l.TradingSymbol)}</b> {Esc(l.TransactionType)} ×{l.Quantity} <b>{priceText}</b>{orderType} - {status}";
        if (!string.IsNullOrWhiteSpace(l.Status))
            line += $"  [{Esc(l.Status!)}]";
        if (!string.IsNullOrWhiteSpace(l.OrderId))
            line += $"  <code>#{Esc(l.OrderId!)}</code>";
        return line;
    }

    private static string FormatTime(DateTimeOffset ts) => $"{ts.ToOffset(IstOffset):dd-MMM HH:mm:ss} IST";

    /// <summary>Escapes the five characters Telegram's HTML parse mode cares about so a symbol or
    /// error message containing &lt; &gt; &amp; " ' can't break the message or be interpreted as markup.</summary>
    private static string Esc(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&#39;");
}
