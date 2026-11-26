using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mbr.Infrastructure.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddMailboxBackup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "DataContainers",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "StateJson",
                table: "DataContainers",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "DataContainers");

            migrationBuilder.DropColumn(
                name: "StateJson",
                table: "DataContainers");
        }
    }
}
