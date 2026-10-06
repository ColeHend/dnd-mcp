using DndMcp.Domain.Campaign;
using DndMcp.Domain.Characters;
using DndMcp.Domain.Combat;
using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation;
using DndMcp.Tests.Srd.Combatants;
using Xunit;

namespace DndMcp.Tests.Combat;

/// <summary>
/// Builders for the tracker tests: encounters, the exit-criteria fixtures' sheets (FIX §1, §3 as amended by contract
/// §16), real SRD stat blocks, and a step runner that asks <see cref="CombatTracker.Needs"/> what to roll, answers from a
/// scripted queue of faces and applies the step, as the Repository will.
/// </summary>
internal static class CombatKit
{
    public const string E2014 = DslValues.Editions.E2014;
    public const string E2024 = DslValues.Editions.E2024;

    /// <summary>An active encounter with no combatants.</summary>
    public static EncounterState Encounter(string ruleset = E2024, string name = "Test fight", bool lair = false, bool player = false) => new()
    {
        Id = "enc-1",
        Name = name,
        Ruleset = ruleset,
        Status = CampaignValues.EncounterStatuses.Active,
        Lair = lair,
        PlayerCampaign = player,
    };

    /// <summary>A real stat block from the shipped corrected data.</summary>
    public static StatBlock Block(string edition, string slug) => CorrectedSrd.Shipped.StatBlock(edition, slug);

    /// <summary>A sheet created through <see cref="SheetUpdate"/> from spec JSON, for an entity.</summary>
    public static CharacterSheet Sheet(string entityId, string json, string edition) =>
        SheetUpdate.Apply(null, DslJson.Deserialize<SheetSpec>(json, "sheet"), null, edition, entityId).Sheet;

    // ---------------------------------------------------------------------------------------------------------------
    // Fixture A (FIX §1, §2.1; contract §16): Belmakor's full sheet, Torch, four minimal sheets.

    public static CharacterSheet Belmakor()
    {
        var sheet = Sheet("e-belmakor", """
            { "player": "Cole", "ruleset": "2014", "species": "High Elf", "background": "Noble",
              "classes": [{ "class": "Wizard", "subclass": "Bladesinger", "level": 12 }],
              "abilities": { "str": 11, "dex": 20, "con": 16, "int": 20, "wis": 13, "cha": 12 },
              "ac": 17, "max_hp": 110, "temp_hp": 7, "initiative_bonus": 5, "spell_save_dc": 17, "spell_attack": 9,
              "save_proficiencies": ["int", "wis", "con"],
              "resources": [
                { "name": "Bladesong", "max": 4, "recharge": "long_rest" },
                { "name": "Arcane Recovery", "max": 1, "recharge": "long_rest" },
                { "name": "Contingency", "state": "set", "note": "Polymorph (T-rex) at low HP" } ],
              "feats": ["War Caster", "Resilient (Constitution)", "Fey Touched", "Tough"],
              "sheet_source": "fixture" }
            """, E2014);
        // False Life before the fight: one 1st-level slot already spent (never rewritten by the fight).
        return sheet with { SpellSlots = SheetMaps.With(sheet.SpellSlots, "1", sheet.SpellSlots["1"] with { Used = 1 }) };
    }

    public static CharacterSheet Torch() =>
        Sheet("e-torch", """{ "classes": [{ "class": "wizard", "level": 12 }], "max_hp": 74, "sheet_source": "fixture" }""", E2014);

    public static CharacterSheet Minimal(string entityId, string classes) => Sheet(entityId, $$"""{ "classes": {{classes}}, "sheet_source": "fixture" }""", E2014);

    /// <summary>The six living PCs of fixture A, in name order (A1's add_party order), as add entries.</summary>
    public static IReadOnlyList<AddEntry> PartyA() =>
    [
        Pc("e-aiden", "Aiden Ironstar", "character:aiden-ironstar", Minimal("e-aiden", """[{"class":"paladin","level":6},{"class":"sorcerer","level":6}]""")),
        Pc("e-belmakor", "Belmakor Silverwind", "character:belmakor", Belmakor()),
        Pc("e-ignis", "Ignis", "character:ignis", Minimal("e-ignis", """[{"class":"bard","level":12}]""")),
        Pc("e-torch", "Lieutenant James Torch", "character:torch", Torch()),
        Pc("e-serif", "Serif", "character:serif", Minimal("e-serif", """[{"class":"artificer","level":12,"hit_die":8}]""")),
        Pc("e-vars", "Vars Nocturne", "character:vars", Minimal("e-vars", """[{"class":"ranger","level":6},{"class":"rogue","level":6}]""")),
    ];

