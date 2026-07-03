using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saga.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DocumentCondensedText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CondensedText",
                table: "Documents",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CondensedText",
                table: "Documents");
        }
    }
}
