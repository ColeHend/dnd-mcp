using System.Globalization;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Characters;
using DndMcp.Domain.Core;
using DndMcp.Domain.Dice;
using DndMcp.Domain.Features;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Repository.Campaign.Write;
using CV = DndMcp.Domain.Campaign.CampaignValues;

namespace DndMcp.Repository.Campaign.Characters;

/// <summary>
/// The writes of <c>campaign_character</c> (contract §7.1, §13.3): one method per action, each ONE logged batch
/// (<see cref="WriteBatch.Run{T}"/>, tool <c>campaign_character/&lt;action&gt;</c>, the session context and reason of the
/// call, <c>dry_run</c> rolling everything back), writing only through the batch's recorder: a new sheet whole, then plain
/// columns with <c>Update</c> and the per-key columns with <c>PatchObject</c> (<see cref="CharacterSheetStore"/>), holdings
/// and coins with <c>Insert</c>/<c>Update</c>/<c>Delete</c>. The sheet rules are P's (<see cref="SheetUpdate"/>,
/// <see cref="SheetRest"/>, <see cref="SheetLevelUp"/>, <see cref="SheetUse"/>, <see cref="SheetConditions"/>,
/// <see cref="SheetXp"/>) and R's (damage, healing and temporary hit points: <see cref="Domain.Rules.CombatRules"/>), so the
/// sheet, the tracker and the simulator agree on every number.
///
/// <para>
/// <b>Nothing logged, no batch id.</b> A call that changes nothing ("Nothing changed"), a dry run, and an action routed to
/// the live fight return <see cref="CharacterWriteResult.BatchId"/> null: there is nothing to undo, and an undo hint for a
/// batch with no rows would fail.
/// </para>
/// <para>
/// <b>The live fight</b> (contract D5). While the character is a sheet-seeded combatant of its campaign's active encounter,
/// damage, heal, temp_hp, use and condition go to the combatant through <see cref="ICombatRouter"/> (unlogged; the sheet is
/// written when the fight ends), and a rest is refused. A character in the fight from a stat block is acted on as out of
/// combat, with a note that the fight was not changed. The router is consulted inside the batch's transaction, so a fight
/// that started a moment ago is seen.
/// </para>
/// <para>
/// <b>Every next sheet is built with <c>with</c> from the sheet as read</b> (P's operations do the same), so members a
/// later version wrote (<see cref="CharacterSheet.Extras"/>, <see cref="CharacterSheet.Unreadable"/>) survive every write.
/// A stored row that cannot be read at all is refused as a store problem (<see cref="CharacterSheetStore"/>).
/// </para>
/// <para>
/// <b>Refusals</b> are <see cref="DndInputException"/>s for the author (the only caller of a write): they may name the
/// character, its resources and conditions, never anything another campaign holds.
/// </para>
/// </summary>
public sealed partial class CharacterWriter
{
    private readonly CampaignDatabase _database;
    private readonly ICombatRouter? _router;

    /// <param name="router">The combat layer's router (contract D5); null: every action acts on the sheet.</param>
    public CharacterWriter(CampaignDatabase database, ICombatRouter? router = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        _router = router;
    }

    /// <summary>
    /// <c>update {sheet, sim_profile}</c>: creates the sheet on first use (the character must exist), else patches only
    /// the fields given, with P's derivations (§7.2).
    /// </summary>
    /// <exception cref="DndInputException">Nothing given, an unknown character (with the call that creates it), or P's refusals.</exception>
    public CharacterWriteResult Update(CampaignRow campaign, string? character, SheetSpec? sheet, BuildSpec? simProfile, WriteContext context)
    {
        if (sheet is null && simProfile is null)
        {
            throw new DndInputException(
                "update needs sheet (the fields to set), sim_profile, or both, e.g. {\"action\": \"update\", \"sheet\": {\"classes\": [{\"class\": \"wizard\", \"level\": 12}], \"ac\": 17}}.");
        }

        return Run(campaign, character, CharacterActions.Update, context, sheetRequired: false, missingHint: CharacterLookup.CreateHint(campaign), work: s =>
        {
            var result = SheetUpdate.Apply(s.Sheet, sheet, simProfile, s.Edition, s.Entity.Id);
            if (result.Created)
            {
                CharacterSheetStore.Insert(s.Batch.Recorder, result.Sheet);
            }
            else
            {
                CharacterSheetStore.Apply(s.Batch.Recorder, s.Entity.Id, result.Diff);
            }

            return s.Result(result.Diff, result.Notes, result.Reminders, created: result.Created);
        });
    }

