using DndMcp.Domain.Core;
using DndMcp.Domain.Dpr;
using DndMcp.Domain.Features;
using Xunit;
using static DndMcp.Tests.Dpr.DprTestKit;

namespace DndMcp.Tests.Dpr;

/// <summary>
/// Invariant: <c>balance_compare</c> measures Δ = variant − baseline under identical assumptions and turns it into
/// level-equivalents by the tier slope of the BASELINE's own curve (contract §4.6), falling back to RPGBOT's slope only
/// for the three stated reasons, each with a note; the §8 deltas (GWM, the ASI alternatives, Savage Attacker, Action
/// Surge over a day) come out of it through both the feature and the variant path; Bonus Action and Reaction
/// collisions are reported with the opportunity-cost wording; and a signature condition's landing chances per turn and
/// per fight are the hand arithmetic. Every expected value below is worked by hand in its comment.
/// </summary>
public sealed class DprComparisonTests
{
    private const double Exact = 1e-9;

    private const string Ac15 = """{ "ac": 15 }""";

    /// <summary>
    /// A greatsword fighter with Extra Attack at 5 and 11 and Str 16/18/20 at 1/4/8. Against AC 15: D(1) = 5.85 (+5: 0.50 ×
    /// 10 + 0.05 × 17), D(4) = 6.95 (+6: 0.55 × 11 + 0.05 × 18), D(10) = 18.7 (+9, two swings: 2 × (0.70 × 12 + 0.05 × 19)),
    /// D(16) = 29.85 (+10, three swings: 3 × (0.75 × 12 + 0.05 × 19)). Tier slopes: tier 1 (6.95 − 5.85) ÷ 3 = 11/30, tier 2
    /// (18.7 − 6.95) ÷ 6 = 47/24, tier 3 (29.85 − 18.7) ÷ 6 = 223/120.
    /// </summary>
    private const string Scaler = """
        { "name": "Scaler", "edition": "2024", "level": 5, "abilities": {"str": {"1": 16, "4": 18, "8": 20}},
          "attacks": [{ "name": "Greatsword", "count": {"1": 1, "5": 2, "11": 3}, "damage": "2d6", "damage_type": "slashing",
                        "properties": ["melee", "heavy", "two-handed"] }] }
        """;

    private static FeatureSpec Feature(string json) => DslJson.Deserialize<FeatureSpec>(json, "feature");

    private static ComparisonReport Compare(
        string baseline,
        string? feature = null,
        string? variant = null,
        string? target = Ac15,
        IReadOnlyList<int>? levels = null,
        string? horizon = null,
        double? encounters = null,
        int? shortRests = null,
        string? rulings = null) =>
        DprComparison.Compare(new CompareRequest
        {
            Baseline = Build(baseline),
            Feature = feature is null ? null : Feature(feature),
            Variant = variant is null ? null : Build(variant),
            Target = Target(target),
            Rulings = Rulings(rulings),
            Levels = levels,
            Horizon = horizon,
            EncountersPerDay = encounters,
            ShortRests = shortRests,
        });

    private static string ModifierFeature(string name, string modifiers) => $$"""{ "name": "{{name}}", "modifiers": [{{modifiers}}] }""";

