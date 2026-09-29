using System.Globalization;
using System.Text.RegularExpressions;
using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation;

namespace DndMcp.Repository.Srd.Combatants;

/// <summary>
/// Reads the facts the structured data leaves out of a stat block's prose: where the "Hit:" part starts, each damage
/// roll the text names and what surrounds it, save clauses, areas, target counts, conditions and how long they last.
///
/// <para>
/// <b>Why prose at all.</b> The structured data cannot say what a damage entry MEANS: 64 attacks in 2014 and 148 in
/// 2024 list several entries, and some of those are riders behind a save, damage a charge adds, a swarm's weaker roll
/// while bloodied, or damage every 10 minutes of a curse. Summing them overstates the monster; dropping them
/// understates it. Only the sentence around each roll tells which it is, so every roll is read in its sentence and the
/// data is kept as the cross-check.
/// </para>
/// <para>
/// Every pattern is written against the vendored text of BOTH editions (2014 "Melee Weapon Attack: +9 to hit", "be
/// knocked prone"; 2024 "Melee Attack Roll: +9", "has the Prone condition"). Anything a pattern misses surfaces as a
/// warning on the stat block, never as a silently different number; the normalizer's tests pin the warning counts, so
/// a changed pattern is a visible diff.
/// </para>
/// </summary>
internal static partial class ProseText
{
    private static readonly string[] AbilityNames = ["strength", "dexterity", "constitution", "intelligence", "wisdom", "charisma"];

