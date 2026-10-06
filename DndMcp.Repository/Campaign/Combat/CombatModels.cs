using DndMcp.Domain.Characters;
using DndMcp.Domain.Combat;
using DndMcp.Domain.Rules;
using DndMcp.Domain.Simulation;

namespace DndMcp.Repository.Campaign.Combat;

/// <summary>
/// One <c>combatants</c> entry of <c>prepare</c>, <c>start</c> or <c>add</c> as the host binds it (contract §6.2): the
/// SOURCE (<see cref="Monster"/>, the host's <c>StatBlockService</c> result for <c>srd</c>; <see cref="Character"/>, a
/// character handle the Repository resolves inside the step's transaction; or <see cref="Name"/> alone, a custom
/// combatant) and its fields. The host resolves the monster (it owns the SRD index); the Repository resolves the
/// character (it owns the campaign), reads its sheet in the same transaction as the add, and decides sheet seeding (D19:
/// a character with a sheet and no stat block).
/// </summary>
/// <remarks>
/// A cross-campaign handle (<c>one-piece/character:keras</c>) is refused (contract §0: <c>same_as</c> links are its only use).
/// <c>max_hp_reduction</c> is not here: <c>add</c> refuses it (set it with <c>set</c>), so the host refuses it before calling.
/// Named <c>…Request</c>, not <c>…Input</c>: the host's tool parameter types are <c>CombatantInput</c>, <c>LootInput</c>
/// and <c>CurrencyInput</c> (contract §15; <c>srd</c> is a string there), and a host file importing both namespaces would
/// not compile with one simple name in each (CS0104).
/// </remarks>
public sealed record CombatantRequest
{
    /// <summary>The resolved stat block of <c>srd</c> (snapshotted whole), or null.</summary>
    public StatBlock? Monster { get; init; }

    /// <summary>A campaign character's handle (<c>character:torch</c>, <c>torch</c>, <c>e:12</c>), or null.</summary>
    public string? Character { get; init; }

    /// <summary>A typed tracker name (replaces the monster's or the character's; alone it makes a custom combatant).</summary>
    public string? Name { get; init; }

    /// <summary>Copies (1-20; "Mummy", "Mummy 2"…, one init group).</summary>
    public int Count { get; init; } = 1;

    /// <summary>"avg", "roll" (the server rolls the Hit Dice, logged as "hit points"), "unknown" or a number; null takes D17's default.</summary>
    public HpChoice? Hp { get; init; }

    /// <summary>The AC (replaces a stat block's; a custom combatant's own), or null.</summary>
    public int? Ac { get; init; }

    /// <summary>The initiative bonus (replaces a stat block's or a sheet's), or null.</summary>
    public int? InitBonus { get; init; }

    /// <summary>party | ally | enemy | neutral; null takes the default (enemy for srd and custom, party for a PC, ally for another character).</summary>
    public string? Side { get; init; }

    /// <summary>Hidden from the party's views (an ambusher; <c>set {hidden: false}</c> reveals it): its rolls are secret, the board leaves it out.</summary>
    public bool Hidden { get; init; }

    /// <summary>Makes death saves at 0 HP (null: a PC or a sheet-seeded combatant does, anything else does not).</summary>
    public bool? DeathSaves { get; init; }
}

/// <summary><c>prepare {name, lair?, edition?, combatants?}</c> (contract §6.1): a planned fight, no party added.</summary>
public sealed record PrepareRequest(string Name)
{
    /// <summary>Fought in a lair: in-lair legendary counts and XP, the 2014 initiative-20 lair reminder.</summary>
    public bool Lair { get; init; }

    /// <summary>"2014" or "2024"; required in a mixed campaign, else the campaign's ruleset.</summary>
    public string? Edition { get; init; }

    /// <summary>The fight's first combatants (the party is not added to a planned fight).</summary>
    public IReadOnlyList<CombatantRequest> Combatants { get; init; } = [];
}

/// <summary>
/// <c>start {name? | encounter?, add_party = true, lair?, edition?, combatants?, surprised?}</c> (contract §6.1): a new
/// active fight, or a planned (or paused) one made active.
/// </summary>
public sealed record StartRequest
{
    /// <summary>The new fight's name (a planned or paused fight of that name is activated instead); null: "Fight N".</summary>
    public string? Name { get; init; }

