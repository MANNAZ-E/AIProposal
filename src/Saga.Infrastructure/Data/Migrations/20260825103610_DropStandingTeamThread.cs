using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saga.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Drops the standing "Bid Chat" thread. The bid team chat now opens on an unstarted draft the
    /// way a new AI chat does, so nothing needs a thread that exists before anybody has said
    /// anything — and with the flag gone the list is plain recency, newest first.
    /// </summary>
    public partial class DropStandingTeamThread : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The standing threads nobody ever posted to were created by the app, not by a person,
            // and would otherwise sit in every list as an empty conversation. One that did collect
            // messages stays, as an ordinary thread with no creator behind it; its read marks are
            // cascaded away with the rows that go. Only auto-created threads can be empty —
            // starting one has always written its first message in the same save.
            migrationBuilder.Sql("""
                DELETE FROM TeamThreads
                WHERE NOT EXISTS (SELECT 1 FROM TeamMessages m WHERE m.TeamThreadId = TeamThreads.Id);
                """);

            migrationBuilder.DropIndex(
                name: "IX_TeamThreads_ProposalId",
                table: "TeamThreads");

            migrationBuilder.DropColumn(
                name: "IsDefault",
                table: "TeamThreads");
        }

        /// <summary>
        /// The column and its one-per-proposal index come back empty: the deleted threads are
        /// gone, and every surviving one reads as an ordinary thread. The old code creates a fresh
        /// standing thread the next time somebody opens the section.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDefault",
                table: "TeamThreads",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_TeamThreads_ProposalId",
                table: "TeamThreads",
                column: "ProposalId",
                unique: true,
                filter: "[IsDefault] = 1");
        }
    }
}
