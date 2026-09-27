using System.Collections.ObjectModel;

namespace DndMcp.Domain.Probability;

/// <summary>
/// How many d20s a roll uses and which one counts. In-process only: the build DSL carries its own string wire values
/// (<c>"advantage"</c>, <c>"disadvantage"</c>) and converts them at resolution, so nothing stored or sent depends on this
/// enum's numbering.
/// </summary>
public enum D20Mode
{
    /// <summary>One d20.</summary>
    Normal,

    /// <summary>Two d20s, the higher counts (three with Elven Accuracy, see <see cref="D20Options.ElvenAccuracy"/>).</summary>
    Advantage,

    /// <summary>Two d20s, the lower counts.</summary>
    Disadvantage,
}

/// <summary>
/// Everything about an attacker's d20 that changes which natural face is kept. Hit and crit chances follow from the
/// kept face alone (<see cref="D20.FacePmf"/>), so these three settings are the whole cache key.
/// </summary>
/// <param name="Mode">Normal, Advantage or Disadvantage, after sources on both sides have cancelled (<see cref="D20.Resolve"/>).</param>
/// <param name="Lucky">
/// Halfling Lucky (2014) / Luck (2024): when a d20 shows a natural 1, reroll it and use the new roll, even if that is
/// another 1. With two or three dice only ONE die is rerolled however many show 1 (the 2024 text says "only one die";
/// 2014 is read the same way), so Lucky cannot rescue a double 1 with Disadvantage. The model always rerolls a 1 when one
/// shows: that is the player's best play, because replacing a 1 never lowers the kept face (under Advantage the new roll
/// is compared with the other dice; under Disadvantage a 1 is already the lowest possible), and where the reroll cannot
/// help — a 20 beside the 1 with Advantage, a double 1 with Disadvantage — the kept face is the same whatever it shows.
/// </param>
/// <param name="ElvenAccuracy">
/// Elven Accuracy (XGE, 2014; not in the 2024 rules): with Advantage on an attack using Dex, Int, Wis or Cha, reroll one
/// of the dice once. Rerolling the lower die is always right, which makes it "highest of three". It needs Advantage to
/// do anything, so with <see cref="D20Mode.Normal"/> or <see cref="D20Mode.Disadvantage"/> it is IGNORED — silently, not
/// refused: advantage that is only present on some turns (a rate source) leaves the feat idle on the others, and those
/// turns must still compute. Which attacks may use it (the ability) is the caller's check.
/// </param>
public sealed record D20Options(D20Mode Mode, bool Lucky = false, bool ElvenAccuracy = false)
{
    public static D20Options Normal { get; } = new(D20Mode.Normal);

    public static D20Options Advantage { get; } = new(D20Mode.Advantage);

    public static D20Options Disadvantage { get; } = new(D20Mode.Disadvantage);

    /// <summary>Elven Accuracy is set AND has Advantage to act on: what <see cref="D20.FacePmf"/> actually models.</summary>
    public bool ElvenAccuracyApplies => ElvenAccuracy && Mode == D20Mode.Advantage;
}

/// <summary>
/// The d20 model every attack roll and saving throw goes through: the probability of each final kept natural face,
/// per <see cref="D20Options"/>, by exact enumeration (research A1).
///
/// <para>
/// <b>Why faces and not closed forms.</b> With the kept-face distribution, hit and crit are tail sums whatever the mode,
/// and every combination (Lucky with Disadvantage, Elven Accuracy with Lucky, Bless under Advantage) is exact by
/// construction. The TypeScript port target derived each combination by hand and got Lucky + Disadvantage wrong
/// (1.0975·p² instead of 1.1·p²); the closed forms in research A1 are kept as tests of this table instead.
/// </para>
/// <para>
/// <b>Exact.</b> Every sequence of the rolled faces is visited — at most three dice plus Lucky's reroll, 20^4 = 160,000
/// sequences — with integer counts, and each tail P(kept ≥ t) is ONE division of two integers, so it is the correctly
/// rounded exact fraction. Summing twenty rounded face probabilities instead would drift by an ulp or two: harmless for
/// damage per round, but then no test could compare an attack's odds with an independent enumeration exactly. All eight
/// distinct tables are built once, when the type is first used (well under a millisecond), and shared.
/// </para>
/// </summary>
public static class D20
{
    public const int Faces = 20;

    // Indexed by Key(); EA without Advantage maps to the same slot as without EA, so they share one table.
    private static readonly FaceTable[] Tables = BuildAll();

    /// <summary>
    /// P(final kept natural face = f) for f = 1..20, at index f − 1. Sums to 1. The same read-only instance is returned
    /// for equal options (it is cached), so callers must not expect a copy they can change.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="D20Options.Mode"/> is not a defined <see cref="D20Mode"/>.</exception>
    public static IReadOnlyList<double> FacePmf(D20Options options) => Table(options).Pmf;

    /// <summary>
    /// The mode after cancelling: any Advantage and any Disadvantage together make a normal roll, however many sources
    /// there are of each (both editions). Counting them instead ("two sources of Advantage beat one of Disadvantage") is
    /// a common house rule, and silently applying it would overstate every build with stacked advantage sources.
    /// </summary>
    public static D20Mode Resolve(bool advantage, bool disadvantage) =>
        advantage == disadvantage ? D20Mode.Normal
        : advantage ? D20Mode.Advantage
        : D20Mode.Disadvantage;