    [Fact]
    public void Compare_Gwm2014AsAFeature_IsTheGoldenDeltaOnTheReferenceSlope()
    {
        // §8.2: GWM (PA auto + crit bonus attack) over GWF only, AC 15: 156893/8000 − 253/15 = 65879/24000 = +2.745.
        // The baseline does not change with level, so tier 2 divides by RPGBOT's (220 − 130) ÷ 12 ÷ 6 = 1.25 a level:
        // LE = 2.7449583 ÷ 1.25 = 2.196 → "2.2", Breaking.
        var report = Compare(GoldenBuilds.Fighter2014(), ModifierFeature("Great Weapon Master", GoldenBuilds.Gwm2014));

        var level = Assert.Single(report.Levels);
        Assert.Equal(253.0 / 15, level.Baseline.DamagePerRound, Exact);
        Assert.Equal(156893.0 / 8000, level.Variant.DamagePerRound, Exact);
        Assert.Equal(65879.0 / 24000, level.Delta, Exact);
        Assert.Equal(65879.0 / 24000 / (253.0 / 15), level.RelativeDelta!.Value, Exact);
        Assert.Equal(65879.0 / 24000 / 1.25, level.LevelEquivalent.Value, Exact);
        Assert.Equal("2.2", level.LevelEquivalent.Text);
        Assert.Equal(BalanceBands.Breaking, level.LevelEquivalent.Band);
        Assert.Equal("2014 L5 Fighter", report.BaselineName);
        Assert.Equal("2014 L5 Fighter + Great Weapon Master", report.VariantName);
        Assert.Equal("Great Weapon Master", report.FeatureName);

        var slope = Assert.Single(report.Slopes);
        Assert.Same(slope, level.LevelEquivalent.Slope);
        Assert.Equal((2, 5, 10, SlopeSources.RpgbotReference, 4, 10), (slope.Tier, slope.FirstLevel, slope.LastLevel, slope.Source, slope.FromLevel, slope.ToLevel));
        Assert.Equal(1.25, slope.PerLevel, Exact);
        Assert.Contains(
            "Level-equivalents in tier 2 (levels 5–10) divide by RPGBOT's reference slope, 1.25 DPR per level (the CR = level row's " +
            "maximum HP ÷ 12 from level 4 to 10), because the baseline does not change with level (no step values, from_level/until_level " +
            "or cantrip scaling), so its curve has no slope of its own.",
            report.Notes);
    }

    [Fact]
    public void Compare_Gwm2014AgainstTheAsiAsAVariant_IsTheGoldenDelta()
    {
        // §8.2: the feat over the Str 20 ASI: 156893/8000 − 39/2 = 893/8000 = +0.1116; LE 0.0893 → "0.089", On budget.
        var report = Compare(GoldenBuilds.Fighter2014(strength: 20), variant: GoldenBuilds.Fighter2014(GoldenBuilds.Gwm2014));

        var level = Assert.Single(report.Levels);
        Assert.Equal(893.0 / 8000, level.Delta, Exact);
        Assert.Equal("0.089", level.LevelEquivalent.Text);
        Assert.Equal(BalanceBands.OnBudget, level.LevelEquivalent.Band);
        Assert.Null(report.FeatureName);
    }

    [Theory]
    [InlineData(18, 1209.0 / 250)] // GWM + Str 19 over no feat (Str 18): 24.036 − 19.2 = +4.836
    [InlineData(20, 509.0 / 250)] // over the ASI (Str 20): 24.036 − 22 = +2.036
    public void Compare_Gwm2024_IsTheGoldenDelta(int baselineStrength, double delta)
    {
        // §8.3. The feature sets Str 19 (+1 from the feat) as FeatureSpec.abilities does: SET, not add.
        const string gwm = $$"""{ "name": "Great Weapon Master", "abilities": {"str": 19}, "modifiers": [{{GoldenBuilds.Gwm2024}}] }""";

        var report = Compare(GoldenBuilds.Fighter2024(strength: baselineStrength), gwm);

        Assert.Equal(delta, Assert.Single(report.Levels).Delta, Exact);
    }

    [Theory]
    [InlineData(null, 767.0 / 960)] // one set rerolled on a crit (the default)
    [InlineData("""{ "savage_attacker_on_crit_dice": true }""", 855619.0 / 1036800)] // the whole doubled set
    public void Compare_SavageAttacker_IsTheGoldenGainUnderEachRuling(string? rulings, double gain)
    {
        // §8.4 on build 3 at AC 15; both builds share the rulings.
        var report = Compare(GoldenBuilds.Fighter2024(GoldenBuilds.Gwm2024), ModifierFeature("Savage Attacker", GoldenBuilds.SavageAttacker), rulings: rulings);

        Assert.Equal(gain, Assert.Single(report.Levels).Delta, Exact);
    }

    [Theory]
    [InlineData(6, 62472773.0 / 19200000)] // 3 uses × 19.5227 ÷ (6 × 3): +3.254
    [InlineData(8, 62472773.0 / 25600000)] // 3 uses × 19.5227 ÷ (8 × 3): +2.440
    public void Compare_ActionSurgeOverADay_IsTheExactMarginalAmortized(int encounters, double delta)
    {
        // §8.12 (a PLAN correction): the value per use is the exact marginal of the turn with 4 swings vs 2, crit bonus
        // attack chance included (62472773/3200000 = 19.5227), not research's standalone 2 × 9.35.
        var report = Compare(GoldenBuilds.Fighter2014(GoldenBuilds.Gwm2014), ModifierFeature("Action Surge", GoldenBuilds.ActionSurge),
            horizon: "day", encounters: encounters, shortRests: 2);

        var level = Assert.Single(report.Levels);
        Assert.Equal(156893.0 / 8000, level.Baseline.DamagePerRound, Exact); // nothing limited: the day is the fight
        Assert.Equal(delta, level.Delta, Exact);
        Assert.Equal(62472773.0 / 3200000, Assert.Single(level.Variant.Day!.Features).DamagePerUse!.Value, Exact);
    }

