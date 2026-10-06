using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation;
using DndMcp.Formatting.Srd;
using DndMcp.IntegrationTests.Infrastructure;
using DndMcp.IntegrationTests.Srd;
using DndMcp.Repository.Srd.Combatants;
using Xunit;
using K = DndMcp.Domain.Simulation.StatBlockValues;

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

        Assert.Contains("**Legendary Resistance** 3/day (4 in its lair; balance_simulate uses it for an encounter fought in a lair)", text, StringComparison.Ordinal);
        Assert.Contains("- **Multiattack** (3 uses): Rend ×2; then 1 of: Rend, Scorching Ray", text, StringComparison.Ordinal);
        Assert.Contains("3 uses per round (4 in its lair; balance_simulate uses it for an encounter fought in a lair)", text, StringComparison.Ordinal);
        Assert.Contains("(costs 1, once per round)", text, StringComparison.Ordinal);
        Assert.Contains("### Warnings (what the simulation leaves out or simplifies)", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2014", "**Spell slots** 1st 4 · 2nd 3 · 3rd 3 · 4th 3 · 5th 3 · 6th 1 · 7th 1 · 8th 1 · 9th 1 (a spell uses a slot of its own level; no upcasting)")]
    [InlineData("2024", "- 5th: **Fireball** — Dex save DC 20, a 20-ft sphere (4 creatures by the DMG's count): 10d6 fire (avg 35) on a failure, half on a success (a spell: magical).")]
    // 2014 Power Word Kill does nothing but kill ("If the creature you choose has 100 hit points or fewer, it dies.
    // Otherwise, the spell has no effect"): no "no damage each".
    [InlineData("2014", "- 9th: **Power Word Kill** (a 9th-level slot) — no attack roll or save: 1 creature (a spell: magical). A target dies at 100 HP " +
                        "or fewer (no death saves); above that, it has no effect.")]
    public async Task CombatantFormat_Lich_ShowsItsSpellsAsTheSimulatorCastsThem(string edition, string spells)
    {
        var text = await Combatant($$"""{"name": "Lich", "edition": "{{edition}}", "format": "combatant"}""");

        Assert.Contains("### Spells (as actions)", text, StringComparison.Ordinal);
        Assert.Contains(spells, text, StringComparison.Ordinal);
        Assert.Contains("**Condition immunities** charmed, exhaustion, frightened, paralyzed, poisoned", text, StringComparison.Ordinal);
        Assert.Contains("### Notes", text, StringComparison.Ordinal);
    }

    [Theory]
    // Shield: "Until the start of your next turn, you have a +5 bonus to AC, including against the triggering attack"
    // (both editions), and the simulator keeps it up that long. The erinyes' Parry ("adds 4 to its AC against one melee
    // attack that would hit it"; 2024 "hit by a melee attack roll") still covers only the attack it turns, and only a
    // melee one, as the engine reads it: it never turns an arrow or a spell attack. The 2024 mummy lord's Whirlwind of
    // Sand ("hit by an attack roll") turns any attack.
    [InlineData("Lich", "2024", "**Shield** — +5 AC until the start of its next turn, including against the attack that would hit it (its reaction).")]
    [InlineData("Lich", "2014", "**Shield** (a 1st-level slot) — +5 AC until the start of its next turn, including against the attack that would hit it (its reaction).")]
    [InlineData("Erinyes", "2014", "**Parry** — +4 AC against one melee attack that would hit it (its reaction).")]
    [InlineData("Erinyes", "2024", "**Parry** — +4 AC against one melee attack that would hit it (its reaction).")]
    [InlineData("Mummy Lord", "2024", "**Whirlwind of Sand** — +2 AC against one attack that would hit it (its reaction).")]
    public async Task CombatantFormat_ParryReactions_SayHowLongTheirArmorClassLasts(string name, string edition, string reaction)
    {
        var text = await Combatant($$"""{"name": "{{name}}", "edition": "{{edition}}", "format": "combatant"}""");

        Assert.Contains(reaction, text, StringComparison.Ordinal);
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
    public async Task CombatantFormat_Solar2024_SlayingBowKillsOnlyOnAFailedSave()
    {
        // "Failure: If the creature has 100 Hit Points or fewer, it dies. It otherwise takes 24 (4d8 + 6) Piercing damage
        // plus 36 (8d8) Radiant damage." The engine kills only on a failed save; a target that saves takes nothing.
        var text = await Combatant("""{"ref": "2024/monster/solar", "format": "combatant"}""");

        Assert.Contains(
            "- **Slaying Bow** — Dex save DC 21, 1 creature: 4d8+6 piercing + 8d8 radiant (avg 60) on a failure, none on a success. On a " +
            "failure, a target with 100 HP or fewer dies (no death saves); above that, it takes the damage.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CombatantFormat_PitFiend2024_TheHellfireRoutineIsNotSharedWithItself()
    {
        // The recharge pool is named after the routine; its spells and the other routines say whose recharge they share,
        // and the routine itself does not name itself.
        var text = await Combatant("""{"ref": "2024/monster/pit-fiend", "format": "combatant"}""");

        Assert.Contains("- **Hellfire Spellcasting** (Recharge 4–6, one recharge shared with its spells and options) — uses Fireball ×2.", text, StringComparison.Ordinal);
        Assert.Contains("- **Hellfire Spellcasting (Hold Monster)** (Recharge 4–6, one recharge shared with Hellfire Spellcasting) — uses Fireball, Hold Monster.", text, StringComparison.Ordinal);
        Assert.Contains("**Fireball** (Recharge 4–6, one recharge shared with Hellfire Spellcasting) — Dex save DC 21", text, StringComparison.Ordinal);
        Assert.DoesNotContain("shared with Hellfire Spellcasting) — uses Fireball ×2", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CombatantFormat_Lich2024_DeathlyTeleportHitsEveryoneInItsArea()
    {
        // "each creature within 10 feet of the space it left takes 11 (2d10) Necrotic damage": the engine hits the area's
        // DMG count (a 10-ft emanation: 2 creatures), so the view does not say "1 creature".
        var text = await Combatant("""{"ref": "2024/monster/lich", "format": "combatant"}""");

        Assert.Contains(
            "- **Deathly Teleport** (costs 1) — no attack roll or save: a 10-ft emanation (2 creatures by the DMG's count), 2d10 necrotic " +
            "(avg 11) each.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CombatantBody_KillThresholdAndImmunityAfterASuccess_AreShownWhereSet()
    {
        // Power Word Kill's "If the target has 100 Hit Points or fewer, it dies", the solar's Slaying Longbow rider and
        // Frightful Presence's immunity after a success change what a fight does, so the view says so beside the action.
        // Hand-built actions on a real block pin the view whatever the normalizer fills; an action without them says neither.
        // A save action kills only on a failure (the 2024 solar's Slaying Bow), so its line says so.
        var ogre = new MonsterNormalizer(MonsterOverrides.Load(VendoredSrdLookup.ContentRoot), SpellOverlay.Load(VendoredSrdLookup.ContentRoot))
            .Normalize(VendoredSrdLookup.Instance.Require("2024/monster/ogre"), VendoredSrdLookup.Instance);
        var kill = new StatBlockAction
        {
            Name = "Power Word Kill", Kind = K.ActionKinds.AutoHit, Slot = K.ActionSlots.Action, IsSpell = true, KillAtOrBelowHp = 100,
            Damage = [new DamageRoll(DamageFormula.ParseDamage("12d12", "damage"), "psychic")], Text = "test",
        };
        var presence = new StatBlockAction
        {
            Name = "Frightful Presence", Kind = K.ActionKinds.Save, Slot = K.ActionSlots.Action, Targets = 3, ImmuneAfterSuccess = true,
            Save = new SaveSpec("wis", 16, K.OnSuccess.None),
            Condition = new ConditionEffect { Condition = K.Conditions.Frightened, Duration = K.Durations.UntilEndOfSourceTurn },
            Text = "test",
        };
        var longbow = new StatBlockAction
        {
            Name = "Slaying Longbow", Kind = K.ActionKinds.Attack, Slot = K.ActionSlots.Action, AttackBonus = 13, Range = K.AttackRanges.Ranged,
            Damage = [new DamageRoll(DamageFormula.ParseDamage("2d8+6", "damage"), "piercing")],
            OnHit = [new ActionEffect { Kind = K.EffectKinds.Save, Save = new SaveSpec("con", 15, K.OnSuccess.None), KillAtOrBelowHp = 100, ImmuneAfterSuccess = true }],
            Text = "test",
        };

        var bow = new StatBlockAction
        {
            Name = "Slaying Bow", Kind = K.ActionKinds.Save, Slot = K.ActionSlots.Action, Targets = 1, KillAtOrBelowHp = 100,
            Save = new SaveSpec("dex", 21, K.OnSuccess.None),
            Damage = [new DamageRoll(DamageFormula.ParseDamage("4d8+6", "damage"), "piercing")], Text = "test",
        };
        var glare = new StatBlockAction
        {
            Name = "Deadly Glare", Kind = K.ActionKinds.Save, Slot = K.ActionSlots.Action, Targets = 1, KillAtOrBelowHp = 50,
            Save = new SaveSpec("wis", 15, K.OnSuccess.None),
            Condition = new ConditionEffect { Condition = K.Conditions.Frightened, Duration = K.Durations.UntilEndOfSourceTurn },
            Text = "test",
        };
        var lance = new StatBlockAction
        {
            Name = "Death Lance", Kind = K.ActionKinds.Attack, Slot = K.ActionSlots.Action, AttackBonus = 10, KillAtOrBelowHp = 20,
            Damage = [new DamageRoll(DamageFormula.ParseDamage("1d10", "damage"), "necrotic")], Text = "test",
        };

        var text = CombatantMarkdown.Body(ogre with { Actions = [.. ogre.Actions, kill, presence, longbow, bow, glare, lance] });

        Assert.Contains(
            "- **Power Word Kill** — no attack roll or save: 1 creature, 12d12 psychic (avg 78) each (a spell: magical). A target dies at 100 HP " +
            "or fewer (no death saves); above that, the damage applies.", text, StringComparison.Ordinal);
        Assert.Contains(
            "- **Frightful Presence** — Wis save DC 16, 3 creatures: frightened (until the end of the monster's next turn) on a failure. A " +
            "creature is immune for the rest of the fight after a successful save (or once its condition ends).", text, StringComparison.Ordinal);
        Assert.Contains(
            "- **Slaying Longbow** — ranged attack +13: 2d8+6 piercing (avg 15) per hit; on a hit, Con save DC 15: dies at 100 HP or fewer on a " +
            "failure; the target is immune for the rest of the fight after a successful save.", text, StringComparison.Ordinal);
        Assert.Contains(
            "- **Slaying Bow** — Dex save DC 21, 1 creature: 4d8+6 piercing (avg 24) on a failure, none on a success. On a failure, a target " +
            "with 100 HP or fewer dies (no death saves); above that, it takes the damage.", text, StringComparison.Ordinal);
        Assert.Contains(
            "- **Deadly Glare** — Wis save DC 15, 1 creature: frightened (until the end of the monster's next turn) on a failure. On a failure, " +
            "a target with 50 HP or fewer dies (no death saves); above that, the condition applies.", text, StringComparison.Ordinal);
        // An attack kills only a target it hits (the engine rolls the hit first).
        Assert.Contains(
            "- **Death Lance** — melee attack +10: 1d10 necrotic (avg 5.5) per hit. On a hit, a target with 20 HP or fewer dies (no death " +
            "saves); above that, the damage applies.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("on a success. A target dies", text, StringComparison.Ordinal);
        // Said only where set: the ogre's own actions carry neither.
        Assert.Equal(2, text.Split("dies at 100 HP or fewer").Length - 1);
        Assert.Equal(3, text.Split("100 HP or fewer").Length - 1);
        Assert.Equal(2, text.Split("immune for the rest of the fight after a successful save").Length - 1);
        Assert.Contains("- **Greatclub** — melee attack +6: 2d8+4 bludgeoning (avg 13) per hit.\n", text, StringComparison.Ordinal);
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
