using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Characters;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using DndMcp.Repository.Campaign.Read;
using Microsoft.Data.Sqlite;
using CV = DndMcp.Domain.Campaign.CampaignValues;

namespace DndMcp.Repository.Campaign.Characters;

/// <summary>
/// Reads sheets for <c>campaign_character get</c> and <c>campaign_get include: ["sheet"]</c> (contract §7.3, §7.4), in the
/// view of one perspective.
///
/// <para>
/// <b>The author</b> (author, or dm in a DM campaign) gets everything (<see cref="AuthorSheetView"/>): the whole sheet,
/// the derived numbers, the holdings and the coin balance, and (campaign_get's <c>as_of_session</c>) the sheet, holdings
/// and ledger as they stood at the end of a session.
/// </para>
/// <para>
/// <b>Any other view</b> gets the public line (<see cref="PublicSheetLine"/>) ONLY for a current party member
/// (<see cref="PartyRoster"/>, as of the read's session) that the view is shown undisguised. Every other character (an
/// NPC with a sheet, a dead or departed member, a disguised or hidden one) reads exactly as a character with no sheet: no
/// hint that a sheet exists, and a hidden one gets the Phase 6 not-found. The line's free texts (a homebrew class, every
/// subclass, the species, a non-SRD condition) go through the view-text check (<see cref="ViewTextCheck"/>), ONE check for
/// every line of the read, and a text that fails is left out ("an effect" for a condition). The list form prints only the
/// lines this gives and says nothing about the members it leaves out.
/// </para>
/// <para>
/// <b>Characters are named by the view</b>: a non-author <c>character</c> argument resolves through
/// <see cref="ReadScope.ResolveEntity"/> (only refs that view would print), a short slug completes only as a perspective's
/// does (<see cref="KnowledgeLoader.Resolve"/>), and a miss is <see cref="ReadScope.NotFoundProblem"/>, worded the same
/// whether nothing has the handle or the view may not see it.
/// </para>
/// </summary>
public sealed class SheetReader
{
    private readonly CampaignDatabase _database;

    public SheetReader(CampaignDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>
    /// <c>campaign_character get</c>: one character's sheet in the view of <paramref name="perspective"/> (default author),
    /// or, in a DM campaign with no <paramref name="character"/>, every current party member's.
    /// </summary>
    /// <exception cref="DndInputException">A character that names nothing this view may see, an unknown perspective.</exception>
    /// <exception cref="CampaignStoreUnavailableException">campaigns.db, or a sheet in it, cannot be read.</exception>
    public SheetGetResult Get(CampaignRow campaign, string? character = null, Perspective? perspective = null)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        using var connection = ReadConnection.Open(_database);
        var scope = ReadScope.Open(connection, campaign, perspective, null);
        if (string.IsNullOrWhiteSpace(character) && campaign.Role == CV.Roles.Dm)
        {
            return List(scope);
        }

        var entity = scope.IsAuthorView ? AuthorEntity(scope, character) : ViewEntity(scope, character);
        var read = Reads(scope, [entity]).Single();
        return new SheetGetResult(campaign.Slug, scope.Who.Perspective.Text, scope.IsAuthorView, false, [read], []);
    }

