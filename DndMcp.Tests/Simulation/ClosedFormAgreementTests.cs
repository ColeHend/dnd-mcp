using System.Globalization;
using DndMcp.Domain.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace DndMcp.Tests.Simulation;

/// <summary>
/// Exit criterion (contract §5.8 test 1): the simulator agrees with the closed form. Each build runs through
/// <see cref="DummyDpr"/> — the SAME turn code that runs real fights, against infinite-HP dummies with the target's stats —
/// and through <c>DprEngine</c>'s exact fight horizon (R = 3); both the per-round mean and round 1 must agree.
///
/// <para>
/// <b>Tolerance</b>: |sim − exact| ≤ 4 standard errors, with 200,000 fights of 3 rounds each (600,000 turns; 200,000
/// round-1 samples). The SE comes from the per-fight totals (fights are independent). At 4 SE a true agreement fails with
/// probability 6×10⁻⁵ per comparison, so the ~80 comparisons here are decisive at fixed seeds (each case's seed is fixed:
/// a pass is reproducible), while a real rules difference — even 1% of the damage — is 10+ SE and fails.
/// </para>
/// <para>
/// An untyped rider deals the attack's damage type in both (the settled reading), so it is compared against a target that
/// resists the weapon's type too. Where the rules
/// legitimately differ — a 2014 Stunning Strike lasts into the build's next turn in the simulator, while the closed form
/// resets imposed conditions every turn — only round 1 is compared and the fight's direction is checked.
/// </para>
/// </summary>
public sealed class ClosedFormAgreementTests(ITestOutputHelper output)
{
    private const int Iterations = 200_000;
    private const int Rounds = 3;
    private const double Tolerance = 4.0;

    private const string Greatsword = """{ "name": "Greatsword", "count": 2, "damage": "2d6", "damage_type": "slashing", "properties": ["melee", "heavy", "two-handed"] }""";

    private static string Fighter2014(string modifiers = "", int strength = 18, string style = "\"gwf\"") => $$"""
        { "name": "2014 L5 Fighter", "edition": "2014", "level": 5, "abilities": {"str": {{strength}}}, "fighting_style": {{style}},
          "attacks": [{{Greatsword}}], "modifiers": [{{modifiers}}] }
        """;

    private static string Fighter2024(string modifiers = "", int strength = 19, string mastery = "graze") => $$"""
        { "name": "2024 L5 Fighter", "edition": "2024", "level": 5, "abilities": {"str": {{strength}}}, "fighting_style": "gwf",
          "attacks": [{ "name": "Greatsword", "count": 2, "damage": "2d6", "damage_type": "slashing", "properties": ["melee", "heavy", "two-handed"], "mastery": "{{mastery}}" }],
          "modifiers": [{{modifiers}}] }
        """;

    private static string Longswords(string modifiers, string edition = "2014") => $$"""
        { "name": "Longswords", "edition": "{{edition}}", "level": 5, "abilities": {"str": 18},
          "attacks": [{ "name": "Longsword", "count": 2, "damage": "1d8", "damage_type": "slashing", "properties": ["melee", "versatile"] }],
          "modifiers": [{{modifiers}}] }
        """;

    private const string Gwm2014 =
        """{ "kind": "power_attack", "name": "Great Weapon Master" }, { "kind": "extra_attack", "name": "GWM bonus attack", "attack": "Greatsword", "action": "bonus_action", "trigger": "crit" }""";

    private const string Gwm2024 =
        """{ "kind": "bonus_damage", "name": "Great Weapon Master", "amount": "pb", "attack_action_only": true }, { "kind": "extra_attack", "name": "Hew", "attack": "Greatsword", "action": "bonus_action", "trigger": "crit" }""";

    private const string Smite = """{ "kind": "extra_damage", "name": "Divine Smite", "dice": "2d8", "type": "radiant", "when": "first_hit_per_turn", "policy": "POLICY" }""";

    private const string Fireball = """
        { "name": "Fireball caster", "edition": "2014", "level": 5, "abilities": {"int": 18},
          "modifiers": [{ "kind": "save_effect", "name": "Fireball", "ability": "dex", "dc": 15, "dice": "8d6", "type": "fire", "shape": "sphere", "size": 20 }] }
        """;

