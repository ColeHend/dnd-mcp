using System.Text.RegularExpressions;
using DndMcp.Domain.Simulation;
using K = DndMcp.Domain.Simulation.StatBlockValues;

namespace DndMcp.Domain.Rules;

/// <summary>
/// A trait that may keep a monster alive when damage drops it to 0 HP, read from its stat block exactly as the
/// simulator reads it (<c>CombatantCompiler</c>): Undead Fortitude, Relentless, and a Regeneration that works at 0 HP
/// (the troll's "dies only if it starts its turn with 0 hit points and doesn't regenerate").
/// </summary>
/// <remarks>
/// The simulator resolves these itself (it rolls the Undead Fortitude save), for every creature that drops, death-save
/// makers included. The tracker cannot roll for the table, so a creature dropped to 0 with one that
/// <see cref="CouldApply"/> is held at 0, defeated but not dead (<see cref="DamageOutcome.Intercepted"/>), and the
/// reminder quotes <see cref="Text"/> with what resolves it: set its HP to 1 when the trait holds, or apply
/// <see cref="DamageOutcome.IfTraitFails"/> when it does not.
/// </remarks>
/// <param name="Kind"><see cref="StatBlockValues.TraitKinds.UndeadFortitude"/>, <see cref="StatBlockValues.TraitKinds.Relentless"/> or <see cref="StatBlockValues.TraitKinds.Regeneration"/>.</param>
/// <param name="Name">The trait's name as the stat block prints it ("Undead Fortitude", "Relentless (Recharges after a Short or Long Rest)").</param>
/// <param name="Text">The trait's text, for the reminder.</param>
/// <param name="Amount">Relentless: the most damage it survives; Regeneration: the hit points it regains.</param>
public sealed record DeathInterceptor(string Kind, string Name, string Text, int? Amount = null)
{
    /// <summary>
    /// Whether the trait can apply to damage that dropped the monster (as the simulator decides it,
    /// <c>Fight.ApplyDamage</c>): Undead Fortitude not against radiant damage or a critical hit; Relentless only to a
    /// total (after resistances, before temporary hit points) of at most its <see cref="Amount"/>; Regeneration always
    /// (whether the monster dies is decided at the start of its turn). Relentless's once-per-rest use is the caller's
    /// state: a used Relentless is left out of the list (<see cref="StatBlockFacts.DeathInterceptors"/>'s
    /// <c>relentlessUsed</c>), as the engine skips it once <c>RelentlessUsed</c> is set.
    /// </summary>
    public bool CouldApply(int damageTotal, bool critical, bool radiant) => Kind switch
    {
        K.TraitKinds.UndeadFortitude => !critical && !radiant,
        K.TraitKinds.Relentless => damageTotal <= (Amount ?? int.MaxValue),
        _ => true,
    };

    /// <summary>Undead Fortitude's Constitution save DC: 5 + the damage taken (both editions). Null for the other kinds.</summary>
    public int? SaveDc(int damageTotal) => Kind == K.TraitKinds.UndeadFortitude ? 5 + damageTotal : null;
}

/// <summary>
/// What the tracker needs to know about a stat block snapshot beyond its numbers: the traits that bring a destroyed
/// monster back later (quoted when it is defeated and again when the fight ends), the traits that may stop it dying at 0
/// HP, and its legendary and XP counts in or out of its lair.
/// </summary>
public static partial class StatBlockFacts
{
    /// <summary>
    /// The trait names that bring a monster back after it is destroyed, by catalogue name (parenthetical usage
    /// stripped). They are the normalizer's no-combat-effect traits whose recorded reason is that the monster revives
    /// (<c>TraitCatalogue.NoCombatEffect</c>: "revives days later", "revives elsewhere after dying", "revives a day
    /// later"; a test pins that this list is exactly those), plus <c>Misty Escape</c>: a vampire dropped to 0 HP away
    /// from its resting place turns to mist instead of dying and reforms there, which a table must hear when the
    /// tracker counts it dead. The snapshot carries no "revives" marker (the normalizer's reason lives only in its
    /// catalogue and a note's prose), so the names are the contract between the two.
    /// </summary>
    public static readonly IReadOnlyList<string> RevivalTraitNames =
    [
        "Celestial Restoration", "Demonic Restoration", "Diabolical Restoration", "Eldritch Restoration",
        "Elemental Restoration", "Exalted Restoration", "Fiendish Restoration", "Hellish Rejuvenation",
        "Hellish Restoration", "Misty Escape", "Rejuvenation", "Spirit Jar", "Undead Restoration",
    ];

