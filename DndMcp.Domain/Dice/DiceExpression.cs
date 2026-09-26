using System.Globalization;
using DndMcp.Domain.Core;

namespace DndMcp.Domain.Dice;

/// <summary>
/// A parsed, validated dice expression: the one input both <c>dice_roll</c> and <c>dice_odds</c> take.
///
/// <para>Grammar (case-insensitive):</para>
/// <code>
/// query  := sum [cmp INT]                       ; trailing comparison: "8d6&gt;=30"
/// sum    := ['+'|'-'] term (('+'|'-') term)*
/// term   := factor (('*'|'/') INT)*             ; '/' rounds down
/// factor := dice | INT ['[' label ']'] | '(' sum ')' | adv | dis | ea   ; 2d20kh1 / 2d20kl1 / 3d20kh1
/// dice   := [INT] 'd' (INT|'%') mod* ['[' label ']']
/// mod    := (kh|kl|k|dh|dl)[INT] | r[cmp]INT | ro[cmp]INT | ![cmp INT] | !![cmp INT] | !p[cmp INT]
///         | min INT | max INT | cs cmp INT | cf cmp INT
/// cmp    := '=' | '&lt;' | '&lt;=' | '&gt;' | '&gt;='
/// </code>
/// <para>
/// Whitespace is allowed around operators and comparisons, never inside a dice term: "2 d6" and "2d6 3" are
/// refused rather than read as 2d6 or 2d63, and "8d6! &gt;= 30" (explode, then compare the total) differs from
/// "8d6!&gt;=30" (explode on 30+, which no d6 face reaches, so it is refused with that hint). Every refusal is a
/// <see cref="DndInputException"/> that quotes the input and says what would be accepted.
/// </para>
/// </summary>
public sealed class DiceExpression
{
    internal DiceExpression(string text, string totalText, DiceNode root, DiceCondition? comparison, IReadOnlyList<DiceGroup> groups, long minValue, long maxValue)
    {
        Text = text;
        TotalText = totalText;
        Root = root;
        Comparison = comparison;
        Groups = groups;
        MinValue = minValue;
        MaxValue = maxValue;
    }

    /// <summary>The expression as typed, trimmed.</summary>
    public string Text { get; }

    /// <summary><see cref="Text"/> without the trailing comparison: what the comparison is applied to.</summary>
    public string TotalText { get; }

    public DiceNode Root { get; }

    /// <summary>The trailing comparison, if any. <see cref="Root"/> is the total it applies to.</summary>
    public DiceCondition? Comparison { get; }

    /// <summary>Every dice term, in evaluation (left-to-right) order.</summary>
    public IReadOnlyList<DiceGroup> Groups { get; }

    /// <summary>Conservative static bounds on the total: every possible total lies inside, not every value is reachable.</summary>
    public long MinValue { get; }

    public long MaxValue { get; }

    public static DiceExpression Parse(string expression) => new DiceParser(expression).Parse();
}

/// <summary>Recursive-descent parser for <see cref="DiceExpression"/>. One instance per parse.</summary>
internal sealed class DiceParser
{
    private const string Example = "Examples: \"2d6+3\", \"4d6kh3\", \"adv+5\", \"8d6>=30\".";

    private const string ModifierList =
        "kh/kl/dh/dl N (keep/drop), r/ro [cmp]N (reroll), !, !!, !p [cmp N] (explode), min N / max N (clamp), cs/cf cmp N (count successes/failures)";

    private readonly string _original;
    private readonly string _text;
    private readonly List<DiceGroup> _groups = [];
    private int _pos;
    private int _totalDice;

    public DiceParser(string expression)
    {
        _original = expression?.Trim() ?? string.Empty;
        _text = _original.ToLowerInvariant();
    }

    private bool AtEnd => _pos >= _text.Length;

    private char Current => _pos < _text.Length ? _text[_pos] : '\0';

