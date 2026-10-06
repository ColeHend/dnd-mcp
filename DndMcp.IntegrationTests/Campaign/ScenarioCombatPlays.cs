using System.Text.Json;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// What one step of an exit fight left, as the model and every other view read it: the author result of the combat call,
/// every non-author view's board right after it (<c>combat state {perspective}</c>), and what the store held (the
/// change_log's row count, the dice_roll rows, and how many dice the scripted roller had given).
/// </summary>
/// <param name="Step">FIX's step number (A8, B20, …; a step of several calls gets a suffix: A12-legendary).</param>
/// <param name="Arguments">The combat call's arguments, as sent.</param>
/// <param name="Text">The author result.</param>
/// <param name="ChangeRows">The change_log's row count after the step.</param>
/// <param name="DiceRows">The campaign's dice_roll row count after the step.</param>
/// <param name="Rolled">How many dice the server had rolled after the step (the scripted roller's count).</param>
/// <param name="SheetTables">Every sheet-side row of the campaign after the step (<see cref="ScenarioCombatStore.SheetTables"/>).</param>
/// <param name="Boards">Each non-author view's board after the step.</param>
public sealed record ScenarioCombatStep(
    string Step, string Arguments, string Text, long ChangeRows, int DiceRows, int Rolled, string SheetTables, IReadOnlyDictionary<string, string> Boards);

/// <summary>
/// One non-author read a sweep checks: which surface it is (its own forbidden list: a board legitimately shows what a sheet
/// line must not), its label, the text the model reads, whether it was a refusal, and what the caller typed that a refusal
/// may echo (taken out before the check, as <c>LeakAssert.CleanMessage</c> does).
/// </summary>
public sealed record ScenarioCombatRead(string Surface, string Label, string Text, bool Refused, string Typed)
{
    /// <summary>A board (<c>combat state {perspective}</c>).</summary>
    public const string Board = "board";

    /// <summary>A sheet read (<c>campaign_get include:["sheet"]</c>, <c>campaign_character get</c>).</summary>
    public const string Sheet = "sheet";

    /// <summary>A session read (<c>campaign_session get/list</c>).</summary>
    public const string Session = "session";

    /// <summary>A call made to be refused.</summary>
    public const string Probe = "probe";
}

/// <summary>
/// The shared shape of the two plays: a world built through the tools, its fight played once, step by step, with every
/// view's board read after every step and every view's refusal probes made once mid-fight (after
/// <see cref="ProbeStep"/>); then <c>end</c> and every non-author read of the fight, its session and the sheets.
/// Everything is RECORDED as the text the tools returned, so the test classes that share a play only read strings: no test
/// changes what another sees, and a test's failure message shows exactly what the model would have read.
/// </summary>
public abstract class ScenarioCombatPlay : IAsyncLifetime
{
    private readonly List<ScenarioCombatStep> _steps = [];

    /// <summary>The world (its server and the scripted dice).</summary>
    public ScenarioCombatServer World { get; private set; } = null!;

    /// <summary>The campaign's slug.</summary>
    public abstract string Campaign { get; }

    /// <summary>Every non-author perspective of the campaign (contract §0: party, table, public, every character, and dm in a player campaign).</summary>
    public abstract IReadOnlyList<string> Views { get; }

    /// <summary>Every character entity of the campaign, by handle (the sweeps knock on each one's door from every view).</summary>
    public abstract IReadOnlyList<string> Characters { get; }

    /// <summary>The fight's name.</summary>
    public abstract string Fight { get; }

    /// <summary>The fight's session number.</summary>
    public abstract int Session { get; }

    /// <summary>The steps, in order.</summary>
    public IReadOnlyList<ScenarioCombatStep> Steps => _steps;

    /// <summary>One step by its FIX number.</summary>
    public ScenarioCombatStep this[string step] => _steps.Single(s => s.Step == step);

    /// <summary>The change_log's row count once the session started (FIX A0/B0's N0).</summary>
    public long N0 { get; private set; }

    /// <summary>Every sheet-side row of the campaign once the session started (nothing in combat may touch them before end).</summary>
    public string SheetTablesBefore { get; private set; } = string.Empty;

    /// <summary>The same rows after <c>end</c>.</summary>
    public string SheetTablesAfter { get; private set; } = string.Empty;

    /// <summary>The author's <c>campaign_get include:["sheet"]</c> of the party before the fight.</summary>
    public string SheetsBefore { get; private set; } = string.Empty;

    /// <summary>The same read after <c>end</c>.</summary>
    public string SheetsAfter { get; private set; } = string.Empty;