    // ---------------------------------------------------------------------------------------------------------------
    // Fixture B (FIX §3, §4.1; contract §16): Björn, the fishman monk, the amethyst Dragon Slayer (2024, level 8).

    public static CharacterSheet Bjorn() => Sheet("e-bjorn", """
        { "ruleset": "2024", "classes": [{ "class": "barbarian", "subclass": "Path of the Totem Warrior", "level": 8 }],
          "abilities": { "str": 18, "dex": 14, "con": 16 }, "ac": 15, "initiative_bonus": 2, "xp": 34000,
          "resources": [{ "name": "Rage", "max": 4, "recharge": "short_rest_one" }], "sheet_source": "fixture" }
        """, E2024);

    public static CharacterSheet Monk() => Sheet("e-monk", """
        { "ruleset": "2024", "classes": [{ "class": "monk", "subclass": "Order of the Deep Sea", "level": 8 }],
          "abilities": { "dex": 16, "con": 14, "wis": 16 }, "ac": 16, "initiative_bonus": 3, "xp": 34000,
          "resources": [{ "name": "Focus", "max": 8, "recharge": "short_rest" }], "sheet_source": "fixture" }
        """, E2024);

    public static CharacterSheet DragonSlayer() => Sheet("e-slayer", """
        { "ruleset": "2024", "classes": [{ "class": "Dragon Slayer", "subclass": "Amethyst", "level": 8, "hit_die": 10 }],
          "abilities": { "str": 18, "dex": 14, "con": 14 }, "ac": 16, "initiative_bonus": 2, "xp": 34000, "sheet_source": "fixture" }
        """, E2024);

    /// <summary>B1's add_party, in name order: Björn, the Dragon Slayer, the monk (names as the One Piece world has them).</summary>
    public static IReadOnlyList<AddEntry> PartyB() =>
    [
        Pc("e-bjorn", "Björn Mountainfell", "character:bjorn-mountainfell", Bjorn()),
        Pc("e-slayer", "The amethyst Dragon Slayer", "character:dragon-slayer", DragonSlayer()),
        Pc("e-monk", "The fishman monk", "character:fishman-monk", Monk()),
    ];

    /// <summary>Björn's two Potions of Healing (FIX §3).</summary>
    public static IReadOnlyList<CombatItem> HoldingsB() => [new CombatItem("h-potion", "e-bjorn", "Potion of Healing", 2)];

    public static AddEntry Pc(string entityId, string name, string handle, CharacterSheet? sheet) => new()
    {
        EntityId = entityId,
        EntityName = name,
        EntityHandle = handle,
        EntitySubtype = "pc",
        Sheet = sheet,
    };

    public static AddEntry Monster(StatBlock block, int count = 1, HpChoice? hp = null, string? name = null) => new()
    {
        Monster = block,
        Count = count,
        Hp = hp,
        Name = name,
    };

    // ---------------------------------------------------------------------------------------------------------------
    // Running steps

    /// <summary>Adds entries with ids named after the entries ("e-torch", "m-mummy-lord", "m-mummy#2"), stable for the scripts.</summary>
    public static CombatStepResult Add(EncounterState state, params AddEntry[] entries)
    {
        var ids = new List<string>();
        foreach (var entry in entries)
        {
            var stem = entry.EntityId ?? "m-" + (entry.Name ?? entry.Monster?.Name ?? "c").ToLowerInvariant().Replace(' ', '-');
            for (var i = 0; i < entry.Count; i++)
            {
                var id = stem;
                for (var n = 2; state.Combatants.Any(c => c.Id == id) || ids.Contains(id); n++)
                {
                    id = $"{stem}#{n}";
                }

                ids.Add(id);
            }
        }

        return Step(state, new AddOp(entries, ids));
    }

