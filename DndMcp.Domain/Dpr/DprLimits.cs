using System.Globalization;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;

namespace DndMcp.Domain.Dpr;

/// <summary>
/// The DPR engine's bounds, in one place so its messages, the tool descriptions and the tests quote the same numbers.
///
/// <para>
/// The DSL's own limits (<see cref="DslLimits"/>: 20 attacks a turn, 3 resource-limited modifiers, 3 rate advantage
/// sources, 3 power attacks) already keep realistic builds to a few hundred turn states. These exist so that a build at
/// the edge of every limit at once fails with a message naming what to reduce, instead of running for minutes or
/// exhausting memory.
/// </para>
/// <para>
/// <b>One budget per call.</b> <see cref="WorkBudget"/> covers everything one <c>balance_dpr</c> or
/// <c>balance_compare</c> call computes (<see cref="DprAnalysis"/>, <see cref="DprComparison"/>): every level, every AC of
/// a grid, both builds, and the day horizon's extra fight runs share one meter. Measured on this engine a unit costs 10–80
/// ns, so the budget bounds a call to a few seconds whatever it asks for; a per-evaluation budget would not, since a
/// 320-cell grid on the day horizon runs the engine over a thousand times. A busy level 1–20 paladin's full 20 × 16 grid
/// (Extra Attack, GWF, Graze, GWM, a limited smite, Savage Attacker, Bless, a 30% Help) spends about 1.5% of it on either
/// horizon; a build with nearly every modifier kind spends about 0.5 million units a cell, so most of it for the full grid.
/// </para>
/// </summary>
public static class DprLimits
{
    /// <summary>
    /// Work units for one evaluation, and for one whole balance call: turn states expanded plus the multiply-adds of
    /// every damage convolution. A level-5 fighter's fight horizon spends about a thousand; a build with nearly every
    /// modifier kind about half a million without the round-1 distribution.
    /// </summary>
    public const long WorkBudget = 200_000_000;

    /// <summary>
    /// Distinct turn states one evaluator may hold: the turns of one advantage sample and power-attack choice, from every
    /// carried state the fight reaches (the memo is shared, since the start of a turn is just another state). A state is
    /// the queue position plus everything that changes the rest of the turn: once-per-turn riders spent, uses left, Vex,
    /// conditions applied, the Bonus Action, the triggers seen. Real builds stay in the hundreds (a build with nearly
    /// every modifier kind at level 11 needs about 450); three resources with 20 uses each over a 10-round fight could
    /// otherwise reach millions, and each state keeps its breakdown vector (a few hundred bytes to a kilobyte), so this is
    /// the memory bound: about 100 MB at the limit.
    /// </summary>
    public const int MaxTurnStates = 100_000;

    public const int MinRounds = 1;

    public const int MaxRounds = 10;

    /// <summary>The fight horizon's default: the DMG averages monster damage over "the first three rounds of combat".</summary>
    public const int DefaultRounds = 3;

    /// <summary>
    /// An adventuring day's encounters (the day horizon's E). Fractions are allowed: the light preset's 3.5 stands for
    /// "3–4 encounters", which no whole number says.
    /// </summary>
    public const double MinEncountersPerDay = 1;

    public const double MaxEncountersPerDay = 20;

    /// <summary>Short rests in a day (the day horizon's S): the 2014 DMG assumes two.</summary>
    public const int MaxShortRests = 5;

    /// <summary>
    /// <c>ac_range</c> spans at most this many AC points (hi − lo), so 16 ACs: the research's grid is the CR = level AC ± 3,
    /// and 16 columns still fit a readable table.
    /// </summary>
    public const int MaxAcRangeWidth = 15;

    /// <summary>
    /// Levels × ACs in one grid. With 20 levels and 16 ACs it is reached exactly, never exceeded; the check exists so a
    /// change to either bound cannot silently grow the grid.
    /// </summary>
    public const int MaxGridCells = 320;

    /// <summary>
    /// The smallest tier slope (DPR gained per level) a level-equivalent may divide by. Below it the baseline's curve is
    /// flat or falling (a build that does not scale, fighting the CR = level row whose AC rises faster than its
    /// proficiency bonus), and Δ ÷ slope would turn a 0.1 DPR feature into "two levels"; the reference slope is used.
    /// </summary>
    public const double MinTierSlope = 0.05;

    /// <summary>
    /// Two decisions whose objectives differ by less than this (relative to their size) are a tie, and a tie does not
    /// spend. Exact ties exist in real builds (smite 2d8 at use_value 9 against holding it: 9 − 9 = 0), and double
    /// rounding must not break them in whichever direction the last bit falls.
    /// </summary>
    internal const double TieTolerance = 1e-12;

    /// <summary>Whether <paramref name="candidate"/> beats <paramref name="best"/> by more than a rounding tie.</summary>
    internal static bool Beats(double candidate, double best) =>
        candidate > best + (TieTolerance * Math.Max(1.0, Math.Abs(best)));

    private static string Units => WorkBudget.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The overrun of one evaluation: the build itself is what to reduce.</summary>
    internal static DndInputException BuildTooLarge(Exception inner) => new(
        "this build is too large to compute exactly: it needs more work than one evaluation may do " +
        $"({Units} units). Reduce the damage dice (fewer or smaller " +
        "dice per hit and per rider), the attacks per turn, the resource-limited modifiers, the fight's rounds or the " +
        "levels and ACs evaluated at once.",
        inner);

    /// <summary>
    /// The overrun of a call that runs the engine many times: say how many runs it asked for, so the model can see that
    /// fewer levels, a narrower AC range or the fight horizon is the cheap fix, and the build the expensive one.
    /// </summary>
    /// <param name="what">What the call evaluates, e.g. "20 levels × 16 ACs on the day horizon (3 fight runs each)".</param>
    internal static DndInputException RequestTooLarge(string what, Exception inner) => new(
        $"this request is too large to compute exactly: {what} needs more work than one call may do ({Units} units). " +
        "Evaluate fewer levels or a narrower ac_range, use the fight horizon rather than day (the day horizon runs the fight " +
        "once more per resource-limited feature), or simplify the build: fewer or smaller damage dice, fewer attacks per turn " +
        "or resource-limited modifiers, fewer rounds.",
        inner);
}
