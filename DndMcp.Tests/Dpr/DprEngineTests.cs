using DndMcp.Domain.Core;
using DndMcp.Domain.Dice;
using DndMcp.Domain.Dpr;
using DndMcp.Tests.Features;
using Xunit;
using static DndMcp.Tests.Dpr.DprTestKit;

namespace DndMcp.Tests.Dpr;

/// <summary>
/// Invariant: the engine's behaviour beyond the oracle's cases — resources that run out over a fight and come back when
/// unlimited, save effects that cost no action or have limited uses, a Bonus-Action-costing rider without a resource,
/// Unconscious read as Prone too, the landing chances per turn and per fight, Sap's defensive number, the reaction and
/// Bonus Action reports, the notes and rulings echoed, and every limit failing with an actionable message.
/// </summary>
public sealed class DprEngineTests
{
    private const double Exact = 1e-9;
    private const string Ac15 = """{ "ac": 15 }""";

    private const string Dagger =
        """{ "name": "Dagger", "action": "bonus_action", "offhand": true, "to_hit": {"ability": "dex"}, "damage": "1d4", "damage_type": "piercing", "properties": ["melee", "light", "finesse"] }""";

    [Fact]
    public void Evaluate_BonusActionRiderWithoutResource_SpendsTheBonusActionOnTheFirstHitOnly()
    {
        // An every-hit rider that costs the Bonus Action is a choice even with no resource: one Bonus Action a turn.
        var build = Level5($"[{Longsword2}, {Dagger}]", """[{ "kind": "extra_damage", "name": "Searing", "dice": "1d6", "type": "fire", "action_cost": "bonus_action" }]""");

        var result = Evaluate(build, Ac15);

        Assert.Equal(1 - (0.35 * 0.35), Rider(result, "Searing").UsesPerRound, Exact); // P(at least one hit)
        Assert.Equal(0.35 * 0.35, result.Attacks.Single(a => a.Attack == "Dagger").AttacksPerRound, Exact); // only when both miss
    }

    [Fact]
    public void Evaluate_FreeSaveEffectWithTwoUses_IsCastEachTurnUntilTheUsesRunOut()
    {
        // action_cost none: alongside the attacks, every turn, while uses last. DC 15 vs Con +2: F = 0.6, 5 on a failure.
        var build = Level5($"[{Longsword}]", """[{ "kind": "save_effect", "name": "Aura", "ability": "con", "dc": 15, "amount": 5, "on_success": "none", "action_cost": "none", "resource": { "uses": 2, "per": "long_rest" } }]""");
        const string target = """{ "ac": 15, "saves": {"con": 2} }""";

        var result = Evaluate(build, target, DprOptions.Fight(3));

        Assert.Equal([8.75, 8.75, 5.75], result.DamageByRound.ToArray(), (a, b) => Math.Abs(a - b) < Exact);
        var aura = Assert.Single(result.SaveEffects);
        Assert.Equal(2.0 / 3, aura.CastsPerRound, Exact);
        Assert.Equal(2.0, aura.DamagePerRound, Exact);
    }

    [Fact]
    public void Evaluate_ActionSaveEffectWithTwoUses_LeavesTheThirdRoundEmpty()
    {
        var result = Evaluate(FeatureTestBuilds.FireballJson, """{ "saves": {"dex": 2}, "hp": 7 }""", DprOptions.Fight(3));

        Assert.Equal([89.2, 89.2, 0.0], result.DamageByRound.ToArray(), (a, b) => Math.Abs(a - b) < Exact);
        Assert.Equal(2.0 / 3, Assert.Single(result.SaveEffects).CastsPerRound, Exact);
    }

