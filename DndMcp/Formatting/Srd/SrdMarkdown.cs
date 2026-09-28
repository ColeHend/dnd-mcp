using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DndMcp.Domain.Simulation;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;

namespace DndMcp.Formatting.Srd;

/// <summary>
/// Renders SRD documents for <c>rules_get</c>: one document, or the same entry from both editions side by side.
///
/// <para>
/// The per-kind formatters (<see cref="MonsterMarkdown"/>, <see cref="SpellMarkdown"/>, …) each return only a BODY:
/// markdown whose headings are <c>###</c> or deeper, with no title. This class adds the title and the
/// kind · edition · ref line, so every result is shaped the same, and so the same body can sit under a
/// <c>## 2014</c> heading in a comparison without its headings outranking the edition's.
/// </para>
/// <para>
/// Every result is capped at <see cref="MaxChars"/>. Claude Code warns at 10,000 tokens and moves anything over 25,000
/// to a file the model must read separately; ~32,000 characters is about 8,000 tokens. The cap cuts at a line
/// boundary and says what was cut and how to get the rest, so a truncated rules text never reads as complete.
/// </para>
/// </summary>
internal static class SrdMarkdown
{
    public const string Concise = "concise";
    public const string Full = "full";

    /// <summary>A monster's normalized stat block as the simulator reads it (<see cref="CombatantMarkdown"/>); monsters only.</summary>
    public const string Combatant = "combatant";

    /// <summary>The formats <c>rules_get</c> accepts, in the order its description lists them.</summary>
    public static IReadOnlyList<string> Formats { get; } = [Concise, Full, Combatant];

    /// <summary>
    /// The formats as <c>rules_get</c>'s description and its format errors list them: one text, so the description and
    /// the error can never disagree about what a format is (RulesGetToolTests pins that every one of
    /// <see cref="Formats"/> is in it).
    /// </summary>
    public const string FormatsText =
        "\"concise\" (default), \"full\" (adds the raw SRD JSON) or \"combatant\" (a monster as balance_simulate reads " +
        "it: parsed actions, traits by kind, what is not simulated)";

    /// <summary>About 8,000 tokens at the usual four characters per token.</summary>
    public const int MaxChars = 32_000;

    private static readonly JsonSerializerOptions RawJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>One document: title, kind · edition · ref line, body, and in <see cref="Full"/> format the raw record.</summary>
    /// <param name="trailer">
    /// Notes the caller appends after the document ("Also named …"). Passed in rather than appended afterwards so the cap
    /// accounts for them: a note added after capping could push the result past <see cref="MaxChars"/>, and one cut by
    /// the cap would lose the pointer to the other matches.
    /// </param>
    /// <param name="lead">
    /// A note on why this entry answers the question, shown under the meta line before the body ("Grappling is covered by
    /// this entry's #### Grappling subsection"). At the top because it explains the title: a lookup of "Grappling" that
    /// opens on "# Melee Attacks" reads as a wrong answer until the reader learns why.
    /// </param>
    /// <param name="combatant">
    /// The stat block of a monster document, for <see cref="Combatant"/>: the caller's normalizer (the host's cached
    /// <c>StatBlockService</c>), since normalizing needs the content root's overrides, which this class does not know.
    /// </param>
    public static string Format(
        SrdDocument doc, string format, ISrdLookup lookup, string? trailer = null, string? lead = null, Func<SrdDocument, StatBlock>? combatant = null)
    {
        var builder = new StringBuilder();
        builder.Append("# ").Append(doc.Name).Append('\n');
        AppendMeta(builder, doc, lead);
        builder.Append(Body(doc, format, lookup, combatant).TrimEnd());

        if (format == Full)
        {
            builder.Append("\n\n").Append(RawSection(doc));
        }

        return Cap(builder.ToString(), [doc.Ref], trailer);
    }

    /// <summary>
    /// The same entry in both editions. Either side may be null when nothing in that edition matched the entry, and the
    /// result says so rather than silently showing one side.
    /// </summary>
    /// <param name="trailer">Notes kept whole after the comparison; see <see cref="Format"/>.</param>
    /// <param name="note2014">
    /// A note about the 2014 side (how it was matched, which subsection covers the topic), shown under that side's meta
    /// line: the reader must see it with the entry it qualifies, not after both. When the 2014 side is missing, where to
    /// look instead (see <see cref="NothingMatched"/>).
    /// </param>
    /// <param name="note2024">The same for the 2024 side.</param>
    public static string Compare(
        SrdDocument? doc2014,
        SrdDocument? doc2024,
        string format,
        ISrdLookup lookup,
        string? trailer = null,
        string? note2014 = null,
        string? note2024 = null,
        Func<SrdDocument, StatBlock>? combatant = null)
    {
        if (doc2014 is null && doc2024 is null)
        {
            throw new ArgumentException("At least one edition's document is required.");
        }

        var title = doc2014 is not null && doc2024 is not null && doc2014.Name != doc2024.Name
            ? $"{doc2014.Name} (2014) / {doc2024.Name} (2024)"
            : (doc2024 ?? doc2014)!.Name;

        var builder = new StringBuilder();
        builder.Append("# ").Append(title).Append(" — 2014 vs 2024\n\n");

        if (doc2014 is not null && doc2024 is not null && SrdComparisonMarkdown.Glance(doc2014, doc2024) is { } glance)
        {
            builder.Append("## At a glance\n\n").Append(glance.TrimEnd()).Append("\n\n");
        }

        AppendEdition(builder, SrdEdition.Edition2014, doc2014, format, lookup, note2014, combatant);
        builder.Append("\n\n");
        AppendEdition(builder, SrdEdition.Edition2024, doc2024, format, lookup, note2024, combatant);

        SrdRef[] refs = [.. new[] { doc2014, doc2024 }.OfType<SrdDocument>().Select(d => d.Ref)];
        return Cap(builder.ToString().TrimEnd(), refs, trailer);
    }

