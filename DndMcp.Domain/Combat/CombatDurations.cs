using System.Globalization;
using System.Text.RegularExpressions;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using D = DndMcp.Domain.Combat.CombatValues.Durations;

namespace DndMcp.Domain.Combat;

/// <summary>
/// A duration as the caller typed it, read into the tracker's vocabulary (contract §5.13): the kind, and for
/// <c>rounds</c> how many, for <c>end_of_round</c> which round (null: the current one).
/// </summary>
public sealed record ParsedDuration(string Kind, int? Rounds = null, int? Round = null);

/// <summary>
/// Reads the durations a model types (contract §5.13: "Input phrases (forgiving)") into <see cref="ParsedDuration"/>:
/// every canonical name, "1 minute" (10 rounds), "10 minutes" (100), "1 hour" (600), "3 rounds", "until the end of its
/// next turn" (the target's), "until the end of your next turn" and "… of the source's next turn" (the source's), the
/// same with "start", "save ends", "concentration", "until escape", "end of round 2", "until removed", "fight".
/// </summary>
/// <remarks>
/// "Its" is the target's turn and "your" the source's (contract §5.13), as a spell's caster reads "your" and a condition's
/// bearer "its". A stat block that names the monster ("until the end of the mummy's next turn") is the source's: the
/// caller types the canonical name or "your". A refusal lists the canonical names, which say whose turn outright.
/// </remarks>
public static partial class CombatDurations
{
    /// <summary>The most rounds a duration may run (1,000 hours is far beyond any fight; the column stores an int).</summary>
    public const int MaxRounds = 600_000;

    /// <summary>Rounds per minute (a round is 6 seconds: SRD 5.1 "Time", SRD 5.2 "a round represents about 6 seconds").</summary>
    public const int RoundsPerMinute = 10;

    /// <summary>The phrase read, or a refusal naming what is accepted.</summary>
    /// <exception cref="DndInputException">Not a duration the tracker knows.</exception>
    public static ParsedDuration Parse(string text)
    {
        if (TryParse(text, out var parsed))
        {
            return parsed;
        }

        throw new DndInputException(
            $"duration \"{DslText.Echo(text)}\" is not a duration the tracker knows; give one of {D.Set.List}, or \"1 minute\", " +
            "\"10 minutes\", \"1 hour\", \"3 rounds\", \"until the end of its next turn\" (the target's), \"until the end of your " +
            "next turn\" (the source's), \"save ends\", \"until escape\", \"end of round 2\", \"until removed\".");
    }

    /// <summary>Reads <paramref name="text"/>; false when it is not a known duration.</summary>
    public static bool TryParse(string? text, out ParsedDuration parsed)
    {
        parsed = new ParsedDuration(D.Fight);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var words = Spaces().Replace(text.Trim().ToLowerInvariant().Replace('’', '\'').Replace('_', ' ').Replace('-', ' '), " ");
        if (D.Set.TryMatch(text, out var canonical))
        {
            // "rounds" alone says no count: "3 rounds" or "1 minute" does.
            parsed = new ParsedDuration(canonical);
            return canonical != D.Rounds;
        }

        if (Timed().Match(words) is { Success: true } timed)
        {
            if (!int.TryParse(timed.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1)
            {
                return false;
            }

            var unit = timed.Groups["unit"].Value;
            long rounds = unit.StartsWith("minute", StringComparison.Ordinal) ? (long)n * RoundsPerMinute
                : unit.StartsWith("hour", StringComparison.Ordinal) ? (long)n * RoundsPerMinute * 60
                : n;
            if (rounds > MaxRounds)
            {
                return false;
            }

            parsed = new ParsedDuration(D.Rounds, Rounds: (int)rounds);
            return true;
        }

        if (OneUnit().Match(words) is { Success: true } one)
        {
            var unit = one.Groups["unit"].Value;
            parsed = new ParsedDuration(D.Rounds, Rounds: unit == "minute" ? RoundsPerMinute : unit == "hour" ? RoundsPerMinute * 60 : 1);
            return true;
        }

        if (EndOfRound().Match(words) is { Success: true } endOfRound)
        {
            if (endOfRound.Groups["r"].Success)
            {
                if (!int.TryParse(endOfRound.Groups["r"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var r) || r < 1)
                {
                    return false;
                }

                parsed = new ParsedDuration(D.EndOfRound, Round: r);
            }
            else
            {
                parsed = new ParsedDuration(D.EndOfRound);
            }

            return true;
        }

        if (TurnBoundary().Match(words) is { Success: true } boundary)
        {
            var start = boundary.Groups["edge"].Value == "start";
            var whose = boundary.Groups["whose"].Value;
            var source = whose is "your" or "the source's" or "source's" or "its source's" or "the source" or "source";
            parsed = new ParsedDuration(start
                ? source ? D.UntilStartOfSourceTurn : D.UntilStartOfTargetTurn
                : source ? D.UntilEndOfSourceTurn : D.UntilEndOfTargetTurn);
            return true;
        }

        var known = words switch
        {
            "save ends" or "until saved" or "until it saves" or "save" => D.SaveEnds,
            "concentration" or "while concentrating" or "concentration held" => D.Concentration,
            "until escape" or "until escaped" or "until it escapes" or "escape" => D.UntilEscape,
            "until removed" or "removed" or "permanent" or "until cured" => D.UntilRemoved,
            "fight" or "until the end of the fight" or "the fight" or "combat" or "until the fight ends" or "end of fight" => D.Fight,
            "until stands" or "until it stands" or "until standing" => D.UntilStands,
            _ => null,
        };

        if (known is null)
        {
            return false;
        }

        parsed = new ParsedDuration(known);
        return true;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^(?:for |up to )?(?<n>\d{1,7}) (?<unit>rounds?|minutes?|hours?)$")]
    private static partial Regex Timed();

    [GeneratedRegex(@"^(?:for |up to )?(?:an?|one) (?<unit>round|minute|hour)$")]
    private static partial Regex OneUnit();

    [GeneratedRegex(@"^(?:until )?(?:the )?end of (?:the )?round(?: (?<r>\d{1,6}))?$")]
    private static partial Regex EndOfRound();

    [GeneratedRegex(@"^(?:until )?(?:the )?(?<edge>start|end) of (?<whose>its|your|the target's|target's|the source's|source's|its source's|their|the source|source|the target|target) next turn$")]
    private static partial Regex TurnBoundary();
}
