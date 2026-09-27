using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using DndMcp.Domain.Core;
using DndMcp.Domain.Encounters;
using DndMcp.Formatting;
using DndMcp.Hosting;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DndMcp.Tools;

/// <summary>
/// <c>encounter_difficulty</c>: how hard a fight is for a party, by the 2014 DMG's thresholds and multipliers, the 2024
/// XP budget, or both side by side. Thin by design: validate, resolve each monster to a CR and XP, call
/// <see cref="Encounter2014"/> / <see cref="Encounter2024"/>, render with <see cref="EncounterMarkdown"/>.
///
/// <para>
/// <b>CR and XP come from srd.db</b> (<see cref="SrdMonsterChallenge"/>), which carries the curated corrections, never
/// from the vendored files. A monster that is not in the SRD (most of the Monster Manual) is given by <c>cr</c>; the
/// not-found message says so, because a model told only "no monster is named Beholder" guesses a CR from memory and
/// reports it as found.
/// </para>
/// <para>
/// <b>Editions.</b> A name is looked up in each edition the answer needs. When one edition lacks it, the other edition's
/// recorded counterpart is used, and failing that the other edition's stat block, with a note: a 2014 stat block in a
/// 2024 game is ordinary play, and "not in the 2024 SRD" must not stop the 2024 maths. An explicit ref is used as given
/// for a single edition; for both, the other side is its counterpart. A <c>ref</c> with no slash ("ogre") is looked up as
/// a name: models pass a name as ref often enough, and the table shows the ref that was used.
/// </para>
/// <para>
/// The index is opened only when an item names an SRD monster, so an encounter given entirely by CR works even while
/// srd.db is still building or cannot be built.
/// </para>
/// </summary>
public sealed class EncounterTools
{
    /// <summary>The <c>name</c> of a CR-only monster is shown in the result; longer text is not a label.</summary>
    public const int MaxLabelLength = 60;

    private const int MaxEchoLength = 80;

    private const string Examples =
        "Example: {\"party\": [5, 5, 5, 5], \"monsters\": [{\"name\": \"Ogre\", \"count\": 3}], \"edition\": \"both\"}.";

    private readonly SrdIndexService _indexService;

    public EncounterTools(SrdIndexService indexService)
    {
        _indexService = indexService;
    }