    /// <summary><c>use {slot_level | pact | resource, amount = 1}</c>: spends uses (negative restores); during the fight, on the combatant.</summary>
    /// <exception cref="DndInputException">No sheet, or P's refusals (no such slots or resource, more than is left).</exception>
    public CharacterWriteResult Use(CampaignRow campaign, string? character, int? slotLevel, bool pact, string? resource, int? amount, WriteContext context)
    {
        var uses = amount ?? 1;

        // The call's shape is checked before the fight is asked (the fight then checks what is left, as the sheet does).
        if ((slotLevel is not null ? 1 : 0) + (pact ? 1 : 0) + (resource is not null ? 1 : 0) != 1)
        {
            throw new DndInputException("give exactly one of slot_level (1-9), pact (true) or resource (its name), and amount (default 1; negative restores).");
        }

        // As a long: Math.Abs(int.MinValue) has no int and throws, which reached the model as the SDK's bare error.
        if (uses == 0 || Math.Abs((long)uses) > SheetLimits.MaxUseAmount)
        {
            throw new DndInputException($"amount is {N(uses)}; give 1 to {N(SheetLimits.MaxUseAmount)} uses spent, or a negative number to restore.");
        }

        var action = new CharacterAction { Kind = CharacterActions.Use, SlotLevel = slotLevel, Pact = pact, Resource = resource, Amount = uses };
        return Run(campaign, character, CharacterActions.Use, context, route: action, work: s =>
        {
            var result = SheetUse.Apply(s.RequireSheet(), slotLevel, pact, resource, uses);
            return s.Apply(result);
        });
    }

    /// <summary>
    /// <c>condition {add, remove, level}</c>: conditions and named effects on the sheet (default duration
    /// <c>until_removed</c>), exhaustion by levels; <c>remove: ["concentration"]</c> ends the sheet's concentration (the
    /// resolving call of a failed concentration save out of combat). During the fight, on the combatant, keeping
    /// <c>until_removed</c> (D5).
    /// </summary>
    /// <exception cref="DndInputException">No sheet, or P's refusals (immune, duplicate, not on the sheet).</exception>
    public CharacterWriteResult Condition(CampaignRow campaign, string? character, IReadOnlyList<string>? add, IReadOnlyList<string>? remove, int? level, WriteContext context)
    {
        if ((add?.Count ?? 0) + (remove?.Count ?? 0) == 0)
        {
            throw new DndInputException("give add or remove: condition names, e.g. {\"add\": [\"poisoned\"]}, or exhaustion with level.");
        }

        var action = new CharacterAction
        {
            Kind = CharacterActions.Condition, Add = add, Remove = remove, Level = level, Duration = SheetValues.Durations.UntilRemoved,
        };
        return Run(campaign, character, CharacterActions.Condition, context, route: action, work: s =>
        {
            var sheet = s.RequireSheet();
            var notes = new List<string>();
            var others = (remove ?? []).Where(r => !IsConcentration(r)).ToList();
            var dropsConcentration = others.Count < (remove?.Count ?? 0);
            if (dropsConcentration)
            {
                if (sheet.Concentration is not { } held)
                {
                    throw new DndInputException("remove: the sheet holds no concentration.");
                }

                notes.Add($"Concentration on {held.Display} ended.");
                sheet = sheet with { Concentration = null };
            }

            if ((add?.Count ?? 0) + others.Count == 0)
            {
                return s.Result(SheetDiff.Between(s.Sheet, sheet), notes, []);
            }

            var result = SheetConditions.Apply(sheet, add, others, level, s.Edition);
            return s.Result(SheetDiff.Between(s.Sheet, result.Sheet), [.. notes, .. result.Notes], result.Reminders);
        });
    }

