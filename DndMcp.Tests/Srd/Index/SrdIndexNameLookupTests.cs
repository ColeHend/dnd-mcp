using DndMcp.Domain.Core;
using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.Tests.Srd.Index;

/// <summary>
/// <see cref="SrdIndex.FindByName"/>, which <c>rules_get{name}</c> is built on: which document a name means, in what
/// order several are offered, and what a miss says instead ("did you mean", "the other edition has it").
/// </summary>
public sealed class SrdIndexNameLookupTests : IClassFixture<SrdIndexFixture>
{
    private readonly SrdIndex _index;

    public SrdIndexNameLookupTests(SrdIndexFixture fixture)
    {
        _index = fixture.Index;
    }

    // Case, apostrophes (typographic too), hyphens and diacritics do not matter; the name key absorbs them.
    [Theory]
    [InlineData("FIREBALL", "2024", "2024/spell/fireball")]
    [InlineData("  fireball ", "2014", "2014/spell/fireball")]
    [InlineData("dragons breath", "2024", "2024/spell/dragons-breath")]
    [InlineData("Dragon’s Breath", "2024", "2024/spell/dragons-breath")]
    [InlineData("Will-o'-Wisp", "2024", "2024/monster/will-o-wisp")]
    [InlineData("will o wisp", "2014", "2014/monster/will-o-wisp")]
    [InlineData("Fighter 5", "2014", "2014/level/fighter-5")]
    [InlineData("Champion 3", "2014", "2014/level/champion-3")]
    [InlineData("Stone of Good Luck (Luckstone)", "2024", "2024/magic-item/stone-of-good-luck-(luckstone)")]
    [InlineData("carpet of flying 3 ft × 5 ft", "2014", "2014/magic-item/carpet-of-flying-3x5")]
    public void FindByName_SpelledDifferently_FindsTheDocument(string name, string edition, string expected)
    {
        var best = _index.FindByName(name, edition).Best;

        Assert.NotNull(best);
        Assert.Equal(expected, best.Document.Ref.ToString());
        Assert.Null(best.MatchedAlias);
    }

    // The brief's example: the weapon property by its own name first, the glossary entry by its bare-name alias after.
    [Fact]
    public void FindByName_Finesse2024_IsTheWeaponPropertyThenTheGlossaryEntry()
    {
        var matches = _index.FindByName("Finesse", "2024").Matches;

        Assert.Equal(["2024/weapon-property/finesse", "2024/rule/finesse-weapon-property"], matches.Select(m => m.Document.Ref.ToString()));
        Assert.Null(matches[0].MatchedAlias);
        Assert.Equal("Finesse", matches[1].MatchedAlias);
    }

    // Kind priority decides between documents that share a name: the spell before the magic item before the armor.
    [Theory]
    [InlineData("Shield", "2024", "2024/spell/shield,2024/magic-item/shield,2024/equipment/shield")]
    [InlineData("Prone", "2024", "2024/condition/prone,2024/rule/prone")]
    [InlineData("Grappled", "2024", "2024/condition/grappled,2024/rule/grappled")]
    [InlineData("Reach", "2024", "2024/weapon-property/reach,2024/rule/reach,2024/rule/reach-weapon-property")]
    [InlineData("Light", "2024", "2024/spell/light,2024/weapon-property/light,2024/rule/light-weapon-property")]
    public void FindByName_SharedName_OrdersByKindPriority(string name, string edition, string expected)
    {
        var matches = _index.FindByName(name, edition).Matches;

        Assert.Equal(expected, string.Join(',', matches.Select(m => m.Document.Ref.ToString())));
    }

    // Within one kind an own-name match outranks an alias: the glossary's Reach rule before "Reach (Weapon Property)",
    // which answers to "Reach" only through its bare-name alias. Across kinds, kind priority decides first
    // (QueryNameLookupTests: 2014 "Acid" is the vial by its 2024 name before the damage type).
    [Fact]
    public void FindByName_OwnNameAndAliasOfOneKind_OwnNameComesFirst()
    {
        var rules = _index.FindByName("Reach", "2024").Matches.Where(m => m.Document.Kind == "rule").ToList();

        Assert.Equal(["2024/rule/reach", "2024/rule/reach-weapon-property"], rules.Select(m => m.Document.Ref.ToString()));
        Assert.Null(rules[0].MatchedAlias);
        Assert.Equal("Reach", rules[1].MatchedAlias);
    }

