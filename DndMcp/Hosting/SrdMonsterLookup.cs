using System.Globalization;
using DndMcp.Domain.Core;
using DndMcp.Domain.Encounters;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;

namespace DndMcp.Hosting;

/// <summary>
/// An SRD monster named by a caller — by ref or by name — as the stat block each edition of the answer uses. Shared by
/// every tool that takes a monster (<c>encounter_difficulty</c>, <c>balance_simulate</c> through
/// <see cref="StatBlockService"/>, <c>balance_dpr</c>'s <c>target.monster</c>), so "Ogre" means the same stat block
/// everywhere and every tool refuses a name the same way.
///
/// <para>
/// <b>The rules</b> (first written for <c>encounter_difficulty</c>):
/// <list type="bullet">
/// <item>A ref with a slash is used as given for a single edition, even from the other edition (a 2014 stat block in a
/// 2024 game is ordinary play, with a note); for both editions the other side is its recorded counterpart. A "ref"
/// without a slash ("ogre") is read as a name: models pass a name as ref often enough.</item>
/// <item>A name is looked up in each edition: that edition's own match first, or its shared form (<see cref="SharedForm"/>),
/// then the other edition's recorded counterpart, then the other edition's stat block with a note. Nothing in either
/// edition is an error listing close names in both, then saying what to give for a monster the SRD lacks.</item>
/// </list>
/// </para>
/// <para>
/// <b>Only the wording differs per tool</b> (<see cref="MonsterLookupWording"/>): what the fallback for a monster the SRD
/// lacks is (a CR, a build, a target's own numbers), and whether the result is "this 2024 encounter" or "this 2024 fight".
/// The model reads the tool's own vocabulary; the lookup underneath is one piece of code.
/// </para>
/// </summary>
internal static class SrdMonsterLookup
{
    private const int MaxEchoLength = 80;

    /// <summary>
    /// The stat block for each edition in <paramref name="editions"/>, from <paramref name="text"/> (a ref if it contains a
    /// slash, else a name), with the notes a result must show (a form chosen, another edition's stat block used).
    /// </summary>
    /// <exception cref="DndInputException">Not a monster ref, or no monster by that ref or name; the message starts with <see cref="MonsterLookupWording.Where"/>.</exception>
    public static MonsterDocuments Resolve(SrdIndex index, string text, IReadOnlyList<string> editions, MonsterLookupWording wording)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return text.Contains('/') ? ByRef(index, text, editions, wording) : ByName(index, text, editions, wording);
    }

    /// <summary>
    /// The stat block for each edition from an explicit ref. One edition: the ref as given, even from the other edition
    /// (a 2014 stat block in a 2024 game is ordinary play). Both: the ref's own edition plus its counterpart.
    /// </summary>
    private static MonsterDocuments ByRef(SrdIndex index, string refText, IReadOnlyList<string> editions, MonsterLookupWording wording)
    {
        var where = wording.Where;
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
                $"{where}: ref `{Echo(reference.ToString())}` is a {reference.Kind}, not a monster. {wording.NotAMonsterAdvice}");
        }

        var doc = index.Get(reference) ?? throw RefNotFound(index, reference, wording);
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
                notes.Add($"{doc.Name}: `{doc.Ref}` is a {doc.Edition} stat block, used as given in this {edition} {wording.Occasion}.");
            }
            else
            {
                byEdition[edition] = OtherEdition(index, doc, typedName: null, edition, notes, wording);
            }
        }

        return new MonsterDocuments(byEdition, notes);
    }

    /// <summary>
    /// The stat block for each edition by name: that edition's own match (or its shared form, <see cref="SharedForm"/>)
    /// first, then the other edition's counterpart, then the other edition's stat block with a note. Nothing in either
    /// edition is an error that lists close names before offering the tool's fallback.
    /// </summary>
    private static MonsterDocuments ByName(SrdIndex index, string name, IReadOnlyList<string> editions, MonsterLookupWording wording)
    {
        var where = wording.Where;
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
            throw NameNotFound(name, editions, lookups, wording);
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

            byEdition[edition] = doc.Edition == edition ? doc : OtherEdition(index, doc, name, edition, notes, wording);
        }

        return new MonsterDocuments(byEdition, notes);
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
    private static SrdDocument OtherEdition(
        SrdIndex index, SrdDocument doc, string? typedName, string edition, List<string> notes, MonsterLookupWording wording)
    {
        if (index.Counterparts(doc, typedName).FirstOrDefault(d => d.Kind == SrdKinds.Monster) is { } counterpart)
        {
            return counterpart;
        }

        notes.Add(
            $"{doc.Name}: the {edition} SRD has no counterpart of `{doc.Ref}`, so its {doc.Edition} stat block is used " +
            $"{wording.OtherEditionUse(edition)}.");
        return doc;
    }

    private static DndInputException RefNotFound(SrdIndex index, SrdRef reference, MonsterLookupWording wording)
    {
        var message = $"{wording.Where}: no {reference.Edition} monster has the slug \"{Echo(reference.Slug)}\" (`{Echo(reference.ToString())}`).";
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

        return new DndInputException(message + " " + wording.RefNotFoundAdvice);
    }

    // "No monster is named X" must not read as "so guess the numbers": when the SRD has close names (its "Vampire, Vampire
    // Form" style), those come first, and the tool's fallback is offered for a monster the SRD does not have.
    private static DndInputException NameNotFound(
        string name, IReadOnlyList<string> editions, IReadOnlyDictionary<string, SrdNameLookup> lookups, MonsterLookupWording wording)
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
            $"{wording.Where}: no monster in the 2014 or 2024 SRD is named \"{Echo(name)}\".{suggestion} " +
            wording.NameNotFoundAdvice(JsonText(Echo(name))));
    }

    /// <summary>Text safe inside a JSON string in an example: a name with a quote must not break the example the model copies.</summary>
    internal static string JsonText(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");

    internal static string Echo(string text) => text.Length <= MaxEchoLength ? text : text[..MaxEchoLength] + "…";

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}

/// <summary>
/// What a tool says around <see cref="SrdMonsterLookup"/>'s answers: the item a message is about, the occasion the stat
/// block is used in, and what to give for a monster the SRD lacks. Each tool words these in its own vocabulary, so the
/// model is never told to "give a cr" to a tool that takes none.
/// </summary>
/// <param name="Where">The message subject, as the argument guard counts items: "monsters item 2", "enemies item 1 (Orge)".</param>
/// <param name="Occasion">"encounter", "fight": "used as given in this 2024 {Occasion}".</param>
/// <param name="OtherEditionUse">
/// How another edition's stat block is used when an edition has no counterpart: "for the 2024 maths too", "in this 2024
/// fight". Takes the edition that lacks one.
/// </param>
/// <param name="NotAMonsterAdvice">After "ref `…` is a spell, not a monster.": what to give instead.</param>
/// <param name="RefNotFoundAdvice">The last sentence of a ref-not-found message: the fallback for a monster the SRD lacks.</param>
/// <param name="NameNotFoundAdvice">The same for a name, given the name as JSON-safe text for an example the model can copy.</param>
public sealed record MonsterLookupWording(
    string Where,
    string Occasion,
    Func<string, string> OtherEditionUse,
    string NotAMonsterAdvice,
    string RefNotFoundAdvice,
    Func<string, string> NameNotFoundAdvice);

/// <summary>The stat block document each edition uses, and the notes a result shows about how they were chosen.</summary>
internal sealed record MonsterDocuments(IReadOnlyDictionary<string, SrdDocument> ByEdition, IReadOnlyList<string> Notes);
