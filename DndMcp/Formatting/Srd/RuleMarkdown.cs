using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;

namespace DndMcp.Formatting.Srd;

/// <summary>
/// Body formatter for rules, conditions, damage types and magic schools: the kinds that are mostly prose.
///
/// <para>
/// "Rule" is two different record shapes under one kind. 2014 rules are 5e-database's heading tree (137 nodes, each
/// with a markdown <c>desc</c>, a <c>parent</c> and <c>children</c>); 2024 rules are the SRD 5.2.1 Rules Glossary
/// (174 flat entries with <c>description</c>, <c>tags</c>, <c>category</c> and <c>related</c> names). The edition picks
/// the shape because that is what the index guarantees: every 2024 rule comes from the glossary and every 2014 rule
/// from the tree. Rendering a glossary entry as a tree node would silently drop its Related links, and the reverse
/// would drop the breadcrumb and subsections.
/// </para>
/// </summary>
internal static class RuleMarkdown
{
    /// <summary>
    /// A backstop behind the visited sets for the parent walk and the subsection tree. The real tree is four levels
    /// deep (Appendix › The Planes of Existence › Beyond the Material › Planar Travel), so reaching this means a
    /// re-vendor changed its shape; without it a runaway walk would hang <c>rules_get</c> or overflow the stack.
    /// </summary>
    private const int MaxTreeDepth = 16;

    /// <summary>
    /// The SRD 5.2.1 chapters a glossary entry can send the reader to ("*See also* 'Playing the Game' ('Damage and
    /// Healing')", "as explained in 'Character Creation.'"), each with the pattern that finds it quoted. None of their own
    /// text is served: 2024 rules are the Rules Glossary, and the other chapters reach the index only as their
    /// individual entries (each spell, item, class, monster). The Rules Glossary itself is left out because it is served.
    /// </summary>
    private static readonly (string Title, Regex Quoted)[] Srd521Chapters =
    [
        .. new[]
        {
            "Playing the Game", "Character Creation", "Classes", "Character Origins", "Feats", "Equipment", "Spells",
            "Gameplay Toolbox", "Magic Items", "Monsters", "Monsters A–Z", "Animals",
        }.Select(title => (title, new Regex($"[\"“]{Regex.Escape(title)}[.,]?[\"”]", RegexOptions.CultureInvariant))),
    ];

    /// <summary>
    /// A parenthesised aside, dropped before looking for chapter titles: in 'See also "Equipment" ("Magic Items")' the
    /// quoted name in parentheses is a section of the chapter before it, not a chapter of its own.
    /// </summary>
    private static readonly Regex Parenthesized = new(@"\([^()]*\)", RegexOptions.CultureInvariant);

    public static string Body(SrdDocument doc, ISrdLookup lookup) => doc.Kind switch
    {
        SrdKinds.Rule when doc.Edition == SrdEdition.Edition2024 => GlossaryEntry(doc, lookup),
        SrdKinds.Rule => TreeNode(doc, lookup),
        _ => OrNoDescription(SrdProse.Join(doc.Root.Description())),
    };

    /// <summary>
    /// A 2014 rule: where it sits in the SRD ("Combat › Actions in Combat"), its text verbatim, then every section
    /// below it. The text is upstream's own markdown (tables, lists and <c>####</c> headings already correct), so it is
    /// not re-flowed. Heading-only nodes (Combat, Adventuring, Appendix) have no text; their subsection tree is the body,
    /// so a model that lands on a chapter can see every rule in it and fetch the one it needs.
    /// </summary>
    private static string TreeNode(SrdDocument doc, ISrdLookup lookup)
    {
        var text = SrdMarkdownText.JoinParagraphs(doc.Root.Paragraphs("desc"));

        var subsections = new StringBuilder();
        AppendSubsections(subsections, doc.Root, doc.Edition, lookup, depth: 0, visited: [doc.Slug]);

        return OrNoDescription(SrdMarkdownText.Blocks(
        [
            Breadcrumb(doc, lookup),
            text,
            SrdMarkdownText.Section("Subsections", subsections.ToString()),
        ]));
    }

    /// <summary>
    /// "**Part of** Combat (<c>ref</c>) › Actions in Combat (<c>ref</c>)", outermost first; null for a top-level node.
    /// Each step shows its ref because a model reading "Cover" often needs the surrounding section next.
    /// </summary>
    private static string? Breadcrumb(SrdDocument doc, ISrdLookup lookup)
    {
        var chain = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { doc.Slug };
        var parent = doc.Root.Obj("parent");

        while (parent is { } reference && chain.Count < MaxTreeDepth)
        {
            var slug = reference.Str("index");
            if (slug is not null && !visited.Add(slug))
            {
                break;
            }

            if (Resolve(reference, doc.Edition, lookup) is not { } parentDoc)
            {
                chain.Add(PlainName(reference));
                break;
            }

            chain.Add(Linked(parentDoc));
            parent = parentDoc.Root.Obj("parent");
        }

        if (chain.Count == 0)
        {
            return null;
        }

        chain.Reverse();
        return "**Part of** " + string.Join(" › ", chain);
    }

