using System.Text.Json;
using DndMcp.Domain.Dpr;
using Xunit;

namespace DndMcp.Tests.Dpr;

/// <summary>
/// Invariant: the engine agrees with the independent brute-force oracle (Dpr/Fixtures/oracle, exact rational arithmetic,
/// an explicit outcome tree per turn instead of a memoised state programme) on every round1 and fight case, to 1e-9 on
/// expectations and 1e-12 on probabilities: DPR, P(0), P(≥ HP), the whole round-1 distribution where the oracle lists it,
/// each optional rider's damage, uses and damage per use, the power-attack choice per advantage sample, and a save
/// effect's per-cast and kill figures.
///
/// <para>
/// The two implementations share no code and were written from the contract alone, so they can only agree while both
/// are right or both misread the same sentence; the oracle's README lists every reading it had to choose. A case is never
/// skipped: the day-horizon cases are pinned to a known list below and belong to the day horizon's tests.
/// </para>
/// </summary>
public sealed class OracleCaseTests
{
    private const double ExpectationTolerance = 1e-9;
    private const double ProbabilityTolerance = 1e-12;

    /// <summary>
    /// Day-horizon cases: evaluated by the day horizon on top of this engine, not by it (contract §4.4), in
    /// <see cref="DayHorizonTests"/>, whose theory takes every day case in the file. Pinned here so a day case can neither be
    /// added nor lost without this list changing.
    /// </summary>
    private static readonly string[] DayCases =
    [
        "horizon-action-surge-day-e6",
        "horizon-action-surge-day-e8",
        "horizon-action-surge-2024-day-e4-s1",
        "horizon-action-surge-day-dmg2014-preset",
        "horizon-action-surge-day-light-preset",
        "horizon-smite-day",
        "horizon-two-limited-features-day",
    ];

    public static TheoryData<string> EngineCases()
    {
        var data = new TheoryData<string>();
        foreach (var c in OracleCases.All.Where(c => OracleCases.HorizonKind(c) != DprHorizons.Day))
        {
            data.Add(OracleCases.Id(c));
        }

        return data;
    }

    [Fact]
    public void Cases_EveryCaseIsRound1FightOrAKnownDayCase()
    {
        var day = OracleCases.All.Where(c => OracleCases.HorizonKind(c) == DprHorizons.Day).Select(OracleCases.Id).ToList();

        Assert.Equal(DayCases.Order(), day.Order());
        Assert.All(OracleCases.All, c => Assert.Contains(OracleCases.HorizonKind(c), DprHorizons.All));
        Assert.True(EngineCases().Cast<object[]>().Count() >= 250, "the oracle's round1 and fight cases should all be here");
    }

    [Theory]
    [MemberData(nameof(EngineCases))]
    public void Evaluate_OracleCase_MatchesTheOracle(string id)
    {
        var c = OracleCases.Case(id);
        var expected = c.GetProperty("expected");

        var result = OracleCases.Evaluate(c);

        Near(OracleCases.Number(expected.GetProperty("dpr")), result.DamagePerRound, ExpectationTolerance, $"{id}: dpr");
        if (OracleCases.TryGet(expected, "p_zero", out var zero))
        {
            Near(OracleCases.Number(zero), result.Round1Distribution!.ZeroChance, ProbabilityTolerance, $"{id}: p_zero");
        }

        if (OracleCases.TryGet(expected, "p_at_least_hp", out var atLeast))
        {
            Near(OracleCases.Number(atLeast), result.Round1Distribution!.AtLeastHitPointsChance!.Value, ProbabilityTolerance, $"{id}: p_at_least_hp");
        }

        if (OracleCases.TryGet(expected, "pmf", out var pmf))
        {
            CheckDistribution(id, pmf, result.Round1Distribution!);
        }

        if (OracleCases.TryGet(expected, "riders", out var riders))
        {
            CheckRiders(id, riders, result);
        }

        if (OracleCases.TryGet(expected, "power_attack", out var power))
        {
            CheckPowerAttack(id, power, result);
        }

        if (OracleCases.TryGet(expected, "save", out var save))
        {
            CheckSave(id, save, result);
        }
    }