    [Fact]
    public void Compare_Summaries_GiveTheDeltaOnEveryHorizon()
    {
        // On the fight headline the summaries still carry the day: its Δ is §8.12's +3.254 (E 6, S 2).
        var report = Compare(GoldenBuilds.Fighter2014(GoldenBuilds.Gwm2014), ModifierFeature("Action Surge", GoldenBuilds.ActionSurge),
            encounters: 6, shortRests: 2);

        Assert.Equal(62472773.0 / 19200000, report.VariantSummary.DayDamagePerRound - report.BaselineSummary.DayDamagePerRound, Exact);
        Assert.Equal(report.Detail.Delta, report.VariantSummary.FightDamagePerRound - report.BaselineSummary.FightDamagePerRound, Exact);
        Assert.True(report.VariantSummary.Round1Damage - report.BaselineSummary.Round1Damage > report.Detail.Delta, "a nova is worth most in round 1");
    }

    [Fact]
    public void Compare_ActionSurge2024OverADay_IsTheGolden()
    {
        // §8.12: build 3, S 1 → U 2, E 4: 2 × 23.94474 ÷ 12 = +3.99079 (research's 3.85 used the standalone swings).
        var report = Compare(GoldenBuilds.Fighter2024(GoldenBuilds.Gwm2024), ModifierFeature("Action Surge", GoldenBuilds.ActionSurge),
            horizon: "day", encounters: 4, shortRests: 1);

        Assert.Equal(399079.0 / 100000, Assert.Single(report.Levels).Delta, Exact);
    }

    [Theory]
    // Level 5 (tier 2, slope 47/24): +2 damage on two +7 swings (P 0.65) = 2.6 → 2.6 × 24/47 = 1.328 → "1.3" Breaking.
    [InlineData(5, """{ "kind": "bonus_damage", "amount": 2 }""", 2.6, 2.6 * 24 / 47, "1.3", BalanceBands.Breaking)]
    // +1 to hit: each swing's normal hits 0.60 → 0.65 of 11 damage: 2 × 0.05 × 11 = 1.1 → 0.5617 → "0.56" Over.
    [InlineData(5, """{ "kind": "to_hit", "amount": 1 }""", 1.1, 1.1 * 24 / 47, "0.56", BalanceBands.Over)]
    // +1 on the first hit of the turn: P(at least one hit in two) = 1 − 0.35² = 0.8775 → 0.4481 → "0.45" Creeping.
    [InlineData(5, """{ "kind": "extra_damage", "amount": 1, "when": "first_hit_per_turn" }""", 0.8775, 0.8775 * 24 / 47, "0.45", BalanceBands.Creeping)]
    // +1 on a crit: 2 × 0.05 × 1 = 0.1 → 0.0511 → "0.051" On budget.
    [InlineData(5, """{ "kind": "extra_damage", "amount": 1, "when": "on_crit" }""", 0.1, 0.1 * 24 / 47, "0.051", BalanceBands.OnBudget)]
    // −2 to hit: normal hits 0.60 → 0.50 on each swing: 2 × −0.10 × 11 = −2.2 → −1.123 → "-1.1" Under.
    [InlineData(5, """{ "kind": "to_hit", "amount": -2 }""", -2.2, -2.2 * 24 / 47, "-1.1", BalanceBands.Under)]
    // Level 3 (tier 1, slope 11/30): +2 damage on one +5 swing (P 0.55) = 1.1 → 1.1 × 30/11 = 3 → "3.0" Breaking.
    [InlineData(3, """{ "kind": "bonus_damage", "amount": 2 }""", 1.1, 3.0, "3.0", BalanceBands.Breaking)]
    // Level 11 (tier 3, slope 223/120): +2 damage on three +9 swings (P 0.75) = 4.5 → 540/223 = 2.42 → "2.4" Breaking.
    [InlineData(11, """{ "kind": "bonus_damage", "amount": 2 }""", 4.5, 540.0 / 223, "2.4", BalanceBands.Breaking)]
    public void Compare_ScalingBaseline_DividesByItsOwnTierSlope(int level, string modifier, double delta, double le, string text, string band)
    {
        var report = Compare(Scaler, ModifierFeature("Feature", modifier), levels: [level]);

        var compared = Assert.Single(report.Levels);
        Assert.Equal(delta, compared.Delta, Exact);
        Assert.Equal(le, compared.LevelEquivalent.Value, Exact);
        Assert.Equal(text, compared.LevelEquivalent.Text);
        Assert.Equal(band, compared.LevelEquivalent.Band);
        Assert.Equal(SlopeSources.Baseline, compared.LevelEquivalent.Slope.Source);
        Assert.Null(compared.LevelEquivalent.Slope.FallbackReason);
        Assert.DoesNotContain(report.Notes, n => n.StartsWith("Level-equivalents", StringComparison.Ordinal));
    }

