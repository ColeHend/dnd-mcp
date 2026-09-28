using System.Text.Json;
using DndMcp.Domain.Core;
using DndMcp.Domain.Encounters;
using DndMcp.Domain.Simulation;
using FluentValidation;
using Q = DndMcp.Domain.Simulation.StatBlockValues.DamageQualifiers;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Features;

/// <summary>
/// What an instance of damage is made with, as a qualified resistance reads it ("from nonmagical attacks that aren't
/// silvered"). A rider takes its attack's properties: a smite on a mundane longsword is the longsword's hit. A save
/// effect is magical when it says so (<see cref="ResolvedSaveEffect.Magical"/>), and never silvered or adamantine.
/// </summary>
/// <param name="Magical">A spell, a magic weapon (<see cref="DslValues.Properties.Magical"/>), or a magical save effect.</param>
public readonly record struct DamageProperties(bool Magical, bool Silvered, bool Adamantine)
{
    /// <summary>Plain damage: every qualified adjustment applies to it.</summary>
    public static DamageProperties Mundane => default;

    public static DamageProperties Of(ResolvedAttack attack)
    {
        ArgumentNullException.ThrowIfNull(attack);
        return new DamageProperties(attack.IsMagical, attack.IsSilvered, attack.IsAdamantine);
    }

    public static DamageProperties Of(ResolvedSaveEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        return new DamageProperties(effect.Magical, false, false);
    }
}

/// <summary>
/// The target at one level, every value concrete, with where each came from (results print "AC 15 (DMG 2014 row for CR
/// 5)" so a reader can tell a convention from an input).
///
/// <para>
/// <b>Cover is NOT in <see cref="ArmorClass"/> or the save bonuses</b>: it is <see cref="CoverBonus"/>, added by the engine
/// to AC for attacks that do not ignore cover (2024 Sharpshooter) and to Dex saves. Folding it in would make ignore_cover
/// impossible to apply.
/// </para>
/// <para>
/// <b>Damage adjustments come in two halves.</b> <see cref="Resistances"/> (and the other two) are the damage types it
/// resists whatever the damage is made with: everything a <see cref="TargetSpec"/> gives, and a stat block's unqualified
/// entries. <see cref="QualifiedResistances"/> keep a stat block's qualified entries whole ("bludgeoning from nonmagical
/// attacks that aren't silvered"), because reading them as plain types would make a werewolf resist a +1 sword and a
/// silvered blade. Ask <see cref="IsResistant(string?, DamageProperties)"/>, which reads both; the one-argument form asks
/// about plain (mundane) damage, which every qualified entry covers.
/// </para>
/// </summary>
public sealed record ResolvedTarget
{
    /// <summary>AC before cover.</summary>
    public required int ArmorClass { get; init; }

    /// <summary>"given", "DMG 2014 row for CR 5", or "Ogre stat block".</summary>
    public required string ArmorClassSource { get; init; }

    /// <summary>Save bonus per ability key, before cover and save dice.</summary>
    public required IReadOnlyDictionary<string, int> SaveBonuses { get; init; }

    /// <summary>Where the save bonuses come from (given, the typical-save-bonus table and its source, or the stat block).</summary>
    public required string SaveBonusSource { get; init; }

    public int SaveBonus(string ability) => SaveBonuses[ability];

    /// <summary>Hit points when given (or the stat block's average); null disables kill chances and HP-capped damage.</summary>
    public int? HitPoints { get; init; }

    /// <summary>Damage types it resists whatever the damage is made with (unqualified), sorted.</summary>
    public required IReadOnlyList<string> Resistances { get; init; }

    public required IReadOnlyList<string> Vulnerabilities { get; init; }

    public required IReadOnlyList<string> Immunities { get; init; }

    /// <summary>
    /// A stat block's qualified resistances, whole (<see cref="StatBlockValues.DamageQualifiers"/>): they apply only to
    /// damage whose <see cref="DamageProperties"/> the qualifier admits. An <see cref="Q.Other"/> entry (a condition the
    /// engine cannot read) always applies, and the result says so. Empty for a target without a stat block.
    /// </summary>
    public IReadOnlyList<DamageAdjustment> QualifiedResistances { get; init; } = [];

    public IReadOnlyList<DamageAdjustment> QualifiedVulnerabilities { get; init; } = [];

    public IReadOnlyList<DamageAdjustment> QualifiedImmunities { get; init; } = [];

    /// <summary>Whether it resists plain (mundane) damage of this type. Typeless damage (null) is never resisted, and so on for the other two.</summary>
    public bool IsResistant(string? damageType) => IsResistant(damageType, DamageProperties.Mundane);

