namespace DndMcp.Domain.Campaign;

/// <summary>A name to look for, and what it names (an entity row, an alias, a known_as: the caller's payload).</summary>
/// <param name="Surface">The name as stored ("The Old King", "Axiom Cage").</param>
/// <param name="Payload">Whatever the caller needs back when the name is found.</param>
public sealed record NameEntry(string Surface, object Payload);

/// <summary>One name found in a text.</summary>
/// <param name="Start">Index of the name's first character in the text (after any leading article).</param>
/// <param name="Length">Characters to the end of the name's last word (a possessive's "s" included).</param>
/// <param name="Matched">The text as written ("Old king", "Axiom Cage's").</param>
/// <param name="Entries">Every entry with the matched name: two entities may share a name, and the caller decides.</param>
public sealed record NameHit(int Start, int Length, string Matched, IReadOnlyList<NameEntry> Entries);

/// <summary>
/// Finds known names in text: the knowledge check's first step ("does this lyric say a name Belmakor does not know?").
///
/// <para>
/// <b>Matching</b> follows <see cref="CampaignText.Key"/>: case, diacritics and apostrophes are ignored (Björn is
/// Bjorn); a leading "the", "a" or "an" is optional on both sides ("Old king, come down" is "the old king"); the last
/// word may take a possessive or plural s ("the old king's errand"). Whole words only.
/// </para>
/// <para>
/// <b>Longest match wins at a position</b>, and hits never overlap: in "the old king's errand" the thread "The old king's
/// errand" is found, not the king inside it. Getting this wrong in the other direction (reporting the king) would flag
/// a leak that is not there; failing to find a name at all would pass a leak, which is why an exact last word is
/// preferred over a plural reading when both exist (an entity called "Kings" beats "King" + s).
/// </para>
/// </summary>
public sealed class NameScanner
{
    private readonly Dictionary<string, List<Group>> _byFirstWord = new(StringComparer.Ordinal);

    /// <summary>Indexes <paramref name="entries"/>; entries whose name has no letters or digits are ignored.</summary>
    public NameScanner(IEnumerable<NameEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry is null)
            {
                continue;
            }

            var words = NameWords(entry.Surface);
            if (words.Length == 0)
            {
                continue;
            }

            var key = string.Join(' ', words);
            if (!groups.TryGetValue(key, out var group))
            {
                group = new Group(words, []);
                groups.Add(key, group);
                if (!_byFirstWord.TryGetValue(words[0], out var list))
                {
                    list = [];
                    _byFirstWord.Add(words[0], list);
                }

                list.Add(group);
            }

            group.Entries.Add(entry);
        }

        foreach (var list in _byFirstWord.Values)
        {
            list.Sort((a, b) => b.Words.Length.CompareTo(a.Words.Length));
        }
    }

    /// <summary>Every name found in <paramref name="text"/>, in text order, never overlapping.</summary>
    public IReadOnlyList<NameHit> Scan(string? text)
    {
        var hits = new List<NameHit>();
        if (string.IsNullOrEmpty(text) || _byFirstWord.Count == 0)
        {
            return hits;
        }

        var words = CampaignWords.Split(text);
        var i = 0;
        while (i < words.Count)
        {
            var match = Best(words, i);
            if (match is null)
            {
                i++;
                continue;
            }

            var (group, length) = match.Value;
            var start = words[i].Start;
            var end = words[i + length - 1].End;
            hits.Add(new NameHit(start, end - start, text[start..end], group.Entries));
            i += length;
        }

        return hits;
    }

    /// <summary>The folded words of a name without a leading article (kept when it is the whole name).</summary>
    private static string[] NameWords(string? surface)
    {
        var words = CampaignWords.Keys(surface);
        return words.Count > 1 && CampaignWords.IsArticle(words[0]) ? words.Skip(1).ToArray() : words.ToArray();
    }

    private (Group Group, int Length)? Best(IReadOnlyList<TextWord> words, int at)
    {
        (Group Group, int Length, bool Exact)? best = null;
        var first = words[at].Key;
        var firsts = first.Length > 1 && first.EndsWith('s') ? new[] { first, first[..^1] } : new[] { first };
        foreach (var candidateFirst in firsts)
        {
            if (!_byFirstWord.TryGetValue(candidateFirst, out var groups))
            {
                continue;
            }

            foreach (var group in groups)
            {
                if (!Matches(words, at, group.Words, out var exact))
                {
                    continue;
                }

                var length = group.Words.Length;
                if (best is not { } b || length > b.Length || (length == b.Length && exact && !b.Exact))
                {
                    best = (group, length, exact);
                }
            }
        }

        return best is { } found ? (found.Group, found.Length) : null;
    }

    /// <summary>Whether <paramref name="name"/> matches at <paramref name="at"/>: every word exact, the last also with a trailing s.</summary>
    private static bool Matches(IReadOnlyList<TextWord> words, int at, string[] name, out bool exact)
    {
        exact = false;
        if (at + name.Length > words.Count)
        {
            return false;
        }

        for (var k = 0; k < name.Length - 1; k++)
        {
            if (words[at + k].Key != name[k])
            {
                return false;
            }
        }

        var last = words[at + name.Length - 1].Key;
        exact = last == name[^1];
        return exact || last == name[^1] + "s";
    }

    private sealed record Group(string[] Words, List<NameEntry> Entries);
}