    [Fact]
    public void Compare_SeveralLevels_ComputesEachTiersSlopeOnceFromTheBaselinesCurve()
    {
        var report = Compare(Scaler, ModifierFeature("Feature", """{ "kind": "bonus_damage", "amount": 2 }"""), levels: [5, 3, 11, 6]);

        Assert.Equal([3, 5, 6, 11], report.Levels.Select(l => l.Level));
        Assert.Equal(5, report.DetailLevel); // the baseline's own level
        Assert.Equal([1, 2, 3], report.Slopes.Select(s => s.Tier));
        Assert.Equal(
            [(1, 5.85, 4, 6.95), (4, 6.95, 10, 18.7), (10, 18.7, 16, 29.85)],
            report.Slopes.Select(s => (s.FromLevel, Math.Round(s.FromDamage, 9), s.ToLevel, Math.Round(s.ToDamage, 9))));
        Assert.Equal(11.0 / 30, report.Slopes[0].PerLevel, Exact);
        Assert.Equal(47.0 / 24, report.Slopes[1].PerLevel, Exact);
        Assert.Equal(223.0 / 120, report.Slopes[2].PerLevel, Exact);
        Assert.Same(report.Levels[1].LevelEquivalent.Slope, report.Levels[2].LevelEquivalent.Slope);

        // Only the detail level carries the round-1 distributions; the slope's own levels (1, 4, 10, 16) are extra runs.
        Assert.All(report.Levels, l => Assert.Equal(l.Level == 5, l.Baseline.Evaluation.Round1Distribution is not null && l.Variant.Evaluation.Round1Distribution is not null));
        Assert.Equal((2 * 4) + 4, report.Runs);
    }

    [Fact]
    public void Compare_BaselineInvalidAtASlopeLevel_FallsBackAndSaysWhy()
    {
        // Its greatsword first exists at level 5, so the tier-2 slope's level 4 cannot be evaluated.
        const string late = """
            { "name": "Late", "level": 5, "abilities": {"str": {"1": 16, "8": 20}},
              "attacks": [{ "name": "Greatsword", "count": {"5": 2}, "damage": "2d6", "damage_type": "slashing", "properties": ["melee"] }] }
            """;

        var report = Compare(late, ModifierFeature("Feature", """{ "kind": "bonus_damage", "amount": 2 }"""));

        var slope = Assert.Single(report.Slopes);
        Assert.Equal(SlopeSources.RpgbotReference, slope.Source);
        Assert.StartsWith("the baseline cannot be evaluated at level 4: attacks item 1 (Greatsword):", slope.FallbackReason, StringComparison.Ordinal);
        Assert.Contains(report.Notes, n => n.StartsWith("Level-equivalents in tier 2", StringComparison.Ordinal) && n.Contains("cannot be evaluated at level 4", StringComparison.Ordinal));
        Assert.Equal(2.4 / 1.25, Assert.Single(report.Levels).LevelEquivalent.Value, Exact); // two +6 swings (P 0.60) × 2
    }

