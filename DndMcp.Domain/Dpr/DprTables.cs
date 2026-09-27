using DndMcp.Domain.Features;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Dpr;

/// <summary>
/// The data behind the rules tables the DPR tools cite (contract §4.7): Great Weapon Fighting and Savage Attacker
/// expected values, and the DMG's targets in areas of effect. The host renders them as <c>rules://tables/*</c>; the
/// per-level targets are <see cref="ReferenceCurves.TargetsByLevel"/>.
///
/// <para>
/// <b>Computed by the engine's own functions</b> (<see cref="DamageDice"/>, <see cref="DslValues.Shapes.Targets"/>), never
/// typed a second time: a table the model quotes and the arithmetic behind a DPR figure cannot disagree, and the tests
/// pin the table against the research's exact values (d6 with GWF 2014 = 25/6, 2d6 with Savage Attacker = 5425/648).
/// </para>
/// </summary>
public static class DprTables
{
    /// <summary>The weapon dice the Savage Attacker table lists: every SRD weapon's damage die, and the greatsword's 2d6.</summary>
    public static readonly IReadOnlyList<(int Count, int Sides)> WeaponDice = [(1, 4), (1, 6), (1, 8), (1, 10), (1, 12), (2, 6)];

    /// <summary>The DMG's own caveat on its area table, which results repeat with every area count.</summary>
    public const string AreaTargetsCaveat = "Add or subtract 1d3 for how bunched the creatures are.";

    public const string AreaTargetsSource = "Dungeon Master's Guide (2014), p. 249, \"Targets in Areas of Effect\"";

    private static readonly Lazy<IReadOnlyList<GwfDieRow>> Gwf = new(() =>
        new[] { 4, 6, 8, 10, 12 }.Select(sides => new GwfDieRow(
            sides,
            DamageDice.ExpectedValue(sides),
            DamageDice.ExpectedValue(sides, V.Remaps.Gwf2014),
            DamageDice.ExpectedValue(sides, V.Remaps.Gwf2024),
            DamageDice.ExpectedBestOfTwo(1, sides))).ToList());

    private static readonly Lazy<IReadOnlyList<SavageAttackerRow>> Savage = new(() =>
        WeaponDice.SelectMany(dice => new string?[] { null, V.Remaps.Gwf2014, V.Remaps.Gwf2024 }.Select(remap =>
        {
            var one = MeanOf(dice.Count, dice.Sides, remap);
            var best = DamageDice.ExpectedBestOfTwo(dice.Count, dice.Sides, remap);
            return new SavageAttackerRow(
                dice.Count,
                dice.Sides,
                remap,
                one,
                best,
                2 * one,
                best + one,
                DamageDice.ExpectedBestOfTwo(2 * dice.Count, dice.Sides, remap));
        })).ToList());

    /// <summary>
    /// Per die, d4 to d12: the plain mean, Great Weapon Fighting 2014 (reroll a 1 or 2 once, keep the new roll) and 2024
    /// (a 1 or 2 counts as 3), and Savage Attacker's better of two rolls. Research A2: d6 3.5 / 25/6 / 4.
    /// </summary>
    public static IReadOnlyList<GwfDieRow> GreatWeaponFighting => Gwf.Value;

    /// <summary>
    /// Savage Attacker on each <see cref="WeaponDice"/> set, plain and with each GWF remap: a hit's mean with and without
    /// it, and a crit's under both readings of ruling <c>savage_attacker_on_crit_dice</c> (off by default: the better of two
    /// rolls of one set plus the extra crit set once; on: the better of two rolls of the whole doubled set).
    /// </summary>
    public static IReadOnlyList<SavageAttackerRow> SavageAttacker => Savage.Value;

    /// <summary>The DMG's rule per shape: targets = the measure ÷ the divisor, rounded up, at least 1.</summary>
    public static IReadOnlyList<AreaTargetRule> AreaTargets { get; } =
    [
        new(V.Shapes.Cone, "length", 10),
        new(V.Shapes.Cube, "side", 5),
        new(V.Shapes.Cylinder, "radius", 5),
        new(V.Shapes.Line, "length", 30),
        new(V.Shapes.Sphere, "radius", 5),
    ];

    /// <summary>
    /// Worked examples from SRD spells (sizes checked against both SRDs): the counts a save_effect with that shape and size
    /// uses.
    /// </summary>
    public static IReadOnlyList<AreaTargetExample> AreaTargetExamples { get; } =
    [
        Example("Burning Hands", V.Shapes.Cone, 15),
        Example("Thunderwave", V.Shapes.Cube, 15),
        Example("Flame Strike", V.Shapes.Cylinder, 10),
        Example("Fireball", V.Shapes.Sphere, 20),
        Example("Lightning Bolt", V.Shapes.Line, 100),
        Example("Cone of Cold", V.Shapes.Cone, 60),
    ];

    private static AreaTargetExample Example(string spell, string shape, int size) =>
        new(spell, shape, size, V.Shapes.Targets(shape, size));

    private static double MeanOf(int count, int sides, string? remap) =>
        count * DamageDice.ExpectedValue(sides, remap);
}

/// <summary>One die's expected values: plain, with each Great Weapon Fighting remap, and Savage Attacker's better of two.</summary>
public sealed record GwfDieRow(int Sides, double Plain, double Gwf2014, double Gwf2024, double SavageAttacker)
{
    /// <summary>What GWF 2014 adds per die (d12: +0.833).</summary>
    public double Gain2014 => Gwf2014 - Plain;

    /// <summary>What GWF 2024 adds per die (d6: +0.5).</summary>
    public double Gain2024 => Gwf2024 - Plain;
}

/// <summary>
/// Savage Attacker on one weapon's dice (research A2, contract §8.4): each figure is the dice's mean, flat damage
/// excluded, since a reroll never changes the modifier.
/// </summary>
/// <param name="Remap">Null, gwf2014 or gwf2024.</param>
/// <param name="Hit">E[the dice].</param>
/// <param name="HitWithSavageAttacker">E[better of two rolls]: 1d8 4.5 → 5.8125.</param>
/// <param name="Crit">E[the doubled set].</param>
/// <param name="CritWithSavageAttacker">Ruling off (the default): the better of two rolls of one set, plus the extra crit set once.</param>
/// <param name="CritWithSavageAttackerOnCritDice">Ruling on: the better of two rolls of the whole doubled set (1d8: 10.8457).</param>
public sealed record SavageAttackerRow(
    int Count,
    int Sides,
    string? Remap,
    double Hit,
    double HitWithSavageAttacker,
    double Crit,
    double CritWithSavageAttacker,
    double CritWithSavageAttackerOnCritDice)
{
    /// <summary>"2d6".</summary>
    public string Dice => $"{DslText.Number(Count)}d{DslText.Number(Sides)}";

    public double HitGain => HitWithSavageAttacker - Hit;
}

/// <summary>The DMG's area rule for one shape.</summary>
/// <param name="Measure">What the size is: a cone's or line's length, a cube's side, a sphere's or cylinder's radius.</param>
public sealed record AreaTargetRule(string Shape, string Measure, int Divisor);

/// <summary>An SRD spell's area and the creatures the DMG table puts in it.</summary>
public sealed record AreaTargetExample(string Spell, string Shape, int Size, int Targets);
