using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Saga.Core.Abstractions;
using Saga.Core.Domain;
using Saga.Infrastructure.Data;

namespace Saga.Infrastructure.Ai;

/// <summary>
/// Wraps the real (or fake) <see cref="IAiService"/> and records one <see cref="AiUsageRecord"/>
/// per call, so the services that generate content no longer carry logging boilerplate.
/// Requests without an <see cref="AiCallContext"/> pass straight through unmetered.
/// </summary>
public class UsageTrackingAiService(
    IAiService inner,
    IDbContextFactory<SagaDbContext> dbFactory,
    PricingService pricing,
    ILogger<UsageTrackingAiService>? logger = null) : IAiService
{
    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (request.Context is null)
        {
            await foreach (var passthrough in inner.StreamAsync(request, ct))
                yield return passthrough;
            yield break;
        }

        var record = new AiUsageRecord
        {
            Id = Guid.NewGuid(),
            OperationId = request.Context.OperationId,
            ProposalId = request.Context.ProposalId,
            Service = AiServiceKind.AzureOpenAI,
            Model = "",
            Operation = request.Context.Operation,
            ArtifactType = request.Context.ArtifactType,
            Label = Truncate(request.Context.Label, 256),
            InstructionText = request.Context.InstructionText,
            StartedById = request.Context.UserId,
            StartedAt = DateTimeOffset.UtcNow,
            Outcome = GenerationOutcome.Failed,
            RequestText = pricing.CapturePayloads ? Render(request) : null,
        };

        var stopwatch = Stopwatch.StartNew();
        var text = new StringBuilder();

        // The stream is consumed by hand because C# forbids `yield return` inside a try/catch,
        // and the outcome of a failed or cancelled call still has to be recorded.
        var events = inner.StreamAsync(request, ct).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                AiStreamEvent evt;
                try
                {
                    if (!await events.MoveNextAsync()) break;
                    evt = events.Current;
                }
                catch (OperationCanceledException)
                {
                    record.Outcome = GenerationOutcome.Cancelled;
                    throw;
                }
                catch (Exception ex)
                {
                    record.Outcome = GenerationOutcome.Failed;
                    record.ErrorMessage = Truncate(ex.Message, 1024);
                    throw;
                }

                switch (evt)
                {
                    case AiStreamEvent.Delta d:
                        text.Append(d.Text);
                        break;
                    case AiStreamEvent.Completed c:
                        record.InputTokens = c.PromptTokens;
                        record.OutputTokens = c.CompletionTokens;
                        record.CachedInputTokens = c.CachedPromptTokens;
                        record.Model = c.Model;
                        record.EstimatedCostUsd = pricing.EstimateLlmUsd(c.Model, c.PromptTokens, c.CompletionTokens);
                        record.Outcome = GenerationOutcome.Succeeded;
                        break;
                }

                yield return evt;
            }
        }
        finally
        {
            await events.DisposeAsync();
            record.Duration = stopwatch.Elapsed;
            if (pricing.CapturePayloads)
                record.ResponseText = text.ToString();
            await SaveAsync(record);
        }
    }

    /// <summary>The prompt as sent, flattened so a call can be read back and reconstructed.</summary>
    private static string Render(AiRequest request)
    {
        var sb = new StringBuilder();
        sb.Append("[system]\n").Append(request.SystemPrompt);
        foreach (var message in request.Messages)
            sb.Append("\n\n[").Append(message.Role).Append("]\n").Append(message.Content);
        return sb.ToString();
    }

    private static string? Truncate(string? value, int max)
        => value is not null && value.Length > max ? value[..max] : value;

    /// <summary>Never lets a metering failure break the generation it was measuring.</summary>
    private async Task SaveAsync(AiUsageRecord record)
    {
        try
        {
            // CancellationToken.None: a cancelled or failed call is exactly the one worth keeping.
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            db.AiUsage.Add(record);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to record AI usage for operation {OperationId}.", record.OperationId);
        }
    }
}
