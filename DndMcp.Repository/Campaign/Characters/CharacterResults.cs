using DndMcp.Domain.Characters;
using DndMcp.Repository.Campaign.Write;

namespace DndMcp.Repository.Campaign.Characters;

/// <summary>One field a <c>campaign_character</c> call changed, with its value before and after (author view).</summary>
/// <param name="Field">A sheet column (<c>hp</c>, <c>spell_slots</c>), or <see cref="CharacterChangeFields.Inventory"/> / <see cref="CharacterChangeFields.Coins"/>.</param>
/// <param name="Key">For a per-key column the key (a slot level, a resource slug, a die); an item's name; a coin.</param>
/// <param name="Before">The value before as stored (JSON text for JSON values, the number, the text), or null when there was none.</param>
/// <param name="After">The value after, or null when there is none now.</param>
public sealed record CharacterChange(string Field, string? Key, string? Before, string? After);

/// <summary>The non-sheet <see cref="CharacterChange.Field"/> values.</summary>
public static class CharacterChangeFields
{
    /// <summary>A holding's quantity (key: the item's name; before null = gained, after null = gone).</summary>
    public const string Inventory = "inventory";

    /// <summary>A coin balance (key: cp, sp, ep, gp or pp; before and after are the balances).</summary>
    public const string Coins = "coins";
}

/// <summary>
/// The reminder kinds the character writer adds itself, beside P's <see cref="SheetValues.ReminderKinds"/> (wire strings,
/// contract §0).
/// </summary>
public static class CharacterReminderKinds
{
    /// <summary>
    /// A concentration save the sheet owes after damage out of combat (contract §5.8): the DC, the roll, and the call that
    /// ends the concentration if it fails. The same wire string as the tracker's own reminder for the save in a fight (T's
    /// <c>CombatValues.ReminderKinds.ConcentrationSave</c>), so a host prints both one way and a routed reminder and a sheet
    /// one never read as two kinds.
    /// </summary>
    public const string ConcentrationSave = "concentration_save";
}

/// <summary>Something the caller should do next, with the call that does it when there is one (author view).</summary>
/// <param name="Kind">A <see cref="SheetValues.ReminderKinds"/> or <see cref="CharacterReminderKinds"/> value, or a tracker kind for a routed action.</param>
/// <param name="Text">One line.</param>
/// <param name="Call">The resolving call (<c>campaign_character {"action": "level_up", …}</c>), or null.</param>
public sealed record CharacterReminder(string Kind, string Text, string? Call = null);

/// <summary>A roll the call made and logged (a rest's Hit Dice), as the dice log holds it.</summary>
/// <param name="Expression">What was rolled ("2d10").</param>
/// <param name="Faces">The faces, in order.</param>
/// <param name="Total">The sum.</param>
/// <param name="Label">The label the session's dice list shows ("Belmakor Silverwind: hit dice").</param>
/// <param name="Secret">Logged secret (the author view only lists it).</param>
public sealed record CharacterRoll(string Expression, IReadOnlyList<int> Faces, long Total, string Label, bool Secret);

/// <summary>
/// What a <c>campaign_character</c> write did (author view: the host prints it to the author only, contract D10).
/// <see cref="BatchId"/> is null when nothing was logged: a dry run, an action routed to the live fight, or a call that
/// changed nothing; the host prints the batch paragraph and the undo hint only when it is set (§7.1).
/// </summary>
/// <param name="Action">The action (<see cref="CharacterActions"/>).</param>
/// <param name="Ref">The character's handle (<c>character:belmakor</c>).</param>
/// <param name="Name">The character's name.</param>
/// <param name="BatchId">The logged batch, or null (see the summary).</param>
/// <param name="DryRun">Nothing was kept.</param>
/// <param name="SessionNumber">The batch's session context, if any.</param>
/// <param name="Created">update created the sheet.</param>
/// <param name="Changes">What changed on the sheet, in its holdings or coins (empty for a routed action and when nothing changed).</param>
/// <param name="Notes">What happened ("Bladesong: spent 1, 3/4 left."; "Applied to the live fight …").</param>
/// <param name="Reminders">What to do next.</param>
/// <param name="Rolls">Dice the call rolled and logged.</param>
/// <param name="Routed">The fight's report when the action went to the live fight (or the character is in it from a stat block).</param>
/// <param name="Warnings">Applied anyway: a negative coin balance.</param>
public sealed record CharacterWriteResult(
    string Action,
    string Ref,
    string Name,
    string? BatchId,
    bool DryRun,
    int? SessionNumber,
    bool Created,
    IReadOnlyList<CharacterChange> Changes,
    IReadOnlyList<string> Notes,
    IReadOnlyList<CharacterReminder> Reminders,
    IReadOnlyList<CharacterRoll> Rolls,
    RoutedResult? Routed,
    IReadOnlyList<WriteWarning> Warnings)
{
    /// <summary>Anything changed (or, for a dry run, would): a sheet field, a holding, a coin balance, or the fight.</summary>
    public bool Changed => Changes.Count > 0 || Routed?.Applied == true;
}

/// <summary>One item of a <c>campaign_character inventory</c> call (contract §7.1).</summary>
public sealed record InventoryItem
{
    /// <summary>The item's name ("Potion of Healing"), or an item entity's handle (<c>item:thing-he-wants</c>, <c>e:12</c>).</summary>
    public string? Item { get; init; }

    /// <summary>How many to add (positive) or take away (negative); default 1. A holding that reaches 0 is deleted.</summary>
    public double? Qty { get; init; }

    /// <summary>The SRD entry it is ("2024/equipment/potion-of-healing").</summary>
    public string? Srd { get; init; }

    /// <summary>Equipped (true/false).</summary>
    public bool? Equipped { get; init; }

    /// <summary>Attuned (true/false).</summary>
    public bool? Attuned { get; init; }

    /// <summary>A note (replaces the holding's note).</summary>
    public string? Notes { get; init; }
}

/// <summary>Coins in or out (negative) for <c>campaign_character currency</c>: one ledger row.</summary>
public sealed record Coins(long Cp = 0, long Sp = 0, long Ep = 0, long Gp = 0, long Pp = 0)
{
    /// <summary>Every denomination 0.</summary>
    public bool IsZero => Cp == 0 && Sp == 0 && Ep == 0 && Gp == 0 && Pp == 0;
}
