using System.Globalization;
using System.Text;
using DndMcp.Domain.Encounters;
using DndMcp.Domain.Simulation;
using DndMcp.Repository.Srd;
using DndMcp.Tests.Simulation;
using DndMcp.Tests.Srd.Combatants;
using Xunit;

namespace DndMcp.Tests.Encounters;

/// <summary>
/// Invariant: the pinned <see cref="MonsterStatsEmpirical"/> table is exactly what the normalized SRD monsters give.
/// Every cell is recomputed from the vendored data (with the corrections, overrides and overlay the server uses); a
/// difference fails with the regenerated rows to paste, so a re-vendor or a normalizer change that moves a median is a
/// reviewed diff and the table can never drift from the data.
/// </summary>
public sealed class MonsterStatsEmpiricalTests
{
    [Theory]
    [InlineData("2014")]
    [InlineData("2024")]
    public void Rows_EveryCell_IsWhatTheNormalizedMonstersGive(string edition)
    {
        var computed = MonsterStatsEmpirical.Compute(CorrectedSrd.Shipped.StatBlocks(edition));

        Assert.True(computed.SequenceEqual(MonsterStatsEmpirical.Rows(edition)), "Regenerated rows:\n" + Render(edition, computed));
    }

    [Theory]
    [InlineData("2014", 334, 12)]
    [InlineData("2024", 341, 12)]
    public void Rows_CountEveryMonsterOnceAndEachShapechangerOnce(string edition, int monsters, int extraForms)
    {
        // Six shapechangers (vampire and five lycanthropes) have three records each: two per group are not counted.
        Assert.Equal(monsters - extraForms, MonsterStatsEmpirical.Rows(edition).Sum(r => r.Count));
    }

    [Theory]
    [InlineData("2014", "5", 15, 95)]
    [InlineData("2024", "5", 15, 104)]
    [InlineData("2024", "17", 19, 249.5)]
    public void MonsterStats_ACrWithMonsters_IsItsRow(string edition, string cr, double ac, double hp)
    {
        var row = MonsterStatsEmpirical.MonsterStats(edition, ChallengeRating.Parse(cr));

        Assert.False(row.IsInterpolated);
        Assert.Equal((ac, hp), (row.ArmorClass, row.HitPoints));
    }

    [Theory]
    [InlineData("2014")]
    [InlineData("2024")]
    public void MonsterStats_CR18_HasNoSrdMonsterAndIsInterpolatedFromItsNeighbours(string edition)
    {
        var cr17 = MonsterStatsEmpirical.MonsterStats(edition, ChallengeRating.Parse("17"));
        var cr19 = MonsterStatsEmpirical.MonsterStats(edition, ChallengeRating.Parse("19"));

        var cr18 = MonsterStatsEmpirical.MonsterStats(edition, ChallengeRating.Parse("18"));

        Assert.True(cr18.IsInterpolated);
        Assert.Equal(0, cr18.Count);
        Assert.Equal(Math.Round((cr17.HitPoints + cr19.HitPoints) / 2, 2), cr18.HitPoints);
        Assert.InRange(cr18.SaveDc, cr17.SaveDc, 30);
    }

    [Theory]
    [InlineData("2014")]
    [InlineData("2024")]
    public void MonsterStats_CR25_IsOneSixthOfTheWayFromCR24ToCR30(string edition)
    {
        var cr24 = MonsterStatsEmpirical.MonsterStats(edition, ChallengeRating.Parse("24"));
        var cr30 = MonsterStatsEmpirical.MonsterStats(edition, ChallengeRating.Parse("30"));

        var cr25 = MonsterStatsEmpirical.MonsterStats(edition, ChallengeRating.Parse("25"));

        Assert.True(cr25.IsInterpolated);
        Assert.Equal(Math.Round(cr24.HitPoints + ((cr30.HitPoints - cr24.HitPoints) / 6), 2), cr25.HitPoints);
    }

    [Theory]
    [InlineData("2014")]
    [InlineData("2024")]
    public void MonsterStats_EveryCr_HasARow(string edition)
    {
        Assert.All(ChallengeRating.All, cr =>
        {
            var row = MonsterStatsEmpirical.MonsterStats(edition, cr);
            Assert.Equal(cr, row.ChallengeRating);
            Assert.InRange(row.ArmorClass, 5, 26);
            Assert.True(row.HitPoints > 0);
        });
    }

    [Fact]
    public void Compute_AShapechangersForms_CountOnceAsTheHybrid()
    {
        // Three records of one shapechanger (human, hybrid, animal): one monster, counted as the form it fights in.
        StatBlock Form(string slug, int ac) =>
            TestStatBlocks.Create("Werething", ac, 50, "10d8", (10, 10, 10, 10, 10, 10), [TestStatBlocks.Attack("Bite", 4, "1d6", "piercing")]) with
            {
                Ref = $"2014/monster/{slug}",
                Forms = new[] { "werething-animal", "werething-human", "werething-hybrid" }.Where(s => s != slug).Select(s => $"2014/monster/{s}").ToList(),
            };

        var rows = MonsterStatsEmpirical.Compute([Form("werething-animal", 13), Form("werething-human", 11), Form("werething-hybrid", 15)]);

        Assert.Equal((1, 15.0), (rows.Single().Count, rows.Single().ArmorClass));
    }

    [Fact]
    public void Rows_UnknownEdition_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MonsterStatsEmpirical.Rows("2020"));
    }

    private static string Render(string edition, IReadOnlyList<MonsterStatsEmpiricalRow> rows)
    {
        var text = new StringBuilder($"    private static readonly MonsterStatsEmpiricalRow[] Rows{edition} =\n    [\n");
        foreach (var r in rows)
        {
            string D(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
            text.Append(CultureInfo.InvariantCulture,
                $"        new(ChallengeRating.All[{r.ChallengeRating.Row}], {r.Count}, {D(r.ArmorClass)}, {D(r.HitPoints)}, {D(r.AttackBonus)}, {r.AttackCount}, {D(r.SaveDc)}, {r.SaveDcCount}, {D(r.MeanSaveBonus)}),\n");
        }

        return text.Append("    ];\n").ToString();
    }
}
