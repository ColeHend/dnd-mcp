using System.Text.Json;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;

namespace DndMcp.Domain.Campaign;

/// <summary>Where one route of a gate stands.</summary>
/// <param name="Id">The route's id ("testimonies").</param>
/// <param name="Known">Its clues the party knows.</param>
/// <param name="Needed">Clues needed to complete it (<c>min_clues</c>, default all).</param>
/// <param name="Total">Its clues.</param>
/// <param name="Complete">Known ≥ Needed.</param>
public sealed record RouteStatus(string Id, int Known, int Needed, int Total, bool Complete);

/// <summary>
/// Where a gate stands at one point in time (<see cref="FactGates.Evaluate"/>). Fact references are whatever the gate
/// holds (ids when read from campaigns.db); the caller maps them to handles for output.
/// </summary>
/// <param name="UnmetAfter">The <c>after</c> facts not in play.</param>
/// <param name="Routes">Each route's progress, in gate order.</param>
/// <param name="RoutesComplete">Routes complete.</param>
/// <param name="MinRoutes">Routes needed (<c>min_routes</c>, default all; 0 when the gate has no routes).</param>
/// <param name="UnmetPrefer">The advisory <c>prefer</c> facts not in play.</param>
/// <param name="MustLandWith">The <c>with</c> facts the party does not know yet: reveal them in the same session.</param>
/// <param name="ForbiddenActive">The gate's vocabulary rule holds now.</param>
/// <param name="Seeded">The party knows a seed or a route clue.</param>
public sealed record GateStatus(
    IReadOnlyList<string> UnmetAfter,
    IReadOnlyList<RouteStatus> Routes,
    int RoutesComplete,
    int MinRoutes,
    IReadOnlyList<string> UnmetPrefer,
    IReadOnlyList<string> MustLandWith,
    bool ForbiddenActive,
    bool Seeded)
{
    public bool AfterMet => UnmetAfter.Count == 0;

    public bool RoutesMet => RoutesComplete >= MinRoutes;

    /// <summary>The reveal may land: every <c>after</c> fact in play and enough routes complete.</summary>
    public bool Ready => AfterMet && RoutesMet;

    /// <summary>The ids of the complete routes, in gate order.</summary>
    public IReadOnlyList<string> CompleteRouteIds => Routes.Where(r => r.Complete).Select(r => r.Id).ToList();

    /// <summary>The party could work the secret out now although its <c>after</c> conditions are not met.</summary>
    public bool ReachableBeforeGate => Routes.Count > 0 && RoutesMet && !AfterMet;
}

/// <summary>A <c>with</c> fact the knower already had from an earlier session: the pair did not land together.</summary>
/// <param name="FactId">The coupled fact.</param>
/// <param name="LearnedSession">The session it was learned in; null when the knower holds it with no session recorded.</param>
public sealed record LandedSeparately(string FactId, int? LearnedSession);

/// <summary>The <c>with</c> coupling of one reveal to one knower (<see cref="FactGates.CheckWith(GateSpec, Func{string, int?}, int?, IReadOnlySet{string})"/>).</summary>
/// <param name="Unmet">Coupled facts the knower does not know and that this batch does not reveal.</param>
/// <param name="LandedSeparately">Coupled facts the knower learned in another session.</param>
public sealed record WithCheck(IReadOnlyList<string> Unmet, IReadOnlyList<LandedSeparately> LandedSeparately)
{
    public bool Met => Unmet.Count == 0 && LandedSeparately.Count == 0;
}

