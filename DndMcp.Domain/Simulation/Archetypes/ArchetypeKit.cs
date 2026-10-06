using System.Globalization;
using System.Text.Json;
using DndMcp.Domain.Characters;
using DndMcp.Domain.Features;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Simulation.Archetypes;

/// <summary>
/// Step values for the archetype builds, made as the <see cref="JsonElement"/>s a model's JSON binds to.
///
/// <para>
/// <b>Why JsonElements and not plain C# values:</b> <see cref="LevelValue.ToElement"/> would serialise an int or a
/// dictionary itself, but anything that reads the spec later (the simulator's echo, a stored sim profile, a test that
/// prints a build) should see exactly what it would see from the tool. An archetype is then indistinguishable from a
/// build the model wrote, and goes through the same validation.
/// </para>
/// <para>
/// <see cref="ByLevel(int, int, Func{int, int})"/> writes a key only where the value changes, so a table the rules give
/// per level (Second Wind's 1d10 + level, Sneak Attack's dice) becomes the shortest step map that means the same.
/// </para>
/// </summary>
internal static class Lv
{
    public static JsonElement Of(int value) => JsonSerializer.SerializeToElement(value, DslJson.Options);

    public static JsonElement Of(string value) => JsonSerializer.SerializeToElement(value, DslJson.Options);

    public static JsonElement Steps(params (int Level, int Value)[] steps) =>
        Serialize(steps.Select(s => (s.Level, (object)s.Value)));

    public static JsonElement Steps(params (int Level, string Value)[] steps) =>
        Serialize(steps.Select(s => (s.Level, (object)s.Value)));

    /// <summary>A step map over levels [from, until] of an integer the rules give per level.</summary>
    public static JsonElement ByLevel(int from, int until, Func<int, int> value) =>
        Serialize(Changes(from, until, level => (object)value(level)));

    /// <summary>A step map over levels [from, until] of a dice string the rules give per level.</summary>
    public static JsonElement ByLevel(int from, int until, Func<int, string> value) =>
        Serialize(Changes(from, until, level => (object)value(level)));

    private static IEnumerable<(int Level, object Value)> Changes(int from, int until, Func<int, object> value)
    {
        object? previous = null;
        for (var level = from; level <= until; level++)
        {
            var current = value(level);
            if (!current.Equals(previous))
            {
                yield return (level, current);
                previous = current;
            }
        }
    }

    private static JsonElement Serialize(IEnumerable<(int Level, object Value)> steps)
    {
        var map = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (level, value) in steps)
        {
            map.Add(level.ToString(CultureInfo.InvariantCulture), value);
        }

        return map.Count == 1 && map.ContainsKey("1")
            ? JsonSerializer.SerializeToElement(map["1"], DslJson.Options)
            : JsonSerializer.SerializeToElement(map, DslJson.Options);
    }
}

/// <summary>
/// The ability scores of an archetype at every level 1–20: the standard array, the +2/+1 increase, and the Ability
/// Score Improvements in one fixed order, so every class follows the same stated rule.
///
/// <para>
/// <b>The rule.</b> The standard array (15, 14, 13, 12, 10, 8) is assigned in the class's order (primary, secondary,
/// tertiary, then the rest); the primary gets +2 and the secondary +1 (2014: the race's increase; 2024: the
/// background's, taken as +2/+1 — the 2024 PHB backgrounds cover every pair the classes here need, though the SRD's four
/// do not). ASIs go: first +1 primary and +1 secondary (17/15 → 18/16), then +2 primary (→ 20), then +2 secondary twice
/// (→ 20), then +2 tertiary for any left (fighters and rogues have more). A 2024 character's level 19 feature is an Epic
/// Boon, not an ASI; its +1 would land on a 20 and change no modifier, so it is left out. Capstones that raise scores
/// past 20 (Primal Champion; 2024 Body and Mind) are applied at 20. No feats, species traits or magic items that change
/// scores.
/// </para>
/// <para>
/// A score never changes except at those levels, which is what lets <see cref="ToSpec"/> write each ability as a short
/// step map and lets the HP formula read one Con modifier per level.
/// </para>
/// </summary>
internal sealed class AbilityTrack
{
    /// <summary>The standard array, highest first (both editions).</summary>
    public static readonly IReadOnlyList<int> StandardArray = [15, 14, 13, 12, 10, 8];

    private readonly Dictionary<string, int[]> _scores;

