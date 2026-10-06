using System.Globalization;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using DndMcp.Repository.Campaign.Write;
using CV = DndMcp.Domain.Campaign.CampaignValues;

namespace DndMcp.Repository.Campaign.Characters;

/// <summary>
/// A character's holdings and coins (contract §7.1): <c>inventory</c> adds to and takes from the character's
/// <c>holding</c> rows, <c>currency</c> appends one <c>currency_txn</c> row. Both are author-only in v1 (D20), logged like
/// every other change (one batch; undo gives the potion and the gold back), and never shown to a non-author view.
///
/// <para>
/// <b>Inventory.</b> Each item is matched to a holding of the character by its name's comparison key
/// (<see cref="CampaignText.Key"/>: "potion of healing" is "Potion of Healing"), or, given as an item entity's handle
/// (<c>item:…</c>, <c>e:&lt;n&gt;</c>), by that entity. A positive <c>qty</c> adds (a new holding when there is none,
/// recorded with the batch's session as where it was gained), a negative one takes away, refused beyond what is held; a
/// holding that reaches 0 is DELETED (logged, so undo restores it: hence the tool's destructive hint). <c>srd</c>,
/// <c>equipped</c>, <c>attuned</c> and <c>notes</c> set those fields; for an item already held, an entry with one of them
/// and no <c>qty</c> (or <c>qty: 0</c>) changes only that, so unequipping a ring never gives a second ring, while a new
/// item's <c>qty</c> defaults to 1. A name with no letter or digit is refused: it would match every other such name.
/// Quantities are REAL (a pound of flour, 0.5); a non-finite one is refused.
/// </para>
/// <para>
/// <b>Currency.</b> The ledger is append-only: a balance is the sum of the rows, so each call is one row with the signed
/// coins and a note (the call's reason, else "campaign_character currency"). A balance that goes below 0 in a
/// denomination is applied and warned about (change was not recorded, or the table is mid-trade).
/// </para>
/// </summary>
public sealed partial class CharacterWriter
{
    /// <summary>The ledger note when the call gives no reason.</summary>
    public const string CurrencyNote = "campaign_character currency";

    /// <summary>The most items one inventory call takes.</summary>
    public const int MaxInventoryItems = 50;

    /// <summary>The most one holding may hold (and one call add or take).</summary>
    public const double MaxQuantity = 1_000_000;

    /// <summary>The most of one coin one currency call moves.</summary>
    public const long MaxCoins = 100_000_000;

    /// <summary>The warning kind of a coin balance that went below 0.</summary>
    public const string NegativeBalanceWarning = "negative_balance";

