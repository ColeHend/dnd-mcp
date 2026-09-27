namespace DndMcp.Repository.Srd;

/// <summary>
/// The kinds of SRD document the rules index holds, as the wire strings tools accept and return
/// (<c>2024/spell/fireball</c>), and where each comes from.
///
/// <para>
/// Strings, not an enum, because they are stored: every srd.db row and every ref the model is shown carries one.
/// They are singular and kebab-case, which is how people name them ("a spell", "a magic item"), unlike upstream's
/// plural API resource names (<c>spells</c>, <c>magic-items</c>). <see cref="All"/> maps between the two, since the
/// vendored JSON links records by API URL (<c>/api/2014/traits/darkvision</c>) and those links must become refs.
/// </para>
/// <para>
/// Not every kind exists in both editions. 2014 has races, subraces and rules; 2024 renamed races to species and
/// has poisons and weapon masteries. 2024 rules come from the Rules Glossary (<c>content/rules-glossary-2024.json</c>),
/// not from 5e-database, which has no 2024 rules at all. A kind that exists in only one edition has a null file name
/// in the other.
/// </para>
/// </summary>
public static class SrdKinds
{
    public const string AbilityScore = "ability-score";
    public const string Alignment = "alignment";
    public const string Background = "background";
    public const string Class = "class";
    public const string Condition = "condition";
    public const string DamageType = "damage-type";
    public const string Equipment = "equipment";
    public const string EquipmentCategory = "equipment-category";
    public const string Feat = "feat";
    public const string Feature = "feature";
    public const string Language = "language";
    public const string Level = "level";
    public const string MagicItem = "magic-item";
    public const string MagicSchool = "magic-school";
    public const string Monster = "monster";
    public const string Poison = "poison";
    public const string Proficiency = "proficiency";
    public const string Race = "race";
    public const string Rule = "rule";
    public const string Skill = "skill";
    public const string Species = "species";
    public const string Spell = "spell";
    public const string Subclass = "subclass";
    public const string Subrace = "subrace";
    public const string Subspecies = "subspecies";
    public const string Trait = "trait";
    public const string WeaponMastery = "weapon-mastery";
    public const string WeaponProperty = "weapon-property";

    /// <summary>File name of the 2024 Rules Glossary, directly under <c>content/</c> (outside the 5e-database manifest).</summary>
    public const string RulesGlossary2024FileName = "rules-glossary-2024.json";

