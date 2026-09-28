using DndMcp.Domain.Core;
using DndMcp.Domain.Dpr;
using DndMcp.Domain.Features;
using DndMcp.Tests.Features;
using Xunit;
using static DndMcp.Tests.Dpr.DprTestKit;

namespace DndMcp.Tests.Dpr;

/// <summary>
/// Invariant: against a stat block target the engine reads the stat block the way the rules do — a qualified resistance
/// ("from nonmagical attacks that aren't silvered") halves only damage the qualifier admits, judged by the ATTACK for its
/// riders too and by <c>magical</c> for a save effect; an immunity zeroes; Magic Resistance and Legendary Resistance (the
/// non-lair count) change the save odds and the casts to land; a condition the target is immune to is never attempted
/// (condition_on_hit, Topple) or never lands (save effects) — and every such case equals the same build against the
/// equivalent explicit target, so no new arithmetic hides here.
/// </summary>
public sealed class StatBlockTargetTests
{
    private const double Exact = 1e-12;

    // Werewolf saves are its ability modifiers: Str +2, Dex +1, Con +2, Int +0, Wis +0, Cha +0.
    private const string WerewolfSaves = """ "saves": {"str": 2, "dex": 1, "con": 2, "int": 0, "wis": 0, "cha": 0} """;

    private static string Sword(string properties) =>
        $$"""{ "name": "Longsword", "count": 2, "damage": "1d8", "damage_type": "slashing", "properties": ["melee"{{properties}}] }""";

    [Theory]
    [InlineData("", true)]
    [InlineData(", \"magical\"", false)]
    [InlineData(", \"silvered\"", false)]
    [InlineData(", \"adamantine\"", true)] // adamantine does not overcome "that aren't silvered"
    public void Evaluate_WerewolfQualifiedResistance_HalvesOnlyWhatTheQualifierAdmits(string properties, bool resisted)
    {
        // +7 vs AC 11: 0.80 normal hits, 0.05 crits. Resisted: E[⌊(1d8+4)/2⌋] = 4 and E[⌊(2d8+4)/2⌋] = 6.25 → 3.5125 per
        // swing; plain: 0.80 × 8.5 + 0.05 × 13 = 7.45 per swing.
        var build = Level5($"[{Sword(properties)}]");

        var result = EvaluateVs(build, TargetStatBlocks.WerewolfLike());
        var equivalent = Round1(build, resisted ? """{ "ac": 11, "resistances": ["slashing"] }""" : """{ "ac": 11 }""");

        Assert.Equal(resisted ? 7.025 : 14.9, result.DamagePerRound, Exact);
        Assert.Equal(equivalent, result.DamagePerRound, Exact);
    }