    private static string Monk(string edition) => $$"""
        { "name": "Monk", "edition": "{{edition}}", "level": 5, "abilities": {"dex": 18, "wis": 16},
          "attacks": [{ "name": "Unarmed Strike", "count": 2, "to_hit": {"ability": "dex"}, "damage": "1d8", "damage_type": "bludgeoning", "properties": ["melee"] }],
          "modifiers": [{ "kind": "condition_on_hit", "name": "Stunning Strike", "condition": "stunned", "ability": "con", "dc_ability": "wis", "when": "first_hit_per_turn", "resource": {"uses": 5, "per": "short_rest"} }] }
        """;

    /// <summary>How a case is compared.</summary>
    public enum Check
    {
        /// <summary>Both the fight's per-round mean and round 1 within tolerance.</summary>
        Both,

        /// <summary>Round 1 within tolerance; over the fight the simulator may only be HIGHER (a condition lasting longer helps).</summary>
        Round1AndFightAtLeast,
    }

    public sealed record Case(string Name, string Build, string? Target, string? Rulings = null, Check Check = Check.Both)
    {
        public override string ToString() => Name;
    }

    public static readonly IReadOnlyList<Case> Cases =
    [
        new("2014 fighter GWF + GWM auto, AC 13", Fighter2014(Gwm2014), """{ "ac": 13 }"""),
        new("2014 fighter GWF + GWM auto, AC 19", Fighter2014(Gwm2014), """{ "ac": 19 }"""),
        new("2014 fighter GWF + GWM always, AC 15", Fighter2014("""{ "kind": "power_attack", "name": "GWM", "policy": "always" }, { "kind": "extra_attack", "name": "GWM bonus attack", "attack": "Greatsword", "action": "bonus_action", "trigger": "crit" }"""), """{ "ac": 15 }"""),
        new("2014 fighter GWF + GWM never, AC 15", Fighter2014("""{ "kind": "power_attack", "name": "GWM", "policy": "never" }, { "kind": "extra_attack", "name": "GWM bonus attack", "attack": "Greatsword", "action": "bonus_action", "trigger": "crit" }"""), """{ "ac": 15 }"""),
        new("2024 fighter GWF + Graze + GWM + Hew, AC 15", Fighter2024(Gwm2024), """{ "ac": 15 }"""),
        new("2024 fighter GWM, hew_gets_pb, AC 15", Fighter2024(Gwm2024), """{ "ac": 15 }""", """{ "hew_gets_pb": true }"""),
        new("2024 fighter + Savage Attacker (ruling off)", Fighter2024(Gwm2024 + """, { "kind": "reroll_damage_take_best", "name": "Savage Attacker" }"""), """{ "ac": 15 }"""),
        new("2024 fighter + Savage Attacker (crit dice ruling)", Fighter2024(Gwm2024 + """, { "kind": "reroll_damage_take_best", "name": "Savage Attacker" }"""), """{ "ac": 15 }""", """{ "savage_attacker_on_crit_dice": true }"""),
        new("Smite any_hit", Longswords(Smite.Replace("POLICY", "any_hit")), """{ "ac": 15 }"""),
        new("Smite crit_or_last", Longswords(Smite.Replace("POLICY", "crit_or_last")), """{ "ac": 15 }"""),
        new("Smite crits_only", Longswords(Smite.Replace("POLICY", "crits_only")), """{ "ac": 15 }"""),
        new("Smite optimal, use_value 8", Longswords("""{ "kind": "extra_damage", "name": "Divine Smite", "dice": "2d8", "type": "radiant", "when": "first_hit_per_turn", "policy": "optimal", "use_value": 8 }"""), """{ "ac": 15 }"""),
        new("2014 Divine Smite every hit, 3 uses", Longswords("""{ "kind": "extra_damage", "name": "Divine Smite", "dice": "2d8", "type": "radiant", "resource": {"uses": 3, "per": "long_rest"} }"""), """{ "ac": 15 }"""),
        new("2024 Divine Smite (Bonus Action) + offhand", """
            { "name": "Paladin", "edition": "2024", "level": 5, "abilities": {"dex": 18},
              "attacks": [{ "name": "Shortsword", "count": 2, "to_hit": {"ability": "dex"}, "damage": "1d6", "damage_type": "piercing", "properties": ["melee", "finesse", "light"] },
                          { "name": "Offhand", "action": "bonus_action", "offhand": true, "to_hit": {"ability": "dex"}, "damage": "1d6", "damage_type": "piercing", "properties": ["melee", "finesse", "light"] }],
              "modifiers": [{ "kind": "extra_damage", "name": "Divine Smite", "dice": "2d8", "type": "radiant", "action_cost": "bonus_action", "resource": {"uses": 2, "per": "long_rest"} }] }
            """, """{ "ac": 15 }"""),
        new("Vex (shortswords)", """
            { "name": "Vex", "edition": "2024", "level": 5, "abilities": {"dex": 18},
              "attacks": [{ "name": "Shortsword", "count": 2, "to_hit": {"ability": "dex"}, "damage": "1d6", "damage_type": "piercing", "properties": ["melee", "finesse", "light"], "mastery": "vex" }] }
            """, """{ "ac": 16 }"""),
        new("Topple (maul)", """
            { "name": "Topple", "edition": "2024", "level": 5, "abilities": {"str": 18},
              "attacks": [{ "name": "Maul", "count": 2, "damage": "2d6", "damage_type": "bludgeoning", "properties": ["melee", "heavy", "two-handed"], "mastery": "topple" }] }
            """, """{ "ac": 15, "saves": {"con": 1} }"""),
        new("Cleave, second_target_rate 0.5", """
            { "name": "Cleave", "edition": "2024", "level": 5, "abilities": {"str": 18},
              "attacks": [{ "name": "Greataxe", "count": 2, "damage": "1d12", "damage_type": "slashing", "properties": ["melee", "heavy", "two-handed"], "mastery": "cleave" }] }
            """, """{ "ac": 15, "second_target_rate": 0.5 }"""),
        new("Bless (+1d4)", Fighter2014("""{ "kind": "to_hit", "name": "Bless", "dice": "1d4" }"""), """{ "ac": 17 }"""),
        new("Lucky", Fighter2014("""{ "kind": "lucky" }"""), """{ "ac": 17 }"""),
        new("Elven Accuracy, advantage rate 0.5", """
            { "name": "Rapier", "edition": "2014", "level": 5, "abilities": {"dex": 18},
              "attacks": [{ "name": "Rapier", "count": 2, "to_hit": {"ability": "dex"}, "damage": "1d8", "damage_type": "piercing", "properties": ["melee", "finesse"] }],
              "modifiers": [{ "kind": "elven_accuracy" }, { "kind": "advantage", "name": "Help", "rate": 0.5 }] }
            """, """{ "ac": 16 }"""),
        new("Crit range 19", Fighter2014("""{ "kind": "crit_range", "name": "Improved Critical", "min": 19 }"""), """{ "ac": 15 }"""),
        new("Resistant target", Fighter2014(), """{ "ac": 15, "resistances": ["slashing"] }"""),
        new("Vulnerable target", Fighter2014(), """{ "ac": 15, "vulnerabilities": ["slashing"] }"""),
        new("Immune target, typed radiant rider", Longswords("""{ "kind": "extra_damage", "name": "Radiant rider", "dice": "1d8", "type": "radiant" }"""), """{ "ac": 15, "immunities": ["slashing"] }"""),
        new("Typed rider vs resistance to it", Longswords("""{ "kind": "extra_damage", "name": "Hunter's Mark", "dice": "1d6", "type": "force" }"""), """{ "ac": 15, "resistances": ["force"] }"""),
        new("Untyped rider, no resistances", Longswords("""{ "kind": "extra_damage", "name": "Untyped rider", "dice": "1d6" }"""), """{ "ac": 15 }"""),
        new("Untyped rider vs a target resisting the weapon's type", Longswords("""{ "kind": "extra_damage", "name": "Sneak Attack", "dice": "3d6", "when": "first_hit_per_turn" }"""), """{ "ac": 15, "resistances": ["slashing"] }"""),
        new("Spiritual Weapon (bonus_action spell attack) + Eldritch Blast", """
            { "name": "Cleric-lock", "edition": "2024", "level": 5, "abilities": {"wis": 18, "cha": 16},
              "attacks": [{ "name": "Eldritch Blast", "to_hit": {"ability": "cha"}, "damage": "1d10", "damage_type": "force", "ability_to_damage": false, "properties": ["ranged", "spell"], "cantrip": "beams" },
                          { "name": "Spiritual Weapon", "action": "bonus_action", "to_hit": {"ability": "wis"}, "damage": "1d8", "damage_type": "force", "properties": ["melee", "spell"] }] }
            """, """{ "ac": 15 }"""),
        new("Action Surge (fight horizon)", Fighter2014(Gwm2014 + """, { "kind": "extra_attack", "name": "Action Surge", "attack": "Greatsword", "count": 2, "action": "action", "resource": { "uses": 1, "per": "short_rest" } }"""), """{ "ac": 15 }"""),
        new("Reaction attack (Sentinel, 0.5)", Fighter2014("""{ "kind": "extra_attack", "name": "Sentinel", "attack": "Greatsword", "action": "reaction", "trigger_probability": 0.5 }"""), """{ "ac": 15 }"""),
        new("Offhand attack (2024)", """
            { "name": "Two weapons", "edition": "2024", "level": 5, "abilities": {"dex": 18},
              "attacks": [{ "name": "Shortsword", "count": 2, "to_hit": {"ability": "dex"}, "damage": "1d6", "damage_type": "piercing", "properties": ["melee", "finesse", "light"] },
                          { "name": "Offhand", "action": "bonus_action", "offhand": true, "to_hit": {"ability": "dex"}, "damage": "1d6", "damage_type": "piercing", "properties": ["melee", "finesse", "light"] }] }
            """, """{ "ac": 15 }"""),
        new("Fireball vs 4 targets", Fireball, """{ "ac": 12, "saves": {"dex": 2} }"""),
        new("Fireball vs 4 targets with Evasion", Fireball, """{ "ac": 15, "saves": {"dex": 4}, "evasion": true }"""),
        new("Sacred Flame vs Magic Resistance", """
            { "name": "Cleric", "edition": "2024", "level": 5, "abilities": {"wis": 18},
              "modifiers": [{ "kind": "save_effect", "name": "Sacred Flame", "ability": "dex", "dc_ability": "wis", "dice": "1d8", "type": "radiant", "on_success": "none", "cantrip": true }] }
            """, """{ "ac": 15, "save_bonus": 2, "magic_resistance": true }"""),
        new("Stunning Strike (2024: until the start of the next turn)", Monk("2024"), """{ "ac": 15, "saves": {"con": 2} }"""),
        new("Stunning Strike (2014: until the end of the next turn)", Monk("2014"), """{ "ac": 15, "saves": {"con": 2} }""", Check: Check.Round1AndFightAtLeast),
        new("Warlock baseline, level 5", """{ "name": "Warlock", "preset": "warlock_baseline", "level": 5 }""", null),
        new("Hex set up with the Bonus Action", """
            { "name": "Hexblade", "edition": "2024", "level": 5, "abilities": {"cha": 18},
              "attacks": [{ "name": "Eldritch Blast", "to_hit": {"ability": "cha"}, "damage": "1d10", "damage_type": "force", "ability_to_damage": false, "properties": ["ranged", "spell"], "cantrip": "beams" }],
              "modifiers": [{ "kind": "extra_damage", "name": "Hex", "dice": "1d6", "type": "necrotic", "concentration": true, "setup": "bonus_action" }] }
            """, """{ "ac": 15 }"""),
        new("Archer vs a prone target (Disadvantage)", """
            { "name": "Archer", "edition": "2024", "level": 5, "abilities": {"dex": 18}, "fighting_style": "archery",
              "attacks": [{ "name": "Longbow", "count": 2, "to_hit": {"ability": "dex"}, "damage": "1d8", "damage_type": "piercing", "properties": ["ranged", "heavy", "two-handed"] }] }
            """, """{ "ac": 15, "condition": "prone" }"""),
        new("Half cover, one attack ignores it", """
            { "name": "Sharpshooter", "edition": "2024", "level": 5, "abilities": {"dex": 18},
              "attacks": [{ "name": "Longbow", "to_hit": {"ability": "dex"}, "damage": "1d8", "damage_type": "piercing", "properties": ["ranged"] },
                          { "name": "Hand crossbow", "to_hit": {"ability": "dex"}, "damage": "1d6", "damage_type": "piercing", "properties": ["ranged"] }],
              "modifiers": [{ "kind": "ignore_cover", "attacks": ["Longbow"] }] }
            """, """{ "ac": 15, "cover": "half" }"""),
        new("Paralyzed target (melee auto-crits)", Longswords(""), """{ "ac": 15, "condition": "paralyzed" }"""),
        new("Dodging target", Fighter2014(), """{ "ac": 15, "condition": "dodging" }"""),
    ];

