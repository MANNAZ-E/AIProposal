using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Saga.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class SeedFullNamesAndTestUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "Users",
                keyColumn: "Id",
                keyValue: new Guid("6f9e2f9e-0001-4a7e-9f10-000000000001"),
                column: "DisplayName",
                value: "Emil Lindeløv Vestergaard");

            migrationBuilder.UpdateData(
                table: "Users",
                keyColumn: "Id",
                keyValue: new Guid("6f9e2f9e-0001-4a7e-9f10-000000000002"),
                column: "DisplayName",
                value: "Stefanie Baptiste");

            migrationBuilder.InsertData(
                table: "Users",
                columns: new[] { "Id", "CreatedAt", "DisplayName", "Email", "EntraObjectId" },
                values: new object[,]
                {
                    { new Guid("6f9e2f9e-0001-4a7e-9f10-000000000003"), new DateTimeOffset(new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Mikkel Kjær Nielsen", "mkn@mannaz.com", null },
                    { new Guid("6f9e2f9e-0001-4a7e-9f10-000000000004"), new DateTimeOffset(new DateTime(2026, 7, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Pauline Thorsen Holm", "jth@mannaz.com", null }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "Users",
                keyColumn: "Id",
                keyValue: new Guid("6f9e2f9e-0001-4a7e-9f10-000000000003"));

            migrationBuilder.DeleteData(
                table: "Users",
                keyColumn: "Id",
                keyValue: new Guid("6f9e2f9e-0001-4a7e-9f10-000000000004"));

            migrationBuilder.UpdateData(
                table: "Users",
                keyColumn: "Id",
                keyValue: new Guid("6f9e2f9e-0001-4a7e-9f10-000000000001"),
                column: "DisplayName",
                value: "Emil");

            migrationBuilder.UpdateData(
                table: "Users",
                keyColumn: "Id",
                keyValue: new Guid("6f9e2f9e-0001-4a7e-9f10-000000000002"),
                column: "DisplayName",
                value: "sda");
        }
    }
}
