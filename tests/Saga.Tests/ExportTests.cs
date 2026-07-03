using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Saga.Core.Domain;
using Saga.Core.Models;
using Saga.Infrastructure.Export;
using Saga.Infrastructure.Services;

namespace Saga.Tests;

public class ExportTests(LocalDbFixture db) : IClassFixture<LocalDbFixture>
{
    private readonly ProposalService _proposals = new(db);
    private readonly UserService _users = new(db);
    private readonly ExportService _export = new(db);

    private static (Proposal Proposal, StructurePayload Structure, ContentPayload Content) Sample()
    {
        var proposal = new Proposal
        {
            Id = Guid.NewGuid(),
            Title = "Leadership Development Program",
            ClientName = "ACME A/S",
            Description = "A proposal for 120 managers.",
            OutputFormat = OutputFormat.PowerPoint,
        };
        var structure = new StructurePayload
        {
            Items =
            [
                new StructureItem { Title = "Understanding your situation", KeyMessage = "We know where you are" },
                new StructureItem { Title = "Our proposed approach", KeyMessage = "Phased and practical" },
                new StructureItem { Title = "Deliverables", KeyMessage = "Concrete outcomes" },
            ],
        };
        var content = new ContentPayload
        {
            Units =
            [
                new ContentUnit
                {
                    StructureItemId = structure.Items[0].Id,
                    Title = "Understanding your situation",
                    KeyMessage = "We know where you are",
                    BodyMarkdown = "- **120 managers** across three units\n- Strategy demands new leadership\n\nSome *prose* too.",
                },
                new ContentUnit
                {
                    StructureItemId = structure.Items[1].Id,
                    Title = "Our proposed approach",
                    BodyMarkdown = "1. Discover\n2. Design\n3. Deliver",
                },
                // structure.Items[2] intentionally has no unit -> placeholder slide.
            ],
        };
        return (proposal, structure, content);
    }

    private static void AssertNoValidationErrors(IEnumerable<ValidationErrorInfo> errors)
    {
        var list = errors.Take(5).Select(e => $"{e.ErrorType}: {e.Description}").ToList();
        Assert.True(list.Count == 0, string.Join("\n", list));
    }

    [Fact]
    public void Pptx_output_is_valid_openxml_with_one_slide_per_structure_entry()
    {
        var (proposal, structure, content) = Sample();
        var bytes = PptxExporter.Build(proposal, structure, content);

        using var stream = new MemoryStream(bytes);
        using var pptx = PresentationDocument.Open(stream, false);
        AssertNoValidationErrors(new OpenXmlValidator().Validate(pptx));

        var slideCount = pptx.PresentationPart!.SlideParts.Count();
        Assert.Equal(1 + structure.Items.Count, slideCount); // title + one per entry

        // Slide text contains the titles and body lines.
        var allText = string.Join(" ", pptx.PresentationPart.SlideParts
            .Select(s => s.Slide.InnerText));
        Assert.Contains("Leadership Development Program", allText);
        Assert.Contains("Understanding your situation", allText);
        Assert.Contains("120 managers across three units", allText); // markdown markers stripped
        Assert.Contains("No content generated", allText); // placeholder for the uncovered entry
    }

    [Fact]
    public void Docx_output_is_valid_openxml_with_heading_hierarchy()
    {
        var (proposal, structure, content) = Sample();
        var bytes = DocxExporter.Build(proposal, structure, content);

        using var stream = new MemoryStream(bytes);
        using var docx = WordprocessingDocument.Open(stream, false);
        AssertNoValidationErrors(new OpenXmlValidator().Validate(docx));

        var text = docx.MainDocumentPart!.Document.InnerText;
        Assert.Contains("Leadership Development Program", text);
        Assert.Contains("ACME A/S", text);
        Assert.Contains("Our proposed approach", text);
        Assert.Contains("Discover", text);
    }

    [Fact]
    public void Markdown_lite_flattens_bullets_and_strips_emphasis()
    {
        var blocks = MarkdownLite.Blocks("## Heading\n- **Bold** bullet\n  - nested\n1. numbered\n\nplain *text*").ToList();

        Assert.Equal(5, blocks.Count);
        Assert.Equal(("Heading", false), (blocks[0].Text, blocks[0].IsBullet));
        Assert.Equal(("Bold bullet", true), (blocks[1].Text, blocks[1].IsBullet));
        Assert.True(blocks[2].IsBullet && blocks[2].Level > 0);
        Assert.Equal(("numbered", true), (blocks[3].Text, blocks[3].IsBullet));
        Assert.Equal(("plain text", false), (blocks[4].Text, blocks[4].IsBullet));
    }

    [Fact]
    public async Task Export_service_enforces_content_and_membership()
    {
        var elv = (await _users.FindByEmailAsync("elv@mannaz.com"))!.Id;
        var proposalId = await _proposals.CreateAsync(elv, "Empty", "C", null, OutputFormat.PowerPoint);

        // No structure/content yet.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _export.ExportAsync(proposalId, elv, OutputFormat.PowerPoint));
        var readiness = await _export.GetReadinessAsync(proposalId, elv);
        Assert.False(readiness.CanExport);

        // Outsiders are rejected.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _export.ExportAsync(proposalId, Guid.NewGuid(), OutputFormat.Word));
    }
}
