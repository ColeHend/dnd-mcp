using System.Globalization;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Combat;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign.Read;
using Microsoft.Data.Sqlite;
using ES = DndMcp.Domain.Campaign.CampaignValues.EncounterStatuses;
using S = DndMcp.Domain.Campaign.CampaignValues.CombatSides;

namespace DndMcp.Repository.Campaign.Combat;

/// <summary>
/// <c>combat state</c> and <c>/combat/current</c> (contract §6.1, §6.12, §14.4): the AUTHOR's state of a fight (the
/// encounter, its order, the reminders its state calls for now, the last change), the listing when no fight runs (the
/// planned and paused fights and the last ended one), and the party BOARD a non-author view reads. Every read is on a read
/// connection, never inside a write transaction; SQLite's user-fixable failures become the store message
/// (<see cref="CampaignDatabase.TryMapUnavailable"/>).
///
/// <para>
/// <b>The board is a whitelist</b> (§6.12): <see cref="CombatBoard"/> holds only the strings and numbers it prints, built
/// through a <see cref="ReadScope"/> for the perspective that is opened BEFORE the encounter is looked up (a damaged
/// entity page then fails with the store message whatever the fight). It never nests a tracker state, a row, a stat
/// block or a snapshot; it never says an encounter's name, notes, outcome, lair, session, write-back or XP; hidden and left
/// combatants leave no trace (rows renumbered over what is shown, copies renumbered per view); only party-side rows carry
/// numbers, and only when the row shows the combatant's own name (a Shown entity, an srd monster's own name, a typed or
/// custom name that passes): a party-side row read as "an unknown creature", a monster's name over a typed one, or a
/// disguise gets one HP word like every other side, so its numbers cannot tie the stand-in to the sheet the party knows
/// them from; a name, an effect or a spell is printed only when it passes the view-text check (one check per render), and a
/// fight the view may not see (none, a planned or paused one, a name that matches nothing) is one wording,
/// <see cref="CombatBoard.NothingText"/>, so no refusal confirms that a fight of some name exists.
/// </para>
/// </summary>
public sealed class CombatReader
{
    private readonly CampaignDatabase _database;

    /// <param name="database">campaigns.db (read connections only).</param>
    public CombatReader(CampaignDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    // -------------------------------------------------------------------------------------------------------------------
    // The author's state

    /// <summary>
    /// The AUTHOR's <c>combat state</c>: the fight <paramref name="encounter"/> names ("current" by default, "last", or a
    /// name) with the reminders its state calls for (the turn's, and every concentration save still owed:
    /// <see cref="PendingSaves"/>) and the last combat_log row; or, when "current" or "last" names no fight, the listing
    /// (it succeeds: "no combat running").
    /// </summary>
    /// <exception cref="DndInputException">A name that matches no encounter (the refusal lists the campaign's encounters: author output).</exception>
    public CombatStateRead State(CampaignRow campaign, string? encounter = null)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        return Mapped(() =>
        {
            using var connection = ReadConnection.Open(_database);
            var row = EncounterResolver.TryResolve(connection, campaign.Id, encounter);
            if (row is null)
            {
                var key = CampaignText.Key(string.IsNullOrWhiteSpace(encounter) ? EncounterResolver.Current : encounter);
                if (key is EncounterResolver.Current or EncounterResolver.Last)
                {
                    return new CombatStateRead(campaign.Slug, null, [], Listing(connection, campaign));
                }

                EncounterResolver.Resolve(connection, campaign, encounter);
            }

            var state = CombatStore.Load(connection, campaign, row!);
            var view = CombatViews.Encounter(connection, null, row!, state);
            var reminders = state.Status == ES.Active ? [.. CombatContext.Reminders(state), .. PendingSaves(state)] : new List<CombatReminder>();
            return new CombatStateRead(campaign.Slug, view, reminders, null) { LastChange = LastChange(connection, state) };
        });
    }

    /// <summary>
    /// The concentration saves still owed, from the state itself (each combatant's pending DCs, oldest first), in the words
    /// and with the call the step that owed them printed. A step's reminders are said once, in its own result; one lost
    /// from it (a result cut short, a client that dropped it) would otherwise never be seen again, though the save still
    /// blocks nothing and is still due. Death saves are not recorded as owed in the state (a dying creature's turn start
    /// says so), so only these can be recovered here.
    /// </summary>
    internal static IEnumerable<CombatReminder> PendingSaves(EncounterState state)
    {
        foreach (var c in state.Combatants.Where(c => !c.Removed && c.Concentration is { Pending.Count: > 0 }))
        {
            var held = c.Concentration!;
            var bonus = c.SheetSnapshot is { } snapshot ? snapshot.ConSaveBonus
                : c.StatBlock is { } block ? Domain.Rules.CombatRules.SaveBonus(Domain.Features.DslValues.Abilities.Con, block)
                : 0;
            foreach (var dc in held.Pending)
            {
                yield return new CombatReminder(
                    CombatValues.ReminderKinds.ConcentrationSave,
                    c.Id,
                    $"{c.Name}: concentration save DC {dc.ToString(CultureInfo.InvariantCulture)} to keep {held.Spell} " +
                    $"(Con save {(bonus >= 0 ? "+" : "−")}{Math.Abs(bonus).ToString(CultureInfo.InvariantCulture)})",
                    CombatCalls.Combat("concentration", ("targets", new[] { c.Address }), ("total", CombatCalls.Fill)));
            }
        }
    }

