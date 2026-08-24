using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saga.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DocumentTypeNamesPlural : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The seeded categories are plural now. Proposals still carrying the old default
            // names follow along; types renamed or added by hand are left alone.
            migrationBuilder.Sql("""
                UPDATE [DocumentTypes] SET [Name] = 'Client materials' WHERE [Name] = 'Client material';
                UPDATE [DocumentTypes] SET [Name] = 'Mannaz materials' WHERE [Name] = 'Mannaz material';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE [DocumentTypes] SET [Name] = 'Client material' WHERE [Name] = 'Client materials';
                UPDATE [DocumentTypes] SET [Name] = 'Mannaz material' WHERE [Name] = 'Mannaz materials';
                """);
        }
    }
}
