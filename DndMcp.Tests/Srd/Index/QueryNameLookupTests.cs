using DndMcp.Domain.Core;
using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.Tests.Srd.Index;

/// <summary>
/// How <see cref="SrdIndex.FindByName"/> picks between documents that answer to one name, says how each matched, reads
/// names people actually type ("Healing Word spell", "The Fiend", "Death Saving Throws"), and what it suggests on a miss.
/// A wrong best match is a confident wrong answer: <c>rules_get {name:"Goblin"}</c> rendering a three-line language
/// entry, or <c>{name:"Druid"}</c> the NPC stat block instead of the class.
/// </summary>
public sealed class QueryNameLookupTests : IClassFixture<SrdIndexFixture>
{
    private readonly SrdIndex _index;

    public QueryNameLookupTests(SrdIndexFixture fixture)
    {
        _index = fixture.Index;
    }

    /// <summary>
    /// The name-match tiers, best first: a content kind's own name; another name of a content kind (a bare glossary name,
    /// the other edition's name, a curated name); a reference kind's own name (a language, a damage type, a category…);
    /// another name of a reference kind; a 2014 subsection heading or the 2024 name a 2014 section covers. Within a tier:
    /// kind priority, then the lower class level for features, then slug.
    ///
    /// <para>
    /// What each tier boundary prevents. 2024's Goblin monster is "Goblin Warrior" and answers to "Goblin" only by its
    /// 2014 name, yet it is what a person asking for "Goblin" means, not the Goblin language. But a name another edition
    /// uses must not beat an entry that owns it in this one: 2024 "Acolyte" is the background (the monster is "Priest
    /// Acolyte"), "Berserker" the monster (the subclass is "Path of the Berserker"), "Potion of Healing" the gear entry
    /// of that name, not the magic-item table. And a 2014 feature split into grades opens with the grade gained first:
    /// "Bardic Inspiration" is the d6 at level 1, never the d10 at level 10 that slug order put first.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Goblin", "2024", "2024/monster/goblin-warrior,2024/language/goblin")]
    [InlineData("Goblin", "2014", "2014/monster/goblin,2014/language/goblin")]
    [InlineData("Druid", "2024", "2024/class/druid,2024/monster/druid")]
    [InlineData("Druid", "2014", "2014/class/druid,2014/monster/druid")]
    [InlineData("Acolyte", "2024", "2024/background/acolyte,2024/monster/priest-acolyte")]
    [InlineData("Berserker", "2024", "2024/monster/berserker,2024/subclass/path-of-the-berserker")]
    [InlineData("Potion of Healing", "2024", "2024/equipment/potion-of-healing,2024/magic-item/potions-of-healing")]
    [InlineData("Damage Resistance", "2024", "2024/rule/resistance,2024/trait/draconic-damage-resistance-acid,2024/trait/draconic-damage-resistance-cold,2024/trait/draconic-damage-resistance-fire,2024/trait/draconic-damage-resistance-lightning,2024/trait/draconic-damage-resistance-poison")]
    [InlineData("Reach", "2024", "2024/weapon-property/reach,2024/rule/reach,2024/rule/reach-weapon-property")]
    [InlineData("Bardic Inspiration", "2014", "2014/feature/bardic-inspiration-d6,2014/feature/bardic-inspiration-d8,2014/feature/bardic-inspiration-d10,2014/feature/bardic-inspiration-d12")]
    [InlineData("Song of Rest", "2014", "2014/feature/song-of-rest-d6,2014/feature/song-of-rest-d8,2014/feature/song-of-rest-d10,2014/feature/song-of-rest-d12")]
    [InlineData("Wild Shape", "2014", "2014/feature/wild-shape-cr-1-4-or-below-no-flying-or-swim-speed,2014/feature/wild-shape-cr-1-2-or-below-no-flying-speed,2014/feature/wild-shape-cr-1-or-below")]
    [InlineData("Channel Divinity", "2014", "2014/feature/channel-divinity,2014/feature/channel-divinity-1-rest,2014/feature/channel-divinity-2-rest,2014/feature/channel-divinity-3-rest")]
    public void FindByName_NameSharedByKinds_OrdersByTierThenKindThenLevelThenSlug(string name, string edition, string expected)
    {
        var matches = _index.FindByName(name, edition).Matches;

        Assert.Equal(expected, string.Join(',', matches.Select(m => m.Document.Ref.ToString())));
    }

