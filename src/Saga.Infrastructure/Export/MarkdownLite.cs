using System.Text.RegularExpressions;

namespace Saga.Infrastructure.Export;

public record TextBlock(string Text, bool IsBullet, int Level = 0);

/// <summary>
/// Flattens the units' markdown into plain-text blocks for Office export. Office styling
/// replaces markdown styling, so emphasis/heading markers are stripped rather than mapped.
/// </summary>
public static class MarkdownLite
{
    public static IEnumerable<TextBlock> Blocks(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) yield break;

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var trimmed = line.TrimStart();
            var indent = line.Length - trimmed.Length;
            var bullet = Regex.Match(trimmed, @"^([-*+]|\d+[.)])\s+(.*)$");
            if (bullet.Success)
            {
                yield return new TextBlock(Clean(bullet.Groups[2].Value), IsBullet: true,
                    Level: Math.Min(indent / 2, 4));
                continue;
            }

            yield return new TextBlock(Clean(trimmed.TrimStart('#', ' ')), IsBullet: false);
        }
    }

    private static string Clean(string text)
    {
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", "$1");
        text = Regex.Replace(text, @"\*(.+?)\*", "$1");
        text = Regex.Replace(text, @"__(.+?)__", "$1");
        text = Regex.Replace(text, @"_(.+?)_", "$1");
        text = Regex.Replace(text, @"`(.+?)`", "$1");
        text = Regex.Replace(text, @"\[(.+?)\]\(.+?\)", "$1");
        return text.Trim();
    }
}
