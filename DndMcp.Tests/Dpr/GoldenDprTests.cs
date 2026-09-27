using DndMcp.Domain.Dpr;
using DndMcp.Tests.Features;
using Xunit;
using static DndMcp.Tests.Dpr.DprTestKit;

namespace DndMcp.Tests.Dpr;

/// <summary>
/// Invariant: the Phase 4 contract's §8 worked examples (items 1–10, item 11's warlock curve and item 12's Action Surge
/// day) come out of the whole pipeline — DSL JSON as the model sends it, validation, resolution, the engine and the
/// horizons — to 1e-9 of the exact value (its fraction in the comment), or to the digits the contract prints where it
/// prints rounded values. These are the numbers the phase's exit criterion names, so each is pinned here directly and not
/// only through the oracle's file. Item 11's RPGBOT curve is <see cref="ReferenceCurvesTests"/>'s; the same deltas through
/// <c>balance_compare</c>'s path are <see cref="DprComparisonTests"/>'.
/// </summary>
public sealed class GoldenDprTests
{
    private const double Exact = 1e-9;

    /// <summary>
    /// A value the contract prints to two decimals: within half a unit of the last digit (plus rounding slack, since an
    /// exact 25.175 is printed as 25.18 and is 25.174999… as a double).
    /// </summary>
    private const double TwoDecimals = 0.005 + 1e-9;

    private static string Fighter2014(string modifiers = """[{ "kind": "power_attack", "name": "Great Weapon Master" }, { "kind": "extra_attack", "name": "GWM bonus attack", "attack": "Greatsword", "action": "bonus_action", "trigger": "crit" }]""", int strength = 18, string style = "\"gwf\"") =>
        $$"""
        { "name": "2014 L5 Fighter", "edition": "2014", "level": 5, "abilities": {"str": {{strength}}}, "fighting_style": {{style}},
          "attacks": [{ "name": "Greatsword", "count": 2, "damage": "2d6", "damage_type": "slashing", "properties": ["melee", "heavy", "two-handed"] }],
          "modifiers": {{modifiers}} }
        """;

    private static string Fighter2024(string modifiers = Gwm2024, int strength = 19, string style = "\"gwf\"", string mastery = "\"graze\"") =>
        $$"""
        { "name": "2024 L5 Fighter", "edition": "2024", "level": 5, "abilities": {"str": {{strength}}}, "fighting_style": {{style}},
          "attacks": [{ "name": "Greatsword", "count": 2, "damage": "2d6", "damage_type": "slashing", "properties": ["melee", "heavy", "two-handed"], "mastery": {{mastery}} }],
          "modifiers": {{modifiers}} }
        """;

    private const string Gwm2024 =
        """[{ "kind": "bonus_damage", "name": "Great Weapon Master", "amount": "pb", "attack_action_only": true }, { "kind": "extra_attack", "name": "Hew", "attack": "Greatsword", "action": "bonus_action", "trigger": "crit" }]""";

    private const string SavageAttacker = """{ "kind": "reroll_damage_take_best", "name": "Savage Attacker" }""";

    private static string Ac(int ac) => $$"""{ "ac": {{ac}} }""";

    [Fact]
    public void Round1_CritFloorRegression_IsOnePointTwo()
    {
        // §8.1: +5 vs AC 30, crit 19, 1d8+3: only a natural 19 or 20 hits, and both crit: 0.1 × (2 × 4.5 + 3) = 6/5.
        // dndMath.ts, without the crit floor, gives 0.825.
        const string build = """
            { "name": "Crit floor", "edition": "2014", "level": 5, "abilities": {"str": 16},
              "attacks": [{ "name": "Longsword", "to_hit": {"total": 5}, "damage": "1d8", "damage_type": "slashing" }],
              "modifiers": [{ "kind": "crit_range", "name": "Improved Critical", "min": 19 }] }
            """;

        Assert.Equal(1.2, Round1(build, Ac(30)), Exact);
    }

    [Fact]
    public void Round1_Fighter2014GwmAtAc15_IsTheGolden()
    {
        // §8.2: 18.7 + 0.0975 × 9.35 = 156893/8000. F's golden build JSON (count as a step map) must give the same.
        Assert.Equal(19.611625, Round1(Fighter2014(), Ac(15)), Exact);
        Assert.Equal(19.611625, Round1(FeatureTestBuilds.Fighter2014GwmJson, Ac(15)), Exact);
    }

