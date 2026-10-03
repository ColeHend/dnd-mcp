using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using FluentValidation;
using CV = DndMcp.Domain.Campaign.CampaignValues;
using O = DndMcp.Domain.Campaign.CampaignValues.OpKinds;

namespace DndMcp.Domain.Campaign.Ops;

/// <summary>
/// Validates a <c>campaign_write</c> batch before anything is written: every problem that can be seen without the
/// database, reported together (up to five), each located as <c>ops item N (&lt;op&gt; &lt;ref&gt;)</c>.
///
/// <para>
/// <b>Why before any write, and all at once:</b> a batch is one transaction and one undo unit; the write path refuses the
/// whole batch on the first op that fails, so a problem this validator can see must be reported here, with every other
/// such problem, instead of costing a round trip per mistake. What needs the database (does the ref exist, is the code
/// taken, would a parent make a cycle) is the write path's, and it uses the same <c>ops item N (…)</c> wording.
/// </para>
/// <para>
/// <b>What it checks:</b> the op and its field table (<see cref="CampaignOpFields"/>), each op's required fields, every
/// vocabulary through <see cref="CampaignValues"/> (forgiving: "Party", "all-of"), handle syntax and whether the handle
/// can name what the field needs (a fact where a fact is wanted), lengths and counts (<see cref="CampaignLimits"/>),
/// numeric ranges, a non-finite sort_key (1e400 binds as infinity), the gate (<see cref="FactGates.Validate"/>), the
/// knowers (<see cref="KnowerSpecs.Validate"/>), <c>data</c> keys that stay plain change_log paths, and the rules between
/// fields (clock only on a clock, mode only on leads_to, not both supersedes and superseded_by). A link is also checked
/// against what it will be stored as: a same_as to another campaign's entity is a cross-link and keeps only
/// <c>note</c>; a leads_to between two beats is a beat edge, keeps only <c>mode</c> and needs it; any other link is a
/// relation and keeps no <c>note</c>. A field one of these has no column for would otherwise be dropped while the
/// model reads it as written.
/// </para>
/// <para>
/// <b>Never quotes stored text.</b> Messages name things by the handles the model sent and echo at most 60 characters
/// of its own input; a statement or secret body is never echoed, because the message reaches whoever drives the client.
/// A repeated alias, tag or term is named by its position ("aliases item 2 repeats item 1"), never quoted, since an alias
/// may be author-only and a forbidden term is the word a secret hides behind. The one piece of free text echoed is an
/// upsert's name in the item label (at most 40 characters): it is the caller's own input and the only way to point at an
/// op that has no handle yet.
/// </para>
/// <para>
/// <b>Kind-dependent checks and <c>e:&lt;n&gt;</c> refs.</b> Whether a status or subtype fits, whether a clock, tick,
/// objective or answer fits, and what a link becomes (relation, cross-link or beat edge) depend on the kinds of the
/// handles. This pass reads a kind only from a <c>kind:slug</c> handle; for <c>e:&lt;n&gt;</c>, a register code or a bare
/// slug it cannot, and checks against every kind instead. The write path resolves the handles and calls
/// <see cref="ResolvedProblems"/> with the kinds it found, which runs the same checks with the same messages.
/// </para>
/// </summary>
public static partial class CampaignOpValidation
{
    /// <summary>Validates a batch.</summary>
    /// <exception cref="DndInputException">At least one problem; up to five are listed, then a count of the rest.</exception>
    public static void Validate(IReadOnlyList<CampaignOpSpec?>? ops) => DslProblems.ThrowIfAny(Problems(ops), "ops");

