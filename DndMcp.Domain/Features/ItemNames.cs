using FluentValidation;

namespace DndMcp.Domain.Features;

/// <summary>
/// An item a validation message names: an attack or a modifier by its position in the build being validated, an
/// ability score, or the fighting style.
/// </summary>
/// <param name="List">"attacks", "modifiers", "abilities" or "fighting_style".</param>
/// <param name="Number">The 1-based position in the validated build's list (0 for abilities and the fighting style).</param>
/// <param name="Label">The attack's name, the modifier's kind and name, the ability key or the style.</param>
internal readonly record struct ItemRef(string List, int Number, string Label)
{
    public const string Attacks = "attacks";
    public const string Modifiers = "modifiers";
    public const string Abilities = "abilities";
    public const string FightingStyle = "fighting_style";

    public static ItemRef Of(CompiledAttack attack) => new(Attacks, attack.Number, $" ({DslText.Echo(attack.Name)})");

    public static ItemRef Of(AttackItem item) =>
        new(Attacks, item.Number, item.Spec?.Name is { } name && !string.IsNullOrWhiteSpace(name) ? $" ({DslText.Echo(name.Trim())})" : string.Empty);

    public static ItemRef Of(CompiledModifier modifier) => Of(modifier.Ref);

    public static ItemRef Of(ModifierRef modifier) => new(Modifiers, modifier.Number, ModifierLabel(modifier.Kind, modifier.Name));

    public static ItemRef Of(ModifierItem item) => new(Modifiers, item.Number, ModifierLabel(item.Kind, item.Spec?.Name));

    public static ItemRef Ability(string ability) => new(Abilities, 0, ability);

    public static ItemRef Style(string style) => new(FightingStyle, 0, style);

    /// <summary>The text for position <paramref name="number"/>: "attacks item 2 (Greatsword)", "abilities str".</summary>
    public string Text(int number) => List switch
    {
        Abilities => $"abilities {Label}",
        FightingStyle => $"fighting_style {Label}",
        _ => $"{List} item {DslText.Number(number)}{Label}",
    };

    // " (extra_damage \"Hex\")", " (extra_damage)", or nothing for a modifier whose kind is missing or unknown: the
    // parenthesis ModifierItem.WhereOf writes.
    private static string ModifierLabel(string? kind, string? name) =>
        kind is null
            ? string.Empty
            : string.IsNullOrWhiteSpace(name) ? $" ({kind})" : $" ({kind} \"{DslText.Echo(name.Trim())}\")";
}

/// <summary>Where one item of a merged build came from: the feature (numbered in the feature's own list) or the baseline.</summary>
internal readonly record struct ItemOrigin(bool FromFeature, int Number);

/// <summary>
/// Where every item of a baseline + feature merge (<see cref="FeatureMerge.MergeWithOrigins"/>) came from: each merged
/// attack and modifier by position, the ability scores and the fighting style the feature set, and how to call the
/// baseline's items ("baseline", or "the warlock_baseline preset's" when the baseline is a preset the model never wrote
/// out).
/// </summary>
internal sealed record ItemOrigins(
    IReadOnlyList<ItemOrigin> Attacks,
    IReadOnlyList<ItemOrigin> Modifiers,
    IReadOnlySet<string> FeatureAbilities,
    bool FeatureFightingStyle,
    string BaselineSource);

/// <summary>
/// How validation messages name a build's items.
///
/// <para>
/// <b>Why not the merged position.</b> <c>balance_compare</c>'s variant is baseline + feature merged into one build and
/// validated as one; numbered by the merged lists, "modifiers item 3" is an item the model never wrote in a one-item
/// feature. With <see cref="ItemOrigins"/> each item is named in the list the model wrote it in. A message whose items all
/// come from the feature names them plainly (the error already says "Invalid feature"); one that also names a baseline
/// item prefixes every item with its list ("baseline modifiers item 2 (to_hit \"Bless\") and feature modifiers item 1
/// (…)"). The merged numbers themselves never change: the engine keys removed and unlimited modifiers, the day
/// horizon's features and every result label on them.
/// </para>
/// <para>
/// The decision is made per message from the items it names, never by rewriting finished text. Without origins (a build,
/// a baseline, a whole variant) every item keeps its position, exactly as before.
/// </para>
/// </summary>
internal sealed class ItemNames
{
    private const string ContextKey = "dnd-mcp:item-names";

