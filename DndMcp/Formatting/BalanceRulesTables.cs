using System.Globalization;
using DndMcp.Domain.Dpr;
using DndMcp.Domain.Features;
using DndMcp.Formatting.Srd;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Formatting;

/// <summary>
/// The three rules tables the DPR tools cite (contract §4.7): the per-level targets and reference curves, Great Weapon
/// Fighting and Savage Attacker expected values, and the DMG's creatures in an area. Entries of the
/// <see cref="RulesTables"/> catalog, so each is a <c>rules://tables/&lt;slug&gt;</c> resource and a <c>rules_get</c>
/// answer like the encounter tables.
///
/// <para>
/// <b>Computed by the engine's own functions, never typed in</b> (<see cref="ReferenceCurves.TargetsByLevel"/>,
/// <see cref="DprTables"/>): the numbers a model quotes from a table and the ones behind a <c>balance_dpr</c> result cannot
/// disagree. The warlock column is the <c>warlock_baseline</c> preset evaluated by the engine, not the published curve
/// copied, and the tests pin it to the published values.
/// </para>
/// <para>
/// <b>Only one of them is rules text.</b> Great Weapon Fighting and Savage Attacker are SRD rules and the table is
/// arithmetic on them; the targets table mixes the DMG 2014's monster row with The Finished Book's save bonus and two
/// community conventions, and the area table is the DMG's. Each page says whose each column is, and
/// <c>rules://attribution</c> lists the non-SRD ones, so a model never cites "the DMG's typical save bonus" or "the
/// official DPR target".
/// </para>
/// </summary>
internal static class BalanceRulesTables
{
    public const string TargetsSlug = "dpr-targets-by-level";

    public const string GwfSlug = "gwf-expected-values";

    public const string AreaSlug = "aoe-targets";

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static IReadOnlyList<RulesTable> All { get; } =
    [
        new(
            TargetsSlug,
            "DPR Targets by Level",
            "2014 and 2024",
            "Dungeon Master's Guide (2014), pp. 274–275 (AC, hit points, attack, save DC; not in either SRD); typical save bonus: " +
            "The Finished Book (tomedunn), not a DMG table; RPGBOT's DPR target and the Warlock Baseline (Form of Dread): " +
            "community conventions",
            "Per character level 1–20: the default balance_dpr target (the DMG 2014 monster row for CR = level, with a typical " +
            "save bonus) and two reference DPR curves (RPGBOT's target and the warlock baseline).",
            ["DPR Targets by Level", "DPR Targets", "DPR Reference Curves", "Damage per Round Targets"],
            Targets),
        new(
            GwfSlug,
            "Great Weapon Fighting Expected Damage",
            "2014 and 2024",
            "SRD 5.1 and SRD 5.2.1 (Great Weapon Fighting), SRD 5.2.1 (Savage Attacker); the expected values are computed from " +
            "those rules by this server's DPR engine",
            "Expected damage per die with Great Weapon Fighting (2014: reroll 1–2 once; 2024: 1–2 count as 3) and with Savage " +
            "Attacker (the better of two rolls), on hits and crits.",
            ["Great Weapon Fighting Expected Damage", "GWF Expected Values", "GWF Expected Damage", "Savage Attacker Expected Damage"],
            GreatWeaponFighting),
        new(
            AreaSlug,
            "Targets in Areas of Effect",
            "2014",
            "Dungeon Master's Guide (2014), p. 249; not in either SRD",
            "The DMG's rule of thumb for how many creatures an area of effect covers, by shape and size, with SRD spell examples.",
            ["Targets in Areas of Effect", "AoE Targets", "Area of Effect Targets", "Creatures in an Area of Effect"],
            AreaTargets),
    ];

    private static string Targets(RulesTable table) => RulesTables.Page(
        table,
        SrdMarkdownText.Table(
            ["Level", "PB", "Target AC", "Target HP (max)", "Target attack", "Target DC", "Typical save", "RPGBOT target DPR", "Warlock baseline DPR", "Warlock to hit"],
            ReferenceCurves.TargetsByLevel.Select(row => (IReadOnlyList<string>)
            [
                Number(row.Level),
                SrdMarkdownText.Signed(row.ProficiencyBonus),
                Number(row.ArmorClass),
                Number(row.HitPointsMax),
                SrdMarkdownText.Signed(row.AttackBonus),
                Number(row.SaveDc),
                SrdMarkdownText.Signed(row.TypicalSaveBonus),
                row.RpgbotTarget.ToString("0.00", Invariant),
                row.WarlockBaseline.ToString("0.00", Invariant),
                $"{SrdMarkdownText.Signed(row.WarlockAttackBonus)} ({(row.WarlockHitChance * 100).ToString("0.#", Invariant)}%)",
            ])),
        "**The target** at level L is the DMG 2014 \"Monster Statistics by Challenge Rating\" row for CR = L (`" +
        RulesTables.UriPrefix + "monster-stats-by-cr-2014`): the convention that a CR L monster is a fair fight for a level L " +
        "party. balance_dpr and balance_compare use it when the target gives neither ac nor cr, in either edition (the 2024 " +
        "books have no such table).",
        $"**Typical save** is the average save bonus of published monsters by CR, from The Finished Book's \"Baseline Monster " +
        $"Stats\" ({TypicalSaveBonus.SourceUrl}), not a DMG column: the default target save bonus for save effects.",
        $"**RPGBOT target DPR** is the row's maximum hit points ÷ 12 (a party of four ending the fight in three rounds). " +
        $"**Warlock baseline DPR** is the community Warlock Baseline (Form of Dread): Eldritch Blast with Agonizing Blast (from " +
        "level 2) and Hex, Cha 16/18/20 at levels 1/4/8, against the row's AC, computed by this server from the " +
        "warlock_baseline preset; Hex is assumed already up, as in the published curve. Both are community conventions, not " +
        "rules, and balance_compare falls back to RPGBOT's slope for level-equivalents when a baseline does not scale with level.");

