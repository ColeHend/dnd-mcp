using System.Text.Json;
using System.Text.Json.Serialization;
using DndMcp.Repository.Srd.Json;

namespace DndMcp.Repository.Srd;

/// <summary>
/// The fields every record in every 5e-database file shares: the minimum the Phase 2 importer needs to put any record
/// into the <c>doc(edition, kind, index, name, json, text)</c> table and its full-text index.
///
/// <para>
/// Only monsters and spells get full typed models in Phase 0. The other 22–23 resource kinds are indexed from this
/// header and their raw JSON, so they still need one model that reads all 49 files without exceptions. The
/// description text changes shape across files and editions, and a per-kind model would multiply that variation:
/// </para>
/// <list type="bullet">
/// <item>2014 uses <c>desc</c> as a <c>string[]</c> in most files but a plain <c>string</c> in Alignments, Languages,
/// Magic-Schools, Monsters, Rules and Subraces.</item>
/// <item>2024 renames it <c>description</c> (always a string), except Magic-Items, which keeps <c>desc</c> as a
/// string.</item>
/// </list>
/// <para>
/// Both properties therefore go through <see cref="StringOrStringArrayConverter"/>, and callers always get
/// paragraphs. Everything else is kept in <see cref="Other"/>, untouched, so nothing is lost before the typed models
/// for other kinds exist.
/// </para>
/// </summary>
public sealed class SrdEntry
{
    /// <summary>The slug, unique within one file (e.g. <c>adult-red-dragon</c>).</summary>
    public required string Index { get; init; }

    /// <summary>Display name. Absent only on 2014 class levels, which are named by index (<c>fighter-5</c>).</summary>
    public string? Name { get; init; }

    /// <summary>The API path, always beginning <c>/api/&lt;edition&gt;/</c>.</summary>
    public required string Url { get; init; }

    /// <summary>Description paragraphs from <c>desc</c> (2014 files; 2024 magic items).</summary>
    [JsonConverter(typeof(StringOrStringArrayConverter))]
    public IReadOnlyList<string>? Desc { get; init; }

    /// <summary>Description from <c>description</c> (2024 files), as a single paragraph.</summary>
    [JsonConverter(typeof(StringOrStringArrayConverter))]
    public IReadOnlyList<string>? Description { get; init; }

    /// <summary>Every other field of the record, unparsed.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Other { get; init; }
}