    private readonly ItemOrigins? _origins;

    private ItemNames(ItemOrigins? origins) => _origins = origins;

    /// <summary>Items named by their position in the build validated.</summary>
    public static ItemNames Positional { get; } = new(null);

    /// <summary>Items of a merged variant named by where they came from.</summary>
    public static ItemNames Merged(ItemOrigins origins) => new(origins ?? throw new ArgumentNullException(nameof(origins)));

    /// <summary>The naming stored in a validation's root context (<see cref="Attach{T}"/>), or <see cref="Positional"/>.</summary>
    public static ItemNames From<T>(ValidationContext<T> context) =>
        context.RootContextData.TryGetValue(ContextKey, out var names) && names is ItemNames itemNames ? itemNames : Positional;

    /// <summary>A validation context for <paramref name="instance"/> whose rules name items this way.</summary>
    public ValidationContext<T> Attach<T>(T instance)
    {
        var context = new ValidationContext<T>(instance);
        context.RootContextData[ContextKey] = this;
        return context;
    }

    /// <summary>One item named alone.</summary>
    public string One(ItemRef item) => Names([item])[0];

    /// <summary>"A and B": items named together.</summary>
    public string Join(IEnumerable<ItemRef> items, string separator = " and ") => string.Join(separator, Names(items.ToList()));

    /// <summary>
    /// The items of one message, in order: plain when they all come from one list the error already names, prefixed with
    /// their list ("baseline …", "feature …") when a merged build's message mixes the two or names the baseline's alone.
    /// </summary>
    public IReadOnlyList<string> Names(IReadOnlyList<ItemRef> items)
    {
        if (_origins is null)
        {
            return items.Select(i => i.Text(i.Number)).ToList();
        }

        var mapped = items.Select(i => (Item: i, Origin: Origin(i))).ToList();
        var prefix = mapped.Any(m => !m.Origin.FromFeature);
        return mapped
            .Select(m => (prefix ? (m.Origin.FromFeature ? "feature " : _origins.BaselineSource + " ") : string.Empty) + m.Item.Text(m.Origin.Number))
            .ToList();
    }

    /// <summary>
    /// "attacks has 11 items; at most 10 are accepted.", or for a merged build "the baseline and feature together have 11
    /// attacks; …": a list limit only the merge can break is not an item of either list.
    /// </summary>
    public string TooMany(string list, int count, int max) =>
        _origins is null
            ? $"{list} has {DslText.Number(count)} items; at most {DslText.Number(max)} are accepted."
            : $"{BaselineOwner} and the feature together have {DslText.Number(count)} {list}; at most {DslText.Number(max)} are accepted.";

    // "the baseline", or "the warlock_baseline preset" for "the warlock_baseline preset's".
    private string BaselineOwner =>
        _origins!.BaselineSource.EndsWith("'s", StringComparison.Ordinal) ? _origins.BaselineSource[..^2] : $"the {_origins.BaselineSource}";

    private ItemOrigin Origin(ItemRef item)
    {
        var origins = _origins!;
        return item.List switch
        {
            ItemRef.Attacks when item.Number >= 1 && item.Number <= origins.Attacks.Count => origins.Attacks[item.Number - 1],
            ItemRef.Modifiers when item.Number >= 1 && item.Number <= origins.Modifiers.Count => origins.Modifiers[item.Number - 1],
            ItemRef.Abilities => new ItemOrigin(origins.FeatureAbilities.Contains(item.Label), 0),
            ItemRef.FightingStyle => new ItemOrigin(origins.FeatureFightingStyle, 0),
            _ => new ItemOrigin(false, item.Number),
        };
    }
}
