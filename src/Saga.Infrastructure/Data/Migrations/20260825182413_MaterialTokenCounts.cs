using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saga.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MaterialTokenCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TokenCount",
                table: "DocumentVersions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CondensedTokenCount",
                table: "Documents",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TokenCount",
                table: "Documents",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TokenCount",
                table: "DocumentVersions");

            migrationBuilder.DropColumn(
                name: "CondensedTokenCount",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "TokenCount",
                table: "Documents");
        }
    }
}
