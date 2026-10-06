using System.ComponentModel;

namespace DndMcp.Tools;

/// <summary>
/// One <c>items</c> entry of <c>campaign_character inventory</c>: the host's input record, published as the parameter's
/// item schema, so the argument guard refuses a misspelt field ("argument 'items' item 1 has unknown field 'qtty'")
/// instead of letting System.Text.Json drop it and a "take 2 potions" silently take one. Mapped to the repository's
/// <see cref="Repository.Campaign.Characters.InventoryItem"/> by the tool; ranges (a quantity of 0, more than a holding
/// has) are the writer's checks.
/// </summary>
public sealed record InventoryItemInput
{
    [Description("The item's name, e.g. \"Potion of Healing\", or an item entity's handle.")]
    public string? Item { get; init; }

    // Fix F1, C11: an entry that only changes a held item's flags, note or SRD entry ("unequip the ring") must not add another
    // one, so qty defaults to 1 only for an item not held yet; the writer applies that (srd included), and this text says it
    // (F2: it named only equipped, attuned and notes, so "set the potion's srd" read as one more potion).
    [Description("How many: positive adds, negative takes away; a holding that reaches 0 is removed. Default 1 for an item not held; for one held, " +
                 "an entry with equipped, attuned, notes or srd and no qty changes only those.")]
    public double? Qty { get; init; }

    [Description("The SRD entry it is, e.g. 2024/equipment/potion-of-healing.")]
    public string? Srd { get; init; }

    [Description("Equipped (true or false).")]
    public bool? Equipped { get; init; }

    [Description("Attuned (true or false).")]
    public bool? Attuned { get; init; }

    [Description("A note; replaces the holding's note.")]
    public string? Notes { get; init; }
}

/// <summary>
/// The <c>coins</c> of <c>campaign_character currency</c>: each denomination gained (positive) or spent (negative), one
/// ledger row per call. <c>campaign_character</c>'s own type (contract review m3): <c>combat end</c>'s currency entries
/// also name who receives them, so the two tools never share a record whose fields one of them would change.
/// </summary>
public sealed record CoinsInput
{
    [Description("Copper pieces.")]
    public long? Cp { get; init; }

    [Description("Silver pieces.")]
    public long? Sp { get; init; }

    [Description("Electrum pieces.")]
    public long? Ep { get; init; }

    [Description("Gold pieces.")]
    public long? Gp { get; init; }

    [Description("Platinum pieces.")]
    public long? Pp { get; init; }
}
