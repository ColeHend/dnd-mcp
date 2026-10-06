using DndMcp.Domain.Characters;
using DndMcp.Domain.Features;
using Microsoft.Data.Sqlite;

namespace DndMcp.Repository.Campaign.Characters;

/// <summary>
/// The actions of <c>campaign_character</c> (contract §7.1), as wire strings: the change_log tool label is
/// <c>campaign_character/&lt;action&gt;</c>, and <see cref="CharacterAction.Kind"/> carries one to the combat router.
/// </summary>
public static class CharacterActions
{
    public const string Get = "get";
    public const string Update = "update";
    public const string Damage = "damage";
    public const string Heal = "heal";
    public const string TempHp = "temp_hp";
    public const string Use = "use";
    public const string Rest = "rest";
    public const string Condition = "condition";
    public const string LevelUp = "level_up";
    public const string Xp = "xp";
    public const string Inventory = "inventory";
    public const string Currency = "currency";

    /// <summary>The tool name change_log records before the action: <c>campaign_character/damage</c>.</summary>
    public const string Tool = "campaign_character";

    /// <summary>Every action (forgiving spelling for a host that parses one).</summary>
    public static readonly DslValueSet Set = new("action", [Get, Update, Damage, Heal, TempHp, Use, Rest, Condition, LevelUp, Xp, Inventory, Currency]);

    /// <summary>
    /// The actions that go to the live fight while the character is a sheet-seeded combatant of its campaign's active
    /// encounter (contract D5): what a fight owns on the sheet (hit points, temporary hit points, slots and resources,
    /// conditions). Everything else (update, level_up, xp, inventory, currency, get) acts on the sheet, and rest is refused
    /// during the fight.
    /// </summary>
    public static readonly IReadOnlyList<string> Routable = [Damage, Heal, TempHp, Use, Condition];

    /// <summary>The change_log tool of an action: <c>campaign_character/&lt;action&gt;</c>.</summary>
    public static string ToolOf(string action) => Tool + "/" + action;
}

/// <summary>
/// One <c>campaign_character</c> action as the combat router receives it (contract D5): the action and its arguments,
/// already checked by the writer (amounts in range, the damage type canonical). For a routed <c>condition</c> the
/// <see cref="Duration"/> is <see cref="SheetValues.Durations.UntilRemoved"/>, the sheet's default: a condition added through
/// <c>campaign_character</c> during a fight is the author marking a lasting state (a curse), so it must persist when the
/// fight ends, where a <c>combat condition</c> would default to the fight (§5.13).
/// </summary>
public sealed record CharacterAction
{
    /// <summary>One of <see cref="CharacterActions.Routable"/>, or <see cref="CharacterActions.Rest"/> (asked only to learn whether the character is in the fight).</summary>
    public required string Kind { get; init; }

    /// <summary>damage, heal, temp_hp: the amount (0 or more); use: the uses spent (negative restores).</summary>
    public int? Amount { get; init; }

    /// <summary>damage: the canonical damage type, or null for untyped.</summary>
    public string? DamageType { get; init; }

    /// <summary>use: a spell slot level 1-9.</summary>
    public int? SlotLevel { get; init; }

    /// <summary>use: the Pact Magic slots.</summary>
    public bool Pact { get; init; }

    /// <summary>use: a resource by name.</summary>
    public string? Resource { get; init; }

    /// <summary>condition: names to add.</summary>
    public IReadOnlyList<string>? Add { get; init; }

    /// <summary>condition: names to remove.</summary>
    public IReadOnlyList<string>? Remove { get; init; }

    /// <summary>condition: exhaustion levels (default 1).</summary>
    public int? Level { get; init; }

    /// <summary>condition: the duration the added conditions get (<see cref="SheetValues.Durations.UntilRemoved"/>).</summary>
    public string? Duration { get; init; }
}

/// <summary>What <see cref="ICombatRouter.TryRoute"/> did with an action (<see cref="RoutedResult.Outcome"/>).</summary>
public static class RouteOutcomes
{
    /// <summary>
    /// Applied to the sheet-seeded combatant (unlogged, as a <c>combat</c> step is): the sheet is written when the fight ends.
    /// The writer logs nothing and returns no batch id.
    /// </summary>
    public const string Applied = "applied";

