using System.Text.Json;
using System.Text.RegularExpressions;
using DndMcp.Domain.Dice;
using DndMcp.IntegrationTests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// The exit-criteria fixtures of Phase 7 (understand-fixtures.md §1-§4 as amended by contract §16) as the MODEL would make
/// them: every sheet, inventory and fixture-B entity is written through <c>campaign_write</c> and <c>campaign_character</c>,
/// and every step of the two fights is a <c>combat</c> call with the §16 call shape, as JSON.
///
/// <para>
/// Why the sheets are written through the tools and not copied from the repository fixtures (DndMcp.Tests'
/// FixtureSheets, which the integration project cannot reference anyway): the repository scenarios already prove the
/// writers; what only this layer can prove is that the tool surface takes every field the fixture needs (a resource's
/// <c>state</c> and <c>note</c>, a homebrew class's <c>hit_die</c>, a <c>sim_profile</c> beside the sheet, an inventory
/// item's <c>srd</c>) and that a fight seeded from what the TOOLS wrote round-trips. The JSON is FixtureSheets' own text,
/// field for field, so both layers fight the same sheets; a drift between them shows as the two layers' goldens
/// disagreeing.
/// </para>
/// </summary>
internal static class ScenarioCombatFixtures
{
    /// <summary>Fixture A's fight (FIX §2.2 A1).</summary>
    public const string CryptName = "The crypt (fixture)";

    /// <summary>Fixture B's fight (FIX §4.2 B1).</summary>
    public const string StationName = "The dark station (fixture)";

    /// <summary>FIX §1 / §2.1: Belmakor Silverwind, 2014 Bladesinger 12 (invented AC, HP 110, resources and spells; temp 7 from False Life).</summary>
    public const string BelmakorSheet = """
        { "player": "Cole", "ruleset": "2014", "species": "High Elf", "background": "Noble",
          "classes": [{ "class": "wizard", "subclass": "Bladesinger", "level": 12 }],
          "abilities": { "str": 11, "dex": 20, "con": 16, "int": 20, "wis": 13, "cha": 12 },
          "ac": 17, "max_hp": 110, "temp_hp": 7, "initiative_bonus": 5, "spell_save_dc": 17, "spell_attack": 9,
          "save_proficiencies": ["int", "wis", "con"],
          "resources": [
            { "name": "Bladesong", "max": 4, "recharge": "long_rest" },
            { "name": "Arcane Recovery", "max": 1, "recharge": "long_rest" },
            { "name": "Contingency", "state": "set", "note": "Polymorph (T-rex) when he drops low" } ],
          "feats": ["War Caster", "Resilient (Constitution)", "Fey Touched", "Tough"],
          "spells": ["Circle of Power", "Fly", "Contingency", "Polymorph", "Mirror Image", "False Life", "Shield"],
          "languages": ["Common", "Elvish"],
          "notes": "Planned: Hold Monster, Wall of Force; Forcecage or Simulacrum at 13.",
          "sheet_source": "fixture" }
        """;

    /// <summary>FIX §2.10: Belmakor's sim_profile (invented magical scimitars).</summary>
    public const string BelmakorSimProfile = """
        {"name":"Belmakor L12 Bladesinger (fixture)","edition":"2014","level":12,
         "abilities":{"str":11,"dex":20,"con":16,"int":20,"wis":13,"cha":12},
         "attacks":[
          {"name":"Scimitar","count":2,"to_hit":{"ability":"dex"},"damage":"1d6","damage_type":"slashing","properties":["melee","finesse","light","magical"]},
          {"name":"Offhand scimitar","action":"bonus_action","offhand":true,"to_hit":{"ability":"dex"},"damage":"1d6","damage_type":"slashing","properties":["melee","finesse","light","magical"]}],
         "modifiers":[{"kind":"ac","name":"Bladesong","amount":"int","setup":"bonus_action","resource":{"uses":4,"per":"long_rest"}}]}
        """;

    /// <summary>Contract §16: fixture A's five minimal sheets (classes and levels; Torch's max_hp given, FIX §1.4). Tristan (dead) gets none.</summary>
    public static readonly IReadOnlyList<(string Handle, string Sheet)> BelmakorParty =
    [
        ("character:vars", """{ "classes": [{ "class": "ranger", "level": 6 }, { "class": "rogue", "level": 6 }], "sheet_source": "fixture" }"""),
        ("character:aiden-ironstar", """{ "classes": [{ "class": "paladin", "level": 6 }, { "class": "sorcerer", "level": 6 }], "sheet_source": "fixture" }"""),
        ("character:ignis", """{ "classes": [{ "class": "bard", "level": 12 }], "sheet_source": "fixture" }"""),
        ("character:serif", """{ "classes": [{ "class": "artificer", "subclass": "Battle Smith", "level": 12, "hit_die": 8 }], "sheet_source": "fixture" }"""),
        ("character:torch", """{ "classes": [{ "class": "wizard", "level": 12 }], "max_hp": 74, "sheet_source": "fixture" }"""),
    ];

    /// <summary>FIX §3: Björn Mountainfell, 2024 Barbarian 8 (HP 85 derived from Con 16), XP 34,000.</summary>
    public const string BjornSheet = """
        { "ruleset": "2024", "classes": [{ "class": "barbarian", "subclass": "Path of the Totem Warrior", "level": 8 }],
          "abilities": { "str": 18, "dex": 14, "con": 16 }, "ac": 15, "initiative_bonus": 2, "xp": 34000,
          "resources": [{ "name": "Rage", "max": 4, "recharge": "short_rest_one" }], "sheet_source": "fixture" }
        """;

