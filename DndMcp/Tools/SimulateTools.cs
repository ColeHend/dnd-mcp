using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation;
using DndMcp.Formatting;
using DndMcp.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DndMcp.Tools;

/// <summary>
/// <c>balance_simulate</c>: Monte Carlo fights between a party and enemies (<see cref="Simulator"/>), reported as outcome
/// probabilities with confidence intervals, rounds and per-combatant statistics. Thin by design, like
/// <see cref="BalanceTools"/>: bind the spec classes, resolve every SRD monster to its stat block
/// (<see cref="StatBlockService"/>), hand everything to the Domain, render with <see cref="SimulationMarkdown"/>.
///
/// <para>
/// <b>Every check lives in the Domain</b> except the two only the host can make: whether a monster name is in the SRD
/// (<see cref="StatBlockService"/>, with <c>encounter_difficulty</c>'s resolution and close names) and the argument
/// guard's schema checks. A monster entry never reaches the Domain without its stat block: <see cref="Simulator"/> treats
/// that as a host bug (an <see cref="ArgumentException"/>, the SDK's generic error), so a blank monster name is refused here.
/// </para>
/// <para>
/// <b>Which edition's stat block.</b> A monster is looked up in its entry's <c>edition</c>, else the fight's
/// <c>edition</c>, else the party's (the first party entry's build or entry edition), else 2024: a party of 2014 builds
/// fights 2014 ogres unless told otherwise, as the fight itself follows the party's rules.
/// </para>
/// <para>
/// <b>Not idempotent, deliberately.</b> Without a seed each call draws a fresh 64-bit seed from the OS
/// (<see cref="RandomNumberGenerator"/>: the host, never the Domain, which only ever sees the seed) and echoes it with how
/// to pass it back; with a seed the result is identical at any thread count. The seed is a JSON number or a decimal
/// string, because a JavaScript-based client reads numbers as doubles and silently rounds a seed above 2^53; the echo
/// gives it as a string for that reason.
/// </para>
/// <para>
/// <b>Schema size.</b> <c>party</c> carries the full entry schema (the build DSL inside it is most of it); <c>enemies</c>
/// has the same shape and is published untyped (<see cref="SameShapeAsAttribute"/>), checked by the argument guard
/// against <c>party</c>'s schema item by item, as <c>balance_compare</c>'s <c>variant</c> is against its baseline. A
/// second typed copy would push the tool's definition past 32 KB.
/// </para>
/// <para>
/// <b>Progress and cancellation.</b> The rules index wait, the wait for another call's fights (<see cref="FightGate"/>)
/// and then the fights (after each chunk, a second or so of work at most) report through one <see cref="ProgressPump"/>,
/// which sends the latest value at once and then at most every quarter second, each send awaited before the next, so a
/// watching user sees the run move and Claude Code's idle timeout is reset on a long run. The fights stop between fights
/// when the request is cancelled or the client closes the session (<see cref="SessionEnded"/>). The work is bounded by
/// <see cref="SimulationLimits.WorkBudget"/> (about 20 s at worst): a run over it is refused, except that precision mode is
/// charged only its first batch and stops its later batches at the limit, saying so.
/// </para>
/// <para>
/// <b>The fights never take the whole thread pool</b> (<see cref="FightThreads"/>, <see cref="FightGate"/>). The stdio
/// transport reads the client's next message — its <c>notifications/cancelled</c> among them — on a pool thread, and the
/// pump's sends need one too. With every pool thread in the parallel loop, a cancel was read 12.6 s after it was sent
/// while 15 cores burned on an abandoned run, and the first progress came at 81,920 of 100,000 fights: exactly what the
/// two promises above exist to prevent, and every other request on the session waited behind the run as well.
/// </para>
/// </summary>
public sealed class SimulateTools
{
    private const string Example =
        "{\"party\": [{\"archetype\": \"fighter\", \"level\": 5, \"count\": 2}, {\"archetype\": \"cleric\", \"level\": 5}, " +
        "{\"archetype\": \"wizard\", \"level\": 5}], \"enemies\": [{\"monster\": \"ogre\", \"count\": 3}], \"seed\": 42}";

