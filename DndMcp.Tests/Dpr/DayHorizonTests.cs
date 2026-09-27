using DndMcp.Domain.Core;
using DndMcp.Domain.Dice;
using DndMcp.Domain.Dpr;
using Xunit;
using static DndMcp.Tests.Dpr.DprTestKit;

namespace DndMcp.Tests.Dpr;

/// <summary>
/// Invariant: the day horizon is the fight DPR with every resource-limited feature removed, plus for each feature
/// min(U, u·E·R) · d ÷ (E·R), with u and d measured in a fight where that feature alone is added back with unlimited uses
/// (contract §4.4) — checked against every day case of the independent oracle (each part of the sum, not only the total)
/// and against hand arithmetic for the paths the oracle has no case for: a build whose only damage is limited, a feature
/// that is never used or lowers damage, more uses than a day wants, and short rests multiplying uses.
/// </summary>
public sealed class DayHorizonTests
{
    private const double Exact = 1e-9;

    public static TheoryData<string> OracleDayCases()
    {
        var data = new TheoryData<string>();
        foreach (var c in OracleCases.All.Where(c => OracleCases.HorizonKind(c) == DprHorizons.Day))
        {
            data.Add(OracleCases.Id(c));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(OracleDayCases))]
    public void Evaluate_OracleDayCase_MatchesEveryPartOfTheOraclesSum(string id)
    {
        var c = OracleCases.Case(id);
        var (build, target) = OracleCases.Resolve(c);
        var horizon = c.GetProperty("horizon");
        var encounters = horizon.GetProperty("encounters_per_day").GetDouble();
        var shortRests = horizon.GetProperty("short_rests").GetInt32();
        var day = OracleCases.TryGet(horizon, "rest_preset", out var preset)
            ? DayAssumptions.Resolve(preset.GetString(), null, null)
            : DayAssumptions.Resolve("custom", encounters, shortRests);
        Assert.Equal(encounters, day.EncountersPerDay); // a preset's numbers are the oracle's
        Assert.Equal(shortRests, day.ShortRests);

        var result = HorizonEvaluator.Evaluate(build, target, HorizonSettings.ForDay(day, horizon.GetProperty("rounds").GetInt32()));

        var expected = c.GetProperty("expected");
        var expectedDay = expected.GetProperty("day");
        Assert.Equal(OracleCases.Number(expected.GetProperty("dpr")), result.DamagePerRound, Exact);
        Assert.Equal(OracleCases.Number(expectedDay.GetProperty("fight_dpr_without_limited_features")), result.Day!.Base.DamagePerRound, Exact);
        var features = expectedDay.GetProperty("features").EnumerateArray().ToList();
        Assert.Equal(features.Count, result.Day.Features.Count);
        foreach (var feature in features)
        {
            var name = feature.GetProperty("name").GetString();
            var actual = Assert.Single(result.Day.Features, f => f.Source.Label == name);
            Assert.Equal(feature.GetProperty("uses_per_day").GetInt32(), actual.UsesPerDay);
            Assert.Equal(OracleCases.Number(feature.GetProperty("uses_per_round_unlimited")), actual.UsesPerRound, Exact);
            Assert.Equal(OracleCases.Number(feature.GetProperty("marginal_damage_per_use")), actual.DamagePerUse!.Value, Exact);
            Assert.Equal(OracleCases.Number(feature.GetProperty("contribution")), actual.Contribution, Exact);
        }
    }

    [Fact]
    public void OracleDayCases_AreAllHere()
    {
        // OracleCaseTests pins the day cases by id; this theory must see each of them.
        Assert.Equal(7, OracleDayCases().Cast<object[]>().Count());
    }

    private const string Smite2014 = """
        { "name": "Paladin", "edition": "2014", "level": 5, "abilities": {"str": 18},
          "attacks": [{ "name": "Longsword", "count": 2, "damage": "1d8", "damage_type": "slashing", "properties": ["melee", "versatile"] }],
          "modifiers": [{ "kind": "extra_damage", "name": "Divine Smite", "dice": "2d8", "type": "radiant", "resource": { "uses": USES, "per": "long_rest" } }] }
        """;

    private static HorizonResult Day(string build, string? target, DayAssumptions day, int rounds = 3)
    {
        var (resolved, resolvedTarget) = Resolve(build, target);
        return HorizonEvaluator.Evaluate(resolved, resolvedTarget, HorizonSettings.ForDay(day, rounds));
    }

