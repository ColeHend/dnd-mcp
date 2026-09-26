namespace DndMcp.Repository.Srd.Models;

/// <summary>
/// 5e-database's link to another record (<c>{index, name, url}</c>): a damage type, an ability score, a condition, a
/// spell or a class. The same shape and meaning appear in both editions, so there is one type.
///
/// <para>
/// <see cref="Index"/> is the join key into the other file. <see cref="Name"/> is display text (ability scores are
/// <c>"DEX"</c>, not <c>"Dexterity"</c>), and joining on it breaks as soon as upstream re-labels something.
/// </para>
/// </summary>
public sealed class ApiReference
{
    public required string Index { get; init; }

    public required string Name { get; init; }

    /// <summary><c>/api/&lt;edition&gt;/&lt;kind&gt;/&lt;index&gt;</c>. It names the edition, which the index alone does not.</summary>
    public required string Url { get; init; }

    /// <summary>A qualifier on the link. In the vendored monsters it appears only on two 2024 condition immunities.</summary>
    public string? Note { get; init; }
}
