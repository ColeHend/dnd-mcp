using System.Globalization;
using DndMcp.Domain.Characters;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation;

namespace DndMcp.Domain.Combat;

/// <summary>
/// One <c>combat</c> step's input, already bound by the host (contract §6.0): which action, with what. Every operation is
/// a subtype; <see cref="CombatTracker.Needs"/> says what it must roll and <see cref="CombatTracker.Apply"/> applies it.
/// Combatants are named by ADDRESS (contract §6.2: an exact tracker name, a linked entity's handle or slug, a unique
/// prefix, or <c>"name*"</c> for every copy); the tracker resolves them.
/// </summary>
/// <remarks>
/// <c>secret</c> is not here: secrecy and labels are the Repository's when it logs the roll (§6.10); the tracker only
/// names each roll's subject (<see cref="RollNeed.CombatantId"/>).
/// </remarks>
public abstract record CombatOp;

/// <summary>
/// <c>add {combatants}</c> (and <c>start</c>'s <c>add_party</c>): new combatants from a stat block, a sheet, or a name
/// (contract §6.2). <see cref="NewIds"/> are the ids the new combatants take, in entry and copy order (the Domain makes
/// no ids: the Repository passes at least Σ count of them); a re-join takes none.
/// </summary>
public sealed record AddOp(IReadOnlyList<AddEntry> Entries, IReadOnlyList<string> NewIds) : CombatOp;

/// <summary>
/// One <c>add</c> entry with its source resolved by the caller: <see cref="Monster"/> (the host's
/// <c>StatBlockService</c> result), and/or the linked character (<see cref="EntityId"/>, its name, handle and subtype, and
/// <see cref="Sheet"/> when it has one), or only a <see cref="Name"/> (custom).
/// </summary>
/// <remarks>
/// SHEET-SEEDED (snapshot and write-back, D19) exactly when a character entity with a sheet is added without a stat
/// block. With <see cref="Monster"/> the stat block rules and the link serves names and proposals.
/// </remarks>
public sealed record AddEntry
{
    public StatBlock? Monster { get; init; }

    /// <summary>The linked character's sheet (current, read in the step's transaction), or null.</summary>
    public CharacterSheet? Sheet { get; init; }

    public string? EntityId { get; init; }

    /// <summary>The entity's name (the tracker name of a character added without a stat block or a typed name).</summary>
    public string? EntityName { get; init; }

    /// <summary>The entity's handle, <c>character:torch</c>.</summary>
    public string? EntityHandle { get; init; }

    /// <summary>The entity's subtype ("pc", "npc"): a PC makes death saves and defaults to the party side.</summary>
    public string? EntitySubtype { get; init; }

    /// <summary>A typed tracker name (replaces the monster's or the entity's; alone it makes a custom combatant).</summary>
    public string? Name { get; init; }

    /// <summary>Copies, 1-20 ("Mummy", "Mummy 2"…, one init group).</summary>
    public int Count { get; init; } = 1;

    /// <summary>Hit points: null takes the default (D17: the average in a DM campaign, unknown in a player one, for a stat block).</summary>
    public HpChoice? Hp { get; init; }

    public int? Ac { get; init; }

    public int? InitBonus { get; init; }

    /// <summary>party | ally | enemy | neutral; null takes the default (enemy for srd and custom, party for a PC, ally for another character).</summary>
    public string? Side { get; init; }

    public bool Hidden { get; init; }

    /// <summary>Makes death saves at 0 HP (null: a PC or sheet-seeded combatant does, anything else does not).</summary>
    public bool? DeathSaves { get; init; }
}

/// <summary>
/// The hit points an <c>add</c> entry asks for (contract §6.2): <see cref="CombatValues.HpChoices"/> "avg", "roll",
/// "unknown", or a number (<see cref="Value"/>).
/// </summary>
public sealed record HpChoice(string Kind, int? Value = null)
{
    public static HpChoice Avg { get; } = new(CombatValues.HpChoices.Avg);

    public static HpChoice Roll { get; } = new(CombatValues.HpChoices.Roll);

    public static HpChoice Unknown { get; } = new(CombatValues.HpChoices.Unknown);

    /// <summary>A given number of hit points.</summary>
    public static HpChoice Of(int value) => new(CombatValues.HpChoices.Number, value);