    [Theory]
    [InlineData("[]", 18, "\"gwf\"", 253.0 / 15)] // GWF only: 2 × (0.60 × 37/3 + 0.05 × 62/3) = 253/15
    [InlineData("[]", 18, "null", 15.0)] // no GWF, no feat: 2 × (0.60 × 11 + 0.05 × 18)
    [InlineData("""[{ "kind": "power_attack", "policy": "always" }]""", 18, "\"gwf\"", 18.7)] // PA always, no crit BA: 187/10
    [InlineData("[]", 20, "\"gwf\"", 19.5)] // the ASI alternative, Str 20: 39/2
    public void Round1_Fighter2014Variants_AreTheGoldens(string modifiers, int strength, string style, double expected)
    {
        Assert.Equal(expected, Round1(Fighter2014(modifiers, strength, style), Ac(15)), Exact);
    }

    [Theory]
    [InlineData(13, 24.30, 19.33)]
    [InlineData(14, 21.95, 18.10)]
    [InlineData(15, 19.61, 16.87)]
    [InlineData(16, 17.27, 15.63)]
    [InlineData(17, 15.10, 14.40)]
    [InlineData(18, 13.81, 13.17)]
    [InlineData(19, 12.52, 11.93)]
    public void Round1_Fighter2014AcColumns_MatchThePrintedTable(int ac, double withFeat, double withoutFeat)
    {
        Assert.Equal(withFeat, Round1(Fighter2014(), Ac(ac)), TwoDecimals);
        Assert.Equal(withoutFeat, Round1(Fighter2014("[]"), Ac(ac)), TwoDecimals);
    }

    [Theory]
    [InlineData(13, true)]
    [InlineData(14, true)]
    [InlineData(15, true)]
    [InlineData(16, true)]
    [InlineData(17, false)]
    [InlineData(18, false)]
    [InlineData(19, false)]
    public void Evaluate_Fighter2014PowerAttackAuto_TogglesOffFromAc17(int ac, bool on)
    {
        var power = Assert.Single(Evaluate(Fighter2014(), Ac(ac)).PowerAttacks);

        Assert.Equal(on, Assert.Single(power.Round1Choices).On);
        Assert.Equal(on ? 1 : 0, power.OnChancePerRound, Exact);

        // The per-attack rule agrees here (research A4): on iff P'/P > D/(D + 10), D = 37/3 + 4 with GWF 2014.
        var toggle = Assert.Single(power.Toggles);
        Assert.Equal(on, toggle.RuleSaysOn);
        Assert.Equal(37.0 / 67, toggle.Threshold, Exact);
    }

    [Fact]
    public void Round1_Fighter2014GwmDeltas_AreTheGoldens()
    {
        var feat = Round1(Fighter2014(), Ac(15));

        Assert.Equal(65879.0 / 24000, feat - Round1(Fighter2014("[]"), Ac(15)), Exact); // +2.745 over no feat
        Assert.Equal(893.0 / 8000, feat - Round1(Fighter2014("[]", strength: 20), Ac(15)), Exact); // +0.1116 over the ASI
    }

    [Fact]
    public void Round1_Fighter2024GwmAndHew_IsTheGolden()
    {
        // §8.3: 23.10 + 0.0975 × 9.6 = 6009/250; with hew_gets_pb 193809/8000.
        Assert.Equal(24.036, Round1(Fighter2024(), Ac(15)), Exact);
        Assert.Equal(24.036, Round1(FeatureTestBuilds.Fighter2024GwmJson, Ac(15)), Exact);
        Assert.Equal(24.226125, Round1(Fighter2024(), Ac(15), """{ "hew_gets_pb": true }"""), Exact);
    }

    [Fact]
    public void Round1_Fighter2024Alternatives_AreTheGoldens()
    {
        var feat = Round1(Fighter2024(), Ac(15));
        var noFeat = Round1(Fighter2024("[]", strength: 18), Ac(15));
        var asi = Round1(Fighter2024("[]", strength: 20), Ac(15));

        Assert.Equal(19.2, noFeat, Exact); // 96/5
        Assert.Equal(22.0, asi, Exact);
        Assert.Equal(4.836, feat - noFeat, Exact); // 1209/250
        Assert.Equal(2.036, feat - asi, Exact); // 509/250
    }