    /// <summary>The listing of a campaign with no fight running (the planned and paused fights, the last ended one).</summary>
    public CombatNoFight Encounters(CampaignRow campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        return Mapped(() =>
        {
            using var connection = ReadConnection.Open(_database);
            return Listing(connection, campaign);
        });
    }

    private static CombatNoFight Listing(SqliteConnection connection, CampaignRow campaign)
    {
        var all = EncounterResolver.All(connection, campaign.Id);
        var counts = connection.Query<(string EncounterId, long Count)>(
                "SELECT c.encounter_id, count(*) FROM combatant c JOIN encounter e ON e.id = c.encounter_id " +
                "WHERE e.campaign_id = @campaignId AND c.removed = 0 GROUP BY c.encounter_id", new { campaignId = campaign.Id })
            .ToDictionary(r => r.EncounterId, r => checked((int)r.Count), StringComparer.Ordinal);
        CombatEncounterSummary Summary(EncounterRow e) => new(
            e.Name, e.Status, checked((int)e.Round), counts.GetValueOrDefault(e.Id), e.EndedAt, e.OutcomeMd,
            CombatViews.WritebackStatus(connection, e.WritebackBatchId));
        var last = EncounterResolver.LastEnded(connection, campaign.Id);
        return new CombatNoFight(
            campaign.Slug,
            all.Where(e => e.Status == ES.Planned).Select(Summary).ToList(),
            all.Where(e => e.Status == ES.Paused).Select(Summary).ToList(),
            last is null ? null : Summary(last));
    }

    // The encounter's last combat_log row, its combatants named by their tracker names (author output).
    private static CombatLogView? LastChange(SqliteConnection connection, EncounterState state)
    {
        CampaignDatabase.EnsureDapperConfigured();
        var row = StoredRows.Read(connection, "combat_log", () => connection.QueryFirstOrDefault<CombatLogRow>(
            $"SELECT {CombatLogRow.Columns} FROM combat_log WHERE encounter_id = @id ORDER BY seq DESC LIMIT 1", new { id = state.Id }));
        return row is null ? null : CombatLogView.Of(row, state);
    }

    // -------------------------------------------------------------------------------------------------------------------
    // The board

