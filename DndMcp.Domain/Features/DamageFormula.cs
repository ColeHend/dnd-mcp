using System.Globalization;
using System.Text;
using DndMcp.Domain.Core;
using DndMcp.Domain.Dice;

namespace DndMcp.Domain.Features;

/// <summary>
/// <c>Count</c> dice of <c>Sides</c> sides, added (or, for bonus dice such as Bane's −1d4, subtracted).
/// </summary>
public sealed record DiceTerm(int Count, int Sides, bool Negative = false)
{
    /// <summary>"2d6", "-1d4".</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{(Negative ? "-" : "")}{Count}d{Sides}");
}

/// <summary>
/// A damage (or bonus-dice) formula in the DSL's deliberately small grammar: plain <c>NdM</c> terms and whole numbers
/// joined by + or −. "2d6", "1d8+1", "1d10+1d6", "1d4-1".
///
/// <para>
/// <b>Why not the full dice grammar</b> (<see cref="DiceExpression"/>): the DPR engine needs each die's own distribution
/// to apply remaps (GWF, Elemental Adept), crit doubling ("every damage die is rolled twice, flat modifiers are not"),
/// Savage Attacker's max over the weapon dice, and per-type resistance. A kept-highest, exploding or scaled term has no
/// meaning under those rules ("2d6kh1" doubled on a crit is not a rule anyone plays), so every modifier is refused with
/// the field and what to write instead. Parsing still goes through <see cref="DiceExpression.Parse"/> so syntax errors
/// read exactly as <c>dice_roll</c>'s do, and the AST is then walked keeping only sums of plain terms.
/// </para>
/// <para>
/// Terms with the same sides and sign are merged ("1d6+1d6" is "2d6", the same distribution), in first-appearance order,
/// so the canonical <see cref="ToString"/> is stable for display and for equality. Damage refuses subtracted dice
/// ("1d8-1d4" is dndMath.ts bug 5's territory and no damage rule subtracts dice); bonus dice keep their signs
/// (Bless + Bane = "1d4-1d4", which must NOT cancel) and refuse flat numbers, which belong in an amount.
/// </para>
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(DamageFormulaJsonConverter))]
public sealed class DamageFormula : IEquatable<DamageFormula>
{
    /// <summary>
    /// Where an attack's modifier, proficiency bonus and type go instead of its damage string: the words a model most often
    /// writes into it ("2d6+str", "1d8+PB", "1d10 force").
    /// </summary>
    public const string AttackHint =
        "the ability modifier is added for you (to_hit ability; ability_to_damage, default true); proficiency bonus is a " +
        "bonus_damage modifier with amount \"pb\"; the damage type goes in damage_type";

    /// <summary>Where a rider's or save effect's flat part and type go instead of its dice string.</summary>
    public const string AmountHint = "a flat bonus, \"pb\" or an ability modifier goes in amount; the damage type goes in type";

    /// <summary>Where a heal's flat part goes instead of its dice string.</summary>
    public const string HealHint = "a flat bonus, \"pb\" or an ability modifier (\"wis\") goes in amount";

    private const string DamageExample = "e.g. \"2d6\", \"1d8+1\" or \"1d10+1d6\"";
    private const string BonusExample = "e.g. \"1d4\" (Bless) or \"-1d4\" (Bane)";

    /// <summary>
    /// The dice grammar's own closing examples (<c>DiceParser</c>): every one of them ("4d6kh3", "adv+5", "8d6>=30") is a
    /// form this field refuses, so a syntax error drops them and says what the field takes instead. A test pins that they
    /// are gone, so a change to the parser's wording cannot bring them back unnoticed.
    /// </summary>
    private const string ParserExamples = "Examples: \"2d6+3\", \"4d6kh3\", \"adv+5\", \"8d6>=30\".";

    private DamageFormula(IReadOnlyList<DiceTerm> dice, int flat)
    {
        Dice = dice;
        Flat = flat;
        Text = Format(dice, flat);
    }

    public static DamageFormula Zero { get; } = new([], 0);

    /// <summary>The dice terms, merged by (sides, sign), in first-appearance order.</summary>
    public IReadOnlyList<DiceTerm> Dice { get; }

    /// <summary>The sum of the whole numbers (may be negative: a −1 weapon).</summary>
    public int Flat { get; }

    /// <summary>Canonical text: "2d6+1d4+3", "1d4-1d4", "5", "0".</summary>
    public string Text { get; }

    /// <summary>Every die of the formula, whatever its sign (what the 50-dice limit counts).</summary>
    public int DiceCount => Dice.Sum(d => d.Count);

