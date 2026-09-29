using System.Globalization;
using System.Text.RegularExpressions;
using DndMcp.Domain.Core;
using DndMcp.Domain.Dpr;
using DndMcp.Domain.Encounters;
using DndMcp.Domain.Features;
using DndMcp.IntegrationTests.Infrastructure;
using Xunit;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: <c>balance_dpr</c> through the real client reproduces the Phase 4 contract's worked examples (§8) from the
/// JSON a model sends, and every number it prints comes with what it was computed against: the level, the horizon, the
/// target and where its AC and saves came from, the rulings and the policies.
///
/// <para>
/// The maths is pinned to 1e-9 in DndMcp.Tests; these pin what only the host decides: that the spec classes bind the
/// model's JSON to the same build, the text the model reads (the contract prints two decimals, so the tests do), the
/// echo of every assumption, and the size of the answer. Each golden appears as the model would read it, e.g. the
/// headline "**19.61** damage per round at level 5 against AC 15".
/// </para>
/// </summary>
public sealed partial class BalanceDprToolTests : IClassFixture<McpServerHarness>
{
    private const string Prefix = "An error occurred invoking 'balance_dpr': ";

    private const string Gwm2014 =
        """{ "kind": "power_attack", "name": "Great Weapon Master" }, { "kind": "extra_attack", "name": "GWM bonus attack", "attack": "Greatsword", "action": "bonus_action", "trigger": "crit" }""";

    private const string Gwm2024 =
        """{ "kind": "bonus_damage", "name": "Great Weapon Master", "amount": "pb", "attack_action_only": true }, { "kind": "extra_attack", "name": "Hew", "attack": "Greatsword", "action": "bonus_action", "trigger": "crit" }""";

    private const string ActionSurge =
        """{ "kind": "extra_attack", "name": "Action Surge", "attack": "Greatsword", "count": 2, "action": "action", "resource": { "uses": 1, "per": "short_rest" } }""";

    private const string Fireball2014 = """
        { "name": "2014 Wizard 5", "edition": "2014", "level": 5, "abilities": {"int": 16},
          "modifiers": [{ "kind": "save_effect", "name": "Fireball", "ability": "dex", "dc": 15, "dice": "8d6", "type": "fire", "shape": "sphere", "size": 20 }] }
        """;

    private readonly McpServerHarness _server;

    public BalanceDprToolTests(McpServerHarness server)
    {
        _server = server;
    }

    /// <summary>§8.2: 2014 level 5 fighter, Str 18, greatsword 2d6 ×2, GWF 2014.</summary>
    internal static string Fighter2014(string modifiers = Gwm2014, int strength = 18, string style = "\"gwf\"") =>
        $$"""
        { "name": "2014 L5 Fighter", "edition": "2014", "level": 5, "abilities": {"str": {{strength}}}, "fighting_style": {{style}},
          "attacks": [{ "name": "Greatsword", "count": 2, "damage": "2d6", "damage_type": "slashing", "properties": ["melee", "heavy", "two-handed"] }],
          "modifiers": [{{modifiers}}] }
        """;

    /// <summary>§8.3: 2024 level 5 fighter, Str 19, greatsword 2d6 ×2 with Graze, GWF 2024.</summary>
    internal static string Fighter2024(string modifiers = Gwm2024, int strength = 19) =>
        $$"""
        { "name": "2024 L5 Fighter", "edition": "2024", "level": 5, "abilities": {"str": {{strength}}}, "fighting_style": "gwf",
          "attacks": [{ "name": "Greatsword", "count": 2, "damage": "2d6", "damage_type": "slashing", "properties": ["melee", "heavy", "two-handed"], "mastery": "graze" }],
          "modifiers": [{{modifiers}}] }
        """;

    private async Task<string> Success(string arguments) =>
        _server.SuccessText(await _server.CallToolJsonAsync("balance_dpr", arguments));

    private async Task<string> Error(string arguments) =>
        _server.ErrorText(await _server.CallToolJsonAsync("balance_dpr", arguments));

    private static string Round1(string build, string target = """{"ac": 15}""", string extra = "") =>
        $$"""{"build": {{build}}, "target": {{target}}, "horizon": "round1"{{extra}}}""";

    [GeneratedRegex(@"^\*\*(?<dpr>\d+\.\d\d)\*\* damage per round at level (?<level>\d+) against (?<target>.+?) — (?<horizon>.+?)\. For scale", RegexOptions.Multiline)]
    private static partial Regex HeadlineRegex();