    private static string GreatWeaponFighting(RulesTable table)
    {
        var perDie = SrdMarkdownText.Table(
            ["Die", "Plain", "GWF 2014", "Gain", "GWF 2024", "Gain", "Savage Attacker (best of 2)"],
            DprTables.GreatWeaponFighting.Select(row => (IReadOnlyList<string>)
            [
                $"d{Number(row.Sides)}",
                Mean(row.Plain),
                Mean(row.Gwf2014),
                Gain(row.Gain2014),
                Mean(row.Gwf2024),
                Gain(row.Gain2024),
                Mean(row.SavageAttacker),
            ]));
        var savage = SrdMarkdownText.Table(
            ["Weapon dice", "Great Weapon Fighting", "Hit", "Hit with Savage Attacker", "Crit", "Crit with Savage Attacker", "Crit, savage_attacker_on_crit_dice"],
            DprTables.SavageAttacker.Select(row => (IReadOnlyList<string>)
            [
                row.Dice,
                row.Remap switch { V.Remaps.Gwf2014 => "2014", V.Remaps.Gwf2024 => "2024", _ => "—" },
                Mean(row.Hit),
                Mean(row.HitWithSavageAttacker),
                Mean(row.Crit),
                Mean(row.CritWithSavageAttacker),
                Mean(row.CritWithSavageAttackerOnCritDice),
            ]));

        return RulesTables.Page(
            table,
            "**Per die** (expected value of one die):\n\n" + perDie,
            // The rules cover "the damage dice you roll for an attack with the weapon"; whether a rider's dice count is the
            // table's call, so this names the default as balance_dpr's, not as the rule.
            GwfCoverage,
            "**Savage Attacker** (2024 feat: once per turn, roll a weapon hit's damage dice twice and use either), per weapon's " +
            "dice, flat damage excluded since a reroll never changes it:\n\n" + savage,
            "On a crit the rules text does not settle what is rerolled. By default (ruling savage_attacker_on_crit_dice false) the " +
            "better of two rolls of one set of the weapon's dice plus the extra crit set once; with the ruling on, the better of " +
            "two rolls of the whole doubled set.");
    }

    /// <summary>What Great Weapon Fighting covers, and which dice balance_dpr remaps by default.</summary>
    internal const string GwfCoverage =
        "Great Weapon Fighting 2014: reroll a 1 or 2 on a damage die once and use the new roll. 2024: treat a 1 or 2 as a 3. " +
        "Both texts cover the damage dice you roll for an attack with the weapon; they do not settle whether dice that another " +
        "feature adds to the hit (smites, Hex) count, which is a table ruling. By default (ruling gwf_on_riders false) " +
        "balance_dpr remaps only the weapon's own dice; with the ruling on, it remaps rider dice too.";

    private static string AreaTargets(RulesTable table) => RulesTables.Page(
        table,
        SrdMarkdownText.Table(
            ["Shape", "Size is its", "Creatures"],
            DprTables.AreaTargets.Select(rule => (IReadOnlyList<string>)
            [
                rule.Shape,
                rule.Measure,
                $"{rule.Measure} ÷ {Number(rule.Divisor)}, rounded up (at least 1)",
            ])),
        "Examples (sizes as the SRD spells give them):\n\n" + SrdMarkdownText.Table(
            ["Spell", "Area", "Creatures"],
            DprTables.AreaTargetExamples.Select(example => (IReadOnlyList<string>)
            [
                example.Spell,
                $"{Number(example.Size)}-ft {example.Shape}",
                Number(example.Targets),
            ])),
        $"{DprTables.AreaTargetsCaveat} It is a rule of thumb for creatures spread through a fight, not a count of who is " +
        "actually in the area. A save_effect with shape and size uses it in either edition; give targets instead when you " +
        "know the count.");

    private static string Mean(double value) => value.ToString("0.####", Invariant);

    private static string Gain(double value) => "+" + value.ToString("0.####", Invariant);

    private static string Number(int value) => value.ToString(Invariant);
}
