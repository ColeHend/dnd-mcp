using DndMcp.Domain.Core;
using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.Tests.Srd.Index;

/// <summary>
/// <see cref="SrdRefParser"/>: the three ref forms the model will send (what search printed, a shortened one, and an
/// API URL copied out of raw JSON), what "the ref carried an edition" means, and the exact refusals.
/// </summary>
public sealed class SrdRefParserTests
{
    [Theory]
    [InlineData("spell/fireball", "2024", "2024/spell/fireball", false)]
    [InlineData("spell/fireball", "2014", "2014/spell/fireball", false)]
    [InlineData("2014/spell/fireball", "2024", "2014/spell/fireball", true)]    // the ref's edition wins over the default
    [InlineData("  `2024/spell/fireball`  ", "2014", "2024/spell/fireball", true)] // copied with the output's backticks
    [InlineData("\"spell/fireball\"", "2024", "2024/spell/fireball", false)]
    [InlineData("2024/Spells/Fireball", "2014", "2024/spell/fireball", true)]   // plural kind, capitalised slug
    [InlineData("race/elf", "2024", "2024/species/elf", false)]                 // race means species in 2024
    [InlineData("2014/species/elf", "2024", "2014/race/elf", true)]
    [InlineData("2014/level/fighter-5", "2024", "2014/level/fighter-5", true)]
    [InlineData("2024/magic-item/stone-of-good-luck-(luckstone)", "2024", "2024/magic-item/stone-of-good-luck-(luckstone)", true)]
    [InlineData("2014/feature/dragon-ancestor-black---acid-damage", "2024", "2014/feature/dragon-ancestor-black---acid-damage", true)]
    [InlineData("glossary/finesse-weapon-property", "2024", "2024/rule/finesse-weapon-property", false)]
    public void Parse_KindSlugOrEditionKindSlug_ReturnsTheRef(string text, string defaultEdition, string expected, bool editionGiven)
    {
        var parsed = SrdRefParser.Parse(text, defaultEdition, out var given);

        Assert.Equal(expected, parsed.ToString());
        Assert.Equal(editionGiven, given);
    }

    [Theory]
    [InlineData("/api/2014/spells/fireball", "2014/spell/fireball")]
    [InlineData("api/2024/spells/fireball", "2024/spell/fireball")]
    [InlineData("https://www.dnd5eapi.co/api/2024/magic-items/flame-tongue", "2024/magic-item/flame-tongue")]
    [InlineData("/api/2024/weapon-mastery-properties/vex", "2024/weapon-mastery/vex")]
    [InlineData("/api/2014/classes/fighter/levels/5", "2014/level/fighter-5")]
    [InlineData("/api/2024/subclasses/berserker/levels/3", "2024/level/berserker-3")]
    [InlineData("/api/2014/traits/darkvision?lang=en#top", "2014/trait/darkvision")]
    [InlineData("/api/2024/races/elf", "2024/species/elf")]
    public void Parse_ApiUrl_ReturnsTheRefWithItsEdition(string text, string expected)
    {
        var parsed = SrdRefParser.Parse(text, "2014", out var given);

        Assert.Equal(expected, parsed.ToString());
        Assert.True(given);
    }

    private const string Forms =
        "Use kind/slug (spell/fireball), edition/kind/slug (2024/spell/fireball) or an API URL (/api/2024/spells/fireball).";

    [Theory]
    [InlineData("fireball", "'fireball' is not a rules ref. " + Forms)]
    [InlineData("a/b/c/d", "'a/b/c/d' is not a rules ref. " + Forms)]
    [InlineData("spell//fireball", "'spell//fireball' is not a rules ref. " + Forms)]
    [InlineData("/spell/fireball", "'/spell/fireball' is not a rules ref. " + Forms)]
    [InlineData("/api/2014/spells", "'/api/2014/spells' is not a rules ref. " + Forms)]
    [InlineData("/api/2015/spells/fireball", "'/api/2015/spells/fireball' is not a rules ref. " + Forms)]
    [InlineData("", "The ref is empty. " + Forms)]
    [InlineData("``", "The ref is empty. " + Forms)]
    [InlineData("2024/spell", "'2024/spell' has an edition and a kind but no slug. " + Forms)]
    [InlineData("2015/spell/fireball", "Unknown edition '2015' in '2015/spell/fireball'. Editions are 2014 and 2024, for example 2024/spell/fireball.")]
    [InlineData("both/spell/fireball", "Unknown edition 'both' in 'both/spell/fireball'. Editions are 2014 and 2024, for example 2024/spell/fireball.")]
    [InlineData("2014/poison/serpent-venom", "Kind 'poison' is not in the 2014 SRD; only the 2024 SRD has it. Pass edition 2024 (or both).")]
    public void Parse_NotARef_ThrowsWithTheAcceptedForms(string text, string expected)
    {
        var error = Assert.Throws<DndInputException>(() => SrdRefParser.Parse(text, "2024"));

        Assert.Equal(expected, error.Message);
    }

    // An unknown kind gets the kind list, from the same normaliser the kind filters use.
    [Fact]
    public void Parse_UnknownKind_ListsTheKinds()
    {
        var error = Assert.Throws<DndInputException>(() => SrdRefParser.Parse("/api/2014/rule-sections/cover", "2024"));

        Assert.StartsWith("Unknown kind 'rule-sections'. Kinds in the 2014 SRD: ability-score,", error.Message, StringComparison.Ordinal);
    }

    // A ref is short (the longest real one, an API URL with its host, is about 100 characters). Pasted text is refused
    // before it is parsed, looked up or turned into a name to suggest from, and is not echoed back.
    [Fact]
    public void Parse_LongerThanTheLimit_ThrowsWithoutEchoingIt()
    {
        var text = "2024/spell/" + string.Concat(Enumerable.Repeat("a-", 30_000));

        var error = Assert.Throws<DndInputException>(() => SrdRefParser.Parse(text, "2024"));

        Assert.Equal(
            $"The ref is 60,011 characters long; refs are at most {SrdRefParser.MaxLength} characters, like 2024/spell/fireball. " + Forms,
            error.Message);
    }

    [Fact]
    public void Parse_AtTheLimit_IsParsed()
    {
        var text = "2024/spell/" + new string('a', SrdRefParser.MaxLength - "2024/spell/".Length);

        Assert.Equal(SrdRefParser.MaxLength, SrdRefParser.Parse(text, "2024").ToString().Length);
    }

    [Fact]
    public void Parse_DefaultEditionNotAnEdition_IsAProgrammingError()
    {
        Assert.Throws<ArgumentException>(() => SrdRefParser.Parse("spell/fireball", "both"));
    }
}
