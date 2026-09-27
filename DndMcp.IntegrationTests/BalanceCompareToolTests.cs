using DndMcp.Domain.Dpr;
using DndMcp.Domain.Features;
using DndMcp.IntegrationTests.Infrastructure;
using Xunit;
using static DndMcp.IntegrationTests.BalanceDprToolTests;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: <c>balance_compare</c> through the real client measures a feature as the contract's worked examples do
/// (§8.2–8.4, 8.12), whether it is given as a feature merged into the baseline or as a whole variant, and never prints a
/// balance band without the level-equivalent it was read from, the band's bounds and the slope behind it (with that
/// slope's source). Collisions over the Bonus Action or Reaction and the chance a condition lands are reported where they
/// apply, once.
///
/// <para>
/// The delta, slope and band arithmetic is pinned in DndMcp.Tests. Where a figure depends on that arithmetic rather than a
/// published golden (a level-equivalent from a baseline's own slope), the expectation is computed by the Domain here, so
/// these tests pin that the tool passes it through, not the numbers twice.
/// </para>
/// </summary>
public sealed class BalanceCompareToolTests : IClassFixture<McpServerHarness>
{
    private const string Prefix = "An error occurred invoking 'balance_compare': ";

    private const string SavageAttacker = """{"name": "Savage Attacker", "modifiers": [{"kind": "reroll_damage_take_best", "name": "Savage Attacker"}]}""";

    /// <summary>A fighter whose curve scales: Extra Attack at 5, 11 and 20, Str 16/18/20 at 1/4/8.</summary>
    private const string ScalingFighter = """
        {"name": "Fighter", "edition": "2024", "level": 5, "abilities": {"str": {"1": 16, "4": 18, "8": 20}}, "fighting_style": "gwf",
         "attacks": [{"name": "Greatsword", "count": {"1": 1, "5": 2, "11": 3, "20": 4}, "damage": "2d6", "damage_type": "slashing",
                      "properties": ["melee", "heavy", "two-handed"], "mastery": "graze"}]}
        """;

    private readonly McpServerHarness _server;

    public BalanceCompareToolTests(McpServerHarness server)
    {
        _server = server;
    }

    private async Task<string> Success(string arguments) =>
        _server.SuccessText(await _server.CallToolJsonAsync("balance_compare", arguments));

    private async Task<string> Error(string arguments) =>
        _server.ErrorText(await _server.CallToolJsonAsync("balance_compare", arguments));

