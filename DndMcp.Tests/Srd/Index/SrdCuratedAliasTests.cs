using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.Tests.Srd.Index;

/// <summary>
/// Which names besides its own a document answers to, where that is a curated decision rather than a rule: the 2014
/// subsection headings that name a rule a player looks up ("Grappling" inside Melee Attacks), the 2024 names a 2014
/// section covers without a heading of that name ("Unarmed Strike"), and the few everyday names no record carries
/// ("Shove", "Damage Resistance").
///
/// <para>
/// What breaks without the curation: every <c>####</c> heading used to be an exact name, so 2014 "Alignment" answered
/// with Creating Sentient Magic Items (its "#### Alignment" is about a sentient sword), "Bonus Action" with Casting
/// Time's paragraph on bonus-action spells, "Spellcasting Ability" with Charisma, and the tool then said "Alignment is
/// covered by this entry's #### Alignment subsection". Before those aliases existed these names were honest misses.
/// And a 2024 name stored as a heading made the tool name subsections that do not exist ("Unarmed Strike subsection").
/// </para>
/// </summary>
public sealed class SrdCuratedAliasTests : IClassFixture<SrdIndexFixture>
{
    private readonly SrdIndexFixture _fixture;
    private readonly SrdIndex _index;

    public SrdCuratedAliasTests(SrdIndexFixture fixture)
    {
        _fixture = fixture;
        _index = fixture.Index;
    }

    // Sub-attributes of one narrow section, and headings several sections share, are not names of the section.
    [Theory]
    [InlineData("Alignment", "2014/rule/creating-sentient-magic-items")]
    [InlineData("Senses", "2014/rule/creating-sentient-magic-items")]
    [InlineData("Abilities", "2014/rule/creating-sentient-magic-items")]
    [InlineData("Communication", "2014/rule/creating-sentient-magic-items")]
    [InlineData("Special Purpose", "2014/rule/creating-sentient-magic-items")]
    [InlineData("Bonus Action", "2014/rule/casting-time")]
    [InlineData("Reactions", "2014/rule/casting-time")]
    [InlineData("Spellcasting Ability", "2014/rule/charisma")]
    [InlineData("Spellcasting Ability", "2014/rule/intelligence")]
    [InlineData("Attack Rolls and Damage", "2014/rule/strength")]
    [InlineData("Armor Class", "2014/rule/dexterity")]
    [InlineData("Initiative", "2014/rule/dexterity")]
    [InlineData("Hit Points", "2014/rule/constitution")]
    [InlineData("Strength", "2014/rule/skills")]
    [InlineData("Range", "2014/rule/ranged-attacks")]
    [InlineData("Instantaneous", "2014/rule/duration")]
    [InlineData("Modifiers to the Roll", "2014/rule/attack-rolls")]
    public void FindByName_HeadingThatIsASubAttribute_IsNotANameOfItsSection(string name, string section)
    {
        var lookup = _index.FindByName(name, "2014");

        Assert.DoesNotContain(section, lookup.Matches.Select(m => m.Document.Ref.ToString()));
    }

    // The general rule, not the spell-specific paragraph: 2014 "Bonus Action" is Your Turn's "#### Bonus Actions",
    // the section edition "both" already compares with 2024 Bonus Action.
    [Fact]
    public void FindByName_BonusAction2014_IsTheYourTurnRuleAlone()
    {
        var lookup = _index.FindByName("Bonus Action", "2014");

        Assert.Equal(["2014/rule/your-turn"], lookup.Matches.Select(m => m.Document.Ref.ToString()));
    }

    [Theory]
    [InlineData("alignment")]
    [InlineData("senses")]
    [InlineData("abilities")]
    public void Search_2014GeneralWord_DoesNotPutANarrowSectionFirst(string query)
    {
        var first = _index.Search(query, ["2014"], limit: 1).Hits[0];

        Assert.NotEqual("2014/rule/creating-sentient-magic-items", first.Ref.ToString());
    }

    [Fact]
    public void Search_BonusAction2014_RanksYourTurnFirst()
    {
        Assert.Equal("2014/rule/your-turn", _index.Search("bonus action", ["2014"], limit: 1).Hits[0].Ref.ToString());
    }

    // Headings that ARE the rule a player asks for keep answering for their section.
    [Theory]
    [InlineData("Grappling", "2014/rule/melee-attacks")]
    [InlineData("Shoving a Creature", "2014/rule/melee-attacks")]
    [InlineData("Opportunity Attacks", "2014/rule/melee-attacks")]
    [InlineData("Critical Hits", "2014/rule/damage-rolls")]
    [InlineData("Death Saving Throws", "2014/rule/dropping-to-0-hit-points")]
    [InlineData("Concentration", "2014/rule/duration")]
    [InlineData("Hiding", "2014/rule/dexterity")]
    [InlineData("Bonus Actions", "2014/rule/your-turn")]
    [InlineData("Variant: Encumbrance", "2014/rule/strength")]
    [InlineData("Travel Pace", "2014/rule/speed")]
    [InlineData("Cackle Fever", "2014/rule/sample-diseases")]
    public void FindByName_HeadingThatNamesARule_IsTheSectionHoldingIt(string name, string section)
    {
        var best = _index.FindByName(name, "2014").Best!;

        Assert.Equal(section, best.Document.Ref.ToString());
        Assert.Equal("heading", best.Source);
    }

