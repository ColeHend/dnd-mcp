using DndMcp.Domain.Encounters;
using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation;
using Q = DndMcp.Domain.Simulation.StatBlockValues.DamageQualifiers;
using T = DndMcp.Domain.Simulation.StatBlockValues.TraitKinds;

namespace DndMcp.Tests.Features;

/// <summary>
/// Hand-built stat blocks for target tests. They are shaped like SRD creatures (numbers from the 2014 SRD where the name
/// says so) but built here, so a target test pins what the RESOLVER and the ENGINE do with a stat block, independent of
/// the monster normalizer, whose own tests pin what it reads from the data.
/// </summary>
internal static class TargetStatBlocks
{
    /// <summary>
    /// Werewolf-like (2014 SRD werewolf numbers: AC 11, HP 58, Str 15 Dex 13 Con 14 Int 10 Wis 11 Cha 10, CR 3), with
    /// RESISTANCE to bludgeoning, piercing and slashing from nonmagical attacks that aren't silvered, and cold resistance
    /// unqualified. (The 2014 werewolf is immune; resistance shows the halving.)
    /// </summary>
    public static StatBlock WerewolfLike() => Create(
        "Werewolf", "2014", "3", ac: 11, hp: 58, abilities: new ResolvedAbilities(15, 13, 14, 10, 11, 10),
        resistances:
        [
            new DamageAdjustment("cold", null, "cold"),
            Bps("bludgeoning", Q.NonmagicalNotSilvered),
            Bps("piercing", Q.NonmagicalNotSilvered),
            Bps("slashing", Q.NonmagicalNotSilvered),
        ],
        acNote: "12 in wolf or hybrid form");

    /// <summary>The 2014 werewolf as printed: IMMUNE to B/P/S from nonmagical attacks that aren't silvered.</summary>
    public static StatBlock Werewolf2014() => Create(
        "Werewolf", "2014", "3", ac: 11, hp: 58, abilities: new ResolvedAbilities(15, 13, 14, 10, 11, 10),
        immunities: [Bps("bludgeoning", Q.NonmagicalNotSilvered), Bps("piercing", Q.NonmagicalNotSilvered), Bps("slashing", Q.NonmagicalNotSilvered)]);

    /// <summary>Young Red Dragon-like (2014: CR 10, AC 18, HP 178; saves Dex +4, Con +9, Wis +4, Cha +8): immune to fire.</summary>
    public static StatBlock FireDragon() => Create(
        "Young Red Dragon", "2014", "10", ac: 18, hp: 178, abilities: new ResolvedAbilities(23, 10, 21, 14, 11, 19),
        saves: new Dictionary<string, int> { ["str"] = 6, ["dex"] = 4, ["con"] = 9, ["int"] = 2, ["wis"] = 4, ["cha"] = 8 },
        immunities: [new DamageAdjustment("fire", null, "fire")]);

    /// <summary>
    /// A legendary caster (2024-shaped: CR 17, AC 17, HP 199; Con +9, Int +11, Wis +9): Magic Resistance and Legendary
    /// Resistance 3 (4 in its lair), a Parry reaction and Regeneration, immune to the frightened condition.
    /// </summary>
    public static StatBlock LegendaryCaster() => Create(
        "Archlich", "2024", "17", ac: 17, hp: 199, abilities: new ResolvedAbilities(11, 16, 16, 20, 14, 16),
        saves: new Dictionary<string, int> { ["str"] = 0, ["dex"] = 3, ["con"] = 9, ["int"] = 11, ["wis"] = 9, ["cha"] = 3 },
        traits:
        [
            Trait("Magic Resistance", T.MagicResistance),
            Trait("Legendary Resistance", T.LegendaryResistance),
            Trait("Regeneration", T.Regeneration),
        ],
        reactions: [Parry()],
        conditionImmunities: ["frightened"],
        legendaryResistance: 3,
        legendaryResistanceInLair: 4);

    /// <summary>
    /// Helmed Horror-like (2014: CR 4, AC 20, HP 60, Str 18 Dex 13 Con 16 Int 10 Wis 10 Cha 10): resistance to B/P/S from
    /// nonmagical attacks that aren't adamantine; immune to force, necrotic and poison; immune to stunned, paralyzed and
    /// more; Magic Resistance.
    /// </summary>
    public static StatBlock StunImmune() => Create(
        "Helmed Horror", "2014", "4", ac: 20, hp: 60, abilities: new ResolvedAbilities(18, 13, 16, 10, 10, 10),
        resistances: [Bps("bludgeoning", Q.NonmagicalNotAdamantine), Bps("piercing", Q.NonmagicalNotAdamantine), Bps("slashing", Q.NonmagicalNotAdamantine)],
        immunities: [new DamageAdjustment("force", null, "force"), new DamageAdjustment("necrotic", null, "necrotic"), new DamageAdjustment("poison", null, "poison")],
        conditionImmunities: ["blinded", "charmed", "deafened", "frightened", "paralyzed", "petrified", "poisoned", "stunned"],
        traits: [Trait("Magic Resistance", T.MagicResistance)]);

