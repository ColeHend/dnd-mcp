using DndMcp.IntegrationTests.Infrastructure;
using Xunit;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: <c>rules_get</c> with <c>format "combatant"</c> shows a monster exactly as the simulator reads it — the
/// numbers, parsed attacks and saves with averages, traits by kind, multiattack routines, spells, legendary actions,
/// notes and warnings — for any monster by name or ref in either edition or both, and refuses any other entry with the
/// list of formats.
///
/// <para>
/// The expected facts are the stat blocks' own (hand-checked against the SRD text, as the normalizer's spot checks are):
/// if a line here moves, either the normalizer's reading changed (a reviewed diff there) or this view stopped showing
/// what the simulator uses.
/// </para>
/// </summary>
public sealed class RulesGetCombatantTests : IClassFixture<McpServerHarness>
{
    private readonly McpServerHarness _server;

    public RulesGetCombatantTests(McpServerHarness server)
    {
        _server = server;
    }

    private async Task<string> Combatant(string argumentsJson) =>
        _server.SuccessText(await _server.CallToolJsonAsync("rules_get", argumentsJson));

    [Theory]
    [InlineData("2024", "**AC** 11 · **HP** 68 (rolled: 8d10+24) · **Initiative** −1 · **Speed** 40 ft.",
        "- **Greatclub** — melee attack +6: 2d8+4 bludgeoning (avg 13) per hit.",
        "- **Javelin** — melee or ranged attack +6: 2d6+4 piercing (avg 11) per hit.")]
    [InlineData("2014", "**AC** 11 (Hide Armor) · **HP** 59 (rolled: 7d10+21) · **Initiative** −1 · **Speed** 40 ft.",
        "- **Greatclub** — melee attack +6: 2d8+4 bludgeoning (avg 13) per hit.",
        "- **Javelin** — melee or ranged attack +6: 2d6+4 piercing (avg 11) per hit.")]
    public async Task CombatantFormat_Ogre_ShowsItsNumbersAndParsedAttacks(string edition, string numbers, params string[] actions)
    {
        var text = await Combatant($$"""{"name": "Ogre", "edition": "{{edition}}", "format": "combatant"}""");

        Assert.StartsWith($"# Ogre\n*monster · {edition} · ", text, StringComparison.Ordinal);
        Assert.Contains("\n\n*The stat block as balance_simulate reads it", text, StringComparison.Ordinal);
        Assert.Contains(numbers, text, StringComparison.Ordinal);
        Assert.Contains("**Abilities** Str 19 (+4) · Dex 8 (−1) · Con 16 (+3) · Int 5 (−3) · Wis 7 (−2) · Cha 7 (−2)", text, StringComparison.Ordinal);
        Assert.All(actions, a => Assert.Contains(a, text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CombatantFormat_AdultRedDragon2014_ShowsBreathRoutineLegendaryActionsAndResistance()
    {
        var text = await Combatant("""{"ref": "2014/monster/adult-red-dragon", "format": "combatant"}""");

        Assert.Contains("**Saves** Str +8 · **Dex +6** · **Con +13** · Int +3 · **Wis +7** · **Cha +11** (bold: proficient)", text, StringComparison.Ordinal);
        Assert.Contains("**Legendary Resistance** 3/day", text, StringComparison.Ordinal);
        Assert.Contains("**Immunities** fire", text, StringComparison.Ordinal);
        Assert.Contains("- **Bite** — melee attack +14: 2d10+8 piercing + 2d6 fire (avg 26) per hit.", text, StringComparison.Ordinal);
        // One of the five mislabeled 2014 half-saves: the override makes it half, and the note says why.
        Assert.Contains(
            "- **Fire Breath** (Recharge 5–6) — Dex save DC 21, a 60-ft cone (6 creatures by the DMG's count): 18d6 fire (avg 63) on a " +
            "failure, half on a success.", text, StringComparison.Ordinal);
        Assert.Contains("- **Multiattack** (4 uses): Frightful Presence, Bite, Claw ×2", text, StringComparison.Ordinal);
        Assert.Contains("### Legendary actions\n\n3 uses per round, reset at the start of its turn", text, StringComparison.Ordinal);
        Assert.Contains("- **Wing Attack** (costs 2) — Dex save DC 22", text, StringComparison.Ordinal);
        Assert.Contains("- **Tail Attack** (costs 1) — uses Tail.", text, StringComparison.Ordinal);
        Assert.Contains("- Fire Breath: a successful save takes half damage (overrides file:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CombatantFormat_AdultRedDragon2024_ShowsTheChoiceRoutineAndInLairUses()
    {
        var text = await Combatant("""{"name": "Adult Red Dragon", "format": "combatant"}""");

        Assert.Contains("**Legendary Resistance** 3/day (4 in its lair; the simulator is never in a lair)", text, StringComparison.Ordinal);
        Assert.Contains("- **Multiattack** (3 uses): Rend ×2; then 1 of: Rend, Scorching Ray", text, StringComparison.Ordinal);
        Assert.Contains("3 uses per round (4 in its lair; the simulator is never in a lair)", text, StringComparison.Ordinal);
        Assert.Contains("(costs 1, once per round)", text, StringComparison.Ordinal);
        Assert.Contains("### Warnings (what the simulation leaves out or simplifies)", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2014", "**Spell slots** 1st 4 · 2nd 3 · 3rd 3 · 4th 3 · 5th 3 · 6th 1 · 7th 1 · 8th 1 · 9th 1 (a spell uses a slot of its own level; no upcasting)")]
    [InlineData("2024", "- 5th: **Fireball** — Dex save DC 20, a 20-ft sphere (4 creatures by the DMG's count): 10d6 fire (avg 35) on a failure, half on a success (a spell: magical).")]
    public async Task CombatantFormat_Lich_ShowsItsSpellsAsTheSimulatorCastsThem(string edition, string spells)
    {
        var text = await Combatant($$"""{"name": "Lich", "edition": "{{edition}}", "format": "combatant"}""");

        Assert.Contains("### Spells (as actions)", text, StringComparison.Ordinal);
        Assert.Contains(spells, text, StringComparison.Ordinal);
        Assert.Contains("**Condition immunities** charmed, exhaustion, frightened, paralyzed, poisoned", text, StringComparison.Ordinal);
        Assert.Contains("### Notes", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CombatantFormat_QualifiedResistances_SayWhenTheyApply()
    {
        // The werewolf is the reason qualifiers are parsed at all: split on commas, it would be immune to every weapon.
        var text = await Combatant("""{"ref": "2014/monster/werewolf-hybrid", "format": "combatant"}""");

        Assert.Contains("bludgeoning (nonmagical and not silvered only)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CombatantFormat_BothEditions_PutsEachStatBlockUnderItsEdition()
    {
        var text = await Combatant("""{"name": "Ogre", "edition": "both", "format": "combatant"}""");

        Assert.StartsWith("# Ogre — 2014 vs 2024\n", text, StringComparison.Ordinal);
        var at2014 = text.IndexOf("## 2014 (SRD 5.1)", StringComparison.Ordinal);
        var at2024 = text.IndexOf("## 2024 (SRD 5.2.1)", StringComparison.Ordinal);
        Assert.True(at2014 > 0 && at2024 > at2014);
        Assert.Contains("(rolled: 7d10+21)", text[at2014..at2024], StringComparison.Ordinal);
        Assert.Contains("(rolled: 8d10+24)", text[at2024..], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CombatantFormat_IsNotTheConciseStatBlock()
    {
        var concise = await Combatant("""{"name": "Ogre"}""");
        var combatant = await Combatant("""{"name": "Ogre", "format": "combatant"}""");

        Assert.Contains("***Greatclub.*** Melee Attack Roll", concise, StringComparison.Ordinal);
        Assert.DoesNotContain("***Greatclub.***", combatant, StringComparison.Ordinal);
    }
}