    /// <summary>
    /// Every kind, alphabetical. <see cref="SrdKindInfo.ApiResource"/> is the URL segment upstream uses; level records
    /// are the exception, living under <c>/classes/{c}/levels/{n}</c> and <c>/subclasses/{s}/levels/{n}</c>, so theirs
    /// is null and <see cref="SrdRef.FromApiUrl"/> handles them separately.
    /// </summary>
    public static IReadOnlyList<SrdKindInfo> All { get; } =
    [
        new(AbilityScore, "ability-scores", "5e-SRD-Ability-Scores.json", "5e-SRD-Ability-Scores.json"),
        new(Alignment, "alignments", "5e-SRD-Alignments.json", "5e-SRD-Alignments.json"),
        new(Background, "backgrounds", "5e-SRD-Backgrounds.json", "5e-SRD-Backgrounds.json"),
        new(Class, "classes", "5e-SRD-Classes.json", "5e-SRD-Classes.json"),
        new(Condition, "conditions", "5e-SRD-Conditions.json", "5e-SRD-Conditions.json"),
        new(DamageType, "damage-types", "5e-SRD-Damage-Types.json", "5e-SRD-Damage-Types.json"),
        new(Equipment, "equipment", "5e-SRD-Equipment.json", "5e-SRD-Equipment.json"),
        new(EquipmentCategory, "equipment-categories", "5e-SRD-Equipment-Categories.json", "5e-SRD-Equipment-Categories.json"),
        new(Feat, "feats", "5e-SRD-Feats.json", "5e-SRD-Feats.json"),
        new(Feature, "features", "5e-SRD-Features.json", "5e-SRD-Features.json"),
        new(Language, "languages", "5e-SRD-Languages.json", "5e-SRD-Languages.json"),
        new(Level, null, "5e-SRD-Levels.json", "5e-SRD-Levels.json"),
        new(MagicItem, "magic-items", "5e-SRD-Magic-Items.json", "5e-SRD-Magic-Items.json"),
        new(MagicSchool, "magic-schools", "5e-SRD-Magic-Schools.json", "5e-SRD-Magic-Schools.json"),
        new(Monster, "monsters", "5e-SRD-Monsters.json", "5e-SRD-Monsters.json"),
        new(Poison, "poisons", null, "5e-SRD-Poisons.json"),
        new(Proficiency, "proficiencies", "5e-SRD-Proficiencies.json", "5e-SRD-Proficiencies.json"),
        new(Race, "races", "5e-SRD-Races.json", null),
        new(Rule, "rules", "5e-SRD-Rules.json", null),
        new(Skill, "skills", "5e-SRD-Skills.json", "5e-SRD-Skills.json"),
        new(Species, "species", null, "5e-SRD-Species.json"),
        new(Spell, "spells", "5e-SRD-Spells.json", "5e-SRD-Spells.json"),
        new(Subclass, "subclasses", "5e-SRD-Subclasses.json", "5e-SRD-Subclasses.json"),
        new(Subrace, "subraces", "5e-SRD-Subraces.json", null),
        new(Subspecies, "subspecies", null, "5e-SRD-Subspecies.json"),
        new(Trait, "traits", "5e-SRD-Traits.json", "5e-SRD-Traits.json"),
        new(WeaponMastery, "weapon-mastery-properties", null, "5e-SRD-Weapon-Mastery-Properties.json"),
        new(WeaponProperty, "weapon-properties", "5e-SRD-Weapon-Properties.json", "5e-SRD-Weapon-Properties.json"),
    ];

    private static readonly Dictionary<string, SrdKindInfo> ByName =
        All.ToDictionary(k => k.Name, StringComparer.Ordinal);

    private static readonly Dictionary<string, SrdKindInfo> ByApiResource =
        All.Where(k => k.ApiResource is not null).ToDictionary(k => k.ApiResource!, StringComparer.Ordinal);

    /// <summary>The kind with this exact wire name, or null. Input normalisation (plurals, synonyms) happens elsewhere.</summary>
    public static SrdKindInfo? Find(string name) => ByName.GetValueOrDefault(name);

    /// <summary>The kind whose upstream API URL segment is <paramref name="apiResource"/> (e.g. <c>magic-items</c>), or null.</summary>
    public static SrdKindInfo? FromApiResource(string apiResource) => ByApiResource.GetValueOrDefault(apiResource);

    /// <summary>
    /// True when <paramref name="kind"/> has documents in <paramref name="edition"/>. 2024 rules come from the glossary,
    /// so they exist although 5e-database has no 2024 rules file.
    /// </summary>
    public static bool ExistsIn(string kind, string edition) =>
        Find(kind) is { } info && (info.FileFor(edition) is not null || (kind == Rule && edition == SrdEdition.Edition2024));
}

/// <summary>One row of <see cref="SrdKinds.All"/>.</summary>
/// <param name="Name">Wire name, e.g. <c>magic-item</c>.</param>
/// <param name="ApiResource">Upstream URL segment, e.g. <c>magic-items</c>; null for level records.</param>
/// <param name="File2014">Vendored 2014 file name, or null when the kind has no 2014 file.</param>
/// <param name="File2024">Vendored 2024 file name, or null when the kind has no 2024 file.</param>
public sealed record SrdKindInfo(string Name, string? ApiResource, string? File2014, string? File2024)
{
    public string? FileFor(string edition) => edition switch
    {
        SrdEdition.Edition2014 => File2014,
        SrdEdition.Edition2024 => File2024,
        _ => null,
    };
}