    [Fact]
    public void Evaluate_AreaEffectWithHitPoints_LeavesOutTheSingleCreatureKillChance()
    {
        // The turn's total is summed over four goblins, so "P(turn damage ≥ HP)" would not mean "drops one creature".
        var area = Evaluate(FeatureTestBuilds.FireballJson, """{ "saves": {"dex": 2}, "hp": 7 }""");
        var single = Evaluate(
            """{ "name": "Cleric", "level": 5, "abilities": {"wis": 18}, "modifiers": [{ "kind": "save_effect", "name": "Sacred Flame", "ability": "dex", "dc": 13, "dice": "2d8", "type": "radiant", "on_success": "none" }] }""",
            """{ "saves": {"dex": 1}, "hp": 9 }""");

        Assert.Null(area.Round1Distribution!.AtLeastHitPointsChance);
        Assert.NotNull(single.Round1Distribution!.AtLeastHitPointsChance);
        Assert.Equal(single.Round1Distribution.AtLeast(9), single.Round1Distribution.AtLeastHitPointsChance!.Value, 1e-15);
    }

    [Fact]
    public void Evaluate_RangedAttackAgainstUnconsciousTarget_IsANormalRoll()
    {
        // Both editions: an Unconscious creature is also Prone, so a ranged attack's Advantage (Unconscious) and
        // Disadvantage (Prone, attacker beyond 5 feet) cancel: +7 vs AC 15 with 1d8+4 is 0.6 × 8.5 + 0.05 × 13.
        const string longbow = """{ "name": "Longbow", "to_hit": {"ability": "dex"}, "damage": "1d8", "damage_type": "piercing", "properties": ["ranged", "heavy", "two-handed"] }""";
        const string unconscious = """{ "ac": 15, "condition": "unconscious" }""";

        var ranged = Evaluate(Level5($"[{longbow}]"), unconscious);
        var melee = Evaluate(Level5($"[{Longsword}]"), unconscious);

        Assert.Equal(5.75, ranged.DamagePerRound, Exact);
        Assert.Contains(ranged.Notes, n => n.StartsWith("An Unconscious creature is also Prone", StringComparison.Ordinal));
        Assert.Equal(0.8775 * 13, melee.DamagePerRound, Exact); // Advantage, every hit a crit within 5 feet
    }

    [Fact]
    public void Evaluate_ConditionOnHitInAFight_ReportsItsChancePerTurnAndPerFight()
    {
        // One attack a turn (P(hit) 0.65), DC 15 vs Con +2 (F 0.6): it lands with 0.39 a turn, independently each turn
        // (conditions last to the end of the turn), so at least once in three rounds with 1 − 0.61³.
        var build = Level5($"[{Longsword}]", """[{ "kind": "condition_on_hit", "name": "Stunning Strike", "condition": "stunned", "ability": "con", "dc": 15 }]""");

        var condition = Assert.Single(Evaluate(build, """{ "ac": 15, "saves": {"con": 2} }""", DprOptions.Fight(3)).Conditions);

        Assert.Equal(0.65, condition.AttemptsPerRound, Exact);
        Assert.Equal(0.39, condition.LandsPerRound, Exact);
        Assert.Equal(0.39, condition.LandChancePerTurn, Exact);
        Assert.Equal(1 - Math.Pow(0.61, 3), condition.LandChancePerFight!.Value, Exact);
    }

    [Fact]
    public void Evaluate_SaveEffectConditionInAFight_ReportsItsChancePerFight()
    {
        const string build = """
            { "name": "Cleric", "level": 5, "abilities": {"wis": 18},
              "modifiers": [{ "kind": "save_effect", "name": "Hold Person", "ability": "wis", "dc": 15, "condition": "paralyzed" }] }
            """;

        var fight = Assert.Single(Evaluate(build, """{ "saves": {"wis": 2} }""", DprOptions.Fight(3)).SaveEffects);
        var round1 = Assert.Single(Evaluate(build, """{ "saves": {"wis": 2} }""").SaveEffects);

        Assert.Equal(1 - Math.Pow(0.4, 3), fight.LandChancePerFight!.Value, Exact);
        Assert.Equal(0.6, fight.LandChancePerTurn!.Value, Exact);
        Assert.Equal(1, fight.CastsPerRound, Exact);
        Assert.Null(round1.LandChancePerFight);
        Assert.Equal(0.6, round1.LandChancePerTurn!.Value, Exact);
    }