    private const string EntryExample = "{\"monster\": \"Ogre\", \"count\": 3}";

    /// <summary>
    /// The threads the fights may use: every core but one (at least one). Reading it the first time also raises the
    /// thread pool's minimum to four above the core count, once per process, so the pool hands out the threads the
    /// fights leave free at once rather than injecting them one every half second or so after it sees starvation: the
    /// stdio transport's read of stdin can hold one, and the pump needs one per send. Both halves are needed (measured on
    /// 16 cores, 100,000 fights): capped alone, the cancel was still unread 5 s later and the first progress came at
    /// 81,920 fights, as with no cap; raised alone, the parallel loop takes the raised threads too, and the first progress
    /// came at 30,000 to 54,000 fights. Together the cancel was read in 10 ms, and the first progress comes with the
    /// first chunks (<c>SimulateToolTests</c>' long stdio runs pin both). The cap holds across calls only because one
    /// call fights at a time (<see cref="FightGate"/>). The cost is one core of throughput; the report does not depend on
    /// the thread count (<see cref="Simulator"/>).
    /// </summary>
    internal static readonly int FightThreads = ReservePoolThreads();

    /// <summary>
    /// One call's fights at a time, process-wide, so <see cref="FightThreads"/> caps the fights of every call together.
    /// <c>balance_simulate</c> is read-only, so a client may run two at once ("party A, and party B"); each with a set of
    /// its own, two long runs wanted 30 pool threads of the 20 the minimum provides, and the starvation came back: a ping
    /// took 6 to 25 s, each call's first progress came at 60% of its run, and a cancel was read 3.3 s late. A call that
    /// waits here reports it every second (<see cref="WaitForTheFightThreadsAsync"/>), so its idle timer is reset too, and
    /// stops waiting when it is cancelled. The total time is the same as sharing the threads; the first call's answer
    /// comes sooner.
    /// </summary>
    private static readonly SemaphoreSlim FightGate = new(1, 1);

    /// <summary>How often a call waiting for <see cref="FightGate"/> reports it: as the rules index wait does.</summary>
    private static readonly TimeSpan GateReportInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The answer to a call whose session ended mid-run (<see cref="SessionEnded"/>). Nobody reads it; returning it rather
    /// than the cancellation keeps a client's normal shutdown out of the log as a failed tool call with a stack trace.
    /// </summary>
    internal const string SessionEndedText = "The session ended; the simulation was stopped.";

    private readonly StatBlockService _statBlocks;

    private readonly ILogger<SimulateTools> _logger;

    private readonly ITransport? _transport;

    /// <param name="statBlocks">The SRD stat blocks, resolved and cached once per process.</param>
    /// <param name="logger">Notes a run stopped because the session ended (<see cref="SessionEndedText"/>).</param>
    /// <param name="transport">
    /// The session's transport, which the SDK registers for the stdio (and stream) transport; absent, only the request's
    /// own token stops the fights. See <see cref="SessionEnded"/>.
    /// </param>
    public SimulateTools(StatBlockService statBlocks, ILogger<SimulateTools> logger, ITransport? transport = null)
    {
        _statBlocks = statBlocks;
        _logger = logger;
        _transport = transport;
    }

    /// <summary>
    /// Completes when the client has closed the session: the transport's message reader completes once the client's input
    /// has ended (stdin closed, the MCP spec's stdio shutdown) and every message has been taken off the queue; requests
    /// still being handled, this one included, are not waited for. The SDK itself does not cancel a request still running
    /// then, and its shutdown waits for it, so an abandoned run kept 15 cores busy for 30 s more before the process could
    /// exit. The fights are stopped then instead: nobody is left to read the answer.
    /// </summary>
    private Task? SessionEnded => _transport?.MessageReader.Completion;

