using System.Globalization;
using System.Text.Json.Nodes;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Characters;
using DndMcp.Domain.Combat;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using DndMcp.Repository.Campaign.Characters;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Repository.Campaign.Write;
using Microsoft.Data.Sqlite;
using CV = DndMcp.Domain.Campaign.CampaignValues;
using ES = DndMcp.Domain.Campaign.CampaignValues.EncounterStatuses;
using L = DndMcp.Domain.Campaign.CampaignValues.CombatLogKinds;
using R = DndMcp.Domain.Combat.CombatValues.ResourceKeys;

namespace DndMcp.Repository.Campaign.Combat;

public sealed partial class CombatService
{
    /// <summary>The change_log tool of the write-back batch (D6).</summary>
    public const string EndTool = "combat/end";

    /// <summary>The change_log action of every row the write-back logs.</summary>
    public const string EndAction = "combat_end";

    /// <summary>The most loot entries one <c>end</c> takes.</summary>
    public const int MaxLoot = 50;

    /// <summary>The most currency entries one <c>end</c> takes.</summary>
    public const int MaxCurrency = 20;

    /// <summary>The longest <c>outcome</c> (author text, stored as <c>outcome_md</c>).</summary>
    public const int MaxOutcomeLength = CampaignLimits.MaxNoteLength;

    /// <summary>
    /// Test seam: runs after <c>end</c>'s pre-read (the fight, its session, its names) and before its transaction, so a test
    /// can make another call reach the fight in between (end it and start another, start a planned one, change its
    /// session) and prove the end refuses rather than act on what it did not plan for.
    /// </summary>
    internal Action? AfterEndPreRead { get; set; }

    /// <summary>
    /// <c>end</c> (contract §6.7, D6): the fight ends and its sheet-seeded combatants' combat-owned fields are written back
    /// to their sheets as ONE logged batch (tool <see cref="EndTool"/>), with the consumed items (the holding's CURRENT
    /// quantity − used), the XP awards and the loot and coins; in the same transaction, unlogged, the encounter is marked
    /// ended (<c>ended_at</c>, <c>outcome_md</c>, <c>writeback_batch_id</c>) and a combat_log <c>end</c> row is written.
    /// The batch is filed under the encounter's session, else the live one, else none.
    ///
    /// <para>
    /// <b>What never happens here.</b> A combatant row or combat_log row is never deleted (they are the fight's record), a
    /// holding is never deleted (a quantity of 0 is written: views hide it), and an entity's status is never changed: a
    /// death is a PROPOSAL, a ready <c>campaign_write</c> dry run the author decides on (D19).
    /// </para>
    /// <para>
    /// <b>Refused, writing nothing:</b> drift (a written field changed on the sheet since the combatant joined, a sheet or a
    /// consumed holding that no longer exists) unless <see cref="EndRequest.Force"/>; a second <c>end</c> (it says how to take
    /// the write-back back, or, once that was undone, that the fight stays ended and how to redo it); loot, coins or XP with
    /// a discard. <b>A dry run</b> plans and writes everything, then rolls the whole transaction back: the fight stays as it
    /// was. <b>A discard</b> (or a planned or paused fight, which never ran here: the call Z's undo guard prints) ends it
    /// with no write-back, and reads none of its combatants (before the transaction or in it): it is the way out the
    /// unreadable-row refusal offers (<see cref="CombatStore.Load"/>), so a combatant row nobody can read must not block it.
    /// </para>
    /// <para>
    /// <b>A race.</b> The fight, its session (the batch is filed under it) and its party-safe names are read before the
    /// transaction (a <see cref="ReadScope"/> cannot run inside one); inside it the fight is re-read BY ID, never by its
    /// address again: "current" may name another fight by then (this one ended by another call, the next one started),
    /// and that fight must never be ended with this one's names and session. The loser of two ends is told the fight
    /// already ended; a fight whose status or session changed in between (a planned fight started meanwhile) is refused
    /// with "send the end again", so the next try plans from what is there.
    /// </para>
    /// </summary>
    /// <param name="encounter">"current" (null), "last" or a name (D13).</param>
    /// <exception cref="DndInputException">No such fight, it has ended, drift without force, or a bad entry.</exception>
    public CombatEndOutcome End(CampaignRow campaign, string? encounter, EndRequest request)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(request);
        CheckEnd(request);