    /// <summary>
    /// The problems with one op once the write path has resolved its handles to entities of known kinds: every check of
    /// <see cref="Problems"/> for that op, with <paramref name="kinds"/> standing in for the kinds a <c>kind:slug</c>
    /// handle would have given. After <see cref="Validate"/> passed, only the kind-dependent checks can add anything here:
    /// status and subtype per kind, kind session refused, clock only on a clock, tick only a clock, objectives only on a
    /// quest or thread, answer only a question, and a link's shape (a <c>leads_to</c> between two beats is a beat edge:
    /// mode required, relation fields refused; <c>mode</c> on any other leads_to refused). Messages are located
    /// <c>ops item N (…)</c> exactly as the batch check's are; throw them with
    /// <c>DslProblems.ThrowIfAny(problems, "ops")</c>.
    /// </summary>
    /// <param name="index">The op's 0-based position in the batch (printed 1-based).</param>
    /// <param name="spec">The op as the model sent it.</param>
    /// <param name="kinds">The resolved kinds; a null member means that handle was not given, is a fact, or is not resolved.</param>
    public static IReadOnlyList<string> ResolvedProblems(int index, CampaignOpSpec spec, ResolvedKinds kinds)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(kinds);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return DslProblems.Messages(CampaignOpItemValidator.Instance.Validate(new CampaignOpItem(index, spec, kinds)));
    }

    /// <summary>Every problem with a batch, in op order; empty when it is valid.</summary>
    public static IReadOnlyList<string> Problems(IReadOnlyList<CampaignOpSpec?>? ops)
    {
        if (ops is null || ops.Count == 0)
        {
            return ["ops is required: at least one op, e.g. [{\"op\": \"upsert\", \"kind\": \"character\", \"name\": \"Iron Guts\"}]."];
        }

        var problems = new List<string>();
        if (ops.Count > CampaignLimits.MaxOpsPerCall)
        {
            problems.Add(
                $"ops has {DslText.Number(ops.Count)} items; at most {DslText.Number(CampaignLimits.MaxOpsPerCall)} per call (one call is one undo unit): split it.");
        }

        for (var i = 0; i < ops.Count; i++)
        {
            problems.AddRange(DslProblems.Messages(CampaignOpItemValidator.Instance.Validate(new CampaignOpItem(i, ops[i]))));
        }

        return problems;
    }

    /// <summary>
    /// A relation name in snake_case ("Member Of", "memberOf" and "member-of" are <c>member_of</c>), or null when it is not
    /// one (anything but letters, digits, spaces, hyphens and underscores, or longer than
    /// <see cref="CampaignLimits.MaxRelLength"/>). Relations are open-ended, so this is the one spelling rule they follow.
    /// </summary>
    public static string? NormalizeRel(string? rel)
    {
        if (string.IsNullOrWhiteSpace(rel))
        {
            return null;
        }

        var trimmed = rel.Trim();
        var builder = new StringBuilder(trimmed.Length + 4);
        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (char.IsAsciiLetterUpper(c))
            {
                if (i > 0 && (char.IsAsciiLetterLower(trimmed[i - 1]) || char.IsAsciiDigit(trimmed[i - 1])))
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c))
            {
                builder.Append(c);
            }
            else if (c is ' ' or '-' or '_')
            {
                if (builder.Length > 0 && builder[^1] != '_')
                {
                    builder.Append('_');
                }
            }
            else
            {
                return null;
            }
        }

        var name = builder.ToString().Trim('_');
        return name.Length <= CampaignLimits.MaxRelLength && RelPattern().IsMatch(name) ? name : null;
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(_[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex RelPattern();

    /// <summary>
    /// A <c>data</c> key: ASCII letters, digits, <c>_</c> and <c>-</c>. change_log records each changed key as the field
    /// path <c>data.&lt;key&gt;</c> and undo writes it back with <c>json_set</c> / <c>json_remove</c>; a key holding
    /// <c>.</c>, <c>[</c>, <c>$</c> or a quote would read as a nested path there (<c>data.a.b</c>) and undo the wrong value.
    /// </summary>
    [GeneratedRegex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    internal static partial Regex DataKeyPattern();
}

/// <summary>
/// The entity kinds the write path resolved for an op's handles (<see cref="CampaignOpValidation.ResolvedProblems"/>):
/// <paramref name="Ref"/> for <c>ref</c> (or the entity an upsert found by kind and name), <paramref name="From"/> and
/// <paramref name="To"/> for a link's ends. Kinds are <see cref="CampaignValues.Kinds"/> values ("beat", "clock").
/// </summary>
public sealed record ResolvedKinds(string? Ref = null, string? From = null, string? To = null);

/// <summary>One op of a batch, with its position (0-based) for messages, and the resolved kinds when the write path has them.</summary>
internal sealed record CampaignOpItem(int Index, CampaignOpSpec? Spec, ResolvedKinds? Kinds = null);

/// <summary>The checks of one op; see <see cref="CampaignOpValidation"/>.</summary>
internal sealed class CampaignOpItemValidator : AbstractValidator<CampaignOpItem>
{
    private const string FactHandleHint = "give f:<n> or a register code such as \"F36\"";

    public static CampaignOpItemValidator Instance { get; } = new();

    private CampaignOpItemValidator()
    {
        RuleFor(i => i.Spec).Custom((spec, context) => Check(context.InstanceToValidate.Index, spec, context.InstanceToValidate.Kinds, context));
    }

    private enum HandleUse
    {
        Entity,
        Fact,
        EntityOrFact,
        EntityOrOtherCampaign,
    }

    private static void Check(int index, CampaignOpSpec? spec, ResolvedKinds? kinds, ValidationContext<CampaignOpItem> context)
    {
        var item = $"ops item {DslText.Number(index + 1)}";
        var bare = new Problems<CampaignOpItem>(context, item);
        if (spec is null)
        {
            new Problems<CampaignOpItem>(context, string.Empty).Add(
                $"{item} is null; give an op such as {{\"op\": \"upsert\", \"kind\": \"character\", \"name\": \"Iron Guts\"}}.");
            return;
        }

        if (string.IsNullOrWhiteSpace(spec.Op))
        {
            bare.Add($"op is required; ops are {O.Set.List}.");
            return;
        }

        if (!O.Set.TryMatch(spec.Op, out var op))
        {
            bare.Add($"op \"{DslText.Echo(spec.Op)}\" is not an op; ops are {O.Set.List}.");
            return;
        }

        var where = $"{item} ({Label(op, spec)})";
        var p = new Problems<CampaignOpItem>(context, where);
        var refused = CampaignOpFields.Refused(op, spec);
        if (refused.Count > 0)
        {
            var names = string.Join(" or ", refused.Select(f => $"\"{f}\""));
            p.Add($"does not take {names}; {op} takes {CampaignOpFields.Describe(op)}.");
        }

        var check = new OpCheck(p, context, where, kinds);
        switch (op)
        {
            case O.Upsert:
                check.Upsert(spec);
                break;
            case O.Delete or O.Restore:
                check.RefRequired(spec, op, HandleUse.EntityOrFact);
                break;
            case O.Link or O.Unlink:
                check.Link(spec, op);
                break;
            case O.Fact:
                check.Fact(spec);
                break;
            case O.Status:
                check.Status(spec);
                break;
            case O.Objective:
                check.Objective(spec);
                break;
            case O.Tick:
                check.Tick(spec);
                break;
            case O.Answer:
                check.Answer(spec);
                break;
        }
    }

    /// <summary>"upsert character:iron-guts", "upsert character \"Iron Guts\"", "link belmakor member_of the-party", "fact".</summary>
    private static string Label(string op, CampaignOpSpec spec)
    {
        switch (op)
        {
            case O.Upsert when spec.Ref is not null:
                return $"upsert {DslText.Echo(spec.Ref.Trim())}";
            case O.Upsert when spec.Kind is not null:
                var kind = CV.Kinds.Set.TryMatch(spec.Kind, out var k) ? k : DslText.Echo(spec.Kind);
                var name = string.IsNullOrWhiteSpace(spec.Name) ? string.Empty : $" \"{Short(spec.Name.Trim())}\"";
                return $"upsert {kind}{name}";
            case O.Link or O.Unlink:
                var parts = new[] { spec.From, spec.Rel, spec.To }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => DslText.Echo(s!.Trim()));
                return string.Join(' ', new[] { op }.Concat(parts));
            default:
                return spec.Ref is null ? op : $"{op} {DslText.Echo(spec.Ref.Trim())}";
        }
    }

    private static string Short(string text) => text.Length <= 40 ? text : text[..40] + "…";

    /// <summary>The per-op checks, sharing the item's location prefix.</summary>
    private sealed class OpCheck(Problems<CampaignOpItem> p, ValidationContext<CampaignOpItem> context, string where, ResolvedKinds? resolved)
    {
        /// <summary>Relation columns: refused on a link stored as a cross-link or a beat edge, where nothing keeps them.</summary>
        private static readonly string[] RelationFields = ["label", "attitude", "symmetric", "visibility", "status", "since", "until", "data"];

        public void Upsert(CampaignOpSpec s)
        {
            var handle = Handle("ref", s.Ref, HandleUse.Entity);
            string? kind = null;
            if (s.Kind is not null && p.Known(CV.Kinds.Set, "kind", s.Kind))
            {
                CV.Kinds.Set.TryMatch(s.Kind, out kind);
            }

            if ((resolved?.Ref ?? (handle as CampaignHandle.EntityBySlug)?.Kind) is { } refKind)
            {
                if (kind is not null && kind != refKind)
                {
                    p.Add($"kind {kind} differs from the ref's kind {refKind}; an entity's kind never changes.");
                }

                kind ??= refKind;
            }

            if (s.Ref is null && (s.Kind is null || s.Name is null))
            {
                p.Add((s.Kind, s.Name) switch
                {
                    (null, null) =>
                        "give ref (an existing entity) or kind and name (to find or create one), e.g. {\"op\": \"upsert\", \"kind\": \"character\", \"name\": \"Iron Guts\"}.",
                    (not null, null) => "name is required with kind when there is no ref, e.g. {\"op\": \"upsert\", \"kind\": \"character\", \"name\": \"Iron Guts\"}.",
                    _ => $"kind is required with name when there is no ref; kinds are {CV.Kinds.Set.List}.",
                });
            }

            if (kind == CV.Kinds.Session || handle is CampaignHandle.SessionByNumber or CampaignHandle.SessionLive or CampaignHandle.SessionLast)
            {
                p.Add("sessions are written with campaign_session (plan, start, end, record_past), not upsert.");
            }

            OneLine("name", s.Name, CampaignLimits.MaxNameLength);
            if (s.Subtype is not null)
            {
                if (kind is null)
                {
                    if (!CV.Subtypes.ByKind.Values.Any(set => set.TryMatch(s.Subtype, out _)))
                    {
                        p.Add($"subtype \"{DslText.Echo(s.Subtype)}\" is not a subtype of any kind; give kind (or a kind:slug ref) to see its subtypes.");
                    }
                }
                else if (CV.Subtypes.ByKind.TryGetValue(kind, out var subtypes))
                {
                    p.Known(subtypes, "subtype", s.Subtype);
                }
                else
                {
                    p.Add($"kind {kind} takes no subtype; leave it out.");
                }
            }

            if (s.Slug is not null)
            {
                if (s.Ref is not null)
                {
                    p.Add("slug is only for creating (a slug never changes); leave it out when giving ref.");
                }
                else if (!CampaignSlugs.IsValid(s.Slug.Trim().ToLowerInvariant()))
                {
                    p.Add($"slug \"{DslText.Echo(s.Slug)}\" must be lower-case letters and digits in hyphen-separated runs, at most {DslText.Number(CampaignSlugs.MaxLength)} characters, e.g. \"old-king\".");
                }
            }

            Codes(s);
            Text("summary", s.Summary, CampaignLimits.MaxSummaryLength);
            Text("body_md", s.BodyMd, CampaignLimits.MaxBodyLength, blankAllowed: true);
            Text("secret_md", s.SecretMd, CampaignLimits.MaxBodyLength, blankAllowed: true);
            EntityStatus(kind, s.Status);
            p.Known(CV.Visibilities.Set, "visibility", s.Visibility);
            p.Known(CV.CanonStatuses.Set, "canon_status", s.CanonStatus);
            p.Known(CV.Confidences.Set, "confidence", s.Confidence);
            Handle("parent", s.Parent, HandleUse.Entity);
            if (s.Parent is not null && s.Ref is not null && s.Parent.Trim() == s.Ref.Trim())
            {
                p.Add("parent is the entity itself; an entity cannot be its own parent.");
            }

            if (s.SortKey is { } sortKey && !double.IsFinite(sortKey))
            {
                p.Add("sort_key must be a finite number, e.g. 1.5.");
            }
            OneLine("source", s.Source, CampaignLimits.MaxSourceLength);
            Session("introduced_session", s.IntroducedSession);
            Aliases(s.Aliases, s.RemoveAliases);
            Tags(s.Tags, s.RemoveTags);
            Data(s.Data);
            if (s.Clock is not null)
            {
                if (kind is not null && kind != CV.Kinds.Clock)
                {
                    p.Add($"clock is only for kind clock, not {kind}.");
                }

                Clock(s.Clock);
            }

            Knowers(s.KnownBy);
        }

        public void RefRequired(CampaignOpSpec s, string op, HandleUse use)
        {
            if (s.Ref is null)
            {
                p.Add(op switch
                {
                    O.Delete or O.Restore => $"ref is required: the entity or fact to {op}, e.g. \"character:iron-guts\" or \"f:12\".",
                    O.Status => "ref is required: the entity whose status changes, e.g. \"quest:the-old-kings-errand\".",
                    O.Objective => "ref is required: the quest or thread the objective belongs to.",
                    O.Tick => "ref is required: the clock to tick, e.g. \"clock:three-days\".",
                    _ => "ref is required: the question to answer, e.g. \"Q7\".",
                });
                return;
            }

            Handle("ref", s.Ref, use);
        }

        public void Link(CampaignOpSpec s, string op)
        {
            var missing = new[] { ("from", s.From), ("rel", s.Rel), ("to", s.To) }.Where(f => string.IsNullOrWhiteSpace(f.Item2)).Select(f => f.Item1).ToList();
            if (missing.Count > 0)
            {
                p.Add($"from, rel and to are required (missing {string.Join(", ", missing)}), e.g. {{\"op\": \"{op}\", \"from\": \"character:belmakor\", \"rel\": \"member_of\", \"to\": \"faction:the-party\"}}.");
            }

            Handle("from", s.From, HandleUse.Entity);
            string? rel = null;
            if (!string.IsNullOrWhiteSpace(s.Rel))
            {
                rel = CampaignOpValidation.NormalizeRel(s.Rel);
                if (rel is null)
                {
                    p.Add($"rel \"{DslText.Echo(s.Rel)}\" must be a relation name in snake_case of at most {DslText.Number(CampaignLimits.MaxRelLength)} characters, e.g. member_of, ally_of or leads_to.");
                }
            }

            var sameAs = rel == CV.Rels.SameAs;
            var to = Handle("to", s.To, sameAs ? HandleUse.EntityOrOtherCampaign : HandleUse.Entity);
            if (s.From is not null && s.To is not null && to is not CampaignHandle.CrossCampaign &&
                string.Equals(s.From.Trim(), s.To.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                p.Add("from and to are the same entity; a relation needs two.");
            }

            if (op == O.Unlink)
            {
                return;
            }

            // What the link is stored as decides what it can keep (contract §6): a same_as to another campaign's entity is a
            // cross-link (note only), a leads_to between two beats is a beat edge (mode only), anything else a relation.
            var fromKind = resolved?.From ?? KindOf(s.From);
            var toKind = resolved?.To ?? KindOf(s.To);
            if (sameAs && to is CampaignHandle.CrossCampaign)
            {
                RefuseShape(s, [.. RelationFields, "mode"], "rel same_as to another campaign's entity is a cross-link, which keeps only note");
                Text("note", s.Note, CampaignLimits.MaxNoteLength);
                return;
            }

            if (rel == CV.Rels.LeadsTo && fromKind == CV.Kinds.Beat && toKind == CV.Kinds.Beat)
            {
                RefuseShape(s, [.. RelationFields, "note"], "rel leads_to between two beats is a beat edge, which keeps only mode");
                if (s.Mode is null)
                {
                    p.Add($"mode is required with rel leads_to between two beats: {Problems<CampaignOpItem>.Or(CV.BeatEdgeModes.Set)} (all_of: every prerequisite beat must be met; any_of: one is enough).");
                }
                else
                {
                    p.Known(CV.BeatEdgeModes.Set, "mode", s.Mode);
                }

                return;
            }

            OneLine("label", s.Label, CampaignLimits.MaxLabelLength);
            p.InRange("attitude", s.Attitude, -CampaignLimits.MaxAttitude, CampaignLimits.MaxAttitude);
            if (s.Visibility is not null && !CV.Visibilities.RowSet.TryMatch(s.Visibility, out _))
            {
                p.Add($"visibility \"{DslText.Echo(s.Visibility)}\" is not a relation visibility; give public, party or author (a relation has no knowledge rows, so it cannot be restricted).");
            }

            p.Known(CV.RelationStatuses.Set, "status", s.Status);
            Session("since", s.Since);
            Session("until", s.Until);
            if (s.Since is { } since && s.Until is { } until && until < since)
            {
                p.Add($"until (session {DslText.Number(until)}) is before since (session {DslText.Number(since)}).");
            }

            if (s.Mode is not null)
            {
                p.Known(CV.BeatEdgeModes.Set, "mode", s.Mode);
                if (rel is not null && rel != CV.Rels.LeadsTo)
                {
                    p.Add($"mode is only for rel leads_to (a beat's prerequisites), not {rel}.");
                }
                else if (rel == CV.Rels.LeadsTo && ((fromKind, toKind) switch
                         {
                             ({ } f, _) when f != CV.Kinds.Beat => f,
                             (_, { } t) when t != CV.Kinds.Beat => t,
                             _ => null,
                         }) is { } notBeat)
                {
                    p.Add($"mode is only for leads_to between two beats (a beat's prerequisites), not with {Problems<CampaignOpItem>.Article(notBeat)} {notBeat}; a leads_to between other entities is a plain relation.");
                }
            }

            Data(s.Data);
            if (s.Note is not null)
            {
                if (sameAs && to is not null)
                {
                    p.Add("note is kept only on a same_as link to another campaign's entity (the cross-link's note); a same_as inside this campaign is a relation: put its words in label or data.");
                }
                else if (rel is not null && !sameAs)
                {
                    p.Add($"note is kept only on a same_as link to another campaign's entity (the cross-link's note), not on {rel}; put a relation's words in label or data.");
                }

                Text("note", s.Note, CampaignLimits.MaxNoteLength);
            }
        }

        /// <summary>Refuses the given fields a link of this shape has nowhere to keep.</summary>
        private void RefuseShape(CampaignOpSpec s, IReadOnlyList<string> fields, string why)
        {
            var given = CampaignOpFields.All.Where(f => fields.Contains(f.Name) && f.IsGiven(s)).Select(f => $"\"{f.Name}\"").ToList();
            if (given.Count > 0)
            {
                p.Add($"does not take {string.Join(" or ", given)} here: {why}.");
            }
        }

        public void Fact(CampaignOpSpec s)
        {
            var self = Handle("ref", s.Ref, HandleUse.Fact);
            if (s.Ref is null && s.Statement is null)
            {
                p.Add("give ref (a fact to update) or statement (a new fact), e.g. {\"op\": \"fact\", \"statement\": \"Tristan died on the ocean job.\", \"known_by\": [{\"who\": \"party\"}]}.");
            }

            Text("statement", s.Statement, CampaignLimits.MaxStatementLength);
            Codes(s);
            p.Known(CV.FactTypes.Set, "fact_type", s.FactType);
            p.Known(CV.Truths.Set, "truth", s.Truth);
            p.Known(CV.CanonStatuses.Set, "canon_status", s.CanonStatus);
            p.Known(CV.Confidences.Set, "confidence", s.Confidence);
            p.Known(CV.Visibilities.Set, "visibility", s.Visibility);
            OneLine("source", s.Source, CampaignLimits.MaxSourceLength);
            HandleList("about", s.About, HandleUse.Entity, CampaignLimits.MaxFactLinksPerOp);
            Links(s.Links);
            if (s.Gate is not null)
            {
                foreach (var problem in FactGates.Validate(s.Gate, "gate"))
                {
                    p.Add(problem);
                }

                if (self is not null && FactGates.References(s.Gate).Any(r => CampaignHandle.TryParse(r, out var h, out _) && h.Text == self.Text))
                {
                    p.Add($"gate names {self.Text} itself; a gate lists other facts.");
                }
            }

            Knowers(s.KnownBy);
            var dependsOn = HandleList("depends_on", s.DependsOn, HandleUse.Fact, CampaignLimits.MaxFactLinksPerOp);
            if (self is not null && dependsOn.Contains(self.Text))
            {
                p.Add($"depends_on names {self.Text} itself; a fact cannot depend on itself.");
            }

            var supersedes = Handle("supersedes", s.Supersedes, HandleUse.Fact);
            var supersededBy = Handle("superseded_by", s.SupersededBy, HandleUse.Fact);
            if (s.Supersedes is not null && s.SupersededBy is not null)
            {
                p.Add("give supersedes (this fact replaces that one) or superseded_by (that fact replaces this one), not both.");
            }

            if (self is not null && (supersedes?.Text == self.Text || supersededBy?.Text == self.Text))
            {
                p.Add($"a fact cannot supersede itself ({self.Text}).");
            }

            if (s.SupersededBy is not null && s.CanonStatus is not null && CV.CanonStatuses.Set.TryMatch(s.CanonStatus, out var canon) &&
                canon != CV.CanonStatuses.Superseded)
            {
                p.Add($"superseded_by makes this fact superseded; canon_status {canon} contradicts it (leave canon_status out or give superseded).");
            }

            Session("established_session", s.EstablishedSession);
        }

        public void Status(CampaignOpSpec s)
        {
            RefRequired(s, O.Status, HandleUse.Entity);
            if (s.Status is null)
            {
                p.Add("status is required: the entity's new status, e.g. \"resolved\".");
                return;
            }

            EntityStatus(RefKind(s), s.Status);
        }

        public void Objective(CampaignOpSpec s)
        {
            RefRequired(s, O.Objective, HandleUse.Entity);
            if (RefKind(s) is { } kind && kind is not (CV.Kinds.Quest or CV.Kinds.Thread))
            {
                p.Add($"objectives belong to a quest or a thread, not to kind {kind}.");
            }

            p.InRange("objective", s.Objective, 1, CampaignLimits.MaxObjectiveIndex, "the objective's 1-based position; omit it to add one");
            if (s.Objective is null && s.Text is null)
            {
                p.Add("text is required to add an objective; to update one, give objective (its 1-based position).");
            }
            else if (s.Objective is not null && s.Text is null && s.Status is null && s.Progress is null && s.ProgressMax is null && s.Visibility is null)
            {
                p.Add("give what changes: text, status, progress, progress_max or visibility.");
            }

            OneLine("text", s.Text, CampaignLimits.MaxNoteLength);
            p.Known(CV.ObjectiveStatuses.Set, "status", s.Status);
            p.InRange("progress", s.Progress, 0, CampaignLimits.MaxProgress);
            p.InRange("progress_max", s.ProgressMax, 1, CampaignLimits.MaxProgress);
            if (s.Progress is { } progress && s.ProgressMax is { } max && progress > max)
            {
                p.Add($"progress {DslText.Number(progress)} is more than progress_max {DslText.Number(max)}.");
            }

            if (s.Visibility is not null && !CV.Visibilities.RowSet.TryMatch(s.Visibility, out _))
            {
                p.Add($"visibility \"{DslText.Echo(s.Visibility)}\" is not an objective visibility; give public, party or author.");
            }
        }

        public void Tick(CampaignOpSpec s)
        {
            RefRequired(s, O.Tick, HandleUse.Entity);
            if (RefKind(s) is { } kind && kind != CV.Kinds.Clock)
            {
                p.Add($"only a clock is ticked, not kind {kind}.");
            }

            if (s.Amount == 0)
            {
                p.Add("amount 0 changes nothing; give a whole number such as 1, or -1 to untick.");
            }
            else
            {
                p.InRange("amount", s.Amount, -CampaignLimits.MaxTickAmount, CampaignLimits.MaxTickAmount);
            }
        }

        public void Answer(CampaignOpSpec s)
        {
            RefRequired(s, O.Answer, HandleUse.Entity);
            if (RefKind(s) is { } kind && kind != CV.Kinds.Question)
            {
                p.Add($"only a question is answered, not kind {kind}.");
            }

            if (s.AnswerMd is null && s.AnsweredBy is null)
            {
                p.Add("give answer_md (the answer) or answered_by (the entity or fact that answers it), or both.");
            }

            Text("answer_md", s.AnswerMd, CampaignLimits.MaxBodyLength);
            Handle("answered_by", s.AnsweredBy, HandleUse.EntityOrFact);
        }

        /// <summary>The kind of the op's ref: resolved by the write path, else read from a kind:slug handle, else unknown.</summary>
        private string? RefKind(CampaignOpSpec s) => resolved?.Ref ?? KindOf(s.Ref);

        private static string? KindOf(string? text) =>
            text is not null && CampaignHandle.TryParse(text, out var handle, out _) && handle is CampaignHandle.EntityBySlug { Kind: { } kind } ? kind : null;

        private void EntityStatus(string? kind, string? status)
        {
            if (status is null)
            {
                return;
            }

            if (kind is null)
            {
                if (!CV.Statuses.ByKind.Values.Any(set => set.TryMatch(status, out _)))
                {
                    p.Add($"status \"{DslText.Echo(status)}\" is not a status of any kind; give kind (or a kind:slug ref) to see its statuses.");
                }
            }
            else if (CV.Statuses.ByKind.TryGetValue(kind, out var statuses))
            {
                p.Known(statuses, "status", status);
            }
            else
            {
                p.Add($"kind {kind} takes no status; leave it out.");
            }
        }

        private void Codes(CampaignOpSpec s)
        {
            if (s.Code is not null && !CampaignHandle.IsCode(s.Code.Trim()))
            {
                p.Add($"code \"{DslText.Echo(s.Code)}\" is not a register code: 1-4 letters, 1-6 digits and an optional letter, e.g. \"Q22\" or \"F56a\".");
            }

            if (s.AutoCode is not null && !RegisterCodes.IsLetters(s.AutoCode))
            {
                p.Add($"auto_code \"{DslText.Echo(s.AutoCode)}\" must be 1-4 letters, e.g. \"C\".");
            }

            if (s.Code is not null && s.AutoCode is not null)
            {
                p.Add("give code (an explicit code) or auto_code (the next code of a letter), not both.");
            }
        }

        private CampaignHandle? Handle(string field, string? text, HandleUse use)
        {
            if (text is null)
            {
                return null;
            }

            if (!CampaignHandle.TryParse(text, out var handle, out var problem))
            {
                p.Add($"{field}: {problem}");
                return null;
            }

            var echo = DslText.Echo(text.Trim());
            switch (use)
            {
                case HandleUse.Fact when handle is not (CampaignHandle.FactBySeq or CampaignHandle.ByCode):
                    p.Add($"{field} \"{echo}\" is not a fact handle; {FactHandleHint}.");
                    return null;
                case HandleUse.Entity or HandleUse.EntityOrOtherCampaign when handle is CampaignHandle.FactBySeq:
                    p.Add($"{field} \"{echo}\" is a fact; give an entity handle such as \"character:iron-guts\" or e:<n>.");
                    return null;
                case HandleUse.Entity or HandleUse.EntityOrFact when handle is CampaignHandle.CrossCampaign:
                    p.Add($"{field} \"{echo}\" names another campaign's entity; only the to of a same_as link may.");
                    return null;
            }

            return handle;
        }

        private List<string> HandleList(string field, IReadOnlyList<string>? list, HandleUse use, int max)
        {
            var handles = new List<string>();
            if (list is null)
            {
                return handles;
            }

            if (list.Count == 0)
            {
                p.Add($"{field} is empty; give handles or leave it out.");
                return handles;
            }

            if (list.Count > max)
            {
                p.Add($"{field} has {DslText.Number(list.Count)} items; at most {DslText.Number(max)}.");
            }

            for (var i = 0; i < list.Count; i++)
            {
                var itemField = $"{field} item {DslText.Number(i + 1)}";
                if (string.IsNullOrWhiteSpace(list[i]))
                {
                    p.Add($"{itemField} is blank; give a handle.");
                    continue;
                }

                if (Handle(itemField, list[i], use) is { } handle)
                {
                    if (handles.Contains(handle.Text))
                    {
                        p.Add($"{itemField}: {handle.Text} is listed twice.");
                    }
                    else
                    {
                        handles.Add(handle.Text);
                    }
                }
            }

            return handles;
        }

        private void Links(IReadOnlyList<FactLinkSpec?>? links)
        {
            if (links is null)
            {
                return;
            }

            if (links.Count == 0)
            {
                p.Add("links is empty; give [{\"ref\": \"secret:fruits-are-the-seal\", \"role\": \"clue_for\"}] or leave it out.");
                return;
            }

            if (links.Count > CampaignLimits.MaxFactLinksPerOp)
            {
                p.Add($"links has {DslText.Number(links.Count)} items; at most {DslText.Number(CampaignLimits.MaxFactLinksPerOp)}.");
            }

            for (var i = 0; i < links.Count; i++)
            {
                var itemWhere = $"links item {DslText.Number(i + 1)}";
                var link = links[i];
                if (link is null)
                {
                    p.Add($"{itemWhere} is null; give {{\"ref\": \"character:old-king\", \"role\": \"about\"}}.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(link.Ref))
                {
                    p.Add($"{itemWhere}: ref is required, the entity's handle.");
                }
                else
                {
                    Handle($"{itemWhere} ref", link.Ref, HandleUse.Entity);
                }

                Sub(itemWhere).Known(CV.FactLinkRoles.Set, "role", link.Role);
            }
        }

        private void Aliases(IReadOnlyList<AliasSpec?>? aliases, IReadOnlyList<string>? remove)
        {
            var added = new Dictionary<string, int>(StringComparer.Ordinal);
            if (aliases is not null)
            {
                if (aliases.Count == 0)
                {
                    p.Add("aliases is empty; give [{\"alias\": \"the sorcerer king\", \"visibility\": \"party\"}] or leave it out.");
                }

                if (aliases.Count > CampaignLimits.MaxAliasesPerOp)
                {
                    p.Add($"aliases has {DslText.Number(aliases.Count)} items; at most {DslText.Number(CampaignLimits.MaxAliasesPerOp)}.");
                }

                for (var i = 0; i < aliases.Count; i++)
                {
                    var itemWhere = $"aliases item {DslText.Number(i + 1)}";
                    var alias = aliases[i];
                    if (alias is null)
                    {
                        p.Add($"{itemWhere} is null; give {{\"alias\": \"the sorcerer king\", \"visibility\": \"party\"}}.");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(alias.Alias))
                    {
                        p.Add($"{itemWhere}: alias is required, e.g. \"the sorcerer king\".");
                    }
                    else if (!DslText.IsOneLine(alias.Alias.Trim(), CampaignLimits.MaxAliasLength))
                    {
                        p.Add($"{itemWhere}: alias must be one line of at most {DslText.Number(CampaignLimits.MaxAliasLength)} characters.");
                    }
                    else if (added.TryGetValue(CampaignText.Key(alias.Alias), out var first))
                    {
                        p.Add($"{itemWhere} repeats item {DslText.Number(first)}.");
                    }
                    else
                    {
                        added[CampaignText.Key(alias.Alias)] = i + 1;
                    }

                    Sub(itemWhere).Known(CV.Visibilities.Set, "visibility", alias.Visibility);
                }
            }

            StringList("remove_aliases", remove, CampaignLimits.MaxAliasesPerOp, CampaignLimits.MaxAliasLength, added, "aliases");
        }

        private void Tags(IReadOnlyList<string>? tags, IReadOnlyList<string>? remove)
        {
            var added = new Dictionary<string, int>(StringComparer.Ordinal);
            StringList("tags", tags, CampaignLimits.MaxTagsPerOp, CampaignLimits.MaxTagLength, null, null, added);
            StringList("remove_tags", remove, CampaignLimits.MaxTagsPerOp, CampaignLimits.MaxTagLength, added, "tags");
        }

        /// <summary>
        /// A list of one-line strings; with <paramref name="conflicts"/> (key → 1-based position in
        /// <paramref name="conflictField"/>), an item also in that list is refused. Repeats and conflicts are named by
        /// position, never quoted (see the class summary).
        /// </summary>
        private void StringList(string field, IReadOnlyList<string>? list, int maxItems, int maxLength, Dictionary<string, int>? conflicts,
            string? conflictField, Dictionary<string, int>? collect = null)
        {
            if (list is null)
            {
                return;
            }

            if (list.Count == 0)
            {
                p.Add($"{field} is empty; give items or leave it out.");
                return;
            }

            if (list.Count > maxItems)
            {
                p.Add($"{field} has {DslText.Number(list.Count)} items; at most {DslText.Number(maxItems)}.");
            }

            var seen = collect ?? new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < list.Count; i++)
            {
                var itemWhere = $"{field} item {DslText.Number(i + 1)}";
                var text = list[i];
                if (string.IsNullOrWhiteSpace(text))
                {
                    p.Add($"{itemWhere} is blank.");
                    continue;
                }

                if (!DslText.IsOneLine(text.Trim(), maxLength))
                {
                    p.Add($"{itemWhere} must be one line of at most {DslText.Number(maxLength)} characters.");
                    continue;
                }

                var key = CampaignText.Key(text);
                if (conflicts is not null && conflicts.TryGetValue(key, out var other))
                {
                    p.Add($"{itemWhere} is also {conflictField} item {DslText.Number(other)}; add it or remove it, not both.");
                }
                else if (seen.TryGetValue(key, out var first))
                {
                    p.Add($"{itemWhere} repeats item {DslText.Number(first)}.");
                }
                else
                {
                    seen[key] = i + 1;
                }
            }
        }

        private void Data(Dictionary<string, JsonElement>? data)
        {
            if (data is null)
            {
                return;
            }

            foreach (var (key, value) in data)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    p.Add("data has a blank key; keys are names such as \"dm_pronouns\".");
                }
                else if (key.Length > CampaignLimits.MaxDataKeyLength || !CampaignOpValidation.DataKeyPattern().IsMatch(key))
                {
                    p.Add($"data key \"{DslText.Echo(key)}\" must be letters, digits, \"_\" and \"-\" (at most {DslText.Number(CampaignLimits.MaxDataKeyLength)} characters), e.g. \"dm_pronouns\"; each key is logged and undone as data.<key>, so \".\", \"[\", \"$\", quotes and spaces cannot be in one.");
                }

                if (value.ValueKind == JsonValueKind.Undefined)
                {
                    p.Add($"data \"{DslText.Echo(key)}\" has no value; give a JSON value, or null to remove the key.");
                    return;
                }
            }

            var length = JsonSerializer.Serialize(data, CampaignJson.Options).Length;
            if (length > CampaignLimits.MaxDataLength)
            {
                p.Add($"data is {DslText.Number(length)} characters as JSON; at most {DslText.Number(CampaignLimits.MaxDataLength)}.");
            }
        }

        private void Clock(ClockSpec clock)
        {
            var sub = Sub("clock");
            sub.InRange("segments", clock.Segments, 1, CampaignLimits.MaxClockSegments);
            sub.InRange("filled", clock.Filled, 0, clock.Segments is >= 1 and <= CampaignLimits.MaxClockSegments ? clock.Segments.Value : CampaignLimits.MaxClockSegments,
                clock.Segments is null ? null : "at most segments");
            sub.Known(CV.ClockUnits.Set, "unit", clock.Unit);
            if (clock.OnFillMd is { Length: > CampaignLimits.MaxBodyLength })
            {
                sub.Add($"on_fill_md is longer than {DslText.Number(CampaignLimits.MaxBodyLength)} characters.");
            }

            Handle("clock front", clock.Front, HandleUse.Entity);
        }

        private void Knowers(IReadOnlyList<KnowerSpec?>? knowers)
        {
            foreach (var problem in KnowerSpecs.Validate(knowers, "known_by"))
            {
                p.Add(problem);
            }
        }

        private void Session(string field, int? number) =>
            p.InRange(field, number, 0, CampaignLimits.MaxSessionNumber, "a session number");

        private void OneLine(string field, string? text, int max)
        {
            if (text is null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                p.Add($"{field} is blank; give text or leave it out.");
            }
            else if (!DslText.IsOneLine(text.Trim(), max))
            {
                p.Add($"{field} must be one line of at most {DslText.Number(max)} characters.");
            }
        }

        private void Text(string field, string? text, int max, bool blankAllowed = false)
        {
            if (text is null)
            {
                return;
            }

            if (!blankAllowed && string.IsNullOrWhiteSpace(text))
            {
                p.Add($"{field} is blank; give text or leave it out.");
            }
            else if (text.Length > max)
            {
                p.Add($"{field} is {DslText.Number(text.Length)} characters; at most {DslText.Number(max)}.");
            }
        }

        private Problems<CampaignOpItem> Sub(string field) => new(context, $"{where}: {field}");
    }
}
