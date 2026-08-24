namespace Saga.Core.Abstractions;

/// <summary>One page (or section) of extracted text, as offsets into the full text.</summary>
public record PageSpan(int Page, int Offset, int Length);

/// <summary>
/// What Content Understanding said the call consumed — the <c>usage</c> object beside <c>result</c>
/// in the analyze response, never anything counted locally. Which counter is filled is decided by
/// the work the service performed, not by the analyzer asked for: a digital Office file bills
/// Minimal even under the layout analyzer, while image-based input (PDF, PNG, a screenshot lifted
/// out of a .docx) bills Standard.
/// </summary>
/// <param name="ContextualizationTokens">
/// Zero for pure content extraction; only a generative analyzer produces any.
/// </param>
public record ExtractionUsage(int MinimalPages, int BasicPages, int StandardPages,
    int ContextualizationTokens = 0)
{
    /// <summary>Nothing billed — a local read rather than a service call.</summary>
    public static readonly ExtractionUsage Free = new(0, 0, 0);
}

/// <param name="Usage">
/// Null means the service reported no usage at all, which is <b>not</b> the same as zero: the call
/// was still billed, we just cannot say for how much. Page geometry is deliberately not a stand-in —
/// Office files come back with none, which is how every Office upload came to be metered as free.
/// </param>
public record ExtractionResult(string Text, IReadOnlyList<PageSpan> Pages, ExtractionUsage? Usage = null);

/// <summary>Extracts plain text (with page positions) from an uploaded client document.</summary>
public interface IDocumentTextExtractor
{
    /// <summary>File extensions this extractor accepts, lowercase with dot (e.g. ".pdf").</summary>
    IReadOnlySet<string> SupportedExtensions { get; }

    Task<ExtractionResult> ExtractAsync(Stream content, string fileName,
        AiCallContext? context = null, CancellationToken ct = default);
}
