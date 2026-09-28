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
using ModelContextProtocol;
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
/// <b>Progress and cancellation.</b> Waiting for the rules index reports progress as every rules tool does; the fights
/// run on the thread pool while this method forwards their progress (chunks of 1,024) at most every quarter second
/// (<see cref="FightProgressPump"/>), which also resets Claude Code's idle timeout on a long run. The token is honoured
/// between fights. The work is bounded by <see cref="SimulationLimits.WorkBudget"/> (about 15 s at worst).
/// </para>
/// </summary>
public sealed class SimulateTools
{
    private const string Example =
        "{\"party\": [{\"archetype\": \"fighter\", \"level\": 5, \"count\": 2}, {\"archetype\": \"cleric\", \"level\": 5}, " +
        "{\"archetype\": \"wizard\", \"level\": 5}], \"enemies\": [{\"monster\": \"ogre\", \"count\": 3}], \"seed\": 42}";

    private const string EntryExample = "{\"monster\": \"Ogre\", \"count\": 3}";

    private readonly StatBlockService _statBlocks;

    public SimulateTools(StatBlockService statBlocks)
    {
        _statBlocks = statBlocks;
    }

    // Not idempotent: without a seed the same call draws new dice. Closed-world: the SRD data this binary ships.
    [McpServerTool(Name = "balance_simulate", Title = "Simulate a fight", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Monte Carlo simulation of a D&D 5e fight (2014 or 2024 rules): a party vs enemies, fought thousands of times with the " +
        "dice rolled. Gives P(party wins) with a 95% CI, P(defeat), P(draw), P(a party member dies), rounds (mean, median, " +
        "p90) and per combatant: dropped to 0, dead, HP lost, damage dealt and taken, kills, resources used; what the SRD " +
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
        [Description("Run batches of 10,000 fights until P(party wins)'s 95% half-width is at most this, 0.001-0.5 (e.g. 0.01), up to 100,000 fights. Replaces iterations.")]
        double? precision = null,
        [Description("The number of one fight (1-based) to show turn by turn, e.g. 1.")] int? replay = null,
        [Description("How each side fights; every field optional, e.g. {\"enemies\": \"focus_fire\"}.")] PolicySpec? policies = null,
        [Description("The same fights with one party entry changed: {member: its 1-based position in party, feature: {name, " +
                     "attacks, modifiers, abilities, fighting_style}, as balance_compare's feature (attacks and modifiers as in a " +
                     "build)}, e.g. {\"member\": 1, \"feature\": {\"name\": \"Great Weapon Master\", \"modifiers\": [...]}}.")]
        [CheckedAs(typeof(CompareSpec))] object? compare = null,
        [Description("Table rulings for every build, each false by default, as balance_dpr's.")] RulingsSpec? rulings = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
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

        var seedGiven = seed is not null;
        var masterSeed = seed ?? RandomSeed();
        var pump = progress is null ? null : new FightProgressPump();
        var run = Task.Run(() => Simulator.Run(spec, masterSeed, cancellationToken, pump), cancellationToken);
        if (pump is not null)
        {
            await pump.ForwardAsync(run, progress!);
        }

        var report = await run;
        return SimulationMarkdown.Format(report, seedGiven, notes.Distinct(StringComparer.Ordinal).ToList());
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
    /// The simulator's (completed, planned) as MCP progress, forwarded from ONE loop at most every
    /// <see cref="MinInterval"/>. The SDK sends each report fire-and-forget; reports made back to back from the worker
    /// threads that finish chunks overtook each other on the wire (a 3,976 arriving after the 5,000), and the MCP spec
    /// requires progress to increase with every notification. Spaced out, each send has long finished before the next
    /// starts. The first chunk is forwarded at once, so even a short run shows it is moving; a run that ends between two
    /// reports sends nothing more (the result follows).
    /// </summary>
    private sealed class FightProgressPump : IProgress<(int Completed, int Total)>
    {
        /// <summary>Often enough for Claude Code's idle timer and a watching user; far apart for sends never to overlap.</summary>
        public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(250);

        private readonly Lock _gate = new();
        private (int Completed, int Total)? _latest;
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Called by the simulator after each chunk (it serialises the calls); only records the value.</summary>
        public void Report((int Completed, int Total) value)
        {
            lock (_gate)
            {
                _latest = value;
                _changed.TrySetResult();
            }
        }

        /// <summary>Forwards the latest value to <paramref name="progress"/> until <paramref name="work"/> ends.</summary>
        public async Task ForwardAsync(Task work, IProgress<ProgressNotificationValue> progress)
        {
            var sent = -1;
            var since = System.Diagnostics.Stopwatch.StartNew();
            var first = true;
            while (!work.IsCompleted)
            {
                Task changed;
                lock (_gate)
                {
                    changed = _changed.Task;
                }

                await Task.WhenAny(work, changed).ConfigureAwait(false);
                var wait = MinInterval - since.Elapsed;
                if (!first && wait > TimeSpan.Zero)
                {
                    await Task.WhenAny(work, Task.Delay(wait)).ConfigureAwait(false);
                }

                if (work.IsCompleted)
                {
                    return;
                }

                (int Completed, int Total)? value;
                lock (_gate)
                {
                    value = _latest;
                    _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                if (value is { } v && v.Completed > sent)
                {
                    progress.Report(new ProgressNotificationValue
                    {
                        Progress = v.Completed,
                        Total = v.Total,
                        Message = string.Create(CultureInfo.InvariantCulture, $"Simulated {v.Completed:N0} of {v.Total:N0} fights."),
                    });
                    sent = v.Completed;
                    since.Restart();
                    first = false;
                }
            }
        }
    }
}
