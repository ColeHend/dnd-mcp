using DndMcp.Domain.Characters;

namespace DndMcp.Domain.Combat;

/// <summary>
/// One condition or named effect on a combatant (contract §4, <c>combatant.conditions</c> items):
/// <c>{"id":"c3","name":"frightened","source":"&lt;combatant id&gt;","duration":"until_end_of_source_turn","skip_end":true,
/// "expires":{"round":11,"at":"start","of":"&lt;combatant id&gt;"},"save":{"ability":"wis","dc":11},"escape_dc":14,
/// "held_by":"&lt;combatant id&gt;","effect":{"ac":5},"note":"…","applied":{"round":1,"turn_of":"&lt;combatant id&gt;"}}</c>.
/// Optional keys are left out when null; unknown keys are kept in <see cref="Extra"/> and written back.
/// </summary>
/// <param name="Id">"c1", "c2", …: unique within the combatant, never reused, so a turn row's restore and a removal name one entry.</param>
/// <param name="Name">An SRD condition's canonical name ("frightened"), or a named effect as typed ("Bladesong").</param>
public sealed record CombatCondition(string Id, string Name, string Duration)
{
    /// <summary>The combatant that imposed it, by id (null: none, or <see cref="SourceNote"/>).</summary>
    public string? Source { get; init; }

    /// <summary>
    /// A source that named no combatant ("water pressure"), or the party-safe source a sheet condition carried in. A
    /// source-turn duration cannot hang on it (there is no turn to end it).
    /// </summary>
    public string? SourceNote { get; init; }

    /// <summary>
    /// It was applied during its anchor's own turn (the source for <c>until_end_of_source_turn</c>, the target for
    /// <c>until_end_of_target_turn</c>): that turn's end does not end it ("its NEXT turn", §5.13); the flag is cleared
    /// there instead.
    /// </summary>
    public bool SkipEnd { get; init; }

    /// <summary>The fixed expiry of a <c>rounds</c> or <c>end_of_round</c> duration.</summary>
    public ConditionExpiry? Expires { get; init; }

    /// <summary>A save repeated at the end of each of the target's turns (<c>save_ends</c>, or a timed or fight duration with a DC).</summary>
    public ConditionSave? Save { get; init; }

    /// <summary>The escape DC of an <c>until_escape</c> condition (a grapple's).</summary>
    public int? EscapeDc { get; init; }

    /// <summary>The combatant whose concentration holds it (<c>concentration</c> duration): it ends when that concentration does.</summary>
    public string? HeldBy { get; init; }

    /// <summary>What a named effect does to displayed AC and damage while it lasts (<c>effect {ac, resist, immune, vulnerable, except}</c>).</summary>
    public CombatEffect? Effect { get; init; }

    /// <summary>Author text.</summary>
    public string? Note { get; init; }

    /// <summary>When it was applied in this fight (null: it came in from the sheet, and goes back with its own note).</summary>
    public AppliedAt? Applied { get; init; }

    /// <summary>
    /// A 2024 knock-out's Unconscious (<c>"knock_out":true</c>, duration <c>zero_hp</c>): the creature is at 1 HP and it
    /// ends on any healing or first aid (SRD 5.2.1 "Knocking Out a Creature"). This flag is the combatant's knocked-out
    /// state (the table has no column for it).
    /// </summary>
    public bool KnockOut { get; init; }

    /// <summary>Stored members this version does not read, as a compact JSON object (written back unchanged).</summary>
    public string? Extra { get; init; }

    /// <summary>Whether the name is one of the 15 SRD conditions (an effect otherwise).</summary>
    public bool IsSrdCondition => CombatConditions.IsSrd(Name);
}

/// <summary>
/// A timed expiry: at the START of <see cref="Of"/>'s turn in round <see cref="Round"/> (<see cref="CombatValues.ExpiryPoints.Start"/>),
/// or after the last turn of round <see cref="Round"/> (<see cref="CombatValues.ExpiryPoints.RoundEnd"/>). <see cref="Of"/>
/// is null for a duration applied before initiative: it is anchored on the top of the order when round 1 begins.
/// </summary>
public sealed record ConditionExpiry(int Round, string At, string? Of)
{
    public string? Extra { get; init; }
}

/// <summary>The save that ends it: an ability key ("wis") and a DC.</summary>
public sealed record ConditionSave(string Ability, int Dc)
{
    public string? Extra { get; init; }
}