    [Fact]
    public void Evaluate_SaveEffectWithACondition_TracksLandingWithoutChangingTheDistribution()
    {
        // Tracking "did the first target fail" splits the shared-roll distribution into two conditional ones; mixed back
        // together they must be exactly the unsplit distribution of the same effect without a condition.
        const string effect = """{ "kind": "save_effect", "name": "Lash", "ability": "wis", "dc": 14, "dice": "3d6", "type": "psychic", "targets": 3{0} }""";
        string Build(string condition) => $$"""{ "name": "Caster", "level": 5, "modifiers": [{{effect.Replace("{0}", condition, StringComparison.Ordinal)}}] }""";
        const string target = """{ "saves": {"wis": 1} }""";

        var tracked = Evaluate(Build(""", "condition": "frightened" """), target, DprOptions.Fight(2));
        var plain = Evaluate(Build(""), target, DprOptions.Fight(2));

        Assert.Equal(plain.DamagePerRound, tracked.DamagePerRound, Exact);
        var a = tracked.Round1Distribution!.Values;
        var b = plain.Round1Distribution!.Values;
        Assert.Equal(b.Select(v => v.Damage), a.Select(v => v.Damage));
        Assert.All(a.Zip(b), pair => Assert.Equal(pair.Second.Probability, pair.First.Probability, 1e-15));
        var lash = Assert.Single(tracked.SaveEffects);
        Assert.Equal(0.6, lash.LandChancePerTurn!.Value, Exact); // DC 14 vs +1: fails on 1–12
        Assert.Equal(1 - (0.4 * 0.4), lash.LandChancePerFight!.Value, Exact);
    }

    [Fact]
    public void Evaluate_SapWeapon_ReportsTheChanceOfASappingHitAndNoDamage()
    {
        var sap = Evaluate(Level5("""[{ "name": "Longsword", "count": 2, "damage": "1d8", "damage_type": "slashing", "mastery": "sap" }]"""), Ac15);
        var plain = Evaluate(Level5($"[{Longsword2}]"), Ac15);

        Assert.Equal(1 - (0.35 * 0.35), sap.SapChancePerTurn!.Value, Exact);
        Assert.Equal(plain.DamagePerRound, sap.DamagePerRound, Exact);
        Assert.Contains(sap.Notes, n => n.StartsWith("Sap adds no damage", StringComparison.Ordinal));
        Assert.Null(plain.SapChancePerTurn);
    }

    [Fact]
    public void Evaluate_TwoReactions_TakesTheBestByChanceTimesDamage()
    {
        var build = Level5($"[{Longsword}]", """
            [{ "kind": "extra_attack", "name": "Opportunity Attack", "attack": "Longsword", "action": "reaction", "trigger_probability": 0.4 },
             { "kind": "extra_attack", "name": "Sentinel", "attack": "Longsword", "action": "reaction", "trigger_probability": 0.5 }]
            """);

        var result = Evaluate(build, Ac15);

        var reaction = result.Reaction!;
        Assert.Equal("Sentinel", reaction.Name);
        Assert.Equal(0.5 * 5.75, reaction.DamagePerRound, Exact);
        Assert.Equal(["Opportunity Attack", "Sentinel"], reaction.Considered.Select(c => c.Name));
        Assert.Equal(5.75 * 1.5, result.DamagePerRound, Exact);
        Assert.Equal(0, result.ExtraAttacks.Single(e => e.Name == "Opportunity Attack").UsesPerRound);
        Assert.Equal(0.5, result.ExtraAttacks.Single(e => e.Name == "Sentinel").UsesPerRound, Exact);
    }

    [Fact]
    public void Evaluate_CritWithARangedOrSpellAttack_DoesNotTriggerACritBonusAttack()
    {
        // Hew and the 2014 GWM bonus attack need "a critical hit with a melee weapon": a longbow's or a spell's crit is not one.
        foreach (var (attack, properties) in new[] { ("Longbow", "[\"ranged\"]"), ("Spiritual Weapon", "[\"melee\", \"spell\"]"), ("Longsword", "[\"melee\"]") })
        {
            var build = Level5(
                $$"""[{ "name": "{{attack}}", "count": 2, "damage": "1d8", "damage_type": "force", "properties": {{properties}} }]""",
                $$"""[{ "kind": "extra_attack", "name": "Bonus attack", "attack": "{{attack}}", "action": "bonus_action", "trigger": "crit" }]""");

            var uses = Evaluate(build, Ac15).ExtraAttacks.Single().UsesPerRound;

            Assert.Equal(attack == "Longsword" ? 1 - (0.95 * 0.95) : 0, uses, Exact);
        }
    }