    [Fact]
    public void Compare_FlatBaselineCurve_FallsBackAndSaysWhy()
    {
        // A fixed +5 crossbow with no ability damage deals 0.50 × 4.5 + 0.05 × 9 = 2.7 at every level against AC 15; a shield
        // from level 2 makes the build "scale" without changing its damage, so the own slope is 0.
        const string flat = """
            { "name": "Flat", "level": 5,
              "attacks": [{ "name": "Crossbow", "to_hit": {"total": 5}, "damage": "1d8", "ability_to_damage": false, "damage_type": "piercing", "properties": ["ranged"] }],
              "modifiers": [{ "kind": "ac", "name": "Shield", "amount": 2, "from_level": 2 }] }
            """;

        var report = Compare(flat, ModifierFeature("Feature", """{ "kind": "bonus_damage", "amount": 1 }"""));

        var slope = Assert.Single(report.Slopes);
        Assert.Equal(SlopeSources.RpgbotReference, slope.Source);
        Assert.Equal("the baseline's own slope from level 4 to 10 is 0 DPR per level (2.7 → 2.7), below 0.05", slope.FallbackReason);
        Assert.Equal(0.55 / 1.25, Assert.Single(report.Levels).LevelEquivalent.Value, Exact);
    }

    [Fact]
    public void Compare_IdenticalBuilds_IsExactlyNothing()
    {
        // A defensive modifier changes no damage dealt: Δ is 0 to the engine's precision and reads as 0, On budget.
        var report = Compare(Scaler, ModifierFeature("Tough", """{ "kind": "temp_hp", "amount": 5 }"""));

        var level = Assert.Single(report.Levels);
        Assert.Equal(0, level.Delta);
        Assert.Equal(0, level.RelativeDelta);
        Assert.Equal("0", level.LevelEquivalent.Text);
        Assert.Equal(BalanceBands.OnBudget, level.LevelEquivalent.Band);
        Assert.Equal(0, DprComparison.Delta(10, 10 + 5e-9));
        Assert.Equal(1e-6, DprComparison.Delta(10, 10 + 1e-6), 1e-15);
    }

    private const string DualWielder = """
        { "name": "Dual wielder", "level": 5, "abilities": {"str": 18},
          "attacks": [
            { "name": "Shortsword", "count": 2, "damage": "1d6", "damage_type": "piercing", "properties": ["melee", "light"] },
            { "name": "Offhand Shortsword", "action": "bonus_action", "offhand": true, "damage": "1d6", "damage_type": "piercing", "properties": ["melee", "light"] } ]
          MODIFIERS }
        """;

    [Fact]
    public void Compare_FeatureAddingABonusActionWhereOneWasUsed_ReportsTheOpportunityCost()
    {
        var feature = ModifierFeature("Follow-up", """{ "kind": "extra_attack", "name": "Crit follow-up", "attack": "Shortsword", "action": "bonus_action", "trigger": "crit" }""");

        var report = Compare(DualWielder.Replace("MODIFIERS", "", StringComparison.Ordinal), feature);

        Assert.Equal(ActionSlots.BonusAction, report.BonusAction.Slot);
        Assert.Equal(["Offhand Shortsword"], report.BonusAction.Baseline);
        Assert.Equal(["Offhand Shortsword", "Crit follow-up"], report.BonusAction.Variant);
        Assert.Equal(["Crit follow-up"], report.BonusAction.Added);
        Assert.True(report.BonusAction.OpportunityCost);
        Assert.Contains(
            "Bonus Action: the variant adds Crit follow-up where the baseline already uses Offhand Shortsword; opportunity cost: the " +
            "engine picks the better use each turn, so Δ is net.",
            report.Notes);

        // Net: after a crit (P 1 − 0.95² = 0.0975) the follow-up (1d6+4: 0.60 × 7.5 + 0.05 × 11 = 5.05) replaces the offhand
        // swing (no Str to damage: 0.60 × 3.5 + 0.05 × 7 = 2.45), so Δ = 0.0975 × (5.05 − 2.45) = 0.2535, not its own 0.0975 × 5.05.
        var level = Assert.Single(report.Levels);
        Assert.Equal(0.2535, level.Delta, Exact);
        Assert.Equal(
            [("bonus_action attacks (Offhand Shortsword)", 0.9025), ("Crit follow-up", 0.0975), ("none", 0.0)],
            level.Variant.Evaluation.BonusActionChoices.Select(c => (c.Option, Math.Round(c.ChosenPerRound, 12))));
        Assert.False(report.Reaction.OpportunityCost);
        Assert.Empty(report.Reaction.Variant);
    }