    /// <summary>"avg", "roll", "unknown" (forgiving) or a whole number, as the host binds the untyped <c>hp</c>.</summary>
    /// <exception cref="DndInputException">Anything else.</exception>
    public static HpChoice Parse(string text)
    {
        if (CombatValues.HpChoices.Set.TryMatch(text, out var kind))
        {
            return new HpChoice(kind);
        }

        if (int.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            return Of(value);
        }

        throw new DndInputException($"hp \"{DslText.Echo(text)}\" is not hit points; give \"avg\", \"roll\", \"unknown\" or a whole number.");
    }
}

/// <summary><c>set {combatants}</c>: change combatants already in the fight (contract §6.2).</summary>
public sealed record SetOp(IReadOnlyList<SetEntry> Entries) : CombatOp;

/// <summary>
/// One <c>set</c> entry: the combatant's address (the <c>character</c> handle or the <c>name</c>) and the fields to set.
/// <see cref="Hp"/> sets current HP and the maximum when it had none or exceeds it.
/// </summary>
public sealed record SetEntry(string Combatant)
{
    public int? Hp { get; init; }

    public int? Ac { get; init; }

    public int? InitBonus { get; init; }

    public string? Side { get; init; }

    public bool? Hidden { get; init; }

    public bool? DeathSaves { get; init; }

    public int? MaxHpReduction { get; init; }
}

/// <summary><c>leave {targets}</c>: the combatants leave the fight (contract §6.2).</summary>
public sealed record LeaveOp(IReadOnlyList<string> Targets) : CombatOp;

/// <summary>
/// <c>start {surprised}</c> (contract §6.1): mark combatants surprised before initiative is rolled (2024: Disadvantage on
/// their roll; 2014: they lose their first turn). <c>initiative {surprised}</c> marks them in the same call instead.
/// </summary>
public sealed record SurpriseOp(IReadOnlyList<string> Targets) : CombatOp;

/// <summary>
/// <c>initiative {rolls?, surprised?}</c> (contract §6.3, §5.11): the given values, then a server roll for everyone else
/// with no initiative yet; the first call begins round 1.
/// </summary>
public sealed record InitiativeOp : CombatOp
{
    public IReadOnlyList<InitiativeRollInput> Rolls { get; init; } = [];

    /// <summary>Combatants surprised (2024: Disadvantage on the roll; 2014: they lose their first turn).</summary>
    public IReadOnlyList<string> Surprised { get; init; } = [];
}

/// <summary>A given initiative: the combatant's address and the kept d20 face OR the total (decimals reorder ties).</summary>
public sealed record InitiativeRollInput(string Combatant, int? Face = null, double? Total = null);

/// <summary><c>next {from?}</c>: end the current turn and begin the next (contract §6.3). <see cref="From"/> guards a stale call.</summary>
public sealed record NextOp(string? From = null) : CombatOp;

/// <summary>
/// <c>prev</c> (contract §6.3). <see cref="LastChange"/> is the encounter's LAST combat_log row (its kind and detail),
/// which the Repository reads: a <c>turn</c> row of a <c>next</c> there means nothing changed since, and its automatic
/// changes are reverted exactly; otherwise the pointer moves back only.
/// </summary>
public sealed record PrevOp(CombatLogEntry? LastChange = null) : CombatOp;

/// <summary>A combat_log row as <c>prev</c> needs it.</summary>
public sealed record CombatLogEntry(string Kind, string? Detail);

/// <summary>
/// <c>damage</c> (contract §6.4): exactly one of <see cref="Amount"/>, <see cref="Dice"/> or <see cref="Parts"/>; dice are
/// rolled once for every target, and <see cref="Critical"/> doubles the dice of a SERVER roll (a given amount is taken as
/// given).
/// </summary>
public sealed record DamageOp(IReadOnlyList<string> Targets) : CombatOp
{
    public int? Amount { get; init; }

    public string? Dice { get; init; }

    public IReadOnlyList<DamagePartInput>? Parts { get; init; }

    /// <summary>The type of <see cref="Amount"/> or <see cref="Dice"/> (null: untyped).</summary>
    public string? DamageType { get; init; }

    public bool Critical { get; init; }

    public bool Magical { get; init; }

    /// <summary>The targets taking half (they saved).</summary>
    public IReadOnlyList<string>? Half { get; init; }

    public bool Raw { get; init; }

    public bool KnockOut { get; init; }

    /// <summary>The attacker (default: the turn-holder, for the actor's condition line; the roll's subject when given).</summary>
    public string? Source { get; init; }
}

/// <summary>One damage part: an amount or dice, and its type.</summary>
public sealed record DamagePartInput(int? Amount = null, string? Dice = null, string? Type = null);