    // Not idempotent: without a seed the same call draws new dice. Closed-world: the SRD data this binary ships.
    [McpServerTool(Name = "balance_simulate", Title = "Simulate a fight", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Monte Carlo simulation of a D&D 5e fight (2014 or 2024 rules): a party vs enemies, fought thousands of times with the " +
        "dice rolled. Gives P(party wins) with a 95% CI, P(defeat), P(draw), P(a party member dies), rounds (mean, median, " +
        "p90) and per combatant: dropped to 0, dead, still dying, HP lost, damage dealt and taken, kills, resources used; what the SRD " +
        "stat blocks' simulation leaves out; the assumptions. Exact damage per round: balance_dpr; the books' XP " +
        "difficulty: encounter_difficulty.\n" +
        "- party, enemies (required): lists of entries, each exactly one of:\n" +
        "  - archetype: fighter, barbarian, paladin, ranger, rogue, monk, cleric, druid, wizard, sorcerer, warlock or bard " +
        "(simple, subclass-free), with level 1-20 and edition;\n" +
        "  - monster: an SRD monster by name or ref (\"Ogre\", \"2014/monster/lich\");\n" +
        "  - build: a build as balance_dpr takes it (PC, NPC, homebrew creature), with hp and ac (save_proficiencies optional).\n" +
        "  Per entry also: count (copies, 1-20), name, hp, ac, saves, initiative_bonus, position (\"front\" or \"back\"), death_saves.\n" +
        "- iterations: default 10,000 (max 100,000); or precision: run until P(win)'s 95% half-width is at most this, e.g. 0.01.\n" +
        "- seed: repeats a result exactly (number or decimal string); without one a random seed is drawn and shown.\n" +
        "- round_cap (default 20, then a draw), edition (the fight's rules; default the party's), surprise (\"party\" or " +
        "\"enemies\"), enemy_hp (\"average\" or \"roll\").\n" +
        "- policies: {party: focus_fire|spread|threat, enemies: spread|focus_fire|threat|healer_first|break_concentration, " +
        "legendary_resistance, healing, finish_downed, pcs_win_ties}.\n" +
        "- replay: one fight's number, shown turn by turn.\n" +
        "- compare: {member: a party entry's position (1-based), feature: as balance_compare's}: the same fights with and " +
        "without it, paired.\n" +
        "- rulings: as balance_dpr's.\n" +
        "Example: " + Example)]
    public async Task<string> Simulate(
        [Description("The party: entries with archetype (+ level), monster or build (+ hp, ac), and count, e.g. " +
                     "[{\"archetype\": \"fighter\", \"level\": 5, \"count\": 4}].")]
        CombatantSpec[] party,
        [Description("The enemies: entries with the same fields as party, e.g. [" + EntryExample + "].")]
        [SameShapeAs("party")] object enemies,
        [Description("Fights to run, 1-100,000. Default 10,000 (P(win) to about ±1%).")] int? iterations = null,
        [Description("A seed, 0 to 18446744073709551615, as a number or a decimal string, to repeat a result exactly. Default: a random seed, shown in the result.")]
        ulong? seed = null,
        [Description("Rounds before a fight still going is called a draw, 1-100. Default 20.")][AIParameterName("round_cap")] int? roundCap = null,
        [Description("The fight's rules: \"2014\" or \"2024\" (surprise, exhaustion, concentration). Default: the party's.")] string? edition = null,
        [Description("Who is surprised: \"none\" (default), \"party\" or \"enemies\".")] string? surprise = null,
        [Description("Enemy hit points: \"average\" (default, the stat block's) or \"roll\" (from the hit dice each fight).")]
        [AIParameterName("enemy_hp")] string? enemyHp = null,
        [Description("Run batches of 10,000 fights until P(party wins)'s 95% half-width is at most this, 0.001-0.5 (e.g. 0.01), up to 100,000 fights (fewer for a very large fight: the work limit). Replaces iterations.")]
        double? precision = null,
        [Description("The number of one fight (1-based) to show turn by turn, e.g. 1.")] int? replay = null,
        [Description("How each side fights; every field optional, e.g. {\"enemies\": \"focus_fire\"}.")] PolicySpec? policies = null,
        [Description("The same fights with one party entry changed: {member: its 1-based position in party, feature: {name, " +
                     "attacks, modifiers, abilities, fighting_style}, as balance_compare's feature (attacks and modifiers as in a " +
                     "build)}, e.g. {\"member\": 1, \"feature\": {\"name\": \"Great Weapon Master\", \"modifiers\": [...]}}.")]
        [CheckedAs(typeof(CompareSpec))] object? compare = null,
        [Description("Table rulings for every build, each false by default, as balance_dpr's.")] RulingsSpec? rulings = null,
        RequestContext<CallToolRequestParams>? context = null,
        CancellationToken cancellationToken = default)
    {
        // The call stops with its request or with the session (SessionEnded), whichever ends first.
        using var call = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // The request's context (bound by the SDK, not in the schema) carries the client's progress token: the pump sends
        // through the session itself, awaited, where the SDK's IProgress would send fire-and-forget (ProgressPump).
        var pump = context is { Params.ProgressToken: { } token, Server: { } server }
            ? new ProgressPump((value, ct) => server.NotifyProgressAsync(token, value, cancellationToken: ct))
            : null;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sending = pump?.SendUntilAsync(finished.Task, call.Token) ?? Task.CompletedTask;
        try
        {
            var work = SimulateAsync(
                party, enemies, iterations, seed, roundCap, edition, surprise, enemyHp, precision, replay, policies, compare, rulings, pump, call.Token);
            if (SessionEnded is { } ended && await Task.WhenAny(work, ended) != work)
            {
                await call.CancelAsync();
                try
                {
                    return await work;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Stopped by the session's end, not by the request: the SDK would log that as a tool that failed.
                    _logger.LogInformation("balance_simulate: {Message}", SessionEndedText);
                    return SessionEndedText;
                }
            }

            return await work;
        }
        finally
        {
            // Nothing is sent once the call has its answer: a send under way finishes before the result goes out.
            finished.TrySetResult();
            await sending;
        }
    }