    public bool IsVulnerable(string? damageType) => IsVulnerable(damageType, DamageProperties.Mundane);

    public bool IsImmune(string? damageType) => IsImmune(damageType, DamageProperties.Mundane);

    /// <summary>Whether it resists damage of this type made with these properties: an unqualified entry, or a qualified one that admits them.</summary>
    public bool IsResistant(string? damageType, DamageProperties properties) => Applies(damageType, Resistances, QualifiedResistances, properties);

    public bool IsVulnerable(string? damageType, DamageProperties properties) => Applies(damageType, Vulnerabilities, QualifiedVulnerabilities, properties);

    public bool IsImmune(string? damageType, DamageProperties properties) => Applies(damageType, Immunities, QualifiedImmunities, properties);

    /// <summary>Any resistance, vulnerability or immunity at all, qualified or not.</summary>
    public bool HasDamageAdjustments =>
        Resistances.Count + Vulnerabilities.Count + Immunities.Count +
        QualifiedResistances.Count + QualifiedVulnerabilities.Count + QualifiedImmunities.Count > 0;

    public required bool MagicResistance { get; init; }

    public required bool Evasion { get; init; }

    /// <summary>A <see cref="DslValues.Conditions.TargetSet"/> value, or null.</summary>
    public string? Condition { get; init; }

    /// <summary>
    /// <see cref="StatBlockValues.Conditions"/> it cannot have (a stat block's condition immunities): a condition_on_hit,
    /// a save effect's condition and Topple never land on it. Empty for a target without a stat block.
    /// </summary>
    public IReadOnlyList<string> ConditionImmunities { get; init; } = [];

    public bool IsImmuneToCondition(string condition) => ConditionImmunities.Contains(condition, StringComparer.Ordinal);

    /// <summary><see cref="DslValues.Cover"/> value, or null.</summary>
    public string? Cover { get; init; }

    /// <summary>+2 (half), +5 (three-quarters) or 0: to AC and Dex saves.</summary>
    public required int CoverBonus { get; init; }

    /// <summary>Uses per day; a stat block's non-lair count (the fight is never in a lair here).</summary>
    public required int LegendaryResistance { get; init; }

    /// <summary>Signed dice on the target's saves (Bane "-1d4"), dice only, or null.</summary>
    public DamageFormula? SaveDice { get; init; }

    /// <summary>Chance a second creature is adjacent (Cleave).</summary>
    public required double SecondTargetRate { get; init; }

    /// <summary>
    /// The target's CR: the one whose profile row was used (for AC and/or saves), or the stat block's. Null when neither
    /// came from a row and there is no stat block.
    /// </summary>
    public ChallengeRating? ChallengeRating { get; init; }

    /// <summary>The DMG 2014 row for <see cref="ChallengeRating"/>, when the dmg2014 profile supplied a number.</summary>
    public MonsterStatsRow? Row { get; init; }

    /// <summary>The <see cref="DslValues.Profiles"/> value whose row supplied the AC or saves, or null when none did.</summary>
    public string? Profile { get; init; }

    /// <summary>The profile row behind <see cref="Profile"/> (its citation, and for an empirical profile its unrounded medians), or null.</summary>
    public TargetProfileRow? ProfileRow { get; init; }

    /// <summary>
    /// The row is the one for CR = the level evaluated (no cr given), so the target changes with the level: a curve and a
    /// level-equivalent slope measure against a rising target.
    /// </summary>
    public bool CrFollowsLevel { get; init; }

    /// <summary>The stat block the target is, or null for a target described by its numbers.</summary>
    public StatBlock? Monster { get; init; }

    /// <summary>"Ogre (2024 SRD stat block)": how results name a monster target; null without one.</summary>
    public string? MonsterLabel => Monster is null ? null : $"{Monster.Name} ({Monster.Edition} SRD stat block)";

    public required IReadOnlyList<string> Notes { get; init; }