    /// <summary><c>campaign_session start</c>'s result (A0/B0).</summary>
    public string SessionStart { get; private set; } = string.Empty;

    /// <summary>The <c>end</c> call's dry run, made just before it.</summary>
    public string EndDryRun { get; private set; } = string.Empty;

    /// <summary>The <c>end</c> call's author result.</summary>
    public string End { get; private set; } = string.Empty;

    /// <summary>The write-back's batch id (printed by <see cref="End"/>).</summary>
    public string EndBatch { get; private set; } = string.Empty;

    /// <summary>The change_log's row count after <c>end</c>.</summary>
    public long ChangeRowsAfterEnd { get; private set; }

    /// <summary>The fight's encounter id.</summary>
    public string EncounterId { get; private set; } = string.Empty;

    /// <summary>The author's <c>combat state {encounter: "last"}</c> after <c>end</c>.</summary>
    public string LastAuthor { get; private set; } = string.Empty;

    /// <summary>
    /// Every non-author read after <c>end</c>, per view, labelled: the board by "current", "last" and the fight's name; a
    /// <c>campaign_get include:["sheet"]</c> and a <c>campaign_character get</c> of every character; the list form; the
    /// session's get and the session list; and the refusal probes (<see cref="RefusalProbes"/>). Refusals are kept as the
    /// text the model reads, with whether it was refused and what the caller typed that a refusal may echo.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ScenarioCombatRead>> ReadsAfterEnd { get; private set; } =
        new Dictionary<string, IReadOnlyList<ScenarioCombatRead>>();

    /// <summary>The author's <c>campaign_session get</c> of the fight's session after <c>end</c>.</summary>
    public string SessionAuthor { get; private set; } = string.Empty;

    /// <summary>
    /// The refusal probes (<see cref="RefusalProbes"/>) of every view made DURING the fight, right after
    /// <see cref="ProbeStep"/>: while a fight runs, a <c>damage</c> with a <c>perspective</c> could only be refused for the
    /// perspective itself, so its refusal proves that rule (after the end, "no fight is running" would refuse it anyway).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ScenarioCombatRead>> ProbesDuringFight { get; private set; } =
        new Dictionary<string, IReadOnlyList<ScenarioCombatRead>>();

    /// <summary>The step after which the refusal probes run during the fight.</summary>
    public abstract string ProbeStep { get; }

    /// <summary>Starts the world, plays the fight and ends it, recording every read on the way.</summary>
    public async Task InitializeAsync()
    {
        World = await BuildAsync();
        await BeforeFightAsync();
        SheetsBefore = await World.Call("campaign_get", SheetsCall);
        SessionStart = await World.Call("campaign_session", SessionCall);
        N0 = World.Store.ChangeRows();
        SheetTablesBefore = World.Store.SheetTables(Campaign);

        foreach (var (step, arguments, faces) in Script)
        {
            World.Dice.Enqueue(faces);
            var text = await World.Combat(arguments);
            Assert.True(World.Dice.Remaining == 0, $"{step} left {World.Dice.Remaining} queued faces unrolled");
            var boards = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var view in Views)
            {
                boards[view] = await World.Combat($$"""{"action": "state", "perspective": "{{view}}"}""");
            }

            _steps.Add(new ScenarioCombatStep(
                step, arguments, text, World.Store.ChangeRows(), World.Store.Dice(Campaign).Count, World.Dice.Rolled.Count, World.Store.SheetTables(Campaign), boards));
            await AfterStepAsync(step);
            if (step == ProbeStep)
            {
                var probes = new Dictionary<string, IReadOnlyList<ScenarioCombatRead>>(StringComparer.Ordinal);
                foreach (var view in Views)
                {
                    probes[view] = await ProbeAllAsync(view);
                }

                ProbesDuringFight = probes;
            }
        }

        EncounterId = World.Store.EncounterId(Campaign, Fight);
        EndDryRun = await World.Combat(EndDryRunCall);
        End = await World.Combat(EndCall);
        EndBatch = ScenarioCombatText.Batch(End);
        ChangeRowsAfterEnd = World.Store.ChangeRows();
        SheetTablesAfter = World.Store.SheetTables(Campaign);
        SheetsAfter = await World.Call("campaign_get", SheetsCall);
        LastAuthor = await World.Combat("""{"action": "state", "encounter": "last"}""");
        await OnEndedAsync();

        SessionAuthor = await World.Call("campaign_session", $$"""{"action": "get", "campaign": "{{Campaign}}", "session": {{Session}}}""");
        var reads = new Dictionary<string, IReadOnlyList<ScenarioCombatRead>>(StringComparer.Ordinal);
        foreach (var view in Views)
        {
            reads[view] = await ReadAllAsync(view);
        }