    [Fact]
    public void Evaluate_BonusActionAttack_ReportsTheChoicesMade()
    {
        var result = Evaluate(Level5($"[{Longsword}, {Dagger}]"), Ac15);

        Assert.Equal(2, result.BonusActionChoices.Count);
        Assert.Equal(1, result.BonusActionChoices[0].ChosenPerRound, Exact);
        Assert.Equal("none", result.BonusActionChoices[1].Option);
        Assert.Equal(0, result.BonusActionChoices[1].ChosenPerRound, Exact);
        Assert.Empty(Evaluate(Level5($"[{Longsword}]"), Ac15).BonusActionChoices);
    }

    [Fact]
    public void Evaluate_SmiteWithOneUseOverAFight_IsSpentOnceAtMost()
    {
        // 2014 paladin, 2d8 once-per-turn smite with one use: spent at the first hit of the fight, if any.
        const string build = """
            { "name": "Paladin", "edition": "2014", "level": 5, "abilities": {"str": 18},
              "attacks": [{ "name": "Longsword", "count": 2, "damage": "1d8", "damage_type": "slashing", "properties": ["melee", "versatile"] }],
              "modifiers": [{ "kind": "extra_damage", "name": "Divine Smite", "dice": "2d8", "type": "radiant", "when": "first_hit_per_turn",
                              "resource": { "uses": 1, "per": "long_rest" } }] }
            """;

        var limited = Evaluate(build, Ac15, DprOptions.Fight(3));
        var unlimited = Evaluate(build, Ac15, DprOptions.Fight(3) with { UnlimitedResources = [1] });

        Assert.Equal((1 - Math.Pow(0.35, 6)) / 3, Rider(limited, "Divine Smite").UsesPerRound, Exact);
        Assert.Equal(20.005, limited.DamageByRound[0], Exact);
        Assert.Equal(11.5 + (0.1225 * 8.505), limited.DamageByRound[1], Exact);
        Assert.All(unlimited.DamageByRound, d => Assert.Equal(20.005, d, Exact));
        Assert.Equal(0.8775, Rider(unlimited, "Divine Smite").UsesPerRound, Exact);
    }

    [Fact]
    public void Evaluate_ActionSurgeOverAFight_ReportsItsUsesPerRound()
    {
        // One use per short rest, one use per turn: taken in round 1 only; with unlimited uses, every round (the day
        // horizon's u = 1).
        var build = FeatureTestBuilds.Fighter2014GwmJson.Replace(
            "\"trigger\": \"crit\" }",
            "\"trigger\": \"crit\" }, { \"kind\": \"extra_attack\", \"name\": \"Action Surge\", \"attack\": \"Greatsword\", \"count\": 2, \"action\": \"action\", \"resource\": { \"uses\": 1, \"per\": \"short_rest\" } }",
            StringComparison.Ordinal);

        var limited = Evaluate(build, Ac15, DprOptions.Fight(3));
        var unlimited = Evaluate(build, Ac15, DprOptions.Fight(3) with { UnlimitedResources = [3] });

        var surge = Assert.Single(limited.Resources);
        Assert.Equal("Action Surge", surge.Source.Label);
        Assert.Equal(1.0 / 3, surge.UsesPerRound, Exact);
        Assert.False(surge.Unlimited);
        Assert.Equal(1.0, Assert.Single(unlimited.Resources).UsesPerRound, Exact);
        Assert.True(unlimited.Resources[0].Unlimited);
        Assert.Equal(limited.DamageByRound[0], unlimited.DamageByRound[2], Exact);
        Assert.True(limited.DamageByRound[0] > limited.DamageByRound[1]);
    }