    // Idempotent and closed-world: a pure function of the arguments and the vendored content this binary ships.
    [McpServerTool(Name = "encounter_difficulty", Title = "Encounter difficulty", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "How hard a combat encounter is for a party, computed by the rules rather than estimated. 2014 rules: the DMG's " +
        "XP thresholds (Easy, Medium, Hard, Deadly) with the group multiplier and the party-size shift. 2024 rules: the SRD " +
        "5.2.1 XP budget (Low, Moderate, High), no multiplier, plus the SRD's troubleshooting warnings. edition \"both\" shows " +
        "the two side by side and how their bands compare at the party's levels. Also gives the XP the party earns.\n" +
        "- party: one level per character, e.g. [5, 5, 5, 5] for four level 5 characters, or [6, 5, 5, 4].\n" +
        "- monsters: one item per kind of monster, each with exactly one of ref, name or cr:\n" +
        "  - ref: an SRD monster's ref (\"2014/monster/ogre\"); name: an SRD monster's name (\"Ogre\", \"Adult Red Dragon\"), " +
        "looked up in both editions;\n" +
        "  - cr: for a monster not in the SRD (most of the Monster Manual), \"1/4\" or \"13\", with name as its label;\n" +
        "  - count (default 1); exclude (2014: leave a far weaker monster out of the multiplier's count); lair (2024: " +
        "the stat block's in-lair XP; with cr, the next CR's XP).\n" +
        "- edition: \"2024\" (default), \"2014\" or \"both\".\n" +
        "- effective_level_offset: optional whole number, e.g. 1 for a party that fights like one level higher (a strong " +
        "party or house rules); the result then shows both the book label and the effective-level label.\n" +
        "The tables themselves: rules_get with ref \"rules://tables\" lists them (XP by CR, 2024 budget, 2014 thresholds, " +
        "multipliers, adventuring-day XP, DMG monster statistics by CR).\n" +
        Examples)]
    public async Task<string> Difficulty(
        [Description("One level (1-20) per character, e.g. [5, 5, 5, 5].")] int[] party,
        [Description(
            "The monsters: [{\"name\": \"Ogre\", \"count\": 3}], [{\"ref\": \"2014/monster/goblin\", \"count\": 6}] or " +
            "[{\"cr\": \"5\", \"name\": \"Homebrew brute\"}].")]
        EncounterMonsterInput[] monsters,
        [Description("\"2024\" (default), \"2014\" or \"both\".")] string? edition = SrdEdition.Edition2024,
        [Description("Optional levels to add to every character for an effective-level reading, -10 to 10, e.g. 1. Default 0.")]
        [AIParameterName("effective_level_offset")] int effectiveLevelOffset = 0,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var editions = Editions(edition);
        var levels = party ?? [];
        EncounterLimits.ValidateParty(levels);
        EncounterLimits.ValidateOffset(effectiveLevelOffset);
        var requests = Requests(monsters);

        var entries = requests.Any(r => r.NeedsIndex)
            ? await _indexService.QueryAsync(index => Resolve(requests, editions, index), progress, cancellationToken)
            : Resolve(requests, editions, index: null);

        var effective = effectiveLevelOffset == 0 ? null : EffectiveParty.Of(levels, effectiveLevelOffset);

        Edition2014Report? for2014 = null;
        Edition2024Report? for2024 = null;
        if (editions.Contains(SrdEdition.Edition2014))
        {
            var monsters2014 = Monsters(entries, SrdEdition.Edition2014);
            for2014 = new Edition2014Report(
                monsters2014,
                Encounter2014.Assess(levels, monsters2014),
                effective is null ? null : Encounter2014.Assess(effective.Levels, monsters2014));
        }

        if (editions.Contains(SrdEdition.Edition2024))
        {
            var monsters2024 = Monsters(entries, SrdEdition.Edition2024);
            for2024 = new Edition2024Report(
                Encounter2024.Assess(levels, monsters2024),
                effective is null ? null : Encounter2024.Assess(effective.Levels, monsters2024),
                Encounter2024.Troubleshoot(levels, monsters2024));
        }

        return EncounterMarkdown.Format(new EncounterReport(levels, effective, editions, entries, for2014, for2024));
    }

    private static IReadOnlyList<string> Editions(string? edition)
    {
        var value = string.IsNullOrWhiteSpace(edition) ? SrdEdition.Edition2024 : edition.Trim().ToLowerInvariant();
        return value switch
        {
            SrdEdition.Edition2014 => [SrdEdition.Edition2014],
            SrdEdition.Edition2024 => [SrdEdition.Edition2024],
            RulesTools.Both => [SrdEdition.Edition2014, SrdEdition.Edition2024],
            _ => throw new DndInputException($"edition must be \"2014\", \"2024\" or \"both\" (got \"{Echo(edition!)}\")."),
        };
    }

    /// <summary>
    /// Each item checked on its own terms before any lookup: exactly one of ref, name and cr (a name beside cr is its
    /// label), a label that fits on one line, and a count in range. Messages count items as the argument guard does
    /// ("monsters item 2: …"), so the model reads one vocabulary whichever layer caught the mistake.
    /// </summary>
    private static List<MonsterRequest> Requests(EncounterMonsterInput[]? monsters)
    {
        monsters ??= [];
        EncounterLimits.ValidateMonsterCount(monsters.Length, Examples);

        var requests = new List<MonsterRequest>(monsters.Length);
        for (var i = 0; i < monsters.Length; i++)
        {
            var position = i + 1;
            var where = $"monsters item {position.ToString(CultureInfo.InvariantCulture)}";
            var input = monsters[i] ?? throw new DndInputException($"{where}: is null; give an object. {Examples}");
            var refText = Blank(input.Ref);
            var name = Blank(input.Name);
            var cr = input.Cr is JsonElement crValue ? ParseCr(crValue, where) : (ChallengeRating?)null;
            EncounterLimits.ValidateCount(position, input.Count);

            if (refText is not null && (name is not null || cr is not null))
            {
                throw new DndInputException(
                    $"{where}: has both ref and {(name is not null ? "name" : "cr")}; give one: ref or name for an SRD monster, or cr " +
                    "(with name as its label) for any other.");
            }

            if (refText is null && name is null && cr is null)
            {
                throw new DndInputException(
                    $"{where}: names no monster; give ref or name for an SRD monster, or cr for any other, e.g. {{\"name\": \"Ogre\", " +
                    "\"count\": 2} or {\"cr\": \"3\", \"name\": \"Bandit boss\"}.");
            }

            if (cr is not null && name is not null && (name.Length > MaxLabelLength || name.Any(char.IsControl)))
            {
                throw new DndInputException(
                    $"{where}: with cr, name is only a label, one line of at most {MaxLabelLength} characters (got \"{Echo(Printable(name))}\").");
            }

            requests.Add(new MonsterRequest(position, refText, cr is null ? name : null, cr, cr is null ? null : name, input.Count, input.Exclude, input.Lair));
        }

        return requests;
    }

