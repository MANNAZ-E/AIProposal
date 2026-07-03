using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Saga.Core.Abstractions;
using Saga.Core.Domain;
using Saga.Core.Pipeline;
using Saga.Core.Prompts;
using Saga.Infrastructure.Data;

namespace Saga.Infrastructure.Services;

/// <summary>
/// Proposal chat (spec: Q&amp;A only in v1). Answers stream against the user-selected
/// Working Context; every exchange is persisted and every run logged for the usage page.
/// Readers can chat — chatting never modifies the proposal.
/// </summary>
public class ChatService(
    IDbContextFactory<SagaDbContext> dbFactory,
    IAiService ai,
    WorkingContextService contextService,
    IConfiguration configuration)
{
    /// <summary>How many previous messages travel with each question as conversation history.</summary>
    private const int HistoryWindow = 20;

    public async Task<List<ChatMessage>> GetMessagesAsync(Guid proposalId, Guid userId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Reader, ct);
        return await db.ChatMessages
            .Include(m => m.Author)
            .Where(m => m.ChatSession!.ProposalId == proposalId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Asks a question against the selected working context, streaming the answer via
    /// <paramref name="onDelta"/>. Both question and answer are persisted.
    /// </summary>
    public async Task<ChatMessage> AskAsync(Guid proposalId, Guid userId, string question,
        WorkingContextKind kind, Func<string, Task>? onDelta = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new InvalidOperationException("Ask a question first.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Reader, ct);

        var proposal = await db.Proposals.FirstAsync(p => p.Id == proposalId, ct);
        var session = await db.ChatSessions.FirstOrDefaultAsync(s => s.ProposalId == proposalId, ct);
        if (session is null)
        {
            session = new ChatSession
            {
                Id = Guid.NewGuid(),
                ProposalId = proposalId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.ChatSessions.Add(session);
        }

        var history = await db.ChatMessages
            .Where(m => m.ChatSession!.ProposalId == proposalId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(HistoryWindow)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

        // The question is saved regardless of whether the answer succeeds.
        var userMessage = new ChatMessage
        {
            Id = Guid.NewGuid(),
            ChatSessionId = session.Id,
            Role = ChatRole.User,
            Text = question.Trim(),
            WorkingContext = kind,
            AuthorId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.ChatMessages.Add(userMessage);
        await db.SaveChangesAsync(ct);

        var loaded = await contextService.LoadAsync(proposalId, null, ct);
        var context = WorkingContextBuilder.Build(kind, loaded.Documents, loaded.Artifacts,
            useCondensedDocuments: loaded.UseCondensed);

        var messages = new List<AiMessage>
        {
            AiMessage.User($"<working_context>\n{context}\n</working_context>"),
            AiMessage.Assistant("Understood. I will answer questions strictly from this context."),
        };
        foreach (var m in history)
            messages.Add(m.Role == ChatRole.User ? AiMessage.User(m.Text) : AiMessage.Assistant(m.Text));
        messages.Add(AiMessage.User(userMessage.Text));

        var request = new AiRequest(ChatPrompts.BuildSystemPrompt(proposal, kind), messages);

        var run = new GenerationRun
        {
            Id = Guid.NewGuid(),
            ProposalId = proposalId,
            ArtifactType = null,
            Model = "",
            StartedById = userId,
            StartedAt = DateTimeOffset.UtcNow,
            Outcome = GenerationOutcome.Failed,
        };

        var stopwatch = Stopwatch.StartNew();
        var text = new System.Text.StringBuilder();
        try
        {
            await foreach (var evt in ai.StreamAsync(request, ct))
            {
                switch (evt)
                {
                    case AiStreamEvent.Delta d:
                        text.Append(d.Text);
                        if (onDelta is not null) await onDelta(d.Text);
                        break;
                    case AiStreamEvent.Completed c:
                        run.PromptTokens = c.PromptTokens;
                        run.CompletionTokens = c.CompletionTokens;
                        run.Model = c.Model;
                        run.EstimatedCost = Ai.UsageCost.Estimate(configuration, request.Tier,
                            c.PromptTokens, c.CompletionTokens);
                        run.Outcome = GenerationOutcome.Succeeded;
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            run.Outcome = GenerationOutcome.Cancelled;
            throw;
        }
        finally
        {
            run.Duration = stopwatch.Elapsed;
            db.GenerationRuns.Add(run);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        var answer = new ChatMessage
        {
            Id = Guid.NewGuid(),
            ChatSessionId = session.Id,
            Role = ChatRole.Assistant,
            Text = text.ToString().Trim(),
            WorkingContext = kind,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.ChatMessages.Add(answer);
        await db.SaveChangesAsync(CancellationToken.None);
        return answer;
    }
}