    [Fact]
    public void Round1_Fighter2024Components_AreTheGoldens()
    {
        // The two Attack-action swings without Hew (as research A11 computes the components).
        const string gwm = """[{ "kind": "bonus_damage", "name": "Great Weapon Master", "amount": "pb", "attack_action_only": true }]""";
        const string gwf2014 = """[{ "kind": "bonus_damage", "amount": "pb", "attack_action_only": true }, { "kind": "damage_die_remap", "remap": "gwf2014", "attacks": ["Greatsword"] }]""";
        var withGwf = Round1(Fighter2024(gwm), Ac(15));
        var withoutGwf = Round1(Fighter2024(gwm, style: "null"), Ac(15));

        Assert.Equal(23.1, withGwf, Exact); // 231/10
        Assert.Equal(1.4, withGwf - withoutGwf, Exact); // GWF 2024: 7/5
        Assert.Equal(28.0 / 15, Round1(Fighter2024(gwf2014, style: "null"), Ac(15)) - withoutGwf, Exact); // GWF 2014: 1.8667
        Assert.Equal(2.8, withGwf - Round1(Fighter2024(gwm, mastery: "null"), Ac(15)), Exact); // Graze: 14/5
    }

    [Theory]
    [InlineData(13, 26.31, 2.71)]
    [InlineData(14, 25.18, 2.38)]
    [InlineData(15, 24.04, 2.04)]
    [InlineData(16, 22.90, 1.70)]
    [InlineData(17, 21.76, 1.36)]
    [InlineData(18, 20.62, 1.02)]
    [InlineData(19, 19.48, 0.68)]
    public void Round1_Fighter2024AcColumns_MatchThePrintedTable(int ac, double withFeat, double overAsi)
    {
        var feat = Round1(Fighter2024(), Ac(ac));

        Assert.Equal(withFeat, feat, TwoDecimals);
        Assert.Equal(overAsi, feat - Round1(Fighter2024("[]", strength: 20), Ac(ac)), TwoDecimals);
    }

    [Fact]
    public void Round1_SavageAttackerOnBuild3AtAc15_IsTheGoldenForEachRuling()
    {
        // §8.4 (a PLAN correction): +767/960 = 0.798958… by default (one set rerolled on a crit), +855619/1036800 =
        // 0.825250 with savage_attacker_on_crit_dice. Research's "+0.825" assumed the ruling on.
        var build = Fighter2024($"[{Gwm2024[1..^1]}, {SavageAttacker}]");
        var baseline = Round1(Fighter2024(), Ac(15));

        Assert.Equal(767.0 / 960, Round1(build, Ac(15)) - baseline, Exact);
        Assert.Equal(855619.0 / 1036800, Round1(build, Ac(15), """{ "savage_attacker_on_crit_dice": true }""") - baseline, Exact);
    }

    [Theory]
    [InlineData(13, 0.8536, 0.88)]
    [InlineData(14, 0.8285, 0.85)]
    [InlineData(15, 0.7990, 0.83)]
    [InlineData(16, 0.7648, 0.79)]
    [InlineData(17, 0.7261, 0.75)]
    [InlineData(18, 0.6829, 0.71)]
    [InlineData(19, 0.6351, 0.67)]
    public void Round1_SavageAttackerAcColumns_MatchThePrintedTable(int ac, double rulingOff, double rulingOn)
    {
        var build = Fighter2024($"[{Gwm2024[1..^1]}, {SavageAttacker}]");
        var baseline = Round1(Fighter2024(), Ac(ac));

        Assert.Equal(rulingOff, Round1(build, Ac(ac)) - baseline, 0.00005);
        Assert.Equal(rulingOn, Round1(build, Ac(ac), """{ "savage_attacker_on_crit_dice": true }""") - baseline, TwoDecimals);
    }

