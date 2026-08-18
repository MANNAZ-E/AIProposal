using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Saga.Core.Domain;
using Saga.Core.Models;

namespace Saga.Infrastructure.Export;

/// <summary>
/// Word export: title page, one heading per structure entry, content prose per section.
/// Clean Saga styling (#5A616D) until Emil's sample Mannaz document arrives to mimic.
/// </summary>
public static class DocxExporter
{
    private const string ThemeColor = "5A616D";

    public static byte[] Build(Proposal proposal, StructurePayload structure, ContentPayload content)
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document();
            var body = main.Document.AppendChild(new Body());
            AddStyles(main);

            // Title page
            body.AppendChild(Paragraph(proposal.Title, "SagaTitle"));
            body.AppendChild(Paragraph(proposal.ClientName, "SagaSubtitle"));
            if (!string.IsNullOrWhiteSpace(proposal.Description))
                body.AppendChild(Paragraph(proposal.Description, "SagaSubtitle"));
            body.AppendChild(Paragraph($"Mannaz · {DateTime.Today:d MMMM yyyy}", "SagaSubtitle"));
            body.AppendChild(new Paragraph(new Run(new Break { Type = BreakValues.Page })));

            // Sections follow the approved structure; bodies come from the content units.
            var unitsByStructureId = content.Units
                .Where(u => u.StructureItemId != Guid.Empty)
                .GroupBy(u => u.StructureItemId)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var item in structure.Items)
            {
                body.AppendChild(Paragraph(item.Title, "SagaHeading1"));
                var unit = unitsByStructureId.GetValueOrDefault(item.Id)
                    ?? content.Units.FirstOrDefault(u => u.Title == item.Title);
                if (unit is null) continue;

                if (!string.IsNullOrWhiteSpace(unit.KeyMessage))
                    body.AppendChild(Paragraph(unit.KeyMessage, "SagaKeyMessage"));
                foreach (var block in MarkdownLite.Blocks(unit.BodyMarkdown))
                    body.AppendChild(block.IsBullet
                        ? BulletParagraph(main, block.Text)
                        : Paragraph(block.Text, "SagaBody"));
            }

            main.Document.Save();
        }
        return stream.ToArray();
    }

    private static Paragraph Paragraph(string text, string styleId)
        => new(new ParagraphProperties(new ParagraphStyleId { Val = styleId }), new Run(new Text(text)));

    private static Paragraph BulletParagraph(MainDocumentPart main, string text)
        => new(
            new ParagraphProperties(
                new ParagraphStyleId { Val = "SagaBody" },
                new NumberingProperties(
                    new NumberingLevelReference { Val = 0 },
                    new NumberingId { Val = 1 })),
            new Run(new Text(text)));

    private static void AddStyles(MainDocumentPart main)
    {
        var styles = main.AddNewPart<StyleDefinitionsPart>();
        styles.Styles = new Styles(
            Style("SagaTitle", size: 56, bold: true, color: ThemeColor, spacingAfter: 240),
            Style("SagaSubtitle", size: 28, bold: false, color: "6C7280", spacingAfter: 120),
            Style("SagaHeading1", size: 32, bold: true, color: ThemeColor, spacingAfter: 160, outlineLevel: 0),
            Style("SagaKeyMessage", size: 24, bold: false, color: "6C7280", spacingAfter: 160, italic: true),
            Style("SagaBody", size: 22, bold: false, color: "24272C", spacingAfter: 120));
        styles.Styles.Save();

        var numbering = main.AddNewPart<NumberingDefinitionsPart>();
        numbering.Numbering = new Numbering(
            new AbstractNum(
                new Level(
                    new NumberingFormat { Val = NumberFormatValues.Bullet },
                    new LevelText { Val = "•" },
                    new ParagraphProperties(new Indentation { Left = "720", Hanging = "360" }))
                { LevelIndex = 0 })
            { AbstractNumberId = 1 },
            new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 1 });
        numbering.Numbering.Save();
    }

    private static Style Style(string id, int size, bool bold, string color, int spacingAfter,
        int? outlineLevel = null, bool italic = false)
    {
        // Schema order matters: rFonts, b, i, color, sz.
        var props = new StyleRunProperties();
        props.AppendChild(new RunFonts { Ascii = "Segoe UI", HighAnsi = "Segoe UI" });
        if (bold) props.AppendChild(new Bold());
        if (italic) props.AppendChild(new Italic());
        props.AppendChild(new Color { Val = color });
        props.AppendChild(new FontSize { Val = size.ToString() });

        var paragraphProps = new StyleParagraphProperties(
            new SpacingBetweenLines { After = spacingAfter.ToString() });
        if (outlineLevel is not null)
            paragraphProps.AppendChild(new OutlineLevel { Val = outlineLevel.Value });

        return new Style(paragraphProps, props)
        {
            Type = StyleValues.Paragraph,
            StyleId = id,
            StyleName = new StyleName { Val = id },
        };
    }
}
