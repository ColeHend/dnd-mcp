using DndMcp.Domain.Simulation;
using DndMcp.Formatting;
using Xunit;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: <see cref="SimulationMarkdown"/> never prints a probability as certain unless it is, keeps every capped
/// list honest about what it leaves out, and keeps a replay's summary whole however little room the log gets. These are
/// the formatter's own promises, checked on hand-built reports whose extremes a real run seldom reaches on demand.
/// </summary>
public sealed class SimulationMarkdownTests
{
    [Theory]
    [InlineData(9_999, 10_000, "**The party wins 99.99%** of 10,000 fights (95% CI ")]
    [InlineData(99_999, 100_000, "**The party wins > 99.99%** of 100,000 fights (95% CI ")]
    [InlineData(1, 100_000, "**The party wins < 0.01%** of 100,000 fights (95% CI ")]
    [InlineData(10_000, 10_000, "**The party wins 100%** of 10,000 fights (all 10,000; 95% CI 99.96–100%).")]
    [InlineData(0, 100, "**The party wins 0%** of 100 fights (none; 95% CI 0–3.7%).")]
    public void Format_PartyWins_IsCertainOnlyWhenEveryFightSaysSo(long wins, long fights, string expected)
    {
        var text = SimulationMarkdown.Format(Report(wins, fights), seedGiven: true, notes: []);

        Assert.Contains(expected, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_WithoutASeed_SaysHowToReproduceWithTheSeedAsAString()
    {
        var text = SimulationMarkdown.Format(Report(5, 10, seed: 18_446_744_073_709_551_615), seedGiven: false, notes: []);

        Assert.Contains("seed 18446744073709551615 (random)*", text, StringComparison.Ordinal);
        Assert.EndsWith(
            "Seed 18446744073709551615 (drawn at random): pass \"seed\": \"18446744073709551615\" with the same arguments to reproduce " +
            "this result exactly.\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_ReplayLongerThanTheRoom_CutsTheEventsAndKeepsTheSummaryWhole()
    {
        var events = string.Concat(Enumerable.Range(1, 3_000).Select(i => $"  event {i}: something happened to someone\n"));
        var summary = "Summary of fight 7: the party wins after 3 rounds.\n- Fighter (party): 12/44 HP; dealt 90 (80 effective), took 32\n";
        var report = Report(5, 10) with { ReplayIteration = 7, ReplayLog = events + summary, ReplayOutcome = SimulationValues.Outcomes.PartyWins, ReplayRounds = 3 };

        var text = SimulationMarkdown.Format(report, seedGiven: true, notes: []);

        Assert.True(text.Length <= SimulationMarkdown.MaxChars, $"{text.Length} characters.");
        Assert.Contains("### Fight 7, turn by turn (the party wins in 3 rounds)\n\n```text\n  event 1: ", text, StringComparison.Ordinal);
        Assert.Matches(@"… [\d,]+ more characters of this fight's events are not shown, to keep the result readable; the summary below is complete\.\n" +
                       "Summary of fight 7: ", text);
        Assert.EndsWith(summary.TrimEnd() + "\n```\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_ManyWarnings_ShowsTheFirstPerStatBlockAndNamesTheRest()
    {
        var blocks = Enumerable.Range(1, SimulationMarkdown.MaxWarningBlocks + 2).Select(b => new StatBlockWarnings(
            $"Monster {b}", $"2024/monster/m{b}", "enemies",
            Enumerable.Range(1, SimulationMarkdown.MaxWarningsPerBlock + 3).Select(w => new NormalizationWarning("not_modelled", $"Action {w}", $"Warning {w}.")).ToList())).ToList();

        var text = SimulationMarkdown.Format(Report(5, 10) with { Warnings = blocks }, seedGiven: true, notes: []);

        Assert.Contains("**Monster 1** (`2024/monster/m1`, enemies):\n- Action 1: Warning 1.\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain($"- Action {SimulationMarkdown.MaxWarningsPerBlock + 1}:", text, StringComparison.Ordinal);
        Assert.Contains("- … and 3 more.", text, StringComparison.Ordinal);
        Assert.Contains($"Also warned: Monster {SimulationMarkdown.MaxWarningBlocks + 1} (7), Monster {SimulationMarkdown.MaxWarningBlocks + 2} (7).", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_ManyLongAssumptions_KeepsTheFirstAndCountsTheRest()
    {
        var assumptions = Enumerable.Range(1, 20).Select(i => $"Assumption {i}: " + new string('x', 700)).ToList();

        var text = SimulationMarkdown.Format(Report(5, 10) with { Assumptions = assumptions }, seedGiven: true, notes: []);

        Assert.Contains("- Assumption 1: ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("- Assumption 20: ", text, StringComparison.Ordinal);
        Assert.Matches(@"- … and \d+ more assumptions \(the party archetypes' own simplifications, listed last\)", text);
    }

    [Fact]
    public void Format_Compare_ShowsThePairedDifferenceInPoints()
    {
        var compare = new CompareReport
        {
            Member = 1,
            MemberName = "Fighter",
            Feature = "Plus Two",
            BaselineWins = SimulationStatistics.Wilson(700, 1000),
            VariantWins = SimulationStatistics.Wilson(800, 1000),
            WinDifference = new MeanEstimate(0.1, 0.01, 0.0804, 0.1196, 1000),
            BaselineRounds = new MeanEstimate(6.2, 0.05, 6.1, 6.3, 1000),
            VariantRounds = new MeanEstimate(5.7, 0.05, 5.6, 5.8, 1000),
            RoundsDifference = new MeanEstimate(-0.5, 0.02, -0.54, -0.46, 1000),
            BaselineAnyDeath = SimulationStatistics.Wilson(75, 1000),
            VariantAnyDeath = SimulationStatistics.Wilson(63, 1000),
            AnyDeathDifference = new MeanEstimate(-0.012, 0.003, -0.018, -0.006, 1000),
        };

        var text = SimulationMarkdown.Format(Report(700, 1000) with { Compare = compare }, seedGiven: true, notes: []);

        Assert.Contains("### Compare: Fighter (party entry 1) with Plus Two\n", text, StringComparison.Ordinal);
        var wins = text.Split('\n').Single(l => l.StartsWith("| Party wins | ", StringComparison.Ordinal));
        Assert.StartsWith("| Party wins | 70% (", wins, StringComparison.Ordinal);
        Assert.Contains("%) | 80% (", wins, StringComparison.Ordinal);
        Assert.EndsWith("%) | +10.00 points (+8.04 to +11.96) |", wins, StringComparison.Ordinal);
        Assert.Contains("| Rounds (mean) | 6.20 | 5.70 | −0.50 (−0.54 to −0.46) |", text, StringComparison.Ordinal);
    }

    /// <summary>A small, valid report: one party entry and one enemy entry, the given wins, no optional sections.</summary>
    private static SimulationReport Report(long wins, long fights, ulong seed = 42)
    {
        var zero = new MeanEstimate(0, 0, 0, 0, fights);
        CombatantReport Entry(string name, string side) => new()
        {
            Name = name,
            Side = side,
            Count = 1,
            Source = side == "party" ? "build \"Fighter\" (level 5, 2024)" : "monster 2024/monster/ogre",
            MaxHp = 44,
            ArmorClass = 18,
            DeathSaves = side == "party",
            DroppedToZero = SimulationStatistics.Wilson(0, fights),
            DeadAtEnd = SimulationStatistics.Wilson(0, fights),
            HpLost = zero,
            HpLostP50 = 0,
            HpLostP90 = 0,
            DamageDealt = zero,
            DamageDealtEffective = zero,
            DamageTaken = zero,
            DamageTakenEffective = zero,
            Kills = zero,
            Resources = [],
        };

        return new SimulationReport
        {
            Seed = seed,
            Iterations = (int)fights,
            RoundCap = 20,
            Edition = "2024",
            Surprise = SimulationValues.Surprise.None,
            EnemyHp = SimulationValues.EnemyHp.Average,
            Policies = [new PolicyEcho("party", "focus_fire", "the reachable enemy with the lowest current HP")],
            PartyWins = SimulationStatistics.Wilson(wins, fights),
            PartyDefeated = SimulationStatistics.Wilson(fights - wins, fights),
            Draw = SimulationStatistics.Wilson(0, fights),
            AnyPartyDeath = SimulationStatistics.Wilson(0, fights),
            Rounds = new RoundsSummary(new MeanEstimate(3, 0.01, 2.98, 3.02, fights), 3, 4, [0, 0, fights]),
            Combatants = [Entry("Fighter", "party"), Entry("Ogre", "enemies")],
            Warnings = [],
            Assumptions = ["No grid."],
        };
    }
}