    /// <summary>
    /// The call itself: resolve the monsters, check the run, wait for the fight threads (<see cref="FightGate"/>), run the
    /// fights on <see cref="FightThreads"/> threads, render.
    /// </summary>
    private async Task<string> SimulateAsync(
        CombatantSpec[] party, object enemies, int? iterations, ulong? seed, int? roundCap, string? edition, string? surprise, string? enemyHp,
        double? precision, int? replay, PolicySpec? policies, object? compare, RulingsSpec? rulings, ProgressPump? progress,
        CancellationToken cancellationToken)
    {
        var sinceCall = System.Diagnostics.Stopwatch.StartNew();
        var partyEntries = party ?? [];
        var enemyEntries = Enemies(enemies);
        var fightEdition = Edition(edition);
        var partyEdition = PartyEdition(partyEntries);

        var sides = new[] { (List: "party", Entries: partyEntries), (List: "enemies", Entries: enemyEntries) };
        var requests = new List<(int Side, int Item, StatBlockRequest Request)>();
        for (var side = 0; side < sides.Length; side++)
        {
            var (list, entries) = sides[side];
            for (var item = 0; item < entries.Length; item++)
            {
                if (entries[item] is { Monster: { } monster, Build: null, Archetype: null } entry)
                {
                    var where = Where(list, item, entry);
                    if (string.IsNullOrWhiteSpace(monster))
                    {
                        throw new DndInputException(
                            $"{where}: monster is empty; give an SRD monster's name or ref, e.g. {EntryExample}, or use build or archetype instead.");
                    }

                    var lookupEdition = Edition(entry.Edition) ?? fightEdition ?? partyEdition;
                    requests.Add((side, item, new StatBlockRequest(monster, lookupEdition, Wording(where))));
                }
            }
        }

        var resolved = requests.Count == 0
            ? []
            : await _statBlocks.ResolveAsync(requests.Select(r => r.Request).ToList(), progress, cancellationToken);

        var blocks = new Dictionary<(int Side, int Item), StatBlock>();
        var notes = new List<string>();
        for (var i = 0; i < requests.Count; i++)
        {
            blocks[(requests[i].Side, requests[i].Item)] = resolved[i].Block;
            notes.AddRange(resolved[i].Notes);
        }

        var spec = new SimulationSpec
        {
            Party = Combatants(partyEntries, 0, blocks),
            Enemies = Combatants(enemyEntries, 1, blocks),
            Iterations = iterations ?? SimulationLimits.DefaultIterations,
            RoundCap = roundCap ?? SimulationLimits.DefaultRoundCap,
            Edition = edition,
            Surprise = surprise,
            EnemyHp = enemyHp,
            Precision = precision,
            Replay = replay,
            Policies = policies,
            Compare = Compare(compare),
            Rulings = rulings,
        };

        // Bad input and an oversized run are refused now, not after waiting for another call's fights.
        var prepared = Simulator.Prepare(spec);
        var seedGiven = seed is not null;
        var masterSeed = seed ?? RandomSeed();
        await WaitForTheFightThreadsAsync(progress, sinceCall, cancellationToken);
        SimulationReport report;
        try
        {
            report = await Task.Run(() => Simulator.Run(prepared, masterSeed, cancellationToken, progress, FightThreads), cancellationToken);
        }
        finally
        {
            FightGate.Release();
        }

        return SimulationMarkdown.Format(report, seedGiven, notes.Distinct(StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// Enters <see cref="FightGate"/>, reporting every second it waits: the seconds since the call began, which stay above
    /// the rules index wait's (its own seconds, counted from later) and below the fights' counts in all but the shortest
    /// runs (a value not above the last is dropped, <see cref="ProgressPump"/>).
    /// </summary>
    /// <exception cref="OperationCanceledException">The call was cancelled while it waited; the gate is not held.</exception>
    private static async Task WaitForTheFightThreadsAsync(ProgressPump? progress, System.Diagnostics.Stopwatch sinceCall, CancellationToken cancellationToken)
    {
        if (await FightGate.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return;
        }

        var waited = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            progress?.Report(new ProgressNotificationValue
            {
                Progress = (float)sinceCall.Elapsed.TotalSeconds,
                Message = string.Create(CultureInfo.InvariantCulture,
                    $"Waiting for another simulation to finish ({waited.Elapsed.TotalSeconds:0} s); this one's fights start when it does."),
            });
        }
        while (!await FightGate.WaitAsync(GateReportInterval, cancellationToken));
    }

    private static int ReservePoolThreads()
    {
        ThreadPool.GetMinThreads(out var workers, out var io);
        ThreadPool.SetMinThreads(Math.Max(workers, Environment.ProcessorCount + 4), io);
        return Math.Max(1, Environment.ProcessorCount - 1);
    }

    /// <summary>A 64-bit seed from the OS's cryptographic generator: the one source of randomness a seedless call has.</summary>
    internal static ulong RandomSeed() => BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(sizeof(ulong)));

    /// <summary>
    /// <c>enemies</c> as entries. It is published untyped (<see cref="SameShapeAsAttribute"/>) and binds as a
    /// <see cref="JsonElement"/>; the argument guard has already checked it item by item against <c>party</c>'s schema and
    /// test-bound it as <c>CombatantSpec[]</c>, so this reads it with the options the SDK would have bound it with.
    /// </summary>
    private static CombatantSpec[] Enemies(object? enemies)
    {
        if (enemies is null || enemies is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
        {
            return [];
        }

        if (enemies is not JsonElement { ValueKind: JsonValueKind.Array } element)
        {
            throw new DndInputException($"enemies must be a list of entries like party's, e.g. [{EntryExample}].");
        }

        try
        {
            return element.Deserialize<CombatantSpec[]>(McpJson.Options) ?? [];
        }
        catch (JsonException ex)
        {
            // Unreachable while the guard test-binds the same type first; kept so a gap there names the field, not the SDK's
            // generic error.
            throw new DndInputException($"enemies could not be read as entries at '{ex.Path}'; each takes the same fields as a party entry.", ex);
        }
    }

    /// <summary>
    /// <c>compare</c> as a <see cref="CompareSpec"/>. Published untyped (<see cref="CheckedAsAttribute"/>: its feature's
    /// schema alone is 12 KB) and checked by the argument guard against <see cref="CompareSpec"/>'s schema, then read here
    /// with the options the SDK would have bound it with.
    /// </summary>
    private static CompareSpec? Compare(object? compare)
    {
        if (compare is null || compare is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
        {
            return null;
        }

        if (compare is not JsonElement { ValueKind: JsonValueKind.Object } element)
        {
            throw new DndInputException("compare must be an object: {\"member\": 1, \"feature\": {\"name\": …, \"modifiers\": […]}}.");
        }

        try
        {
            return element.Deserialize<CompareSpec>(McpJson.Options);
        }
        catch (JsonException ex)
        {
            throw new DndInputException($"compare could not be read at '{ex.Path}'; it takes member and feature.", ex);
        }
    }

    private static IReadOnlyList<SimulationCombatant> Combatants(CombatantSpec[] entries, int side, IReadOnlyDictionary<(int Side, int Item), StatBlock> blocks) =>
        entries.Select((entry, item) => new SimulationCombatant(entry, blocks.GetValueOrDefault((side, item)))).ToList();

    /// <summary>A canonical edition ("2014"/"2024") or null when absent or not an edition (the Domain names a bad one).</summary>
    private static string? Edition(string? text) =>
        text is not null && DslValues.Editions.Set.TryMatch(text, out var canonical) ? canonical : null;

    /// <summary>The first party entry's rules: its build's edition, else its own edition, else 2024.</summary>
    private static string PartyEdition(CombatantSpec[] party) =>
        party.FirstOrDefault() is { } first
            ? Edition(first.Build?.Edition) ?? Edition(first.Edition) ?? DslValues.Editions.Default
            : DslValues.Editions.Default;

    /// <summary>
    /// balance_simulate's words around the shared monster lookup: items as the Domain counts them ("enemies item 1
    /// (Orge)"), the stat block serves "this 2024 fight", and a creature the SRD lacks is a build with hp and ac.
    /// </summary>
    private static MonsterLookupWording Wording(string where) => new(
        where,
        "fight",
        edition => $"in this {edition} fight",
        "Give a monster's ref (e.g. \"2024/monster/ogre\") or its name; a creature the SRD lacks is a build with hp and ac.",
        "For a creature not in the SRD, give it as a build with hp and ac instead of monster.",
        name => "For a creature the SRD does not have (it has only some of the Monster Manual), give it as a build with hp and " +
                $"ac instead of monster: {{\"name\": \"{name}\", \"build\": {{\"name\": \"{name}\", \"level\": 5, \"attacks\": [...]}}, " +
                "\"hp\": 60, \"ac\": 14}.");

    /// <summary>"enemies item 1 (Orge)": the item as the Domain's messages and the argument guard count it.</summary>
    private static string Where(string list, int item, CombatantSpec entry)
    {
        var name = new[] { entry.Name, entry.Monster, entry.Build?.Name, entry.Archetype }.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        var position = (item + 1).ToString(CultureInfo.InvariantCulture);
        return name is null ? $"{list} item {position}" : $"{list} item {position} ({Echo(name.Trim())})";
    }

    // As the Domain echoes a name: control characters escaped, at most 60 characters.
    private static string Echo(string text)
    {
        var printable = string.Concat(text.Select(c => char.IsControl(c) ? $"\\u{(int)c:x4}" : c.ToString()));
        return printable.Length <= 60 ? printable : printable[..60] + "…";
    }

    /// <summary>
    /// One call's progress notifications, sent ONE AT A TIME, each awaited before the next and each above the last: the
    /// MCP spec requires progress to increase with every notification. The SDK's own <see cref="IProgress{T}"/> sends
    /// fire-and-forget, and reports made in turn still overtook each other on the wire — back to back a 3,976 arrived
    /// after the 5,000, and spaced a quarter second apart under load a 1,024 after an 8,736 — so this sends through
    /// <see cref="McpSession.NotifyProgressAsync(ProgressToken, ProgressNotificationValue, RequestOptions?, CancellationToken)"/>
    /// itself and waits for each. <see cref="Report(ProgressNotificationValue)"/> only records the value (the rules index
    /// wait's, the wait for another call's fights, then the simulator's after each chunk); <see cref="SendUntilAsync"/>
    /// sends the latest, the first at once so even a short run shows it is moving, then at most every
    /// <see cref="MinInterval"/>, so a burst of chunks costs one notification. A value not above the last one reported is
    /// dropped: the waits count seconds and the fights count fights, and a one-fight run after a two-second wait must not
    /// step back.
    /// </summary>
    internal sealed class ProgressPump : IProgress<ProgressNotificationValue>, IProgress<(int Completed, int Total)>
    {
        /// <summary>Often enough for Claude Code's idle timer and a watching user; seldom enough not to flood the client.</summary>
        public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(250);

        private readonly Func<ProgressNotificationValue, CancellationToken, Task> _send;
        private readonly Lock _gate = new();
        private ProgressNotificationValue? _pending;
        private float _last = float.NegativeInfinity;
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <param name="send">Sends one notification; the pump never starts one before the last has finished.</param>
        public ProgressPump(Func<ProgressNotificationValue, CancellationToken, Task> send)
        {
            _send = send;
        }

        /// <summary>Records <paramref name="value"/> as the next to send, unless it is not above the last reported.</summary>
        public void Report(ProgressNotificationValue value)
        {
            lock (_gate)
            {
                if (!(value.Progress > _last))
                {
                    return;
                }

                _last = value.Progress;
                _pending = value;
                _changed.TrySetResult();
            }
        }

        /// <summary>Called by the simulator after each chunk (it serialises the calls): fights completed of fights planned.</summary>
        public void Report((int Completed, int Total) value) => Report(new ProgressNotificationValue
        {
            Progress = value.Completed,
            Total = value.Total,
            Message = string.Create(CultureInfo.InvariantCulture, $"Simulated {value.Completed:N0} of {value.Total:N0} fights."),
        });

        /// <summary>
        /// Sends the latest value reported until <paramref name="stop"/> completes, waiting for each send to finish. A send
        /// cancelled, or failed because the transport is gone, ends the progress but not the call, whose own result or
        /// cancellation follows (a transport that fails a notification fails the answer too, and the SDK logs that).
        /// </summary>
        public async Task SendUntilAsync(Task stop, CancellationToken cancellationToken)
        {
            var since = System.Diagnostics.Stopwatch.StartNew();
            var first = true;
            while (!stop.IsCompleted)
            {
                Task changed;
                lock (_gate)
                {
                    changed = _changed.Task;
                }

                await Task.WhenAny(stop, changed).ConfigureAwait(false);
                var wait = MinInterval - since.Elapsed;
                if (!first && wait > TimeSpan.Zero)
                {
                    await Task.WhenAny(stop, Task.Delay(wait, CancellationToken.None)).ConfigureAwait(false);
                }

                if (stop.IsCompleted)
                {
                    return;
                }

                ProgressNotificationValue? value;
                lock (_gate)
                {
                    value = _pending;
                    _pending = null;
                    _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                if (value is null)
                {
                    continue;
                }

                try
                {
                    await _send(value, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
                {
                    // Cancelled, or the transport is gone (the SDK's "not connected" is an InvalidOperationException).
                    return;
                }

                since.Restart();
                first = false;
            }
        }
    }
}