    /// <summary>
    /// The body in a format: <see cref="CombatantMarkdown"/> for a monster in <see cref="Combatant"/> format, else the
    /// per-kind body. The other side of a comparison may be a different kind (matched by name only); it keeps its own body.
    /// </summary>
    internal static string Body(SrdDocument doc, string format, ISrdLookup lookup, Func<SrdDocument, StatBlock>? combatant)
    {
        if (format != Combatant || doc.Kind != SrdKinds.Monster)
        {
            return Body(doc, lookup);
        }

        ArgumentNullException.ThrowIfNull(combatant);
        return CombatantMarkdown.Body(combatant(doc));
    }

    /// <summary>The per-kind body. Kinds without a dedicated formatter fall back to <see cref="GenericMarkdown"/>.</summary>
    internal static string Body(SrdDocument doc, ISrdLookup lookup) => doc.Kind switch
    {
        SrdKinds.Monster => MonsterMarkdown.Body(doc, lookup),
        SrdKinds.Spell => SpellMarkdown.Body(doc, lookup),

        SrdKinds.Class or SrdKinds.Subclass or SrdKinds.Level or SrdKinds.Feature => ClassMarkdown.Body(doc, lookup),
        SrdKinds.Race or SrdKinds.Subrace or SrdKinds.Species or SrdKinds.Subspecies or SrdKinds.Trait
            or SrdKinds.Background or SrdKinds.Feat => OriginMarkdown.Body(doc, lookup),
        SrdKinds.AbilityScore or SrdKinds.Skill or SrdKinds.Proficiency or SrdKinds.Language or SrdKinds.Alignment
            => ReferenceMarkdown.Body(doc, lookup),

        SrdKinds.Equipment or SrdKinds.EquipmentCategory or SrdKinds.MagicItem or SrdKinds.WeaponProperty
            or SrdKinds.WeaponMastery or SrdKinds.Poison => EquipmentMarkdown.Body(doc, lookup),
        SrdKinds.Rule or SrdKinds.Condition or SrdKinds.DamageType or SrdKinds.MagicSchool
            => RuleMarkdown.Body(doc, lookup),

        _ => GenericMarkdown.Body(doc, lookup),
    };

    /// <summary>
    /// <c>*spell · 2024 · SRD 5.2.1 · `2024/spell/fireball`*</c>, then what else the reader needs to trust it: the other
    /// edition's name when it differs ("2014 name: Thug" — not "also known as", which would claim the 2024 SRD uses it),
    /// the subsections a long 2014 rule covers ("Covers: Grappling; …", so a lookup of "Grappling" that lands here makes
    /// sense), and any curated correction applied to the upstream text. Covers lists only the literal headings
    /// (<see cref="SrdAliasSources.Heading"/>), separated by "; " because a heading may hold commas ("Climbing, Swimming,
    /// and Crawling" read as three items with ", "); a 2024 name a section merely covers is not a heading. Qualifier
    /// aliases ("Finesse" for "Finesse (Weapon Property)") are left out: they only drop a word from the title.
    /// </summary>
    internal static string MetaLine(SrdDocument doc)
    {
        var lines = new List<string>
        {
            $"*{doc.Kind} \u00b7 {doc.Edition} \u00b7 {SrdMarkdownText.SourceFor(doc.Edition)}{GlossarySuffix(doc)} \u00b7 `{doc.Ref}`*",
        };

        var otherNames = AliasNames(doc, SrdAliasSources.Counterpart);
        if (otherNames.Count > 0)
        {
            var other = doc.Edition == SrdEdition.Edition2014 ? SrdEdition.Edition2024 : SrdEdition.Edition2014;
            lines.Add($"{other} name: {string.Join(", ", otherNames)}");
        }

        var headings = AliasNames(doc, SrdAliasSources.Heading);
        if (headings.Count > 0)
        {
            lines.Add($"Covers: {string.Join("; ", headings)}");
        }

        foreach (var reason in doc.Corrections)
        {
            lines.Add($"*Corrected from the upstream data: {reason.Trim()}*");
        }

        return string.Join("\n", lines);
    }

