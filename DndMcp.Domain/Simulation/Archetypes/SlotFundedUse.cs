namespace DndMcp.Domain.Simulation.Archetypes;

/// <summary>
/// A build modifier whose limited uses ARE spell slots: the archetypes' "uses = every slot of 3rd level or higher"
/// (Fireball, Lightning Bolt; Shatter from the 2nd; Divine Smite every slot) and the healers' "uses the 1st-level slots"
/// (Healing Word, Cure Wounds). The DSL has no shared slot pool, so each archetype splits its slots between such modifiers
/// without counting a slot twice; this record says which slots fund which modifier.
/// </summary>
/// <remarks>
/// <b>Why it exists.</b> A fresh fight takes the uses from the class table at the archetype's level, but a fight resumed
/// from a live tracker (<c>balance_simulate {from_state}</c>, contract §6.11) must start from the slots the fight has left
/// (review C03/U01: a wizard with no 3rd-level slot left kept casting Fireball twice in every resumed fight). The tracker
/// loader (<c>TrackerSimulation</c>) reads these to turn the live <c>slot:N</c> and <c>pact</c> counts into the modifier's
/// uses left; a slot no record funds is said "not simulated", never dropped.
/// </remarks>
/// <param name="Modifier">The modifier's label, as the build names it ("Fireball").</param>
/// <param name="SlotLevel">The lowest slot level that funds it.</param>
/// <param name="OrHigher">Every slot of <paramref name="SlotLevel"/> or higher funds it; false: exactly that level (a heal on the 1st-level slots).</param>
/// <param name="ExtraUses">Uses beyond the slots, which the tracker does not count (the 2024 paladin's free Divine Smite a day).</param>
public sealed record SlotFundedUse(string Modifier, int SlotLevel, bool OrHigher, int ExtraUses = 0)
{
    /// <summary>Whether a slot of <paramref name="slotLevel"/> funds this modifier.</summary>
    public bool Funds(int slotLevel) => OrHigher ? slotLevel >= SlotLevel : slotLevel == SlotLevel;
}
