using System.ComponentModel;
using System.Globalization;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using DndMcp.Formatting.Campaign;
using DndMcp.Hosting;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Repository.Campaign.Write;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DndMcp.Tools;

/// <summary>
/// <c>campaign_history</c>: what changed and when (<c>since</c>, <c>entity</c>, <c>batch</c>), entries as they stood after a
/// session (<c>as_of</c>), and undo of one batch (<c>undo</c>). Thin by design: <see cref="HistoryReader"/> and
/// <see cref="HistoryWriter.Undo"/> do the work, <see cref="HistoryMarkdown"/> and <see cref="EntityMarkdown"/> render.
///
/// <para>
/// <b>Author only, so no perspective.</b> The change log records every edit, secret text and author aliases included; no
/// other view is ever given history (the leak rule). Taking a perspective here would suggest a filtered history exists.
/// </para>
/// <para>
/// <b>Undo goes through the write path's <see cref="HistoryWriter"/></b>, never around it: it reverses exactly one batch as
/// a new batch (itself undoable, which is redo), refuses when later batches build on it (listing them, newest first) and
/// re-derives secret statuses in the same batch, whose <see cref="UndoWriteResult.Consequences"/> are rendered. With
/// <c>dry_run</c> it runs everything and rolls back, so the model can show the user what an undo will reverse before it
/// happens; the result shows the reversed batch as history renders it (old and new values by handle), since the engine's
/// own list names only the rows. An undo that removes or renames a campaign changes the <c>campaign://</c> resource list,
/// so the list is compared before and after and the client told when it changed.
/// </para>
/// <para>
/// <b><c>entity</c> resolves its ref here first</b>, so a name typed where a handle belongs ("The Old King") is refused
/// about <c>ref</c>, the argument the call sent, with the handles it is close to, rather than with the history reader's
/// wording about a <c>targets</c> list the call never had. The tool is the author's, so the suggestions may draw on every
/// entry.
/// </para>
/// <para>
/// Hints: not read-only and destructive (undo deletes rows a batch created), not idempotent (a second undo of the same
/// batch is refused; an undo of the undo is a redo), closed-world.
/// </para>
/// </summary>
public sealed class CampaignHistoryTools
{
    public const string ActionSince = "since";
    public const string ActionEntity = "entity";
    public const string ActionAsOf = "as_of";
    public const string ActionBatch = "batch";
    public const string ActionUndo = "undo";

    /// <summary>The actions, in the order the description lists them.</summary>
    public static readonly DslValueSet Actions = new("action", [ActionSince, ActionEntity, ActionAsOf, ActionBatch, ActionUndo]);

    private const string Tool = "campaign_history";

    // The description's Example runs as written on any campaign (review CR02): it was an undo with an elided batch id, a
    // template refused as "not a batch id" everywhere. The undo template stays the undo's own: its line in the action list
    // ("Its shape: …") and its refusal ("action \"undo\" needs batch_id. Example: …") give it.
    private const string Example = "{\"action\": \"since\", \"limit\": 5}";

    private const string UndoExample = "{\"action\": \"undo\", \"batch_id\": \"0199a1b2-…\", \"dry_run\": true}";

    private static readonly IReadOnlyDictionary<string, CampaignToolArguments.ActionShape> Shapes =
        new Dictionary<string, CampaignToolArguments.ActionShape>(StringComparer.Ordinal)
        {
            [ActionSince] = new(["campaign", "since", "session", "targets", "limit", "cursor"], [], "{\"action\": \"since\", \"session\": 12}"),
            [ActionEntity] = new(["campaign", "ref", "limit", "cursor"], ["ref"], "{\"action\": \"entity\", \"ref\": \"character:iron-guts\"}"),
            [ActionAsOf] = new(["campaign", "session", "refs", "detail"], ["session", "refs"],
                "{\"action\": \"as_of\", \"session\": 3, \"refs\": [\"character:old-king\"]}"),
            [ActionBatch] = new(["campaign", "batch_id"], ["batch_id"], "{\"action\": \"batch\", \"batch_id\": \"0199a1b2-…\"}"),
            [ActionUndo] = new(["campaign", "batch_id", "dry_run", "reason", "session"], ["batch_id"], UndoExample),
        };

