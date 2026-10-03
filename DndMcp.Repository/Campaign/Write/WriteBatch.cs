using System.Globalization;
using System.Text.Json.Nodes;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using Microsoft.Data.Sqlite;
using V = DndMcp.Domain.Campaign.CampaignValues.Visibilities;

namespace DndMcp.Repository.Campaign.Write;

/// <summary>What a session writer's session chooser can read before the batch exists (inside the transaction).</summary>
internal sealed record WriteBatchSetup(SqliteConnection Connection, SqliteTransaction Transaction, CampaignRow Campaign, HandleResolver Resolver);

/// <summary>
/// The session a session writer files its batch under: its entity id (a fresh id for a session the call creates, which
/// the call then creates with exactly that id) and its number.
/// </summary>
internal sealed record SessionTarget(string EntityId, int Number);

/// <summary>
/// One write call's batch: its transaction, its <see cref="ChangeRecorder"/>, the campaign as it is inside the
/// transaction, the session context, and what the ops report (applied, warnings, consequences). Every writer of an
/// existing campaign (<see cref="CampaignWriter"/>, <see cref="KnowledgeWriter"/>, <see cref="SessionWriter"/>,
/// <see cref="CampaignStore.Update"/>) runs its work through <see cref="Run{T}"/>, so the rules that make a call one batch
/// hold everywhere. (Creating a campaign has no campaign to re-read yet; <see cref="CampaignStore.Create"/> opens its own
/// batch the same way.)
///
/// <para>
/// <b>One call, one transaction, one batch.</b> <see cref="Run{T}"/> opens <see cref="CampaignDatabase.Write{T}"/>
/// (BEGIN IMMEDIATE), builds the recorder, runs the work, and lets the database flush and commit, or roll back for a dry
/// run. Anything thrown rolls the whole batch back: an op that fails at item 3 leaves items 1 and 2 unwritten, which is
/// what the model needs to be able to fix item 3 and resend the same call. So nothing here catches and continues.
/// </para>
/// <para>
/// <b>Everything is read inside the transaction.</b> The campaign row is re-read (the caller's copy may be stale), handles
/// resolve through the transaction (an op can name an entity an earlier op of the same call created), and register
/// codes are computed from the rows as they stand (so a dry run shows the code the real run assigns).
/// </para>
/// </summary>
internal sealed class WriteBatch
{
    private List<string>? _codes;

    private WriteBatch(
        CampaignDatabase database,
        SqliteConnection connection,
        SqliteTransaction transaction,
        CampaignRow campaign,
        SessionRow? session,
        SessionTarget? ownSession,
        BatchContext context,
        bool dryRun,
        ChangeRecorder? recorder = null)
    {
        Database = database;
        Connection = connection;
        Transaction = transaction;
        Campaign = campaign;
        Session = ownSession is null ? session : null;
        SessionNumber = ownSession?.Number ?? (session is null ? null : checked((int)session.Number));
        Context = context;
        DryRun = dryRun;
        Recorder = recorder ?? new ChangeRecorder(connection, transaction, context, database.Now());
        Resolver = new HandleResolver(connection, campaign.Id, transaction);
        Knowledge = new KnowledgeRows(this);
        Reveals = new RevealChecks(this);
        PlayerText = new PlayerTextChecks(this);
    }

    public CampaignDatabase Database { get; }

    public SqliteConnection Connection { get; }

    public SqliteTransaction Transaction { get; }

    /// <summary>The campaign as read inside this transaction (refresh with <see cref="ReloadCampaign"/> after changing it).</summary>
    public CampaignRow Campaign { get; private set; }

    /// <summary>
    /// The session context's row as it stood when the batch opened (explicit, else live), or null: none, or a session
    /// writer filing the batch under the session it writes (<see cref="SessionTarget"/>), which may not exist yet.
    /// </summary>
    public SessionRow? Session { get; }

    /// <summary>The session context's number (the default learned session of knowledge rows), or null.</summary>
    public int? SessionNumber { get; }

    /// <summary>The session context's entity id: change_log.session_id of every row, and the default learned/established session.</summary>
    public string? SessionId => Context.SessionId;

