using O = DndMcp.Domain.Campaign.CampaignValues.OpKinds;

namespace DndMcp.Domain.Campaign.Ops;

/// <summary>
/// The per-op field table of <see cref="CampaignOpSpec"/>: which fields each op takes, and which a spec gave.
///
/// <para>
/// <b>Why explicit accessors and not reflection:</b> the wire names must be the snake_case names the model sees, and the
/// check must never silently skip a field. <c>CampaignOpFieldsTests</c> pins that every public property of
/// <see cref="CampaignOpSpec"/> appears in <see cref="All"/> under its snake_case name and that every field belongs to
/// at least one op, so a new field cannot be added without deciding which ops take it.
/// </para>
/// <para>
/// <b>Narrow on purpose:</b> <c>data</c> is taken by upsert and link (the rows with a data column), <c>note</c> only by
/// link (a same_as cross-link's note; a knower's note is inside <c>known_by</c>), <c>known_by</c> by upsert (who has met
/// an entity) and fact. A field accepted but ignored would read to the model as written.
/// </para>
/// <para>
/// This table is per op; a link is narrowed further by what it is stored as, which depends on its handles, so
/// <see cref="CampaignOpValidation"/> does that: a same_as to another campaign's entity (a cross-link) keeps only
/// <c>note</c>, a leads_to between two beats (a beat edge) keeps only <c>mode</c>, and a relation keeps no <c>note</c>.
/// </para>
/// </summary>
public static class CampaignOpFields
{
    /// <summary>A field of <see cref="CampaignOpSpec"/> by its wire name, and whether a spec gave it.</summary>
    public sealed record Field(string Name, Func<CampaignOpSpec, bool> IsGiven);

    /// <summary>Fields every op takes.</summary>
    public static readonly IReadOnlyList<string> Common = ["op"];

    /// <summary>Every field of <see cref="CampaignOpSpec"/>, in declaration order.</summary>
    public static readonly IReadOnlyList<Field> All =
    [
        new("op", s => s.Op is not null),
        new("ref", s => s.Ref is not null),
        new("kind", s => s.Kind is not null),
        new("name", s => s.Name is not null),
        new("subtype", s => s.Subtype is not null),
        new("slug", s => s.Slug is not null),
        new("code", s => s.Code is not null),
        new("summary", s => s.Summary is not null),
        new("body_md", s => s.BodyMd is not null),
        new("secret_md", s => s.SecretMd is not null),
        new("status", s => s.Status is not null),
        new("visibility", s => s.Visibility is not null),
        new("canon_status", s => s.CanonStatus is not null),
        new("confidence", s => s.Confidence is not null),
        new("parent", s => s.Parent is not null),
        new("sort_key", s => s.SortKey is not null),
        new("source", s => s.Source is not null),
        new("introduced_session", s => s.IntroducedSession is not null),
        new("aliases", s => s.Aliases is not null),
        new("remove_aliases", s => s.RemoveAliases is not null),
        new("tags", s => s.Tags is not null),
        new("remove_tags", s => s.RemoveTags is not null),
        new("data", s => s.Data is not null),
        new("clock", s => s.Clock is not null),
        new("from", s => s.From is not null),
        new("rel", s => s.Rel is not null),
        new("to", s => s.To is not null),
        new("label", s => s.Label is not null),
        new("attitude", s => s.Attitude is not null),
        new("symmetric", s => s.Symmetric is not null),
        new("since", s => s.Since is not null),
        new("until", s => s.Until is not null),
        new("mode", s => s.Mode is not null),
        new("note", s => s.Note is not null),
        new("statement", s => s.Statement is not null),
        new("fact_type", s => s.FactType is not null),
        new("truth", s => s.Truth is not null),
        new("about", s => s.About is not null),
        new("links", s => s.Links is not null),
        new("gate", s => s.Gate is not null),
        new("auto_code", s => s.AutoCode is not null),
        new("known_by", s => s.KnownBy is not null),
        new("depends_on", s => s.DependsOn is not null),
        new("supersedes", s => s.Supersedes is not null),
        new("superseded_by", s => s.SupersededBy is not null),
        new("established_session", s => s.EstablishedSession is not null),
        new("objective", s => s.Objective is not null),
        new("text", s => s.Text is not null),
        new("progress", s => s.Progress is not null),
        new("progress_max", s => s.ProgressMax is not null),
        new("amount", s => s.Amount is not null),
        new("answer_md", s => s.AnswerMd is not null),
        new("answered_by", s => s.AnsweredBy is not null),
    ];

    /// <summary>The fields each op takes beyond <see cref="Common"/>, in the order a message lists them.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ByOp = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        [O.Upsert] =
        [
            "ref", "kind", "name", "subtype", "slug", "code", "auto_code", "summary", "body_md", "secret_md", "status", "visibility",
            "canon_status", "confidence", "parent", "sort_key", "source", "introduced_session", "aliases", "remove_aliases", "tags",
            "remove_tags", "data", "clock", "known_by",
        ],
        [O.Delete] = ["ref"],
        [O.Restore] = ["ref"],
        [O.Link] = ["from", "rel", "to", "label", "attitude", "symmetric", "visibility", "status", "since", "until", "mode", "data", "note"],
        [O.Unlink] = ["from", "rel", "to"],
        [O.Fact] =
        [
            "ref", "statement", "code", "auto_code", "fact_type", "truth", "canon_status", "confidence", "visibility", "source", "about",
            "links", "gate", "known_by", "depends_on", "supersedes", "superseded_by", "established_session",
        ],
        [O.Status] = ["ref", "status"],
        [O.Objective] = ["ref", "objective", "text", "status", "progress", "progress_max", "visibility"],
        [O.Tick] = ["ref", "amount"],
        [O.Answer] = ["ref", "answer_md", "answered_by"],
    };

    /// <summary>Whether <paramref name="op"/> takes the field <paramref name="field"/>.</summary>
    public static bool Takes(string op, string field) => Common.Contains(field) || ByOp[op].Contains(field);

    /// <summary>The given fields of <paramref name="spec"/> that <paramref name="op"/> does not take, in declaration order.</summary>
    public static IReadOnlyList<string> Refused(string op, CampaignOpSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return All.Where(f => f.IsGiven(spec) && !Takes(op, f.Name)).Select(f => f.Name).ToList();
    }

    /// <summary>"from, rel, to": the fields an op takes, as a message lists them.</summary>
    public static string Describe(string op) => string.Join(", ", ByOp[op]);
}
