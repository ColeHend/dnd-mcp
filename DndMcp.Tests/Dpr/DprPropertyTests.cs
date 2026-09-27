using System.Text.Json;
using System.Text.Json.Nodes;
using DndMcp.Domain.Dice;
using DndMcp.Domain.Dpr;
using DndMcp.Domain.Features;
using Xunit;

namespace DndMcp.Tests.Dpr;

/// <summary>
/// Invariants over every build in the oracle file (254 builds covering every modifier kind), not just the goldens:
/// the round-1 distribution is a distribution whose mean is the expected damage; the breakdown adds up to the headline;
/// a one-round fight is round 1; a higher AC never raises a fixed build's damage; and an optional rider left to the
/// optimal policy (use_value 0) never lowers it, because "never spend it" is always one of the choices.
/// </summary>
public sealed class DprPropertyTests
{
    public static TheoryData<string> Builds()
    {
        var data = new TheoryData<string>();
        foreach (var c in OracleCases.All.Where(c => OracleCases.HorizonKind(c) != DprHorizons.Day))
        {
            data.Add(OracleCases.Id(c));
        }

        return data;
    }

    private static DprResult Evaluate(ResolvedBuild build, ResolvedTarget target, DprOptions options) =>
        DprEngine.Evaluate(build, target, options, new WorkMeter(DprLimits.WorkBudget));

    [Theory]
    [MemberData(nameof(Builds))]
    public void Evaluate_AnyBuild_DistributionSumsToOneWithTheExpectedMean(string id)
    {
        var (build, target) = OracleCases.Resolve(OracleCases.Case(id));

        var result = Evaluate(build, target, DprOptions.Round1);

        var distribution = result.Round1Distribution!;
        Assert.Equal(1, distribution.Values.Sum(v => v.Probability), 1e-12);
        Assert.Equal(result.DamagePerRound, distribution.Mean, 1e-9);
        Assert.All(distribution.Values, v => Assert.True(v.Probability >= 0 && v.Damage >= 0));
        Assert.Equal(distribution.Values.OrderBy(v => v.Damage).Select(v => v.Damage), distribution.Values.Select(v => v.Damage));
    }

    [Theory]
    [MemberData(nameof(Builds))]
    public void Evaluate_AnyBuild_BreakdownAddsUpToTheHeadline(string id)
    {
        var c = OracleCases.Case(id);

        var result = OracleCases.Evaluate(c);

        var parts = result.Attacks.Sum(a => a.DamagePerRound) + result.SaveEffects.Sum(s => s.DamagePerRound);
        Assert.Equal(result.DamagePerRound, parts, 1e-9);
        Assert.All(result.Attacks, a => Assert.True(a.HitChance >= a.CritChance && a.HitChance <= 1 + 1e-12, $"{a.Attack}: hit {a.HitChance}, crit {a.CritChance}"));
        Assert.Equal(result.DamagePerRound, result.DamageByRound.Average(), 1e-9);
    }

    [Theory]
    [MemberData(nameof(Builds))]
    public void Evaluate_OneRoundFight_EqualsRound1(string id)
    {
        var (build, target) = OracleCases.Resolve(OracleCases.Case(id));

        var round1 = Evaluate(build, target, DprOptions.Round1 with { IncludeDistribution = false });
        var fight = Evaluate(build, target, DprOptions.Fight(1) with { IncludeDistribution = false });

        Assert.Equal(round1.DamagePerRound, fight.DamagePerRound, 1e-12);
    }