    public BatchContext Context { get; }

    /// <summary>
    /// The change_log action of a derived secret-status update: <c>secret_status</c>, or <c>undo</c> when the derivation
    /// runs inside an undo batch (every row of an undo batch is labelled undo, §3.5).
    /// </summary>
    public string DerivationAction { get; private init; } = "secret_status";

    public bool DryRun { get; }

    public ChangeRecorder Recorder { get; }

    public HandleResolver Resolver { get; }

    /// <summary>The knowledge-row writer shared by <c>known_by</c> and campaign_knowledge.</summary>
    public KnowledgeRows Knowledge { get; }

    /// <summary>The gate checks and secret-status derivation that run when the batch finishes.</summary>
    public RevealChecks Reveals { get; }

    /// <summary>
    /// The player-text check that runs when the batch finishes: every op notes the entities and facts it is about to
    /// change, and the text the batch made readable to a player-side view is scanned for forbidden words and hidden names.
    /// </summary>
    public PlayerTextChecks PlayerText { get; }

    public List<AppliedOp> Applied { get; } = [];

    public List<WriteWarning> Warnings { get; } = [];

    public List<Consequence> Consequences { get; } = [];

    /// <summary>Set by anything that can change a secret's derived status (knowledge of facts, facts, fact links).</summary>
    public bool DerivationNeeded { get; set; }

    /// <summary>Secrets whose status an op set by hand (entity id → the status set), for the "derived" warning.</summary>
    public Dictionary<string, string> SecretStatusSetByHand { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Runs <paramref name="work"/> as one batch. <paramref name="work"/> must call <see cref="Finish"/> before it builds
    /// its result: the gate warnings it adds are appended to the batch's reason, which every change_log row carries, and
    /// the rows are written when the work returns.
    /// </summary>
    /// <param name="ownSession">
    /// For a session writer (start, record_past): chooses, inside the transaction, the session the call writes, and the
    /// batch is filed under it when the call gives no explicit session. Point-in-time replay reverses a row's changes by
    /// the session they are filed under, so starting session 12 filed under no session would leave it "live" as of
    /// session 5, and recording session 3 while 12 is live would make session 3 not exist as of session 5.
    /// </param>
    /// <exception cref="DndInputException">The campaign no longer exists, the session context names no session, the reason is too long, or the work refused.</exception>
    public static T Run<T>(CampaignDatabase database, CampaignRow campaign, WriteContext context, string tool, Func<WriteBatch, T> work,
        Func<WriteBatchSetup, SessionTarget>? ownSession = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(work);
        var reason = CheckReason(context.Reason);
        return database.Write((connection, transaction) =>
        {
            var current = LoadCampaign(connection, transaction, campaign.Id) ??
                          throw new DndInputException($"Campaign {campaign.Slug} no longer exists; campaign {{\"action\": \"list\"}} shows the campaigns.");
            var resolver = new HandleResolver(connection, current.Id, transaction);
            var session = ResolveSessionContext(resolver, context.Session, current.Slug);
            var own = ownSession is not null && string.IsNullOrWhiteSpace(context.Session)
                ? ownSession(new WriteBatchSetup(connection, transaction, current, resolver))
                : null;
            var batchContext = new BatchContext(current.Id, CampaignDatabase.NewId(), context.Actor, context.Tool ?? tool,
                own?.EntityId ?? session?.EntityId, reason);
            var batch = new WriteBatch(database, connection, transaction, current, session, own, batchContext, context.DryRun);
            return work(batch);
        }, context.DryRun);
    }

    /// <summary>
    /// A batch over a recorder that already exists on the transaction (the undo batch's, inside
    /// <see cref="UndoEngine.Undo"/>): one transaction has one recorder, so the undo's derived secret statuses must be
    /// written through the undo's own. Its derived updates are labelled <paramref name="derivationAction"/>.
    /// </summary>
    public static WriteBatch OnRecorder(CampaignDatabase database, SqliteConnection connection, SqliteTransaction transaction, CampaignRow campaign,
        SessionRow? session, ChangeRecorder recorder, bool dryRun, string derivationAction) =>
        new(database, connection, transaction, campaign, session, null, recorder.Batch, dryRun, recorder) { DerivationAction = derivationAction };

    /// <summary>Runs <paramref name="work"/> as one batch, finishes it and returns the standard result.</summary>
    public static WriteResult Run(CampaignDatabase database, CampaignRow campaign, WriteContext context, string tool, Action<WriteBatch> work) =>
        Run(database, campaign, context, tool, batch =>
        {
            work(batch);
            batch.Finish();
            return batch.Result();
        });

    /// <summary>
    /// The end-of-batch work every writer shares: the reveal checks of §3.4 for every gated fact that reached a non-author
    /// knower, the "reachable before the gate" warning, and the secret statuses derived from them (written as ordinary
    /// logged updates, so undo reverts them); then the player-text check (<see cref="PlayerTextChecks"/>), which reads the
    /// gates as the batch leaves them. Gate warnings are appended to the batch's reason; player-text warnings are not.
    /// </summary>
    public void Finish()
    {
        Reveals.Finish();
        PlayerText.Finish();
    }

    /// <summary>The standard result: the batch id (null for a dry run), the session context, and what was reported.</summary>
    public WriteResult Result() =>
        new(DryRun ? null : Context.BatchId, DryRun, SessionNumber, Applied.ToList(), Warnings.ToList(), Consequences.ToList());

    /// <summary>Re-reads the campaign row (after the batch changed it).</summary>
    public void ReloadCampaign() => Campaign = LoadCampaign(Connection, Transaction, Campaign.Id)!;

    /// <summary>The campaign row by id, or null.</summary>
    public static CampaignRow? LoadCampaign(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        CampaignDatabase.EnsureDapperConfigured();
        return connection.QueryFirstOrDefault<CampaignRow>($"SELECT {CampaignRow.Columns} FROM campaign WHERE id = @id", new { id }, transaction);
    }

    /// <summary>The reason as stored, or null; refused when longer than a note.</summary>
    public static string? CheckReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        var trimmed = reason.Trim();
        if (trimmed.Length > CampaignLimits.MaxNoteLength)
        {
            throw new DndInputException(
                $"reason is {Number(trimmed.Length)} characters; at most {Number(CampaignLimits.MaxNoteLength)} (it is stored on every change_log row of the call).");
        }

        return trimmed;
    }