        ReadsAfterEnd = reads;
        await AfterEndAsync();
    }

    /// <summary>Stops the server and deletes its data.</summary>
    public async Task DisposeAsync()
    {
        if (World is not null)
        {
            await World.DisposeAsync();
        }
    }

    /// <summary>
    /// The calls a view may get refused, beside the reads (contract §15 X2: "each call's refusal text under every
    /// non-author perspective"): a perspective on an action that takes none, a perspective naming a character the view
    /// cannot see (by handle and by a prefix of its slug), and a get of a hidden character by handle. A refusal must be
    /// as clean as a page once the caller's own typed text is taken out. They run twice: during the fight (after
    /// <see cref="ProbeStep"/>, where only the perspective rule can refuse a combat action) and after the end.
    /// </summary>
    protected abstract IReadOnlyList<(string Label, string Tool, string Arguments, string Typed)> RefusalProbes(string view);

    /// <summary>Builds the world (server, Phase 6 world, fixture sheets).</summary>
    protected abstract Task<ScenarioCombatServer> BuildAsync();

    /// <summary>The fight's steps: label, combat arguments, the faces the server rolls during it.</summary>
    protected abstract IEnumerable<(string Step, string Arguments, int[] Faces)> Script { get; }

    /// <summary>The A0/B0 campaign_session start arguments.</summary>
    protected abstract string SessionCall { get; }

    /// <summary>The author's campaign_get of the party's sheets.</summary>
    protected abstract string SheetsCall { get; }

    /// <summary>The end call's arguments.</summary>
    protected abstract string EndCall { get; }

    /// <summary>The end call's arguments as a dry run.</summary>
    protected abstract string EndDryRunCall { get; }

    /// <summary>What a play reads before the session starts (the difficulty goldens, the planned simulations).</summary>
    protected virtual Task BeforeFightAsync() => Task.CompletedTask;

    /// <summary>What a play reads after one step (the simulations at their FIX step).</summary>
    protected virtual Task AfterStepAsync(string step) => Task.CompletedTask;

    /// <summary>What a play does right after end, before the sweeps' reads (nothing it does may change what they read).</summary>
    protected virtual Task OnEndedAsync() => Task.CompletedTask;

    /// <summary>What a play does after end and the sweeps' reads (the rest-roll session).</summary>
    protected virtual Task AfterEndAsync() => Task.CompletedTask;

    private async Task<List<ScenarioCombatRead>> ReadAllAsync(string view)
    {
        var reads = new List<ScenarioCombatRead>();
        async Task Read(string surface, string label, string tool, string arguments, string typed)
        {
            var (text, refused) = await World.Read(tool, arguments);
            reads.Add(new ScenarioCombatRead(surface, label, text, refused, typed));
        }

        await Read(ScenarioCombatRead.Board, "board current", "combat", $$"""{"action": "state", "perspective": "{{view}}"}""", string.Empty);
        await Read(ScenarioCombatRead.Board, "board last", "combat", $$"""{"action": "state", "perspective": "{{view}}", "encounter": "last"}""", string.Empty);
        await Read(ScenarioCombatRead.Board, "board by name", "combat",
            $$"""{"action": "state", "perspective": "{{view}}", "encounter": {{JsonSerializer.Serialize(Fight)}}}""", string.Empty);
        foreach (var handle in Characters)
        {
            await Read(ScenarioCombatRead.Sheet, $"campaign_get {handle}", "campaign_get",
                $$"""{"campaign": "{{Campaign}}", "refs": ["{{handle}}"], "include": ["sheet"], "perspective": "{{view}}"}""", handle);
            await Read(ScenarioCombatRead.Sheet, $"campaign_character get {handle}", "campaign_character",
                $$"""{"action": "get", "campaign": "{{Campaign}}", "character": "{{handle}}", "perspective": "{{view}}"}""", handle);
        }

        await Read(ScenarioCombatRead.Sheet, "campaign_character get (no character)", "campaign_character",
            $$"""{"action": "get", "campaign": "{{Campaign}}", "perspective": "{{view}}"}""", string.Empty);
        await Read(ScenarioCombatRead.Session, $"session {Session}", "campaign_session",
            $$"""{"action": "get", "campaign": "{{Campaign}}", "session": {{Session}}, "perspective": "{{view}}"}""", Session.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Read(ScenarioCombatRead.Session, "sessions", "campaign_session", $$"""{"action": "list", "campaign": "{{Campaign}}", "perspective": "{{view}}"}""", string.Empty);
        reads.AddRange(await ProbeAllAsync(view));
        return reads;
    }

    private async Task<List<ScenarioCombatRead>> ProbeAllAsync(string view)
    {
        var reads = new List<ScenarioCombatRead>();
        foreach (var (label, tool, arguments, typed) in RefusalProbes(view))
        {
            var (text, refused) = await World.Read(tool, arguments);
            reads.Add(new ScenarioCombatRead(ScenarioCombatRead.Probe, label, text, refused, typed));
        }

        return reads;
    }
}

