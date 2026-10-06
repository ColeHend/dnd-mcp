using System.ComponentModel;
using System.Text.Json;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using DndMcp.Formatting.Campaign;
using DndMcp.Hosting;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Repository.Campaign.Write;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DndMcp.Tools;

/// <summary>
/// <c>campaign</c>: the campaigns themselves: list them, create one, choose the one later calls use, change its record,
/// read its record or its state of play. Thin by design: check the arguments the action takes, call
/// <see cref="CampaignService"/> and the repository's <see cref="CampaignStore"/> / <see cref="CampaignSummary"/>, render
/// with <see cref="CampaignMarkdown"/> and <see cref="SummaryMarkdown"/>.
///
/// <para>
/// <b>create and use set this process's current campaign</b> (<see cref="CampaignService.Use"/>), not only the persisted
/// active one: two Claude sessions on two campaigns must each keep their own, and a create that left the previous campaign
/// current would file the model's next write into the wrong campaign with no error at all.
/// </para>
/// <para>
/// <b>Once create has committed, the call succeeds.</b> The campaign becomes this process's current one in memory (which
/// cannot fail) before the separate write that saves it as the persisted active campaign; if that write fails (the file
/// locked by another process past the busy timeout, the store gone), the result says so and the call still reports the
/// campaign it created. An error there would read as "the create failed" and invite a second create, which would make a
/// second campaign (<c>sky-2</c>).
/// </para>
/// <para>
/// <b>Arguments an action does not take are refused</b>, with what it does take. The tool is one flat parameter list
/// (the SDK has no per-action schemas), so the guard cannot know that <c>perspective</c> means nothing to <c>create</c>;
/// silently ignoring it would let a model believe a filtered read happened. Null and blank count as not given, since
/// models send null for "not this one".
/// </para>
/// <para>
/// <b>resources/list changes are announced.</b> A created or renamed campaign changes the <c>campaign://</c> entries
/// <see cref="Resources.CampaignResources"/> lists, and Claude Code only re-fetches the list on
/// <c>notifications/resources/list_changed</c>; without it the new campaign would be missing from <c>@</c> mentions until a
/// restart. A failed notification is logged and never fails the call: the write has happened, and an error would invite
/// the model to create the campaign again.
/// </para>
/// <para>
/// Hints: not read-only (create, update and use write campaigns.db), not destructive (nothing is deleted; update's
/// changes are logged and undoable), not idempotent (a second create makes a second campaign), closed-world.
/// </para>
/// </summary>
public sealed class CampaignTools
{
    public const string ActionList = "list";
    public const string ActionGet = "get";
    public const string ActionCreate = "create";
    public const string ActionUpdate = "update";
    public const string ActionUse = "use";
    public const string ActionSummary = "summary";

    /// <summary>The actions, in the order the description lists them.</summary>
    public static readonly DslValueSet Actions = new("action", [ActionList, ActionSummary, ActionGet, ActionCreate, ActionUpdate, ActionUse]);

    private const string Tool = "campaign";

    private const string Example =
        "{\"action\": \"create\", \"name\": \"Belmakor\", \"role\": \"player\", \"ruleset\": \"2014\", \"my_character\": \"Belmakor\"}";

    private static readonly IReadOnlyDictionary<string, CampaignToolArguments.ActionShape> Shapes =
        new Dictionary<string, CampaignToolArguments.ActionShape>(StringComparer.Ordinal)
        {
            [ActionList] = new([], [], "{\"action\": \"list\"}"),
            [ActionSummary] = new(["campaign", "perspective"], [], "{\"action\": \"summary\", \"perspective\": \"party\"}"),
            [ActionGet] = new(["campaign", "perspective"], [], "{\"action\": \"get\", \"campaign\": \"belmakor\"}"),
            [ActionCreate] = new(
                ["name", "role", "ruleset", "dm_name", "slug", "settings", "party_name", "my_character", "reason", "dry_run"],
                ["name", "role", "ruleset"], Example),
            [ActionUpdate] = new(
                ["campaign", "name", "status", "ruleset", "dm_name", "settings", "summary_md", "current_location", "current_ingame", "my_character",
                    "reason", "session", "dry_run"],
                [], "{\"action\": \"update\", \"current_ingame\": \"3rd of Frostmoon\", \"campaign\": \"belmakor\"}"),
            [ActionUse] = new(["campaign"], ["campaign"], "{\"action\": \"use\", \"campaign\": \"belmakor\"}"),
        };

    private static readonly string[] UpdateFields =
        ["name", "status", "ruleset", "dm_name", "settings", "summary_md", "current_location", "current_ingame", "my_character"];

    private readonly CampaignService _campaigns;
    private readonly ILogger<CampaignTools> _logger;

