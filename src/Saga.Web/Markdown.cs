using Markdig;
using Microsoft.AspNetCore.Components;

namespace Saga.Web;

public static class Markdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    public static MarkupString Render(string? markdown)
        => new(Markdig.Markdown.ToHtml(markdown ?? "", Pipeline));
}