    /// <summary>
    /// The party board (contract §6.12) of the fight <paramref name="encounter"/> names, as <paramref name="perspective"/>
    /// (a non-author view) may see it; <see cref="CombatBoard.Nothing"/> when there is no fight to show it.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="perspective"/> is the author's view (it reads <see cref="State"/>).</exception>
    /// <exception cref="DndInputException">A perspective this campaign cannot resolve (an unknown character).</exception>
    public CombatBoard Board(CampaignRow campaign, Perspective perspective, string? encounter = null)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(perspective);
        return Mapped(() =>
        {
            using var connection = ReadConnection.Open(_database);
            var scope = ReadScope.Open(connection, campaign, perspective, null);
            if (scope.IsAuthorView)
            {
                throw new ArgumentException("The author's view reads the author state (CombatReader.State), not the board.", nameof(perspective));
            }

            var row = Shown(connection, campaign.Id, encounter);
            return row is null ? CombatBoard.Nothing : Build(scope, CombatStore.Load(connection, campaign, row, authorView: false));
        });
    }

    // The fight a board may show: the active one ("current"), the last ended one ("last"), or a name of an active or ended
    // fight (the active one first, else the most recently ended). Planned and paused fights are never shown.
    private static EncounterRow? Shown(SqliteConnection connection, string campaignId, string? encounter)
    {
        var text = string.IsNullOrWhiteSpace(encounter) ? EncounterResolver.Current : encounter.Trim();
        var key = CampaignText.Key(text);
        if (key == EncounterResolver.Current)
        {
            return EncounterResolver.Active(connection, campaignId);
        }

        if (key == EncounterResolver.Last)
        {
            return EncounterResolver.LastEnded(connection, campaignId);
        }

        if (key.Length == 0)
        {
            return null;
        }

        var matches = EncounterResolver.All(connection, campaignId).Where(e => CampaignText.Key(e.Name) == key).ToList();
        return matches.FirstOrDefault(e => e.Status == ES.Active)
               ?? matches.Where(e => e.Status == ES.Ended).OrderBy(e => e.EndedAt, StringComparer.Ordinal).LastOrDefault();
    }

    /// <summary>The board of <paramref name="state"/> for the scope's view (class summary).</summary>
    internal static CombatBoard Build(ReadScope scope, EncounterState state)
    {
        var shown = state.Order.Where(c => !c.Removed && !c.Hidden)
            .Concat(state.Combatants.Where(c => c.Initiative is null && !c.Removed && !c.Hidden).OrderBy(c => c.OrderKey))
            .ToList();
        scope.LoadEntities(shown.Select(c => c.EntityId).OfType<string>());
        var allNames = state.Combatants.Select(c => c.Name).ToList();

        // Every free text a row may print, checked in one render; each draft remembers its text's index.
        var texts = new List<string?>();
        int Check(string text)
        {
            texts.Add(text);
            return texts.Count - 1;
        }

        var drafts = new List<RowDraft>();
        foreach (var c in shown)
        {
            var draft = new RowDraft(c);
            if (c.EntityId is not null)
            {
                if (scope.Entity(c.EntityId) is { Visible: true } entity)
                {
                    draft.Fixed = entity.Name;
                    draft.Ref = entity.Ref;
                    draft.OwnName = entity.Shown;
                }
                else
                {
                    draft.Fixed = c.StatBlock?.Name ?? BoardNames.UnknownCreature;
                }
            }
            else
            {
                var name = CombatBoardNames.BaseName(c.Name, c.StatBlock?.Name, allNames);
                if (c.StatBlock is { } block && CampaignText.Key(name) == CampaignText.Key(block.Name))
                {
                    draft.Fixed = block.Name;
                    draft.OwnName = true;
                }
                else
                {
                    draft.Typed = name;
                    draft.NameCheck = Check(name);
                    draft.Fallback = c.StatBlock?.Name ?? BoardNames.UnknownCreature;
                }
            }

            foreach (var condition in c.Conditions)
            {
                draft.Conditions.Add(condition.IsSrdCondition
                    ? (CombatConditions.TryMatch(condition.Name, out var canonical) ? canonical : condition.Name, null)
                    : (condition.Name, Check(condition.Name)));
            }

            if (c.Side == S.Party && c.Concentration is { Spell.Length: > 0 } held)
            {
                draft.SpellCheck = Check(held.Spell);
            }

            drafts.Add(draft);
        }

        var verdicts = texts.Count == 0 ? null : ViewTextCheck.Check(scope, texts);
        bool Passes(int? index) => index is { } i && verdicts is not null && verdicts.Passes(i);

        var names = drafts.Select(d => d.Fixed ?? (Passes(d.NameCheck) ? d.Typed! : d.Fallback!)).ToList();
        var numbered = CombatBoardNames.Renumber(names, drafts.Select(d => d.Ref).ToList());
        var turn = TurnRow(state, shown);
        var rows = new List<CombatBoardRow>();
        for (var i = 0; i < drafts.Count; i++)
        {
            var d = drafts[i];
            var c = d.Combatant;
            var conditions = d.Conditions.Select(x => x.Check is null ? x.Name : Passes(x.Check) ? x.Name : BoardNames.Effect)
                .Distinct(StringComparer.Ordinal).ToList();
            if (c.Side == S.Party && (d.OwnName || Passes(d.NameCheck)))
            {
                var max = c.EffectiveMaxHp(state.Ruleset);
                var saves = c.MakesDeathSaves && c.HpKnown && c.Hp == 0 && !c.Dead && (c.Dying || c.DeathSaves.Stable) ? c.DeathSaves : null;
                rows.Add(new CombatBoardRow(i + 1, numbered[i], d.Ref, turn == i, true,
                    c.HpKnown ? c.Hp : null, c.HpKnown ? max : null, c.TempHp, c.DisplayedAc, c.Exhaustion, saves,
                    c.Concentration is not null, Passes(d.SpellCheck) ? c.Concentration!.Spell : null, null, conditions));
            }
            else
            {
                rows.Add(new CombatBoardRow(i + 1, numbered[i], d.Ref, turn == i, false,
                    null, null, null, null, null, null, false, null, HpWord(state, c), conditions));
            }
        }

        return new CombatBoard(true, state.Round, state.Status == ES.Ended, rows);
    }

    // The row the turn marker goes on: the turn-holder when shown; when it is hidden, the last shown row before it in this
    // round's order; else none (§6.12: a hidden turn-holder must not be revealed by an empty turn).
    private static int? TurnRow(EncounterState state, IReadOnlyList<CombatantState> shown)
    {
        if (state.Status != ES.Active || state.Round < 1 || state.TurnHolder is not { } holder)
        {
            return null;
        }

        var index = shown.ToList().FindIndex(c => c.Id == holder.Id);
        if (index >= 0)
        {
            return index;
        }

        string? before = null;
        foreach (var c in state.Order)
        {
            if (c.Id == holder.Id)
            {
                break;
            }

            if (!c.Removed && !c.Hidden)
            {
                before = c.Id;
            }
        }

        return before is null ? null : shown.ToList().FindIndex(c => c.Id == before);
    }

    // One HP word for a row that is not party-side (§6.12): no numbers.
    private static string HpWord(EncounterState state, CombatantState c)
    {
        if (c.Dead || c.Defeated || (c.HpKnown && c.Hp == 0))
        {
            return BoardHpWords.Down;
        }

        if (!c.HpKnown)
        {
            return c.DamageTaken > 0 ? BoardHpWords.Hurt : BoardHpWords.Unhurt;
        }

        var max = c.EffectiveMaxHp(state.Ruleset) ?? c.MaxHp!.Value;
        if (c.Hp >= max)
        {
            return BoardHpWords.Unhurt;
        }

        return c.Hp <= max / 2 ? BoardHpWords.Bloodied : BoardHpWords.Hurt;
    }

    private sealed class RowDraft(CombatantState combatant)
    {
        public CombatantState Combatant { get; } = combatant;

        /// <summary>A name that needs no check (an entity's view name, a monster's name, a stand-in).</summary>
        public string? Fixed { get; set; }

        /// <summary>
        /// <see cref="Fixed"/> is the combatant's own name as the view knows it: a Shown entity's name, or an srd
        /// combatant's monster name when it has no typed name (a typed name shows only when it passes its check).
        /// </summary>
        public bool OwnName { get; set; }

        public string? Ref { get; set; }

        public string? Typed { get; set; }

        public int? NameCheck { get; set; }

        public string? Fallback { get; set; }

        public List<(string Name, int? Check)> Conditions { get; } = [];

        public int? SpellCheck { get; set; }
    }

    // -------------------------------------------------------------------------------------------------------------------

    private T Mapped<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (SqliteException ex) when (CampaignDatabase.TryMapUnavailable(ex, _database.Path, out var unavailable))
        {
            throw unavailable;
        }
    }
}

