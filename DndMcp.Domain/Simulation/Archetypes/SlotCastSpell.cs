namespace DndMcp.Domain.Simulation.Archetypes;

/// <summary>
/// A spell a build casts once a fight from a spell slot and keeps up, with no uses to count down: the cleric's Spirit
/// Guardians (3rd level) and Spiritual Weapon (2nd, an attack), the druid's Call Lightning (3rd), the warlock's Hex (1st,
/// from a Pact Magic slot) and the 2014 ranger's Hunter's Mark (1st). Casting it needs one slot of
/// <see cref="SlotLevel"/> or higher, Pact Magic included.
/// </summary>
/// <remarks>
/// <b>Why it exists.</b> A fresh fight has every slot, so these spells are a setup cost or an every-turn effect with no
/// resource (<see cref="SlotFundedUse"/> counts only spells whose uses ARE slots). A fight resumed from a live tracker
/// (<c>balance_simulate {from_state}</c>) must not cast them when the slots are spent (review CR03: a cleric, a druid and
/// a warlock with no slot of the spell's level left resumed byte-identical to the full-slots report): the tracker loader
/// (<c>TrackerSimulation</c>) reads these, shares the slots left among them and the slot-funded uses in the build's order,
/// and marks a spell with no slot left <see cref="CombatantStart.Unavailable"/> — unless the fight is already
/// concentrating on it, when it keeps running and only cannot be cast again.
/// </remarks>
/// <param name="Name">The spell's label as the build names it: a modifier's name or an attack's ("Spiritual Weapon").</param>
/// <param name="SlotLevel">The lowest slot level that casts it.</param>
public sealed record SlotCastSpell(string Name, int SlotLevel);
