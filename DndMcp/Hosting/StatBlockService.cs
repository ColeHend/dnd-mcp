using System.Collections.Concurrent;
using DndMcp.Domain.Core;
using DndMcp.Domain.Simulation;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Combatants;
using DndMcp.Repository.Srd.Index;
using ModelContextProtocol;

namespace DndMcp.Hosting;

/// <summary>
/// SRD monsters as the simulator reads them: a monster named by ref or name → its normalized <see cref="StatBlock"/>,
/// for <c>balance_simulate</c>'s monster entries, <c>rules_get</c>'s <c>combatant</c> format and <c>balance_dpr</c>'s
/// <c>target.monster</c>. One service so all three read the same stat block for the same name, and pay for each
/// normalization once.
///
/// <para>
/// <b>Resolution is <c>encounter_difficulty</c>'s</b> (<see cref="SrdMonsterLookup"/>): refs, names, a shapechanger's
/// forms, the other edition's counterpart when an edition lacks the monster, close names when nothing matches. A caller
/// names the edition the stat block is for and gets the notes the result must show ("`2014/monster/ogre` is a 2014 stat
/// block, used as given in this 2024 fight").
/// </para>
/// <para>
/// <b>Lazy, cached, thread-safe.</b> The normalizer (<see cref="MonsterNormalizer"/>, with the content root's
/// <c>overrides/</c>) is built on first use, not at startup, so a server that never simulates never reads the overlays,
/// and a broken overlay costs the simulation tools only, never the handshake or the rules tools. Stat blocks are cached by
/// ref: srd.db's content is keyed on this binary's content directory, so a document's stat block never changes while the
/// server runs (a reopened index serves the same records). A failed normalizer build is not cached; the next call tries
/// again, as <see cref="SrdIndexService"/> does.
/// </para>
/// <para>
/// <b>Failures say what to do.</b> A missing or damaged overrides file is an install problem the model cannot fix; it
/// reaches the model as an <see cref="McpException"/> naming the file and the fix, never as the SDK's generic error.
/// </para>
/// </summary>
public sealed class StatBlockService
{
    private readonly SrdIndexService _indexService;
    private readonly DndMcpServerOptions _options;
    private readonly ConcurrentDictionary<string, StatBlock> _cache = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private MonsterNormalizer? _normalizer;

    public StatBlockService(SrdIndexService indexService, DndMcpServerOptions options)
    {
        _indexService = indexService;
        _options = options;
    }

    /// <summary>
    /// The stat block of one monster named by ref ("2024/monster/ogre") or name ("Ogre", "Adult Red Dragon") for a fight
    /// under <paramref name="edition"/>'s rules, with the notes a result must show about how it was found. Waits for the
    /// rules index (with progress) like every rules tool.
    /// </summary>
    /// <param name="edition">"2014" or "2024": the edition whose stat block is wanted (a ref from the other edition is still used as given, with a note).</param>
    /// <param name="wording">
    /// The caller's words for the item and its fallback (<see cref="Wording"/>); default: a "monster" subject whose
    /// fallback is to describe the creature another way.
    /// </param>
    /// <exception cref="DndInputException">No SRD monster by that ref or name (the message lists close names), or a ref of another kind.</exception>
    public async Task<ResolvedStatBlock> ResolveAsync(
        string refOrName,
        string edition,
        IProgress<ProgressNotificationValue>? progress,
        CancellationToken cancellationToken,
        MonsterLookupWording? wording = null)
    {
        var resolved = await ResolveAsync([new StatBlockRequest(refOrName, edition, wording ?? Wording("monster"))], progress, cancellationToken);
        return resolved[0];
    }

    /// <summary>
    /// Several monsters at once, in order, with one wait for the index. Every request that fails is reported, up to five,
    /// in one <see cref="DndInputException"/> (one message per line), so a party of three misspelt monsters costs one retry.
    /// </summary>
    public Task<IReadOnlyList<ResolvedStatBlock>> ResolveAsync(
        IReadOnlyList<StatBlockRequest> requests, IProgress<ProgressNotificationValue>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        return _indexService.QueryAsync(index => Resolve(requests, index), progress, cancellationToken);
    }

    /// <summary>
    /// The stat block of an index document, cached by ref: <c>rules_get</c>'s <c>combatant</c> format, which has already
    /// found the document. Spells are read through <paramref name="lookup"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The document is not a monster (a caller's bug: callers check the kind first).</exception>
    public StatBlock Normalize(SrdDocument monster, ISrdLookup lookup)
    {
        ArgumentNullException.ThrowIfNull(monster);
        var key = monster.Ref.ToString();
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var block = Normalizer().Normalize(monster, lookup);
        return _cache.GetOrAdd(key, block);
    }

    /// <summary>
    /// The default words around a lookup: "{where}: no monster … is named …", with a fallback that fits any tool (describe
    /// the creature another way). Tools with their own fallback pass their own <see cref="MonsterLookupWording"/>.
    /// </summary>
    public static MonsterLookupWording Wording(string where, string occasion = "fight", string? fallback = null)
    {
        var advice = fallback ?? "For a creature the SRD does not have, describe it yourself instead of naming it.";
        return new MonsterLookupWording(
            where,
            occasion,
            edition => $"in this {edition} {occasion}",
            $"Give a monster's ref (e.g. \"2024/monster/ogre\") or its name. {advice}",
            advice,
            _ => advice);
    }

    private IReadOnlyList<ResolvedStatBlock> Resolve(IReadOnlyList<StatBlockRequest> requests, SrdIndex index)
    {
        var results = new ResolvedStatBlock[requests.Count];
        var problems = new List<string>();
        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            try
            {
                var docs = SrdMonsterLookup.Resolve(index, request.RefOrName.Trim(), [request.Edition], request.Wording);
                results[i] = new ResolvedStatBlock(Normalize(docs.ByEdition[request.Edition], index), docs.Notes);
            }
            catch (DndInputException ex) when (problems.Count < MaxProblems)
            {
                problems.Add(ex.Message);
            }
            catch (DndInputException)
            {
                // Past the cap: the model fixes the ones it was told about first.
            }
        }

        return problems.Count switch
        {
            0 => results,
            1 => throw new DndInputException(problems[0]),
            _ => throw new DndInputException(string.Join("\n", problems)),
        };
    }

    private const int MaxProblems = 5;

    /// <summary>The normalizer, built once from the content root's overrides (not cached when building it fails).</summary>
    private MonsterNormalizer Normalizer()
    {
        lock (_gate)
        {
            if (_normalizer is not null)
            {
                return _normalizer;
            }

            try
            {
                _normalizer = new MonsterNormalizer(MonsterOverrides.Load(_options.ContentRoot), SpellOverlay.Load(_options.ContentRoot));
                return _normalizer;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                throw new McpException(
                    $"The monster overrides that turn SRD stat blocks into combatants could not be read ({ex.Message}). They ship in " +
                    $"the content directory ({Path.Combine(_options.ContentRoot, MonsterOverrides.DirectoryName)}); reinstall dnd-mcp " +
                    "into an empty directory so the whole publish output, content/ included, is in place.",
                    ex);
            }
        }
    }
}

/// <summary>One monster to resolve: its ref or name, the edition the stat block is for, and the caller's wording.</summary>
public sealed record StatBlockRequest(string RefOrName, string Edition, MonsterLookupWording Wording);

/// <summary>A resolved monster: its stat block and the notes a result must show about how it was found.</summary>
public sealed record ResolvedStatBlock(StatBlock Block, IReadOnlyList<string> Notes);
