using DndMcp.Domain.Core;
using DndMcp.Domain.Encounters;
using DndMcp.Domain.Features;
using Xunit;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Tests.Features;

/// <summary>
/// Invariant: the warlock_baseline preset is the published community baseline at every level 1–20: Eldritch Blast
/// (a Cha spell attack, 1d10 force per beam, 1/2/3/4 beams at 1/5/11/17), Agonizing Blast from level 2, Hex on every hit
/// with no setup cost, Cha 16/18/20 — and so, against the CR = level row, it reproduces the published DPR curve.
/// </summary>
public sealed class BuildPresetsTests
{
    private static ResolvedBuild Warlock(int level, string edition = "2024") =>
        BuildResolver.Resolve(new BuildSpec { Name = "EB", Preset = "warlock_baseline", Edition = edition, Level = level }, level);

    [Theory]
    [InlineData(1, 16, 1, false)]
    [InlineData(2, 16, 1, true)]
    [InlineData(3, 16, 1, true)]
    [InlineData(4, 18, 1, true)]
    [InlineData(5, 18, 2, true)]
    [InlineData(7, 18, 2, true)]
    [InlineData(8, 20, 2, true)]
    [InlineData(10, 20, 2, true)]
    [InlineData(11, 20, 3, true)]
    [InlineData(16, 20, 3, true)]
    [InlineData(17, 20, 4, true)]
    [InlineData(20, 20, 4, true)]
    public void WarlockBaseline_ChaBeamsAndAgonizing_FollowTheLevel(int level, int cha, int beams, bool agonizing)
    {
        var build = Warlock(level);

        Assert.Equal(cha, build.Abilities.Cha);
        var blast = Assert.Single(build.Attacks);
        Assert.Equal(beams, blast.Count);
        Assert.Equal(beams, blast.CantripMultiplier);
        Assert.Equal("1d10", blast.Damage.Text);
        var mod = DslLimits.AbilityModifier(cha);
        Assert.Equal(mod + DslLimits.ProficiencyBonus(level), blast.AttackBonus);
        IReadOnlyList<DamagePart> parts = agonizing ? [new DamagePart("Agonizing Blast", mod, DamagePartSources.BonusDamage)] : [];
        Assert.Equal(parts, blast.DamageParts);
    }

    [Fact]
    public void WarlockBaseline_EveryLevel_FollowsTheBaselinesSteps()
    {
        var builds = BuildResolver.Resolve(BuildPresets.WarlockBaseline(1), Enumerable.Range(1, 20).ToList());

        Assert.Equal([16, 16, 16, 18, 18, 18, 18, 20, 20, 20, 20, 20, 20, 20, 20, 20, 20, 20, 20, 20], builds.Select(b => b.Abilities.Cha));
        Assert.Equal([1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3, 3, 4, 4, 4, 4], builds.Select(b => b.Attacks[0].Count));
        Assert.Equal(
            [0, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5],
            builds.Select(b => b.Attacks[0].FlatDamage(partOfAttackAction: true)));
        Assert.All(builds, b => Assert.Equal("1d6", Assert.Single(b.Riders).Damage.Text));
    }

    [Fact]
    public void WarlockBaseline_Blast_IsARangedForceSpellAttackWithHexAndNoSetup()
    {
        var build = Warlock(5);

        var blast = build.Attacks[0];
        Assert.Equal(("Eldritch Blast", V.Abilities.Cha, "force"), (blast.Name, blast.Ability, blast.DamageType));
        Assert.True(blast.IsRanged);
        Assert.True(blast.IsSpell);
        Assert.False(blast.IsWeapon);
        Assert.Equal(V.Cantrips.Beams, blast.Cantrip);
        Assert.Equal(0, blast.AbilityDamage);

        var hex = Assert.Single(build.Riders);
        Assert.Equal(("Hex", "1d6", "necrotic", V.When.EveryHit), (hex.Source.Label, hex.Damage.Text, hex.DamageType, hex.When));
        Assert.True(hex.Concentration);
        Assert.True(hex.CritDoubles);
        Assert.False(hex.IsOptional);
        Assert.Empty(build.SetupCosts);
        Assert.Empty(build.Notes);
        Assert.True(build.ScalesWithLevel);
    }