    [Fact]
    public void Evaluate_RemovedModifier_IsLeftOutOfTheEvaluation()
    {
        var build = Level5($"[{Longsword}]", """[{ "kind": "extra_damage", "name": "Hex", "dice": "1d6", "type": "necrotic" }, { "kind": "advantage", "name": "Reckless" }]""");

        var withoutHex = Evaluate(build, Ac15, DprOptions.Round1 with { RemovedModifiers = [1] });
        var neither = Evaluate(build, Ac15, DprOptions.Round1 with { RemovedModifiers = [1, 2] });

        Assert.Equal(0.8775 * 8.5 + (0.0975 * 4.5), withoutHex.DamagePerRound, Exact); // advantage, no Hex
        Assert.Equal(5.75, neither.DamagePerRound, Exact);
        Assert.Throws<ArgumentException>(() => Evaluate(Level5($"[{Longsword}]", """[{ "kind": "to_hit", "amount": 1 }]"""), Ac15, DprOptions.Round1 with { RemovedModifiers = [1] }));
    }

    [Fact]
    public void Evaluate_Fighter2014Gwm_NotesTheKillTriggerAndEchoesNoRulingItCannotUse()
    {
        var result = Evaluate(FeatureTestBuilds.Fighter2014GwmJson, Ac15);

        Assert.Contains(result.Notes, n => n.StartsWith("GWM bonus attack: only its critical-hit trigger is modelled", StringComparison.Ordinal));
        Assert.Empty(result.Rulings);
        Assert.Equal(DprHorizons.Round1, result.Horizon);
        Assert.Equal(1, result.Rounds);
    }

    [Fact]
    public void Evaluate_Fighter2024GwmWithSavageAttacker_EchoesTheRulingsThatCanChangeIt()
    {
        const string build = """
            { "name": "2024 L5 Fighter", "edition": "2024", "level": 5, "abilities": {"str": 19}, "fighting_style": "gwf",
              "attacks": [{ "name": "Greatsword", "count": 2, "damage": "2d6", "damage_type": "slashing", "properties": ["melee", "heavy", "two-handed"], "mastery": "graze" }],
              "modifiers": [
                { "kind": "bonus_damage", "name": "Great Weapon Master", "amount": "pb", "attack_action_only": true },
                { "kind": "extra_attack", "name": "Hew", "attack": "Greatsword", "action": "bonus_action", "trigger": "crit" },
                { "kind": "reroll_damage_take_best", "name": "Savage Attacker" } ] }
            """;

        var result = Evaluate(build, Ac15, rulingsJson: """{ "hew_gets_pb": true }""");

        Assert.Equal(["hew_gets_pb", "savage_attacker_on_crit_dice"], result.Rulings.Select(r => r.Name));
        Assert.True(result.Rulings[0].Value);
        Assert.False(result.Rulings[1].Value);
        Assert.Equal("Savage Attacker", result.Riders.Single(r => r.Kind == "reroll_damage_take_best").Name);
    }

