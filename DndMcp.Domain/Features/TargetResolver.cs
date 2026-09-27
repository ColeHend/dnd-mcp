using System.Text.Json;
using DndMcp.Domain.Core;
using DndMcp.Domain.Encounters;
using FluentValidation;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Features;

/// <summary>
/// The target at one level, every value concrete, with where each came from (results print "AC 15 (DMG 2014 row for CR
/// 5)" so a reader can tell a convention from an input).
///
/// <para>
/// <b>Cover is NOT in <see cref="ArmorClass"/> or the save bonuses</b>: it is <see cref="CoverBonus"/>, added by the engine
/// to AC for attacks that do not ignore cover (2024 Sharpshooter) and to Dex saves. Folding it in would make ignore_cover
/// impossible to apply.
/// </para>
/// </summary>
public sealed record ResolvedTarget
{
    /// <summary>AC before cover.</summary>
    public required int ArmorClass { get; init; }

    /// <summary>"given", or "DMG 2014 row for CR 5".</summary>
    public required string ArmorClassSource { get; init; }

    /// <summary>Save bonus per ability key, before cover and save dice.</summary>
    public required IReadOnlyDictionary<string, int> SaveBonuses { get; init; }

    /// <summary>Where the save bonuses come from (given, or the typical-save-bonus table and its source).</summary>
    public required string SaveBonusSource { get; init; }

    public int SaveBonus(string ability) => SaveBonuses[ability];

    /// <summary>Hit points when given; null disables kill chances and HP-capped damage.</summary>
    public int? HitPoints { get; init; }

    public required IReadOnlyList<string> Resistances { get; init; }

    public required IReadOnlyList<string> Vulnerabilities { get; init; }

    public required IReadOnlyList<string> Immunities { get; init; }

    /// <summary>Typeless damage (null) is never resisted, and so on for the other two.</summary>
    public bool IsResistant(string? damageType) => damageType is not null && Resistances.Contains(damageType);

    public bool IsVulnerable(string? damageType) => damageType is not null && Vulnerabilities.Contains(damageType);

    public bool IsImmune(string? damageType) => damageType is not null && Immunities.Contains(damageType);

    public required bool MagicResistance { get; init; }

    public required bool Evasion { get; init; }

    /// <summary>A <see cref="DslValues.Conditions.TargetSet"/> value, or null.</summary>
    public string? Condition { get; init; }

    /// <summary><see cref="DslValues.Cover"/> value, or null.</summary>
    public string? Cover { get; init; }

    /// <summary>+2 (half), +5 (three-quarters) or 0: to AC and Dex saves.</summary>
    public required int CoverBonus { get; init; }

    public required int LegendaryResistance { get; init; }

    /// <summary>Signed dice on the target's saves (Bane "-1d4"), dice only, or null.</summary>
    public DamageFormula? SaveDice { get; init; }

    /// <summary>Chance a second creature is adjacent (Cleave).</summary>
    public required double SecondTargetRate { get; init; }

    /// <summary>The CR whose DMG row was used (for AC and/or saves), or null when neither came from a row.</summary>
    public ChallengeRating? ChallengeRating { get; init; }

