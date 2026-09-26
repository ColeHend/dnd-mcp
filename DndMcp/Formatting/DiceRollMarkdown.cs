using System.Globalization;
using System.Text;
using DndMcp.Domain.Dice;

namespace DndMcp.Formatting;

/// <summary>
/// Renders <c>dice_roll</c> results. The one rule the format keeps: <b>the total can be checked from what is
/// shown</b> — each group's counted faces add up to its part of the total (a success count, or a group too long to
/// draw, states its value as "= n"), and the expression's operators are shown between the groups.
///
/// <para>
/// Output is bounded by characters, not dice. A 1000d1000 rolled 100 times is 100,000 faces, about 1M characters,
/// against Claude Code's 25k-token cap on a tool result; but 85 one-die terms rolled 100 times is just as long with
/// only a few dice each. So the breakdowns share <see cref="CharacterBudget"/>: a roll draws dice until its share is
/// spent and summarises the rest of the group ("…+980 more = 500123"); if even the expression's shape does not fit,
/// every group is summarised; and if that still does not fit, the roll shows its total alone and a closing note says
/// so. Long reroll and compounding chains inside one die are shortened too.
/// </para>
/// </summary>
internal static class DiceRollMarkdown
{
    /// <summary>Breakdown characters across one result: about 2k tokens.</summary>
    public const int CharacterBudget = 8_000;

    /// <summary>Every roll's share, however many rolls there are: enough for a few dice and a subtotal.</summary>
    public const int MinCharactersPerRoll = 80;

    /// <summary>Rerolls or compounded faces shown inside one die before the middle is elided.</summary>
    private const int MaxPartsPerDie = 6;

    /// <summary>Room kept for a group's closing "…+N more = S]".</summary>
    private const int SummaryReserve = 32;

    public static string Format(DiceExpression expression, IReadOnlyList<DiceRoll> rolls, string? label, string source)
    {
        var output = new StringBuilder();
        var markers = new Markers();
        var perRoll = Math.Max(MinCharactersPerRoll, CharacterBudget / rolls.Count);
        var prefix = label is null ? string.Empty : label + ": ";
        var omitted = 0;

        if (rolls.Count == 1)
        {
            var roll = rolls[0];
            var breakdown = Breakdown(roll, perRoll, markers);
            output.Append(prefix)
                  .Append(Total(roll))
                  .Append("  (")
                  .Append(expression.TotalText)
                  .Append(" → ")
                  .Append(breakdown ?? "breakdown omitted")
                  .Append(')')
                  .Append(Verdict(expression, roll))
                  .AppendLine();
            omitted += breakdown is null ? 1 : 0;
        }
        else
        {
            output.Append(prefix).Append(expression.Text).Append(", ").Append(Invariant($"{rolls.Count} rolls:")).AppendLine();
            for (var i = 0; i < rolls.Count; i++)
            {
                var breakdown = Breakdown(rolls[i], perRoll, markers);
                output.Append(Invariant($"#{i + 1}: ")).Append(Total(rolls[i]));
                if (breakdown is null)
                {
                    omitted++;
                }
                else
                {
                    output.Append("  (").Append(breakdown).Append(')');
                }

                output.Append(Verdict(expression, rolls[i])).AppendLine();
            }

            if (expression.Comparison is { } comparison)
            {
                var met = rolls.Count(r => r.ComparisonMet == true);
                output.Append(Invariant($"{comparison.Symbol} {comparison.Target} on {met} of {rolls.Count} rolls.")).AppendLine();
            }
        }

        if (markers.Legend() is { } legend)
        {
            output.Append('_').Append(legend).Append("._").AppendLine();
        }

        if (omitted > 0)
        {
            output.Append(Invariant(
                $"_Dice omitted for {omitted} roll{(omitted == 1 ? "" : "s")} to keep this result short; roll fewer times to see every die._")).AppendLine();
        }

        output.Append("_Randomness: ").Append(source).Append("._");
        return output.ToString();
    }

    private static string Total(DiceRoll roll) => Invariant($"**{roll.Total}**");

    private static string Verdict(DiceExpression expression, DiceRoll roll) =>
        expression.Comparison is { } comparison
            ? Invariant($" · {comparison.Symbol} {comparison.Target}? **{(roll.ComparisonMet == true ? "yes" : "no")}**")
            : string.Empty;

    /// <summary>The roll's breakdown within <paramref name="budget"/> characters, or null if not even its shape fits.</summary>
    private static string? Breakdown(DiceRoll roll, int budget, Markers markers)
    {
        var used = new Markers();
        var full = Render(roll, budget, summariseEveryGroup: false, used);
        if (full.Length <= budget)
        {
            markers.Merge(used);
            return full;
        }

        var summary = Render(roll, budget, summariseEveryGroup: true, new Markers());
        return summary.Length <= budget ? summary : null;
    }

