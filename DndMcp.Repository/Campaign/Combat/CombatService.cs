using DndMcp.Domain.Campaign;
using DndMcp.Domain.Combat;
using DndMcp.Domain.Core;
using DndMcp.Domain.Dice;
using DndMcp.Repository.Campaign.Characters;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Repository.Campaign.Write;
using Microsoft.Data.Sqlite;
using CV = DndMcp.Domain.Campaign.CampaignValues;

namespace DndMcp.Repository.Campaign.Combat;

/// <summary>
/// The <c>combat</c> tool's writes (contract §6, §14.1): one method per action, each ONE
/// <see cref="CampaignDatabase.Write{T}"/> transaction that reads the encounter and its combatants INSIDE it (CRIT Q10),
/// runs T's pure step (<see cref="CombatTracker"/>), rolls what the step needs with the caller's <see cref="IDiceRoller"/>
/// and logs each roll (<see cref="CombatDice"/>, D11), and writes the changed rows and the combat_log rows with plain SQL
/// (<see cref="CombatStore"/>). Nothing here goes through the <see cref="ChangeRecorder"/>: HP ticks never enter
/// change_log (PLAN 7). The one logged batch is <see cref="End"/>'s write-back (D6).
///
/// <para>
/// <b>What is read before the transaction</b> (a <see cref="ReadScope"/> cannot run inside one, §6.10): the session-safe
/// name of every combatant (dice labels, a persisted condition's source) and the D20b warnings for typed names. The
/// encounter is re-resolved inside the transaction; a combatant another process added in between is named "a combatant".
/// </para>
/// <para>
/// <b>Refusals</b> are <see cref="DndInputException"/>s for the author (every <c>combat</c> action but <c>state
/// {perspective}</c> is author-only): they may name tracker names, handles and encounter names. A refused step writes
/// nothing (the tracker refuses before anything is rolled; anything thrown rolls the transaction back).
/// </para>
/// </summary>
public sealed partial class CombatService
{
    private readonly CampaignDatabase _database;
    private readonly IDiceRoller _roller;