    /// <summary>
    /// Deliberate consequences of the tiers, pinned so they stay choices: an alias of a content kind beats a reference
    /// kind's own name (2024 "Evocation" is the Evoker subclass before the school, 2014 "Armor" the "+1, +2, or +3"
    /// armor before the equipment category, 2014 "Acid" the vial before the damage type), and a content kind's own name
    /// beats another edition's name (2014 "Circle of the Land" is the druid feature of that name before the subclass
    /// 2014 calls "Land"; 2014 "Ammunition" the weapon property before the magic ammunition). And a subsection heading
    /// comes after every other match: 2014 "Two-Weapon Fighting" is the fighting style (by its 2024 feat name) before
    /// Melee Attacks' "#### Two-Weapon Fighting".
    /// </summary>
    [Theory]
    [InlineData("Evocation", "2024", "2024/subclass/evoker,2024/magic-school/evocation")]
    [InlineData("Draconic", "2024", "2024/subclass/draconic-sorcery,2024/language/draconic")]
    [InlineData("Armor", "2014", "2014/magic-item/armor,2014/equipment-category/armor")]
    [InlineData("Acid", "2014", "2014/equipment/acid-vial,2014/damage-type/acid")]
    [InlineData("Circle of the Land", "2014", "2014/feature/circle-of-the-land,2014/subclass/land")]
    [InlineData("Ammunition", "2014", "2014/weapon-property/ammunition,2014/magic-item/ammunition,2014/equipment-category/ammunition")]
    [InlineData("Two-Weapon Fighting", "2014", "2014/feature/fighter-fighting-style-two-weapon-fighting,2014/feature/ranger-fighting-style-two-weapon-fighting,2014/rule/melee-attacks")]
    public void FindByName_AliasAgainstAnOwnName_FollowsTheTiers(string name, string edition, string expected)
    {
        var matches = _index.FindByName(name, edition).Matches;

        Assert.Equal(expected, string.Join(',', matches.Select(m => m.Document.Ref.ToString())));
    }

    [Fact]
    public void PriorityOf_ClassAndSubclass_ComeBeforeMonster()
    {
        Assert.True(SrdKindNames.PriorityOf("class") < SrdKindNames.PriorityOf("monster"));
        Assert.True(SrdKindNames.PriorityOf("subclass") < SrdKindNames.PriorityOf("monster"));
        Assert.True(SrdKindNames.PriorityOf("spell") < SrdKindNames.PriorityOf("class"));
    }

    // The tool words its answer by how the name matched: "Thug" is the 2014 name of 2024's Tough, not a 2024 name.
    [Theory]
    [InlineData("Fireball", "2024", "2024/spell/fireball", null, "name")]
    [InlineData("Finesse", "2024", "2024/weapon-property/finesse", null, "name")]
    [InlineData("Thug", "2024", "2024/monster/tough", "Thug", "counterpart")]
    [InlineData("Goblin", "2024", "2024/monster/goblin-warrior", "Goblin", "counterpart")]
    [InlineData("Grappling", "2014", "2014/rule/melee-attacks", "Grappling", "heading")]
    [InlineData("Unarmed Strike", "2014", "2014/rule/melee-attacks", "Unarmed Strike", "section")]
    [InlineData("Shove", "2024", "2024/rule/unarmed-strike", "Shove", "curated")]
    [InlineData("Wild Shape", "2014", "2014/feature/wild-shape-cr-1-4-or-below-no-flying-or-swim-speed", "Wild Shape", "qualifier")]
    public void FindByName_Best_SaysHowTheNameMatched(string name, string edition, string reference, string? alias, string source)
    {
        var best = _index.FindByName(name, edition).Best!;

        Assert.Equal(reference, best.Document.Ref.ToString());
        Assert.Equal(alias, best.MatchedAlias);
        Assert.Equal(source, best.Source);
        Assert.Equal(alias is null, best.IsOwnName);
    }

