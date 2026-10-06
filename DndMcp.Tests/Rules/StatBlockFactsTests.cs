using DndMcp.Domain.Rules;
using DndMcp.Domain.Simulation;
using DndMcp.Repository.Srd.Combatants;
using DndMcp.Tests.Simulation;
using DndMcp.Tests.Srd.Combatants;
using Xunit;
using static DndMcp.Tests.Rules.RulesKit;
using K = DndMcp.Domain.Simulation.StatBlockValues;

namespace DndMcp.Tests.Rules;

/// <summary>
/// Invariant: over EVERY SRD monster of both editions, <see cref="StatBlockFacts.RevivalTraits"/> lists exactly the
/// traits the normalizer's catalogue records as reviving (plus Misty Escape), and
/// <see cref="StatBlockFacts.DeathInterceptors"/> exactly what the simulator compiles (Undead Fortitude, Relentless with
/// its threshold, Regeneration from 0 HP); both are pinned per monster so a re-vendor or a catalogue change is a visible
/// diff. The in-lair counts pick the stat block's own.
/// </summary>
public sealed class StatBlockFactsTests
{
    public static TheoryData<string> Editions => new(["2014", "2024"]);

    /// <summary>Monster → its revival traits, per edition (from the vendored data's special abilities).</summary>
    private static readonly Dictionary<string, string> RevivalExpected = new(StringComparer.Ordinal)
    {
        ["2014/monster/guardian-naga"] = "Rejuvenation",
        ["2014/monster/lemure"] = "Hellish Rejuvenation",
        ["2014/monster/lich"] = "Rejuvenation",
        ["2014/monster/mummy-lord"] = "Rejuvenation",
        ["2014/monster/spirit-naga"] = "Rejuvenation",
        ["2014/monster/vampire-bat"] = "Misty Escape",
        ["2014/monster/vampire-mist"] = "Misty Escape",
        ["2014/monster/vampire-vampire"] = "Misty Escape",
        ["2024/monster/aboleth"] = "Eldritch Restoration",
        ["2024/monster/barbed-devil"] = "Diabolical Restoration",
        ["2024/monster/bone-devil"] = "Diabolical Restoration",
        ["2024/monster/chain-devil"] = "Diabolical Restoration",
        ["2024/monster/deva"] = "Exalted Restoration",
        ["2024/monster/djinni"] = "Elemental Restoration",
        ["2024/monster/efreeti"] = "Elemental Restoration",
        ["2024/monster/erinyes"] = "Diabolical Restoration",
        ["2024/monster/glabrezu"] = "Demonic Restoration",
        ["2024/monster/guardian-naga"] = "Celestial Restoration",
        ["2024/monster/hezrou"] = "Demonic Restoration",
        ["2024/monster/horned-devil"] = "Diabolical Restoration",
        ["2024/monster/ice-devil"] = "Diabolical Restoration",
        ["2024/monster/lemure"] = "Hellish Restoration",
        ["2024/monster/lich"] = "Spirit Jar",
        ["2024/monster/marilith"] = "Demonic Restoration",
        ["2024/monster/mummy-lord"] = "Undead Restoration",
        ["2024/monster/nalfeshnee"] = "Demonic Restoration",
        ["2024/monster/pit-fiend"] = "Diabolical Restoration",
        ["2024/monster/planetar"] = "Exalted Restoration",
        ["2024/monster/rakshasa"] = "Fiendish Restoration",
        ["2024/monster/solar"] = "Exalted Restoration",
        ["2024/monster/spirit-naga"] = "Fiendish Restoration",
        ["2024/monster/vampire-bat"] = "Misty Escape",
        ["2024/monster/vampire-mist"] = "Misty Escape",
        ["2024/monster/vampire-vampire"] = "Misty Escape",
        ["2024/monster/vrock"] = "Demonic Restoration",
    };

    /// <summary>Monster → "kind:amount" of each interceptor.</summary>
    private static readonly Dictionary<string, string> InterceptorsExpected = new(StringComparer.Ordinal)
    {
        ["2014/monster/boar"] = "relentless:7",
        ["2014/monster/giant-boar"] = "relentless:10",
        ["2014/monster/ogre-zombie"] = "undead_fortitude:",
        ["2014/monster/troll"] = "regeneration:10",
        ["2014/monster/wereboar-boar"] = "relentless:14",
        ["2014/monster/wereboar-human"] = "relentless:14",
        ["2014/monster/wereboar-hybrid"] = "relentless:14",
        ["2014/monster/zombie"] = "undead_fortitude:",
        ["2024/monster/ogre-zombie"] = "undead_fortitude:",
        ["2024/monster/troll"] = "regeneration:15",
        ["2024/monster/troll-limb"] = "regeneration:5",
        ["2024/monster/zombie"] = "undead_fortitude:",
    };

