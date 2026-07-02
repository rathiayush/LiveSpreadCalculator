namespace SpreadTrader.Core.Models;

public sealed class OrderLegRequest
{
    public required string TradingSymbol { get; set; }
    public required string Exchange { get; set; }
    public required string TransactionType { get; set; } // BUY or SELL
    public required int Quantity { get; set; }
    public string Product { get; set; } = "NRML";
    public string OrderType { get; set; } = "MARKET";
    public decimal? Price { get; set; } // required for LIMIT orders
    public string Variety { get; set; } = "regular";
    public string? Tag { get; set; }
}

public sealed class OrderResult
{
    public bool Success { get; set; }
    public string? OrderId { get; set; }
    public string? ErrorMessage { get; set; }
}
