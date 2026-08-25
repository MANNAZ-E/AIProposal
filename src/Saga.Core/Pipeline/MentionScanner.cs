using Saga.Core.Models;

namespace Saga.Core.Pipeline;

/// <summary>One resolved mention: where it sits in the text and who it points at.</summary>
public readonly record struct MentionMatch(Guid UserId, int Start, int Length);

/// <summary>
/// Finds <c>@mentions</c> in a posted message by re-scanning the text against the bid team. The
/// composer's picker is a convenience, not the source of truth: a mention typed by hand has to
/// resolve identically, so the server does this over whatever text arrives.
/// </summary>
public static class MentionScanner
{
    public static List<MentionMatch> Scan(string text, IReadOnlyList<TeamChatMember> candidates)
    {
        var matches = new List<MentionMatch>();
        if (string.IsNullOrEmpty(text) || candidates.Count == 0) return matches;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '@') continue;
            // Mid-word: an email address in prose (a@b) is not a mention of b.
            if (i > 0 && !char.IsWhiteSpace(text[i - 1])) continue;

            var best = FindLongest(text, i + 1, candidates);
            if (best is null) continue;

            matches.Add(new MentionMatch(best.Value.UserId, i, best.Value.Length + 1));
            // Skip past what was consumed: an '@' inside a matched email cannot start another.
            i += best.Value.Length;
        }

        return matches;
    }

    /// <summary>
    /// The longest name or email that starts at <paramref name="from"/>. Longest wins so that
    /// "@Emil" and "@Emil Larsen" are both right when both are on the team — the shorter one
    /// would otherwise eat the first word and leave the surname as loose text.
    /// </summary>
    private static (Guid UserId, int Length)? FindLongest(string text, int from,
        IReadOnlyList<TeamChatMember> candidates)
    {
        (Guid UserId, int Length)? best = null;
        foreach (var candidate in candidates)
        {
            foreach (var handle in new[] { candidate.DisplayName, candidate.Email })
            {
                if (string.IsNullOrEmpty(handle)) continue;
                if (best is not null && handle.Length <= best.Value.Length) continue;
                if (!Matches(text, from, handle)) continue;
                best = (candidate.UserId, handle.Length);
            }
        }
        return best;
    }

    /// <summary>
    /// Case-insensitive match of <paramref name="handle"/> at <paramref name="from"/>, with the
    /// following character required not to be a letter or digit — otherwise "@Emilia" would bold
    /// "@Emil" and leave "ia" dangling behind it.
    /// </summary>
    private static bool Matches(string text, int from, string handle)
    {
        if (from + handle.Length > text.Length) return false;
        if (string.Compare(text, from, handle, 0, handle.Length,
                StringComparison.OrdinalIgnoreCase) != 0) return false;

        var after = from + handle.Length;
        return after >= text.Length || !char.IsLetterOrDigit(text[after]);
    }
}