/// <summary>When a condition, effect or concentration began in this fight: the round and whose turn it was (null before initiative).</summary>
public sealed record AppliedAt(int Round, string? TurnOf)
{
    public string? Extra { get; init; }
}

/// <summary>
/// A named effect's mechanics while it lasts (contract §6.5): <c>{"ac":5,"resist":["all"],"except":["psychic"]}</c>.
/// <see cref="Ac"/> adds to the displayed AC; the lists join the combatant's damage adjustments ("all" with
/// <see cref="Except"/>). Damage types are canonical.
/// </summary>
public sealed record CombatEffect(int? Ac, IReadOnlyList<string> Resist, IReadOnlyList<string> Immune, IReadOnlyList<string> Vulnerable, IReadOnlyList<string> Except)
{
    public static CombatEffect None { get; } = new(null, [], [], [], []);

    public string? Extra { get; init; }

    /// <summary>Nothing set (not persisted as <c>effect</c>).</summary>
    public bool IsEmpty => Ac is null && Resist.Count == 0 && Immune.Count == 0 && Vulnerable.Count == 0 && Except.Count == 0 && Extra is null;
}

/// <summary>
/// What a combatant concentrates on (contract §4): <c>{"spell":"Circle of Power","level":5,"duration":"rounds",
/// "expires":{…},"pending":[17],"applied":{…}}</c>. No <see cref="Duration"/>: it never ends by time (cleared at
/// <c>end</c>). <see cref="Pending"/> lists the DCs of the concentration saves due, oldest first.
/// </summary>
public sealed record CombatConcentration(string Spell)
{
    public int? Level { get; init; }

    /// <summary><see cref="CombatValues.Durations.Rounds"/>, or null (no time limit known).</summary>
    public string? Duration { get; init; }

    public ConditionExpiry? Expires { get; init; }

    /// <summary>The DCs of saves due, oldest first (a damage instance adds one; a save resolves the oldest).</summary>
    public IReadOnlyList<int> Pending { get; init; } = [];

    /// <summary>When it was started in this fight (null: it came in from the sheet).</summary>
    public AppliedAt? Applied { get; init; }

    /// <summary>Author text (a sheet concentration's note, kept).</summary>
    public string? Note { get; init; }

    public string? Extra { get; init; }
}

/// <summary>
/// A legendary creature's counts (contract §4): <c>{"actions":4,"used":1,"resistance":4,"resistance_used":1}</c>, the
/// in-lair counts when the encounter is in a lair. Legendary actions reset at the start of its turn; Legendary
/// Resistance never resets in a fight (per day).
/// </summary>
public sealed record LegendaryState(int Actions, int Used, int Resistance, int ResistanceUsed)
{
    public string? Extra { get; init; }

    public int ActionsLeft => Math.Max(0, Actions - Used);

    public int ResistanceLeft => Math.Max(0, Resistance - ResistanceUsed);
}

/// <summary>
/// One entry of a combatant's <c>resources</c> (contract §4), by its key (<see cref="CombatValues.ResourceKeys"/>): a
/// counted mirror of the sheet (<c>{"name":"Bladesong","max":4,"used":1}</c>, <c>"slot:5"</c> <c>{"max":2,"used":1}</c>,
/// <c>"pact"</c> <c>{"level":3,"max":2,"used":0}</c>), a sheet tracker (<c>{"name":"Contingency","state":"set"}</c>), a
/// stat block's limited use (<c>{"kind":"recharge","min":5,"ready":false}</c>, <c>{"kind":"per_day","max":2,"used":0}</c>),
/// a 2014 slot pool, or a consumed holding (<c>{"name":"Potion of Healing","used":1}</c>). Only the fields its kind uses are
/// set.
/// </summary>
public sealed record CombatResource
{
    /// <summary>The display name (a sheet resource's or an item's name, a limited action's name).</summary>
    public string? Name { get; init; }

    public int? Max { get; init; }

    public int? Used { get; init; }

    /// <summary>Pact Magic: the slot level.</summary>
    public int? Level { get; init; }

    /// <summary><see cref="CombatValues.ResourceKinds"/> for a stat block's limited use.</summary>
    public string? Kind { get; init; }

    /// <summary>Recharge: the lowest d6 face that recharges it.</summary>
    public int? Min { get; init; }

    /// <summary>Recharge: whether it can be used now.</summary>
    public bool? Ready { get; init; }

