using System.Net;
using System.Text;
using Microsoft.AspNetCore.Components;
using Saga.Core.Domain;

namespace Saga.Web;

/// <summary>
/// Renders a team message: plain text with its stored mentions bolded. Deliberately not markdown
/// — the team chat is a conversation, not a document, and <c>white-space: pre-wrap</c> on the
/// bubble keeps the newlines that matter.
///
/// The splice uses the offsets the scanner found when the message was posted, so nothing is
/// re-resolved here: somebody removed from the bid team since keeps their mention bolded.
/// </summary>
public static class TeamMessageText
{
    public static MarkupString Render(string text, IEnumerable<TeamMessageMention> mentions, Guid meId)
    {
        text ??= "";
        var ordered = mentions
            .Where(m => m.Start >= 0 && m.Length > 0 && m.Start + m.Length <= text.Length)
            .OrderBy(m => m.Start)
            .ToList();

        var html = new StringBuilder();
        var cursor = 0;
        foreach (var mention in ordered)
        {
            // Stored offsets that overlap could only come from a bug, and dropping the later one
            // is better than emitting torn markup.
            if (mention.Start < cursor) continue;
            html.Append(WebUtility.HtmlEncode(text[cursor..mention.Start]));
            var css = mention.UserId == meId ? "team-mention team-mention-me" : "team-mention";
            html.Append("<strong class=\"").Append(css).Append("\">")
                .Append(WebUtility.HtmlEncode(text.Substring(mention.Start, mention.Length)))
                .Append("</strong>");
            cursor = mention.Start + mention.Length;
        }
        html.Append(WebUtility.HtmlEncode(text[cursor..]));
        return new MarkupString(html.ToString());
    }
}