    /// <summary>FIX §3: the fishman monk, 2024 Monk 8 of the homebrew Order of the Deep Sea (HP 59 derived from Con 14), XP 34,000.</summary>
    public const string MonkSheet = """
        { "ruleset": "2024", "classes": [{ "class": "monk", "subclass": "Order of the Deep Sea", "level": 8 }],
          "abilities": { "dex": 16, "con": 14, "wis": 16 }, "ac": 16, "initiative_bonus": 3, "spell_save_dc": 14, "xp": 34000,
          "resources": [{ "name": "Focus", "max": 8, "recharge": "short_rest" }], "sheet_source": "fixture" }
        """;

    /// <summary>FIX §3: the amethyst Dragon Slayer, homebrew class (d10; saves Con, Str), level 8 (HP 68 derived from Con 14), XP 34,000.</summary>
    public const string DragonSlayerSheet = """
        { "ruleset": "2024", "classes": [{ "class": "dragon slayer", "subclass": "Amethyst", "level": 8, "hit_die": 10 }],
          "abilities": { "str": 18, "dex": 14, "con": 14, "int": 10, "wis": 12, "cha": 10 }, "ac": 16, "initiative_bonus": 2,
          "save_proficiencies": ["con", "str"], "xp": 34000, "sheet_source": "fixture" }
        """;

    /// <summary>FIX §4.10: the Dragon Slayer's sim_profile.</summary>
    public const string DragonSlayerSimProfile = """
        {"name":"Amethyst Dragon Slayer L8 (fixture)","edition":"2024","level":8,
         "abilities":{"str":18,"dex":14,"con":14,"int":10,"wis":12,"cha":10},
         "attacks":[{"name":"Draconic Strike","count":2,"damage":"1d8","damage_type":"bludgeoning","properties":["melee"]}],
         "modifiers":[{"kind":"extra_damage","name":"Amethyst rider","dice":"1d6","type":"psychic","when":"first_hit_per_turn"}]}
        """;

    /// <summary>FIX §4.1: the Nester's secret (author-only text: no non-author output may carry any of it).</summary>
    public const string NesterSecret = "Void-touched; what nests here is close to what the Wraiths are: the drowned, unmourned dead.";

    /// <summary>
    /// FIX §4.1 / contract §13: the entities the Phase 6 One Piece world lacks, in one campaign_write batch: the fishman monk
    /// (a current party member) and the Nester (restricted, its secret).
    /// </summary>
    public static readonly string OnePieceEntities = $$"""
        {"campaign": "one-piece", "reason": "Phase 7 fixture B: the fishman monk and the Nester",
         "ops": [
          {"op": "upsert", "kind": "character", "name": "The fishman monk", "slug": "fishman-monk", "subtype": "pc", "visibility": "party"},
          {"op": "link", "from": "character:fishman-monk", "rel": "member_of", "to": "faction:the-party", "status": "current"},
          {"op": "upsert", "kind": "character", "name": "The Nester", "slug": "the-nester", "subtype": "npc", "visibility": "restricted",
           "status": "alive", "secret_md": {{JsonSerializer.Serialize(NesterSecret)}}}]}
        """;

    /// <summary>
    /// A0: session 4 started live with the six living PCs (FIX §2.1). No title, as FIX's call has none: the fight's name
    /// stays author-only text the sweeps can look for. The played_on date is fixed so no text depends on the day it runs.
    /// </summary>
    public const string SessionA = """
        {"campaign": "belmakor", "action": "start", "session": 4, "played_on": "2026-08-29", "precision": "day",
         "attendance": [{"character": "character:belmakor"}, {"character": "character:vars"}, {"character": "character:ignis"},
                        {"character": "character:serif"}, {"character": "character:torch"}, {"character": "character:aiden-ironstar"}]}
        """;

    /// <summary>B0: session 13 started live with the three PCs (FIX §4.1; no title, as for A0).</summary>
    public const string SessionB = """
        {"campaign": "one-piece", "action": "start", "session": 13, "played_on": "2026-10-03", "precision": "day",
         "attendance": [{"character": "character:bjorn-mountainfell"}, {"character": "character:fishman-monk"}, {"character": "character:dragon-slayer"}]}
        """;

    /// <summary>A33 (contract §16): no xp, so none is awarded (no party sheet tracks XP).</summary>
    public const string EndA = """{"action": "end", "outcome": "The crypt is cleared."}""";

    /// <summary>B28 (contract §16): the ledger to the party, the potion to Björn, 120 gp to the party; no xp, so 7,200 is shared by default.</summary>
    public const string EndB = """
        {"action": "end", "outcome": "The sixth station is clear.",
         "loot": [{"item": "The sixth station's ledger", "to": "faction:the-party"},
                  {"item": "Potion of Water Breathing", "srd": "2024/magic-item/potion-of-water-breathing", "to": "character:bjorn-mountainfell"}],
         "currency": [{"to": "faction:the-party", "gp": 120}]}
        """;

    /// <summary>B28 as a dry run (the same arguments).</summary>
    public static string EndBDryRun => EndB.TrimEnd()[..^1] + ", \"dry_run\": true}";

