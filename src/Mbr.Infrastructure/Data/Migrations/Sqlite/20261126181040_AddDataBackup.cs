using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mbr.Infrastructure.Data.Migrations.Sqlite
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    WebUrl = table.Column<string>(type: "TEXT", nullable: true),
                    RootItemId = table.Column<string>(type: "TEXT", nullable: true),
                    DeltaLink = table.Column<string>(type: "TEXT", nullable: true),
                    ResumeLink = table.Column<string>(type: "TEXT", nullable: true),
                    ResumeIsFull = table.Column<bool>(type: "INTEGER", nullable: false),
                    RetryItemIds = table.Column<string>(type: "TEXT", nullable: false),
                    LastSyncAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Missing = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataContainers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DataItemVersions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContainerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemId = table.Column<string>(type: "TEXT", nullable: false),
                    ParentItemId = table.Column<string>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    IsFolder = table.Column<bool>(type: "INTEGER", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    Chunks = table.Column<string>(type: "TEXT", nullable: false),
                    StorageTargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ETag = table.Column<string>(type: "TEXT", nullable: true),
                    CTag = table.Column<string>(type: "TEXT", nullable: true),
                    QuickXorHash = table.Column<string>(type: "TEXT", nullable: true),
                    ContentType = table.Column<string>(type: "TEXT", nullable: true),
                    ModifiedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    CapturedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    SupersededAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DeletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataItemVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DataSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    Address = table.Column<string>(type: "TEXT", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Missing = table.Column<bool>(type: "INTEGER", nullable: false),
                    DiscoveredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastBackupAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastStatus = table.Column<string>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    ItemCount = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalBytes = table.Column<long>(type: "INTEGER", nullable: false)
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