    [Theory]
    [InlineData(1, 8, null, 93.0 / 16, 5553.0 / 512)] // 4.5 → 5.8125; 9 → 10.8457
    [InlineData(1, 10, null, 143.0 / 20, 66583.0 / 5000)] // 5.5 → 7.15; 11 → 13.3166
    [InlineData(1, 12, null, 611.0 / 72, 81835.0 / 5184)] // 6.5 → 8.4861; 13 → 15.7861
    [InlineData(2, 6, null, 5425.0 / 648, 6690481.0 / 419904)] // 7 → 8.3719; 14 → 15.9334
    [InlineData(2, 6, "gwf2024", 2887.0 / 324, 1210723.0 / 69984)] // 8 → 8.9105; 16 → 17.3000
    public void ExpectedBestOfTwo_SavageAttackerTable_IsTheGolden(int count, int sides, string? remap, double hit, double critWithRuling)
    {
        Assert.Equal(hit, DamageDice.ExpectedBestOfTwo(count, sides, remap), Exact);
        Assert.Equal(critWithRuling, DamageDice.ExpectedBestOfTwo(2 * count, sides, remap), Exact);
    }

    [Theory]
    [InlineData(4, 2.5, 3.0, 13.0 / 4)]
    [InlineData(6, 3.5, 25.0 / 6, 4.0)]
    [InlineData(8, 4.5, 21.0 / 4, 39.0 / 8)]
    [InlineData(10, 5.5, 63.0 / 10, 29.0 / 5)]
    [InlineData(12, 6.5, 22.0 / 3, 27.0 / 4)]
    public void ExpectedValue_GreatWeaponFightingPerDie_IsTheGolden(int sides, double plain, double gwf2014, double gwf2024)
    {
        // §8.5 (research A2).
        Assert.Equal(plain, DamageDice.ExpectedValue(sides), Exact);
        Assert.Equal(gwf2014, DamageDice.ExpectedValue(sides, "gwf2014"), Exact);
        Assert.Equal(gwf2024, DamageDice.ExpectedValue(sides, "gwf2024"), Exact);
    }

    [Fact]
    public void Fight_VexOnOneAttack_ConvergesToTheSteadyState()
    {
        // §8.6: x* = P_N / (1 − P_A + P_N) = 260/309 at P_N = 0.65, P_A = 0.8775. A flat 1-damage Vex weapon makes each
        // round's expected damage its hit chance; the chain x_{k+1} = x_k·P_A + (1 − x_k)·P_N closes on x* by a factor
        // (P_A − P_N) = 0.2275 a round, so round 10 is within 1e-6.
        const string build = """
            { "name": "Vex", "level": 5, "abilities": {"str": 18},
              "attacks": [{ "name": "Shortsword", "damage": "1", "ability_to_damage": false, "damage_type": "piercing", "mastery": "vex" }] }
            """;

        var result = Evaluate(build, Ac(15), DprOptions.Fight(10));

        Assert.Equal(0.65, result.DamageByRound[0], Exact);
        Assert.Equal(0.65 * 0.8775 + 0.35 * 0.65, result.DamageByRound[1], Exact);
        Assert.Equal(260.0 / 309, result.DamageByRound[9], 1e-6);
    }

    private static string Smite(string policy, string when = "first_hit_per_turn", string extra = "") =>
        $$"""
        { "name": "Paladin", "edition": "2014", "level": 5, "abilities": {"str": 18},
          "attacks": [{ "name": "Longsword", "count": 2, "damage": "1d8", "damage_type": "slashing", "properties": ["melee", "versatile"] }],
          "modifiers": [{ "kind": "extra_damage", "name": "Divine Smite", "dice": "2d8", "type": "radiant", "when": "{{when}}", "policy": "{{policy}}" {{extra}} }] }
        """;

    [Theory]
    [InlineData("any_hit", "first_hit_per_turn", "", 8.505, 0.8775, 126.0 / 13)] // 1701/200; 351/400
    [InlineData("crit_or_last", "first_hit_per_turn", "", 6.885, 0.6675, 2754.0 / 267)] // 1377/200; 267/400; 10.315 per use
    [InlineData("crits_only", "first_hit_per_turn", "", 1.755, 0.0975, 18.0)]
    [InlineData("any_hit", "every_hit", """, "resource": { "uses": 4, "per": "long_rest" }""", 12.6, 1.3, 126.0 / 13)] // 2014 smite on every hit
    public void Round1_SmitePolicies_AreTheGoldens(string policy, string when, string extra, double damage, double uses, double perUse)
    {
        // §8.7: +7 vs AC 15, two attacks, a 2d8 radiant smite that doubles on a crit.
        var rider = Rider(Evaluate(Smite(policy, when, extra), Ac(15)), "Divine Smite");

        Assert.Equal(damage, rider.DamagePerRound, Exact);
        Assert.Equal(uses, rider.UsesPerRound, Exact);
        Assert.Equal(perUse, rider.DamagePerUse!.Value, Exact);
    }

