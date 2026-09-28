using System.Text.Json;
using DndMcp.Domain.Features;
using DndMcp.Repository.Srd.Index;
using DndMcp.Repository.Srd.Models;

namespace DndMcp.Repository.Srd.Combatants;

/// <summary>
/// One monster document read into the shape the normalizer works on: the two editions' typed models folded into one
/// view, keeping every edition-specific signal the normalizer needs (a spell's level MEANING, the 2014 breath options,
/// the 2014 damage choices) as explicit fields rather than as "which model did it come from".
///
/// <para>
/// Why not normalize straight from <see cref="Monster2014"/> / <see cref="Monster2024"/>: every rule below would be
/// written twice, and the copies would drift. The typed models stay the strict readers (unknown shapes fail their
/// tests); this view only re-labels what they read.
/// </para>
/// </summary>
internal sealed record MonsterRecord
{
    public required string Edition { get; init; }

    public required string Index { get; init; }

    public required string Name { get; init; }

    public required string Size { get; init; }

    public required string CreatureType { get; init; }

    /// <summary>Armour class entries, first is the default; each with its source text ("natural armor", "with mage armor").</summary>
    public required IReadOnlyList<(int Value, string? Source)> ArmorClass { get; init; }

    public required int HitPoints { get; init; }

    public required string HitPointsRoll { get; init; }

    public required ResolvedAbilities Abilities { get; init; }

    public required IReadOnlyList<MonsterProficiency> Proficiencies { get; init; }

    public required MonsterSpeed Speed { get; init; }

    public required IReadOnlyList<string> Resistances { get; init; }

    public required IReadOnlyList<string> Immunities { get; init; }

    public required IReadOnlyList<string> Vulnerabilities { get; init; }

    public required IReadOnlyList<ApiReference> ConditionImmunities { get; init; }

    public required double ChallengeRating { get; init; }

    public required int ProficiencyBonus { get; init; }

    public required int Xp { get; init; }

    public int? XpInLair { get; init; }

    public required IReadOnlyList<RecordAction> Traits { get; init; }

    public required IReadOnlyList<RecordAction> Actions { get; init; }

    public required IReadOnlyList<RecordAction> BonusActions { get; init; }

    public required IReadOnlyList<RecordAction> Reactions { get; init; }

    public required IReadOnlyList<RecordAction> Legendary { get; init; }

    public required IReadOnlyList<string> Forms { get; init; }

    /// <summary>The creature's short name as its prose uses it ("the dragon", "the vampire"), lower case, or null.</summary>
    public string ShortName => ProseText.ShortName(Name);

    /// <summary>Reads a srd.db monster document with the strict typed model of its edition.</summary>
    /// <exception cref="InvalidDataException">The JSON is not a monster record of that edition.</exception>
    public static MonsterRecord Read(SrdDocument document)
    {
        try
        {
            return document.Edition switch
            {
                SrdEdition.Edition2014 => From2014(JsonSerializer.Deserialize<Monster2014>(document.Json, SrdJson.Options)!),
                SrdEdition.Edition2024 => From2024(JsonSerializer.Deserialize<Monster2024>(document.Json, SrdJson.Options)!),
                _ => throw new InvalidDataException($"`{document.Ref}` has edition {document.Edition}, which has no monster model."),
            };
        }
        catch (Exception ex) when (ex is JsonException or NullReferenceException or InvalidOperationException)
        {
            throw new InvalidDataException($"`{document.Ref}` is not a readable {document.Edition} monster record: {ex.Message}", ex);
        }
    }

    private static MonsterRecord From2014(Monster2014 m) => new()
    {
        Edition = SrdEdition.Edition2014,
        Index = m.Index,
        Name = m.Name,
        Size = m.Size,
        CreatureType = m.Type,
        ArmorClass = m.ArmorClass.Select(ac => (ac.Value, Describe2014(ac))).ToList(),
        HitPoints = m.HitPoints,
        HitPointsRoll = m.HitPointsRoll,
        Abilities = new ResolvedAbilities(m.Strength, m.Dexterity, m.Constitution, m.Intelligence, m.Wisdom, m.Charisma),
        Proficiencies = m.Proficiencies,
        Speed = m.Speed,
        Resistances = m.DamageResistances,
        Immunities = m.DamageImmunities,
        Vulnerabilities = m.DamageVulnerabilities,
        ConditionImmunities = m.ConditionImmunities,
        ChallengeRating = m.ChallengeRating,
        ProficiencyBonus = m.ProficiencyBonus,
        Xp = m.Xp,
        XpInLair = null,
        Traits = (m.SpecialAbilities ?? []).Select(From2014).ToList(),
        Actions = (m.Actions ?? []).Select(From2014).ToList(),
        BonusActions = [],
        Reactions = (m.Reactions ?? []).Select(From2014).ToList(),
        Legendary = (m.LegendaryActions ?? []).Select(From2014).ToList(),
        Forms = (m.Forms ?? []).Select(f => f.Index).ToList(),
    };

