using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Characters;
using DndMcp.Domain.Features;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Characters;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tests.CampaignScenarios;

namespace DndMcp.Tests.CampaignCharacters;

/// <summary>
/// The exit-criteria fixtures' sheets (FIX §1, §3, as amended by contract §16), written onto the Phase 6 scenario worlds
/// through the Repository's own writers: the sheets and inventories through <see cref="CharacterWriter"/>, the One Piece
/// entities the Phase 6 world lacks through the Phase 6 write path (one campaign_write batch). Nothing here edits a Phase 6
/// fixture file, and nothing is seeded with raw SQL, so every sheet K's and X's scenarios fight with is one the real path
/// made. Every sheet says <c>sheet_source: "fixture"</c> (its numbers are invented where the skills give none).
///
/// <para>
/// No session is live while these are written, so every batch is timeless (sessions 4 and 13, the fights, are started by
/// the scenarios themselves: A0/B0). Belmakor's pre-fight state (FIX §2.1) is the update's <c>temp_hp: 7</c> (False Life)
/// and one 1st-level slot spent through <c>use</c>.
/// </para>
/// </summary>
internal static class FixtureSheets
{
    /// <summary>The fixture's reason on every batch (history says where the sheets came from).</summary>
    public const string Reason = "Phase 7 fixture sheets (FIX §1, §3; invented numbers flagged sheet_source fixture).";

    /// <summary>FIX §1 / §2.1: Belmakor Silverwind, 2014 Bladesinger 12 (invented AC, HP 110, resources and spells).</summary>
    public const string BelmakorJson = """
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

    /// <summary>Contract §16: fixture A's minimal sheets (classes and levels only; Torch's max_hp given, FIX §1.4).</summary>
    public static readonly IReadOnlyList<(string Handle, string Json)> BelmakorParty =
    [
        ("character:vars", """{ "classes": [{ "class": "ranger", "level": 6 }, { "class": "rogue", "level": 6 }], "sheet_source": "fixture" }"""),
        ("character:aiden-ironstar", """{ "classes": [{ "class": "paladin", "level": 6 }, { "class": "sorcerer", "level": 6 }], "sheet_source": "fixture" }"""),
        ("character:ignis", """{ "classes": [{ "class": "bard", "level": 12 }], "sheet_source": "fixture" }"""),
        ("character:serif", """{ "classes": [{ "class": "artificer", "subclass": "Battle Smith", "level": 12, "hit_die": 8 }], "sheet_source": "fixture" }"""),
        ("character:torch", """{ "classes": [{ "class": "wizard", "level": 12 }], "max_hp": 74, "sheet_source": "fixture" }"""),
    ];

    /// <summary>FIX §3: Björn Mountainfell, 2024 Barbarian 8 (HP 85 derived from Con 16).</summary>
    public const string BjornJson = """
        { "ruleset": "2024", "classes": [{ "class": "barbarian", "subclass": "Path of the Totem Warrior", "level": 8 }],
          "abilities": { "str": 18, "dex": 14, "con": 16 }, "ac": 15, "initiative_bonus": 2, "xp": 34000,
          "resources": [{ "name": "Rage", "max": 4, "recharge": "short_rest_one" }], "sheet_source": "fixture" }
        """;

    /// <summary>FIX §3: the fishman monk, 2024 Monk 8 of the homebrew Order of the Deep Sea (HP 59 derived from Con 14).</summary>
    public const string FishmanMonkJson = """
        { "ruleset": "2024", "classes": [{ "class": "monk", "subclass": "Order of the Deep Sea", "level": 8 }],
          "abilities": { "dex": 16, "con": 14, "wis": 16 }, "ac": 16, "initiative_bonus": 3, "spell_save_dc": 14, "xp": 34000,
          "resources": [{ "name": "Focus", "max": 8, "recharge": "short_rest" }], "sheet_source": "fixture" }
        """;

    /// <summary>FIX §3: the amethyst Dragon Slayer, homebrew class (d10; saves Con, Str), level 8 (HP 68 derived from Con 14).</summary>
    public const string DragonSlayerJson = """
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

    /// <summary>FIX §4.1: the secret of the Nester (author-only text: no non-author output may carry any of it).</summary>
    public const string NesterSecret = "Void-touched; what nests here is close to what the Wraiths are: the drowned, unmourned dead.";

    /// <summary>The One Piece fixture's potion (FIX §3: two, SRD 2024).</summary>
    public const string PotionOfHealing = "Potion of Healing";

    /// <summary>The potion's SRD ref.</summary>
    public const string PotionRef = "2024/equipment/potion-of-healing";

    public static SheetSpec Spec(string json) => DslJson.Deserialize<SheetSpec>(json, "sheet");

    public static BuildSpec Profile(string json) => DslJson.Deserialize<BuildSpec>(json, "sim_profile");

    /// <summary>
    /// Fixture A's sheets on the Belmakor world (FIX §1, §2.1, contract §16): Belmakor's full sheet with his sim_profile and
    /// one 1st-level slot spent, the five minimal sheets; Tristan (dead) gets none.
    /// </summary>
    public static CharacterWriter Belmakor(BelmakorScenario world, ICombatRouter? router = null)
    {
        var writer = new CharacterWriter(world.F.Db.Database, router);
        var context = new WriteContext { Reason = Reason };
        writer.Update(world.Campaign, "character:belmakor", Spec(BelmakorJson), Profile(BelmakorSimProfile), context);
        writer.Use(world.Campaign, "character:belmakor", 1, false, null, 1, context);
        foreach (var (handle, json) in BelmakorParty)
        {
            writer.Update(world.Campaign, handle, Spec(json), null, context);
        }

        return writer;
    }

    /// <summary>
    /// Fixture B's additions to the One Piece world (FIX §3, §4.1, contract §13): the fishman monk (a current party member)
    /// and the Nester (restricted, its secret) in one campaign_write batch; the three PC sheets (XP 34,000 each) and the
    /// Dragon Slayer's sim_profile; Björn's two potions through inventory.
    /// </summary>
    public static CharacterWriter OnePiece(OnePieceScenario world, ICombatRouter? router = null)
    {
        world.F.Apply(world.Campaign, new WriteContext { Reason = Reason },
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "The fishman monk", Slug = "fishman-monk", Subtype = "pc", Visibility = "party" },
            new CampaignOpSpec { Op = "link", From = "character:fishman-monk", Rel = "member_of", To = "faction:the-party", Status = "current" },
            new CampaignOpSpec
            {
                Op = "upsert", Kind = "character", Name = "The Nester", Slug = "the-nester", Subtype = "npc", Visibility = "restricted", Status = "alive",
                SecretMd = NesterSecret,
            });
        var writer = new CharacterWriter(world.F.Db.Database, router);
        var context = new WriteContext { Reason = Reason };
        writer.Update(world.Campaign, "character:bjorn-mountainfell", Spec(BjornJson), null, context);
        writer.Update(world.Campaign, "character:fishman-monk", Spec(FishmanMonkJson), null, context);
        writer.Update(world.Campaign, "character:dragon-slayer", Spec(DragonSlayerJson), Profile(DragonSlayerSimProfile), context);
        writer.Inventory(world.Campaign, "character:bjorn-mountainfell", [new InventoryItem { Item = PotionOfHealing, Qty = 2, Srd = PotionRef }], context);
        return writer;
    }
}
