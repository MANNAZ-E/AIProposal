using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saga.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DocumentTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DocumentTypeId",
                table: "Documents",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "DocumentTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentTypes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentTypes_Proposals_ProposalId",
                        column: x => x.ProposalId,
                        principalTable: "Proposals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Documents_DocumentTypeId",
                table: "Documents",
                column: "DocumentTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentTypes_ProposalId_Name",
                table: "DocumentTypes",
                columns: new[] { "ProposalId", "Name" },
                unique: true);

            // Every proposal gets the two default categories, and existing material is filed:
            // uploads are the client's own documents, notes were written by the consultant.
            // This has to run before the foreign key goes on, since the added column starts empty.
            migrationBuilder.Sql("""
                INSERT INTO [DocumentTypes] ([Id], [ProposalId], [Name], [SortOrder], [CreatedAt])
                SELECT NEWID(), p.[Id], t.[Name], t.[SortOrder], SYSDATETIMEOFFSET()
                FROM [Proposals] p
                CROSS JOIN (VALUES ('Client documents', 0), ('Mannaz documents', 1)) AS t([Name], [SortOrder]);
                """);
            migrationBuilder.Sql("""
                UPDATE d SET d.[DocumentTypeId] = t.[Id]
                FROM [Documents] d
                INNER JOIN [DocumentTypes] t ON t.[ProposalId] = d.[ProposalId]
                    AND t.[Name] = CASE WHEN d.[Kind] = 1 THEN 'Mannaz documents' ELSE 'Client documents' END;
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_Documents_DocumentTypes_DocumentTypeId",
                table: "Documents",
                column: "DocumentTypeId",
                principalTable: "DocumentTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Documents_DocumentTypes_DocumentTypeId",
                table: "Documents");

            migrationBuilder.DropTable(
                name: "DocumentTypes");

            migrationBuilder.DropIndex(
                name: "IX_Documents_DocumentTypeId",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "DocumentTypeId",
                table: "Documents");
        }
    }
}
