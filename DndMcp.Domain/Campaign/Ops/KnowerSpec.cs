using System.ComponentModel;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using K = DndMcp.Domain.Campaign.CampaignValues.KnowerKinds;

namespace DndMcp.Domain.Campaign.Ops;

/// <summary>
/// One knowledge row as a tool receives it: who knows a fact or entity, in what state, under which name, since when.
/// Used by <c>known_by</c> on upsert and fact ops and by <c>campaign_knowledge record</c>.
/// </summary>
public sealed class KnowerSpec
{
    [Description("Required: party, table, public, dm, author or character:<handle>, e.g. \"character:belmakor\".")]
    public string? Who { get; init; }

    [Description("knows (default), suspects, believes, misbelieves, heard, met, aware, unrecognized, unaware or forgot.")]
    public string? State { get; init; }

    /// <summary>
    /// The knower's own name for the target. Left out, an existing row keeps the one it has (re-recording a state must
    /// never erase a disguise) and a new row has none (the knower uses the true name). So the way to END a disguise, when
    /// the knower learns the true name, is to give the true name (a known_as that is the entity's own name disguises
    /// nothing); a blank one is refused. The description says so: without it, a batch that told the party the name left
    /// the party knowing the fact that states it while every check still forbade the party the name.
    /// </summary>
    [Description("The name or phrasing this knower uses, e.g. \"the old king\". Omit it to keep the current one (a new row: when theirs is " +
                 "the true one); when the knower now uses the true name, give the true name, e.g. \"Keras\", which ends the disguise.")]
    public string? KnownAs { get; init; }

    [Description("How they learned it, e.g. witnessed, told, read, deduced, backstory or sang.")]
    public string? How { get; init; }

    [Description("The entity it came through, e.g. \"character:old-king\".")]
    public string? Via { get; init; }

    [Description("The session number it was learned in. Default: the batch's session.")]
    public int? Session { get; init; }

    [Description("A note for the author.")]
    public string? Note { get; init; }
}

/// <summary>
/// Parsing and validation of <see cref="KnowerSpec"/> lists.
///
/// <para>
/// <c>who</c> uses the perspective vocabulary (party, table, public, dm, author, character:&lt;handle&gt;), matched
/// forgivingly, because a knower and a perspective are the same people: a row written for <c>character:belmakor</c> is
/// what the <c>character:belmakor</c> perspective reads. A knower named twice in one list is refused: the unique index
/// allows one row per knower per target, so the second would silently overwrite the first.
/// </para>
/// </summary>
public static class KnowerSpecs
{
    private const string WhoForms = "party, table, public, dm, author or character:<handle> (e.g. \"character:belmakor\")";

    /// <summary>
    /// The knower kind of <paramref name="who"/> and, for a character, its handle (not yet resolved):
    /// "Party" → (party, null); "character:belmakor" → (character, belmakor).
    /// </summary>
    /// <exception cref="DndInputException"><paramref name="who"/> is not a knower.</exception>
    public static (string KnowerKind, CampaignHandle? Character) Parse(string? who)
    {
        if (TryParse(who, out var kind, out var character, out var problem))
        {
            return (kind, character);
        }

        throw new DndInputException(problem);
    }

    /// <summary>As <see cref="Parse"/>; on failure <paramref name="problem"/> is a message for the model.</summary>
    public static bool TryParse(string? who, out string knowerKind, out CampaignHandle? character, out string problem)
    {
        knowerKind = string.Empty;
        character = null;
        problem = string.Empty;
        var trimmed = who?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            problem = $"who is required: {WhoForms}.";
            return false;
        }

        var colon = trimmed.IndexOf(':');
        var head = colon < 0 ? trimmed : trimmed[..colon];
        if (!K.Set.TryMatch(head, out var kind))
        {
            problem = $"who \"{DslText.Echo(trimmed)}\" is not a knower; give {WhoForms}.";
            return false;
        }

        if (kind != K.Character)
        {
            if (colon >= 0)
            {
                problem = $"who \"{DslText.Echo(trimmed)}\": only character:<handle> takes a handle; give {WhoForms}.";
                return false;
            }

            knowerKind = kind;
            return true;
        }

        var rest = colon < 0 ? string.Empty : trimmed[(colon + 1)..].Trim();
        if (rest.Length == 0)
        {
            problem = "who \"character\" needs the character after a colon, e.g. \"character:belmakor\".";
            return false;
        }

        if (!CampaignHandle.TryParse(rest, out var handle, out var handleProblem) ||
            handle is not (CampaignHandle.EntityBySlug or CampaignHandle.EntityBySeq or CampaignHandle.ByCode))
        {
            problem = $"who \"{DslText.Echo(trimmed)}\": the part after \"character:\" must name a character, e.g. \"character:belmakor\" or " +
                      $"\"character:e:12\".{(handleProblem.Length > 0 ? " " + handleProblem : string.Empty)}";
            return false;
        }