    // The index never stores an alias with the document's own name key, nor two with one key.
    private static List<string> AliasNames(SrdDocument doc, string source) =>
        doc.Aliases.Where(a => a.Source == source).Select(a => a.Name).ToList();

    private static string GlossarySuffix(SrdDocument doc) =>
        doc.Kind == SrdKinds.Rule && doc.Edition == SrdEdition.Edition2024 ? " Rules Glossary" : string.Empty;

    /// <summary>
    /// What a comparison says for an edition in which nothing matched. Worded as a search that failed, never as a fact
    /// about the SRD: "No 2014 equivalent in the SRD" was relayed by models as "2014 has no grappling rules", when the 2014
    /// SRD keeps Grappling inside Melee Attacks and Serpent Venom inside Sample Poisons, which no rename or name reaches.
    /// </summary>
    /// <param name="edition">The edition nothing matched in.</param>
    /// <param name="whereInstead">
    /// Where to look instead of a search, when a search cannot help: level records are not searchable, so for one the
    /// caller says where that edition's levels are.
    /// </param>
    internal static string NothingMatched(string edition, string? whereInstead = null) =>
        whereInstead is null
            ? $"*No {edition} entry matched this one by a known rename or by name. The {edition} SRD may cover it under another " +
              $"heading — try rules_search with edition {edition}.*"
            : $"*No {edition} entry matched this one by a known rename or by name.*\n{whereInstead.Trim()}";

    private static void AppendMeta(StringBuilder builder, SrdDocument doc, string? note)
    {
        builder.Append(MetaLine(doc));
        if (!string.IsNullOrWhiteSpace(note))
        {
            builder.Append('\n').Append(note.Trim());
        }

        builder.Append("\n\n");
    }

    private static void AppendEdition(
        StringBuilder builder, string edition, SrdDocument? doc, string format, ISrdLookup lookup, string? note, Func<SrdDocument, StatBlock>? combatant)
    {
        builder.Append("## ").Append(edition).Append(" (").Append(SrdMarkdownText.SourceFor(edition)).Append(")\n\n");
        if (doc is null)
        {
            builder.Append(NothingMatched(edition, string.IsNullOrWhiteSpace(note) ? null : note));
            return;
        }

        AppendMeta(builder, doc, note);
        builder.Append(Body(doc, format, lookup, combatant).TrimEnd());
        if (format == Full)
        {
            builder.Append("\n\n").Append(RawSection(doc));
        }
    }

    private static string RawSection(SrdDocument doc)
    {
        using var parsed = JsonDocument.Parse(doc.Json);
        var pretty = JsonSerializer.Serialize(parsed.RootElement, RawJsonOptions);
        return $"### Raw data\n\n```json\n{pretty}\n```";
    }

    /// <summary>
    /// <paramref name="markdown"/> followed by <paramref name="trailer"/>, within <see cref="MaxChars"/>. When too long,
    /// the markdown is cut at a line boundary, an open code fence is closed so the note after it is not swallowed into
    /// the code block, a note says how much was cut from which refs, and the trailer is kept whole after it.
    /// </summary>
    internal static string Cap(string markdown, IReadOnlyList<SrdRef> refs, string? trailer = null)
    {
        var tail = string.IsNullOrWhiteSpace(trailer) ? string.Empty : "\n\n" + trailer.Trim();
        if (markdown.Length + tail.Length <= MaxChars)
        {
            return markdown + tail;
        }

        const int NoteRoom = 400;
        var room = Math.Max(MaxChars - NoteRoom - tail.Length, NoteRoom);
        var cut = markdown.LastIndexOf('\n', Math.Min(room, markdown.Length - 1));
        if (cut <= 0)
        {
            cut = Math.Min(room, markdown.Length);
        }

        var kept = markdown[..cut];
        var fences = 0;
        foreach (var line in kept.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fences++;
            }
        }

        var builder = new StringBuilder(kept.TrimEnd());
        if (fences % 2 == 1)
        {
            builder.Append("\n```");
        }

        builder.Append("\n\n*[Truncated: ").Append(markdown.Length - cut).Append(" more characters of ")
            .Append(string.Join(" and ", refs.Select(r => $"`{r}`")))
            .Append(" not shown. Use format concise, or look up the linked entries individually.]*");
        return builder.Append(tail).ToString();
    }
}

/// <summary>
/// The fallback body for a kind with no dedicated formatter: the description paragraphs, or a note that there are none.
/// </summary>
internal static class GenericMarkdown
{
    public static string Body(SrdDocument doc, ISrdLookup lookup)
    {
        var paragraphs = doc.Root.Description();
        return paragraphs.Count > 0 ? SrdProse.Join(paragraphs) : SrdMarkdownText.NoDescription;
    }
}