    /// <summary><c>level_up {class, amount}</c>: one level (P's <see cref="SheetLevelUp"/>, D18).</summary>
    /// <exception cref="DndInputException">No sheet, or P's refusals.</exception>
    public CharacterWriteResult LevelUp(CampaignRow campaign, string? character, string? className, int? amount, WriteContext context) =>
        Run(campaign, character, CharacterActions.LevelUp, context, work: s => s.Apply(SheetLevelUp.Apply(s.RequireSheet(), className, amount, s.Edition)));

    /// <summary><c>xp {amount}</c> (signed): the sheet's XP only (no award row: <c>combat end</c> is the only writer of awards).</summary>
    /// <exception cref="DndInputException">No amount, no sheet, or P's refusals.</exception>
    public CharacterWriteResult Xp(CampaignRow campaign, string? character, int? amount, WriteContext context)
    {
        var xp = amount ?? throw new DndInputException("amount is required for xp: the XP gained (negative to take XP away), e.g. {\"action\": \"xp\", \"amount\": 2400}.");
        return Run(campaign, character, CharacterActions.Xp, context, work: s => s.Apply(SheetXp.Apply(s.RequireSheet(), xp)));
    }

    /// <summary>
    /// <c>rest {kind, hit_dice, rolls}</c> (§5.15): a short or long rest. The Hit Dice a short rest spends are rolled here
    /// with <paramref name="roller"/> when <paramref name="rolls"/> gives no faces, each die size one dice_roll row in the
    /// same transaction (D11: labelled "&lt;name&gt;: hit dice" with the session-safe name, secret unless the character is a
    /// current party member the party is shown undisguised). Refused while the character is in the live fight.
    /// </summary>
    /// <exception cref="DndInputException">A bad kind, no sheet, in the fight, or P's refusals (dead, dying, too many Hit Dice).</exception>
    public CharacterWriteResult Rest(CampaignRow campaign, string? character, string? kind, int? hitDice, IReadOnlyList<int>? rolls, IDiceRoller roller, WriteContext context)
    {
        ArgumentNullException.ThrowIfNull(roller);
        if (string.IsNullOrWhiteSpace(kind) || !SheetValues.RestKinds.Set.TryMatch(kind, out var restKind))
        {
            throw new DndInputException($"kind is {(string.IsNullOrWhiteSpace(kind) ? "required" : $"\"{WriteBatch.Echo(kind)}\"")}: give \"short\" or \"long\".");
        }

        // The label's name and the roll's secrecy need a read scope, which cannot run inside the write transaction
        // (contract §6.10): computed first, for the character the call names now.
        var (subjectId, label, secret) = RestRollNaming(campaign, character);
        return Run(campaign, character, CharacterActions.Rest, context, route: new CharacterAction { Kind = CharacterActions.Rest }, work: s =>
        {
            if (s.Entity.Id != subjectId)
            {
                (label, secret) = (SessionSafeNames.CharacterFallback + ": " + SheetRest.HitDicePurpose, true);
            }

            var sheet = s.RequireSheet();
            var request = new RestRequest(restKind!, hitDice, rolls);
            var needs = SheetRest.Needs(sheet, request, s.Edition);
            var faces = new List<int>();
            var logged = new List<CharacterRoll>();
            for (var i = 0; i < needs.Count; i++)
            {
                var roll = DiceEvaluator.Roll(DiceExpression.Parse(needs[i].Expression), roller);
                var rolled = roll.Groups.SelectMany(g => g.Dice).Select(d => checked((int)d.Value)).ToList();
                faces.AddRange(rolled);
                DiceRollLog.Append(s.Batch.Connection, s.Batch.Transaction, new DiceRollRow(0, CampaignDatabase.NewId(), s.Batch.Campaign.Id,
                    s.Batch.SessionId, needs[i].Expression, label, roll.Total, null, DiceLogDetail.Json(roll, i, needs.Count, CharacterActions.Tool),
                    secret ? 1 : 0, s.Batch.Recorder.At, EncounterId: null));
                logged.Add(new CharacterRoll(needs[i].Expression, rolled, roll.Total, label, secret));
            }

            // A stored Unconscious on a sheet above 0 HP ends with the rest (F2, review CR06), a sheet convenience: the SRD's
            // knock-out lasts until the creature regains hit points or is given first aid (F3, review F2R08: no rest ends it
            // by rule), but a sheet that rested has plainly woken. Taken off first, so a long rest neither keeps it nor
            // reminds about it, and again after a 2014 short rest whose Hit Dice brought the sheet up from 0.
            var notes = new List<string>();
            var result = SheetRest.Apply(WithoutUnconscious(sheet, notes), request, s.Edition, needs.Count == 0 ? null : faces);
            var after = WithoutUnconscious(result.Sheet, notes);
            return s.Result(SheetDiff.Between(sheet, after), [.. result.Notes, .. notes], result.Reminders, rolls: logged);
        });
    }

    // The safe name and secrecy of a rest's Hit Dice rolls (D11), read before the transaction; a handle that names nothing
    // is refused here exactly as inside it.
    private (string? SubjectId, string Label, bool Secret) RestRollNaming(CampaignRow campaign, string? character)
    {
        using var connection = ReadConnection.Open(_database);
        var entity = CharacterLookup.Resolve(connection, campaign, character);
        var name = SessionSafeNames.ForCharacter(connection, campaign, entity.Id);
        var roster = PartyRoster.Read(connection, campaign, sheets: false);
        var party = ReadScope.Open(connection, campaign, Perspective.Parse(CV.PerspectiveKinds.Party), null);
        var shown = roster.Contains(entity.Id) && party.Entity(entity.Id) is { Shown: true };
        return (entity.Id, name + ": " + SheetRest.HitDicePurpose, !shown);
    }

    private static bool IsConcentration(string? text) => CampaignText.Key(text ?? string.Empty) == "concentration";

    /// <summary>
    /// Runs one action as one batch: resolves the character, asks the router (when the action is one the fight takes),
    /// reads the sheet, runs <paramref name="work"/>, finishes the batch.
    /// </summary>
    private CharacterWriteResult Run(
        CampaignRow campaign,
        string? character,
        string action,
        WriteContext context,
        Func<Step, CharacterWriteResult> work,
        CharacterAction? route = null,
        bool sheetRequired = true,
        string? missingHint = null)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(context);
        return WriteBatch.Run(_database, campaign, context, CharacterActions.ToolOf(action), b =>
        {
            var entity = CharacterLookup.Resolve(b.Connection, b.Campaign, character, b.Transaction, missingHint);
            RoutedResult? presence = null;
            if (route is not null && _router is not null &&
                _router.TryRoute(b.Connection, b.Transaction, b.Campaign, entity.Id, route, out var routed))
            {
                if (route.Kind == CharacterActions.Rest)
                {
                    throw new DndInputException(
                        $"{entity.Name} is in the live fight \"{routed.EncounterName}\" as {routed.CombatantName}{(routed.Left ? " (it left the fight)" : string.Empty)}: " +
                        $"rest once it ends (combat {{\"action\": \"end\", \"campaign\": \"{b.Campaign.Slug}\"}} ends it).");
                }

                if (routed.Applied)
                {
                    b.Finish();
                    var where = routed.Left ? $"{routed.EncounterName}, which {routed.CombatantName} left" : routed.EncounterName;
                    return new CharacterWriteResult(action, entity.Handle, entity.Name, null, b.DryRun, b.SessionNumber, false, [],
                        [$"Applied to the live fight {where}; written to the sheet when it ends.", .. routed.Lines],
                        routed.Reminders.Select(r => new CharacterReminder(r.Kind, r.Text, WithCampaign(r.Call, b.Campaign.Slug))).ToList(), [], routed, []);
                }

                presence = routed;
            }

            var sheet = CharacterSheetStore.Read(b.Connection, entity.Id, b.Transaction);
            if (sheet is null && sheetRequired)
            {
                throw NoSheet(b.Campaign, entity);
            }

            var step = new Step(b, entity, sheet, sheet is null ? SheetReader.CampaignEdition(b.Campaign) : SheetReader.Edition(sheet, b.Campaign), action, presence);
            var result = work(step);
            b.Finish();
            return result;
        });
    }

    /// <summary>
    /// The refusal of an action on a character with no sheet yet, with the call that creates one: written as every printed
    /// sheet call is (the reminders' <see cref="WithCampaign"/>, the host's get and encounter_difficulty texts), the
    /// campaign last, so one call reads the same wherever the model meets it.
    /// </summary>
    private static DndInputException NoSheet(CampaignRow campaign, EntityRow entity) =>
        new($"{entity.Name} ({entity.Handle}) has no sheet yet: create it first with campaign_character {{\"action\": \"update\", " +
            $"\"character\": \"{entity.Handle}\", \"sheet\": {{\"level\": …}}, \"campaign\": \"{campaign.Slug}\"}}.");

    /// <summary>
    /// One action's state inside its batch: the batch, the character, the sheet as read (null only for update's create),
    /// the edition the sheet follows, and the fight's report when the character is in it from a stat block.
    /// </summary>
    private sealed record Step(WriteBatch Batch, EntityRow Entity, CharacterSheet? Sheet, string Edition, string Action, RoutedResult? Presence)
    {
        public CharacterSheet RequireSheet() => Sheet ?? throw NoSheet(Batch.Campaign, Entity);

        /// <summary>Writes an operation's diff and returns the result.</summary>
        public CharacterWriteResult Apply(SheetResult result) => Result(result.Diff, result.Notes, result.Reminders);

        /// <summary>
        /// Writes <paramref name="diff"/> (unless <paramref name="created"/>: the caller inserted the sheet) and builds the
        /// result; the batch id only when rows were logged.
        /// </summary>
        public CharacterWriteResult Result(
            SheetDiff diff,
            IReadOnlyList<string> notes,
            IReadOnlyList<SheetReminder> reminders,
            bool created = false,
            IReadOnlyList<CharacterRoll>? rolls = null,
            IReadOnlyList<CharacterChange>? extra = null,
            IReadOnlyList<WriteWarning>? warnings = null,
            IReadOnlyList<CharacterReminder>? more = null)
        {
            if (!created && !diff.IsEmpty)
            {
                CharacterSheetStore.Apply(Batch.Recorder, Entity.Id, diff);
            }

            var changes = diff.Fields.Select(f => new CharacterChange(f.Column, f.Key, f.Before, f.After)).Concat(extra ?? []).ToList();
            var allNotes = notes.ToList();
            if (Presence is { } presence)
            {
                allNotes.Add(presence.Outcome == RouteOutcomes.StatBlock
                    ? $"{Entity.Name} is in the live fight as {presence.CombatantName} from a stat block: this changed the sheet, not the fight (use combat to change the fight)."
                    : $"{Entity.Name} is in the live fight {presence.EncounterName}: this changed the sheet, not the fight (use combat to change the fight).");
            }

            if (changes.Count == 0)
            {
                allNotes.Add("Nothing changed.");
            }

            var calls = reminders.Select(r => new CharacterReminder(r.Kind, r.Text, WithCampaign(r.CallFor(Entity.Handle), Batch.Campaign.Slug)))
                .Concat(more ?? [])
                .ToList();
            var batchId = !Batch.DryRun && Batch.Recorder.RowsLogged > 0 ? Batch.Context.BatchId : null;
            return new CharacterWriteResult(Action, Entity.Handle, Entity.Name, batchId, Batch.DryRun, Batch.SessionNumber, created, changes, allNotes,
                calls, rolls ?? [], Presence, warnings ?? []);
        }
    }

    /// <summary>
    /// A reminder's <c>campaign_character {…}</c> call with the campaign named (a printed call goes to whatever campaign is
    /// current when it is sent, so it names the one the reminder is about, as the Phase 6 refusals do).
    /// </summary>
    internal static string? WithCampaign(string? call, string campaignSlug) =>
        call is { Length: > 1 } && call.EndsWith('}')
            ? call[..^1] + $", \"campaign\": \"{campaignSlug}\"}}"
            : call;

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
