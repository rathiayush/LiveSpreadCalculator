using Microsoft.Extensions.Hosting;

namespace LiveSpreadCalculator.Notifications;

/// <summary>Shared "which deployed instance sent this" prefix for every Telegram message. Production,
/// pradeep-1, and pradeep-2 all run the identical codebase and (as of now) can point at the same
/// Telegram bot/chat, so without this tag a message has no way to tell you which server it actually
/// came from - see TelegramNotifier and TelegramOrderExecutionSink, both of which prepend this to
/// every message right before it's sent.</summary>
internal static class TelegramEnvTag
{
    /// <summary>Builds a one-line HTML tag ending in its own newline, ready to prepend directly to a
    /// message body, e.g. "🖥 <b>PRODUCTION</b>\n". Reads IHostEnvironment.EnvironmentName, which is
    /// ASPNETCORE_ENVIRONMENT - set per-instance by deploy.ps1 ("Production"), deploy-pradeep-1.ps1
    /// ("pradeep-1"), and deploy-pradeep-2.ps1 ("pradeep-2").</summary>
    public static string Build(IHostEnvironment environment) =>
        $"🖥 <b>{Esc(environment.EnvironmentName.ToUpperInvariant())}</b>\n";

    private static string Esc(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");
}
