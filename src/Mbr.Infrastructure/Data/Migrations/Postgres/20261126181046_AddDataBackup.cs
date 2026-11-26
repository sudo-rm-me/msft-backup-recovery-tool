using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Mbr.Infrastructure.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddDataBackup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DataContainers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    WebUrl = table.Column<string>(type: "text", nullable: true),
                    RootItemId = table.Column<string>(type: "text", nullable: true),
                    DeltaLink = table.Column<string>(type: "text", nullable: true),
                    ResumeLink = table.Column<string>(type: "text", nullable: true),
                    ResumeIsFull = table.Column<bool>(type: "boolean", nullable: false),
                    RetryItemIds = table.Column<string>(type: "text", nullable: false),
                    LastSyncAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Missing = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataContainers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DataItemVersions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContainerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<string>(type: "text", nullable: false),
                    ParentItemId = table.Column<string>(type: "text", nullable: true),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false),
                    IsFolder = table.Column<bool>(type: "boolean", nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Chunks = table.Column<string>(type: "text", nullable: false),
                    StorageTargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    ETag = table.Column<string>(type: "text", nullable: true),
                    CTag = table.Column<string>(type: "text", nullable: true),
                    QuickXorHash = table.Column<string>(type: "text", nullable: true),
                    ContentType = table.Column<string>(type: "text", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "text", nullable: true),
                    CapturedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SupersededAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    MetadataJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataItemVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DataSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    ExternalId = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    Address = table.Column<string>(type: "text", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Missing = table.Column<bool>(type: "boolean", nullable: false),
                    DiscoveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastBackupAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastStatus = table.Column<string>(type: "text", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    ItemCount = table.Column<long>(type: "bigint", nullable: false),
                    TotalBytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataSources", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DataContainers_SourceId_ExternalId",
                table: "DataContainers",
                columns: new[] { "SourceId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DataItemVersions_ContainerId_ItemId_SupersededAt",
                table: "DataItemVersions",
                columns: new[] { "ContainerId", "ItemId", "SupersededAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DataItemVersions_ContainerId_ParentItemId",
                table: "DataItemVersions",
                columns: new[] { "ContainerId", "ParentItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_DataItemVersions_TenantId_CapturedAt",
                table: "DataItemVersions",
                columns: new[] { "TenantId", "CapturedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DataItemVersions_TenantId_SourceId",
                table: "DataItemVersions",
                columns: new[] { "TenantId", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_DataSources_TenantId_Kind_ExternalId",
                table: "DataSources",
                columns: new[] { "TenantId", "Kind", "ExternalId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DataContainers");

            migrationBuilder.DropTable(
                name: "DataItemVersions");

            migrationBuilder.DropTable(
                name: "DataSources");
        }
    }
}