/// <summary>
/// Validation, storage and evaluation of fact gates (contract §3.4). Pure: the caller supplies "is this fact in play",
/// "does the party know this fact" and learned sessions, so the rules are pinned by tests with no database and the
/// write path (reveal warnings) and the read path (status, knowledge check) evaluate a gate the same way.
///
/// <para>
/// <b>In play</b> (the caller's <c>inPlay</c>): established in a session numbered ≤ the point in time, canon status not
/// proposed / planned / struck / superseded / lean, not deleted. <b>Party knows</b> (the caller's <c>partyKnows</c>):
/// the party's verdict on the fact is Knows at that time. Keeping both decisions with the caller is what lets as_of
/// evaluation ("was the vocabulary rule active in session 10?") reuse this code unchanged.
/// </para>
/// </summary>
public static class FactGates
{
    /// <summary>
    /// For <see cref="CheckWith(GateSpec, Func{string, int?}, int?, IReadOnlySet{string})"/>: what the learned-session
    /// callback returns for a coupled fact the knower holds with no session recorded (backstory). It never equals a
    /// session number, so the pair counts as landed separately.
    /// </summary>
    public const int KnownWithoutSession = -1;

    private const string FactHandleHint = "give f:<n> or a register code such as \"F36\"";

    /// <summary>
    /// The problems with a gate as a tool received it (fact handles, not ids), each starting with
    /// <paramref name="subject"/> ("gate", "ops item 2 (fact f:12): gate"). Empty when it is valid. An entirely empty gate
    /// is valid: see <see cref="IsEmpty"/>.
    /// </summary>
    public static IReadOnlyList<string> Validate(GateSpec? gate, string subject)
    {
        var problems = new List<string>();
        if (gate is null)
        {
            return problems;
        }

        var after = FactList(problems, subject, "after", gate.After);
        var with = FactList(problems, subject, "with", gate.With);
        FactList(problems, subject, "prefer", gate.Prefer);
        FactList(problems, subject, "seeds", gate.Seeds);
        var until = FactList(problems, subject, "forbidden_until", gate.ForbiddenUntil);
        foreach (var both in after.Intersect(with, StringComparer.Ordinal))
        {
            problems.Add($"{subject}: {both} is in both after and with; a fact that must land with this one cannot also have to come before it.");
        }

        var routeCount = RouteProblems(problems, subject, gate.Routes);
        if (gate.MinRoutes is { } minRoutes)
        {
            if (routeCount == 0)
            {
                problems.Add($"{subject}: min_routes needs routes; give routes or leave min_routes out.");
            }
            else if (minRoutes < 1 || minRoutes > routeCount)
            {
                problems.Add($"{subject}: min_routes is {DslText.Number(minRoutes)}; it is 1 to {DslText.Number(routeCount)} (the number of routes).");
            }
        }

        var terms = TermList(problems, subject, "forbidden_terms", gate.ForbiddenTerms, pattern: false);
        var patterns = TermList(problems, subject, "forbidden_patterns", gate.ForbiddenPatterns, pattern: true);
        TermList(problems, subject, "preferred_terms", gate.PreferredTerms, pattern: false);
        if (until.Count > 0 && terms == 0 && patterns == 0)
        {
            problems.Add($"{subject}: forbidden_until lifts forbidden_terms and forbidden_patterns; give those, or leave forbidden_until out.");
        }

        if (gate.Note is { Length: > CampaignLimits.MaxNoteLength })
        {
            problems.Add($"{subject}: note is longer than {DslText.Number(CampaignLimits.MaxNoteLength)} characters.");
        }

        return problems;
    }

    /// <summary>
    /// Whether the gate says nothing at all (<c>{}</c>). The write path stores such a gate as NULL: <c>"gate": {}</c> is how
    /// an op removes a gate.
    /// </summary>
    public static bool IsEmpty(GateSpec gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        return gate.After is null && gate.With is null && gate.Prefer is null && gate.Seeds is null && gate.Routes is null &&
               gate.MinRoutes is null && gate.ForbiddenTerms is null && gate.ForbiddenPatterns is null && gate.ForbiddenUntil is null &&
               gate.PreferredTerms is null && gate.Note is null;
    }

