using DndMcp.Domain.Core;
using DndMcp.Domain.Encounters;
using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation;
using DndMcp.Domain.Simulation.Archetypes;
using DndMcp.Tests.Srd.Combatants;
using Xunit;

namespace DndMcp.Tests.Simulation.Archetypes;

/// <summary>
/// Invariant: <see cref="PartyArchetypes"/> is a faithful adapter — every archetype, edition and level expands to a
/// combatant the simulator runs (four copies against a real SRD monster of CR ≈ level), the entry's own fields override
/// the expansion sensibly (saves per ability, the fight's edition as the default), unknown names are refused with the list,
/// and a standard party beats an easy fight.
/// </summary>
public sealed class PartyArchetypesTests
{
    /// <summary>Fixed seeds: every simulation here is reproducible.</summary>
    private const ulong Seed = 20260927;

    private static readonly Lazy<Dictionary<(string Edition, int Level), StatBlock>> Opponents = new(() =>
    {
        // The SRD monster whose CR is closest to each level (ties by name), so every level fights something of its tier;
        // without damage immunities, so "the party dealt damage" is a fair check (a greatsword against a black pudding,
        // immune to slashing, rightly deals none; the wizard's fire-immune case has its own test).
        var map = new Dictionary<(string, int), StatBlock>();
        foreach (var edition in ArchetypeTestKit.Editions)
        {
            var blocks = CorrectedSrd.Shipped.StatBlocks(edition).Where(b => b.Immunities.Count == 0).ToList();
            for (var level = 1; level <= 20; level++)
            {
                map[(edition, level)] = blocks
                    .OrderBy(b => Math.Abs(b.ChallengeRating.Value - level))
                    .ThenBy(b => b.Name, StringComparer.Ordinal)
                    .First();
            }
        }

        return map;
    });

    public static IEnumerable<object[]> AllMembers() => ArchetypeTestKit.AllMembers();

    [Fact]
    public void Names_AreTheCatalogues()
    {
        Assert.Equal(ArchetypeCatalog.Names, PartyArchetypes.Names);
    }

    [Theory]
    [MemberData(nameof(AllMembers))]
    public void Expand_EveryArchetypeEditionAndLevel_CopiesTheMemberAcross(string name, string edition, int level)
    {
        var member = ArchetypeCatalog.Build(name, level, edition);
        var combatant = PartyArchetypes.Expand(name, level, edition);

        Assert.Same(member.Build.Attacks, combatant.Build!.Attacks);
        Assert.Equal((level, member.Hp, member.Ac, member.Position), (combatant.Level!.Value, combatant.Hp!.Value, combatant.Ac!.Value, combatant.Position));
        Assert.Equal(member.SaveProficiencies, combatant.SaveProficiencies);
        Assert.Equal(char.ToUpperInvariant(name[0]) + name[1..], combatant.Name);
        Assert.Null(combatant.Archetype);
        Assert.Null(combatant.Monster);
        Assert.Null(combatant.Count);
        Assert.Null(combatant.InitiativeBonus);

        // Saves are set only where they are not modifier + proficiency: the paladin's own Aura of Protection from 6.
        if (name == "paladin" && level >= 6)
        {
            Assert.All(DslValues.Abilities.All, a => Assert.Equal(member.Saves[a], combatant.Saves!.Get(a)));
        }
        else
        {
            Assert.Null(combatant.Saves);
        }
    }

    [Theory]
    [MemberData(nameof(AllMembers))]
    public void Simulate_EveryArchetype_FourCopiesAgainstAMonsterOfItsLevel_RunsWithoutError(string name, string edition, int level)
    {
        var monster = Opponents.Value[(edition, level)];
        var spec = SimKit.Spec(
            [new SimulationCombatant(new CombatantSpec { Archetype = name, Level = level, Edition = edition, Count = 4 })],
            [SimKit.Monster(monster)],
            iterations: 12,
            roundCap: 8);

        var report = Simulator.Run(spec, Seed, maxThreads: 1);

        var member = ArchetypeCatalog.Build(name, level, edition);
        var party = report.Combatants[0];
        Assert.Equal(12, report.Iterations);
        Assert.Equal(edition, report.Edition);
        Assert.Equal((4, (double)member.Hp, member.Ac, true), (party.Count, party.MaxHp, party.ArmorClass, party.DeathSaves));
        Assert.Equal($"archetype {name} (level {level}, {edition})", party.Source);
        Assert.True(party.DamageDealt.Mean > 0, $"{name} {edition} {level} dealt no damage to {monster.Name} in 12 fights.");
        Assert.Contains(member.Summary, report.Assumptions);
        Assert.Contains(ArchetypeCatalog.SharedRules, report.Assumptions);
    }