    /// <summary>
    /// The cr argument as a CR: a string ("1/2", "5", "0.5") or a JSON number (0.5, 5). The SDK binds the untyped
    /// property as a <see cref="JsonElement"/>. A number is read by its JSON text, like a string: read as a double, 1e-400
    /// underflowed to CR 0 and 1e1 passed as 10 while "1e1" was refused.
    /// </summary>
    private static ChallengeRating? ParseCr(JsonElement value, string where)
    {
        try
        {
            return value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => ChallengeRating.Parse(value.GetString()),
                JsonValueKind.Number => ChallengeRating.Parse(value.GetRawText()),
                _ => throw new DndInputException($"cr must be a string such as \"1/2\" or \"5\", or a number, but was {Describe(value)}."),
            };
        }
        catch (DndInputException ex)
        {
            throw new DndInputException($"{where}: {ex.Message}", ex);
        }
    }

    private static List<EncounterEntry> Resolve(IReadOnlyList<MonsterRequest> requests, IReadOnlyList<string> editions, SrdIndex? index) =>
        requests.Select(request => Resolve(request, editions, index)).ToList();

    private static EncounterEntry Resolve(MonsterRequest request, IReadOnlyList<string> editions, SrdIndex? index)
    {
        if (request.Cr is { } cr)
        {
            return ByCr(request, cr, editions);
        }

        ArgumentNullException.ThrowIfNull(index);
        var docs = request.RefText is { } refText && refText.Contains('/')
            ? ByRef(index, request, refText, editions)
            : ByName(index, request, request.RefText ?? request.NameText!, editions);

        var notes = new List<string>(docs.Notes);
        var resolved = new Dictionary<string, EncounterEntrySide>(StringComparer.Ordinal);
        foreach (var edition in editions)
        {
            var doc = docs.ByEdition[edition];
            var challenge = SrdMonsterChallenge.Read(doc);
            var xp = challenge.Xp;
            string? lairNote = null;
            if (doc.Edition == SrdEdition.Edition2024 && challenge.ChallengeRating == ChallengeRating.Zero && xp == ChallengeRatingTables.DefaultCrZeroXp)
            {
                // 26 of the 28 SRD 5.2.1 CR 0 stat blocks print "XP 0 or 10"; upstream stores 10.
                notes.Add($"{doc.Name} (`{doc.Ref}`): its SRD 5.2.1 stat block gives XP 0 or 10; {xp} is used.");
            }

            if (request.Lair)
            {
                if (challenge.XpInLair is { } inLair)
                {
                    xp = inLair;
                    lairNote = "in lair";
                }
                else
                {
                    notes.Add($"{doc.Name} (`{doc.Ref}`): its stat block gives no in-lair XP, so lair changes nothing; its normal XP is used.");
                }
            }

            resolved[edition] = new EncounterEntrySide(doc.Name, doc.Ref.ToString(), challenge.ChallengeRating, xp, lairNote);
        }

        return new EncounterEntry(resolved[editions[0]].Name, request.Count, request.Exclude, resolved, notes);
    }

    /// <summary>
    /// A monster given by CR: the XP table's value in every edition, labelled with its name or "CR n monster". In its lair
    /// a 2024 monster is worth the next CR's XP, as every SRD 5.2.1 stat block with an in-lair XP gives it; its CR stays.
    /// 2014 stat blocks have no in-lair XP, and CR 30 has no row above.
    /// </summary>
    private static EncounterEntry ByCr(MonsterRequest request, ChallengeRating cr, IReadOnlyList<string> editions)
    {
        var label = request.Label ?? $"CR {cr} monster";
        var notes = new List<string>();
        if (cr == ChallengeRating.Zero)
        {
            notes.Add($"{label}: CR 0 is worth 0 or 10 XP by its stat block; {ChallengeRatingTables.DefaultCrZeroXp} is used.");
        }

        var sides = new Dictionary<string, EncounterEntrySide>(StringComparer.Ordinal);
        foreach (var edition in editions)
        {
            var xp = ChallengeRatingTables.Xp(cr);
            string? xpNote = null;
            if (request.Lair && edition == SrdEdition.Edition2024 && cr.Next is { } next)
            {
                xp = ChallengeRatingTables.Xp(next);
                xpNote = "in lair";
                notes.Add(
                    $"{label}: in its lair a CR {cr} monster is worth the next CR's XP ({Number(xp)}), as every SRD 5.2.1 stat block " +
                    "with an in-lair XP gives it; its CR stays the same.");
            }
            else if (request.Lair)
            {
                notes.Add(edition == SrdEdition.Edition2024
                    ? $"{label}: CR 30 has no row above it, so lair changes nothing; its normal XP is used."
                    : $"{label}: 2014 stat blocks give no in-lair XP, so lair changes nothing for 2014.");
            }

            sides[edition] = new EncounterEntrySide(label, null, cr, xp, xpNote);
        }

        return new EncounterEntry(label, request.Count, request.Exclude, sides, notes);
    }

    /// <summary>
    /// The stat block for each edition from an explicit ref. One edition: the ref as given, even from the other edition
    /// (a 2014 stat block in a 2024 game is ordinary play). Both: the ref's own edition plus its counterpart.
    /// </summary>
    private static ResolvedDocs ByRef(SrdIndex index, MonsterRequest request, string refText, IReadOnlyList<string> editions)
    {
        var where = $"monsters item {request.Position.ToString(CultureInfo.InvariantCulture)}";
        var defaultEdition = editions.Count == 1 ? editions[0] : SrdEdition.Edition2024;
        SrdRef reference;
        try
        {
            reference = SrdRefParser.Parse(refText, defaultEdition);
        }
        catch (DndInputException ex)
        {
            throw new DndInputException($"{where}: {ex.Message} A monster's ref looks like \"2024/monster/ogre\"; name \"Ogre\" works too.", ex);
        }

        if (reference.Kind != SrdKinds.Monster)
        {
            throw new DndInputException(
                $"{where}: ref `{Echo(reference.ToString())}` is a {reference.Kind}, not a monster. Give a monster's ref (e.g. " +
                "\"2024/monster/ogre\"), its name, or a cr.");
        }

        var doc = index.Get(reference) ?? throw RefNotFound(index, reference, where);
        var byEdition = new Dictionary<string, SrdDocument>(StringComparer.Ordinal);
        var notes = new List<string>();
        foreach (var edition in editions)
        {
            if (edition == doc.Edition)
            {
                byEdition[edition] = doc;
            }
            else if (editions.Count == 1)
            {
                byEdition[edition] = doc;
                notes.Add($"{doc.Name}: `{doc.Ref}` is a {doc.Edition} stat block, used as given in this {edition} encounter.");
            }
            else
            {
                byEdition[edition] = OtherEdition(index, doc, typedName: null, edition, notes);
            }
        }

        return new ResolvedDocs(byEdition, notes);
    }

    /// <summary>
    /// The stat block for each edition by name: that edition's own match (or its shared form, <see cref="SharedForm"/>)
    /// first, then the other edition's counterpart, then the other edition's stat block with a note. Nothing in either
    /// edition is an error that lists close names before offering cr.
    /// </summary>
    private static ResolvedDocs ByName(SrdIndex index, MonsterRequest request, string name, IReadOnlyList<string> editions)
    {
        var where = $"monsters item {request.Position.ToString(CultureInfo.InvariantCulture)}";
        var lookups = new Dictionary<string, SrdNameLookup>(StringComparer.Ordinal);
        var own = new Dictionary<string, (SrdDocument Doc, string? Note)?>(StringComparer.Ordinal);
        foreach (var edition in SrdEdition.All)
        {
            try
            {
                lookups[edition] = index.FindByName(name, edition, SrdKinds.Monster);
            }
            catch (DndInputException ex)
            {
                throw new DndInputException($"{where}: {ex.Message}", ex);
            }

            own[edition] = lookups[edition].Best is { } match ? (match.Document, null) : SharedForm(index, name, edition);
        }

        var found = SrdEdition.All.Where(e => own[e] is not null).ToList();
        if (found.Count == 0)
        {
            throw NameNotFound(name, editions, lookups, where);
        }

        var byEdition = new Dictionary<string, SrdDocument>(StringComparer.Ordinal);
        var notes = new List<string>();
        foreach (var edition in editions)
        {
            var (doc, note) = own[edition] ?? own[found[0]]!.Value;
            if (note is not null)
            {
                notes.Add(note);
            }

            byEdition[edition] = doc.Edition == edition ? doc : OtherEdition(index, doc, name, edition, notes);
        }

        return new ResolvedDocs(byEdition, notes);
    }

    /// <summary>
    /// The stat block for a name the SRD data splits into forms ("Vampire" is "Vampire, Vampire Form", "Vampire, Bat
    /// Form" and "Vampire, Mist Form"; each lycanthrope likewise), when every form has the same CR and XP, as they all do:
    /// the form named after the creature, else the first. Both SRDs print one stat block titled "Vampire", so "no monster
    /// is named Vampire" was false, and it sent the model to guess a CR. Forms that differ in CR or XP are left to the
    /// not-found message, which lists them.
    /// </summary>
    private static (SrdDocument Doc, string? Note)? SharedForm(SrdIndex index, string name, string edition)
    {
        var key = SrdNames.Key(name);
        IReadOnlyList<SrdSearchHit> hits;
        try
        {
            hits = index.Search(name, [edition], [SrdKinds.Monster], SrdIndex.MaxSearchLimit).Hits;
        }
        catch (DndInputException)
        {
            return null;
        }

        var forms = hits
            .Where(h => h.Name.EndsWith(" Form", StringComparison.Ordinal) &&
                        h.Name.IndexOf(", ", StringComparison.Ordinal) is > 0 and var comma &&
                        SrdNames.Key(h.Name[..comma]) == key)
            .Select(h => index.Get(h.Ref))
            .OfType<SrdDocument>()
            .OrderBy(d => d.Slug, StringComparer.Ordinal)
            .ToList();
        if (forms.Count == 0 || forms.Select(f => SrdMonsterChallenge.Read(f)).Distinct().Count() != 1)
        {
            return null;
        }

        var chosen = forms.FirstOrDefault(f => SrdNames.Key(f.Name) == SrdNames.Key($"{name}, {name} Form")) ?? forms[0];
        var challenge = SrdMonsterChallenge.Read(chosen);
        return (chosen,
            $"{Echo(name)}: the {edition} SRD data splits this stat block into forms ({string.Join("; ", forms.Select(f => f.Name))}), " +
            $"which share CR {challenge.ChallengeRating} and {Number(challenge.Xp)} XP, so {chosen.Name} (`{chosen.Ref}`) is used.");
    }

    // The other edition's stat block for doc: its recorded counterpart (the one answering to the typed name first), or
    // doc itself with a note saying so.
    private static SrdDocument OtherEdition(SrdIndex index, SrdDocument doc, string? typedName, string edition, List<string> notes)
    {
        if (index.Counterparts(doc, typedName).FirstOrDefault(d => d.Kind == SrdKinds.Monster) is { } counterpart)
        {
            return counterpart;
        }

        notes.Add(
            $"{doc.Name}: the {edition} SRD has no counterpart of `{doc.Ref}`, so its {doc.Edition} stat block is used for the " +
            $"{edition} maths too.");
        return doc;
    }

    private static DndInputException RefNotFound(SrdIndex index, SrdRef reference, string where)
    {
        var message = $"{where}: no {reference.Edition} monster has the slug \"{Echo(reference.Slug)}\" (`{Echo(reference.ToString())}`).";
        var asName = reference.Slug.Replace('-', ' ');
        if (asName.Length <= SrdIndex.MaxNameLength && SrdNames.Key(asName).Length > 0)
        {
            var lookup = index.FindByName(asName, reference.Edition, SrdKinds.Monster);
            var close = lookup.Matches.Count > 0 ? lookup.Matches.Select(m => m.Document) : lookup.Suggestions;
            var refs = close.DistinctBy(d => d.Ref).Take(SrdIndex.MaxSuggestions).Select(d => $"{d.Name} (`{d.Ref}`)").ToList();
            if (refs.Count > 0)
            {
                message += $" Did you mean {string.Join(" or ", refs)}?";
            }
        }

        var otherEdition = reference.Edition == SrdEdition.Edition2014 ? SrdEdition.Edition2024 : SrdEdition.Edition2014;
        if (index.Get(otherEdition, SrdKinds.Monster, reference.Slug) is { } other)
        {
            message += $" The {otherEdition} SRD has {other.Name} (`{other.Ref}`): pass that ref, or name \"{other.Name}\", which also " +
                       $"finds its {reference.Edition} counterpart when there is one.";
        }

        return new DndInputException(message + " For a monster not in the SRD, give its cr instead.");
    }

    // "No monster is named X" must not read as "so guess a CR": when the SRD has close names (its "Vampire, Vampire Form"
    // style), those come first, and the CR route is offered for a monster the SRD does not have.
    private static DndInputException NameNotFound(
        string name, IReadOnlyList<string> editions, IReadOnlyDictionary<string, SrdNameLookup> lookups, string where)
    {
        // Both editions: resolution falls back to the other one, so a close name there is a real answer (2024
        // "Svirfneblin" is close only to 2014's Deep Gnome).
        var close = editions.Concat(SrdEdition.All.Except(editions))
            .SelectMany(e => lookups[e].Suggestions)
            .DistinctBy(d => d.Ref)
            .Take(SrdIndex.MaxSuggestions)
            .Select(d => $"{d.Name} (`{d.Ref}`)")
            .ToList();
        var suggestion = close.Count > 0
            ? $" Close SRD names: {string.Join(", ", close)}; if one is the monster you mean, pass its ref."
            : string.Empty;
        return new DndInputException(
            $"{where}: no monster in the 2014 or 2024 SRD is named \"{Echo(name)}\".{suggestion} For a monster the SRD does not " +
            "have (it has only some of the Monster Manual), give the CR from its stat block, with name as a label: " +
            $"{{\"name\": \"{JsonText(Echo(name))}\", \"cr\": \"<its CR>\"}}.");
    }

    // Text safe inside a JSON string in an example: a name with a quote must not break the example the model copies.
    private static string JsonText(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static List<EncounterMonster> Monsters(IReadOnlyList<EncounterEntry> entries, string edition) =>
        entries.Select(e =>
        {
            var side = e.Sides[edition];
            // A CR-only monster is its own stat block per (CR, label): two "Bandit" entries at CR 1 and CR 8 are two blocks,
            // and keying by the label alone merged them and hid the CR 8 one from the troubleshooting checks.
            var statBlock = side.Ref ?? $"cr:{side.ChallengeRating}:{side.Name}";
            return new EncounterMonster(side.Name, statBlock, side.ChallengeRating, side.Xp, e.Count, e.Exclude);
        }).ToList();

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False => $"the boolean {value.GetRawText()}",
        JsonValueKind.Null => "null",
        JsonValueKind.Array => "an array",
        JsonValueKind.Object => "an object",
        _ => value.GetRawText(),
    };

    private static string Echo(string text) => text.Length <= MaxEchoLength ? text : text[..MaxEchoLength] + "…";

    // Control characters written as escapes, so a refused label with a newline comes back on one line.
    private static string Printable(string text) =>
        string.Concat(text.Select(c => c switch
        {
            '\n' => "\\n",
            '\r' => "\\r",
            '\t' => "\\t",
            _ when char.IsControl(c) => $"\\u{(int)c:x4}",
            _ => c.ToString(),
        }));

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private sealed record ResolvedDocs(IReadOnlyDictionary<string, SrdDocument> ByEdition, IReadOnlyList<string> Notes);
}

/// <summary>One monsters item, checked: which of ref, name or cr it gives, and the rest of its settings.</summary>
/// <param name="Position">1-based, as messages count items.</param>
/// <param name="Label">A CR-only monster's display name, from its name argument.</param>
internal sealed record MonsterRequest(
    int Position, string? RefText, string? NameText, ChallengeRating? Cr, string? Label, int Count, bool Exclude, bool Lair)
{
    public bool NeedsIndex => Cr is null;
}