    /// <summary>
    /// Fixture A's steps A1-A31 (FIX §2.2, contract §16's call shapes and mechanical translations), each the arguments of
    /// one combat call. Every value is given, so no step rolls a die.
    /// </summary>
    public static readonly IReadOnlyList<(string Step, string Arguments)> StepsA =
    [
        ("A1", $$"""{"action": "start", "campaign": "belmakor", "name": "{{CryptName}}", "add_party": true}"""),
        ("A2", """{"action": "add", "combatants": [{"srd": "2014/monster/mummy-lord", "hp": "avg", "side": "enemy"}, {"srd": "2014/monster/mummy", "count": 2, "hp": "avg", "side": "enemy"}]}"""),
        ("A3", """
            {"action": "initiative", "rolls": [{"combatant": "belmakor", "face": 17}, {"combatant": "mummy-lord", "face": 18}, {"combatant": "mummy", "face": 10},
              {"combatant": "vars", "total": 25}, {"combatant": "serif", "total": 16}, {"combatant": "ignis", "total": 14}, {"combatant": "torch", "total": 12},
              {"combatant": "aiden-ironstar", "total": 7}]}
            """),
        ("A4", """{"action": "next"}"""),
        ("A5", """{"action": "condition", "targets": ["belmakor"], "add": ["Bladesong"], "effect": {"ac": 5}, "duration": "1 minute", "resource": "Bladesong"}"""),
        ("A6", """{"action": "concentration", "targets": ["belmakor"], "spell": "Circle of Power", "slot_level": 5, "duration": "10 minutes"}"""),
        ("A7", """{"action": "next"}"""),
        ("A8", """{"action": "damage", "targets": ["belmakor"], "parts": [{"amount": 14, "type": "bludgeoning"}, {"amount": 21, "type": "necrotic"}], "source": "mummy-lord"}"""),
        ("A9", """{"action": "concentration", "targets": ["belmakor"], "total": 20}"""),
        ("A10", """{"action": "next"}"""),
        ("A11", """{"action": "next"}"""),
        ("A12-legendary", """{"action": "legendary", "source": "mummy-lord", "amount": 1, "name": "Attack (Rotting Fist)"}"""),
        ("A12", """{"action": "damage", "targets": ["torch"], "parts": [{"amount": 14, "type": "bludgeoning"}, {"amount": 21, "type": "necrotic"}], "source": "mummy-lord"}"""),
        ("A13", """{"action": "next"}"""),
        ("A14", """{"action": "damage", "targets": ["mummy-lord", "mummy", "mummy-2"], "parts": [{"amount": 28, "type": "fire"}]}"""),
        ("A15", """{"action": "next"}"""),
        ("A16", """{"action": "condition", "targets": ["torch"], "add": ["frightened"], "source": "mummy", "duration": "until_end_of_source_turn"}"""),
        ("A17", """{"action": "damage", "targets": ["torch"], "parts": [{"amount": 10, "type": "bludgeoning"}, {"amount": 10, "type": "necrotic"}], "source": "mummy"}"""),
        ("A18-next", """{"action": "next"}"""),
        ("A18", """{"action": "damage", "targets": ["torch"], "parts": [{"amount": 10, "type": "bludgeoning"}, {"amount": 10, "type": "necrotic"}], "source": "mummy-2"}"""),
        ("A19-aiden", """{"action": "next"}"""),
        ("A19", """{"action": "next"}"""),
        ("A20", """{"action": "next"}"""),
        ("A21", """{"action": "damage", "targets": ["mummy-2"], "parts": [{"amount": 8, "type": "slashing"}], "magical": true}"""),
        ("A22", """{"action": "next"}"""),
        ("A23", """{"action": "damage", "targets": ["torch"], "parts": [{"amount": 25, "type": "bludgeoning"}, {"amount": 42, "type": "necrotic"}], "critical": true, "source": "mummy-lord"}"""),
        ("A24-serif", """{"action": "next"}"""),
        ("A24-ignis", """{"action": "next"}"""),
        ("A24", """{"action": "next"}"""),
        ("A25", """{"action": "death_save", "targets": ["torch"], "face": 20}"""),
        ("A26", """{"action": "damage", "targets": ["mummy-lord"], "parts": [{"amount": 16, "type": "fire"}]}"""),
        ("A27", """{"action": "next"}"""),
        ("A28", """{"action": "next"}"""),
        ("A28a-prev", """{"action": "prev"}"""),
        ("A28a-next", """{"action": "next"}"""),
        ("A29", """{"action": "damage", "targets": ["mummy-lord"], "parts": [{"amount": 12, "type": "radiant"}]}"""),
        ("A30", """{"action": "next"}"""),
        ("A31", """{"action": "damage", "targets": ["mummy"], "parts": [{"amount": 10, "type": "piercing"}], "magical": true}"""),
    ];

