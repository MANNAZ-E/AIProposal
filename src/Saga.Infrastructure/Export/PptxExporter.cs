using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using Saga.Core.Domain;
using Saga.Core.Models;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace Saga.Infrastructure.Export;

/// <summary>
/// PowerPoint export: title slide + one slide per structure entry, filled from the content
/// units (title, key message as subtitle, body bullets). Clean Saga styling (#5A616D) until
/// Emil's sample Mannaz deck arrives to mimic.
/// </summary>
public static class PptxExporter
{
    private const string ThemeColor = "5A616D";
    private const string MutedColor = "6C7280";
    private const string TextColor = "24272C";

    // 16:9 in EMU.
    private const int SlideWidth = 12192000;
    private const int SlideHeight = 6858000;

    public static byte[] Build(Proposal proposal, StructurePayload structure, ContentPayload content)
    {
        using var stream = new MemoryStream();
        using (var pptx = PresentationDocument.Create(stream, PresentationDocumentType.Presentation))
        {
            var presentationPart = pptx.AddPresentationPart();
            presentationPart.Presentation = new Presentation();

            var masterPart = AddSlideMaster(presentationPart);
            var layoutPart = masterPart.SlideLayoutParts.First();

            var unitsByStructureId = content.Units
                .Where(u => u.StructureItemId != Guid.Empty)
                .GroupBy(u => u.StructureItemId)
                .ToDictionary(g => g.Key, g => g.First());

            var slideParts = new List<SlidePart> { TitleSlide(presentationPart, layoutPart, proposal) };
            foreach (var item in structure.Items)
            {
                var unit = unitsByStructureId.GetValueOrDefault(item.Id)
                    ?? content.Units.FirstOrDefault(u => u.Title == item.Title);
                slideParts.Add(ContentSlide(presentationPart, layoutPart, item, unit));
            }

            var slideIdList = new SlideIdList();
            uint id = 256;
            foreach (var slidePart in slideParts)
                slideIdList.AppendChild(new SlideId
                {
                    Id = id++,
                    RelationshipId = presentationPart.GetIdOfPart(slidePart),
                });

            presentationPart.Presentation.Append(
                new SlideMasterIdList(new SlideMasterId
                {
                    Id = 2147483648,
                    RelationshipId = presentationPart.GetIdOfPart(masterPart),
                }),
                slideIdList,
                new SlideSize { Cx = SlideWidth, Cy = SlideHeight },
                new NotesSize { Cx = 6858000, Cy = 9144000 });
            presentationPart.Presentation.Save();
        }
        return stream.ToArray();
    }

    private static SlidePart TitleSlide(PresentationPart presentationPart, SlideLayoutPart layoutPart,
        Proposal proposal)
    {
        var shapes = new List<P.Shape>
        {
            TextShape(2, "Title", x: 914400, y: 2400000, w: SlideWidth - 1828800, h: 1200000,
                [(proposal.Title, 4000, true, "FFFFFF")]),
            TextShape(3, "Subtitle", x: 914400, y: 3700000, w: SlideWidth - 1828800, h: 900000,
                [
                    (proposal.ClientName, 2400, false, "E8EAED"),
                    ($"Mannaz · {DateTime.Today:d MMMM yyyy}", 1600, false, "C8CCD2"),
                ]),
        };
        return AddSlide(presentationPart, layoutPart, shapes, background: ThemeColor);
    }

    private static SlidePart ContentSlide(PresentationPart presentationPart, SlideLayoutPart layoutPart,
        StructureItem item, ContentUnit? unit)
    {
        var shapes = new List<P.Shape>
        {
            TextShape(2, "Title", x: 685800, y: 365760, w: SlideWidth - 1371600, h: 750000,
                [(item.Title, 2800, true, ThemeColor)]),
        };

        var lines = new List<(string Text, int Size, bool Bold, string Color)>();
        var keyMessage = unit?.KeyMessage ?? item.KeyMessage;
        if (!string.IsNullOrWhiteSpace(keyMessage))
            lines.Add((keyMessage, 1600, false, MutedColor));

        SlidePart slidePart;
        if (unit is not null)
        {
            var bulletLines = MarkdownLite.Blocks(unit.BodyMarkdown)
                .Select(b => (b.Text, 1400, false, TextColor, b.IsBullet, b.Level))
                .ToList();
            shapes.Add(BodyShape(3, lines, bulletLines));
            slidePart = AddSlide(presentationPart, layoutPart, shapes, background: null);
        }
        else
        {
            lines.Add(("(No content generated for this slide yet.)", 1400, false, MutedColor));
            shapes.Add(BodyShape(3, lines, []));
            slidePart = AddSlide(presentationPart, layoutPart, shapes, background: null);
        }
        return slidePart;
    }