    [Fact]
    public void Compare_FirstReactionUse_IsListedWithoutAnOpportunityCost()
    {
        var feature = ModifierFeature("Sentinel", """{ "kind": "extra_attack", "name": "Opportunity Attack", "attack": "Shortsword", "action": "reaction", "trigger_probability": 0.5 }""");

        var report = Compare(DualWielder.Replace("MODIFIERS", "", StringComparison.Ordinal), feature);

        Assert.Empty(report.Reaction.Baseline);
        Assert.Equal(["Opportunity Attack"], report.Reaction.Added);
        Assert.False(report.Reaction.OpportunityCost);
        Assert.DoesNotContain(report.Notes, n => n.StartsWith("Reaction:", StringComparison.Ordinal));
        Assert.Equal(0.5 * 5.05, Assert.Single(report.Levels).Delta, Exact); // half the rounds, one 1d6+4 swing
    }

    [Fact]
    public void Compare_SecondReactionUse_IsAnOpportunityCost()
    {
        var baseline = DualWielder.Replace(
            "MODIFIERS",
            """, "modifiers": [{ "kind": "extra_attack", "name": "Opportunity Attack", "attack": "Shortsword", "action": "reaction", "trigger_probability": 0.3 }]""",
            StringComparison.Ordinal);
        var feature = ModifierFeature("Sentinel", """{ "kind": "extra_attack", "name": "Sentinel", "attack": "Shortsword", "action": "reaction", "trigger_probability": 0.6 }""");

        var report = Compare(baseline, feature);

        Assert.True(report.Reaction.OpportunityCost);
        Assert.Contains(
            "Reaction: the variant adds Sentinel where the baseline already uses Opportunity Attack; opportunity cost: the engine picks " +
            "the better use each turn, so Δ is net.",
            report.Notes);
    }

    private const string Monk = """
        { "name": "Monk", "level": 5, "abilities": {"str": 18},
          "attacks": [{ "name": "Unarmed Strike", "damage": "1d8", "damage_type": "bludgeoning" }] }
        """;

    [Theory]
    [InlineData("fight", 2)] // each build's fight run serves the headline, the summary and the landing chances
    [InlineData("round1", 4)] // one more fight each, shared by the summary and the landing chances
    [InlineData("day", 2)] // nothing limited: the day is the fight
    public void Compare_SignatureCondition_LandsPerTurnAndPerFightByHand(string horizon, int runs)
    {
        // One +7 swing vs AC 15 (0.65), then Con DC 15 vs +2 (fails 0.6): stunned in 0.65 × 0.6 = 0.39 of turns. Turns are
        // independent (the condition lasts the turn), so over 3 rounds: 1 − 0.61³ = 0.773019. On every horizon it is measured
        // over a fight of the call's rounds.
        var feature = ModifierFeature("Stunning Strike", """{ "kind": "condition_on_hit", "name": "Stunning Strike", "condition": "stunned", "ability": "con", "dc": 15 }""");

        var report = Compare(Monk, feature, target: """{ "ac": 15, "saves": {"con": 2} }""", horizon: horizon);

        var stun = Assert.Single(report.SignatureEffects);
        Assert.Equal(("Stunning Strike", "condition_on_hit", "stunned", true, 3), (stun.Name, stun.Kind, stun.Condition, stun.New, stun.Rounds));
        Assert.Equal(0.39, stun.LandChancePerTurn, Exact);
        Assert.Equal(1 - (0.61 * 0.61 * 0.61), stun.LandChancePerFight, Exact);
        Assert.Empty(report.BaselineSignatureEffects);
        Assert.Equal(runs, report.Runs);
    }

    [Fact]
    public void Compare_SignatureSaveEffect_ComparedWithTheBaselines()
    {
        // Both cast Hold Person (Wis DC 15 vs +2: F 0.6); the variant's DC is 17 (F 0.7). Over 3 rounds: 1 − 0.4³ = 0.936 and
        // 1 − 0.3³ = 0.973. Same name and kind in both, so it is not new.
        static string Caster(int dc) => $$"""
            { "name": "Cleric", "level": 5, "abilities": {"wis": 18},
              "modifiers": [{ "kind": "save_effect", "name": "Hold Person", "ability": "wis", "dc": {{dc}}, "condition": "paralyzed" }] }
            """;

        var report = Compare(Caster(15), variant: Caster(17), target: """{ "saves": {"wis": 2} }""");

        var before = Assert.Single(report.BaselineSignatureEffects);
        var after = Assert.Single(report.SignatureEffects);
        Assert.Equal((0.6, 0.936), (Math.Round(before.LandChancePerTurn, 12), Math.Round(before.LandChancePerFight, 12)));
        Assert.Equal((0.7, 0.973), (Math.Round(after.LandChancePerTurn, 12), Math.Round(after.LandChancePerFight, 12)));
        Assert.False(after.New);
        Assert.Equal("save_effect", after.Kind);
    }

