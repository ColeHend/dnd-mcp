using System.Globalization;
using DndMcp.Domain.Dpr;
using DndMcp.Formatting.Srd;
using static DndMcp.Formatting.BalanceMarkdownText;

namespace DndMcp.Formatting;

/// <summary>
/// Renders <c>balance_compare</c>: the delta in the headline with its level-equivalent and band, the per-level table, the
/// slopes the level-equivalents divide by (and where each came from), all three horizons for both builds, the action
/// economy collisions, the signature effects, then each build at the detail level as <c>balance_dpr</c> shows one.
///
/// <para>
/// <b>The band never stands alone.</b> It is printed with the level-equivalent it was read from and the band's bounds
/// ("a level-equivalent of 0.64: Over (0.5 ≤ LE &lt; 1.0)") and the whole scale, and the slope behind the level-equivalent
/// is shown with its source, because
/// "Over" from a baseline slope and "Over" from RPGBOT's reference are different claims, and a model asked "why Over?"
/// must be able to answer from the result.
/// </para>
/// <para>
/// <b>Said once, where it applies.</b> The Domain's call-level notes repeat two things this renders in place: the slope
/// fallback's reason (on the slope's line) and the Bonus Action or Reaction opportunity cost (in the action-economy
/// section). Those notes are dropped from the notes list rather than printed twice.
/// </para>
/// </summary>
internal static class BalanceCompareMarkdown
{
    public static string Format(ComparisonReport report)
    {
        var detail = report.Detail;
        var level = Number(detail.Level);
        var baseline = detail.Baseline.Evaluation;
        var variant = detail.Variant.Evaluation;
        var inPlace = new List<string>();

        var blocks = new List<string?>
        {
            $"# Build comparison: {report.VariantName} vs {report.BaselineName}",
            Headline(report),
            Levels(report),
            Slopes(report, inPlace),
            Horizons(report),
            ActionEconomy(report, inPlace),
            SignatureEffects(report),
            Target(detail.Baseline.Evaluation.Target, $"## Target at level {level}"),
            Build("Baseline", detail.Baseline, level),
            Build("Variant", detail.Variant, level),
            Assumptions(report),
            Notes(report.Notes
                .Concat(baseline.Notes)
                .Concat(variant.Notes)
                .Where(n => !inPlace.Any(p => n.Contains(p, StringComparison.Ordinal)))),
            Sources(
                report.Levels.Select(l => l.Baseline.Evaluation.Target),
                [baseline.Build, variant.Build],
                references: report.Slopes.Any(s => s.Source == SlopeSources.RpgbotReference)),
        };

        return SrdMarkdownText.Blocks(blocks) + "\n";
    }

    // "Adding Savage Attacker to Fighter: **+0.80** damage per round (+3.3%; 24.04 → 24.83) at level 5 against AC 15 — round 1
    // (the nova). That is a level-equivalent of **0.64**: **Over** (0.5 ≤ LE < 1.0). The bands …"
    private static string Headline(ComparisonReport report)
    {
        var detail = report.Detail;
        var le = detail.LevelEquivalent;
        var what = report.FeatureName is { } feature
            ? $"Adding {feature} to {report.BaselineName}"
            : $"{report.VariantName} instead of {report.BaselineName}";
        return
            $"{what}: **{SignedDpr(detail.Delta)}** damage per round ({SignedPercent(detail.RelativeDelta)}; " +
            $"{Dpr(detail.Baseline.DamagePerRound)} → {Dpr(detail.Variant.DamagePerRound)}) at level {Number(detail.Level)} against " +
            $"{TargetShort(detail.Baseline.Evaluation.Target, detail.Baseline.Evaluation.Build)} — {report.Horizon.Label}. That is a level-equivalent of " +
            $"**{LevelEquivalentText(le)}**: **{BalanceBands.Display(le.Band)}** ({BandRule(le.Band)}). {BandScale}";
    }

    /// <summary>The whole scale, so every band printed can be placed against its neighbours.</summary>
    private const string BandScale =
        "The bands (the homebrew-balance scale): Under ≤ −0.25 < On budget < 0.25 ≤ Creeping < 0.5 ≤ Over < 1.0 ≤ Breaking.";

    private static string BandRule(string band) => band switch
    {
        BalanceBands.Under => "LE ≤ −0.25",
        BalanceBands.OnBudget => "−0.25 < LE < 0.25",
        BalanceBands.Creeping => "0.25 ≤ LE < 0.5",
        BalanceBands.Over => "0.5 ≤ LE < 1.0",
        _ => "LE ≥ 1.0",
    };

    // One row per level compared; with one level this table repeats the headline, so it is left out.
    private static string? Levels(ComparisonReport report)
    {
        if (report.Levels.Count < 2)
        {
            return null;
        }

        return $"## By level, {report.Horizon.Label}\n\n" + SrdMarkdownText.Table(
            ["Level", "Baseline", "Variant", "Δ", "Δ %", "Level-equivalent", "Band"],
            report.Levels.Select(l => (IReadOnlyList<string>)
            [
                l.Level == report.DetailLevel ? $"**{Number(l.Level)}**" : Number(l.Level),
                Dpr(l.Baseline.DamagePerRound),
                Dpr(l.Variant.DamagePerRound),
                SignedDpr(l.Delta),
                SignedPercent(l.RelativeDelta),
                LevelEquivalentText(l.LevelEquivalent),
                BalanceBands.Display(l.LevelEquivalent.Band),
            ]));
    }

