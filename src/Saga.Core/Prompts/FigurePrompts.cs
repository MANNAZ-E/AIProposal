namespace Saga.Core.Prompts;

/// <summary>
/// Describes a figure that OCR could not read — a chart, diagram, photo or logo embedded in a
/// client document. Only reached when the OCR pass came back with almost no text, so the model is
/// asked to say what the picture shows rather than to transcribe it.
/// </summary>
public static class FigurePrompts
{
    /// <summary>
    /// Byte-identical for every figure of every upload, so the provider serves it from its prompt
    /// cache; nothing about the specific document belongs here.
    /// </summary>
    public const string SystemPrompt = """
        You describe figures taken from tender and client documents so that a later model, which
        cannot see the picture, can work with what it shows.

        Rules:
        - Describe only what is visible. Never guess at numbers, names or intent that the figure
          does not show.
        - Report the figure's own words verbatim where they carry meaning: axis labels, legends,
          column headers, box captions.
        - For a chart, say what is plotted, over what range, and the direction or shape of the
          trend. For a diagram, say what the boxes are and how they connect. For a photo or a
          decorative graphic, say so plainly and briefly.
        - Write in the same language as the text in the figure; use English if it has no text.
        - One paragraph, no heading, no markdown, no preamble such as "This image shows".
        - If the figure carries no information a reader would need — a logo, a divider, a blank
          box — answer with exactly: NO CONTENT
        """;

    /// <summary>The material message the image rides on; deliberately says nothing task-specific.</summary>
    public const string MaterialMessage = "The following figure was embedded in a client document.";

    /// <summary>The trailing instruction, behind the image, per the prompt assembly order.</summary>
    public const string Instruction = "Describe this figure.";

    /// <summary>The answer that means the figure is not worth keeping in the extracted text.</summary>
    public const string NoContentMarker = "NO CONTENT";
}
