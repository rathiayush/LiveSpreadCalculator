namespace LiveSpreadCalculator.Persistence;

/// <summary>
/// EF Core entity backing the dbo.SpreadHistory table (see SpreadHistoryDbContext /
/// EfSpreadHistoryStore). Kept separate from the domain's SpreadHistoryPoint (a readonly record
/// struct in Core, which EF can't use as a tracked entity) - EfSpreadHistoryStore maps between the
/// two. Id is a DB-generated identity key.
/// </summary>
public sealed class SpreadHistoryRow
{
    public long Id { get; set; }
    public string SpreadId { get; set; } = string.Empty;
    public string Underlying { get; set; } = string.Empty;
    public string NearSymbol { get; set; } = string.Empty;
    public string FarSymbol { get; set; } = string.Empty;
    public DateOnly? NearExpiry { get; set; }
    public DateOnly? FarExpiry { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
    public decimal NearPrice { get; set; }
    public decimal FarPrice { get; set; }
    public string Direction { get; set; } = string.Empty;
    public decimal BuySpreadValue { get; set; }
    public decimal SellSpreadValue { get; set; }
    public decimal BuySpreadPercent { get; set; }
    public decimal SellSpreadPercent { get; set; }
}
