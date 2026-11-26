using Mbr.Core.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Mbr.Infrastructure.Data;

/// <summary>
/// The metadata database. Use <see cref="SqliteMbrDbContext"/> or <see cref="PostgresMbrDbContext"/>; each has its
/// own migrations (Data/Migrations/Sqlite, Data/Migrations/Postgres). See docs/development.md for adding migrations.
/// </summary>
public abstract class MbrDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<StorageTarget> StorageTargets => Set<StorageTarget>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobLog> JobLogs => Set<JobLog>();
    public DbSet<Schedule> Schedules => Set<Schedule>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<Operator> Operators => Set<Operator>();
    public DbSet<OperatorTenantRole> OperatorTenantRoles => Set<OperatorTenantRole>();
    public DbSet<Lease> Leases => Set<Lease>();
    public DbSet<StoredObject> StoredObjects => Set<StoredObject>();
    public DbSet<Snapshot> Snapshots => Set<Snapshot>();
    public DbSet<SnapshotItem> SnapshotItems => Set<SnapshotItem>();
    public DbSet<SnapshotIssue> SnapshotIssues => Set<SnapshotIssue>();
    public DbSet<TenantSettings> TenantSettings => Set<TenantSettings>();
    public DbSet<IdMapping> IdMappings => Set<IdMapping>();
    public DbSet<RestorePlan> RestorePlans => Set<RestorePlan>();
    public DbSet<DataSource> DataSources => Set<DataSource>();
    public DbSet<DataContainer> DataContainers => Set<DataContainer>();
    public DbSet<DataItemVersion> DataItemVersions => Set<DataItemVersion>();
    public DbSet<WorkerInstance> WorkerInstances => Set<WorkerInstance>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // SQLite cannot compare or order DateTimeOffset values; store them as sortable UTC ticks.
        // All timestamps in this app are UTC, so ordering is preserved.
        if (Database.IsSqlite())
        {
            builder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
            builder.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        }
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Tenant>(e =>
        {
            e.HasIndex(x => x.DirectoryId).IsUnique();
            e.Property(x => x.Status).HasConversion<string>();
        });
        b.Entity<AppSetting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(200);
        });
        b.Entity<StorageTarget>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Kind).HasConversion<string>();
        });
        b.Entity<Job>(e =>
        {
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.Status, x.NotBefore });
            e.HasIndex(x => new { x.TenantId, x.CreatedAt });
            e.HasIndex(x => x.ScheduleId);
        });
        b.Entity<JobLog>(e =>
        {
            e.Property(x => x.Level).HasConversion<string>();
            e.HasIndex(x => new { x.JobId, x.Id });
        });
        b.Entity<Schedule>(e => e.HasIndex(x => new { x.Enabled, x.NextRunAt }));
        b.Entity<AuditEntry>(e => e.HasIndex(x => new { x.TenantId, x.At }));
        b.Entity<Operator>(e =>
        {
            e.HasIndex(x => x.ObjectId).IsUnique();
            e.HasMany(x => x.Roles).WithOne().HasForeignKey(r => r.OperatorId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<OperatorTenantRole>(e => e.Property(x => x.Role).HasConversion<string>());
        b.Entity<Lease>(e =>
        {
            e.HasKey(x => x.Name);
            e.Property(x => x.Version).IsConcurrencyToken();
        });
        b.Entity<StoredObject>(e => e.HasKey(x => new { x.TenantId, x.StorageTargetId, x.Hash }));
        b.Entity<Snapshot>(e =>
        {
            e.Property(x => x.Status).HasConversion<string>();
            e.HasIndex(x => new { x.TenantId, x.CreatedAt });
        });
        b.Entity<SnapshotItem>(e =>
        {
            e.HasIndex(x => new { x.SnapshotId, x.ResourceType });
            e.HasIndex(x => new { x.ResourceType, x.ObjectId });
            e.HasIndex(x => x.Hash);
        });
        b.Entity<SnapshotIssue>(e => e.HasIndex(x => x.SnapshotId));
        b.Entity<TenantSettings>(e => e.HasKey(x => x.TenantId));
        b.Entity<IdMapping>(e => e.HasIndex(x => new { x.SourceTenantId, x.TargetTenantId, x.ResourceType, x.SourceId }).IsUnique());
        b.Entity<RestorePlan>(e =>
        {
            e.Property(x => x.Status).HasConversion<string>();
            e.HasIndex(x => new { x.TenantId, x.CreatedAt });
        });
        b.Entity<DataSource>(e =>
        {
            e.Property(x => x.Kind).HasConversion<string>();
            e.HasIndex(x => new { x.TenantId, x.Kind, x.ExternalId }).IsUnique();
        });
        b.Entity<DataContainer>(e => e.HasIndex(x => new { x.SourceId, x.ExternalId }).IsUnique());
        b.Entity<DataItemVersion>(e =>
        {
            e.HasIndex(x => new { x.ContainerId, x.ItemId, x.SupersededAt });
            e.HasIndex(x => new { x.ContainerId, x.ParentItemId });
            e.HasIndex(x => new { x.TenantId, x.SourceId });
            e.HasIndex(x => new { x.TenantId, x.CapturedAt });
        });
        b.Entity<WorkerInstance>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.HasIndex(x => x.LastSeenAt);
        });
    }
}

public sealed class SqliteMbrDbContext(DbContextOptions<SqliteMbrDbContext> options) : MbrDbContext(options);

public sealed class PostgresMbrDbContext(DbContextOptions<PostgresMbrDbContext> options) : MbrDbContext(options);

/// <summary>Design-time factories used by "dotnet ef migrations add".</summary>
public sealed class SqliteDesignFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<SqliteMbrDbContext>
{
    public SqliteMbrDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SqliteMbrDbContext>().UseSqlite("Data Source=design.db").Options);
}

public sealed class PostgresDesignFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<PostgresMbrDbContext>
{
    public PostgresMbrDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PostgresMbrDbContext>().UseNpgsql("Host=localhost;Database=mbr_design").Options);
}
