using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saga.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AiUsageRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Renamed rather than recreated: GenerationRuns holds the real generation history and
            // is worth keeping, even though every existing row has cost 0 (no prices were ever
            // configured). Scaffolding would have dropped the table.
            migrationBuilder.DropForeignKey(name: "FK_GenerationRuns_Proposals_ProposalId", table: "GenerationRuns");
            migrationBuilder.DropForeignKey(name: "FK_GenerationRuns_Users_StartedById", table: "GenerationRuns");
            migrationBuilder.DropIndex(name: "IX_GenerationRuns_ProposalId", table: "GenerationRuns");
            migrationBuilder.DropIndex(name: "IX_GenerationRuns_StartedById", table: "GenerationRuns");
            migrationBuilder.DropPrimaryKey(name: "PK_GenerationRuns", table: "GenerationRuns");

            migrationBuilder.RenameTable(name: "GenerationRuns", newName: "AiUsage");

            migrationBuilder.RenameColumn(name: "PromptTokens", table: "AiUsage", newName: "InputTokens");
            migrationBuilder.RenameColumn(name: "CompletionTokens", table: "AiUsage", newName: "OutputTokens");
            migrationBuilder.RenameColumn(name: "EstimatedCost", table: "AiUsage", newName: "EstimatedCostUsd");

            // Nullable so a future call that belongs to no proposal still has somewhere to land.
            migrationBuilder.AlterColumn<Guid>(
                name: "ProposalId",
                table: "AiUsage",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<Guid>(
                name: "OperationId", table: "AiUsage", type: "uniqueidentifier",
                nullable: false, defaultValue: Guid.Empty);
            migrationBuilder.AddColumn<int>(
                name: "Service", table: "AiUsage", type: "int", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<int>(
                name: "Operation", table: "AiUsage", type: "int", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<string>(
                name: "Label", table: "AiUsage", type: "nvarchar(256)", maxLength: 256, nullable: true);
            migrationBuilder.AddColumn<int>(
                name: "CachedInputTokens", table: "AiUsage", type: "int", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<int>(
                name: "PageCount", table: "AiUsage", type: "int", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<string>(
                name: "ErrorMessage", table: "AiUsage", type: "nvarchar(1024)", maxLength: 1024, nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "RequestText", table: "AiUsage", type: "nvarchar(max)", nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "ResponseText", table: "AiUsage", type: "nvarchar(max)", nullable: true);

            // Every existing row is a single-call Azure OpenAI generation, so it is its own
            // operation; the artifact it produced tells us which operation kind it was.
            migrationBuilder.Sql("""
                UPDATE [AiUsage]
                SET [OperationId] = [Id],
                    [Service] = 0,
                    [Operation] = CASE
                        WHEN [ArtifactType] IS NULL THEN 2  -- chat runs -> Chat
                        WHEN [ArtifactType] = 2 THEN 5      -- Requirements -> ExtractRequirements
                        WHEN [ArtifactType] = 6 THEN 1      -- Content -> GenerateContentUnit
                        WHEN [ArtifactType] = 7 THEN 3      -- Review -> ReviewDraft
                        ELSE 0                              -- everything else -> GenerateArtifact
                    END;
                """);

            migrationBuilder.AddPrimaryKey(name: "PK_AiUsage", table: "AiUsage", column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsage_OperationId", table: "AiUsage", column: "OperationId");
            migrationBuilder.CreateIndex(
                name: "IX_AiUsage_ProposalId_StartedAt", table: "AiUsage",
                columns: new[] { "ProposalId", "StartedAt" });
            migrationBuilder.CreateIndex(
                name: "IX_AiUsage_StartedById", table: "AiUsage", column: "StartedById");

            migrationBuilder.AddForeignKey(
                name: "FK_AiUsage_Proposals_ProposalId",
                table: "AiUsage",
                column: "ProposalId",
                principalTable: "Proposals",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
            migrationBuilder.AddForeignKey(
                name: "FK_AiUsage_Users_StartedById",
                table: "AiUsage",
                column: "StartedById",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(name: "FK_AiUsage_Proposals_ProposalId", table: "AiUsage");
            migrationBuilder.DropForeignKey(name: "FK_AiUsage_Users_StartedById", table: "AiUsage");
            migrationBuilder.DropIndex(name: "IX_AiUsage_OperationId", table: "AiUsage");
            migrationBuilder.DropIndex(name: "IX_AiUsage_ProposalId_StartedAt", table: "AiUsage");
            migrationBuilder.DropIndex(name: "IX_AiUsage_StartedById", table: "AiUsage");
            migrationBuilder.DropPrimaryKey(name: "PK_AiUsage", table: "AiUsage");

            // Content Understanding rows have no place in the old shape, and rows without a
            // proposal cannot satisfy the old non-null FK.
            migrationBuilder.Sql("DELETE FROM [AiUsage] WHERE [Service] <> 0 OR [ProposalId] IS NULL;");

            migrationBuilder.DropColumn(name: "OperationId", table: "AiUsage");
            migrationBuilder.DropColumn(name: "Service", table: "AiUsage");
            migrationBuilder.DropColumn(name: "Operation", table: "AiUsage");
            migrationBuilder.DropColumn(name: "Label", table: "AiUsage");
            migrationBuilder.DropColumn(name: "CachedInputTokens", table: "AiUsage");
            migrationBuilder.DropColumn(name: "PageCount", table: "AiUsage");
            migrationBuilder.DropColumn(name: "ErrorMessage", table: "AiUsage");
            migrationBuilder.DropColumn(name: "RequestText", table: "AiUsage");
            migrationBuilder.DropColumn(name: "ResponseText", table: "AiUsage");

            migrationBuilder.AlterColumn<Guid>(
                name: "ProposalId",
                table: "AiUsage",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: Guid.Empty,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.RenameColumn(name: "InputTokens", table: "AiUsage", newName: "PromptTokens");
            migrationBuilder.RenameColumn(name: "OutputTokens", table: "AiUsage", newName: "CompletionTokens");
            migrationBuilder.RenameColumn(name: "EstimatedCostUsd", table: "AiUsage", newName: "EstimatedCost");

            migrationBuilder.RenameTable(name: "AiUsage", newName: "GenerationRuns");

            migrationBuilder.AddPrimaryKey(name: "PK_GenerationRuns", table: "GenerationRuns", column: "Id");
            migrationBuilder.CreateIndex(
                name: "IX_GenerationRuns_ProposalId", table: "GenerationRuns", column: "ProposalId");
            migrationBuilder.CreateIndex(
                name: "IX_GenerationRuns_StartedById", table: "GenerationRuns", column: "StartedById");

            migrationBuilder.AddForeignKey(
                name: "FK_GenerationRuns_Proposals_ProposalId",
                table: "GenerationRuns",
                column: "ProposalId",
                principalTable: "Proposals",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
            migrationBuilder.AddForeignKey(
                name: "FK_GenerationRuns_Users_StartedById",
                table: "GenerationRuns",
                column: "StartedById",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