    /// <summary>
    /// Runs one step: its needs rolled from <paramref name="faces"/> (each need takes as many faces as its dice; the total
    /// is the faces plus the expression's flat part), then applied. Asserts every face was used.
    /// </summary>
    public static CombatStepResult Step(EncounterState state, CombatOp op, params int[] faces)
    {
        var needs = CombatTracker.Needs(state, op);
        var queue = new Queue<int>(faces);
        var rolls = new Dictionary<string, RolledValue>(StringComparer.Ordinal);
        foreach (var need in needs)
        {
            rolls[need.Key] = Roll(need.Expression, queue);
        }

        Assert.Empty(queue);
        return CombatTracker.Apply(state, op, rolls);
    }

    /// <summary>
    /// A rolled expression from scripted faces: "4d6+5" takes four faces; "2d20kl1-2" takes two and keeps the lower; the
    /// flat part is added.
    /// </summary>
    public static RolledValue Roll(string expression, Queue<int> faces)
    {
        var match = System.Text.RegularExpressions.Regex.Match(expression, @"^(?<n>\d+)d(?<s>\d+)(?<keep>k[hl]1)?(?<rest>([+-]\d+d\d+)*)(?<flat>[+-]\d+)?$");
        Assert.True(match.Success, $"unexpected expression {expression}");
        var count = int.Parse(match.Groups["n"].Value);
        var dice = Enumerable.Range(0, count).Select(_ => faces.Dequeue()).ToList();
        var kept = match.Groups["keep"].Value switch
        {
            "kh1" => [dice.Max()],
            "kl1" => [dice.Min()],
            _ => dice,
        };
        var extra = System.Text.RegularExpressions.Regex.Matches(match.Groups["rest"].Value, @"[+-](\d+)d\d+")
            .SelectMany(m => Enumerable.Range(0, int.Parse(m.Groups[1].Value)).Select(_ => faces.Dequeue())).ToList();
        var flat = match.Groups["flat"].Success ? int.Parse(match.Groups["flat"].Value) : 0;
        return new RolledValue(kept.Sum() + extra.Sum() + flat, [.. kept, .. extra]);
    }

    /// <summary>
    /// A running fight of custom combatants (30 HP, AC 12, enemies unless named in <paramref name="party"/>) with the given
    /// initiative totals: round 1, the first one's turn.
    /// </summary>
    public static EncounterState Fight(string edition, params (string Name, double Init)[] who) => Fight(edition, [], who);

    public static EncounterState Fight(string edition, string[] party, params (string Name, double Init)[] who)
    {
        var s = Encounter(edition);
        s = Add(s, who.Select(w => new AddEntry
        {
            Name = w.Name,
            Hp = HpChoice.Of(30),
            Ac = 12,
            Side = party.Contains(w.Name) ? "party" : "enemy",
            DeathSaves = party.Contains(w.Name) ? true : null,
        }).ToArray()).Next;
        return Step(s, new InitiativeOp { Rolls = who.Select(w => new InitiativeRollInput(w.Name, Total: w.Init)).ToList() }).Next;
    }

    /// <summary>Ends turns until it is <paramref name="name"/>'s turn (at most two rounds), returning every step.</summary>
    public static (EncounterState State, List<CombatStepResult> Steps) NextUntil(EncounterState state, string name)
    {
        var steps = new List<CombatStepResult>();
        for (var i = 0; i < 2 * state.Combatants.Count + 2 && (steps.Count == 0 || state.TurnHolder?.Name != name); i++)
        {
            var result = Step(state, new NextOp());
            steps.Add(result);
            state = result.Next;
            if (state.TurnHolder?.Name == name)
            {
                break;
            }
        }

        Assert.Equal(name, state.TurnHolder?.Name);
        return (state, steps);
    }

    /// <summary>One <c>next</c>.</summary>
    public static EncounterState Next(this EncounterState state) => Step(state, new NextOp()).Next;

    /// <summary>The combatant named (exact tracker name).</summary>
    public static CombatantState Named(this EncounterState state, string name) =>
        state.Combatants.Single(c => c.Name == name);

    /// <summary>The reminders of a kind.</summary>
    public static IReadOnlyList<CombatReminder> Of(this CombatStepResult result, string kind) => result.Reminders.Where(r => r.Kind == kind).ToList();

    /// <summary>Whether any reminder of the kind contains every token.</summary>
    public static bool Says(this CombatStepResult result, string kind, params string[] tokens) =>
        result.Reminders.Any(r => r.Kind == kind && tokens.All(t => r.Text.Contains(t, StringComparison.Ordinal)));
}