    /// <summary>
    /// The ASI levels of every class but the fighter (which adds 6 and 14) and the rogue (which adds 10): 4, 8, 12, 16 and,
    /// in 2014, 19 (2024's level 19 feature is an Epic Boon).
    /// </summary>
    public static IReadOnlyList<int> StandardAsiLevels(string edition) =>
        edition == V.Editions.E2014 ? [4, 8, 12, 16, 19] : [4, 8, 12, 16];

    private AbilityTrack(IReadOnlyList<string> order, IReadOnlyList<int> asiLevels, Dictionary<string, int[]> scores, string capstone)
    {
        Order = order;
        AsiLevels = asiLevels;
        _scores = scores;
        Capstone = capstone;
    }

    /// <summary>The six abilities in array order: primary, secondary, tertiary, then the rest.</summary>
    public IReadOnlyList<string> Order { get; }

    public IReadOnlyList<int> AsiLevels { get; }

    /// <summary>"; at 20 Str and Con +4 (max 24)" or empty: the capstone as the plan states it.</summary>
    public string Capstone { get; }

    /// <param name="order">Six ability keys: primary, secondary, tertiary, then the ones that get 12, 10 and 8.</param>
    /// <param name="asiLevels">The class's ASI levels in this edition (2024: without the Epic Boon at 19).</param>
    /// <param name="capstoneAbilities">Abilities a level 20 feature raises by 4, or empty.</param>
    /// <param name="capstoneMax">That feature's new maximum (24 for 2014 Primal Champion, 25 in 2024).</param>
    public static AbilityTrack Standard(
        IReadOnlyList<string> order, IReadOnlyList<int> asiLevels, IReadOnlyList<string>? capstoneAbilities = null, int capstoneMax = 20)
    {
        if (order.Count != 6 || order.Distinct().Count() != 6 || order.Any(a => !V.Abilities.All.Contains(a)))
        {
            throw new ArgumentException("The order must name each of the six abilities once.", nameof(order));
        }

        var current = order.Select((ability, i) => (ability, score: StandardArray[i] + (i == 0 ? 2 : i == 1 ? 1 : 0)))
            .ToDictionary(p => p.ability, p => p.score);
        var scores = V.Abilities.All.ToDictionary(a => a, _ => new int[DslLimits.MaxLevel]);
        var asisTaken = 0;
        for (var level = DslLimits.MinLevel; level <= DslLimits.MaxLevel; level++)
        {
            if (asiLevels.Contains(level))
            {
                Improve(current, order, asisTaken++);
            }

            if (level == DslLimits.MaxLevel)
            {
                foreach (var ability in capstoneAbilities ?? [])
                {
                    current[ability] = Math.Min(capstoneMax, current[ability] + 4);
                }
            }

            foreach (var ability in V.Abilities.All)
            {
                scores[ability][level - 1] = current[ability];
            }
        }

        var capstone = capstoneAbilities is { Count: > 0 }
            ? $"; at 20 {string.Join(" and ", capstoneAbilities.Select(V.Abilities.Display))} +4 (max {capstoneMax})"
            : string.Empty;
        return new AbilityTrack(order, asiLevels, scores, capstone);
    }

    public int Score(string ability, int level) => _scores[ability][level - 1];

    public int Modifier(string ability, int level) => DslLimits.AbilityModifier(Score(ability, level));

    public ResolvedAbilities At(int level) => new(
        Score(V.Abilities.Str, level), Score(V.Abilities.Dex, level), Score(V.Abilities.Con, level),
        Score(V.Abilities.Int, level), Score(V.Abilities.Wis, level), Score(V.Abilities.Cha, level));

    /// <summary>The scores as the DSL's abilities: a number when constant, else a step map at the levels it changes.</summary>
    public AbilitiesSpec ToSpec() => new()
    {
        Str = Spec(V.Abilities.Str),
        Dex = Spec(V.Abilities.Dex),
        Con = Spec(V.Abilities.Con),
        Int = Spec(V.Abilities.Int),
        Wis = Spec(V.Abilities.Wis),
        Cha = Spec(V.Abilities.Cha),
    };

    /// <summary>"Str 18, Con 16, Dex 13, Wis 12, Cha 10, Int 8" in array order.</summary>
    public string Describe(int level) =>
        string.Join(", ", Order.Select(a => $"{V.Abilities.Display(a)} {Score(a, level).ToString(CultureInfo.InvariantCulture)}"));

