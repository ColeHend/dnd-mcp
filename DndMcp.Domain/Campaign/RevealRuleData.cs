using System.Text.Json;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;

namespace DndMcp.Domain.Campaign;

/// <summary>
/// The <c>data</c> of a <c>rule</c> entity of subtype <c>reveal_rule</c>: a vocabulary rule not tied to one fact, such as
/// Belmakor's "no name, no timespan for the old king" (contract §3.4).
///
/// <para>
/// <b>Shape:</b> <c>{forbidden_terms, forbidden_patterns, preferred_terms, until, note}</c>, the same vocabulary a gate
/// carries, so both feed one <see cref="ForbiddenVocabulary.Scan"/>. The rule is active while any <c>until</c> fact is
/// not in play, and always when <c>until</c> is empty. <c>until</c> holds fact handles as the model wrote them
/// (<c>f:12</c>, <c>F36</c>): both are stable (fact sequence numbers are never reused and codes never change), so the
/// stored data is exactly what the author wrote and needs no translation on the way in or out.
/// </para>
/// <para>
/// <b>Unknown keys are refused</b>, listed by name: <c>data</c> is otherwise a free object, and a misspelt
/// <c>"forbiden_terms"</c> stored silently would be a rule that never fires, which is worse than no rule because the
/// author believes it is checked.
/// </para>
/// </summary>
public sealed record RevealRuleData(
    IReadOnlyList<string> ForbiddenTerms,
    IReadOnlyList<string> ForbiddenPatterns,
    IReadOnlyList<string> PreferredTerms,
    IReadOnlyList<string> Until,
    string? Note)
{
    /// <summary>The keys a reveal rule's data takes, in the order messages list them.</summary>
    public static IReadOnlyList<string> Keys { get; } = ["forbidden_terms", "forbidden_patterns", "preferred_terms", "until", "note"];

    /// <summary>
    /// The problems with a reveal rule's data (the whole object, after any merge patch), each starting with
    /// <paramref name="subject"/>. Empty when valid.
    /// </summary>
    public static IReadOnlyList<string> Validate(JsonElement data, string subject)
    {
        var problems = new List<string>();
        Read(data, subject, problems);
        return problems;
    }

    /// <summary>Reads and validates a reveal rule's data.</summary>
    /// <exception cref="DndInputException">The data is not a valid reveal rule; every problem is listed.</exception>
    public static RevealRuleData Parse(JsonElement data, string subject = "data")
    {
        var problems = new List<string>();
        var rule = Read(data, subject, problems);
        DslProblems.ThrowIfAny(problems, "reveal rule");
        return rule!;
    }

    /// <summary>Reads and validates a reveal rule's stored data (a JSON object's text).</summary>
    /// <exception cref="DndInputException">The text is not JSON, or not a valid reveal rule.</exception>
    public static RevealRuleData Parse(string json, string subject = "data")
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            return Parse(document.RootElement, subject);
        }
        catch (JsonException ex)
        {
            throw new DndInputException($"Invalid reveal rule: {subject} is not a JSON object.", ex);
        }
    }

    /// <summary>
    /// For readers: the rule, or false when the stored data is not a valid reveal rule (a read must never fail on one bad
    /// row; the write path validates on every change, so this is only reachable through data written by hand).
    /// </summary>
    public static bool TryParse(string? json, out RevealRuleData? rule)
    {
        rule = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var problems = new List<string>();
            var read = Read(document.RootElement, "data", problems);
            rule = problems.Count == 0 ? read : null;
            return rule is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Whether the rule holds: some <c>until</c> fact is not in play, or there are none.</summary>
    public bool IsActive(Func<string, bool> inPlay)
    {
        ArgumentNullException.ThrowIfNull(inPlay);
        return Until.Count == 0 || Until.Any(f => !inPlay(f));
    }

    /// <summary>The rule for <see cref="ForbiddenVocabulary.Scan"/>.</summary>
    /// <param name="source">The rule entity's handle, for the report.</param>
    public ForbiddenRule ToRule(string source) => new(source, ForbiddenTerms, ForbiddenPatterns, PreferredTerms, Note);

    private static RevealRuleData? Read(JsonElement data, string subject, List<string> problems)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"{subject} must be a JSON object such as {{\"forbidden_terms\": [\"Keras\"], \"forbidden_patterns\": [\"<number>-year-old\"]}}.");
            return null;
        }

        var unknown = data.EnumerateObject().Select(p => p.Name).Where(n => !Keys.Contains(n, StringComparer.Ordinal)).ToList();
        if (unknown.Count > 0)
        {
            var names = string.Join(", ", unknown.Take(5).Select(n => $"\"{DslText.Echo(n)}\""));
            problems.Add($"{subject} has unknown key{(unknown.Count == 1 ? "" : "s")} {names}; a reveal rule's data takes {string.Join(", ", Keys)}.");
        }

        var terms = Strings(data, subject, "forbidden_terms", problems);
        var patterns = Strings(data, subject, "forbidden_patterns", problems);
        var preferred = Strings(data, subject, "preferred_terms", problems);
        var until = Strings(data, subject, "until", problems);
        var termCount = FactGates.TermList(problems, subject, "forbidden_terms", terms, pattern: false);
        var patternCount = FactGates.TermList(problems, subject, "forbidden_patterns", patterns, pattern: true);
        FactGates.TermList(problems, subject, "preferred_terms", preferred, pattern: false);
        FactGates.FactList(problems, subject, "until", until);
        if (termCount == 0 && patternCount == 0 && terms is null && patterns is null)
        {
            problems.Add($"{subject}: a reveal rule forbids words; give forbidden_terms or forbidden_patterns, e.g. {{\"forbidden_terms\": [\"Keras\"]}}.");
        }

        string? note = null;
        if (data.TryGetProperty("note", out var noteValue) && noteValue.ValueKind != JsonValueKind.Null)
        {
            if (noteValue.ValueKind != JsonValueKind.String)
            {
                problems.Add($"{subject} note must be a string, not {DslText.Describe(noteValue)}.");
            }
            else
            {
                note = noteValue.GetString();
                if (note is { Length: > CampaignLimits.MaxNoteLength })
                {
                    problems.Add($"{subject} note is longer than {DslText.Number(CampaignLimits.MaxNoteLength)} characters.");
                }
            }
        }

        return new RevealRuleData(terms ?? [], patterns ?? [], preferred ?? [], until ?? [], note);
    }

    /// <summary>An optional array of strings; null when absent or JSON null.</summary>
    private static List<string>? Strings(JsonElement data, string subject, string key, List<string> problems)
    {
        if (!data.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            problems.Add($"{subject} {key} must be an array of strings, not {DslText.Describe(value)}.");
            return null;
        }

        var list = new List<string>();
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            index++;
            if (item.ValueKind == JsonValueKind.String)
            {
                list.Add(item.GetString()!);
            }
            else
            {
                problems.Add($"{subject} {key} item {DslText.Number(index)} must be a string, not {DslText.Describe(item)}.");
            }
        }

        return list;
    }
}