    /// <summary>A sheet tracker's state ("set").</summary>
    public string? State { get; init; }

    public string? Extra { get; init; }

    /// <summary>Uses left of a counted entry (null for a tracker or a recharge).</summary>
    public int? Left => Max is { } max ? Math.Max(0, max - (Used ?? 0)) : null;
}

/// <summary>
/// The sheet's combat-owned fields at <c>add</c> (contract §6.7), exactly as stored on the sheet (<see cref="SheetJson.Column"/>:
/// a long, a JSON text, or null), plus what the fight reads from the sheet while it runs (<see cref="Defenses"/> for the
/// damage pipeline and condition immunity, <see cref="ConSaveBonus"/> for concentration saves), which the combatant row
/// has no column for. Stored as <c>{"hp":110,"temp_hp":7,…,"facts":{"defenses":{…},"con_save":7}}</c>: the column members
/// flat (JSON columns nested as JSON, as P reads a snapshot back), the facts under <c>"facts"</c> (not a sheet column).
/// </summary>
/// <remarks>
/// A combatant is SHEET-SEEDED exactly when it has a snapshot (D19; Z's undo guard reads <c>sheet_snapshot IS NOT NULL</c>).
/// The write-back compares each combat-owned field's final value with the snapshot (only a change is written) and the
/// current sheet with the snapshot (a difference is drift, D6).
/// </remarks>
public sealed record SheetSnapshot
{
    /// <summary>The columns captured: <see cref="SheetColumns.CombatOwned"/> plus <c>max_hp</c> (D17's "the sheet had no max_hp").</summary>
    public static readonly IReadOnlyList<string> Captured = [.. SheetColumns.CombatOwned, SheetColumns.MaxHp];

    /// <summary>Column → stored value (long, JSON text, or null).</summary>
    public required IReadOnlyDictionary<string, object?> Columns { get; init; }

    /// <summary>The sheet's damage and condition defences at add.</summary>
    public SheetDefenses Defenses { get; init; } = SheetDefenses.None;

    /// <summary>The sheet's Constitution save bonus at add (§5.8).</summary>
    public int ConSaveBonus { get; init; }

    /// <summary>
    /// The names (canonical SRD names, else as stored) of the sheet's stored conditions that a re-seed in a RUNNING fight did
    /// not bring in, because the fight already had a condition of that name on the combatant (F3, review F2R03: "poisoned
    /// (Goblin 2), 1 minute" shadows the sheet's "poisoned, until removed"). The write-back keeps each such stored entry on
    /// the sheet exactly as stored, its <c>until_removed</c> persistence winning over the fight's shorter one; without the
    /// mark the write-back read it as removed in the fight and dropped it from the sheet. A <c>condition remove</c> of that
    /// name in the fight clears the mark (the condition was ended on purpose). Stored under <c>"facts"</c> as
    /// <c>"shadowed": ["poisoned"]</c>; empty for every other combatant.
    /// </summary>
    public IReadOnlyList<string> Shadowed { get; init; } = [];

    public string? Extra { get; init; }

    /// <summary>The captured value of a column (null when not captured or NULL).</summary>
    public object? Column(string column) => Columns.TryGetValue(column, out var value) ? value : null;

    /// <summary>The captured JSON column as text (null when NULL).</summary>
    public string? Text(string column) => Column(column) as string;

    /// <summary>The captured integer column (null when NULL).</summary>
    public int? Integer(string column) => Column(column) is long l ? (int)l : null;

    /// <summary>A snapshot of <paramref name="sheet"/>'s captured columns and facts.</summary>
    public static SheetSnapshot Of(CharacterSheet sheet, int conSaveBonus)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        var columns = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var column in Captured)
        {
            columns[column] = SheetJson.Column(sheet, column);
        }

        return new SheetSnapshot { Columns = columns, Defenses = sheet.Defenses, ConSaveBonus = conSaveBonus };
    }
}

/// <summary>
/// A holding a heal or a <c>use</c> may consume (contract §12.4b): the Repository maps its <c>holding</c> rows to this.
/// <see cref="Quantity"/> is the stored quantity (REAL); the fight's uses accumulate on the consumer's
/// <c>"item:&lt;holding id&gt;"</c> resource and are written at <c>end</c> as the current quantity − used.
/// </summary>
public sealed record CombatItem(string HoldingId, string HolderEntityId, string Name, double Quantity);