    /// <summary>
    /// The session context: the explicit session (a number or a session handle), else the live session, else none.
    /// <paramref name="campaignSlug"/> is the resolver's campaign, named in the call a refusal prints (<see cref="SessionCall"/>).
    /// </summary>
    /// <exception cref="DndInputException">The explicit session is not a session handle, or names no session.</exception>
    public static SessionRow? ResolveSessionContext(HandleResolver resolver, string? session, string campaignSlug)
    {
        if (string.IsNullOrWhiteSpace(session))
        {
            return resolver.LiveSession();
        }

        var text = session.Trim();
        CampaignHandle handle;
        if (text.All(char.IsAsciiDigit))
        {
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number > CampaignLimits.MaxSessionNumber)
            {
                throw new DndInputException($"session {Echo(text)} is not a session number (0 to {Number(CampaignLimits.MaxSessionNumber)}).");
            }

            handle = new CampaignHandle.SessionByNumber(number);
        }
        else if (!CampaignHandle.TryParse(text, out handle, out _) ||
                 handle is not (CampaignHandle.SessionByNumber or CampaignHandle.SessionLive or CampaignHandle.SessionLast))
        {
            throw new DndInputException($"session \"{Echo(text)}\" is not a session: give its number (12), session:12, session:live or session:last.");
        }