    [Fact]
    public void Compare_NeitherVariantNorFeature_SaysGiveOne()
    {
        var error = Assert.Throws<DndInputException>(() => Compare(Scaler));

        Assert.Equal(
            "Give exactly one of variant (the whole changed build) or feature (only what to add to the baseline), e.g. \"feature\": " +
            "{\"name\": \"Great Weapon Master\", \"modifiers\": [{\"kind\": \"bonus_damage\", \"amount\": \"pb\", \"attack_action_only\": true}]}.",
            error.Message);
    }

    [Fact]
    public void Compare_BothVariantAndFeature_SaysGiveOne()
    {
        var error = Assert.Throws<DndInputException>(() => Compare(Scaler, ModifierFeature("F", """{ "kind": "to_hit", "amount": 1 }"""), variant: Scaler));

        Assert.Equal("Give variant or feature, not both: variant is the whole changed build, feature only what to add to the baseline.", error.Message);
    }

    [Fact]
    public void Compare_EachBuildsMistake_IsReportedAsItsOwn()
    {
        var badBaseline = Assert.Throws<DndInputException>(() => Compare(Scaler.Replace("\"2d6\"", "\"2d6kh1\"", StringComparison.Ordinal), ModifierFeature("F", """{ "kind": "to_hit", "amount": 1 }""")));
        var badFeature = Assert.Throws<DndInputException>(() => Compare(Scaler, ModifierFeature("F", """{ "kind": "to_hit", "amount": 1, "type": "fire" }""")));
        var badVariant = Assert.Throws<DndInputException>(() => Compare(Scaler, variant: Scaler.Replace("\"2d6\"", "\"2d6!\"", StringComparison.Ordinal)));

        Assert.StartsWith("Invalid baseline: attacks item 1 (Greatsword):", badBaseline.Message, StringComparison.Ordinal);
        Assert.StartsWith("Invalid feature: modifiers item 1 (to_hit):", badFeature.Message, StringComparison.Ordinal);
        Assert.StartsWith("Invalid variant: attacks item 1 (Greatsword):", badVariant.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_MismatchedLevelAndEdition_AreNoted()
    {
        var variant = GoldenBuilds.Fighter2024(GoldenBuilds.Gwm2024).Replace("\"level\": 5", "\"level\": 6", StringComparison.Ordinal);

        var report = Compare(GoldenBuilds.Fighter2014(), variant: variant);

        Assert.Equal([5], report.Levels.Select(l => l.Level));
        Assert.Contains(
            "The baseline follows the 2014 rules and the variant the 2024 rules, so Δ includes the edition change, not only the difference between the builds.",
            report.Notes);
        Assert.Contains(
            "The variant says level 6 and the baseline level 5; both are compared at level 5, the baseline's (give levels to compare at others).",
            report.Notes);
        Assert.DoesNotContain(Compare(GoldenBuilds.Fighter2014(), variant: variant, levels: [5]).Notes, n => n.StartsWith("The variant says", StringComparison.Ordinal));
    }

    [Fact]
    public void Compare_BudgetExceeded_NamesTheComparison()
    {
        var request = new CompareRequest
        {
            Baseline = Build(Scaler),
            Feature = Feature(ModifierFeature("F", """{ "kind": "to_hit", "amount": 1 }""")),
            WorkBudget = 50,
        };

        var error = Assert.Throws<DndInputException>(() => DprComparison.Compare(request));

        Assert.StartsWith(
            "this request is too large to compute exactly: comparing two builds at 1 level on the fight horizon needs more work than one call may do",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_CancelledToken_Stops()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var request = new CompareRequest { Baseline = Build(Scaler), Feature = Feature(ModifierFeature("F", """{ "kind": "to_hit", "amount": 1 }""")) };

        Assert.ThrowsAny<OperationCanceledException>(() => DprComparison.Compare(request, cancellation.Token));
    }
}