    /// <summary><c>inventory {items}</c>: adds to and takes from the character's holdings (class summary).</summary>
    /// <exception cref="DndInputException">No items, a bad item (up to five problems listed), taking more than is held.</exception>
    public CharacterWriteResult Inventory(CampaignRow campaign, string? character, IReadOnlyList<InventoryItem>? items, WriteContext context)
    {
        CheckItems(items);
        return Run(campaign, character, CharacterActions.Inventory, context, sheetRequired: false, work: s =>
        {
            var b = s.Batch;
            var changes = new List<CharacterChange>();
            var notes = new List<string>();
            var problems = new List<string>();
            for (var i = 0; i < items!.Count; i++)
            {
                var item = items[i];
                var where = $"items item {N(i + 1)}";
                var (name, itemId) = ItemOf(b, item.Item!.Trim(), where, problems);
                if (name is null)
                {
                    continue;
                }

                var holding = Holdings(b, s.Entity.Id).FirstOrDefault(h => itemId is not null ? h.ItemId == itemId : CampaignText.Key(h.Name) == CampaignText.Key(name));

                // No qty: one more of a new item, or of a held one named alone; a held item given only fields changes only
                // those (unequipping a ring must not give a second ring).
                var qty = item.Qty ?? (holding is not null && HasFields(item) ? 0 : 1);
                if (holding is null)
                {
                    if (qty <= 0)
                    {
                        problems.Add($"{where}: {s.Entity.Name} holds no \"{WriteBatch.Echo(name)}\"" + (qty < 0 ? " to take." : "; give a positive qty to add it."));
                        continue;
                    }

                    b.Recorder.Insert(CampaignTables.Holding.Name, new Dictionary<string, object?>
                    {
                        ["campaign_id"] = b.Campaign.Id,
                        ["holder_id"] = s.Entity.Id,
                        ["item_id"] = itemId,
                        ["name"] = name,
                        ["srd_ref"] = Clean(item.Srd),
                        ["quantity"] = qty,
                        ["equipped"] = item.Equipped == true ? 1L : 0L,
                        ["attuned"] = item.Attuned == true ? 1L : 0L,
                        ["acquired_session_id"] = b.SessionId,
                        ["notes"] = Clean(item.Notes),
                    }, CharacterActions.Inventory);
                    changes.Add(new CharacterChange(CharacterChangeFields.Inventory, name, null, Quantity(qty)));
                    continue;
                }

                var now = holding.Quantity + qty;
                if (now < 0)
                {
                    problems.Add($"{where}: {s.Entity.Name} holds {Quantity(holding.Quantity)} {holding.Name}; cannot take {Quantity(-qty)}.");
                    continue;
                }

                if (now > MaxQuantity)
                {
                    problems.Add($"{where}: {holding.Name} would be {Quantity(now)}; at most {Quantity(MaxQuantity)}.");
                    continue;
                }

                if (now == 0 && qty != 0)
                {
                    b.Recorder.Delete(CampaignTables.Holding.Name, holding.Id, CharacterActions.Inventory);
                    changes.Add(new CharacterChange(CharacterChangeFields.Inventory, holding.Name, Quantity(holding.Quantity), null));
                    notes.Add($"{holding.Name}: none left (removed).");
                    continue;
                }

                var update = new Dictionary<string, object?>(StringComparer.Ordinal) { ["quantity"] = now };
                if (item.Srd is not null)
                {
                    update["srd_ref"] = Clean(item.Srd);
                }

                if (item.Equipped is { } equipped)
                {
                    update["equipped"] = equipped ? 1L : 0L;
                }

                if (item.Attuned is { } attuned)
                {
                    update["attuned"] = attuned ? 1L : 0L;
                }

                if (item.Notes is not null)
                {
                    update["notes"] = Clean(item.Notes);
                }

                var fields = b.Update(CampaignTables.Holding.Name, holding.Id, update, CharacterActions.Inventory);
                if (fields.Contains("quantity"))
                {
                    changes.Add(new CharacterChange(CharacterChangeFields.Inventory, holding.Name, Quantity(holding.Quantity), Quantity(now)));
                }

                foreach (var field in fields.Where(f => f != "quantity"))
                {
                    var (was, @is) = field switch
                    {
                        "equipped" => (Flag(holding.Equipped), Flag((long)update["equipped"]!)),
                        "attuned" => (Flag(holding.Attuned), Flag((long)update["attuned"]!)),
                        "srd_ref" => (holding.SrdRef, (string?)update["srd_ref"]),
                        _ => (holding.Notes, (string?)update["notes"]),
                    };
                    changes.Add(new CharacterChange(CharacterChangeFields.Inventory, holding.Name + " " + field, was, @is));
                }
            }

            DslProblems.ThrowIfAny(problems, CharacterActions.Inventory);
            return s.Result(Domain.Characters.SheetDiff.Between(EmptySheet(s), EmptySheet(s)), notes, [], extra: changes);
        });
    }