    /// <summary>A planned or paused encounter to activate, by name (D13).</summary>
    public string? Encounter { get; init; }

    /// <summary>Adds every current party member (D8) not already in the fight, in name order.</summary>
    public bool AddParty { get; init; } = true;

    /// <summary>Fought in a lair (null: false for a new fight, unchanged for a planned one).</summary>
    public bool? Lair { get; init; }

    /// <summary>A new fight's edition ("2014" or "2024"; required in a mixed campaign); a planned fight keeps its own.</summary>
    public string? Edition { get; init; }

    /// <summary>Combatants to add after the party.</summary>
    public IReadOnlyList<CombatantRequest> Combatants { get; init; } = [];

    /// <summary>Combatants surprised (addresses, after the adds).</summary>
    public IReadOnlyList<string> Surprised { get; init; } = [];
}

/// <summary>One <c>loot</c> entry of <c>end</c>: a holding created for <see cref="To"/> (a character or the party faction; default the party).</summary>
public sealed record LootRequest(string Item)
{
    /// <summary>An SRD ref stored on the holding (<c>2024/magic-item/potion-of-water-breathing</c>), or null.</summary>
    public string? Srd { get; init; }

    /// <summary>How many (above 0; default 1).</summary>
    public double Qty { get; init; } = 1;

    /// <summary>A character handle or the party faction's; null: the party.</summary>
    public string? To { get; init; }
}

/// <summary>One <c>currency</c> entry of <c>end</c>: one currency_txn row for <see cref="To"/> (default the party).</summary>
public sealed record CurrencyRequest
{
    /// <summary>A character handle or the party faction's; null: the party.</summary>
    public string? To { get; init; }

    /// <summary>Copper pieces (signed).</summary>
    public long Cp { get; init; }

    /// <summary>Silver pieces (signed).</summary>
    public long Sp { get; init; }

    /// <summary>Electrum pieces (signed).</summary>
    public long Ep { get; init; }

    /// <summary>Gold pieces (signed).</summary>
    public long Gp { get; init; }

    /// <summary>Platinum pieces (signed).</summary>
    public long Pp { get; init; }
}

/// <summary><c>end {outcome?, xp?, loot?, currency?, discard?, force?, dry_run?, reason?}</c> (contract §6.7).</summary>
public sealed record EndRequest
{
    /// <summary>Author text stored as the encounter's <c>outcome_md</c> (author-only: never in a non-author output).</summary>
    public string? Outcome { get; init; }

    /// <summary>Null: the defeated and fled enemies' XP when a party sheet tracks XP; 0: none; N: N, split.</summary>
    public int? Xp { get; init; }

    /// <summary>Items found: holdings created in the write-back batch.</summary>
    public IReadOnlyList<LootRequest> Loot { get; init; } = [];

    /// <summary>Coins found: currency_txn rows created in the write-back batch.</summary>
    public IReadOnlyList<CurrencyRequest> Currency { get; init; } = [];

    /// <summary>End with no write-back.</summary>
    public bool Discard { get; init; }

    /// <summary>Write over drift (a sheet or holding changed since the fight began).</summary>
    public bool Force { get; init; }

    /// <summary>Plan everything, keep nothing: the fight stays as it was.</summary>
    public bool DryRun { get; init; }