    public DiceExpression Parse()
    {
        if (_original.Length == 0)
        {
            throw new DndInputException("The dice expression is empty. " + Example);
        }

        if (_original.Length > DiceLimits.MaxExpressionLength)
        {
            throw new DndInputException(
                $"The dice expression is {_original.Length} characters long; the limit is {DiceLimits.MaxExpressionLength}. {Example}");
        }

        if (_text.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new DndInputException($"The dice expression must be one line. {Example}");
        }

        var root = ParseSum();
        var totalText = _original[.._pos].TrimEnd();

        SkipWhitespace();
        DiceCondition? comparison = null;
        if (TryReadComparison(out var op))
        {
            SkipWhitespace();
            var negative = TryConsume('-');
            if (!char.IsAsciiDigit(Current))
            {
                throw Error($"a number must follow \"{Symbol(op)}\"", "e.g. \"8d6>=30\"");
            }

            var target = ReadInteger(DiceLimits.MaxMagnitude, "the comparison target");
            comparison = new DiceCondition(op, negative ? -target : target);
            SkipWhitespace();

            if (TryPeekComparison(out _))
            {
                throw Error("only one comparison is allowed, at the end", "e.g. \"1d20+5>=15\"");
            }
        }

        if (!AtEnd)
        {
            throw UnexpectedAfterTerm();
        }

        var (min, max) = Bounds(root);
        return new DiceExpression(_original, totalText, root, comparison, _groups, min, max);
    }

    private DiceNode ParseSum()
    {
        SkipWhitespace();
        var negate = false;
        if (Current is '+' or '-')
        {
            negate = Current == '-';
            _pos++;
            SkipWhitespace();
        }

        var node = ParseTerm();
        if (negate)
        {
            node = new NegateNode(node);
        }

        while (true)
        {
            SkipWhitespace();
            if (Current is not ('+' or '-'))
            {
                return node;
            }

            var subtract = Current == '-';
            _pos++;
            SkipWhitespace();

            if (Current is '+' or '-')
            {
                throw Error("two signs in a row", "write \"2d6-3\" or \"2d6+3\", not \"2d6+-3\"");
            }

            var right = ParseTerm();
            node = new SumNode(node, right, subtract);
        }
    }

    private DiceNode ParseTerm()
    {
        var node = ParseFactor();

        while (true)
        {
            var save = _pos;
            SkipWhitespace();
            if (Current is not ('*' or '/'))
            {
                _pos = save;
                return node;
            }

            var divide = Current == '/';
            _pos++;
            SkipWhitespace();

            if (!char.IsAsciiDigit(Current))
            {
                throw Error(
                    $"\"{(divide ? '/' : '*')}\" must be followed by a whole number",
                    "dice can only be multiplied or divided by a constant, e.g. \"2d6*2\" or \"(1d8+3)/2\"");
            }

            var factor = ReadInteger(DiceLimits.MaxFactor, divide ? "the divisor" : "the multiplier");
            if (Current == 'd')
            {
                throw Error(
                    $"\"{(divide ? '/' : '*')}\" must be followed by a whole number, not dice",
                    "dice can only be multiplied or divided by a constant, e.g. \"2d6*2\" or \"(1d8+3)/2\"");
            }

            if (factor < 1)
            {
                throw Invalid($"{(divide ? "the divisor" : "the multiplier")} must be at least 1", "e.g. \"2d6*2\"");
            }

            node = new ScaleNode(node, factor, divide);
        }
    }

    private DiceNode ParseFactor()
    {
        SkipWhitespace();

        if (AtEnd)
        {
            throw Error("the expression ends where a number or dice were expected", Example);
        }

        if (Current == '(')
        {
            _pos++;
            var inner = ParseSum();
            SkipWhitespace();
            if (!TryConsume(')'))
            {
                throw AtEnd ? Error("a \")\" is missing", "every \"(\" needs a matching \")\"") : UnexpectedAfterTerm();
            }

            return new GroupingNode(inner);
        }

        if (TryReadShorthand(out var shorthand))
        {
            return shorthand;
        }

        if (char.IsAsciiDigit(Current) || Current == 'd')
        {
            return ParseDiceOrConstant();
        }

        throw Error("expected a number, dice such as \"2d6\", adv/dis/ea, or \"(\"", Example);
    }