    public CampaignTools(CampaignService campaigns, ILogger<CampaignTools> logger)
    {
        _campaigns = campaigns;
        _logger = logger;
    }

    // Not read-only (create/update/use write), not destructive (nothing is deleted), not idempotent (create twice = two
    // campaigns), closed-world (campaigns.db only).
    [McpServerTool(Name = "campaign", Title = "Campaigns", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Your D&D campaigns: list them, create one, choose the one other campaign tools use, change its record, or see where " +
        "things stand. Every campaign tool takes campaign (a slug); omitted, it is this session's current one (set by use, create, " +
        "or a campaign_session start or end that names its campaign), else the one last chosen with use or create, else the only one.\n" +
        "- action \"list\": every campaign, with your character's handle in a player campaign.\n" +
        "- action \"summary\": campaign, perspective. The state of play: party, place, last recap, open quests and threads, " +
        "clocks; the author also sees secrets, inventions (accept / strike) and changes since the last session.\n" +
        "- action \"get\": campaign, perspective. The campaign's record: role, ruleset, DM, party, settings.\n" +
        "- action \"create\" (required: name, role, ruleset): role \"player\" (you play in it) or \"dm\" (you run it); ruleset " +
        "\"2014\", \"2024\" or \"mixed\"; optional dm_name, slug, party_name, my_character (a player campaign: your character's " +
        "name; it joins the party), settings, reason, dry_run. The new campaign becomes the current one.\n" +
        "- action \"update\": campaign, then any of name, status (active, hiatus, ended), ruleset, dm_name, settings (a merge " +
        "patch: null removes a key), summary_md, current_location (a location handle), current_ingame (the in-game date), " +
        "my_character (a character handle); reason, session (the session number it happened in), dry_run (show, write nothing).\n" +
        "- action \"use\" (required: campaign): make it the current campaign.\n" +
        "settings with meaning: effective_level_offset (-10 to 10, encounter_difficulty's default) and default_visibility (public, " +
        "party, restricted or author, for new entries). perspective: \"author\" (default: everything), \"dm\", \"table\", " +
        "\"party\", \"public\" or \"character:<slug>\"; any other view shows only what it knows, by the names it knows. Writes " +
        "print a batch id; campaign_history undo reverses it.\n" +
        "Example: " + Example)]
    public async Task<string> Campaign(
        [Description("What to do: \"list\", \"summary\", \"get\", \"create\", \"update\" or \"use\".")] string action,
        [Description("The campaign's slug, e.g. \"belmakor\" (or its exact name). Omit for the current campaign.")] string? campaign = null,
        [Description("create: the campaign's name, e.g. \"Belmakor\". update: a new name (the slug stays).")] string? name = null,
        [Description("create: \"player\" (you play in it) or \"dm\" (you run it).")] string? role = null,
        [Description("create/update: \"2014\", \"2024\" or \"mixed\".")] string? ruleset = null,
        [Description("create/update: the DM's name, e.g. \"Sam\". update: \"\" clears it.")][AIParameterName("dm_name")] string? dmName = null,
        [Description("create: the slug to use, lower-case words joined by hyphens, e.g. \"sky-world\". Default: made from the name.")] string? slug = null,
        [Description("create/update: flags, e.g. {\"effective_level_offset\": 1, \"default_visibility\": \"party\"}. update merges; null removes a key.")]
        Dictionary<string, JsonElement>? settings = null,
        [Description("create: the party's name. Default \"The Party\".")][AIParameterName("party_name")] string? partyName = null,
        [Description("create (player campaigns): your character's name, e.g. \"Belmakor\" (its handle is then character:belmakor). update: a character handle.")]
        [AIParameterName("my_character")] string? myCharacter = null,
        [Description("update: \"active\", \"hiatus\" or \"ended\".")] string? status = null,
        [Description("update: the campaign's summary text (markdown).")][AIParameterName("summary_md")] string? summaryMd = null,
        [Description("update: where the party is now, a location handle, e.g. \"location:flotsam\"; \"\" clears it.")]
        [AIParameterName("current_location")] string? currentLocation = null,
        [Description("update: the in-game date now, e.g. \"3rd of Frostmoon, 1492\"; \"\" clears it.")]
        [AIParameterName("current_ingame")] string? currentIngame = null,
        [Description("summary/get: whose view: \"author\" (default), \"dm\", \"table\", \"party\", \"public\" or \"character:<slug>\".")]
        string? perspective = null,
        [Description("create/update: why, kept in the change history, e.g. \"session 12 recap\".")] string? reason = null,
        [Description("update: the session number the change happened in, e.g. 12. Default: the live session, if any.")] int? session = null,
        [Description("create/update: true shows what would change and writes nothing. Default false.")][AIParameterName("dry_run")] bool? dryRun = null,
        RequestContext<CallToolRequestParams>? context = null,
        CancellationToken cancellationToken = default)
    {
        var chosen = CampaignToolArguments.Action(Tool, action, Actions, Example);
        var given = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["campaign"] = Given(campaign),
            ["name"] = Given(name),
            ["role"] = Given(role),
            ["ruleset"] = Given(ruleset),
            ["dm_name"] = dmName is not null && (chosen == ActionUpdate || Given(dmName)),
            ["slug"] = Given(slug),
            ["settings"] = settings is { Count: > 0 },
            ["party_name"] = Given(partyName),
            ["my_character"] = Given(myCharacter),
            ["status"] = Given(status),
            ["summary_md"] = summaryMd is not null && (chosen == ActionUpdate || Given(summaryMd)),
            ["current_location"] = currentLocation is not null && (chosen == ActionUpdate || Given(currentLocation)),
            ["current_ingame"] = currentIngame is not null && (chosen == ActionUpdate || Given(currentIngame)),
            ["perspective"] = Given(perspective),
            ["reason"] = Given(reason),
            ["session"] = session is not null,
            ["dry_run"] = dryRun is not null,
        };
        CampaignToolArguments.Check(Tool, chosen, given, Shapes[chosen]);

