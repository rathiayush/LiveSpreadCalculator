using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using EFCore.BulkExtensions;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;

namespace LiveSpreadCalculator.Persistence;

/// <summary>
/// EF Core (SQL Server / LocalDB) implementation of ISpreadHistoryStore.
///
/// - InitializeAsync: connects to master to CREATE DATABASE if the database is missing, then runs an
///   idempotent "IF the table doesn't exist, CREATE it" statement - so a fresh machine with only
///   LocalDB works with no manual setup, and an existing database that's simply missing the table
///   gets it created (EnsureCreated alone wouldn't cover that case).
/// - AddBatchAsync: uses EFCore.BulkExtensions BulkInsertAsync so the whole hourly capture is one
///   set-based bulk insert rather than a row-per-INSERT round trip.
/// - DeleteBySpreadIdAsync / PurgeExpiredAsync: use EF Core's native set-based ExecuteDeleteAsync
///   (a single DELETE, no load-then-remove).
///
/// A singleton DbContext isn't safe, so this depends on IDbContextFactory and creates a short-lived
/// context per operation (registered via AddDbContextFactory in Program.cs).
/// </summary>
public sealed class EfSpreadHistoryStore : ISpreadHistoryStore
{
    private readonly IDbContextFactory<SpreadHistoryDbContext> _contextFactory;
    private readonly string _connectionString;
    private readonly ILogger<EfSpreadHistoryStore> _logger;

    public EfSpreadHistoryStore(
        IDbContextFactory<SpreadHistoryDbContext> contextFactory,
        string connectionString,
        ILogger<EfSpreadHistoryStore> logger)
    {
        _contextFactory = contextFactory;
        _connectionString = connectionString;
        _logger = logger;
    }

    // Kept in sync with SpreadHistoryDbContext.OnModelCreating so the raw-created table and the
    // EF-modelled table are identical.
    private const string CreateTableSql = @"
IF OBJECT_ID('dbo.SpreadHistory', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.SpreadHistory (
        Id                BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SpreadHistory PRIMARY KEY,
        SpreadId          NVARCHAR(100)   NOT NULL,
        Underlying        NVARCHAR(50)    NOT NULL,
        NearSymbol        NVARCHAR(50)    NOT NULL,
        FarSymbol         NVARCHAR(50)    NOT NULL,
        NearExpiry        DATE            NULL,
        FarExpiry         DATE            NULL,
        CapturedAt        DATETIMEOFFSET(0) NOT NULL,
        NearPrice         DECIMAL(18,4)   NOT NULL,
        FarPrice          DECIMAL(18,4)   NOT NULL,
        Direction         NVARCHAR(20)    NOT NULL,
        BuySpreadValue    DECIMAL(18,4)   NOT NULL,
        SellSpreadValue   DECIMAL(18,4)   NOT NULL,
        BuySpreadPercent  DECIMAL(18,6)   NOT NULL,
        SellSpreadPercent DECIMAL(18,6)   NOT NULL
    );
    CREATE INDEX IX_SpreadHistory_SpreadId_CapturedAt ON dbo.SpreadHistory (SpreadId, CapturedAt);
    CREATE INDEX IX_SpreadHistory_NearExpiry ON dbo.SpreadHistory (NearExpiry);
END";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // 1) Ensure the database exists (connect to master; CREATE DATABASE can't be parameterized,
        //    so the name is bracket-quoted with any embedded ']' doubled to prevent injection).
        var builder = new SqlConnectionStringBuilder(_connectionString);
        var databaseName = string.IsNullOrWhiteSpace(builder.InitialCatalog)
            ? "SpreadTraderHistory"
            : builder.InitialCatalog;
        var masterBuilder = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = "master" };
        var safeDbName = "[" + databaseName.Replace("]", "]]") + "]";

        await using (var master = new SqlConnection(masterBuilder.ConnectionString))
        {
            await master.OpenAsync(cancellationToken);
            await using var createDb = master.CreateCommand();
            createDb.CommandText = $"IF DB_ID(@db) IS NULL EXEC('CREATE DATABASE {safeDbName}');";
            createDb.Parameters.AddWithValue("@db", databaseName);
            await createDb.ExecuteNonQueryAsync(cancellationToken);
        }

