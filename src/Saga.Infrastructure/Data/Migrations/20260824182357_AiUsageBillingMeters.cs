using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saga.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AiUsageBillingMeters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // PageCount goes without a backfill on purpose. Its values were counted from layout
            // geometry rather than read from what Azure said it billed, so they are 0 for every
            // Office row and only accidentally right for PDFs. Leaving the new counters null says
            // "we cannot say what this call cost", which is the truth about those rows.
            migrationBuilder.DropColumn(
                name: "PageCount",
                table: "AiUsage");

            migrationBuilder.AddColumn<int>(
                name: "BasicPages",
                table: "AiUsage",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ContextualizationTokens",
                table: "AiUsage",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MinimalPages",
                table: "AiUsage",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StandardPages",
                table: "AiUsage",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BasicPages",
                table: "AiUsage");

            migrationBuilder.DropColumn(
                name: "ContextualizationTokens",
                table: "AiUsage");

            migrationBuilder.DropColumn(
                name: "MinimalPages",
                table: "AiUsage");

            migrationBuilder.DropColumn(
                name: "StandardPages",
                table: "AiUsage");

            migrationBuilder.AddColumn<int>(
                name: "PageCount",
                table: "AiUsage",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }
    }
}