    private bool TryReadShorthand(out DiceNode node)
    {
        foreach (var (word, count, highest, description) in new[]
                 {
                     ("adv", 2, true, "2d20kh1"),
                     ("dis", 2, false, "2d20kl1"),
                     ("ea", 3, true, "3d20kh1"),
                 })
        {
            if (!_text.AsSpan(_pos).StartsWith(word) ||
                (_pos + word.Length < _text.Length && char.IsAsciiLetterOrDigit(_text[_pos + word.Length])))
            {
                continue;
            }

            var start = _pos;
            _pos += word.Length;

            if (Current == '!')
            {
                throw Error($"{word} takes no modifiers", $"write \"{description}\" and add modifiers to that");
            }

            var label = TryReadLabel();
            AddDice(count, start);
            var group = new DiceGroup(count, 20, new KeepRule(highest, 1), null, null, null, null, null, null, label, word);
            _groups.Add(group);
            node = new DiceGroupNode(group);
            return true;
        }

        node = null!;
        return false;
    }

    private DiceNode ParseDiceOrConstant()
    {
        var start = _pos;
        long? count = null;
        if (char.IsAsciiDigit(Current))
        {
            count = ReadInteger(long.MaxValue, "the number");
        }

        if (Current != 'd' || (count is null && !IsDieStart()))
        {
            // A plain number. The limit is checked here rather than in ReadInteger so "1001d6" gets the dice message.
            if (count is null)
            {
                throw Error("expected a number, dice such as \"2d6\", adv/dis/ea, or \"(\"", Example);
            }

            if (count > DiceLimits.MaxConstant)
            {
                throw Invalid($"{count} is too large; numbers go up to {DiceLimits.MaxConstant}", Example);
            }

            return new ConstantNode(count.Value, TryReadLabel());
        }

        _pos++; // 'd'
        int sides;
        if (TryConsume('%'))
        {
            sides = 100;
        }
        else if (char.IsAsciiDigit(Current))
        {
            var sideValue = ReadInteger(long.MaxValue, "the number of sides");
            if (sideValue < 1)
            {
                throw Invalid($"\"{Slice(start)}\" needs at least one side", "e.g. \"1d6\"");
            }

            if (sideValue > DiceLimits.MaxSides)
            {
                throw Invalid($"\"{Slice(start)}\" has {sideValue} sides; the limit is {DiceLimits.MaxSides}", "e.g. \"1d100\"");
            }

            sides = (int)sideValue;
        }
        else
        {
            throw Error("\"d\" must be followed by the number of sides or %", "e.g. \"1d20\" or \"d%\"");
        }

        var diceCount = count ?? 1;
        if (diceCount < 1)
        {
            throw Invalid($"\"{Slice(start)}\" needs at least one die", "e.g. \"1d6\"");
        }

        if (diceCount > DiceLimits.MaxDice)
        {
            throw Invalid($"\"{Slice(start)}\" rolls {diceCount} dice; the limit is {DiceLimits.MaxDice}", "e.g. \"100d6\"");
        }

        var group = ParseModifiers((int)diceCount, sides, start);
        AddDice(group.Count, start);
        _groups.Add(group);
        return new DiceGroupNode(group);
    }

    // "d" starts a die only when followed by a digit or %; this keeps "dl"/"dh" (modifiers) and "dis" apart.
    private bool IsDieStart() =>
        Current == 'd' && _pos + 1 < _text.Length && (char.IsAsciiDigit(_text[_pos + 1]) || _text[_pos + 1] == '%');