    /// <summary>
    /// The node's children as a nested list (two spaces per level), each with its own children below it, so a chapter
    /// reads as its table of contents. A child already listed higher up (a cycle) is listed but not descended into.
    /// </summary>
    private static void AppendSubsections(
        StringBuilder builder, JsonElement node, string edition, ISrdLookup lookup, int depth, HashSet<string> visited)
    {
        if (depth >= MaxTreeDepth)
        {
            return;
        }

        foreach (var child in node.Arr("children"))
        {
            var childDoc = Resolve(child, edition, lookup);
            builder.Append(' ', depth * 2).Append("- ").Append(childDoc is null ? PlainName(child) : Linked(childDoc)).Append('\n');
            if (childDoc is not null && visited.Add(childDoc.Slug))
            {
                AppendSubsections(builder, childDoc.Root, edition, lookup, depth + 1, visited);
            }
        }
    }

    /// <summary>
    /// The rule a tree link points at, or null. The walk needs each node's record anyway (for its parent or children),
    /// so a link that resolves to nothing is shown as a plain name instead of a ref: the tree has been reshaped
    /// upstream before (5e-database v7 merged the old rule sections into it), and a ref that fetches nothing would send
    /// the model round in a circle.
    /// </summary>
    private static SrdDocument? Resolve(JsonElement reference, string edition, ISrdLookup lookup) =>
        reference.Str("index") is { } slug ? lookup.Get(edition, SrdKinds.Rule, slug) : null;

    private static string Linked(SrdDocument doc) => $"{doc.Name} (`{doc.Ref}`)";

    private static string PlainName(JsonElement reference) => reference.Str("name") ?? reference.Str("index") ?? "?";

    /// <summary>
    /// A 2024 Rules Glossary entry: its category ("*Condition*"), the text, a note when the text cites an SRD chapter
    /// this server does not have (<see cref="ChapterNote"/>), then its Related entries as refs.
    /// Related names are glossary names ("Unarmed Strike"), resolved the same way the index slugs glossary entries
    /// (<see cref="SrdSlug.FromName"/>); a name that resolves to nothing is shown plain rather than as a ref that
    /// would fail when the model fetches it.
    /// </summary>
    private static string GlossaryEntry(SrdDocument doc, ISrdLookup lookup)
    {
        var root = doc.Root;

        var labels = new List<string>();
        if (root.Str("category") is { Length: > 0 } category)
        {
            labels.Add(category);
        }

        labels.AddRange(root.Arr("tags")
            .Where(t => t.ValueKind == JsonValueKind.String)
            .Select(t => t.GetString()!)
            .Where(t => t.Length > 0));

        var labelLine = labels.Count == 0
            ? null
            : "*" + string.Join(", ", labels.Distinct(StringComparer.OrdinalIgnoreCase)) + "*";

        var related = root.Arr("related")
            .Where(r => r.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(r.GetString()))
            .Select(r => RelatedLink(r.GetString()!.Trim(), doc.Edition, lookup));

        var description = root.Paragraphs("description");
        return OrNoDescription(SrdMarkdownText.Blocks(
        [
            labelLine,
            SrdProse.Join(description),
            ChapterNote(string.Join("\n", description), doc.Edition, lookup),
            SrdMarkdownText.Field("Related", string.Join(", ", related)),
        ]));
    }

    /// <summary>
    /// "*Not included here: the text of the SRD 5.2.1 chapter "Playing the Game". …*" when the entry's text quotes one
    /// or more SRD chapters, in the order it cites them; null otherwise. 50 glossary entries point at a chapter (37 at
    /// "Playing the Game" alone), and a model that follows the pointer searches for text that is not here, then answers
    /// from memory. The note sits after the text rather than inside it, so the SRD's words stay exactly as printed. A
    /// title the glossary also has as an entry is left alone: that one the model can fetch.
    /// </summary>
    private static string? ChapterNote(string text, string edition, ISrdLookup lookup)
    {
        var outsideAsides = Parenthesized.Replace(text, string.Empty);
        var cited = Srd521Chapters
            .Select(chapter => (chapter.Title, Match: chapter.Quoted.Match(outsideAsides)))
            .Where(c => c.Match.Success && lookup.Get(edition, SrdKinds.Rule, SrdSlug.FromName(c.Title)) is null)
            .OrderBy(c => c.Match.Index)
            .Select(c => $"\"{c.Title}\"")
            .ToList();

        if (cited.Count == 0)
        {
            return null;
        }

        var chapters = cited.Count == 1
            ? $"chapter {cited[0]}"
            : $"chapters {string.Join(", ", cited.Take(cited.Count - 1))} and {cited[^1]}";
        return $"*Not included here: the text of the SRD 5.2.1 {chapters}. The 2024 rules available are this Rules Glossary " +
               "and the individual entries (spells, equipment, classes, monsters and more).*";
    }

    private static string RelatedLink(string name, string edition, ISrdLookup lookup) =>
        lookup.Get(edition, SrdKinds.Rule, SrdSlug.FromName(name)) is { } target ? $"{name} (`{target.Ref}`)" : name;

    private static string OrNoDescription(string body) => string.IsNullOrWhiteSpace(body) ? SrdMarkdownText.NoDescription : body;
}
