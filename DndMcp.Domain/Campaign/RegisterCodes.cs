using System.Globalization;
using System.Text.RegularExpressions;
using DndMcp.Domain.Core;

namespace DndMcp.Domain.Campaign;

/// <summary>
/// Register codes (<c>F36</c>, <c>Q22</c>, <c>C13</c>, <c>F56a</c>): the author's own numbering for inventions, open
/// questions and callbacks, which he refers to by number for months (contract §3.1).
///
/// <para>
/// <b>One sequence per campaign per letter, shared by entities and facts</b>, and never reused: the next code is 1 + the
/// largest number among every existing code with that letter (struck and soft-deleted rows included, a suffix ignored:
/// F56a counts as 56). Renumbering or reusing a code would silently re-point every note that says "F36 accepted". The
/// caller passes the codes of both tables of this campaign, read inside the write transaction, so a dry run shows the
/// same code the real run assigns.
/// </para>
/// </summary>
public static partial class RegisterCodes
{
    /// <summary>
    /// The next code for <paramref name="letter"/> given the campaign's <paramref name="existing"/> codes (any letters;
    /// others and malformed ones are ignored): "F" and [F1, F2, F56a, Q9] give "F57"; none gives "F1".
    /// </summary>
    /// <param name="letter">1-4 letters, any case ("f" is F).</param>
    /// <exception cref="ArgumentException"><paramref name="letter"/> is not 1-4 letters.</exception>
    /// <exception cref="DndInputException">The letter's numbers are used up (999999).</exception>
    public static string Next(string letter, IEnumerable<string?> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        if (!IsLetters(letter))
        {
            throw new ArgumentException($"\"{letter}\" is not 1-4 letters.", nameof(letter));
        }

        var prefix = letter.Trim().ToUpperInvariant();
        var max = 0L;
        foreach (var code in existing)
        {
            if (TryParse(code, out var letters, out var number, out _) && letters == prefix && number > max)
            {
                max = number;
            }
        }

        if (max >= 999_999)
        {
            throw new DndInputException($"Register {prefix} has no numbers left (the largest is {prefix}999999); use another letter.");
        }

        return prefix + (max + 1).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Whether <paramref name="letter"/> is a usable register letter: 1-4 ASCII letters.</summary>
    public static bool IsLetters(string? letter) => letter is not null && LettersPattern().IsMatch(letter.Trim());

    /// <summary>A code's parts, letters upper-cased: "f56a" → ("F", 56, "a").</summary>
    public static bool TryParse(string? code, out string letters, out long number, out string suffix)
    {
        letters = string.Empty;
        number = 0;
        suffix = string.Empty;
        if (code is null)
        {
            return false;
        }

        var match = CodePattern().Match(code.Trim());
        if (!match.Success)
        {
            return false;
        }

        letters = match.Groups["letters"].Value.ToUpperInvariant();
        number = long.Parse(match.Groups["digits"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        suffix = match.Groups["suffix"].Value.ToLowerInvariant();
        return true;
    }

    [GeneratedRegex("^[A-Za-z]{1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex LettersPattern();

    // The same shape CampaignHandle.IsCode accepts (a suffix in either case, review C11); stored codes are canonical
    // already, and a stray upper-case suffix must still count toward the sequence.
    [GeneratedRegex("^(?<letters>[A-Za-z]{1,4})(?<digits>[0-9]{1,6})(?<suffix>[A-Za-z]?)$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