    [Fact]
    public void Evaluate_SmiteOverTheDmgDay_IsTheHandArithmetic()
    {
        // +7 vs AC 15, two longswords (1d8+4): 2 × (0.60 × 8.5 + 0.05 × 13) = 11.5 without the smite.
        // Unlimited, the smite goes on every hit: u = 2 × 0.65 = 1.3 a round, d = (0.60 × 9 + 0.05 × 18) ÷ 0.65 = 126/13.
        // U = 3 (long rest); E × R = 7 × 3 = 21; min(3, 1.3 × 21 = 27.3) = 3 → 3 × 126/13 ÷ 21 = 18/13 → 11.5 + 18/13 = 335/26.
        var result = Day(Smite2014.Replace("USES", "3", StringComparison.Ordinal), """{ "ac": 15 }""", DayAssumptions.Dmg2014);

        var smite = Assert.Single(result.Day!.Features);
        Assert.Equal(11.5, result.Day.Base.DamagePerRound, Exact);
        Assert.Equal(3, smite.UsesPerDay);
        Assert.Equal(1.3, smite.UsesPerRound, Exact);
        Assert.Equal(126.0 / 13, smite.DamagePerUse!.Value, Exact);
        Assert.Equal(27.3, smite.UsesWantedPerDay, Exact);
        Assert.Equal(3, smite.UsesSpentPerDay, Exact);
        Assert.True(smite.LimitedByUses);
        Assert.Equal(18.0 / 13, smite.Contribution, Exact);
        Assert.Equal(335.0 / 26, result.DamagePerRound, Exact);
        Assert.Equal(21, result.Day.RoundsPerDay, Exact);
    }