/// <summary>The test classes that share one fixture-A play (collection fixtures: built once, read by every class in it).</summary>
[CollectionDefinition(Name)]
public sealed class ScenarioCombatBelmakorCollection : ICollectionFixture<ScenarioCombatBelmakorPlay>
{
    /// <summary>The collection's name.</summary>
    public const string Name = "Scenario combat: fixture A";
}

/// <summary>The test classes that share one fixture-B play.</summary>
[CollectionDefinition(Name)]
public sealed class ScenarioCombatOnePieceCollection : ICollectionFixture<ScenarioCombatOnePiecePlay>
{
    /// <summary>The collection's name.</summary>
    public const string Name = "Scenario combat: fixture B";
}

/// <summary>
/// Fixture A through the MCP tools (FIX §2 as amended by contract §16), played once per test class that shares it: the
/// Belmakor world and its sheets built through the tools, <c>encounter_difficulty party:"campaign"</c> (§2.4), session 4
/// started (A0), A1-A31 with every non-author board after every step, the encounter form of <c>balance_simulate</c> and the
/// explicit call it equals (§2.5, after A2), <c>from_state</c> twice at the start of round 2 (§2.6, after A19), the end's
/// dry run and the end (A33), and every non-author read after it. The roller is never given a face: fixture A gives every
/// value, so any server roll fails the play.
/// </summary>
public sealed class ScenarioCombatBelmakorPlay : ScenarioCombatPlay
{
    /// <summary>The party's six living members and Tristan, then the old king: every character of the campaign.</summary>
    public static readonly IReadOnlyList<string> CharacterHandles =
    [
        "character:belmakor", "character:vars", "character:ignis", "character:serif", "character:torch", "character:aiden-ironstar",
        "character:tristan", "character:old-king",
    ];

    /// <summary>Every non-author view of the player campaign (dm is one: contract §0).</summary>
    public static readonly IReadOnlyList<string> NonAuthorViews = ["party", "table", "dm", "public", .. CharacterHandles];

    /// <summary>The explicit balance_simulate call the encounter form equals (contract §6.11: the party's character entries in order_key order, Serif left out; the enemies grouped).</summary>
    public const string ExplicitSimulation = """
        {"party": [{"character": "character:aiden-ironstar"}, {"character": "character:belmakor"}, {"character": "character:ignis"},
                   {"character": "character:torch"}, {"character": "character:vars"}],
         "enemies": [{"monster": "2014/monster/mummy-lord"}, {"monster": "2014/monster/mummy", "count": 2}],
         "campaign": "belmakor", "iterations": 2000, "seed": 7}
        """;

    /// <inheritdoc/>
    public override string Campaign => "belmakor";

    /// <inheritdoc/>
    public override IReadOnlyList<string> Views => NonAuthorViews;

    /// <inheritdoc/>
    public override IReadOnlyList<string> Characters => CharacterHandles;

    /// <inheritdoc/>
    public override string Fight => ScenarioCombatFixtures.CryptName;

    /// <inheritdoc/>
    public override int Session => 4;

    /// <inheritdoc/>
    public override string ProbeStep => "A8";

    /// <summary>§2.4: <c>encounter_difficulty party:"campaign"</c>, both editions.</summary>
    public string Difficulty { get; private set; } = string.Empty;

    /// <summary>§2.4: the same with no edition (the campaign's 2014).</summary>
    public string DifficultyDefaultEdition { get; private set; } = string.Empty;

    /// <summary>§2.5: the encounter form (active, round 0, after A2).</summary>
    public string Simulation { get; private set; } = string.Empty;

    /// <summary>§2.5: the explicit call (<see cref="ExplicitSimulation"/>), made beside it.</summary>
    public string SimulationExplicit { get; private set; } = string.Empty;

    /// <summary>§2.6: <c>from_state</c> at the start of round 2 (after A19), and the same call again.</summary>
    public (string First, string Again) FromState { get; private set; }