    /// <summary>
    /// Fixture B's steps B1-B26 (FIX §4.2, contract §16), each the arguments of one combat call and the faces the server
    /// rolls during it (FIX §4.1's [5], [4, 5, 2, 3] and [3, 2] at B16, B20 and B22; none anywhere else).
    /// </summary>
    public static readonly IReadOnlyList<(string Step, string Arguments, int[] Faces)> StepsB =
    [
        ("B1", $$"""{"action": "start", "campaign": "one-piece", "name": "{{StationName}}", "add_party": true, "lair": true}""", []),
        ("B2", """{"action": "add", "combatants": [{"srd": "2024/monster/aboleth", "hp": "avg", "side": "enemy", "character": "character:the-nester"}]}""", []),
        ("B3", """{"action": "condition", "targets": ["bjorn-mountainfell"], "add": ["exhaustion"], "level": 1}""", []),
        ("B4", """
            {"action": "initiative", "rolls": [{"combatant": "fishman-monk", "face": 16}, {"combatant": "dragon-slayer", "face": 9},
              {"combatant": "bjorn-mountainfell", "face": 8}, {"combatant": "aboleth", "total": 13}]}
            """, []),
        ("B5-focus", """{"action": "use", "targets": ["fishman-monk"], "resource": "Focus", "amount": 2}""", []),
        ("B5", """{"action": "damage", "targets": ["aboleth"], "amount": 18, "damage_type": "bludgeoning"}""", []),
        ("B5-resistance", """{"action": "legendary", "source": "aboleth", "resistance": true}""", []),
        ("B6-legendary", """{"action": "legendary", "source": "aboleth", "amount": 1, "name": "Lash"}""", []),
        ("B6", """{"action": "damage", "targets": ["fishman-monk"], "amount": 12, "damage_type": "bludgeoning", "source": "aboleth"}""", []),
        ("B7", """{"action": "next"}""", []),
        ("B8-monk", """{"action": "damage", "targets": ["fishman-monk"], "amount": 12, "damage_type": "bludgeoning", "source": "aboleth"}""", []),
        ("B8-grapple-monk", """{"action": "condition", "targets": ["fishman-monk"], "add": ["grappled"], "source": "aboleth", "dc": 14}""", []),
        ("B8-bjorn", """{"action": "damage", "targets": ["bjorn-mountainfell"], "amount": 12, "damage_type": "bludgeoning", "source": "aboleth"}""", []),
        ("B8-grapple-bjorn", """{"action": "condition", "targets": ["bjorn-mountainfell"], "add": ["grappled"], "source": "aboleth", "dc": 14}""", []),
        ("B8", """{"action": "damage", "targets": ["fishman-monk"], "amount": 10, "damage_type": "psychic", "source": "aboleth"}""", []),
        ("B9", """{"action": "condition", "targets": ["fishman-monk"], "add": ["cursed (Mucus Cloud)"], "source": "aboleth", "duration": "until removed"}""", []),
        ("B10-next", """{"action": "next"}""", []),
        ("B10", """{"action": "damage", "targets": ["aboleth"], "parts": [{"amount": 22, "type": "bludgeoning"}, {"amount": 4, "type": "psychic"}]}""", []),
        ("B11-legendary", """{"action": "legendary", "source": "aboleth", "amount": 1, "name": "Lash"}""", []),
        ("B11", """{"action": "damage", "targets": ["dragon-slayer"], "amount": 12, "damage_type": "bludgeoning", "source": "aboleth"}""", []),
        ("B12-next", """{"action": "next"}""", []),
        ("B12-rage", """{"action": "condition", "targets": ["bjorn-mountainfell"], "add": ["Rage"], "effect": {"resist": ["all"], "except": ["psychic"]}, "resource": "Rage"}""", []),
        ("B12", """{"action": "damage", "targets": ["aboleth"], "amount": 24, "damage_type": "slashing"}""", []),
        ("B13-legendary", """{"action": "legendary", "source": "aboleth", "amount": 1, "name": "Lash"}""", []),
        ("B13", """{"action": "damage", "targets": ["bjorn-mountainfell"], "amount": 12, "damage_type": "bludgeoning", "source": "aboleth"}""", []),
        ("B14", """{"action": "next"}""", []),
        ("B15-focus", """{"action": "use", "targets": ["fishman-monk"], "resource": "Focus", "amount": 2}""", []),
        ("B15", """{"action": "damage", "targets": ["aboleth"], "amount": 20, "damage_type": "bludgeoning"}""", []),
        ("B15-resistance", """{"action": "legendary", "source": "aboleth", "resistance": true}""", []),
        ("B16-legendary", """{"action": "legendary", "source": "aboleth", "amount": 1, "name": "Psychic Drain"}""", []),
        ("B16-damage", """{"action": "damage", "targets": ["fishman-monk"], "amount": 10, "damage_type": "psychic", "source": "aboleth"}""", []),
        ("B16", """{"action": "heal", "targets": ["aboleth"], "dice": "1d10"}""", [5]),
        ("B17", """{"action": "next"}""", []),
        ("B18-first", """{"action": "damage", "targets": ["fishman-monk"], "amount": 12, "damage_type": "bludgeoning", "source": "aboleth"}""", []),
        ("B18-second", """{"action": "damage", "targets": ["fishman-monk"], "amount": 12, "damage_type": "bludgeoning", "source": "aboleth"}""", []),
        ("B18", """{"action": "damage", "targets": ["bjorn-mountainfell"], "amount": 10, "damage_type": "psychic", "source": "aboleth"}""", []),
        ("B19-next", """{"action": "next"}""", []),
        ("B19", """{"action": "damage", "targets": ["aboleth"], "parts": [{"amount": 24, "type": "bludgeoning"}, {"amount": 4, "type": "psychic"}]}""", []),
        ("B20-legendary", """{"action": "legendary", "source": "aboleth", "amount": 1, "name": "Lash"}""", []),
        ("B20", """{"action": "damage", "targets": ["fishman-monk"], "dice": "2d6+5", "damage_type": "bludgeoning", "critical": true, "source": "aboleth"}""", [4, 5, 2, 3]),
        ("B21", """{"action": "next"}""", []),
        ("B22", """{"action": "heal", "targets": ["fishman-monk"], "dice": "2d4+2", "source": "bjorn-mountainfell", "item": "Potion of Healing"}""", [3, 2]),
        ("B23", """{"action": "damage", "targets": ["aboleth"], "amount": 26, "damage_type": "slashing"}""", []),
        ("B24-legendary", """{"action": "legendary", "source": "aboleth", "amount": 1, "name": "Lash"}""", []),
        ("B24", """{"action": "damage", "targets": ["bjorn-mountainfell"], "amount": 12, "damage_type": "bludgeoning", "source": "aboleth"}""", []),
        ("B25", """{"action": "next"}""", []),
        ("B26", """{"action": "damage", "targets": ["aboleth"], "amount": 18, "damage_type": "bludgeoning"}""", []),
    ];