    [Theory]
    // §8.4 (a PLAN correction): +767/960 = 0.798958 by default, +0.825250 with savage_attacker_on_crit_dice. The baseline does
    // not scale, so the level-equivalent divides by RPGBOT's tier 2 slope, 1.25: 0.64 and 0.66, both Over.
    [InlineData(null, "+0.80", "+3.3%", "24.83", "0.64", "savage_attacker_on_crit_dice = false")]
    [InlineData("""{"savage_attacker_on_crit_dice": true}""", "+0.83", "+3.4%", "24.86", "0.66", "savage_attacker_on_crit_dice = true")]
    public async Task CallTool_SavageAttackerOnThe2024Fighter_IsTheGoldenUnderEachRuling(
        string? rulings, string delta, string percent, string variant, string levelEquivalent, string echoed)
    {
        var text = await Success(
            $$"""{"baseline": {{Fighter2024()}}, "feature": {{SavageAttacker}}, "target": {"ac": 15}, "horizon": "round1"{{(rulings is null ? "" : $", \"rulings\": {rulings}")}}}""");

        Assert.StartsWith(
            "# Build comparison: 2024 L5 Fighter + Savage Attacker vs 2024 L5 Fighter\n\n" +
            $"Adding Savage Attacker to 2024 L5 Fighter: **{delta}** damage per round ({percent}; 24.04 → {variant}) at level 5 against AC 15 — " +
            $"round 1 (the nova). That is a level-equivalent of **{levelEquivalent}**: **Over** (0.5 ≤ LE < 1.0).",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "- Tier 2 (levels 5–10): 1.25 damage per round per level, from RPGBOT's reference curve (the CR = level row's maximum HP ÷ 12): " +
            "level 4 10.83 → level 10 18.33, because the baseline does not change with level",
            text,
            StringComparison.Ordinal);
        Assert.Contains(echoed, text, StringComparison.Ordinal);
        Assert.Contains("| Savage Attacker | once per turn, a weapon hit, policy any_hit | 0.8775 |", text, StringComparison.Ordinal);

        // The fallback's reason is on the slope's line; the Domain's note saying the same is not printed again.
        Assert.DoesNotContain("Level-equivalents in tier 2", text, StringComparison.Ordinal);
        Assert.Contains("reference curves: RPGBOT's DPR target", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_2014GreatWeaponMasterAsAWholeVariant_IsTheGoldenDelta()
    {
        // §8.2: GWM Δ vs no feat +2.745 (65879/24000), given as the whole changed build.
        var text = await Success(
            $$"""{"baseline": {{Fighter2014("")}}, "variant": {{Fighter2014()}}, "target": {"ac": 15}, "horizon": "round1"}""");

        Assert.Contains("2014 L5 Fighter instead of 2014 L5 Fighter: **+2.74** damage per round (+16.3%; 16.87 → 19.61)", text, StringComparison.Ordinal);
        Assert.Contains("## Variant at level 5: 2014 L5 Fighter", text, StringComparison.Ordinal);
        Assert.Contains("- Great Weapon Master (power_attack): −5 to hit, +10 damage, policy auto.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_2024GreatWeaponMasterAsAFeatureThatSetsStrength_IsTheGoldenDelta()
    {
        // §8.3: the feat (+1 Str 18 → 19, +PB on Attack-action hits, Hew) over no feat: 24.036 − 19.2 = +4.836.
        const string feature = """
            {"name": "Great Weapon Master", "abilities": {"str": 19}, "modifiers": [
              {"kind": "bonus_damage", "name": "Great Weapon Master", "amount": "pb", "attack_action_only": true},
              {"kind": "extra_attack", "name": "Hew", "attack": "Greatsword", "action": "bonus_action", "trigger": "crit"}]}
            """;

        var text = await Success($$"""{"baseline": {{Fighter2024("", strength: 18)}}, "feature": {{feature}}, "target": {"ac": 15}, "horizon": "round1"}""");

        Assert.Contains("Adding Great Weapon Master to 2024 L5 Fighter: **+4.84** damage per round (+25.2%; 19.20 → 24.04)", text, StringComparison.Ordinal);
        Assert.Contains("Proficiency bonus +3; Str 19 (+4)", text, StringComparison.Ordinal);
        Assert.Contains("- Bonus Action: baseline none; variant Hew (Hew added).", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_ScalingBaseline_UsesItsOwnSlopeAndPrintsTheDomainsLevelEquivalent()
    {
        const string feature = """{"name": "+1 weapon", "modifiers": [{"kind": "to_hit", "name": "+1 weapon", "amount": 1}]}""";
        var expected = DprComparison.Compare(new CompareRequest
        {
            Baseline = DslJson.Deserialize<BuildSpec>(ScalingFighter, "baseline"),
            Feature = DslJson.Deserialize<FeatureSpec>(feature, "feature"),
            Levels = [4, 5, 11],
        });

        var text = await Success($$"""{"baseline": {{ScalingFighter}}, "feature": {{feature}}, "levels": [4, 5, 11]}""");

        var detail = expected.Detail;
        Assert.Equal(5, detail.Level);
        Assert.Contains(
            $"That is a level-equivalent of **{Le(detail.LevelEquivalent)}**: **{BalanceBands.Display(detail.LevelEquivalent.Band)}**",
            text,
            StringComparison.Ordinal);
        Assert.All(expected.Slopes, slope => Assert.Equal(SlopeSources.Baseline, slope.Source));
        Assert.Contains("damage per round per level, from the baseline's own curve: level 4 ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("reference curves: RPGBOT", text, StringComparison.Ordinal);
        Assert.Contains("## By level, a 3-round fight (the mean per round)", text, StringComparison.Ordinal);
        foreach (var level in expected.Levels)
        {
            var label = level.Level == 5 ? "**5**" : level.Level.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.Contains($"| {label} | ", text, StringComparison.Ordinal);
            Assert.Contains($" | {Le(level.LevelEquivalent)} | {BalanceBands.Display(level.LevelEquivalent.Band)} |", text, StringComparison.Ordinal);
        }
    }

    [Theory]
    // The non-scaling §8.2 fighter without the feat, round 1 vs AC 15 (two swings, P(hit) 0.65, P(crit) 0.05), against
    // RPGBOT's tier 2 slope of 1.25: −1 a hit is Δ −1.3 (LE −1.0); +1 on a crit Δ 0.1 (0.08); +1d6 on a crit Δ 0.35 (0.28);
    // +1 on a miss Δ 0.7 (0.56); +2 a hit Δ 2.6 (2.1).
    [InlineData("""{"kind": "bonus_damage", "name": "Blunt", "amount": -1}""", "−1.0", "under", "LE ≤ −0.25")]
    [InlineData("""{"kind": "extra_damage", "name": "Sting", "amount": 1, "when": "on_crit"}""", "0.080", "on_budget", "−0.25 < LE < 0.25")]
    [InlineData("""{"kind": "extra_damage", "name": "Brutal", "dice": "1d6", "when": "on_crit"}""", "0.28", "creeping", "0.25 ≤ LE < 0.5")]
    [InlineData("""{"kind": "extra_damage", "name": "Grit", "amount": 1, "when": "on_miss"}""", "0.56", "over", "0.5 ≤ LE < 1.0")]
    [InlineData("""{"kind": "bonus_damage", "name": "Big", "amount": 2}""", "2.1", "breaking", "LE ≥ 1.0")]
    public async Task CallTool_EveryBand_IsPrintedWithItsLevelEquivalentBoundsAndTheScale(string modifier, string levelEquivalent, string band, string bounds)
    {
        var text = await Success(
            $$"""{"baseline": {{Fighter2014("")}}, "feature": {"name": "Test", "modifiers": [{{modifier}}]}, "target": {"ac": 15}, "horizon": "round1"}""");

        Assert.Contains(
            $"That is a level-equivalent of **{levelEquivalent}**: **{BalanceBands.Display(band)}** ({bounds}). The bands (the homebrew-balance " +
            "scale): Under ≤ −0.25 < On budget < 0.25 ≤ Creeping < 0.5 ≤ Over < 1.0 ≤ Breaking.",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_ActionSurge_ShowsTheDeltaOnEveryHorizon()
    {
        // §8.12's Action Surge on the §8.2 fighter: +19.52 in round 1 (four swings instead of two, and the bigger crit-attack
        // chance), +6.51 over a 3-round fight, +2.79 over the 2014 DMG day (3 uses × 19.52 ÷ 21 rounds).
        const string feature = """
            {"name": "Action Surge", "modifiers": [{"kind": "extra_attack", "name": "Action Surge", "attack": "Greatsword", "count": 2, "action": "action", "resource": {"uses": 1, "per": "short_rest"}}]}
            """;

        var text = await Success($$$"""{"baseline": {{{Fighter2014()}}}, "feature": {{{feature}}}, "target": {"ac": 15}}""");

        Assert.Contains("| Round 1 (the nova) | 19.61 | 39.13 | +19.52 |", text, StringComparison.Ordinal);
        Assert.Contains("| **A 3-round fight (the mean per round)** (headline) | 19.61 | 26.12 | +6.51 |", text, StringComparison.Ordinal);
        Assert.Contains("| An adventuring day (2014 DMG: 6–8 encounters, 2 short rests; 7 encounters of 3 rounds, 2 short rests) | 19.61 | 22.40 | +2.79 |", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_FeatureThatCompetesForTheBonusAction_SaysTheDeltaIsNetOnce()
    {
        // Hew already spends the Bonus Action on a crit; an offhand attack every turn competes with it.
        const string feature = """
            {"name": "Offhand", "attacks": [{"name": "Shortsword", "action": "bonus_action", "offhand": true, "damage": "1d6", "damage_type": "piercing", "properties": ["melee", "light"]}]}
            """;

        var text = await Success($$$"""{"baseline": {{{Fighter2024()}}}, "feature": {{{feature}}}, "target": {"ac": 15}}""");

        Assert.Contains(
            "- Bonus Action: baseline Hew; variant Shortsword, Hew (Shortsword added). **Collision**: opportunity cost: the engine picks the " +
            "better use each turn, so Δ is net, not what the new option would add on a free Bonus Action.",
            text,
            StringComparison.Ordinal);
        Assert.Equal(1, CountOf(text, ActionSlotReport.OpportunityCostText));
        Assert.Contains("- Reaction: neither build uses it.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_DayHorizon_KeepsEachBuildsDayNotesInItsOwnSection()
    {
        // The baseline has no limited feature and the variant one: "no resource-limited features" belongs to the baseline's
        // day only, and in a shared notes list it would read as a claim about the variant too.
        const string feature = """{"name": "Smite", "modifiers": [{"kind": "extra_damage", "name": "Smite", "dice": "2d8", "type": "radiant", "when": "first_hit_per_turn", "resource": {"uses": 2, "per": "long_rest"}}]}""";

        var text = await Success($$"""{"baseline": {{Fighter2024()}}, "feature": {{feature}}, "horizon": "day"}""");

        var baseline = text.IndexOf("## Baseline at level 5", StringComparison.Ordinal);
        var variant = text.IndexOf("## Variant at level 5", StringComparison.Ordinal);
        Assert.InRange(text.IndexOf("- No resource-limited features: the day horizon equals the fight horizon.", StringComparison.Ordinal), baseline, variant);
        Assert.Equal(1, CountOf(text, "No resource-limited features"));
        Assert.InRange(text.IndexOf("- Day horizon: each resource-limited feature is measured alone", StringComparison.Ordinal), variant, text.IndexOf("**Assumptions:**", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CallTool_ConditionFeature_ReportsHowOftenItLands()
    {
        const string feature = """
            {"name": "Stunning blow", "modifiers": [{"kind": "condition_on_hit", "name": "Stunning blow", "condition": "stunned", "ability": "con", "dc": 15, "when": "first_hit_per_turn", "resource": {"uses": 3, "per": "short_rest"}}]}
            """;
        var expected = DprComparison.Compare(new CompareRequest
        {
            Baseline = DslJson.Deserialize<BuildSpec>(Fighter2024(), "baseline"),
            Feature = DslJson.Deserialize<FeatureSpec>(feature, "feature"),
        });
        var effect = Assert.Single(expected.SignatureEffects);

        var text = await Success($$"""{"baseline": {{Fighter2024()}}, "feature": {{feature}}}""");

        Assert.Contains("## Signature effects at level 5 (a 3-round fight)", text, StringComparison.Ordinal);
        Assert.Contains($"| Stunning blow | variant (new) | stunned | {Percent(effect.LandChancePerTurn)} | {Percent(effect.LandChancePerFight)} |", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_NothingButTheTablesToCite_HasNoEmptySourcesLine()
    {
        // A target given in full and a baseline with its own slope: no DMG row, typical save, area table or reference curve
        // was used, so only the tables are pointed to.
        const string feature = """{"name": "+1 weapon", "modifiers": [{"kind": "to_hit", "name": "+1 weapon", "amount": 1}]}""";

        var text = await Success($$$"""{"baseline": {{{ScalingFighter}}}, "feature": {{{feature}}}, "target": {"ac": 15, "save_bonus": 2}}""");

        Assert.EndsWith("\n\n*The tables: rules_get ref \"rules://tables/dpr-targets-by-level\", \"rules://tables/gwf-expected-values\", \"rules://tables/aoe-targets\".*\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("*Sources:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_DifferentEditions_SaysTheDeltaIncludesTheEditionChange()
    {
        var text = await Success($$$"""{"baseline": {{{Fighter2024("")}}}, "variant": {{{Fighter2014("")}}}, "target": {"ac": 15}}""");

        Assert.Contains("The baseline follows the 2024 rules and the variant the 2014 rules, so Δ includes the edition change", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_VariantAndFeature_IsRefused()
    {
        var text = await Error($$"""{"baseline": {{Fighter2024()}}, "variant": {{Fighter2024()}}, "feature": {{SavageAttacker}}}""");

        Assert.Equal(Prefix + "Give variant or feature, not both: variant is the whole changed build, feature only what to add to the baseline.", text);
    }

    [Theory]
    [InlineData("")]
    [InlineData(""", "variant": null, "feature": null""")]
    public async Task CallTool_NeitherVariantNorFeature_SaysToGiveOneWithAnExample(string rest)
    {
        var text = await Error($$"""{"baseline": {{Fighter2024()}}{{rest}}}""");

        Assert.Equal(
            Prefix + "Give exactly one of variant (the whole changed build) or feature (only what to add to the baseline), e.g. " +
            "\"feature\": {\"name\": \"Great Weapon Master\", \"modifiers\": [{\"kind\": \"bonus_damage\", \"amount\": \"pb\", " +
            "\"attack_action_only\": true}]}.",
            text);
    }

    [Theory]
    [InlineData("""{"name": "F", "level": 5}""", SavageAttacker, "Invalid baseline: a build needs at least one attack")]
    [InlineData(null, """{"name": "Bad", "modifiers": [{"kind": "smite"}]}""", "Invalid feature: modifiers item 1: kind \"smite\" is not a modifier kind; kinds are ")]
    [InlineData(null, """{"name": "Bad", "modifiers": [{"kind": "extra_damage", "dice": "2d6!"}]}""", "Invalid feature: modifiers item 1 (extra_damage): dice \"2d6!\"")]
    [InlineData(null, """{"name": "Nothing"}""", "Invalid feature: the feature changes nothing")]
    public async Task CallTool_InvalidBaselineOrFeature_SaysWhichOneIsWrong(string? baseline, string feature, string start)
    {
        var text = await Error($$"""{"baseline": {{baseline ?? Fighter2024()}}, "feature": {{feature}}}""");

        Assert.StartsWith(Prefix + start, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_InvalidVariant_IsNamedAsTheVariant()
    {
        var text = await Error($$$"""{"baseline": {{{Fighter2024()}}}, "variant": {"name": "V", "level": 5, "attacks": [{"name": "A", "damage": "1d8"}], "modifiers": [{"kind": "smite"}]}}""");

        Assert.StartsWith(Prefix + "Invalid variant: modifiers item 1: kind \"smite\" is not a modifier kind", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_LargestComparison_StaysUnderTheOutputCeiling()
    {
        const string feature = """{"name": "Lucky", "modifiers": [{"kind": "lucky", "name": "Lucky feat"}]}""";

        var text = await Success(
            $$"""{"baseline": {{KitchenSink}}, "feature": {{feature}}, "target": {"ac": 16, "saves": {"str": 3, "con": 2}, "hp": 60, "resistances": ["fire"], "second_target_rate": 0.5}, "levels": [1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20], "horizon": "day"}""");

        Assert.True(text.Length < 24_000, $"The result is {text.Length} characters.");
        Assert.Contains("## Baseline at level 11: Kitchen sink", text, StringComparison.Ordinal);
        Assert.Contains("### The adventuring day", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_TypicalComparison_StaysConcise()
    {
        var text = await Success($$"""{"baseline": {{Fighter2024()}}, "feature": {{SavageAttacker}}}""");

        Assert.True(text.Length < 9_000, $"The result is {text.Length} characters.");
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    // The tool's own formats, for expectations computed by the Domain.
    private static string Percent(double probability) => DndMcp.Formatting.BalanceMarkdownText.Percent(probability);

    private static string Le(LevelEquivalent levelEquivalent) => DndMcp.Formatting.BalanceMarkdownText.LevelEquivalentText(levelEquivalent);
}