    /// <summary>
    /// Research B3's published curve, recomputed from the resolved preset with the single-attack closed form (normal roll,
    /// crit on 20 only, crit doubles the dice, flat not doubled) against the DMG row for CR = level. This pins that the
    /// preset's numbers are the baseline's; the DPR engine's own golden test pins the engine against the same values.
    /// </summary>
    [Theory]
    [InlineData(1, 6.30)]
    [InlineData(2, 8.25)]
    [InlineData(3, 8.25)]
    [InlineData(4, 8.90)]
    [InlineData(5, 17.80)]
    [InlineData(8, 19.10)]
    [InlineData(9, 20.50)]
    [InlineData(10, 19.10)]
    [InlineData(11, 28.65)]
    [InlineData(16, 28.65)]
    [InlineData(17, 38.20)]
    [InlineData(20, 38.20)]
    public void WarlockBaseline_AgainstTheCrEqualsLevelRow_ReproducesThePublishedCurve(int level, double dpr)
    {
        var build = Warlock(level);
        var target = TargetResolver.Resolve(null, level);
        var blast = build.Attacks[0];
        var hex = build.Riders[0];

        var hitFaces = Enumerable.Range(1, 20).Count(f => f == 20 || (f != 1 && f + blast.AttackBonus >= target.ArmorClass));
        var hit = hitFaces / 20.0;
        const double crit = 0.05;
        var dice = 5.5 + 3.5;
        Assert.Equal("1d6", hex.Damage.Text);
        var perBeam = (hit - crit) * (dice + blast.FlatDamage(true)) + crit * (2 * dice + blast.FlatDamage(true));

        Assert.Equal(dpr, blast.Count * perBeam, 9);
    }

    [Fact]
    public void Expand_TakesNameEditionAndLevelFromTheSpec()
    {
        var spec = BuildPresets.Expand(new BuildSpec { Name = "My warlock", Preset = "Warlock-Baseline", Edition = "2014", Level = 7 });

        Assert.Null(spec.Preset);
        Assert.Equal(("My warlock", "2014", 7), (spec.Name, spec.Edition, spec.Level));
        Assert.Single(spec.Attacks!);
        Assert.Equal(2, spec.Modifiers!.Count);
    }

    [Fact]
    public void Expand_NoPreset_IsTheSpecItself()
    {
        var spec = new BuildSpec { Name = "X", Level = 1 };

        Assert.Same(spec, BuildPresets.Expand(spec));
    }

    [Fact]
    public void Expand_UnknownPreset_IsRefused()
    {
        var ex = Assert.Throws<DndInputException>(() => BuildPresets.Expand(new BuildSpec { Name = "X", Preset = "wizard", Level = 1 }));

        Assert.Equal("preset \"wizard\" is not a preset; presets are warlock_baseline.", ex.Message);
    }

    [Fact]
    public void WarlockBaseline_IsValidAtEveryLevelInBothEditions()
    {
        foreach (var edition in new[] { "2014", "2024" })
        {
            var builds = BuildResolver.Resolve(BuildPresets.WarlockBaseline(1, edition), Enumerable.Range(1, 20).ToList());

            Assert.Equal(Enumerable.Range(1, 20), builds.Select(b => b.Level));
            Assert.All(builds, b => Assert.Equal(edition, b.Edition));
        }

        Assert.Equal(["warlock_baseline"], BuildPresets.Names);
        Assert.True(BuildResolver.ScalesWithLevel(new BuildSpec { Name = "EB", Preset = "warlock_baseline", Level = 1 }));
    }

    [Fact]
    public void WarlockBaseline_TargetRowsUsed_AreTheDmgRows()
    {
        Assert.Equal(13, TargetResolver.Resolve(null, 1).ArmorClass);
        Assert.Equal(ChallengeRatingTables.MonsterStats(ChallengeRating.Parse("9")).ArmorClass, TargetResolver.Resolve(null, 9).ArmorClass);
    }
}