    [Theory]
    [InlineData("""{ "name": "Push", "damage": "1d8", "mastery": "push" }""", "Push: push adds no damage per round")]
    [InlineData("""{ "name": "Nick", "damage": "1d4", "mastery": "nick", "properties": ["light"] }""", "Nick: Nick changes only the action economy")]
    [InlineData("""{ "name": "Axe", "damage": "1d12", "mastery": "cleave" }""", "Cleave never triggers here")]
    public void Evaluate_MasteryWithoutDamageValue_SaysSo(string attack, string note)
    {
        var result = Evaluate(Level5($"[{attack}]"), Ac15);

        Assert.Contains(result.Notes, n => n.StartsWith(note, StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_NotesFromTheBuildAndTheTarget_AreCarriedThrough()
    {
        // Typeless damage against a resisting target (the resolver's warning), a setup cost, and an area's ±1d3.
        var build = Level5(
            """[{ "name": "Force of will", "damage": "1d8" }]""",
            """
            [{ "kind": "extra_damage", "name": "Hex", "dice": "1d6", "type": "necrotic", "setup": "bonus_action", "concentration": true },
             { "kind": "save_effect", "name": "Breath", "ability": "dex", "dc": 13, "dice": "2d6", "type": "fire", "shape": "cone", "size": 15, "action_cost": "none" }]
            """);

        var result = Evaluate(build, """{ "ac": 15, "resistances": ["fire"], "legendary_resistance": 1 }""");

        Assert.Contains(result.Notes, n => n.StartsWith("Force of will has no damage type", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n == "Hex: set up with round 1's Bonus Action, active from then on.");
        Assert.Contains(result.Notes, n => n.StartsWith("Breath: 2 targets from the DMG's \"Targets in Areas of Effect\"", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n == "Breath: Legendary Resistance is assumed not spent on a damage-only effect.");
    }

    [Theory]
    [InlineData("day", "horizon \"day\" is an average over fights")]
    [InlineData("nova", "horizon \"nova\" is not a horizon; use \"round1\"")]
    public void Evaluate_UnknownHorizon_IsRefusedWithWhatIsAccepted(string horizon, string message)
    {
        var (build, target) = Resolve(Level5($"[{Longsword}]"), Ac15);

        var error = Assert.Throws<DndInputException>(() => DprEngine.Evaluate(build, target, new DprOptions { Horizon = horizon }));

        Assert.StartsWith(message, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Evaluate_RoundsOutsideOneToTen_AreRefused(int rounds)
    {
        var (build, target) = Resolve(Level5($"[{Longsword}]"), Ac15);

        var error = Assert.Throws<DndInputException>(() => DprEngine.Evaluate(build, target, DprOptions.Fight(rounds)));

        Assert.Equal($"rounds is {rounds}; a fight is 1 to 10 rounds (3 is the DMG's convention).", error.Message);
        Assert.Equal(5.75, DprEngine.Evaluate(build, target, DprOptions.Round1 with { Rounds = rounds }).DamagePerRound, Exact); // round1 ignores rounds
    }

    [Fact]
    public void Evaluate_WorkBudgetExceeded_SaysWhatToReduce()
    {
        var (build, target) = Resolve(FeatureTestBuilds.Fighter2014GwmJson, Ac15);

        var error = Assert.Throws<DndInputException>(() => DprEngine.Evaluate(build, target, DprOptions.Round1, new WorkMeter(50)));

        Assert.StartsWith("this build is too large to compute exactly: it needs more work than one evaluation may do", error.Message, StringComparison.Ordinal);
        Assert.Contains("Reduce the damage dice", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_TooManyTurnStates_SaysWhatToReduce()
    {
        var (build, target) = Resolve(FeatureTestBuilds.Fighter2014GwmJson, Ac15);

        var error = Assert.Throws<DndInputException>(() => DprEngine.Evaluate(build, target, DprOptions.Round1 with { TurnStateLimit = 5 }));

        Assert.StartsWith("this build is too large to compute exactly: one turn reaches more than 5 distinct states.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_CancelledToken_StopsTheEvaluation()
    {
        var (build, target) = Resolve(FeatureTestBuilds.Fighter2014GwmJson, Ac15);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => DprEngine.Evaluate(build, target, DprOptions.Fight(), cancellation.Token));
    }

    [Fact]
    public void Evaluate_RealisticGrid_StaysWellInsideTheBudget()
    {
        // A level 1–20 × AC 10–25 grid of the 2014 GWM fighter over a 3-round fight: 320 evaluations. The contract asks
        // under 5 s; each evaluation must also fit its own budgeted meter.
        var spec = Build(FeatureTestBuilds.Fighter2014GwmJson.Replace("\"level\": 5", "\"level\": 20", StringComparison.Ordinal));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (var build in Domain.Features.BuildResolver.Resolve(spec, Enumerable.Range(1, 20).ToList()))
        {
            var target = Domain.Features.TargetResolver.Resolve(null, build.Level);
            for (var ac = 10; ac <= 25; ac++)
            {
                DprEngine.Evaluate(build, target with { ArmorClass = ac }, DprOptions.Fight() with { IncludeDistribution = false }, new WorkMeter(DprLimits.WorkBudget));
            }
        }

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"the grid took {clock.Elapsed}");
    }
}