    [Theory]
    [InlineData("2014", "adult-brass-dragon", 13)]
    [InlineData("2024", "adult-gold-dragon", 17)]
    public void Simulate_WizardAgainstAFireImmuneDragon_FallsBackToAcidSplash(string edition, string slug, int level)
    {
        // Fire Bolt and Fireball do nothing to it; Acid Splash (the second cantrip) is what the wizard casts instead.
        var dragon = CorrectedSrd.Shipped.StatBlock(edition, slug);
        Assert.Contains(dragon.Immunities, i => i.DamageType == "fire");
        var spec = SimKit.Spec(
            [new SimulationCombatant(new CombatantSpec { Archetype = "wizard", Level = level, Edition = edition, Count = 4 })],
            [SimKit.Monster(dragon)],
            iterations: 12,
            roundCap: 8);

        var report = Simulator.Run(spec, Seed, maxThreads: 1);

        Assert.True(report.Combatants[0].DamageDealt.Mean > 0);
    }

    [Fact]
    public void Overrides_TheEntrysFieldsReplaceTheArchetypes_SavesPerAbility()
    {
        // A level 6 2024 paladin: Str 18, Cha 16, Con 13, Wis 12, Dex 10, Int 8; PB 3; Aura of Protection +3 on every save.
        var entry = new CombatantSpec
        {
            Archetype = "paladin", Level = 6, Edition = "2024", Name = "Sir Tank", Hp = 99, Ac = 21, Position = "back", Count = 2,
            InitiativeBonus = 5, Saves = new SavesSpec { Wis = 12 },
        };

        var templates = Prepare(entry);

        Assert.Equal(["Sir Tank", "Sir Tank 2"], templates.Select(t => t.Label));
        Assert.All(templates, t =>
        {
            Assert.Equal((99, 21, 5, false, true), (t.AverageHp, t.ArmorClass, t.InitiativeBonus, t.Front, t.PcLike));
            Assert.Equal("2024", t.Edition);
            // wis as given; the other five keep the aura (str 4 + 3, dex 0 + 3, con 1 + 3, int -1 + 3, cha 3 + 3 + 3).
            Assert.Equal([7, 3, 4, 2, 12, 9], t.Saves);
        });
    }

    [Fact]
    public void Overrides_SaveProficiencies_ReplaceTheArchetypesAndItsComputedSaves()
    {
        var templates = Prepare(new CombatantSpec { Archetype = "paladin", Level = 6, Edition = "2024", SaveProficiencies = ["str", "con"] });

        // Proficient in str and con (+3); the aura, computed from the archetype's own proficiencies, goes with them.
        Assert.Equal([7, 0, 4, -1, 1, 3], templates.Single().Saves);
    }

    [Fact]
    public void Overrides_NoneGiven_TheArchetypesOwnNumbers()
    {
        var template = Prepare(new CombatantSpec { Archetype = "wizard", Level = 5 }).Single();
        var member = ArchetypeCatalog.Build("wizard", 5);

        Assert.Equal(("Wizard", member.Hp, member.Ac, false, 1), (template.Label, template.AverageHp, template.ArmorClass, template.Front, template.InitiativeBonus));
        Assert.Equal("2024", template.Edition);
        Assert.Equal([-1, 1, 3, 7, 4, 0], template.Saves); // Int 18 +4 and Wis 12 +1 proficient (+3); Dex 13, Con 16, Cha 10, Str 8
    }

    [Theory]
    // entry edition, fight edition → the archetype's edition
    [InlineData(null, null, "2024")]
    [InlineData(null, "2014", "2014")]
    [InlineData("2024", "2014", "2024")]
    [InlineData("2014", null, "2014")]
    public void Edition_TheEntrysOrElseTheFightsOrElse2024(string? entryEdition, string? fightEdition, string expected)
    {
        var template = Prepare(new CombatantSpec { Archetype = "rogue", Level = 3, Edition = entryEdition }, fightEdition).Single();

        Assert.Equal(expected, template.Edition);
    }

