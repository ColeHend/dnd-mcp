using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Characters;
using DndMcp.Domain.Combat;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation;
using DndMcp.Formatting;
using DndMcp.Formatting.Campaign;
using DndMcp.Hosting;
using DndMcp.Repository.Campaign.Characters;
using DndMcp.Repository.Campaign.Combat;
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
/// <c>edition</c>, else the party's (the first party entry's build or entry edition), else the active campaign's ruleset,
/// else 2024: a party of 2014 builds fights 2014 ogres unless told otherwise, as the fight itself follows the party's rules.
/// </para>
/// <para>
/// <b>The campaign's ruleset</b> (contract §9): with a campaign active, every build and archetype entry that names no
/// edition is given one before the Domain sees it (<see cref="CampaignEditionFill"/>): the call's fight <c>edition</c>,
/// else the first party entry's, else the campaign's ruleset, and the result's notes say so when the campaign decided.
/// The fight's own edition then follows the party as always. The call's editions come first so a campaign never mixes
/// editions the call did not: without it, in a 2014 campaign, <c>"edition": "2014"</c> would leave a build that names none
/// on 2024 rules (the Domain's build default ignores the fight edition) while omitting the edition gave 2014. With no
/// campaign active nothing is filled, so that pre-campaign behaviour (and every answer) is unchanged.
/// </para>
/// <para>
/// <b>Not idempotent, deliberately.</b> Without a seed each call draws a fresh seed below 2^53 from the OS
/// (<see cref="RandomNumberGenerator"/>: the host, never the Domain, which only ever sees the seed) and echoes it with how
/// to pass it back, as a JSON number; with a seed the result is identical at any thread count. A given seed is a JSON
/// number or a decimal string, because a JavaScript-based client reads numbers as doubles and silently rounds a seed above
/// 2^53: a drawn seed is kept below that so the number it is echoed as comes back exactly (<see cref="RandomSeed"/>).
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
        "{\"party\": [{\"archetype\": \"fighter\", \"level\": 5, \"count\": 4}], \"enemies\": [{\"monster\": \"ogre\", \"count\": 3}], \"seed\": 42}";

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

    private readonly CampaignService _campaigns;

    /// <param name="statBlocks">The SRD stat blocks, resolved and cached once per process.</param>
    /// <param name="logger">Notes a run stopped because the session ended (<see cref="SessionEndedText"/>).</param>
    /// <param name="campaigns">The active campaign, whose ruleset fills the editions the call leaves out.</param>
    /// <param name="transport">
    /// The session's transport, which the SDK registers for the stdio (and stream) transport; absent, only the request's
    /// own token stops the fights. See <see cref="SessionEnded"/>.
    /// </param>
    public SimulateTools(StatBlockService statBlocks, ILogger<SimulateTools> logger, CampaignService campaigns, ITransport? transport = null)
    {
        _statBlocks = statBlocks;
        _logger = logger;
        _campaigns = campaigns;
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
        "Monte Carlo simulation of a D&D 5e fight: a party vs enemies, fought thousands of times. Gives P(party wins) with a 95% " +
        "CI, P(defeat), P(draw), P(a party member dies), rounds and per combatant: dropped, dead, HP lost, damage dealt and taken, " +
        "kills, resources used; what the simulation leaves out; the assumptions. Exact damage per round: balance_dpr; the books' " +
        "XP difficulty: encounter_difficulty.\n" +
        "- encounter: a stored fight (\"current\", \"last\" or its name): its combatants are the sides (party, enemies append); " +
        "from_state: true resumes the live fight from its HP, slots, conditions and turn.\n" +
        "- party, enemies: lists of entries, each exactly one of:\n" +
        "  - archetype: fighter, barbarian, paladin, ranger, rogue, monk, cleric, druid, wizard, sorcerer, warlock or bard " +
        "(subclass-free), with level 1-20 and edition;\n" +
        "  - monster: an SRD monster's name or ref (\"Ogre\");\n" +
        "  - build: a build as balance_dpr takes it, with hp and ac (save_proficiencies optional);\n" +
        "  - character: a campaign character with a sheet (\"character:torch\").\n" +
        "  Per entry also: count (1-20), name, hp, ac, saves, initiative_bonus, position (\"front\" or \"back\"), death_saves.\n" +
        "- campaign: whose encounter and characters (default the active one).\n" +
        "- iterations: default 10,000 (max 100,000); or precision: run until P(win)'s 95% half-width is at most this.\n" +
        "- seed: repeats a result exactly (default: random, shown).\n" +
        "- round_cap (default 20, then a draw), edition (default the party's, else the campaign's), surprise (\"party\" or " +
        "\"enemies\"), enemy_hp (\"average\" or \"roll\").\n" +
        "- policies: {party: focus_fire|spread|threat, enemies: spread|focus_fire|threat|healer_first|break_concentration, " +
        "legendary_resistance, healing, finish_downed, pcs_win_ties}.\n" +
        "- replay: one fight's number, shown turn by turn.\n" +
        "- compare: {member: a party entry's position (1-based), feature: as balance_compare's}: the fights with and without it.\n" +
        "- rulings: as balance_dpr's.\n" +
        "Example: " + Example)]
    public async Task<string> Simulate(
        [Description("The party: entries with archetype (+ level), monster, build (+ hp, ac) or character, and count, e.g. " +
                     "[{\"archetype\": \"fighter\", \"level\": 5, \"count\": 4}]. With encounter: added to its party.")]
        CombatantSpec[]? party = null,
        [Description("The enemies: entries with the same fields as party, e.g. [" + EntryExample + "]. With encounter: added to its enemies.")]
        [SameShapeAs("party")] object? enemies = null,
        [Description("A stored fight of the campaign as the sides: current (the active one), last (the last ended) or its name. Instead of party and enemies, or with them.")]
        string? encounter = null,
        [Description("With encounter (default current): resume the running fight from its live HP, slots, conditions, concentration and turn. Default false.")]
        [AIParameterName("from_state")] bool? fromState = null,
        [Description("The campaign of encounter and of character entries, whose ruleset fills editions. Default: the current campaign.")]
        string? campaign = null,
        [Description("Fights to run, 1-100,000. Default 10,000 (P(win) to about ±1%).")] int? iterations = null,
        [Description("A seed to repeat a result exactly, 0 to 18446744073709551615 (number or decimal string). Default: a random one, shown in the result.")]
        ulong? seed = null,
        [Description("Rounds before a fight still going is called a draw, 1-100. Default 20.")][AIParameterName("round_cap")] int? roundCap = null,
        [Description("The fight's rules: \"2014\" or \"2024\" (surprise, exhaustion, concentration). Default: the party's (an encounter's own). With a campaign active, entries naming no edition follow this, else the first party entry's, else the active campaign's ruleset.")] string? edition = null,
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
                new SimulateCall(party, enemies, encounter, fromState, campaign, iterations, seed, roundCap, edition, surprise, enemyHp, precision,
                    replay, policies, compare, rulings),
                pump,
                call.Token);
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

    /// <summary>One call's arguments, as bound.</summary>
    private sealed record SimulateCall(
        CombatantSpec[]? Party, object? Enemies, string? Encounter, bool? FromState, string? Campaign, int? Iterations, ulong? Seed, int? RoundCap,
        string? Edition, string? Surprise, string? EnemyHp, double? Precision, int? Replay, PolicySpec? Policies, object? Compare, RulingsSpec? Rulings);

    /// <summary>
    /// The call itself: read the campaign's part (the encounter, the <c>character</c> entries' sheets) when the call names
    /// one, resolve the monsters, check the run, wait for the fight threads (<see cref="FightGate"/>), run the fights on
    /// <see cref="FightThreads"/> threads, render.
    /// </summary>
    private async Task<string> SimulateAsync(SimulateCall call, ProgressPump? progress, CancellationToken cancellationToken)
    {
        var sinceCall = System.Diagnostics.Stopwatch.StartNew();
        var resume = call.FromState ?? false;
        var fight = string.IsNullOrWhiteSpace(call.Encounter) ? resume ? EncounterResolver.Current : null : call.Encounter;
        CheckSides(call, fight, resume);

        var partyEntries = call.Party ?? [];
        var enemyEntries = Enemies(call.Enemies);
        var edition = call.Edition;
        var fightEdition = Edition(edition);

        // The campaign is read only when the call names a part of one (contract §6.11, D7): an encounter, a character entry,
        // or the campaign itself. Then everything the call takes from a campaign comes from THAT campaign
        // (CampaignArgumentDefaults), never the active one; otherwise the ambient defaults stand as before.
        var chosen = fight is not null || HasCharacters(partyEntries) || HasCharacters(enemyEntries) || !string.IsNullOrWhiteSpace(call.Campaign)
            ? CampaignArgumentDefaults.Mapped(_campaigns, () => CampaignArgumentDefaults.Read(_campaigns, call.Campaign), "the campaign's settings were not read")
            : null;

        var simulation = fight is null ? null : CallsNameTheCampaign(chosen!, () => new EncounterSimulationLoader(_campaigns.Database).Load(chosen!.Row, fight, resume, call.Rulings));

        // character entries become the build or archetype entries of their sheets (D7) before anything reads an edition: the
        // fight edition then follows the sheet, as it follows any other first party entry. A sheet that names no ruleset
        // fights in the encounter's when the call has one (as the encounter's own members do), else in the campaign's. Every
        // entry's problems are reported together, as the Domain's are.
        var sheetAssumptions = new List<string>();
        if (chosen is not null)
        {
            var characterEdition = simulation?.Edition ?? SheetFallbackEdition(chosen, fightEdition);
            var problems = new List<string>();
            partyEntries = Expand(chosen, partyEntries, "party", characterEdition, sheetAssumptions, problems);
            enemyEntries = Expand(chosen, enemyEntries, "enemies", characterEdition, sheetAssumptions, problems);
            DslProblems.ThrowIfAny(problems, "simulation");
        }

        var campaign = chosen is null ? new CampaignEditionFill(_campaigns) : new CampaignEditionFill(chosen);

        // With a campaign active, an entry that names no edition takes the call's own first (the fight's, else the encounter's,
        // else the first party entry's) and only then the campaign's ruleset. An edition that is not one is left for the Domain
        // to refuse.
        if (edition is null || fightEdition is not null)
        {
            var given = fightEdition ?? simulation?.Edition ?? PartyEdition(partyEntries);
            partyEntries = campaign.Fill(partyEntries, given);
            enemyEntries = campaign.Fill(enemyEntries, given);
        }

        var partyEdition = simulation?.Edition ?? PartyEdition(partyEntries);

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

                    var lookupEdition = Edition(entry.Edition) ?? fightEdition ?? partyEdition ??
                                        (edition is null ? campaign.MonsterFallback() : DslValues.Editions.Default);
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

        // The encounter's own entries first, then the call's (appended, §6.11); a side left with nobody is refused with the
        // encounter's notes (who was left out and why) only now, so "this prepared fight against X" works.
        IReadOnlyList<SimulationCombatant> party = [.. simulation?.Party ?? [], .. Combatants(partyEntries, 0, blocks)];
        IReadOnlyList<SimulationCombatant> enemies = [.. simulation?.Enemies ?? [], .. Combatants(enemyEntries, 1, blocks)];
        if (simulation is not null)
        {
            CallsNameTheCampaign(chosen!, () => TrackerSimulation.RequireBothSides(party, enemies, simulation.Notes, resume));
            RefuseTooManyForTheEncounterForm(simulation, party, enemies, resume);
        }

        var spec = new SimulationSpec
        {
            Party = party,
            Enemies = enemies,
            Iterations = call.Iterations ?? SimulationLimits.DefaultIterations,
            RoundCap = call.RoundCap ?? SimulationLimits.DefaultRoundCap,
            Edition = edition ?? simulation?.Edition,
            Surprise = call.Surprise,
            EnemyHp = call.EnemyHp,
            Precision = call.Precision,
            Replay = call.Replay,
            Policies = call.Policies,
            Compare = Compare(call.Compare),
            Rulings = call.Rulings,
            Lair = simulation?.Lair ?? false,
            Resume = simulation?.Resume,
        };

        // Bad input and an oversized run are refused now, not after waiting for another call's fights.
        var prepared = Simulator.Prepare(spec);
        var seedGiven = call.Seed is not null;
        var masterSeed = call.Seed ?? RandomSeed();
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

        // The sheets' own assumption lines (D7: which members used an archetype, how a multiclass was read) go first, in entry
        // order, whichever form named the character: the encounter's (the loader's) then the call's.
        IReadOnlyList<string> assumptions = [.. simulation?.Assumptions ?? [], .. sheetAssumptions];
        if (assumptions.Count > 0)
        {
            report = report with { Assumptions = [.. assumptions, .. report.Assumptions] };
        }

        var resolutionNotes = campaign.WithNote(notes.Distinct(StringComparer.Ordinal).ToList());
        if (simulation is null)
        {
            return SimulationMarkdown.Format(report, seedGiven, resolutionNotes);
        }

        return SimulationMarkdown.FormatEncounter(report, seedGiven, resolutionNotes, simulation, chosen!.Row.Slug, resume);
    }

    /// <summary>
    /// The 40-creature cap (<see cref="SimulationLimits.MaxCombatants"/>) of an encounter form, refused with that form's fix
    /// (fix F1, review C09). The Domain's own refusal ends "Lower some counts", the explicit call's fix: in the encounter
    /// form the fight's combatants are the input (no count to lower), and under <c>from_state</c> nothing can be appended
    /// or left out. The fix there is an explicit call with fewer entries (or, appending, fewer appended). Counted exactly as
    /// the Domain counts (each entry's count, 1 to 20), so a run this lets through is never refused for its size after.
    /// </summary>
    private static void RefuseTooManyForTheEncounterForm(
        EncounterSimulation simulation, IReadOnlyList<SimulationCombatant> party, IReadOnlyList<SimulationCombatant> enemies, bool resume)
    {
        static int Count(IEnumerable<SimulationCombatant?> entries) => entries.Sum(c => Math.Clamp(c?.Spec?.Count ?? 1, 1, SimulationLimits.MaxCount));
        static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

        var total = Count(party) + Count(enemies);
        if (total <= SimulationLimits.MaxCombatants)
        {
            return;
        }

        var name = EncounterResolver.Json(simulation.EncounterName);
        var max = N(SimulationLimits.MaxCombatants);
        if (resume)
        {
            throw DslProblems.Exception(
                [
                    $"the live fight {name} has {N(total)} creatures to resume; at most {max} are simulated, and from_state resumes every one of them. " +
                    "Simulate part of it with an explicit call instead: party and enemies with no encounter or from_state (a fresh fight: a character " +
                    "entry starts at its sheet's maximum HP).",
                ],
                "simulation");
        }

        var fromEncounter = Count(simulation.Party) + Count(simulation.Enemies);
        var appended = total - fromEncounter;
        throw DslProblems.Exception(
            [
                $"the fight {name} has {N(total)} combatants to simulate (counting copies" +
                (appended > 0 ? $": {N(fromEncounter)} from the encounter, {N(appended)} appended" : string.Empty) +
                $"); at most {max} are simulated, and the encounter form simulates every one of the encounter's. " +
                (appended > 0 ? "Append fewer, or simulate" : "Simulate") +
                " part of it with an explicit call instead: party and enemies with no encounter (a character entry plays its sheet).",
            ],
            "simulation");
    }

    /// <summary>
    /// Runs the encounter's own refusals (the loader's, <see cref="TrackerSimulation.RequireBothSides"/>'s) with every call
    /// they print naming the campaign last (<see cref="CombatMarkdown.CampaignLastIn"/>): "give hp with combat set (combat
    /// {…})" is the tracker's, which never knows the slug, and a call sent while another campaign is current must still
    /// reach this encounter.
    /// </summary>
    private static T CallsNameTheCampaign<T>(ResolvedCampaignDefaults chosen, Func<T> read)
    {
        try
        {
            return read();
        }
        catch (DndInputException ex) when (CombatMarkdown.CampaignLastIn(ex.Message, chosen.Row.Slug) is var message && message != ex.Message)
        {
            throw new DndInputException(message, ex);
        }
    }

    private static void CallsNameTheCampaign(ResolvedCampaignDefaults chosen, Action check) =>
        CallsNameTheCampaign(chosen, () =>
        {
            check();
            return true;
        });

    /// <summary>
    /// The sides the call gives (contract §6.11): <c>party</c> and <c>enemies</c>, or an <c>encounter</c> (to which they are
    /// appended), never neither; with <c>from_state</c> the fight is resumed exactly as it stands, so nothing is appended and
    /// nobody is surprised.
    /// </summary>
    private static void CheckSides(SimulateCall call, string? fight, bool resume)
    {
        var enemiesGiven = call.Enemies is not null and not JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined };
        if (fight is null && (call.Party is null || !enemiesGiven))
        {
            throw new DndInputException(
                $"give party and enemies, or encounter (a stored fight: \"current\", \"last\" or its name); {(call.Party is null && !enemiesGiven ? "neither was given" : call.Party is null ? "party is missing" : "enemies is missing")}. " +
                $"Example: {Example}");
        }

        if (resume && (call.Party is not null || enemiesGiven))
        {
            throw new DndInputException(
                "from_state resumes the fight exactly as it stands, so party and enemies cannot be added to it: leave them out, or simulate the encounter " +
                "without from_state (a fresh fight) to add them.");
        }

        if (resume && call.Surprise is not null)
        {
            throw new DndInputException("from_state resumes a fight already under way, so nobody is surprised: leave surprise out.");
        }
    }

    private static bool HasCharacters(CombatantSpec?[] entries) => entries.Any(e => e?.Character is not null);

    /// <summary>
    /// The edition a sheet that names none is simulated in (D7: the campaign's ruleset); in a mixed campaign, which has
    /// none, the call's fight edition, else 2024.
    /// </summary>
    private static string SheetFallbackEdition(ResolvedCampaignDefaults chosen, string? fightEdition) =>
        chosen.Edition ?? fightEdition ?? DslValues.Editions.Default;

    /// <summary>
    /// Each <c>character</c> entry as its sheet's entry (contract D7, P's <see cref="SheetSimulation.Entry"/>): its
    /// sim_profile as a build, else its main class's archetype at its level, with the sheet's HP, AC, saves and initiative;
    /// <c>character</c> cleared and <c>name</c> filled (the entity's name unless the call gave one), so the Domain never
    /// sees a campaign reference. The fields a call gives beside it (level, hp, ac, save_proficiencies, saves,
    /// initiative_bonus, position, death_saves) replace the sheet's: the call's word wins, as everywhere else, and the
    /// entry's assumption line names each one as the call gave it, never the sheet's value it replaced (P lays them over,
    /// so the line and the entry cannot disagree). Each problem goes to <paramref name="problems"/>, naming the item: a
    /// character beside another source, more than one copy, an edition (the sheet decides), no such character, no sheet,
    /// and every sheet D7 refuses (with its fix).
    /// </summary>
    private CombatantSpec[] Expand(
        ResolvedCampaignDefaults chosen, CombatantSpec[] entries, string list, string fallbackEdition, List<string> assumptions, List<string> problems)
    {
        if (!HasCharacters(entries))
        {
            return entries;
        }

        var expanded = entries.ToArray();
        var reader = new SheetReader(_campaigns.Database);
        for (var item = 0; item < entries.Length; item++)
        {
            if (entries[item] is not { Character: { } character } entry)
            {
                continue;
            }

            var position = (item + 1).ToString(CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(character))
            {
                problems.Add($"{list} item {position}: character is empty; give a campaign character's handle, e.g. \"character:torch\".");
                continue;
            }

            var where = $"{list} item {position} ({Echo(character.Trim())})";
            var before = problems.Count;
            if (entry.Monster is not null || entry.Build is not null || entry.Archetype is not null)
            {
                problems.Add($"{where}: give only one of monster, build, archetype and character.");
            }

            if (entry.Count is { } count && count != 1)
            {
                problems.Add($"{where}: a character is one creature; leave count out.");
            }

            if (entry.Edition is not null)
            {
                problems.Add($"{where}: a character fights under its sheet's ruleset; leave edition out (edition at the top level is the fight's).");
            }

            if (problems.Count > before)
            {
                continue;
            }

            CharacterSheetRead read;
            try
            {
                read = CampaignArgumentDefaults.Mapped(_campaigns, () => reader.Get(chosen.Row, character.Trim(), Perspective.Author).Characters.Single(),
                    "the character's sheet was not read");
            }
            catch (DndInputException ex)
            {
                problems.Add($"{where}: {ex.Message}");
                continue;
            }

            if (read.Author?.Sheet is not { } sheet)
            {
                problems.Add($"{where}: {read.Name} has no sheet to simulate yet; make one with {SheetMarkdown.UpdateCall(chosen.Row.Slug, read.Ref)}, or leave {read.Name} out.");
                continue;
            }

            var name = string.IsNullOrWhiteSpace(entry.Name) ? read.Name : entry.Name.Trim();
            try
            {
                var simulated = SheetSimulation.Entry(sheet, name, fallbackEdition, entry);
                assumptions.AddRange(simulated.Assumptions);
                expanded[item] = simulated.Entry with { Character = null };
            }
            catch (DndInputException ex)
            {
                problems.Add($"{where}: {ex.Message}");
            }
        }

        return expanded;
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

    /// <summary>
    /// A seed from the OS's cryptographic generator, below 2^53: the one source of randomness a seedless call has. Below 2^53
    /// because the result tells the model to pass it back as a JSON number, and a JavaScript-based client reads numbers as
    /// doubles: a drawn 64-bit seed came back rounded (13275752732527329221 as 13275752732527330000) and the "(given)" rerun
    /// was another set of fights (fix F1, review U10). 53 bits are plenty of seeds; a given seed may still be any 64-bit one.
    /// </summary>
    internal static ulong RandomSeed() => BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(sizeof(ulong))) & MaxDrawnSeed;

    /// <summary>The largest seed <see cref="RandomSeed"/> draws: 2^53 − 1, the largest integer a double holds exactly.</summary>
    internal const ulong MaxDrawnSeed = (1UL << 53) - 1;

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

    /// <summary>
    /// The first party entry's rules: its build's edition, else its own edition; null when it names neither (the caller then
    /// falls back to the campaign's ruleset, else 2024).
    /// </summary>
    private static string? PartyEdition(CombatantSpec[] party) =>
        party.FirstOrDefault() is { } first ? Edition(first.Build?.Edition) ?? Edition(first.Edition) : null;

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
        var name = new[] { entry.Name, entry.Character, entry.Monster, entry.Build?.Name, entry.Archetype }.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
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