    /// <summary>
    /// Every fact the gate references (after, with, prefer, seeds, route clues, forbidden_until), each once, in that
    /// order. The write path resolves these; undo's conflict check and the dependents of a deleted fact are found through
    /// them.
    /// </summary>
    public static IReadOnlyList<string> References(GateSpec gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        return (gate.After ?? [])
            .Concat(gate.With ?? [])
            .Concat(gate.Prefer ?? [])
            .Concat(gate.Seeds ?? [])
            .Concat((gate.Routes ?? []).Where(r => r is not null).SelectMany(r => r!.Clues ?? []))
            .Concat(gate.ForbiddenUntil ?? [])
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The same gate with every fact reference mapped through <paramref name="map"/>: handles to ids before storing, ids to
    /// handles before printing. Everything else is kept as is.
    /// </summary>
    public static GateSpec Rewrite(GateSpec gate, Func<string, string> map)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(map);
        IReadOnlyList<string>? Map(IReadOnlyList<string>? list) => list?.Select(map).ToList();
        return new GateSpec
        {
            After = Map(gate.After),
            With = Map(gate.With),
            Prefer = Map(gate.Prefer),
            Seeds = Map(gate.Seeds),
            Routes = gate.Routes?.Select(r => r is null ? null : new RouteSpec { Id = r.Id, Clues = Map(r.Clues), MinClues = r.MinClues }).ToList(),
            MinRoutes = gate.MinRoutes,
            ForbiddenTerms = gate.ForbiddenTerms,
            ForbiddenPatterns = gate.ForbiddenPatterns,
            ForbiddenUntil = Map(gate.ForbiddenUntil),
            PreferredTerms = gate.PreferredTerms,
            Note = gate.Note,
        };
    }

    /// <summary>
    /// The canonical stored JSON of a gate: snake_case, fields not given left out, list order kept. Two equal gates always
    /// serialize to the same text, which is what change_log compares to decide whether a gate changed.
    /// </summary>
    public static string Serialize(GateSpec gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        return JsonSerializer.Serialize(gate, CampaignJson.Options);
    }

