using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saga.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class BidTeamChat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TeamChatSeen",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamChatSeen", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TeamChatSeen_Proposals_ProposalId",
                        column: x => x.ProposalId,
                        principalTable: "Proposals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TeamChatSeen_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TeamMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuthorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TeamMessages_Proposals_ProposalId",
                        column: x => x.ProposalId,
                        principalTable: "Proposals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TeamMessages_Users_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TeamMessageMentions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TeamMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Start = table.Column<int>(type: "int", nullable: false),
                    Length = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamMessageMentions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TeamMessageMentions_TeamMessages_TeamMessageId",
                        column: x => x.TeamMessageId,
                        principalTable: "TeamMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TeamMessageMentions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TeamChatSeen_ProposalId_UserId",
                table: "TeamChatSeen",
                columns: new[] { "ProposalId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TeamChatSeen_UserId",
                table: "TeamChatSeen",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_TeamMessageMentions_TeamMessageId",
                table: "TeamMessageMentions",
                column: "TeamMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_TeamMessageMentions_UserId",
                table: "TeamMessageMentions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_TeamMessages_AuthorId",
                table: "TeamMessages",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_TeamMessages_ProposalId_CreatedAt",
                table: "TeamMessages",
                columns: new[] { "ProposalId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TeamChatSeen");

            migrationBuilder.DropTable(
                name: "TeamMessageMentions");

            migrationBuilder.DropTable(
                name: "TeamMessages");
        }
    }
}