    /// <summary>Fixture A's sheets through campaign_character (contract §16): Belmakor's with his sim_profile and one 1st-level slot spent, then the five minimal ones.</summary>
    public static async Task WriteBelmakorSheetsAsync(McpServerHarness server)
    {
        await ScenarioCalls.Call(server, "campaign_character",
            $$"""{"action": "update", "campaign": "belmakor", "character": "character:belmakor", "sheet": {{BelmakorSheet}}, "sim_profile": {{BelmakorSimProfile}}}""");
        await ScenarioCalls.Call(server, "campaign_character", """{"action": "use", "campaign": "belmakor", "character": "character:belmakor", "slot_level": 1}""");
        foreach (var (handle, sheet) in BelmakorParty)
        {
            await ScenarioCalls.Call(server, "campaign_character", $$"""{"action": "update", "campaign": "belmakor", "character": "{{handle}}", "sheet": {{sheet}}}""");
        }
    }

    /// <summary>
    /// Fixture B's additions through the tools (FIX §3, §4.1, contract §13): the monk and the Nester in one campaign_write
    /// batch, the three PC sheets (the Dragon Slayer's sim_profile beside its sheet), and Björn's two potions.
    /// </summary>
    public static async Task WriteOnePieceFixtureAsync(McpServerHarness server)
    {
        await ScenarioCalls.Call(server, "campaign_write", OnePieceEntities);
        await ScenarioCalls.Call(server, "campaign_character",
            $$"""{"action": "update", "campaign": "one-piece", "character": "character:bjorn-mountainfell", "sheet": {{BjornSheet}}}""");
        await ScenarioCalls.Call(server, "campaign_character",
            $$"""{"action": "update", "campaign": "one-piece", "character": "character:fishman-monk", "sheet": {{MonkSheet}}}""");
        await ScenarioCalls.Call(server, "campaign_character",
            $$"""{"action": "update", "campaign": "one-piece", "character": "character:dragon-slayer", "sheet": {{DragonSlayerSheet}}, "sim_profile": {{DragonSlayerSimProfile}}}""");
        await ScenarioCalls.Call(server, "campaign_character", """
            {"action": "inventory", "campaign": "one-piece", "character": "character:bjorn-mountainfell",
             "items": [{"item": "Potion of Healing", "qty": 2, "srd": "2024/equipment/potion-of-healing"}]}
            """);
    }
}

/// <summary>
/// One server for an exit scenario: the production registrations with a <see cref="ScriptedDiceRoller"/> in place of the
/// real dice (contract §15 X2: "scripted IDiceRoller via McpServerHarness.WithExtraTools"), holding fixture A's or fixture
/// B's world built through the tools. The roller starts EMPTY: a step that rolls a die the test did not queue fails (the
/// roller throws), so fixture A proves it never rolls and fixture B queues its three rolls step by step.
///
/// <para>
/// Owned by whoever made it (a play fixture or a test that writes): <see cref="DisposeAsync"/> stops the server and deletes
/// its data directory.
/// </para>
/// </summary>
public sealed class ScenarioCombatServer : IAsyncDisposable
{
    private ScenarioCombatServer()
    {
        Server = McpServerHarness.WithExtraTools(builder => builder.Services.AddSingleton<IDiceRoller>(Dice));
    }

    /// <summary>The server's dice: enqueue the faces a call rolls before making it.</summary>
    public ScriptedDiceRoller Dice { get; } = new();

    /// <summary>The server (its own data directory).</summary>
    public McpServerHarness Server { get; }

    /// <summary>Fixture A's Phase 6 world (its fact handles: the dm's told fact), or null in fixture B's server.</summary>
    public ScenarioBelmakorWorld? Belmakor { get; private set; }

    /// <summary>The campaigns.db the server writes, read beside it by <see cref="ScenarioCombatStore"/>.</summary>
    public ScenarioCombatStore Store => new(Path.Combine(Server.DataDirectory, "campaigns.db"));

    /// <summary>Fixture A's world: Phase 6's Belmakor world (through the tools) and fixture A's sheets (through campaign_character).</summary>
    public static async Task<ScenarioCombatServer> BelmakorAsync()
    {
        var world = new ScenarioCombatServer();
        try
        {
            await world.Server.InitializeAsync();
            world.Belmakor = await ScenarioBelmakorWorld.BuildAsync(world.Server);
            await ScenarioCombatFixtures.WriteBelmakorSheetsAsync(world.Server);
            return world;
        }
        catch
        {
            await world.DisposeAsync();
            throw;
        }
    }

