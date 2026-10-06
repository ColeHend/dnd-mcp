using System.Globalization;
using System.Text.Json.Nodes;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Characters;
using DndMcp.Domain.Core;
using DndMcp.Domain.Rules;
using C = DndMcp.Domain.Simulation.StatBlockValues.Conditions;
using D = DndMcp.Domain.Combat.CombatValues.Durations;
using E = DndMcp.Domain.Combat.CombatValues.ExpiryPoints;
using K = DndMcp.Domain.Combat.CombatValues.ReminderKinds;
using R = DndMcp.Domain.Combat.CombatValues.ResourceKeys;
using S = DndMcp.Domain.Campaign.CampaignValues.CombatSides;

namespace DndMcp.Domain.Combat;

/// <summary><c>end</c>'s options that change the write-back (contract §6.7): the XP to award and how.</summary>
/// <param name="Xp">Null: the defeated and fled enemies' XP, awarded only when a party sheet tracks XP; 0: none; N: N.</param>
/// <param name="Discard">End with no write-back (a sandbox, a probe; a planned or paused fight).</param>
/// <param name="Force">Write over drift (a sheet changed since the fight began); a missing sheet's or holding's write is skipped.</param>
/// <param name="DryRun">
/// The end is a preview (<c>dry_run</c>): the plan is the same, only its words change, so the summary never says
/// "Awarded" (or an award "is recorded", or a level reached) under "nothing was written".
/// </param>
public sealed record EndOptions(int? Xp = null, bool Discard = false, bool Force = false, bool DryRun = false);

/// <summary>
/// What the write-back reads besides the fight (contract §12.4b, G6), read by the Repository INSIDE the end transaction:
/// the CURRENT sheets of the sheet-seeded combatants' characters (by entity id; a sheet missing from the map no longer
/// exists), the CURRENT holdings the fight consumed (by holding id; a missing one no longer exists), and the party-safe
/// name of every combatant (§6.10, computed before the transaction), which a persisted condition stores as its source.
/// </summary>
public sealed record EndInputs
{
    public IReadOnlyDictionary<string, CharacterSheet> Sheets { get; init; } = new Dictionary<string, CharacterSheet>();

    public IReadOnlyList<CombatItem> Holdings { get; init; } = [];

    /// <summary>Combatant id → the name a party view would print for it ("Aboleth" for the Nester's combatant).</summary>
    public IReadOnlyDictionary<string, string> SafeNames { get; init; } = new Dictionary<string, string>();

    /// <summary>The campaign's slug, for the ready calls the result prints (null: the calls name no campaign).</summary>
    public string? CampaignSlug { get; init; }
}

/// <summary>
/// One sheet's write-back: the sheet as it is now (<see cref="Before"/>), as it will be (<see cref="After"/>, built with
/// <c>with</c> from the current sheet so unknown keys and unreadable items survive), and the <see cref="SheetDiff"/> the
/// Repository logs (whole values for plain columns, merge patches of only the changed keys for <c>spell_slots</c> and
/// <c>resources</c>).
/// </summary>
public sealed record SheetWriteBack(string CombatantId, string EntityId, string Name, CharacterSheet Before, CharacterSheet After, SheetDiff Diff, IReadOnlyList<string> Notes);

/// <summary>A consumed holding: its quantity now, what is written (now − used, never below 0), and the uses.</summary>
public sealed record ItemConsumption(string HoldingId, string HolderEntityId, string Name, double Before, double After, int Used);

/// <summary>One <c>award</c> row (kind xp) and whether the recipient's sheet tracks XP (null XP: no XP total, the sheet is unchanged).</summary>
public sealed record XpAward(string CombatantId, string EntityId, string Name, int Amount, string Source, bool SheetTracksXp);

/// <summary>What the fight was worth and what was awarded.</summary>
/// <param name="Worth">The defeated and fled enemies' XP (in-lair where the stat block has it and the fight was in its lair).</param>
/// <param name="Awarded">The XP awarded (0 when none).</param>
/// <param name="Each">Each recipient's share (floor).</param>
/// <param name="Remainder">What the floor left undistributed.</param>
/// <param name="Recipients">How many share it (party-side sheet-seeded combatants that did not leave).</param>
/// <param name="Text">The summary line.</param>
public sealed record XpSummary(int Worth, int Awarded, int Each, int Remainder, int Recipients, string Text);

/// <summary>A field whose sheet value changed since the fight began (drift, D6): the column, the key, and both values as stored.</summary>
public sealed record DriftItem(string CombatantId, string Name, string Field, string? Key, string? WhenAdded, string? Now);

/// <summary>An author-only status proposal (D19): a ready <c>campaign_write</c> dry run, never applied by the write-back.</summary>
public sealed record StatusProposal(string CombatantId, string EntityHandle, string Status, string Text, string Call);

/// <summary>
/// The end-of-combat write-back, planned (contract §6.7, D6): per sheet-seeded combatant the combat-owned fields it
/// changed, the consumed holdings, the XP awards, the status proposals and revival traits, the reminders, and the summary
/// lines. The Repository applies it as ONE logged batch (plus the unlogged encounter end); an empty plan ends the fight
/// with "Nothing changed".
/// </summary>
public sealed record EndPlan
{
    public IReadOnlyList<SheetWriteBack> WriteBacks { get; init; } = [];

    public IReadOnlyList<ItemConsumption> Items { get; init; } = [];

    public IReadOnlyList<XpAward> Awards { get; init; } = [];

    public required XpSummary Xp { get; init; }

    public IReadOnlyList<StatusProposal> Proposals { get; init; } = [];

    public IReadOnlyList<CombatReminder> Reminders { get; init; } = [];

    public IReadOnlyList<string> Summary { get; init; } = [];

    /// <summary>Drift written over because of <c>force</c> (said in the result).</summary>
    public IReadOnlyList<DriftItem> Overwritten { get; init; } = [];

    /// <summary>Nothing to log: the fight still ends, with no batch.</summary>
    public bool IsEmpty => WriteBacks.Count == 0 && Items.Count == 0 && Awards.Count == 0;
}