    // Each tier's slope with its source; a fallback's reason is said here, and its note dropped (see the type's remarks).
    private static string Slopes(ComparisonReport report, List<string> inPlace)
    {
        var lines = report.Slopes.Select(slope =>
        {
            var source = slope.Source == SlopeSources.Baseline
                ? "the baseline's own curve"
                : "RPGBOT's reference curve (the CR = level row's maximum HP ÷ 12)";
            var text =
                $"Tier {Number(slope.Tier)} (levels {Number(slope.FirstLevel)}–{Number(slope.LastLevel)}): " +
                $"{slope.PerLevel.ToString("0.###", CultureInfo.InvariantCulture)} damage per round per level, from {source}: " +
                $"level {Number(slope.FromLevel)} {Dpr(slope.FromDamage)} → level {Number(slope.ToLevel)} {Dpr(slope.ToDamage)}";
            if (slope.FallbackReason is { } reason)
            {
                inPlace.Add(reason);
                text += $", because {reason}";
            }

            return text + ".";
        });

        return "**Level-equivalent** = Δ ÷ the damage per round one level adds in that tier (the same horizon and target):\n" + Bullets(lines);
    }

    private static string Horizons(ComparisonReport report)
    {
        var baseline = report.BaselineSummary;
        var variant = report.VariantSummary;
        return $"## All three horizons at level {Number(report.DetailLevel)}\n\n" + SrdMarkdownText.Table(
            ["Horizon", "Baseline", "Variant", "Δ"],
            HorizonLabels(report.Horizon).Select(h => (IReadOnlyList<string>)
            [
                h.Horizon == report.Horizon.Horizon ? $"**{h.Label}** (headline)" : h.Label,
                Dpr(baseline.For(h.Horizon)),
                Dpr(variant.For(h.Horizon)),
                SignedDpr(variant.For(h.Horizon) - baseline.For(h.Horizon)),
            ]));
    }

    // What spends the Bonus Action and the Reaction in each build; a new consumer beside an old one is an opportunity cost.
    private static string ActionEconomy(ComparisonReport report, List<string> inPlace)
    {
        var lines = new List<string>();
        foreach (var slot in new[] { report.BonusAction, report.Reaction })
        {
            var name = ActionSlots.Display(slot.Slot);
            if (slot.Baseline.Count == 0 && slot.Variant.Count == 0)
            {
                lines.Add($"{name}: neither build uses it.");
                continue;
            }

            var text = $"{name}: baseline {List(slot.Baseline)}; variant {List(slot.Variant)}";
            if (slot.Added.Count > 0)
            {
                text += $" ({string.Join(", ", slot.Added)} added)";
            }

            if (slot.OpportunityCost)
            {
                inPlace.Add(ActionSlotReport.OpportunityCostText);
                text += $". **Collision**: {ActionSlotReport.OpportunityCostText}, not what the new option would add on a free {name}";
            }

            lines.Add(text + ".");
        }

        return "## Action economy\n\n" + Bullets(lines);

        static string List(IReadOnlyList<string> items) => items.Count == 0 ? "none" : string.Join(", ", items);
    }

    // How reliably each condition-imposing effect lands, per turn and at least once per fight.
    private static string? SignatureEffects(ComparisonReport report)
    {
        var rows = report.BaselineSignatureEffects.Select(e => (Build: "baseline", Effect: e))
            .Concat(report.SignatureEffects.Select(e => (Build: e.New ? "variant (new)" : "variant", Effect: e)))
            .ToList();
        if (rows.Count == 0)
        {
            return null;
        }

        var rounds = rows[0].Effect.Rounds;
        return $"## Signature effects at level {Number(report.DetailLevel)} (a {Number(rounds)}-round fight)\n\n" + SrdMarkdownText.Table(
            ["Effect", "Build", "Condition", "Lands in a turn", "At least once in the fight"],
            rows.Select(r => (IReadOnlyList<string>)
            [
                r.Effect.Name,
                r.Build,
                r.Effect.Condition,
                Percent(r.Effect.LandChancePerTurn),
                Percent(r.Effect.LandChancePerFight),
            ]));
    }

    // One build at the detail level, as balance_dpr shows it, one heading level down.
    private static string Build(string role, HorizonResult result, string level)
    {
        var evaluation = result.Evaluation;
        return SrdMarkdownText.Blocks(
        [
            $"## {role} at level {level}: {evaluation.BuildName}",
            BuildAsRead(evaluation.Build, $"### The build as read ({evaluation.Build.Edition} rules)"),
            Breakdown(evaluation, $"### Where the damage comes from, {PerRound(evaluation)}"),
            Distribution(evaluation.Round1Distribution, evaluation.Target),
            PowerAttacks(evaluation),
            SaveEffects(evaluation),
            result.Day is { } day ? Day(day, "### The adventuring day") : null,
        ]);
    }

    private static string Assumptions(ComparisonReport report)
    {
        var detail = report.Detail;
        var lines = new List<string>
        {
            "Both builds are evaluated under identical assumptions: the same horizon, the same target spec at each level, the same rulings.",
            HorizonAssumption(report.Horizon),
            Rulings(report.Levels.SelectMany(l => l.Baseline.Evaluation.Rulings.Concat(l.Variant.Evaluation.Rulings))),
        };

        foreach (var policies in new[] { Policies(detail.Baseline.Evaluation.Build), Policies(detail.Variant.Evaluation.Build) })
        {
            if (policies is not null && !lines.Contains(policies))
            {
                lines.Add(policies);
            }
        }

        return "**Assumptions:**\n" + Bullets(lines);
    }
}
