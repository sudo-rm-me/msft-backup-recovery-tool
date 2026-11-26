using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mbr.Infrastructure.Data.Migrations.Sqlite
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
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "WorkerInstances",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Host = table.Column<string>(type: "TEXT", nullable: false),
                    ProcessId = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    MaxConcurrency = table.Column<int>(type: "INTEGER", nullable: false),
                    RunningJobs = table.Column<int>(type: "INTEGER", nullable: false),
                    JobTypes = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: true)
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
