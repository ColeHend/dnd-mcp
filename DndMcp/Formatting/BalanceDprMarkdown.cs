using DndMcp.Domain.Dpr;
using DndMcp.Formatting.Srd;
using static DndMcp.Formatting.BalanceMarkdownText;

namespace DndMcp.Formatting;

/// <summary>
/// Renders <c>balance_dpr</c>: the headline with its horizon and target, all three horizons side by side, the level curve
/// and AC grid when asked for, then the detail level in full (the build as read, the target, the breakdown, the round-1
/// spread, power attacks, save effects, the day's arithmetic), the assumptions, notes and sources.
///
/// <para>
/// <b>The headline never stands alone.</b> It names the level, the horizon and the target it was computed against, and
/// sits beside the two community reference curves for that level (RPGBOT's target, the warlock baseline), because "19.61
/// DPR" means nothing to a reader who cannot tell whether that is a lot at level 5. The curve table repeats both per
/// level for the same reason.
/// </para>
/// <para>
/// <b>Only the detail level is shown in full</b> (the build's own level when evaluated, else the lowest): a 20-level curve
/// with twenty breakdowns would bury the curve, and the table already gives every level's figure, round-1 figure, target
/// AC and what changed. The detail level's heading says which it is.
/// </para>
/// </summary>
internal static class BalanceDprMarkdown
{
    public static string Format(DprReport report)
    {
        var detail = report.Detail;
        var evaluation = detail.Result.Evaluation;
        var blocks = new List<string?>
        {
            $"# Damage per round: {evaluation.BuildName}",
            Headline(report),
            Horizons(report),
            Levels(report),
            Grid(report),
            BuildAsRead(detail.Build, $"## The build as read: level {Number(detail.Level)}, {detail.Build.Edition} rules"),
            Target(detail.Target, $"## Target at level {Number(detail.Level)}"),
            Breakdown(evaluation, $"## Where the damage comes from: level {Number(detail.Level)}, {PerRound(evaluation)}"),
            Distribution(evaluation.Round1Distribution, detail.Target),
            PowerAttacks(evaluation),
            SaveEffects(evaluation),
            detail.Result.Day is { } day ? Day(day, $"## The adventuring day at level {Number(detail.Level)}") : null,
            Assumptions(report),
            Notes(report.Notes.Concat(evaluation.Notes)),
            Sources(report.Levels.Select(l => l.Target), [detail.Build], references: true),
        };

        return SrdMarkdownText.Blocks(blocks) + "\n";
    }

    // "**19.61** damage per round at level 5 against AC 15 — round 1 (the nova). For scale at level 5: RPGBOT's target …"
    private static string Headline(DprReport report)
    {
        var detail = report.Detail;
        var reference = detail.Reference;
        return
            $"**{Dpr(detail.DamagePerRound)}** damage per round at level {Number(detail.Level)} against {TargetShort(detail.Target, detail.Build)} — " +
            $"{report.Horizon.Label}. For scale at level {Number(detail.Level)}: RPGBOT's target " +
            $"{Dpr(reference.RpgbotTarget)}, the warlock baseline {Dpr(reference.WarlockBaseline)}.";
    }

    // All three horizons at the detail level, the headline's marked: a feature that shines in round 1 and fades over a
    // day (or the reverse) shows whatever the call asked for.
    private static string Horizons(DprReport report)
    {
        var summary = report.Summary;
        return SrdMarkdownText.Table(
            ["Horizon", $"Damage per round at level {Number(report.DetailLevel)}"],
            HorizonLabels(report.Horizon).Select(h => (IReadOnlyList<string>)
            [
                h.Horizon == report.Horizon.Horizon ? $"**{h.Label}** (headline)" : h.Label,
                Dpr(summary.For(h.Horizon)),
            ]));
    }

    // The level curve with the reference curves beside it and what changed at each level.
    private static string? Levels(DprReport report)
    {
        if (report.Levels.Count < 2)
        {
            return null;
        }

        var round1 = report.Horizon.Horizon != DprHorizons.Round1;
        var marks = report.Marks.GroupBy(m => m.Level).ToDictionary(g => g.Key, g => string.Join("; ", g.Select(m => m.Text)));
        var headers = new List<string> { "Level", "Damage per round" };
        if (round1)
        {
            headers.Add("Round 1");
        }

        headers.AddRange(["Target AC", "RPGBOT target", "Warlock baseline", "What changed"]);
        var rows = report.Levels.Select(level =>
        {
            var cells = new List<string>
            {
                level.Level == report.DetailLevel ? $"**{Number(level.Level)}**" : Number(level.Level),
                Dpr(level.DamagePerRound),
            };
            if (round1)
            {
                cells.Add(Dpr(level.Result.Round1Damage));
            }

            cells.AddRange(
            [
                Number(level.Target.ArmorClass),
                Dpr(level.Reference.RpgbotTarget),
                Dpr(level.Reference.WarlockBaseline),
                marks.GetValueOrDefault(level.Level, string.Empty),
            ]);
            return (IReadOnlyList<string>)cells;
        });

        return $"## Damage per round by level, {report.Horizon.Label}\n\n" + SrdMarkdownText.Table(headers, rows);
    }

    // The level × AC grid. A cell whose power attack was not always on says so: that is where −5/+10 stops paying.
    private static string? Grid(DprReport report)
    {
        if (report.Grid is not { } grid)
        {
            return null;
        }

        var headers = new List<string> { "Level" };
        headers.AddRange(grid.ArmorClasses.Select(ac => $"AC {Number(ac)}"));
        var rows = grid.Rows.Select(row =>
        {
            var cells = new List<string> { Number(row.Level) };
            cells.AddRange(row.Cells.Select(Cell));
            return (IReadOnlyList<string>)cells;
        });

        var text = $"## Damage per round by target AC, {report.Horizon.Label}\n\n" + SrdMarkdownText.Table(headers, rows);
        if (grid.Rows.Any(r => r.Cells.Any(c => c.PowerAttackOnChance.Count > 0)))
        {
            text += "\n\n\"PA off\": the power attack is never switched on against that AC; \"PA n%\": on in that share of rounds.";
        }

        return text;
    }

    private static string Cell(AcGridCell cell)
    {
        var dpr = Dpr(cell.DamagePerRound);
        if (cell.PowerAttackOnChance.Count == 0 || cell.PowerAttackOnChance.All(p => p >= 1 - 1e-12))
        {
            return dpr;
        }

        return cell.PowerAttackOnChance.All(p => p <= 1e-12)
            ? $"{dpr} (PA off)"
            : $"{dpr} (PA {string.Join("/", cell.PowerAttackOnChance.Select(WholePercent))})";
    }

    private static string Assumptions(DprReport report)
    {
        var lines = new List<string>
        {
            HorizonAssumption(report.Horizon),
            Rulings(report.Levels.SelectMany(l => l.Result.Evaluation.Rulings)),
        };

        if (Policies(report.Detail.Build) is { } policies)
        {
            lines.Add(policies);
        }

        return "**Assumptions:**\n" + Bullets(lines);
    }
}
