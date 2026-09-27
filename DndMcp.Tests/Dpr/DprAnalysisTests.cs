using DndMcp.Domain.Core;
using DndMcp.Domain.Dice;
using DndMcp.Domain.Dpr;
using DndMcp.Domain.Features;
using Xunit;
using static DndMcp.Tests.Dpr.DprTestKit;

namespace DndMcp.Tests.Dpr;

/// <summary>
/// Invariant: <c>balance_dpr</c>'s analysis evaluates each level exactly as a direct engine run of the build resolved at
/// that level against the target at that level; marks every Extra Attack and ASI jump from the resolved builds; fills an
/// AC grid whose cells are the same evaluations with only the AC replaced (the research's power-attack column and toggle
/// come out of it); warns when a build that does not scale is read as a curve; refuses every malformed level list and AC
/// range with a message naming it; and keeps a busy build's full 20 × 16 grid inside one call's budget and a few seconds.
/// </summary>
public sealed class DprAnalysisTests
{
    private const double Exact = 1e-9;

    /// <summary>A greatsword fighter that gains Extra Attack at 5 and 11 and Str at 4 and 8.</summary>
    private const string Scaler = """
        { "name": "Scaler", "edition": "2024", "level": 5, "abilities": {"str": {"1": 16, "4": 18, "8": 20}},
          "attacks": [{ "name": "Greatsword", "count": {"1": 1, "5": 2, "11": 3}, "damage": "2d6", "damage_type": "slashing",
                        "properties": ["melee", "heavy", "two-handed"] }] }
        """;

    private static DprReport Analyze(string build, string? target = null, IReadOnlyList<int>? levels = null, IReadOnlyList<int>? acRange = null, string? horizon = null, string? preset = null, double? encounters = null, int? shortRests = null) =>
        DprAnalysis.Analyze(new DprRequest
        {
            Build = Build(build),
            Target = Target(target),
            Levels = levels,
            AcRange = acRange,
            Horizon = horizon,
            RestPreset = preset,
            EncountersPerDay = encounters,
            ShortRests = shortRests,
        });

    [Fact]
    public void Analyze_OneLevel_IsTheBuildsOwnLevelInFull()
    {
        var report = Analyze(GoldenBuilds.Fighter2014(GoldenBuilds.Gwm2014), """{ "ac": 15 }""");

        var detail = Assert.Single(report.Levels);
        Assert.Equal(5, report.DetailLevel);
        Assert.Same(detail, report.Detail);
        Assert.Equal(156893.0 / 8000, detail.DamagePerRound, Exact); // §8.2 over the default 3-round fight
        Assert.NotNull(detail.Result.Evaluation.Round1Distribution);
        Assert.Equal(DprHorizons.Fight, report.Horizon.Horizon);
        Assert.Null(report.Grid);
        Assert.Empty(report.Marks);
        Assert.Empty(report.Notes);
        Assert.Equal(1, report.Runs);
        Assert.Equal(5, detail.Reference.Level);
        Assert.Equal(145.0 / 12, detail.Reference.RpgbotTarget, Exact);
        Assert.Equal(17.8, detail.Reference.WarlockBaseline, Exact);
    }

    [Fact]
    public void Analyze_Curve_EachLevelIsTheDirectEvaluationAgainstTheCrLevelRow()
    {
        var report = Analyze(Scaler, levels: Enumerable.Range(1, 20).ToList());

        Assert.Equal(Enumerable.Range(1, 20), report.Levels.Select(l => l.Level));
        var spec = Build(Scaler);
        foreach (var level in report.Levels)
        {
            var build = BuildResolver.Resolve(spec, level.Level);
            var target = TargetResolver.Resolve(null, level.Level);
            var direct = DprEngine.Evaluate(build, target, DprOptions.Fight(), WorkMeter.Unlimited);
            Assert.Equal(direct.DamagePerRound, level.DamagePerRound, Exact);
            Assert.Equal(ReferenceCurves.Row(level.Level).ArmorClass, level.Target.ArmorClass);
            Assert.Equal(level.Level == 5, level.Result.Evaluation.Round1Distribution is not null); // only the detail level
        }

        // By hand: level 1, +5 vs AC 13 (hit on 8+): 0.60 × (7 + 3) + 0.05 × (14 + 3) = 6.85. Level 5, +7 vs AC 15 (8+),
        // two swings: 2 × (0.60 × 11 + 0.05 × 18) = 15.
        Assert.Equal(6.85, report.Levels[0].DamagePerRound, Exact);
        Assert.Equal(15.0, report.Levels[4].DamagePerRound, Exact);
        Assert.Empty(report.Notes);
    }

