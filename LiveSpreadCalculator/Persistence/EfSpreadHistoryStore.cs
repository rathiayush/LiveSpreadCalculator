using Npgsql;
using Microsoft.EntityFrameworkCore;
using EFCore.BulkExtensions;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;

namespace LiveSpreadCalculator.Persistence;

/// <summary>
/// EF Core (PostgreSQL / Npgsql) implementation of ISpreadHistoryStore.
///
/// - InitializeAsync: connects to the "postgres" maintenance database to CREATE DATABASE if the
///   target database is missing, then runs an idempotent "CREATE TABLE IF NOT EXISTS" statement -
///   so a fresh Postgres server works with no manual setup, and a database that's simply missing
///   the table gets it created (EnsureCreated alone wouldn't cover that case). In the Docker/Dokploy
///   deployment the target database is normally already created via the Postgres container's own
///   POSTGRES_DB variable, so step 1 here is typically a no-op - it stays as a defensive fallback
///   for any deployment that points at a Postgres server it doesn't provision.
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

    // Guards the one-time "create the table if it doesn't exist" DDL so it runs at most once per
    // process, whether triggered from startup InitializeAsync or lazily from the first insert.
    private readonly SemaphoreSlim _ensureLock = new(1, 1);
    private bool _tableEnsured;

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
    // EF-modelled table are identical. Postgres has native IF NOT EXISTS on both CREATE TABLE and
    // CREATE INDEX, so this needs none of the OBJECT_ID/BEGIN-END dance the SQL Server version did.
    private const string CreateTableSql = @"
CREATE TABLE IF NOT EXISTS ""SpreadHistory"" (
    ""Id""                BIGINT GENERATED ALWAYS AS IDENTITY CONSTRAINT ""PK_SpreadHistory"" PRIMARY KEY,
    ""SpreadId""          VARCHAR(100)  NOT NULL,
    ""Underlying""        VARCHAR(50)   NOT NULL,
    ""NearSymbol""        VARCHAR(50)   NOT NULL,
    ""FarSymbol""         VARCHAR(50)   NOT NULL,
    ""NearExpiry""        DATE          NULL,
    ""FarExpiry""         DATE          NULL,
    ""CapturedAt""        TIMESTAMPTZ   NOT NULL,
    ""NearPrice""         NUMERIC(18,4) NOT NULL,
    ""FarPrice""          NUMERIC(18,4) NOT NULL,
    ""Direction""         VARCHAR(20)   NOT NULL,
    ""BuySpreadValue""    NUMERIC(18,4) NOT NULL,
    ""SellSpreadValue""   NUMERIC(18,4) NOT NULL,
    ""BuySpreadPercent""  NUMERIC(18,6) NOT NULL,
    ""SellSpreadPercent"" NUMERIC(18,6) NOT NULL
);
CREATE INDEX IF NOT EXISTS ""IX_SpreadHistory_SpreadId_CapturedAt"" ON ""SpreadHistory"" (""SpreadId"", ""CapturedAt"");
CREATE INDEX IF NOT EXISTS ""IX_SpreadHistory_NearExpiry"" ON ""SpreadHistory"" (""NearExpiry"");";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var builder = new NpgsqlConnectionStringBuilder(_connectionString);
        var databaseName = string.IsNullOrWhiteSpace(builder.Database)
            ? "spreadtrader_history"
            : builder.Database;

        // 1) BEST-EFFORT: create the database if it's missing. Wrapped so a failure here NEVER
        //    blocks the table creation below - a managed Postgres instance may not grant this login
        //    CREATEDB, and in the Docker/Dokploy deployment the Postgres container already creates
        //    the database itself via POSTGRES_DB on first boot, making this a no-op there. Kept for
        //    any deployment that points at a Postgres server it doesn't provision (matches the SQL
        //    Server version's Azure SQL reasoning). Postgres also refuses CREATE DATABASE inside a
        //    transaction, and Npgsql's ExecuteNonQuery does not open one implicitly, so no explicit
        //    transaction handling is needed here.
        try
        {
            var maintenanceBuilder = new NpgsqlConnectionStringBuilder(_connectionString) { Database = "postgres" };
            await using var maintenance = new NpgsqlConnection(maintenanceBuilder.ConnectionString);
            await maintenance.OpenAsync(cancellationToken);

            await using var exists = maintenance.CreateCommand();
            exists.CommandText = "SELECT 1 FROM pg_database WHERE datname = @db";
            exists.Parameters.AddWithValue("db", databaseName);
            var alreadyExists = await exists.ExecuteScalarAsync(cancellationToken) is not null;

            if (!alreadyExists)
            {
                // CREATE DATABASE can't take a parameter for the name - quote it safely instead
                // (Postgres identifier-quoting: double the embedded double-quotes).
                var safeDbName = "\"" + databaseName.Replace("\"", "\"\"") + "\"";
                await using var createDb = maintenance.CreateCommand();
                createDb.CommandText = $"CREATE DATABASE {safeDbName};";
                await createDb.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not ensure database '{Database}' exists via the postgres maintenance database " +
                "(the login may lack CREATEDB, or the DB was created out-of-band). Continuing to table " +
                "creation.", databaseName);
        }

        // 2) Ensure the table (+ indexes) exist - the step that actually matters, runs regardless of
        //    whether step 1 above succeeded. Called AT STARTUP (see Program.cs, writer instance
        //    only) so a missing table is caught and created before the first capture runs.
        await EnsureTableAsync(cancellationToken);
        _logger.LogInformation("SpreadHistory store ready (database {Database}).", databaseName);
    }

    /// <summary>Creates dbo.SpreadHistory (+ indexes) if it doesn't already exist. Idempotent and
    /// guarded so the DDL runs at most once per process. Called from InitializeAsync at startup, and
    /// again defensively from AddBatchAsync before every insert, so captures still self-heal even if
    /// startup init didn't run on this instance or failed partway through.</summary>
    private async Task EnsureTableAsync(CancellationToken cancellationToken)
    {
        if (_tableEnsured) return;
        await _ensureLock.WaitAsync(cancellationToken);
        try
        {
            if (_tableEnsured) return;
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            await context.Database.ExecuteSqlRawAsync(CreateTableSql, cancellationToken);
            _tableEnsured = true;
            _logger.LogInformation("Ensured SpreadHistory table exists.");
        }
        finally
        {
            _ensureLock.Release();
        }
    }

    public async Task AddBatchAsync(IReadOnlyList<SpreadHistoryPoint> points, CancellationToken cancellationToken = default)
    {
        if (points.Count == 0) return;

        var rows = points.Select(ToRow).ToList();

        // Safety net: guarantee the table exists before writing, in case startup InitializeAsync
        // didn't run on this instance or failed partway - so the first capture self-heals instead of
        // failing with "Invalid object name 'SpreadHistory'".
        await EnsureTableAsync(cancellationToken);

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