    [Theory]
    [InlineData("Shield", "equipment", "2024/equipment/shield")]
    [InlineData("Shield", "magic-item", "2024/magic-item/shield")]
    [InlineData("Finesse", "rule", "2024/rule/finesse-weapon-property")]
    public void FindByName_WithKind_NarrowsToThatKind(string name, string kind, string expected)
    {
        var matches = _index.FindByName(name, "2024", kind).Matches;

        Assert.Equal([expected], matches.Select(m => m.Document.Ref.ToString()));
    }

    // Renamed entries answer to their other-edition name (the counterpart alias).
    [Theory]
    [InlineData("Thug", "2024", "2024/monster/tough")]
    [InlineData("Tough", "2014", "2014/monster/thug")]
    [InlineData("Lore", "2024", "2024/subclass/college-of-lore")]
    [InlineData("College of Lore", "2014", "2014/subclass/lore")]
    [InlineData("High Elf", "2024", "2024/subspecies/elven-lineage-high-elf")]
    [InlineData("Feeblemind", "2024", "2024/spell/befuddlement")]
    [InlineData("Cast a Spell", "2024", "2024/rule/magic")]
    [InlineData("Advantage", "2014", "2014/rule/advantage-and-disadvantage")]
    public void FindByName_OtherEditionsName_FindsTheRenamedEntryByAlias(string name, string edition, string expected)
    {
        var best = _index.FindByName(name, edition).Best;

        Assert.NotNull(best);
        Assert.Equal(expected, best.Document.Ref.ToString());
        Assert.Equal(name, best.MatchedAlias);
    }

    // A miss in one edition names the exact matches in the other, so the answer can be "the 2014 SRD has it".
    [Theory]
    [InlineData("Duergar", "2024", null, "2014/monster/duergar")]
    [InlineData("Half-Orc", "2024", null, "2014/race/half-orc")]
    [InlineData("Half-Orc", "2024", "species", "2014/race/half-orc")]
    [InlineData("Bugbear Stalker", "2014", null, "2024/monster/bugbear-stalker")]
    [InlineData("Pirate Captain", "2014", "monster", "2024/monster/pirate-captain")]
    public void FindByName_OnlyInTheOtherEdition_ReportsTheOtherEditionsMatch(string name, string edition, string? kind, string expected)
    {
        var lookup = _index.FindByName(name, edition, kind);

        Assert.Empty(lookup.Matches);
        Assert.Null(lookup.Best);
        Assert.Equal([expected], lookup.OtherEdition.Select(m => m.Document.Ref.ToString()));
    }

    // The other edition is searched for the SAME kind (mapped race ⇄ species); a spell named like a monster is not offered.
    [Fact]
    public void FindByName_OtherEditionWithKind_OnlyOffersThatKind()
    {
        Assert.Empty(_index.FindByName("Duergar", "2024", "spell").OtherEdition);
    }

    [Theory]
    [InlineData("Fierball", "2024/spell/fireball")]        // transposition: no prefix reaches it
    [InlineData("magic misile", "2024/spell/magic-missile")]
    [InlineData("Firebal", "2024/spell/fireball")]         // prefix: the shortest name first
    [InlineData("fire bol", "2024/spell/fire-bolt")]
    [InlineData("Stone of Good", "2024/magic-item/stone-of-good-luck-(luckstone)")]
    public void FindByName_Typo_SuggestsTheIntendedNameFirst(string name, string expected)
    {
        var lookup = _index.FindByName(name, "2024");

        Assert.Empty(lookup.Matches);
        Assert.NotEmpty(lookup.Suggestions);
        Assert.Equal(expected, lookup.Suggestions[0].Ref.ToString());
        Assert.True(lookup.Suggestions.Count <= SrdIndex.MaxSuggestions);
    }

    [Fact]
    public void FindByName_Suggestions_RespectTheKind()
    {
        var lookup = _index.FindByName("fire", "2024", "monster");

        Assert.NotEmpty(lookup.Suggestions);
        Assert.All(lookup.Suggestions, d => Assert.Equal("monster", d.Kind));
    }

    [Fact]
    public void FindByName_Nonsense_HasNoSuggestionsOrOtherEdition()
    {
        var lookup = _index.FindByName("xyzzyq", "2024");

        Assert.Empty(lookup.Matches);
        Assert.Empty(lookup.Suggestions);
        Assert.Empty(lookup.OtherEdition);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("?!")]
    public void FindByName_NoLettersOrDigits_ThrowsDndInputException(string name)
    {
        var error = Assert.Throws<DndInputException>(() => _index.FindByName(name, "2024"));

        Assert.Equal($"The name needs at least one letter or digit; \"{name}\" has none. Example: name \"Fireball\".", error.Message);
    }

    [Fact]
    public void FindByName_UnknownEdition_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => _index.FindByName("Fireball", "both"));
    }
}
