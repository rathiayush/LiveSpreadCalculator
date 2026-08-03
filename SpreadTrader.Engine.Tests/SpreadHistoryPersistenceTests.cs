using LiveSpreadCalculator.Persistence;
using SpreadTrader.Core.Models;

namespace SpreadTrader.Engine.Tests;

/// <summary>
/// Pins the one invariant that silently destroyed every hourly spread-history capture in production:
/// anything handed to Npgsql for a 'timestamp with time zone' column MUST carry offset 0.
///
/// The capture service stamps CapturedAt in IST (+05:30) deliberately - that is not a bug, it is how
/// it schedules and logs against the exchange clock - so the conversion has to happen at the Postgres
/// boundary. When it didn't, Npgsql threw
///     ArgumentException: Cannot write DateTimeOffset with Offset=05:30:00 ... only offset 0 (UTC)
/// on EVERY bulk insert, the capture service caught and logged it, and the app carried on looking
/// healthy while persisting nothing at all for weeks.
///
/// These are deliberately pure (no database) so they run in CI, where no Postgres exists. They were
/// written alongside a live end-to-end run against a real PostgreSQL 16 server, and verified by
/// mutation: reverting ToUtc to an identity function reproduces the exact production exception.
/// </summary>
public class SpreadHistoryPersistenceTests
{
    private static readonly TimeSpan Ist = new(5, 30, 0);

    /// <summary>The exact instant from the production failure log: "at IST 08/03/2026 14:28:59 +05:30".</summary>
    private static readonly DateTimeOffset CapturedAtIst = new(2026, 8, 3, 14, 28, 59, Ist);

    private static SpreadHistoryPoint Point(DateTimeOffset capturedAt) => new(
        SpreadId: "NIFTY-AUG-SEP",
        Underlying: "NIFTY",
        NearSymbol: "NIFTY26AUGFUT",
        FarSymbol: "NIFTY26SEPFUT",
        NearExpiry: new DateOnly(2026, 8, 27),
        FarExpiry: new DateOnly(2026, 9, 24),
        CapturedAt: capturedAt,
        NearPrice: 24500.25m,
        FarPrice: 24610.75m,
        Direction: "Contango",
        BuySpreadValue: 110.50m,
        SellSpreadValue: 109.80m,
        BuySpreadPercent: 0.451m,
        SellSpreadPercent: 0.448m);

    [Fact]
    public void RowWrittenToPostgresAlwaysCarriesUtcOffset()
    {
        // THE regression: an IST-stamped capture must reach Npgsql already normalised, or the whole
        // batch throws and the hour's data is lost.
        var row = EfSpreadHistoryStore.ToRow(Point(CapturedAtIst));

        Assert.Equal(TimeSpan.Zero, row.CapturedAt.Offset);
    }

    [Fact]
    public void NormalisingDoesNotShiftTheInstant()
    {
        // 14:28:59+05:30 and 08:58:59Z are the same moment - the row must mean exactly what the
        // capture service meant, just expressed in UTC.
        var row = EfSpreadHistoryStore.ToRow(Point(CapturedAtIst));

        Assert.Equal(CapturedAtIst.ToUnixTimeMilliseconds(), row.CapturedAt.ToUnixTimeMilliseconds());
        Assert.Equal(new DateTimeOffset(2026, 8, 3, 8, 58, 59, TimeSpan.Zero), row.CapturedAt);
    }

    [Fact]
    public void AValueAlreadyInUtcPassesThroughUnchanged()
    {
        var utc = new DateTimeOffset(2026, 8, 3, 8, 58, 59, TimeSpan.Zero);

        var row = EfSpreadHistoryStore.ToRow(Point(utc));

        Assert.Equal(utc, row.CapturedAt);
    }

    [Theory]
    [InlineData(5, 30)]    // IST - what the capture service actually produces
    [InlineData(-8, 0)]    // a negative offset, in case the host clock is ever elsewhere
    [InlineData(0, 0)]     // already UTC
    [InlineData(13, 45)]   // an extreme real-world zone (Chatham Islands)
    public void EveryOffsetIsNormalisedForPostgres(int hours, int minutes)
    {
        var offset = new TimeSpan(hours, minutes, 0);
        var captured = new DateTimeOffset(2026, 8, 3, 14, 28, 59, offset);

        var normalised = EfSpreadHistoryStore.ToUtc(captured);

        Assert.Equal(TimeSpan.Zero, normalised.Offset);
        Assert.Equal(captured.ToUnixTimeMilliseconds(), normalised.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void EveryOtherFieldSurvivesTheMappingUntouched()
    {
        // Guards against the normalisation being "fixed" by rebuilding the row and quietly dropping
        // or reordering a price/percent column on the way through.
        var point = Point(CapturedAtIst);

        var row = EfSpreadHistoryStore.ToRow(point);

        Assert.Equal(point.SpreadId, row.SpreadId);
        Assert.Equal(point.Underlying, row.Underlying);
        Assert.Equal(point.NearSymbol, row.NearSymbol);
        Assert.Equal(point.FarSymbol, row.FarSymbol);
        Assert.Equal(point.NearExpiry, row.NearExpiry);
        Assert.Equal(point.FarExpiry, row.FarExpiry);
        Assert.Equal(point.NearPrice, row.NearPrice);
        Assert.Equal(point.FarPrice, row.FarPrice);
        Assert.Equal(point.Direction, row.Direction);
        Assert.Equal(point.BuySpreadValue, row.BuySpreadValue);
        Assert.Equal(point.SellSpreadValue, row.SellSpreadValue);
        Assert.Equal(point.BuySpreadPercent, row.BuySpreadPercent);
        Assert.Equal(point.SellSpreadPercent, row.SellSpreadPercent);
    }
}