    // Each match's tier, as the ordering uses it.
    [Theory]
    [InlineData("Acolyte", "2024", "2024/background/acolyte", 0)]
    [InlineData("Acolyte", "2024", "2024/monster/priest-acolyte", 1)]
    [InlineData("Shove", "2024", "2024/rule/unarmed-strike", 1)]
    [InlineData("Goblin", "2024", "2024/language/goblin", 2)]
    [InlineData("Weapon", "2024", "2024/equipment-category/weapons", 3)]
    [InlineData("Grappling", "2014", "2014/rule/melee-attacks", 4)]
    [InlineData("Unarmed Strike", "2014", "2014/rule/melee-attacks", 4)]
    public void Tier_OfAMatch_FollowsHowItMatchedAndItsKind(string name, string edition, string reference, int tier)
    {
        var match = Assert.Single(_index.FindByName(name, edition).Matches, m => m.Document.Ref.ToString() == reference);

        Assert.Equal(tier, match.Tier);
    }

    [Fact]
    public void FindByName_BareGlossaryName_IsAQualifierMatch()
    {
        var glossary = Assert.Single(_index.FindByName("Finesse", "2024").Matches, m => m.Document.Kind == "rule");

        Assert.Equal(SrdAliasSources.Qualifier, glossary.Source);
        Assert.Equal("Finesse", glossary.MatchedAlias);
    }

    /// <summary>
    /// A name with a leading "the", a trailing kind word or the other number still finds the entry, flagged as a loose
    /// match. Before, "Healing Word spell" suggested Heal, Spell, Healing, Mass Heal and Spellbook, and "The Fiend" only
    /// Pit Fiend.
    /// </summary>
    [Theory]
    [InlineData("Healing Word spell", "2024", "2024/spell/healing-word")]
    [InlineData("Hunter's Mark spell", "2024", "2024/spell/hunters-mark")]
    [InlineData("Mage Armor spell", "2024", "2024/spell/mage-armor")]
    [InlineData("Longsword weapon", "2024", "2024/equipment/longsword")]
    [InlineData("The Fiend", "2014", "2014/subclass/fiend")]
    [InlineData("the fiend", "2024", "2024/subclass/fiend-patron")]
    [InlineData("Dodge action", "2024", "2024/rule/dodge")]
    [InlineData("Death Saving Throws", "2024", "2024/rule/death-saving-throw")]
    [InlineData("Opportunity Attack", "2024", "2024/rule/opportunity-attacks")]
    [InlineData("Goblin stat block", "2024", "2024/monster/goblin-warrior")]
    [InlineData("Fire damage", "2024", "2024/damage-type/fire")]
    public void FindByName_NameWithAnExtraWord_FindsTheEntryAsALooseMatch(string name, string edition, string expected)
    {
        var lookup = _index.FindByName(name, edition);

        Assert.Equal(expected, lookup.Best?.Document.Ref.ToString());
        Assert.True(lookup.LooseMatch);
        Assert.Empty(lookup.Suggestions);
    }

    // The trailing kind word also says which of several same-named entries was meant.
    [Theory]
    [InlineData("Acolyte background", "2024", "2024/background/acolyte")]
    [InlineData("Goblin language", "2024", "2024/language/goblin")]
    [InlineData("Druid stat block", "2024", "2024/monster/druid")]
    [InlineData("Berserker monster", "2024", "2024/monster/berserker")]
    [InlineData("Shield armor", "2024", "2024/equipment/shield")]
    public void FindByName_TrailingKindWord_PutsThatKindFirst(string name, string edition, string expected)
    {
        var lookup = _index.FindByName(name, edition);

        Assert.Equal(expected, lookup.Best?.Document.Ref.ToString());
        Assert.True(lookup.Matches.Count > 1);
    }

    // A dropped kind word has to fit: no 2024 feat is named Tough, so "Tough feat" is a miss that suggests Tough, not
    // the Tough monster served as if it were the feat. A kind phrase that is the whole name is not dropped at all:
    // "Magic Item" is not the Magic action.
    [Theory]
    [InlineData("Tough feat", "2024", "2024/monster/tough")]
    [InlineData("Magic Item", "2024", null)]
    public void FindByName_KindWordThatDoesNotFit_IsNotALooseMatch(string name, string edition, string? suggested)
    {
        var lookup = _index.FindByName(name, edition);

        Assert.Empty(lookup.Matches);
        Assert.False(lookup.LooseMatch);
        if (suggested is not null)
        {
            Assert.Contains(suggested, lookup.Suggestions.Select(d => d.Ref.ToString()));
        }
    }

