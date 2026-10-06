namespace DndMcp.Domain.Characters;

/// <summary>
/// Every bound a character sheet's input is checked against, in one place so the validator, the messages and the tests
/// quote the same numbers. The integer columns that have a CHECK in <c>0002_characters_combat.sql</c> use the same ranges
/// here (AC 0-50, max HP 1-5000), so a value the validator passes is never refused by the database as a generic error.
/// </summary>
public static class SheetLimits
{
    /// <summary>Player, species, lineage, background, class, subclass, resource and list-item names: one line.</summary>
    public const int MaxNameLength = 80;

    /// <summary>A resource's state ("set") and a condition's or resource's note.</summary>
    public const int MaxNoteLength = 500;

    /// <summary>notes (→ notes_md): the sheet's free text.</summary>
    public const int MaxNotesLength = 20_000;

    /// <summary>feats, features, spells, languages: items per list.</summary>
    public const int MaxListItems = 100;

    /// <summary>Resources on one sheet.</summary>
    public const int MaxResources = 40;

    /// <summary>A counted resource's maximum (a Lay on Hands pool is 5 × level = 100 at 20).</summary>
    public const int MaxResourceUses = 999;

    public const int MinAc = 0;
    public const int MaxAc = 50;

    public const int MinMaxHp = 1;
    public const int MaxHp = 5000;

    public const int MaxXp = 100_000_000;

    public const int MaxSpeed = 1000;

    /// <summary>The simulator's range, so a sheet's bonus always fits a simulation entry.</summary>
    public const int MinInitiativeBonus = -10;
    public const int MaxInitiativeBonus = 20;

    public const int MinPassivePerception = 0;
    public const int MaxPassivePerception = 50;

    public const int MinSpellSaveDc = 1;
    public const int MaxSpellSaveDc = 40;

    public const int MinSpellAttack = -10;
    public const int MaxSpellAttack = 30;

    /// <summary>A flat extra on one saving throw (<c>saves.bonus</c>).</summary>
    public const int MaxSaveBonus = 20;

    /// <summary>Slots of one spell level (the class tables give at most 4; a homebrew or multiclass sheet may give more).</summary>
    public const int MaxSlotsPerLevel = 10;

    public const int MaxSpellLevel = 9;

    /// <summary>Pact Magic slots are of one level, 1-5, and number 1-4 in both editions' tables.</summary>
    public const int MaxPactLevel = 5;
    public const int MaxPactSlots = 4;

    /// <summary>The hit dice a class may have: the SRD's d6/d8/d10/d12 and a homebrew d4.</summary>
    public static readonly IReadOnlyList<int> HitDice = [4, 6, 8, 10, 12];

    public const int MaxExhaustion = 6;

    /// <summary>A level-up's hit point gain when given (2024: at least 1).</summary>
    public const int MinLevelUpHp = -10;
    public const int MaxLevelUpHp = 100;

    /// <summary>The 2014 rules give no minimum per level; 2024: "add the total (minimum of 1)".</summary>
    public const int MinLevelUpHp2024 = 1;

    /// <summary>Uses spent or restored by one <c>use</c> call.</summary>
    public const int MaxUseAmount = 999;

    /// <summary>XP gained or lost by one <c>xp</c> call.</summary>
    public const int MaxXpAmount = 10_000_000;

    /// <summary>The rounds in a short rest (1 hour): timed effects with this many rounds left or fewer end during one.</summary>
    public const int ShortRestRounds = 600;
}