    private const string KitchenSink = """
        { "name": "Kitchen sink", "edition": "2024", "level": 11, "abilities": {"str": 20, "dex": 16}, "fighting_style": "gwf",
          "attacks": [
            { "name": "Greataxe", "count": 2, "damage": "1d12", "damage_type": "slashing", "properties": ["melee", "heavy", "two-handed"], "mastery": "cleave" },
            { "name": "Maul", "damage": "2d6", "damage_type": "bludgeoning", "properties": ["melee", "heavy", "two-handed"], "mastery": "topple" },
            { "name": "Shortsword", "action": "bonus_action", "offhand": true, "to_hit": {"ability": "dex"}, "damage": "1d6", "damage_type": "piercing",
              "properties": ["melee", "light", "finesse"], "mastery": "vex" } ],
          "modifiers": [
            { "kind": "extra_damage", "name": "Sneak", "dice": "2d6", "when": "first_hit_per_turn", "policy": "optimal", "use_value": 0 },
            { "kind": "extra_damage", "name": "Smite", "dice": "2d8", "type": "radiant", "policy": "crit_or_last", "resource": { "uses": 2, "per": "long_rest" } },
            { "kind": "extra_damage", "name": "Brutal", "dice": "1d12", "when": "on_crit" },
            { "kind": "extra_damage", "name": "Grit", "amount": 1, "when": "on_miss" },
            { "kind": "reroll_damage_take_best", "name": "Savage Attacker", "policy": "crits_only" },
            { "kind": "condition_on_hit", "name": "Trip", "condition": "prone", "ability": "str", "dc": 15, "when": "first_hit_per_turn" },
            { "kind": "advantage", "name": "Help", "rate": 0.5 },
            { "kind": "advantage", "name": "Darkness", "mode": "disadvantage", "rate": 0.25 },
            { "kind": "power_attack", "name": "Power attack" },
            { "kind": "extra_attack", "name": "Hew", "attack": "Greataxe", "action": "bonus_action", "trigger": "crit" },
            { "kind": "extra_attack", "name": "Action Surge", "attack": "Greataxe", "count": 2, "action": "action", "resource": { "uses": 1, "per": "short_rest" } },
            { "kind": "extra_attack", "name": "Opportunity Attack", "attack": "Maul", "action": "reaction", "trigger_probability": 0.3 },
            { "kind": "to_hit", "name": "Bless", "dice": "1d4", "concentration": true },
            { "kind": "lucky" },
            { "kind": "crit_range", "min": 19 },
            { "kind": "save_effect", "name": "Aura", "ability": "con", "dc": 14, "dice": "1d6", "type": "fire", "targets": 2, "action_cost": "none" } ] }
        """;

    [Fact]
    public void Evaluate_NearlyEveryModifierAtOnce_StaysConsistentAndSmall()
    {
        var (build, target) = DprTestKit.Resolve(
            KitchenSink, """{ "ac": 16, "saves": {"str": 3, "con": 2}, "hp": 60, "resistances": ["fire"], "second_target_rate": 0.5 }""");

        var round1 = Evaluate(build, target, DprOptions.Round1);
        var fight = Evaluate(build, target, DprOptions.Fight(4));
        var oneRound = Evaluate(build, target, DprOptions.Fight(1));

        Assert.Equal(1, round1.Round1Distribution!.Values.Sum(v => v.Probability), 1e-12);
        Assert.Equal(round1.DamagePerRound, round1.Round1Distribution.Mean, 1e-9);
        Assert.Equal(round1.DamagePerRound, oneRound.DamagePerRound, 1e-12);
        Assert.Equal(round1.DamagePerRound, fight.DamageByRound[0], 1e-12);
        Assert.Equal(fight.DamagePerRound, fight.Attacks.Sum(a => a.DamagePerRound) + fight.SaveEffects.Sum(s => s.DamagePerRound), 1e-9);
        Assert.True(fight.DamageByRound[0] > fight.DamageByRound[3], "Action Surge and the smites are spent early");
        Assert.True(round1.TurnStates < 50_000, $"{round1.TurnStates} states");
        Assert.Equal(2, round1.Resources.Count);
    }