    [Theory]
    [InlineData("0", 8.505)]
    [InlineData("7.71", 8.505)] // below 2.7/0.35 = 7.714…: spend on the first hit
    [InlineData("7.72", 6.885)] // between 7.714 and 9: crit or last
    [InlineData("8.99", 6.885)]
    [InlineData("9", 1.755)] // exact tie at the last attack (E[2d8] − 9 = 0): ties do not spend
    [InlineData("9.01", 1.755)]
    [InlineData("17.99", 1.755)] // crits only up to 18
    [InlineData("18.01", 0.0)] // never
    public void Round1_SmiteOptimal_SwitchesPolicyAtTheExactBoundaries(string useValue, double damage)
    {
        var rider = Rider(Evaluate(Smite("optimal", extra: $""", "use_value": {useValue}"""), Ac(15)), "Divine Smite");

        Assert.Equal(damage, rider.DamagePerRound, Exact);
    }

    private const string Fireball2014 = """
        { "name": "2014 Wizard 5", "edition": "2014", "level": 5, "abilities": {"int": 16},
          "modifiers": [{ "kind": "save_effect", "name": "Fireball", "ability": "dex", "dc": 15, "dice": "8d6", "type": "fire", "shape": "sphere", "size": 20 }] }
        """;

    [Fact]
    public void Round1_FireballOnFourGoblins_IsTheGoldenWithTheSharedRoll()
    {
        // §8.8: DC 15 vs Dex +2 → F = 3/5; 8d6 shared by the four targets; HP 7.
        var result = Evaluate(Fireball2014, """{ "saves": {"dex": 2}, "hp": 7 }""");
        var fireball = Assert.Single(result.SaveEffects);

        Assert.Equal(4, fireball.Targets);
        Assert.Equal(0.6, fireball.FailChance, 1e-12);
        Assert.Equal(22.3, fireball.DamagePerTarget, Exact); // 0.6 × 28 + 0.4 × 13.75 (E[floor(8d6/2)] = 13.75)
        Assert.Equal(89.2, fireball.RawDamage, Exact);
        Assert.Equal(89.2, result.DamagePerRound, Exact);
        Assert.Equal(9797273.0 / 349920, fireball.EffectiveDamage!.Value, Exact); // 27.998608…
        Assert.Equal(466417.0 / 466560, fireball.KillChanceEach!.Value, 1e-12); // 0.9996935
        Assert.Equal(3642569.0 / 3645000, fireball.AllDieChance!.Value, 1e-12); // 0.999333059
        Assert.Equal(466417.0 / 116640, fireball.ExpectedKills!.Value, Exact); // 3.998774

        // Rolling the damage separately per target (the wrong model) would give 0.998775: pin that it is not used.
        Assert.True(Math.Abs(fireball.AllDieChance.Value - 0.9987745690202604) > 5e-4);
    }

    [Fact]
    public void Round1_FireballOnFour2024GoblinWarriors_IsTheGolden()
    {
        var fireball = Assert.Single(Evaluate(Fireball2014, """{ "saves": {"dex": 2}, "hp": 10 }""").SaveEffects);

        Assert.Equal(0.9844489, fireball.KillChanceEach!.Value, 5e-8);
        Assert.Equal(0.9661672, fireball.AllDieChance!.Value, 5e-8);
        Assert.Equal(39.908441, fireball.EffectiveDamage!.Value, 5e-7);
    }