    /// <summary>Fixture B's world: Phase 6's One Piece world (through the tools), the monk, the Nester, the sheets and the potions.</summary>
    public static async Task<ScenarioCombatServer> OnePieceAsync()
    {
        var world = new ScenarioCombatServer();
        try
        {
            await world.Server.InitializeAsync();
            await ScenarioOnePieceWorld.BuildAsync(world.Server);
            await ScenarioCombatFixtures.WriteOnePieceFixtureAsync(world.Server);
            return world;
        }
        catch
        {
            await world.DisposeAsync();
            throw;
        }
    }

    /// <summary>A successful call's text (the test fails, with the server log, when it is an error).</summary>
    public Task<string> Call(string tool, string argumentsJson) => ScenarioCalls.Call(Server, tool, argumentsJson);

    /// <summary>A refused call's error text (the test fails when it succeeds).</summary>
    public Task<string> Fail(string tool, string argumentsJson) => ScenarioCalls.Fail(Server, tool, argumentsJson);

    /// <summary>A combat call that must succeed.</summary>
    public Task<string> Combat(string argumentsJson) => Call("combat", argumentsJson);

    /// <summary>
    /// A call's text whether it succeeded or was refused (a sweep reads both: a refusal must be as clean as a page), and
    /// which it was.
    /// </summary>
    public async Task<(string Text, bool Refused)> Read(string tool, string argumentsJson)
    {
        var result = await Server.CallToolJsonAsync(tool, argumentsJson);
        return (Server.SingleText(result), result.IsError == true);
    }

    /// <summary>Stops the server and deletes its data directory.</summary>
    public async ValueTask DisposeAsync() => await Server.DisposeAsync();
}

/// <summary>
/// Reads campaigns.db beside a running server (its own short connection each time, never pooled, as the WAL allows), for
/// what no tool prints: how many change_log rows exist, which tables a batch touched, the raw dice_roll and combat_log
/// rows, and a sheet row's every column. The exit criterion is about the store as much as the text: "HP ticks never enter
/// change_log", "combat rolls link to dice_roll rows", "undoing the write-back restores the sheets exactly".
/// </summary>
public sealed class ScenarioCombatStore(string path)
{
    /// <summary>One dice_roll row, with its session's number.</summary>
    public sealed record DiceRow(string Id, string Expression, string? Label, long Total, bool Secret, string Detail, long? SessionNumber, string? EncounterId);

    /// <summary>One combat_log row.</summary>
    public sealed record LogRow(long Seq, string Kind, long Round, long? Amount, string? Detail, string? RollId, string? ActorId, string? TargetId);

    /// <summary>The change_log's row count.</summary>
    public long ChangeRows() => Scalar("SELECT COUNT(*) FROM change_log");

    /// <summary>The tables a batch's rows target, distinct and sorted.</summary>
    public IReadOnlyList<string> BatchTables(string batchId) =>
        Strings("SELECT DISTINCT target_table FROM change_log WHERE batch_id = $p ORDER BY target_table", batchId);

    /// <summary>How many change_log rows a batch wrote.</summary>
    public long BatchRows(string batchId) => Scalar("SELECT COUNT(*) FROM change_log WHERE batch_id = $p", batchId);

    /// <summary>The batch an undo batch undoes (its rows' undo_of), or null when it undoes none.</summary>
    public string? UndoOf(string batchId) =>
        Strings("SELECT DISTINCT undo_of FROM change_log WHERE batch_id = $p AND undo_of IS NOT NULL", batchId).SingleOrDefault();

    /// <summary>An encounter's id by its name in a campaign.</summary>
    public string EncounterId(string campaign, string name) =>
        Strings("SELECT e.id FROM encounter e JOIN campaign c ON c.id = e.campaign_id WHERE c.slug = $p AND e.name = $q", campaign, name).Single();

