using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.Tests.Srd.Index;

/// <summary>
/// What the builder's aliases and pairs make reachable, seen through the queries the tools run: a 2014 rule that is
/// only a subsection ("#### Grappling" inside Melee Attacks), a 2014 feature whose name carries its grade ("Wild Shape
/// (CR 1 or below)"), a 2014 variant item 2024 folded into its parent ("Belt of Hill Giant Strength"). Before these
/// aliases existed each of them answered "Not in the 2014 SRD" or "Not in the 2024 SRD", a false statement about the
/// SRD that a model relays as fact.
///
/// <para>
/// Ranking between several matches is the query's business (<see cref="SrdIndex"/>); these tests pin that the right
/// document is found (for a graded feature, first: its lowest grade), and for search that it is among the first three
/// hits.
/// </para>
/// </summary>
public sealed class SrdIndexReachTests : IClassFixture<SrdIndexFixture>
{
    private readonly SrdIndex _index;

    public SrdIndexReachTests(SrdIndexFixture fixture)
    {
        _index = fixture.Index;
    }

    // 2014 has no Grappling, Concentration or Critical Hit entry: those rules are subsections of larger sections, and
    // each subsection heading (or the 2024 glossary's name for it) answers for its section.
    [Theory]
    [InlineData("Grappling", "2014/rule/melee-attacks")]
    [InlineData("Opportunity Attacks", "2014/rule/melee-attacks")]
    [InlineData("Two-Weapon Fighting", "2014/rule/melee-attacks")]
    [InlineData("Shoving a Creature", "2014/rule/melee-attacks")]
    [InlineData("Unarmed Strike", "2014/rule/melee-attacks")]
    [InlineData("Critical Hits", "2014/rule/damage-rolls")]
    [InlineData("Critical Hit", "2014/rule/damage-rolls")]
    [InlineData("Death Saving Throws", "2014/rule/dropping-to-0-hit-points")]
    [InlineData("Death Saving Throw", "2014/rule/dropping-to-0-hit-points")]
    [InlineData("Concentration", "2014/rule/duration")]
    [InlineData("Long Jump", "2014/rule/special-types-of-movement")]
    [InlineData("Serpent Venom", "2014/rule/sample-poisons")]
    public void FindByName_2014SubsectionName_FindsTheSectionHoldingIt(string name, string expected)
    {
        var lookup = _index.FindByName(name, "2014");

        Assert.Contains(expected, lookup.Matches.Select(m => m.Document.Ref.ToString()));
    }

    /// <summary>
    /// Graded 2014 features answer to their bare name, and the grade a class gains first comes first: "Bardic
    /// Inspiration" is the d6 (level 1), not the d10 (level 10) that slug order put first, whose "Level 10" header a
    /// model relayed as when bards get the feature.
    /// </summary>
    [Theory]
    [InlineData("Wild Shape", "2014/feature/wild-shape-cr-1-4-or-below-no-flying-or-swim-speed")]
    [InlineData("Bardic Inspiration", "2014/feature/bardic-inspiration-d6")]
    [InlineData("Song of Rest", "2014/feature/song-of-rest-d6")]
    [InlineData("Action Surge", "2014/feature/action-surge-1-use")]
    [InlineData("Indomitable", "2014/feature/indomitable-1-use")]
    [InlineData("Favored Enemy", "2014/feature/favored-enemy-1-type")]
    [InlineData("Mystic Arcanum", "2014/feature/mystic-arcanum-6th-level")]
    [InlineData("Destroy Undead", "2014/feature/destroy-undead-cr-1-2-or-below")]
    [InlineData("Natural Explorer", "2014/feature/natural-explorer-1-terrain-type")]
    [InlineData("Brutal Critical", "2014/feature/brutal-critical-1-die")]
    public void FindByName_2014GradedFeature_BestIsTheFirstGrade(string name, string expected)
    {
        var lookup = _index.FindByName(name, "2014");

        Assert.Equal(expected, lookup.Best?.Document.Ref.ToString());
    }

    // Names several classes' features answer to reach each of them (the order is FindByName's, pinned elsewhere).
    [Theory]
    [InlineData("Channel Divinity", "2014/feature/channel-divinity-1-rest")]
    [InlineData("Spellcasting", "2014/feature/spellcasting-wizard")]
    public void FindByName_2014SharedFeatureName_ReachesEveryClassesFeature(string name, string expected)
    {
        var lookup = _index.FindByName(name, "2014");

        Assert.Contains(expected, lookup.Matches.Select(m => m.Document.Ref.ToString()));
    }

    // 2014 variant items and renamed swarms reach the 2024 entry that absorbed them, in the default edition.
    [Theory]
    [InlineData("Belt of Hill Giant Strength", "2024/magic-item/belt-of-giant-strength")]
    [InlineData("Potion of Greater Healing", "2024/magic-item/potions-of-healing")]
    [InlineData("Ioun Stone of Protection", "2024/magic-item/ioun-stone")]
    [InlineData("Black Dragon Scale Mail", "2024/magic-item/dragon-scale-mail")]
    [InlineData("Swarm of Wasps", "2024/monster/swarm-of-insects")]
    [InlineData("Primal Path", "2024/feature/barbarian-subclass")]
    [InlineData("Fighting Style: Archery", "2024/feat/archery")]
    public void FindByName_2014NameOfA2024Entry_FindsIt(string name, string expected)
    {
        var lookup = _index.FindByName(name, "2024");

        Assert.Contains(expected, lookup.Matches.Select(m => m.Document.Ref.ToString()));
    }

    // The heading alias counts as an exact name in search, so the section holding the rule outranks the short records
    // that merely share the word (Grappling hook, the Grappled condition, the Grappler feat, monsters that grapple).
    [Theory]
    [InlineData("grappling", "2014/rule/melee-attacks")]
    [InlineData("critical hit", "2014/rule/damage-rolls")]
    [InlineData("opportunity attack", "2014/rule/melee-attacks")]
    [InlineData("death saving throws", "2014/rule/dropping-to-0-hit-points")]
    [InlineData("concentration", "2014/rule/duration")]
    public void Search_2014SubsectionName_RanksTheSectionInTheTopThree(string query, string expected)
    {
        var hits = _index.Search(query, ["2014"], limit: 3).Hits;

        Assert.Contains(expected, hits.Select(h => h.Ref.ToString()));
    }
}
