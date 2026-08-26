using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Saga.Core.Abstractions;
using Saga.Core.Prompts;

namespace Saga.Infrastructure.Extraction;

/// <param name="MinBytes">Below this an image is a bullet, icon or logo chip, not content.</param>
/// <param name="MaxImages">Ceiling on paid calls per document; the excess is logged, not hidden.</param>
/// <param name="MinTextChars">
/// OCR output shorter than this means the picture is not a screenshot of text — a chart, diagram or
/// photo — and is worth a look from the vision model instead.
/// </param>
public record EmbeddedImageOptions(
    int MinBytes = 8 * 1024,
    int MaxImages = 40,
    int MinTextChars = 40,
    int Concurrency = 4);

/// <summary>
/// Recovers the text inside images embedded in Word, PowerPoint and Excel uploads.
/// <para>
/// Content Understanding runs OCR over a PDF or an image upload, but takes the native
/// digital-extraction path for Office formats and never looks inside embedded bitmaps — it leaves an
/// empty <c>![](figures/1.1)</c> where each one stood. Tenders exported from a procurement portal are
/// routinely nothing but such screenshots, so the questions vanish entirely.
/// </para>
/// <para>
/// So each embedded image is handed back to the same analyzer as an image, where OCR does run, and
/// the result is spliced over its placeholder. This sits <em>outside</em>
/// <see cref="UsageTrackingTextExtractor"/> on purpose: every per-image call is then metered as its
/// own row, sharing the parent operation, rather than disappearing into the document row.
/// </para>
/// </summary>
public partial class EmbeddedImageTextExtractor(
    IDocumentTextExtractor inner,
    IAiService ai,
    EmbeddedImageOptions? options = null,
    ILogger<EmbeddedImageTextExtractor>? logger = null) : IDocumentTextExtractor
{
    private readonly EmbeddedImageOptions _options = options ?? new EmbeddedImageOptions();

    /// <summary>Media types worth reading, mapped to the extension the inner extractor routes on.</summary>
    private static readonly Dictionary<string, string> ReadableTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/jpg"] = ".jpg",
        ["image/bmp"] = ".bmp",
        ["image/x-ms-bmp"] = ".bmp",
        ["image/tiff"] = ".tiff",
        ["image/heif"] = ".heif",
    };

    public IReadOnlySet<string> SupportedExtensions => inner.SupportedExtensions;

    public async Task<ExtractionResult> ExtractAsync(Stream content, string fileName,
        AiCallContext? context = null, CancellationToken ct = default)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!OfficeImageReader.Extensions.Contains(extension))
            return await inner.ExtractAsync(content, fileName, context, ct);

        // Buffered once: the analyzer reads the file, then the OpenXML reader reads it again.
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);

        buffer.Position = 0;
        var result = await inner.ExtractAsync(buffer, fileName, context, ct);

        // No placeholders means the document had no figures — or an analyzer that does not mark
        // them — and there is nothing to splice into either way.
        var placeholders = FigureSplicer.CountPlaceholders(result.Text);
        if (placeholders == 0) return result;

        IReadOnlyList<EmbeddedImage> images;
        try
        {
            buffer.Position = 0;
            images = OfficeImageReader.Read(buffer, extension);
        }
        catch (Exception ex)
        {
            // A malformed or password-protected package must not fail an upload that already
            // extracted its native text successfully.
            logger?.LogWarning(ex, "Could not read embedded images from {File}", Path.GetFileName(fileName));
            return result;
        }
        if (images.Count == 0) return result;

        if (images.Count != placeholders)
            logger?.LogWarning(
                "{File} holds {Images} embedded images but {Placeholders} figure placeholders; the "
                + "recovered text will be appended instead of placed.", Path.GetFileName(fileName),
                images.Count, placeholders);

        var texts = await RecoverAsync(images, fileName, context, ct);
        if (texts.All(string.IsNullOrWhiteSpace)) return result;

        return FigureSplicer.Splice(result, texts);
    }

    /// <summary>
    /// Reads each image once and returns the recovered text per image, aligned with
    /// <paramref name="images"/> — a null entry leaves that placeholder as it was.
    /// </summary>
    private async Task<IReadOnlyList<string?>> RecoverAsync(IReadOnlyList<EmbeddedImage> images,
        string fileName, AiCallContext? context, CancellationToken ct)
    {
        // Decided up front and in order, so the same document always spends the same way: identical
        // images (a logo repeated on every slide) are read once, and the budget goes to the earliest.
        var selected = new Dictionary<string, EmbeddedImage>();
        var budget = _options.MaxImages;
        var skipped = 0;
        foreach (var image in images)
        {
            if (!ReadableTypes.ContainsKey(image.MediaType)) continue;
            if (image.Data.Length < _options.MinBytes) continue;
            if (selected.ContainsKey(image.Hash)) continue;
            if (budget-- <= 0) { skipped++; continue; }
            selected[image.Hash] = image;
        }
        if (skipped > 0)
            logger?.LogWarning("{File} holds more embedded images than the {Max} allowed per document; "
                + "{Skipped} were left unread.", Path.GetFileName(fileName), _options.MaxImages, skipped);

        var recovered = new Dictionary<string, string?>();
        using var limit = new SemaphoreSlim(_options.Concurrency);
        await Task.WhenAll(selected.Values.Select(async image =>
        {
            await limit.WaitAsync(ct);
            try
            {
                var text = await RecoverOneAsync(image, fileName, context, ct);
                lock (recovered) recovered[image.Hash] = text;
            }
            finally
            {
                limit.Release();
            }
        }));

        return images.Select(i => recovered.GetValueOrDefault(i.Hash)).ToList();
    }

    private async Task<string?> RecoverOneAsync(EmbeddedImage image, string fileName,
        AiCallContext? context, CancellationToken ct)
    {
        var label = $"{Path.GetFileName(fileName)} – figure {image.Ordinal}";
        var text = "";
        try
        {
            using var stream = new MemoryStream(image.Data, writable: false);
            var ocr = await inner.ExtractAsync(stream,
                $"figure {image.Ordinal}{ReadableTypes[image.MediaType]}",
                context is null ? null : context with { Label = label }, ct);
            text = Flatten(ocr.Text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One unreadable screenshot is not worth failing the upload over; the placeholder stays.
            logger?.LogWarning(ex, "Could not read text from {Label}", label);
        }

        if (VisibleLength(text) >= _options.MinTextChars)
            return text;

        // Little or no text: a chart, diagram or photo rather than a screenshot, so ask what it shows.
        return await DescribeAsync(image, label, context, ct);
    }

    private async Task<string?> DescribeAsync(EmbeddedImage image, string label,
        AiCallContext? context, CancellationToken ct)
    {
        try
        {
            var request = new AiRequest(
                FigurePrompts.SystemPrompt,
                [
                    AiMessage.User(FigurePrompts.MaterialMessage,
                        [new AiImage(image.Data, image.MediaType)]),
                    AiMessage.User(FigurePrompts.Instruction),
                ],
                AiModelTier.Light,
                context is null ? null : context with
                {
                    Operation = AiOperation.DescribeFigure,
                    Label = label,
                });
            var completion = await ai.CompleteAsync(request, ct);

            var description = completion.Text.Trim();
            if (description.Length == 0 ||
                description.StartsWith(FigurePrompts.NoContentMarker, StringComparison.OrdinalIgnoreCase))
                return null;

            // Marked as a description so a later model does not read it as the client's own wording.
            return $"*Figure description:* {description}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Could not describe {Label}", label);
            return null;
        }
    }

    /// <summary>
    /// Analysing a screenshot turns the icons and buttons inside it into figure placeholders of its
    /// own, pointing at images nobody will ever fetch — and at figure numbers that collide with the
    /// document's. What the analyzer read off them is kept; the dangling link is dropped.
    /// </summary>
    private static string Flatten(string markdown)
        => NestedFigureRegex().Replace(markdown, m => m.Groups["alt"].Value);

    /// <summary>
    /// How much text the OCR actually found, ignoring the scaffolding Content Understanding wraps
    /// around it — an empty result comes back as little more than a page-header comment.
    /// </summary>
    private static int VisibleLength(string markdown)
        => CommentRegex().Replace(markdown, "").Count(c => !char.IsWhiteSpace(c));

    [GeneratedRegex(@"!\[(?<alt>[^\]]*)\]\(figures/[0-9.]+\)")]
    private static partial Regex NestedFigureRegex();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();
}
