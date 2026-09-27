using System.Globalization;
using DndMcp.Domain.Probability;
using Xunit;

namespace DndMcp.Tests.Probability;

/// <summary>
/// Invariant: for every attack bonus −5..+15, AC 5..30 and crit range 18–20, under every mode × Lucky × Elven Accuracy,
/// with and without Bless/Bane and autoCrit, <see cref="AttackRoll.Odds"/> equals an independent enumeration of the raw
/// dice (<see cref="RawD20Enumerator"/>, plus every face of the bonus d4s) judged by the literal hit rule: a face hits
/// when it crits or when it is not a natural 1 and meets the AC.
///
/// <para>
/// Without bonus dice the comparison is EXACT (bit for bit): both sides are one correctly rounded division of the same
/// fraction. With bonus dice the engine sums up to seven rounded terms, so it may differ by rounding alone (about one
/// case in six does, by one ulp at most when written); the tolerance is 1e-15, about four ulps, while any modelling
/// difference moves a probability by at least 1/(20^4·16) ≈ 4e-7.
/// </para>
/// </summary>
public sealed class AttackRollBruteForceTests
{
    private const double BonusDiceTolerance = 1e-15;

    /// <summary>Each bonus-dice text with its raw outcomes, every one equally likely.</summary>
    private static readonly Dictionary<string, int[]> BonusDiceOutcomes = new()
    {
        ["none"] = [0],
        ["1d4"] = [1, 2, 3, 4],
        ["-1d4"] = [-1, -2, -3, -4],
        ["1d4-1d4"] = [.. from bless in Enumerable.Range(1, 4) from bane in Enumerable.Range(1, 4) select bless - bane],
    };

    public static TheoryData<D20Mode, bool, bool, string, bool> Cases()
    {
        var data = new TheoryData<D20Mode, bool, bool, string, bool>();
        foreach (var mode in Enum.GetValues<D20Mode>())
        {
            foreach (var lucky in new[] { false, true })
            {
                foreach (var elvenAccuracy in new[] { false, true })
                {
                    foreach (var dice in BonusDiceOutcomes.Keys)
                    {
                        foreach (var autoCrit in new[] { false, true })
                        {
                            data.Add(mode, lucky, elvenAccuracy, dice, autoCrit);
                        }
                    }
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Odds_EveryBonusAcAndCritRange_MatchesRawDiceEnumeration(D20Mode mode, bool lucky, bool elvenAccuracy, string dice, bool autoCrit)
    {
        var kept = RawD20Enumerator.Enumerate(mode, lucky, elvenAccuracy);
        var outcomes = BonusDiceOutcomes[dice];
        var bonusDice = dice == "none" ? null : BonusDice.Parse(dice, "test dice");
        var options = new D20Options(mode, lucky, elvenAccuracy);
        var denominator = kept.Total * outcomes.Length;
        var failures = new List<string>();

        for (var attackBonus = -5; attackBonus <= 15; attackBonus++)
        {
            for (var ac = 5; ac <= 30; ac++)
            {
                for (var critMin = 18; critMin <= 20; critMin++)
                {
                    long hits = 0;
                    long crits = 0;
                    for (var face = 1; face <= 20; face++)
                    {
                        foreach (var bonus in outcomes)
                        {
                            var crit = face >= critMin;
                            var hit = crit || (face != 1 && face + attackBonus + bonus >= ac);
                            hits += hit ? kept.Counts[face] : 0;
                            crits += (autoCrit ? hit : crit) ? kept.Counts[face] : 0;
                        }
                    }

                    var expectedHit = (double)hits / denominator;
                    var expectedCrit = (double)crits / denominator;
                    var odds = AttackRoll.Odds(attackBonus, ac, critMin, options, bonusDice, autoCrit);

                    // Crit is exact whenever it does not come from the bonus-dice sum (autoCrit makes it the hit chance).
                    var hitTolerance = bonusDice is null ? 0 : BonusDiceTolerance;
                    var critTolerance = autoCrit ? hitTolerance : 0;
                    if (Math.Abs(odds.Hit - expectedHit) > hitTolerance || Math.Abs(odds.Crit - expectedCrit) > critTolerance || odds.NormalHit < 0)
                    {
                        failures.Add(string.Create(CultureInfo.InvariantCulture,
                            $"+{attackBonus} vs AC {ac} crit {critMin}: hit {odds.Hit:R} (expected {hits}/{denominator} = {expectedHit:R}), crit {odds.Crit:R} (expected {crits}/{denominator} = {expectedCrit:R})"));
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} of 1638 cases differ; first: {string.Join(" | ", failures.Take(5))}");
    }
}
