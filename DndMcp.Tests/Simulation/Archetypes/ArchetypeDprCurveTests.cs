using DndMcp.Domain.Dpr;
using DndMcp.Domain.Simulation.Archetypes;
using Xunit;
using static DndMcp.Tests.Simulation.Archetypes.ArchetypeTestKit;

namespace DndMcp.Tests.Simulation.Archetypes;

/// <summary>
/// Invariant: every archetype's damage curve, read by the closed-form DPR engine, is plausible — pinned at levels 1, 5,
/// 11, 17, 20 against the DMG 2014 row for CR = level (round 1, setup costs paid, the best Action routine: see
/// <see cref="ArchetypeTestKit.BestRound1"/>), never dropping below 85% of its best earlier level (the target's AC and
/// saves step up at some levels while a class gains nothing, so small dips are real), and growing from level 1 to 20.
///
/// <para>
/// "At will" leaves out Action save effects that spend a resource (Fireball, Lightning Bolt, Shatter): they are the
/// casters' nova, pinned separately. Features that cost nothing per round stay in both (Action Surge and smites in round
/// 1, Spirit Guardians, Call Lightning, Hunter's Mark and Hex after their round-1 Bonus Action).
/// </para>
/// <para>
/// Checked by hand: 2014 fighter at 1 = 0.65 × (2 × 25/6 + 3) + 0.05 × 2 × 25/6 = 7.78333; 2014 rogue at 1 = 4.4 + 2.45 +
/// Sneak Attack 3.3075 = 10.1575; 2014 cleric at 1 = 0.6 × 4.5 = 2.7 (DC 13 vs +0); the wizard's level 5 nova is the
/// Phase 4 Fireball golden (89.2); the warlock is the published warlock baseline at every level.
/// </para>
/// </summary>
public sealed class ArchetypeDprCurveTests
{
    private const double Exact = 1e-6;

    private static readonly int[] PinnedLevels = [1, 5, 11, 17, 20];

    [Theory]
    // archetype, edition, round-1 DPR at levels 1, 5, 11, 17, 20
    [InlineData("fighter", "2014", 7.783333333, 33.733333333, 62.7, 62.7, 83.6)]
    [InlineData("fighter", "2024", 8.6, 38.4, 70.2, 70.2, 93.6)]
    [InlineData("barbarian", "2014", 7.8, 16.9, 23.0, 25.7, 32.2)]
    [InlineData("barbarian", "2024", 7.8, 16.9, 22.35, 23.75, 30.25)]
    [InlineData("paladin", "2014", 5.1, 26.7, 38.2, 38.2, 38.2)]
    [InlineData("paladin", "2024", 5.1, 22.605, 33.475, 33.475, 33.475)]
    [InlineData("ranger", "2014", 5.1, 18.8, 23.2, 23.2, 23.2)]
    [InlineData("ranger", "2024", 7.55, 18.8, 23.2, 28.44, 32.67)]
    [InlineData("rogue", "2014", 10.1575, 17.4225, 29.435, 39.515, 42.875)]
    [InlineData("rogue", "2024", 10.783125, 21.35496875, 34.792625, 46.032875, 49.779625)]
    [InlineData("monk", "2014", 7.4, 22.758853172, 28.255442969, 31.692128125, 31.362915313)]
    [InlineData("monk", "2024", 8.8, 25.934652703, 39.637520359, 44.074317422, 57.979367188)]
    [InlineData("cleric", "2014", 2.7, 37.85, 37.45, 36.4, 35.35)]
    [InlineData("cleric", "2024", 2.7, 32.1, 31.05, 30.0, 28.95)]
    [InlineData("druid", "2014", 3.15, 13.1, 12.675, 12.6, 12.6)]
    [InlineData("druid", "2024", 3.15, 13.1, 12.7, 15.85, 15.85)]
    [InlineData("wizard", "2014", 3.85, 7.7, 11.55, 15.4, 15.4)]
    [InlineData("wizard", "2024", 3.85, 7.7, 11.55, 15.4, 15.4)]
    [InlineData("sorcerer", "2014", 3.85, 7.7, 11.55, 15.4, 15.4)]
    [InlineData("sorcerer", "2024", 5.3625, 10.725, 16.0875, 21.45, 21.45)]
    [InlineData("warlock", "2014", 6.3, 17.8, 28.65, 38.2, 38.2)]
    [InlineData("warlock", "2024", 6.3, 17.8, 28.65, 38.2, 38.2)]
    [InlineData("bard", "2014", 1.5, 3.0, 4.125, 5.0, 4.5)]
    [InlineData("bard", "2024", 3.15, 6.3, 9.45, 12.6, 12.6)]
    public void AtWillRound1_AgainstTheCrEqualsLevelRow_IsPinned(string name, string edition, double l1, double l5, double l11, double l17, double l20)
    {
        double[] expected = [l1, l5, l11, l17, l20];

        for (var i = 0; i < PinnedLevels.Length; i++)
        {
            var actual = BestRound1(ArchetypeCatalog.Build(name, PinnedLevels[i], edition), includeLimited: false);
            Assert.True(Math.Abs(expected[i] - actual) <= Exact, $"{name} {edition} level {PinnedLevels[i]}: expected {expected[i]}, got {actual}.");
        }
    }