    /// <summary><c>currency {coins}</c>: one ledger row of signed coins (class summary).</summary>
    /// <exception cref="DndInputException">No coins, or a coin out of range.</exception>
    public CharacterWriteResult Currency(CampaignRow campaign, string? character, Coins? coins, WriteContext context)
    {
        if (coins is null || coins.IsZero)
        {
            throw new DndInputException("coins is required: the coins gained (positive) or spent (negative), e.g. {\"action\": \"currency\", \"coins\": {\"gp\": 120}}.");
        }

        var problems = new[] { ("cp", coins.Cp), ("sp", coins.Sp), ("ep", coins.Ep), ("gp", coins.Gp), ("pp", coins.Pp) }
            .Where(c => c.Item2 is > MaxCoins or < -MaxCoins)
            .Select(c => $"coins.{c.Item1} is {c.Item2.ToString(CultureInfo.InvariantCulture)}; at most {MaxCoins.ToString("N0", CultureInfo.InvariantCulture)} either way.")
            .ToList();
        DslProblems.ThrowIfAny(problems, "coins");
        return Run(campaign, character, CharacterActions.Currency, context, sheetRequired: false, work: s =>
        {
            var b = s.Batch;
            var before = Balance(b, s.Entity.Id);
            var after = After(before, coins, s.Entity.Name);
            b.Recorder.Insert(CampaignTables.CurrencyTxn.Name, new Dictionary<string, object?>
            {
                ["campaign_id"] = b.Campaign.Id,
                ["holder_id"] = s.Entity.Id,
                ["session_id"] = b.SessionId,
                ["cp"] = coins.Cp,
                ["sp"] = coins.Sp,
                ["ep"] = coins.Ep,
                ["gp"] = coins.Gp,
                ["pp"] = coins.Pp,
                ["note"] = b.Context.Reason ?? CurrencyNote,
            }, CharacterActions.Currency);
            var changes = new[] { ("cp", before.Cp, after.Cp), ("sp", before.Sp, after.Sp), ("ep", before.Ep, after.Ep), ("gp", before.Gp, after.Gp), ("pp", before.Pp, after.Pp) }
                .Where(c => c.Item2 != c.Item3)
                .Select(c => new CharacterChange(CharacterChangeFields.Coins, c.Item1, c.Item2.ToString(CultureInfo.InvariantCulture), c.Item3.ToString(CultureInfo.InvariantCulture)))
                .ToList();
            var warnings = new List<WriteWarning>();
            if (after.HasNegative)
            {
                warnings.Add(new WriteWarning(NegativeBalanceWarning, WarningSeverities.Warning,
                    $"{s.Entity.Name}'s coins are now below 0 in a denomination ({CoinText(after)}): record the change they got, or the coins they had."));
            }

            return s.Result(Domain.Characters.SheetDiff.Between(EmptySheet(s), EmptySheet(s)), [$"Coins: {CoinText(after)}."], [], extra: changes, warnings: warnings);
        });
    }

    private static void CheckItems(IReadOnlyList<InventoryItem>? items)
    {
        if (items is null || items.Count == 0)
        {
            throw new DndInputException("items is required: what to add (qty positive, default 1) or take (qty negative), e.g. {\"action\": \"inventory\", \"items\": [{\"item\": \"Potion of Healing\", \"qty\": 2}]}.");
        }

        if (items.Count > MaxInventoryItems)
        {
            throw new DndInputException($"items has {N(items.Count)} entries; give at most {N(MaxInventoryItems)} per call.");
        }

        var problems = new List<string>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var where = $"items item {N(i + 1)}";
            if (item is null || string.IsNullOrWhiteSpace(item.Item) || !OneLine(item.Item.Trim(), CampaignLimits.MaxNameLength))
            {
                problems.Add($"{where}: item must be the item's name (one line of at most {N(CampaignLimits.MaxNameLength)} characters) or an item handle.");
                continue;
            }

            if (CampaignText.Key(item.Item).Length == 0)
            {
                // Holdings are matched by this key: names with none ("!!!", "—") would all be one holding.
                problems.Add($"{where}: {NoKeyProblem("item")}");
                continue;
            }

            if (item.Qty is { } qty && (!double.IsFinite(qty) || Math.Abs(qty) > MaxQuantity))
            {
                problems.Add($"{where}: qty must be a number from -{Quantity(MaxQuantity)} to {Quantity(MaxQuantity)}.");
            }
            else if (item.Qty == 0 && !HasFields(item))
            {
                problems.Add($"{where}: qty 0 changes nothing; give a positive qty to add, a negative one to take, or equipped/attuned/notes/srd to set.");
            }

            if (item.Srd is not null && !OneLine(item.Srd.Trim(), CampaignLimits.MaxLabelLength))
            {
                problems.Add($"{where}: srd must be one line of at most {N(CampaignLimits.MaxLabelLength)} characters, e.g. \"2024/equipment/potion-of-healing\".");
            }

            if (item.Notes is not null && item.Notes.Length > CampaignLimits.MaxNoteLength)
            {
                problems.Add($"{where}: notes is {N(item.Notes.Length)} characters; at most {N(CampaignLimits.MaxNoteLength)}.");
            }
        }