    [Fact]
    public void Evaluate_MoreUsesThanTheDayWants_IsTheFeatureAlwaysOn()
    {
        // Light day: E × R = 3.5 × 3 = 10.5 rounds want 1.3 × 10.5 = 13.65 smites; 20 are there, so all 13.65 are spent and
        // the day is the unlimited fight: 11.5 + 1.3 × 126/13 = 11.5 + 12.6 = 24.1.
        var result = Day(Smite2014.Replace("USES", "20", StringComparison.Ordinal), """{ "ac": 15 }""", DayAssumptions.Light);

        var smite = Assert.Single(result.Day!.Features);
        Assert.False(smite.LimitedByUses);
        Assert.Equal(13.65, smite.UsesSpentPerDay, Exact);
        Assert.Equal(12.6, smite.Contribution, Exact);
        Assert.Equal(24.1, result.DamagePerRound, Exact);
        Assert.Equal(smite.UnlimitedDamagePerRound, result.DamagePerRound, Exact);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 3)]
    [InlineData(5, 6)]
    public void Evaluate_ShortRestFeature_GetsItsUsesBackAtEachShortRest(int shortRests, int usesPerDay)
    {
        // Action Surge (1 per short rest) on the §8.2 fighter: U = 1 × (S + 1); its d is the exact marginal 62472773/3200000.
        var build = GoldenBuilds.Fighter2014(GoldenBuilds.Gwm2014 + ", " + GoldenBuilds.ActionSurge);

        var result = Day(build, """{ "ac": 15 }""", DayAssumptions.Custom(6, shortRests));

        var surge = Assert.Single(result.Day!.Features);
        Assert.Equal(usesPerDay, surge.UsesPerDay);
        Assert.Equal(1, surge.UsesPerRound, Exact); // unlimited, it is taken every turn
        Assert.Equal(62472773.0 / 3200000, surge.DamagePerUse!.Value, Exact);
        Assert.Equal(156893.0 / 8000 + (usesPerDay * (62472773.0 / 3200000) / 18), result.DamagePerRound, Exact);
    }

    [Fact]
    public void Evaluate_OnlyDamageIsLimited_TheBaseIsZeroAndTheDayIsTheAmortizedCasts()
    {
        // Fireball 3/day on four goblins (§8.8's 89.2 raw per cast). Without it the build does nothing (its Action is the
        // save effect), so the day is 3 casts × 89.2 over 7 × 3 rounds = 267.6/21 = 12.742857…
        const string wizard = """
            { "name": "Wizard", "edition": "2014", "level": 5, "abilities": {"int": 16},
              "modifiers": [{ "kind": "save_effect", "name": "Fireball", "ability": "dex", "dc": 15, "dice": "8d6", "type": "fire",
                              "shape": "sphere", "size": 20, "resource": { "uses": 3, "per": "long_rest" } }] }
            """;

        var result = Day(wizard, """{ "saves": {"dex": 2}, "hp": 7 }""", DayAssumptions.Dmg2014);

        var fireball = Assert.Single(result.Day!.Features);
        Assert.Equal(0, result.Day.Base.DamagePerRound);
        Assert.Equal(1, fireball.UsesPerRound, Exact);
        Assert.Equal(89.2, fireball.DamagePerUse!.Value, Exact);
        Assert.Equal(3 * 89.2 / 21, result.DamagePerRound, Exact);

        // The breakdown is one fight from fresh slots: 89.2, 89.2, 89.2 over 3 rounds, all three slots in the first fight.
        Assert.Equal(89.2, result.FightDamagePerRound!.Value, Exact);
        Assert.Equal(89.2, result.Round1Damage, Exact);
    }

    [Fact]
    public void Evaluate_FeatureNeverUsed_AddsNothingAndSaysSo()
    {
        // A limited trip against a target that is already prone is never attempted: u = 0, so d is undefined and it adds 0.
        const string build = """
            { "name": "Tripper", "level": 5, "abilities": {"str": 18},
              "attacks": [{ "name": "Longsword", "count": 2, "damage": "1d8", "damage_type": "slashing" }],
              "modifiers": [{ "kind": "condition_on_hit", "name": "Trip", "condition": "prone", "ability": "str", "dc": 15,
                              "resource": { "uses": 2, "per": "short_rest" } }] }
            """;

        var result = Day(build, """{ "ac": 15, "condition": "prone" }""", DayAssumptions.Dmg2014);

        var trip = Assert.Single(result.Day!.Features);
        Assert.Equal(0, trip.UsesPerRound);
        Assert.Null(trip.DamagePerUse);
        Assert.Equal(0, trip.Contribution);
        Assert.Equal(result.Day.Base.DamagePerRound, result.DamagePerRound);
        Assert.Contains("Trip: not used even with unlimited uses, so it adds nothing to the day.", result.Day.Notes);
    }

    [Fact]
    public void Evaluate_FeatureThatLowersDamage_ContributesItsLossAndSaysSo()
    {
        // Knocking the target prone gives a ranged attacker's next shot Disadvantage: spent on every hit (any_hit), each
        // use costs damage, and the day carries that loss rather than hiding it.
        const string build = """
            { "name": "Archer", "level": 5, "abilities": {"dex": 18},
              "attacks": [{ "name": "Longbow", "count": 2, "to_hit": {"ability": "dex"}, "damage": "1d8", "damage_type": "piercing", "properties": ["ranged"] }],
              "modifiers": [{ "kind": "condition_on_hit", "name": "Knockdown", "condition": "prone", "ability": "str", "dc": 15,
                              "resource": { "uses": 2, "per": "long_rest" } }] }
            """;

        var result = Day(build, """{ "ac": 15, "saves": {"str": 2} }""", DayAssumptions.Dmg2014);

        var knockdown = Assert.Single(result.Day!.Features);
        Assert.True(knockdown.DamagePerUse < 0);
        Assert.True(knockdown.Contribution < 0);
        Assert.True(result.DamagePerRound < result.Day.Base.DamagePerRound);
        Assert.Contains(result.Day.Notes, n => n.StartsWith("Knockdown: each use lowers the damage per round (d = -", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_NoLimitedFeatures_IsTheFightWithOneRun()
    {
        var (build, target) = Resolve(GoldenBuilds.Fighter2014(GoldenBuilds.Gwm2014), """{ "ac": 15 }""");

        var result = HorizonEvaluator.Evaluate(build, target, HorizonSettings.ForDay(DayAssumptions.Dmg2014));

        Assert.Empty(result.Day!.Features);
        Assert.Same(result.Evaluation, result.Day.Base);
        Assert.Equal(156893.0 / 8000, result.DamagePerRound, Exact);
        Assert.Equal(["No resource-limited features: the day horizon equals the fight horizon."], result.Day.Notes);
        Assert.Equal(1, HorizonEvaluator.FullRuns(build, HorizonSettings.ForDay(DayAssumptions.Dmg2014)));
    }

    [Fact]
    public void Evaluate_TwoLimitedFeatures_SaysTheyAreAddedAndShowsTheFullFight()
    {
        var c = OracleCases.Case("horizon-two-limited-features-day");
        var (build, target) = OracleCases.Resolve(c);
        var horizon = HorizonSettings.ForDay(DayAssumptions.Custom(6, 2));

        var result = HorizonEvaluator.Evaluate(build, target, horizon);

        Assert.Equal(2, result.Day!.Features.Count);
        Assert.StartsWith("Day horizon: each resource-limited feature is measured alone", result.Day.Notes[0], StringComparison.Ordinal);
        Assert.Contains("E × R = 6 × 3 = 18 rounds", result.Day.Notes[0], StringComparison.Ordinal);
        Assert.StartsWith("The limited features are amortized one at a time and added", result.Day.Notes[1], StringComparison.Ordinal);

        // The breakdown is the whole build over one fight from fresh resources, as the fight horizon evaluates it.
        var fight = DprEngine.Evaluate(build, target, DprOptions.Fight(), WorkMeter.Unlimited);
        Assert.Equal(fight.DamagePerRound, result.Evaluation.DamagePerRound, Exact);
        Assert.Equal(fight.DamagePerRound, result.FightDamagePerRound!.Value, Exact);
        Assert.NotNull(result.Evaluation.Round1Distribution);
        Assert.Equal(4, HorizonEvaluator.FullRuns(build, horizon));
        Assert.Equal(3, HorizonEvaluator.HeadlineRuns(build, horizon));
        Assert.Equal(["Action Surge", "Divine Smite"], HorizonEvaluator.LimitedFeatures(build).Select(f => f.Source.Label)); // build order
    }

    [Fact]
    public void LimitedFeatures_DefensiveResource_IsNotAmortized()
    {
        // A defensive modifier deals no damage; it stays in every run instead of being removed from the base.
        const string build = """
            { "name": "Guarded", "level": 5, "abilities": {"str": 18},
              "attacks": [{ "name": "Longsword", "damage": "1d8", "damage_type": "slashing" }],
              "modifiers": [{ "kind": "temp_hp", "name": "Second Wind", "amount": 10, "resource": { "uses": 1, "per": "short_rest" } }] }
            """;
        var (resolved, target) = Resolve(build, """{ "ac": 15 }""");

        Assert.Empty(HorizonEvaluator.LimitedFeatures(resolved));
        Assert.Equal(5.75, HorizonEvaluator.Evaluate(resolved, target, HorizonSettings.ForDay(DayAssumptions.Dmg2014)).DamagePerRound, Exact);
    }

    [Theory]
    [InlineData("round1", 1)]
    [InlineData("fight", 3)]
    public void Evaluate_Round1AndFight_AreTheEnginesOwnRun(string horizon, int rounds)
    {
        var (build, target) = Resolve(GoldenBuilds.Fighter2014(GoldenBuilds.Gwm2014 + ", " + GoldenBuilds.ActionSurge), """{ "ac": 15 }""");
        var settings = horizon == "round1" ? HorizonSettings.Round1 : HorizonSettings.Fight(rounds);

        var result = HorizonEvaluator.Evaluate(build, target, settings, includeDistribution: false);

        var direct = DprEngine.Evaluate(build, target, horizon == "round1" ? DprOptions.Round1 : DprOptions.Fight(rounds), WorkMeter.Unlimited);
        Assert.Equal(direct.DamagePerRound, result.DamagePerRound, Exact);
        Assert.Null(result.Day);
        Assert.Null(result.Evaluation.Round1Distribution);
        Assert.Equal(horizon == "round1" ? null : direct.DamagePerRound, result.FightDamagePerRound);
        Assert.Equal(direct.Round1Damage, result.Round1Damage, Exact);
    }

    [Fact]
    public void Evaluate_DayBudgetExceeded_NamesTheDaysRuns()
    {
        var (build, target) = Resolve(GoldenBuilds.Fighter2014(GoldenBuilds.Gwm2014 + ", " + GoldenBuilds.ActionSurge), """{ "ac": 15 }""");

        var error = Assert.Throws<DndInputException>(() =>
            HorizonEvaluator.Evaluate(build, target, HorizonSettings.ForDay(DayAssumptions.Dmg2014), true, CancellationToken.None, budget: 50));

        Assert.StartsWith(
            "this request is too large to compute exactly: the day horizon (3 fight runs) needs more work than one call may do (200,000,000 units).",
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains("use the fight horizon rather than day", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_FightBudgetExceeded_IsTheBuildMessage()
    {
        var (build, target) = Resolve(GoldenBuilds.Fighter2014(GoldenBuilds.Gwm2014), """{ "ac": 15 }""");

        var error = Assert.Throws<DndInputException>(() =>
            HorizonEvaluator.Evaluate(build, target, HorizonSettings.Fight(), true, CancellationToken.None, budget: 50));

        Assert.StartsWith("this build is too large to compute exactly:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_CancelledToken_Stops()
    {
        var (build, target) = Resolve(GoldenBuilds.Fighter2014(GoldenBuilds.Gwm2014 + ", " + GoldenBuilds.ActionSurge), """{ "ac": 15 }""");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            HorizonEvaluator.Evaluate(build, target, HorizonSettings.ForDay(DayAssumptions.Dmg2014), cancellationToken: cancellation.Token));
    }
}