    private DiceGroup ParseModifiers(int count, int sides, int start)
    {
        (string Word, long Count)? keepDrop = null;
        RerollRule? reroll = null;
        ExplodeRule? explode = null;
        var explodeConditionStart = 0;
        int? min = null;
        int? max = null;
        DiceCondition? success = null;
        DiceCondition? failure = null;

        while (!AtEnd)
        {
            var modifierStart = _pos;

            if (TryConsumeWord("kh") || TryConsumeWord("kl") || TryConsumeWord("dh") || TryConsumeWord("dl") || TryConsumeWord("k"))
            {
                var word = _text[modifierStart.._pos];
                if (keepDrop is not null)
                {
                    throw Invalid($"\"{Slice(start)}\" has more than one keep/drop modifier", "use one of kh, kl, dh or dl");
                }

                // "4d6kh-1" would otherwise read as 4d6kh1 - 1: the count defaults to 1 and the "-1" becomes a term.
                if (Current is '+' or '-')
                {
                    throw Error($"\"{word}\" must be followed by how many dice, not a sign",
                        $"e.g. \"4d6{word}3\"; to add or subtract, give the count first: \"4d6{word}1 - 1\"");
                }

                keepDrop = (word, char.IsAsciiDigit(Current) ? ReadInteger(long.MaxValue, "the keep/drop count") : 1);
                continue;
            }

            if (TryConsumeWord("ro") || TryConsumeWord("r"))
            {
                var once = _pos - modifierStart == 2;
                if (reroll is not null)
                {
                    throw Invalid($"\"{Slice(start)}\" has more than one reroll modifier", "use either r or ro, once");
                }

                var condition = ReadModifierCondition(once ? "ro" : "r", required: false, "\"1d20r1\" or \"2d6ro<=2\"");
                reroll = new RerollRule(condition, once);
                ValidateReroll(condition, sides, start, once);
                continue;
            }

            if (Current == '!')
            {
                _pos++;
                var kind = TryConsume('!') ? ExplodeKind.Compound : TryConsume('p') ? ExplodeKind.Penetrating : ExplodeKind.Standard;
                if (explode is not null)
                {
                    throw Invalid($"\"{Slice(start)}\" has more than one explode modifier", "use one of !, !! or !p");
                }

                // Validated after the loop, against the faces a reroll can leave ("1d6r<6!" always explodes).
                explodeConditionStart = _pos;
                var condition = IsComparisonOrDigit()
                    ? ReadModifierCondition("!", required: false, "\"1d6!\" or \"1d6!>=5\"")
                    : new DiceCondition(DiceComparison.Equal, sides);
                explode = new ExplodeRule(condition, kind);
                continue;
            }

            if (TryConsumeWord("min") || TryConsumeWord("max"))
            {
                var isMin = _text[modifierStart + 1] == 'i';
                if ((isMin ? min : max) is not null)
                {
                    throw Invalid($"\"{Slice(start)}\" has more than one {(isMin ? "min" : "max")}", "use each at most once");
                }

                if (!char.IsAsciiDigit(Current))
                {
                    throw Error($"\"{(isMin ? "min" : "max")}\" must be followed by a number", "e.g. \"1d20min10\"");
                }

                var value = (int)ReadInteger(DiceLimits.MaxSides * (DiceLimits.MaxExplosionsPerDie + 1L), isMin ? "min" : "max");
                if (isMin)
                {
                    min = value;
                }
                else
                {
                    max = value;
                }

                continue;
            }

            if (TryConsumeWord("cs") || TryConsumeWord("cf"))
            {
                var isSuccess = _text[modifierStart + 1] == 's';
                if ((isSuccess ? success : failure) is not null)
                {
                    throw Invalid($"\"{Slice(start)}\" has more than one {(isSuccess ? "cs" : "cf")}", "use each at most once");
                }

                var condition = ReadModifierCondition(isSuccess ? "cs" : "cf", required: true, "\"10d10cs>=8\" or \"10d10cs>=8cf=1\"");
                if (isSuccess)
                {
                    success = condition;
                }
                else
                {
                    failure = condition;
                }

                continue;
            }

            if (IsDieStart() || char.IsAsciiDigit(Current))
            {
                throw Error("two dice terms must be joined with + or -", "e.g. \"2d6+1d4\"");
            }

            if (char.IsAsciiLetter(Current) || Current == '%')
            {
                throw Error($"unknown modifier after \"{Slice(start, modifierStart)}\"", "modifiers are " + ModifierList);
            }

            break;
        }

        if (min is { } lo && max is { } hi && lo > hi)
        {
            throw Invalid($"\"{Slice(start)}\" has min {lo} above max {hi}", "e.g. \"1d20min10\"");
        }

        if (failure is not null && success is null)
        {
            throw Invalid($"\"{Slice(start)}\" counts failures (cf) without successes (cs)", "e.g. \"10d10cs>=8cf=1\"");
        }

        if (explode is not null)
        {
            ValidateExplosion(explode.Condition, reroll, sides, start, explodeConditionStart);
        }

        // With ! or !p every explosion adds a die, so the pool the keep/drop acts on is bigger than the dice typed.
        var pooled = explode is { Kind: ExplodeKind.Standard or ExplodeKind.Penetrating };
        var keep = keepDrop is { } kd ? KeepRuleFor(kd.Word, kd.Count, count, pooled, start) : null;

        var text = Slice(start);
        var label = TryReadLabel();
        return new DiceGroup(count, sides, keep, reroll, explode, min, max, success, failure, label, text);
    }