    private static string Render(DiceRoll roll, int budget, bool summariseEveryGroup, Markers markers)
    {
        var groups = new Queue<RolledGroup>(roll.Groups);
        var text = new StringBuilder();
        Visit(roll.Expression.Root);
        return text.ToString();

        void Visit(DiceNode node)
        {
            switch (node)
            {
                case ConstantNode c:
                    text.Append(Invariant($"{c.Value}"));
                    AppendLabel(c.Label);
                    break;
                case DiceGroupNode:
                    var group = groups.Dequeue();
                    AppendGroup(text, group, summariseEveryGroup ? 0 : budget, markers);
                    AppendLabel(group.Group.Label);
                    break;
                case NegateNode { Operand: ScaleNode } n:
                    // "-1d4/2" is -(1d4/2); "-[1] / 2" would invite (-1)/2, which rounds down to -1, not 0.
                    text.Append("-(");
                    Visit(n.Operand);
                    text.Append(')');
                    break;
                case NegateNode n:
                    text.Append('-');
                    Visit(n.Operand);
                    break;
                case GroupingNode p:
                    text.Append('(');
                    Visit(p.Inner);
                    text.Append(')');
                    break;
                case SumNode s:
                    Visit(s.Left);
                    text.Append(s.Subtract ? " - " : " + ");
                    Visit(s.Right);
                    break;
                case ScaleNode m:
                    Visit(m.Operand);
                    text.Append(Invariant($" {(m.Divide ? '/' : '*')} {m.Factor}"));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown dice node {node.GetType().Name}.");
            }
        }

        void AppendLabel(string? label)
        {
            // Italic, so a label never reads as part of the arithmetic.
            if (label is not null)
            {
                text.Append(" _").Append(label).Append('_');
            }
        }
    }

    /// <summary>
    /// Draws the group's dice while the breakdown stays inside <paramref name="budget"/>, then states how many were
    /// left out and the group's value, so the total can still be checked.
    /// </summary>
    private static void AppendGroup(StringBuilder text, RolledGroup group, int budget, Markers markers)
    {
        var tokens = new List<string>();
        var length = text.Length + 1;
        foreach (var die in group.Dice)
        {
            var dieMarkers = new Markers();
            var token = Die(die, group.Group, dieMarkers);
            if (length + token.Length + 2 + SummaryReserve > budget)
            {
                break;
            }

            tokens.Add(token);
            length += token.Length + 2;
            markers.Merge(dieMarkers);
        }

        var hidden = group.Dice.Count - tokens.Count;
        if (hidden > 0)
        {
            tokens.Add(tokens.Count == 0 ? Invariant($"…{hidden} {(hidden == 1 ? "die" : "dice")}") : Invariant($"…+{hidden} more"));
        }

        text.Append('[').Append(string.Join(", ", tokens));

        // A success count is not the sum of the faces shown, and a list with dice left out cannot be summed by eye:
        // say the group's value outright in both cases.
        if (group.Group.CountsSuccesses || hidden > 0)
        {
            text.Append(Invariant($" = {group.Value}"));
        }

        text.Append(']');
    }

    private static string Die(RolledDie die, DiceGroup group, Markers markers)
    {
        var parts = new List<string>();
        var rerolls = new List<int>();

        foreach (var face in die.Faces)
        {
            if (face.Rerolled)
            {
                rerolls.Add(face.Face);
                continue;
            }

            var shown = Rerolls(rerolls, markers) + Invariant($"{face.Face}") + (face.Exploded ? "!" : string.Empty);
            markers.Exploded |= face.Exploded;
            rerolls.Clear();
            parts.Add(shown);
        }

        var token = parts.Count <= MaxPartsPerDie
            ? string.Join("+", parts)
            : string.Join("+", parts.Take(MaxPartsPerDie - 2)) + Invariant($"+…{parts.Count - MaxPartsPerDie + 1} more…+") + parts[^1];

        if (parts.Count > 1)
        {
            token += Invariant($"={die.Raw}");
        }

        if (die.Penetrated)
        {
            token += "-1";
            markers.Penetrated = true;
        }

        if (die.Value != die.Raw)
        {
            token += Invariant($"⇒{die.Value}");
            markers.Clamped = true;
        }

        if (group.CountsSuccesses && die.Score != 0)
        {
            token += die.Score > 0 ? "✓" : "✗";
            markers.Counted = true;
        }

        if (die.Dropped)
        {
            markers.Dropped = true;
            return "~~" + token + "~~";
        }

        return token;
    }

    private static string Rerolls(List<int> rerolls, Markers markers)
    {
        if (rerolls.Count == 0)
        {
            return string.Empty;
        }

        markers.Rerolled = true;
        return rerolls.Count <= 3
            ? string.Concat(rerolls.Select(r => Invariant($"{r}⟳")))
            : Invariant($"{rerolls[0]}⟳…{rerolls.Count - 2} more…⟳{rerolls[^1]}⟳");
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    /// <summary>Which notations a result used, so the legend explains only those.</summary>
    private sealed class Markers
    {
        public void Merge(Markers other)
        {
            Dropped |= other.Dropped;
            Rerolled |= other.Rerolled;
            Exploded |= other.Exploded;
            Penetrated |= other.Penetrated;
            Clamped |= other.Clamped;
            Counted |= other.Counted;
        }

        public bool Dropped { get; set; }

        public bool Rerolled { get; set; }

        public bool Exploded { get; set; }

        public bool Penetrated { get; set; }

        public bool Clamped { get; set; }

        public bool Counted { get; set; }

        public string? Legend()
        {
            var entries = new List<string>();
            if (Dropped)
            {
                entries.Add("~~struck~~ = dropped, not counted");
            }

            if (Rerolled)
            {
                entries.Add("1⟳5 = rolled 1, rerolled to 5");
            }

            if (Exploded)
            {
                entries.Add("! = exploded, rolled again");
            }

            if (Penetrated)
            {
                entries.Add("-1 = penetrating die, counts one less");
            }

            if (Clamped)
            {
                entries.Add("⇒ = raised or lowered by min/max");
            }

            if (Counted)
            {
                entries.Add("✓/✗ = success/failure");
            }

            return entries.Count == 0 ? null : string.Join(" · ", entries);
        }
    }
}