    [Theory]
    [MemberData(nameof(Editions))]
    public void RevivalTraits_EverySrdMonster_AreExactlyThePinnedOnes(string edition)
    {
        var actual = CorrectedSrd.Shipped.StatBlocks(edition)
            .Select(b => (b.Ref, Traits: StatBlockFacts.RevivalTraits(b)))
            .Where(r => r.Traits.Count > 0)
            .ToDictionary(r => r.Ref, r => string.Join("; ", r.Traits.Select(t => t.Name)), StringComparer.Ordinal);
        var expected = RevivalExpected.Where(e => e.Key.StartsWith(edition, StringComparison.Ordinal)).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);

        Assert.Equal(expected.OrderBy(e => e.Key, StringComparer.Ordinal), actual.OrderBy(e => e.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void RevivalTraitNames_AreTheCatalogueReviveLaterTraitsPlusMistyEscape()
    {
        var reviving = TraitCatalogue.NoCombatEffect
            .Where(e => e.Value.StartsWith("revives", StringComparison.Ordinal))
            .Select(e => e.Key)
            .Append("Misty Escape")
            .Order(StringComparer.Ordinal);

        Assert.Equal(reviving, StatBlockFacts.RevivalTraitNames.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void RevivalTraits_TheTraitTextIsQuotable()
    {
        var rejuvenation = Assert.Single(StatBlockFacts.RevivalTraits(SrdBlock("2014", "mummy-lord")));

        Assert.Equal("Rejuvenation", rejuvenation.Name);
        Assert.StartsWith("A destroyed mummy lord gains a new body in 24 hours if its heart is intact", rejuvenation.Text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Editions))]
    public void CatalogueName_AgreesWithTheNormalizersOnEveryTraitName(string edition)
    {
        foreach (var name in CorrectedSrd.Shipped.StatBlocks(edition).SelectMany(b => b.Traits).Select(t => t.Name).Distinct())
        {
            Assert.Equal(TraitCatalogue.CatalogueName(name), StatBlockFacts.CatalogueName(name));
        }

        Assert.Equal("Legendary Resistance", StatBlockFacts.CatalogueName("Legendary Resistance (3/Day, or 4/Day in Lair)"));
    }

    [Theory]
    [MemberData(nameof(Editions))]
    public void DeathInterceptors_EverySrdMonster_AreExactlyThePinnedOnes(string edition)
    {
        var actual = CorrectedSrd.Shipped.StatBlocks(edition)
            .Select(b => (b.Ref, Interceptors: StatBlockFacts.DeathInterceptors(b)))
            .Where(r => r.Interceptors.Count > 0)
            .ToDictionary(r => r.Ref, r => string.Join("; ", r.Interceptors.Select(i => $"{i.Kind}:{i.Amount}")), StringComparer.Ordinal);
        var expected = InterceptorsExpected.Where(e => e.Key.StartsWith(edition, StringComparison.Ordinal)).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);

        Assert.Equal(expected.OrderBy(e => e.Key, StringComparer.Ordinal), actual.OrderBy(e => e.Key, StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Editions))]
    public void DeathInterceptors_EverySrdMonster_AgreeWithWhatTheSimulatorCompiles(string edition)
    {
        var party = SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "Fighter");
        foreach (var block in CorrectedSrd.Shipped.StatBlocks(edition))
        {
            var fight = Scripted.Begin([party], [SimKit.Monster(block, name: "M")]);
            var template = fight.Named("M").T;
            var interceptors = StatBlockFacts.DeathInterceptors(block);

            Assert.True(template.UndeadFortitude == interceptors.Any(i => i.Kind == K.TraitKinds.UndeadFortitude), block.Ref);
            Assert.True(template.RelentlessAmount == (interceptors.FirstOrDefault(i => i.Kind == K.TraitKinds.Relentless)?.Amount ?? 0), block.Ref);
            Assert.True(
                (template.RegeneratesFromZero && template.RegenerationAmount > 0) == interceptors.Any(i => i.Kind == K.TraitKinds.Regeneration),
                block.Ref);
        }
    }

    [Theory]
    [InlineData(10, false, false, true)]
    [InlineData(10, false, true, false)]
    [InlineData(10, true, false, false)]
    public void DeathInterceptor_UndeadFortitude_NotOnRadiantOrCritical(int damage, bool critical, bool radiant, bool applies)
    {
        var fortitude = Assert.Single(StatBlockFacts.DeathInterceptors(SrdBlock("2024", "zombie")));

        Assert.Equal(applies, fortitude.CouldApply(damage, critical, radiant));
        Assert.Equal(15, fortitude.SaveDc(damage));
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(11, false)]
    public void DeathInterceptor_Relentless_OnlyWithinItsThreshold(int damage, bool applies)
    {
        var relentless = Assert.Single(StatBlockFacts.DeathInterceptors(SrdBlock("2014", "giant-boar")));

        Assert.Equal(applies, relentless.CouldApply(damage, critical: true, radiant: true));
        Assert.Null(relentless.SaveDc(damage));
    }

    [Theory]
    [InlineData("2014", "giant-boar", 0)]
    [InlineData("2014", "zombie", 1)]
    [InlineData("2014", "troll", 1)]
    public void DeathInterceptors_RelentlessUsed_LeavesOnlyRelentlessOut(string edition, string slug, int left)
    {
        // The engine's RelentlessUsed: once Relentless has kept it alive, it does not until a rest.
        var block = SrdBlock(edition, slug);

        Assert.Equal(left, StatBlockFacts.DeathInterceptors(block, relentlessUsed: true).Count);
        Assert.DoesNotContain(StatBlockFacts.DeathInterceptors(block, relentlessUsed: true), i => i.Kind == K.TraitKinds.Relentless);
        Assert.Equal(StatBlockFacts.DeathInterceptors(block).Where(i => i.Kind != K.TraitKinds.Relentless), StatBlockFacts.DeathInterceptors(block, true));
    }

    [Fact]
    public void DeathInterceptors_RegenerationThatNeedsAHitPoint_IsNotOne()
    {
        Assert.Empty(StatBlockFacts.DeathInterceptors(SrdBlock("2014", "oni")));
        Assert.Empty(StatBlockFacts.DeathInterceptors(SrdBlock("2014", "vampire-vampire")));
        Assert.Equal(K.TraitKinds.Regeneration, Assert.Single(StatBlockFacts.DeathInterceptors(SrdBlock("2014", "troll"))).Kind);
    }

    [Theory]
    [InlineData("2024", "aboleth", false, 3, 3, 5900)]
    [InlineData("2024", "aboleth", true, 4, 4, 7200)]
    [InlineData("2014", "mummy-lord", false, 3, 0, 13000)]
    [InlineData("2014", "mummy-lord", true, 3, 0, 13000)]
    [InlineData("2014", "mummy", true, 0, 0, 700)]
    [InlineData("2024", "tarrasque", true, 3, 6, 155000)]
    public void LairCounts_TheStatBlocksInLairValuesWhenInItsLair(string edition, string slug, bool lair, int legendary, int resistance, int xp)
    {
        var block = SrdBlock(edition, slug);

        Assert.Equal(legendary, StatBlockFacts.LegendaryActionUses(block, lair));
        Assert.Equal(resistance, StatBlockFacts.LegendaryResistanceUses(block, lair));
        Assert.Equal(xp, StatBlockFacts.Xp(block, lair));
    }

    [Theory]
    [MemberData(nameof(Editions))]
    public void LairCounts_EverySrdMonster_NeverFallInItsLair(string edition)
    {
        foreach (var block in CorrectedSrd.Shipped.StatBlocks(edition))
        {
            Assert.True(StatBlockFacts.LegendaryActionUses(block, true) >= StatBlockFacts.LegendaryActionUses(block, false), block.Ref);
            Assert.True(StatBlockFacts.LegendaryResistanceUses(block, true) >= StatBlockFacts.LegendaryResistanceUses(block, false), block.Ref);
            Assert.True(StatBlockFacts.Xp(block, true) >= StatBlockFacts.Xp(block, false), block.Ref);
            Assert.Equal(block.Xp, StatBlockFacts.Xp(block, false));
        }
    }
}
