using System.Runtime.CompilerServices;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Wordprocessing;
using Saga.Core.Abstractions;
using Saga.Infrastructure.Extraction;

namespace Saga.Tests;

/// <summary>Stands in for the analyzer: answers the document, then each figure, recording both.</summary>
internal sealed class RecordingExtractor : IDocumentTextExtractor
{
    public List<(string FileName, AiCallContext? Context)> Calls { get; } = [];
    public required string DocumentMarkdown { get; init; }
    public IReadOnlyList<PageSpan> Pages { get; init; } = [];
    public Func<int, string> FigureText { get; init; } =
        n => $"Question {n}.01 Do you confirm that the proposed coaches hold the required qualifications?";

    public IReadOnlySet<string> SupportedExtensions
        => new HashSet<string> { ".docx", ".pptx", ".xlsx", ".png", ".jpg" };

    public Task<ExtractionResult> ExtractAsync(Stream content, string fileName,
        AiCallContext? context = null, CancellationToken ct = default)
    {
        lock (Calls) Calls.Add((fileName, context));

        if (!fileName.StartsWith("figure ", StringComparison.Ordinal))
            // The parent .docx is a digital file: Minimal, however many Standard-billed
                // screenshots turn up inside it.
                return Task.FromResult(new ExtractionResult(DocumentMarkdown, Pages,
                    new ExtractionUsage(MinimalPages: 1, BasicPages: 0, StandardPages: 0)));

        var ordinal = int.Parse(Path.GetFileNameWithoutExtension(fileName)["figure ".Length..]);
        return Task.FromResult(new ExtractionResult(FigureText(ordinal),
            [new PageSpan(1, 0, FigureText(ordinal).Length)],
            new ExtractionUsage(MinimalPages: 0, BasicPages: 0, StandardPages: 1)));
    }
}

/// <summary>Records the vision calls and answers with a canned description.</summary>
internal sealed class DescribingAiService : IAiService
{
    public List<AiRequest> Requests { get; } = [];
    public string Answer { get; init; } = "A bar chart rising from 20% in Q1 to 40% in Q3.";

    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        lock (Requests) Requests.Add(request);
        await Task.Yield();
        yield return new AiStreamEvent.Delta(Answer);
        yield return new AiStreamEvent.Completed(100, 20, "fake-model");
    }
}

