using DndMcp.Repository.Srd.Index;

namespace DndMcp.IntegrationTests.Srd;

/// <summary>
/// The vendored lookup with one thing taken away or changed, for formatter paths the real data never takes today: a
/// linked record that is missing, a subclass without level records, a record that carries a curated correction.
///
/// <para>
/// Those paths still run the day upstream drops a record or <c>content/srd-corrections.json</c> gains an entry, and a
/// formatter that only ever saw the happy path would then print an empty section or pass corrected text off as the
/// untouched upstream record. A hand-written fake over the real data keeps every other record as it is served.
/// </para>
/// </summary>
internal sealed class AlteredSrdLookup(ISrdLookup inner) : ISrdLookup
{
    /// <summary>A ref (<c>2024/feature/…</c>) this lookup acts as if it did not have.</summary>
    public string? HiddenRef { get; init; }

    /// <summary>When true, no subclass has level records.</summary>
    public bool HideSubclassLevels { get; init; }

    /// <summary>A ref whose document comes back with this correction reason, as if the corrections file had fixed it.</summary>
    public (string Ref, string Reason)? Corrected { get; init; }

    public SrdDocument? Get(string edition, string kind, string slug)
    {
        var reference = $"{edition}/{kind}/{slug}";
        if (reference == HiddenRef)
        {
            return null;
        }

        var doc = inner.Get(edition, kind, slug);
        return doc is not null && Corrected is { } corrected && corrected.Ref == reference
            ? new SrdDocument
            {
                Edition = doc.Edition,
                Kind = doc.Kind,
                Slug = doc.Slug,
                Name = doc.Name,
                Json = doc.Json,
                Aliases = doc.Aliases,
                Corrections = [.. doc.Corrections, corrected.Reason],
            }
            : doc;
    }

    public IReadOnlyList<SrdDocument> ClassLevels(string edition, string classSlug) => inner.ClassLevels(edition, classSlug);

    public IReadOnlyList<SrdDocument> SubclassLevels(string edition, string subclassSlug) =>
        HideSubclassLevels ? [] : inner.SubclassLevels(edition, subclassSlug);
}