    /// <summary>
    /// A stored gate, or null for SQL NULL or blank text. Unknown fields are ignored (a later version's gate still loads).
    /// For the write path, where refusing the call is right; readers use <see cref="TryParse"/>, so one bad stored gate
    /// never fails a read.
    /// </summary>
    /// <exception cref="DndInputException">The text is not a JSON object of the gate's shape. The message names the path only.</exception>
    public static GateSpec? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<GateSpec>(json, CampaignJson.Options)
                   ?? throw new DndInputException("gate: is null; give a JSON object such as {\"after\": [\"f:12\"]}.");
        }
        catch (JsonException ex)
        {
            var where = string.IsNullOrEmpty(ex.Path) || ex.Path == "$" ? string.Empty : $" at '{ex.Path}'";
            throw new DndInputException($"gate: could not be read{where}; a gate is a JSON object such as {{\"after\": [\"f:12\"]}}.", ex);
        }
    }

    /// <summary>
    /// The read-safe form of <see cref="Parse"/>, for every path that reads stored gates (get, search, the knowledge check,
    /// the reveal checks, secret status). True with a null gate for SQL NULL or blank text (no gate); true with the gate
    /// when it reads; false with null when the stored text is not a gate.
    ///
    /// <para>
    /// <b>Why it exists:</b> the schema only checks that <c>fact.gate</c> is a JSON object, so a hand-edited or
    /// older-version gate such as <c>{"after": "f:1"}</c> can be stored. <see cref="Parse"/> throws a
    /// <see cref="DndInputException"/> that tells the model to fix "a gate" it never sent, and one bad row would then fail
    /// every read that evaluates it. A reader treats false as "this fact's gate cannot be evaluated" (no warnings from it,
    /// no status change) and says so to the author view only.
    /// </para>
    /// </summary>
    public static bool TryParse(string? json, out GateSpec? gate)
    {
        gate = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        try
        {
            gate = JsonSerializer.Deserialize<GateSpec>(json, CampaignJson.Options);
            return gate is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Where the gate stands, given which facts are in play and which the party knows at the point in time being asked
    /// about.
    /// </summary>
    /// <param name="inPlay">Whether a referenced fact is in play (see the class summary).</param>
    /// <param name="partyKnows">Whether the party knows a referenced fact.</param>
    /// <param name="gatedFactKnownToParty">Whether the party knows the gated fact itself (lifts a vocabulary rule that has no forbidden_until).</param>
    public static GateStatus Evaluate(GateSpec gate, Func<string, bool> inPlay, Func<string, bool> partyKnows, bool gatedFactKnownToParty)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(inPlay);
        ArgumentNullException.ThrowIfNull(partyKnows);

        var unmetAfter = Distinct(gate.After).Where(f => !inPlay(f)).ToList();
        var routes = new List<RouteStatus>();
        foreach (var route in (gate.Routes ?? []).Where(r => r is not null))
        {
            var clues = Distinct(route!.Clues).ToList();
            var known = clues.Count(partyKnows);
            var needed = clues.Count == 0 ? 0 : Math.Clamp(route.MinClues ?? clues.Count, 1, clues.Count);
            routes.Add(new RouteStatus(route.Id ?? string.Empty, known, needed, clues.Count, clues.Count > 0 && known >= needed));
        }

        var minRoutes = routes.Count == 0 ? 0 : Math.Clamp(gate.MinRoutes ?? routes.Count, 1, routes.Count);
        var unmetPrefer = Distinct(gate.Prefer).Where(f => !inPlay(f)).ToList();
        var mustLandWith = Distinct(gate.With).Where(f => !partyKnows(f)).ToList();
        var hasVocabulary = (gate.ForbiddenTerms?.Count ?? 0) > 0 || (gate.ForbiddenPatterns?.Count ?? 0) > 0;
        var until = Distinct(gate.ForbiddenUntil).ToList();
        var forbiddenActive = hasVocabulary && (until.Count > 0 ? until.Any(f => !inPlay(f)) : !gatedFactKnownToParty);
        var seeded = Distinct(gate.Seeds)
            .Concat((gate.Routes ?? []).Where(r => r is not null).SelectMany(r => Distinct(r!.Clues)))
            .Any(partyKnows);
        return new GateStatus(unmetAfter, routes, routes.Count(r => r.Complete), minRoutes, unmetPrefer, mustLandWith, forbiddenActive, seeded);
    }

    /// <summary>
    /// Whether a knowledge write moved the gate from "not enough routes" to "enough routes" while its <c>after</c>
    /// conditions are still unmet: the party can work the secret out before the story is ready for it, which the write
    /// path reports as "reachable before the gate".
    /// </summary>
    public static bool BecameReachableBeforeGate(GateStatus before, GateStatus after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return !before.RoutesMet && after.ReachableBeforeGate;
    }

    /// <summary>
    /// Whether the gate's <c>with</c> facts land together with a reveal to one knower: revealed in this batch, or learned
    /// by that knower in the reveal's session. A coupled fact learned in another session "landed separately"; one the
    /// knower does not know is unmet.
    /// </summary>
    /// <param name="learnedSessionForKnower">The session the knower learned a fact in; null when the knower does not know it; <see cref="KnownWithoutSession"/> when known with no session recorded.</param>
    /// <param name="revealSession">The reveal's session number; null when the write has no session context (then only the same batch counts as together).</param>
    /// <param name="revealedInThisBatch">Facts this batch reveals to the same knower.</param>
    public static WithCheck CheckWith(
        GateSpec gate,
        Func<string, int?> learnedSessionForKnower,
        int? revealSession,
        IReadOnlySet<string> revealedInThisBatch)
    {
        ArgumentNullException.ThrowIfNull(gate);
        return CheckWith(Distinct(gate.With).ToList(), learnedSessionForKnower, revealSession, revealedInThisBatch);
    }

    /// <summary>
    /// <see cref="CheckWith(GateSpec, Func{string, int?}, int?, IReadOnlySet{string})"/> over an explicit list of coupled
    /// facts, such as <see cref="WithPartners"/>'s symmetric list.
    /// </summary>
    public static WithCheck CheckWith(
        IReadOnlyList<string> with,
        Func<string, int?> learnedSessionForKnower,
        int? revealSession,
        IReadOnlySet<string> revealedInThisBatch)
    {
        ArgumentNullException.ThrowIfNull(with);
        ArgumentNullException.ThrowIfNull(learnedSessionForKnower);
        ArgumentNullException.ThrowIfNull(revealedInThisBatch);
        var unmet = new List<string>();
        var separately = new List<LandedSeparately>();
        foreach (var fact in with.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.Ordinal))
        {
            if (revealedInThisBatch.Contains(fact))
            {
                continue;
            }

            var learned = learnedSessionForKnower(fact);
            if (learned is null)
            {
                unmet.Add(fact);
            }
            else if (revealSession is null || learned != revealSession)
            {
                separately.Add(new LandedSeparately(fact, learned < 0 ? null : learned));
            }
        }

        return new WithCheck(unmet, separately);
    }

    /// <summary>
    /// The facts that must land with <paramref name="factId"/>: its own gate's <c>with</c>, plus every fact whose gate
    /// lists it in <c>with</c>. The coupling is symmetric ("the two land together or not at all"): revealing Nadar's plan
    /// alone breaks the seal fact's coupling even when only the seal fact's gate names it.
    /// </summary>
    public static IReadOnlyList<string> WithPartners(string factId, GateSpec? ownGate, IEnumerable<(string FactId, GateSpec? Gate)> gates)
    {
        ArgumentNullException.ThrowIfNull(factId);
        ArgumentNullException.ThrowIfNull(gates);
        var partners = Distinct(ownGate?.With).Where(f => f != factId).ToList();
        foreach (var (other, gate) in gates)
        {
            if (other != factId && gate?.With is { } list && list.Contains(factId, StringComparer.Ordinal) && !partners.Contains(other))
            {
                partners.Add(other);
            }
        }

        return partners;
    }

    /// <summary>The gate's vocabulary as a rule for <see cref="ForbiddenVocabulary.Scan"/>, or null when it forbids nothing.</summary>
    /// <param name="source">The gated fact's handle, for the report.</param>
    public static ForbiddenRule? VocabularyRule(GateSpec gate, string source)
    {
        ArgumentNullException.ThrowIfNull(gate);
        var terms = gate.ForbiddenTerms ?? [];
        var patterns = gate.ForbiddenPatterns ?? [];
        return terms.Count == 0 && patterns.Count == 0
            ? null
            : new ForbiddenRule(source, terms, patterns, gate.PreferredTerms ?? [], gate.Note);
    }

    private static IEnumerable<string> Distinct(IReadOnlyList<string>? list) =>
        (list ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.Ordinal);

    /// <summary>Checks a list of fact handles; returns the canonical handles that parsed.</summary>
    internal static List<string> FactList(List<string> problems, string subject, string field, IReadOnlyList<string>? list)
    {
        var handles = new List<string>();
        if (list is null)
        {
            return handles;
        }

        if (list.Count == 0)
        {
            problems.Add($"{subject} {field} is empty; leave it out or give fact handles such as \"f:12\".");
            return handles;
        }

        if (list.Count > CampaignLimits.MaxGateListItems)
        {
            problems.Add($"{subject} {field} has {DslText.Number(list.Count)} items; at most {DslText.Number(CampaignLimits.MaxGateListItems)}.");
        }

        for (var i = 0; i < list.Count; i++)
        {
            var where = $"{subject} {field} item {DslText.Number(i + 1)}";
            var text = list[i];
            if (string.IsNullOrWhiteSpace(text))
            {
                problems.Add($"{where} is blank; {FactHandleHint}.");
            }
            else if (!CampaignHandle.TryParse(text, out var handle, out var problem))
            {
                problems.Add($"{where}: {problem}");
            }
            else if (handle is not (CampaignHandle.FactBySeq or CampaignHandle.ByCode))
            {
                problems.Add($"{where}: \"{DslText.Echo(text)}\" is not a fact handle; {FactHandleHint}.");
            }
            else if (handles.Contains(handle.Text))
            {
                problems.Add($"{where}: {handle.Text} is listed twice.");
            }
            else
            {
                handles.Add(handle.Text);
            }
        }

        return handles;
    }

    /// <summary>Checks the routes; returns how many there are (valid or not) for the min_routes check.</summary>
    private static int RouteProblems(List<string> problems, string subject, IReadOnlyList<RouteSpec?>? routes)
    {
        if (routes is null)
        {
            return 0;
        }

        if (routes.Count == 0)
        {
            problems.Add($"{subject} routes is empty; leave it out or give routes such as [{{\"id\": \"testimonies\", \"clues\": [\"f:40\", \"f:41\"]}}].");
            return 0;
        }

        if (routes.Count > CampaignLimits.MaxGateListItems)
        {
            problems.Add($"{subject} routes has {DslText.Number(routes.Count)} items; at most {DslText.Number(CampaignLimits.MaxGateListItems)}.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < routes.Count; i++)
        {
            var where = $"{subject} routes item {DslText.Number(i + 1)}";
            var route = routes[i];
            if (route is null)
            {
                problems.Add($"{where} is null; give {{\"id\": \"testimonies\", \"clues\": [\"f:40\", \"f:41\"]}}.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(route.Id))
            {
                problems.Add($"{where}: id is required, a slug such as \"testimonies\".");
            }
            else if (!CampaignSlugs.IsValid(route.Id))
            {
                problems.Add($"{where}: id \"{DslText.Echo(route.Id)}\" must be a slug (lower-case letters and digits in hyphen-separated runs), e.g. \"temple-arithmetic\".");
            }
            else if (!ids.Add(route.Id))
            {
                problems.Add($"{where}: id \"{route.Id}\" is used by another route.");
            }

            if (route.Clues is null)
            {
                problems.Add($"{where}: clues is required, e.g. [\"f:40\", \"f:41\"].");
                continue;
            }

            FactList(problems, subject, $"routes item {DslText.Number(i + 1)} clues", route.Clues);
            if (route.MinClues is { } minClues && route.Clues.Count > 0 && (minClues < 1 || minClues > route.Clues.Count))
            {
                problems.Add($"{where}: min_clues is {DslText.Number(minClues)}; it is 1 to {DslText.Number(route.Clues.Count)} (the number of clues).");
            }
        }

        return routes.Count;
    }

    /// <summary>
    /// Checks a list of terms or patterns; returns how many were given. A repeated item is named by position, never
    /// quoted: forbidden terms are the words a secret hides behind ("seal"), and the message reaches whoever drives the
    /// client.
    /// </summary>
    internal static int TermList(List<string> problems, string subject, string field, IReadOnlyList<string>? list, bool pattern)
    {
        if (list is null)
        {
            return 0;
        }

        if (list.Count == 0)
        {
            problems.Add($"{subject} {field} is empty; leave it out or give words such as [\"seal\"].");
            return 0;
        }

        if (list.Count > CampaignLimits.MaxGateListItems)
        {
            problems.Add($"{subject} {field} has {DslText.Number(list.Count)} items; at most {DslText.Number(CampaignLimits.MaxGateListItems)}.");
        }

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < list.Count; i++)
        {
            var where = $"{subject} {field} item {DslText.Number(i + 1)}";
            var text = list[i];
            var usable = pattern ? ForbiddenVocabulary.IsUsablePattern(text, out var problem) : ForbiddenVocabulary.IsUsableTerm(text, out problem);
            if (!usable)
            {
                problems.Add($"{where} {problem}.");
            }
            else if (seen.TryGetValue(CampaignText.Key(text), out var first))
            {
                problems.Add($"{where} repeats item {DslText.Number(first)}.");
            }
            else
            {
                seen[CampaignText.Key(text)] = i + 1;
            }
        }

        return list.Count;
    }
}