        DslProblems.ThrowIfAny(problems, CharacterActions.Inventory);
    }

    /// <summary>
    /// The refusal of a name whose comparison key (<see cref="CampaignText.Key"/>) is empty, a name of punctuation or
    /// symbols alone: names are matched by that key, so every such name would be the same holding. One wording for every
    /// name the Repository matches this way (inventory items, combat loot), and the tracker's for combatants and effects.
    /// </summary>
    internal static string NoKeyProblem(string field) => $"give {field} a name with a letter or digit.";

    // The item sets a field besides its quantity (equipped, attuned, notes or srd): with qty 0, or with no qty on an item
    // already held, that is all it changes.
    private static bool HasFields(InventoryItem item) =>
        item.Equipped is not null || item.Attuned is not null || item.Notes is not null || item.Srd is not null;

    // The name a holding takes, and the item entity it links: an item handle names an entity (its name), any other text is the name.
    private static (string? Name, string? ItemId) ItemOf(WriteBatch b, string text, string where, List<string> problems)
    {
        if (text.StartsWith(CV.Kinds.Item + ":", StringComparison.OrdinalIgnoreCase) || text.StartsWith("e:", StringComparison.OrdinalIgnoreCase))
        {
            if (!CampaignHandle.TryParse(text, out var handle, out var problem))
            {
                problems.Add($"{where}: {problem}");
                return (null, null);
            }

            if (b.Resolver.TryEntity(handle) is { Kind: CV.Kinds.Item } entity)
            {
                return (entity.Name, entity.Id);
            }

            problems.Add($"{where}: no item {WriteBatch.Echo(text)} in this campaign; give the item's name instead.");
            return (null, null);
        }

        return (text, null);
    }

    private static IReadOnlyList<HoldingRow> Holdings(WriteBatch b, string entityId) =>
        StoredRows.Read(b.Connection, "holding", () => b.Connection.Query<HoldingRow>(
            $"SELECT {HoldingRow.Columns} FROM holding WHERE holder_id = @entityId ORDER BY created_at, id", new { entityId }, b.Transaction).ToList());

    // The balance before the call; a ledger whose sum no longer fits a whole number (rows written by something else) is
    // refused as the balance an addition would overflow, never thrown as an unexpected error.
    private static CoinsView Balance(WriteBatch b, string entityId)
    {
        var rows = StoredRows.Read(b.Connection, "currency_txn", () => b.Connection.Query<CurrencyTxnRow>(
            $"SELECT {CurrencyTxnRow.Columns} FROM currency_txn WHERE holder_id = @entityId", new { entityId }, b.Transaction).ToList());
        try
        {
            return new CoinsView(rows.Sum(r => r.Cp), rows.Sum(r => r.Sp), rows.Sum(r => r.Ep), rows.Sum(r => r.Gp), rows.Sum(r => r.Pp));
        }
        catch (OverflowException)
        {
            throw Overflow();
        }
    }

    // The balance after the call (checked: a sum past a whole number's range is refused, not wrapped to a negative balance).
    private static CoinsView After(CoinsView before, Coins coins, string name)
    {
        try
        {
            return checked(new CoinsView(before.Cp + coins.Cp, before.Sp + coins.Sp, before.Ep + coins.Ep, before.Gp + coins.Gp, before.Pp + coins.Pp));
        }
        catch (OverflowException)
        {
            throw Overflow(name);
        }
    }

    private static DndInputException Overflow(string? name = null) =>
        new($"coins: {(name is null ? "the" : name + "'s")} coin balance would be larger than a whole number holds; record the coins in another denomination (1 pp = 10 gp).");

    // Holdings and coins change no sheet column: the step's diff is empty (a character with no sheet holds things too).
    private static Domain.Characters.CharacterSheet EmptySheet(Step s) => s.Sheet ?? Domain.Characters.CharacterSheet.New(s.Entity.Id);

    private static string CoinText(CoinsView coins)
    {
        var parts = new[] { (coins.Pp, "pp"), (coins.Gp, "gp"), (coins.Ep, "ep"), (coins.Sp, "sp"), (coins.Cp, "cp") }
            .Where(c => c.Item1 != 0)
            .Select(c => c.Item1.ToString(CultureInfo.InvariantCulture) + " " + c.Item2)
            .ToList();
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    private static bool OneLine(string text, int max) => text.Length <= max && !text.Any(char.IsControl);

    private static string Flag(long value) => value != 0 ? "yes" : "no";

    private static string Quantity(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