    /// <summary>
    /// Builds whose damage need not fall with AC are left out, deliberately: an on_miss rider can deal more than the hit
    /// it replaces, and with a use_value above 0 the optimal policy maximises damage minus use_value, so its damage alone
    /// may rise when a higher AC makes spending more attractive.
    /// </summary>
    public static TheoryData<string> AcMonotoneBuilds()
    {
        var data = new TheoryData<string>();
        foreach (var c in OracleCases.All.Where(c => OracleCases.HorizonKind(c) != DprHorizons.Day))
        {
            var modifiers = c.GetProperty("build").TryGetProperty("modifiers", out var m) ? m.EnumerateArray().ToList() : [];
            var excluded = modifiers.Any(x =>
                (x.TryGetProperty("when", out var when) && when.GetString() == "on_miss") ||
                (x.TryGetProperty("use_value", out var value) && value.GetDouble() > 0));
            if (!excluded)
            {
                data.Add(OracleCases.Id(c));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AcMonotoneBuilds))]
    public void Evaluate_HigherAc_NeverRaisesDamage(string id)
    {
        var c = OracleCases.Case(id);
        var (build, target) = OracleCases.Resolve(c);
        var options = OracleCases.HorizonKind(c) == DprHorizons.Round1 ? DprOptions.Round1 : DprOptions.Fight(c.GetProperty("horizon").GetProperty("rounds").GetInt32());
        options = options with { IncludeDistribution = false };

        var previous = double.PositiveInfinity;
        for (var ac = 8; ac <= 26; ac++)
        {
            var dpr = Evaluate(build, target with { ArmorClass = ac }, options).DamagePerRound;
            Assert.True(dpr <= previous + 1e-9, $"{id}: AC {ac} gives {dpr}, more than AC {ac - 1}'s {previous}");
            previous = dpr;
        }
    }

    [Theory]
    [MemberData(nameof(Builds))]
    public void Evaluate_OptionalRiderUnderOptimal_NeverLowersDamage(string id)
    {
        // Every optional choice of the build is switched to optimal with use_value 0; then removing any one of them
        // (the day horizon's own mechanism) must not raise round 1's damage.
        var c = OracleCases.Case(id);
        var buildNode = JsonNode.Parse(c.GetProperty("build").GetRawText())!.AsObject();
        if (buildNode["modifiers"] is not JsonArray modifiers)
        {
            return;
        }

        var optional = new List<int>();
        for (var i = 0; i < modifiers.Count; i++)
        {
            var modifier = modifiers[i]!.AsObject();
            var kind = modifier["kind"]!.GetValue<string>();
            var when = modifier["when"]?.GetValue<string>();
            var isOptional = kind switch
            {
                "reroll_damage_take_best" or "condition_on_hit" => true,
                "extra_damage" => when == "first_hit_per_turn" || ((when is null or "every_hit") && (modifier["resource"] is not null || modifier["action_cost"] is not null)),
                _ => false,
            };
            if (isOptional)
            {
                modifier["policy"] = "optimal";
                modifier["use_value"] = 0;
                optional.Add(i + 1);
            }
        }

        if (optional.Count == 0)
        {
            return;
        }

        var spec = DslJson.Deserialize<BuildSpec>(buildNode.ToJsonString(), "build");
        var rulings = OracleCases.TryGet(c, "rulings", out var r) ? DslJson.Deserialize<RulingsSpec>(r.GetRawText(), "rulings") : null;
        var targetSpec = OracleCases.TryGet(c, "target", out var t) ? DslJson.Deserialize<TargetSpec>(t.GetRawText(), "target") : null;
        var build = BuildResolver.Resolve(spec, spec.Level!.Value, rulings);
        var target = TargetResolver.Resolve(targetSpec, build.Level);
        var options = DprOptions.Round1 with { IncludeDistribution = false };

        var with = Evaluate(build, target, options).DamagePerRound;
        foreach (var number in optional)
        {
            var without = Evaluate(build, target, options with { RemovedModifiers = [number] }).DamagePerRound;
            Assert.True(with >= without - 1e-9, $"{id}: modifier {number} under optimal lowers round 1 from {without} to {with}");
        }
    }
}
