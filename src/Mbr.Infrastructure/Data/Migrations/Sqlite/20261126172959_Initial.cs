using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mbr.Infrastructure.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    At = table.Column<long>(type: "INTEGER", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    Target = table.Column<string>(type: "TEXT", nullable: true),
                    DetailsJson = table.Column<string>(type: "TEXT", nullable: true),
                    IpAddress = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false),
                    Level = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ParametersJson = table.Column<string>(type: "TEXT", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: true),
                    CheckpointJson = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    NotBefore = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    ScheduleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LeaseOwner = table.Column<string>(type: "TEXT", nullable: true),
                    LeaseExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Progress = table.Column<double>(type: "REAL", nullable: false),
                    ProgressMessage = table.Column<string>(type: "TEXT", nullable: true),
                    CancelRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Leases",
                columns: table => new
                {
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Owner = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Version = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Leases", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "Operators",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ObjectId = table.Column<string>(type: "TEXT", nullable: false),
                    UserPrincipalName = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    IsGlobalAdmin = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Operators", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Schedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    JobType = table.Column<string>(type: "TEXT", nullable: false),
                    ParametersJson = table.Column<string>(type: "TEXT", nullable: false),
                    Cron = table.Column<string>(type: "TEXT", nullable: false),
                    TimeZone = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    NextRunAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastRunAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Schedules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SnapshotIssues",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SnapshotId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ResourceType = table.Column<string>(type: "TEXT", nullable: false),
                    ObjectId = table.Column<string>(type: "TEXT", nullable: true),
                    Severity = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnapshotIssues", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SnapshotItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SnapshotId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ResourceType = table.Column<string>(type: "TEXT", nullable: false),
                    Workload = table.Column<string>(type: "TEXT", nullable: false),
                    ObjectId = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnapshotItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StorageTargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Workloads = table.Column<string>(type: "TEXT", nullable: false),
                    ItemCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorCount = table.Column<int>(type: "INTEGER", nullable: false),
                    WarningCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    AddedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ModifiedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RemovedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: true),
                    IsBaseline = table.Column<bool>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Snapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StorageTargets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    SettingsJson = table.Column<string>(type: "TEXT", nullable: false),
                    EncryptedSecret = table.Column<string>(type: "TEXT", nullable: true),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageTargets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StoredObjects",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StorageTargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    StoredSize = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoredObjects", x => new { x.TenantId, x.StorageTargetId, x.Hash });
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DirectoryId = table.Column<string>(type: "TEXT", nullable: false),
                    DefaultDomain = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    BackupConsentedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    RestoreConsentedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastPermissionCheckAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastPermissionCheckJson = table.Column<string>(type: "TEXT", nullable: true),
                    DefaultStorageTargetId = table.Column<Guid>(type: "TEXT", nullable: true),
                    WrappedDataKey = table.Column<string>(type: "TEXT", nullable: true),
                    DataKeyWrapperId = table.Column<string>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantSettings",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConfigRetentionDays = table.Column<int>(type: "INTEGER", nullable: false),
                    ConfigKeepMinimum = table.Column<int>(type: "INTEGER", nullable: false),
                    DataRetentionDays = table.Column<int>(type: "INTEGER", nullable: false),
                    DriftAlerts = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantSettings", x => x.TenantId);
                });

            migrationBuilder.CreateTable(
                name: "OperatorTenantRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OperatorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Role = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperatorTenantRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OperatorTenantRoles_Operators_OperatorId",
                        column: x => x.OperatorId,
                        principalTable: "Operators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_TenantId_At",
                table: "AuditEntries",
                columns: new[] { "TenantId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_JobLogs_JobId_Id",
                table: "JobLogs",
                columns: new[] { "JobId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_ScheduleId",
                table: "Jobs",
                column: "ScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Status_NotBefore",
                table: "Jobs",
                columns: new[] { "Status", "NotBefore" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_TenantId_CreatedAt",
                table: "Jobs",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Operators_ObjectId",
                table: "Operators",
                column: "ObjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OperatorTenantRoles_OperatorId",
                table: "OperatorTenantRoles",
                column: "OperatorId");

            migrationBuilder.CreateIndex(
                name: "IX_Schedules_Enabled_NextRunAt",
                table: "Schedules",
                columns: new[] { "Enabled", "NextRunAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SnapshotIssues_SnapshotId",
                table: "SnapshotIssues",
                column: "SnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_SnapshotItems_Hash",
                table: "SnapshotItems",
                column: "Hash");

            migrationBuilder.CreateIndex(
                name: "IX_SnapshotItems_ResourceType_ObjectId",
                table: "SnapshotItems",
                columns: new[] { "ResourceType", "ObjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_SnapshotItems_SnapshotId_ResourceType",
                table: "SnapshotItems",
                columns: new[] { "SnapshotId", "ResourceType" });

            migrationBuilder.CreateIndex(
                name: "IX_Snapshots_TenantId_CreatedAt",
                table: "Snapshots",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StorageTargets_Name",
                table: "StorageTargets",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_DirectoryId",
                table: "Tenants",
                column: "DirectoryId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEntries");

            migrationBuilder.DropTable(
                name: "JobLogs");

            migrationBuilder.DropTable(
                name: "Jobs");

            migrationBuilder.DropTable(
                name: "Leases");

            migrationBuilder.DropTable(
                name: "OperatorTenantRoles");

            migrationBuilder.DropTable(
                name: "Schedules");

            migrationBuilder.DropTable(
                name: "SnapshotIssues");

            migrationBuilder.DropTable(
                name: "SnapshotItems");

            migrationBuilder.DropTable(
                name: "Snapshots");

            migrationBuilder.DropTable(
                name: "StorageTargets");

            migrationBuilder.DropTable(
                name: "StoredObjects");

            migrationBuilder.DropTable(
                name: "Tenants");

            migrationBuilder.DropTable(
                name: "TenantSettings");

            migrationBuilder.DropTable(
                name: "Operators");
        }
    }
}
