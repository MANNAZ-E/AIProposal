using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saga.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Gives the bid team chat many threads instead of one. The rename of ProposalId to
    /// TeamThreadId is what makes the backfill cheap: until the new foreign keys go on, every
    /// existing message and read mark still carries its proposal id, so one standing thread per
    /// proposal plus a join is the whole migration. No message and no read mark is lost.
    /// </summary>
    public partial class BidTeamThreads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TeamChatSeen_Proposals_ProposalId",
                table: "TeamChatSeen");

            migrationBuilder.DropForeignKey(
                name: "FK_TeamMessages_Proposals_ProposalId",
                table: "TeamMessages");

            migrationBuilder.RenameColumn(
                name: "ProposalId",
                table: "TeamMessages",
                newName: "TeamThreadId");

            migrationBuilder.RenameIndex(
                name: "IX_TeamMessages_ProposalId_CreatedAt",
                table: "TeamMessages",
                newName: "IX_TeamMessages_TeamThreadId_CreatedAt");

            migrationBuilder.RenameColumn(
                name: "ProposalId",
                table: "TeamChatSeen",
                newName: "TeamThreadId");

            migrationBuilder.RenameIndex(
                name: "IX_TeamChatSeen_ProposalId_UserId",
                table: "TeamChatSeen",
                newName: "IX_TeamChatSeen_TeamThreadId_UserId");

            migrationBuilder.CreateTable(
                name: "TeamThreads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastMessageAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamThreads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TeamThreads_Proposals_ProposalId",
                        column: x => x.ProposalId,
                        principalTable: "Proposals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TeamThreads_Users_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TeamThreads_CreatedById",
                table: "TeamThreads",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_TeamThreads_ProposalId",
                table: "TeamThreads",
                column: "ProposalId",
                unique: true,
                filter: "[IsDefault] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_TeamThreads_ProposalId_LastMessageAt",
                table: "TeamThreads",
                columns: new[] { "ProposalId", "LastMessageAt" });

            // Every proposal gets its standing "Bid Chat" thread, and everything written under the
            // single-thread schema moves onto it. This runs before the foreign keys below, while
            // TeamThreadId still holds the proposal id it was renamed from.
            migrationBuilder.Sql("""
                INSERT INTO TeamThreads (Id, ProposalId, Title, CreatedById, IsDefault, CreatedAt, LastMessageAt)
                SELECT NEWID(), p.Id, N'Bid Chat', NULL, 1, SYSDATETIMEOFFSET(),
                       COALESCE((SELECT MAX(m.CreatedAt) FROM TeamMessages m WHERE m.TeamThreadId = p.Id),
                                SYSDATETIMEOFFSET())
                FROM Proposals p;

                UPDATE m SET m.TeamThreadId = t.Id
                FROM TeamMessages m
                JOIN TeamThreads t ON t.ProposalId = m.TeamThreadId AND t.IsDefault = 1;

                UPDATE s SET s.TeamThreadId = t.Id
                FROM TeamChatSeen s
                JOIN TeamThreads t ON t.ProposalId = s.TeamThreadId AND t.IsDefault = 1;
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_TeamChatSeen_TeamThreads_TeamThreadId",
                table: "TeamChatSeen",
                column: "TeamThreadId",
                principalTable: "TeamThreads",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_TeamMessages_TeamThreads_TeamThreadId",
                table: "TeamMessages",
                column: "TeamThreadId",
                principalTable: "TeamThreads",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TeamChatSeen_TeamThreads_TeamThreadId",
                table: "TeamChatSeen");

            migrationBuilder.DropForeignKey(
                name: "FK_TeamMessages_TeamThreads_TeamThreadId",
                table: "TeamMessages");

            // Fold every thread back onto its proposal before the table goes: the old schema has
            // nowhere to put a second conversation, so they merge rather than disappear. One
            // person's read marks merge too, and the earliest wins — re-showing something already
            // read is a smaller harm than hiding something unread.
            migrationBuilder.Sql("""
                UPDATE m SET m.TeamThreadId = t.ProposalId
                FROM TeamMessages m JOIN TeamThreads t ON t.Id = m.TeamThreadId;

                DELETE s FROM TeamChatSeen s
                JOIN TeamThreads t ON t.Id = s.TeamThreadId
                WHERE EXISTS (
                    SELECT 1 FROM TeamChatSeen s2
                    JOIN TeamThreads t2 ON t2.Id = s2.TeamThreadId
                    WHERE t2.ProposalId = t.ProposalId AND s2.UserId = s.UserId
                      AND (s2.LastSeenAt < s.LastSeenAt
                           OR (s2.LastSeenAt = s.LastSeenAt AND s2.Id < s.Id)));

                UPDATE s SET s.TeamThreadId = t.ProposalId
                FROM TeamChatSeen s JOIN TeamThreads t ON t.Id = s.TeamThreadId;
                """);

            migrationBuilder.DropTable(
                name: "TeamThreads");

            migrationBuilder.RenameColumn(
                name: "TeamThreadId",
                table: "TeamMessages",
                newName: "ProposalId");

            migrationBuilder.RenameIndex(
                name: "IX_TeamMessages_TeamThreadId_CreatedAt",
                table: "TeamMessages",
                newName: "IX_TeamMessages_ProposalId_CreatedAt");

            migrationBuilder.RenameColumn(
                name: "TeamThreadId",
                table: "TeamChatSeen",
                newName: "ProposalId");

            migrationBuilder.RenameIndex(
                name: "IX_TeamChatSeen_TeamThreadId_UserId",
                table: "TeamChatSeen",
                newName: "IX_TeamChatSeen_ProposalId_UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_TeamChatSeen_Proposals_ProposalId",
                table: "TeamChatSeen",
                column: "ProposalId",
                principalTable: "Proposals",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_TeamMessages_Proposals_ProposalId",
                table: "TeamMessages",
                column: "ProposalId",
                principalTable: "Proposals",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
