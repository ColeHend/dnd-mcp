using System.Text.Json;
using DndMcp.Repository.Srd.Index;
using DndMcp.Tools;
using Xunit;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: when a name reaches an entry it is not the title of — one of its subsection headings (2014 "Grappling" is
/// a <c>####</c> heading inside Melee Attacks), a 2024 name a 2014 section covers ("Unarmed Strike"), or a curated
/// everyday name ("Shove") — <c>rules_get</c> says the entry covers it, naming the heading or run-in paragraph that holds
/// it only when the text has one; and it says nothing when the name reached the entry any other way.
///
/// <para>
/// Without the note, "Grappling" answered with "# Melee Attacks" reads as the wrong entry, and a model either distrusts
/// a correct answer or quotes the whole section as the grappling rule. A note that names a subsection the text does not
/// have ("this entry's Unarmed Strike subsection", for one sentence of Melee Attacks) sends the reader looking for it. The
/// note is built from the document's aliases and its own text, so these tests use hand-made documents: the aliases
/// themselves come from the index, whose tests pin which headings and names become aliases.
/// </para>
/// </summary>
public sealed class RulesToolsNotesTests
{
    private const string MeleeAttacksText =
        "Used in hand-to-hand combat, a melee attack allows you to attack a foe within your reach.\n\n" +
        "#### Opportunity Attacks\n\nIn a fight, everyone is constantly watching for a chance to strike.\n\n" +
        "#### Grappling\n\nWhen you want to grab a creature or wrestle with it, you can use the Attack action.";

    [Theory]
    [InlineData("Grappling")]
    [InlineData("grappling")]
    [InlineData("GRAPPLING")]
    public void HeadingNote_NameIsASubsectionHeading_NamesThatSubsectionWithItsLevel(string name)
    {
        var doc = Rule("melee-attacks", "Melee Attacks", MeleeAttacksText, new SrdAlias("Grappling", SrdAliasSources.Heading));

        Assert.Equal("*Grappling is covered by this entry's #### Grappling subsection.*", RulesTools.HeadingNote(doc, name));
    }

    [Fact]
    public void HeadingNote_FiveHashHeading_KeepsItsLevel()
    {
        var doc = Rule(
            "strength",
            "Strength",
            "Strength measures bodily power.\n\n#### Lifting and Carrying\n\nYour Strength score determines…\n\n##### Variant: Encumbrance\n\nThe rules…",
            new SrdAlias("Variant: Encumbrance", SrdAliasSources.Heading));

        Assert.Equal(
            "*Variant: Encumbrance is covered by this entry's ##### Variant: Encumbrance subsection.*",
            RulesTools.HeadingNote(doc, "Variant Encumbrance"));
    }

    [Theory]
    // The heading is plural and the question singular (or the other way round); the lookup may bridge that.
    [InlineData("Critical Hit")]
    [InlineData("Critical Hits")]
    public void HeadingNote_SingularOrPluralOfTheHeading_StillNamesIt(string name)
    {
        var doc = Rule(
            "damage-rolls",
            "Damage Rolls",
            "Each weapon, spell, and harmful monster ability specifies the damage it deals.\n\n#### Critical Hits\n\nWhen you score…",
            new SrdAlias("Critical Hits", SrdAliasSources.Heading));

        Assert.Equal("*Critical Hits is covered by this entry's #### Critical Hits subsection.*", RulesTools.HeadingNote(doc, name));
    }

    [Fact]
    public void HeadingNote_SingularAliasBesideThePluralHeading_NamesTheHeadingAsWritten()
    {
        // The index gives the 2024 glossary's singular "Critical Hit" as a section alias beside the "Critical Hits" heading;
        // the note quotes the text's own heading.
        var doc = Rule(
            "damage-rolls",
            "Damage Rolls",
            "Each weapon, spell, and harmful monster ability specifies the damage it deals.\n\n#### Critical Hits\n\nWhen you score…",
            new SrdAlias("Critical Hits", SrdAliasSources.Heading),
            new SrdAlias("Critical Hit", SrdAliasSources.Section));

        Assert.Equal("*Critical Hit is covered by this entry's #### Critical Hits subsection.*", RulesTools.HeadingNote(doc, "critical hit"));
    }

    [Fact]
    public void HeadingNote_RunInHeading_NamesItsParagraph()
    {
        // 2014 Sample Poisons introduces each poison with a bold-italic run-in heading, not a markdown one.
        var doc = Rule(
            "sample-poisons",
            "Sample Poisons",
            "Each type of poison has its own debilitating effects.\n\n***Serpent Venom (Injury).*** This poison must be harvested from a " +
            "dead or incapacitated giant poisonous snake.",
            new SrdAlias("Serpent Venom", SrdAliasSources.Heading));

        Assert.Equal("*Serpent Venom is covered by this entry's Serpent Venom (Injury) paragraph.*", RulesTools.HeadingNote(doc, "Serpent Venom"));
    }

    [Theory]
    // The entry's own name: nothing to explain.
    [InlineData("Melee Attacks")]
    // A name that is not one of its headings.
    [InlineData("Shoving a Creature")]
    public void HeadingNote_NameIsNotAHeadingAlias_IsNull(string name)
    {
        var doc = Rule("melee-attacks", "Melee Attacks", MeleeAttacksText, new SrdAlias("Grappling", SrdAliasSources.Heading));

        Assert.Null(RulesTools.HeadingNote(doc, name));
    }

    [Theory]
    [InlineData(SrdAliasSources.Counterpart)]
    [InlineData(SrdAliasSources.Qualifier)]
    public void HeadingNote_AliasFromAnotherSource_IsNull(string source)
    {
        // "Tough" finds 2014 Thug through its 2024 name; that is not a subsection, and MetaLine already says so.
        var doc = Rule("melee-attacks", "Melee Attacks", MeleeAttacksText, new SrdAlias("Grappling", source));

        Assert.Null(RulesTools.HeadingNote(doc, "Grappling"));
    }

    [Theory]
    [InlineData(SrdAliasSources.Heading)]
    [InlineData(SrdAliasSources.Section)]
    [InlineData(SrdAliasSources.Curated)]
    public void HeadingNote_NoHeadingOrParagraphHoldsTheName_SaysTheEntryCoversItWithoutNamingASubsection(string source)
    {
        // Melee Attacks mentions an unarmed strike in one sentence of its opening; "this entry's Unarmed Strike subsection"
        // sent the reader looking for a heading that does not exist.
        var doc = Rule("melee-attacks", "Melee Attacks", MeleeAttacksText, new SrdAlias("Unarmed Strike", source));

        Assert.Equal("*Unarmed Strike is covered by this entry.*", RulesTools.HeadingNote(doc, "unarmed strike"));
    }

    [Theory]
    // A 2024 name the section covers, inside a heading that names several things.
    [InlineData("Climbing", SrdAliasSources.Section, "*Climbing is covered by this entry's #### Climbing, Swimming, and Crawling subsection.*")]
    [InlineData("Swimming", SrdAliasSources.Section, "*Swimming is covered by this entry's #### Climbing, Swimming, and Crawling subsection.*")]
    // A run-in paragraph under another heading.
    [InlineData("Long Jump", SrdAliasSources.Section, "*Long Jump is covered by this entry's Long Jump paragraph.*")]
    public void HeadingNote_NameInsideALongerHeadingOrARunInParagraph_NamesIt(string name, string source, string expected)
    {
        var doc = Rule(
            "special-types-of-movement",
            "Special Types of Movement",
            "Adventurers might have to climb, crawl, swim, or jump.\n\n#### Climbing, Swimming, and Crawling\n\nWhile climbing or " +
            "swimming, each foot of movement costs 1 extra foot.\n\n#### Jumping\n\nYour Strength determines how far you can jump.\n\n" +
            "***Long Jump.*** When you make a long jump, you cover a number of feet up to your Strength score.",
            new SrdAlias("Climbing, Swimming, and Crawling", SrdAliasSources.Heading),
            new SrdAlias("Jumping", SrdAliasSources.Heading),
            new SrdAlias(name, source));

        Assert.Equal(expected, RulesTools.HeadingNote(doc, name));
    }

    [Fact]
    public void HeadingNote_HeadingThatIsTheNameAndOneThatContainsIt_NamesTheOneThatIsTheName()
    {
        // Document order must not decide: the heading that is the topic beats an earlier one that merely mentions it.
        var doc = Rule(
            "combat",
            "Combat",
            "#### Grappling and Shoving\n\nSee below.\n\n#### Grappling\n\nWhen you want to grab a creature…",
            new SrdAlias("Grappling", SrdAliasSources.Heading));

        Assert.Equal("*Grappling is covered by this entry's #### Grappling subsection.*", RulesTools.HeadingNote(doc, "Grappling"));
    }

    [Theory]
    // A curated everyday word finds the heading written another way: "Shove" is "#### Shoving a Creature".
    [InlineData("Shove", "*Shove is covered by this entry's #### Shoving a Creature subsection.*")]
    [InlineData("Grapple", "*Grapple is covered by this entry's #### Grappling subsection.*")]
    public void HeadingNote_CuratedWordForAHeading_NamesThatHeading(string name, string expected)
    {
        var doc = Rule(
            "melee-attacks",
            "Melee Attacks",
            MeleeAttacksText + "\n\n#### Shoving a Creature\n\nUsing the Attack action, you can make a special melee attack to shove.",
            new SrdAlias("Grappling", SrdAliasSources.Heading),
            new SrdAlias(name, SrdAliasSources.Curated));

        Assert.Equal(expected, RulesTools.HeadingNote(doc, name));
    }

    [Fact]
    public void HeadingNote_CuratedFormOfTheEntrysOwnName_IsNull()
    {
        // 2024 "Grapple" answers with the Grappling entry: nothing needs explaining.
        var doc = Rule("grappling", "Grappling", "A creature can grapple another creature.", new SrdAlias("Grapple", SrdAliasSources.Curated));

        Assert.Null(RulesTools.HeadingNote(doc, "Grapple"));
    }

    private static SrdDocument Rule(string slug, string name, string desc, params SrdAlias[] aliases) => new()
    {
        Edition = "2014",
        Kind = "rule",
        Slug = slug,
        Name = name,
        Json = JsonSerializer.Serialize(new { index = slug, name, desc, url = $"/api/2014/rules/{slug}" }),
        Aliases = aliases,
    };
}
