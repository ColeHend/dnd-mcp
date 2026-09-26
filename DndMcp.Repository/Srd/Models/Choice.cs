namespace DndMcp.Repository.Srd.Models;

/// <summary>
/// 5e-database's generic "choose N of these" shape (<c>{choose, type, desc?, from: {option_set_type, options}}</c>),
/// typed by the option it offers.
///
/// <para>
/// Monsters use it in three places with three different option shapes: multiattack <c>action_options</c>, 2014 breath
/// weapon <c>options</c>, and 2014 damage choices. A single untyped Choice (a <c>JsonElement</c>) would push that
/// variation into every consumer. The simulator would then have to re-parse what a multiattack actually allows, which
/// is exactly where the "replace one attack" rule gets lost. Choices in class and background data (equipment,
/// proficiencies) are deeper unions and stay untyped until Phase 2 needs them.
/// </para>
/// </summary>
/// <typeparam name="TOption">The shape of one option.</typeparam>
public sealed class Choice<TOption>
{
    /// <summary>How many options are picked. In a multiattack this is added to the fixed <c>actions[]</c> routine.</summary>
    public required int Choose { get; init; }

    /// <summary>What is being chosen; one of <see cref="ChoiceTypes"/>.</summary>
    public required string Type { get; init; }

    /// <summary>
    /// Free-text qualifier. It is 2024-only and is usually <c>"Any combination"</c> (47 multiattacks): pick
    /// <see cref="Choose"/> times, repeats allowed.
    /// </summary>
    public string? Desc { get; init; }

    public required OptionSet<TOption> From { get; init; }
}

/// <summary>The <c>from</c> part of a <see cref="Choice{TOption}"/>.</summary>
/// <typeparam name="TOption">The shape of one option.</typeparam>
public sealed class OptionSet<TOption>
{
    /// <summary>Always <c>"options_array"</c> in monster data; other set types exist only in class and equipment data.</summary>
    public required string OptionSetType { get; init; }

    public required IReadOnlyList<TOption> Options { get; init; }
}

/// <summary>
/// <see cref="Choice{TOption}.Type"/> values seen in monster data. They are string constants because the discriminator
/// is upstream's wire value, not something this code controls.
/// </summary>
public static class ChoiceTypes
{
    /// <summary>Multiattack <c>action_options</c>.</summary>
    public const string Action = "action";

    /// <summary>2014 breath-weapon <c>options</c>.</summary>
    public const string Attack = "attack";

    /// <summary>2014 damage choices inside <c>damage[]</c>.</summary>
    public const string Damage = "damage";
}