    /// <inheritdoc/>
    protected override IEnumerable<(string Step, string Arguments, int[] Faces)> Script =>
        ScenarioCombatFixtures.StepsA.Select(s => (s.Step, s.Arguments, Array.Empty<int>()));

    /// <inheritdoc/>
    protected override string SessionCall => ScenarioCombatFixtures.SessionA;

    /// <inheritdoc/>
    protected override string SheetsCall => """
        {"campaign": "belmakor", "refs": ["character:aiden-ironstar", "character:belmakor", "character:ignis", "character:torch", "character:serif", "character:vars"],
         "include": ["sheet"]}
        """;

    /// <inheritdoc/>
    protected override string EndCall => ScenarioCombatFixtures.EndA;

    /// <inheritdoc/>
    protected override string EndDryRunCall => """{"action": "end", "outcome": "The crypt is cleared.", "dry_run": true}""";

    /// <inheritdoc/>
    protected override Task<ScenarioCombatServer> BuildAsync() => ScenarioCombatServer.BelmakorAsync();

    /// <inheritdoc/>
    protected override async Task BeforeFightAsync()
    {
        Difficulty = await World.Call("encounter_difficulty",
            """{"party": "campaign", "campaign": "belmakor", "monsters": [{"ref": "2014/monster/mummy-lord"}, {"ref": "2014/monster/mummy", "count": 2}], "edition": "both"}""");
        DifficultyDefaultEdition = await World.Call("encounter_difficulty",
            """{"party": "campaign", "campaign": "belmakor", "monsters": [{"ref": "2014/monster/mummy-lord"}, {"ref": "2014/monster/mummy", "count": 2}]}""");
    }

    /// <inheritdoc/>
    protected override async Task AfterStepAsync(string step)
    {
        if (step == "A2")
        {
            Simulation = await World.Call("balance_simulate", """{"encounter": "current", "campaign": "belmakor", "iterations": 2000, "seed": 7}""");
            SimulationExplicit = await World.Call("balance_simulate", ExplicitSimulation);
        }
        else if (step == "A19")
        {
            const string Call = """{"encounter": "current", "campaign": "belmakor", "from_state": true, "iterations": 2000, "seed": 7}""";
            FromState = (await World.Call("balance_simulate", Call), await World.Call("balance_simulate", Call));
        }
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<(string Label, string Tool, string Arguments, string Typed)> RefusalProbes(string view) =>
    [
        ("combat damage with a perspective", "combat", $$"""{"action": "damage", "targets": ["torch"], "amount": 1, "perspective": "{{view}}"}""", string.Empty),
        ("board of the true name", "combat", """{"action": "state", "perspective": "character:keras"}""", "keras"),
        ("board of a prefix of the old king", "combat", """{"action": "state", "perspective": "character:old"}""", "old"),
        ("sheet of the true name", "campaign_character", $$"""{"action": "get", "campaign": "belmakor", "character": "keras", "perspective": "{{view}}"}""", "keras"),
        ("campaign_get of the true name", "campaign_get",
            $$"""{"campaign": "belmakor", "refs": ["character:keras"], "include": ["sheet"], "perspective": "{{view}}"}""", "character:keras"),
        ("sheet by a prefix", "campaign_character", $$"""{"action": "get", "campaign": "belmakor", "character": "old", "perspective": "{{view}}"}""", "old"),
    ];
}

/// <summary>
/// Fixture B through the MCP tools (FIX §4 as amended by contract §16), played once per test class that shares it: the One
/// Piece world, the monk, the Nester, the sheets and the potions built through the tools; <c>encounter_difficulty
/// party:"campaign"</c> (§4.6); a planned copy of the fight outside its lair simulated in the encounter form and as the
/// explicit call it equals (§4.7); session 13 started (B0); B1-B26 with the scripted faces queued step by step
/// ([5] at B16, [4, 5, 2, 3] at B20, [3, 2] at B22) and every non-author board after every step; the lair fight's encounter
/// form (after B2) and <c>from_state</c> twice at the start of round 3 (§4.8, after B25); the end's dry run and the end
/// (B28); every non-author read after it; then session 13 ended, the Nester given an NPC sheet with the subclass
/// "Void-touched" (contract §15 X2's probe), the arch mage (an NPC the party sees, not a member) given a sheet and hurt,
/// and session 14 with three short rests whose Hit Dice the server rolls (Björn's open; the Nester's and the arch mage's
/// secret, D11), read from every view. Before the fight it also reads §4.6's book-only and not-in-lair variants.
/// </summary>
public sealed class ScenarioCombatOnePiecePlay : ScenarioCombatPlay
{
    /// <summary>Every character of the One Piece world and fixture B's two.</summary>
    public static readonly IReadOnlyList<string> CharacterHandles =
    [
        "character:bjorn-mountainfell", "character:dragon-slayer", "character:fishman-monk", "character:the-nester", "character:protector",
        "character:peaceful-one", "character:mistaken-one", "character:dutiful-one", "character:nadar", "character:arch-mage",
    ];