        // Before the transaction (a ReadScope cannot run inside one): the encounter as it stands, its session (the batch is
        // filed under it), and every combatant's party-safe name (a persisted condition's source, §6.10). A discard (or a
        // fight that never ran) writes nothing back, so it needs no names and reads no combatant: an unreadable combatant
        // row must never block the one way out its refusal offers.
        var pre = Mapped(() =>
        {
            using var connection = ReadConnection.Open(_database);
            var row = EncounterResolver.Resolve(connection, campaign, encounter);
            var session = row.SessionId is null
                ? null
                : connection.QueryFirstOrDefault<long?>("SELECT number FROM session WHERE entity_id = @id", new { id = row.SessionId });
            if (WritesNothingBack(request, row))
            {
                return (Row: row, Names: (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal), Session: session);
            }

            var subjects = CombatSubjects.Compute(connection, campaign, CombatStore.Load(connection, campaign, row), []);
            return (Row: row, Names: subjects.Names, Session: session);
        });

        AfterEndPreRead?.Invoke();
        var context = new WriteContext
        {
            Session = pre.Session?.ToString(CultureInfo.InvariantCulture),
            Reason = string.IsNullOrWhiteSpace(request.Reason) ? $"combat end: {pre.Row.Name}" : request.Reason,
            DryRun = request.DryRun,
            Tool = EndTool,
        };
        return WriteBatch.Run(_database, campaign, context, EndTool, b => EndInBatch(b, request, pre.Row, pre.Names));
    }

    private CombatEndOutcome EndInBatch(WriteBatch b, EndRequest request, EncounterRow expected, IReadOnlyDictionary<string, string> names)
    {
        var connection = b.Connection;
        var transaction = b.Transaction;
        var campaign = b.Campaign;

        // By id (class summary, "A race"): the fight the pre-read planned for, whatever "current" names now.
        var row = CombatStore.Encounter(connection, expected.Id, transaction) ?? throw new DndInputException(
            $"\"{WriteBatch.Echo(expected.Name)}\" no longer exists in {campaign.Slug}; nothing was changed.");
        if (row.Status == ES.Ended)
        {
            throw AlreadyEnded(connection, transaction, campaign, row);
        }

        if (row.Status != expected.Status || row.SessionId != expected.SessionId)
        {
            throw new DndInputException(
                $"\"{WriteBatch.Echo(row.Name)}\" changed while this end was being prepared (another call reached it first); nothing was changed: send the end again.");
        }

        var at = b.Recorder.At;
        var neverRan = row.Status is ES.Planned or ES.Paused;
        var discard = WritesNothingBack(request, row);
        if (discard && (request.Loot.Count > 0 || request.Currency.Count > 0 || request.Xp is > 0))
        {
            throw new DndInputException(
                $"end writes nothing back {(neverRan ? $"for a {row.Status} fight (it never ran here)" : "with discard")}: leave out loot, currency and xp, " +
                "or give them with campaign_character inventory, currency and xp.");
        }

        // A discard reads no combatant (the pre-read's reason): the fight is marked ended from its own row alone.
        var state = discard ? CombatStore.Unloaded(campaign, row) : CombatStore.Load(connection, campaign, row, transaction);

        EndPlan plan;
        if (discard)
        {
            plan = CombatEnd.Plan(state, new EndOptions(request.Xp, Discard: true), new EndInputs());
        }
        else
        {
            var seeded = state.Combatants.Where(c => c.IsSheetSeeded && c.EntityId is not null).Select(c => c.EntityId!).Distinct(StringComparer.Ordinal).ToList();
            var holdingIds = state.Combatants.Where(c => c.IsSheetSeeded)
                .SelectMany(c => c.Resources.Keys)
                .Where(k => k.StartsWith(R.ItemPrefix, StringComparison.Ordinal))
                .Select(k => k[R.ItemPrefix.Length..])
                .ToList();
            var inputs = new EndInputs
            {
                Sheets = CharacterSheetStore.ReadMany(connection, seeded, transaction),
                Holdings = CombatStore.HoldingsById(connection, holdingIds, transaction),
                SafeNames = names,
                CampaignSlug = campaign.Slug,
            };
            plan = CombatEnd.Plan(state, new EndOptions(request.Xp, Discard: false, request.Force, request.DryRun), inputs);
        }

        var handles = state.Combatants.ToDictionary(c => c.Id, c => c.EntityHandle ?? c.Name, StringComparer.Ordinal);
        var changes = new List<CombatSheetChange>();
        foreach (var write in plan.WriteBacks)
        {
            CharacterSheetStore.Apply(b.Recorder, write.EntityId, write.Diff, EndAction);
            changes.AddRange(write.Diff.Fields.Select(f => new CombatSheetChange(write.Name, handles.GetValueOrDefault(write.CombatantId) ?? write.Name, f.Column, f.Key, f.Before, f.After)));
        }

        foreach (var item in plan.Items)
        {
            b.Recorder.Update(CampaignTables.Holding.Name, item.HoldingId, new Dictionary<string, object?> { ["quantity"] = item.After }, EndAction);
        }

        foreach (var award in plan.Awards)
        {
            b.Recorder.Insert(CampaignTables.Award.Name, new Dictionary<string, object?>
            {
                ["campaign_id"] = campaign.Id,
                ["recipient_id"] = award.EntityId,
                ["session_id"] = b.SessionId,
                ["kind"] = CV.AwardKinds.Xp,
                ["amount"] = (long)award.Amount,
                ["note"] = null,
                ["source"] = award.Source,
            }, EndAction);
        }

        var summary = plan.Summary.ToList();
        var loot = Loot(b, request.Loot, row.Name, summary);
        var coins = Coins(b, request.Currency, row.Name, summary);
        if (neverRan)
        {
            summary.Insert(0, $"\"{row.Name}\" was {row.Status} and never ran here: it ends with nothing written back.");
        }

        // The batch's own end-of-batch checks first (they may log), so the encounter names the batch exactly when it exists.
        b.Finish();
        var logged = b.Recorder.RowsLogged > 0;
        var batchId = logged ? b.Context.BatchId : null;
        var before = row;
        connection.Execute(
            "UPDATE encounter SET status = @ended, ended_at = @at, outcome_md = @outcome, writeback_batch_id = @batchId, updated_at = @at WHERE id = @id",
            new { ended = ES.Ended, at, outcome = string.IsNullOrWhiteSpace(request.Outcome) ? null : request.Outcome.Trim(), batchId, id = row.Id },
            transaction);
        var detail = new JsonObject { ["from"] = row.Status, ["discarded"] = discard };
        if (batchId is not null)
        {
            detail["writeback_batch_id"] = batchId;
        }

        if (plan.Xp.Awarded > 0)
        {
            detail["xp"] = plan.Xp.Awarded;
        }

        CombatStore.AppendLog(connection, transaction, row.Id,
            new CombatChange(L.End, state.Round, state.TurnCombatantId, null, null, null, CombatJson.Serialize(detail)), null, at);

        var endedRow = CombatStore.Encounter(connection, row.Id, transaction) ?? row;
        var view = b.DryRun
            ? CombatViews.Encounter(connection, transaction, before, state)
            : CombatViews.Encounter(connection, transaction, endedRow, state with { Status = ES.Ended });
        return new CombatEndOutcome(
            campaign.Slug,
            view,
            b.DryRun ? null : batchId,
            b.DryRun,
            b.SessionNumber,
            discard,
            !logged,
            changes,
            plan.Xp,
            plan.Awards,
            plan.Items,
            loot,
            coins,
            plan.Proposals,
            plan.Reminders,
            summary,
            plan.Overwritten);
    }

    // Whether an end writes nothing back, and so reads no combatant: a discard, or a fight that never ran here (planned or
    // paused, the call Z's undo guard prints).
    private static bool WritesNothingBack(EndRequest request, EncounterRow row) => request.Discard || row.Status is ES.Planned or ES.Paused;

    // A second end (D6: "No second end"): what the write-back's batch chain says now. Applied: the batch to undo to take it
    // back (the latest of the chain after a redo, since an undone batch cannot be undone again). Undone: the fight stays
    // ended, and the redo is the undo of the undo; pointing at the write-back batch there would send a call that fails.
    private static DndInputException AlreadyEnded(SqliteConnection connection, SqliteTransaction transaction, CampaignRow campaign, EncounterRow row)
    {
        var said = $"\"{WriteBatch.Echo(row.Name)}\" already ended (round {CombatStore.N(row.Round)}); a fight ends once. ";
        if (row.WritebackBatchId is null)
        {
            return new DndInputException(said + "It wrote nothing back.");
        }

        var (status, last) = CombatViews.WritebackChain(connection, row.WritebackBatchId, transaction);
        var call = $"campaign_history {{\"action\": \"undo\", \"batch_id\": \"{last}\", \"campaign\": \"{campaign.Slug}\"}}";
        return new DndInputException(status == WritebackStatuses.Undone
            ? said + $"Its write-back was undone, and the fight stays ended; to apply the write-back again, redo it with {call} (undoing the undo redoes it)."
            : said + $"To take its write-back back, undo batch {last}: {call}.");
    }

    // The loot as holdings (contract §6.7): each item to its holder (a character, or the party faction by default).
    private static IReadOnlyList<CombatLootView> Loot(WriteBatch b, IReadOnlyList<LootRequest> loot, string encounterName, List<string> summary)
    {
        var views = new List<CombatLootView>();
        for (var i = 0; i < loot.Count; i++)
        {
            var entry = loot[i];
            var holder = Holder(b, entry.To, $"loot item {CombatStore.N(i + 1)}");
            var (name, itemId) = LootItem(b, entry.Item.Trim(), $"loot item {CombatStore.N(i + 1)}");
            var row = b.Recorder.Insert(CampaignTables.Holding.Name, new Dictionary<string, object?>
            {
                ["campaign_id"] = b.Campaign.Id,
                ["holder_id"] = holder.Id,
                ["item_id"] = itemId,
                ["name"] = name,
                ["srd_ref"] = string.IsNullOrWhiteSpace(entry.Srd) ? null : entry.Srd.Trim(),
                ["quantity"] = entry.Qty,
                ["equipped"] = 0L,
                ["attuned"] = 0L,
                ["acquired_session_id"] = b.SessionId,
                ["notes"] = $"loot: {encounterName}",
            }, EndAction);
            views.Add(new CombatLootView((string)row["id"]!, holder.Handle, name, string.IsNullOrWhiteSpace(entry.Srd) ? null : entry.Srd.Trim(), entry.Qty));
            summary.Add($"Loot: {name} ×{entry.Qty.ToString("0.###", CultureInfo.InvariantCulture)} to {holder.Handle}.");
        }

        return views;
    }

    // The coins as ledger rows (contract §6.7): one currency_txn row per entry, its note the encounter's name.
    private static IReadOnlyList<CombatCurrencyView> Coins(WriteBatch b, IReadOnlyList<CurrencyRequest> currency, string encounterName, List<string> summary)
    {
        var views = new List<CombatCurrencyView>();
        for (var i = 0; i < currency.Count; i++)
        {
            var entry = currency[i];
            var holder = Holder(b, entry.To, $"currency item {CombatStore.N(i + 1)}");
            var row = b.Recorder.Insert(CampaignTables.CurrencyTxn.Name, new Dictionary<string, object?>
            {
                ["campaign_id"] = b.Campaign.Id,
                ["holder_id"] = holder.Id,
                ["session_id"] = b.SessionId,
                ["cp"] = entry.Cp,
                ["sp"] = entry.Sp,
                ["ep"] = entry.Ep,
                ["gp"] = entry.Gp,
                ["pp"] = entry.Pp,
                ["note"] = encounterName,
            }, EndAction);
            views.Add(new CombatCurrencyView((string)row["id"]!, holder.Handle, entry.Cp, entry.Sp, entry.Ep, entry.Gp, entry.Pp));
            summary.Add($"Coins to {holder.Handle}: {CoinText(entry)}.");
        }

        return views;
    }

    // A loot or currency holder: a character of this campaign, or the party faction (the default).
    private static EntityRow Holder(WriteBatch b, string? to, string where)
    {
        if (string.IsNullOrWhiteSpace(to))
        {
            return b.Campaign.PartyId is { } partyId && b.EntityById(partyId, includeDeleted: false) is { } party
                ? party
                : throw new DndInputException($"{where}: {b.Campaign.Slug} has no party to hold it; give to (a character's handle).");
        }

        var text = to.Trim();
        if (!CampaignHandle.TryParse(text, out var handle, out var problem))
        {
            throw new DndInputException($"{where}: to: {problem}");
        }

        if (handle is CampaignHandle.CrossCampaign cross)
        {
            throw new DndInputException(CharacterLookup.OwnSlugProblem($"{where}: to", text, cross, b.Campaign) ??
                $"{where}: to \"{WriteBatch.Echo(text)}\" names another campaign's entity; give a character of {b.Campaign.Slug} or the party.");
        }

        var entity = b.Resolver.TryEntity(handle) ?? throw new DndInputException(
            $"{where}: to \"{WriteBatch.Echo(text)}\" names nothing in {b.Campaign.Slug}; give a character's handle (character:slug) or leave it out for the party.");
        if (entity.Kind != CV.Kinds.Character && entity.Id != b.Campaign.PartyId)
        {
            throw new DndInputException($"{where}: to {entity.Handle} is a {entity.Kind}; loot and coins go to a character or the party.");
        }

        return entity;
    }

    // A loot item's holding name, and the item entity it links: an item handle names an entity, any other text is the name.
    private static (string Name, string? ItemId) LootItem(WriteBatch b, string text, string where)
    {
        if (!text.StartsWith(CV.Kinds.Item + ":", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("e:", StringComparison.OrdinalIgnoreCase))
        {
            return (text, null);
        }

        if (CampaignHandle.TryParse(text, out var handle, out _) && b.Resolver.TryEntity(handle) is { Kind: CV.Kinds.Item } entity)
        {
            return (entity.Name, entity.Id);
        }

        throw new DndInputException($"{where}: no item {WriteBatch.Echo(text)} in {b.Campaign.Slug}; give the item's name instead.");
    }

    // The request's own problems (before anything is read): up to five, numbered by item.
    private static void CheckEnd(EndRequest request)
    {
        var problems = new List<string>();
        if (request.Outcome is { } outcome && outcome.Length > MaxOutcomeLength)
        {
            problems.Add($"outcome is {CombatStore.N(outcome.Length)} characters; at most {CombatStore.N(MaxOutcomeLength)}.");
        }

        if (request.Xp is { } xp && xp is < 0 or > 10_000_000)
        {
            problems.Add($"xp is {CombatStore.N(xp)}; give 0 (none) up to 10,000,000, or leave it out for what the fight was worth.");
        }

        if (request.Loot.Count > MaxLoot)
        {
            problems.Add($"loot has {CombatStore.N(request.Loot.Count)} entries; at most {CombatStore.N(MaxLoot)}.");
        }

        if (request.Currency.Count > MaxCurrency)
        {
            problems.Add($"currency has {CombatStore.N(request.Currency.Count)} entries; at most {CombatStore.N(MaxCurrency)}.");
        }

        for (var i = 0; i < request.Loot.Count && i < MaxLoot; i++)
        {
            var entry = request.Loot[i];
            var where = $"loot item {CombatStore.N(i + 1)}";
            if (entry is null || string.IsNullOrWhiteSpace(entry.Item) || entry.Item.Trim().Length > CampaignLimits.MaxNameLength || entry.Item.Any(char.IsControl))
            {
                problems.Add($"{where}: item must be the item's name (one line of at most {CombatStore.N(CampaignLimits.MaxNameLength)} characters) or an item handle.");
                continue;
            }

            if (CampaignText.Key(entry.Item).Length == 0)
            {
                // A holding is matched by this key later (inventory, heal {item}): every such name would be one holding.
                problems.Add($"{where}: {CharacterWriter.NoKeyProblem("item")}");
            }

            if (!double.IsFinite(entry.Qty) || entry.Qty <= 0 || entry.Qty > CharacterWriter.MaxQuantity)
            {
                problems.Add($"{where}: qty must be a number above 0 and at most {CharacterWriter.MaxQuantity.ToString("N0", CultureInfo.InvariantCulture)}.");
            }

            if (entry.Srd is { } srd && (srd.Trim().Length > CampaignLimits.MaxLabelLength || srd.Any(char.IsControl)))
            {
                problems.Add($"{where}: srd must be one line of at most {CombatStore.N(CampaignLimits.MaxLabelLength)} characters, e.g. \"2024/magic-item/potion-of-water-breathing\".");
            }
        }

        for (var i = 0; i < request.Currency.Count && i < MaxCurrency; i++)
        {
            var entry = request.Currency[i];
            var where = $"currency item {CombatStore.N(i + 1)}";
            if (entry is null)
            {
                problems.Add($"{where} is empty; give coins, e.g. {{\"gp\": 120}}.");
                continue;
            }

            var coins = new[] { ("cp", entry.Cp), ("sp", entry.Sp), ("ep", entry.Ep), ("gp", entry.Gp), ("pp", entry.Pp) };
            if (coins.All(c => c.Item2 == 0))
            {
                problems.Add($"{where} gives no coins; give at least one of cp, sp, ep, gp, pp, e.g. {{\"gp\": 120}}.");
            }

            foreach (var (coin, amount) in coins.Where(c => c.Item2 is > CharacterWriter.MaxCoins or < -CharacterWriter.MaxCoins))
            {
                problems.Add($"{where}: {coin} is {amount.ToString(CultureInfo.InvariantCulture)}; at most {CharacterWriter.MaxCoins.ToString("N0", CultureInfo.InvariantCulture)} either way.");
            }
        }

        DslProblems.ThrowIfAny(problems, "end");
    }

    private static string CoinText(CurrencyRequest coins)
    {
        var parts = new[] { (coins.Pp, "pp"), (coins.Gp, "gp"), (coins.Ep, "ep"), (coins.Sp, "sp"), (coins.Cp, "cp") }
            .Where(c => c.Item1 != 0)
            .Select(c => c.Item1.ToString(CultureInfo.InvariantCulture) + " " + c.Item2);
        return string.Join(", ", parts);
    }
}