    // An exact name is never loosened, even when it ends in a kind word or an "s".
    [Theory]
    [InlineData("Magic Weapon", "2024", "2024/spell/magic-weapon")]
    [InlineData("Opportunity Attacks", "2024", "2024/rule/opportunity-attacks")]
    [InlineData("Fireball", "2024", "2024/spell/fireball")]
    public void FindByName_ExactName_IsNotALooseMatch(string name, string edition, string expected)
    {
        var lookup = _index.FindByName(name, edition);

        Assert.Equal(expected, lookup.Best?.Document.Ref.ToString());
        Assert.False(lookup.LooseMatch);
    }

    [Fact]
    public void FindByName_LooseNameOnlyInTheOtherEdition_ReportsTheOtherEditionsMatch()
    {
        var lookup = _index.FindByName("Duergar stat block", "2024");

        Assert.Empty(lookup.Matches);
        Assert.Equal(["2014/monster/duergar"], lookup.OtherEdition.Select(m => m.Document.Ref.ToString()));
    }

    /// <summary>
    /// Suggestions rank names by how many of the typed words they contain, and never by a stop word: "Way of the Open
    /// Hand" offered Handaxe first, and a 2014-only swarm looked up in 2024 offered five unrelated names containing "of".
    /// </summary>
    [Theory]
    [InlineData("Way of the Open Hand", "2014", "2014/subclass/open-hand")]
    [InlineData("School of Evocation", "2014", "2014/subclass/evocation")]
    [InlineData("Dragonborn breath weapon", "2024", "2024/trait/draconic-breath-weapon-acid")]
    [InlineData("Healing Wurd spell", "2024", "2024/spell/healing-word")]
    public void FindByName_Miss_SuggestsTheNameWithTheMostWordsFirst(string name, string edition, string expected)
    {
        var lookup = _index.FindByName(name, edition);

        Assert.Empty(lookup.Matches);
        Assert.Equal(expected, lookup.Suggestions.FirstOrDefault()?.Ref.ToString());
    }

    /// <summary>
    /// A typo at the most edits allowed is weak evidence and no longer ends the search: "Old Fiend" is three edits from
    /// Pit Fiend, which used to be the only suggestion; the entry named by the one real word typed comes first, and the
    /// near name still follows.
    /// </summary>
    [Fact]
    public void FindByName_WeakTypoAndAWordMatch_PutsTheWordMatchFirst()
    {
        var suggestions = _index.FindByName("Old Fiend", "2014").Suggestions.Select(d => d.Ref.ToString()).ToList();

        Assert.Equal("2014/subclass/fiend", suggestions[0]);
        Assert.Contains("2014/monster/pit-fiend", suggestions);
    }

    // Each suggestion shares a real word (or its stem) with the name typed.
    [Theory]
    [InlineData("Swarm of Hornets", "swarm", "hornet")]
    [InlineData("Mounted Combat", "mount", "combat")]
    public void FindByName_Miss_NeverSuggestsByAStopWord(string name, params string[] words)
    {
        var suggestions = _index.FindByName(name, "2024").Suggestions;

        Assert.NotEmpty(suggestions);
        Assert.All(suggestions, d => Assert.Contains(words, w => SrdNames.Key(d.Name).Contains(w, StringComparison.Ordinal)));
    }

    // Typing errors: a two-edit typo of a short name and a three-edit typo of a long one still come first, and the
    // shortest name wins among prefixes ("Aci" is Acid, not Acid Arrow).
    [Theory]
    [InlineData("Fyrebal", "2024", "2024/spell/fireball")]
    [InlineData("Maagic Misil", "2024", "2024/spell/magic-missile")]
    [InlineData("Aci", "2014", "2014/damage-type/acid")]
    public void FindByName_Typo_SuggestsTheIntendedNameFirst(string name, string edition, string expected)
    {
        var lookup = _index.FindByName(name, edition);

        Assert.Empty(lookup.Matches);
        Assert.Equal(expected, lookup.Suggestions.FirstOrDefault()?.Ref.ToString());
    }