    /// <summary>
    /// A 2024 entry's name that the 2014 section covers without a heading of that name is a <c>section</c> alias, not a
    /// <c>heading</c> one, so nothing downstream names a subsection that is not there: Melee Attacks mentions an unarmed
    /// strike in one sentence, and Special Types of Movement has "#### Climbing, Swimming, and Crawling", not
    /// "#### Climbing". A name that IS a heading (Jumping) stays a heading.
    /// </summary>
    [Theory]
    [InlineData("2014/rule/melee-attacks", "Unarmed Strike", "section")]
    [InlineData("2014/rule/special-types-of-movement", "Climbing", "section")]
    [InlineData("2014/rule/special-types-of-movement", "High Jump", "section")]
    [InlineData("2014/rule/special-types-of-movement", "Jumping", "heading")]
    [InlineData("2014/rule/special-types-of-movement", "Climbing, Swimming, and Crawling", "heading")]
    [InlineData("2014/rule/damage-rolls", "Critical Hit", "section")]
    [InlineData("2014/rule/damage-rolls", "Critical Hits", "heading")]
    [InlineData("2014/rule/dropping-to-0-hit-points", "Death Saving Throw", "section")]
    [InlineData("2014/rule/your-turn", "Bonus Action", "section")]
    [InlineData("2014/rule/your-turn", "Bonus Actions", "heading")]
    [InlineData("2014/rule/sample-poisons", "Serpent Venom", "section")]
    [InlineData("2014/rule/melee-attacks", "Grappling", "heading")]
    public void AliasTable_SectionPairName_IsAHeadingOnlyWhenTheSectionHasThatHeading(string reference, string alias, string source)
    {
        Assert.Equal([source], Sources(reference, alias));
    }

    // Every section alias is the name of a 2024 entry the section is paired with; nothing else is stored as one.
    [Fact]
    public void AliasTable_EverySectionAlias_NamesASectionPairedEntry()
    {
        var orphans = _fixture.Column(
            """
            SELECT d.slug || ': ' || a.alias FROM alias a JOIN doc d ON d.id = a.doc_id
            WHERE a.source = 'section' AND NOT EXISTS (
                SELECT 1 FROM counterpart c JOIN doc n ON n.id = c.doc_2024
                WHERE c.doc_2014 = d.id AND c.source = 'section' AND n.name_key = a.alias_key);
            """);

        Assert.Empty(orphans);
        Assert.True(_fixture.Scalar("SELECT COUNT(*) FROM alias WHERE source = 'section';") > 0);
    }

    /// <summary>
    /// Everyday names no record carries. "Shove" missed in both editions and suggested the Shovel; 2024 "Damage
    /// Resistance" answered with five dragonborn traits (the 2014 trait's name), never the Resistance rule. Each ranks
    /// like another edition's name (an alias of a content kind), so it beats nothing that owns the name.
    /// </summary>
    [Theory]
    [InlineData("Shove", "2024", "2024/rule/unarmed-strike")]
    [InlineData("Shoving", "2024", "2024/rule/unarmed-strike")]
    [InlineData("Shoving a Creature", "2024", "2024/rule/unarmed-strike")]
    [InlineData("Shove", "2014", "2014/rule/melee-attacks")]
    [InlineData("Shoving", "2014", "2014/rule/melee-attacks")]
    [InlineData("Grapple", "2024", "2024/rule/grappling")]
    [InlineData("Grapple", "2014", "2014/rule/melee-attacks")]
    [InlineData("Damage Resistance", "2024", "2024/rule/resistance")]
    public void FindByName_CuratedName_IsTheRuleThatCoversIt(string name, string edition, string expected)
    {
        var best = _index.FindByName(name, edition).Best;

        Assert.Equal(expected, best?.Document.Ref.ToString());
        Assert.Equal("curated", best!.Source);
    }

    [Theory]
    [InlineData("damage resistance", "2024", "2024/rule/resistance")]
    [InlineData("shove", "2024", "2024/rule/unarmed-strike")]
    [InlineData("grapple", "2014", "2014/rule/melee-attacks")]
    public void Search_CuratedName_RanksItsRuleFirst(string query, string edition, string expected)
    {
        Assert.Equal(expected, _index.Search(query, [edition], limit: 1).Hits[0].Ref.ToString());
    }

    // The sources an alias row of one document has (one at most: the alias key is the primary key per document).
    private IReadOnlyList<string> Sources(string reference, string alias) =>
        _fixture.Column(
            $"""
            SELECT a.source FROM alias a JOIN doc d ON d.id = a.doc_id
            WHERE d.edition || '/' || d.kind || '/' || d.slug = '{reference}' AND a.alias = '{alias.Replace("'", "''", StringComparison.Ordinal)}';
            """);
}
