using System.Globalization;

namespace DndMcp.Domain.Features;

/// <summary>
/// An <c>amount</c> field's value before a level is known: a whole number, the proficiency bonus (<c>"pb"</c>), or an
/// ability's modifier (<c>"cha"</c>: Agonizing Blast). Kept symbolic so one spec resolves correctly at every level: 2024
/// Great Weapon Master's <c>"pb"</c> is +2 at level 4 and +3 at level 5 without a step map.
/// </summary>
public readonly record struct DslAmount
{
    /// <summary>The wire keyword for the proficiency bonus.</summary>
    public const string ProficiencyBonusKeyword = "pb";

    private DslAmount(int value, string? reference)
    {
        Value = value;
        Reference = reference;
    }

    /// <summary>The whole number, when <see cref="Reference"/> is null.</summary>
    public int Value { get; }

    /// <summary><c>"pb"</c> or an ability key, or null for a whole number.</summary>
    public string? Reference { get; }

    public static DslAmount ProficiencyBonus { get; } = new(0, ProficiencyBonusKeyword);

    public static DslAmount Of(int value) => new(value, null);

    public static DslAmount AbilityModifier(string ability)
    {
        if (!DslValues.Abilities.All.Contains(ability))
        {
            throw new ArgumentOutOfRangeException(nameof(ability), ability, "Not an ability key.");
        }

        return new DslAmount(0, ability);
    }

    /// <summary>The number at a level: the value, the proficiency bonus, or the ability's modifier.</summary>
    public int Resolve(int proficiencyBonus, ResolvedAbilities abilities) => Reference switch
    {
        null => Value,
        ProficiencyBonusKeyword => proficiencyBonus,
        var ability => abilities.Modifier(ability),
    };

    /// <summary>"3", "pb", "cha": as it is written in the DSL.</summary>
    public override string ToString() => Reference ?? Value.ToString(CultureInfo.InvariantCulture);
}