    internal static FaceTable Table(D20Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Table(options.Mode, options.Lucky, options.ElvenAccuracy);
    }

    internal static FaceTable Table(D20Mode mode, bool lucky = false, bool elvenAccuracy = false) =>
        Tables[Key(mode, lucky, elvenAccuracy)];

    private static int Key(D20Mode mode, bool lucky, bool elvenAccuracy)
    {
        // A range check rather than Enum.IsDefined: this runs once per attack per DPR state.
        if ((uint)mode > (uint)D20Mode.Disadvantage)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a d20 mode.");
        }

        return ((int)mode * 4) + (lucky ? 2 : 0) + (elvenAccuracy && mode == D20Mode.Advantage ? 1 : 0);
    }

    private static FaceTable[] BuildAll()
    {
        var tables = new FaceTable[12];
        foreach (var mode in Enum.GetValues<D20Mode>())
        {
            foreach (var lucky in new[] { false, true })
            {
                foreach (var elvenAccuracy in new[] { false, true })
                {
                    var key = Key(mode, lucky, elvenAccuracy);
                    tables[key] ??= FaceTable.Enumerate(mode, lucky, elvenAccuracy && mode == D20Mode.Advantage);
                }
            }
        }

        // Slots 1, 3, 9 and 11 (Elven Accuracy without Advantage) are never produced by Key, so they stay null.
        return tables;
    }
}

/// <summary>
/// One option set's kept-face distribution, with its tails precomputed from integer counts so the hot paths
/// (<see cref="AttackRoll.Odds"/>, <see cref="SavingThrow.FailChance"/>, called once per attack per DPR state) are an
/// array read.
/// </summary>
internal sealed class FaceTable
{
    private readonly double[] _atLeast;
    private readonly double[] _below;

    private FaceTable(IReadOnlyList<double> pmf, double[] atLeast, double[] below)
    {
        Pmf = pmf;
        _atLeast = atLeast;
        _below = below;
    }

    public IReadOnlyList<double> Pmf { get; }

    /// <summary>P(kept natural face ≥ <paramref name="face"/>): 1 at or below 1, 0 above 20.</summary>
    public double AtLeast(long face) => _atLeast[(int)Math.Clamp(face, 1, D20.Faces + 1)];

    /// <summary>P(kept natural face &lt; <paramref name="face"/>): 0 at or below 1, 1 above 20.</summary>
    public double Below(long face) => _below[(int)Math.Clamp(face, 1, D20.Faces + 1)];

    /// <summary>
    /// Visits every sequence of <c>dice</c> faces, plus the Lucky reroll die as one more position when Lucky is on. The
    /// reroll position is always enumerated, and simply ignored when no die shows 1, so every sequence has the same
    /// weight and the counts divide by one total.
    /// </summary>
    public static FaceTable Enumerate(D20Mode mode, bool lucky, bool elvenAccuracy)
    {
        var dice = mode switch
        {
            D20Mode.Normal => 1,
            D20Mode.Advantage => elvenAccuracy ? 3 : 2,
            D20Mode.Disadvantage => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a d20 mode."),
        };
        var highest = mode != D20Mode.Disadvantage;
        var positions = dice + (lucky ? 1 : 0);

        var total = 1L;
        for (var i = 0; i < positions; i++)
        {
            total *= D20.Faces;
        }

        var counts = new long[D20.Faces + 2];
        var roll = new int[positions];
        for (var sequence = 0L; sequence < total; sequence++)
        {
            var rest = sequence;
            for (var i = 0; i < positions; i++)
            {
                roll[i] = (int)(rest % D20.Faces) + 1;
                rest /= D20.Faces;
            }

            counts[Kept(roll, dice, lucky, highest)]++;
        }

        var pmf = new double[D20.Faces];
        for (var face = 1; face <= D20.Faces; face++)
        {
            pmf[face - 1] = (double)counts[face] / total;
        }

        // Index t holds P(kept ≥ t) and P(kept < t) for t = 1..21 (index 0 unused), each one exact division.
        var atLeast = new double[D20.Faces + 2];
        var below = new double[D20.Faces + 2];
        var tail = 0L;
        for (var face = D20.Faces + 1; face >= 1; face--)
        {
            tail += counts[face];
            atLeast[face] = (double)tail / total;
            below[face] = (double)(total - tail) / total;
        }

        return new FaceTable(Array.AsReadOnly(pmf), atLeast, below);
    }

    /// <summary>
    /// The face that counts. With Lucky, the first die showing 1 is replaced by the reroll (the last position) — one die
    /// only, whatever the others show; which 1 is replaced cannot matter, they are the same face.
    /// </summary>
    private static int Kept(int[] roll, int dice, bool lucky, bool highest)
    {
        var rerolled = lucky ? Array.IndexOf(roll, 1, 0, dice) : -1;
        var kept = highest ? 0 : D20.Faces + 1;
        for (var i = 0; i < dice; i++)
        {
            var face = i == rerolled ? roll[dice] : roll[i];
            kept = highest ? Math.Max(kept, face) : Math.Min(kept, face);
        }

        return kept;
    }
}
