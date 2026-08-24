using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saga.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MultiChatWithLockedMaterial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ChatSessions_ProposalId",
                table: "ChatSessions");

            migrationBuilder.AddColumn<string>(
                name: "ContextSnapshot",
                table: "ChatSessions",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastMessageAt",
                table: "ChatSessions",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "MaterialSelectionJson",
                table: "ChatSessions",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerId",
                table: "ChatSessions",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "ChatSessions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Visibility",
                table: "ChatSessions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "WorkingContext",
                table: "ChatSessions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ChatSeen",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChatSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatSeen", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatSeen_ChatSessions_ChatSessionId",
                        column: x => x.ChatSessionId,
                        principalTable: "ChatSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChatSeen_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Chat used to be one implicitly-shared thread per proposal, so the scaffolded
            // defaults above (empty owner, empty title) are placeholders that have to be filled
            // before the OwnerId foreign key is added below.
            //
            // Owner is whoever asked first. AuthorId is nullable with SetNull, so the earliest
            // user message may have no author left; the rule is "the first user message that
            // still has an author", falling back to the proposal's owner. Proposals.OwnerId is
            // non-nullable and Restrict, so that fallback can never violate the new FK.
            //
            // Every migrated chat becomes Private (Visibility = 0). The old thread was visible
            // to the whole team, so a proposal where two people used it ends up with the whole
            // transcript private to whoever asked first; the owner can re-share it in one click.
            //
            // Title is a blunt LEFT(...) cut and may land mid-word â€” not worth reproducing the
            // word-boundary trim in T-SQL for legacy rows. New chats get ChatTitle.FromQuestion.
            //
            // MaterialSelectionJson and ContextSnapshot stay empty: empty means "never frozen",
            // and the next question in the chat freezes it against the material as it is then.
            migrationBuilder.Sql(@"
UPDATE s
SET [OwnerId]        = COALESCE(fu.[AuthorId], p.[OwnerId]),
    [Title]          = COALESCE(NULLIF(LTRIM(RTRIM(LEFT(fu.[Text], 60))), ''), N'Chat'),
    [LastMessageAt]  = COALESCE(lm.[CreatedAt], s.[CreatedAt]),
    [Visibility]     = 0,
    [WorkingContext] = 2
FROM [ChatSessions] s
JOIN [Proposals] p ON p.[Id] = s.[ProposalId]
OUTER APPLY (SELECT TOP 1 m.[AuthorId], m.[Text] FROM [ChatMessages] m
             WHERE m.[ChatSessionId] = s.[Id] AND m.[Role] = 0 AND m.[AuthorId] IS NOT NULL
             ORDER BY m.[CreatedAt]) fu
OUTER APPLY (SELECT TOP 1 m2.[CreatedAt] FROM [ChatMessages] m2
             WHERE m2.[ChatSessionId] = s.[Id] ORDER BY m2.[CreatedAt] DESC) lm;");

            migrationBuilder.CreateIndex(
                name: "IX_ChatSessions_OwnerId",
                table: "ChatSessions",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatSessions_ProposalId_LastMessageAt",
                table: "ChatSessions",
                columns: new[] { "ProposalId", "LastMessageAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatSeen_ChatSessionId_UserId",
                table: "ChatSeen",
                columns: new[] { "ChatSessionId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChatSeen_UserId",
                table: "ChatSeen",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_ChatSessions_Users_OwnerId",
                table: "ChatSessions",
                column: "OwnerId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The old shape holds one chat per proposal, so extra chats cannot be represented.
            // Keep the oldest chat of each proposal and drop the rest; their messages cascade.
            migrationBuilder.Sql(@"
WITH ranked AS (
    SELECT [Id], ROW_NUMBER() OVER (PARTITION BY [ProposalId] ORDER BY [CreatedAt], [Id]) AS rn
    FROM [ChatSessions])
DELETE FROM [ChatSessions] WHERE [Id] IN (SELECT [Id] FROM ranked WHERE rn > 1);");

            migrationBuilder.DropForeignKey(
                name: "FK_ChatSessions_Users_OwnerId",
                table: "ChatSessions");

            migrationBuilder.DropTable(
                name: "ChatSeen");

            migrationBuilder.DropIndex(
                name: "IX_ChatSessions_OwnerId",
                table: "ChatSessions");

            migrationBuilder.DropIndex(
                name: "IX_ChatSessions_ProposalId_LastMessageAt",
                table: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "ContextSnapshot",
                table: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "LastMessageAt",
                table: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "MaterialSelectionJson",
                table: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "Visibility",
                table: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "WorkingContext",
                table: "ChatSessions");

            migrationBuilder.CreateIndex(
                name: "IX_ChatSessions_ProposalId",
                table: "ChatSessions",
                column: "ProposalId");
        }
    }
}
