using Dapper;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Combat;
using DndMcp.Domain.Dice;
using DndMcp.Domain.Simulation;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Characters;
using DndMcp.Repository.Campaign.Combat;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tests.CampaignCharacters;
using DndMcp.Tests.CampaignScenarios;
using DndMcp.Tests.CampaignWrite;
using DndMcp.Tests.Srd.Combatants;
using Microsoft.Data.Sqlite;

namespace DndMcp.Tests.CampaignCombat;

/// <summary>
/// A faces-queue <see cref="IDiceRoller"/> (the repo uses no mocking library): each die takes the next queued face, and
/// running out throws, so a test that rolls more than it scripted fails loudly instead of rolling at random.
/// </summary>
internal sealed class QueueRoller : IDiceRoller
{
    private readonly Queue<int> _faces = new();

    public string Source => "scripted (combat tests)";

    /// <summary>Every side count rolled, in order.</summary>
    public List<int> Sides { get; } = [];

    public int Left => _faces.Count;

    public QueueRoller Push(params int[] faces)
    {
        foreach (var face in faces)
        {
            _faces.Enqueue(face);
        }

        return this;
    }

    public int Roll(int sides)
    {
        Sides.Add(sides);
        return _faces.TryDequeue(out var face) ? face : throw new InvalidOperationException($"The script ran out of faces (a d{sides} was rolled).");
    }
}

/// <summary>
/// One campaign world for the combat repository tests: the Phase 6 write fixture, the combat service over a scripted
/// roller, the reader, the loader, and a character writer routed through <see cref="CombatRouter"/>. The worlds:
/// <list type="bullet">
/// <item><see cref="Belmakor"/>: fixture A (FIX §2, contract §16) on the Phase 6 Belmakor scenario, C's sheets.</item>
/// <item><see cref="OnePiece"/>: fixture B (FIX §4) on the One Piece scenario, C's sheets, the fishman monk and the Nester.</item>
/// <item><see cref="Dm"/> / <see cref="Player"/>: C's small worlds (Hero Prime, Sidekick; Aria Vale, Bram) for mechanics.</item>
/// </list>
/// </summary>
internal sealed class CombatWorld : IDisposable
{
    private readonly IDisposable _owner;

    private CombatWorld(IDisposable owner, WriteFixture f, CampaignRow campaign)
    {
        _owner = owner;
        F = f;
        Campaign = campaign;
        Combat = new CombatService(f.Db.Database, Roller);
        Reader = new CombatReader(f.Db.Database);
        Loader = new EncounterSimulationLoader(f.Db.Database);
        Router = new CombatRouter(f.Db.Time);
        Characters = new CharacterWriter(f.Db.Database, Router);
    }

    public WriteFixture F { get; }

    public CampaignRow Campaign { get; private set; }

    public QueueRoller Roller { get; } = new();

    public CombatService Combat { get; }

    public CombatReader Reader { get; }

    public EncounterSimulationLoader Loader { get; }

    public CombatRouter Router { get; }

    public CharacterWriter Characters { get; }

    public CampaignDatabase Database => F.Db.Database;

    /// <summary>Fixture A's world: the Belmakor campaign (player, 2014) with C's fixture sheets.</summary>
    public static CombatWorld Belmakor()
    {
        var scenario = new BelmakorScenario();
        try
        {
            FixtureSheets.Belmakor(scenario);
            return new CombatWorld(scenario, scenario.F, scenario.F.Reload(scenario.Campaign));
        }
        catch
        {
            scenario.Dispose();
            throw;
        }
    }

    /// <summary>Fixture B's world: the One Piece campaign (DM, 2024) with C's fixture sheets, the monk and the Nester.</summary>
    public static CombatWorld OnePiece()
    {
        var scenario = OnePieceScenario.Build();
        try
        {
            FixtureSheets.OnePiece(scenario);
            return new CombatWorld(scenario, scenario.F, scenario.F.Reload(scenario.Campaign));
        }
        catch
        {
            scenario.Dispose();
            throw;
        }
    }

    /// <summary>C's DM world (2024 by default): Hero Prime and Sidekick in the party, The Villain (restricted).</summary>
    public static CombatWorld Dm(string ruleset = "2024")
    {
        var world = SheetWorld.Dm(ruleset);
        return new CombatWorld(world, world.F, world.Campaign);
    }

    /// <summary>C's player world (2014 by default): Aria Vale (mine) and Bram in the party, The Old Guard (restricted).</summary>
    public static CombatWorld Player(string ruleset = "2014")
    {
        var world = SheetWorld.Player(ruleset);
        return new CombatWorld(world, world.F, world.Campaign);
    }

    public void Reload() => Campaign = F.Reload(Campaign);

    /// <summary>A real stat block from the shipped corrected data.</summary>
    public static StatBlock Block(string edition, string slug) => CorrectedSrd.Shipped.StatBlock(edition, slug);

    /// <summary>A monster entry.</summary>
    public static CombatantRequest Monster(string edition, string slug, int count = 1, HpChoice? hp = null, string? name = null, string? side = null,
        string? character = null, bool hidden = false) => new()
    {
        Monster = Block(edition, slug),
        Count = count,
        Hp = hp,
        Name = name,
        Side = side,
        Character = character,
        Hidden = hidden,
    };

    /// <summary>update with a sheet JSON.</summary>
    public CharacterWriteResult Sheet(string character, string json, string? simProfile = null) =>
        Characters.Update(Campaign, character, SheetWorld.Spec(json), simProfile is null ? null : FixtureSheets.Profile(simProfile), WriteContext.Default);

    public SessionWriteResult StartSession(int number) => F.Sessions.Start(Campaign, number);

    public SqliteConnection Open() => F.Open();

    public long ChangeRows() => F.Count("SELECT count(*) FROM change_log");

    public string Id(string handle) => F.Entity(Campaign, handle).Id;

    public EncounterRow Encounter(string name)
    {
        using var connection = Open();
        return CombatStore.Encounters(connection, "WHERE name = @name ORDER BY rowid DESC", new { name }).First();
    }

    public EncounterState State(string name)
    {
        using var connection = Open();
        return CombatStore.Load(connection, Campaign, Encounter(name));
    }

    public CombatantState Combatant(string encounter, string name) => State(encounter).Combatants.Single(c => c.Name == name);

    public IReadOnlyList<CombatLogRow> Log(string encounter)
    {
        using var connection = Open();
        return CombatStore.Log(connection, Encounter(encounter).Id);
    }

    public IReadOnlyList<DiceRollRow> Dice()
    {
        using var connection = Open();
        return connection.Query<DiceRollRow>($"SELECT {DiceRollRow.Columns} FROM dice_roll ORDER BY seq").ToList();
    }

    public Domain.Characters.CharacterSheet SheetOf(string handle)
    {
        using var connection = Open();
        return CharacterSheetStore.Read(connection, Id(handle)) ?? throw new InvalidOperationException($"{handle} has no sheet.");
    }

    public void Apply(params CampaignOpSpec[] ops)
    {
        F.Apply(Campaign, ops);
        Reload();
    }

    public void Dispose() => _owner.Dispose();
}
