namespace DndMcp.Domain.Characters;

/// <summary>
/// The columns of <c>character_sheet</c> (contract §3), by name and in table order: what <see cref="SheetJson"/> reads
/// and writes, what a <see cref="SheetDiff"/> names, and what the Repository passes to its <c>ChangeRecorder</c>.
///
/// <para>
/// <b>Why the names are here and not only in the Repository's row record:</b> the sheet operations live in the Domain and
/// report which columns they changed; a change named by a string the table does not have would only fail when the
/// Repository tried to log it. The Repository's catalogue (<c>CampaignTables.CharacterSheet</c>) and this list must
/// agree column for column; stage 2 pins that.
/// </para>
/// </summary>
public static class SheetColumns
{
    public const string EntityId = "entity_id";
    public const string Player = "player";
    public const string Ruleset = "ruleset";
    public const string Species = "species";
    public const string Lineage = "lineage";
    public const string Background = "background";
    public const string Size = "size";
    public const string Classes = "classes";
    public const string Level = "level";
    public const string Xp = "xp";
    public const string Abilities = "abilities";
    public const string Saves = "saves";
    public const string Skills = "skills";
    public const string Ac = "ac";
    public const string MaxHp = "max_hp";
    public const string MaxHpReduction = "max_hp_reduction";
    public const string Hp = "hp";
    public const string TempHp = "temp_hp";
    public const string Speed = "speed";
    public const string Movement = "movement";
    public const string Senses = "senses";
    public const string InitiativeBonus = "initiative_bonus";
    public const string PassivePerception = "passive_perception";
    public const string SpellSaveDc = "spell_save_dc";
    public const string SpellAttack = "spell_attack";
    public const string Defenses = "defenses";
    public const string HitDice = "hit_dice";
    public const string SpellSlots = "spell_slots";
    public const string Resources = "resources";
    public const string Conditions = "conditions";
    public const string Concentration = "concentration";
    public const string DeathSaves = "death_saves";
    public const string Exhaustion = "exhaustion";
    public const string Inspiration = "inspiration";
    public const string Feats = "feats";
    public const string Features = "features";
    public const string Spells = "spells";
    public const string Languages = "languages";
    public const string SimProfile = "sim_profile";
    public const string NotesMd = "notes_md";
    public const string SheetSource = "sheet_source";
    public const string CreatedAt = "created_at";
    public const string UpdatedAt = "updated_at";

    /// <summary>Every column, in the order the migration creates them.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        EntityId, Player, Ruleset, Species, Lineage, Background, Size, Classes, Level, Xp, Abilities, Saves, Skills, Ac, MaxHp,
        MaxHpReduction, Hp, TempHp, Speed, Movement, Senses, InitiativeBonus, PassivePerception, SpellSaveDc, SpellAttack,
        Defenses, HitDice, SpellSlots, Resources, Conditions, Concentration, DeathSaves, Exhaustion, Inspiration, Feats,
        Features, Spells, Languages, SimProfile, NotesMd, SheetSource, CreatedAt, UpdatedAt,
    ];

    /// <summary>
    /// The JSON object columns logged one top-level key at a time (the Repository's <c>LoggedPerKey</c>): a change to one
    /// slot level, resource or die size is its own change_log row, so undoing it never reverts another key changed later.
    /// A <see cref="SheetDiff"/> gives these as merge patches.
    /// </summary>
    public static readonly IReadOnlyList<string> PerKey = [Abilities, HitDice, SpellSlots, Resources];

    /// <summary>The integer columns (stored as SQLite INTEGER; <see cref="Inspiration"/> as 0/1).</summary>
    public static readonly IReadOnlyList<string> Integers =
    [
        Level, Xp, Ac, MaxHp, MaxHpReduction, Hp, TempHp, Speed, InitiativeBonus, PassivePerception, SpellSaveDc, SpellAttack,
        Exhaustion, Inspiration,
    ];

    /// <summary>The JSON columns (objects and arrays; <see cref="Concentration"/> and <see cref="SimProfile"/> nullable).</summary>
    public static readonly IReadOnlyList<string> Json =
    [
        Classes, Abilities, Saves, Skills, Movement, Senses, Defenses, HitDice, SpellSlots, Resources, Conditions, Concentration,
        DeathSaves, Feats, Features, Spells, Languages, SimProfile,
    ];

    /// <summary>
    /// The fields a fight owns while a sheet-seeded combatant is in it (contract §6.7): written back at its end when they
    /// changed, and checked for drift against the combatant's <c>sheet_snapshot</c>. For <see cref="SpellSlots"/> and
    /// <see cref="Resources"/> only each key's <c>used</c> is the fight's.
    /// </summary>
    public static readonly IReadOnlyList<string> CombatOwned =
        [Hp, TempHp, MaxHpReduction, DeathSaves, Conditions, Concentration, Exhaustion, SpellSlots, Resources];

    /// <summary>Columns the sheet operations never change: the Repository sets them (the key and the clock).</summary>
    public static readonly IReadOnlyList<string> Managed = [EntityId, CreatedAt, UpdatedAt];
}