    /// <summary>
    /// Builds the keep rule once the whole group is read. A fixed pool (the dice typed) normalises drops to keeps —
    /// 4d6dl1 is 4d6kh3 — and drops a keep of every die. An exploding pool cannot: its size is only known after
    /// rolling, so "drop the lowest 1" must stay a drop (4d6!dl1 keeps every die but one, however many there are),
    /// and 3d6!kh3 still discards dice whenever something explodes.
    /// </summary>
    private KeepRule? KeepRuleFor(string word, long n, int count, bool pooled, int start)
    {
        var drop = word is "dh" or "dl";
        if (n < 1)
        {
            throw Invalid($"\"{Slice(start)}\" {(drop ? "drops" : "keeps")} no dice", $"e.g. \"4d6{(drop ? "dl1" : "kh3")}\"");
        }

        if (drop)
        {
            if (n >= count)
            {
                throw Invalid($"\"{Slice(start)}\" drops every die{(pooled ? " unless one explodes" : "")}", "e.g. \"4d6dl1\"");
            }

            // dl keeps the highest, dh the lowest.
            return pooled ? new KeepRule(word == "dl", (int)n, Drop: true) : new KeepRule(word == "dl", count - (int)n);
        }

        if (n > count && !pooled)
        {
            throw Invalid($"\"{Slice(start)}\" keeps {n} of only {count} dice", "e.g. \"4d6kh3\"");
        }

        // Keeping every die of a fixed pool is a plain sum; normalising it spares the exact path a DP it does not need.
        return n == count && !pooled ? null : new KeepRule(word != "kl", (int)Math.Min(n, DiceLimits.MaxDice * (DiceLimits.MaxExplosionsPerDie + 1L)));
    }