    /// <summary>Every non-author view of the DM campaign (the dm is the author there: contract §0).</summary>
    public static readonly IReadOnlyList<string> NonAuthorViews = ["party", "table", "public", .. CharacterHandles];

    /// <summary>The player views (FIX §5.2's list applies to them; the NPCs know some of its words).</summary>
    public static readonly IReadOnlyList<string> PlayerViews =
        ["party", "table", "public", "character:bjorn-mountainfell", "character:dragon-slayer", "character:fishman-monk"];

    /// <summary>The planned copy of the fight, outside its lair, whose encounter form has an explicit equal.</summary>
    public const string PlannedName = "The dark station without its lair (planned)";

    /// <summary>
    /// The explicit call the planned copy's encounter form equals (contract §6.11: a planned encounter with no party
    /// combatants takes the campaign's current party from sheets, in roster order).
    /// </summary>
    public const string ExplicitSimulation = """
        {"party": [{"character": "character:bjorn-mountainfell"}, {"character": "character:dragon-slayer"}, {"character": "character:fishman-monk"}],
         "enemies": [{"monster": "2024/monster/aboleth"}], "campaign": "one-piece", "iterations": 2000, "seed": 7}
        """;

    /// <inheritdoc/>
    public override string Campaign => "one-piece";

    /// <inheritdoc/>
    public override IReadOnlyList<string> Views => NonAuthorViews;

    /// <inheritdoc/>
    public override IReadOnlyList<string> Characters => CharacterHandles;

    /// <inheritdoc/>
    public override string Fight => ScenarioCombatFixtures.StationName;

    /// <inheritdoc/>
    public override int Session => 13;

    /// <inheritdoc/>
    public override string ProbeStep => "B15";

    /// <summary>§4.6: <c>encounter_difficulty party:"campaign"</c>, both editions, in lair.</summary>
    public string Difficulty { get; private set; } = string.Empty;

    /// <summary>§4.6's book-only variant: <c>effective_level_offset: 0</c> wins over the campaign's 1.</summary>
    public string DifficultyBookOnly { get; private set; } = string.Empty;

    /// <summary>§4.6's not-in-lair variant: the same Aboleth outside its lair (5,900 XP in both editions).</summary>
    public string DifficultyNoLair { get; private set; } = string.Empty;

    /// <summary>The planned copy's <c>prepare</c> result.</summary>
    public string Prepared { get; private set; } = string.Empty;

    /// <summary>§4.7: the planned copy's encounter form.</summary>
    public string PlannedSimulation { get; private set; } = string.Empty;

    /// <summary>§4.7: the explicit call (<see cref="ExplicitSimulation"/>).</summary>
    public string SimulationExplicit { get; private set; } = string.Empty;

    /// <summary>The lair fight's encounter form after B2 (pinned in the encounter form only: contract §6.11).</summary>
    public string LairSimulation { get; private set; } = string.Empty;

    /// <summary>§4.8: <c>from_state</c> at the start of round 3 (after B25), and the same call again.</summary>
    public (string First, string Again) FromState { get; private set; }

    /// <summary>The end's proposal (D19) sent exactly as printed: a campaign_write dry run that writes nothing.</summary>
    public string ProposalDryRun { get; private set; } = string.Empty;

    /// <summary>The author's <c>campaign_get</c> of the Nester after the end and the proposal's dry run (FIX §4.3: still alive).</summary>
    public string NesterAfterEnd { get; private set; } = string.Empty;

    /// <summary>The planned copy's board for every view, read by its name (a planned fight is never shown).</summary>
    public IReadOnlyDictionary<string, string> PlannedBoards { get; private set; } = new Dictionary<string, string>();

    /// <summary>The Nester's NPC sheet (subclass "Void-touched") as the author's update result.</summary>
    public string NesterSheet { get; private set; } = string.Empty;

    /// <summary>
    /// The three short rests of session 14: Björn's (a current member Shown to the party: open), the Nester's (restricted:
    /// secret) and the arch mage's (an NPC the party SEES but not a member: secret by D11's roster clause alone).
    /// </summary>
    public (string Bjorn, string Nester, string ArchMage) Rests { get; private set; }