    /// <summary>Black Pudding-like (2014: CR 4, AC 7, HP 85): immune to prone (and acid, cold, lightning, slashing).</summary>
    public static StatBlock ProneImmune() => Create(
        "Black Pudding", "2014", "4", ac: 7, hp: 85, abilities: new ResolvedAbilities(16, 5, 16, 1, 6, 1),
        immunities: [new DamageAdjustment("acid", null, "acid"), new DamageAdjustment("cold", null, "cold"), new DamageAdjustment("lightning", null, "lightning"), new DamageAdjustment("slashing", null, "slashing")],
        conditionImmunities: ["blinded", "charmed", "deafened", "exhaustion", "frightened", "prone"]);

    /// <summary>Ogre-like (2024: CR 2, AC 11, HP 68, Str 19 Dex 8 Con 16 Int 5 Wis 7 Cha 7): nothing special.</summary>
    public static StatBlock Ogre() => Create("Ogre", "2024", "2", ac: 11, hp: 68, abilities: new ResolvedAbilities(19, 8, 16, 5, 7, 7));

    /// <summary>
    /// Any stat block; saves default to the ability modifiers (the normalizer's rule for non-proficient saves).
    /// </summary>
    public static StatBlock Create(
        string name,
        string edition,
        string cr,
        int ac,
        int hp,
        ResolvedAbilities abilities,
        IReadOnlyDictionary<string, int>? saves = null,
        IReadOnlyList<DamageAdjustment>? resistances = null,
        IReadOnlyList<DamageAdjustment>? immunities = null,
        IReadOnlyList<DamageAdjustment>? vulnerabilities = null,
        IReadOnlyList<string>? conditionImmunities = null,
        IReadOnlyList<StatBlockTrait>? traits = null,
        IReadOnlyList<StatBlockAction>? reactions = null,
        int legendaryResistance = 0,
        int? legendaryResistanceInLair = null,
        string? acNote = null)
    {
        var challenge = ChallengeRating.Parse(cr);
        return new StatBlock
        {
            Ref = $"{edition}/monster/{name.ToLowerInvariant().Replace(' ', '-')}",
            Name = name,
            Edition = edition,
            Size = "Large",
            CreatureType = "monstrosity",
            ChallengeRating = challenge,
            Xp = ChallengeRatingTables.Xp(challenge),
            ProficiencyBonus = ChallengeRatingTables.ProficiencyBonus(challenge),
            ArmorClass = ac,
            ArmorClassNote = acNote,
            HitPoints = hp,
            HitDice = DamageFormula.ParseDamage("7d10+14", "hit dice"),
            Abilities = abilities,
            SaveBonuses = saves ?? DslValues.Abilities.All.ToDictionary(a => a, abilities.Modifier),
            InitiativeBonus = abilities.Modifier("dex"),
            Speeds = new Dictionary<string, int> { ["walk"] = 30 },
            Resistances = resistances ?? [],
            Immunities = immunities ?? [],
            Vulnerabilities = vulnerabilities ?? [],
            ConditionImmunities = conditionImmunities ?? [],
            Traits = traits ?? [],
            Actions = [],
            BonusActions = [],
            Reactions = reactions ?? [],
            Spells = [],
            SpellSlots = new Dictionary<int, int>(),
            Multiattacks = [],
            LegendaryResistance = legendaryResistance,
            LegendaryResistanceInLair = legendaryResistanceInLair,
            Notes = [],
            Warnings = [],
        };
    }

    private static DamageAdjustment Bps(string type, string qualifier) =>
        new(type, qualifier, $"{type} from nonmagical attacks{(qualifier == Q.NonmagicalNotSilvered ? " that aren't silvered" : qualifier == Q.NonmagicalNotAdamantine ? " that aren't adamantine" : "")}");

    private static StatBlockTrait Trait(string name, string kind) => new() { Name = name, Kind = kind, Text = name + "." };

    private static StatBlockAction Parry() => new()
    {
        Name = "Parry",
        Kind = StatBlockValues.ActionKinds.Parry,
        Slot = StatBlockValues.ActionSlots.Reaction,
        AcBonus = 3,
        Text = "Parry.",
    };
}