    /// <summary>
    /// The character is in the fight through a stat block (<c>srd</c>): nothing was applied; the writer acts on the sheet
    /// as out of combat (one logged batch) and says the fight was not changed.
    /// </summary>
    public const string StatBlock = "stat_block";

    /// <summary>
    /// The character is in the fight and the action is not one the fight takes (a rest): nothing was applied; the writer
    /// refuses a rest, or acts on the sheet for anything else.
    /// </summary>
    public const string InFight = "in_fight";
}

/// <summary>A reminder the tracker raised while applying a routed action (author text), with the call that resolves it.</summary>
/// <param name="Kind">The tracker's reminder kind (<c>concentration_save</c>, <c>dying</c>, …).</param>
/// <param name="Text">One line.</param>
/// <param name="Call">The resolving call, when one exists.</param>
public sealed record RoutedReminder(string Kind, string Text, string? Call = null);

/// <summary>
/// What the combat router reports for a character in the active encounter (contract D5). Author-facing: it names the
/// encounter and the tracker's name for the combatant.
/// </summary>
/// <param name="Outcome">One of <see cref="RouteOutcomes"/>.</param>
/// <param name="EncounterName">The active encounter's name.</param>
/// <param name="CombatantName">The tracker's name for the combatant ("Belmakor Silverwind", "Lich").</param>
/// <param name="Lines">What changed in the fight, one line per change with its arithmetic (empty unless applied).</param>
/// <param name="Reminders">What the change calls for (a concentration save, a death save), each with its call.</param>
/// <param name="Left">
/// The combatant left the fight (<c>leave</c>) and is still tied to it until it ends (F2, review CR07): the writer says so.
/// </param>
public sealed record RoutedResult(
    string Outcome,
    string EncounterName,
    string CombatantName,
    IReadOnlyList<string> Lines,
    IReadOnlyList<RoutedReminder> Reminders,
    bool Left = false)
{
    /// <summary>The router changed the fight (the writer then logs nothing).</summary>
    public bool Applied => Outcome == RouteOutcomes.Applied;
}

/// <summary>
/// The seam between the character writer and the combat tracker (contract D5): while a character is a combatant of its
/// campaign's ACTIVE encounter that has not left (or a sheet-seeded one that left: it is written back at the end all the
/// same, F2, review CR07), the fight owns its hit points, temporary hit points, slots, resources
/// and conditions, so <c>campaign_character damage</c>, <c>heal</c>, <c>temp_hp</c>, <c>use</c> and <c>condition</c> go to the
/// combatant, and the sheet catches up once, when the fight ends (the write-back). Without it a heal on the sheet during
/// the fight would be lost (the write-back writes the combatant's hit points over it) or would make <c>combat end</c>
/// refuse for drift.
///
/// <para>
/// <b>Implemented by the combat layer</b> (agent K's <c>CombatRouter</c>); the host passes it to
/// <see cref="CharacterWriter"/>. With no router (tests, a host without combat) every action acts on the sheet.
/// </para>
/// <para>
/// <b>Called inside the writer's transaction</b>, after the character is resolved and before anything is written, so the
/// encounter and its combatants are read as they stand at that moment (contract Q10): a fight that starts between a read
/// and the write cannot be missed. The router writes only unlogged rows (combatant, combat_log, dice_roll) in the same
/// transaction, so a dry run rolls its changes back with everything else.
/// </para>
/// </summary>
public interface ICombatRouter
{
    /// <summary>
    /// Whether the character is a combatant of the campaign's active encounter (not left, or sheet-seeded and left); when it
    /// is, what was done.
    /// Applies the action only when that combatant is SHEET-SEEDED and <paramref name="action"/> is one of
    /// <see cref="CharacterActions.Routable"/> (<see cref="RouteOutcomes.Applied"/>); a combatant from a stat block
    /// (<see cref="RouteOutcomes.StatBlock"/>) or a rest (<see cref="RouteOutcomes.InFight"/>) is reported, never changed.
    /// </summary>
    /// <returns>False when the character is not in the active fight (or there is none): the writer acts on the sheet.</returns>
    /// <exception cref="Domain.Core.DndInputException">The fight refuses the action (as <c>combat</c> would: a dead combatant healed, more slots than are left).</exception>
    bool TryRoute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CampaignRow campaign,
        string characterEntityId,
        CharacterAction action,
        out RoutedResult result);
}