    /// <summary>
    /// A typo of a name a document answers to only as an alias is suggested too: 2014 has no Grappling entry, only the
    /// "#### Grappling" heading of Melee Attacks, so "Grapling" offered Grappled, Grappler and Grappling hook, and
    /// "Opportunity Atacks" nothing at all.
    /// </summary>
    [Theory]
    [InlineData("Grapling", "2014", "2014/rule/melee-attacks", "Grappling")]
    [InlineData("Opportunity Atacks", "2014", "2014/rule/melee-attacks", "Opportunity Attacks")]
    public void FindByName_TypoOfAnAlias_SuggestsItsDocumentFirst(string name, string edition, string expected, string alias)
    {
        var lookup = _index.FindByName(name, edition);

        Assert.Empty(lookup.Matches);
        Assert.Equal(expected, lookup.Suggestions.FirstOrDefault()?.Ref.ToString());
        Assert.Equal(lookup.Suggestions, lookup.SuggestionMatches.Select(m => m.Document));
        Assert.Equal(alias, lookup.SuggestionMatches[0].MatchedAlias);
        Assert.Equal(SrdAliasSources.Heading, lookup.SuggestionMatches[0].Source);
    }

    // A typo of an own name says so: no alias, source "name".
    [Fact]
    public void FindByName_TypoOfAName_SuggestionMatchHasNoAlias()
    {
        var suggestion = _index.FindByName("Fierball", "2024").SuggestionMatches[0];

        Assert.Equal("2024/spell/fireball", suggestion.Document.Ref.ToString());
        Assert.Null(suggestion.MatchedAlias);
        Assert.Equal(SrdNameMatch.OwnName, suggestion.Source);
    }

    /// <summary>
    /// Copied text carries compatibility characters (ligatures, fullwidth letters) and invisible ones (soft hyphen,
    /// zero-width space); none of them may turn an exact name into a miss.
    /// </summary>
    [Theory]
    [InlineData("ﬂaming sphere", "2024/spell/flaming-sphere")]
    [InlineData("ﬁre bolt", "2024/spell/fire-bolt")]
    [InlineData("Fire­ball", "2024/spell/fireball")]
    [InlineData("Fire​ball", "2024/spell/fireball")]
    [InlineData("Ｆｉｒｅｂａｌｌ", "2024/spell/fireball")]
    public void FindByName_CompatibilityOrInvisibleCharacters_FindsTheEntry(string name, string expected)
    {
        var lookup = _index.FindByName(name, "2024");

        Assert.Equal(expected, lookup.Best?.Document.Ref.ToString());
        Assert.False(lookup.LooseMatch);
    }

    // A pasted paragraph is not a name: refused before any query runs, with the limit and an example, and without
    // echoing the text back.
    [Fact]
    public void FindByName_NameLongerThanTheLimit_ThrowsDndInputException()
    {
        var name = string.Join(' ', Enumerable.Repeat("a", 10_000));

        var error = Assert.Throws<DndInputException>(() => _index.FindByName(name, "2024"));

        Assert.Equal(
            $"The name is {name.Length.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} characters long; names are at most {SrdIndex.MaxNameLength} characters. " +
            "Pass just the entry's name, e.g. \"Fireball\", or search for longer text with rules_search.",
            error.Message);
    }

    [Fact]
    public void FindByName_NameAtTheLimit_IsLookedUp()
    {
        var name = new string('q', SrdIndex.MaxNameLength);

        var lookup = _index.FindByName(name, "2024");

        Assert.Empty(lookup.Matches);
    }

    // The typo step compares lengths first, so a long name costs no more than a short one.
    [Fact]
    public void FindByName_LongNameThatMatchesNothing_AnswersQuickly()
    {
        var name = string.Join(' ', Enumerable.Range(0, 40).Select(i => "w" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        _index.FindByName("warm up", "2024");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _index.FindByName(name[..Math.Min(name.Length, SrdIndex.MaxNameLength)], "2024");

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"took {stopwatch.Elapsed.TotalMilliseconds:0} ms");
    }
}
