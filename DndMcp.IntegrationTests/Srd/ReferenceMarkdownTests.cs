using DndMcp.Formatting.Srd;
using DndMcp.Repository.Srd;
using Xunit;

namespace DndMcp.IntegrationTests.Srd;

/// <summary>
/// Invariant: ability score, skill, proficiency, language and alignment bodies show each record's defining fields and
/// relations (linked) and its prose; and every record of the sixteen kinds the class, origin and reference formatters
/// handle gets a structured body, not the description-only fallback.
///
/// <para>
/// Many of these records have no prose at all (every proficiency, most languages), so without the field lines their
/// body would be the fallback's "No description" and <c>rules_get</c> would answer "what is Thieves' Tools proficiency"
/// with nothing.
/// </para>
/// </summary>
public sealed class ReferenceMarkdownTests
{
    private static readonly VendoredSrdLookup Lookup = VendoredSrdLookup.Instance;

    private static readonly string[] CharacterKinds =
    [
        SrdKinds.Class, SrdKinds.Subclass, SrdKinds.Level, SrdKinds.Feature,
        SrdKinds.Race, SrdKinds.Subrace, SrdKinds.Species, SrdKinds.Subspecies, SrdKinds.Trait, SrdKinds.Background, SrdKinds.Feat,
        SrdKinds.AbilityScore, SrdKinds.Skill, SrdKinds.Proficiency, SrdKinds.Language, SrdKinds.Alignment,
    ];

    public static TheoryData<string, string> CharacterEditionKinds()
    {
        var data = new TheoryData<string, string>();
        foreach (var edition in SrdEdition.All)
        {
            foreach (var kind in CharacterKinds.Where(k => SrdKinds.ExistsIn(k, edition)))
            {
                data.Add(edition, kind);
            }
        }

        return data;
    }

    [Theory]
    [InlineData("2014/ability-score/str", "**Full Name** Strength")]
    [InlineData("2014/ability-score/str", "**Skills** Athletics (`2014/skill/athletics`)")]
    [InlineData("2014/ability-score/str",
        "Strength measures bodily power, athletic training, and the extent to which you can exert raw physical force.")]
    [InlineData("2024/ability-score/str", "Physical might")]
    [InlineData("2024/ability-score/dex",
        "**Skills** Acrobatics (`2024/skill/acrobatics`), Sleight of Hand (`2024/skill/sleight-of-hand`), Stealth (`2024/skill/stealth`)")]
    [InlineData("2014/skill/acrobatics", "**Ability** DEX (`2014/ability-score/dex`)")]
    [InlineData("2024/skill/acrobatics", "**Ability** DEX (`2024/ability-score/dex`)")]
    [InlineData("2024/skill/acrobatics", "Stay on your feet in a tricky situation, or perform an acrobatic stunt.")]
    [InlineData("2014/proficiency/all-armor", "**Type** Armor")]
    [InlineData("2014/proficiency/all-armor", "**Classes** Fighter (`2014/class/fighter`), Paladin (`2014/class/paladin`)")]
    [InlineData("2014/proficiency/all-armor", "**Reference** Armor (`2014/equipment-category/armor`)")]
    [InlineData("2014/proficiency/battleaxes", "**Races** Dwarf (`2014/race/dwarf`)")]
    [InlineData("2014/proficiency/skill-acrobatics", "**Reference** Acrobatics (`2014/skill/acrobatics`)")]
    [InlineData("2024/proficiency/tool-thieves-tools", "**Type** Tools")]
    [InlineData("2024/proficiency/tool-thieves-tools", "**Backgrounds** Criminal (`2024/background/criminal`)")]
    [InlineData("2024/proficiency/tool-thieves-tools", "**Reference** Thieves' Tools (`2024/equipment/thieves-tools`)")]
    [InlineData("2014/language/common", "**Type** Standard")]
    [InlineData("2014/language/common", "**Typical Speakers** Humans")]
    [InlineData("2014/language/common", "**Script** Common")]
    [InlineData("2014/language/draconic", "**Typical Speakers** Dragons, Dragonborn")]
    [InlineData("2014/language/elvish",
        "Elvish is fluid, with subtle intonations and intricate grammar. Elven literature is rich and varied, and their songs and poems are famous among other races. Many bards learn their language so they can add Elvish ballads to their repertoires.")]
    [InlineData("2024/language/abyssal", "**Type** Rare")]
    [InlineData("2024/language/common", "**Type** Standard")]
    [InlineData("2024/language/primordial",
        "Primordial includes the Aquan, Auran, Ignan, and Terran dialects. Creatures that know one of these dialects can communicate with those that know a different one.")]
    [InlineData("2014/alignment/lawful-good", "**Abbreviation** LG")]
    [InlineData("2014/alignment/lawful-good",
        "Lawful good (LG) creatures can be counted on to do the right thing as expected by society. Gold dragons, paladins, and most dwarves are lawful good.")]
    [InlineData("2024/alignment/unaligned", "**Abbreviation** U")]
    public void Body_ReferenceRecord_PinsFieldsLinksAndProse(string reference, string expectedLine)
    {
        Assert.Contains(expectedLine, BodyLines(reference));
    }

    [Theory]
    // Constitution has no skills; Deep Speech has no script; a skill proficiency is granted by no class directly.
    [InlineData("2014/ability-score/con", "**Skills**")]
    [InlineData("2024/ability-score/con", "**Skills**")]
    [InlineData("2014/language/deep-speech", "**Script**")]
    [InlineData("2014/proficiency/skill-acrobatics", "**Classes**")]
    [InlineData("2024/language/common", "**Typical Speakers**")]
    public void Body_FieldWithNothingToShow_IsOmitted(string reference, string label)
    {
        Assert.DoesNotContain(BodyLines(reference), l => l.StartsWith(label, StringComparison.Ordinal));
    }

    [Fact]
    public void Body_LanguageWithOnlyAnEmptyNote_IsJustItsType()
    {
        Assert.Equal("**Type** Standard", SrdMarkdown.Body(Lookup.Require("2024/language/common"), Lookup));
    }

    [Theory]
    [MemberData(nameof(CharacterEditionKinds))]
    public void Body_EveryDocumentOfACharacterKind_IsStructuredRatherThanTheDescriptionFallback(string edition, string kind)
    {
        foreach (var doc in Lookup.OfKind(edition, kind))
        {
            var body = SrdMarkdown.Body(doc, Lookup);

            Assert.NotEqual(GenericMarkdown.Body(doc, Lookup), body);
            Assert.Contains("**", body, StringComparison.Ordinal);
        }
    }

    private static string[] BodyLines(string reference) =>
        SrdMarkdown.Body(Lookup.Require(reference), Lookup).Split('\n');
}