    /// <summary>The allocation rule in one line, for the assumptions.</summary>
    public string Plan
    {
        get
        {
            var names = Order.Select(V.Abilities.Display).ToList();
            var asis = AsiLevels.Count == 0 ? "none" : string.Join(", ", AsiLevels.Select(l => l.ToString(CultureInfo.InvariantCulture)));
            return $"standard array as {names[0]} 15, {names[1]} 14, {names[2]} 13, {names[3]} 12, {names[4]} 10, {names[5]} 8 with +2 " +
                   $"{names[0]} and +1 {names[1]}; ASIs at {asis}: +1 {names[0]} and +1 {names[1]}, then +2 {names[0]}, then +2 " +
                   $"{names[1]} twice, then +2 {names[2]}{Capstone}";
        }
    }

    private object Spec(string ability) => Lv.ByLevel(DslLimits.MinLevel, DslLimits.MaxLevel, level => Score(ability, level));

    private static void Improve(Dictionary<string, int> scores, IReadOnlyList<string> order, int asi)
    {
        var (primary, secondary, tertiary) = (order[0], order[1], order[2]);
        switch (asi)
        {
            case 0:
                Raise(primary, 1);
                Raise(secondary, 1);
                break;
            case 1:
                Raise(primary, 2);
                break;
            case 2 or 3:
                Raise(secondary, 2);
                break;
            default:
                Raise(tertiary, 2);
                break;
        }

        void Raise(string ability, int by) => scores[ability] = Math.Min(20, scores[ability] + by);
    }
}

/// <summary>
/// Hit points by the rule every archetype states: the hit die's maximum at level 1, the fixed average (die ÷ 2 + 1) at
/// each later level, and the Constitution modifier at every level. A Con increase raises the maximum retroactively (1 HP
/// per level per +1, the rule in both editions), so the Con modifier AT the level multiplies the whole level count.
/// </summary>
internal static class ArchetypeHitPoints
{
    /// <summary>
    /// The archetype's maximum: <see cref="LevelHitPoints.FixedTotal"/> for one class, which is the sheet's derivation
    /// too, with no 2024 per-level minimum (an archetype's Con is never low enough for it to matter, and an archetype's HP
    /// must not change with the edition).
    /// </summary>
    public static int At(int hitDie, int level, int conModifier) =>
        LevelHitPoints.FixedTotal([(hitDie, level)], conModifier, minimumOnePerLevel: false);
}

/// <summary>Armour weight categories (training decides which a class may wear).</summary>
internal enum ArmorWeight
{
    Light,
    Medium,
    Heavy,
}

/// <summary>
/// One mundane armour: base AC, the Dex cap (null: full Dex; 0: none), cost and whether it is metal (2014 druids will
/// not wear metal). The SRD numbers, the same in both editions.
/// </summary>
internal sealed record Armor(string Name, int Base, int? DexCap, ArmorWeight Weight, int CostGp, bool Metal)
{
    public static readonly Armor Leather = new("leather", 11, null, ArmorWeight.Light, 10, false);
    public static readonly Armor StuddedLeather = new("studded leather", 12, null, ArmorWeight.Light, 45, true);
    public static readonly Armor Hide = new("hide", 12, 2, ArmorWeight.Medium, 10, false);
    public static readonly Armor ChainShirt = new("chain shirt", 13, 2, ArmorWeight.Medium, 50, true);
    public static readonly Armor ScaleMail = new("scale mail", 14, 2, ArmorWeight.Medium, 50, true);
    public static readonly Armor Breastplate = new("breastplate", 14, 2, ArmorWeight.Medium, 400, true);
    public static readonly Armor HalfPlate = new("half plate", 15, 2, ArmorWeight.Medium, 750, true);
    public static readonly Armor ChainMail = new("chain mail", 16, 0, ArmorWeight.Heavy, 75, true);
    public static readonly Armor Splint = new("splint", 17, 0, ArmorWeight.Heavy, 200, true);
    public static readonly Armor Plate = new("plate", 18, 0, ArmorWeight.Heavy, 1500, true);

    public static readonly IReadOnlyList<Armor> All = [Leather, StuddedLeather, Hide, ChainShirt, ScaleMail, Breastplate, HalfPlate, ChainMail, Splint, Plate];

    public int ArmorClass(int dexModifier) => Base + (DexCap is { } cap ? Math.Min(cap, dexModifier) : dexModifier);
}

