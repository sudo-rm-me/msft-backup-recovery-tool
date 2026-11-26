using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Mbr.Infrastructure.Data.Migrations.Postgres
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
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "IdMappings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SourceTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceType = table.Column<string>(type: "text", nullable: false),
                    SourceId = table.Column<string>(type: "text", nullable: false),
                    TargetId = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdMappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RestorePlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceSnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ApprovedBy = table.Column<string>(type: "text", nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PlanJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApplyJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    AppliedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RequestJson = table.Column<string>(type: "text", nullable: false),
                    PlanJson = table.Column<string>(type: "text", nullable: false),
                    CreateCount = table.Column<int>(type: "integer", nullable: false),
                    UpdateCount = table.Column<int>(type: "integer", nullable: false),
                    RecycleBinCount = table.Column<int>(type: "integer", nullable: false),
                    DeleteCount = table.Column<int>(type: "integer", nullable: false),
                    SkipCount = table.Column<int>(type: "integer", nullable: false),
                    WarningCount = table.Column<int>(type: "integer", nullable: false),
                    SucceededCount = table.Column<int>(type: "integer", nullable: false),
                    FailedCount = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RestorePlans", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IdMappings_SourceTenantId_TargetTenantId_ResourceType_Sourc~",
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