    public static IEnumerable<object[]> CaseData => Cases.Select((c, i) => new object[] { c.Name, i });

    [Fact]
    public void Battery_HasAtLeastTwentyFiveBuilds() => Assert.True(Cases.Count >= 25, $"{Cases.Count} builds");

    [Theory]
    [MemberData(nameof(CaseData))]
    public void Simulator_AgreesWithTheClosedForm(string name, int index)
    {
        var c = Cases[index];
        var (build, target) = SimKit.Resolve(c.Build, c.Target, c.Rulings);
        var exact = SimKit.Exact(build, target, Rounds);
        var sim = DummyDpr.Run(build, target, Rounds, Iterations, (ulong)(20260927 + (index * 7919)));

        var fightGap = (sim.MeanPerRound - exact.DamagePerRound) / sim.StandardError;
        var round1Gap = (sim.Round1Mean - exact.Round1Damage) / sim.Round1StandardError;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"| {name} | {exact.DamagePerRound:0.0000} | {sim.MeanPerRound:0.0000} ± {sim.StandardError:0.0000} ({fightGap:+0.00;-0.00} SE) | {exact.Round1Damage:0.0000} | {sim.Round1Mean:0.0000} ± {sim.Round1StandardError:0.0000} ({round1Gap:+0.00;-0.00} SE) |"));

        Assert.True(Math.Abs(round1Gap) <= Tolerance, $"{name}: round 1 exact {exact.Round1Damage}, sim {sim.Round1Mean} ± {sim.Round1StandardError} ({round1Gap:0.00} SE).");
        if (c.Check == Check.Both)
        {
            Assert.True(Math.Abs(fightGap) <= Tolerance, $"{name}: per round exact {exact.DamagePerRound}, sim {sim.MeanPerRound} ± {sim.StandardError} ({fightGap:0.00} SE).");
        }
        else
        {
            // The condition lasts through the build's next turn in the simulator: its later attacks keep Advantage.
            Assert.True(fightGap > Tolerance, $"{name}: expected the simulator above the closed form over the fight; gap {fightGap:0.00} SE.");
        }
    }

    [Theory]
    [InlineData("2014 fighter GWF + GWM auto, AC 13")]
    [InlineData("2024 fighter GWF + Graze + GWM + Hew, AC 15")]
    [InlineData("Smite any_hit")]
    [InlineData("Fireball vs 4 targets")]
    [InlineData("Reaction attack (Sentinel, 0.5)")]
    public void Round1Distribution_PercentilesMatchTheExactOnes(string name)
    {
        var index = Cases.ToList().FindIndex(c => c.Name == name);
        var c = Cases[index];
        var (build, target) = SimKit.Resolve(c.Build, c.Target, c.Rulings);
        var exact = SimKit.ExactRound1(build, target).Round1Distribution!;
        var sim = DummyDpr.Run(build, target, 1, Iterations, (ulong)(777 + index));

        foreach (var fraction in new[] { 0.5, 0.9 })
        {
            var expected = exact.Percentile(fraction);
            var actual = sim.Round1Percentile(fraction);
            Assert.True(Math.Abs(actual - expected) <= 1, $"{name}: p{fraction * 100} exact {expected}, sim {actual}.");
        }
    }
}