        // 2) Ensure the table (+ indexes) exist. Idempotent - no-op when already present.
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.ExecuteSqlRawAsync(CreateTableSql, cancellationToken);

        _logger.LogInformation("SpreadHistory store ready (database {Database}).", databaseName);
    }

    public async Task AddBatchAsync(IReadOnlyList<SpreadHistoryPoint> points, CancellationToken cancellationToken = default)
    {
        if (points.Count == 0) return;

        var rows = points.Select(ToRow).ToList();

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await context.BulkInsertAsync(rows, cancellationToken: cancellationToken);

        _logger.LogInformation("Persisted {Count} spread history point(s) (bulk insert).", rows.Count);
    }

    public async Task<IReadOnlyList<SpreadHistoryPoint>> GetHistoryAsync(
        string spreadId,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

            var query = context.SpreadHistory.AsNoTracking().Where(x => x.SpreadId == spreadId);
            if (fromUtc.HasValue) query = query.Where(x => x.CapturedAt >= fromUtc.Value);
            if (toUtc.HasValue) query = query.Where(x => x.CapturedAt <= toUtc.Value);

            var rows = await query.OrderBy(x => x.CapturedAt).ToListAsync(cancellationToken);
            return rows.Select(ToPoint).ToList();
        }
        catch (Exception ex)
        {
            // Never let a history read take down the request / surface a 500. If the table doesn't
            // exist yet (the writer instance hasn't provisioned it), the DB is unreachable, or any
            // other read error occurs, log it and return an empty series so the chart just shows
            // "no history captured yet" instead of erroring.
            _logger.LogWarning(ex, "Spread history read failed for {SpreadId} - returning empty series.", spreadId);
            return Array.Empty<SpreadHistoryPoint>();
        }
    }

    public async Task<int> DeleteBySpreadIdAsync(string spreadId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.SpreadHistory
            .Where(x => x.SpreadId == spreadId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<int> PurgeExpiredAsync(DateOnly asOf, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        // Only rows whose near leg has a known expiry that has already passed. Rows with a NULL
        // NearExpiry (leg never resolved an expiry) are left untouched rather than blindly deleted.
        var removed = await context.SpreadHistory
            .Where(x => x.NearExpiry != null && x.NearExpiry <= asOf)
            .ExecuteDeleteAsync(cancellationToken);

        if (removed > 0)
            _logger.LogInformation("Purged {Count} expired-leg spread history row(s) (as of {AsOf}).", removed, asOf);
        return removed;
    }

    public async Task<int> PurgeOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var removed = await context.SpreadHistory
            .Where(x => x.CapturedAt < cutoffUtc)
            .ExecuteDeleteAsync(cancellationToken);

        if (removed > 0)
            _logger.LogInformation("Purged {Count} spread history row(s) older than {Cutoff}.", removed, cutoffUtc);
        return removed;
    }

    private static SpreadHistoryRow ToRow(SpreadHistoryPoint p) => new()
    {
        SpreadId = p.SpreadId,
        Underlying = p.Underlying,
        NearSymbol = p.NearSymbol,
        FarSymbol = p.FarSymbol,
        NearExpiry = p.NearExpiry,
        FarExpiry = p.FarExpiry,
        CapturedAt = p.CapturedAt,
        NearPrice = p.NearPrice,
        FarPrice = p.FarPrice,
        Direction = p.Direction,
        BuySpreadValue = p.BuySpreadValue,
        SellSpreadValue = p.SellSpreadValue,
        BuySpreadPercent = p.BuySpreadPercent,
        SellSpreadPercent = p.SellSpreadPercent,
    };

    private static SpreadHistoryPoint ToPoint(SpreadHistoryRow r) => new(
        SpreadId: r.SpreadId,
        Underlying: r.Underlying,
        NearSymbol: r.NearSymbol,
        FarSymbol: r.FarSymbol,
        NearExpiry: r.NearExpiry,
        FarExpiry: r.FarExpiry,
        CapturedAt: r.CapturedAt,
        NearPrice: r.NearPrice,
        FarPrice: r.FarPrice,
        Direction: r.Direction,
        BuySpreadValue: r.BuySpreadValue,
        SellSpreadValue: r.SellSpreadValue,
        BuySpreadPercent: r.BuySpreadPercent,
        SellSpreadPercent: r.SellSpreadPercent);
}