    private static readonly Dictionary<string, int> Numbers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8,
        ["nine"] = 9, ["ten"] = 10, ["a"] = 1, ["an"] = 1,
    };

    /// <summary>
    /// Typographic apostrophes and dashes to ASCII, "5-footwide" to "5-foot-wide", runs of spaces to one. The 2024 text
    /// mixes ’ and ' and uses U+2212 for minus signs; a pattern written for one would miss the other.
    /// </summary>
    public static string Normalize(string text)
    {
        var s = text.Replace('’', '\'').Replace('‘', '\'').Replace('−', '-').Replace('–', '-')
            .Replace("—", " — ");
        s = FootWide().Replace(s, "-foot-wide");
        return Spaces().Replace(s, " ").Trim();
    }

    /// <summary>"Adult Red Dragon" → "dragon": the last word, which is how the prose names the creature.</summary>
    public static string ShortName(string name)
    {
        var bare = Parenthetical().Replace(name, string.Empty).Trim();
        var words = bare.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? name.ToLowerInvariant() : words[^1].ToLowerInvariant();
    }

    /// <summary>A name without its parenthetical qualifiers: "Bite (Bat or Vampire Form Only)" → "Bite".</summary>
    public static string StripParentheticals(string name) => Spaces().Replace(Parenthetical().Replace(name, " "), " ").Trim();

    /// <summary>2014 "Wing Attack (Costs 2 Actions)" → ("Wing Attack", 2); anything else → (name, 1).</summary>
    public static (string Name, int Cost) LegendaryCost(string name)
    {
        var match = CostsActions().Match(name);
        return match.Success
            ? (name.Remove(match.Index, match.Length).Trim(), int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture))
            : (name, 1);
    }

    /// <summary>The ability key for "Dexterity", "DEX", "dex"; null when it is not an ability.</summary>
    public static string? AbilityKey(string word)
    {
        var lower = word.Trim().ToLowerInvariant();
        var full = Array.IndexOf(AbilityNames, lower);
        if (full >= 0)
        {
            return DslValues.Abilities.All[full];
        }

        return DslValues.Abilities.All.Contains(lower) ? lower : null;
    }

    /// <summary>"two" → 2, "3" → 3; null otherwise.</summary>
    public static int? NumberWord(string word) =>
        int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : Numbers.TryGetValue(word, out var w) ? w : null;

    /// <summary>A damage-type word ("Fire", "fire") as a DSL damage type, or null.</summary>
    public static string? DamageType(string? word)
    {
        if (word is null)
        {
            return null;
        }

        var lower = word.Trim().ToLowerInvariant();
        return DslValues.DamageTypes.Set.Values.Contains(lower) ? lower : null;
    }

    /// <summary>
    /// A monster dice string ("2d10+8", "19d12 + 133", "1d4-1", flat "1") as a formula. Built with
    /// <see cref="DamageFormula.Of"/> rather than the DSL parser: a tarrasque's 33d20+330 hit points is past the DSL's
    /// per-formula limits, which exist to bound user input, not the SRD.
    /// </summary>
    public static DamageFormula? Dice(string text)
    {
        var compact = text.Replace(" ", string.Empty, StringComparison.Ordinal);
        var match = DiceText().Match(compact);
        if (!match.Success)
        {
            return null;
        }

        var flat = 0;
        if (match.Groups["flat"].Success)
        {
            flat = int.Parse(match.Groups["flat"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        }

        if (!match.Groups["count"].Success)
        {
            return DamageFormula.Constant(flat);
        }

        var count = int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture);
        var sides = int.Parse(match.Groups["sides"].Value, CultureInfo.InvariantCulture);
        return DamageFormula.Of([new DiceTerm(count, sides)], flat);
    }

    /// <summary>The attack header: "Melee Weapon Attack: +9 to hit", "Ranged Spell Attack: +7", "Melee or Ranged Attack Roll: +5".</summary>
    public static AttackHeader? ReadAttackHeader(string text)
    {
        var match = AttackHeaderPattern().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var how = match.Groups["how"].Value.ToLowerInvariant();
        var bonus = int.Parse(match.Groups["bonus"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var kind = match.Groups["kind"].Value.ToLowerInvariant();
        return new AttackHeader(
            kind.StartsWith("ranged", StringComparison.Ordinal) ? StatBlockValues.AttackRanges.Ranged : StatBlockValues.AttackRanges.Melee,
            kind.Contains(" or ", StringComparison.Ordinal),
            how.Contains("spell", StringComparison.Ordinal),
            bonus);
    }

    /// <summary>The text after "Hit:" up to "Miss:" / "Hit or Miss:", or null when there is no "Hit:".</summary>
    public static string? HitText(string text)
    {
        var hit = HitMarker().Match(text);
        if (!hit.Success)
        {
            return null;
        }

        var rest = text[(hit.Index + hit.Length)..];
        var end = MissMarker().Match(rest);
        return (end.Success ? rest[..end.Index] : rest).Trim();
    }

    /// <summary>Every "N (XdY + Z) Type damage" and flat "N Type damage" in order, with where it sits.</summary>
    public static IReadOnlyList<DamageMention> DamageMentions(string text)
    {
        var mentions = new List<DamageMention>();
        foreach (Match m in DiceDamage().Matches(text))
        {
            var dice = Dice(m.Groups["dice"].Value);
            if (dice is null)
            {
                continue;
            }

            var typeWord = m.Groups["type"].Success ? m.Groups["type"].Value : null;
            var type = DamageType(typeWord);
            var alternative = m.Groups["alt"].Success ? DamageType(m.Groups["alt"].Value) : null;
            if (typeWord is not null && type is null)
            {
                // "10 (3d6) Psychic damage" matched; "28 (8d6) damage of the type chosen" has no type word at all.
                continue;
            }

            mentions.Add(new DamageMention(m.Index, m.Length, dice, type, alternative, int.Parse(m.Groups["avg"].Value, CultureInfo.InvariantCulture)));
        }

        foreach (Match m in FlatDamage().Matches(text))
        {
            if (mentions.Any(x => m.Index >= x.Index && m.Index < x.Index + x.Length))
            {
                continue;
            }

            var value = int.Parse(m.Groups["flat"].Value, CultureInfo.InvariantCulture);
            mentions.Add(new DamageMention(m.Index, m.Length, DamageFormula.Constant(value), DamageType(m.Groups["type"].Value), null, value));
        }

        mentions.Sort((a, b) => a.Index.CompareTo(b.Index));
        return mentions;
    }

    /// <summary>The start of the sentence holding <paramref name="index"/>.</summary>
    public static int SentenceStart(string text, int index)
    {
        for (var i = Math.Min(index, text.Length) - 1; i > 0; i--)
        {
            if (IsSentenceEnd(text, i))
            {
                return i + 1;
            }
        }

        return 0;
    }

    /// <summary>The end (exclusive) of the sentence holding <paramref name="index"/>.</summary>
    public static int SentenceEnd(string text, int index)
    {
        for (var i = index; i < text.Length; i++)
        {
            if (IsSentenceEnd(text, i))
            {
                return i + 1;
            }
        }

        return text.Length;
    }

    // A full stop that ends a sentence: the end of the text, or followed by a space and a capital or digit. The "ft." of
    // "within 30 ft. of it" is followed by lower case and so is not an end; "reach 10 ft. Hit:" is, as it should be.
    private static bool IsSentenceEnd(string text, int i) =>
        text[i] == '.' &&
        (i + 1 >= text.Length || (text[i + 1] == ' ' && i + 2 < text.Length && (char.IsUpper(text[i + 2]) || char.IsDigit(text[i + 2]))));

    /// <summary>
    /// A save clause: 2014 "a DC 13 Constitution saving throw", 2024 "Constitution Saving Throw: DC 21". Null when
    /// the text has none.
    /// </summary>
    public static SaveClause? ReadSave(string text, int from = 0)
    {
        Match? best = null;
        foreach (var pattern in new[] { SaveClause2014(), SaveClause2024() })
        {
            var match = pattern.Match(text, from);
            if (match.Success && (best is null || match.Index < best.Index))
            {
                best = match;
            }
        }

        if (best is null)
        {
            return null;
        }

        var ability = AbilityKey(best.Groups["ability"].Value);
        return ability is null
            ? null
            : new SaveClause(best.Index, best.Length, ability, int.Parse(best.Groups["dc"].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>Whether the text says a success halves the damage ("half as much damage", "Success: Half damage", "takes half").</summary>
    public static bool SaysHalf(string text) => HalfDamage().IsMatch(text);

    /// <summary>
    /// The area a save effect covers, from either edition's phrasing: "60-foot cone", "a 30-foot-long, 5-foot-wide
    /// Line", "line that is 30 ft. long", "20-foot-radius Sphere", "10-foot-radius, 40-foot-high Cylinder", "15-foot
    /// Emanation", "within 10 ft. of the dragon" (an emanation). Null when the text names none.
    /// </summary>
    public static AreaSpec? ReadArea(string text)
    {
        foreach (var (pattern, shape) in AreaPatterns)
        {
            var match = pattern.Match(text);
            if (match.Success)
            {
                return new AreaSpec(shape, int.Parse(match.Groups["size"].Value, CultureInfo.InvariantCulture));
            }
        }

        return null;
    }

    private static readonly (Regex Pattern, string Shape)[] AreaPatterns =
    [
        (LineArea2024(), StatBlockValues.Shapes.Line),
        (LineArea2014(), StatBlockValues.Shapes.Line),
        (LineAreaLong(), StatBlockValues.Shapes.Line),
        (CylinderArea(), StatBlockValues.Shapes.Cylinder),
        (SphereArea(), StatBlockValues.Shapes.Sphere),
        (RadiusArea(), StatBlockValues.Shapes.Sphere),
        (ConeArea(), StatBlockValues.Shapes.Cone),
        (CubeArea(), StatBlockValues.Shapes.Cube),
        (EmanationArea(), StatBlockValues.Shapes.Emanation),
        (PointArea(), StatBlockValues.Shapes.Sphere),
        (WithinArea(), StatBlockValues.Shapes.Emanation),
    ];

    /// <summary>"up to three creatures", "three bolts of lightning, each of which can strike a target", "one creature": how many it names, or null.</summary>
    public static int? ReadTargetCount(string text)
    {
        var match = TargetCount().Match(text);
        return match.Success ? NumberWord(match.Groups["n"].Value) : null;
    }

    /// <summary>"If the target is a Large or smaller creature" / "one Medium or smaller creature" → "Large"/"Medium", or null.</summary>
    public static string? ReadMaxSize(string text)
    {
        var match = MaxSizePattern().Match(text);
        return match.Success ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(match.Groups["size"].Value.ToLowerInvariant()) : null;
    }

    /// <summary>The "(escape DC N)" of a grapple, or null.</summary>
    public static int? ReadEscapeDc(string text)
    {
        var match = EscapeDc().Match(text);
        return match.Success ? int.Parse(match.Groups["dc"].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// Every condition an effect clause imposes, in order: 2024 "has the Blinded and Restrained conditions", 2014 "is
    /// grappled", "be knocked prone", "become frightened". Targeting text ("one creature Grappled by the kraken") must
    /// not be passed in: the caller hands over only the Hit or Failure part.
    /// </summary>
    public static IReadOnlyList<ConditionMention> ConditionMentions(string text)
    {
        var found = new List<ConditionMention>();
        // A condition named inside a test ("if the target is Grappled by the mimic", "unless the creature is
        // incapacitated", "one creature that is grappled") is a state the text checks, not one it imposes.
        bool IsTest(Match m) => StateTest().IsMatch(text[Math.Max(0, m.Index - 60)..m.Index]);
        foreach (Match m in HasConditions().Matches(text))
        {
            if (IsTest(m))
            {
                continue;
            }

            foreach (var word in ListSeparator().Split(m.Groups["list"].Value))
            {
                if (ConditionKey(word) is { } key)
                {
                    found.Add(new ConditionMention(m.Index, m.Length, key));
                }
            }
        }

        foreach (Match m in IsCondition().Matches(text))
        {
            if (IsTest(m))
            {
                continue;
            }

            foreach (var word in ListSeparator().Split(m.Groups["list"].Value))
            {
                if (ConditionKey(word) is { } key && !found.Any(f => f.Condition == key && Math.Abs(f.Index - m.Index) < 3))
                {
                    found.Add(new ConditionMention(m.Index, m.Length, key));
                }
            }
        }

        foreach (Match m in GainsExhaustion().Matches(text))
        {
            found.Add(new ConditionMention(m.Index, m.Length, StatBlockValues.Conditions.Exhaustion));
        }

        found.Sort((a, b) => a.Index.CompareTo(b.Index));
        return found.DistinctBy(f => f.Condition).ToList();
    }

    /// <summary>A condition word as a <see cref="StatBlockValues.Conditions"/> key, or null.</summary>
    public static string? ConditionKey(string word)
    {
        var lower = word.Trim().ToLowerInvariant();
        return StatBlockValues.Conditions.All.Contains(lower) ? lower : null;
    }

    /// <summary>
    /// How long a condition mentioned at <paramref name="at"/> lasts, from the rest of its sentence and the clause's
    /// repeat-the-save sentence. Null when the text says nothing about it (the caller picks the condition's default).
    /// </summary>
    public static DurationReading? ReadDuration(string clause, int at, SaveSpec? save)
    {
        var sentenceEnd = SentenceEnd(clause, at);
        var sentence = clause[at..sentenceEnd];
        var whole = clause;
        var repeats = RepeatSave().IsMatch(whole);
        int? rounds = OneMinute().IsMatch(sentence) || AfterOneMinute().IsMatch(whole) ? 10 : null;

        if (UntilGrappleEnds().IsMatch(sentence) || UntilGrappleEnds().IsMatch(clause[SentenceStart(clause, at)..at]))
        {
            return new DurationReading(StatBlockValues.Durations.UntilEscape, null, false);
        }

        var until = UntilTurn().Match(sentence);
        if (until.Success)
        {
            var whose = until.Groups["whose"].Value.ToLowerInvariant();
            var targetTurn = whose is "its" or "the target's" or "the creature's" or "that creature's" or "the target's own";
            var start = until.Groups["edge"].Value.Equals("start", StringComparison.OrdinalIgnoreCase);
            var duration = (targetTurn, start) switch
            {
                (true, true) => StatBlockValues.Durations.UntilStartOfTargetTurn,
                (true, false) => StatBlockValues.Durations.UntilEndOfTargetTurn,
                (false, true) => StatBlockValues.Durations.UntilStartOfSourceTurn,
                (false, false) => StatBlockValues.Durations.UntilEndOfSourceTurn,
            };
            return new DurationReading(duration, null, false);
        }

        if (save is not null && RepeatNextTurn().IsMatch(whole))
        {
            // Basilisk gaze, gorgon breath: restrained until the end of the target's next turn, when a second
            // failure petrifies it (the caller warns that the second stage is not simulated).
            return new DurationReading(StatBlockValues.Durations.UntilEndOfTargetTurn, null, false);
        }

        if (repeats && save is not null)
        {
            return new DurationReading(StatBlockValues.Durations.SaveEnds, rounds, true);
        }

        if (rounds is not null)
        {
            return new DurationReading(StatBlockValues.Durations.Rounds, rounds, false);
        }

        if (LongDuration().IsMatch(sentence))
        {
            return new DurationReading(StatBlockValues.Durations.Fight, null, false);
        }

        return null;
    }

    /// <summary>"The target regains 11 (2d8 + 2) hit points" → the dice; null when it heals nothing.</summary>
    public static DamageFormula? ReadHealing(string text)
    {
        var match = Regains().Match(text);
        return match.Success ? Dice(match.Groups["dice"].Value) : null;
    }

    /// <summary>
    /// "makes one Tail attack", "makes one claw attack or tail attack", "uses Lightning Strike", "uses Spellcasting to
    /// cast Scorching Ray (level 3 version)", "casts Fear": the names another action or spell is used by, in order.
    /// </summary>
    public static IReadOnlyList<string> UsedNames(string text)
    {
        var names = new List<string>();
        var casts = CastsSpell().Match(text);
        if (casts.Success)
        {
            names.Add(casts.Groups["spell"].Value.Trim());
            return names;
        }

        foreach (Match m in MakesAttack().Matches(text))
        {
            foreach (var part in OrSplit().Split(m.Groups["what"].Value))
            {
                var name = AttackSuffix().Replace(part, string.Empty).Trim();
                if (name.Length > 0 && !name.Equals("attack", StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }
        }

        foreach (Match m in AttackWithIts().Matches(text))
        {
            names.Add(m.Groups["what"].Value.Trim());
        }

        foreach (Match m in UsesAction().Matches(text))
        {
            foreach (var part in OrSplit().Split(m.Groups["what"].Value))
            {
                var name = part.Trim();
                if (name.Length > 0)
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    /// <summary>"(level 3 version)" after a spell name → 3.</summary>
    public static int? VersionLevel(string text)
    {
        var match = LevelVersion().Match(text);
        return match.Success ? int.Parse(match.Groups["level"].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>The 2024 legendary restriction "can't take this action again until the start of its next turn".</summary>
    public static bool IsOncePerRound(string text) => OncePerRoundPattern().IsMatch(text);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"-foot-?\s?wide|-footwide")]
    private static partial Regex FootWide();

    [GeneratedRegex(@"\s*\([^)]*\)")]
    private static partial Regex Parenthetical();

    [GeneratedRegex(@"\s*\(Costs (?<n>\d) Actions\)", RegexOptions.IgnoreCase)]
    private static partial Regex CostsActions();

    [GeneratedRegex(@"^(?:(?<count>\d+)d(?<sides>\d+)(?<flat>[+-]\d+)?|(?<flat>-?\d+))$")]
    private static partial Regex DiceText();

    [GeneratedRegex(@"(?<kind>Melee or Ranged|Melee|Ranged)\s+(?<how>Weapon Attack|Spell Attack|Attack Roll)\s*:\s*(?<bonus>[+-]\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex AttackHeaderPattern();

    [GeneratedRegex(@"(?<![a-z] )\bHit\s*:")]
    private static partial Regex HitMarker();

    [GeneratedRegex(@"\b(?:Hit or Miss|Miss|Failure or Success)\s*:")]
    private static partial Regex MissMarker();

    [GeneratedRegex(@"(?<avg>\d+)\s*\((?<dice>\d+d\d+(?:\s*[+-]\s*\d+)?)\)\s+(?:(?<type>[A-Za-z]+)(?:\s+or\s+(?<alt>[A-Za-z]+))?\s+)?damage\b")]
    private static partial Regex DiceDamage();

    [GeneratedRegex(@"(?<![\d(])\b(?<flat>\d+)\s+(?<type>acid|bludgeoning|cold|fire|force|lightning|necrotic|piercing|poison|psychic|radiant|slashing|thunder)\s+damage\b", RegexOptions.IgnoreCase)]
    private static partial Regex FlatDamage();

    [GeneratedRegex(@"\bDC\s+(?<dc>\d+)\s+(?<ability>Strength|Dexterity|Constitution|Intelligence|Wisdom|Charisma)\s+saving throw", RegexOptions.IgnoreCase)]
    private static partial Regex SaveClause2014();

    [GeneratedRegex(@"(?<ability>Strength|Dexterity|Constitution|Intelligence|Wisdom|Charisma)\s+Saving Throw\s*:\s*DC\s+(?<dc>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SaveClause2024();

    [GeneratedRegex(@"half as much damage|Success\s*:\s*Half damage|takes? half (?:the|as much)|half damage", RegexOptions.IgnoreCase)]
    private static partial Regex HalfDamage();

    [GeneratedRegex(@"(?<size>\d+)-foot-long(?:,\s*\d+-foot-wide)?\s+Line", RegexOptions.IgnoreCase)]
    private static partial Regex LineArea2024();

    [GeneratedRegex(@"(?<size>\d+)-foot line", RegexOptions.IgnoreCase)]
    private static partial Regex LineArea2014();

    [GeneratedRegex(@"line (?:of [a-z]+ )?that is (?<size>\d+) (?:ft\.|feet) long", RegexOptions.IgnoreCase)]
    private static partial Regex LineAreaLong();

    [GeneratedRegex(@"(?<size>\d+)-foot-radius,?\s+\d+-foot-(?:high|tall)\s+Cylinder", RegexOptions.IgnoreCase)]
    private static partial Regex CylinderArea();

    [GeneratedRegex(@"(?<size>\d+)-foot-radius\s+Sphere", RegexOptions.IgnoreCase)]
    private static partial Regex SphereArea();

    [GeneratedRegex(@"(?<size>\d+)-foot[- ]radius(?! Sphere)", RegexOptions.IgnoreCase)]
    private static partial Regex RadiusArea();

    [GeneratedRegex(@"(?<size>\d+)-foot Cone", RegexOptions.IgnoreCase)]
    private static partial Regex ConeArea();

    [GeneratedRegex(@"(?<size>\d+)-foot Cube", RegexOptions.IgnoreCase)]
    private static partial Regex CubeArea();

    [GeneratedRegex(@"(?<size>\d+)-foot Emanation", RegexOptions.IgnoreCase)]
    private static partial Regex EmanationArea();

    [GeneratedRegex(@"(?:Each|each|every) (?:non-undead |living )?creature(?: of (?:its|the [a-z]+'s) choice)?(?: other than [a-z ]+?)?(?: that is)? within (?<size>\d+) (?:ft\.|feet) of (?:the [a-z ]+|it|itself)\b|each creature within (?<size>\d+) feet of the space it left", RegexOptions.IgnoreCase)]
    private static partial Regex WithinArea();

    [GeneratedRegex(@"within (?<size>\d+) (?:ft\.|feet) of (?:that|a|the) point", RegexOptions.IgnoreCase)]
    private static partial Regex PointArea();

    [GeneratedRegex(@"\bup to (?<n>two|three|four|five|six|\d+) (?:creatures|targets)(?! swallowed| at a time| Grappled)|\b(?<n>two|three|four|five|six) (?:bolts|rays|beams|darts)[^.]*?each", RegexOptions.IgnoreCase)]
    private static partial Regex TargetCount();

    [GeneratedRegex(@"\b(?<size>Tiny|Small|Medium|Large|Huge|Gargantuan) or smaller\b", RegexOptions.IgnoreCase)]
    private static partial Regex MaxSizePattern();

    [GeneratedRegex(@"escape DC (?<dc>\d+)|\bDC (?<dc>\d+) Strength (?:\(Athletics\) )?check", RegexOptions.IgnoreCase)]
    private static partial Regex EscapeDc();

    [GeneratedRegex(@"\b(?:has|have|gains?) the (?<list>[A-Z][a-z]+(?:(?:,\s*|\s+and\s+|,\s*and\s+)[A-Z][a-z]+)*) conditions?\b")]
    private static partial Regex HasConditions();

    [GeneratedRegex(@"\b(?:is|be|become|becomes|are|falls|fall)\s+(?:also\s+)?(?:magically\s+)?(?:knocked\s+)?(?<list>(?:blinded|charmed|deafened|frightened|grappled|incapacitated|paralyzed|petrified|poisoned|prone|restrained|stunned|unconscious)(?:(?:,\s*|\s+and\s+|,\s*and\s+)(?:blinded|charmed|deafened|frightened|grappled|incapacitated|paralyzed|petrified|poisoned|prone|restrained|stunned|unconscious))*)\b|\bknocked (?<list>prone)\b", RegexOptions.IgnoreCase)]
    private static partial Regex IsCondition();

    /// <summary>
    /// The 2024 Rules Glossary's Burning hazard ("A burning creature or object takes 1d4 Fire damage at the start of
    /// each of its turns"), as a warning names it: a stat block that sets a creature burning gives no damage of its own.
    /// </summary>
    public const string BurningHazard = "Burning: 1d4 Fire damage at the start of each of its turns until the fire is put out.";

    /// <summary>
    /// A 2024 text that sets a creature burning: the Burn and Touch hits ("If the target is a creature or a flammable
    /// object, it starts burning") and the fire elemental's Fire Aura ("Creatures and flammable objects in the
    /// Emanation start burning"). Hurl Flame's "If the target is a flammable object …, it starts burning" names no
    /// creature and does not match: an object burning changes no fight.
    /// </summary>
    [GeneratedRegex(@"\bcreatures?\b[^.]*\bstarts? burning\b", RegexOptions.IgnoreCase)]
    internal static partial Regex StartsBurning();

    /// <summary>The separators of a written list ("a, b, and c", "a and b"): conditions here, damage types in <see cref="DamageAdjustmentReader"/>.</summary>
    [GeneratedRegex(@",\s*and\s+|\s+and\s+|,\s*")]
    internal static partial Regex ListSeparator();

    [GeneratedRegex(@"\b(?:if|unless|while|when|whether|that|who|which|until)\b[^,.;:\u2014]*$", RegexOptions.IgnoreCase)]
    private static partial Regex StateTest();

    [GeneratedRegex(@"gains? (?:1|one) (?:level of exhaustion|Exhaustion level)", RegexOptions.IgnoreCase)]
    private static partial Regex GainsExhaustion();

    [GeneratedRegex(@"repeats? the (?:saving throw|save) at the end of each of its turns|At the end of each of its turns, the target repeats the save|repeat the saving throw at the end of each of its turns", RegexOptions.IgnoreCase)]
    private static partial Regex RepeatSave();

    [GeneratedRegex(@"repeats? the (?:saving throw|save) at the end of its next turn|must repeat the saving throw at the end of its next turn", RegexOptions.IgnoreCase)]
    private static partial Regex RepeatNextTurn();

    [GeneratedRegex(@"\bfor (?:1|one) minute\b", RegexOptions.IgnoreCase)]
    private static partial Regex OneMinute();

    [GeneratedRegex(@"After 1 minute, it succeeds automatically", RegexOptions.IgnoreCase)]
    private static partial Regex AfterOneMinute();

    [GeneratedRegex(@"until (?:this|the) grapple ends|until the grapple ends", RegexOptions.IgnoreCase)]
    private static partial Regex UntilGrappleEnds();

    [GeneratedRegex(@"until the (?<edge>start|end) of (?<whose>its|the target's|the creature's|that creature's|the [a-z ]+?'s) next turn", RegexOptions.IgnoreCase)]
    private static partial Regex UntilTurn();

    [GeneratedRegex(@"\bfor (?:1|one|\d+) (?:hour|hours|day|days)\b|\buntil (?:it is removed|the web is destroyed|the rope is destroyed|magic such as|the [a-z]+ dies)", RegexOptions.IgnoreCase)]
    private static partial Regex LongDuration();

    [GeneratedRegex(@"regains? (?<avg>\d+) \((?<dice>\d+d\d+(?:\s*[+-]\s*\d+)?)\) (?:hit points|Hit Points)", RegexOptions.IgnoreCase)]
    private static partial Regex Regains();

    [GeneratedRegex(@"(?:uses Spellcasting to cast|casts|to cast)\s+(?<spell>[A-Z][A-Za-z' ]+?)(?=\s*\(|[,.]|\s+on\s|\s+using\s|\s+requiring\s|$)")]
    private static partial Regex CastsSpell();

    [GeneratedRegex(@"makes (?:one|a) (?<what>[A-Za-z' ]+?(?:attack|strike)(?:\s+or\s+[A-Za-z' ]+?(?:attack|strike))?)(?=[.,]|\s+with|\s+or\s+uses|$)", RegexOptions.IgnoreCase)]
    private static partial Regex MakesAttack();

    [GeneratedRegex(@"\buses (?:its |either )?(?<what>[A-Z][A-Za-z' ]+?(?:\s+or\s+[A-Z][A-Za-z' ]+?)?)(?=[.,]|\s+and\s|\s+if\s|$)")]
    private static partial Regex UsesAction();

    [GeneratedRegex(@"\s+or\s+")]
    private static partial Regex OrSplit();

    [GeneratedRegex(@"makes one attack with its (?<what>[A-Za-z' ]+?)(?=\s+or\s|[.,]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex AttackWithIts();

    [GeneratedRegex(@"\s*\battack\b\s*$|^with its\s+|\s+attack$", RegexOptions.IgnoreCase)]
    private static partial Regex AttackSuffix();

    [GeneratedRegex(@"\(level (?<level>\d) version\)", RegexOptions.IgnoreCase)]
    private static partial Regex LevelVersion();

    [GeneratedRegex(@"can't take this action again until the start of its next turn", RegexOptions.IgnoreCase)]
    private static partial Regex OncePerRoundPattern();
}

/// <summary>What an attack's first line says.</summary>
/// <param name="Range">Melee for "Melee" and "Melee or Ranged"; ranged for "Ranged".</param>
internal sealed record AttackHeader(string Range, bool AlsoRanged, bool IsSpell, int Bonus);

/// <summary>One damage roll named in prose.</summary>
/// <param name="AlternativeType">"lightning or thunder damage": the second type (the first is used).</param>
internal sealed record DamageMention(int Index, int Length, DamageFormula Dice, string? Type, string? AlternativeType, int Average)
{
    public int End => Index + Length;
}

/// <summary>A save clause's position, ability key and DC.</summary>
internal sealed record SaveClause(int Index, int Length, string Ability, int Dc)
{
    public int End => Index + Length;
}

/// <summary>A condition named in an effect clause.</summary>
internal sealed record ConditionMention(int Index, int Length, string Condition);

/// <summary>A duration read from prose; <see cref="Repeats"/> when a repeated save ends it.</summary>
internal sealed record DurationReading(string Duration, int? Rounds, bool Repeats);
