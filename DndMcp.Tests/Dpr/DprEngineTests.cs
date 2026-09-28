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

    // ------------------------------------------------------------------------------------------------------------------
    // Phase 4 review fixes.
    // ------------------------------------------------------------------------------------------------------------------

    private const string Rapier =
        """{ "name": "Rapier", "to_hit": {"ability": "dex"}, "damage": "1d8", "damage_type": "piercing", "properties": ["melee", "finesse"] }""";

    private static string SneakAttack(string type = "") =>
        $$"""{ "kind": "extra_damage", "name": "Sneak Attack", "dice": "3d6", "when": "first_hit_per_turn"{{type}} }""";

    [Fact]
    public void Evaluate_UntypedRiderOnATypedAttack_TakesTheAttacksTypeAgainstResistance()
    {
        // +7 vs AC 15; halved per hit: normal E[⌊(1d8 + 3d6 + 4)/2⌋] = (19 − ½)/2 = 9.25, crit (34 − ½)/2 = 16.75 (the parity
        // of a sum with a d8 is even half the time): 0.6 × 9.25 + 0.05 × 16.75 = 6.3875. Typeless it would be 10.06.
        const string resists = """{ "ac": 15, "resistances": ["piercing"] }""";

        var untyped = Round1(Level5($"[{Rapier}]", $"[{SneakAttack()}]"), resists);
        var typed = Round1(Level5($"[{Rapier}]", $"[{SneakAttack(", \"type\": \"piercing\"")}]"), resists);

        Assert.Equal(6.3875, untyped, Exact);
        Assert.Equal(typed, untyped, Exact);
    }

    [Fact]
    public void Evaluate_UntypedRiderOnAnAttackTheTargetIsImmuneTo_DealsNothing()
    {
        Assert.Equal(0, Round1(Level5($"[{Rapier}]", $"[{SneakAttack()}]"), """{ "ac": 15, "immunities": ["piercing"] }"""), Exact);
    }

    [Fact]
    public void Evaluate_UntypedOnMissRider_TakesTheAttacksType()
    {
        // Miss 0.35 × E[⌊1d6/2⌋] 1.5 + normal 0.6 × E[⌊(1d8+4)/2⌋] 4 + crit 0.05 × E[⌊(2d8+4)/2⌋] 6.25 = 3.2375.
        const string resists = """{ "ac": 15, "resistances": ["piercing"] }""";
        const string rider = """{ "kind": "extra_damage", "name": "Grit", "dice": "1d6", "when": "on_miss"TYPE }""";

        var untyped = Round1(Level5($"[{Rapier}]", $"[{rider.Replace("TYPE", "", StringComparison.Ordinal)}]"), resists);
        var typed = Round1(Level5($"[{Rapier}]", $"[{rider.Replace("TYPE", ", \"type\": \"piercing\"", StringComparison.Ordinal)}]"), resists);

        Assert.Equal(3.2375, untyped, Exact);
        Assert.Equal(typed, untyped, Exact);
    }

    [Fact]
    public void Evaluate_OneUntypedRiderOnTwoAttackTypes_IsAdjustedPerAttack()
    {
        // The same rider lands as slashing on the longsword (not resisted) and as fire on the flame blade (resisted).
        const string flameBlade = """{ "name": "Flame Blade", "damage": "1d8", "damage_type": "fire", "properties": ["melee"] }""";
        const string resistsFire = """{ "ac": 15, "resistances": ["fire"] }""";

        var untyped = Round1(Level5($"[{Longsword}, {flameBlade}]", """[{ "kind": "extra_damage", "name": "Hex", "dice": "1d6" }]"""), resistsFire);
        var perAttack = Round1(
            Level5(
                $"[{Longsword}, {flameBlade}]",
                """[{ "kind": "extra_damage", "name": "Hex", "dice": "1d6", "type": "slashing", "attacks": ["Longsword"] }, { "kind": "extra_damage", "name": "Hex 2", "dice": "1d6", "type": "fire", "attacks": ["Flame Blade"] }]"""),
            resistsFire);
        var allSlashing = Round1(Level5($"[{Longsword}, {flameBlade}]", """[{ "kind": "extra_damage", "name": "Hex", "dice": "1d6", "type": "slashing" }]"""), resistsFire);

        Assert.Equal(perAttack, untyped, Exact);
        Assert.NotEqual(allSlashing, untyped, 6);
    }

    [Fact]
    public void Evaluate_UntypedRiderOnAnElementalAdeptAttack_GetsElementalAdept()
    {
        const string fireBolt =
            """{ "name": "Fire Bolt", "to_hit": {"ability": "dex"}, "damage": "1d10", "damage_type": "fire", "ability_to_damage": false, "properties": ["ranged", "spell"] }""";
        const string adept = """{ "kind": "damage_die_remap", "name": "Elemental Adept", "remap": "elemental_adept", "type": "fire" }""";

        var untyped = Round1(Level5($"[{fireBolt}]", $$"""[{{adept}}, { "kind": "extra_damage", "name": "Hex", "dice": "1d6" }]"""), Ac15);
        var fire = Round1(Level5($"[{fireBolt}]", $$"""[{{adept}}, { "kind": "extra_damage", "name": "Hex", "dice": "1d6", "type": "fire" }]"""), Ac15);
        var necrotic = Round1(Level5($"[{fireBolt}]", $$"""[{{adept}}, { "kind": "extra_damage", "name": "Hex", "dice": "1d6", "type": "necrotic" }]"""), Ac15);

        Assert.Equal(fire, untyped, Exact);
        Assert.True(untyped > necrotic + 0.05, $"{untyped} vs {necrotic}: the rider's 1s count as 2s only as fire");
    }

    [Fact]
    public void Evaluate_AttackActionOnlyBonusOnASpellAttack_AddsNothing()
    {
        // A spell attack made with the Action is the spell's casting action, not the Attack action (2024 "Attack [Action]: an
        // attack roll with a weapon or an Unarmed Strike"). Fire Bolt 2d10 at level 5: 0.6 × 11 + 0.05 × 22 = 7.70.
        const string fireBolt =
            """{ "name": "Fire Bolt", "to_hit": {"ability": "dex"}, "damage": "1d10", "damage_type": "fire", "ability_to_damage": false, "properties": ["ranged", "spell"], "cantrip": "dice" }""";

        var plain = Round1(Level5($"[{fireBolt}]"), Ac15);
        var flat = Round1(Level5($"[{fireBolt}]", """[{ "kind": "bonus_damage", "name": "GWM-ish", "amount": 3, "attack_action_only": true }]"""), Ac15);
        var rider = Round1(Level5($"[{fireBolt}]", """[{ "kind": "extra_damage", "name": "Only AA", "dice": "1d6", "attack_action_only": true }]"""), Ac15);
        var weapon = Round1(Level5($"[{Longsword}]", """[{ "kind": "bonus_damage", "name": "GWM-ish", "amount": 3, "attack_action_only": true }]"""), Ac15);

        Assert.Equal(7.70, plain, Exact);
        Assert.Equal(plain, flat, Exact);
        Assert.Equal(plain, rider, Exact);
        Assert.Equal(Round1(Level5($"[{Longsword}]"), Ac15) + (0.65 * 3), weapon, Exact); // weapons still get it
    }

    [Theory]
    [InlineData("stunned", "con")]
    [InlineData("paralyzed", "con")]
    [InlineData("blinded", "con")]
    public void Evaluate_2014ConditionOnHit_NotesThatItLastsOnlyTheTurnAndIsUnderstated(string condition, string ability)
    {
        var result = Evaluate(StunBuild(condition, ability, "2014"), Ac15, DprOptions.Fight(3));

        var note = Assert.Single(result.Notes, n => n.StartsWith($"Strike: the {condition} condition counts only for the rest of the turn it lands in", StringComparison.Ordinal));
        Assert.Contains("It lasts into your next turn", note, StringComparison.Ordinal);
        Assert.Contains(condition == "stunned" ? "(2014 Stunning Strike: until the end of your next turn)" : "the 2014 default", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_2024ConditionOnHit_NotesTheTurnOnlyWithoutClaimingItCarries()
    {
        // 2024 Stunning Strike lasts until the start of your next turn: for the monk's own attacks the reset is exact.
        var result = Evaluate(StunBuild("stunned", "con", "2024"), Ac15, DprOptions.Fight(3));
        var given = Evaluate(StunBuild("stunned", "con", "2024", ", \"duration\": \"end_of_next_turn\""), Ac15, DprOptions.Fight(3));

        var note = Assert.Single(result.Notes, n => n.StartsWith("Strike: the stunned condition counts only for the rest of the turn", StringComparison.Ordinal));
        Assert.DoesNotContain("lasts into your next turn", note, StringComparison.Ordinal);
        Assert.Contains(given.Notes, n => n.Contains("It lasts into your next turn (duration end_of_next_turn)", StringComparison.Ordinal));
        Assert.Equal(result.DamagePerRound, given.DamagePerRound, Exact); // duration is the simulator's; DPR ignores it
    }

    [Fact]
    public void Evaluate_ProneOrNoConditionOnHit_HasNoTurnOnlyNote()
    {
        var prone = Evaluate(StunBuild("prone", "str", "2014"), Ac15, DprOptions.Fight(3));
        var none = Evaluate(Level5($"[{Longsword2}]"), Ac15, DprOptions.Fight(3));

        Assert.DoesNotContain(prone.Notes, n => n.Contains("counts only for the rest of the turn", StringComparison.Ordinal));
        Assert.DoesNotContain(none.Notes, n => n.Contains("counts only for the rest of the turn", StringComparison.Ordinal));
    }

    private static string StunBuild(string condition, string ability, string edition, string extra = "") =>
        Level5($"[{Longsword2}]", $$"""[{ "kind": "condition_on_hit", "name": "Strike", "condition": "{{condition}}", "ability": "{{ability}}", "dc": 15{{extra}} }]""")
            .Replace("\"edition\": \"2024\"", $"\"edition\": \"{edition}\"", StringComparison.Ordinal);

    [Fact]
    public void Evaluate_ConditionEffectsAgainstLegendaryResistance_SayTheLandingChancesIgnoreIt()
    {
        // Con DC 15 vs +2: F = 0.6, L = 3 → (3 + 1) / 0.6 = 20/3 attempts, the parity of a save effect's casts to land.
        const string legendary = """{ "ac": 15, "saves": {"con": 2, "wis": 2}, "legendary_resistance": 3 }""";
        const string holdPerson = """{ "name": "Cleric", "level": 5, "abilities": {"wis": 18}, "modifiers": [{ "kind": "save_effect", "name": "Hold Person", "ability": "wis", "dc": 15, "condition": "paralyzed" }] }""";

        var stun = Evaluate(StunBuild("stunned", "con", "2024"), legendary, DprOptions.Fight(3));
        var hold = Evaluate(holdPerson, legendary, DprOptions.Fight(3));
        var stunPlain = Evaluate(StunBuild("stunned", "con", "2024"), """{ "ac": 15, "saves": {"con": 2} }""", DprOptions.Fight(3));

        Assert.Contains(stun.Notes, n => n.StartsWith("Strike: the landing chances and the stunned condition's effect on damage assume every failed save sticks: Legendary Resistance (3)", StringComparison.Ordinal));
        Assert.Contains(hold.Notes, n => n.StartsWith("Hold Person: the chances its paralyzed condition lands assume every failed save sticks: Legendary Resistance (3)", StringComparison.Ordinal));
        Assert.Equal(20.0 / 3, Assert.Single(stun.Conditions).ExpectedAttemptsToLand!.Value, Exact);
        Assert.DoesNotContain(stunPlain.Notes, n => n.Contains("Legendary Resistance", StringComparison.Ordinal));
        Assert.Null(Assert.Single(stunPlain.Conditions).ExpectedAttemptsToLand);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData(", \"cantrip\": true", false)]
    [InlineData(", \"resource\": {\"uses\": 2, \"per\": \"long_rest\"}", false)]
    public void Evaluate_SaveEffectWithoutAResource_NotesItIsUsedEveryRound(string extra, bool noted)
    {
        var build = $$"""{ "name": "Wizard", "level": 5, "abilities": {"int": 18}, "modifiers": [{ "kind": "save_effect", "name": "Fireball", "ability": "dex", "dc": 15, "dice": "8d6", "type": "fire", "shape": "sphere", "size": 20{{extra}} }] }""";

        var result = Evaluate(build, """{ "saves": {"dex": 2} }""", DprOptions.Fight(3));

        Assert.Equal(noted, result.Notes.Any(n => n.StartsWith("Fireball: no resource, so it is used every round of every fight", StringComparison.Ordinal)));
    }

    // Evasion (2024: "You don't benefit from this feature if you have the Incapacitated condition").
    private static string Fireball(string edition, string actionCost = "action", int targets = 1) =>
        $$"""{ "name": "Wizard", "edition": "{{edition}}", "level": 5, "modifiers": [{ "kind": "save_effect", "name": "Fireball", "ability": "dex", "dc": 15, "dice": "8d6", "type": "fire", "targets": {{targets}}, "action_cost": "{{actionCost}}", "resource": {"uses": 3, "per": "long_rest"} }] }""";

    [Theory]
    [InlineData("stunned")]
    [InlineData("paralyzed")]
    [InlineData("unconscious")]
    public void Evaluate_2024EvasionWhileIncapacitated_TakesFullDamage(string condition)
    {
        // The save auto-fails, and without Evasion a failure takes the whole 8d6: 28.
        var evasion = Evaluate(Fireball("2024"), $$"""{ "save_bonus": 2, "evasion": true, "condition": "{{condition}}", "hp": 20 }""");
        var none = Evaluate(Fireball("2024"), $$"""{ "save_bonus": 2, "condition": "{{condition}}", "hp": 20 }""");

        Assert.Equal(28, evasion.DamagePerRound, Exact);
        Assert.Equal(none.DamagePerRound, evasion.DamagePerRound, Exact);
        var report = Assert.Single(evasion.SaveEffects);
        Assert.Equal(28, report.DamagePerTarget, Exact);
        Assert.Equal(Assert.Single(none.SaveEffects).KillChanceEach!.Value, report.KillChanceEach!.Value, Exact);
        Assert.Equal(Assert.Single(none.SaveEffects).EffectiveDamage!.Value, report.EffectiveDamage!.Value, Exact);
        Assert.Contains(evasion.Notes, n => n.StartsWith("2024 Evasion: not while Incapacitated", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_2014EvasionWhileStunned_StillHalves()
    {
        // 2014 Evasion has no Incapacitated clause: a failed save takes half, E[⌊8d6/2⌋] = 13.75.
        var result = Evaluate(Fireball("2014"), """{ "save_bonus": 2, "evasion": true, "condition": "stunned" }""");

        Assert.Equal(13.75, result.DamagePerRound, Exact);
        Assert.DoesNotContain(result.Notes, n => n.StartsWith("2024 Evasion", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_2024EvasionWhileRestrained_StillApplies()
    {
        // Restrained is not Incapacitated: Disadvantage on the Dex save (fails 1 − 0.4² = 0.84), half on a failure, none on a
        // success: 0.84 × 13.75 = 11.55.
        var result = Evaluate(Fireball("2024"), """{ "save_bonus": 2, "evasion": true, "condition": "restrained" }""");

        Assert.Equal(0.84 * 13.75, result.DamagePerRound, Exact);
    }

    [Fact]
    public void Evaluate_2024EvasionOnATargetStunnedThisTurn_IsLostForTheLaterSave()
    {
        // Stunning Strike (DC 30 vs Con −5: always fails) on the Longsword (hits AC 1 but on a 1: 0.95), then a Bonus Action
        // 8d6 Dex-half effect (DC 15 vs +2: fails 0.6). Stunned, the save auto-fails: 28 without Evasion, and on a 2024
        // target also with it (Incapacitated), while a 2014 target still halves it (13.75). Not stunned (0.05), Evasion
        // gives 0.6 × 13.75 = 8.25 against 0.6 × 28 + 0.4 × 13.75 = 22.3 without.
        static string Build(string edition) =>
            $$"""
            { "name": "Monk", "edition": "{{edition}}", "level": 5, "abilities": {"str": 18},
              "attacks": [{{Longsword}}],
              "modifiers": [{ "kind": "condition_on_hit", "name": "Stunning Strike", "condition": "stunned", "ability": "con", "dc": 30 },
                            { "kind": "save_effect", "name": "Burst", "ability": "dex", "dc": 15, "dice": "8d6", "type": "fire", "action_cost": "bonus_action" }] }
            """;
        const string evasion = """{ "ac": 1, "saves": {"con": -5, "dex": 2}, "evasion": true }""";
        const string plain = """{ "ac": 1, "saves": {"con": -5, "dex": 2} }""";

        Assert.Equal(-0.05 * (22.3 - 8.25), Round1(Build("2024"), evasion) - Round1(Build("2024"), plain), Exact);
        Assert.Equal((-0.95 * (28 - 13.75)) - (0.05 * (22.3 - 8.25)), Round1(Build("2014"), evasion) - Round1(Build("2014"), plain), Exact);
    }

    // Dodge is lost once the target is Incapacitated or restrained; blinded loses only the attack Disadvantage.
    private static string DodgeBuild(string condition, string ability, int count = 2, string extra = "") =>
        $$"""
        { "name": "Test", "edition": "2024", "level": 5, "abilities": {"dex": 18},
          "attacks": [{ "name": "Blade", "count": {{count}}, "to_hit": {"ability": "dex"}, "damage": "1d8", "damage_type": "slashing", "properties": ["melee"] }],
          "modifiers": [{ "kind": "condition_on_hit", "name": "Hold", "condition": "{{condition}}", "ability": "{{ability}}", "dc": 40 }{{extra}}] }
        """;

    private const string Dodging = """{ "ac": 15, "save_bonus": 0, "condition": "dodging" }""";

    [Theory]
    [InlineData("stunned", "con", 9.0196375)] // 3.6025 + 0.4225 × 7.8975 + 0.5775 × 3.6025: the second swing has Advantage
    [InlineData("restrained", "str", 9.0196375)]
    [InlineData("blinded", "con", 9.0196375)] // a blinded dodger cannot see the attacker: its Disadvantage is gone
    [InlineData("paralyzed", "con", 10.5026125)] // 3.6025 + 0.4225 × (0.8775 × 13) + 0.5775 × 3.6025: melee auto-crits
    [InlineData("prone", "str", 8.11231875)] // Prone does not end Dodge: Advantage and Disadvantage cancel
    public void Evaluate_DodgingTargetGivenACondition_LosesDodgeWhereTheRulesSay(string condition, string ability, double expected)
    {
        Assert.Equal(expected, Round1(DodgeBuild(condition, ability), Dodging), Exact);
    }

    [Theory]
    [InlineData("restrained", "str", 9.2305)] // 3.6025 + 10 × (0.4225 × 0.84 + 0.5775 × 0.36): restrained ends Dodge, Dex Disadvantage
    [InlineData("blinded", "con", 7.2025)] // 3.6025 + 10 × 0.36: blinded keeps Dodge's Dex Advantage
    public void Evaluate_DodgingTargetsDexSave_FollowsTheConditionImposedThisTurn(string condition, string ability, double expected)
    {
        var build = DodgeBuild(condition, ability, count: 1,
            extra: """, { "kind": "save_effect", "name": "Push", "ability": "dex", "dc": 13, "amount": 10, "on_success": "none", "action_cost": "none" }""");

        Assert.Equal(expected, Round1(build, Dodging), Exact);
    }

    // The Light weapon's offhand attack needs the Attack action (2014 Two-Weapon Fighting, 2024 Light).
    private const string Shortsword2 =
        """{ "name": "Shortsword", "count": 2, "to_hit": {"ability": "dex"}, "damage": "1d6", "damage_type": "piercing", "properties": ["melee", "finesse", "light"] }""";

    private const string BlessSetup = """{ "kind": "to_hit", "name": "Bless", "dice": "1d4", "setup": "action" }""";

    [Fact]
    public void Evaluate_OffhandAfterASetupAction_IsNotMadeThatRound()
    {
        var round1 = Evaluate(Level5($"[{Shortsword2}, {Dagger}]", $"[{BlessSetup}]"), Ac15);
        var fight = Evaluate(Level5($"[{Shortsword2}, {Dagger}]", $"[{BlessSetup}]"), Ac15, DprOptions.Fight(3));

        Assert.Equal(0, round1.DamagePerRound, Exact);
        Assert.Contains(round1.Notes, n => n.Contains("Dagger (offhand) is not made in round 1: the Light weapon's extra attack needs the Attack action.", StringComparison.Ordinal));
        // Rounds 2 and 3: two blessed swings (0.725 × 7.5 + 0.05 × 11 each) and the blessed Dagger (0.725 × 2.5 + 0.05 × 5).
        Assert.Equal([0, 14.0375, 14.0375], fight.DamageByRound.ToArray(), (a, b) => Math.Abs(a - b) < Exact);
    }

    [Fact]
    public void Evaluate_OffhandAfterASetupActionWithActionSurge_IsMade()
    {
        // Action Surge is a second Attack action: 2 × 5.9875 + 2.0625 = 14.0375 in round 1.
        const string surge = """{ "kind": "extra_attack", "name": "Action Surge", "attack": "Shortsword", "count": 2, "action": "action", "resource": {"uses": 1, "per": "short_rest"} }""";

        var result = Evaluate(Level5($"[{Shortsword2}, {Dagger}]", $"[{BlessSetup}, {surge}]"), Ac15);

        Assert.Equal(14.0375, result.DamagePerRound, Exact);
        Assert.DoesNotContain(result.Notes, n => n.Contains("is not made in round 1", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_OffhandBesideAnActionSaveEffect_IsNeverMade()
    {
        var withDagger = Evaluate(Fireball("2024").Replace("\"modifiers\"", $"\"abilities\": {{\"dex\": 18}}, \"attacks\": [{Dagger}], \"modifiers\"", StringComparison.Ordinal), """{ "ac": 15, "save_bonus": 2 }""");
        var alone = Evaluate(Fireball("2024"), """{ "ac": 15, "save_bonus": 2 }""");

        Assert.Equal(alone.DamagePerRound, withDagger.DamagePerRound, Exact);
        Assert.Equal(0, withDagger.Attacks.Single(a => a.Attack == "Dagger").AttacksPerRound, Exact);
        Assert.Contains(withDagger.Notes, n => n.StartsWith("Dagger (offhand) is never made: the Light weapon's extra attack needs the Attack action, and this build's Action is Fireball.", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_OffhandOnly_DealsNothingAndSaysWhy()
    {
        var result = Evaluate(Level5($"[{Dagger}]"), Ac15);

        Assert.Equal(0, result.DamagePerRound, Exact);
        Assert.Contains(result.Notes, n => n.EndsWith("this build's Action makes no weapon attack.", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_SetupRoundWithAnOffhandAndAnotherBonusActionAttack_MakesOnlyTheOther()
    {
        const string spiritual = """{ "name": "Spiritual Weapon", "action": "bonus_action", "to_hit": {"ability": "dex"}, "damage": "1d8", "damage_type": "force", "ability_to_damage": false, "properties": ["spell"] }""";

        var result = Evaluate(Level5($"[{Shortsword2}, {Dagger}, {spiritual}]", $"[{BlessSetup}]"), Ac15);

        Assert.Equal(0, result.Attacks.Single(a => a.Attack == "Dagger").AttacksPerRound, Exact);
        Assert.Equal(1, result.Attacks.Single(a => a.Attack == "Spiritual Weapon").AttacksPerRound, Exact);
        Assert.Equal((0.725 * 4.5) + (0.05 * 9), result.DamagePerRound, Exact); // the blessed Spiritual Weapon alone
    }

    [Fact]
    public void Evaluate_NonOffhandBonusActionAttackBesideAnActionSaveEffect_IsStillMade()
    {
        const string spiritual = """{ "name": "Spiritual Weapon", "action": "bonus_action", "to_hit": {"ability": "wis"}, "damage": "1d8", "damage_type": "force", "properties": ["spell"] }""";

        var result = Evaluate(Fireball("2024").Replace("\"modifiers\"", $"\"abilities\": {{\"wis\": 18}}, \"attacks\": [{spiritual}], \"modifiers\"", StringComparison.Ordinal), """{ "ac": 15, "save_bonus": 2 }""");

        Assert.Equal(1, result.Attacks.Single(a => a.Attack == "Spiritual Weapon").AttacksPerRound, Exact);
    }

    // A condition imposed on the main target this turn changes ITS save, not every creature's in the area.
    private static string StunThenBurst(int targets, string condition = "") =>
        $$"""
        { "name": "Monk", "edition": "2024", "level": 5, "abilities": {"str": 18},
          "attacks": [{{Longsword2}}],
          "modifiers": [{ "kind": "condition_on_hit", "name": "Stunning Strike", "condition": "CONDITION", "ability": "con", "dc": 30 },
                        { "kind": "save_effect", "name": "Burst", "ability": "dex", "dc": 15, "dice": "4d6", "type": "fire", "targets": {{targets}}, "action_cost": "bonus_action"{{condition}} }] }
        """;

    private const string Crowd = """{ "ac": 15, "saves": {"con": -5, "dex": 2} }""";

    [Theory]
    // Stunned lands with P(at least one hit) 0.8775: then the main target auto-fails (14) and the three others fail 0.6
    // (11.1 each); otherwise all four at 0.6: 0.8775 × 47.3 + 0.1225 × 44.4 = 46.94475. Restrained: the main target's Dex
    // save at Disadvantage fails 0.84 (12.84): 0.8775 × (12.84 + 33.3) + 0.1225 × 44.4 = 45.92685. One target: 13.64475.
    [InlineData("stunned", 4, 46.94475)]
    [InlineData("restrained", 4, 45.92685)]
    [InlineData("stunned", 1, 13.64475)]
    public void Evaluate_AreaSaveAfterAConditionOnTheMainTarget_ChangesOnlyItsSave(string condition, int targets, double expected)
    {
        var result = Evaluate(StunThenBurst(targets).Replace("CONDITION", condition, StringComparison.Ordinal), Crowd);

        var burst = Assert.Single(result.SaveEffects);
        Assert.Equal(expected, burst.DamagePerRound, Exact);
        Assert.Equal(targets * 11.1, burst.RawDamage, Exact); // per cast against the target as given
        Assert.Equal(result.DamagePerRound, result.Round1Distribution!.Mean, 1e-9);
    }

    [Fact]
    public void Evaluate_AreaSaveWithAConditionAfterAStun_LandsOnTheMainTargetAndKeepsTheDistributionExact()
    {
        // Prone lands on the main target: always when it is stunned (0.8775), else 0.6: 0.951. The turn: the swings 5.75 +
        // 0.65 × 7.8975 (the second has Advantage on the stunned target) + 0.35 × 5.75 = 12.895875, plus the burst 46.94475.
        var result = Evaluate(StunThenBurst(4, ", \"condition\": \"prone\"").Replace("CONDITION", "stunned", StringComparison.Ordinal), Crowd);

        Assert.Equal(0.951, Assert.Single(result.SaveEffects).LandChancePerTurn!.Value, Exact);
        Assert.Equal(12.895875 + 46.94475, result.Round1Damage, Exact);
        Assert.Equal(result.Round1Damage, result.Round1Distribution!.Mean, 1e-9);
    }

    [Fact]
    public void Evaluate_CritOrKillTrigger_IsTheCritTriggerWithANote()
    {
        // §8.2's golden with the 2014 GWM bonus attack on "crit_or_kill": the closed form has no hit points, so 19.611625.
        var build = FeatureTestBuilds.Fighter2014GwmJson.Replace("\"trigger\": \"crit\"", "\"trigger\": \"crit_or_kill\"", StringComparison.Ordinal);

        var result = Evaluate(build, Ac15);

        Assert.Equal(19.611625, result.DamagePerRound, Exact);
        Assert.Contains(result.Notes, n => n.StartsWith("GWM bonus attack: reducing a creature to 0 HP also triggers it; the closed form has no hit points", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_HealAndMagicalProperty_ChangeNoDamage()
    {
        var plain = Evaluate(Level5($"[{Longsword2}]"), Ac15, DprOptions.Fight(3));
        var healer = Evaluate(
            Level5(
                """[{ "name": "Longsword", "count": 2, "damage": "1d8", "damage_type": "slashing", "properties": ["melee", "versatile", "magical", "silvered"] }]""",
                """[{ "kind": "heal", "name": "Second Wind", "dice": "1d10", "amount": {"1": 1, "5": 5}, "action_cost": "bonus_action", "self_only": true, "resource": {"uses": 2, "per": "short_rest"} }]"""),
            Ac15,
            DprOptions.Fight(3));

        Assert.Equal(plain.DamagePerRound, healer.DamagePerRound, Exact);
        Assert.Empty(healer.BonusActionChoices); // a heal never competes for the Bonus Action in the closed form
        Assert.Empty(healer.Resources);
        Assert.Contains(healer.Notes, n => n.StartsWith("Second Wind (heal) is healing: kept for the simulator", StringComparison.Ordinal));
    }
}