    /// <param name="roller">The server's dice (the host's singleton; a scripted one in tests): every roll a step needs is made with it.</param>
    public CombatService(CampaignDatabase database, IDiceRoller roller)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(roller);
        _database = database;
        _roller = roller;
    }

    /// <summary>
    /// Test seam: runs inside <c>start</c>'s transaction after the one-active check and before the encounter is made
    /// active, so a test can make the partial unique index (not the check) refuse a second active fight.
    /// </summary>
    internal Action<SqliteConnection, SqliteTransaction>? BeforeActivate { get; set; }

    // -------------------------------------------------------------------------------------------------------------------
    // The tracker's steps

    /// <summary><c>set {combatants}</c>: change combatants already in the fight (a planned one too).</summary>
    public CombatOutcome Set(CampaignRow campaign, string? encounter, SetOp op) => Step(campaign, encounter, op);

    /// <summary><c>leave {targets}</c>.</summary>
    public CombatOutcome Leave(CampaignRow campaign, string? encounter, LeaveOp op) => Step(campaign, encounter, op);

    /// <summary><c>initiative {rolls?, surprised?, secret?}</c>: given values, server rolls for the rest (logged).</summary>
    public CombatOutcome Initiative(CampaignRow campaign, string? encounter, InitiativeOp op, bool? secret = null) => Step(campaign, encounter, op, secret);

    /// <summary><c>next {from?}</c>: a <paramref name="from"/> that is not the turn-holder is refused (a stale or retried call).</summary>
    public CombatOutcome Next(CampaignRow campaign, string? encounter, string? from = null) => Step(campaign, encounter, new NextOp(from));

    /// <summary><c>prev</c>: exact when the encounter's last combat_log row is the last <c>next</c>'s turn row.</summary>
    public CombatOutcome Prev(CampaignRow campaign, string? encounter) => Step(campaign, encounter, new PrevOp());

    /// <summary><c>damage</c>: dice rolled once for every target (logged); <c>critical</c> doubles a server roll's dice.</summary>
    public CombatOutcome Damage(CampaignRow campaign, string? encounter, DamageOp op, bool? secret = null) => Step(campaign, encounter, op, secret);

    /// <summary><c>heal</c> (and <c>temp</c>): <c>item</c> consumes from the source's holdings, else the target's.</summary>
    public CombatOutcome Heal(CampaignRow campaign, string? encounter, HealOp op, bool? secret = null) => Step(campaign, encounter, op, secret);

    /// <summary><c>condition</c>: add or remove conditions and named effects.</summary>
    public CombatOutcome Condition(CampaignRow campaign, string? encounter, ConditionOp op) => Step(campaign, encounter, op);

    /// <summary><c>concentration</c>: start, drop, or resolve the oldest pending save (rolled by the server when no value is given).</summary>
    public CombatOutcome Concentration(CampaignRow campaign, string? encounter, ConcentrationOp op, bool? secret = null) => Step(campaign, encounter, op, secret);

    /// <summary><c>use</c>: spend (negative restores) a slot, the Pact slots, a resource or an item.</summary>
    public CombatOutcome Use(CampaignRow campaign, string? encounter, UseOp op) => Step(campaign, encounter, op);

    /// <summary><c>legendary {source, amount, name?, resistance?}</c>.</summary>
    public CombatOutcome Legendary(CampaignRow campaign, string? encounter, LegendaryOp op) => Step(campaign, encounter, op);

    /// <summary><c>death_save {face? | total? | stable?}</c> (rolled by the server when no value is given).</summary>
    public CombatOutcome DeathSave(CampaignRow campaign, string? encounter, DeathSaveOp op, bool? secret = null) => Step(campaign, encounter, op, secret);

    /// <summary>
    /// Any tracker step but <c>add</c> (which resolves characters: <see cref="Add"/>): one transaction, the encounter read
    /// inside it (<paramref name="encounter"/>: "current", "last" or a name; null is "current"), the rolls made and logged,
    /// the rows written.
    /// </summary>
    /// <param name="secret">The call's <c>secret</c>: overrides §6.10's default for every roll the step makes.</param>
    /// <exception cref="DndInputException">The encounter is not found, or the tracker refuses the step.</exception>
    public CombatOutcome Step(CampaignRow campaign, string? encounter, CombatOp op, bool? secret = null)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(op);
        var action = ActionOf(op);
        var (subjects, warnings) = PreRead(campaign, encounter ?? EncounterResolver.Current, (_, preview) => MayRoll(preview, op) ? [] : null,
            (_, preview) => TypedTexts(op, preview));
        return Write(campaign, (connection, transaction, current) =>
        {
            var row = EncounterResolver.Resolve(connection, current, encounter, transaction);
            var state = CombatStore.Load(connection, current, row, transaction);
            var run = new CombatRun(connection, transaction, current, row, state, _database.Now(), subjects, _roller, secret);
            run.Apply(Fill(connection, transaction, state, op));
            return run.Outcome(action, warnings);
        });
    }

    /// <summary>
    /// What a step that marks combatants surprised is called (no action of its own: <c>start</c> and <c>initiative</c>
    /// take <c>surprised</c>).
    /// </summary>
    public const string SurprisedStep = "surprised";

    /// <summary>The <c>combat</c> action an op is (<see cref="CombatActions"/>, for results and messages); <c>add</c> goes through <see cref="Add"/>.</summary>
    /// <exception cref="ArgumentException">An <see cref="AddOp"/> (it needs the Repository's character resolution), or not a step.</exception>
    public static string ActionOf(CombatOp op) => op switch
    {
        AddOp => throw new ArgumentException("add resolves characters and sheets: call CombatService.Add.", nameof(op)),
        SetOp => CombatActions.Set,
        LeaveOp => CombatActions.Leave,
        SurpriseOp => SurprisedStep,
        InitiativeOp => CombatActions.Initiative,
        NextOp => CombatActions.Next,
        PrevOp => CombatActions.Prev,
        DamageOp => CombatActions.Damage,
        HealOp => CombatActions.Heal,
        ConditionOp => CombatActions.Condition,
        ConcentrationOp => CombatActions.Concentration,
        UseOp => CombatActions.Use,
        LegendaryOp => CombatActions.Legendary,
        DeathSaveOp => CombatActions.DeathSave,
        _ => throw new ArgumentException($"Not a combat step: {op.GetType().Name}.", nameof(op)),
    };

    /// <summary>
    /// What the tracker needs that only the Repository has: the holdings of the sheet-seeded combatants' characters for
    /// <c>heal {item}</c> and <c>use {item}</c>, the encounter's last combat_log row for <c>prev</c>.
    /// </summary>
    internal static CombatOp Fill(SqliteConnection connection, SqliteTransaction transaction, EncounterState state, CombatOp op) => op switch
    {
        HealOp heal when heal.Item is not null => heal with { Holdings = SheetHoldings(connection, transaction, state) },
        UseOp use when use.Item is not null => use with { Holdings = SheetHoldings(connection, transaction, state) },
        PrevOp => new PrevOp(CombatStore.LastChange(connection, state.Id, transaction)),
        _ => op,
    };

    private static IReadOnlyList<CombatItem> SheetHoldings(SqliteConnection connection, SqliteTransaction transaction, EncounterState state) =>
        CombatStore.Holdings(connection, state.Combatants.Where(c => c.IsSheetSeeded && c.EntityId is not null).Select(c => c.EntityId!), transaction);

    // -------------------------------------------------------------------------------------------------------------------
    // Plumbing

    /// <summary>
    /// One transaction with the campaign re-read inside it (the caller's copy may be stale or deleted).
    /// </summary>
    private T Write<T>(CampaignRow campaign, Func<SqliteConnection, SqliteTransaction, CampaignRow, T> work, bool dryRun = false) =>
        _database.Write((connection, transaction) =>
        {
            var current = WriteBatch.LoadCampaign(connection, transaction, campaign.Id) ??
                          throw new DndInputException($"Campaign {campaign.Slug} no longer exists; campaign {{\"action\": \"list\"}} shows the campaigns.");
            return work(connection, transaction, current);
        }, dryRun);

    /// <summary>
    /// The reads a step needs before its transaction (class summary): the safe names of the encounter's combatants and of
    /// the extra subjects (a call's new add entries), and the D20b warnings of the call's typed texts. A store failure is
    /// mapped to the store message (<see cref="CampaignDatabase.TryMapUnavailable"/>).
    /// </summary>
    /// <param name="encounter">The fight the call addresses, whose combatants may be roll subjects; null: none (a call that rolls only for new entries).</param>
    /// <param name="extra">
    /// The extra subjects, or null when the call rolls nothing: then no name is computed at all (each name may take a
    /// view-text check, and most steps roll nothing). A step that rolls after all (another process changed the fight in
    /// between) labels its subject "a combatant" and counts its entity as not shown to the party, so §6.10 errs toward
    /// secret: the safe side.
    /// </param>
    private (CombatSubjects Subjects, IReadOnlyList<string> Warnings) PreRead(
        CampaignRow campaign,
        string? encounter,
        Func<SqliteConnection, EncounterState?, IReadOnlyList<SafeNameSubject>?> extra,
        Func<SqliteConnection, EncounterState?, IReadOnlyList<TypedText>> texts)
    {
        return Mapped(() =>
        {
            using var connection = ReadConnection.Open(_database);
            EncounterState? state = null;
            if (encounter is not null && EncounterResolver.TryResolve(connection, campaign.Id, encounter) is { } row)
            {
                state = CombatStore.Load(connection, campaign, row);
            }

            var subjects = extra(connection, state) is { } more ? CombatSubjects.Compute(connection, campaign, state, more) : CombatSubjects.None;
            var warnings = CombatWarnings.For(connection, campaign, texts(connection, state));
            return (subjects, warnings);
        });
    }

    /// <summary>
    /// Whether a step may roll (so its subjects' names are worth computing): the tracker's needs on the state as read
    /// before the transaction; when the tracker refuses there (or cannot tell: an item heal reads holdings), it may.
    /// </summary>
    private static bool MayRoll(EncounterState? preview, CombatOp op)
    {
        if (preview is null || op is NextOp or PrevOp or SetOp or LeaveOp or ConditionOp or UseOp or LegendaryOp or SurpriseOp)
        {
            return false;
        }

        try
        {
            return CombatTracker.Needs(preview, op).Count > 0;
        }
        catch (DndInputException)
        {
            return true;
        }
    }

    /// <summary>A read outside the transaction, with SQLite's user-fixable failures mapped to the store message.</summary>
    internal T Mapped<T>(Func<T> read)
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

    /// <summary>The typed texts of a step that a party view may read (D20b): effect names, a spell, a revealed typed name.</summary>
    private static IReadOnlyList<TypedText> TypedTexts(CombatOp op, EncounterState? state)
    {
        var texts = new List<TypedText>();
        switch (op)
        {
            case ConditionOp { Add: { } add }:
                texts.AddRange(add.Where(n => !string.IsNullOrWhiteSpace(n) && !CombatConditions.TryMatch(n, out _) &&
                                               CampaignText.Key(n) != CampaignText.Key(CombatTracker.HiddenName))
                    .Select(n => new TypedText(n.Trim(), "this effect", BoardNames.Effect)));
                break;
            case ConcentrationOp { Spell: { } spell } when !string.IsNullOrWhiteSpace(spell):
                texts.Add(new TypedText(spell.Trim(), "this concentration", "concentrating, with no spell name"));
                break;
            case SetOp set when state is not null:
                foreach (var entry in set.Entries.Where(e => e.Hidden == false))
                {
                    try
                    {
                        var c = CombatAddressing.Resolve(state.Combatants, entry.Combatant, "combatants");
                        if (TypedName(c, state.Combatants.Select(x => x.Name).ToList()) is { } typed)
                        {
                            texts.Add(typed);
                        }
                    }
                    catch (DndInputException)
                    {
                        // The step refuses it inside the transaction.
                    }
                }

                break;
        }

        return texts;
    }

    /// <summary>
    /// A combatant's typed name as the party board would test it, or null: an entity-linked combatant is named by the
    /// view's own name for the entity (never its typed name), and an srd combatant whose name is the monster's needs no check.
    /// </summary>
    internal static TypedText? TypedName(CombatantState c, IReadOnlyCollection<string> names)
    {
        if (c.EntityId is not null)
        {
            return null;
        }

        var name = CombatBoardNames.BaseName(c.Name, c.StatBlock?.Name, names);
        if (c.StatBlock is { } block && CampaignText.Key(name) == CampaignText.Key(block.Name))
        {
            return null;
        }

        return new TypedText(name, "this combatant", c.StatBlock?.Name ?? BoardNames.UnknownCreature);
    }

    /// <summary>
    /// The edition the host resolves a call's <c>srd</c> monsters in (contract §6.2: "in the encounter's edition"), BEFORE it
    /// calls <see cref="Add"/>, <see cref="Start"/> or <see cref="Prepare"/>: for <c>add</c>, the ruleset of the fight
    /// <paramref name="encounter"/> names; for <c>start</c>, the ruleset of the planned or paused fight
    /// <paramref name="encounter"/> (else <paramref name="name"/>) names, else a new fight's; for <c>prepare</c> (and a new
    /// fight), <paramref name="edition"/>, else the campaign's ruleset (required in a mixed campaign). A stat block resolved
    /// in another edition than the fight's would carry the other edition's numbers into it.
    /// </summary>
    /// <param name="action"><see cref="CombatActions.Add"/>, <see cref="CombatActions.Start"/> or <see cref="CombatActions.Prepare"/> (any other action resolves no monster: the fight's ruleset).</param>
    /// <exception cref="DndInputException">The fight is not found (add), or no edition can be chosen (a mixed campaign).</exception>
    public string MonsterEdition(CampaignRow campaign, string action, string? encounter = null, string? name = null, string? edition = null)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        if (action == CombatActions.Prepare)
        {
            return Edition(campaign, edition);
        }

        return Mapped(() =>
        {
            using var connection = ReadConnection.Open(_database);
            if (action == CombatActions.Start)
            {
                var named = !string.IsNullOrWhiteSpace(encounter)
                    ? EncounterResolver.TryResolve(connection, campaign.Id, encounter)
                    : !string.IsNullOrWhiteSpace(name) ? EncounterResolver.ByName(connection, campaign.Id, name) : null;
                return named is { Status: CV.EncounterStatuses.Planned or CV.EncounterStatuses.Paused } existing ? existing.Ruleset : Edition(campaign, edition);
            }

            return EncounterResolver.Resolve(connection, campaign, encounter).Ruleset;
        });
    }

    /// <summary>The fight's edition: <paramref name="edition"/> when given, else the campaign's ruleset (required in a mixed campaign).</summary>
    /// <exception cref="DndInputException">Not an edition, or none given in a mixed campaign.</exception>
    public static string Edition(CampaignRow campaign, string? edition)
    {
        if (!string.IsNullOrWhiteSpace(edition))
        {
            return CV.Rulesets.Editions.TryMatch(edition, out var canonical)
                ? canonical
                : throw new DndInputException($"edition \"{WriteBatch.Echo(edition)}\" is not an edition; give \"2014\" or \"2024\".");
        }

        return campaign.Ruleset == CV.Rulesets.Mixed
            ? throw new DndInputException($"edition is required: {campaign.Slug} mixes the 2014 and 2024 rules; give \"2014\" or \"2024\" for this fight.")
            : campaign.Ruleset;
    }
}
