using System.Globalization;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;

namespace DndMcp.Domain.Dpr;

/// <summary>
/// The day horizon's rest presets (research A9), as <c>rest_preset</c> takes them. Strings, not an enum: results echo
/// them and a stored comparison keeps them.
///
/// <para>
/// <b>Only one of them is official.</b> The 2014 DMG (p. 84) gives "about six to eight medium or hard encounters" and
/// "two short rests" a day; the 2024 DMG removed the adventuring day and every encounters-per-rest figure. The light day
/// is a community convention for the shorter days tables actually play, and its label says so, so a model never cites
/// it as a rule.
/// </para>
/// </summary>
public static class RestPresets
{
    /// <summary>The 2014 DMG: 6–8 encounters (E = 7, the midpoint), 2 short rests.</summary>
    public const string Dmg2014 = "dmg2014";

    /// <summary>3–4 encounters (E = 3.5), 1 short rest; unofficial.</summary>
    public const string Light = "light";

    /// <summary>Encounters and short rests given by the caller (or a preset with either overridden).</summary>
    public const string Custom = "custom";

    public const string Default = Dmg2014;

    public static readonly DslValueSet Set = new(
        "rest preset",
        [Dmg2014, Light, Custom],
        new Dictionary<string, string> { ["dmg"] = Dmg2014, ["2014"] = Dmg2014, ["light day"] = Light });
}

/// <summary>
/// An adventuring day for the day horizon: E encounters and S short rests between long rests. Uses per day of a limited
/// feature are its uses per long rest, or its uses per short rest × (S + 1).
/// </summary>
/// <param name="Preset">A <see cref="RestPresets"/> value.</param>
/// <param name="EncountersPerDay">E (1–20; fractions allowed: 3.5 means "3–4").</param>
/// <param name="ShortRests">S (0–5).</param>
/// <param name="Label">How results name the assumption, source included ("2014 DMG: 6–8 encounters, 2 short rests").</param>
public sealed record DayAssumptions(string Preset, double EncountersPerDay, int ShortRests, string Label)
{
    public static DayAssumptions Dmg2014 { get; } = new(RestPresets.Dmg2014, 7, 2, "2014 DMG: 6–8 encounters, 2 short rests");

    public static DayAssumptions Light { get; } = new(
        RestPresets.Light, 3.5, 1, "light day: 3–4 encounters, 1 short rest — unofficial; 2024 has no adventuring-day guidance");

    /// <summary>A custom day, labelled with its numbers.</summary>
    /// <exception cref="DndInputException">E or S out of range.</exception>
    public static DayAssumptions Custom(double encountersPerDay, int shortRests)
    {
        CheckEncounters(encountersPerDay);
        CheckShortRests(shortRests);
        return new DayAssumptions(
            RestPresets.Custom,
            encountersPerDay,
            shortRests,
            $"custom: {Encounters(encountersPerDay)}, {shortRests.ToString(CultureInfo.InvariantCulture)} short rest{(shortRests == 1 ? "" : "s")}");
    }

    /// <summary>
    /// The day from <c>rest_preset</c>, <c>encounters_per_day</c> and <c>short_rests</c> as the tools take them: the preset
    /// (default dmg2014), with either number overriding it and making the day "custom"; "custom" itself needs both.
    /// </summary>
    /// <exception cref="DndInputException">An unknown preset, a value out of range, or "custom" without both numbers.</exception>
    public static DayAssumptions Resolve(string? restPreset, double? encountersPerDay, int? shortRests)
    {
        var preset = RestPresets.Default;
        if (!string.IsNullOrWhiteSpace(restPreset) && !RestPresets.Set.TryMatch(restPreset, out preset))
        {
            throw new DndInputException(
                $"rest_preset \"{DslText.Echo(restPreset)}\" is not a rest preset; use \"dmg2014\" (the 2014 DMG's 6–8 encounters and 2 " +
                "short rests), \"light\" (3–4 encounters, 1 short rest; unofficial) or \"custom\" with encounters_per_day and " +
                "short_rests, e.g. {\"rest_preset\": \"custom\", \"encounters_per_day\": 4, \"short_rests\": 1}.");
        }

        if (encountersPerDay is { } e)
        {
            CheckEncounters(e);
        }

        if (shortRests is { } s)
        {
            CheckShortRests(s);
        }

        if (preset == RestPresets.Custom)
        {
            if (encountersPerDay is null || shortRests is null)
            {
                throw new DndInputException(
                    "rest_preset \"custom\" needs both encounters_per_day and short_rests, e.g. {\"rest_preset\": \"custom\", " +
                    "\"encounters_per_day\": 4, \"short_rests\": 1}; or give either number with a preset to override just that one.");
            }

            return Custom(encountersPerDay.Value, shortRests.Value);
        }

        var basis = preset == RestPresets.Light ? Light : Dmg2014;
        return encountersPerDay is null && shortRests is null
            ? basis
            : Custom(encountersPerDay ?? basis.EncountersPerDay, shortRests ?? basis.ShortRests);
    }

