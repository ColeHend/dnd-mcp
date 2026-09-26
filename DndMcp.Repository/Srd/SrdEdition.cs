namespace DndMcp.Repository.Srd;

/// <summary>
/// Edition identifiers exactly as they appear in the 5e-database layout (<c>src/2014</c>, <c>src/2024</c>), in API
/// URLs (<c>/api/2014/…</c>) and in every tool's <c>edition</c> argument.
///
/// <para>
/// They are strings rather than an enum because they are stored: srd.db rows and campaign rulesets carry them.
/// Renumbering an enum would silently re-label stored data, whereas a string either matches or fails loudly.
/// </para>
/// </summary>
public static class SrdEdition
{
    public const string Edition2014 = "2014";
    public const string Edition2024 = "2024";

    /// <summary>Both editions, oldest first: the order the vendored directories and manifest use.</summary>
    public static IReadOnlyList<string> All { get; } = [Edition2014, Edition2024];
}

/// <summary>
/// File names inside <c>content/5e-database/&lt;version&gt;/&lt;edition&gt;/</c>. They are upstream's own names, kept
/// unchanged so a re-vendor is a byte-for-byte replacement and the manifest paths still match.
/// </summary>
public static class SrdFileNames
{
    public const string Monsters = "5e-SRD-Monsters.json";
    public const string Spells = "5e-SRD-Spells.json";
}