    /// <summary>The DMG 2014 row for <see cref="ChallengeRating"/>.</summary>
    public MonsterStatsRow? Row { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// Resolves a <see cref="TargetSpec"/> at a level. With neither <c>ac</c> nor <c>cr</c>, the target is the DMG 2014
/// "Monster Statistics by Challenge Rating" row for CR = the level: the community convention that a level L party's fair
/// fight is a CR L monster (2014 SRD: a party of four "should be able to defeat a monster that has a challenge rating equal
/// to its level"), which puts a max-stat character at about 65% to hit. With <c>ac</c> only, saves still come from that
/// row's CR, and a note says so. With <c>cr</c>, its row gives the AC (unless <c>ac</c> overrides it) and the saves.
/// </summary>
public static class TargetResolver
{
    /// <summary>The target at <paramref name="level"/>. Validates the spec first (one exception, every problem).</summary>
    /// <exception cref="DndInputException">The spec is not valid.</exception>
    public static ResolvedTarget Resolve(TargetSpec? spec, int level)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, DslLimits.MinLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, DslLimits.MaxLevel);
        Validate(spec);
        spec ??= new TargetSpec();

        var notes = new List<string>();
        var givenCr = ParseCr(spec.Cr, "cr");
        var cr = givenCr ?? ChallengeRating.Parse(DslText.Number(level));
        var row = ChallengeRatingTables.MonsterStats(cr);
        var rowText = $"DMG 2014 row for CR {cr}";

        string acSource;
        int ac;
        if (spec.Ac is { } givenAc)
        {
            ac = givenAc;
            acSource = "given";
        }
        else
        {
            ac = row.ArmorClass;
            acSource = givenCr is null ? $"{rowText} (CR = level {DslText.Number(level)})" : rowText;
            if (row.IsCeiling)
            {
                notes.Add($"The CR 0 row's AC {DslText.Number(ac)} is a ceiling (\"13 or lower\"), used as the AC.");
            }
        }

        var typical = TypicalSaveBonus.For(cr);
        var saves = V.Abilities.All.ToDictionary(a => a, a => spec.Saves?.Get(a) ?? spec.SaveBonus ?? typical, StringComparer.Ordinal);
        var perAbility = V.Abilities.All.Count(a => spec.Saves?.Get(a) is not null);
        var typicalText = $"{Signed(typical)}, the {TypicalSaveBonus.Source} for CR {cr}";
        string saveSource;
        if (perAbility == V.Abilities.All.Count || (perAbility == 0 && spec.SaveBonus is not null))
        {
            saveSource = "given";
        }
        else if (spec.SaveBonus is not null)
        {
            saveSource = $"given per ability; the rest save_bonus {Signed(spec.SaveBonus.Value)}";
        }
        else
        {
            saveSource = perAbility > 0 ? $"given per ability; the rest {typicalText}" : typicalText;
            if (givenCr is null && spec.Ac is not null)
            {
                notes.Add(
                    $"Only ac was given, so the saves use CR {cr} (the CR = level row): {Signed(typical)} on every save not given. " +
                    "Give save_bonus or saves to set them.");
            }
        }

        var resistances = Types(spec.Resistances);
        var vulnerabilities = Types(spec.Vulnerabilities);
        var immunities = Types(spec.Immunities);
        foreach (var type in immunities.Intersect(resistances.Concat(vulnerabilities)).Distinct())
        {
            notes.Add($"{type} is both an immunity and a resistance or vulnerability; immunity wins (no {type} damage).");
        }

        // The CR a reader should see: the one given, or CR = level when its row supplied the AC or any save.
        var typicalUsed = spec.SaveBonus is null && perAbility < V.Abilities.All.Count;
        var rowCr = givenCr ?? (spec.Ac is null || typicalUsed ? cr : null);
        var cover = CompiledBuild.Match(V.Cover.Set, spec.Cover);
        return new ResolvedTarget
        {
            ArmorClass = ac,
            ArmorClassSource = acSource,
            SaveBonuses = saves,
            SaveBonusSource = saveSource,
            HitPoints = spec.Hp,
            Resistances = resistances,
            Vulnerabilities = vulnerabilities,
            Immunities = immunities,
            MagicResistance = spec.MagicResistance ?? false,
            Evasion = spec.Evasion ?? false,
            Condition = CompiledBuild.Match(V.Conditions.TargetSet, spec.Condition),
            Cover = cover,
            CoverBonus = V.Cover.Bonus(cover),
            LegendaryResistance = spec.LegendaryResistance ?? 0,
            SaveDice = spec.SaveDice is null ? null : DamageFormula.ParseBonusDice(spec.SaveDice, "save_dice", SaveDiceFlatHint),
            SecondTargetRate = spec.SecondTargetRate ?? 0,
            ChallengeRating = rowCr,
            Row = rowCr is null ? null : row,
            Notes = notes,
        };
    }

    /// <summary>Validates a target spec (null is the default target and always valid).</summary>
    /// <exception cref="DndInputException">Every problem found, up to five, in one message.</exception>
    public static void Validate(TargetSpec? spec)
    {
        if (spec is not null)
        {
            DslProblems.ThrowIfAny(DslProblems.Messages(TargetSpecValidator.Instance.Validate(spec)), "target");
        }
    }