    [Theory]
    [InlineData("necromancer", 5, null, "party item 1 (necromancer): archetype \"necromancer\" is not a party archetype; archetypes are fighter, barbarian, paladin, ranger, rogue, monk, cleric, druid, wizard, sorcerer, warlock, bard")]
    [InlineData("fighter", 21, null, "party item 1 (fighter): level 21 is not a character level; archetypes exist at levels 1-20.")]
    [InlineData("fighter", 5, "3.5", "party item 1 (fighter): edition \"3.5\" is not an edition; give \"2014\" or \"2024\" (default \"2024\").")]
    public void Refusals_NameTheItemAndSayWhatIsAccepted(string archetype, int level, string? edition, string expected)
    {
        var spec = SimKit.Spec([new SimulationCombatant(new CombatantSpec { Archetype = archetype, Level = level, Edition = edition })], [SimKit.Monster(TestStatBlocks.Ogre)]);

        var ex = Assert.Throws<DndInputException>(() => Simulator.Run(spec, Seed));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void ReportAssumptions_SharedRulesOnce_ThenOneLinePerDistinctArchetype()
    {
        var lines = PartyArchetypes.ReportAssumptions([("Fighter", 5, "2024"), ("fighter", 5, "2024"), ("wizard", 5, "2024"), ("fighter", 5, "2014")]);

        Assert.Equal(
            [ArchetypeCatalog.SharedRules, ArchetypeCatalog.Build("fighter", 5, "2024").Summary, ArchetypeCatalog.Build("wizard", 5).Summary, ArchetypeCatalog.Build("fighter", 5, "2014").Summary],
            lines);
        Assert.Empty(PartyArchetypes.ReportAssumptions([]));
    }

    [Theory]
    [MemberData(nameof(ArchetypeTestKit.AllArchetypes), MemberType = typeof(ArchetypeTestKit))]
    public void Summary_IsOneShortLineWithoutWhatTheSharedRulesSay(string name, string edition)
    {
        foreach (var level in new[] { 1, 5, 20 })
        {
            var summary = ArchetypeCatalog.Build(name, level, edition).Summary;

            Assert.StartsWith($"{char.ToUpperInvariant(name[0])}{name[1..]} ({edition}) level {level}: ", summary);
            Assert.DoesNotContain(ArchetypeParts.NoUpcasting, summary, StringComparison.Ordinal);
            Assert.DoesNotContain("\n", summary, StringComparison.Ordinal);
            Assert.InRange(summary.Length, 100, 700); // a four-archetype party adds under 3,000 characters to the report
        }
    }

    [Fact]
    public void Expand_UnknownName_IsRefusedWithTheList()
    {
        var ex = Assert.Throws<DndInputException>(() => PartyArchetypes.Expand("Artificer", 5, null));

        Assert.Equal(ArchetypeCatalog.Refusal("Artificer"), ex.Message);
        Assert.Contains(ArchetypeCatalog.NamesList, ex.Message);
    }

    [Theory]
    // Four level 5 archetypes (fighter, rogue, cleric, wizard) against three SRD ogres. Why they should win nearly always:
    // three CR 2 ogres are 1,350 XP, under the 2024 Low budget for four level 5 characters (4 × 500 = 2,000), and a Medium
    // 2014 fight (2,700 adjusted XP against thresholds 2,000 medium / 3,000 hard). The party (172 HP) opens with Fireball
    // on all three ogres (AC 11, Dex -1: 176 HP in 2014, 204 in 2024), Action Surge and Spirit Guardians, so the ogres
    // barely act. Seen with this seed and 2,000 fights: the party wins every fight in both editions (Wilson 95% [0.998,
    // 1]), in 1.9 (2014) and 2.1 (2024) rounds on average, and nobody dies. The bounds are loose on purpose: a regression
    // that breaks the party's routines (no damage, no area spell) or the ogres' (no attacks) still moves them.
    [InlineData("2024")]
    [InlineData("2014")]
    public void Sanity_FourLevelFiveArchetypes_BeatThreeOgresMostOfTheTime(string edition)
    {
        var ogre = CorrectedSrd.Shipped.StatBlock(edition, "ogre");
        var party = new[] { "fighter", "rogue", "cleric", "wizard" }
            .Select(n => new SimulationCombatant(new CombatantSpec { Archetype = n, Level = 5, Edition = edition }))
            .ToList();

        var report = Simulator.Run(SimKit.Spec(party, [SimKit.Monster(ogre, count: 3)], iterations: 2_000), Seed);

        Assert.InRange(report.PartyWins.Estimate, 0.85, 1.0);
        Assert.InRange(report.Rounds.Mean.Mean, 1.0, 5.0);
        Assert.True(report.AnyPartyDeath.Estimate < 0.15, $"a party member died in {report.AnyPartyDeath.Estimate:P1} of fights.");
        Assert.Equal(4 + 1, report.Assumptions.Count(a => a == ArchetypeCatalog.SharedRules || a.Contains("(" + edition + ") level 5:", StringComparison.Ordinal)));
    }

    private static IReadOnlyList<CombatantTemplate> Prepare(CombatantSpec entry, string? fightEdition = null)
    {
        var run = SimulationPreparation.Prepare(SimKit.Spec([new SimulationCombatant(entry)], [SimKit.Monster(TestStatBlocks.Ogre)], edition: fightEdition));
        return run.Entries[0].Ids.Select(id => run.Setup.Templates[id]).ToList();
    }
}
