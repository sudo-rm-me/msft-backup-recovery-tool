using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mbr.Infrastructure.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddRestore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequireRestoreApproval",
                table: "TenantSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "IdMappings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceTenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetTenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ResourceType = table.Column<string>(type: "TEXT", nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", nullable: false),
                    TargetId = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdMappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RestorePlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceTenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceSnapshotId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ApprovedBy = table.Column<string>(type: "TEXT", nullable: true),
                    ApprovedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    PlanJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ApplyJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AppliedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    RequestJson = table.Column<string>(type: "TEXT", nullable: false),
                    PlanJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreateCount = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdateCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RecycleBinCount = table.Column<int>(type: "INTEGER", nullable: false),
                    DeleteCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SkipCount = table.Column<int>(type: "INTEGER", nullable: false),
                    WarningCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SucceededCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FailedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RestorePlans", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IdMappings_SourceTenantId_TargetTenantId_ResourceType_SourceId",
                table: "IdMappings",
                columns: new[] { "SourceTenantId", "TargetTenantId", "ResourceType", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RestorePlans_TenantId_CreatedAt",
                table: "RestorePlans",
                columns: new[] { "TenantId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IdMappings");

            migrationBuilder.DropTable(
                name: "RestorePlans");

            migrationBuilder.DropColumn(
                name: "RequireRestoreApproval",
                table: "TenantSettings");
        }
    }
}