/// <summary>
/// Plans <c>end</c>'s write-back (contract §6.7): pure, so a dry run and the real end plan the same thing, and the
/// Repository only reads the current sheets and holdings and writes what this returns.
/// </summary>
/// <remarks>
/// <para>
/// <b>Combat-owned fields</b> of a sheet-seeded combatant: <c>hp</c>, <c>temp_hp</c>, <c>max_hp_reduction</c>,
/// <c>death_saves</c>, <c>conditions</c>, <c>concentration</c>, <c>exhaustion</c>, and each <c>spell_slots</c>/<c>resources</c>
/// key's <c>used</c>. Only a field whose final value differs from the snapshot is written (the 1st-level slot spent before
/// fixture A is never rewritten), and only when the current sheet still holds the snapshot's value: otherwise it is drift,
/// refused naming each field and both values, unless <c>force</c> (D6).
/// </para>
/// <para>
/// <b>What persists</b>: exhaustion; conditions and effects lasting <c>until_removed</c>; <c>rounds</c> conditions,
/// effects and concentration with MORE than 10 rounds left, stored with <c>remaining_rounds = expires.round − round −
/// (1 if the anchor has already acted this round)</c> (fixture A: Circle of Power expiring at round 101, ended in round 3
/// before Belmakor acts → 98) and the note "from &lt;encounter&gt;, round R"; temporary HP; the maximum reduction; the
/// death-save state. Everything else ends with the fight and the summary says what — except that a combatant down at 0 HP
/// and alive (dying or stable) keeps Unconscious as <c>until_removed</c>, the shape <c>campaign_character damage</c>
/// writes for a drop to 0 (F1, review C08: one representation of "unconscious at 0 HP"; the next fight seeds it back as
/// the tracker's down state, Prone included), and a 2024 knock-out still out keeps it noted <see cref="KnockedOutNote"/>
/// (review CR06).
/// </para>
/// <para>
/// <b>Never Prone</b> (review UR03): on the sheet nothing would end it — a heal ends Unconscious only, a rest keeps what
/// lasts until removed — so a PC who went down once stayed prone through the heal, the long rest and the next day's fight
/// (Disadvantage reminded every turn), which wrote it back again. The down state's Prone is the tracker's
/// <c>until_stands</c>, re-added by the next seed; a Prone the sheet already held (an author's) is written back as stored.
/// Unconscious is written only when the sheet lacks it (review CR08), so a fight that changed nothing writes nothing.
/// </para>
/// </remarks>
public static class CombatEnd
{
    /// <summary>A timed effect with more rounds left than this persists to the sheet (1 minute = 10 ends with the fight).</summary>
    public const int PersistsAboveRounds = 10;

    /// <summary>The <see cref="DriftItem.Field"/> of a consumed holding (its key is the item's name).</summary>
    public const string HoldingField = "holding";

    /// <summary>
    /// The <c>note</c> of the Unconscious a sheet-seeded combatant still knocked out (2024) is written back with (review
    /// CR06), so the sheet says why it is out cold at 1 HP: a heal or a rest ends it on the sheet, and the next fight's seed
    /// reads an Unconscious so noted back as the tracker's knock-out (<see cref="CombatantFactory.FromSheet"/>), so a heal
    /// there wakes it too. Only this note makes one (review F2R04): any other stored Unconscious above 0 HP (a Sleep, an
    /// author's) is seeded until removed.
    /// </summary>
    public const string KnockedOutNote = "knocked out";

    /// <summary>
    /// The summary line of a combatant still knocked out at <c>end</c> (review F2R08): what ends a knock-out, in the words
    /// of the server's own SRD 5.2.1 ("Knocking Out a Creature": "remains Unconscious until it regains any Hit Points or
    /// until someone uses an action to administer first aid to it, which requires a successful DC 10 Wisdom (Medicine)
    /// check"), and the sheet's own convenience said as the sheet's, not as the rule: F2's "or finishes a Short Rest" was a
    /// rule the SRD does not state (the knock-out STARTS a Short Rest), and it left first aid out.
    /// </summary>
    public static string KnockedOutSummary(string name) =>
        $"{name} is still knocked out: the sheet keeps Unconscious ({KnockedOutNote}). By the SRD 5.2.1 rules it remains Unconscious until it " +
        "regains any Hit Points or until someone uses an action to administer first aid to it, which requires a successful DC 10 Wisdom (Medicine) " +
        "check (then remove it with campaign_character condition); on the sheet, a heal or a rest also ends it.";

