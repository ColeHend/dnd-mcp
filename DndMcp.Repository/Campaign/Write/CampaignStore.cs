using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using CV = DndMcp.Domain.Campaign.CampaignValues;

namespace DndMcp.Repository.Campaign.Write;

/// <summary>What <see cref="CampaignStore.Create"/> did.</summary>
/// <param name="Campaign">The campaign as stored.</param>
/// <param name="BatchId">The creating batch (null for a dry run).</param>
/// <param name="DryRun">Nothing was kept.</param>
/// <param name="Party">The party faction's ref (<c>faction:the-party</c>).</param>
/// <param name="MyCharacter">The player's character's ref, for a player campaign that named one.</param>
/// <param name="Warnings">A taken slug, for instance.</param>
public sealed record CampaignCreateResult(
    CampaignRow Campaign,
    string? BatchId,
    bool DryRun,
    string Party,
    string? MyCharacter,
    IReadOnlyList<WriteWarning> Warnings);

/// <summary>
/// What <see cref="CampaignStore.Update"/> changes; every property is optional (null: leave it). <see cref="Settings"/> is
/// an RFC 7396 merge patch (a key set to null is removed). <see cref="DmName"/>, <see cref="CurrentLocation"/> and
/// <see cref="CurrentIngame"/> are cleared with an empty string.
/// </summary>
public sealed record CampaignUpdate
{
    public string? Name { get; init; }

    public string? Status { get; init; }

    public string? Ruleset { get; init; }

    public string? DmName { get; init; }

    public IReadOnlyDictionary<string, JsonElement>? Settings { get; init; }

    public string? SummaryMd { get; init; }

    /// <summary>A location entity's handle.</summary>
    public string? CurrentLocation { get; init; }

    public string? CurrentIngame { get; init; }

    /// <summary>A character entity's handle (player campaigns).</summary>
    public string? MyCharacter { get; init; }
}

/// <summary>What <see cref="CampaignStore.Update"/> did.</summary>
public sealed record CampaignUpdateResult(
    CampaignRow Campaign,
    string? BatchId,
    bool DryRun,
    IReadOnlyList<string> ChangedFields,
    IReadOnlyList<WriteWarning> Warnings);

/// <summary>
/// Campaigns themselves (contract §6, §3.7): create, update, list, the active campaign, and the resolution rule every
/// campaign tool uses to decide which campaign a call is about.
///
/// <para>
/// <b>Create is one batch</b> (and one undo unit): the campaign row, its party faction (kind faction, subtype party,
/// visibility party; <c>campaign.party_id</c> points at it, and party knowledge and membership hang on it), and for a
/// player campaign the player's character (character / pc, visibility party, <c>member_of</c> the party, current) as
/// <c>my_character_id</c>.
/// </para>
/// <para>
/// <b>Settings</b> are an object whose keys are plain identifiers. Two have meaning: <c>effective_level_offset</c> (an
/// integer −10..10, encounter_difficulty's default: One Piece's "budget against printed level +1") and
/// <c>default_visibility</c> (public, party, restricted or author: a new entity's visibility). Other keys are stored as
/// given, when they are strings, numbers or booleans; objects and arrays are refused (settings are flags, not data).
/// </para>
/// <para>
/// <b>Resolution</b> (§3.7): the argument (slug, or exact name) → the process's current campaign (the host's) →
/// <c>app_state.active_campaign</c> (ignored when that campaign is gone) → the only campaign → a refusal listing the
/// campaigns. Reads never create campaigns.db: with no file, there are no campaigns.
/// </para>
/// </summary>
public sealed partial class CampaignStore
{
    /// <summary>The app_state key of the active campaign (not logged: which campaign is active is not history).</summary>
    public const string ActiveCampaignKey = "active_campaign";

    /// <summary>The party faction's name when none is given.</summary>
    public const string DefaultPartyName = "The Party";

    private const string Subject = "campaign";

    private readonly CampaignDatabase _database;