    /// <summary>An encounter's status and write-back batch id.</summary>
    public (string Status, string? WritebackBatchId) Encounter(string encounterId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status, writeback_batch_id FROM encounter WHERE id = $p";
        command.Parameters.AddWithValue("$p", encounterId);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    /// <summary>Every dice_roll row of a campaign, in roll order.</summary>
    public IReadOnlyList<DiceRow> Dice(string campaign)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.id, d.expression, d.label, d.total, d.secret, d.detail, s.number, d.encounter_id
            FROM dice_roll d JOIN campaign c ON c.id = d.campaign_id LEFT JOIN session s ON s.entity_id = d.session_id
            WHERE c.slug = $p ORDER BY d.seq
            """;
        command.Parameters.AddWithValue("$p", campaign);
        using var reader = command.ExecuteReader();
        var rows = new List<DiceRow>();
        while (reader.Read())
        {
            rows.Add(new DiceRow(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt64(3),
                reader.GetInt64(4) == 1, reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return rows;
    }

    /// <summary>An encounter's combat_log rows, in order.</summary>
    public IReadOnlyList<LogRow> CombatLog(string encounterId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT seq, kind, round, amount, detail, roll_id, actor_id, target_id FROM combat_log WHERE encounter_id = $p ORDER BY seq";
        command.Parameters.AddWithValue("$p", encounterId);
        using var reader = command.ExecuteReader();
        var rows = new List<LogRow>();
        while (reader.Read())
        {
            rows.Add(new LogRow(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return rows;
    }

    /// <summary>
    /// Every sheet-side row of a campaign, every column (sheets by their character's slug; holdings, coins and awards by
    /// holder and name), as one comparable text: what an undo must restore exactly and what a combat step must never touch.
    /// <paramref name="withUpdatedAt"/> false leaves out the columns an undo may re-stamp (updated_at).
    /// </summary>
    public string SheetTables(string campaign, bool withUpdatedAt = true)
    {
        var lines = new List<string>();
        lines.AddRange(Rows("SELECT e.slug AS who, s.* FROM character_sheet s JOIN entity e ON e.id = s.entity_id JOIN campaign c ON c.id = e.campaign_id WHERE c.slug = $p ORDER BY e.slug", campaign, withUpdatedAt).Select(r => "sheet " + r));
        lines.AddRange(Rows("SELECT e.slug AS who, h.* FROM holding h JOIN entity e ON e.id = h.holder_id JOIN campaign c ON c.id = h.campaign_id WHERE c.slug = $p ORDER BY e.slug, h.name", campaign, withUpdatedAt).Select(r => "holding " + r));
        lines.AddRange(Rows("SELECT e.slug AS who, t.* FROM currency_txn t JOIN entity e ON e.id = t.holder_id JOIN campaign c ON c.id = t.campaign_id WHERE c.slug = $p ORDER BY e.slug, t.created_at", campaign, withUpdatedAt).Select(r => "coins " + r));
        lines.AddRange(Rows("SELECT e.slug AS who, a.* FROM award a JOIN entity e ON e.id = a.recipient_id JOIN campaign c ON c.id = a.campaign_id WHERE c.slug = $p ORDER BY e.slug, a.created_at", campaign, withUpdatedAt).Select(r => "award " + r));
        return string.Join("\n", lines);
    }

    /// <summary>
    /// The holdings, coin entries and awards of a campaign as comparable lines, every column a reader can tell apart but the
    /// ids and timestamps: the holder by its slug and a session by its NUMBER (so a line reads the same in every run). What
    /// the write-back creates (loot with the session it came in, the coins' note, the awards' session, kind and source) is
    /// only ever printed in part, so the exit test pins it here, at the store.
    /// </summary>
    public IReadOnlyList<string> LedgerRows(string campaign)
    {
        var lines = new List<string>();
        lines.AddRange(Rows(
            """
            SELECT e.slug AS who, h.name, h.srd_ref, h.quantity, h.equipped, h.attuned, h.charges, s.number AS session, h.notes
            FROM holding h JOIN entity e ON e.id = h.holder_id JOIN campaign c ON c.id = h.campaign_id LEFT JOIN session s ON s.entity_id = h.acquired_session_id
            WHERE c.slug = $p ORDER BY e.slug, h.name
            """, campaign, withUpdatedAt: true).Select(r => "holding " + r));
        lines.AddRange(Rows(
            """
            SELECT e.slug AS who, s.number AS session, t.cp, t.sp, t.ep, t.gp, t.pp, t.note
            FROM currency_txn t JOIN entity e ON e.id = t.holder_id JOIN campaign c ON c.id = t.campaign_id LEFT JOIN session s ON s.entity_id = t.session_id
            WHERE c.slug = $p ORDER BY e.slug, t.created_at
            """, campaign, withUpdatedAt: true).Select(r => "coins " + r));
        lines.AddRange(Rows(
            """
            SELECT e.slug AS who, s.number AS session, a.kind, a.amount, a.note, a.source
            FROM award a JOIN entity e ON e.id = a.recipient_id JOIN campaign c ON c.id = a.campaign_id LEFT JOIN session s ON s.entity_id = a.session_id
            WHERE c.slug = $p ORDER BY e.slug, a.created_at
            """, campaign, withUpdatedAt: true).Select(r => "award " + r));
        return lines;
    }

    /// <summary>An entity's stored status (alive, dead, …) by its slug.</summary>
    public string? EntityStatus(string campaign, string slug) =>
        Strings("SELECT e.status FROM entity e JOIN campaign c ON c.id = e.campaign_id WHERE c.slug = $p AND e.slug = $q", campaign, slug).SingleOrDefault();

    /// <summary>One sheet column of a character, as stored (JSON text or a number's text; null when the column is null or there is no sheet).</summary>
    public string? SheetColumn(string campaign, string slug, string column)
    {
        Assert.Matches("^[a-z_]+$", column);
        return Strings($"SELECT CAST(s.{column} AS TEXT) FROM character_sheet s JOIN entity e ON e.id = s.entity_id JOIN campaign c ON c.id = e.campaign_id WHERE c.slug = $p AND e.slug = $q", campaign, slug).SingleOrDefault();
    }

    private IEnumerable<string> Rows(string sql, string campaign, bool withUpdatedAt)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$p", campaign);
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            var cells = new List<string>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (!withUpdatedAt && reader.GetName(i) == "updated_at")
                {
                    continue;
                }

                cells.Add(reader.GetName(i) + "=" + (reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)));
            }

            rows.Add(string.Join(" | ", cells));
        }

        return rows;
    }

    private long Scalar(string sql, params string[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        Bind(command, parameters);
        return (long)command.ExecuteScalar()!;
    }

    private List<string> Strings(string sql, params string[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        Bind(command, parameters);
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                values.Add(reader.GetString(0));
            }
        }

        return values;
    }

    private static void Bind(SqliteCommand command, string[] parameters)
    {
        if (parameters.Length > 0)
        {
            command.Parameters.AddWithValue("$p", parameters[0]);
        }

        if (parameters.Length > 1)
        {
            command.Parameters.AddWithValue("$q", parameters[1]);
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        return connection;
    }
}

/// <summary>
/// The texts the scenarios take apart: a combat result's sections, its initiative table and a board's rows, the batch ids a
/// write or an undo printed, and every call a text prints.
/// </summary>
internal static partial class ScenarioCombatText
{
    /// <summary>One board row (§6.12): the turn marker, the row number, the name the view uses, its ref (null when none is printed), the status and the conditions.</summary>
    public sealed record BoardRow(bool Turn, int Number, string Name, string? Ref, string Status, string Conditions);

    /// <summary>
    /// One author initiative-table row: the turn marker, the tracker name (with no handle or hidden mark), whether it is
    /// struck through, its side (party, ally, enemy, neutral: which board rows may carry numbers, §6.12), and its HP and
    /// conditions cells.
    /// </summary>
    public sealed record TableRow(bool Turn, string Name, bool Struck, string Side, string Hp, string Conditions);

    /// <summary>The lines of a "## {heading}" section (each "- " line, without the dash), empty when the result has none.</summary>
    public static IReadOnlyList<string> Section(string text, string heading)
    {
        var start = text.IndexOf("\n## " + heading + "\n", StringComparison.Ordinal);
        if (start < 0)
        {
            return [];
        }

        var body = text[(start + heading.Length + 5)..];
        var end = body.IndexOf("\n\n", StringComparison.Ordinal);
        return (end < 0 ? body : body[..end]).Split('\n').Where(l => l.StartsWith("- ", StringComparison.Ordinal)).Select(l => l[2..]).ToList();
    }

    /// <summary>A board's rows (the markdown table of <c>combat state {perspective}</c>).</summary>
    public static IReadOnlyList<BoardRow> Board(string board)
    {
        var rows = new List<BoardRow>();
        foreach (Match match in BoardRowPattern().Matches(board))
        {
            var name = match.Groups["name"].Value;
            var reference = NameRef().Match(name);
            rows.Add(new BoardRow(match.Groups["turn"].Value == "▶", int.Parse(match.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture),
                reference.Success ? reference.Groups[1].Value : name, reference.Success ? reference.Groups[2].Value : null,
                match.Groups["status"].Value, match.Groups["conditions"].Value));
        }

        return rows;
    }

    /// <summary>An author result's initiative-table rows (the turn-holder marked, the dead struck through).</summary>
    public static List<TableRow> Table(string text)
    {
        var rows = new List<TableRow>();
        foreach (Match match in TableRowPattern().Matches(text))
        {
            var cell = match.Groups["name"].Value;
            var struck = cell.StartsWith("~~", StringComparison.Ordinal);
            var name = cell.Trim('~');
            name = Regex.Replace(name, @" \((?:character:[a-z0-9-]+|hidden|surprised)\)", string.Empty);
            rows.Add(new TableRow(match.Groups["turn"].Value == "▶", name, struck, match.Groups["side"].Value, match.Groups["hp"].Value, match.Groups["conditions"].Value));
        }

        return rows;
    }

    /// <summary>The batch id a write result printed ("Batch `…`") or an undo printed ("as a new batch `…`").</summary>
    public static string Batch(string text)
    {
        var match = BatchPattern().Match(text);
        Assert.True(match.Success, $"No batch id in:\n{text}");
        return match.Groups[1].Value;
    }

    /// <summary>
    /// Every call a text prints for the model to send — <c>combat {…}</c>, <c>campaign_… {…}</c>, <c>balance_simulate {…}</c>,
    /// <c>encounter_difficulty {…}</c> — each from its tool name to the brace that closes its object (braces inside JSON
    /// strings do not count), so a call holding a nested object or a name with a quote is taken whole.
    /// </summary>
    public static IReadOnlyList<string> PrintedCalls(string text)
    {
        var calls = new List<string>();
        foreach (Match match in CallStart().Matches(text))
        {
            var open = match.Index + match.Length - 1;
            var depth = 0;
            var inString = false;
            for (var i = open; i < text.Length; i++)
            {
                var c = text[i];
                if (inString)
                {
                    if (c == '\\')
                    {
                        i++;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                }
                else if (c == '{')
                {
                    depth++;
                }
                else if (c == '}' && --depth == 0)
                {
                    calls.Add(text[match.Index..(i + 1)]);
                    break;
                }
            }
        }

        return calls;
    }

    /// <summary>A printed call's arguments (the object after the tool's name).</summary>
    public static string Arguments(string call) => call[(call.IndexOf('{', StringComparison.Ordinal))..];

    [GeneratedRegex(@"^\| (?<turn>▶?) \| (?<n>\d+) \| (?<name>[^|]+) \| (?<status>[^|]+) \| (?<conditions>[^|]+) \|$", RegexOptions.Multiline)]
    private static partial Regex BoardRowPattern();

    [GeneratedRegex(@"^\| (?<turn>▶?) \| (?:\d+|—) \| (?:[\d.]+|—) \| (?<name>[^|]+) \| (?<side>[a-z]+) \| (?<hp>[^|]+) \| (?<ac>[^|]+) \| (?<conditions>[^|]+) \|$", RegexOptions.Multiline)]
    private static partial Regex TableRowPattern();

    [GeneratedRegex(@"^(.+) \((e:\d+|[a-z]+:[a-z0-9-]+)\)$")]
    private static partial Regex NameRef();

    [GeneratedRegex("[Bb]atch `([0-9a-f-]{36})`")]
    private static partial Regex BatchPattern();

    [GeneratedRegex(@"\b(?:combat|campaign(?:_[a-z]+)?|balance_simulate|encounter_difficulty) \{")]
    private static partial Regex CallStart();
}