    /// <summary>
    /// An explosion must be possible and must be able to stop, judged on the faces a die can actually end on: after
    /// <c>r</c> (reroll until it stops matching) only the non-matching faces remain, so "1d6r&lt;6!" explodes on every
    /// roll it keeps and "1d6r6!" never explodes. <c>ro</c> can still end on any face, so it removes none.
    /// </summary>
    private void ValidateExplosion(DiceCondition condition, RerollRule? reroll, int sides, int start, int conditionStart)
    {
        var survivors = Enumerable.Range(1, sides).Where(f => reroll is not { Once: false } r || !r.Condition.Matches(f)).ToList();
        var exploding = survivors.Count(f => condition.Matches(f));
        var die = $"d{sides}";
        var afterReroll = survivors.Count < sides ? $" left after {(reroll!.Once ? "ro" : "r")}{reroll.Condition}" : "";

        if (exploding == 0)
        {
            var hint = afterReroll.Length == 0 && condition.Comparison is DiceComparison.Greater or DiceComparison.GreaterOrEqual
                ? $"To compare the total instead, leave a space before the comparison: \"{Slice(start, conditionStart)} {condition.Operator} {condition.Target}\""
                : $"The faces a {die} can end on{afterReroll} are {string.Join(", ", survivors.Take(10))}{(survivors.Count > 10 ? ", …" : "")}";
            throw Invalid($"\"{Slice(start)}\" never explodes: no face of a {die}{afterReroll} is {condition.Operator}{condition.Target}", hint);
        }

        if (exploding == survivors.Count)
        {
            throw Invalid($"\"{Slice(start)}\" explodes on every face of a {die}{afterReroll}, so it would never finish", "e.g. \"1d6!\"");
        }
    }

    private DiceCondition ReadModifierCondition(string modifier, bool required, string example)
    {
        var comparison = DiceComparison.Equal;
        var hasOperator = TryReadComparison(out var op);
        if (hasOperator)
        {
            comparison = op;
        }
        else if (required)
        {
            throw Error($"\"{modifier}\" needs a comparison", $"e.g. {example}");
        }

        if (!char.IsAsciiDigit(Current))
        {
            throw Error($"\"{modifier}\" must be followed by {(hasOperator ? "a number" : "a face number or a comparison")}", $"e.g. {example}");
        }

        return new DiceCondition(comparison, ReadInteger(DiceLimits.MaxMagnitude, $"the {modifier} target"));
    }

    /// <summary>
    /// A reroll condition must match some face (otherwise it does nothing and is almost always a typo) and, for
    /// <c>r</c>, not every face (it would never stop). <c>ro</c> matching every face is refused too: it is just a
    /// second roll, so it is almost certainly a mistake.
    /// </summary>
    private void ValidateReroll(DiceCondition condition, int sides, int start, bool once)
    {
        var matching = Enumerable.Range(1, sides).Count(f => condition.Matches(f));
        var die = $"d{sides}";
        if (matching == 0)
        {
            throw Invalid($"\"{Slice(start)}\" never rerolls: no face of a {die} is {condition.Operator}{condition.Target}", $"The faces of a {die} are 1-{sides}");
        }

        if (matching == sides)
        {
            throw Invalid($"\"{Slice(start)}\" rerolls every face of a {die}{(once ? "" : ", so it would never finish")}", "e.g. \"1d20r1\"");
        }
    }

    private string? TryReadLabel()
    {
        var save = _pos;
        SkipWhitespace();
        if (!TryConsume('['))
        {
            _pos = save;
            return null;
        }

        var close = _original.IndexOf(']', _pos);
        if (close < 0)
        {
            throw Error("a label's \"[\" has no closing \"]\"", "e.g. \"2d6[fire]\"");
        }

        // Letters, digits, spaces and ' - . only: the label is shown next to the dice in the result, and "[+10]" or
        // "[= 20]" there would read as part of the arithmetic the user is meant to be able to check.
        var label = _original[_pos..close].Trim();
        if (label.Length == 0 || label.Length > DiceLimits.MaxLabelLength ||
            !label.All(c => char.IsLetterOrDigit(c) || c is ' ' or '\'' or '-' or '.'))
        {
            throw Invalid(
                $"labels must be 1-{DiceLimits.MaxLabelLength} characters of letters, digits, spaces, apostrophes, hyphens or periods",
                "e.g. \"2d6[fire]\" or \"1d6[Hunter's Mark]\"");
        }

        _pos = close + 1;
        return label;
    }