/// <summary>
/// How the board names copies and typed names (contract §6.12): the base a copy was numbered from, and the per-view
/// numbering of rows that show the same name ("Mummy", "Mummy 2", …) — never the tracker's suffix, which would count the
/// hidden copy the view must not know of.
/// </summary>
internal static class CombatBoardNames
{
    /// <summary>
    /// "Mummy 2" → "Mummy": the name the tracker numbered from, when the stem is the monster's name or the name of another
    /// combatant of the fight (the tracker numbers copies "Base", "Base 2", … and never deletes a combatant, so the first
    /// copy's name is always there). Any other name is returned as it is ("Room 101" stays).
    /// </summary>
    public static string BaseName(string name, string? monsterName, IReadOnlyCollection<string>? names = null)
    {
        var trimmed = name.Trim();
        var space = trimmed.LastIndexOf(' ');
        if (space <= 0)
        {
            return trimmed;
        }

        var suffix = trimmed[(space + 1)..];
        if (suffix.Length == 0 || !suffix.All(char.IsAsciiDigit) ||
            !int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 2)
        {
            return trimmed;
        }

        var stem = trimmed[..space].TrimEnd();
        var key = CampaignText.Key(stem);
        if (key.Length == 0)
        {
            return trimmed;
        }

        if ((monsterName is not null && CampaignText.Key(monsterName) == key) || (names?.Any(n => CampaignText.Key(n) == key) ?? false))
        {
            return stem;
        }

        return trimmed;
    }

    /// <summary>
    /// The rows' names numbered per view: the second row showing a name is "Name 2", the third "Name 3", in row order.
    /// Rows with a ref (an entity the view sees) keep their name: the ref tells them apart.
    /// </summary>
    public static IReadOnlyList<string> Renumber(IReadOnlyList<string> names, IReadOnlyList<string?> refs)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<string>(names.Count);
        for (var i = 0; i < names.Count; i++)
        {
            if (refs[i] is not null)
            {
                result.Add(names[i]);
                continue;
            }

            var key = CampaignText.Key(names[i]);
            var n = seen.TryGetValue(key, out var count) ? count + 1 : 1;
            seen[key] = n;
            result.Add(n == 1 ? names[i] : $"{names[i]} {n.ToString(CultureInfo.InvariantCulture)}");
        }

        return result;
    }
}