    /// <summary>
    /// The list form in any campaign (a player campaign's too): every current party member in the view of
    /// <paramref name="perspective"/> (default author), as <c>campaign://&lt;slug&gt;/party</c> and the list form of
    /// <c>get</c> show it. The author gets every member (with or without a sheet) and the dead or departed ones apart;
    /// another view only the lines it is given.
    /// </summary>
    /// <exception cref="DndInputException">An unknown character perspective.</exception>
    /// <exception cref="CampaignStoreUnavailableException">campaigns.db, or a member's sheet, cannot be read.</exception>
    public SheetGetResult Party(CampaignRow campaign, Perspective? perspective = null)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        using var connection = ReadConnection.Open(_database);
        return List(ReadScope.Open(connection, campaign, perspective, null));
    }

    /// <summary>
    /// The sheet of each entity of a campaign_get render in the scope's view, keyed by entity id (one view-text check for
    /// all of them); an entity that gets no sheet in this view (not a character, none, or not shown) is absent.
    /// </summary>
    internal static IReadOnlyDictionary<string, SheetView> ForEntities(ReadScope scope, IReadOnlyList<EntityState> entities)
    {
        var characters = entities.Where(e => e.Row.Kind == CV.Kinds.Character).ToList();
        var views = new Dictionary<string, SheetView>(StringComparer.Ordinal);
        if (characters.Count == 0)
        {
            return views;
        }

        foreach (var (entity, read) in characters.Zip(Reads(scope, characters)))
        {
            if (read.HasSheet)
            {
                views[entity.Row.Id] = new SheetView(read.Author, read.Line);
            }
        }

        return views;
    }

    // The list form: every current member (author), or the lines of the members this view is shown (any other view).
    private static SheetGetResult List(ReadScope scope)
    {
        var roster = PartyRoster.Read(scope.Connection, scope.Campaign, scope.AsOf, sheets: false);
        scope.LoadEntities(roster.Members.Select(m => m.EntityId));
        var entities = roster.Members.Select(m => scope.Entity(m.EntityId)).OfType<EntityState>().ToList();
        var reads = Reads(scope, entities, roster);
        return new SheetGetResult(
            scope.Campaign.Slug,
            scope.Who.Perspective.Text,
            scope.IsAuthorView,
            true,
            scope.IsAuthorView ? reads : reads.Where(r => r.HasSheet).ToList(),
            scope.IsAuthorView ? roster.Excluded : []);
    }

    // The sheets of the entities in the scope's view, in order (one view-text check for every public line).
    private static IReadOnlyList<CharacterSheetRead> Reads(ReadScope scope, IReadOnlyList<EntityState> entities, PartyRosterResult? roster = null)
    {
        if (scope.IsAuthorView)
        {
            return entities.Select(e => AuthorRead(scope, e)).ToList();
        }

        roster ??= PartyRoster.Read(scope.Connection, scope.Campaign, scope.AsOf, sheets: false);
        var members = roster.Members.Select(m => m.EntityId).ToHashSet(StringComparer.Ordinal);
        var shown = entities
            .Select(e => (Entity: e, Sheet: e.Shown && members.Contains(e.Row.Id) ? SheetOf(scope, e.Row.Id) : null))
            .ToList();
        var texts = new List<string?>();
        var lines = shown.Select(s => s.Sheet is null ? null : LineTexts(s.Sheet, texts)).ToList();
        var check = ViewTextCheck.Check(scope, texts);
        return shown.Select((s, i) => new CharacterSheetRead(s.Entity.Ref!, s.Entity.Name, null,
            s.Sheet is null ? null : Line(scope, s.Entity, s.Sheet, lines[i]!, check))).ToList();
    }

    private static CharacterSheetRead AuthorRead(ReadScope scope, EntityState entity) =>
        new(entity.Ref!, entity.Name, SheetOf(scope, entity.Row.Id) is { } sheet ? Author(scope, entity, sheet) : null, null);

    // A sheet as of the scope's session (now when none); an unreadable one is refused naming the character as this view may.
    private static CharacterSheet? SheetOf(ReadScope scope, string entityId) => scope.AsOf is { } session
        ? CharacterSheetStore.AsOf(scope.Connection, entityId, session, authorView: scope.IsAuthorView)
        : CharacterSheetStore.Read(scope.Connection, entityId, authorView: scope.IsAuthorView);

    /// <summary>The author view of a sheet (§7.3), with the holdings and coin balance as of the scope's session.</summary>
    internal static AuthorSheetView Author(ReadScope scope, EntityState entity, CharacterSheet sheet)
    {
        var edition = Edition(sheet, scope.Campaign);
        var next = sheet.Xp is not null && sheet.Level is { } level and < DslLimits.MaxLevel ? Advancement.NextThreshold(level) : null;
        return new AuthorSheetView(
            entity.Row.Handle,
            entity.Row.Name,
            edition,
            sheet,
            sheet.EffectiveMaxHp(edition),
            sheet.ProficiencyBonus,
            sheet.InitiativeOrDex,
            sheet.IsDying(edition),
            sheet.IsDead(edition),
            next,
            Inventory(scope, entity.Row.Id),
            Coins(scope, entity.Row.Id),
            SimProfileOf(sheet.SimProfile));
    }

    /// <summary>The edition a sheet follows: its own ruleset, else the campaign's (a mixed campaign's: the 2024 default).</summary>
    public static string Edition(CharacterSheet sheet, CampaignRow campaign)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(campaign);
        return sheet.EditionOr(CampaignEdition(campaign));
    }

    /// <summary>The campaign's ruleset as a sheet operation's fallback edition ("mixed" has none: the 2024 default).</summary>
    public static string CampaignEdition(CampaignRow campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        return campaign.Ruleset is CV.Rulesets.R2014 or CV.Rulesets.R2024 ? campaign.Ruleset : DslValues.Editions.Default;
    }

    // The free texts of one line, appended to the render's list (their indexes kept for the line).
    private static LineTextIndexes LineTexts(CharacterSheet sheet, List<string?> texts)
    {
        int Add(string? text)
        {
            texts.Add(text);
            return texts.Count - 1;
        }

        var classes = sheet.Classes.Select(c => (ClassText: c.Srd is null ? Add(c.Class) : -1, Subclass: c.Subclass is null ? -1 : Add(c.Subclass))).ToList();
        var species = sheet.Species is null ? -1 : Add(sheet.Species);
        var conditions = sheet.Conditions.Select(c => SheetValues.Conditions.Set.TryMatch(c.Name, out _) ? -1 : Add(c.Name)).ToList();
        return new LineTextIndexes(classes, species, conditions);
    }

    private static PublicSheetLine Line(ReadScope scope, EntityState entity, CharacterSheet sheet, LineTextIndexes at, ViewTextResult check)
    {
        bool Passes(int index) => index >= 0 && check.Passes(index);
        var edition = Edition(sheet, scope.Campaign);
        var classes = new List<PublicClassLine>();
        for (var i = 0; i < sheet.Classes.Count; i++)
        {
            var c = sheet.Classes[i];
            var (classText, subclassText) = at.Classes[i];
            var name = c.Srd?.Name ?? (Passes(classText) ? c.Class.Trim() : null);
            if (name is not null)
            {
                classes.Add(new PublicClassLine(name, c.Level, Passes(subclassText) ? c.Subclass!.Trim() : null));
            }
        }

        var conditions = sheet.Conditions.Select((c, i) =>
            SheetValues.Conditions.Set.TryMatch(c.Name, out var srd) ? srd! : Passes(at.Conditions[i]) ? c.Name.Trim() : "an effect").ToList();
        var hpTracked = sheet.Hp is not null && sheet.MaxHp is not null;
        return new PublicSheetLine(
            entity.Ref!,
            entity.Name,
            sheet.Level,
            classes,
            Passes(at.Species) ? sheet.Species!.Trim() : null,
            hpTracked ? sheet.Hp : null,
            hpTracked ? sheet.EffectiveMaxHp(edition) : null,
            hpTracked ? sheet.TempHp : 0,
            sheet.Ac,
            sheet.Exhaustion,
            conditions);
    }

    // The holdings of a character with a quantity above 0 (as of the scope's session), in the order gained; holdings gained
    // in one call share created_at, and their ids (UUIDv7) are random within a millisecond, so the tie is broken by the
    // name's key, then the id: the same sheet reads the same every time. Never rowid: an as-of read rebuilds rows from
    // change_log, and an undo re-inserts them.
    private static IReadOnlyList<HoldingView> Inventory(ReadScope scope, string entityId)
    {
        var rows = StoredRows.Read(scope.Connection, "holding",
            () => scope.Connection.Query<HoldingRow>($"SELECT {HoldingRow.Columns} FROM holding WHERE holder_id = @entityId", new { entityId }).ToList());
        if (scope.AsOf is { } session)
        {
            rows = AsOfRows.ForEntities<HoldingRow>(scope.Connection, CampaignTables.Holding, rows.Select(r => r.Id), [entityId], session)
                .Where(r => r.HolderId == entityId)
                .ToList();
        }

        return rows.Where(r => r.Quantity > 0)
            .OrderBy(r => r.CreatedAt, StringComparer.Ordinal).ThenBy(r => CampaignText.Key(r.Name), StringComparer.Ordinal)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .Select(r => new HoldingView(r.Name, r.Quantity, r.SrdRef, r.Equipped != 0, r.Attuned != 0, r.Charges, r.Notes,
                r.ItemId is null ? null : scope.AuthorRef(r.ItemId)))
            .ToList();
    }

    // The coin balance of a character: the sums of its currency ledger (as of the scope's session).
    internal static CoinsView Coins(ReadScope scope, string entityId)
    {
        var rows = StoredRows.Read(scope.Connection, "currency_txn",
            () => scope.Connection.Query<CurrencyTxnRow>($"SELECT {CurrencyTxnRow.Columns} FROM currency_txn WHERE holder_id = @entityId", new { entityId }).ToList());
        if (scope.AsOf is { } session)
        {
            rows = AsOfRows.ForEntities<CurrencyTxnRow>(scope.Connection, CampaignTables.CurrencyTxn, rows.Select(r => r.Id), [entityId], session)
                .Where(r => r.HolderId == entityId)
                .ToList();
        }

        return new CoinsView(rows.Sum(r => r.Cp), rows.Sum(r => r.Sp), rows.Sum(r => r.Ep), rows.Sum(r => r.Gp), rows.Sum(r => r.Pp));
    }

    private static SimProfileView? SimProfileOf(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            var build = SimProfile.Read(json);
            return new SimProfileView(build.Name, build.Edition, build.Level,
                (build.Attacks ?? []).Select(a => a.Name ?? "attack").ToList(),
                (build.Modifiers ?? []).Select(m => m.Name ?? m.Kind ?? "modifier").ToList());
        }
        catch (Exception ex) when (ex is DndInputException or System.Text.Json.JsonException)
        {
            return new SimProfileView(null, null, null, [], [], Unreadable: true);
        }
    }

    // The character an author's get names (CharacterLookup's rules: the campaign's own by default in a player campaign).
    private static EntityState AuthorEntity(ReadScope scope, string? character)
    {
        var row = CharacterLookup.Resolve(scope.Connection, scope.Campaign, character);
        scope.LoadEntities([row.Id]);
        return scope.Entity(row.Id) ?? throw new DndInputException($"character {row.Handle} is not readable now.");
    }

    // The character a non-author get names, as that view may address it (class summary).
    private static EntityState ViewEntity(ReadScope scope, string? character)
    {
        if (string.IsNullOrWhiteSpace(character))
        {
            // A player campaign's own character, when this view is shown it; nothing names it otherwise.
            if (scope.Campaign.MyCharacterId is { } mine && scope.VisibleEntity(mine) is { Row.Kind: CV.Kinds.Character } own)
            {
                return own;
            }

            throw new DndInputException("character is required for this perspective: give the character's handle.");
        }

        var text = character.Trim();
        if (!CampaignHandle.TryParse(text, out var handle, out var problem))
        {
            throw new DndInputException($"character: {problem}");
        }

        var state = handle is CampaignHandle.CrossCampaign ? null : scope.ResolveEntity(handle);
        if (state is null && handle is CampaignHandle.EntityBySlug { Kind: null or CV.Kinds.Character })
        {
            state = Completed(scope, handle);
        }

        if (state is { Row.Kind: CV.Kinds.Character })
        {
            return state;
        }

        throw new DndInputException("character: " + scope.NotFoundProblem(text, CV.Kinds.Character));
    }

    // A short slug completed as a perspective's is (KnowledgeLoader.Resolve: one character the party knows by its own name
    // whose slug starts with it), then addressed as the view would print it: never a completion the view may not see.
    private static EntityState? Completed(ReadScope scope, CampaignHandle handle)
    {
        string? id;
        try
        {
            id = scope.Loader.Resolve(Perspective.ForCharacter(handle), scope.AsOf).CharacterId;
        }
        catch (DndInputException)
        {
            return null;
        }

        if (id is null || scope.VisibleEntity(id) is not { } state)
        {
            return null;
        }

        return scope.ResolveEntity(new CampaignHandle.EntityBySlug(CV.Kinds.Character, state.Row.Slug));
    }

    /// <summary>Where a line's free texts sit in the render's list (-1: none, or no check needed).</summary>
    private sealed record LineTextIndexes(IReadOnlyList<(int ClassText, int Subclass)> Classes, int Species, IReadOnlyList<int> Conditions);
}