    private readonly CampaignService _campaigns;
    private readonly ILogger<CampaignHistoryTools> _logger;

    public CampaignHistoryTools(CampaignService campaigns, ILogger<CampaignHistoryTools> logger)
    {
        _campaigns = campaigns;
        _logger = logger;
    }

    // Not read-only and destructive: undo deletes what a batch created. Not idempotent: undoing twice is refused, and an
    // undo of an undo is a redo. Closed-world: campaigns.db only.
    [McpServerTool(Name = "campaign_history", Title = "Campaign history and undo", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "A campaign's change history (the author's view: it includes every edit, secrets too) and undo. Every call that " +
        "writes is one batch with a batch id; undo reverses exactly one batch.\n" +
        "- action \"since\": the batches in order, oldest first; optional since (a UTC date, \"2026-09-19\", or a timestamp), " +
        "session (from the first change of that session number on; give since or session, not both), targets (handles: only " +
        "changes to these entries), limit, cursor. The \"what changed since\" feed.\n" +
        "- action \"entity\" (required: ref): the batches that changed one entry (an entity or fact handle), newest first; " +
        "limit, cursor.\n" +
        "- action \"as_of\" (required: session, refs): entries (1-10 handles) as they stood at the end of that session; " +
        "detail \"full\" for whole bodies.\n" +
        "- action \"batch\" (required: batch_id): one batch with every change.\n" +
        "- action \"undo\" (required: batch_id, in full as a result printed it: batches made within a minute share their first 8 " +
        "characters): reverse that batch as " +
        "a new batch (undo that to redo). Refused, with the batches to undo first, when later changes build on it. dry_run " +
        "true shows what would be reversed and changes nothing: use it first and ask before undoing; reason and session (the " +
        "session number it belongs to; default the live one) are kept with the undo. Its shape: " + UndoExample + "\n" +
        "- limit: batches per page, 1-50 (default 15); cursor: the next-page cursor a result gives.\n" +
        "- campaign: the campaign's slug (default: the current campaign).\n" +
        "Example: " + Example)]
    public async Task<string> History(
        [Description("What to do: \"since\", \"entity\", \"as_of\", \"batch\" or \"undo\".")] string action,
        [Description("since: a UTC date or timestamp, e.g. \"2026-09-19\" or \"2026-09-19T20:00:00Z\".")] string? since = null,
        [Description("since: from this session number on. as_of: the session to read as of. undo: the session the undo belongs to.")] int? session = null,
        [Description("since: only changes to these handles, e.g. [\"character:iron-guts\", \"f:12\"].")] string[]? targets = null,
        [Description("entity: the entity or fact handle, e.g. \"character:iron-guts\" or \"f:12\".")] string? @ref = null,
        [Description("as_of: 1-10 handles, e.g. [\"character:old-king\"].")] string[]? refs = null,
        [Description("as_of: \"concise\" (default) or \"full\".")] string? detail = null,
        [Description("batch/undo: the batch id a write's result or this history printed, in full (batches made within a minute share their first 8 characters).")]
        [AIParameterName("batch_id")] string? batchId = null,
        [Description("undo: true shows what would be reversed and writes nothing. Default false.")][AIParameterName("dry_run")] bool? dryRun = null,
        [Description("undo: why, kept with the undo batch.")] string? reason = null,
        [Description("since/entity: batches per page, 1-50. Default 15.")] int? limit = null,
        [Description("since/entity: the cursor a previous page gave.")] string? cursor = null,
        [Description("The campaign's slug, e.g. \"belmakor\". Omit for the current campaign.")] string? campaign = null,
        RequestContext<CallToolRequestParams>? context = null,
        CancellationToken cancellationToken = default)
    {
        var chosen = CampaignToolArguments.Action(Tool, action, Actions, Example);
        var given = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["campaign"] = Given(campaign),
            ["since"] = Given(since),
            ["session"] = session is not null,
            ["targets"] = targets is { Length: > 0 },
            ["ref"] = Given(@ref),
            ["refs"] = refs is { Length: > 0 },
            ["detail"] = Given(detail),
            ["batch_id"] = Given(batchId),
            ["dry_run"] = dryRun is not null,
            ["reason"] = Given(reason),
            ["limit"] = limit is not null,
            ["cursor"] = Given(cursor),
        };
        CampaignToolArguments.Check(Tool, chosen, given, Shapes[chosen]);
        var full = chosen == ActionAsOf && CampaignGetTools.Detail(detail);
        var row = _campaigns.Resolve(Given(campaign) ? campaign : null);
        var reader = new HistoryReader(_campaigns.Database);
        switch (chosen)
        {
            case ActionSince:
            {
                var page = reader.Since(row, Given(since) ? since!.Trim() : null, session, targets is { Length: > 0 } ? targets : null, limit,
                    Given(cursor) ? cursor : null);
                var from = Given(since) ? $" since {CampaignMarkdownText.Echo(since!.Trim())}"
                    : session is { } n ? $" since session {n.ToString(CultureInfo.InvariantCulture)}"
                    : string.Empty;
                return HistoryMarkdown.Page(page, $"History of {row.Slug}{from}",
                    Given(since) || session is not null || targets is { Length: > 0 } ? "No changes match." : "No changes yet.", row.Slug);
            }

            case ActionEntity:
            {
                var handle = @ref!.Trim();
                CheckRef(row, handle);
                var page = reader.Entity(row, handle, limit, Given(cursor) ? cursor : null);
                return HistoryMarkdown.Page(page, $"History of {CampaignMarkdownText.Echo(handle)} in {row.Slug} (newest first)",
                    "No logged changes to it.", row.Slug);
            }

            case ActionAsOf:
            {
                var result = reader.AsOf(row, session!.Value, refs!);
                return EntityMarkdown.Format(result, CampaignView.Author, row.Slug, full,
                    $"{row.Slug} as of the end of session {session.Value.ToString(CultureInfo.InvariantCulture)}");
            }

            case ActionBatch:
                return HistoryMarkdown.Batch(reader.Batch(row, batchId!.Trim()), row.Slug);

            default:
            {
                // The batch as history shows it (values by handle), read BEFORE the undo: rendered afterwards, an entity the
                // batch created and the undo deleted would print as "(deleted entity)". The same resolver finds the batch
                // for both, so a missing or ambiguous id is refused here with the undo's own wording, before anything runs.
                var reversed = reader.Batch(row, batchId!.Trim());
                var before = CampaignListKey();
                var result = new HistoryWriter(_campaigns.Database).Undo(row, reversed.BatchId,
                    WriteContext.For(session, Given(reason) ? reason : null, dryRun ?? false));
                if (!result.DryRun && CampaignListKey() != before)
                {
                    await CampaignTools.AnnounceResourceListChangedAsync(context, _logger, cancellationToken);
                }

                return HistoryMarkdown.Undo(result, reversed, row.Slug);
            }
        }
    }

    /// <summary>
    /// Refuses an <c>entity</c> ref that names nothing in the campaign (deleted entries count: their history is still
    /// there), naming <c>ref</c> and the handles it is close to (class summary).
    /// </summary>
    /// <exception cref="DndInputException">The ref does not parse or names nothing.</exception>
    private void CheckRef(CampaignRow row, string text)
    {
        using var connection = _campaigns.Database.TryOpenExisting();
        if (connection is null)
        {
            return;
        }

        if (!CampaignHandle.TryParse(text, out var handle, out var problem))
        {
            throw new DndInputException($"ref: {problem} Example: {Shapes[ActionEntity].Example}");
        }

        var resolver = new HandleResolver(connection, row.Id);
        if (resolver.TryEntityOrFact(handle, includeDeleted: true) is not (null, null))
        {
            return;
        }

        var colon = text.LastIndexOf(':');
        var suggestions = resolver.Suggest(null, (colon >= 0 ? text[(colon + 1)..] : text).Replace('-', ' '), (Func<EntityRow, bool>)(_ => true));
        throw new DndInputException(
            $"ref \"{CampaignMarkdownText.Echo(text)}\": nothing in this campaign has that handle." + (suggestions.Count == 0
                ? " campaign_search finds entities and facts by name."
                : $" Did you mean {string.Join(", ", suggestions)}?"));
    }

    // What the campaign:// resource list is built from (slugs and names), to tell whether an undo changed it.
    private string CampaignListKey() => string.Join("\n", _campaigns.Store.List().Select(c => c.Slug + "\t" + c.Name));

    private static bool Given(string? text) => !string.IsNullOrWhiteSpace(text);
}