    private static MonsterRecord From2024(Monster2024 m) => new()
    {
        Edition = SrdEdition.Edition2024,
        Index = m.Index,
        Name = m.Name,
        Size = m.Size,
        CreatureType = m.Type,
        ArmorClass = m.ArmorClass.Select(ac => (ac.Value, ac.Armor is { Count: > 0 } armor ? string.Join(", ", armor.Select(a => a.Name)) : null)).ToList(),
        HitPoints = m.HitPoints,
        HitPointsRoll = m.HitPointsRoll,
        Abilities = new ResolvedAbilities(m.Strength, m.Dexterity, m.Constitution, m.Intelligence, m.Wisdom, m.Charisma),
        Proficiencies = m.Proficiencies,
        Speed = m.Speed,
        Resistances = m.DamageResistances,
        Immunities = m.DamageImmunities,
        Vulnerabilities = m.DamageVulnerabilities,
        ConditionImmunities = m.ConditionImmunities,
        ChallengeRating = m.ChallengeRating,
        ProficiencyBonus = m.ProficiencyBonus,
        Xp = m.Xp,
        XpInLair = m.XpInLair,
        Traits = (m.SpecialAbilities ?? []).Select(From2024).ToList(),
        Actions = (m.Actions ?? []).Select(From2024).ToList(),
        BonusActions = (m.BonusActions ?? []).Select(From2024).ToList(),
        Reactions = (m.Reactions ?? []).Select(From2024).ToList(),
        Legendary = (m.LegendaryActions ?? []).Select(From2024).ToList(),
        Forms = (m.Forms ?? []).Select(f => f.Index).ToList(),
    };

    private static string? Describe2014(MonsterArmorClass2014 ac) => ac.Type switch
    {
        "natural" => "natural armor",
        "dex" => null,
        "armor" => ac.Armor is { Count: > 0 } armor ? string.Join(", ", armor.Select(a => a.Name)) : ac.Desc ?? "armor",
        "spell" => ac.Spell is { } spell ? "with " + spell.Name : "with a spell",
        "condition" => ac.Condition is { } condition ? "while " + condition.Name.ToLowerInvariant() : "conditional",
        _ => ac.Desc ?? ac.Type,
    };

    private static RecordAction From2014(MonsterAction2014 a) => new()
    {
        Name = a.Name,
        Desc = a.Desc,
        AttackBonus = a.AttackBonus,
        Damage = (a.Damage ?? []).Where(d => !d.IsChoice).Select(d => d.Damage!).ToList(),
        DamageChoices = (a.Damage ?? []).Where(d => d.IsChoice).Select(d => d.Choice!).ToList(),
        Dc = a.Dc,
        Usage = a.Usage,
        MultiattackActions = a.Actions,
        MultiattackOptions = a.ActionOptions,
        Spellcasting = a.Spellcasting is { } sc
            ? new RecordSpellcasting(
                sc.Ability.Index,
                sc.Dc,
                sc.Modifier,
                sc.Level,
                sc.Slots,
                sc.Spells.Select(s => new RecordSpell(SlugFromUrl(s.Url), s.Name, s.Level, LevelIsCastLevel: false, s.Usage, s.Notes)).ToList())
            : null,
        BreathOptions = a.Options,
        SubAttacks = a.Attacks,
    };

    private static RecordAction From2024(MonsterAction2024 a) => new()
    {
        Name = a.Name,
        Desc = a.Desc,
        AttackBonus = a.AttackBonus,
        Damage = a.Damage ?? [],
        DamageChoices = [],
        Dc = a.Dc,
        Usage = a.Usage,
        MultiattackActions = a.Actions,
        MultiattackOptions = a.ActionOptions,
        Spellcasting = a.Spellcasting is { } sc
            ? new RecordSpellcasting(
                sc.Ability.Index,
                sc.Dc,
                sc.Modifier,
                CasterLevel: null,
                Slots: null,
                sc.Spells.Select(s => new RecordSpell(s.Index, s.Name, s.CastLevel, LevelIsCastLevel: true, s.Usage, s.Notes)).ToList())
            : null,
        BreathOptions = null,
        SubAttacks = null,
    };

    /// <summary>2014 spell references carry no index; the slug is the URL's last segment.</summary>
    internal static string SlugFromUrl(string url) => url.TrimEnd('/').Split('/')[^1];
}

/// <summary>One trait, action, bonus action, reaction or legendary action of either edition.</summary>
internal sealed record RecordAction
{
    public required string Name { get; init; }

    public required string Desc { get; init; }

    public int? AttackBonus { get; init; }

    /// <summary>Plain damage entries in data order (2014 Choices are in <see cref="DamageChoices"/>).</summary>
    public required IReadOnlyList<MonsterDamage> Damage { get; init; }

    public required IReadOnlyList<Choice<DamageOption2014>> DamageChoices { get; init; }

    public MonsterDifficultyClass? Dc { get; init; }

    public MonsterUsage? Usage { get; init; }

    public IReadOnlyList<MultiattackAction>? MultiattackActions { get; init; }

    public Choice<MultiattackOption>? MultiattackOptions { get; init; }

    public RecordSpellcasting? Spellcasting { get; init; }

    /// <summary>2014 dragons' "Breath Weapons" choice.</summary>
    public Choice<BreathOption2014>? BreathOptions { get; init; }

    /// <summary>2014 androsphinx roars.</summary>
    public IReadOnlyList<MonsterSubAttack2014>? SubAttacks { get; init; }

    public bool IsMultiattack => Name.Equals("Multiattack", StringComparison.OrdinalIgnoreCase) &&
                                 (MultiattackActions is not null || MultiattackOptions is not null);
}

/// <summary>A spellcasting block of either edition.</summary>
/// <param name="CasterLevel">2014 slot casters' level ("an 18th-level spellcaster"): what their cantrips scale by.</param>
/// <param name="Slots">2014 slot casters' slots per spell level.</param>
internal sealed record RecordSpellcasting(
    string Ability,
    int? Dc,
    int? Modifier,
    int? CasterLevel,
    IReadOnlyDictionary<int, int>? Slots,
    IReadOnlyList<RecordSpell> Spells);

/// <param name="Level">The data's level: the spell's own level in 2014, the level it is CAST at in 2024.</param>
internal sealed record RecordSpell(string Slug, string Name, int Level, bool LevelIsCastLevel, MonsterUsage? Usage, string? Notes);