    /// <summary>
    /// The traits of <paramref name="block"/> that bring it back after it is destroyed (2014 mummy lord Rejuvenation,
    /// 2024 aboleth Eldritch Restoration, a 2024 devil's Diabolical Restoration, a vampire's Misty Escape), in stat-block
    /// order, each once. The tracker quotes each one once at the defeat and again in the end-of-combat result.
    /// </summary>
    public static IReadOnlyList<StatBlockTrait> RevivalTraits(StatBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return block.Traits
            .Where(t => RevivalTraitNames.Contains(CatalogueName(t.Name), StringComparer.OrdinalIgnoreCase) && seen.Add(t.Name))
            .ToList();
    }

    /// <summary>
    /// The traits of <paramref name="block"/> that may keep it alive at 0 HP, as the simulator compiles them: the FIRST
    /// trait of each kind (<c>CombatantCompiler</c> reads one per kind), Undead Fortitude, Relentless with its threshold,
    /// and Regeneration only when its text says the monster dies only if it starts its turn at 0 hit points and it
    /// regains a positive amount. Regeneration that needs at least 1 hit point (an oni, a vampire) is not one.
    /// </summary>
    /// <param name="relentlessUsed">
    /// Its Relentless has already kept it alive since its last rest ("Recharges after a Short or Long Rest"; the engine's
    /// <c>RelentlessUsed</c>, which <c>from_state</c> seeds): Relentless is left out, so the next drop kills it. The
    /// tracker sets it once the table resolves a Relentless interception by setting the HP to 1.
    /// </param>
    public static IReadOnlyList<DeathInterceptor> DeathInterceptors(StatBlock block, bool relentlessUsed = false)
    {
        ArgumentNullException.ThrowIfNull(block);
        var result = new List<DeathInterceptor>();
        if (First(K.TraitKinds.UndeadFortitude) is { } fortitude)
        {
            result.Add(new DeathInterceptor(K.TraitKinds.UndeadFortitude, fortitude.Name, fortitude.Text));
        }

        if (!relentlessUsed && First(K.TraitKinds.Relentless) is { } relentless)
        {
            result.Add(new DeathInterceptor(K.TraitKinds.Relentless, relentless.Name, relentless.Text, relentless.Amount));
        }

        if (First(K.TraitKinds.Regeneration) is { Amount: > 0 } regeneration &&
            regeneration.Text.Contains("starts its turn with 0 hit points", StringComparison.OrdinalIgnoreCase))
        {
            result.Add(new DeathInterceptor(K.TraitKinds.Regeneration, regeneration.Name, regeneration.Text, regeneration.Amount));
        }

        return result;

        StatBlockTrait? First(string kind) => block.Traits.FirstOrDefault(t => t.Kind == kind);
    }

    /// <summary>
    /// Legendary action uses per round: the in-lair count when the fight is in its lair and the stat block has one
    /// (2024 "3 (4 in Lair)"), else the plain count; 0 without legendary actions. The snapshot stores both; the
    /// encounter's <c>lair</c> picks.
    /// </summary>
    public static int LegendaryActionUses(StatBlock block, bool inLair)
    {
        ArgumentNullException.ThrowIfNull(block);
        return block.Legendary is not { } legendary ? 0 : inLair && legendary.UsesInLair is { } lair ? lair : legendary.Uses;
    }

    /// <summary>Legendary Resistance uses per day: the in-lair count when in its lair and the stat block has one (2024 "3/Day, or 4/Day in Lair").</summary>
    public static int LegendaryResistanceUses(StatBlock block, bool inLair)
    {
        ArgumentNullException.ThrowIfNull(block);
        return inLair && block.LegendaryResistanceInLair is { } lair ? lair : block.LegendaryResistance;
    }

    /// <summary>The XP a defeated monster is worth: 2024 <c>XpInLair</c> when fought in its lair and it has one, else <c>Xp</c>.</summary>
    public static int Xp(StatBlock block, bool inLair)
    {
        ArgumentNullException.ThrowIfNull(block);
        return inLair && block.XpInLair is { } lair ? lair : block.Xp;
    }

    /// <summary>
    /// A trait name without parenthetical usage ("Legendary Resistance (3/Day)" → "Legendary Resistance"), as the
    /// normalizer's catalogue names it (<c>ProseText.StripParentheticals</c>: every "(…)" removed, spaces collapsed).
    /// </summary>
    internal static string CatalogueName(string name) =>
        string.Join(' ', Parenthetical().Replace(name, " ").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [GeneratedRegex(@"\s*\([^)]*\)")]
    private static partial Regex Parenthetical();
}