    [Fact]
    public void Analyze_Curve_MarksExtraAttackAndAsiJumps()
    {
        var report = Analyze(Scaler, levels: Enumerable.Range(1, 20).ToList());

        Assert.Equal(
            [
                new LevelMark(4, LevelMarkKinds.Asi, "ASI: Str 16 → 18"),
                new LevelMark(5, LevelMarkKinds.ExtraAttack, "Extra Attack: Greatsword 1 → 2 attacks"),
                new LevelMark(8, LevelMarkKinds.Asi, "ASI: Str 18 → 20"),
                new LevelMark(11, LevelMarkKinds.ExtraAttack, "Extra Attack: Greatsword 2 → 3 attacks"),
            ],
            report.Marks);
    }

    [Fact]
    public void Analyze_SparseLevels_MarkWhatRoseSinceThePreviousLevelEvaluated()
    {
        var report = Analyze(Scaler, levels: [11, 1, 11]);

        Assert.Equal([1, 11], report.Levels.Select(l => l.Level)); // distinct and ascending
        Assert.Equal(1, report.DetailLevel); // the build's own level 5 is not among them: the lowest is shown in full
        Assert.Equal(
            [
                new LevelMark(11, LevelMarkKinds.ExtraAttack, "Extra Attack: Greatsword 1 → 3 attacks"),
                new LevelMark(11, LevelMarkKinds.Asi, "ASI: Str 16 → 20"),
            ],
            report.Marks);
    }

    [Fact]
    public void Analyze_WarlockPresetCurve_IsThePublishedCurveWithItsBeamsAndAsisMarked()
    {
        var report = Analyze("""{ "name": "Warlock", "preset": "warlock_baseline", "level": 5 }""", levels: Enumerable.Range(1, 20).ToList());

        double[] published = [6.30, 8.25, 8.25, 8.90, 17.80, 17.80, 17.80, 19.10, 20.50, 19.10, 28.65, 28.65, 28.65, 28.65, 28.65, 28.65, 38.20, 38.20, 38.20, 38.20];
        Assert.Equal(published, report.Levels.Select(l => Math.Round(l.DamagePerRound, 9)));
        Assert.Equal(
            ["4 asi ASI: Cha 16 → 18", "5 extra_attack Extra Attack: Eldritch Blast 1 → 2 attacks", "8 asi ASI: Cha 18 → 20",
             "11 extra_attack Extra Attack: Eldritch Blast 2 → 3 attacks", "17 extra_attack Extra Attack: Eldritch Blast 3 → 4 attacks"],
            report.Marks.Select(m => $"{m.Level} {m.Kind} {m.Text}"));
        Assert.Empty(report.Notes);
    }

    [Fact]
    public void Analyze_FixedTarget_UsesItAtEveryLevel()
    {
        var report = Analyze(Scaler, """{ "ac": 15 }""", levels: [1, 10, 20]);

        Assert.All(report.Levels, l => Assert.Equal(15, l.Target.ArmorClass));
        Assert.Equal(18.7, report.Levels[1].DamagePerRound, Exact); // level 10: +9 vs 15, 2 × (0.70 × 12 + 0.05 × 19)
    }

    [Fact]
    public void Analyze_BuildThatDoesNotScaleAtOtherLevels_WarnsThatOnlyProficiencyScales()
    {
        var report = Analyze(GoldenBuilds.Fighter2014(GoldenBuilds.Gwm2014), levels: [1, 5, 20]);

        var note = Assert.Single(report.Notes);
        Assert.StartsWith(
            "2014 L5 Fighter does not change with level (no step values, from_level/until_level or cantrip scaling), so across these " +
            "levels only its proficiency bonus changes, and a real character gains attacks, ability scores and features: this curve understates one.",
            note,
            StringComparison.Ordinal);
        Assert.Contains("\"count\": {\"1\": 1, \"5\": 2}", note, StringComparison.Ordinal);
        Assert.Empty(Analyze(GoldenBuilds.Fighter2014(GoldenBuilds.Gwm2014), levels: [5]).Notes);
    }

