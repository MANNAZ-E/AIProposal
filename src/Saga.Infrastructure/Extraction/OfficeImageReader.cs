using System.Security.Cryptography;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Saga.Infrastructure.Extraction;

/// <param name="Ordinal">Position in reading order, 1-based — this is what lines an image up with
/// its <c>![](figures/1.1)</c> placeholder, so nothing may be dropped from the sequence.</param>
public record EmbeddedImage(int Ordinal, string MediaType, byte[] Data)
{
    /// <summary>Identifies repeats — a logo on every slide is read once and OCR'd once.</summary>
    public string Hash { get; } = Convert.ToHexString(SHA256.HashData(Data));
}

/// <summary>
/// Pulls the images embedded in an Office file out in reading order, using the OpenXML package the
/// file already is. Reading order is the whole point: Content Understanding numbers its figure
/// placeholders in the order the images appear, so image n must be the n-th one a reader would see.
/// That rules out enumerating the parts (<c>ImageParts</c> comes back in package order, which is
/// arbitrary) — the relationship references inside the content have to be walked instead.
/// </summary>
public static class OfficeImageReader
{
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string> { ".docx", ".pptx", ".xlsx" };

    /// <summary>
    /// Every embedded image, unfiltered. Filtering happens in the caller: dropping an image here
    /// would shift every later image onto the wrong placeholder.
    /// </summary>
    public static IReadOnlyList<EmbeddedImage> Read(Stream file, string extension)
    {
        var images = new List<EmbeddedImage>();
        switch (extension.ToLowerInvariant())
        {
            case ".docx":
                using (var document = WordprocessingDocument.Open(file, false))
                {
                    var main = document.MainDocumentPart;
                    // Headers, footers and footnotes are deliberately left out: Content Understanding
                    // renders those as <!-- PageHeader: … --> notes, not as figures.
                    if (main?.Document is not null) Collect(main, main.Document, images);
                }
                break;

            case ".pptx":
                using (var presentation = PresentationDocument.Open(file, false))
                {
                    var part = presentation.PresentationPart;
                    // SldIdLst order, not SlideParts order — the latter is package order.
                    foreach (var slideId in part?.Presentation?.SlideIdList?.Elements<SlideId>() ?? [])
                    {
                        if (slideId.RelationshipId?.Value is not { } id) continue;
                        if (part!.GetPartById(id) is not SlidePart slide || slide.Slide is null) continue;
                        Collect(slide, slide.Slide, images);
                    }
                }
                break;

            case ".xlsx":
                using (var workbook = SpreadsheetDocument.Open(file, false))
                {
                    var part = workbook.WorkbookPart;
                    foreach (var sheet in part?.Workbook?.Sheets?.Elements<Sheet>() ?? [])
                    {
                        if (sheet.Id?.Value is not { } id) continue;
                        if (part!.GetPartById(id) is not WorksheetPart worksheet) continue;
                        var drawings = worksheet.DrawingsPart;
                        if (drawings?.WorksheetDrawing is null) continue;
                        Collect(drawings, drawings.WorksheetDrawing, images);
                    }
                }
                break;
        }
        return images;
    }

    /// <summary>
    /// Walks one part's content in document order and resolves every image reference it holds —
    /// both DrawingML pictures and the legacy VML shapes older tools still emit.
    /// </summary>
    private static void Collect(OpenXmlPartContainer container, OpenXmlElement root, List<EmbeddedImage> images)
    {
        foreach (var element in root.Descendants())
        {
            var relationshipId = element switch
            {
                DocumentFormat.OpenXml.Drawing.Blip blip => blip.Embed?.Value,
                DocumentFormat.OpenXml.Vml.ImageData data => data.RelationshipId?.Value,
                _ => null,
            };
            // A null id means a linked (external) image: there are no bytes in the package to read.
            if (string.IsNullOrEmpty(relationshipId)) continue;

            ImagePart part;
            try
            {
                if (container.GetPartById(relationshipId) is not ImagePart image) continue;
                part = image;
            }
            catch (ArgumentOutOfRangeException)
            {
                continue; // Dangling relationship — the file is malformed, but the rest still reads.
            }

            using var stream = part.GetStream();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            images.Add(new EmbeddedImage(images.Count + 1, part.ContentType, buffer.ToArray()));
        }
    }
}