    private static double Headline(string text)
    {
        var match = HeadlineRegex().Match(text);
        Assert.True(match.Success, $"No headline in:\n{text}");
        return double.Parse(match.Groups["dpr"].Value, CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task CallTool_2014FighterWithGwm_IsTheGoldenWithItsBreakdown()
    {
        // §8.2: 18.7 + 0.0975 × 9.35 = 19.611625. The −5/+10 is on at AC 15, so the swings hit 40%, not 65%.
        var text = await Success(Round1(Fighter2014()));

        Assert.StartsWith("# Damage per round: 2014 L5 Fighter\n\n**19.61** damage per round at level 5 against AC 15 — round 1 (the nova).", text, StringComparison.Ordinal);
        Assert.Contains("| Greatsword | 2 × Attack action | +7 (Str +4, proficiency +3) | 2d6 slashing +4 (Str) | melee, heavy, two-handed; Great Weapon Fighting (fighting style): reroll a 1 or 2 once |", text, StringComparison.Ordinal);
        Assert.Contains("| Greatsword | Attack action | 2 | +7 vs AC 15 | 40% | 5% | 18.70 |", text, StringComparison.Ordinal);
        Assert.Contains("| Greatsword | Bonus Action (GWM bonus attack) | 0.0975 | +7 vs AC 15 | 40% | 5% | 0.91 |", text, StringComparison.Ordinal);
        Assert.Contains("- Great Weapon Master (−5/+10, auto): on in round 1; on in 100% of rounds.", text, StringComparison.Ordinal);
        Assert.Contains("- Great Weapon Master (power_attack): −5 to hit, +10 damage, policy auto.", text, StringComparison.Ordinal);
        Assert.Contains("- GWM bonus attack (extra_attack): 1 × Greatsword with the Bonus Action, after a melee crit this turn.", text, StringComparison.Ordinal);

        // Said once: the extra attack's own row has its uses, and with one Bonus Action option there is no choice to report.
        Assert.DoesNotContain("- GWM bonus attack: taken", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Bonus Action, when there was something to spend it on", text, StringComparison.Ordinal);
        Assert.Contains("P(hit) is averaged over the turns and includes the power attack's penalty when it is on", text, StringComparison.Ordinal);
        Assert.DoesNotContain("creatures in an area", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_TargetFullyGiven_CitesNeitherTheDmgRowNorTheTypicalSave()
    {
        // With ac and save_bonus given, no CR row and no typical save bonus were used, so neither is named as a source.
        var text = await Success(Round1(Fighter2014(), """{"ac": 15, "save_bonus": 3}"""));

        Assert.Contains("AC 15 (given). Saves +3 on every save (given).", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Monster Statistics by Challenge Rating", text, StringComparison.Ordinal);
        Assert.DoesNotContain("The Finished Book", text, StringComparison.Ordinal);
        Assert.Contains("*Sources: reference curves: RPGBOT's DPR target and the community Warlock Baseline (Form of Dread)", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", 18, "\"gwf\"", "16.87")] // GWF only: 253/15
    [InlineData("", 18, "null", "15.00")] // no GWF, no feat
    [InlineData("""{ "kind": "power_attack", "policy": "always" }""", 18, "\"gwf\"", "18.70")] // PA always, no crit bonus attack
    [InlineData("", 20, "\"gwf\"", "19.50")] // the ASI alternative
    public async Task CallTool_2014FighterAlternatives_AreTheGoldens(string modifiers, int strength, string style, string expected)
    {
        var text = await Success(Round1(Fighter2014(modifiers, strength, style)));

        Assert.Contains($"**{expected}** damage per round at level 5 against AC 15", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_2014FighterAcGrid_IsTheResearchColumnAndSaysWherePowerAttackStops()
    {
        // §8.2's column "2014 GWF, PA + crit BA" for AC 13–19; the −5/+10 is off from AC 17.
        var text = await Success(Round1(Fighter2014(), extra: """, "ac_range": [13, 19]"""));

        Assert.Contains("## Damage per round by target AC, round 1 (the nova)", text, StringComparison.Ordinal);
        Assert.Contains("| Level | AC 13 | AC 14 | AC 15 | AC 16 | AC 17 | AC 18 | AC 19 |", text, StringComparison.Ordinal);
        Assert.Contains("| 5 | 24.30 | 21.95 | 19.61 | 17.27 | 15.10 (PA off) | 13.81 (PA off) | 12.52 (PA off) |", text, StringComparison.Ordinal);
        Assert.Contains("\"PA off\": the power attack is never switched on against that AC", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_AcGridWithAnAdvantageRate_SaysWhereThePowerAttackIsOnlySometimesOn()
    {
        // With Reckless Attack on half the turns, the −5/+10 pays only with Advantage from AC 17: on in half the rounds,
        // then never. A cell that said nothing there would read as "always on".
        var text = await Success(Round1(
            Fighter2014("""{ "kind": "power_attack", "name": "Great Weapon Master" }, { "kind": "advantage", "name": "Reckless", "rate": 0.5 }"""),
            extra: """, "ac_range": [15, 20]"""));

        Assert.Matches(new Regex(@"^\| 5 \| [\d.]+ \| [\d.]+ \| [\d.]+ \(PA 50%\) \| [\d.]+ \(PA 50%\) \| [\d.]+ \(PA off\) \| [\d.]+ \(PA off\) \|$", RegexOptions.Multiline), text);
        Assert.Contains("- Great Weapon Master (−5/+10, auto): in round 1 on with no rate source present (50%), on with Reckless (50%);", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "24.04", "hew_gets_pb = false")] // §8.3: 23.10 + 0.0975 × 9.6 = 24.036
    [InlineData("""{"hew_gets_pb": true}""", "24.23", "hew_gets_pb = true")] // 24.226125
    public async Task CallTool_2024FighterWithHew_IsTheGoldenUnderEachRulingAndEchoesIt(string? rulings, string expected, string echoed)
    {
        var text = await Success(Round1(Fighter2024(), extra: rulings is null ? "" : $$""", "rulings": {{rulings}}"""));

        Assert.Contains($"**{expected}** damage per round at level 5 against AC 15", text, StringComparison.Ordinal);
        Assert.Contains($"- Rulings: {echoed} (true means: Bonus Action extra attacks (Hew) get attack_action_only flat damage", text, StringComparison.Ordinal);
        Assert.Contains("| 2d6 slashing +4 (Str) +3 (Great Weapon Master, Attack action only) |", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_2014Fireball_SharedRollKillFiguresAreTheGolden()
    {
        // §8.8: DC 15 vs Dex +2 (F 0.6), 8d6 fire on four goblins (HP 7): raw 89.2, effective 27.998608, P(all four die)
        // 0.999333 over one shared damage roll.
        var text = await Success($$"""{"build": {{Fireball2014}}, "target": {"saves": {"dex": 2}, "hp": 7}, "horizon": "round1"}""");

        Assert.Contains("**89.20** damage per round at level 5 against a target with +2 on every save — round 1 (the nova).", text, StringComparison.Ordinal);
        Assert.Contains(
            "- **Fireball** (Dex save, DC 15; 4 targets): P(a target fails) 60%; 22.30 damage per target, 89.20 in all per cast; cast once a round, " +
            "89.20 damage a round. With 7 hit points each (overkill removed; one damage roll shared by every target, saves independent): " +
            "effective 28.00 per cast; P(each dies) 99.97%; P(all 4 die) 99.93%; expected kills 4.00; P(exactly k die), k = 0–4: ",
            text,
            StringComparison.Ordinal);
        Assert.Contains("- Fireball: 4 targets from the DMG's \"Targets in Areas of Effect\" for a 20-ft sphere (±1d3 for how bunched the creatures are).", text, StringComparison.Ordinal);
        Assert.Contains("- Fireball (save_effect): Dex save DC 15 (given), 8d6 fire, half on a success, 4 targets (20-ft sphere), uses the Action.", text, StringComparison.Ordinal);
        Assert.Contains("creatures in an area: the DMG 2014 \"Targets in Areas of Effect\" (p. 249; not SRD text)", text, StringComparison.Ordinal);

        // An area's turn total is not one creature's damage, so P(≥ HP) is not claimed for it.
        Assert.Contains("P(≥ the target's hit points) is not given, since the turn's damage is spread over several creatures", text, StringComparison.Ordinal);
    }

    private static string Smite(string policy, string when = "first_hit_per_turn", string extra = "") =>
        $$"""
        { "name": "Paladin", "edition": "2014", "level": 5, "abilities": {"str": 18},
          "attacks": [{ "name": "Longsword", "count": 2, "damage": "1d8", "damage_type": "slashing", "properties": ["melee", "versatile"] }],
          "modifiers": [{ "kind": "extra_damage", "name": "Divine Smite", "dice": "2d8", "type": "radiant", "when": "{{when}}", "policy": "{{policy}}" {{extra}} }] }
        """;

    [Theory]
    // §8.7: uses per round, damage per use and damage per round (8.505 = 1701/200, 6.885, 1.755 print half away from zero).
    [InlineData("any_hit", "first_hit_per_turn", "", "| Divine Smite | first hit each turn, policy any_hit | 0.8775 | 9.69 | 8.51 |")]
    [InlineData("crit_or_last", "first_hit_per_turn", "", "| Divine Smite | first hit each turn, policy crit_or_last | 0.6675 | 10.31 | 6.89 |")]
    [InlineData("crits_only", "first_hit_per_turn", "", "| Divine Smite | first hit each turn, policy crits_only | 0.0975 | 18.00 | 1.76 |")]
    [InlineData("any_hit", "every_hit", """, "resource": { "uses": 4, "per": "long_rest" }""", "| Divine Smite | every hit, policy any_hit, 4 per long rest | 1.3 | 9.69 | 12.60 |")]
    [InlineData("optimal", "first_hit_per_turn", """, "use_value": 8""", "| Divine Smite | first hit each turn, policy optimal (use_value 8) | 0.6675 | 10.31 | 6.89 |")]
    [InlineData("optimal", "first_hit_per_turn", """, "use_value": 20""", "| Divine Smite | first hit each turn, policy optimal (use_value 20) | 0 | never used | 0.00 |")]
    public async Task CallTool_SmitePolicies_AreTheGoldens(string policy, string when, string extra, string row)
    {
        var text = await Success(Round1(Smite(policy, when, extra)));

        Assert.Contains(row, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_SmitePolicy_IsEchoedWithWhatItMeans()
    {
        var text = await Success(Round1(Smite("optimal", extra: """, "use_value": 9""")));

        Assert.Contains(
            "- Policies (Paladin): Divine Smite optimal (use_value 9) (optimal: spent where it adds the most expected damage net of use_value per use (exact backward induction)).",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_WarlockBaselineCurve_IsThePublishedCurveWithItsMarks()
    {
        // §8.11: the preset against the CR = level row, levels 1–20; Hex has no setup cost, so round 1 = the fight.
        string[] published =
            ["6.30", "8.25", "8.25", "8.90", "17.80", "17.80", "17.80", "19.10", "20.50", "19.10",
             "28.65", "28.65", "28.65", "28.65", "28.65", "28.65", "38.20", "38.20", "38.20", "38.20"];
        var text = await Success("""{"build": {"name": "Warlock", "preset": "warlock_baseline", "level": 5}, "levels": [1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20]}""");

        var rows = text.Split('\n').SkipWhile(l => !l.StartsWith("## Damage per round by level", StringComparison.Ordinal))
            .Where(l => l.StartsWith("| ", StringComparison.Ordinal)).Skip(1).Take(20).Select(l => l.Split(" | ")).ToList();
        Assert.Equal(Enumerable.Range(1, 20).Select(l => l == 5 ? "| **5**" : $"| {l}"), rows.Select(r => r[0]));
        Assert.Equal(published, rows.Select(r => r[1]));
        Assert.Equal(published, rows.Select(r => r[2])); // round 1
        Assert.Equal(published, rows.Select(r => r[5])); // the reference curve beside it
        Assert.Contains("| 4 | 8.90 | 8.90 | 14 | 10.83 | 8.90 | ASI: Cha 16 → 18 |", text, StringComparison.Ordinal);
        // Eldritch Blast's beams are its Cantrip Upgrade, and it is cast with the Magic action: never "Extra Attack" or the
        // "Attack action", rules terms a model would repeat.
        Assert.Contains("| **5** | 17.80 | 17.80 | 15 | 12.08 | 17.80 | Cantrip Upgrade: Eldritch Blast 1 → 2 beams |", text, StringComparison.Ordinal);
        Assert.Contains("| 17 | 38.20 | 38.20 | 19 | 27.08 | 38.20 | Cantrip Upgrade: Eldritch Blast 3 → 4 beams |", text, StringComparison.Ordinal);
        Assert.Contains("## The build as read: level 5, 2024 rules", text, StringComparison.Ordinal);
        Assert.Contains("| Eldritch Blast | 2 × Magic action | +7 (Cha +4, proficiency +3) | 1d10 force +4 (Agonizing Blast) | ranged, spell; cantrip beams ×2 |", text, StringComparison.Ordinal);
        Assert.Contains("| Eldritch Blast | Magic action | 2 | +7 vs AC 15 |", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Extra Attack: Eldritch Blast", text, StringComparison.Ordinal);
    }

    [Theory]
    // §8.12: 3 uses a day × 19.5227 per use ÷ (E × 3 rounds): E 6 → +3.254, E 8 → +2.440, over the §8.2 golden 19.61.
    [InlineData(6, "+3.25", "18", "22.87")]
    [InlineData(8, "+2.44", "24", "22.05")]
    public async Task CallTool_DayHorizon_ActionSurgeIsTheCorrectedGolden(int encounters, string adds, string wanted, string day)
    {
        var text = await Success(
            $$"""{"build": {{Fighter2014(Gwm2014 + ", " + ActionSurge)}}, "target": {"ac": 15}, "horizon": "day", "encounters_per_day": {{encounters}}}""");

        Assert.Contains($"| Action Surge | 3 (1 per short rest) | 1 | 19.52 | {wanted} | {adds} |", text, StringComparison.Ordinal);
        Assert.Contains($"Day: 19.61 {adds} = **{day}** a round", text, StringComparison.Ordinal);
        Assert.Contains($"**{day}** damage per round at level 5 against AC 15 — an adventuring day (custom: {encounters} encounters, 2 short rests;", text, StringComparison.Ordinal);
        Assert.Contains("- Action Surge: taken 0.3333 times a round (1 per short rest), 6.23 damage a round.", text, StringComparison.Ordinal);

        // The day's own note stays with its arithmetic, before the assumptions.
        var section = text.IndexOf("## The adventuring day at level 5", StringComparison.Ordinal);
        var note = text.IndexOf("- Day horizon: each resource-limited feature is measured alone", StringComparison.Ordinal);
        Assert.InRange(note, section, text.IndexOf("**Assumptions:**", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CallTool_EveryResult_ShowsAllThreeHorizonsAndMarksTheHeadline()
    {
        var text = await Success($$$"""{"build": {{{Fighter2014(Gwm2014 + ", " + ActionSurge)}}}, "target": {"ac": 15}}""");

        Assert.Contains("| Round 1 (the nova) | 39.13 |", text, StringComparison.Ordinal);
        Assert.Contains("| **A 3-round fight (the mean per round)** (headline) | 26.12 |", text, StringComparison.Ordinal);
        Assert.Contains("| An adventuring day (2014 DMG: 6–8 encounters, 2 short rests; 7 encounters of 3 rounds, 2 short rests) | 22.40 |", text, StringComparison.Ordinal);
        Assert.Contains("- By round: 1: 39.13 · 2: 19.61 · 3: 19.61.", text, StringComparison.Ordinal);
        Assert.Equal(26.12, Headline(text));
    }

    [Fact]
    public async Task CallTool_DefaultTarget_IsTheCrEqualsLevelRowAndSaysWhoseItIs()
    {
        var text = await Success($$"""{"build": {{Fighter2024()}}}""");

        Assert.Contains("## Target at level 5\n\nAC 15 (DMG 2014 row for CR 5 (CR = level 5)). Saves +2 on every save (the typical save bonus by CR from The Finished Book's", text, StringComparison.Ordinal);
        Assert.Contains($"(pp. 274–275; not SRD text); typical save bonus: The Finished Book's \"Baseline Monster Stats\" ({TypicalSaveBonus.SourceUrl}; not a DMG table)", text, StringComparison.Ordinal);
        Assert.Contains("For scale at level 5: RPGBOT's target 12.08, the warlock baseline 17.80.", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mm2024", "AC 15 (2024 SRD monster medians for CR 5 (25 monsters; CR = level 5)). Saves +1 on every save (the median of the 2024 SRD's CR 5 monsters' mean save bonuses (0.83, rounded)).")]
    [InlineData("MM-2014", "AC 15 (2014 SRD monster medians for CR 5 (25 monsters; CR = level 5)). Saves +1 on every save (the median of the 2014 SRD's CR 5 monsters' mean save bonuses (0.83, rounded)).")]
    public async Task CallTool_EmpiricalProfile_IsTheSrdMediansAndSaysWhoseTheyAre(string profile, string target)
    {
        var text = await Success($$$"""{"build": {{{Fighter2024()}}}, "target": {"profile": "{{{profile}}}"}}""");

        Assert.Contains("## Target at level 5\n\n" + target, text, StringComparison.Ordinal);
        Assert.Contains("- Target profile mm20", text, StringComparison.Ordinal);
        Assert.Contains($"{MonsterStatsEmpirical.Source} (the 20", text, StringComparison.Ordinal);
        Assert.Contains("target for CR 5).", text, StringComparison.Ordinal); // the warlock reference follows the profile
    }

    [Fact]
    public async Task CallTool_TargetWithHp_GivesTheRound1SpreadAndKillChance()
    {
        var text = await Success(Round1(Fighter2014(), """{"ac": 15, "hp": 45}"""));

        Assert.Matches(new Regex(@"\*\*Round 1 damage\*\* \(exact distribution\): p10 \d+ · p25 \d+ · p50 \d+ · p75 \d+ · p90 \d+; P\(no damage\) [\d.]+%; range \d+–\d+; P\(≥ 45, the target's hit points\) [\d.]+%\."), text);
        Assert.Contains("45 hit points.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_VexAgainstATargetImmuneToTheWeapon_AnswersNoDamage()
    {
        // Every hit deals 0, so Vex never grants Advantage. The damage's P(0) summed from the dice was 0.9999999999999999,
        // which opened an empty "the hit dealt damage" branch; the model read only the SDK's bare error, not 0.
        var text = await Success("""
            {"build": {"name": "Vex duelist", "edition": "2024", "level": 5, "abilities": {"dex": 18},
                       "attacks": [{"name": "Shortsword", "count": 2, "to_hit": {"ability": "dex"}, "damage": "1d6", "damage_type": "piercing",
                                    "properties": ["melee", "finesse", "light"], "mastery": "vex"}]},
             "target": {"ac": 15, "immunities": ["piercing"]}}
            """);

        Assert.StartsWith("# Damage per round: Vex duelist\n\n**0.00** damage per round at level 5 against AC 15", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_BuildThatDoesNotScale_WarnsWhenEvaluatedAtOtherLevels()
    {
        var text = await Success($$"""{"build": {{Fighter2024()}}, "levels": [1, 5, 11]}""");

        Assert.Contains("2024 L5 Fighter does not change with level", text, StringComparison.Ordinal);
        Assert.Contains("\"count\": {\"1\": 1, \"5\": 2}", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_UntypedRiderOnATypedAttack_TakesTheAttacksTypeAndIsNotWarned()
    {
        // Sneak Attack without a type deals the Rapier's piercing, so the resistance halves it (settled decision; 2024 SRD
        // "The extra damage's type is the same as the weapon's type"): 6.3875 exactly, as with "type": "piercing".
        var text = await Success("""
            {"build": {"name": "Rogue", "level": 5, "abilities": {"dex": 18},
                       "attacks": [{"name": "Rapier", "to_hit": {"ability": "dex"}, "damage": "1d8", "damage_type": "piercing", "properties": ["finesse"]}],
                       "modifiers": [{"kind": "extra_damage", "name": "Sneak Attack", "dice": "3d6", "when": "first_hit_per_turn"}]},
             "target": {"ac": 15, "resistances": ["piercing"]}}
            """);

        Assert.StartsWith("# Damage per round: Rogue\n\n**6.39** damage per round", text, StringComparison.Ordinal);
        Assert.DoesNotContain("has no damage type", text, StringComparison.Ordinal);
        Assert.Contains("Sneak Attack (extra_damage): 3d6 (the attack's damage type), first hit each turn", text, StringComparison.Ordinal);
        Assert.DoesNotContain("typeless", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_TypelessDamageAgainstAResistingTarget_IsWarned()
    {
        // An attack without damage_type (and an untyped rider on it) is typeless, which no resistance touches: warned.
        var text = await Success("""
            {"build": {"name": "Rogue", "level": 5, "abilities": {"dex": 18},
                       "attacks": [{"name": "Rapier", "to_hit": {"ability": "dex"}, "damage": "1d8", "properties": ["finesse"]}],
                       "modifiers": [{"kind": "extra_damage", "name": "Sneak Attack", "dice": "3d6", "when": "first_hit_per_turn"}]},
             "target": {"ac": 15, "resistances": ["piercing"]}}
            """);

        Assert.Contains("- Rapier, Sneak Attack have no damage type, so the target's resistances, vulnerabilities and immunities never apply to them", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_LargestRequest_StaysUnderTheOutputCeiling()
    {
        // Nearly every modifier kind, 20 levels and a 16-AC grid: the biggest answer the validator lets through in practice.
        var text = await Success($$"""{"build": {{KitchenSink}}, "target": {"ac": 16, "saves": {"str": 3, "con": 2}, "hp": 60, "resistances": ["fire"], "second_target_rate": 0.5}, "levels": [1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20], "ac_range": [10, 25]}""");

        Assert.True(text.Length < 24_000, $"The result is {text.Length} characters.");
        Assert.Contains("| Level | AC 10 |", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_TypicalBuild_StaysConcise()
    {
        var text = await Success($$$"""{"build": {{{Fighter2024()}}}, "target": {"ac": 15}}""");

        Assert.True(text.Length < 6_000, $"The result is {text.Length} characters.");
    }

    internal const string KitchenSink = """
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
    public async Task CallTool_KitchenSink_EchoesEveryModifierAndTheBonusActionChoice()
    {
        var text = await Success($$$"""{"build": {{{KitchenSink}}}, "target": {"ac": 16, "saves": {"str": 3, "con": 2}, "hp": 60, "resistances": ["fire"], "second_target_rate": 0.5}}""");

        Assert.Contains("| Greataxe | 2 × Attack action | +9 (Str +5, proficiency +4) +1d4 (Bless); crit 19–20; Lucky | 1d12 slashing +5 (Str) |", text, StringComparison.Ordinal);
        Assert.Contains("- Opportunity Attack (extra_attack): 1 × Maul as a reaction on 30% of rounds.", text, StringComparison.Ordinal);
        Assert.Contains("- Help (advantage): Advantage on 50% of turns.", text, StringComparison.Ordinal);
        Assert.Contains("- Aura (save_effect): Con save DC 14 (given), 1d6 fire, half on a success, 2 targets, costs no action.", text, StringComparison.Ordinal);
        Assert.Contains("- Bonus Action, when there was something to spend it on: bonus_action attacks (Shortsword) ", text, StringComparison.Ordinal);
        Assert.Contains("| Greataxe | Cleave, second creature |", text, StringComparison.Ordinal);
        Assert.Contains("- Reaction: Opportunity Attack (Maul) on 30% of rounds,", text, StringComparison.Ordinal);
        Assert.Contains("- Power attack (−5/+10, auto): in round 1 on with no rate source present (37.5%), off with Darkness (12.5%), on with Help (37.5%), on with Help and Darkness (12.5%);", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"build": {"name": "F", "level": 5, "attacks": [{"name": "A", "damage": "2d6kh1"}]}}""")]
    [InlineData("""{"build": {"name": "F", "level": 5, "attacks": [{"name": "A", "damage": "1d8"}], "modifiers": [{"kind": "smite"}]}}""")]
    [InlineData("""{"build": {"name": "F", "level": 5, "attacks": [{"name": "A", "count": {"0": 1}, "damage": "1d8"}]}}""")]
    [InlineData("""{"build": {"name": "F", "level": 5}}""")]
    public async Task CallTool_InvalidBuild_ReturnsTheValidatorsMessage(string arguments)
    {
        var build = System.Text.Json.JsonDocument.Parse(arguments).RootElement.GetProperty("build").GetRawText();
        var expected = Assert.Throws<DndInputException>(() =>
            BuildResolver.Resolve(DslJson.Deserialize<BuildSpec>(build, "build"), (IReadOnlyList<int>?)null, null, "build")).Message;

        Assert.Equal(Prefix + expected, await Error(arguments));
        Assert.StartsWith("Invalid build", expected, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_UnknownKind_ListsEveryKind()
    {
        var text = await Error("""{"build": {"name": "F", "level": 5, "attacks": [{"name": "A", "damage": "1d8"}], "modifiers": [{"kind": "smite"}]}}""");

        Assert.Contains("kind \"smite\" is not a modifier kind; kinds are ", text, StringComparison.Ordinal);
        Assert.All(DslValues.Kinds.Set.Values, kind => Assert.Contains(kind, text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CallTool_BadDice_SaysWhatDamageMayBe()
    {
        var text = await Error("""{"build": {"name": "F", "level": 5, "attacks": [{"name": "Greatsword", "damage": "2d6kh1"}]}}""");

        Assert.Equal(
            Prefix + "Invalid build: attacks item 1 (Greatsword): damage \"2d6kh1\" keeps or drops dice (\"2d6kh1\"); damage is plain " +
            "dice and whole numbers joined by + or -, e.g. \"2d6\", \"1d8+1\" or \"1d10+1d6\".",
            text);
    }

    [Theory]
    [InlineData(""", "ac_range": [20, 10]""", "ac_range [20, 10] runs backwards; give [low, high], e.g. [10, 20].")]
    [InlineData(""", "ac_range": [13]""", "ac_range has 1 value; give two, [low, high], e.g. [13, 19] (at most 16 ACs).")]
    [InlineData(", \"horizon\": \"forever\"", "horizon \"forever\" is not a horizon; use \"round1\" (one turn: the nova), \"fight\" (the mean over a fight of rounds, default 3) or \"day\" (limited features spread over an adventuring day, see rest_preset).")]
    [InlineData(""", "rounds": 11""", "rounds is 11; a fight is 1 to 10 rounds (3 is the DMG's convention).")]
    [InlineData(""", "levels": [0, 21]""", "levels: 0, 21 are not character levels; levels are 1 to 20.")]
    [InlineData(", \"rest_preset\": \"custom\"", "rest_preset \"custom\" needs both encounters_per_day and short_rests, e.g. {\"rest_preset\": \"custom\", \"encounters_per_day\": 4, \"short_rests\": 1}; or give either number with a preset to override just that one.")]
    [InlineData(""", "encounters_per_day": 1e300""", "encounters_per_day is 1e+300; an adventuring day has 1 to 20 encounters (the 2014 DMG's is 6–8; 3.5 means 3–4).")]
    [InlineData(""", "target": {"cr": "1/3"}""", "Invalid target: cr: \"1/3\" is not a Challenge Rating. A CR is 0, 1/8, 1/4, 1/2 (or 0.125, 0.25, 0.5) or a whole number from 1 to 30.")]
    public async Task CallTool_InvalidSetting_SaysWhatIsAccepted(string extra, string message)
    {
        var text = await Error($$"""{"build": {{Fighter2014()}}{{extra}}}""");

        Assert.Equal(Prefix + message, text);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Phase 4 review fixes, as the model reads them.
    // ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CallTool_FireBoltAddingIntByDefault_SaysSoInTheNotes()
    {
        var text = await Success(Round1("""
            { "name": "Wizard", "level": 5, "abilities": {"int": 18},
              "attacks": [{ "name": "Fire Bolt", "to_hit": {"ability": "int"}, "damage": "1d10", "damage_type": "fire", "properties": ["ranged", "spell"], "cantrip": "dice" }] }
            """));

        Assert.Contains(
            "- Fire Bolt: a spell attack, so Int +4 is added to its damage only because ability_to_damage defaults to true. Most spell " +
            "attacks add no modifier (Fire Bolt, Eldritch Blast): give ability_to_damage false; Spiritual Weapon does add it.",
            text[text.IndexOf("**Notes:**", StringComparison.Ordinal)..],
            StringComparison.Ordinal);
        Assert.Contains("| Fire Bolt | 1 × Magic action |", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_LongbowOnTheDefaultStr_KeepsTheDefaultAndSaysSo()
    {
        // Str 10 (+0) and proficiency: +3 vs AC 15, two arrows of 1d8: 2 × (0.40 × 4.5 + 0.05 × 9) = 4.50.
        var text = await Success(Round1("""
            { "name": "Archer", "level": 5, "abilities": {"dex": 18},
              "attacks": [{ "name": "Longbow", "count": 2, "damage": "1d8", "damage_type": "piercing", "properties": ["ranged", "heavy", "two-handed"] }] }
            """));

        Assert.Contains("**4.50** damage per round at level 5 against AC 15", text, StringComparison.Ordinal);
        Assert.Contains(
            "- Longbow is a ranged weapon attack but uses Str +0 (the default) while Dex is +4; ranged weapon attacks use Dex: give to_hit {\"ability\": \"dex\"}.",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_FireballWithoutAResource_SaysItIsCastEveryRound()
    {
        var text = await Success($$$"""{"build": {{{Fireball2014}}}, "target": {"saves": {"dex": 2}, "hp": 7}}""");

        Assert.Contains(
            "- Fireball: no resource, so it is used every round of every fight (it counts the same in round 1, a fight and a day). If it " +
            "spends spell slots or limited uses, give resource {\"uses\": n, \"per\": \"long_rest\"} for the fight and day figures.",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_RiderTable_SaysTheAttackDamageAlreadyIncludesIt()
    {
        var withRider = await Success(Round1(Fighter2014("""{ "kind": "extra_damage", "name": "Hex", "dice": "1d6", "type": "necrotic" }""")));
        var without = await Success(Round1(Fighter2014("")));

        var riderTable = withRider.IndexOf("| Rider | When |", StringComparison.Ordinal);
        Assert.True(riderTable > 0);
        Assert.Contains(
            "Each attack's Damage already includes its riders (and damage rerolls); the rider table breaks that share out, so do not add the two tables.",
            withRider[riderTable..],
            StringComparison.Ordinal);
        Assert.DoesNotContain("already includes its riders", without, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_2024EvasionOnAStunnedTarget_TakesTheFullRoll()
    {
        var text = await Success(Round1(
            """{ "name": "Wizard", "level": 5, "modifiers": [{ "kind": "save_effect", "name": "Fireball", "ability": "dex", "dc": 15, "dice": "8d6", "type": "fire", "resource": {"uses": 3, "per": "long_rest"} }] }""",
            """{ "save_bonus": 2, "evasion": true, "condition": "stunned" }"""));

        Assert.Contains("P(a target fails) 100%; 28.00 damage per target", text, StringComparison.Ordinal);
        Assert.Contains("- 2024 Evasion: not while Incapacitated (stunned, paralyzed or unconscious); such a target takes full damage on its failed save.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_StunningStrike_SaysTheConditionLastsTheTurnAndLabelsTheChance()
    {
        var build = Fighter2014("""{ "kind": "condition_on_hit", "name": "Stunning Strike", "condition": "stunned", "ability": "con", "dc": 15, "when": "first_hit_per_turn" }""");
        var text = await Success($$$"""{"build": {{{build}}}, "target": {"ac": 15, "saves": {"con": 2}, "legendary_resistance": 3}}""");

        Assert.Contains(
            "- Stunning Strike: the stunned condition counts only for the rest of the turn it lands in; the target starts each later turn without it, so " +
            "your later turns' attacks, allies' attacks and the target's lost turns are not counted (balance_simulate carries its duration). " +
            "It lasts into your next turn (2014 Stunning Strike: until the end of your next turn), so a fight's DPR and the feature's value are understated here.",
            text,
            StringComparison.Ordinal);
        Assert.Contains("the target is stunned for the rest of a turn ", text, StringComparison.Ordinal);
        Assert.Contains("(ignoring Legendary Resistance); expected attempts to land it past 3 Legendary Resistances: 6.67.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_HoldPersonAgainstLegendaryResistance_NeverPrintsABareLandingChanceBesideTheCasts()
    {
        var text = await Success("""
            {"build": {"name": "Cleric", "level": 5, "abilities": {"wis": 18}, "modifiers": [{"kind": "save_effect", "name": "Hold Person", "ability": "wis", "dc": 15, "condition": "paralyzed", "resource": {"uses": 2, "per": "long_rest"}}]},
             "target": {"saves": {"wis": 2}, "legendary_resistance": 3}}
            """);

        Assert.Contains(
            "Paralyzed: lands on the main target in a turn 40% (ignoring Legendary Resistance); at least once in the fight 84% (ignoring Legendary " +
            "Resistance); expected casts to land it past 3 Legendary Resistances: 6.67.",
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("at least once in the fight 84%;", text, StringComparison.Ordinal);
    }

    [Theory]
    // A javelin thrown at +7 (Str +4, PB +3; Archery does not apply to a thrown melee weapon) vs AC 15: 2 × (0.60 × 7.5 +
    // 0.05 × 3.5) = 10.10; with Dueling's +2 (it does apply): 2 × (0.60 × 9.5 + 0.05 × 3.5) = 12.70. Both editions.
    [InlineData("2014", "archery", "10.10")]
    [InlineData("2024", "archery", "10.10")]
    [InlineData("2014", "dueling", "12.70")]
    [InlineData("2024", "dueling", "12.70")]
    public async Task CallTool_ThrownJavelin_IsAMeleeWeaponForTheFightingStyles(string edition, string style, string dpr)
    {
        var text = await Success(Round1($$"""
            { "name": "Thrower", "edition": "{{edition}}", "level": 5, "abilities": {"str": 18}, "fighting_style": "{{style}}",
              "attacks": [{ "name": "Javelin", "count": 2, "damage": "1d6", "damage_type": "piercing", "properties": ["ranged", "thrown"] }] }
            """));

        Assert.Contains($"**{dpr}** damage per round at level 5 against AC 15", text, StringComparison.Ordinal);
        Assert.Contains("| Javelin | Attack action | 2 | +7 vs AC 15 | 65% |", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_HealAndDurations_AreEchoedAsSimulatorOnly()
    {
        var text = await Success(Round1(Fighter2014("""
            { "kind": "condition_on_hit", "name": "Trip", "condition": "prone", "ability": "str", "dc": 15, "duration": "start_of_next_turn" },
            { "kind": "heal", "name": "Second Wind", "dice": "1d10", "amount": 5, "action_cost": "bonus_action", "self_only": true, "resource": {"uses": 2, "per": "short_rest"} }
            """)));

        Assert.Contains("- Trip (condition_on_hit): prone on a failed Str save, DC 15, every hit, policy any_hit, duration start_of_next_turn (balance_simulate).", text, StringComparison.Ordinal);
        Assert.Contains("- Second Wind (heal): 1d10+5 healing, itself only, uses the Bonus Action, healing: kept for the simulator (balance_simulate), no effect on damage dealt; 2 per short rest.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_WordInTheDamage_SaysWhereItBelongs()
    {
        var text = await Error("""{"build": {"name": "F", "level": 5, "attacks": [{"name": "Greatsword", "damage": "2d6+str"}]}}""");

        Assert.Equal(
            Prefix + "Invalid build: attacks item 1 (Greatsword): damage \"2d6+str\" has \"str\": the ability modifier is added for you " +
            "(to_hit ability; ability_to_damage, default true); proficiency bonus is a bonus_damage modifier with amount \"pb\"; the damage " +
            "type goes in damage_type. Damage is plain dice and whole numbers joined by + or -, e.g. \"2d6\", \"1d8+1\" or \"1d10+1d6\".",
            text);
    }

    [Fact]
    public async Task CallTool_GwfTable_SaysWhichDiceTheRulesCoverAndWhatIsARuling()
    {
        var text = _server.SuccessText(await _server.CallToolJsonAsync("rules_get", """{"ref": "rules://tables/gwf-expected-values"}"""));

        Assert.Contains("they do not settle whether dice that another feature adds to the hit (smites, Hex) count, which is a table ruling", text, StringComparison.Ordinal);
        Assert.Contains("gwf_on_riders", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Both apply to the weapon's own dice", text, StringComparison.Ordinal);
    }
}