    /// <summary>The write-back batch's reason (default "combat end: &lt;encounter&gt;").</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// One server roll a step made (D11), as logged: the tracker's need key and purpose, the expression actually rolled, the
/// KEPT faces, the total, the label and secrecy (§6.10) and the <c>dice_roll</c> id the combat_log rows cite. Author output.
/// </summary>
public sealed record CombatRoll(
    string Key,
    string Purpose,
    string Expression,
    IReadOnlyList<int> Faces,
    long Total,
    string Label,
    bool Secret,
    string RollId,
    string? SubjectId);

/// <summary>
/// One combatant as the AUTHOR's initiative table shows it (H2's table: order, init, name, HP, AC,
/// conditions/effects/concentration, the turn, defeated, hidden). Author-only: names, handles and every number.
/// </summary>
/// <param name="Position">1-based place in the turn order; null when not in it (no initiative yet, or left).</param>
/// <param name="Name">The tracker name.</param>
/// <param name="EntityHandle">The linked entity's handle (author view prints "(character:slug)" for a linked stat block).</param>
/// <param name="Hp">Current HP (null: unknown, D17).</param>
/// <param name="MaxHp">The EFFECTIVE maximum (<see cref="HitPointMath.EffectiveMaxHp"/>), null when unknown.</param>
/// <param name="DamageTaken">What an unknown-HP combatant has taken.</param>
/// <param name="Ac">Displayed AC (base plus active effects).</param>
/// <param name="Conditions">"frightened (Mummy)", "Bladesong": each condition or effect with its source.</param>
/// <param name="Concentration">"Circle of Power (level 5)", or null.</param>
/// <param name="DeathSaves">The tallies when dying, stable or dead (death-save makers), else null.</param>
public sealed record CombatantView(
    int? Position,
    string Id,
    string Name,
    string? EntityHandle,
    string Side,
    double? Initiative,
    int? Hp,
    int? MaxHp,
    int TempHp,
    int DamageTaken,
    int? Ac,
    int Exhaustion,
    IReadOnlyList<string> Conditions,
    string? Concentration,
    DeathSaveTally? DeathSaves,
    LegendaryState? Legendary,
    bool Turn,
    bool Defeated,
    bool Dead,
    bool Dying,
    bool Hidden,
    bool Left,
    bool Surprised,
    bool SheetSeeded);

/// <summary>
/// An encounter as the AUTHOR sees it: its row's fight state, the session it is filed under, the end state (outcome, the
/// write-back batch and whether it was undone, D6), the table rows (the order, then the combatants with no initiative,
/// then the ones that left), and the whole tracker state for anything else H2 prints.
/// </summary>
/// <param name="WritebackStatus">Null (no write-back), <see cref="WritebackStatuses.Applied"/>, <see cref="WritebackStatuses.Undone"/> or <see cref="WritebackStatuses.AppliedAgain"/>.</param>
public sealed record EncounterView(
    string Id,
    string Name,
    string Ruleset,
    string Status,
    int Round,
    bool Lair,
    string? TurnCombatantId,
    string? TurnName,
    int? SessionNumber,
    string? OutcomeMd,
    string? WritebackBatchId,
    string? WritebackStatus,
    IReadOnlyList<CombatantView> Rows,
    EncounterState State);

/// <summary>What <c>combat state</c> says about an ended fight's write-back (D6): applied, undone, or applied again after a redo.</summary>
public static class WritebackStatuses
{
    /// <summary>The write-back batch stands (no undo of it, or an undo that was itself undone twice over).</summary>
    public const string Applied = "applied";

    /// <summary>An undo of the write-back batch exists and has not itself been undone ("write-back undone").</summary>
    public const string Undone = "undone";

