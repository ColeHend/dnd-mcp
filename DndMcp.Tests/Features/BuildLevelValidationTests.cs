using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using Xunit;
using static DndMcp.Tests.Features.FeatureTestBuilds;

namespace DndMcp.Tests.Features;

/// <summary>
/// Invariant: the rules that hold per level are checked at every level that will be evaluated, before any is resolved,
/// and each problem is reported once, at its first failing level: step values must cover every level where their owner is
/// active, a turn must have something to do, one Concentration effect, one use of the Action, and the totals the engine
/// is sized for.
/// </summary>
public sealed class BuildLevelValidationTests
{
    private static string Problem(BuildSpec spec, params int[] levels) =>
        Assert.Throws<DndInputException>(() => BuildResolver.Validate(spec, levels)).Message;

    [Fact]
    public void Validate_StepMapStartingAboveAnActiveLevel_NamesFieldLevelAndFirstKey()
    {
        var spec = With("""[{ "name": "Greatsword", "count": {"5": 2}, "damage": "2d6" }]""");

        Assert.Equal(
            "Invalid build: attacks item 1 (Greatsword): count has no value at level 3: its step map starts at level 5. Add a key " +
            "\"3\" or lower (e.g. \"1\"), or give the attack from_level 5.",
            Problem(spec, 3));
        BuildResolver.Validate(spec, [5, 6, 20]);
    }