    public bool HasDice => Dice.Count > 0;

    /// <summary>A formula of one whole number.</summary>
    public static DamageFormula Constant(int value) => value == 0 ? Zero : new DamageFormula([], value);

    /// <summary>A formula from terms and a flat part, merged into canonical form.</summary>
    public static DamageFormula Of(IEnumerable<DiceTerm> dice, int flat) => new(Merge(dice), flat);

    /// <summary>
    /// Damage: plain dice (added only) and whole numbers. "2d6", "1d8+1", "1d10+1d6", "1d4-1".
    /// </summary>
    /// <param name="field">The field for the message, e.g. "damage" or "damage at level 5".</param>
    /// <param name="hint">
    /// Where a word the model wrote into the dice belongs instead (<see cref="AttackHint"/>, <see cref="AmountHint"/>): said
    /// only when the text fails to parse and holds an ability, "pb" or a damage type.
    /// </param>
    /// <exception cref="DndInputException">Anything else, with what to write instead.</exception>
    public static DamageFormula ParseDamage(string? text, string field, string? hint = null) => Parse(text, field, bonusDice: false, flatHint: hint);

    /// <summary>
    /// Bonus dice on a d20 roll: plain dice with their signs, no whole numbers. "1d4", "-1d4", "1d4-1d4".
    /// </summary>
    /// <param name="flatHint">Where a flat number belongs instead, e.g. "put a flat bonus in amount".</param>
    public static DamageFormula ParseBonusDice(string? text, string field, string flatHint) => Parse(text, field, bonusDice: true, flatHint);

