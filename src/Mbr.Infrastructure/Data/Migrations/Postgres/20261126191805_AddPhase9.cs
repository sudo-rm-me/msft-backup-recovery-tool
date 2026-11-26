using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mbr.Infrastructure.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddPhase9 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TeamsMessagesEnabled",
                table: "TenantSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "WorkerInstances",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Host = table.Column<string>(type: "text", nullable: false),
                    ProcessId = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MaxConcurrency = table.Column<int>(type: "integer", nullable: false),
                    RunningJobs = table.Column<int>(type: "integer", nullable: false),
                    JobTypes = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerInstances", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkerInstances_LastSeenAt",
                table: "WorkerInstances",
                column: "LastSeenAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkerInstances");

            migrationBuilder.DropColumn(
                name: "TeamsMessagesEnabled",
                table: "TenantSettings");
        }
    }
}