    /// <summary>The arch mage's NPC sheet (a Con score, so the rest's Hit Dice heal) and the damage it rests from, as the author's results.</summary>
    public (string Sheet, string Damage) ArchMage { get; private set; }

    /// <summary>Session 14 (the rest rolls) as the author reads it.</summary>
    public string RestSessionAuthor { get; private set; } = string.Empty;

    /// <summary>Session 14 as every non-author view reads it (refused for those that may not see it).</summary>
    public IReadOnlyDictionary<string, ScenarioCombatRead> RestSession { get; private set; } = new Dictionary<string, ScenarioCombatRead>();

    /// <summary>After the Nester's sheet exists: every view's campaign_get of it with include sheet, its campaign_character get, and the list form.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ScenarioCombatRead>> NesterSheetReads { get; private set; } =
        new Dictionary<string, IReadOnlyList<ScenarioCombatRead>>();

    /// <inheritdoc/>
    protected override IEnumerable<(string Step, string Arguments, int[] Faces)> Script => ScenarioCombatFixtures.StepsB;

    /// <inheritdoc/>
    protected override string SessionCall => ScenarioCombatFixtures.SessionB;

    /// <inheritdoc/>
    protected override string SheetsCall => """
        {"campaign": "one-piece", "refs": ["character:bjorn-mountainfell", "character:dragon-slayer", "character:fishman-monk"], "include": ["sheet"]}
        """;

    /// <inheritdoc/>
    protected override string EndCall => ScenarioCombatFixtures.EndB;

    /// <inheritdoc/>
    protected override string EndDryRunCall => ScenarioCombatFixtures.EndBDryRun;

    /// <inheritdoc/>
    protected override Task<ScenarioCombatServer> BuildAsync() => ScenarioCombatServer.OnePieceAsync();

    /// <inheritdoc/>
    protected override async Task BeforeFightAsync()
    {
        Difficulty = await World.Call("encounter_difficulty",
            """{"party": "campaign", "campaign": "one-piece", "monsters": [{"ref": "2024/monster/aboleth", "lair": true}], "edition": "both"}""");
        DifficultyBookOnly = await World.Call("encounter_difficulty",
            """{"party": "campaign", "campaign": "one-piece", "monsters": [{"ref": "2024/monster/aboleth", "lair": true}], "edition": "both", "effective_level_offset": 0}""");
        DifficultyNoLair = await World.Call("encounter_difficulty",
            """{"party": "campaign", "campaign": "one-piece", "monsters": [{"ref": "2024/monster/aboleth"}], "edition": "both"}""");
        Prepared = await World.Combat($$"""{"action": "prepare", "campaign": "one-piece", "name": "{{PlannedName}}", "combatants": [{"srd": "2024/monster/aboleth"}]}""");
        PlannedSimulation = await World.Call("balance_simulate", $$"""{"encounter": "{{PlannedName}}", "campaign": "one-piece", "iterations": 2000, "seed": 7}""");
        SimulationExplicit = await World.Call("balance_simulate", ExplicitSimulation);
        var boards = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var view in Views)
        {
            boards[view] = await World.Combat($$"""{"action": "state", "campaign": "one-piece", "perspective": "{{view}}", "encounter": "{{PlannedName}}"}""");
        }

        PlannedBoards = boards;
    }

    /// <inheritdoc/>
    protected override async Task AfterStepAsync(string step)
    {
        if (step == "B2")
        {
            LairSimulation = await World.Call("balance_simulate", """{"encounter": "current", "campaign": "one-piece", "iterations": 2000, "seed": 7}""");
        }
        else if (step == "B25")
        {
            const string Call = """{"encounter": "current", "campaign": "one-piece", "from_state": true, "iterations": 2000, "seed": 7}""";
            FromState = (await World.Call("balance_simulate", Call), await World.Call("balance_simulate", Call));
        }
    }

    /// <inheritdoc/>
    protected override async Task OnEndedAsync()
    {
        var proposal = Assert.Single(ScenarioCombatText.PrintedCalls(End), c => c.StartsWith("campaign_write {", StringComparison.Ordinal));
        ProposalDryRun = await World.Call("campaign_write", ScenarioCombatText.Arguments(proposal));
        NesterAfterEnd = await World.Call("campaign_get", """{"campaign": "one-piece", "refs": ["character:the-nester"]}""");
    }