/// <summary>
/// <c>heal</c> (contract §6.4): an amount or dice; <see cref="Temp"/> grants temporary hit points instead;
/// <see cref="Item"/> consumes that item from the source's holdings, else the target's (one target).
/// </summary>
public sealed record HealOp(IReadOnlyList<string> Targets) : CombatOp
{
    public int? Amount { get; init; }

    public string? Dice { get; init; }

    public bool Temp { get; init; }

    public string? Item { get; init; }

    public string? Source { get; init; }

    /// <summary>The holdings of the fight's sheet-seeded characters (the Repository reads them in the step's transaction).</summary>
    public IReadOnlyList<CombatItem> Holdings { get; init; } = [];
}

/// <summary>
/// <c>condition</c> (contract §6.5): add or remove conditions and named effects; durations per §5.13; <c>exhaustion</c>
/// by <see cref="Level"/>; <see cref="Resource"/> spends one use of that resource of each target while adding.
/// </summary>
public sealed record ConditionOp(IReadOnlyList<string> Targets) : CombatOp
{
    public IReadOnlyList<string>? Add { get; init; }

    public IReadOnlyList<string>? Remove { get; init; }

    public string? Duration { get; init; }

    /// <summary>A combatant (default: the turn-holder), or text naming no combatant (kept as its source note).</summary>
    public string? Source { get; init; }

    /// <summary>
    /// With no <see cref="Source"/>: the condition has no source at all, never the turn-holder (review F2R09). A
    /// <c>campaign_character condition</c> routed into the fight (D5) is the sheet's own condition, imposed by nobody in
    /// the fight: defaulted to the turn-holder it read "Brak: poisoned (Aria)" and was persisted "from Aria", naming a
    /// poisoner who poisoned no one (and a hidden turn-holder's safe name).
    /// </summary>
    public bool NoSource { get; init; }

    public int? Dc { get; init; }

    public string? Ability { get; init; }

    public int? Level { get; init; }

    /// <summary>The round of an <c>end_of_round</c> duration (default: the current one).</summary>
    public int? Round { get; init; }

    public EffectInput? Effect { get; init; }

    public string? Resource { get; init; }
}

/// <summary>A named effect's mechanics as given: AC bonus and damage adjustments ("all" with except).</summary>
public sealed record EffectInput(int? Ac = null, IReadOnlyList<string>? Resist = null, IReadOnlyList<string>? Immune = null,
    IReadOnlyList<string>? Vulnerable = null, IReadOnlyList<string>? Except = null);

/// <summary>
/// <c>concentration</c> (contract §6.6): start (<see cref="Spell"/>, with <see cref="SlotLevel"/> spending a slot and a
/// <see cref="Duration"/>), <see cref="Drop"/>, or resolve the oldest pending save (<see cref="Total"/> or
/// <see cref="Face"/>; neither: the server rolls it).
/// </summary>
public sealed record ConcentrationOp(IReadOnlyList<string> Targets) : CombatOp
{
    public string? Spell { get; init; }

    public int? SlotLevel { get; init; }

    public string? Duration { get; init; }

    public bool Drop { get; init; }

    public int? Total { get; init; }

    public int? Face { get; init; }
}

/// <summary>
/// <c>use</c> (contract §6.6): spend (negative restores) one of a slot level, the Pact slots, a resource, or an item of the
/// combatant's own holdings.
/// </summary>
public sealed record UseOp(IReadOnlyList<string> Targets) : CombatOp
{
    public int? SlotLevel { get; init; }

    public bool Pact { get; init; }

    public string? Resource { get; init; }

    public string? Item { get; init; }

    public int Amount { get; init; } = 1;

    /// <summary>The holdings of the fight's sheet-seeded characters (for <see cref="Item"/>).</summary>
    public IReadOnlyList<CombatItem> Holdings { get; init; } = [];
}

/// <summary>
/// <c>legendary {source, amount = 1, name?, resistance?}</c> (contract §6.6): spend legendary actions (never during the
/// creature's own turn) or one Legendary Resistance.
/// </summary>
public sealed record LegendaryOp(string Source) : CombatOp
{
    /// <summary>The cost; null takes the named action's cost from the stat block, else 1.</summary>
    public int? Amount { get; init; }

    public string? Name { get; init; }

    public bool Resistance { get; init; }
}

/// <summary><c>death_save</c> (contract §6.6, §5.6): a face or total given, else the server rolls; <see cref="Stable"/> stabilises (first aid).</summary>
public sealed record DeathSaveOp(IReadOnlyList<string> Targets) : CombatOp
{
    public int? Face { get; init; }

    public int? Total { get; init; }

    public bool Stable { get; init; }
}
