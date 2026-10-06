using Dapper;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Characters;
using DndMcp.Domain.Features;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Characters;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tests.CampaignWrite;
using Microsoft.Data.Sqlite;

namespace DndMcp.Tests.CampaignCharacters;

/// <summary>
/// A small campaign for the character tests, written through the Phase 6 write path (a <see cref="WriteFixture"/>):
/// <list type="bullet">
/// <item>DM form (<see cref="Dm"/>, 2024 by default): party members <c>character:hero</c> ("Hero Prime") and
/// <c>character:sidekick</c> ("Sidekick"), the NPC <c>character:villain</c> ("The Villain", restricted), a dead member
/// <c>character:fallen</c> and a location.</item>
/// <item>Player form (<see cref="Player"/>, 2014 by default): my character <c>character:aria-vale</c> ("Aria Vale"), the
/// member <c>character:bram</c>, the NPC <c>character:old-guard</c> (restricted).</item>
/// </list>
/// The scenario worlds (Belmakor, One Piece) are for the leak sweeps; these are for the writer's mechanics, where a fresh
/// world per test keeps every assertion about change_log exact.
/// </summary>
internal sealed class SheetWorld : IDisposable
{
    private SheetWorld(WriteFixture f, CampaignRow campaign, ICombatRouter? router)
    {
        F = f;
        Campaign = campaign;
        Writer = new CharacterWriter(f.Db.Database, router);
        Reader = new SheetReader(f.Db.Database);
    }

    public WriteFixture F { get; }

    public CampaignRow Campaign { get; private set; }

    public CharacterWriter Writer { get; private set; }

    public SheetReader Reader { get; }

    public CampaignDatabase Database => F.Db.Database;

    public static SheetWorld Dm(string ruleset = "2024", ICombatRouter? router = null)
    {
        var f = new WriteFixture();
        try
        {
            var campaign = f.Campaign("Sea", "dm", ruleset);
            f.Apply(campaign,
                new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Hero Prime", Slug = "hero", Subtype = "pc", Visibility = "party" },
                new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Sidekick", Subtype = "pc", Visibility = "party" },
                new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Fallen", Subtype = "pc", Visibility = "party", Status = "dead" },
                new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "The Villain", Slug = "villain", Subtype = "npc", Visibility = "restricted" },
                new CampaignOpSpec { Op = "upsert", Kind = "location", Name = "The Harbor", Visibility = "party" },
                Op.Link("character:hero", "member_of", "faction:the-party"),
                Op.Link("character:sidekick", "member_of", "faction:the-party"),
                Op.Link("character:fallen", "member_of", "faction:the-party"));
            return new SheetWorld(f, f.Reload(campaign), router);
        }
        catch
        {
            f.Dispose();
            throw;
        }
    }

    public static SheetWorld Player(string ruleset = "2014", ICombatRouter? router = null)
    {
        var f = new WriteFixture();
        try
        {
            var campaign = f.Campaign("Sky", "player", ruleset, myCharacter: "Aria Vale");
            f.Apply(campaign,
                new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Bram", Subtype = "pc", Visibility = "party" },
                new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "The Old Guard", Slug = "old-guard", Subtype = "npc", Visibility = "restricted" },
                Op.Link("character:bram", "member_of", "faction:the-party"));
            return new SheetWorld(f, f.Reload(campaign), router);
        }
        catch
        {
            f.Dispose();
            throw;
        }
    }

    /// <summary>Uses another router from now on.</summary>
    public void Route(ICombatRouter? router) => Writer = new CharacterWriter(Database, router);

    public void Reload() => Campaign = F.Reload(Campaign);

    public static SheetSpec Spec(string json) => DslJson.Deserialize<SheetSpec>(json, "sheet");

    /// <summary>update with a sheet JSON (default context: no session, a real run).</summary>
    public CharacterWriteResult Update(string? character, string json, WriteContext? context = null) =>
        Writer.Update(Campaign, character, Spec(json), null, context ?? WriteContext.Default);

    /// <summary>The stored sheet, read back with SQL through the store.</summary>
    public CharacterSheet? Sheet(string handle)
    {
        using var connection = F.Open();
        return CharacterSheetStore.Read(connection, F.Entity(Campaign, handle).Id);
    }

    public CharacterSheet Required(string handle) => Sheet(handle) ?? throw new InvalidOperationException($"{handle} has no sheet.");

    public string Id(string handle) => F.Entity(Campaign, handle).Id;

    /// <summary>The change_log rows of a batch (none for null).</summary>
    public IReadOnlyList<ChangeRow> Log(string? batchId) => batchId is null ? [] : F.Log(batchId);

    public long ChangeRows() => F.Count("SELECT count(*) FROM change_log");

    public IReadOnlyList<DiceRollRow> Dice()
    {
        using var connection = F.Open();
        return connection.Query<DiceRollRow>($"SELECT {DiceRollRow.Columns} FROM dice_roll ORDER BY seq").ToList();
    }

    public SqliteConnection Open() => F.Open();

    public void Dispose() => F.Dispose();
}

/// <summary>
/// A hand-written <see cref="ICombatRouter"/> (no mocks): answers as the combat layer would for one character, records
/// every call, and writes nothing (or, when <see cref="Write"/> is set, one unlogged dice_roll row in the writer's
/// transaction, so a test can see a dry run roll it back).
/// </summary>
internal sealed class FakeRouter : ICombatRouter
{
    private readonly string _entityId;

    public FakeRouter(string entityId, string outcome = RouteOutcomes.Applied, string encounter = "The crypt", string combatant = "Belmakor Silverwind")
    {
        _entityId = entityId;
        Outcome = outcome;
        Encounter = encounter;
        Combatant = combatant;
    }

    public string Outcome { get; }

    public string Encounter { get; }

    public string Combatant { get; }

    public bool Write { get; init; }

    public List<CharacterAction> Calls { get; } = [];

    public bool TryRoute(SqliteConnection connection, SqliteTransaction transaction, CampaignRow campaign, string characterEntityId, CharacterAction action,
        out RoutedResult result)
    {
        Calls.Add(action);
        if (characterEntityId != _entityId)
        {
            result = null!;
            return false;
        }

        var applies = Outcome == RouteOutcomes.Applied && action.Kind != CharacterActions.Rest;
        if (applies && Write)
        {
            DiceRollLog.Append(connection, transaction, new DiceRollRow(0, string.Empty, campaign.Id, null, "1d20", "router", 10, null, "{}", 1,
                "2026-09-01T12:00:00.000Z", EncounterId: null));
        }

        result = new RoutedResult(
            action.Kind == CharacterActions.Rest && Outcome == RouteOutcomes.Applied ? RouteOutcomes.InFight : Outcome,
            Encounter,
            Combatant,
            applies ? [$"{Combatant}: {action.Kind} {action.Amount}"] : [],
            applies && action.Kind == CharacterActions.Damage ? [new RoutedReminder("concentration_save", "Concentration save DC 10.", "combat {\"action\": \"concentration\"}")] : []);
        return true;
    }
}