    /// <summary>
    /// Uses a day: uses per long rest, or uses per short rest × (S + 1), since a short-rest feature comes back at each of
    /// the S short rests and at the long rest.
    /// </summary>
    public int UsesPerDay(ResolvedResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return resource.Per == DslValues.Rests.ShortRest ? resource.Uses * (ShortRests + 1) : resource.Uses;
    }

    /// <summary>"7", "3.5": E as results print it.</summary>
    internal static string Format(double encounters) => encounters.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>"7 encounters", "1 encounter".</summary>
    internal static string Encounters(double encounters) => $"{Format(encounters)} encounter{(encounters == 1 ? "" : "s")}";

    private static void CheckEncounters(double encounters)
    {
        if (!double.IsFinite(encounters) || encounters is < DprLimits.MinEncountersPerDay or > DprLimits.MaxEncountersPerDay)
        {
            throw new DndInputException(
                $"encounters_per_day is {Echo(encounters)}; an adventuring day " +
                $"has {Format(DprLimits.MinEncountersPerDay)} to {Format(DprLimits.MaxEncountersPerDay)} encounters (the 2014 DMG's is 6–8; " +
                "3.5 means 3–4).");
        }
    }

    // The value as a refusal quotes it: 1e300 printed with "0.##" is a 301-digit number, so a large one is shown in
    // exponent form instead.
    private static string Echo(double encounters) =>
        !double.IsFinite(encounters) ? "not a finite number"
        : Math.Abs(encounters) < 1e6 ? Format(encounters)
        : encounters.ToString("0.##e+0", CultureInfo.InvariantCulture);

    private static void CheckShortRests(int shortRests)
    {
        if (shortRests is < 0 or > DprLimits.MaxShortRests)
        {
            throw new DndInputException(
                $"short_rests is {shortRests.ToString(CultureInfo.InvariantCulture)}; a day has 0 to {DprLimits.MaxShortRests} short rests " +
                "between long rests (the 2014 DMG assumes 2).");
        }
    }
}

/// <summary>
/// Which horizon a call headlines and the parameters of all three: what every result echoes (contract §4.4), so a
/// number is never read without the assumption that produced it.
///
/// <para>
/// <b>All three horizons are always described</b> (research A9: "always report three horizons"): a call headlines one,
/// and its summary (<see cref="HorizonSummary"/>) gives the nova, the fight and the day side by side. So
/// <see cref="Rounds"/> and <see cref="Day"/> are always set and always used, whatever <see cref="Horizon"/> is:
/// <see cref="Rounds"/> is the fight length R (the fight horizon's rounds, the length of each of the day's fights, the
/// fight over which "P(it lands at least once per fight)" is measured), and <see cref="Day"/> the adventuring day.
/// </para>
/// </summary>
public sealed record HorizonSettings
{
    /// <summary>The headline horizon: a <see cref="DprHorizons"/> value.</summary>
    public required string Horizon { get; init; }

    /// <summary>R, the rounds in a fight (1–10).</summary>
    public required int Rounds { get; init; }

    /// <summary>The adventuring day: the day horizon's, and the summary's day figure on the others.</summary>
    public required DayAssumptions Day { get; init; }