    private static bool Applies(string? damageType, IReadOnlyList<string> plain, IReadOnlyList<DamageAdjustment> qualified, DamageProperties properties)
    {
        if (damageType is null)
        {
            return false;
        }

        if (plain.Contains(damageType, StringComparer.Ordinal))
        {
            return true;
        }

        foreach (var entry in qualified)
        {
            if (entry.DamageType == damageType && entry.AppliesTo(properties.Magical, properties.Silvered, properties.Adamantine))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Words for damage qualifiers, shared by the resolver's notes and the results, so "from nonmagical attacks that aren't
/// silvered" reads the same wherever it appears.
/// </summary>
public static class DamageQualifierText
{
    /// <summary>"from nonmagical attacks that aren't silvered", or null for <see cref="Q.Other"/> (its own text says it).</summary>
    public static string? Phrase(string qualifier) => qualifier switch
    {
        Q.Nonmagical => "from nonmagical attacks",
        Q.NonmagicalNotSilvered => "from nonmagical attacks that aren't silvered",
        Q.NonmagicalNotAdamantine => "from nonmagical attacks that aren't adamantine",
        _ => null,
    };

    /// <summary>
    /// "cold; bludgeoning, piercing and slashing from nonmagical attacks that aren't silvered": unqualified types, then each
    /// qualifier's types together; an unreadable qualifier keeps the data's words and says it always applies. "none" when
    /// empty.
    /// </summary>
    public static string Describe(IReadOnlyList<string> plain, IReadOnlyList<DamageAdjustment> qualified)
    {
        var groups = new List<string>();
        if (plain.Count > 0)
        {
            groups.Add(And(plain));
        }

        foreach (var group in qualified.Where(e => e.Qualifier != Q.Other).GroupBy(e => e.Qualifier!))
        {
            groups.Add($"{And(group.Select(e => e.DamageType).Distinct().ToList())} {Phrase(group.Key)}");
        }

        foreach (var entry in qualified.Where(e => e.Qualifier == Q.Other))
        {
            groups.Add(Other(entry));
        }

        return groups.Count == 0 ? "none" : string.Join("; ", groups);
    }

    /// <summary>"fire (\"while in dim light\", applied always)": an entry whose qualifier the engine cannot read.</summary>
    public static string Other(DamageAdjustment entry) => $"{entry.DamageType} (\"{entry.Text}\", applied always)";

    /// <summary>"a", "a and b", "a, b and c".</summary>
    public static string And(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Concat(items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];
}

/// <summary>
/// Resolves a <see cref="TargetSpec"/> at a level. With neither <c>ac</c> nor <c>cr</c>, the target is the profile's row
/// (by default the DMG 2014 "Monster Statistics by Challenge Rating") for CR = the level: the community convention that a
/// level L party's fair fight is a CR L monster (2014 SRD: a party of four "should be able to defeat a monster that has a
/// challenge rating equal to its level"), which puts a max-stat character at about 65% to hit. With <c>ac</c> only, saves
/// still come from that row's CR, and a note says so. With <c>cr</c>, its row gives the AC (unless <c>ac</c> overrides it)
/// and the saves.
///
/// <para>
/// <b>A stat block</b> (<c>target.monster</c>, looked up by the host) replaces the row: AC, all six saves, HP, damage
/// adjustments with their qualifiers, Magic Resistance, Evasion, Legendary Resistance (the non-lair count: no lair in these
/// tools) and condition immunities all come from it, and any of those the spec also gives overrides it with a note, so a
/// "what if it had AC 13" question is one field. The Domain never resolves the name itself: a spec naming a monster with
/// no stat block passed is refused rather than silently fought as the default row.
/// </para>
/// </summary>
public static class TargetResolver
{
    /// <summary>The target at <paramref name="level"/>. Validates the spec first (one exception, every problem).</summary>
    /// <param name="monster">
    /// The stat block <c>target.monster</c> names, looked up by the caller; required when the spec names one. A stat block
    /// passed for a spec that names none is used all the same (a caller that already has it).
    /// </param>
    /// <exception cref="DndInputException">The spec is not valid, names a monster without its stat block, or needs a profile that is not available.</exception>
    public static ResolvedTarget Resolve(TargetSpec? spec, int level, StatBlock? monster = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, DslLimits.MinLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, DslLimits.MaxLevel);
        Validate(spec, monster);
        spec ??= new TargetSpec();
        return monster is null ? FromProfile(spec, level) : FromStatBlock(spec, level, monster);
    }

    /// <summary>Validates a target spec's fields (null is the default target and always valid).</summary>
    /// <exception cref="DndInputException">Every problem found, up to five, in one message.</exception>
    public static void Validate(TargetSpec? spec)
    {
        if (spec is not null)
        {
            DslProblems.ThrowIfAny(DslProblems.Messages(TargetSpecValidator.Instance.Validate(spec)), "target");
        }
    }

    /// <summary>
    /// Validates the fields and that the spec and the stat block go together: a spec naming a monster needs its stat block
    /// (the Domain cannot look names up), and a stat block replaces the cr/profile row, so neither is taken beside one.
    /// </summary>
    /// <exception cref="DndInputException">A field is not valid, or the pair does not fit.</exception>
    public static void Validate(TargetSpec? spec, StatBlock? monster)
    {
        Validate(spec);
        if (monster is null && spec?.Monster is { } name)
        {
            throw new DndInputException(
                $"target monster \"{DslText.Echo(name)}\" was not looked up: this call has no stat block for it, and the default " +
                "target is not used in its place. Describe the creature with its numbers instead, e.g. {\"ac\": 11, \"hp\": 59, " +
                "\"saves\": {\"dex\": -1}}.");
        }

        if (monster is not null && spec is not null && spec.Monster is null && (spec.Cr is not null || spec.Profile is not null))
        {
            throw new DndInputException(
                $"Invalid target: the {monster.Name} stat block replaces the {(spec.Cr is not null ? "cr" : "profile")} row; give " +
                "ac, saves or save_bonus to change its numbers instead.");
        }
    }

    /// <summary>
    /// Warnings that need both a build and its target: typeless damage against a target that resists, is vulnerable to
    /// or is immune to something (typeless damage ignores all three, which is rarely what was meant): an attack without
    /// damage_type, a save effect without type, and a rider without type only where an attack it applies to is typeless
    /// too (elsewhere it deals that attack's type, so the target's adjustments apply to it).
    /// </summary>
    public static IReadOnlyList<string> Warnings(ResolvedBuild build, ResolvedTarget target)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(target);
        if (!target.HasDamageAdjustments)
        {
            return [];
        }

        var typeless = build.Attacks.Where(a => a.DamageType is null).Select(a => a.Name)
            .Concat(build.Riders
                .Where(r => r.DamageType is null && r.Damage.HasDice && build.Attacks.Any(a => a.DamageType is null && r.AppliesTo(a)))
                .Select(r => r.Source.Label))
            .Concat(build.SaveEffects.Where(s => s.DamageType is null && s.Damage is not null).Select(s => s.Source.Label))
            .ToList();
        return typeless.Count == 0
            ? []
            : [$"{string.Join(", ", typeless)} {(typeless.Count == 1 ? "has" : "have")} no damage type, so the target's resistances, vulnerabilities and immunities never apply to {(typeless.Count == 1 ? "it" : "them")}; give damage_type (or type) to model them."];
    }

    /// <summary>
    /// Where a qualified resistance, vulnerability or immunity met this build's damage: for each qualifier the build's
    /// damage reaches, which attacks and save effects it applied to and which it did not, and why ("magical: a spell";
    /// "silvered"). A reader who sees a werewolf shrug off half a +1 sword's damage needs to know the sword was not marked
    /// magical, and how to mark it. An entry whose qualifier the engine cannot read is said to apply always.
    /// </summary>
    public static IReadOnlyList<string> AdjustmentNotes(ResolvedBuild build, ResolvedTarget target)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(target);
        var notes = new List<string>();
        var sources = DamageSources(build);
        var anyApplied = false;
        foreach (var (kind, entries) in new[]
                 {
                     ("Resistance", target.QualifiedResistances),
                     ("Vulnerability", target.QualifiedVulnerabilities),
                     ("Immunity", target.QualifiedImmunities),
                 })
        {
            foreach (var group in entries.GroupBy(e => e.Qualifier!))
            {
                var types = group.Select(e => e.DamageType).Distinct().ToList();
                var reached = sources.Where(s => s.Types.Any(types.Contains)).ToList();
                if (reached.Count == 0)
                {
                    continue;
                }

                if (group.Key == Q.Other)
                {
                    notes.Add(
                        $"{kind} to {string.Join("; ", group.Select(DamageQualifierText.Other))}: its condition is not modelled, so it applies " +
                        $"to {DamageQualifierText.And(reached.Select(s => s.Name).ToList())} every time.");
                    continue;
                }

                var entry = group.First();
                var applied = reached.Where(s => entry.AppliesTo(s.Properties.Magical, s.Properties.Silvered, s.Properties.Adamantine)).ToList();
                var bypassed = reached.Except(applied).ToList();
                var parts = new List<string>();
                if (applied.Count > 0)
                {
                    anyApplied = true;
                    parts.Add($"applied to {DamageQualifierText.And(applied.Select(s => s.Name).ToList())} ({AppliedWhy(group.Key)})");
                }

                if (bypassed.Count > 0)
                {
                    parts.Add($"not applied to {string.Join(", ", bypassed.Select(s => $"{s.Name} ({BypassWhy(group.Key, s)})"))}");
                }

                notes.Add($"{kind} to {DamageQualifierText.And(types)} {DamageQualifierText.Phrase(group.Key)}: {string.Join("; ", parts)}.");
            }
        }

        if (anyApplied)
        {
            notes.Add(
                "Mark an attack that overcomes a qualified resistance with properties [\"magical\"] (a magic weapon; spells already are), " +
                "[\"silvered\"] or [\"adamantine\"]; a save effect with magical.");
        }

        return notes;
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
                JsonValueKind.String => Encounters.ChallengeRating.Parse(element.GetString()),
                JsonValueKind.Number => Encounters.ChallengeRating.Parse(element.GetRawText()),
                _ => throw new DndInputException(
                    $"must be a string such as \"1/2\" or \"5\", or a number, but was {DslText.Describe(element)}."),
            };
        }
        catch (DndInputException ex)
        {
            throw new DndInputException($"{field}: {ex.Message}", ex);
        }
    }

    /// <summary>A target described by its numbers: the profile's row for the CR fills whatever the spec leaves out.</summary>
    private static ResolvedTarget FromProfile(TargetSpec spec, int level)
    {
        var notes = new List<string>();
        var profile = CompiledBuild.Match(V.Profiles.Set, spec.Profile) ?? V.Profiles.Default;
        var givenCr = ParseCr(spec.Cr, "cr");
        var cr = givenCr ?? Encounters.ChallengeRating.Parse(DslText.Number(level));
        var perAbility = V.Abilities.All.Count(a => spec.Saves?.Get(a) is not null);
        var typicalUsed = spec.SaveBonus is null && perAbility < V.Abilities.All.Count;

        // The row is looked up only when it supplies something (or its CR was asked for), so a fully described target never
        // depends on a profile's table.
        var row = spec.Ac is null || typicalUsed || givenCr is not null ? TargetProfiles.Row(profile, cr) : null;

        string acSource;
        int ac;
        if (spec.Ac is { } givenAc)
        {
            ac = givenAc;
            acSource = "given";
        }
        else
        {
            ac = row!.ArmorClass;
            var about = new[] { row.Basis, givenCr is null ? $"CR = level {DslText.Number(level)}" : null }.OfType<string>().ToList();
            acSource = about.Count == 0 ? row.RowText : $"{row.RowText} ({string.Join("; ", about)})";
            if (row.ArmorClassIsCeiling)
            {
                notes.Add($"The CR 0 row's AC {DslText.Number(ac)} is a ceiling (\"13 or lower\"), used as the AC.");
            }
        }

        var typical = row?.SaveBonus ?? 0;
        var saves = V.Abilities.All.ToDictionary(a => a, a => spec.Saves?.Get(a) ?? spec.SaveBonus ?? typical, StringComparer.Ordinal);
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
            var typicalText = $"{Signed(typical)}, {row!.SaveBonusText}";
            saveSource = perAbility > 0 ? $"given per ability; the rest {typicalText}" : typicalText;
            if (givenCr is null && spec.Ac is not null)
            {
                notes.Add(
                    $"Only ac was given, so the saves use CR {cr} (the CR = level row): {Signed(typical)} on every save not given. " +
                    "Give save_bonus or saves to set them.");
            }
        }

        if (row?.EmpiricalRow is { } medians && (spec.Ac is null || typicalUsed))
        {
            notes.Add(ProfileNote(row, medians, spec.Ac is null, typicalUsed ? (perAbility > 0 ? "the saves not given" : "every save") : null));
        }

        var resistances = Types(spec.Resistances);
        var vulnerabilities = Types(spec.Vulnerabilities);
        var immunities = Types(spec.Immunities);
        OverlapNotes(immunities, resistances, vulnerabilities, notes);

        // The CR a reader should see: the one given, or CR = level when its row supplied the AC or any save.
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
            SaveDice = SaveDice(spec),
            SecondTargetRate = spec.SecondTargetRate ?? 0,
            ChallengeRating = rowCr,
            Row = rowCr is null ? null : row?.DmgRow,
            Profile = rowCr is null ? null : profile,
            ProfileRow = rowCr is null ? null : row,
            CrFollowsLevel = givenCr is null && rowCr is not null,
            Notes = notes,
        };
    }

    /// <summary>
    /// "Target profile mm2024: AC 13 (median 13) and +1 on every save (median mean save bonus 0.67), from 2024 SRD monster
    /// medians for CR 5 (42 monsters), …": what an empirical profile supplied, the unrounded medians beside the numbers
    /// used, and the DMG row the default would have used, so the two profiles can be told apart in one reading.
    /// </summary>
    private static string ProfileNote(TargetProfileRow row, MonsterStatsEmpiricalRow medians, bool ac, string? saves)
    {
        string Raw(double value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        var dmg = TargetProfiles.Row(V.Profiles.Dmg2014, row.ChallengeRating);
        var used = new List<string>();
        var instead = new List<string>();
        if (ac)
        {
            used.Add($"AC {DslText.Number(row.ArmorClass)} (median {Raw(medians.ArmorClass)})");
            instead.Add($"AC {DslText.Number(dmg.ArmorClass)}");
        }

        if (saves is not null)
        {
            used.Add($"{Signed(row.SaveBonus)} on {saves} (median mean save bonus {Raw(medians.MeanSaveBonus)})");
            instead.Add($"{Signed(dmg.SaveBonus)} on saves");
        }

        return
            $"Target profile {row.Profile}: {string.Join(" and ", used)}, from the {row.Citation}, each rounded to a whole number " +
            $"with a half rounded up. The default profile, the {dmg.RowText}, gives {string.Join(" and ", instead)}.";
    }

    /// <summary>
    /// A stat block target: every number from the stat block, each field the spec gives overriding it (listed in one note,
    /// with the stat block's own value beside it), plus notes on what the DPR engine does not read.
    /// </summary>
    private static ResolvedTarget FromStatBlock(TargetSpec spec, int level, StatBlock monster)
    {
        var notes = new List<string>();
        var overrides = new List<string>();
        var name = monster.Name;
        var source = $"{name} stat block";

        int ac;
        string acSource;
        if (spec.Ac is { } givenAc)
        {
            ac = givenAc;
            acSource = "given";
            overrides.Add($"ac {DslText.Number(givenAc)} (stat block {DslText.Number(monster.ArmorClass)})");
        }
        else
        {
            ac = monster.ArmorClass;
            acSource = source;
            if (monster.ArmorClassNote is { } other)
            {
                notes.Add($"{name}'s stat block also gives AC {other}; its first AC, {DslText.Number(ac)}, is used (give ac for another).");
            }
        }

        int Own(string ability) => monster.SaveBonuses.TryGetValue(ability, out var bonus) ? bonus : monster.Modifier(ability);
        var perAbility = V.Abilities.All.Count(a => spec.Saves?.Get(a) is not null);
        var saves = V.Abilities.All.ToDictionary(a => a, a => spec.Saves?.Get(a) ?? spec.SaveBonus ?? Own(a), StringComparer.Ordinal);
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
            saveSource = perAbility > 0 ? $"given per ability; the rest the {source}" : source;
        }

        if (spec.SaveBonus is { } saveBonus && perAbility < V.Abilities.All.Count)
        {
            overrides.Add($"save_bonus {Signed(saveBonus)} (stat block {OwnSaves(Own)})");
        }

        foreach (var ability in V.Abilities.All.Where(a => spec.Saves?.Get(a) is not null))
        {
            overrides.Add($"saves {ability} {Signed(spec.Saves!.Get(ability)!.Value)} (stat block {Signed(Own(ability))})");
        }

        if (spec.Hp is { } hp)
        {
            overrides.Add($"hp {DslText.Number(hp)} (stat block {DslText.Number(monster.HitPoints)})");
        }

        var (resistances, qualifiedResistances) = Adjustments("resistances", spec.Resistances, monster.Resistances, overrides);
        var (vulnerabilities, qualifiedVulnerabilities) = Adjustments("vulnerabilities", spec.Vulnerabilities, monster.Vulnerabilities, overrides);
        var (immunities, qualifiedImmunities) = Adjustments("immunities", spec.Immunities, monster.Immunities, overrides);
        OverlapNotes(immunities, resistances, vulnerabilities, notes);

        var magicResistance = monster.Has(StatBlockValues.TraitKinds.MagicResistance);
        if (spec.MagicResistance is { } givenMagic)
        {
            overrides.Add($"magic_resistance {Bool(givenMagic)} (stat block {Bool(magicResistance)})");
            magicResistance = givenMagic;
        }

        var evasion = monster.Has(StatBlockValues.TraitKinds.Evasion);
        if (spec.Evasion is { } givenEvasion)
        {
            overrides.Add($"evasion {Bool(givenEvasion)} (stat block {Bool(evasion)})");
            evasion = givenEvasion;
        }

        var legendary = monster.LegendaryResistance;
        if (spec.LegendaryResistance is { } givenLegendary)
        {
            overrides.Add($"legendary_resistance {DslText.Number(givenLegendary)} (stat block {DslText.Number(legendary)})");
            legendary = givenLegendary;
        }
        else if (monster.LegendaryResistanceInLair is { } inLair && inLair != legendary)
        {
            notes.Add(
                $"{name}'s Legendary Resistance is {DslText.Number(legendary)} a day ({DslText.Number(inLair)} in its lair); the " +
                "target is not in its lair here, so it has " + DslText.Number(legendary) + ".");
        }

        if (overrides.Count > 0)
        {
            notes.Add($"Given, overriding the {source}: {string.Join("; ", overrides)}.");
        }

        var conditionImmunities = monster.ConditionImmunities.Distinct(StringComparer.Ordinal).ToList();
        var condition = CompiledBuild.Match(V.Conditions.TargetSet, spec.Condition);
        if (condition is not null && conditionImmunities.Contains(condition, StringComparer.Ordinal))
        {
            notes.Add(
                $"{name} is immune to the {condition} condition, but condition {condition} was given: it is used as given (a " +
                "stat block immunity does not overrule an explicit starting condition).");
        }

        var cr = monster.ChallengeRating;
        var levelCr = Encounters.ChallengeRating.Parse(DslText.Number(level));
        if (cr != levelCr)
        {
            var reference = TargetProfiles.Row(V.Profiles.Dmg2014, levelCr);
            notes.Add(
                $"{name} is CR {cr}, {(cr < levelCr ? "below" : "above")} level {DslText.Number(level)}: the level's reference target " +
                $"(CR = level, the {reference.RowText}) has AC {DslText.Number(reference.ArmorClass)} and {Signed(reference.SaveBonus)} on every save.");
        }

        var unread = monster.Traits
            .Where(t => t.Kind is StatBlockValues.TraitKinds.Regeneration or StatBlockValues.TraitKinds.UndeadFortitude
                or StatBlockValues.TraitKinds.Relentless or StatBlockValues.TraitKinds.Reckless)
            .Select(t => t.Name)
            .Concat(monster.Reactions.Where(r => r.Kind == StatBlockValues.ActionKinds.Parry).Select(r => r.Name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (unread.Count > 0)
        {
            notes.Add(
                $"{name}'s {DamageQualifierText.And(unread)} {(unread.Count == 1 ? "is" : "are")} not read by the damage-per-round " +
                "engine (they act over a fight: balance_simulate models them).");
        }

        var cover = CompiledBuild.Match(V.Cover.Set, spec.Cover);
        return new ResolvedTarget
        {
            ArmorClass = ac,
            ArmorClassSource = acSource,
            SaveBonuses = saves,
            SaveBonusSource = saveSource,
            HitPoints = spec.Hp ?? monster.HitPoints,
            Resistances = resistances,
            Vulnerabilities = vulnerabilities,
            Immunities = immunities,
            QualifiedResistances = qualifiedResistances,
            QualifiedVulnerabilities = qualifiedVulnerabilities,
            QualifiedImmunities = qualifiedImmunities,
            MagicResistance = magicResistance,
            Evasion = evasion,
            Condition = condition,
            ConditionImmunities = conditionImmunities,
            Cover = cover,
            CoverBonus = V.Cover.Bonus(cover),
            LegendaryResistance = legendary,
            SaveDice = SaveDice(spec),
            SecondTargetRate = spec.SecondTargetRate ?? 0,
            ChallengeRating = cr,
            Monster = monster,
            Notes = notes,
        };
    }

    /// <summary>
    /// One kind of damage adjustment: the spec's list when given (it replaces the stat block's whole list, qualified entries
    /// included, with an override note), else the stat block's unqualified types and its qualified entries.
    /// </summary>
    private static (List<string> Plain, List<DamageAdjustment> Qualified) Adjustments(
        string field, IReadOnlyList<string>? given, IReadOnlyList<DamageAdjustment> own, List<string> overrides)
    {
        var ownPlain = own.Where(e => e.Qualifier is null).Select(e => e.DamageType).Distinct().Order(StringComparer.Ordinal).ToList();
        var ownQualified = own.Where(e => e.Qualifier is not null).DistinctBy(e => (e.DamageType, e.Qualifier)).ToList();
        if (given is null)
        {
            return (ownPlain, ownQualified);
        }

        var plain = Types(given);
        overrides.Add($"{field} {(plain.Count == 0 ? "none" : string.Join(", ", plain))} (stat block {DamageQualifierText.Describe(ownPlain, ownQualified)})");
        return (plain, []);
    }

    private static void OverlapNotes(List<string> immunities, List<string> resistances, List<string> vulnerabilities, List<string> notes)
    {
        foreach (var type in immunities.Intersect(resistances.Concat(vulnerabilities)).Distinct())
        {
            notes.Add($"{type} is both an immunity and a resistance or vulnerability; immunity wins (no {type} damage).");
        }
    }

    // "Str +5, Dex -1, …" or "+2 on every save".
    private static string OwnSaves(Func<string, int> own)
    {
        var values = V.Abilities.All.Select(own).ToList();
        return values.Distinct().Count() == 1
            ? $"{Signed(values[0])} on every save"
            : string.Join(", ", V.Abilities.All.Select(a => $"{V.Abilities.Display(a)} {Signed(own(a))}"));
    }

    /// <summary>The damage each attack (its riders included) and each save effect deals, by type, with what it is made with.</summary>
    private static List<(string Name, IReadOnlyList<string> Types, DamageProperties Properties)> DamageSources(ResolvedBuild build)
    {
        var sources = new List<(string, IReadOnlyList<string>, DamageProperties)>();
        foreach (var attack in build.Attacks)
        {
            var types = new List<string>();
            if (attack.DamageType is { } own)
            {
                types.Add(own);
            }

            // A rider deals its own type, or the attack's when it has none; either way it is the attack's hit.
            types.AddRange(build.Riders.Where(r => r.AppliesTo(attack) && r.DamageType is not null).Select(r => r.DamageType!));
            if (types.Count > 0)
            {
                sources.Add((attack.Name, types.Distinct().ToList(), DamageProperties.Of(attack)));
            }
        }

        foreach (var effect in build.SaveEffects.Where(e => e.DamageType is not null && e.Damage is not null))
        {
            sources.Add((effect.Source.Label, [effect.DamageType!], DamageProperties.Of(effect)));
        }

        return sources;
    }

    private static string AppliedWhy(string qualifier) => qualifier switch
    {
        Q.Nonmagical => "not magical",
        Q.NonmagicalNotSilvered => "neither magical nor silvered",
        Q.NonmagicalNotAdamantine => "neither magical nor adamantine",
        _ => qualifier,
    };

    private static string BypassWhy(string qualifier, (string Name, IReadOnlyList<string> Types, DamageProperties Properties) source)
    {
        var why = new List<string>();
        if (source.Properties.Magical)
        {
            why.Add("magical");
        }

        if (qualifier == Q.NonmagicalNotSilvered && source.Properties.Silvered)
        {
            why.Add("silvered");
        }

        if (qualifier == Q.NonmagicalNotAdamantine && source.Properties.Adamantine)
        {
            why.Add("adamantine");
        }

        return string.Join(" and ", why);
    }

    private static DamageFormula? SaveDice(TargetSpec spec) =>
        spec.SaveDice is null ? null : DamageFormula.ParseBonusDice(spec.SaveDice, "save_dice", SaveDiceFlatHint);

    private static List<string> Types(IReadOnlyList<string>? types) =>
        (types ?? []).Select(t => CompiledBuild.Match(V.DamageTypes.Set, t)!).Distinct().Order(StringComparer.Ordinal).ToList();

    private static string Signed(int value) => value >= 0 ? $"+{DslText.Number(value)}" : DslText.Number(value);

    private static string Bool(bool value) => value ? "true" : "false";
}

/// <summary>Field checks of a <see cref="TargetSpec"/>: ranges, wire values, the CR, the save dice, and monster's companions.</summary>
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

        RuleFor(t => t.Monster).Custom((monster, context) =>
        {
            var problems = new Problems<TargetSpec>(context, string.Empty);
            var target = context.InstanceToValidate;
            problems.Known(V.Profiles.Set, "profile", target.Profile);
            if (monster is null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(monster) || !DslText.IsOneLine(monster, DslLimits.MaxMonsterTextLength))
            {
                problems.Add(
                    $"monster \"{DslText.Echo(monster)}\" is not a monster ref or name; give one line of at most " +
                    $"{DslText.Number(DslLimits.MaxMonsterTextLength)} characters, e.g. \"2024/monster/ogre\" or \"ogre\".");
            }

            if (target.Cr is not null)
            {
                problems.Add("give monster or cr, not both: the stat block has its own CR (give ac, saves or save_bonus to change its numbers).");
            }

            if (target.Profile is not null)
            {
                problems.Add("give monster or profile, not both: profile is the table for a target without a stat block.");
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
