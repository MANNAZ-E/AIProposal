using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saga.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class SoftDeleteAndDropArtifactStale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsStale",
                table: "Artifacts");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "Proposals",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "Proposals",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Proposals");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "Proposals");

            migrationBuilder.AddColumn<bool>(
                name: "IsStale",
                table: "Artifacts",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }
    }
}