    [Theory]
    [InlineData("", "", true)]
    [InlineData(", \"magical\"", "", false)]
    [InlineData("", ", \"type\": \"slashing\"", true)]
    [InlineData(", \"silvered\"", ", \"type\": \"slashing\"", false)]
    public void Evaluate_RiderOnAQualifiedResistance_FollowsItsAttack(string properties, string riderType, bool resisted)
    {
        // Hunter's Mark (untyped: the sword's slashing) or a typed slashing rider is part of the sword's hit, so the sword's
        // properties decide; a radiant smite on the same sword is never resisted (the qualifier covers B/P/S only).
        var build = Level5(
            $"[{Sword(properties)}]",
            $$"""[{ "kind": "extra_damage", "name": "Mark", "dice": "1d6"{{riderType}} }, { "kind": "extra_damage", "name": "Smite", "dice": "2d8", "type": "radiant", "when": "first_hit_per_turn" }]""");

        var result = EvaluateVs(build, TargetStatBlocks.WerewolfLike());
        var equivalent = Evaluate(build, resisted ? """{ "ac": 11, "resistances": ["slashing"] }""" : """{ "ac": 11 }""");

        Assert.Equal(equivalent.DamagePerRound, result.DamagePerRound, Exact);
        Assert.Equal(Rider(equivalent, "Mark").DamagePerRound, Rider(result, "Mark").DamagePerRound, Exact);
        Assert.Equal(Rider(Evaluate(build, """{ "ac": 11 }"""), "Smite").DamagePerRound, Rider(result, "Smite").DamagePerRound, Exact);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Evaluate_SaveEffectOnAQualifiedResistance_IsMagicalWhenItSaysSo(bool magical, bool resisted)
    {
        var build = $$"""{ "name": "Thrower", "level": 5, "modifiers": [{ "kind": "save_effect", "name": "Boulder", "ability": "dex", "dc": 13, "dice": "4d6", "type": "bludgeoning", "magical": {{(magical ? "true" : "false")}} }] }""";

        var result = EvaluateVs(build, TargetStatBlocks.WerewolfLike());
        var equivalent = Round1(build, resisted
            ? $$"""{ {{WerewolfSaves}}, "resistances": ["bludgeoning"] }"""
            : $$"""{ {{WerewolfSaves}} }""");

        Assert.Equal(equivalent, result.DamagePerRound, Exact);
        Assert.NotEqual(0, result.DamagePerRound);
    }

    [Theory]
    [InlineData("", 0.0)]
    [InlineData(", \"silvered\"", 14.9)]
    [InlineData(", \"magical\"", 14.9)]
    public void Evaluate_2014WerewolfImmunity_ZeroesOnlyMundaneWeapons(string properties, double expected)
    {
        var result = EvaluateVs(Level5($"[{Sword(properties)}]"), TargetStatBlocks.Werewolf2014());

        Assert.Equal(expected, result.DamagePerRound, Exact);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData(", \"silvered\"", true)] // silver does not overcome "that aren't adamantine"
    [InlineData(", \"adamantine\"", false)]
    [InlineData(", \"magical\"", false)]
    public void Evaluate_AdamantineQualifier_IsOvercomeByAdamantineOrMagicOnly(string properties, bool resisted)
    {
        var build = Level5($"[{Sword(properties)}]");

        var result = EvaluateVs(build, TargetStatBlocks.StunImmune());
        var equivalent = Round1(build, resisted ? """{ "ac": 20, "resistances": ["slashing"] }""" : """{ "ac": 20 }""");

        Assert.Equal(equivalent, result.DamagePerRound, Exact);
    }

    [Fact]
    public void Evaluate_FireImmuneDragon_TakesNoFireButEverythingElse()
    {
        const string fireBolt =
            """{ "name": "Fire Bolt", "to_hit": {"ability": "dex"}, "damage": "1d10", "damage_type": "fire", "ability_to_damage": false, "properties": ["ranged", "spell"], "cantrip": "dice" }""";
        var fireball = FeatureTestBuilds.FireballJson;
        var dragon = TargetStatBlocks.FireDragon();

        var bolt = EvaluateVs(Level5($"[{fireBolt}]"), dragon);
        var ball = EvaluateVs(fireball, dragon);
        var sword = EvaluateVs(Level5($"[{Sword("")}]"), dragon);

        Assert.Equal(0, bolt.DamagePerRound, Exact);
        Assert.Equal(0, ball.DamagePerRound, Exact);
        Assert.Equal(Round1(Level5($"[{Sword("")}]"), """{ "ac": 18 }"""), sword.DamagePerRound, Exact);

        // The stat block's HP turns the kill figures on; its Dex save (+4) is used, not the typical +3 for CR 10.
        var effect = Assert.Single(ball.SaveEffects);
        Assert.Equal(178, ball.Target.HitPoints);
        Assert.Equal(0, effect.EffectiveDamage!.Value, Exact);
        Assert.Equal((15 - 4 - 1) / 20.0, effect.FailChance, Exact);
    }

    [Theory]
    [InlineData(true, 64.0)] // Wis +9 vs DC 15 with Advantage: F = 0.25² = 0.0625; (3 + 1) / F = 64
    [InlineData(false, 16.0)] // not magical: no Advantage, F = 0.25; 4 / 0.25 = 16
    public void Evaluate_MagicResistanceAndLegendaryResistance_ComeFromTheStatBlock(bool magical, double castsToLand)
    {
        var build = $$"""{ "name": "Cleric", "level": 9, "modifiers": [{ "kind": "save_effect", "name": "Hold Person", "ability": "wis", "dc": 15, "condition": "paralyzed", "magical": {{(magical ? "true" : "false")}} }] }""";

        var result = EvaluateVs(build, TargetStatBlocks.LegendaryCaster(), options: DprOptions.Fight(3));

        var effect = Assert.Single(result.SaveEffects);
        Assert.Equal(magical ? 0.0625 : 0.25, effect.FailChance, Exact);
        Assert.Equal(castsToLand, effect.ExpectedCastsToLand!.Value, Exact);
        Assert.Equal(3, result.Target.LegendaryResistance);
        Assert.True(result.Target.MagicResistance);
        Assert.Contains(result.Notes, n => n.Contains("Legendary Resistance is 3 a day (4 in its lair); the target is not in its lair here", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("Archlich's Regeneration and Parry are not read by the damage-per-round engine", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_StunningStrikeOnAStunImmuneTarget_IsNeverAttemptedAndSpendsNothing()
    {
        const string strike = """[{ "kind": "condition_on_hit", "name": "Stunning Strike", "condition": "stunned", "ability": "con", "dc": 15, "when": "first_hit_per_turn", "resource": { "uses": 2, "per": "short_rest" } }]""";
        var withStrike = Level5($"[{Sword("")}]", strike);
        var without = Level5($"[{Sword("")}]");
        var horror = TargetStatBlocks.StunImmune();

        var result = EvaluateVs(withStrike, horror, options: DprOptions.Fight(3));
        var plain = EvaluateVs(without, horror, options: DprOptions.Fight(3));
        var landsElsewhere = Evaluate(withStrike, """{ "ac": 20 }""", DprOptions.Fight(3));

        var condition = Assert.Single(result.Conditions);
        Assert.True(condition.Immune);
        Assert.Equal((0.0, 0.0, 0.0), (condition.AttemptsPerRound, condition.LandsPerRound, condition.LandChancePerTurn));
        Assert.Equal(0.0, condition.LandChancePerFight);
        Assert.Equal(0.0, Assert.Single(result.Resources).UsesPerRound);
        Assert.Equal(plain.DamagePerRound, result.DamagePerRound, Exact);
        Assert.True(Assert.Single(landsElsewhere.Conditions).AttemptsPerRound > 0);
        Assert.Contains(
            "Stunning Strike: the Helmed Horror is immune to the stunned condition, so it is never attempted (no save forced, no use spent) and adds nothing.",
            result.Notes);
        Assert.DoesNotContain(result.Notes, n => n.StartsWith("Stunning Strike: the stunned condition counts only", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_ToppleOnAProneImmuneTarget_ForcesNoSaveAndGrantsNoAdvantage()
    {
        const string maul = """{ "name": "Maul", "count": 2, "damage": "2d6", "damage_type": "bludgeoning", "properties": ["melee", "heavy", "two-handed"]MASTERY }""";
        var topple = Level5($"[{maul.Replace("MASTERY", ", \"mastery\": \"topple\"", StringComparison.Ordinal)}]");
        var plain = Level5($"[{maul.Replace("MASTERY", "", StringComparison.Ordinal)}]");
        var pudding = TargetStatBlocks.ProneImmune();

        var result = EvaluateVs(topple, pudding);

        Assert.Equal(EvaluateVs(plain, pudding).DamagePerRound, result.DamagePerRound, Exact);
        Assert.True(Round1(topple, """{ "ac": 7 }""") > result.DamagePerRound); // against a creature that can fall, Topple adds
        Assert.Contains("Maul: the Black Pudding is immune to the prone condition, so Topple forces no save and never knocks it prone.", result.Notes);
    }

    [Theory]
    [InlineData("\"dice\": \"3d8\", \"type\": \"psychic\", ", true)]
    [InlineData("", false)]
    public void Evaluate_SaveEffectConditionTheTargetIsImmuneTo_NeverLandsButItsDamageCounts(string damage, bool dealsDamage)
    {
        var build = $$"""{ "name": "Cleric", "level": 9, "modifiers": [{ "kind": "save_effect", "name": "Hold", "ability": "wis", "dc": 15, {{damage}}"condition": "paralyzed", "resource": {"uses": 2, "per": "long_rest"} }] }""";

        var result = EvaluateVs(build, TargetStatBlocks.StunImmune(), options: DprOptions.Fight(3));

        var effect = Assert.Single(result.SaveEffects);
        Assert.True(effect.ConditionImmune);
        Assert.Equal(0.0, effect.LandChancePerTurn);
        Assert.Equal(0.0, effect.LandChancePerFight);
        Assert.Null(effect.ExpectedCastsToLand);
        Assert.Equal(dealsDamage, result.DamagePerRound > 0);
        Assert.Contains(result.Notes, n => n.StartsWith("Hold: the Helmed Horror is immune to the paralyzed condition, so it never lands", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Notes, n => n.StartsWith("Hold: its paralyzed is reported", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_ExplicitFieldsOverTheStatBlock_WinAndTheResultSaysSo()
    {
        var build = Level5($"[{Sword("")}]");

        var result = EvaluateVs(build, TargetStatBlocks.WerewolfLike(), """{ "ac": 15, "resistances": [] }""");

        Assert.Equal(Round1(build, """{ "ac": 15 }"""), result.DamagePerRound, Exact);
        Assert.Contains(result.Notes, n => n.StartsWith("Given, overriding the Werewolf stat block: ac 15 (stat block 11); resistances none (stat block cold; bludgeoning, piercing and slashing from nonmagical attacks that aren't silvered).", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_QualifiedResistance_NotesWhereItAppliedAndWhy()
    {
        var build = Level5(
            $$"""[{{Sword("")}}, { "name": "Silver Dagger", "damage": "1d4", "damage_type": "piercing", "properties": ["melee", "silvered"] }, { "name": "Fire Bolt", "to_hit": {"ability": "dex"}, "damage": "1d10", "damage_type": "fire", "ability_to_damage": false, "properties": ["spell", "ranged"] }]""");

        var result = EvaluateVs(build, TargetStatBlocks.WerewolfLike());

        Assert.Contains(
            "Resistance to bludgeoning, piercing and slashing from nonmagical attacks that aren't silvered: applied to Longsword (neither magical nor silvered); not applied to Silver Dagger (silvered).",
            result.Notes);
        Assert.Contains(result.Notes, n => n.StartsWith("Mark an attack that overcomes a qualified resistance with properties [\"magical\"]", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_OnlyBypassingAttacks_NoteWithoutTheHint()
    {
        var result = EvaluateVs(Level5($"[{Sword(", \"magical\"")}]"), TargetStatBlocks.WerewolfLike());

        Assert.Contains(
            "Resistance to bludgeoning, piercing and slashing from nonmagical attacks that aren't silvered: not applied to Longsword (magical).",
            result.Notes);
        Assert.DoesNotContain(result.Notes, n => n.StartsWith("Mark an attack", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_MonsterTarget_IsTheSameCreatureAtEveryLevel()
    {
        var request = new DprRequest
        {
            Build = Build(FeatureTestBuilds.Fighter2014GwmJson),
            Target = new TargetSpec { Monster = "ogre" },
            TargetMonster = TargetStatBlocks.Ogre(),
            Levels = [1, 5, 11],
        };

        var report = DprAnalysis.Analyze(request);

        Assert.All(report.Levels, l => Assert.Equal((11, 68, "Ogre stat block"), (l.Target.ArmorClass, l.Target.HitPoints!.Value, l.Target.ArmorClassSource)));
        Assert.NotNull(report.Detail.Result.Evaluation.Round1Distribution!.AtLeastHitPointsChance);
    }

    [Fact]
    public void Analyze_MonsterNamedWithoutItsStatBlock_FailsBeforeAnyWork()
    {
        var request = new DprRequest { Build = Build(FeatureTestBuilds.Fighter2014GwmJson), Target = new TargetSpec { Monster = "ogre" } };

        var ex = Assert.Throws<DndInputException>(() => DprAnalysis.Analyze(request));

        Assert.StartsWith("target monster \"ogre\" was not looked up", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_StunningStrikeAgainstAStunImmuneMonster_AddsNothingAndSaysImmune()
    {
        var request = new CompareRequest
        {
            Baseline = Build(Level5($"[{Sword("")}]")),
            Feature = DslJson.Deserialize<FeatureSpec>(
                """{ "name": "Stunning Strike", "modifiers": [{ "kind": "condition_on_hit", "name": "Stunning Strike", "condition": "stunned", "ability": "con", "dc": 15 }] }""",
                "feature"),
            Target = new TargetSpec { Monster = "helmed horror" },
            TargetMonster = TargetStatBlocks.StunImmune(),
        };

        var report = DprComparison.Compare(request);

        Assert.Equal(0, report.Detail.Delta, Exact);
        var effect = Assert.Single(report.SignatureEffects);
        Assert.True(effect.Immune);
        Assert.Equal(0, effect.LandChancePerFight);
    }
}
