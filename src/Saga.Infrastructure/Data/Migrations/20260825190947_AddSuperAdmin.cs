using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saga.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSuperAdmin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSuperAdmin",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "Users",
                keyColumn: "Id",
                keyValue: new Guid("6f9e2f9e-0001-4a7e-9f10-000000000001"),
                column: "IsSuperAdmin",
                value: true);

            migrationBuilder.UpdateData(
                table: "Users",
                keyColumn: "Id",
                keyValue: new Guid("6f9e2f9e-0001-4a7e-9f10-000000000002"),
                column: "IsSuperAdmin",
                value: false);

            migrationBuilder.UpdateData(
                table: "Users",
                keyColumn: "Id",
                keyValue: new Guid("6f9e2f9e-0001-4a7e-9f10-000000000003"),
                column: "IsSuperAdmin",
                value: false);

            migrationBuilder.UpdateData(
                table: "Users",
                keyColumn: "Id",
                keyValue: new Guid("6f9e2f9e-0001-4a7e-9f10-000000000004"),
                column: "IsSuperAdmin",
                value: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsSuperAdmin",
                table: "Users");
        }
    }
}