    /// <summary>The write-back plan.</summary>
    /// <exception cref="DndInputException">Drift without <c>force</c>, or an XP award with no party member to receive it.</exception>
    public static EndPlan Plan(EncounterState state, EndOptions options, EndInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(inputs);
        if (options.Xp is { } given && given < 0)
        {
            throw new DndInputException($"xp is {given.ToString(CultureInfo.InvariantCulture)}; give 0 or more (0 awards none).");
        }

        if (options.Discard)
        {
            return new EndPlan
            {
                Xp = new XpSummary(0, 0, 0, 0, 0, "Discarded: no XP awarded."),
                Summary = ["Ended without a write-back (discard): no sheet, inventory or XP was changed."],
            };
        }

        var summary = new List<string>();
        var reminders = new List<CombatReminder>();
        var drift = new List<DriftItem>();
        var overwritten = new List<DriftItem>();
        var newSheets = new Dictionary<string, (CombatantState Combatant, CharacterSheet Current, CharacterSheet Next, List<string> Notes)>(StringComparer.Ordinal);
        var ended = new List<string>();
        var noHp = new List<string>();

        foreach (var c in state.Combatants.Where(x => x.IsSheetSeeded && x.EntityId is not null))
        {
            if (!inputs.Sheets.TryGetValue(c.EntityId!, out var current))
            {
                var missing = new DriftItem(c.Id, c.Name, "sheet", null, "existed", "no longer exists");
                if (options.Force)
                {
                    overwritten.Add(missing);
                    summary.Add($"{c.Name}: the sheet no longer exists, so nothing was written back for it.");
                }
                else
                {
                    drift.Add(missing);
                }

                continue;
            }

            var notes = new List<string>();
            var next = WriteBack(state, c, current, inputs, options.Force, drift, overwritten, notes, ended, reminders);
            if (!c.HpKnown)
            {
                noHp.Add(c.Name);
            }

            newSheets[c.EntityId!] = (c, current, next, notes);
        }

        var items = new List<ItemConsumption>();
        foreach (var c in state.Combatants.Where(x => x.IsSheetSeeded))
        {
            foreach (var (key, resource) in c.Resources.Where(p => p.Key.StartsWith(R.ItemPrefix, StringComparison.Ordinal) && (p.Value.Used ?? 0) > 0))
            {
                var holdingId = key[R.ItemPrefix.Length..];
                var used = resource.Used!.Value;
                var holding = inputs.Holdings.FirstOrDefault(h => h.HoldingId == holdingId);
                var name = resource.Name ?? holdingId;
                if (holding is null || holding.Quantity - used < 0)
                {
                    var item = new DriftItem(c.Id, c.Name, HoldingField, name,
                        $"used {used.ToString(CultureInfo.InvariantCulture)} in the fight",
                        holding is null ? "the holding no longer exists" : $"only {Q(holding.Quantity)} {(holding.Quantity == 1 ? "is" : "are")} left");
                    if (options.Force)
                    {
                        overwritten.Add(item);
                        summary.Add($"{c.Name}: {name} was not written back ({item.Now}).");
                    }
                    else
                    {
                        drift.Add(item);
                    }

                    continue;
                }

                items.Add(new ItemConsumption(holdingId, holding.HolderEntityId, holding.Name, holding.Quantity, holding.Quantity - used, used));
                summary.Add($"{c.Name}: {holding.Name} {Q(holding.Quantity)} → {Q(holding.Quantity - used)}.");
            }
        }

        if (drift.Count > 0)
        {
            // A holding reads "used 2 in the fight; only 1 is left" (review U13): "2 used in the fight when it joined" was
            // the column wording, which a holding's use does not fit.
            throw new DndInputException(
                "end is refused: these changed since the fight began, and the write-back would overwrite them (give force: true to write over them, " +
                "or end with discard: true to write nothing): " +
                string.Join("; ", drift.Select(d => d.Field == HoldingField
                    ? $"{d.Name} {d.Field} {d.Key}: {d.WhenAdded}; {d.Now}"
                    : $"{d.Name} {d.Field}{(d.Key is null ? string.Empty : $" {d.Key}")}: {d.WhenAdded ?? "none"} when it joined, {d.Now ?? "none"} now")) + ".");
        }

        // The XP line, then one line per sheet that has no XP total (review U02): together, so "split the XP" reads as one
        // answer with every call it needs, never scattered among the sheets' other lines.
        var xp = PlanXp(state, options, inputs, newSheets, reminders, out var awards, out var xpLines);
        summary.Add(xp.Text);
        summary.AddRange(xpLines);

        var writeBacks = new List<SheetWriteBack>();
        foreach (var (entityId, (combatant, current, next, notes)) in newSheets)
        {
            var diff = SheetDiff.Between(current, next);
            if (!diff.IsEmpty)
            {
                writeBacks.Add(new SheetWriteBack(combatant.Id, entityId, combatant.Name, current, next, diff, notes));
            }

            summary.AddRange(notes);
        }

        if (ended.Count > 0)
        {
            summary.Add("Ended with the fight: " + string.Join("; ", ended) + ".");
        }

        if (noHp.Count > 0)
        {
            summary.Add($"No hit points tracked (nothing written for them): {string.Join(", ", noHp)}.");
        }

        var proposals = new List<StatusProposal>();
        foreach (var c in state.Combatants)
        {
            // Still held at 0 HP by a death interceptor (the table never resolved it): defeated for the write-back (D16),
            // and, linked to an entity, proposed dead with the trait quoted — the DM decides whether it held.
            var holding = CombatTracker.HeldAtZeroBy(c);
            var held = holding.Count > 0;
            var diedHere = c.Dead && !DeadWhenSeeded(c, state.Ruleset);
            if (c.EntityHandle is { } handle && (c.IsSheetSeeded ? diedHere : c.Defeated || c.Dead || held))
            {
                if (c.Dead || held)
                {
                    var revival = c.StatBlock is { } block ? StatBlockFacts.RevivalTraits(block) : [];
                    var call = $"campaign_write {{\"ops\": [{{\"op\": \"status\", \"ref\": {CombatCalls.Format(handle)}, \"status\": \"dead\"}}], \"dry_run\": true" +
                               $"{(inputs.CampaignSlug is { } slug ? $", \"campaign\": {CombatCalls.Format(slug)}" : string.Empty)}}}";
                    var how = c.Dead
                        ? $"{c.Name} died in this fight"
                        : $"{c.Name} was left at 0 HP held by {string.Join(", ", holding.Select(t => t.Name))}, which may have kept it alive: {string.Join(" ", holding.Select(t => t.Text))}";
                    var consider = revival.Count > 0
                        ? $"; it has {string.Join(", ", revival.Select(t => t.Name))}, so consider \"unknown\""
                        : held ? "; if the trait held, leave it as it is, or consider \"unknown\"" : string.Empty;
                    var text = $"Proposed: mark {handle} dead ({how}){consider}. Not applied.";
                    proposals.Add(new StatusProposal(c.Id, handle, CampaignValues.Statuses.CharacterDead, text, call));
                }
                else
                {
                    summary.Add($"{c.Name} ({handle}) was defeated but not killed: its entity stays as it is.");
                }
            }

            if (held)
            {
                summary.Add($"{c.Name} ended the fight at 0 HP, still held there by {string.Join(", ", holding.Select(t => t.Name))} (never resolved){(c.Side is S.Enemy or S.Neutral ? ": counted as defeated" : string.Empty)}.");
            }

            if ((c.Defeated || c.Dead || held) && c.StatBlock is { } monster)
            {
                foreach (var trait in StatBlockFacts.RevivalTraits(monster))
                {
                    reminders.Add(new CombatReminder(K.Trait, c.Id, $"{c.Name} — {trait.Name}: {trait.Text}"));
                }
            }
        }

        return new EndPlan
        {
            WriteBacks = writeBacks,
            Items = items,
            Awards = awards,
            Xp = xp,
            Proposals = proposals,
            Reminders = reminders,
            Summary = summary,
            Overwritten = overwritten,
        };
    }

    /// <summary>
    /// The rounds a timed effect has left at the end (contract §6.7): <c>expires.round − round − (1 if its anchor has
    /// already acted this round)</c>; before round 1, everything is still ahead (<c>expires.round − 1</c>).
    /// </summary>
    public static int RoundsLeft(EncounterState state, ConditionExpiry expires)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(expires);
        if (state.Round < 1 || expires.Of is null)
        {
            return expires.Round - Math.Max(state.Round, 1);
        }

        return expires.Round - state.Round - (AnchorActed(state, expires.Of) ? 1 : 0);
    }

    /// <summary>
    /// Whether <paramref name="anchorId"/>'s place in this round's order has passed: it is before the turn-holder's (the
    /// turn-holder itself has not: its turn's end is still ahead, as the simulator's resume counts it).
    /// </summary>
    /// <remarks>
    /// <b>Its place, not its turn.</b> A combatant given an initiative mid-round above the turn-holder has not taken a turn
    /// this round, but its place has passed: its next turn start is in the NEXT round, which is exactly what
    /// <c>remaining_rounds</c> (§6.7) and <c>rounds_left</c> (§6.11) count — the anchor's turn starts still ahead before
    /// the expiry — and where the simulator's resume puts it (before <see cref="Simulation.FightResume.StartAt"/>: acted
    /// this round). The one reader for which "has taken a turn" would differ is
    /// <see cref="Simulation.CombatantStart.HasActed"/> in round 1 (whether a lost concentration setup is paid again only
    /// when worth it); the tracker keeps no per-combatant turn record, so a round-1 latecomer placed above the turn-holder
    /// reads as having acted there.
    /// </remarks>
    public static bool AnchorActed(EncounterState state, string anchorId)
    {
        ArgumentNullException.ThrowIfNull(state);
        var order = state.Order;
        var anchor = -1;
        var turn = -1;
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i].Id == anchorId)
            {
                anchor = i;
            }

            if (order[i].Id == state.TurnCombatantId)
            {
                turn = i;
            }
        }

        return anchor >= 0 && turn >= 0 && anchor < turn;
    }

    // -------------------------------------------------------------------------------------------------------------------
    // One sheet

    private static CharacterSheet WriteBack(
        EncounterState state, CombatantState c, CharacterSheet current, EndInputs inputs, bool force,
        List<DriftItem> drift, List<DriftItem> overwritten, List<string> notes, List<string> ended, List<CombatReminder> reminders)
    {
        var snapshot = c.SheetSnapshot!;
        var next = current;
        var died = c.Dead;
        var deadAlready = died && DeadWhenSeeded(c, state.Ruleset);

        // Down at the end, alive (dying or stable): the sheet keeps "unconscious at 0 HP" as campaign_character damage writes
        // it, Unconscious until removed, so the next fight seeds the tracker's down state from it (review C08). A 2024
        // knock-out still out keeps its Unconscious too, noted, until a heal or a rest (review CR06). Neither keeps the
        // Prone (review UR03): the next seed adds it back as until_stands.
        var downAtEnd = !died && c.HpKnown && c.Hp == 0;
        var knockedOutAtEnd = !died && !downAtEnd && c.KnockedOut;
        var outAtEnd = downAtEnd || knockedOutAtEnd;

        // Plain columns: written when the fight changed them, refused when the sheet changed meanwhile.
        bool Write(string column, object? final)
        {
            var before = snapshot.Column(column);
            if (Same(before, final))
            {
                return false;
            }

            var now = SheetJson.Column(current, column);
            if (!Same(before, now))
            {
                var item = new DriftItem(c.Id, c.Name, column, null, Text(before), Text(now));
                if (!force)
                {
                    drift.Add(item);
                    return false;
                }

                overwritten.Add(item);
            }

            return true;
        }

        if (c.HpKnown)
        {
            // D17: a sheet with no max_hp joins with unknown hit points, so whatever set gave the combatant is written as
            // max_hp WITH hp — also when the sheet held an hp (update takes one alone), which written alone left it
            // "HP 25/—" with no maximum (review M02).
            var hp = died ? 0 : c.Hp!.Value;
            var hadNoMax = snapshot.Integer(SheetColumns.MaxHp) is null;
            if (hadNoMax && Write(SheetColumns.MaxHp, (long)c.MaxHp!.Value))
            {
                next = next with { MaxHp = c.MaxHp };
                notes.Add(snapshot.Integer(SheetColumns.Hp) is null
                    ? $"{c.Name}: the sheet had no hit points; the fight gave it {N(c.MaxHp.Value)}, written as max_hp with hp."
                    : $"{c.Name}: the sheet had no max_hp; the fight gave it {N(c.MaxHp.Value)}, written as max_hp with hp.");
            }

            if (Write(SheetColumns.Hp, (long)hp))
            {
                next = next with { Hp = hp };
                notes.Add($"{c.Name}: hp {Text(snapshot.Column(SheetColumns.Hp)) ?? "none"} → {N(hp)}.");
            }
        }

        if (Write(SheetColumns.TempHp, (long)(died ? 0 : c.TempHp)))
        {
            next = next with { TempHp = died ? 0 : c.TempHp };
            notes.Add($"{c.Name}: temporary HP → {N(died ? 0 : c.TempHp)}.");
        }

        if (Write(SheetColumns.MaxHpReduction, (long)c.MaxHpReduction))
        {
            next = next with { MaxHpReduction = c.MaxHpReduction };
            notes.Add($"{c.Name}: hit point maximum reduction → {N(c.MaxHpReduction)}.");
        }

        if (Write(SheetColumns.Exhaustion, (long)c.Exhaustion))
        {
            next = next with { Exhaustion = c.Exhaustion };
            notes.Add($"{c.Name}: exhaustion → {N(c.Exhaustion)}.");
        }

        var tally = died ? new DeathSaveTally(0, 3, false) : c.HpKnown ? c.DeathSaves : DeathSaveTally.Zero;
        var deathSaves = current.DeathSaves with { Successes = tally.Successes, Failures = tally.Failures, Stable = tally.Stable };
        if (Write(SheetColumns.DeathSaves, SheetJson.WriteDeathSaves(deathSaves)))
        {
            next = next with { DeathSaves = deathSaves };
        }

        // Conditions and concentration: what persists (§6.7), the rest ends with the fight.
        var persisted = new List<SheetCondition>();
        var original = SheetJson.ReadConditions(snapshot.Text(SheetColumns.Conditions)).ToList();
        var cameIn = original.ToList();
        bool? unconsciousAdded = null;
        foreach (var condition in c.Conditions)
        {
            if (outAtEnd && condition.Name == C.Unconscious)
            {
                if (unconsciousAdded is null)
                {
                    var (written, added) = UnconsciousAtEnd(cameIn, knockedOutAtEnd);
                    persisted.Add(written);
                    unconsciousAdded = added;
                }

                continue;
            }

            // The down state's (or the knock-out's) Prone: the sheet's Unconscious seeds it back, so it did not "end".
            if (outAtEnd && condition.Name == C.Prone && condition.Duration == D.UntilStands)
            {
                continue;
            }

            var left = condition.Duration == D.Rounds && condition.Expires is { At: E.Start } e ? RoundsLeft(state, e) : (int?)null;
            if (condition.Duration == D.UntilRemoved || left > PersistsAboveRounds)
            {
                var written = new SheetCondition(
                    condition.Name,
                    SourceText(condition, inputs),
                    condition.Duration,
                    left,
                    Note(state, condition.Applied, condition.Note),
                    condition.Effect is { IsEmpty: false } effect ? CombatJson.WriteEffect(effect) : null)
                {
                    Extra = condition.Extra,
                };

                // A condition that came in from the sheet and did not change goes back exactly as the sheet stored it (its
                // spelling, an absent duration, which P reads as until_removed): only a real change rewrites the column, so
                // a fight that changed nothing writes nothing (§6.7) and leaves no batch for a later undo to trip over.
                if (condition.Applied is null && cameIn.FindIndex(o => SameSheetCondition(o, written)) is var at and >= 0)
                {
                    written = cameIn[at];
                    cameIn.RemoveAt(at);
                }

                persisted.Add(written);
            }
            else
            {
                // A stored entry of the same name the fight's shadowed survives (WithShadowed): said, so "ended" is not read
                // as the condition gone from the sheet.
                var shadows = snapshot.Shadowed.Any(n => string.Equals(Canonical(n), Canonical(condition.Name), StringComparison.OrdinalIgnoreCase));
                ended.Add(shadows ? $"{condition.Name} ({c.Name}; the sheet's own {condition.Name} stays)" : $"{condition.Name} ({c.Name})");
            }
        }

        if (downAtEnd && unconsciousAdded is null)
        {
            var (written, added) = UnconsciousAtEnd(cameIn, knockedOut: false);
            persisted.Add(written);
            unconsciousAdded = added;
        }

        persisted = WithShadowed(snapshot, original, cameIn, persisted);

        if (Write(SheetColumns.Conditions, SheetJson.WriteConditions(persisted, Unreadable(current, SheetColumns.Conditions))))
        {
            next = next with { Conditions = persisted };
            notes.Add(persisted.Count == 0
                ? $"{c.Name}: no conditions persist."
                : $"{c.Name}: conditions kept on the sheet: {string.Join(", ", persisted.Select(p => p.Name))}.");
            if (unconsciousAdded == true && downAtEnd && snapshot.Integer(SheetColumns.Hp) == 0)
            {
                // It came in at 0 HP without Unconscious (update {hp: 0} stores none): a correction, not the fight's doing.
                notes.Add($"{c.Name} was at 0 HP on its sheet without Unconscious: the sheet gets it now (a heal ends it).");
            }
            else if (unconsciousAdded == true && knockedOutAtEnd)
            {
                notes.Add(KnockedOutSummary(c.Name));
            }
        }

        SheetConcentration? concentration = null;
        if (!died && c.Concentration is { } held)
        {
            var left = held.Duration == D.Rounds && held.Expires is { At: E.Start } e ? RoundsLeft(state, e) : (int?)null;
            if (left > PersistsAboveRounds)
            {
                concentration = new SheetConcentration(held.Spell, held.Level, left, Note(state, held.Applied, held.Note)) { Extra = held.Extra };
            }
            else
            {
                ended.Add($"concentration on {held.Spell} ({c.Name}{(held.Duration is null ? ": it had no duration" : string.Empty)})");
            }
        }

        if (Write(SheetColumns.Concentration, SheetJson.WriteConcentration(concentration)))
        {
            next = next with { Concentration = concentration };
            notes.Add(concentration is null
                ? $"{c.Name}: concentration ended."
                : $"{c.Name}: concentration on {concentration.Spell} kept ({N(concentration.RemainingRounds!.Value)} rounds left).");
        }

        // Spell slots and resources: only each key's used, per key.
        var slotsBefore = SheetJson.ReadSpellSlots(snapshot.Text(SheetColumns.SpellSlots)).Slots;
        var resourcesBefore = SheetJson.ReadResources(snapshot.Text(SheetColumns.Resources)).Resources;
        foreach (var (key, resource) in c.Resources)
        {
            string? sheetKey = key == R.Pact ? SpellSlotEntry.PactKey
                : key.StartsWith(R.SlotPrefix, StringComparison.Ordinal) ? key[R.SlotPrefix.Length..]
                : null;
            if (sheetKey is not null)
            {
                if (!slotsBefore.TryGetValue(sheetKey, out var was) || was.Used == (resource.Used ?? 0))
                {
                    continue;
                }

                if (!next.SpellSlots.TryGetValue(sheetKey, out var now) || now.Used != was.Used)
                {
                    var item = new DriftItem(c.Id, c.Name, SheetColumns.SpellSlots, sheetKey, $"{N(was.Used)} used", now is null ? "no such slot" : $"{N(now.Used)} used");
                    if (!force || now is null)
                    {
                        (force ? overwritten : drift).Add(item);
                        continue;
                    }

                    overwritten.Add(item);
                }

                next = next with { SpellSlots = SheetMaps.With(next.SpellSlots, sheetKey, now with { Used = resource.Used ?? 0 }) };
                notes.Add($"{c.Name}: {(sheetKey == SpellSlotEntry.PactKey ? "Pact Magic slots" : $"level {sheetKey} slots")} {N(now.Max - (resource.Used ?? 0))}/{N(now.Max)} left.");
                continue;
            }

            if (key.Contains(':') || !resourcesBefore.TryGetValue(key, out var before) || before.Max is null || (before.Used ?? 0) == (resource.Used ?? 0))
            {
                continue;
            }

            if (!next.Resources.TryGetValue(key, out var currentResource) || (currentResource.Used ?? 0) != (before.Used ?? 0))
            {
                var item = new DriftItem(c.Id, c.Name, SheetColumns.Resources, key, $"{N(before.Used ?? 0)} used", currentResource is null ? "no such resource" : $"{N(currentResource.Used ?? 0)} used");
                if (!force || currentResource is null)
                {
                    (force ? overwritten : drift).Add(item);
                    continue;
                }

                overwritten.Add(item);
            }

            next = next with { Resources = SheetMaps.With(next.Resources, key, currentResource with { Used = resource.Used ?? 0 }) };
            notes.Add($"{c.Name}: {currentResource.Name} {N((currentResource.Max ?? 0) - (resource.Used ?? 0))}/{N(currentResource.Max ?? 0)} left.");
        }

        if (deadAlready)
        {
            // Seeded dead (add_party checks the entity, not the sheet): it did not die here (review CV02).
            notes.Add($"{c.Name} was already dead when it joined the fight: no death to record here.");
        }
        else if (died)
        {
            notes.Add($"{c.Name} died: written with hp 0 and three failed death saves.");
        }
        else if (c.Dying)
        {
            // The sheet is written dying; the way out (§6.7): a new fight's death saves, or healing on the sheet — whose call
            // the reminder carries, so the text ends on it (review U13).
            reminders.Add(new CombatReminder(
                K.DyingAtEnd,
                c.Id,
                $"{c.Name} is dying at 0 HP ({CombatContext.Tallies(c.DeathSaves)}): continue with combat {{\"action\": \"start\", …}} and death_save " +
                "(out of combat, a critical at 0 HP is two damage calls, each adding one failure), or heal on the sheet:",
                CombatCalls.Tool("campaign_character", "heal", ("character", c.EntityHandle ?? c.Name), ("amount", CombatCalls.Fill))));
        }

        return next;
    }

    /// <summary>
    /// Whether a sheet-seeded combatant's sheet was already dead when it joined (its snapshot: three failed death saves,
    /// exhaustion 6, or an effective maximum of 0), as <see cref="CharacterSheet.IsDead"/> reads the sheet. Only a combatant
    /// that turned dead during the fight gets the death line and the status proposal (review CV02).
    /// </summary>
    internal static bool DeadWhenSeeded(CombatantState c, string ruleset)
    {
        if (c.SheetSnapshot is not { } snapshot)
        {
            return false;
        }

        var exhaustion = snapshot.Integer(SheetColumns.Exhaustion) ?? 0;
        return SheetJson.ReadDeathSaves(snapshot.Text(SheetColumns.DeathSaves)).Failures >= 3
            || exhaustion >= SheetLimits.MaxExhaustion
            || (snapshot.Integer(SheetColumns.MaxHp) is { } max
                && HitPointMath.EffectiveMaxHp(max, snapshot.Integer(SheetColumns.MaxHpReduction) ?? 0, exhaustion, ruleset) == 0);
    }

    /// <summary>
    /// The Unconscious a sheet keeps for a combatant down at 0 HP or still knocked out (§6.7, F1/F2): the one the sheet
    /// already holds, exactly as stored (review CR08: so a fight that changed nothing writes nothing), else as
    /// <c>campaign_character damage</c> writes a drop to 0 — <c>{"name": "unconscious", "duration": "until_removed"}</c>,
    /// no source — with the note <see cref="KnockedOutNote"/> for a knock-out. <c>Added</c>: the sheet lacked it.
    /// </summary>
    private static (SheetCondition Written, bool Added) UnconsciousAtEnd(List<SheetCondition> cameIn, bool knockedOut)
    {
        var at = cameIn.FindIndex(o => Canonical(o.Name) == C.Unconscious);
        if (at < 0)
        {
            return (new SheetCondition(C.Unconscious, null, D.UntilRemoved, null, knockedOut ? KnockedOutNote : null), true);
        }

        var stored = cameIn[at];
        cameIn.RemoveAt(at);
        return (stored, false);
    }

    /// <summary>
    /// The conditions to write with the stored ones a re-seed left shadowed (<see cref="SheetSnapshot.Shadowed"/>, review
    /// F2R03) put back exactly as stored: such an entry never joined the fight (a condition of its name was already on the
    /// combatant), so nothing in the fight ended it, and its persistence wins at <c>end</c> — a persisting fight condition of
    /// the same name gives way to it, a shorter one ended with the fight (and the summary says so). Stored entries keep the
    /// sheet's order, the fight's own follow, so a fight that changed nothing else rewrites nothing. With no shadowed name,
    /// <paramref name="persisted"/> as it is.
    /// </summary>
    /// <param name="original">The snapshot's conditions as stored.</param>
    /// <param name="unmatched">Those of them no fight condition went back as (the shadowed ones among them survive).</param>
    private static List<SheetCondition> WithShadowed(SheetSnapshot snapshot, List<SheetCondition> original, List<SheetCondition> unmatched, List<SheetCondition> persisted)
    {
        if (snapshot.Shadowed.Count == 0)
        {
            return persisted;
        }

        var names = snapshot.Shadowed.Select(Canonical).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var survivors = unmatched.Where(o => names.Contains(Canonical(o.Name))).ToList();
        if (survivors.Count == 0)
        {
            return persisted;
        }

        var stored = new HashSet<object>(original, ReferenceEqualityComparer.Instance);
        var winning = survivors.Select(o => Canonical(o.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = new HashSet<object>(ReferenceEqualityComparer.Instance);
        kept.UnionWith(persisted.Where(stored.Contains));
        kept.UnionWith(survivors);
        return
        [
            .. original.Where(kept.Contains),
            .. persisted.Where(p => !stored.Contains(p) && !winning.Contains(Canonical(p.Name))),
        ];
    }

    private static IReadOnlyList<SheetRawItem>? Unreadable(CharacterSheet sheet, string column) =>
        sheet.Unreadable.TryGetValue(column, out var items) ? items : null;

    /// <summary>
    /// Whether a condition the sheet stored (<paramref name="stored"/>) means the same as the one the fight would write
    /// back: the same condition (canonical SRD name, else the name as typed), source, duration (an absent one is
    /// until_removed), rounds left, note, effect and unknown keys.
    /// </summary>
    private static bool SameSheetCondition(SheetCondition stored, SheetCondition written) =>
        Canonical(stored.Name) == Canonical(written.Name) &&
        stored.Source == written.Source &&
        stored.DurationOrDefault == written.DurationOrDefault &&
        stored.RemainingRounds == written.RemainingRounds &&
        stored.Note == written.Note &&
        Same(EffectText(stored.Effect), EffectText(written.Effect)) &&
        Same(stored.Extra, written.Extra);

    private static string Canonical(string name) => CombatConditions.TryMatch(name, out var canonical) ? canonical : name;

    private static string? EffectText(string? effect) =>
        effect is not null && CombatJson.ReadEffect(effect) is { IsEmpty: false } read ? CombatJson.WriteEffect(read) : null;

    /// <summary>A persisted condition's source: the source combatant's party-safe name, else the note it came with.</summary>
    private static string? SourceText(CombatCondition condition, EndInputs inputs) =>
        condition.Source is { } id ? inputs.SafeNames.TryGetValue(id, out var safe) ? safe : "a combatant" : condition.SourceNote;

    /// <summary>"from The dark station (fixture), round 1" (+ "; " and its own note); a sheet's own condition keeps its note.</summary>
    private static string? Note(EncounterState state, AppliedAt? applied, string? own) =>
        applied is null ? own : $"from {state.Name}, round {N(applied.Round)}{(own is null ? string.Empty : "; " + own)}";

    // -------------------------------------------------------------------------------------------------------------------
    // XP

    private static XpSummary PlanXp(
        EncounterState state, EndOptions options, EndInputs inputs,
        Dictionary<string, (CombatantState Combatant, CharacterSheet Current, CharacterSheet Next, List<string> Notes)> sheets,
        List<CombatReminder> reminders, out List<XpAward> awards, out List<string> lines)
    {
        awards = [];
        lines = [];
        var worth = 0;
        var unrated = new List<string>();
        var counted = state.Combatants.Where(c => c.Side == S.Enemy && (c.Defeated || c.Dead || c.Removed)).ToList();
        foreach (var enemy in counted)
        {
            if (enemy.StatBlock is { } block)
            {
                worth += StatBlockFacts.Xp(block, state.Lair);
            }
            else
            {
                unrated.Add(enemy.Name);
            }
        }

        var party = state.Combatants.Where(c => c.Side == S.Party && c.IsSheetSeeded && c.EntityId is not null).ToList();
        var recipients = party.Where(c => !c.Removed && sheets.ContainsKey(c.EntityId!)).ToList();
        var tracks = party.Any(c => sheets.TryGetValue(c.EntityId!, out var s) && s.Current.Xp is not null);
        var unratedText = unrated.Count == 0 ? string.Empty : $" ({string.Join(", ", unrated)} {(unrated.Count == 1 ? "has" : "have")} no stat block, so no XP)";
        var rules = state.Ruleset;
        int award;
        if (options.Xp is { } given)
        {
            if (given > 0 && party.Count == 0)
            {
                throw new DndInputException("xp is awarded to the party's sheet-seeded combatants, and this fight has none; leave out xp (or give 0).");
            }

            award = given;
        }
        else
        {
            award = tracks ? worth : 0;
        }

        var count = recipients.Count;
        var each = count == 0 ? 0 : award / count;
        var remainder = count == 0 ? award : award % count;
        if (award == 0 || count == 0)
        {
            if (options.Xp is null && counted.Count == 0 && UnclaimedXp(state, options, tracks, recipients, sheets, lines) is { } unclaimed)
            {
                return new XpSummary(worth, 0, 0, 0, count, unclaimed);
            }

            if (options.Xp is null && !tracks && worth > 0 && count > 0)
            {
                // No sheet tracks XP, so none is written (§6.7): each sheet's line says how to start a total with its share
                // (review U02, the defeated-enemies path: "split the XP" otherwise left the sheets with none and no call).
                lines.AddRange(recipients.Select(c => NoXpTotal(c, worth / count, recorded: null, options.DryRun)));
                lines.AddRange(RealEndPrintsTheCalls(options, recipients.Count));
            }

            // Why nothing is awarded, in the order the rules decide it: the call said 0; no sheet tracks XP; nobody is left to
            // receive it; or the fight is worth nothing (no enemy defeated or left, or those that were are worth 0 XP).
            var why = options.Xp == 0 ? "xp: 0"
                : options.Xp is null && !tracks ? "no party sheet tracks XP"
                : count == 0 ? "no party member to receive it"
                : counted.Count == 0 ? "no enemy was defeated or left"
                : "the defeated and left enemies are worth no XP";
            var split = recipients.Count > 0 ? $", {X(worth / recipients.Count)} each for {N(recipients.Count)}" : string.Empty;
            return new XpSummary(worth, 0, 0, 0, count,
                $"XP not awarded ({why}). By the {rules} rules this fight is worth {X(worth)} XP{split}{unratedText}.");
        }

        var untracked = 0;
        foreach (var c in recipients)
        {
            var (combatant, current, next, notes) = sheets[c.EntityId!];
            var tracksXp = current.Xp is not null;
            awards.Add(new XpAward(c.Id, c.EntityId!, c.Name, each, $"encounter: {state.Name}", tracksXp));
            if (!tracksXp)
            {
                // A sheet with no XP total keeps none (§6.7: the award is recorded, the sheet unchanged). Said as what it is,
                // never "levels by milestone", a policy nobody chose that the model then declined to overturn (review U02).
                lines.Add(NoXpTotal(c, each, $", so its {X(each)} XP share {(options.DryRun ? "would be" : "is")} recorded as an award only", options.DryRun));
                untracked++;
                continue;
            }

            var xp = current.Xp!.Value + each;
            sheets[c.EntityId!] = (combatant, current, next with { Xp = xp }, notes);
            notes.Add($"{c.Name}: XP {X(current.Xp.Value)} → {X(xp)}.");
            if (current.Level is { } level && Advancement.LevelForXp(xp) > level)
            {
                var handle = c.EntityHandle ?? c.Name;
                reminders.Add(new CombatReminder(
                    K.LevelUp,
                    c.Id,
                    $"{c.Name}'s {X(xp)} XP {(options.DryRun ? "would reach" : "reaches")} level {N(Advancement.LevelForXp(xp))}: level up",
                    CombatCalls.Tool("campaign_character", "level_up", ("character", handle))));
            }
        }

        lines.AddRange(RealEndPrintsTheCalls(options, untracked));
        var rest = remainder > 0 ? $"; {X(remainder)} XP undistributed" : string.Empty;
        return new XpSummary(worth, award, each, remainder, count,
            $"{(options.DryRun ? "Would award" : "Awarded")} {X(award)} XP: {X(each)} each to {string.Join(", ", recipients.Select(r => r.Name))}{rest}{unratedText}.");
    }

    /// <summary>
    /// The summary of an end that names no <c>xp</c> after a fight in which no enemy was marked defeated or left (review
    /// U02): "worth 0 XP" with no call read as "there is nothing to split", though the table may simply have won without
    /// telling the tracker. It says what the enemies are worth and how to give it: a dry run's next call is the end with
    /// <c>xp</c>; a real end has ended the fight, so its calls are the sheets' own (<c>campaign_character xp</c>, one per
    /// recipient), each saying what it does (review RR03): on a sheet with an XP total it adds the share, and unlike
    /// <c>end {xp}</c> it records no award row; a sheet without one gets its own line offering the call as the start of a
    /// total (<see cref="NoXpTotal"/>), never a plain call that silently starts tracking it. Null when the enemies are worth
    /// nothing (or nobody could receive it): the plain summary says that.
    /// </summary>
    private static string? UnclaimedXp(
        EncounterState state, EndOptions options, bool tracks, IReadOnlyList<CombatantState> recipients,
        Dictionary<string, (CombatantState Combatant, CharacterSheet Current, CharacterSheet Next, List<string> Notes)> sheets, List<string> lines)
    {
        var enemies = state.Combatants.Where(c => c.Side == S.Enemy).ToList();
        var total = enemies.Where(e => e.StatBlock is not null).Sum(e => StatBlockFacts.Xp(e.StatBlock!, state.Lair));
        if (total == 0 || recipients.Count == 0)
        {
            return null;
        }

        var unrated = enemies.Where(e => e.StatBlock is null).Select(e => e.Name).ToList();
        var share = total / recipients.Count;
        string how;
        if (options.DryRun)
        {
            how = $"award it: {CombatCalls.Combat("end", ("xp", total))}";
        }
        else
        {
            var tracked = recipients.Where(r => sheets[r.EntityId!].Current.Xp is not null).ToList();
            lines.AddRange(recipients.Except(tracked).Select(r => NoXpTotal(r, share, recorded: null, dryRun: false)));

            how = tracked.Count switch
            {
                0 => "start each sheet's XP total with its own call",
                1 => $"add the share to its sheet: {XpCall(tracked[0], share)} (it adds to the sheet's XP total and records no award)",
                _ => $"add each share to its sheet: {string.Join("; ", tracked.Select(r => XpCall(r, share)))} (each adds to that sheet's XP total and records no award)",
            };
        }

        return $"XP not awarded: no enemy was defeated or left{(tracks ? string.Empty : ", and no party sheet tracks XP")}. By the {state.Ruleset} rules its " +
               $"enemies are worth {X(total)} XP, {X(share)} each for {N(recipients.Count)}" +
               $"{(unrated.Count == 0 ? string.Empty : $" ({string.Join(", ", unrated)} {(unrated.Count == 1 ? "has" : "have")} no stat block, so no XP)")}. " +
               $"If the party beat them, {how}.";
    }

    /// <summary>
    /// "XP not written to Mira: the sheet has no XP total; start one with: campaign_character {…} (it sets the sheet's XP
    /// total and records no award)." (review U02/RR03): a sheet with no XP value has no XP total, which the call starts at
    /// the share (<see cref="SheetXp"/> counts from 0) — said as the start of a total, so it is neither read as a milestone
    /// policy to keep nor sent as if it added to one. <paramref name="recorded"/>: what became of the share (an award only).
    /// A dry run prints no call (review F2R06): its own advice read as a step to take before the real end, and sent then it
    /// started the total at the share, which the real end, now finding a total, added again — the share counted twice. The
    /// dry run says instead that the real end prints the calls (<see cref="RealEndPrintsTheCalls"/>).
    /// </summary>
    private static string NoXpTotal(CombatantState c, int amount, string? recorded, bool dryRun) => dryRun
        ? $"XP not written to {c.Name}: the sheet has no XP total{recorded}."
        : $"XP not written to {c.Name}: the sheet has no XP total{recorded}; start one with: {XpCall(c, amount)} (it sets the sheet's XP total and records no award).";

    /// <summary>A dry run's one line after its sheets with no XP total (<see cref="NoXpTotal"/>): the real end prints their calls.</summary>
    private static IEnumerable<string> RealEndPrintsTheCalls(EndOptions options, int sheets)
    {
        if (options.DryRun && sheets > 0)
        {
            yield return sheets == 1 ? "The real end prints the call to start its XP total." : "The real end prints the calls to start XP totals.";
        }
    }

    /// <summary><c>campaign_character {"action": "xp", "character": "character:tor", "amount": 300}</c>: XP on one sheet.</summary>
    private static string XpCall(CombatantState c, int amount) =>
        CombatCalls.Tool("campaign_character", "xp", ("character", c.EntityHandle ?? c.Name), ("amount", amount));

    // -------------------------------------------------------------------------------------------------------------------
    // helpers

    private static bool Same(object? a, object? b)
    {
        if (a is string s && b is string t)
        {
            try
            {
                return JsonNode.DeepEquals(JsonNode.Parse(s), JsonNode.Parse(t));
            }
            catch (System.Text.Json.JsonException)
            {
                return string.Equals(s, t, StringComparison.Ordinal);
            }
        }

        return Equals(Normalize(a), Normalize(b));
    }

    private static object? Normalize(object? value) => value is int i ? (long)i : value;

    private static string? Text(object? value) => value switch
    {
        null => null,
        long l => l.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string X(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Q(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