        switch (chosen)
        {
            case ActionList:
                return List();
            case ActionSummary:
            {
                var row = _campaigns.Resolve(campaign);
                var view = CampaignView.Resolve(_campaigns.Database, row, perspective);
                return SummaryMarkdown.Format(new CampaignSummary(_campaigns.Database).Build(row, view.Perspective), view);
            }

            case ActionGet:
            {
                var row = _campaigns.Resolve(campaign);
                var view = CampaignView.Resolve(_campaigns.Database, row, perspective);
                var links = new CampaignSummary(_campaigns.Database).Build(row, view.Perspective);
                return CampaignMarkdown.Get(row, links, view, row.Id == _campaigns.DefaultCampaignId(_campaigns.Store.List()));
            }

            case ActionCreate:
            {
                var result = _campaigns.Store.Create(name!, role!, ruleset!, Blank(dmName), Blank(slug), settings, Blank(partyName), Blank(myCharacter),
                    WriteContext.For(null, Blank(reason), dryRun ?? false));
                var savedAsActive = true;
                if (!result.DryRun)
                {
                    _campaigns.SetCurrent(result.Campaign.Id);
                    savedAsActive = TrySaveActive(result.Campaign);
                    await AnnounceResourceListChangedAsync(context, _logger, cancellationToken);
                }

                return CampaignMarkdown.Created(result, savedAsActive);
            }

            case ActionUpdate:
            {
                if (!UpdateFields.Any(f => given[f]))
                {
                    throw new DndInputException(
                        $"Invalid campaign call: action \"update\" needs at least one of {string.Join(", ", UpdateFields)} to change. " +
                        $"Example: {Shapes[ActionUpdate].Example}");
                }

                var row = _campaigns.Resolve(campaign);
                var result = _campaigns.Store.Update(row, new CampaignUpdate
                {
                    Name = Blank(name),
                    Status = Blank(status),
                    Ruleset = Blank(ruleset),
                    DmName = dmName,
                    Settings = settings is { Count: > 0 } ? settings : null,
                    SummaryMd = summaryMd,
                    CurrentLocation = currentLocation,
                    CurrentIngame = currentIngame,
                    MyCharacter = Blank(myCharacter),
                }, WriteContext.For(session, Blank(reason), dryRun ?? false));
                if (!result.DryRun && result.ChangedFields.Contains("name"))
                {
                    await AnnounceResourceListChangedAsync(context, _logger, cancellationToken);
                }

                return CampaignMarkdown.Updated(result);
            }

            default:
                return CampaignMarkdown.Used(_campaigns.Use(campaign));
        }
    }

    // Saves a just-created campaign as the persisted active one; false (and a logged warning) when that write fails (class summary).
    private bool TrySaveActive(CampaignRow campaign)
    {
        try
        {
            _campaigns.Store.SetActive(campaign.Id);
            return true;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or CampaignStoreUnavailableException or DndInputException)
        {
            _logger.LogWarning(ex, "Created campaign {Slug} but could not save it as the active campaign; it is current in this session only.", campaign.Slug);
            return false;
        }
    }

    private string List()
    {
        var campaigns = _campaigns.Store.List();
        return CampaignMarkdown.List(campaigns, _campaigns.DefaultCampaignId(campaigns), PlayerCharacters(campaigns));
    }

    /// <summary>
    /// Each player campaign's character handle (its <c>my_character</c>), by campaign id, as <c>get</c> shows it to the
    /// author (<c>kind:slug</c>; a deleted character is left out, as there). One query for the whole list, of the entity rows
    /// the campaign rows name and nothing else: list is the call a user makes when something is wrong, so it must not
    /// depend on reading anything it does not print. Building each campaign's author summary for this (as it first did)
    /// read every entity, relation, session and clock of every player campaign per list, and a damaged page in any of
    /// those tables failed the whole list.
    /// </summary>
    private IReadOnlyDictionary<string, string> PlayerCharacters(IReadOnlyList<CampaignRow> campaigns)
    {
        var characters = new Dictionary<string, string>(StringComparer.Ordinal);
        var wanted = campaigns.Where(c => c.MyCharacterId is not null).ToList();
        if (wanted.Count == 0)
        {
            return characters;
        }

        using var connection = _campaigns.Database.TryOpenExisting();
        if (connection is null)
        {
            return characters;
        }

        using var command = connection.CreateCommand();
        var parameters = new List<string>();
        for (var i = 0; i < wanted.Count; i++)
        {
            parameters.Add("$c" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue(parameters[i], wanted[i].MyCharacterId);
        }

        command.CommandText = $"SELECT id, campaign_id, kind, slug FROM entity WHERE id IN ({string.Join(", ", parameters)}) AND deleted_at IS NULL";
        var handles = new Dictionary<string, (string CampaignId, string Handle)>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                handles[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2) + ":" + reader.GetString(3));
            }
        }

        // The character must be the campaign's own entity, as the summary reader (which reads one campaign) requires.
        foreach (var campaign in wanted)
        {
            if (handles.TryGetValue(campaign.MyCharacterId!, out var character) && character.CampaignId == campaign.Id)
            {
                characters[campaign.Id] = character.Handle;
            }
        }

        return characters;
    }

    /// <summary>
    /// Tells the client that resources/list changed (a campaign was created, renamed or, through undo, removed). Logged and
    /// swallowed on failure: the write it follows has happened, and an error here would read as "the create failed".
    /// </summary>
    internal static async Task AnnounceResourceListChangedAsync(RequestContext<CallToolRequestParams>? context, ILogger logger, CancellationToken cancellationToken)
    {
        if (context?.Server is not { } server)
        {
            return;
        }

        try
        {
            await server.SendNotificationAsync(NotificationMethods.ResourceListChangedNotification, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not send notifications/resources/list_changed; the client's campaign resource list may be stale until it re-lists.");
        }
    }

    private static bool Given(string? text) => !string.IsNullOrWhiteSpace(text);

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}

