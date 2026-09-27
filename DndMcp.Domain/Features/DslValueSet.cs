using System.Diagnostics.CodeAnalysis;

namespace DndMcp.Domain.Features;

/// <summary>
/// One closed vocabulary of the feature DSL (the damage types, the modifier kinds, …): its canonical wire values, in the
/// order messages list them, and the forgiving match a model's spelling goes through.
///
/// <para>
/// <b>Matching ignores case, spaces, hyphens and underscores</b> ("Two Handed", "two_handed" and "two-handed" are one
/// property; "First-Hit-Per-Turn" is <c>first_hit_per_turn</c>), plus a few listed aliases ("strength" for <c>str</c>).
/// A model mixes snake_case and kebab-case freely, and a refusal over a separator costs a round trip that teaches it
/// nothing. What the resolved build carries is always the canonical value, so stored DSL and results use one spelling.
/// Two canonical values of one set must never share a key; <c>DslValuesTests</c> pins that for every set, because a
/// collision would silently turn one value into the other.
/// </para>
/// </summary>
public sealed class DslValueSet
{
    private readonly Dictionary<string, string> _byKey = new(StringComparer.Ordinal);

    /// <param name="what">What one value is called in messages: "damage type", "modifier kind".</param>
    /// <param name="values">The canonical values, in the order messages list them.</param>
    /// <param name="aliases">Extra spellings (already any case) mapped to a canonical value.</param>
    public DslValueSet(string what, IReadOnlyList<string> values, IReadOnlyDictionary<string, string>? aliases = null)
    {
        What = what;
        Values = values;
        foreach (var value in values)
        {
            if (!_byKey.TryAdd(Key(value), value))
            {
                throw new InvalidOperationException($"The {what} values \"{_byKey[Key(value)]}\" and \"{value}\" collide when matched.");
            }
        }

        foreach (var (alias, value) in aliases ?? new Dictionary<string, string>())
        {
            if (!values.Contains(value) || !_byKey.TryAdd(Key(alias), value))
            {
                throw new InvalidOperationException($"The {what} alias \"{alias}\" is not usable.");
            }
        }
    }

    /// <summary>What one value is called in messages, e.g. "damage type".</summary>
    public string What { get; }

    /// <summary>The canonical wire values, in listing order.</summary>
    public IReadOnlyList<string> Values { get; }

    /// <summary>"acid, bludgeoning, cold, …": the values as a message lists them.</summary>
    public string List => string.Join(", ", Values);

    /// <summary>The canonical value <paramref name="text"/> stands for, if any.</summary>
    public bool TryMatch(string? text, [NotNullWhen(true)] out string? canonical)
    {
        canonical = null;
        return !string.IsNullOrWhiteSpace(text) && _byKey.TryGetValue(Key(text), out canonical);
    }

    /// <summary>Whether <paramref name="canonical"/> is one of <see cref="Values"/> exactly (no normalisation).</summary>
    public bool Contains(string? canonical) => canonical is not null && Values.Contains(canonical, StringComparer.Ordinal);

    /// <summary>The comparison key: lower case with spaces, hyphens and underscores removed.</summary>
    internal static string Key(string text) =>
        string.Concat(text.Trim().ToLowerInvariant().Where(c => c is not (' ' or '-' or '_')));
}
