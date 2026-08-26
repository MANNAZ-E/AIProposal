using Saga.Core.Abstractions;

namespace Saga.Web;

/// <summary>
/// Display names for the usage enums. Here rather than in each component because the proposal tab
/// and the admin page both name the same services, and two private copies is how one of them ends
/// up saying something the other does not.
/// </summary>
public static class UsageLabels
{
    public static string ServiceName(AiServiceKind service) => service switch
    {
        AiServiceKind.AzureOpenAI => "Azure OpenAI",
        AiServiceKind.ContentUnderstanding => "Azure Content Understanding",
        _ => service.ToString(),
    };

    public static string OperationName(AiOperation operation) => operation switch
    {
        AiOperation.GenerateArtifact => "Generate",
        AiOperation.GenerateContentUnit => "Generate content unit",
        AiOperation.Chat => "Chat",
        AiOperation.ReviewDraft => "Draft review",
        AiOperation.ReviewProposal => "Proposal review",
        AiOperation.ExtractRequirements => "Extract requirements",
        AiOperation.CondenseDocument => "Condense document",
        AiOperation.ExtractDocument => "Extract document",
        AiOperation.DescribeFigure => "Describe figure",
        _ => operation.ToString(),
    };
}