    private static void Near(double expected, double actual, double tolerance, string what)
    {
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"{what}: expected {expected:R}, engine {actual:R} (difference {actual - expected:R})");
    }

    private static void CheckDistribution(string id, JsonElement pmf, DamageDistribution distribution)
    {
        var expected = pmf.EnumerateArray().ToDictionary(p => p[0].GetInt64(), p => OracleCases.Exact(p[1].GetString()!));
        foreach (var (damage, probability) in expected)
        {
            var actual = distribution.Values.SingleOrDefault(v => v.Damage == damage)?.Probability ?? 0;
            Near(probability, actual, ProbabilityTolerance, $"{id}: P(damage = {damage})");
        }

        var extra = distribution.Values.Where(v => !expected.ContainsKey(v.Damage)).ToList();
        Assert.True(extra.Count == 0, $"{id}: the engine's distribution has totals the oracle's does not: {string.Join(", ", extra.Select(v => v.Damage))}");
        Near(1, distribution.Values.Sum(v => v.Probability), ProbabilityTolerance, $"{id}: total probability");
    }

    private static void CheckRiders(string id, JsonElement riders, DprResult result)
    {
        foreach (var row in riders.EnumerateArray())
        {
            var name = row.GetProperty("name").GetString()!;
            var rider = result.Riders.SingleOrDefault(r => r.Name == name);
            Assert.True(rider is not null, $"{id}: no rider named {name}");
            Near(OracleCases.Number(row.GetProperty("expected_damage")), rider!.DamagePerRound, ExpectationTolerance, $"{id}: {name} expected_damage");
            Near(OracleCases.Number(row.GetProperty("uses_per_round")), rider.UsesPerRound, ExpectationTolerance, $"{id}: {name} uses_per_round");
            if (OracleCases.TryGet(row, "damage_per_use", out var perUse))
            {
                Near(OracleCases.Number(perUse), rider.DamagePerUse!.Value, ExpectationTolerance, $"{id}: {name} damage_per_use");
            }
            else
            {
                Assert.Null(rider.DamagePerUse);
            }
        }
    }

    private static void CheckPowerAttack(string id, JsonElement rows, DprResult result)
    {
        var power = Assert.Single(result.PowerAttacks);
        var expected = rows.EnumerateArray().ToList();
        Assert.Equal(expected.Count, power.Round1Choices.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Near(OracleCases.Exact(expected[i].GetProperty("probability").GetString()!), power.Round1Choices[i].Probability, ProbabilityTolerance, $"{id}: power attack sample {i} probability");
            Assert.True(expected[i].GetProperty("on").GetBoolean() == power.Round1Choices[i].On, $"{id}: power attack sample {i} on");
        }
    }

    private static void CheckSave(string id, JsonElement save, DprResult result)
    {
        var effect = Assert.Single(result.SaveEffects);
        Assert.Equal(save.GetProperty("targets").GetInt32(), effect.Targets);
        Assert.Equal(save.GetProperty("dc").GetInt32(), effect.Dc);
        Near(OracleCases.Number(save.GetProperty("per_target_fail")), effect.FailChance, ProbabilityTolerance, $"{id}: per_target_fail");
        Near(OracleCases.Number(save.GetProperty("expected_damage_per_target")), effect.DamagePerTarget, ExpectationTolerance, $"{id}: expected_damage_per_target");
        Near(OracleCases.Number(save.GetProperty("raw")), effect.RawDamage, ExpectationTolerance, $"{id}: raw");
        if (OracleCases.TryGet(save, "effective", out var effective))
        {
            Near(OracleCases.Number(effective), effect.EffectiveDamage!.Value, ExpectationTolerance, $"{id}: effective");
            Near(OracleCases.Number(save.GetProperty("p_each_dies")), effect.KillChanceEach!.Value, ProbabilityTolerance, $"{id}: p_each_dies");
            Near(OracleCases.Number(save.GetProperty("p_all_die")), effect.AllDieChance!.Value, ProbabilityTolerance, $"{id}: p_all_die");
            Near(OracleCases.Number(save.GetProperty("expected_kills")), effect.ExpectedKills!.Value, ExpectationTolerance, $"{id}: expected_kills");
            var kills = save.GetProperty("kill_distribution").EnumerateArray().ToList();
            Assert.Equal(kills.Count, effect.KillDistribution!.Count);
            foreach (var row in kills)
            {
                var k = row[0].GetInt32();
                Near(OracleCases.Exact(row[1].GetString()!), effect.KillDistribution[k], ProbabilityTolerance, $"{id}: P({k} die)");
            }
        }
        else
        {
            Assert.Null(effect.EffectiveDamage);
        }

        if (OracleCases.TryGet(save, "expected_casts_to_land", out var casts))
        {
            Near(OracleCases.Number(casts), effect.ExpectedCastsToLand!.Value, ExpectationTolerance, $"{id}: expected_casts_to_land");
        }
        else
        {
            Assert.Null(effect.ExpectedCastsToLand);
        }
    }
}
