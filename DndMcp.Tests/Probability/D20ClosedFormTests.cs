using DndMcp.Domain.Probability;
using Xunit;

namespace DndMcp.Tests.Probability;

/// <summary>
/// Invariant: research A1's closed-form table holds for the face PMFs at every single-die success chance the d20 can
/// give (p = 1/20 … 19/20, i.e. "succeed on t or higher" for t = 20 … 2), for hit chances and crit chances alike.
///
/// <para>
/// The closed forms assume what the model guarantees: the successful faces are "t and up" with t ≥ 2, so a natural 1
/// always fails (the Lucky rows rely on that). The Lucky + Elven Accuracy row is not in the research table; it is
/// derived by the same argument as Lucky + Advantage (all three dice fail and one shows 1 → the one reroll decides).
/// </para>
/// </summary>
public sealed class D20ClosedFormTests
{
    private const double Tolerance = 1e-12;

    public static TheoryData<string> ClosedForms => new()
    {
        "normal", "advantage", "disadvantage", "elven accuracy", "lucky", "lucky + advantage", "lucky + disadvantage",
        "lucky + elven accuracy",
    };

    [Theory]
    [MemberData(nameof(ClosedForms))]
    public void FacePmfTail_EverySingleDieChance_MatchesTheClosedForm(string form)
    {
        var (options, closedForm) = Form(form);
        var pmf = D20.FacePmf(options);

        for (var t = 2; t <= 20; t++)
        {
            var p = (21.0 - t) / 20;
            var tail = pmf.Skip(t - 1).Sum();

            Assert.True(Math.Abs(closedForm(p) - tail) <= Tolerance, $"{form}, success on {t}+: closed form {closedForm(p):R}, face PMF {tail:R}");
        }
    }

    [Theory]
    [MemberData(nameof(ClosedForms))]
    public void Odds_HitAndCritAtEverySingleDieChance_MatchTheClosedForm(string form)
    {
        var (options, closedForm) = Form(form);

        for (var t = 2; t <= 20; t++)
        {
            var p = (21.0 - t) / 20;

            // +0 vs AC t: the lowest hitting face is t (crit range 20 caps it only at t = 20, where both agree).
            var hit = AttackRoll.Odds(0, t, 20, options).Hit;

            // Crit range t–20 against an AC nothing but a crit reaches: hit and crit are both "t and up".
            var crit = AttackRoll.Odds(0, 40, t, options);

            Assert.True(Math.Abs(closedForm(p) - hit) <= Tolerance, $"{form}, hit on {t}+: closed form {closedForm(p):R}, odds {hit:R}");
            Assert.True(Math.Abs(closedForm(p) - crit.Crit) <= Tolerance, $"{form}, crit on {t}+: closed form {closedForm(p):R}, odds {crit.Crit:R}");
            Assert.Equal(crit.Crit, crit.Hit);
        }
    }

    [Theory]
    [MemberData(nameof(ClosedForms))]
    public void Odds_BlessAtBaseSixtyFivePercent_IsTheAverageOfTheShiftedClosedForms(string form)
    {
        // Research A1: P = Σ_b ¼·T(p + b/20) for Bless at +7 vs AC 15 (p = 0.65, no clamping at these values).
        var (options, closedForm) = Form(form);
        var expected = Enumerable.Range(1, 4).Sum(b => closedForm(0.65 + (b / 20.0))) / 4;

        var hit = AttackRoll.Odds(7, 15, 20, options, BonusDice.Parse("1d4", "test dice")).Hit;

        Assert.Equal(expected, hit, Tolerance);
    }

    private static (D20Options Options, Func<double, double> ClosedForm) Form(string form) => form switch
    {
        "normal" => (D20Options.Normal, p => p),
        "advantage" => (D20Options.Advantage, p => 1 - ((1 - p) * (1 - p))),
        "disadvantage" => (D20Options.Disadvantage, p => p * p),
        "elven accuracy" => (new D20Options(D20Mode.Advantage, ElvenAccuracy: true), p => 1 - ((1 - p) * (1 - p) * (1 - p))),
        "lucky" => (new D20Options(D20Mode.Normal, Lucky: true), p => p + (p / 20)),
        "lucky + advantage" => (new D20Options(D20Mode.Advantage, Lucky: true), p =>
        {
            var q = 1 - p;
            return 1 - (q * q) + (((q * q) - ((q - 0.05) * (q - 0.05))) * p);
        }),
        "lucky + disadvantage" => (new D20Options(D20Mode.Disadvantage, Lucky: true), p => p * p * (1 + (2.0 / 20))),
        "lucky + elven accuracy" => (new D20Options(D20Mode.Advantage, Lucky: true, ElvenAccuracy: true), p =>
        {
            var q = 1 - p;
            return 1 - (q * q * q) + (((q * q * q) - ((q - 0.05) * (q - 0.05) * (q - 0.05))) * p);
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(form), form, null),
    };
}