    private static SlidePart AddSlide(PresentationPart presentationPart, SlideLayoutPart layoutPart,
        IEnumerable<P.Shape> shapes, string? background)
    {
        var slidePart = presentationPart.AddNewPart<SlidePart>();
        slidePart.AddPart(layoutPart);

        var shapeTree = new ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                new P.NonVisualGroupShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties()),
            new GroupShapeProperties(new A.TransformGroup()));
        foreach (var shape in shapes)
            shapeTree.AppendChild(shape);

        var commonSlideData = new CommonSlideData();
        if (background is not null)
            commonSlideData.AppendChild(new Background(new BackgroundProperties(
                new A.SolidFill(new A.RgbColorModelHex { Val = background }))));
        commonSlideData.AppendChild(shapeTree);

        slidePart.Slide = new Slide(commonSlideData,
            new ColorMapOverride(new A.MasterColorMapping()));
        slidePart.Slide.Save();
        return slidePart;
    }

    private static P.Shape TextShape(uint id, string name, long x, long y, long w, long h,
        IEnumerable<(string Text, int Size, bool Bold, string Color)> lines)
    {
        var body = new P.TextBody(
            new A.BodyProperties { Wrap = A.TextWrappingValues.Square },
            new A.ListStyle());
        foreach (var (text, size, bold, color) in lines)
            body.AppendChild(new A.Paragraph(RunOf(text, size, bold, color)));
        return ShapeOf(id, name, x, y, w, h, body);
    }

    private static P.Shape BodyShape(uint id,
        List<(string Text, int Size, bool Bold, string Color)> introLines,
        List<(string Text, int Size, bool Bold, string Color, bool IsBullet, int Level)> blocks)
    {
        var body = new P.TextBody(
            new A.BodyProperties { Wrap = A.TextWrappingValues.Square },
            new A.ListStyle());
        foreach (var (text, size, bold, color) in introLines)
        {
            var p = new A.Paragraph(
                new A.ParagraphProperties { Indent = 0, LeftMargin = 0 },
                RunOf(text, size, bold, color),
                new A.EndParagraphRunProperties());
            body.AppendChild(p);
        }
        foreach (var (text, size, bold, color, isBullet, level) in blocks)
        {
            var props = isBullet
                ? new A.ParagraphProperties(new A.CharacterBullet { Char = "•" })
                {
                    Level = Math.Clamp(level, 0, 4),
                    LeftMargin = 228600 * (level + 1),
                    Indent = -228600,
                }
                : new A.ParagraphProperties(new A.NoBullet()) { Indent = 0, LeftMargin = 0 };
            body.AppendChild(new A.Paragraph(props, RunOf(text, size, bold, color)));
        }
        return ShapeOf(id, "Body", x: 685800, y: 1280160, w: SlideWidth - 1371600,
            h: SlideHeight - 1645920, body);
    }

    private static A.Run RunOf(string text, int size, bool bold, string color)
        => new(
            new A.RunProperties(
                new A.SolidFill(new A.RgbColorModelHex { Val = color }),
                new A.LatinFont { Typeface = "Segoe UI" })
            { FontSize = size, Bold = bold, Language = "en-US" },
            new A.Text(text));

    private static P.Shape ShapeOf(uint id, string name, long x, long y, long w, long h, P.TextBody body)
        => new(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = name },
                new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                new ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(
                new A.Transform2D(
                    new A.Offset { X = x, Y = y },
                    new A.Extents { Cx = w, Cy = h })),
            body);

    /// <summary>Minimal slide master + blank layout + theme, in Saga colors.</summary>
    private static SlideMasterPart AddSlideMaster(PresentationPart presentationPart)
    {
        var masterPart = presentationPart.AddNewPart<SlideMasterPart>();
        var layoutPart = masterPart.AddNewPart<SlideLayoutPart>();
        var themePart = masterPart.AddNewPart<ThemePart>();
        themePart.Theme = BuildTheme();
        themePart.Theme.Save();

        layoutPart.SlideLayout = new SlideLayout(
            new CommonSlideData(new ShapeTree(
                new P.NonVisualGroupShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                    new P.NonVisualGroupShapeDrawingProperties(),
                    new ApplicationNonVisualDrawingProperties()),
                new GroupShapeProperties(new A.TransformGroup()))),
            new ColorMapOverride(new A.MasterColorMapping()))
        { Type = SlideLayoutValues.Blank };

        masterPart.SlideMaster = new SlideMaster(
            new CommonSlideData(new ShapeTree(
                new P.NonVisualGroupShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                    new P.NonVisualGroupShapeDrawingProperties(),
                    new ApplicationNonVisualDrawingProperties()),
                new GroupShapeProperties(new A.TransformGroup()))),
            new P.ColorMap
            {
                Background1 = A.ColorSchemeIndexValues.Light1,
                Text1 = A.ColorSchemeIndexValues.Dark1,
                Background2 = A.ColorSchemeIndexValues.Light2,
                Text2 = A.ColorSchemeIndexValues.Dark2,
                Accent1 = A.ColorSchemeIndexValues.Accent1,
                Accent2 = A.ColorSchemeIndexValues.Accent2,
                Accent3 = A.ColorSchemeIndexValues.Accent3,
                Accent4 = A.ColorSchemeIndexValues.Accent4,
                Accent5 = A.ColorSchemeIndexValues.Accent5,
                Accent6 = A.ColorSchemeIndexValues.Accent6,
                Hyperlink = A.ColorSchemeIndexValues.Hyperlink,
                FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink,
            },
            new SlideLayoutIdList(new SlideLayoutId
            {
                Id = 2147483649,
                RelationshipId = masterPart.GetIdOfPart(layoutPart),
            }));
        masterPart.SlideMaster.Save();
        layoutPart.SlideLayout.Save();
        return masterPart;
    }

    private static A.Theme BuildTheme() => new(
        new A.ThemeElements(
            new A.ColorScheme(
                new A.Dark1Color(new A.RgbColorModelHex { Val = TextColor }),
                new A.Light1Color(new A.RgbColorModelHex { Val = "FFFFFF" }),
                new A.Dark2Color(new A.RgbColorModelHex { Val = ThemeColor }),
                new A.Light2Color(new A.RgbColorModelHex { Val = "ECEEF1" }),
                new A.Accent1Color(new A.RgbColorModelHex { Val = ThemeColor }),
                new A.Accent2Color(new A.RgbColorModelHex { Val = MutedColor }),
                new A.Accent3Color(new A.RgbColorModelHex { Val = "8A93A3" }),
                new A.Accent4Color(new A.RgbColorModelHex { Val = "B7BEC9" }),
                new A.Accent5Color(new A.RgbColorModelHex { Val = "464C56" }),
                new A.Accent6Color(new A.RgbColorModelHex { Val = "9A6B00" }),
                new A.Hyperlink(new A.RgbColorModelHex { Val = "464C56" }),
                new A.FollowedHyperlinkColor(new A.RgbColorModelHex { Val = MutedColor }))
            { Name = "Saga" },
            new A.FontScheme(
                new A.MajorFont(
                    new A.LatinFont { Typeface = "Segoe UI" },
                    new A.EastAsianFont { Typeface = "" },
                    new A.ComplexScriptFont { Typeface = "" }),
                new A.MinorFont(
                    new A.LatinFont { Typeface = "Segoe UI" },
                    new A.EastAsianFont { Typeface = "" },
                    new A.ComplexScriptFont { Typeface = "" }))
            { Name = "Saga" },
            new A.FormatScheme(
                new A.FillStyleList(
                    new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                    new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                    new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })),
                new A.LineStyleList(
                    new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })),
                    new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })),
                    new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }))),
                new A.EffectStyleList(
                    new A.EffectStyle(new A.EffectList()),
                    new A.EffectStyle(new A.EffectList()),
                    new A.EffectStyle(new A.EffectList())),
                new A.BackgroundFillStyleList(
                    new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                    new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                    new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })))
            { Name = "Saga" }))
    { Name = "Saga" };
}