    public static HorizonSettings Round1 { get; } = new() { Horizon = DprHorizons.Round1, Rounds = DprLimits.DefaultRounds, Day = DayAssumptions.Dmg2014 };

    public static HorizonSettings Fight(int rounds = DprLimits.DefaultRounds, DayAssumptions? day = null)
    {
        CheckRounds(rounds);
        return new HorizonSettings { Horizon = DprHorizons.Fight, Rounds = rounds, Day = day ?? DayAssumptions.Dmg2014 };
    }

    public static HorizonSettings ForDay(DayAssumptions day, int rounds = DprLimits.DefaultRounds)
    {
        ArgumentNullException.ThrowIfNull(day);
        CheckRounds(rounds);
        return new HorizonSettings { Horizon = DprHorizons.Day, Rounds = rounds, Day = day };
    }

    /// <summary>
    /// The settings from the tools' parameters: <c>horizon</c> (default fight), <c>rounds</c> (default 3),
    /// <c>rest_preset</c> (default dmg2014), <c>encounters_per_day</c> and <c>short_rests</c>. Every value is checked and
    /// used whatever the headline horizon, since the summary reports all three.
    /// </summary>
    /// <exception cref="DndInputException">An unknown horizon or preset, or a number out of range.</exception>
    public static HorizonSettings Resolve(string? horizon, int? rounds, string? restPreset, double? encountersPerDay, int? shortRests)
    {
        var kind = DprHorizons.Fight;
        if (!string.IsNullOrWhiteSpace(horizon) && !DprHorizons.Set.TryMatch(horizon, out kind))
        {
            throw new DndInputException(
                $"horizon \"{DslText.Echo(horizon)}\" is not a horizon; use \"round1\" (one turn: the nova), \"fight\" (the mean over a " +
                "fight of rounds, default 3) or \"day\" (limited features spread over an adventuring day, see rest_preset).");
        }

        var r = rounds ?? DprLimits.DefaultRounds;
        CheckRounds(r);
        return new HorizonSettings { Horizon = kind, Rounds = r, Day = DayAssumptions.Resolve(restPreset, encountersPerDay, shortRests) };
    }

    /// <summary>
    /// "round 1 (the nova)", "a 3-round fight (the mean per round)", "an adventuring day (2014 DMG: …; 7 encounters of 3
    /// rounds, 2 short rests)": the headline assumption as a result's heading states it.
    /// </summary>
    public string Label => Horizon switch
    {
        DprHorizons.Round1 => "round 1 (the nova)",
        DprHorizons.Fight => FightLabel,
        _ => DayLabel,
    };

    /// <summary>"a 3-round fight (the mean per round)".</summary>
    public string FightLabel => $"a {Rounds.ToString(CultureInfo.InvariantCulture)}-round fight (the mean per round)";

    /// <summary>"an adventuring day (2014 DMG: 6–8 encounters, 2 short rests; 7 encounters of 3 rounds, 2 short rests)".</summary>
    public string DayLabel =>
        $"an adventuring day ({Day.Label}; {DayAssumptions.Encounters(Day.EncountersPerDay)} of " +
        $"{Rounds.ToString(CultureInfo.InvariantCulture)} rounds, {Day.ShortRests.ToString(CultureInfo.InvariantCulture)} short " +
        $"rest{(Day.ShortRests == 1 ? "" : "s")})";

    /// <summary>The engine options for this horizon's own run: round 1, or a fight of R rounds (the day's fights too).</summary>
    internal DprOptions EngineOptions(bool includeDistribution) => Horizon == DprHorizons.Round1
        ? DprOptions.Round1 with { IncludeDistribution = includeDistribution }
        : DprOptions.Fight(Rounds) with { IncludeDistribution = includeDistribution };

    private static void CheckRounds(int rounds)
    {
        if (rounds is < DprLimits.MinRounds or > DprLimits.MaxRounds)
        {
            throw new DndInputException(string.Create(CultureInfo.InvariantCulture,
                $"rounds is {rounds}; a fight is {DprLimits.MinRounds} to {DprLimits.MaxRounds} rounds (3 is the DMG's convention)."));
        }
    }
}