    [Fact]
    public void Analyze_FixedProficiencyBonusAtOtherLevels_SaysNothingChanges()
    {
        const string build = """
            { "name": "Frozen", "level": 5, "proficiency_bonus": 3, "abilities": {"str": 18},
              "attacks": [{ "name": "Longsword", "damage": "1d8", "damage_type": "slashing" }] }
            """;

        var note = Assert.Single(Analyze(build, levels: [1, 20]).Notes);

        Assert.Contains("so across these levels nothing about it changes (its proficiency_bonus is fixed),", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_LevelOutOfRange_IsTheResolversMessage()
    {
        var error = Assert.Throws<DndInputException>(() => Analyze(Scaler, levels: [5, 25]));

        Assert.Equal("levels: 25 is not a character level; levels are 1 to 20.", error.Message);
    }

    [Fact]
    public void Analyze_AcGrid_CellsAreTheResearchColumnAndThePowerAttackSwitchesOffAt17()
    {
        // §8.2's "2014 GWF, PA + crit BA" column at AC 13–19 (no resources, so the fight is round 1 every round).
        var report = Analyze(GoldenBuilds.Fighter2014(GoldenBuilds.Gwm2014), """{ "ac": 15 }""", acRange: [13, 19]);

        var grid = report.Grid!;
        Assert.Equal([13, 14, 15, 16, 17, 18, 19], grid.ArmorClasses);
        var row = Assert.Single(grid.Rows);
        Assert.Equal(5, row.Level);
        Assert.Equal([24.30, 21.95, 19.61, 17.27, 15.10, 13.81, 12.52], row.Cells.Select(c => Math.Round(c.DamagePerRound, 2)));
        Assert.Equal(156893.0 / 8000, row.Cells[2].DamagePerRound, Exact);
        Assert.Equal([1.0, 1, 1, 1, 0, 0, 0], row.Cells.Select(c => Assert.Single(c.PowerAttackOnChance)));
        Assert.Equal(1 + 7, report.Runs);
    }

    [Fact]
    public void Analyze_AcGrid_CellIsTheLevelsTargetWithOnlyTheAcReplaced()
    {
        // Half cover (+2) stays on every cell: AC 13 behind half cover is AC 15 in the open.
        var covered = Analyze(Scaler, """{ "ac": 20, "cover": "half" }""", levels: [5, 6], acRange: [13, 14]);
        var open = Analyze(Scaler, """{ "ac": 20 }""", levels: [5, 6], acRange: [15, 16]);

        for (var r = 0; r < 2; r++)
        {
            for (var c = 0; c < 2; c++)
            {
                Assert.Equal(open.Grid!.Rows[r].Cells[c].DamagePerRound, covered.Grid!.Rows[r].Cells[c].DamagePerRound, Exact);
            }
        }

        Assert.Empty(covered.Grid!.Rows[0].Cells[0].PowerAttackOnChance);
    }

    [Theory]
    [InlineData(new[] { 15 }, "ac_range has 1 value; give two, [low, high], e.g. [13, 19] (at most 16 ACs).")]
    [InlineData(new[] { 13, 15, 17 }, "ac_range has 3 values; give two, [low, high], e.g. [13, 19] (at most 16 ACs).")]
    [InlineData(new[] { 0, 10 }, "ac_range: AC 0 is outside 1 to 40; give [low, high], e.g. [13, 19] (at most 16 ACs).")]
    [InlineData(new[] { 30, 41 }, "ac_range: AC 41 is outside 1 to 40; give [low, high], e.g. [13, 19] (at most 16 ACs).")]
    [InlineData(new[] { 19, 13 }, "ac_range [19, 13] runs backwards; give [low, high], e.g. [13, 19].")]
    [InlineData(new[] { 5, 30 }, "ac_range [5, 30] spans 26 ACs; at most 16 (high − low ≤ 15), e.g. [5, 20].")]
    public void Analyze_BadAcRange_SaysWhatIsAccepted(int[] range, string message)
    {
        var error = Assert.Throws<DndInputException>(() => Analyze(Scaler, acRange: range));

        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void Analyze_EmptyLists_AreTheDefaults()
    {
        var report = Analyze(Scaler, levels: [], acRange: []);

        Assert.Equal([5], report.Levels.Select(l => l.Level));
        Assert.Null(report.Grid);
    }

    [Fact]
    public void Analyze_WidestGrid_IsExactlyTheCellCap()
    {
        var report = Analyze(Scaler, """{ "ac": 15 }""", levels: Enumerable.Range(1, 20).ToList(), acRange: [10, 25]);

        Assert.Equal(DprLimits.MaxGridCells, report.Grid!.Rows.Sum(r => r.Cells.Count));
        Assert.Equal(20 + 320, report.Runs);
    }

    [Fact]
    public void Analyze_DayHorizon_EachLevelAndCellIsTheDayFigure()
    {
        // §8.12 at AC 15 (custom E 6, S 2): 156893/8000 + 3 × 62472773/3200000 ÷ 18 = 439015973/19200000.
        var report = Analyze(GoldenBuilds.Fighter2014(GoldenBuilds.Gwm2014 + ", " + GoldenBuilds.ActionSurge), """{ "ac": 15 }""",
            acRange: [15, 16], horizon: "day", encounters: 6, shortRests: 2);

        Assert.Equal("custom: 6 encounters, 2 short rests", report.Horizon.Day!.Label);
        Assert.Equal(439015973.0 / 19200000, report.Detail.DamagePerRound, Exact);
        Assert.Equal(439015973.0 / 19200000, report.Grid!.Rows[0].Cells[0].DamagePerRound, Exact);
        Assert.Equal(3 + (2 * 2), report.Runs); // full fight, base, Action Surge; then base and Action Surge per cell
    }

    [Theory]
    [InlineData("round1", 3 + 1)] // one more run for the fight
    [InlineData("fight", 1 + 2)] // the fight, then the day's base and Action Surge
    [InlineData("day", 3)] // the day's full fight, base and Action Surge
    public void Analyze_Summary_GivesAllThreeHorizonsWhateverTheHeadline(string horizon, int runs)
    {
        // §8.2 + Action Surge at AC 15, day E 6, S 2. Round 1 has Action Surge's two extra swings (four swings and the crit
        // bonus attack over four chances); the day is §8.12's 439015973/19200000.
        var build = GoldenBuilds.Fighter2014(GoldenBuilds.Gwm2014 + ", " + GoldenBuilds.ActionSurge);
        var (resolved, target) = Resolve(build, Ac15Json);
        var round1 = DprEngine.Evaluate(resolved, target, DprOptions.Round1, WorkMeter.Unlimited).DamagePerRound;
        var fight = DprEngine.Evaluate(resolved, target, DprOptions.Fight(), WorkMeter.Unlimited).DamagePerRound;

        var report = Analyze(build, Ac15Json, horizon: horizon, encounters: 6, shortRests: 2);

        var summary = report.Summary;
        Assert.Equal(round1, summary.Round1Damage, Exact);
        Assert.Equal(fight, summary.FightDamagePerRound, Exact);
        Assert.Equal(439015973.0 / 19200000, summary.DayDamagePerRound, Exact);
        Assert.Equal((3, "custom: 6 encounters, 2 short rests"), (summary.Rounds, summary.Day.Label));
        Assert.Equal(report.Detail.DamagePerRound, summary.For(horizon), Exact);
        Assert.Equal(runs, report.Runs);
        Assert.True(round1 > fight && fight > summary.DayDamagePerRound, "a nova feature fades over the fight and the day");
    }

    [Fact]
    public void Analyze_SummaryWithoutLimitedFeatures_CostsNoExtraRun()
    {
        var report = Analyze(GoldenBuilds.Fighter2014(GoldenBuilds.Gwm2014), Ac15Json);

        Assert.Equal(1, report.Runs);
        Assert.Equal(156893.0 / 8000, report.Summary.DayDamagePerRound, Exact);
        Assert.Equal(156893.0 / 8000, report.Summary.Round1Damage, Exact);
    }

    private const string Ac15Json = """{ "ac": 15 }""";

    [Fact]
    public void Analyze_InvalidTarget_IsRefusedBeforeAnyWork()
    {
        var error = Assert.Throws<DndInputException>(() => Analyze(Scaler, """{ "ac": 50 }"""));

        Assert.StartsWith("Invalid target:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_BudgetExceededOnAGrid_NamesTheGrid()
    {
        var request = new DprRequest
        {
            Build = Build(Scaler),
            Levels = Enumerable.Range(1, 20).ToList(),
            AcRange = [10, 25],
            WorkBudget = 50,
        };

        var error = Assert.Throws<DndInputException>(() => DprAnalysis.Analyze(request));

        Assert.StartsWith(
            "this request is too large to compute exactly: 20 levels and a 20 × 16 AC grid on the fight horizon needs more work " +
            "than one call may do (200,000,000 units). Evaluate fewer levels or a narrower ac_range,",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_BudgetExceededOnOneRun_IsTheBuildMessage()
    {
        var error = Assert.Throws<DndInputException>(() => DprAnalysis.Analyze(new DprRequest { Build = Build(Scaler), WorkBudget = 50 }));

        Assert.StartsWith("this build is too large to compute exactly:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_CancelledToken_Stops()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            DprAnalysis.Analyze(new DprRequest { Build = Build(Scaler), Levels = [1, 2, 3] }, cancellation.Token));
    }

    /// <summary>
    /// A busy 2024 paladin-fighter, levels 1–20: Extra Attack and ASIs by step value, GWF, Graze, GWM's +PB and Hew,
    /// a limited Divine Smite that costs the Bonus Action (so it competes with Hew), Radiant Strikes, Savage Attacker,
    /// Bless and a 30% Help.
    /// </summary>
    private const string Busy = """
        { "name": "Busy paladin", "edition": "2024", "level": 20, "abilities": {"str": {"1": 16, "4": 18, "8": 20}},
          "fighting_style": "gwf",
          "attacks": [{ "name": "Greatsword", "count": {"1": 1, "5": 2}, "damage": "2d6", "damage_type": "slashing",
                        "properties": ["melee", "heavy", "two-handed"], "mastery": "graze" }],
          "modifiers": [
            { "kind": "bonus_damage", "name": "Great Weapon Master", "amount": "pb", "attack_action_only": true, "from_level": 4 },
            { "kind": "extra_attack", "name": "Hew", "attack": "Greatsword", "action": "bonus_action", "trigger": "crit", "from_level": 4 },
            { "kind": "extra_damage", "name": "Divine Smite", "dice": {"2": "2d8", "5": "3d8", "9": "4d8"}, "type": "radiant",
              "when": "first_hit_per_turn", "policy": "crit_or_last", "action_cost": "bonus_action",
              "resource": { "uses": {"2": 2, "5": 4, "9": 6}, "per": "long_rest" }, "from_level": 2 },
            { "kind": "extra_damage", "name": "Radiant Strikes", "dice": "1d8", "type": "radiant", "from_level": 11 },
            { "kind": "reroll_damage_take_best", "name": "Savage Attacker" },
            { "kind": "to_hit", "name": "Bless", "dice": "1d4", "concentration": true },
            { "kind": "advantage", "name": "Help", "rate": 0.3 } ] }
        """;

    [Theory]
    [InlineData("fight")]
    [InlineData("day")]
    public void Analyze_BusyBuildFullGrid_StaysInsideTheBudgetAndAFewSeconds(string horizon)
    {
        // Contract §4.8: a 20-level × 16-AC grid under 5 s. One meter covers the whole call, so finishing at all is the
        // budget check; the day horizon runs the fight twice per cell (base and the smite), and is allowed twice as long.
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var report = Analyze(Busy, levels: Enumerable.Range(1, 20).ToList(), acRange: [10, 25], horizon: horizon);

        clock.Stop();
        Assert.Equal(320, report.Grid!.Rows.Sum(r => r.Cells.Count));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(horizon == "day" ? 10 : 5), $"the {horizon} grid took {clock.Elapsed}");
        Assert.All(report.Grid.Rows, row => Assert.True(row.Cells[0].DamagePerRound >= row.Cells[^1].DamagePerRound, $"level {row.Level}"));
    }
}
