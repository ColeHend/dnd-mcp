using System.Text.Json;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;

namespace DndMcp.Formatting.Srd;

/// <summary>
/// Body formatter for the small reference kinds: ability scores, skills, proficiencies, languages and alignments.
///
/// <para>
/// These records are mostly relations (a skill's ability, the classes that grant a proficiency, the thing a
/// proficiency covers), and the relations are what a reader looks them up for, so each is a linked field line and the
/// prose follows. Several records have no prose at all (every proficiency, most 2014 and 2024 languages); their fields
/// are the whole body, which is why each kind always prints at least its defining field.
/// </para>
/// </summary>
internal static class ReferenceMarkdown
{
    public static string Body(SrdDocument doc, ISrdLookup lookup)
    {
        var root = doc.Root;
        var fields = doc.Kind switch
        {
            SrdKinds.AbilityScore => SrdMarkdownText.Field("Full Name", root.Str("full_name")),
            SrdKinds.Skill => SrdMarkdownText.Field("Ability", root.Obj("ability_score") is { } a ? SrdMarkdownText.Link(a) : null),
            SrdKinds.Proficiency => ProficiencyFields(root),
            SrdKinds.Language => LanguageFields(root),
            SrdKinds.Alignment => SrdMarkdownText.Field("Abbreviation", root.Str("abbreviation")),
            _ => null,
        };

        // 2024 languages keep their only prose in "note" ("Primordial includes the Aquan, Auran, …").
        var text = SrdProse.Join(root.Description().Concat(root.Paragraphs("note")));
        var skills = SrdMarkdownText.Field("Skills", SrdMarkdownText.LinkList(root.Arr("skills")));

        var body = SrdMarkdownText.Blocks([fields, text, skills]);
        return string.IsNullOrWhiteSpace(body) ? GenericMarkdown.Body(doc, lookup) : body;
    }

    private static string ProficiencyFields(JsonElement root) => SrdMarkdownText.Lines(
    [
        SrdMarkdownText.Field("Type", root.Str("type")),
        SrdMarkdownText.Field("Classes", SrdMarkdownText.LinkList(root.Arr("classes"))),
        SrdMarkdownText.Field("Races", SrdMarkdownText.LinkList(root.Arr("races"))),
        SrdMarkdownText.Field("Backgrounds", SrdMarkdownText.LinkList(root.Arr("backgrounds"))),
        SrdMarkdownText.Field("Reference", root.Obj("reference") is { } reference ? SrdMarkdownText.Link(reference) : null),
    ]);

    // 2014: type, typical speakers, script. 2024 only says whether a language is rare; the SRD 5.2.1 splits its
    // language tables into "Standard" and "Rare", so those are the words used.
    private static string LanguageFields(JsonElement root) => SrdMarkdownText.Lines(
    [
        SrdMarkdownText.Field("Type", root.Str("type") ?? root.Bool("is_rare") switch
        {
            true => "Rare",
            false => "Standard",
            null => null,
        }),
        SrdMarkdownText.Field("Typical Speakers", string.Join(", ", root.Arr("typical_speakers")
            .Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString()))),
        SrdMarkdownText.Field("Script", root.Str("script")),
    ]);
}