/// <summary>
/// The action-argument checks the campaign tools share: an unknown action, arguments the action does not take, and
/// required ones missing, reported together as one <see cref="DndInputException"/> (the <see cref="DslProblems"/> form),
/// each naming what the action does take and giving an example.
///
/// <para>
/// Why one table per tool (<see cref="ActionShape"/>) rather than checks spread through each action: the description,
/// the refusals and the dispatch must agree on what each action takes, and a table is the one place a new argument has
/// to be added before any action accepts it.
/// </para>
/// </summary>
internal static class CampaignToolArguments
{
    /// <summary>What one action takes and requires (wire names), and an example call.</summary>
    public sealed record ActionShape(IReadOnlyList<string> Takes, IReadOnlyList<string> Required, string Example);

    /// <summary>The canonical action (forgiving spelling), or an exception listing the actions.</summary>
    public static string Action(string tool, string? action, DslValueSet actions, string example) =>
        actions.TryMatch(action, out var canonical)
            ? canonical
            : throw new DndInputException(
                $"Invalid {tool} call: action \"{CampaignMarkdownText.Echo(action)}\" is not one of {actions.List}. Example: {example}");

    /// <summary>
    /// Refuses arguments <paramref name="action"/> does not take and reports required ones missing (all at once).
    /// <paramref name="given"/> maps every argument's wire name to whether the call gave it (null and blank count as not given).
    /// </summary>
    public static void Check(string tool, string action, IReadOnlyDictionary<string, bool> given, ActionShape shape)
    {
        var problems = new List<string>();
        var extra = given.Where(g => g.Value && !shape.Takes.Contains(g.Key)).Select(g => g.Key).ToList();
        if (extra.Count > 0)
        {
            problems.Add($"action \"{action}\" does not take {string.Join(", ", extra)}; " +
                         (shape.Takes.Count == 0 ? $"{action} takes no other arguments." : $"{action} takes {string.Join(", ", shape.Takes)}."));
        }

        var missing = shape.Required.Where(r => !given.TryGetValue(r, out var isGiven) || !isGiven).ToList();
        if (missing.Count > 0)
        {
            problems.Add($"action \"{action}\" needs {string.Join(", ", missing)}. Example: {shape.Example}");
        }

        DslProblems.ThrowIfAny(problems, tool + " call");
    }
}