/// <summary>
/// Content Understanding does not OCR images embedded in Office files — it leaves an empty
/// ![](figures/1.1) where each one stood. Tenders exported from a procurement portal are often
/// nothing but such screenshots, so without this the questions are simply absent. These tests pin
/// down that the images are found in reading order, read once each, and put back where they stood
/// with the page map still pointing at the right pages.
/// </summary>
public class EmbeddedImageTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private const string PIC = "http://schemas.openxmlformats.org/drawingml/2006/picture";

    private static byte[] Image(byte seed, int size = 10_000)
    {
        var bytes = new byte[size];
        Array.Fill(bytes, seed);
        return bytes;
    }

    /// <summary>Builds a .docx whose body references the images in <paramref name="order"/>.</summary>
    private static byte[] BuildDocx(IReadOnlyList<byte[]> images, IReadOnlyList<int>? order = null)
    {
        using var file = new MemoryStream();
        using (var document = WordprocessingDocument.Create(file, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            var ids = new List<string>();
            foreach (var bytes in images)
            {
                var part = main.AddImagePart(ImagePartType.Png);
                using var data = new MemoryStream(bytes);
                part.FeedData(data);
                ids.Add(main.GetIdOfPart(part));
            }
            // The real nesting, not a shortcut: the SDK types elements by their position in the
            // schema, and an a:blip somewhere it cannot legally sit parses as an unknown element.
            var blips = string.Concat((order ?? Enumerable.Range(0, images.Count).ToList())
                .Select(i => $"""
                    <w:p><w:r><w:drawing><wp:inline xmlns:wp="{WP}">
                    <wp:extent cx="100" cy="100" /><wp:docPr id="1" name="image" />
                    <a:graphic><a:graphicData uri="{PIC}"><pic:pic xmlns:pic="{PIC}">
                    <pic:nvPicPr><pic:cNvPr id="0" name="image" /><pic:cNvPicPr /></pic:nvPicPr>
                    <pic:blipFill><a:blip r:embed="{ids[i]}" /><a:stretch><a:fillRect /></a:stretch></pic:blipFill>
                    <pic:spPr />
                    </pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>
                    """));
            main.Document = new Document(
                $"""<w:document xmlns:w="{W}" xmlns:r="{R}" xmlns:a="{A}"><w:body>{blips}</w:body></w:document>""");
        }
        return file.ToArray();
    }

    /// <summary>Builds a .pptx with one image per slide, listed in <paramref name="slideOrder"/>.</summary>
    private static byte[] BuildPptx(IReadOnlyList<byte[]> images, IReadOnlyList<int> slideOrder)
    {
        using var file = new MemoryStream();
        using (var document = PresentationDocument.Create(file, PresentationDocumentType.Presentation))
        {
            var presentation = document.AddPresentationPart();
            var slideIds = new List<string>();
            foreach (var bytes in images)
            {
                var slide = presentation.AddNewPart<SlidePart>();
                var image = slide.AddImagePart(ImagePartType.Png);
                using var data = new MemoryStream(bytes);
                image.FeedData(data);
                slide.Slide = new Slide($"""
                    <p:sld xmlns:p="{P}" xmlns:r="{R}" xmlns:a="{A}"><p:cSld><p:spTree>
                    <p:nvGrpSpPr><p:cNvPr id="1" name="" /><p:cNvGrpSpPr /><p:nvPr /></p:nvGrpSpPr>
                    <p:grpSpPr />
                    <p:pic>
                    <p:nvPicPr><p:cNvPr id="2" name="image" /><p:cNvPicPr /><p:nvPr /></p:nvPicPr>
                    <p:blipFill><a:blip r:embed="{slide.GetIdOfPart(image)}" /><a:stretch><a:fillRect /></a:stretch></p:blipFill>
                    <p:spPr />
                    </p:pic></p:spTree></p:cSld></p:sld>
                    """);
                slideIds.Add(presentation.GetIdOfPart(slide));
            }
            var list = string.Concat(slideOrder.Select((s, i) =>
                $"""<p:sldId id="{256 + i}" r:id="{slideIds[s]}" />"""));
            presentation.Presentation = new Presentation(
                $"""<p:presentation xmlns:p="{P}" xmlns:r="{R}"><p:sldIdLst>{list}</p:sldIdLst></p:presentation>""");
        }
        return file.ToArray();
    }

    private static string Markdown(int figures, string prefix = "**Questions**")
        => prefix + "\n\n" + string.Join("\n\n", Enumerable.Range(1, figures).Select(i => $"![](figures/1.{i})"));

    private static async Task<(ExtractionResult Result, RecordingExtractor Inner, DescribingAiService Ai)>
        ExtractAsync(byte[] file, string fileName, RecordingExtractor inner,
            DescribingAiService? ai = null, EmbeddedImageOptions? options = null)
    {
        ai ??= new DescribingAiService();
        var extractor = new EmbeddedImageTextExtractor(inner, ai, options);
        using var stream = new MemoryStream(file);
        var context = new AiCallContext(Guid.NewGuid(), AiOperation.ExtractDocument, Guid.NewGuid(), Guid.NewGuid());
        return (await extractor.ExtractAsync(stream, fileName, context), inner, ai);
    }

    [Fact]
    public void Docx_images_come_back_in_document_order_not_package_order()
    {
        // Stored as A, B, C; referenced by the body as C, A, B — reading order is what lines an
        // image up with its figure placeholder, so that is what has to come back.
        var file = BuildDocx([Image(1), Image(2), Image(3)], order: [2, 0, 1]);

        using var stream = new MemoryStream(file);
        var images = OfficeImageReader.Read(stream, ".docx");

        Assert.Equal([3, 1, 2], images.Select(i => i.Data[0]));
        Assert.Equal([1, 2, 3], images.Select(i => i.Ordinal));
    }

    [Fact]
    public void Pptx_images_follow_the_slide_list_not_the_part_order()
    {
        var file = BuildPptx([Image(1), Image(2)], slideOrder: [1, 0]);

        using var stream = new MemoryStream(file);
        var images = OfficeImageReader.Read(stream, ".pptx");

        Assert.Equal([2, 1], images.Select(i => i.Data[0]));
    }

    [Fact]
    public async Task Each_screenshot_is_read_back_into_the_place_it_stood()
    {
        var inner = new RecordingExtractor { DocumentMarkdown = Markdown(2) };
        var (result, _, ai) = await ExtractAsync(BuildDocx([Image(1), Image(2)]), "tender.docx", inner);

        Assert.DoesNotContain("![](figures/", result.Text);
        Assert.Contains("<!-- figure 1.1 (embedded image) -->", result.Text);
        Assert.Contains("Question 1.01", result.Text);
        Assert.Contains("Question 2.01", result.Text);
        // The heading that introduced the figures still comes first.
        Assert.True(result.Text.IndexOf("**Questions**", StringComparison.Ordinal)
            < result.Text.IndexOf("Question 1.01", StringComparison.Ordinal));
        Assert.Empty(ai.Requests);
    }

    [Fact]
    public async Task Every_figure_is_a_call_of_its_own_under_the_document_operation()
    {
        var inner = new RecordingExtractor { DocumentMarkdown = Markdown(2) };
        var (_, recording, _) = await ExtractAsync(BuildDocx([Image(1), Image(2)]), "tender.docx", inner);

        Assert.Equal(3, recording.Calls.Count);
        Assert.Equal("tender.docx", recording.Calls[0].FileName);
        // One operation, so the usage page groups the document and its figures together.
        Assert.Single(recording.Calls.Select(c => c.Context!.OperationId).Distinct());
        Assert.Equal(["figure 1.png", "figure 2.png"],
            recording.Calls.Skip(1).Select(c => c.FileName).Order());
        Assert.Contains(recording.Calls, c => c.Context!.Label == "tender.docx – figure 1");
    }

    [Fact]
    public async Task A_repeated_logo_is_read_once_and_a_tiny_one_not_at_all()
    {
        var repeated = Image(7);
        var inner = new RecordingExtractor { DocumentMarkdown = Markdown(3) };
        var (result, recording, _) = await ExtractAsync(
            BuildDocx([repeated, repeated, Image(9, size: 500)]), "deck.docx", inner);

        // Two identical images, one analysis; the 500-byte one is an icon and is not paid for.
        Assert.Equal(2, recording.Calls.Count);
        // The duplicate still gets its text back, and the icon keeps its placeholder.
        Assert.Equal(2, result.Text.Split("Question 1.01").Length - 1);
        Assert.Contains("![](figures/1.3)", result.Text);
    }

    [Fact]
    public async Task Placeholders_found_inside_a_screenshot_do_not_survive_as_links()
    {
        // Reading a screenshot of a form turns its icons into figure placeholders of their own,
        // numbered from 1.1 again — left in place they would collide with the document's own.
        var inner = new RecordingExtractor
        {
            DocumentMarkdown = Markdown(1),
            FigureText = _ => "1.06 Please provide your company profile.\n\n![Attach file...](figures/1.1)",
        };
        var (result, _, ai) = await ExtractAsync(BuildDocx([Image(1)]), "tender.docx", inner);

        Assert.Contains("1.06 Please provide your company profile.", result.Text);
        Assert.Contains("Attach file...", result.Text);
        Assert.DoesNotContain("](figures/", result.Text);
        Assert.Empty(ai.Requests);
    }

    [Fact]
    public async Task A_figure_with_no_readable_text_is_described_instead()
    {
        var inner = new RecordingExtractor { DocumentMarkdown = Markdown(1), FigureText = _ => "<!-- PageBreak -->" };
        var ai = new DescribingAiService();
        var (result, _, _) = await ExtractAsync(BuildDocx([Image(1)]), "tender.docx", inner, ai);

        var request = Assert.Single(ai.Requests);
        Assert.Equal(AiOperation.DescribeFigure, request.Context!.Operation);
        Assert.Equal(AiModelTier.Light, request.Tier);
        // System prompt, then the material the image rides on, then the instruction — the order
        // every paid call is assembled in.
        Assert.Contains("You describe figures", request.SystemPrompt);
        Assert.Single(request.Messages[0].Images!);
        Assert.Null(request.Messages[^1].Images);
        Assert.Contains("*Figure description:* A bar chart rising", result.Text);
    }

    [Fact]
    public async Task A_figure_the_model_calls_empty_keeps_its_placeholder()
    {
        var inner = new RecordingExtractor { DocumentMarkdown = Markdown(1), FigureText = _ => "" };
        var ai = new DescribingAiService { Answer = "NO CONTENT" };
        var (result, _, _) = await ExtractAsync(BuildDocx([Image(1)]), "tender.docx", inner, ai);

        Assert.Contains("![](figures/1.1)", result.Text);
    }

    [Fact]
    public async Task Later_pages_still_slice_correctly_after_a_splice()
    {
        var page1 = "Page one.\n\n![](figures/1.1)";
        var page2 = "\n\nPage two, untouched.";
        var inner = new RecordingExtractor
        {
            DocumentMarkdown = page1 + page2,
            Pages = [new PageSpan(1, 0, page1.Length), new PageSpan(2, page1.Length, page2.Length)],
        };

        var (result, _, _) = await ExtractAsync(BuildDocx([Image(1)]), "tender.docx", inner);

        var pages = result.Pages.OrderBy(p => p.Offset).ToList();
        Assert.Equal("Page two, untouched.", result.Text.Substring(pages[1].Offset, pages[1].Length).Trim());
        // The recovered text is charged to the page whose figure it replaced, or the chunker —
        // which reads only what the spans cover — would never see it.
        Assert.Contains("Question 1.01", result.Text.Substring(pages[0].Offset, pages[0].Length));
    }

    [Fact]
    public async Task Mismatched_counts_append_rather_than_guess_at_positions()
    {
        // Three placeholders, two images: something in the document did not become a figure, so
        // the n-th image is no longer the n-th placeholder and placing them would be a guess.
        var inner = new RecordingExtractor { DocumentMarkdown = Markdown(3) };
        var (result, _, _) = await ExtractAsync(BuildDocx([Image(1), Image(2)]), "tender.docx", inner);

        Assert.Contains("![](figures/1.1)", result.Text);
        Assert.Contains("## Text recovered from embedded images", result.Text);
        Assert.Contains("Question 1.01", result.Text);
        Assert.Contains("Question 2.01", result.Text);
    }

    [Fact]
    public async Task A_document_without_figures_is_left_exactly_as_extracted()
    {
        var inner = new RecordingExtractor { DocumentMarkdown = "Plain tender text, no pictures." };
        var (result, recording, ai) = await ExtractAsync(BuildDocx([Image(1)]), "tender.docx", inner);

        Assert.Equal("Plain tender text, no pictures.", result.Text);
        Assert.Single(recording.Calls);
        Assert.Empty(ai.Requests);
    }

    [Fact]
    public async Task A_pdf_never_reaches_the_office_reader()
    {
        var inner = new RecordingExtractor { DocumentMarkdown = Markdown(2) };
        // Not a valid package at all: a PDF must pass straight through untouched.
        var (result, recording, _) = await ExtractAsync([1, 2, 3], "tender.pdf", inner);

        Assert.Equal(Markdown(2), result.Text);
        Assert.Single(recording.Calls);
    }
}