/// <summary>
/// How an archetype's AC is chosen at each level: the best of what it can wear and afford, by the DMG 2014 "Starting at
/// Higher Levels" wealth (standard campaign) as the one stated yardstick.
///
/// <para>
/// <b>Tiers.</b> Levels 1–4: the class's starting armour (the edition's starting equipment). Levels 5–10 (about 500 gp):
/// the best armour the class is trained in costing 500 gp or less (splint 200, breastplate 400; not half plate or
/// plate). Levels 11+ (about 5,000 gp): any mundane armour it is trained in. A shield adds 2 when the build uses one hand
/// for it. An armourless formula (Unarmored Defense, Mage Armor, Armor of Shadows) competes at every level. No magic
/// armour, shields or cloaks: the DMG's uncommon item at 11 is the archetype's +1 weapon instead. Ties go to the
/// armourless formula (it costs nothing), then to the cheaper armour.
/// </para>
/// </summary>
internal sealed record ArmorPlan(
    Armor? Start,
    Func<Armor, bool> Trained,
    bool Shield,
    Func<int, AbilityTrack, (int Ac, string Source)?>? Unarmored = null)
{
    /// <summary>The most a level 5–10 character is assumed to spend on armour.</summary>
    public const int TierTwoBudgetGp = 500;

    public (int Ac, string Source) At(int level, AbilityTrack abilities)
    {
        var dex = abilities.Modifier(V.Abilities.Dex, level);
        // Candidates in tie-break order: a free armourless formula first, then armour from the cheapest (a stable sort
        // keeps that order among equal ACs, so scale mail beats a breastplate that gives the same AC).
        var options = new List<(int Ac, string Source)>();
        if (Unarmored?.Invoke(level, abilities) is { } unarmored)
        {
            options.Add(unarmored);
        }

        IEnumerable<Armor> affordable = level switch
        {
            < 5 => Start is null ? [] : [Start],
            < 11 => Armor.All.Where(a => Trained(a) && a.CostGp <= TierTwoBudgetGp),
            _ => Armor.All.Where(Trained),
        };

        options.AddRange(affordable.OrderBy(a => a.CostGp).Select(a => (a.ArmorClass(dex), a.Name)));
        if (options.Count == 0)
        {
            options.Add((10 + dex, "no armour"));
        }

        var best = options.OrderByDescending(o => o.Ac).First();
        return Shield ? (best.Ac + 2, $"{best.Source} + shield") : best;
    }

    public static bool LightOrMedium(Armor armor) => armor.Weight is ArmorWeight.Light or ArmorWeight.Medium;

    public static bool Any(Armor armor) => true;

    public static bool LightOnly(Armor armor) => armor.Weight == ArmorWeight.Light;

    /// <summary>Unarmored Defense: 10 + Dex + <paramref name="second"/> (Con for barbarians, Wis for monks).</summary>
    public static Func<int, AbilityTrack, (int, string)?> UnarmoredDefense(string second) =>
        (level, a) => (10 + a.Modifier(V.Abilities.Dex, level) + a.Modifier(second, level), "Unarmored Defense");

    /// <summary>Mage Armor (13 + Dex) from <paramref name="fromLevel"/>: cast before the day's first fight (8 hours).</summary>
    public static Func<int, AbilityTrack, (int, string)?> MageArmor(int fromLevel = 1, string label = "Mage Armor") =>
        (level, a) => level >= fromLevel ? (13 + a.Modifier(V.Abilities.Dex, level), label) : null;
}

/// <summary>
/// Spell slots by class level for the archetypes, read from <see cref="SpellSlotTables"/> (the SRD class tables, which the
/// character sheet uses too, so an archetype and a sheet of the same class and level have the same slots). The DSL has no
/// shared slot pool, so each spell modifier gets its own uses from these counts, split so no slot is counted twice (see
/// each archetype).
/// </summary>
internal static class SpellSlots
{
    /// <summary>A full caster's slots of <paramref name="slotLevel"/> or higher at <paramref name="classLevel"/>.</summary>
    public static int FullAtOrAbove(int classLevel, int slotLevel) => SpellSlotTables.Full(classLevel).Skip(slotLevel - 1).Sum();

    /// <summary>A full caster's slots of exactly <paramref name="slotLevel"/>.</summary>
    public static int FullExactly(int classLevel, int slotLevel) => new SlotRow(SpellSlotTables.Full(classLevel), null).At(slotLevel);

    /// <summary>
    /// A half caster's slots of every level, from class level 2, where the editions' tables agree. Level 1 is refused
    /// rather than answered: it differs by edition (2014 none, 2024 paladin two) and no archetype spends a half caster's
    /// slots there (the paladin's smite starts at 2), so a value would be untested data waiting for a caller.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="classLevel"/> is below 2 or above 20.</exception>
    public static int HalfTotal(int classLevel)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(classLevel, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(classLevel, DslLimits.MaxLevel);
        return SpellSlotTables.Half(classLevel, V.Editions.E2024).Sum();
    }
}