    [Fact]
    public void Validate_StepMapGap_IsReportedOnceAtTheFirstUncoveredLevel()
    {
        var message = Problem(With("""[{ "name": "A", "damage": {"4": "1d8"} }]"""), 1, 2, 3, 4);

        Assert.StartsWith("Invalid build: attacks item 1 (A): damage has no value at level 1:", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_StepMapOfAnInactiveOwner_IsNotRequiredBelowItsFromLevel()
    {
        BuildResolver.Validate(
            With("""[{ "name": "Dagger", "damage": "1d4" }, { "name": "Blade", "from_level": 5, "count": {"5": 1, "11": 2}, "damage": {"5": "2d6"} }]""",
                """[{ "kind": "to_hit", "amount": {"9": 1}, "from_level": 9 }]"""),
            [1, 2, 3, 4, 5, 9, 11, 20]);
    }

    [Theory]
    [InlineData("""{ "kind": "to_hit", "amount": {"9": 1} }""", "modifiers item 1 (to_hit): amount has no value at level 5: its step map starts at level 9. Add a key \"5\" or lower (e.g. \"1\"), or give the modifier from_level 9.")]
    [InlineData("""{ "kind": "extra_damage", "dice": {"11": "1d8"} }""", "modifiers item 1 (extra_damage): dice has no value at level 5")]
    [InlineData("""{ "kind": "crit_range", "min": {"15": 18} }""", "modifiers item 1 (crit_range): min has no value at level 5")]
    [InlineData("""{ "kind": "extra_attack", "attack": "Greatsword", "action": "bonus_action", "count": {"6": 1} }""", "count has no value at level 5")]
    [InlineData("""{ "kind": "extra_damage", "dice": "1d8", "resource": {"uses": {"9": 1}, "per": "long_rest"} }""", "modifiers item 1 (extra_damage): resource uses has no value at level 5")]
    public void Validate_ModifierStepValueUncovered_IsRefused(string modifier, string why)
    {
        Assert.Contains(why, Problem(WithModifier(modifier), 5), StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_AbilityStepMapUncovered_OffersNoFromLevel()
    {
        var message = Problem(With(extra: "\"abilities\": {\"cha\": {\"4\": 18}},"), 1, 4);

        Assert.Equal(
            "Invalid build: abilities cha has no value at level 1: its step map starts at level 4. Add a key \"1\" or lower (e.g. \"1\").",
            message);
    }

    [Fact]
    public void Validate_NothingToDoAtALevel_IsRefused()
    {
        var message = Problem(With("""[{ "name": "Blade", "damage": "2d6", "from_level": 5 }]"""), 3, 4, 5);

        Assert.Equal(
            "Invalid build: at level 3 no attack and no save_effect is active, so the build does nothing that turn; give one from " +
            "that level (see from_level and until_level) or evaluate other levels.",
            message);
    }

    [Fact]
    public void Validate_OnlyASaveEffect_IsEnough()
    {
        BuildResolver.Validate(Build(FireballJson), [1, 20]);
    }

    [Fact]
    public void Validate_DefaultLevels_IsTheBuildsOwnLevel()
    {
        var spec = With("""[{ "name": "Blade", "damage": "2d6", "from_level": 5 }]""");

        BuildResolver.Validate(spec);
        Assert.Throws<DndInputException>(() => BuildResolver.Validate(With("""[{ "name": "Blade", "damage": "2d6", "from_level": 6 }]""")));
    }

    [Fact]
    public void Validate_ExtraAttackOnAnInactiveAttack_IsRefused()
    {
        var message = Problem(With(
            """[{ "name": "Dagger", "damage": "1d4" }, { "name": "Blade", "damage": "2d6", "from_level": 5 }]""",
            """[{ "kind": "extra_attack", "attack": "Blade", "action": "bonus_action" }]"""), 3);

        Assert.Equal(
            "Invalid build: modifiers item 1 (extra_attack): at level 3 it makes \"Blade\", which is not active then (it has from_level 5); " +
            "give the modifier the same from_level/until_level.",
            message);
    }

    [Fact]
    public void Validate_ConditionOnHitDefaultDcWithAbilityNone_IsRefused()
    {
        var message = Problem(With(
            """[{ "name": "Trap", "to_hit": {"ability": "none"}, "damage": "1d4" }]""",
            """[{ "kind": "condition_on_hit", "condition": "prone", "ability": "str" }]"""), 5);

        Assert.Contains("its DC defaults to 8 + proficiency + the attack's ability, but \"Trap\" has to_hit ability none; give dc or dc_ability.", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_TwoConcentrationModifiersAtOnce_IsRefused()
    {
        var spec = With(modifiersJson: """
            [{ "kind": "to_hit", "name": "Bless", "dice": "1d4", "concentration": true, "until_level": 4 },
             { "kind": "extra_damage", "name": "Hunter's Mark", "dice": "1d6", "concentration": true, "from_level": 3 }]
            """);

        Assert.Equal(
            "Invalid build: at level 3 modifiers item 1 (to_hit \"Bless\") and modifiers item 2 (extra_damage \"Hunter's Mark\") need " +
            "Concentration, and a character concentrates on one effect at a time; compare them as two builds.",
            Problem(spec, 1, 2, 3, 4, 5));
        BuildResolver.Validate(spec, [1, 2, 5, 20]);
    }

    /// <summary>
    /// The one-routine refusal's fix is two builds; changing action_cost is offered only for an effect that really costs
    /// less, since a Fireball given "none" is cast for free beside the attacks every turn (review finding).
    /// </summary>
    private const string CheaperOnlyIfTrue =
        "Change an action_cost only if the effect really costs less: \"bonus_action\" for a Bonus Action effect (e.g. a Quickened " +
        "spell), \"none\" for one that takes no action on your turn (e.g. an aura already up); a spell cast with the Action, such as " +
        "Fireball, keeps \"action\". (balance_simulate takes both routines and picks one each turn.)";

    [Fact]
    public void Validate_ActionSaveEffectBesideAttackActionAttacks_IsRefused()
    {
        var message = Problem(WithModifier("""{ "kind": "save_effect", "name": "Fireball", "ability": "dex", "dc": 15, "dice": "8d6" }"""), 5);

        Assert.Equal(
            "Invalid build: at level 5 modifiers item 1 (save_effect \"Fireball\") uses the Action, and so does attacks item 1 " +
            "(Greatsword); a turn has one Action, so a build is one turn's routine: compare the two routines as two builds. " +
            CheaperOnlyIfTrue,
            message);
    }

    [Fact]
    public void Validate_TwoActionSaveEffects_IsRefused()
    {
        var message = Problem(Build("""
            { "name": "Caster", "level": 5,
              "modifiers": [{ "kind": "save_effect", "ability": "dex", "dc": 15, "dice": "8d6" },
                            { "kind": "save_effect", "ability": "wis", "dc": 15, "condition": "paralyzed" }] }
            """), 5);

        Assert.Equal(
            "Invalid build: at level 5 modifiers item 1 (save_effect) and modifiers item 2 (save_effect) both use the Action; a turn " +
            "has one Action, so a build is one turn's routine: compare them as two builds. " + CheaperOnlyIfTrue,
            message);
    }

    [Theory]
    [InlineData("""{ "kind": "save_effect", "name": "Fireball", "ability": "dex", "dc": 15, "dice": "8d6" }""")]
    [InlineData("""{ "kind": "save_effect", "name": "Fireball", "ability": "dex", "dc": 15, "dice": "8d6" }, { "kind": "save_effect", "ability": "wis", "dc": 15, "condition": "paralyzed" }""")]
    public void Validate_OneRoutineMessages_NeverOfferACheaperActionCostWithoutItsQualifier(string modifiers)
    {
        var message = Problem(WithModifier(modifiers), 5);

        // Every "none" and "bonus_action" the message offers sits inside the qualified sentence.
        var outside = message.Replace(CheaperOnlyIfTrue, string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("\"none\"", outside, StringComparison.Ordinal);
        Assert.DoesNotContain("\"bonus_action\"", outside, StringComparison.Ordinal);
        Assert.Contains(CheaperOnlyIfTrue, message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "kind": "save_effect", "name": "Fireball", "ability": "dex", "dc": 15, "dice": "8d6", "resource": {"uses": 2, "per": "long_rest"} }""")]
    [InlineData("""{ "kind": "save_effect", "name": "Fireball", "ability": "dex", "dc": 15, "dice": "8d6" }, { "kind": "save_effect", "name": "Hold Person", "ability": "wis", "dc": 15, "condition": "paralyzed" }""")]
    public void Validate_SimulationUse_AcceptsSeveralActionRoutines(string modifiers)
    {
        // The simulator chooses the Action each turn (Fireball while slots last, the Greatsword after), so for it a build
        // that holds both routines is normal; the DPR engine refuses it.
        var spec = WithModifier(modifiers);

        BuildResolver.Validate(spec, [5], use: BuildUse.Simulation);
        var resolved = BuildResolver.Resolve(spec, 5, use: BuildUse.Simulation);
        Assert.NotEmpty(resolved.SaveEffects);
        Assert.Throws<DndInputException>(() => BuildResolver.Validate(spec, [5]));
    }

    [Fact]
    public void Validate_SimulationUse_StillRefusesTheOtherPerLevelRules()
    {
        // Only the one-routine rule is relaxed: two Concentration effects, the attacks-per-turn total and step coverage
        // still hold for the simulator.
        var concentration = With(modifiersJson: """
            [{ "kind": "to_hit", "name": "Bless", "dice": "1d4", "concentration": true },
             { "kind": "extra_damage", "name": "Hunter's Mark", "dice": "1d6", "concentration": true }]
            """);
        var tooMany = With("""[{ "name": "A", "count": 10, "damage": "1d4" }, { "name": "B", "count": 10, "damage": "1d4" }]""",
            """[{ "kind": "extra_attack", "attack": "A", "action": "action", "count": 3, "resource": {"uses": 1, "per": "short_rest"} }]""");
        var uncovered = With("""[{ "name": "A", "count": {"5": 2}, "damage": "1d4" }]""");

        Assert.Contains("need Concentration", Assert.Throws<DndInputException>(() => BuildResolver.Validate(concentration, [5], use: BuildUse.Simulation)).Message, StringComparison.Ordinal);
        Assert.Contains("23 attacks a turn", Assert.Throws<DndInputException>(() => BuildResolver.Validate(tooMany, [5], use: BuildUse.Simulation)).Message, StringComparison.Ordinal);
        Assert.Contains("count has no value at level 1", Assert.Throws<DndInputException>(() => BuildResolver.Validate(uncovered, [1], use: BuildUse.Simulation)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ActionSaveEffectWithOnlyBonusActionAttacks_IsAccepted()
    {
        BuildResolver.Validate(Build("""
            { "name": "Caster", "level": 5,
              "attacks": [{ "name": "Spiritual Weapon", "action": "bonus_action", "to_hit": {"ability": "wis"}, "damage": "1d8", "properties": ["spell"] }],
              "modifiers": [{ "kind": "save_effect", "ability": "dex", "dc": 15, "dice": "8d6" }] }
            """), [5]);
    }

    [Fact]
    public void Validate_MoreThanTwentyAttacksATurn_IsRefused()
    {
        var message = Problem(With(
            """[{ "name": "A", "count": 10, "damage": "1d4" }, { "name": "B", "count": {"1": 5, "11": 10}, "damage": "1d4" }]""",
            """[{ "kind": "extra_attack", "attack": "A", "action": "action", "count": 3, "resource": {"uses": 1, "per": "short_rest"} }]"""), 5, 11);

        Assert.Equal(
            "Invalid build: at level 11 the build makes up to 23 attacks a turn (20 from attacks, 3 from extra_attack modifiers); at " +
            "most 20 are modelled.",
            message);
    }

    [Fact]
    public void Validate_BeamsCountTowardTheTotal()
    {
        var spec = With("""[{ "name": "Blast", "count": 6, "damage": "1d10", "cantrip": "beams" }]""");

        BuildResolver.Validate(spec, [10]);
        Assert.Contains("the build makes up to 24 attacks a turn", Problem(spec, 17), StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_FourResourceModifiers_IsRefused()
    {
        var modifiers = "[" + string.Join(",", Enumerable.Range(1, 4).Select(i =>
            $$"""{ "kind": "extra_damage", "name": "R{{i}}", "dice": "1d6", "when": "first_hit_per_turn", "resource": {"uses": 1, "per": "long_rest"} }""")) + "]";

        var message = Problem(With(modifiersJson: modifiers), 5);

        Assert.Contains("at level 5, 4 modifiers have a resource (modifiers item 1 (extra_damage \"R1\"), modifiers item 2 " +
                        "(extra_damage \"R2\"), modifiers item 3 (extra_damage \"R3\"), modifiers item 4 (extra_damage \"R4\")); at most 3 " +
                        "are accepted, because the engine tracks the uses left of each", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_FourRateAdvantageSources_IsRefusedButCertainOnesAreFree()
    {
        var rated = "[" + string.Join(",", Enumerable.Range(1, 4).Select(_ => """{ "kind": "advantage", "rate": 0.5 }""")) + "]";
        var certain = "[" + string.Join(",", Enumerable.Range(1, 4).Select(_ => """{ "kind": "advantage" }""")) + "]";

        Assert.Contains("are advantage sources with a rate below 1", Problem(With(modifiersJson: rated), 5), StringComparison.Ordinal);
        BuildResolver.Validate(With(modifiersJson: certain), [5]);
    }

    [Fact]
    public void Validate_FourPowerAttacks_IsRefused()
    {
        var modifiers = "[" + string.Join(",", Enumerable.Range(1, 4).Select(_ => """{ "kind": "power_attack" }""")) + "]";

        Assert.Contains("4 modifiers are power attacks", Problem(With(modifiersJson: modifiers), 5), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bonus_action", "Bonus Action")]
    [InlineData("action", "Action")]
    public void Validate_TwoSetupsOfOneCost_IsRefused(string cost, string what)
    {
        var message = Problem(With(modifiersJson: $$"""
            [{ "kind": "extra_damage", "name": "Hex", "dice": "1d6", "setup": "{{cost}}" },
             { "kind": "bonus_damage", "name": "Rage", "amount": 2, "setup": "{{cost}}" }]
            """), 5);

        Assert.Contains($"both have setup \"{cost}\", and the first round has one {what}; give one no setup", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_GreatWeaponFightingTwice_IsRefused()
    {
        var message = Problem(With(
            modifiersJson: """[{ "kind": "damage_die_remap", "remap": "gwf2024" }]""",
            extra: "\"fighting_style\": \"gwf\","), 5);

        Assert.Equal(
            "Invalid build: at level 5 attacks item 1 (Greatsword) gets Great Weapon Fighting twice (fighting_style gwf and modifiers " +
            "item 1 (damage_die_remap)); remove one.",
            message);
    }

    [Fact]
    public void Validate_LevelProblems_AreAllListedInOneException()
    {
        var message = Problem(With(
            """[{ "name": "A", "count": 12, "damage": "1d4" }, { "name": "B", "count": 10, "damage": "1d4" }]""",
            """
            [{ "kind": "to_hit", "dice": "1d4", "concentration": true },
             { "kind": "extra_damage", "dice": "1d6", "concentration": true }]
            """), 5);

        Assert.StartsWith("Invalid build: attacks item 1 (A): count is 12", message, StringComparison.Ordinal);

        var levelMessage = Problem(With(
            """[{ "name": "A", "count": 10, "damage": "1d4" }, { "name": "B", "count": 10, "damage": "1d4" }, { "name": "C", "damage": "1d4" }]""",
            """
            [{ "kind": "to_hit", "dice": "1d4", "concentration": true },
             { "kind": "extra_damage", "dice": "1d6", "concentration": true }]
            """), 5);
        Assert.StartsWith("Invalid build (2 problems):", levelMessage, StringComparison.Ordinal);
    }
}
