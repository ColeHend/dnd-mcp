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
    public void Format_WithoutASeed_SaysHowToReproduceWithTheSeedAsANumber()
    {
        // A drawn seed is below 2^53 (fix F1, U10), so it is passed back as the JSON number the schema takes.
        var text = SimulationMarkdown.Format(Report(5, 10, seed: 9_007_199_254_740_991), seedGiven: false, notes: []);

        Assert.Contains("seed 9007199254740991 (random)*", text, StringComparison.Ordinal);
        Assert.EndsWith(
            "Seed 9007199254740991 (drawn at random): pass \"seed\": 9007199254740991 with the same arguments to reproduce " +
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
    public void Format_ReplayLongerThanTheRoom_FillsTheResultToJustUnderTwentyFourThousandCharacters()
    {
        // 24,000 characters (about 6,000 tokens) is the contract's ceiling, written out here rather than read from MaxChars:
        // with the constant raised, every long replay would grow past it and the tests that compare with MaxChars still pass.
        var events = string.Concat(Enumerable.Range(1, 3_000).Select(i => $"  event {i}: something happened to someone\n"));
        var report = Report(5, 10) with
        {
            ReplayIteration = 1,
            ReplayLog = events + "Summary of fight 1: the party wins after 3 rounds.\n",
            ReplayOutcome = SimulationValues.Outcomes.PartyWins,
            ReplayRounds = 3,
        };

        var text = SimulationMarkdown.Format(report, seedGiven: true, notes: []);

        Assert.InRange(text.Length, 23_000, 24_000);
    }

    [Fact]
    public void Format_ReplaySummaryPastTheCeiling_IsCutWithItsFenceClosed()
    {
        // A summary is never cut by the replay itself; one longer than the whole ceiling is cut by the final cap, which must
        // close the code fence it cuts inside.
        var summary = "Summary of fight 1: a draw at the round cap after 100 rounds.\n" +
                      string.Concat(Enumerable.Range(1, 600).Select(i => $"- Creature {i} (enemy): 5000/5000 HP; dealt 0 (0 effective), took 0\n"));
        var report = Report(5, 10) with { ReplayIteration = 1, ReplayLog = "  event\n" + summary, ReplayOutcome = SimulationValues.Outcomes.Draw, ReplayRounds = 100 };

        var text = SimulationMarkdown.Format(report, seedGiven: true, notes: []);

        Assert.True(text.Length <= SimulationMarkdown.MaxChars, $"{text.Length} characters.");
        Assert.Contains("*[Cut to keep the result readable: ", text, StringComparison.Ordinal);
        Assert.Equal(0, text.Split('\n').Count(l => l.StartsWith("```", StringComparison.Ordinal)) % 2);
    }

    [Fact]
    public void Format_AMonsterOnThePartysSide_ShowsWhetherItDied()
    {
        // "Dead at end" is shown for everything that makes death saves and for every party member: an SRD monster ally dies
        // at 0 HP, and how often it did is the party's loss.
        var report = Report(5, 10);
        var ally = report.Combatants[1] with { Name = "Wolf", Side = "party", DeathSaves = false, DeadAtEnd = new CreatureShare(3, 10) };

        var text = SimulationMarkdown.Format(report with { Combatants = [report.Combatants[0], ally] }, seedGiven: true, notes: []);

        var row = text.Split('\n').Single(l => l.StartsWith("| Wolf | party |", StringComparison.Ordinal));
        Assert.Contains("| 30% |", row, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_PartyMembersLeftDying_AreShownApartFromTheDeadWithWhatThatMeans()
    {
        // A wipe ends the fight with its members dying: "Party defeated 100% · a party member dies 0.1%" alone reads as a
        // wipe nobody died in. The dying are their own figure, in the headline and per combatant, and the result says their
        // death saves were never rolled rather than guessing them.
        var report = Report(0, 1000);
        var fighter = report.Combatants[0] with { DyingAtEnd = new CreatureShare(990, 1000), DeadAtEnd = new CreatureShare(1, 1000) };
        report = report with
        {
            AnyPartyDeath = SimulationStatistics.Wilson(1, 1000),
            AnyPartyDying = SimulationStatistics.Wilson(990, 1000),
            Combatants = [fighter, report.Combatants[1]],
        };

        var text = SimulationMarkdown.Format(report, seedGiven: true, notes: []);

        Assert.Contains("a party member dies 0.1% (0.02–0.56%) · a party member is left dying 99% (98.17–99.46%).\n", text, StringComparison.Ordinal);
        Assert.Contains("their death saves are not rolled, so whether they die is not in the figures.", text, StringComparison.Ordinal);
        Assert.Contains("| Dead at end | Dying at end |", text, StringComparison.Ordinal);
        var row = text.Split('\n').Single(l => l.StartsWith("| Fighter | party |", StringComparison.Ordinal));
        Assert.Contains("| 0.1% | 99% |", row, StringComparison.Ordinal);
        var ogre = text.Split('\n').Single(l => l.StartsWith("| Ogre | enemy |", StringComparison.Ordinal));
        Assert.Contains("| — | — |", ogre, StringComparison.Ordinal); // no death saves: neither dead-at-end nor dying is shown
    }

    [Fact]
    public void Format_NobodyLeftDying_TheHeadlineSaysNothingOfIt()
    {
        var text = SimulationMarkdown.Format(Report(5, 10), seedGiven: true, notes: []);

        Assert.Contains("a party member dies 0% (0–27.75%).\nRounds: ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("dying", text.Split("### Per combatant")[0], StringComparison.Ordinal);
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
        var text = SimulationMarkdown.Format(Report(700, 1000) with { Compare = Compare(dyingWithout: 0, dyingWith: 0) }, seedGiven: true, notes: []);

        Assert.Contains("### Compare: Fighter (party entry 1) with Plus Two\n", text, StringComparison.Ordinal);
        var wins = text.Split('\n').Single(l => l.StartsWith("| Party wins | ", StringComparison.Ordinal));
        Assert.StartsWith("| Party wins | 70% (", wins, StringComparison.Ordinal);
        Assert.Contains("%) | 80% (", wins, StringComparison.Ordinal);
        Assert.EndsWith("%) | +10.00 points (+8.04 to +11.96) |", wins, StringComparison.Ordinal);
        Assert.Contains("| Rounds (mean) | 6.20 | 5.70 | −0.50 (−0.54 to −0.46) |", text, StringComparison.Ordinal);
        Assert.DoesNotContain("left dying", text, StringComparison.Ordinal); // nobody was, in either run
    }

    [Fact]
    public void Format_CompareWithMembersLeftDying_PutsThemBesideTheDeathsWithWhatThatMeans()
    {
        // A feature that ends fights sooner leaves the fallen fewer rounds to fail their death saves: fewer deaths and more
        // left dying. With only "a party member dies" the table read as lives saved (seen: 16.73% → 14.28% beside a
        // headline of 40.88% left dying), so the dying get their own paired row under the deaths, and a line on reading them.
        var text = SimulationMarkdown.Format(Report(700, 1000) with { Compare = Compare(dyingWithout: 409, dyingWith: 441) }, seedGiven: true, notes: []);

        var lines = text.Split('\n');
        var deaths = Array.FindIndex(lines, l => l.StartsWith("| A party member dies | ", StringComparison.Ordinal));
        Assert.StartsWith("| A party member is left dying | 40.9% (", lines[deaths + 1], StringComparison.Ordinal);
        Assert.Contains("%) | 44.1% (", lines[deaths + 1], StringComparison.Ordinal);
        Assert.EndsWith("%) | +3.20 points (+2.10 to +4.30) |", lines[deaths + 1], StringComparison.Ordinal);
        Assert.Contains("\n\n*Read the deaths with the dying: the dying's death saves are not rolled, and a feature that ends fights sooner " +
                        "leaves them fewer rounds to fail, so fewer deaths beside more left dying is not lives saved.*\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_PerCombatantKills_SayADeathFromFailedDeathSavesIsCreditedToNoOne()
    {
        // A party member who bleeds out after a monster dropped it dies with no killer: the table shows far fewer kills
        // than deaths (16.73% of fights with a party member dead beside 0.04 kills a fight), and the note must say why.
        // A kill that deals no damage still counts: Power Word Kill, a Slaying Bow's failed save, a sixth level of exhaustion.
        var text = SimulationMarkdown.Format(Report(5, 10), seedGiven: true, notes: []);

        Assert.Contains("Kills count the other side's deaths it caused (by damage, an outright kill such as Power Word Kill, or a sixth " +
                        "level of exhaustion); a death from failed death saves (or from regeneration stopped at 0 HP) is credited to no " +
                        "one.\n\n| Combatant |", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_PrecisionStoppedAtTheWorkLimit_SaysSoAndWhatAllowsMore()
    {
        // 20 combatants to round cap 100 with a comparison fill the work budget at 10,000 fights: precision stops there. "Not
        // reached in 10,000 fights" alone reads as a fight limit, and says nothing of the round cap that would buy more.
        var report = Report(5_000, 10_000) with
        {
            Precision = 0.001,
            PrecisionReached = false,
            PrecisionMaxFights = 10_000,
            RoundCap = 100,
            Combatants = [.. Report(5_000, 10_000).Combatants.Select(c => c with { Count = 10 })],
            Compare = Compare(dyingWithout: 0, dyingWith: 0),
        };

        var text = SimulationMarkdown.Format(report, seedGiven: true, notes: []);

        Assert.Contains(
            " · precision ±0.1% not reached in 10,000 fights (±0.98%), the most the work limit allows for 20 combatants to round " +
            "cap 100 with compare; a lower round cap, fewer combatants or no compare allow more · seed 42*", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_PrecisionNotReachedIn100000Fights_BlamesNoWorkLimit()
    {
        var report = Report(50_000, 100_000) with { Precision = 0.001, PrecisionReached = false, PrecisionMaxFights = 100_000 };

        var text = SimulationMarkdown.Format(report, seedGiven: true, notes: []);

        Assert.Contains(" · precision ±0.1% not reached in 100,000 fights (±0.31%) · seed 42*", text, StringComparison.Ordinal);
        Assert.DoesNotContain("work limit", text, StringComparison.Ordinal);
    }

    /// <summary>A comparison of "Plus Two" on party entry 1 over 1,000 fights, with the given counts left dying.</summary>
    private static CompareReport Compare(long dyingWithout, long dyingWith) => new()
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
        BaselineAnyDying = SimulationStatistics.Wilson(dyingWithout, 1000),
        VariantAnyDying = SimulationStatistics.Wilson(dyingWith, 1000),
        AnyDyingDifference = dyingWithout == dyingWith
            ? new MeanEstimate(0, 0, 0, 0, 1000)
            : new MeanEstimate((dyingWith - dyingWithout) / 1000.0, 0.0056, 0.021, 0.043, 1000),
    };

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
            DroppedToZero = new CreatureShare(0, fights),
            DeadAtEnd = new CreatureShare(0, fights),
            DyingAtEnd = new CreatureShare(0, fights),
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
            AnyPartyDying = SimulationStatistics.Wilson(0, fights),
            Rounds = new RoundsSummary(new MeanEstimate(3, 0.01, 2.98, 3.02, fights), 3, 4, [0, 0, fights]),
            Combatants = [Entry("Fighter", "party"), Entry("Ogre", "enemies")],
            Warnings = [],
            Assumptions = ["No grid."],
        };
    }
}
