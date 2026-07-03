namespace LiveSpreadCalculator.Notifications;

/// <summary>Binds the "Telegram" section of appsettings.json - see Program.cs. When Enabled is true
/// and a BotToken + ChatId are present, every basket execution (fill/partial/reject/blocked) is
/// pushed to the configured Telegram chat as an HTML message.
///
/// BotToken is a secret - anyone who has it can post as the bot and read its updates. Override it
/// with an environment variable (Telegram__BotToken) or user-secrets in any real deployment rather
/// than leaving it in the committed file.</summary>
public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    /// <summary>Master switch. When false, the Telegram sink is not wired in at all (see Program.cs)
    /// so nothing is sent and no HTTP calls are made.</summary>
    public bool Enabled { get; set; }

    /// <summary>The bot token, e.g. "8705011731:AAE...". Just the token - the "bot" prefix and the
    /// /sendMessage path are added when the URL is built.</summary>
    public string BotToken { get; set; } = "";

    /// <summary>Target chat id, e.g. a channel/group id like "-1003533948678".</summary>
    public string ChatId { get; set; } = "";

    /// <summary>Base URL of the Telegram Bot API. Overridable mainly for testing; defaults to the
    /// real endpoint.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.telegram.org";
}