    private void AddDice(int count, int start)
    {
        _totalDice += count;
        if (_totalDice > DiceLimits.MaxDice)
        {
            throw Invalid($"the expression rolls {_totalDice} dice in total; the limit is {DiceLimits.MaxDice}", "roll fewer dice");
        }
    }

    private long ReadInteger(long max, string what)
    {
        var start = _pos;
        while (char.IsAsciiDigit(Current))
        {
            _pos++;
        }

        var digits = _text[start.._pos];

        // More than 18 digits cannot be a long; the per-kind limits are all far smaller anyway.
        if (digits.Length > 18 || !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value > max)
        {
            throw Invalid($"{what} ({digits}) is too large", max < long.MaxValue ? $"the limit is {max}" : Example);
        }

        return value;
    }

    private bool TryReadComparison(out DiceComparison comparison)
    {
        if (!TryPeekComparison(out comparison))
        {
            return false;
        }

        _pos += comparison is DiceComparison.LessOrEqual or DiceComparison.GreaterOrEqual ? 2 : 1;
        return true;
    }

    private bool TryPeekComparison(out DiceComparison comparison)
    {
        var next = _pos + 1 < _text.Length ? _text[_pos + 1] : '\0';
        switch (Current)
        {
            case '>':
                comparison = next == '=' ? DiceComparison.GreaterOrEqual : DiceComparison.Greater;
                return true;
            case '<':
                comparison = next == '=' ? DiceComparison.LessOrEqual : DiceComparison.Less;
                return true;
            case '=':
                comparison = DiceComparison.Equal;
                return true;
            default:
                comparison = default;
                return false;
        }
    }

    private bool IsComparisonOrDigit() => char.IsAsciiDigit(Current) || TryPeekComparison(out _);

    private bool TryConsume(char c)
    {
        if (Current != c)
        {
            return false;
        }

        _pos++;
        return true;
    }

    private bool TryConsumeWord(string word)
    {
        if (!_text.AsSpan(_pos).StartsWith(word))
        {
            return false;
        }

        _pos += word.Length;
        return true;
    }

    private void SkipWhitespace()
    {
        while (char.IsWhiteSpace(Current))
        {
            _pos++;
        }
    }

    /// <summary>
    /// The error for anything left over after a complete term. Named cases first — they are the model's likeliest
    /// mistakes — then the generic one.
    /// </summary>
    private DndInputException UnexpectedAfterTerm()
    {
        var rest = _text[_pos..];
        var previousIsWhitespace = _pos > 0 && char.IsWhiteSpace(_text[_pos - 1]);

        if (previousIsWhitespace && _groups.Count > 0 && StartsWithModifier(rest) && !IsDieStart())
        {
            return Error("modifiers must follow the dice without a space", "e.g. \"4d6kh3\", not \"4d6 kh3\"");
        }

        if (previousIsWhitespace && IsDieStart())
        {
            return Error("dice are written without spaces, and terms are joined with + or -", "e.g. \"2d6\" or \"1d20 + 1d4\"");
        }

        if (char.IsAsciiLetterOrDigit(Current) || Current == '(')
        {
            return Error("terms must be joined with + or -", "e.g. \"2d6 + 3\"");
        }

        if (Current == ')')
        {
            return Error("there is a \")\" without a matching \"(\"", Example);
        }

        return Error($"unexpected \"{Current}\"", Example);
    }

    private static readonly string[] ModifierPrefixes = ["!", "kh", "kl", "dh", "dl", "k", "ro", "r", "min", "max", "cs", "cf"];

    private static bool StartsWithModifier(string rest) => ModifierPrefixes.Any(m => rest.StartsWith(m, StringComparison.Ordinal));

    /// <summary>A syntax error: says where reading stopped.</summary>
    private DndInputException Error(string reason, string hint)
    {
        var at = AtEnd ? "at the end" : $"at \"{Truncate(_original[_pos..])}\"";
        return new DndInputException($"Could not read the dice expression \"{_original}\" {at}: {reason}. {Sentence(hint)}");
    }