    [Theory]
    // The casters' round-1 nova with the limited area spell (against the CR = level row's creatures, all targets summed).
    // archetype, edition, level, nova
    [InlineData("wizard", "2014", 5, 89.2)]       // Fireball DC 15 vs +2: 4 × (0.6 × 28 + 0.4 × 13.75)
    [InlineData("wizard", "2024", 20, 80.65)]
    [InlineData("sorcerer", "2024", 5, 92.05)]    // Innate Sorcery: DC 16
    [InlineData("bard", "2014", 3, 20.7)]         // Shatter 3d8 on 2 creatures, the 2014 bard's best round
    [InlineData("bard", "2024", 5, 21.4)]
    public void NovaRound1_WithTheAreaSpell_IsPinned(string name, string edition, int level, double nova)
    {
        var member = ArchetypeCatalog.Build(name, level, edition);

        Assert.Equal(nova, BestRound1(member, includeLimited: true), Exact);
        Assert.True(BestRound1(member, includeLimited: true) > BestRound1(member, includeLimited: false));
    }

    [Theory]
    [MemberData(nameof(AllArchetypes), MemberType = typeof(ArchetypeTestKit))]
    public void AtWillCurve_IsMonotoneWithinTheTargetsSteps_AndGrows(string name, string edition)
    {
        var curve = Enumerable.Range(1, 20).Select(l => BestRound1(ArchetypeCatalog.Build(name, l, edition), includeLimited: false)).ToList();

        var best = curve[0];
        for (var i = 1; i < curve.Count; i++)
        {
            // The DMG row's AC and the typical save step up at some levels where a class gains nothing (e.g. 9 → 10: AC 16
            // → 17), so a curve may dip; never by more than 15% of its best so far.
            Assert.True(curve[i] >= 0.85 * best, $"{name} {edition}: level {i + 1} deals {curve[i]:0.00}, under 85% of {best:0.00} earlier.");
            best = Math.Max(best, curve[i]);
        }

        Assert.True(curve[19] >= 2 * curve[0], $"{name} {edition}: level 20 ({curve[19]:0.00}) is not twice level 1 ({curve[0]:0.00}).");
        Assert.All(curve, d => Assert.True(d > 0));
    }

    [Theory]
    [InlineData("2014")]
    [InlineData("2024")]
    public void Warlock_Round1_IsThePublishedWarlockBaseline(string edition)
    {
        // Hex's round-1 Bonus Action costs the archetype nothing it would otherwise do, so its round 1 equals the preset's
        // (which assumes Hex is up): the same curve by two routes.
        for (var level = 1; level <= 20; level++)
        {
            Assert.Equal(ReferenceCurves.WarlockBaseline(level), BestRound1(ArchetypeCatalog.Build("warlock", level, edition), includeLimited: false), 1e-9);
        }
    }
}
