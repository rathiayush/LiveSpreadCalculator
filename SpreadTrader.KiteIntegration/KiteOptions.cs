namespace SpreadTrader.KiteIntegration;

public sealed class KiteOptions
{
    public const string SectionName = "Kite";

    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";

    /// <summary>
    /// Optional - only useful for local dev via `dotnet user-secrets` so you don't have to run
    /// the login flow on every restart. Kite invalidates this daily around 6 AM regardless of
    /// how it was obtained. Never put this (or ApiSecret) in appsettings.json / source control.
    /// </summary>
    public string? AccessToken { get; set; }

    public string WsUrl { get; set; } = "wss://ws.kite.trade";
    public string RestBaseUrl { get; set; } = "https://api.kite.trade";

    /// <summary>ltp, quote, or full. Spread triggers are evaluated against best bid/ask, which
    /// only "full" mode provides (market depth) - "quote" mode has no bid/ask, only LTP+OHLC.
    /// Full mode costs more bandwidth/CPU (depth changes push extra ticks), which is fine at
    /// ~100-200 instruments but worth knowing if you scale up a lot further.</summary>
    public string TickerMode { get; set; } = "full";
}
