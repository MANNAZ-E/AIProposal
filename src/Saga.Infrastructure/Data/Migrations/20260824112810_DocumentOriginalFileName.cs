using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saga.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DocumentOriginalFileName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OriginalFileName",
                table: "Documents",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            // Existing uploads were never renamable, so their name is still the file's own.
            migrationBuilder.Sql(
                "UPDATE [Documents] SET [OriginalFileName] = [Name] WHERE [Kind] = 0;");

            // The seeded categories were relabelled "material". Proposals still carrying the old
            // default names follow along; types renamed or added by hand are left alone.
            migrationBuilder.Sql("""
                UPDATE [DocumentTypes] SET [Name] = 'Client material' WHERE [Name] = 'Client documents';
                UPDATE [DocumentTypes] SET [Name] = 'Mannaz material' WHERE [Name] = 'Mannaz documents';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE [DocumentTypes] SET [Name] = 'Client documents' WHERE [Name] = 'Client material';
                UPDATE [DocumentTypes] SET [Name] = 'Mannaz documents' WHERE [Name] = 'Mannaz material';
                """);

            migrationBuilder.DropColumn(
                name: "OriginalFileName",
                table: "Documents");
        }
    }
}
