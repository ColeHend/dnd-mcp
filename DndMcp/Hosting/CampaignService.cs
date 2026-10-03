using System.Diagnostics.CodeAnalysis;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Repository.Campaign.Write;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DndMcp.Hosting;

/// <summary>
/// The host's one handle on campaigns.db: the database (opened lazily), the campaign this process is working on, and the
/// ambient defaults other tools take from it.
///
/// <para>
/// <b>Lazy, never at startup.</b> Nothing here touches the file until a campaign tool (or a default lookup) asks.
/// Claude Code's <c>initialize</c> must never wait on a migration or a backup, and a user who only rolls dice or reads
/// rules must never find a campaigns.db created for them. <see cref="CampaignDatabase"/> itself does no I/O until its
/// first read or write, which is when it creates, probes and migrates.
/// </para>
/// <para>
/// <b>The process's current campaign.</b> <c>campaign use</c> and <c>campaign create</c> set it here as well as in
/// <c>app_state.active_campaign</c>. Two Claude sessions, one per campaign, each keep working on their own campaign
/// even though the persisted "active" campaign is shared by every process on the machine; the persisted one is only the
/// starting point for a session that has chosen nothing yet (contract §3.7).
/// </para>
/// <para>
/// <b>Defaults fail soft, but not silently.</b> <see cref="ReadDefaults"/> feeds the edition and level-offset defaults of
/// rules_search, rules_get, encounter_difficulty and the balance tools. A damaged, locked or unreadable campaigns.db must
/// not break those tools, so a failure there is "no campaign" plus a logged warning; the campaign tools, which need the
/// database, still report the real problem. But it is told apart from "no campaign": the result then says the campaign's
/// settings could not be read and what was used instead (<see cref="CampaignDefaultsReading.UnreadablePath"/>), because
/// otherwise a 2014 campaign's rules lookup quietly answers with 2024 rules, and nothing in the result says why.
/// </para>
/// </summary>
public sealed class CampaignService : IDisposable
{
    private readonly DndMcpServerOptions _options;
    private readonly ILogger<CampaignService> _logger;
    private readonly ILogger<CampaignDatabase> _databaseLogger;
    private readonly Lock _lock = new();
    private CampaignDatabase? _database;
    private volatile string? _currentCampaignId;

    public CampaignService(DndMcpServerOptions options, ILogger<CampaignService> logger, ILogger<CampaignDatabase> databaseLogger)
    {
        _options = options;
        _logger = logger;
        _databaseLogger = databaseLogger;
    }

    /// <summary>
    /// campaigns.db for this process, created on first use at <see cref="DndMcpServerOptions.ResolveCampaignDatabasePath"/>.
    /// Constructing it does no I/O.
    /// </summary>
    /// <exception cref="CampaignStoreUnavailableException">No path can be decided (no home directory and no override).</exception>
    public CampaignDatabase Database
    {
        get
        {
            if (_database is { } existing)
            {
                return existing;
            }

            lock (_lock)
            {
                if (_database is null)
                {
                    string path;
                    try
                    {
                        path = _options.ResolveCampaignDatabasePath();
                    }
                    catch (InvalidOperationException ex)
                    {
                        throw new CampaignStoreUnavailableException(
                            "dnd-mcp cannot decide where campaigns.db lives: there is no home directory and no DND_MCP_DATA_DIR " +
                            $"or DND_MCP_DB set. Set DND_MCP_DATA_DIR to an absolute directory. ({ex.Message})");
                    }

                    _database = new CampaignDatabase(path, _options.Time, _databaseLogger);
                }

                return _database;
            }
        }
    }

    /// <summary>
    /// The user-facing <see cref="CampaignStoreUnavailableException"/> for a SQLite failure a person can fix that a statement
    /// on this process's campaigns.db raised (<see cref="CampaignDatabase.TryMapUnavailable"/>: locked by another process
    /// past busy_timeout, a damaged page, a file that is not a database, a disk I/O error, a full disk, a read-only or
    /// unopenable file); false for any other code, which is a bug and must stay the SDK's generic error, and before this
    /// process has resolved campaigns.db's path (no campaign tool, prompt, resource or default lookup has asked for
    /// <see cref="Database"/> yet, so nothing can have run a statement on it). Resolving the path does no I/O, so true does
    /// not prove the file was opened: the callers apply it only where campaigns.db is the store in use (the campaign tools,
    /// the prompts, campaign:// resources).
    ///
    /// <para>
    /// For the host's call-tool, get-prompt and read-resource filters (<see cref="DndMcpServerRegistration"/>): the store maps
    /// these codes only while opening and writing, so a read statement that hits a damaged page or another process's lock
    /// reached the model as "An error occurred invoking 'campaign_get'.", with nothing to tell the user. The message names the
    /// file and what to do, and never carries SQL.
    /// </para>
    /// </summary>
    public bool TryMapStoreFailure(SqliteException exception, [NotNullWhen(true)] out CampaignStoreUnavailableException? unavailable)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (_database is not { } database)
        {
            unavailable = null;
            return false;
        }