    public CampaignStore(CampaignDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>Creates a campaign (see the class summary).</summary>
    /// <param name="partySlug">The party faction's slug; default made from its name (<c>the-party</c>).</param>
    /// <param name="myCharacterSlug">The player's character's slug; default made from its name.</param>
    /// <exception cref="DndInputException">An argument is invalid, or the explicit slug is taken; nothing was written.</exception>
    public CampaignCreateResult Create(
        string name,
        string role,
        string ruleset,
        string? dmName = null,
        string? slug = null,
        IReadOnlyDictionary<string, JsonElement>? settings = null,
        string? partyName = null,
        string? myCharacter = null,
        WriteContext? context = null,
        string? partySlug = null,
        string? myCharacterSlug = null)
    {
        context ??= WriteContext.Default;
        var problems = new List<string>();
        CheckLine(problems, "name", name, CampaignLimits.MaxNameLength, required: true);
        if (!CV.Roles.Set.TryMatch(role, out var canonicalRole))
        {
            problems.Add($"role \"{WriteBatch.Echo(role)}\" is not a role; give player (you play in it) or dm (you run it).");
        }

        if (!CV.Rulesets.Set.TryMatch(ruleset, out var canonicalRuleset))
        {
            problems.Add($"ruleset \"{WriteBatch.Echo(ruleset)}\" is not a ruleset; give 2014, 2024 or mixed.");
        }

        CheckLine(problems, "dm_name", dmName, CampaignLimits.MaxNameLength);
        CheckLine(problems, "party_name", partyName, CampaignLimits.MaxNameLength);
        CheckLine(problems, "my_character", myCharacter, CampaignLimits.MaxNameLength);
        if (slug is not null && !CampaignSlugs.IsValid(slug.Trim().ToLowerInvariant()))
        {
            problems.Add($"slug \"{WriteBatch.Echo(slug)}\" must be lower-case letters and digits in hyphen-separated runs, e.g. \"belmakor\".");
        }

        foreach (var (field, value) in new[] { ("party_slug", partySlug), ("my_character_slug", myCharacterSlug) })
        {
            if (value is not null && !CampaignSlugs.IsValid(value.Trim().ToLowerInvariant()))
            {
                problems.Add($"{field} \"{WriteBatch.Echo(value)}\" must be lower-case letters and digits in hyphen-separated runs, e.g. \"old-king\".");
            }
        }

        if (myCharacterSlug is not null && myCharacter is null)
        {
            problems.Add("my_character_slug needs my_character (the character's name).");
        }

        if (partySlug is not null && myCharacterSlug is not null &&
            string.Equals(partySlug.Trim(), myCharacterSlug.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("party_slug and my_character_slug are the same; slugs are unique in a campaign.");
        }

        if (myCharacter is not null && canonicalRole == CV.Roles.Dm)
        {
            problems.Add("my_character is for a player campaign (your own character); in a DM campaign add the PCs with campaign_write.");
        }

        var settingsObject = Settings(problems, settings, patch: false);
        DslProblems.ThrowIfAny(problems, Subject);
        var reason = WriteBatch.CheckReason(context.Reason);
        return _database.Write((connection, transaction) =>
        {
            var warnings = new List<WriteWarning>();
            var campaignSlug = CampaignSlug(connection, transaction, name, slug, warnings);
            var campaignId = CampaignDatabase.NewId();
            var batch = new BatchContext(campaignId, CampaignDatabase.NewId(), context.Actor, context.Tool ?? "campaign/create", null, reason);
            var recorder = new ChangeRecorder(connection, transaction, batch, _database.Now());
            recorder.Insert("campaign", new Dictionary<string, object?>
            {
                ["id"] = campaignId,
                ["slug"] = campaignSlug,
                ["name"] = name.Trim(),
                ["role"] = canonicalRole,
                ["ruleset"] = canonicalRuleset,
                ["dm_name"] = string.IsNullOrWhiteSpace(dmName) ? null : dmName.Trim(),
                ["settings"] = settingsObject,
            }, "create");
            var party = (partyName ?? DefaultPartyName).Trim();
            var factionSlug = partySlug?.Trim().ToLowerInvariant() ?? CampaignSlugs.From(party, CV.Kinds.Faction);
            var partyId = (string)recorder.Insert("entity", new Dictionary<string, object?>
            {
                ["campaign_id"] = campaignId,
                ["kind"] = CV.Kinds.Faction,
                ["subtype"] = CV.Subtypes.PartyFaction,
                ["slug"] = factionSlug,
                ["name"] = party,
                ["visibility"] = CV.Visibilities.Party,
                ["status"] = CV.Statuses.FactionActive,
            }, "create")["id"]!;
            string? characterRef = null;
            var links = new Dictionary<string, object?> { ["party_id"] = partyId };
            if (myCharacter is not null)
            {
                var characterSlug = myCharacterSlug?.Trim().ToLowerInvariant() ?? CampaignSlugs.From(myCharacter, CV.Kinds.Character);
                if (characterSlug == factionSlug)
                {
                    characterSlug = CampaignSlugs.WithSuffix(characterSlug, 2);
                }

                var characterId = (string)recorder.Insert("entity", new Dictionary<string, object?>
                {
                    ["campaign_id"] = campaignId,
                    ["kind"] = CV.Kinds.Character,
                    ["subtype"] = CV.Subtypes.Pc,
                    ["slug"] = characterSlug,
                    ["name"] = myCharacter.Trim(),
                    ["visibility"] = CV.Visibilities.Party,
                    ["status"] = CV.Statuses.CharacterAlive,
                }, "create")["id"]!;
                recorder.Insert("relation", new Dictionary<string, object?>
                {
                    ["campaign_id"] = campaignId,
                    ["from_id"] = characterId,
                    ["rel"] = CV.Rels.MemberOf,
                    ["to_id"] = partyId,
                    ["visibility"] = CV.Visibilities.Party,
                    ["status"] = CV.RelationStatuses.Current,
                }, "create");
                links["my_character_id"] = characterId;
                characterRef = CV.Kinds.Character + ":" + characterSlug;
            }

            recorder.Update("campaign", campaignId, links, "create");
            var created = WriteBatch.LoadCampaign(connection, transaction, campaignId)!;
            return new CampaignCreateResult(created, context.DryRun ? null : batch.BatchId, context.DryRun,
                CV.Kinds.Faction + ":" + factionSlug, characterRef, warnings);
        }, context.DryRun);
    }

    /// <summary>Changes a campaign's name, status, ruleset, DM, settings (merge patch), summary, current place and date, or player character.</summary>
    /// <exception cref="DndInputException">An argument is invalid or a handle is not found; nothing was written.</exception>
    public CampaignUpdateResult Update(CampaignRow campaign, CampaignUpdate update, WriteContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        context ??= WriteContext.Default;
        var problems = new List<string>();
        CheckLine(problems, "name", update.Name, CampaignLimits.MaxNameLength);
        if (update.Status is not null && !CV.CampaignStatuses.Set.TryMatch(update.Status, out _))
        {
            problems.Add($"status \"{WriteBatch.Echo(update.Status)}\" is not a campaign status; give {CV.CampaignStatuses.Set.List}.");
        }

        if (update.Ruleset is not null && !CV.Rulesets.Set.TryMatch(update.Ruleset, out _))
        {
            problems.Add($"ruleset \"{WriteBatch.Echo(update.Ruleset)}\" is not a ruleset; give 2014, 2024 or mixed.");
        }

        CheckLine(problems, "dm_name", update.DmName, CampaignLimits.MaxNameLength, blankClears: true);
        CheckLine(problems, "current_ingame", update.CurrentIngame, CampaignLimits.MaxLabelLength, blankClears: true);
        if (update.SummaryMd is { Length: > CampaignLimits.MaxBodyLength })
        {
            problems.Add($"summary_md is {WriteBatch.Number(update.SummaryMd.Length)} characters; at most {WriteBatch.Number(CampaignLimits.MaxBodyLength)}.");
        }

        var patch = Settings(problems, update.Settings, patch: true);
        DslProblems.ThrowIfAny(problems, Subject);
        return WriteBatch.Run(_database, campaign, context, "campaign/update", b =>
        {
            var changes = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (update.Name is not null)
            {
                changes["name"] = update.Name.Trim();
            }

            if (update.Status is not null)
            {
                changes["status"] = Canonical.Of(CV.CampaignStatuses.Set, update.Status);
            }

            if (update.Ruleset is not null)
            {
                changes["ruleset"] = Canonical.Of(CV.Rulesets.Set, update.Ruleset);
            }

            if (update.DmName is not null)
            {
                changes["dm_name"] = string.IsNullOrWhiteSpace(update.DmName) ? null : update.DmName.Trim();
            }

            if (update.SummaryMd is not null)
            {
                changes["summary_md"] = update.SummaryMd;
            }

            if (update.CurrentIngame is not null)
            {
                changes["current_ingame"] = string.IsNullOrWhiteSpace(update.CurrentIngame) ? null : update.CurrentIngame.Trim();
            }

            if (update.CurrentLocation is not null)
            {
                changes["current_location_id"] = string.IsNullOrWhiteSpace(update.CurrentLocation)
                    ? null
                    : b.RequireEntity(Subject, "current_location", "handle", update.CurrentLocation, CV.Kinds.Location).Id;
            }

            if (update.MyCharacter is not null)
            {
                if (!b.IsDmCampaign)
                {
                    changes["my_character_id"] = b.RequireEntity(Subject, "my_character", "handle", update.MyCharacter, CV.Kinds.Character).Id;
                }
                else
                {
                    throw WriteBatch.Problem(Subject, "my_character is for a player campaign (your own character).");
                }
            }

            if (patch.Count > 0)
            {
                changes["settings"] = ChangeRecorder.MergePatch(JsonNode.Parse(b.Campaign.Settings), patch);
            }

            var changed = b.Update("campaign", b.Campaign.Id, changes, "update");
            b.Finish();
            b.ReloadCampaign();
            return new CampaignUpdateResult(b.Campaign, b.DryRun ? null : b.Context.BatchId, b.DryRun, changed, b.Warnings.ToList());
        });
    }

    /// <summary>Every campaign, by slug; empty when campaigns.db does not exist (a read never creates it).</summary>
    public IReadOnlyList<CampaignRow> List()
    {
        using var connection = _database.TryOpenExisting();
        return connection is null ? [] : HandleResolver.Campaigns(connection);
    }

    /// <summary>A campaign by slug or exact name, or null (none, or no database).</summary>
    public CampaignRow? TryGet(string slugOrName)
    {
        using var connection = _database.TryOpenExisting();
        return connection is null ? null : HandleResolver.TryCampaign(connection, slugOrName);
    }

    /// <summary>Makes a campaign the active one for every process (<c>app_state</c>; not logged).</summary>
    /// <exception cref="DndInputException">No such campaign.</exception>
    public void SetActive(string campaignId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(campaignId);
        _database.Write((connection, transaction) =>
        {
            if (WriteBatch.LoadCampaign(connection, transaction, campaignId) is null)
            {
                throw new DndInputException("That campaign no longer exists; campaign {\"action\": \"list\"} shows the campaigns.");
            }

            return connection.Execute(
                "INSERT INTO app_state(key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                new { key = ActiveCampaignKey, value = campaignId }, transaction);
        });
    }

    /// <summary>The active campaign's id, or null when none is set, the one set no longer exists, or there is no database.</summary>
    public string? ActiveCampaignId()
    {
        using var connection = _database.TryOpenExisting();
        return connection is null ? null : ActiveCampaignId(connection);
    }

    /// <summary>
    /// The campaign a call is about (§3.7): <paramref name="campaign"/> (slug or exact name), else the process's current
    /// campaign, else the active one, else the only one.
    /// </summary>
    /// <param name="processCurrentCampaignId">The host's current campaign for this process (ignored when it no longer exists).</param>
    /// <exception cref="DndInputException">No campaigns, no such campaign, or several and none chosen (the campaigns are listed).</exception>
    public CampaignRow Resolve(string? campaign, string? processCurrentCampaignId = null)
    {
        using var connection = _database.TryOpenExisting();
        var all = connection is null ? [] : HandleResolver.Campaigns(connection);
        if (all.Count == 0)
        {
            throw new DndInputException(
                "There are no campaigns yet. Create one with campaign {\"action\": \"create\", \"name\": \"…\", \"role\": \"player\" or \"dm\", \"ruleset\": \"2024\"}.");
        }

        if (!string.IsNullOrWhiteSpace(campaign))
        {
            return HandleResolver.TryCampaign(connection!, campaign) ??
                   throw new DndInputException($"No campaign \"{WriteBatch.Echo(campaign)}\". Campaigns: {Listing(all)}. Pass one of those slugs.");
        }

        if (processCurrentCampaignId is not null && all.FirstOrDefault(c => c.Id == processCurrentCampaignId) is { } current)
        {
            return current;
        }

        if (ActiveCampaignId(connection!) is { } active && all.FirstOrDefault(c => c.Id == active) is { } activeCampaign)
        {
            return activeCampaign;
        }

        if (all.Count == 1)
        {
            return all[0];
        }

        throw new DndInputException(
            $"Several campaigns exist and none is chosen: {Listing(all)}. Pass campaign (a slug), or call campaign {{\"action\": \"use\", \"campaign\": \"<slug>\"}} once.");
    }

    private static string? ActiveCampaignId(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        CampaignDatabase.EnsureDapperConfigured();
        var id = connection.QueryFirstOrDefault<string?>("SELECT value FROM app_state WHERE key = @key", new { key = ActiveCampaignKey });
        return id is not null && WriteBatch.LoadCampaign(connection, null, id) is not null ? id : null;
    }

    private static string Listing(IReadOnlyList<CampaignRow> campaigns) =>
        string.Join(", ", campaigns.Select(c => $"{c.Slug} ({c.Role}, {c.Ruleset})"));

    private static string CampaignSlug(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction,
        string name, string? slug, List<WriteWarning> warnings)
    {
        var taken = HandleResolver.Campaigns(connection, transaction).Select(c => c.Slug).ToHashSet(StringComparer.Ordinal);
        if (slug is not null)
        {
            var wanted = slug.Trim().ToLowerInvariant();
            return taken.Contains(wanted)
                ? throw WriteBatch.Problem(Subject, $"slug {wanted} is taken by another campaign; give another slug or leave it out.")
                : wanted;
        }

        var baseSlug = CampaignSlugs.From(name, "campaign");
        if (!taken.Contains(baseSlug))
        {
            return baseSlug;
        }

        for (var n = 2; ; n++)
        {
            var candidate = CampaignSlugs.WithSuffix(baseSlug, n);
            if (!taken.Contains(candidate))
            {
                warnings.Add(new WriteWarning(WarningKinds.SlugCollision, WarningSeverities.Warning,
                    $"campaign slug {baseSlug} is taken, so this campaign is {candidate}."));
                return candidate;
            }
        }
    }

    /// <summary>
    /// Validates settings and returns them as the object to store (create) or the merge patch to apply (update; a null
    /// value removes the key): <c>default_visibility</c> canonicalised, everything else as given.
    /// </summary>
    private static JsonObject Settings(List<string> problems, IReadOnlyDictionary<string, JsonElement>? settings, bool patch)
    {
        var result = new JsonObject();
        foreach (var (key, value) in settings ?? new Dictionary<string, JsonElement>())
        {
            var where = $"settings {WriteBatch.Echo(key)}";
            if (!KeyPattern().IsMatch(key))
            {
                problems.Add($"settings key \"{WriteBatch.Echo(key)}\" must be letters, digits, \"_\" and \"-\" (at most 100 characters), e.g. \"dm_pronouns\".");
                continue;
            }

            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                if (patch)
                {
                    result[key] = null;
                }

                continue;
            }

            switch (key)
            {
                case "effective_level_offset":
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var offset) || offset is < -10 or > 10)
                    {
                        problems.Add($"{where} must be a whole number from -10 to 10 (encounter_difficulty's default offset), e.g. 1.");
                    }
                    else
                    {
                        result[key] = offset;
                    }

                    break;
                case "default_visibility":
                    if (value.ValueKind != JsonValueKind.String || !CV.Visibilities.Set.TryMatch(value.GetString(), out var visibility))
                    {
                        problems.Add($"{where} must be public, party, restricted or author (a new entity's visibility).");
                    }
                    else
                    {
                        result[key] = visibility;
                    }

                    break;
                default:
                    if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        problems.Add($"{where} must be a string, number or boolean; settings are flags, not data.");
                    }
                    else if (value.ValueKind == JsonValueKind.String && value.GetString()!.Length > CampaignLimits.MaxNoteLength)
                    {
                        problems.Add($"{where} is longer than {WriteBatch.Number(CampaignLimits.MaxNoteLength)} characters.");
                    }
                    else
                    {
                        result[key] = JsonNode.Parse(value.GetRawText());
                    }

                    break;
            }
        }

        return result;
    }

    private static void CheckLine(List<string> problems, string field, string? text, int max, bool required = false, bool blankClears = false)
    {
        if (text is null)
        {
            if (required)
            {
                problems.Add($"{field} is required.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            if (!blankClears)
            {
                problems.Add(required ? $"{field} is required." : $"{field} is blank; give text or leave it out.");
            }
        }
        else if (text.Trim().Length > max || text.Any(char.IsControl))
        {
            problems.Add($"{field} must be one line of at most {WriteBatch.Number(max)} characters.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
