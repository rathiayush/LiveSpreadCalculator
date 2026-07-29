using Microsoft.EntityFrameworkCore;

namespace LiveSpreadCalculator.Persistence;

/// <summary>
/// EF Core context for the hourly spread history. The Fluent mapping here is kept deliberately in
/// sync with EfSpreadHistoryStore.CreateTableSql (the idempotent "create the table if it doesn't
/// exist" statement) so both the EF model and the raw create produce the identical "SpreadHistory"
/// schema - queries and BulkInsert work regardless of which path first created the table. Postgres
/// (Npgsql provider) - no explicit schema is set, so both this mapping and the raw DDL land in the
/// connection's default schema ("public" unless overridden).
/// </summary>
public sealed class SpreadHistoryDbContext : DbContext
{
    public SpreadHistoryDbContext(DbContextOptions<SpreadHistoryDbContext> options) : base(options)
    {
    }

    public DbSet<SpreadHistoryRow> SpreadHistory => Set<SpreadHistoryRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<SpreadHistoryRow>();
        entity.ToTable("SpreadHistory");
        entity.HasKey(x => x.Id).HasName("PK_SpreadHistory");

        entity.Property(x => x.SpreadId).HasMaxLength(100).IsRequired();
        entity.Property(x => x.Underlying).HasMaxLength(50).IsRequired();
        entity.Property(x => x.NearSymbol).HasMaxLength(50).IsRequired();
        entity.Property(x => x.FarSymbol).HasMaxLength(50).IsRequired();
        entity.Property(x => x.Direction).HasMaxLength(20).IsRequired();

        entity.Property(x => x.NearExpiry).HasColumnType("date");
        entity.Property(x => x.FarExpiry).HasColumnType("date");
        // Postgres has no fixed-precision "seconds only" timestamp type the way SQL Server's
        // datetimeoffset(0) does - timestamptz always stores microsecond precision. CapturedAt is
        // always written from a value already truncated to the second (see the snapshot capture
        // callers), so this is a storage-format change only, not a precision change in practice.
        entity.Property(x => x.CapturedAt).HasColumnType("timestamptz");

        entity.Property(x => x.NearPrice).HasColumnType("numeric(18,4)");
        entity.Property(x => x.FarPrice).HasColumnType("numeric(18,4)");
        entity.Property(x => x.BuySpreadValue).HasColumnType("numeric(18,4)");
        entity.Property(x => x.SellSpreadValue).HasColumnType("numeric(18,4)");
        entity.Property(x => x.BuySpreadPercent).HasColumnType("numeric(18,6)");
        entity.Property(x => x.SellSpreadPercent).HasColumnType("numeric(18,6)");

        entity.HasIndex(x => new { x.SpreadId, x.CapturedAt }).HasDatabaseName("IX_SpreadHistory_SpreadId_CapturedAt");
        entity.HasIndex(x => x.NearExpiry).HasDatabaseName("IX_SpreadHistory_NearExpiry");
    }
}