    /// <summary>Well-formed but refused (a limit, or a modifier that cannot work): the position would only distract.</summary>
    private DndInputException Invalid(string reason, string hint) =>
        new($"The dice expression \"{_original}\" cannot be used: {reason}. {Sentence(hint)}");

    private static string Sentence(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0 ? string.Empty : char.ToUpperInvariant(trimmed[0]) + trimmed[1..] + (trimmed.EndsWith('.') ? "" : ".");
    }

    private string Slice(int start) => _text[start.._pos];

    private string Slice(int start, int end) => _text[start..Math.Max(start, end)];

    private static string Symbol(DiceComparison comparison) => new DiceCondition(comparison, 0).Operator;

    private static string Truncate(string text) => text.Length <= 20 ? text : text[..20] + "…";

    /// <summary>
    /// Static interval bounds of every node, checked against <see cref="DiceLimits.MaxMagnitude"/> as they are built.
    /// Each child is already within the limit, so every intermediate here fits in a long (at most 10^12 × 10^6).
    /// </summary>
    private (long Min, long Max) Bounds(DiceNode node)
    {
        var (min, max) = node switch
        {
            ConstantNode c => (c.Value, c.Value),
            DiceGroupNode g => GroupBounds(g.Group),
            NegateNode n => Negate(Bounds(n.Operand)),
            GroupingNode p => Bounds(p.Inner),
            SumNode s => Add(Bounds(s.Left), s.Subtract ? Negate(Bounds(s.Right)) : Bounds(s.Right)),
            ScaleNode { Divide: true } d => Map(Bounds(d.Operand), v => DiceMath.FloorDivide(v, d.Factor)),
            ScaleNode m => Map(Bounds(m.Operand), v => v * m.Factor),
            _ => throw new InvalidOperationException($"Unknown dice node {node.GetType().Name}."),
        };

        if (Math.Max(Math.Abs(min), Math.Abs(max)) > DiceLimits.MaxMagnitude)
        {
            throw new DndInputException(
                string.Create(CultureInfo.InvariantCulture,
                    $"The dice expression \"{_original}\" cannot be used: it can reach {(Math.Abs(max) >= Math.Abs(min) ? max : min)}, beyond the supported ±10^12. Use smaller multipliers or fewer dice."));
        }

        return (min, max);

        static (long, long) Negate((long Min, long Max) b) => (-b.Max, -b.Min);
        static (long, long) Add((long Min, long Max) a, (long Min, long Max) b) => (a.Min + b.Min, a.Max + b.Max);
        static (long, long) Map((long Min, long Max) b, Func<long, long> f) => (Math.Min(f(b.Min), f(b.Max)), Math.Max(f(b.Min), f(b.Max)));
    }

    private static (long Min, long Max) GroupBounds(DiceGroup g)
    {
        var chainLength = DiceLimits.MaxExplosionsPerDie + 1L;
        var separateDice = g.Explode is { Kind: ExplodeKind.Standard or ExplodeKind.Penetrating };
        var largestPool = separateDice ? g.Count * chainLength : g.Count;

        // KeptOf never shrinks as the pool grows, so the smallest and largest pools bound the dice that count.
        var poolMax = g.Keep?.KeptOf(largestPool) ?? largestPool;
        var poolMin = g.Keep?.KeptOf(g.Count) ?? g.Count;

        if (g.CountsSuccesses)
        {
            return (g.Failure is null ? 0 : -poolMax, poolMax);
        }

        // A penetrating follow-on die can show 0 (a 1, minus one).
        var dieMin = g.Clamp(g.Explode?.Kind == ExplodeKind.Penetrating ? 0 : 1);
        var dieMax = g.Clamp(g.Explode?.Kind == ExplodeKind.Compound ? g.Sides * chainLength : g.Sides);
        return (dieMin * poolMin, dieMax * poolMax);
    }
}