        return resolver.TrySession(handle) ?? throw new DndInputException(handle switch
        {
            CampaignHandle.SessionByNumber n => $"session {Number(n.Number)}: no such session in this campaign. {MissingSessionHint(campaignSlug, n.Number)}",
            CampaignHandle.SessionLive => $"session:live: no session is live; {SessionCall(campaignSlug, "start")} starts one, or give the session's number.",
            _ => "session:last: no session has been played yet; give the session's number.",
        });
    }

    // ---- references -------------------------------------------------------------------------------------------------

    /// <summary>An entity as the author view prints it: <c>kind:slug</c>.</summary>
    public static string Ref(EntityRow entity) => entity.Handle;

    /// <summary>A fact as every view prints it: <c>f:&lt;n&gt;</c>.</summary>
    public static string Ref(FactRow fact) => fact.SeqHandle;

    public EntityRow? EntityById(string? id, bool includeDeleted = true) =>
        id is null
            ? null
            : Connection.QueryFirstOrDefault<EntityRow>(
                $"SELECT {EntityRow.Columns} FROM entity WHERE id = @id AND campaign_id = @campaignId" +
                (includeDeleted ? string.Empty : " AND deleted_at IS NULL"),
                new { id, campaignId = Campaign.Id }, Transaction);

    public FactRow? FactById(string? id, bool includeDeleted = true) =>
        id is null
            ? null
            : Connection.QueryFirstOrDefault<FactRow>(
                $"SELECT {FactRow.Columns} FROM fact WHERE id = @id AND campaign_id = @campaignId" +
                (includeDeleted ? string.Empty : " AND deleted_at IS NULL"),
                new { id, campaignId = Campaign.Id }, Transaction);

    /// <summary>The <c>f:&lt;n&gt;</c> handle of a fact id (the id itself when it is not a fact of this campaign).</summary>
    public string FactRefById(string id) => FactById(id) is { } fact ? Ref(fact) : id;

    /// <summary>The <c>kind:slug</c> of an entity id (the id itself when it is not an entity of this campaign).</summary>
    public string EntityRefById(string id) => EntityById(id) is { } entity ? Ref(entity) : id;

    /// <summary>
    /// The live entity a handle names, or a refusal: "&lt;field&gt; &lt;handle&gt;: no such entity" (with a note when it
    /// is deleted, and suggestions only among entities every player already knows by their own names, so a typo never
    /// makes an error message spell a hidden name).
    /// </summary>
    /// <param name="kind">The kind the handle must name (a character for a knower), or null for any.</param>
    /// <param name="notFoundHint">A sentence to add when nothing is found (upsert: how to create instead).</param>
    public EntityRow RequireEntity(string subject, string where, string field, string text, string? kind = null, string? notFoundHint = null)
    {
        if (!CampaignHandle.TryParse(text, out var handle, out var problem))
        {
            throw Problem(subject, $"{where}: {field}: {problem}");
        }

        EntityRow? entity;
        try
        {
            entity = Resolver.TryEntity(handle);
        }
        catch (DndInputException ex)
        {
            throw Problem(subject, $"{where}: {field}: {ex.Message}");
        }

        if (entity is not null && (kind is null || entity.Kind == kind))
        {
            return entity;
        }

        if (entity is null && Resolver.TryEntity(handle, includeDeleted: true) is { IsDeleted: true } deleted &&
            (kind is null || deleted.Kind == kind))
        {
            throw Problem(subject,
                $"{where}: {field} {handle.Text} is deleted ({deleted.SeqHandle}); restore it first with {{\"op\": \"restore\", \"ref\": \"{deleted.SeqHandle}\"}}.");
        }

        var what = kind is null ? "entity" : kind;
        var suggestions = Suggestions(kind, handle);
        var hint = suggestions.Count == 0 ? string.Empty : $" Did you mean {string.Join(", ", suggestions)}?";
        var extra = notFoundHint is null ? string.Empty : " " + notFoundHint;
        throw Problem(subject, $"{where}: {field}: no {what} {handle.Text} in this campaign.{extra}{hint}");
    }

    /// <summary>The live fact a handle names, or a refusal naming the handle (and noting a deleted one).</summary>
    public FactRow RequireFact(string subject, string where, string field, string text)
    {
        if (!CampaignHandle.TryParse(text, out var handle, out var problem))
        {
            throw Problem(subject, $"{where}: {field}: {problem}");
        }

        if (Resolver.TryFact(handle) is { } fact)
        {
            return fact;
        }

        if (Resolver.TryFact(handle, includeDeleted: true) is { } deleted)
        {
            throw Problem(subject,
                $"{where}: {field} {handle.Text} is deleted ({deleted.SeqHandle}); restore it first with {{\"op\": \"restore\", \"ref\": \"{deleted.SeqHandle}\"}}.");
        }

        throw Problem(subject, $"{where}: {field}: no fact {handle.Text} in this campaign; give f:<n> or its code." +
                               (handle is CampaignHandle.FactBySeq ? SharedNumbers(subject) : string.Empty));
    }

    /// <summary>
    /// Review UR3: f:&lt;n&gt; numbers come from one sequence shared by every campaign in campaigns.db, so a new campaign's
    /// first fact is not f:1, and a batch that creates facts and then names them by the numbers it guessed (gate after
    /// "f:1" in a second campaign) is refused with nothing to say why. A fact made earlier in the same batch has no number
    /// the model can know before the call returns, but it can have a code, which an op may name; campaign_write's ops are
    /// the only place a batch makes facts, so only their refusal says so.
    /// </summary>
    private static string SharedNumbers(string subject) =>
        subject == OpScope.Subject
            ? " f:<n> numbers are shared by all campaigns; to use a fact made earlier in this batch, give it a code (code \"A1\") and use that."
            : " f:<n> numbers are shared by all campaigns.";

    /// <summary>The number of a session entity id; null for null or an id that is not a session of this campaign.</summary>
    public int? SessionNumberOf(string? sessionEntityId) =>
        sessionEntityId is null
            ? null
            : Connection.QueryFirstOrDefault<long?>(
                "SELECT number FROM session WHERE entity_id = @id AND campaign_id = @campaignId",
                new { id = sessionEntityId, campaignId = Campaign.Id }, Transaction) is { } number
                ? checked((int)number)
                : null;

    /// <summary>The session entity id for a session number, or a refusal (with <see cref="MissingSessionHint"/>).</summary>
    public string RequireSession(string subject, string where, string field, int number) =>
        Resolver.SessionByNumber(number)?.EntityId ?? throw Problem(subject,
            $"{where}: {field}: no session {Number(number)} in this campaign. {MissingSessionHint(Campaign.Slug, number)}");

    /// <summary>
    /// What to do about a session number that names no session, by when it was played (review U08): start it when it is
    /// being played now, record_past when it was played already, plan it when it is still to come. Offering only plan (and
    /// record_past in passing) sent "session 7 tonight" down the plan path, which files what happens at the table under a
    /// session that is not live and leaves the author to start it later. The start call is printed whole, naming the
    /// campaign and the session (<see cref="SessionCall"/>), because it is the one a write during a session needs.
    /// </summary>
    public static string MissingSessionHint(string campaignSlug, int number) =>
        $"Start it if it is being played now: {SessionCall(campaignSlug, "start", number)}. If it was played already, send the same call " +
        "with \"record_past\" instead of \"start\"; if it is still to come, with \"plan\".";

    /// <summary>
    /// A campaign_session call as a refusal prints it: <c>campaign_session {"action": "plan", "session": 7, "campaign": "sky"}</c>.
    /// It names the campaign the refused call wrote to, because a printed call goes to whatever campaign is current when
    /// the model sends it: a call that named a campaign other than the current one was refused, and the fix it was handed
    /// (start a session, end the live one, plan the missing one) would land in the current campaign instead, starting or
    /// ending a session there. It names the session when the fix is about one (plan session 7, not the next one).
    /// </summary>
    public static string SessionCall(string campaignSlug, string action, int? session = null) =>
        $"campaign_session {{\"action\": \"{action}\"" + (session is { } n ? $", \"session\": {Number(n)}" : string.Empty) +
        $", \"campaign\": \"{campaignSlug}\"}}";

    /// <summary>
    /// Close handles for a "not found" message: only entities of public or party visibility that no party or public
    /// knowledge row renames or hides, printed as <c>kind:slug</c> when the slug spells the name they show, else
    /// <c>e:&lt;n&gt;</c>. The author reads these messages too, but so does anything driving the client, and a
    /// suggestion is exactly the place a hidden true name would otherwise slip out.
    /// </summary>
    public IReadOnlyList<string> Suggestions(string? kind, CampaignHandle handle)
    {
        var text = handle switch
        {
            CampaignHandle.EntityBySlug bySlug => bySlug.Slug.Replace('-', ' '),
            CampaignHandle.ByCode byCode => byCode.Code,
            _ => string.Empty,
        };
        if (text.Length == 0)
        {
            return [];
        }

        var renamed = Connection.Query<string>(
            "SELECT DISTINCT entity_id FROM knowledge WHERE campaign_id = @campaignId AND entity_id IS NOT NULL " +
            "AND knower_kind IN @knowers AND (known_as IS NOT NULL OR state IN @hiding)",
            new
            {
                campaignId = Campaign.Id,
                knowers = new[] { CampaignValues.KnowerKinds.Party, CampaignValues.KnowerKinds.Public },
                hiding = new[] { CampaignValues.KnowledgeStates.Unrecognized, CampaignValues.KnowledgeStates.Unaware, CampaignValues.KnowledgeStates.Forgot },
            }, Transaction).ToHashSet(StringComparer.Ordinal);
        return Resolver.Suggest(kind, text, entity =>
        {
            if (entity.Visibility is not (V.Public or V.Party) || renamed.Contains(entity.Id))
            {
                return null;
            }

            var shown = entity.Slug == CampaignSlugs.From(entity.Name, entity.Kind) ? entity.Handle : entity.SeqHandle;
            return new SuggestionView(entity.Name, shown);
        });
    }

    // ---- defaults -----------------------------------------------------------------------------------------------------

    /// <summary>The campaign's settings object.</summary>
    public JsonObject Settings => JsonNode.Parse(Campaign.Settings) as JsonObject ?? new JsonObject();

    /// <summary>
    /// A new entity's visibility when the op gives none (contract §3.7, by kind since review L06): a <c>secret</c> is
    /// <c>restricted</c> (known by exactly the knowers its rows name: a reveal makes the party aware of it); a <c>rule</c>,
    /// <c>note</c> or <c>question</c> is <c>author</c> (the author's own machinery, never part of the story); every other
    /// kind takes <c>settings.default_visibility</c>, else <c>restricted</c> in a DM campaign (prep stays unseen until the
    /// party meets it) and <c>party</c> in a player campaign. A setting stricter than a kind's default still wins (author
    /// over restricted for a secret). Without the kind defaults a player campaign listed "The old king is Kerasorn ·
    /// secret · hidden" and a theory note naming him to the party, the table and every member.
    /// </summary>
    public string DefaultEntityVisibility(string kind)
    {
        var setting = Settings["default_visibility"] is JsonValue value && value.TryGetValue<string>(out var text) &&
                      V.Set.TryMatch(text, out var canonical)
            ? canonical
            : null;
        var byKind = kind switch
        {
            CampaignValues.Kinds.Secret => V.Restricted,
            CampaignValues.Kinds.Rule or CampaignValues.Kinds.Note or CampaignValues.Kinds.Question => V.Author,
            _ => null,
        };
        if (byKind is not null)
        {
            return setting is not null && Strictness(setting) > Strictness(byKind) ? setting : byKind;
        }

        return setting ?? (IsDmCampaign ? V.Restricted : V.Party);
    }

    // public < party < restricted < author: how few views a visibility admits.
    private static int Strictness(string visibility) => visibility switch
    {
        V.Public => 0,
        V.Party => 1,
        V.Restricted => 2,
        _ => 3,
    };

    /// <summary>A new relation's visibility when the op gives none: <c>author</c> in a DM campaign, <c>party</c> in a player campaign.</summary>
    public string DefaultRelationVisibility() => IsDmCampaign ? V.Author : V.Party;

    public bool IsDmCampaign => Campaign.Role == CampaignValues.Roles.Dm;

    /// <summary>
    /// Whether a knower kind reads through a non-author view: every kind but <c>author</c>, and <c>dm</c> only in a
    /// player campaign (in a DM campaign the DM is the author).
    /// </summary>
    public bool IsNonAuthorKnower(string knowerKind) =>
        knowerKind != CampaignValues.KnowerKinds.Author && !(knowerKind == CampaignValues.KnowerKinds.Dm && IsDmCampaign);

    // ---- register codes ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Every register code in this campaign, entities and facts, struck and soft-deleted included, as they stand in this
    /// transaction (so an earlier op of the batch counts, and a dry run gives the code the real run will).
    /// </summary>
    public IReadOnlyList<string> Codes() => CodeList();

    /// <summary>The next code of a letter (§3.1): 1 + the largest number with that letter, suffixes ignored.</summary>
    public string NextCode(string letter) => RegisterCodes.Next(letter, Codes());

    /// <summary>Records a code as used by this batch (the list is read once per batch, then kept up to date here).</summary>
    public void CodeAssigned(string code) => CodeList().Add(code);

    private List<string> CodeList() => _codes ??= Connection.Query<string>(
        "SELECT code FROM entity WHERE campaign_id = @campaignId AND code IS NOT NULL " +
        "UNION ALL SELECT code FROM fact WHERE campaign_id = @campaignId AND code IS NOT NULL",
        new { campaignId = Campaign.Id }, Transaction).ToList();

    /// <summary>The <c>e:</c>/<c>f:</c> handle holding <paramref name="code"/> (deleted included), or null.</summary>
    public string? CodeHolder(string code)
    {
        var entity = Connection.QueryFirstOrDefault<long?>(
            "SELECT seq FROM entity WHERE campaign_id = @campaignId AND code = @code", new { campaignId = Campaign.Id, code }, Transaction);
        if (entity is { } e)
        {
            return "e:" + e.ToString(CultureInfo.InvariantCulture);
        }

        var fact = Connection.QueryFirstOrDefault<long?>(
            "SELECT seq FROM fact WHERE campaign_id = @campaignId AND code = @code", new { campaignId = Campaign.Id, code }, Transaction);
        return fact is { } f ? "f:" + f.ToString(CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// The code an entity or fact ends up with (§3.1): an explicit code (refused when another row holds it), the next code
    /// of <paramref name="autoLetter"/>, the next F when it is (or becomes) proposed and has none, else what it has.
    /// A code never changes once assigned, so giving a different one for a row that has one is refused.
    /// </summary>
    /// <param name="currentCode">The row's code now (null for a new row).</param>
    /// <param name="self">
    /// The row's own <c>e:&lt;n&gt;</c> / <c>f:&lt;n&gt;</c> handle, when it exists: its own code is not "taken" (compared
    /// with <see cref="CodeHolder"/>, which gives the same form), and refusals print it. Never <c>kind:slug</c>: a slug
    /// can spell an author-only name, and whoever drives the client reads the refusal.
    /// </param>
    public string? AssignCode(string subject, string where, string? explicitCode, string? autoLetter, string canonStatus, string? currentCode, string? self)
    {
        if (explicitCode is not null)
        {
            var code = CampaignHandle.CanonicalCode(explicitCode.Trim());
            if (currentCode == code)
            {
                return code;
            }

            if (currentCode is not null)
            {
                throw Problem(subject, $"{where}: {self} is {currentCode}; a register code never changes once assigned (notes refer to it by number).");
            }

            if (CodeHolder(code) is { } holder && holder != self)
            {
                RegisterCodes.TryParse(code, out var letters, out _, out _);
                throw Problem(subject, $"{where}: {code} is taken (by {holder}); omit code to auto-number (next {NextCode(letters)}).");
            }

            CodeAssigned(code);
            return code;
        }

        if (autoLetter is not null)
        {
            if (currentCode is not null)
            {
                throw Problem(subject, $"{where}: {self} already has code {currentCode}; a register code never changes once assigned.");
            }

            var next = NextCode(autoLetter);
            CodeAssigned(next);
            return next;
        }

        if (currentCode is null && canonStatus == CampaignValues.CanonStatuses.Proposed)
        {
            var next = NextCode("F");
            CodeAssigned(next);
            return next;
        }

        return currentCode;
    }

    // ---- row writes -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Updates the given columns of a row and returns the ones that changed (for <c>data</c>/<c>settings</c>, each
    /// changed key as <c>data.&lt;key&gt;</c>); nothing is written or logged when nothing changed.
    /// </summary>
    public IReadOnlyList<string> Update(string table, IReadOnlyList<string> key, IReadOnlyDictionary<string, object?> changes, string action)
    {
        var meta = CampaignTables.Get(table);
        var current = Recorder.Read(table, key) ??
                      throw new InvalidOperationException($"No {table} row {meta.TargetId(key)} to update.");
        var changed = new List<string>();
        foreach (var (name, raw) in changes)
        {
            var column = meta.Column(name);
            var value = ChangeValues.Normalize(meta, column, raw);
            if (ChangeValues.Same(column, current[name], value))
            {
                continue;
            }

            if (column.LoggedPerKey)
            {
                changed.AddRange(ChangedKeys(current[name] as string, value as string).Select(k => $"{name}.{k}"));
            }
            else
            {
                changed.Add(name);
            }
        }

        if (changed.Count > 0)
        {
            Recorder.Update(table, key, changes, action);
        }

        return changed;
    }

    /// <inheritdoc cref="Update(string, IReadOnlyList{string}, IReadOnlyDictionary{string, object?}, string)"/>
    public IReadOnlyList<string> Update(string table, string id, IReadOnlyDictionary<string, object?> changes, string action) =>
        Update(table, [id], changes, action);

    /// <summary>
    /// An RFC 7396 merge patch of <paramref name="patch"/> (null value: remove the key) over <paramref name="current"/>,
    /// refused when the result is over <see cref="CampaignLimits.MaxDataLength"/> characters.
    /// </summary>
    public static JsonObject MergeData(string subject, string where, string field, string? current, IReadOnlyDictionary<string, System.Text.Json.JsonElement> patch)
    {
        var document = (current is null ? null : JsonNode.Parse(current)) as JsonObject ?? new JsonObject();
        var patchObject = new JsonObject();
        foreach (var (key, value) in patch)
        {
            patchObject[key] = value.ValueKind == System.Text.Json.JsonValueKind.Null ? null : JsonNode.Parse(value.GetRawText());
        }

        var merged = ChangeRecorder.MergePatch(document, patchObject) as JsonObject ?? new JsonObject();
        var length = CampaignLogJson.Serialize(merged).Length;
        if (length > CampaignLimits.MaxDataLength)
        {
            throw Problem(subject,
                $"{where}: {field} would be {Number(length)} characters as JSON after the merge; at most {Number(CampaignLimits.MaxDataLength)}. Remove keys (set them to null) or shorten values.");
        }

        return merged;
    }

    // ---- messages ---------------------------------------------------------------------------------------------------

    /// <summary>The one exception a refusal throws: "Invalid ops: ops item 3 (…): …", the validator's own wording.</summary>
    public static DndInputException Problem(string subject, string message) => DslProblems.Exception([message], subject);

    public static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>At most 60 characters of the caller's own input, for a message.</summary>
    public static string Echo(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim().ReplaceLineEndings(" ");
        return trimmed.Length <= 60 ? trimmed : trimmed[..60] + "…";
    }

    private static IEnumerable<string> ChangedKeys(string? oldText, string? newText)
    {
        var before = (oldText is null ? null : JsonNode.Parse(oldText)) as JsonObject ?? new JsonObject();
        var after = (newText is null ? null : JsonNode.Parse(newText)) as JsonObject ?? new JsonObject();
        foreach (var key in before.Select(p => p.Key).Concat(after.Select(p => p.Key).Where(k => !before.ContainsKey(k))))
        {
            var had = before.TryGetPropertyValue(key, out var oldValue);
            var has = after.TryGetPropertyValue(key, out var newValue);
            if (!(had && has && JsonNode.DeepEquals(oldValue, newValue)))
            {
                yield return key;
            }
        }
    }
}