    /// <summary>The undo was undone (a redo): "write-back applied again".</summary>
    public const string AppliedAgain = "applied_again";
}

/// <summary>
/// One <c>combat</c> step's AUTHOR result (contract §6, H2's "every author step returns"): the encounter after the step
/// (the heading, the table), what changed (the tracker's lines with their arithmetic), the reminders with their calls,
/// the server rolls made and logged, and the D20b warnings (a typed name the party view would not show).
/// </summary>
/// <param name="Action">The combat action (<c>add</c>, <c>damage</c>, …).</param>
/// <param name="Campaign">The campaign's slug.</param>
/// <param name="Created">The step created the encounter (<c>prepare</c>, a <c>start</c> of a new fight).</param>
public sealed record CombatOutcome(
    string Action,
    string Campaign,
    EncounterView Encounter,
    IReadOnlyList<string> Lines,
    IReadOnlyList<CombatReminder> Reminders,
    IReadOnlyList<CombatRoll> Rolls,
    IReadOnlyList<string> Warnings,
    bool Created = false);

/// <summary>A sheet field the write-back wrote (author output): whose, the column, the per-key key, and both values as stored.</summary>
public sealed record CombatSheetChange(string Combatant, string Ref, string Field, string? Key, string? Before, string? After);

/// <summary>A holding the write-back created from <c>loot</c> (author output).</summary>
public sealed record CombatLootView(string HoldingId, string Holder, string Name, string? SrdRef, double Quantity);

/// <summary>A currency_txn row the write-back created from <c>currency</c> (author output).</summary>
public sealed record CombatCurrencyView(string Id, string Holder, long Cp, long Sp, long Ep, long Gp, long Pp);

/// <summary>
/// <c>end</c>'s AUTHOR result (contract §6.7, D6): the encounter as it ends (or as it stays, for a dry run), the batch
/// (null when nothing was logged or for a dry run), every sheet field written, the XP, the consumed items, the loot and
/// coins created, the D19 proposals (author-only, never applied), the reminders (dying, level up, revival traits), the
/// summary lines (what ended with the fight, what was not written and why) and the drift written over by <c>force</c>.
/// </summary>
/// <param name="Discarded">Ended with no write-back (discard, or a planned or paused fight).</param>
/// <param name="NothingChanged">Nothing to log: no batch (the fight still ends).</param>
public sealed record CombatEndOutcome(
    string Campaign,
    EncounterView Encounter,
    string? BatchId,
    bool DryRun,
    int? SessionNumber,
    bool Discarded,
    bool NothingChanged,
    IReadOnlyList<CombatSheetChange> Changes,
    XpSummary Xp,
    IReadOnlyList<XpAward> Awards,
    IReadOnlyList<ItemConsumption> Items,
    IReadOnlyList<CombatLootView> Loot,
    IReadOnlyList<CombatCurrencyView> Currency,
    IReadOnlyList<StatusProposal> Proposals,
    IReadOnlyList<CombatReminder> Reminders,
    IReadOnlyList<string> Summary,
    IReadOnlyList<DriftItem> Overwritten);

/// <summary>One encounter in the no-fight listing (author): its name, status, round, combatant count, and when it ended.</summary>
public sealed record CombatEncounterSummary(string Name, string Status, int Round, int Combatants, string? EndedAt, string? OutcomeMd, string? WritebackStatus);

/// <summary>
/// <c>combat state</c> with no fight running (contract §6.1; also <c>/combat/current</c>): it succeeds, naming the planned
/// encounters (and paused ones) and the last ended one. Author output.
/// </summary>
public sealed record CombatNoFight(
    string Campaign,
    IReadOnlyList<CombatEncounterSummary> Planned,
    IReadOnlyList<CombatEncounterSummary> Paused,
    CombatEncounterSummary? LastEnded);

/// <summary>
/// The author's <c>combat state</c>: the encounter (with the reminders its state calls for now) or, when "current" names
/// no fight, the no-fight listing.
/// </summary>
public sealed record CombatStateRead(string Campaign, EncounterView? Encounter, IReadOnlyList<CombatReminder> Reminders, CombatNoFight? NoFight)
{
    /// <summary>The encounter's last combat_log row (what changed last), or null.</summary>
    public CombatLogView? LastChange { get; init; }
}

/// <summary>
/// One combat_log row as the AUTHOR reads it (contract §6.9): its kind, when (round, and whose turn), who acted on whom
/// (tracker names), the amount, the detail object as stored (arithmetic, given values, a turn row's automatic changes), and
/// the <c>dice_roll</c> it cites.
/// </summary>
public sealed record CombatLogView(long Seq, string Kind, int Round, string? Turn, string? Actor, string? Target, int? Amount, string? Detail, string? RollId, string At)
{
    /// <summary>The row with its combatants named by their tracker names (a combatant no longer in the state reads as its id).</summary>
    public static CombatLogView Of(CombatLogRow row, EncounterState state)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(state);
        string? Name(string? id) => id is null ? null : state.Find(id)?.Name ?? id;
        return new CombatLogView(row.Seq, row.Kind, checked((int)row.Round), Name(row.TurnCombatantId), Name(row.ActorId), Name(row.TargetId),
            row.Amount is { } amount ? checked((int)amount) : null, row.Detail, row.RollId, row.At);
    }
}

/// <summary>
/// The party board (contract §6.12): a WHITELIST model, holding only the strings and numbers the board prints. It never
/// nests a tracker state, a row, a stat block or a snapshot, so nothing author-only can ride along to a formatter or a
/// serializer (the leak tests serialise this whole model).
/// </summary>
/// <param name="Shown">False: "No combat to show for this perspective." (no fight, a planned one, or no match: one wording).</param>
/// <param name="Round">The round (0: not started).</param>
/// <param name="Ended">The fight has ended ("ended in round N").</param>
public sealed record CombatBoard(bool Shown, int Round, bool Ended, IReadOnlyList<CombatBoardRow> Rows)
{
    /// <summary>The one wording for every fight a view may not be shown (§6.12).</summary>
    public const string NothingText = "No combat to show for this perspective.";