    /// <summary>The same formula with every dice count multiplied (cantrip scaling: 1d10 → 2d10 at level 5).</summary>
    public DamageFormula ScaleDice(int factor)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(factor, 1);
        return factor == 1 ? this : new DamageFormula(Dice.Select(d => d with { Count = d.Count * factor }).ToList(), Flat);
    }

    /// <summary>The sum of two formulas (a rider's dice plus its flat amount).</summary>
    public DamageFormula Plus(DamageFormula other) => Of(Dice.Concat(other.Dice), Flat + other.Flat);

    public bool Equals(DamageFormula? other) => other is not null && Text == other.Text;

    public override bool Equals(object? obj) => Equals(obj as DamageFormula);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Text);

    public override string ToString() => Text;

    private static DamageFormula Parse(string? text, string field, bool bonusDice, string? flatHint)
    {
        var what = bonusDice ? "these dice are plain dice joined by + or -, " + BonusExample : "damage is plain dice and whole numbers joined by + or -, " + DamageExample;
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new DndInputException($"{field} is empty; {what}.");
        }

        var echo = $"{field} \"{DslText.Echo(trimmed)}\"";
        DiceExpression expression;
        try
        {
            expression = DiceExpression.Parse(trimmed);
        }
        catch (DndInputException ex)
        {
            // A word where only dice and numbers go ("2d6+str", "1d8+PB", "1d10 force") says where it belongs; any other
            // syntax error keeps the parser's own words, without its examples of forms this field refuses.
            if (flatHint is not null && MisplacedWord(trimmed) is { } word)
            {
                throw new DndInputException($"{echo} has \"{DslText.Echo(word)}\": {flatHint}. {Capitalized(what)}.", ex);
            }

            var message = ex.Message.EndsWith(ParserExamples, StringComparison.Ordinal)
                ? ex.Message[..^ParserExamples.Length].TrimEnd()
                : ex.Message;
            throw new DndInputException($"{field}: {message} {Capitalized(what)}.", ex);
        }

        if (expression.Comparison is not null)
        {
            throw new DndInputException($"{echo} has a comparison ({expression.Comparison}); {what}.");
        }

        var terms = new List<DiceTerm>();
        long flat = 0;
        Walk(expression.Root, negative: false);

        var formula = new DamageFormula(Merge(terms), (int)flat);
        if (formula.Dice.FirstOrDefault(d => d.Sides > DslLimits.MaxSides) is { } large)
        {
            throw new DndInputException($"{echo} has a d{large.Sides}; dice here have at most {DslLimits.MaxSides} sides.");
        }

        if (formula.DiceCount > DslLimits.MaxDice)
        {
            throw new DndInputException($"{echo} rolls {formula.DiceCount} dice; at most {DslLimits.MaxDice} are accepted.");
        }

        if (Math.Abs(flat) > DslLimits.MaxFlat)
        {
            throw new DndInputException(FormattableString.Invariant($"{echo} adds {flat} in all; whole numbers here total -{DslLimits.MaxFlat} to {DslLimits.MaxFlat}."));
        }

        if (bonusDice && flat != 0)
        {
            throw new DndInputException(FormattableString.Invariant($"{echo} has a whole number ({flat:+0;-0}); these are dice only: {flatHint}."));
        }

        return formula;

        void Walk(DiceNode node, bool negative)
        {
            switch (node)
            {
                case SumNode sum:
                    Walk(sum.Left, negative);
                    Walk(sum.Right, sum.Subtract ? !negative : negative);
                    break;
                case NegateNode negate:
                    Walk(negate.Operand, !negative);
                    break;
                case ConstantNode constant:
                    if (constant.Label is not null)
                    {
                        throw Refused("has a label ([…]); give the damage type in its own field");
                    }

                    if (constant.Value > DslLimits.MaxFlat)
                    {
                        throw new DndInputException($"{echo} adds {constant.Value}; whole numbers here are -{DslLimits.MaxFlat} to {DslLimits.MaxFlat}.");
                    }

                    flat += negative ? -constant.Value : constant.Value;
                    break;
                case DiceGroupNode { Group: var group }:
                    if (RefusedModifier(group) is { } reason)
                    {
                        throw Refused(reason);
                    }

                    if (negative && !bonusDice)
                    {
                        throw new DndInputException(
                            $"{echo} subtracts dice (-{group.Count}d{group.Sides}); damage can only add dice. A penalty die such as " +
                            "Bane's goes on a to_hit modifier's dice.");
                    }

                    terms.Add(new DiceTerm(group.Count, group.Sides, negative));
                    break;
                case GroupingNode:
                    throw Refused("uses parentheses; write the terms out, e.g. \"1d6+2\"");
                case ScaleNode:
                    throw Refused("multiplies or divides (* or /); write the dice out, e.g. \"4d6\" (a crit doubles dice by itself)");
                default:
                    throw Refused("is not plain dice");
            }
        }

        DndInputException Refused(string reason) => new($"{echo} {reason}; {what}.");
    }

    /// <summary>
    /// The first word of <paramref name="text"/> that names an ability, the proficiency bonus or a damage type: what a
    /// model means as "plus my Str modifier" or "fire damage". Letters inside a dice term (the d of 2d6) are not words.
    /// </summary>
    private static string? MisplacedWord(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (!char.IsLetter(text[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < text.Length && char.IsLetter(text[i]))
            {
                i++;
            }

            var word = text[start..i];
            var key = word.ToLowerInvariant();
            if (key is DslAmount.ProficiencyBonusKeyword or "prof" or "proficiency" or "mod" or "modifier" ||
                DslValues.Abilities.Set.TryMatch(word, out _) ||
                DslValues.DamageTypes.Set.TryMatch(word, out _))
            {
                return word;
            }
        }

        return null;
    }

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>Why a dice group is not a plain NdM term, or null when it is.</summary>
    private static string? RefusedModifier(DiceGroup group)
    {
        if (group.Keep is not null)
        {
            return $"keeps or drops dice (\"{group.Text}\")";
        }

        if (group.Reroll is not null)
        {
            return $"rerolls dice (\"{group.Text}\"): for Great Weapon Fighting use fighting_style \"gwf\" or a damage_die_remap modifier";
        }

        if (group.Explode is not null)
        {
            return $"explodes dice (\"{group.Text}\")";
        }

        if (group.Min is not null || group.Max is not null)
        {
            return $"clamps dice (\"{group.Text}\"): for Elemental Adept use a damage_die_remap modifier";
        }

        if (group.Success is not null)
        {
            return $"counts successes (\"{group.Text}\")";
        }

        if (group.Label is not null)
        {
            return $"has a label ([{group.Label}]): give the damage type in its own field";
        }

        return null;
    }

    private static List<DiceTerm> Merge(IEnumerable<DiceTerm> terms)
    {
        var merged = new List<DiceTerm>();
        foreach (var term in terms)
        {
            var index = merged.FindIndex(t => t.Sides == term.Sides && t.Negative == term.Negative);
            if (index >= 0)
            {
                merged[index] = merged[index] with { Count = merged[index].Count + term.Count };
            }
            else
            {
                merged.Add(term);
            }
        }

        return merged;
    }

    private static string Format(IReadOnlyList<DiceTerm> dice, int flat)
    {
        var text = new StringBuilder();
        foreach (var term in dice)
        {
            if (text.Length > 0 && !term.Negative)
            {
                text.Append('+');
            }

            text.Append(term);
        }

        if (flat != 0 || text.Length == 0)
        {
            if (text.Length > 0 && flat > 0)
            {
                text.Append('+');
            }

            text.Append(flat.ToString(CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }
}
