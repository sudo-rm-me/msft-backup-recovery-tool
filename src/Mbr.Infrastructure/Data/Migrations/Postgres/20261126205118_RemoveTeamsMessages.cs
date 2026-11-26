using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mbr.Infrastructure.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class RemoveTeamsMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Teams channel message backup was removed: drop any Team sources and their backed-up threads.
            migrationBuilder.Sql("""
                DELETE FROM "DataItemVersions" WHERE "SourceId" IN (SELECT "Id" FROM "DataSources" WHERE "Kind" = 'Team');
                DELETE FROM "DataContainers" WHERE "SourceId" IN (SELECT "Id" FROM "DataSources" WHERE "Kind" = 'Team');
                DELETE FROM "DataSources" WHERE "Kind" = 'Team';
                """);

            migrationBuilder.DropColumn(
                name: "TeamsMessagesEnabled",
                table: "TenantSettings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TeamsMessagesEnabled",
                table: "TenantSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