    /// <summary>The board of no fight.</summary>
    public static CombatBoard Nothing { get; } = new(false, 0, false, []);
}

/// <summary>
/// One board row (contract §6.12). Numbers only on PARTY-side rows (<see cref="PartySide"/>) that show the combatant's
/// own name (a Shown entity, an srd monster's own name, a typed or custom name that passes the view-text check); every
/// other row, a party-side one read as "an unknown creature" or under a stand-in included, has only its
/// <see cref="HpWord"/>. Conditions by name only.
/// </summary>
/// <param name="Number">1..k over the rows shown (never the tracker's positions).</param>
/// <param name="Name">The view's name for it (entity display name, a passing typed name, the monster's name, or "an unknown creature"), renumbered per view.</param>
/// <param name="Ref">The Phase 6 ref the view may print (<c>kind:slug</c> or <c>e:&lt;n&gt;</c>) for a visible entity; else null.</param>
/// <param name="Turn">The turn marker.</param>
/// <param name="PartySide">The row carries the party's numbers: a party-side combatant shown by its own name (false for a stand-in, so the row says no more than any other).</param>
/// <param name="Hp">Party-side: current HP (null when unknown).</param>
/// <param name="MaxHp">Party-side: the effective maximum.</param>
/// <param name="TempHp">Party-side: temporary HP.</param>
/// <param name="Ac">Party-side: displayed AC.</param>
/// <param name="Exhaustion">Party-side: the exhaustion level.</param>
/// <param name="DeathSaves">Party-side, dying or stable: the tallies.</param>
/// <param name="Concentrating">Party-side: it concentrates.</param>
/// <param name="Concentration">Party-side: the spell's name when it passes the view-text check, else null.</param>
/// <param name="HpWord">Every other row: "unhurt", "hurt", "bloodied" or "down".</param>
/// <param name="Conditions">Condition names (an SRD condition's name, a passing effect name, else "an effect").</param>
public sealed record CombatBoardRow(
    int Number,
    string Name,
    string? Ref,
    bool Turn,
    bool PartySide,
    int? Hp,
    int? MaxHp,
    int? TempHp,
    int? Ac,
    int? Exhaustion,
    DeathSaveTally? DeathSaves,
    bool Concentrating,
    string? Concentration,
    string? HpWord,
    IReadOnlyList<string> Conditions);

/// <summary>The board's HP words (contract §6.12).</summary>
public static class BoardHpWords
{
    /// <summary>No damage taken.</summary>
    public const string Unhurt = "unhurt";

    /// <summary>Damaged, above half its effective maximum (or any damage with unknown hit points).</summary>
    public const string Hurt = "hurt";

    /// <summary>At or below half its effective maximum.</summary>
    public const string Bloodied = "bloodied";

    /// <summary>At 0 HP, defeated or dead.</summary>
    public const string Down = "down";
}

/// <summary>The board's stand-ins for what a view may not read (§6.12).</summary>
public static class BoardNames
{
    /// <summary>A combatant the view has no name for.</summary>
    public const string UnknownCreature = "an unknown creature";

    /// <summary>A named effect whose name the view may not read.</summary>
    public const string Effect = "an effect";
}

/// <summary>
/// The encounter as a simulation (contract §6.11), for <c>balance_simulate {encounter}</c>: the party and enemies
/// (sheet-seeded members through P's <c>SheetSimulation.Entry</c>, snapshots for monsters; possibly EMPTY, the host
/// appends the call's entries and then calls <c>TrackerSimulation.RequireBothSides</c>), the resume point under
/// <c>from_state</c>, the lair flag, the edition, the Notes only the encounter form prints, and the assumption lines the
/// equivalent explicit call also gets.
/// </summary>
/// <param name="Edition">The encounter's ruleset: the run's edition.</param>
public sealed record EncounterSimulation(
    string EncounterName,
    string Edition,
    string Status,
    IReadOnlyList<SimulationCombatant> Party,
    IReadOnlyList<SimulationCombatant> Enemies,
    FightResume? Resume,
    bool Lair,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Assumptions);