    /// <inheritdoc/>
    protected override async Task AfterEndAsync()
    {
        await World.Call("campaign_session", """{"campaign": "one-piece", "action": "end", "recap_md": "The party came back up from the deep."}""");
        NesterSheet = await World.Call("campaign_character", """
            {"action": "update", "campaign": "one-piece", "character": "character:the-nester",
             "sheet": {"classes": [{"class": "warlock", "subclass": "Void-touched", "level": 10}], "max_hp": 90, "hp": 40}}
            """);
        var nester = new Dictionary<string, IReadOnlyList<ScenarioCombatRead>>(StringComparer.Ordinal);
        foreach (var view in Views)
        {
            var reads = new List<ScenarioCombatRead>();
            foreach (var (label, tool, arguments, typed) in new[]
            {
                ("campaign_get the Nester", "campaign_get",
                    $$"""{"campaign": "one-piece", "refs": ["character:the-nester"], "include": ["sheet"], "perspective": "{{view}}"}""", "character:the-nester"),
                ("campaign_character get the Nester", "campaign_character",
                    $$"""{"action": "get", "campaign": "one-piece", "character": "character:the-nester", "perspective": "{{view}}"}""", "character:the-nester"),
                ("campaign_character get (no character)", "campaign_character", $$"""{"action": "get", "campaign": "one-piece", "perspective": "{{view}}"}""", string.Empty),
            })
            {
                var (text, refused) = await World.Read(tool, arguments);
                reads.Add(new ScenarioCombatRead(ScenarioCombatRead.Sheet, label, text, refused, typed));
            }

            nester[view] = reads;
        }

        NesterSheetReads = nester;
        ArchMage = (
            await World.Call("campaign_character", """
                {"action": "update", "campaign": "one-piece", "character": "character:arch-mage",
                 "sheet": {"classes": [{"class": "wizard", "level": 9}], "abilities": {"con": 12}}}
                """),
            await World.Call("campaign_character", """{"action": "damage", "campaign": "one-piece", "character": "character:arch-mage", "amount": 10}"""));
        await World.Call("campaign_session", """
            {"campaign": "one-piece", "action": "start", "session": 14, "played_on": "2026-10-10", "precision": "day",
             "attendance": [{"character": "character:bjorn-mountainfell"}, {"character": "character:fishman-monk"}, {"character": "character:dragon-slayer"}]}
            """);
        World.Dice.Enqueue(7);
        var bjorn = await World.Call("campaign_character", """{"action": "rest", "campaign": "one-piece", "character": "character:bjorn-mountainfell", "kind": "short", "hit_dice": 1}""");
        World.Dice.Enqueue(5);
        var theNester = await World.Call("campaign_character", """{"action": "rest", "campaign": "one-piece", "character": "character:the-nester", "kind": "short", "hit_dice": 1}""");
        World.Dice.Enqueue(4);
        var archMage = await World.Call("campaign_character", """{"action": "rest", "campaign": "one-piece", "character": "character:arch-mage", "kind": "short", "hit_dice": 1}""");
        Assert.Equal(0, World.Dice.Remaining);
        Rests = (bjorn, theNester, archMage);
        RestSessionAuthor = await World.Call("campaign_session", """{"action": "get", "campaign": "one-piece", "session": 14}""");
        var sessions = new Dictionary<string, ScenarioCombatRead>(StringComparer.Ordinal);
        foreach (var view in Views)
        {
            var (text, refused) = await World.Read("campaign_session", $$"""{"action": "get", "campaign": "one-piece", "session": 14, "perspective": "{{view}}"}""");
            sessions[view] = new ScenarioCombatRead(ScenarioCombatRead.Session, "session 14", text, refused, "14");
        }

        RestSession = sessions;
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<(string Label, string Tool, string Arguments, string Typed)> RefusalProbes(string view) =>
    [
        ("combat damage with a perspective", "combat", $$"""{"action": "damage", "targets": ["aboleth"], "amount": 1, "perspective": "{{view}}"}""", string.Empty),
        ("board of a prefix of the Nester", "combat", """{"action": "state", "perspective": "character:the-nest"}""", "the-nest"),
        ("sheet of the Nester by a prefix", "campaign_character", $$"""{"action": "get", "campaign": "one-piece", "character": "the-nest", "perspective": "{{view}}"}""", "the-nest"),
        ("campaign_get of the Nester", "campaign_get",
            $$"""{"campaign": "one-piece", "refs": ["character:the-nester"], "include": ["sheet"], "perspective": "{{view}}"}""", "character:the-nester"),
        ("session 14, not played yet", "campaign_session", $$"""{"action": "get", "campaign": "one-piece", "session": 14, "perspective": "{{view}}"}""", "14"),
    ];
}