    /// <summary>
    /// Warnings that need both a build and its target: typeless damage against a target that resists, is vulnerable to
    /// or is immune to something (typeless damage ignores all three, which is rarely what was meant).
    /// </summary>
    public static IReadOnlyList<string> Warnings(ResolvedBuild build, ResolvedTarget target)
    {
        if (target.Resistances.Count + target.Vulnerabilities.Count + target.Immunities.Count == 0)
        {
            return [];
        }

        var typeless = build.Attacks.Where(a => a.DamageType is null).Select(a => a.Name)
            .Concat(build.Riders.Where(r => r.DamageType is null && r.Damage.HasDice).Select(r => r.Source.Label))
            .Concat(build.SaveEffects.Where(s => s.DamageType is null && s.Damage is not null).Select(s => s.Source.Label))
            .ToList();
        return typeless.Count == 0
            ? []
            : [$"{string.Join(", ", typeless)} {(typeless.Count == 1 ? "has" : "have")} no damage type, so the target's resistances, vulnerabilities and immunities never apply to {(typeless.Count == 1 ? "it" : "them")}; give damage_type (or type) to model them."];
    }

    internal const string SaveDiceFlatHint = "put a flat change in save_bonus";

    /// <summary>
    /// A CR from the bound value: a string ("1/2", "5") or a JSON number read by its raw text, as encounter_difficulty
    /// reads it (a double would turn 1e-400 into CR 0).
    /// </summary>
    internal static ChallengeRating? ParseCr(object? raw, string field)
    {
        if (LevelValue.ToElement(raw) is not { } element)
        {
            return null;
        }

        try
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => ChallengeRating.Parse(element.GetString()),
                JsonValueKind.Number => ChallengeRating.Parse(element.GetRawText()),
                _ => throw new DndInputException(
                    $"must be a string such as \"1/2\" or \"5\", or a number, but was {DslText.Describe(element)}."),
            };
        }
        catch (DndInputException ex)
        {
            throw new DndInputException($"{field}: {ex.Message}", ex);
        }
    }

    private static List<string> Types(IReadOnlyList<string>? types) =>
        (types ?? []).Select(t => CompiledBuild.Match(V.DamageTypes.Set, t)!).Distinct().Order(StringComparer.Ordinal).ToList();

    private static string Signed(int value) => value >= 0 ? $"+{DslText.Number(value)}" : DslText.Number(value);
}

/// <summary>Field checks of a <see cref="TargetSpec"/>: ranges, wire values, the CR and the save dice.</summary>
public sealed class TargetSpecValidator : AbstractValidator<TargetSpec>
{
    public static TargetSpecValidator Instance { get; } = new();

    public TargetSpecValidator()
    {
        RuleFor(t => t.Ac).Custom((ac, context) =>
        {
            var problems = new Problems<TargetSpec>(context, string.Empty);
            var target = context.InstanceToValidate;
            problems.InRange("ac", ac, DslLimits.MinTargetAc, DslLimits.MaxTargetAc);
            problems.Parse(() => TargetResolver.ParseCr(target.Cr, "cr"));
            problems.InRange("save_bonus", target.SaveBonus, DslLimits.MinSaveBonus, DslLimits.MaxSaveBonus);
            foreach (var ability in V.Abilities.All)
            {
                problems.InRange($"saves {ability}", target.Saves?.Get(ability), DslLimits.MinSaveBonus, DslLimits.MaxSaveBonus);
            }

            problems.InRange("hp", target.Hp, 1, DslLimits.MaxTargetHp);
            problems.InRange("legendary_resistance", target.LegendaryResistance, 0, DslLimits.MaxLegendaryResistance);
            problems.InRange("second_target_rate", target.SecondTargetRate, 0, 1);
        });

        RuleFor(t => t.Condition).Custom((condition, context) =>
        {
            var problems = new Problems<TargetSpec>(context, string.Empty);
            var target = context.InstanceToValidate;
            problems.Known(V.Conditions.TargetSet, "condition", condition);
            problems.Known(V.Cover.Set, "cover", target.Cover);
            TypeList("resistances", target.Resistances, problems);
            TypeList("vulnerabilities", target.Vulnerabilities, problems);
            TypeList("immunities", target.Immunities, problems);
            if (target.SaveDice is not null)
            {
                problems.Parse(() => DamageFormula.ParseBonusDice(target.SaveDice, "save_dice", TargetResolver.SaveDiceFlatHint));
            }
        });
    }

    private static void TypeList(string field, IReadOnlyList<string?>? types, Problems<TargetSpec> problems)
    {
        foreach (var type in types ?? [])
        {
            if (!V.DamageTypes.Set.TryMatch(type, out _))
            {
                problems.Add($"{field} has \"{DslText.Echo(type)}\", which is not a damage type; they are {V.DamageTypes.Set.List}.");
            }
        }
    }
}