        return CampaignDatabase.TryMapUnavailable(exception, database.Path, out unavailable);
    }

    /// <summary>The write and campaign services over <see cref="Database"/>.</summary>
    public CampaignStore Store => new(Database);

    /// <summary>The campaign this process is working on (set by <c>campaign use</c> / <c>create</c>); null until one is chosen.</summary>
    public string? CurrentCampaignId => _currentCampaignId;

    /// <summary>Makes <paramref name="campaignId"/> this process's current campaign (not persisted; see <see cref="Use"/>).</summary>
    public void SetCurrent(string campaignId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(campaignId);
        _currentCampaignId = campaignId;
    }

    /// <summary>
    /// The campaign a call is about (contract §3.7): <paramref name="campaign"/> (slug or exact name), else this process's
    /// current campaign, else the active one, else the only one.
    /// </summary>
    /// <exception cref="Domain.Core.DndInputException">No campaigns, no such campaign, or several and none chosen.</exception>
    public CampaignRow Resolve(string? campaign) => Store.Resolve(campaign, _currentCampaignId);

    /// <summary>
    /// The campaign a call without <c>campaign</c> would use (contract §3.7), among <paramref name="campaigns"/> (the
    /// campaigns as listed): this process's current one, else the persisted active one, else the only one; null when such a
    /// call would be refused as ambiguous, or there are none. An id naming a campaign that no longer exists (its create
    /// undone) is skipped, as <see cref="Resolve"/> skips it.
    ///
    /// <para>
    /// For the marks that say which campaign calls use: <c>campaign list</c> ("calls use this one"), <c>campaign get</c>
    /// ("Default for calls without campaign") and <c>campaign://list</c> ("the current campaign"). One rule for all three,
    /// the one <see cref="Resolve"/> applies, so no mark names a campaign other than the one calls go to: campaign://list
    /// once stopped after the persisted active campaign and marked nothing while every call used the only campaign.
    /// </para>
    /// </summary>
    public string? DefaultCampaignId(IReadOnlyList<CampaignRow> campaigns)
    {
        ArgumentNullException.ThrowIfNull(campaigns);
        if (_currentCampaignId is { } current && campaigns.Any(c => c.Id == current))
        {
            return current;
        }

        if (Store.ActiveCampaignId() is { } active && campaigns.Any(c => c.Id == active))
        {
            return active;
        }

        return campaigns.Count == 1 ? campaigns[0].Id : null;
    }

    /// <summary>Resolves a campaign and makes it both this process's current campaign and the persisted active one.</summary>
    public CampaignRow Use(string? campaign)
    {
        var store = Store;
        var row = store.Resolve(campaign, _currentCampaignId);
        store.SetActive(row.Id);
        _currentCampaignId = row.Id;
        return row;
    }

    /// <summary>
    /// The ambient defaults (ruleset, level offset) of this process's current campaign, else the persisted active one; null
    /// when there is none or the database cannot be read. Never creates, migrates or writes campaigns.db.
    /// </summary>
    public CampaignDefaultValues? Defaults() => ReadDefaults().Values;

    /// <summary>
    /// <see cref="Defaults"/>, telling "no campaign" from "campaigns.db exists but could not be read" (locked by another
    /// process past the busy timeout, damaged, unreadable, or from a newer dnd-mcp): the second carries the file's path, for
    /// the one-line note the rules, encounter and balance results then add (class summary). No path for campaigns.db at
    /// all (no home directory and no override) is "no campaign": there is no file to have failed. Never throws for the store.
    /// </summary>
    public CampaignDefaultsReading ReadDefaults()
    {
        CampaignDatabase database;
        try
        {
            database = Database;
        }
        catch (CampaignStoreUnavailableException ex)
        {
            _logger.LogWarning(ex, "Could not read the active campaign's defaults from campaigns.db; using the built-in defaults.");
            return new CampaignDefaultsReading(null, null);
        }

        try
        {
            return new CampaignDefaultsReading(CampaignDefaults.TryRead(database, _currentCampaignId), null);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or CampaignStoreUnavailableException)
        {
            _logger.LogWarning(ex, "Could not read the active campaign's defaults from campaigns.db; using the built-in defaults.");
            return new CampaignDefaultsReading(null, database.Path);
        }
    }

    /// <summary>
    /// The edition a rules or balance tool should use when the call names none: the campaign's ruleset when it is 2014 or
    /// 2024, else null (no campaign, or a mixed one: the caller falls back to 2024). <paramref name="campaignSlug"/> names
    /// the campaign for the one-line note the output adds.
    /// </summary>
    public string? DefaultEdition(out string? campaignSlug)
    {
        var reading = ReadDefaults();
        campaignSlug = reading.Edition is null ? null : reading.Values!.Slug;
        return reading.Edition;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _database?.Dispose();
            _database = null;
        }
    }
}

/// <summary>What <see cref="CampaignService.ReadDefaults"/> found.</summary>
/// <param name="Values">The current or active campaign's defaults; null when there is none, or it could not be read.</param>
/// <param name="UnreadablePath">
/// campaigns.db when it exists but could not be read (the defaults then fall back to the built-in ones, and the result says
/// so); null otherwise.
/// </param>
public sealed record CampaignDefaultsReading(CampaignDefaultValues? Values, string? UnreadablePath)
{
    /// <summary>
    /// The edition a call that names none takes from the campaign: its ruleset when that is one edition (2014 or 2024);
    /// null with no campaign, an unreadable one, or a mixed one (the tools then use 2024). <see cref="Values"/>' slug names
    /// the campaign in the note.
    /// </summary>
    public string? Edition => Values?.Ruleset is Domain.Campaign.CampaignValues.Rulesets.R2014 or Domain.Campaign.CampaignValues.Rulesets.R2024
        ? Values.Ruleset
        : null;
}