        if (handle is CampaignHandle.EntityBySlug { Kind: not null and not CampaignValues.Kinds.Character } bySlug)
        {
            problem = $"who \"{DslText.Echo(trimmed)}\": a knower is a character, not kind {bySlug.Kind}.";
            return false;
        }

        knowerKind = K.Character;
        character = handle is CampaignHandle.EntityBySlug slug ? slug with { Kind = null } : handle;
        return true;
    }

    /// <summary>
    /// The problems with a list of knowers, each starting with "<paramref name="listName"/> item N (who)"; empty when
    /// valid. A null list is valid (nothing given); an empty one is refused.
    /// </summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<KnowerSpec?>? knowers, string listName)
    {
        var problems = new List<string>();
        if (knowers is null)
        {
            return problems;
        }

        if (knowers.Count == 0)
        {
            problems.Add($"{listName} is empty; give knowers such as [{{\"who\": \"party\", \"state\": \"knows\"}}] or leave it out.");
            return problems;
        }

        if (knowers.Count > CampaignLimits.MaxKnowersPerOp)
        {
            problems.Add($"{listName} has {DslText.Number(knowers.Count)} knowers; at most {DslText.Number(CampaignLimits.MaxKnowersPerOp)}.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < knowers.Count; i++)
        {
            var knower = knowers[i];
            var number = DslText.Number(i + 1);
            if (knower is null)
            {
                problems.Add($"{listName} item {number} is null; give {{\"who\": \"party\", \"state\": \"knows\"}}.");
                continue;
            }

            string where;
            if (!TryParse(knower.Who, out var kind, out var character, out var problem))
            {
                problems.Add($"{listName} item {number}: {problem}");
                where = $"{listName} item {number}";
            }
            else
            {
                var text = character is null ? kind : CampaignValues.PerspectiveKinds.CharacterPrefix + character.Text;
                where = $"{listName} item {number} ({text})";
                if (!seen.Add(text))
                {
                    problems.Add($"{where}: {text} is listed twice; one row per knower.");
                }
            }

            Check(knower, where, problems);
        }

        return problems;
    }

    private static void Check(KnowerSpec knower, string where, List<string> problems)
    {
        if (knower.State is { } state && !CampaignValues.KnowledgeStates.Set.TryMatch(state, out _))
        {
            problems.Add($"{where}: state \"{DslText.Echo(state)}\" is not a knowledge state; give {Or(CampaignValues.KnowledgeStates.Set)}.");
        }

        // A blank known_as is the natural guess for "they use the true name now", and it is refused (leaving it out keeps
        // the current one), so the refusal says what does that instead.
        OneLine(problems, where, "known_as", knower.KnownAs, CampaignLimits.MaxLabelLength,
            blankHint: "to record that they now use the true name, give the true name as known_as");
        OneLine(problems, where, "how", knower.How, CampaignLimits.MaxLabelLength);
        if (knower.Via is { } via)
        {
            if (!CampaignHandle.TryParse(via, out var handle, out var problem))
            {
                problems.Add($"{where}: via: {problem}");
            }
            else if (handle is CampaignHandle.FactBySeq or CampaignHandle.CrossCampaign)
            {
                problems.Add($"{where}: via \"{DslText.Echo(via)}\" must be an entity of this campaign, e.g. \"character:old-king\".");
            }
        }

        if (knower.Session is < 0 or > CampaignLimits.MaxSessionNumber)
        {
            problems.Add($"{where}: session is {DslText.Number(knower.Session.Value)}; it is a session number, 0 to {DslText.Number(CampaignLimits.MaxSessionNumber)}.");
        }

        if (knower.Note is { Length: > CampaignLimits.MaxNoteLength })
        {
            problems.Add($"{where}: note is longer than {DslText.Number(CampaignLimits.MaxNoteLength)} characters.");
        }
    }

    internal static void OneLine(List<string> problems, string where, string field, string? text, int max, string? blankHint = null)
    {
        if (text is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            problems.Add($"{where}: {field} is blank; give text or leave it out{(blankHint is null ? string.Empty : "; " + blankHint)}.");
        }
        else if (!DslText.IsOneLine(text.Trim(), max))
        {
            problems.Add($"{where}: {field} must be one line of at most {DslText.Number(max)} characters.");
        }
    }

    internal static string Or(DslValueSet set) =>
        set.Values.Count == 1 ? $"\"{set.Values[0]}\"" : string.Join(", ", set.Values.Take(set.Values.Count - 1)) + " or " + set.Values[^1];
}
