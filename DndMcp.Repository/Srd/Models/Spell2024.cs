namespace DndMcp.Repository.Srd.Models;

/// <summary>
/// A 2024 (SRD 5.2.1) spell, exactly as <c>2024/5e-SRD-Spells.json</c> stores it. See <see cref="Spell2014"/> for why
/// each edition has its own model.
///
/// <para>
/// Structurally thin, and the simulator must not mistake that for rules. None of the 339 spells has a save DC
/// type, an area of effect or a healing table. <see cref="Damage"/> is one object holding only the base slot, so flame
/// strike loses its radiant half, and magic missile and cure wounds carry no dice at all. The Phase 5 overlay
/// (<c>overrides/spells.2024.json</c>, parsed from <see cref="Description"/> and <see cref="HigherLevel"/>) supplies
/// them. Until then, an absent value here means "unknown", never "none".
/// </para>
/// </summary>
public sealed class Spell2024
{
    public required string Index { get; init; }

    public required string Name { get; init; }

    public required string Url { get; init; }

    /// <summary>
    /// Rules text as one string, with paragraphs separated by newlines. For cantrips it ends with the "Cantrip Upgrade"
    /// paragraph, which is the only place cantrip scaling is recorded.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>"Using a Higher-Level Spell Slot" text as a single string (103 spells). Never set on cantrips.</summary>
    public string? HigherLevel { get; init; }

    public required string Range { get; init; }

    public required IReadOnlyList<string> Components { get; init; }

    public string? Material { get; init; }

    public required bool Ritual { get; init; }

    public required string Duration { get; init; }

    public required bool Concentration { get; init; }

    /// <summary>e.g. <c>"Action"</c>, <c>"Bonus Action"</c>.</summary>
    public required string CastingTime { get; init; }

    /// <summary>Spell level; 0 is a cantrip.</summary>
    public required int Level { get; init; }

    public string? AttackType { get; init; }

    public SpellDamage2024? Damage { get; init; }

    public required ApiReference School { get; init; }

    public required IReadOnlyList<ApiReference> Classes { get; init; }
}

/// <summary>
/// A 2024 spell's damage: one type and the dice at the spell's base slot only (cantrips under key 0). Upcast scaling
/// exists only in <see cref="Spell2024.HigherLevel"/> prose, and cantrip scaling only in
/// <see cref="Spell2024.Description"/>.
/// </summary>
public sealed class SpellDamage2024
{
    public required ApiReference DamageType { get; init; }

    public required IReadOnlyDictionary<int, string> DamageAtSlotLevel { get; init; }
}
