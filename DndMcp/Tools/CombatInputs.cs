using System.ComponentModel;

namespace DndMcp.Tools;

/// <summary>
/// One <c>combatants</c> entry of <c>combat</c> (<c>prepare</c>, <c>start</c>, <c>add</c> and <c>set</c>), as the model
/// sends it: the host's input record, published as the parameter's item schema, so the argument guard refuses a misspelt
/// field ("argument 'combatants' item 1 has unknown field 'cuont'") instead of letting System.Text.Json drop it and a
/// "four mummies" silently add one. Mapped by the tool onto the Repository's <c>CombatantRequest</c> (<c>srd</c> resolved
/// to a stat block first) or the tracker's <c>SetEntry</c>.
///
/// <para>
/// <b><c>hp</c> is untyped</b> (an <c>object</c>, which binds as a <c>JsonElement</c>) so that "avg", "roll", "unknown" and
/// a number all bind, as <see cref="EncounterMonsterInput.Cr"/> does: a typed string would refuse 45, a typed number "avg".
/// The tool reads it with the tracker's own parser. Which combination of fields an action takes (no <c>srd</c> or
/// <c>count</c> in <c>set</c>, no <c>max_hp_reduction</c> in <c>add</c>) is the tool's check, with a message naming the
/// item; the schema cannot say it.
/// </para>
/// <para>
/// Named <c>…Input</c> in <c>DndMcp.Tools</c>, beside the Repository's <c>…Request</c> records and the tracker's own
/// <c>DamagePartInput</c>, <c>EffectInput</c> and <c>InitiativeRollInput</c> (which carry no descriptions, so they cannot be
/// a published schema): a simple name in this namespace wins over one a <c>using</c> imports, so this assembly's code
/// always means these.
/// </para>
/// </summary>
public sealed record CombatantInput
{
    [Description("An SRD monster by ref or name, e.g. 2014/monster/mummy (snapshotted whole).")]
    public string? Srd { get; init; }

    [Description("A campaign character's handle, e.g. character:torch: with a sheet and no srd, played from the sheet and written back at end.")]
    public string? Character { get; init; }

    [Description("A tracker name: replaces the monster's or character's; alone, a custom combatant. In set, names the combatant to change.")]
    public string? Name { get; init; }

    [Description("Copies, 1-20 (Mummy, Mummy 2 and so on: one initiative). Default 1.")]
    public int? Count { get; init; }

    [Description("avg, roll, unknown or a number. Default: avg (unknown in a player campaign). In set, current HP.")]
    public object? Hp { get; init; }

    [Description("Armor Class.")]
    public int? Ac { get; init; }

    [Description("Initiative bonus.")]
    public int? InitBonus { get; init; }

    [Description("party, ally, enemy or neutral. Default: enemy (party for a PC, ally for another character).")]
    public string? Side { get; init; }

    [Description("Hidden from the party's views until set with hidden false.")]
    public bool? Hidden { get; init; }

    [Description("Makes death saves at 0 HP (a named NPC). PCs always do.")]
    public bool? DeathSaves { get; init; }

    [Description("set only: the hit point maximum reduction.")]
    public int? MaxHpReduction { get; init; }
}

/// <summary>One <c>parts</c> entry of <c>combat damage</c>: an amount or dice, and its damage type.</summary>
public sealed record DamagePartInput
{
    [Description("The damage dealt.")]
    public int? Amount { get; init; }

    [Description("Dice for the server to roll, e.g. 2d6+5.")]
    public string? Dice { get; init; }

    [Description("Its damage type, e.g. necrotic.")]
    public string? Type { get; init; }
}

/// <summary>The <c>effect</c> of <c>combat condition</c>: what a named effect changes while it lasts (Bladesong's AC, Rage's resistances).</summary>
public sealed record EffectInput
{
    [Description("Added to AC.")]
    public int? Ac { get; init; }

    [Description("Damage types resisted, or all.")]
    public string[]? Resist { get; init; }

    [Description("Damage types it is immune to.")]
    public string[]? Immune { get; init; }

    [Description("Damage types it is vulnerable to.")]
    public string[]? Vulnerable { get; init; }

    [Description("With all: the types left out, e.g. psychic.")]
    public string[]? Except { get; init; }
}

/// <summary>One <c>rolls</c> entry of <c>combat initiative</c>: a combatant and its kept d20 face or its total.</summary>
public sealed record InitiativeRollInput
{
    [Description("The combatant: a tracker name, handle or slug.")]
    public string? Combatant { get; init; }

    [Description("The d20 as rolled (the bonus is added here).")]
    public int? Face { get; init; }

    [Description("The final number; decimals such as 14.5 break ties.")]
    public double? Total { get; init; }
}

/// <summary>One <c>loot</c> entry of <c>combat end</c>: an item found, created as a holding in the write-back batch.</summary>
public sealed record LootInput
{
    [Description("The item's name, or an item entity's handle.")]
    public string? Item { get; init; }

    [Description("The SRD entry it is, e.g. 2024/magic-item/potion-of-healing.")]
    public string? Srd { get; init; }

    [Description("How many, above 0. Default 1.")]
    public double? Qty { get; init; }

    [Description("Who holds it: a character's handle. Default: the party.")]
    public string? To { get; init; }
}

/// <summary>
/// One <c>currency</c> entry of <c>combat end</c>: coins found, one ledger row for <see cref="To"/>. <c>combat</c>'s own
/// type (contract review m3): <c>campaign_character</c>'s <see cref="CoinsInput"/> names no recipient.
/// </summary>
public sealed record CurrencyInput
{
    [Description("Who receives them: a character's handle. Default: the party.")]
    public string? To { get; init; }

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
