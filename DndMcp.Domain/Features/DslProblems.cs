using DndMcp.Domain.Core;
using FluentValidation;
using FluentValidation.Results;

namespace DndMcp.Domain.Features;

/// <summary>
/// Turns validation failures into the ONE <see cref="DndInputException"/> a DSL call throws.
///
/// <para>
/// <b>Why collect instead of throwing at the first problem</b>: a build is a large object the model writes in one go, and
/// fixing one problem per round trip turns a build with four mistakes into five calls. Up to
/// <see cref="DslLimits.MaxReportedProblems"/> problems are listed, one per line, then a count of the rest, so one badly
/// wrong build cannot bury the useful lines. Each problem starts with where it is ("attacks item 2 (Greatsword): …"),
/// counting items from 1 as the host's argument guard does, so the model reads one vocabulary whichever layer caught it.
/// </para>
/// </summary>
public static class DslProblems
{
    /// <summary>Throws one exception listing the problems, if there are any.</summary>
    /// <param name="subject">What was validated, for the first line: "build", "baseline", "variant", "feature", "target".</param>
    /// <exception cref="DndInputException">There is at least one problem.</exception>
    public static void ThrowIfAny(IReadOnlyList<string> problems, string subject)
    {
        if (problems.Count > 0)
        {
            throw Exception(problems, subject);
        }
    }

    /// <summary>The exception for a non-empty list of problems (duplicates removed, order kept).</summary>
    public static DndInputException Exception(IReadOnlyList<string> problems, string subject)
    {
        var distinct = problems.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0)
        {
            throw new ArgumentException("There are no problems to report.", nameof(problems));
        }

        if (distinct.Count == 1)
        {
            return new DndInputException($"Invalid {subject}: {distinct[0]}");
        }

        var shown = distinct.Take(DslLimits.MaxReportedProblems).Select(p => "- " + p);
        var rest = distinct.Count - DslLimits.MaxReportedProblems;
        var more = rest > 0 ? $"\n- … and {DslText.Number(rest)} more; fix these and send it again to see them." : string.Empty;
        return new DndInputException($"Invalid {subject} ({DslText.Number(distinct.Count)} problems):\n{string.Join("\n", shown)}{more}");
    }

    /// <summary>The messages of a FluentValidation result, in rule order.</summary>
    internal static IReadOnlyList<string> Messages(ValidationResult result) => result.Errors.Select(e => e.ErrorMessage).ToList();
}

/// <summary>
/// Adds problems to a FluentValidation context with the item's location in front, without FluentValidation's message
/// formatting (a message quoting JSON such as {"1": 1} must reach the model unchanged, not as a placeholder).
/// </summary>
internal readonly struct Problems<T>(ValidationContext<T> context, string where)
{
    public void Add(string message) =>
        context.AddFailure(new ValidationFailure(where, where.Length == 0 ? message : $"{where}: {message}"));

    /// <summary>Runs a parser whose <see cref="DndInputException"/> is a problem to report; returns its value or default.</summary>
    public TValue? Parse<TValue>(Func<TValue?> parse)
    {
        try
        {
            return parse();
        }
        catch (DndInputException ex)
        {
            Add(ex.Message);
            return default;
        }
    }

    /// <summary>
    /// An optional wire value: true when absent or one of <paramref name="set"/>; otherwise reports it with the accepted
    /// values.
    /// </summary>
    public bool Known(DslValueSet set, string field, string? text)
    {
        if (text is null || set.TryMatch(text, out _))
        {
            return true;
        }

        Add($"{field} \"{DslText.Echo(text)}\" is not {Article(set.What)} {set.What}; give {Or(set)}.");
        return false;
    }

    /// <summary>An optional integer: true when absent or within [min, max].</summary>
    public bool InRange(string field, int? value, int min, int max, string? hint = null)
    {
        if (value is null || (value >= min && value <= max))
        {
            return true;
        }

        Add($"{field} is {DslText.Number(value.Value)}; it is {DslText.Number(min)} to {DslText.Number(max)}{(hint is null ? "" : $" ({hint})")}.");
        return false;
    }

    /// <summary>An optional number: true when absent or finite within [min, max] (1e400 binds as infinity).</summary>
    public bool InRange(string field, double? value, double min, double max)
    {
        if (value is null || (double.IsFinite(value.Value) && value >= min && value <= max))
        {
            return true;
        }

        Add($"{field} is {Show(value.Value)}; it is a number {Show(min)} to {Show(max)}.");
        return false;
    }

    /// <summary>"gwf, archery, dueling or twf".</summary>
    public static string Or(DslValueSet set) =>
        set.Values.Count == 1 ? $"\"{set.Values[0]}\"" : string.Join(", ", set.Values.Take(set.Values.Count - 1)) + " or " + set.Values[^1];

    /// <summary>"a" or "an" for a noun in a message.</summary>
    public static string Article(string noun) => noun.Length > 0 && "aeiou".Contains(char.ToLowerInvariant(noun[0])) ? "an" : "a";

    private static string Show(double value) =>
        double.IsFinite(value) ? value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) : "not a finite number";
}