    [Fact]
    public void Round1_HoldPersonAgainstLegendaryResistance_NeedsTheGoldenCasts()
    {
        // §8.9: F = 0.6 (DC 15 vs Wis +2), L = 3 → (3 + 1) / 0.6 = 20/3 expected casts.
        const string build = """
            { "name": "Cleric", "level": 5, "abilities": {"wis": 18},
              "modifiers": [{ "kind": "save_effect", "name": "Hold Person", "ability": "wis", "dc": 15, "condition": "paralyzed" }] }
            """;

        var hold = Assert.Single(Evaluate(build, """{ "saves": {"wis": 2}, "legendary_resistance": 3 }""").SaveEffects);

        Assert.Equal(20.0 / 3, hold.ExpectedCastsToLand!.Value, Exact);
    }

    [Theory]
    [InlineData(18, 137.0 / 260, 11.0 / 117)] // base P 0.50: +52.7% / +9.4%
    [InlineData(15, 189.0 / 500, 11.0 / 150)] // base P 0.65: +37.8% / +7.3%
    [InlineData(12, 279.0 / 1220, 11.0 / 183)] // base P 0.80: +22.9% / +6.0%
    public void Round1_AdvantageCalibration_IsTheGolden(int ac, double advantageGain, double plusOneGain)
    {
        // §8.10: greatsword 2d6+4 at +7.
        const string greatsword = """{ "name": "Greatsword", "damage": "2d6", "damage_type": "slashing", "properties": ["melee", "heavy", "two-handed"] }""";
        var plain = Round1(Level5($"[{greatsword}]"), Ac(ac));

        Assert.Equal(advantageGain, (Round1(Level5($"[{greatsword}]", """[{ "kind": "advantage" }]"""), Ac(ac)) / plain) - 1, Exact);
        Assert.Equal(plusOneGain, (Round1(Level5($"[{greatsword}]", """[{ "kind": "to_hit", "amount": 1 }]"""), Ac(ac)) / plain) - 1, Exact);
    }

    [Theory]
    [InlineData("2014", 6, 2, 3, 62472773.0 / 19200000)] // 3 uses × 19.5227 ÷ (6 × 3) = +3.254 (research's +3.1 used 2 × 9.35)
    [InlineData("2014", 8, 2, 3, 62472773.0 / 25600000)] // ÷ (8 × 3) = +2.440 (research +2.3)
    [InlineData("2024", 4, 1, 2, 399079.0 / 100000)] // build 3, 2 uses × 23.94474 ÷ (4 × 3) = +3.991 (research 3.85)
    public void Day_ActionSurge_IsTheCorrectedGolden(string edition, int encounters, int shortRests, int usesPerDay, double gain)
    {
        // §8.12 (a PLAN correction): Action Surge's value per use is the exact marginal of the turn with four swings
        // against two, the larger chance of the crit bonus attack (or Hew) included: 62472773/3200000 = 19.5227 on build 2.
        var build = edition == "2014"
            ? GoldenBuilds.Fighter2014(GoldenBuilds.Gwm2014 + ", " + GoldenBuilds.ActionSurge)
            : GoldenBuilds.Fighter2024(GoldenBuilds.Gwm2024 + ", " + GoldenBuilds.ActionSurge);
        var (resolved, target) = Resolve(build, Ac(15));

        var day = HorizonEvaluator.Evaluate(resolved, target, HorizonSettings.ForDay(DayAssumptions.Custom(encounters, shortRests)));

        var surge = Assert.Single(day.Day!.Features);
        Assert.Equal(usesPerDay, surge.UsesPerDay);
        Assert.Equal(gain, surge.Contribution, Exact);
        Assert.Equal(edition == "2014" ? 156893.0 / 8000 : 6009.0 / 250, day.Day.Base.DamagePerRound, Exact); // the §8.2 / §8.3 goldens
        Assert.Equal(day.Day.Base.DamagePerRound + gain, day.DamagePerRound, Exact);
    }

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
    [InlineData(17, 38.20)]
    [InlineData(20, 38.20)]
    public void Evaluate_WarlockBaselineAgainstTheCrLRow_IsThePublishedCurve(int level, double expected)
    {
        // §8.11: the preset vs the DMG row for CR = level; Hex has no setup cost in the preset, so round 1 = the fight.
        var build = $$"""{ "name": "Warlock", "preset": "warlock_baseline", "level": {{level}} }""";

        Assert.Equal(expected, Evaluate(build, options: DprOptions.Round1).DamagePerRound, Exact);
        Assert.Equal(expected, Evaluate(build, options: DprOptions.Fight()).DamagePerRound, Exact);
    }
}
