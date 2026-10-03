using System.Globalization;
using System.Text;
using DndMcp.Domain.Campaign;
using DndMcp.Repository.Campaign.Read;

namespace DndMcp.Formatting.Campaign;

/// <summary>
/// campaign_get's markdown (and the <c>campaign://&lt;slug&gt;/entity/&lt;ref&gt;</c> resource, and campaign_history
/// <c>as_of</c>): entities and facts as <see cref="EntityReader"/> returns them for one perspective.
///
/// <para>
/// <b>Render what the reader gives, and nothing about what it left out.</b> The reader has already applied the perspective
/// filter: a disguised entity arrives as a display name, kind, <c>e:&lt;n&gt;</c>, the party aliases its disguise shares
/// with the view and the facts the view knows; everything
/// author-only sits in the <c>Author</c> sub-records, which are null for every other view. This formatter therefore never
/// prints a section, count or placeholder for something that is absent: an empty list prints nothing (not "Relations:
/// none", which would differ between a disguised entity and an ordinary one and so say that a truer name exists), the
/// "Author" section and the <c>&gt; [!secret]</c> block exist only when the author records exist, and no line says how many
/// rows were hidden. A new field added to a reader's author record is rendered only if it is added here, inside those
/// author-only blocks.
/// </para>
/// <para>
/// <b>Concise by default.</b> <c>detail: "full"</c> opts in to whole bodies, secret text and whole fact statements;
/// concise output keeps the first <see cref="ConciseBodyChars"/> characters of a body or secret and the first
/// <see cref="ConciseFactChars"/> of each linked fact, with a note that more exists. Linked facts are cut too because an
/// entity with thirty long facts would otherwise fill the cap in the default mode and lose its knowledge and author
/// sections behind an "output cut" line.
/// </para>
/// <para>
/// <b>Every entry asked for is in the result.</b> The <see cref="CampaignMarkdownText.MaxChars"/> budget is shared: each
/// of several entries gets an equal part, is cut at a line inside it with a note when it runs over, and a full body or
/// secret takes at most two fifths of that part. Without the sharing, the first of ten long entries (a 50,000-character
/// body, forty relations) would fill the cap on its own and the other nine would vanish behind one "output cut" line,
/// which reads like a complete answer about one entry.
/// </para>
/// <para>
/// <b>One entry in full gets the whole result.</b> Its sections are rendered first without the body and secret text, and
/// those two share whatever room is left (each keeps what the other does not need). A fixed share would cut a
/// 20,000-character body that fits, and nothing else (no tool, no resource) could show the rest; so a body is cut only
/// when it really does not fit beside the rest of the entry, and the note says which (and that include [] makes room).
/// </para>
/// </summary>
internal static class EntityMarkdown
{
    /// <summary>How much of a body (and of secret text) concise output keeps.</summary>
    public const int ConciseBodyChars = 600;

    /// <summary>How much of each linked fact's text concise output keeps (as campaign_search does).</summary>
    public const int ConciseFactChars = 400;

    /// <summary>Relations, facts, children and sessions listed per entity before "… and N more".</summary>
    public const int MaxListed = 40;

    /// <summary>The detail values: concise (default) or full.</summary>
    public static readonly Domain.Features.DslValueSet DetailSet = new("detail", [Concise, Full]);

    public const string Concise = "concise";
    public const string Full = "full";

    private const string FullCapHint = "ask for fewer refs, leave out include, or use detail \"concise\"";

    private const string ConciseCapHint = "ask for fewer refs, or leave out include (facts, knowledge and history are the long ones)";

    // Room kept for the title, banner and as-of line when the budget is shared between entries.
    private const int HeaderAllowance = 1_000;

    // Room kept, in one full entry, for the banner and as-of line above it (its title is part of the entry) and for the
    // blank lines and cut notes around the body and the secret text.
    private const int SingleHeaderAllowance = 400;

    private const int BlockAllowance = 400;

    private const string EntryCutNote = "_… the rest of this entry does not fit beside the others; get it on its own, or with fewer includes._";

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>A get's result: one entry per handle, entities then facts, in the order asked.</summary>
    /// <param name="result">The reader's result.</param>
    /// <param name="view">Whose view it was read for (the banner).</param>
    /// <param name="campaignSlug">The campaign, named in a multi-entry title.</param>
    /// <param name="full">detail "full": whole bodies and secret text.</param>
    /// <param name="title">An explicit title (history as_of); null uses the entry's own name for one entry.</param>
    public static string Format(GetResult result, CampaignView view, string campaignSlug, bool full, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(view);
        var count = result.Entities.Count + result.Facts.Count;
        var builder = new StringBuilder();
        var level = 1;
        if (title is not null || count != 1)
        {
            builder.Append("# ").Append(title ?? $"{Number(count)} entries from {campaignSlug}").Append('\n');
            level = 2;
        }

        var single = count <= 1;
        var entryBudget = single ? CampaignMarkdownText.MaxChars : (CampaignMarkdownText.MaxChars - HeaderAllowance) / count;
        var sharedBudget = Math.Max(ConciseBodyChars, entryBudget * 2 / 5);
        foreach (var entity in result.Entities)
        {
            var entry = !full ? Render(entity, level, false, ConciseBodyChars, ConciseBodyChars, Cut.Concise, campaignSlug, view)
                : single ? SingleFull(entity, level, campaignSlug, view)
                : Render(entity, level, true, sharedBudget, sharedBudget, Cut.Shared, campaignSlug, view);
            AppendEntry(builder, entry, single ? int.MaxValue : entryBudget);
        }

        foreach (var fact in result.Facts)
        {
            var entry = new StringBuilder();
            Fact(entry, fact, level, full, campaignSlug, view);
            AppendEntry(builder, entry, single ? int.MaxValue : entryBudget);
        }

        // A titled result (history as_of) says the point in time in its title already.
        InsertHeader(builder, view, title is null ? result.AsOfSession : null);

        return CampaignMarkdownText.Cap(builder.ToString().TrimEnd() + "\n", full ? FullCapHint : ConciseCapHint);
    }

    // How a cut body or secret says what was left out.
    private enum Cut
    {
        Concise,
        Shared,
        Single,
        SingleBesideIncludes,
    }

    private static StringBuilder Render(EntityDetail e, int level, bool full, int bodyBudget, int secretBudget, Cut cut, string campaignSlug, CampaignView view)
    {
        var entry = new StringBuilder();
        Entity(entry, e, level, full, bodyBudget, secretBudget, cut, campaignSlug, view);
        return entry;
    }

    // One entity in full as the whole result (class summary): the rest of the entry first, then the body and the secret
    // text share the room left, each keeping what the other does not need; when even that is too little they still show
    // their opening, and the final cap trims the tail.
    private static StringBuilder SingleFull(EntityDetail e, int level, string campaignSlug, CampaignView view)
    {
        var body = e.BodyMd?.Trim() ?? string.Empty;
        var secret = e.Author?.SecretMd?.Trim() ?? string.Empty;
        var rest = Render(e, level, true, -1, -1, Cut.Single, campaignSlug, view).Length;
        var room = Math.Max(0, CampaignMarkdownText.MaxChars - SingleHeaderAllowance - BlockAllowance - rest);

        // The secret block prefixes every line with "> ", so its text takes more room than its length.
        var secretLines = secret.Length == 0 ? 0 : secret.Count(c => c == '\n') + 1;
        var secretRoom = secret.Length + 2 * secretLines;
        int bodyBudget, secretBudget;
        if (body.Length + secretRoom <= room)
        {
            (bodyBudget, secretBudget) = (body.Length, secret.Length);
        }
        else if (body.Length <= room / 2)
        {
            (bodyBudget, secretBudget) = (body.Length, Shrink(room - body.Length, secret.Length, secretRoom));
        }
        else if (secretRoom <= room / 2)
        {
            (bodyBudget, secretBudget) = (room - secretRoom, secret.Length);
        }
        else
        {
            (bodyBudget, secretBudget) = (room / 2, Shrink(room / 2, secret.Length, secretRoom));
        }

        var includes = e.Relations is { Count: > 0 } || e.Facts is { Count: > 0 } || e.Children is { Count: > 0 } || e.Sessions is { Count: > 0 } ||
                       e.Knowledge is { Count: > 0 } || e.Author?.History is { Count: > 0 };
        return Render(e, level, true, Math.Max(ConciseBodyChars, bodyBudget), Math.Max(ConciseBodyChars, secretBudget),
            includes ? Cut.SingleBesideIncludes : Cut.Single, campaignSlug, view);

        // The secret's own characters that fit in room, given that its rendered form takes rendered characters.
        static int Shrink(int room, int length, int rendered) => length == 0 ? 0 : (int)((long)room * length / rendered);
    }

    // One entry's text, cut at a line inside its share of the budget (with a note) when it runs over; a blank line before
    // every entry but the first line of the result.
    private static void AppendEntry(StringBuilder builder, StringBuilder entry, int budget)
    {
        if (builder.Length > 0)
        {
            builder.Append('\n');
        }

        if (entry.Length <= budget)
        {
            builder.Append(entry);
            return;
        }

        var text = entry.ToString();
        var cut = text.LastIndexOf('\n', Math.Max(0, budget - EntryCutNote.Length - 1));
        builder.Append(text.AsSpan(0, cut > 0 ? cut : Math.Max(0, budget - EntryCutNote.Length))).Append('\n').Append(EntryCutNote).Append('\n');
    }

    // The banner and the as-of line go right under the first heading (the title, or the one entry's own heading).
    private static void InsertHeader(StringBuilder builder, CampaignView view, int? asOf)
    {
        var header = new StringBuilder();
        if (view.Banner is not null)
        {
            header.Append(view.Banner).Append('\n');
        }

        if (asOf is { } session)
        {
            header.Append(AsOfLine(session)).Append('\n');
        }

        if (header.Length > 0)
        {
            var titleEnd = builder.ToString().IndexOf('\n', StringComparison.Ordinal) + 1;
            builder.Insert(titleEnd, header.ToString());
        }
    }

    /// <summary>"_As of the end of session 3._"</summary>
    public static string AsOfLine(int session) => $"_As of the end of session {Number(session)}._";

    // A budget below zero leaves that block out (SingleFull measures the rest of the entry that way). The author's heading
    // carries e:<n> beside kind:slug (review U05): the one handle every view accepts, so the author can read the same entry
    // as a perspective that knows it under another name (which refuses the kind:slug that spells the true name).
    private static void Entity(StringBuilder b, EntityDetail e, int level, bool full, int bodyBudget, int secretBudget, Cut cut, string campaignSlug,
        CampaignView view)
    {
        Heading(b, level).Append(e.DisplayName).Append(" (`").Append(e.Ref).Append('`');
        if (e.Author?.SeqRef is { } seq && seq != e.Ref)
        {
            b.Append(" · `").Append(seq).Append('`');
        }

        b.Append(")\n");

        var facts = new List<string> { e.Kind };
        AddIf(facts, e.Subtype);
        AddIf(facts, e.Code);
        AddIf(facts, e.Status is null ? null : "status " + e.Status);
        b.Append(string.Join(" · ", facts)).Append('\n');
        if (!string.IsNullOrWhiteSpace(e.Summary))
        {
            b.Append("> ").Append(OneLine(e.Summary)).Append('\n');
        }

        if (e.Aliases.Count > 0)
        {
            b.Append("- **Aliases:** ")
                .Append(string.Join(", ", e.Aliases.Select(a => a.Visibility is null ? a.Alias : $"{a.Alias} ({a.Visibility})")))
                .Append('\n');
        }

        if (e.Tags.Count > 0)
        {
            b.Append("- **Tags:** ").Append(string.Join(", ", e.Tags)).Append('\n');
        }

        if (e.Parent is not null)
        {
            b.Append("- **Part of:** ").Append(Link(e.Parent)).Append('\n');
        }

        if (e.Clock is { } clock)
        {
            b.Append("- **Clock:** ").Append(Number(clock.Filled)).Append(" of ").Append(Number(clock.Segments)).Append(' ')
                .Append(clock.Unit).Append(clock.Segments == 1 ? string.Empty : "s").Append(" filled");
            if (clock.Front is not null)
            {
                b.Append(" · front ").Append(Link(clock.Front));
            }

            if (clock.ShownToPlayers is { } shown)
            {
                b.Append(shown ? " · shown to players" : " · not shown to players");
            }

            if (!string.IsNullOrWhiteSpace(clock.OnFillMd))
            {
                b.Append(" · when it fills: ").Append(OneLine(clock.OnFillMd));
            }

            b.Append('\n');
        }

        if (e.Objectives.Count > 0)
        {
            b.Append("- **Objectives:**\n");
            foreach (var o in e.Objectives)
            {
                b.Append("  ").Append(Number(o.Index)).Append(". ").Append(OneLine(o.Text)).Append(" · ").Append(o.Status);
                if (o.ProgressMax is { } max)
                {
                    b.Append(" · ").Append(Number(o.Progress ?? 0)).Append('/').Append(Number(max));
                }

                if (o.Visibility is not null)
                {
                    b.Append(" · ").Append(o.Visibility);
                }

                b.Append('\n');
            }
        }

        if (!string.IsNullOrWhiteSpace(e.BodyMd) && bodyBudget >= 0)
        {
            b.Append('\n').Append(Body(e.BodyMd, bodyBudget, cut)).Append('\n');
        }

        if (e.Author is { SecretMd.Length: > 0 } secret && !string.IsNullOrWhiteSpace(secret.SecretMd) && secretBudget >= 0)
        {
            b.Append('\n').Append(SecretBlock(Body(secret.SecretMd, secretBudget, cut))).Append('\n');
        }

        var sub = new string('#', level + 1) + " ";
        if (e.Beat is { } beat)
        {
            StoryWeb(b, beat, sub);
        }

        if (e.Relations is { Count: > 0 } relations)
        {
            b.Append('\n').Append(sub).Append("Relations\n");
            foreach (var r in relations.Take(MaxListed))
            {
                b.Append("- ").Append(Relation(r)).Append('\n');
            }

            More(b, MaxListed, relations.Count, view.AuthorView
                ? "read the other end with campaign_get"
                : "read the other end with " + ViewCall(view, campaignSlug, "campaign_get", "\"refs\": [...]"));
        }

        if (e.Facts is { Count: > 0 } linked)
        {
            b.Append('\n').Append(sub).Append("Facts\n");
            foreach (var f in linked.Take(MaxListed))
            {
                b.Append("- ").Append(FactRef(f.Ref, f.Code)).Append(": ")
                    .Append(full ? OneLine(f.Text) : CampaignMarkdownText.Excerpt(OneLine(f.Text), ConciseFactChars));
                if (f.Roles.Count > 0 && !(f.Roles.Count == 1 && f.Roles[0] == CampaignValues.FactLinkRoles.About))
                {
                    b.Append(" _(").Append(string.Join(", ", f.Roles)).Append(")_");
                }

                if (f.Author is { } author)
                {
                    b.Append("\n  ").Append(AuthorFactLine(author));
                }

                b.Append('\n');
            }

            More(b, MaxListed, linked.Count, view.AuthorView
                ? "campaign_search with a query finds more"
                : ViewCall(view, campaignSlug, "campaign_search", "\"query\": ...") + " finds more");
        }

        if (e.Children is { Count: > 0 } children)
        {
            b.Append('\n').Append(sub).Append("Inside it\n");
            foreach (var c in children.Take(MaxListed))
            {
                b.Append("- ").Append(Link(c)).Append(" · ").Append(c.Kind).Append('\n');
            }

            More(b, MaxListed, children.Count, view.AuthorView
                ? "campaign_search lists them all"
                : ViewCall(view, campaignSlug, "campaign_search", "\"kinds\": [...]") + " lists them all");
        }

        if (e.Sessions is { Count: > 0 } sessions)
        {
            b.Append('\n').Append(sub).Append("Sessions\n");
            foreach (var s in sessions.Take(MaxListed))
            {
                b.Append("- `").Append(s.Ref).Append("` ").Append(s.Title).Append(" · ").Append(s.Relation);
                if (!string.IsNullOrWhiteSpace(s.Note))
                {
                    b.Append(" · ").Append(OneLine(s.Note));
                }

                b.Append('\n');
            }

            More(b, MaxListed, sessions.Count, view.AuthorView
                ? "campaign_session list shows every session"
                : SessionListCall(view, campaignSlug) + " shows every session");
        }

        if (e.Knowledge is { Count: > 0 } knowledge)
        {
            b.Append('\n').Append(sub).Append("Knowledge\n");
            foreach (var k in knowledge.Take(MaxListed * 2))
            {
                b.Append("- ").Append(Knowledge(k)).Append('\n');
            }

            More(b, MaxListed * 2, knowledge.Count, KnowledgeHint(view, campaignSlug));
        }

        if (e.Author is { } a)
        {
            AuthorEntity(b, a, sub, e.Ref, campaignSlug);
        }
    }

    // A beat's place in the story web (author view only; the reader leaves it null otherwise), review C03: whether it can
    // happen next and what it waits on, then its prerequisites and the beats it opens, each with the edge's mode and that
    // beat's status. Without it a web the author built could not be read back at all.
    private static void StoryWeb(StringBuilder b, BeatView beat, string sub)
    {
        b.Append('\n').Append(sub).Append("Story web\n");
        b.Append("- ").Append(BeatStanding(beat)).Append('\n');
        foreach (var edge in beat.After.Take(MaxListed))
        {
            b.Append("- after ").Append(Link(edge.Beat)).Append(" · ").Append(edge.Mode).Append(" · ").Append(edge.Status).Append('\n');
        }

        More(b, MaxListed, beat.After.Count, "campaign_search with kinds [\"beat\"] lists every beat");
        foreach (var edge in beat.LeadsTo.Take(MaxListed))
        {
            b.Append("- leads to ").Append(Link(edge.Beat)).Append(" · ").Append(edge.Mode).Append(" · ").Append(edge.Status).Append('\n');
        }

        More(b, MaxListed, beat.LeadsTo.Count, "campaign_search with kinds [\"beat\"] lists every beat");
    }

    // "reachable now (1 of 2 prerequisites met; any one of them is enough)", "blocked by Find the map (`beat:find-the-map`) (…)".
    private static string BeatStanding(BeatView beat)
    {
        var count = beat.TotalCount == 0
            ? "no prerequisites"
            : $"{Number(beat.MetCount)} of {Number(beat.TotalCount)} prerequisite{(beat.TotalCount == 1 ? string.Empty : "s")} met; {GateText(beat.Gate)}";
        return beat.Standing switch
        {
            BeatStandings.Met => "met: it has happened",
            BeatStandings.Cut => "cut: out of the story, it neither opens nor blocks anything",
            BeatStandings.Reachable => $"reachable now ({count})",
            _ => $"blocked by {string.Join(", ", beat.BlockedBy.Select(Link))} ({count})",
        };

        static string GateText(string gate) => gate switch
        {
            BeatGates.AllOf => "all of them are needed",
            BeatGates.AnyOf => "any one of them is enough",
            BeatGates.Mixed => "every all_of one and one any_of one are needed",
            _ => "none gate it",
        };
    }

    private static void AuthorEntity(StringBuilder b, AuthorEntityDetail a, string sub, string entityRef, string campaignSlug)
    {
        b.Append('\n').Append(sub).Append("Author\n");
        b.Append("- visibility ").Append(a.Visibility).Append(" · canon ").Append(a.CanonStatus).Append(" · confidence ").Append(a.Confidence);
        if (a.IntroducedSession is not null)
        {
            b.Append(" · introduced `").Append(a.IntroducedSession).Append('`');
        }

        if (a.SortKey is { } sortKey)
        {
            b.Append(" · sort key ").Append(sortKey.ToString("0.###", Invariant));
        }

        b.Append('\n');
        if (!string.IsNullOrWhiteSpace(a.Source))
        {
            b.Append("- source: ").Append(OneLine(a.Source)).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(a.Data) && a.Data.Trim() != "{}")
        {
            b.Append("- data: `").Append(CampaignMarkdownText.Excerpt(a.Data, 600)).Append("`\n");
        }

        foreach (var link in a.CrossLinks)
        {
            b.Append("- same as `").Append(link.Ref).Append("` (").Append(link.Name).Append(", campaign ").Append(link.Campaign).Append(')');
            if (!string.IsNullOrWhiteSpace(link.Note))
            {
                b.Append(": ").Append(OneLine(link.Note));
            }

            b.Append('\n');
        }

        if (a.Secret is { } secret)
        {
            b.Append("- secret status: ").Append(secret.StoredStatus ?? "none").Append(" (stored), ").Append(secret.DerivedStatus)
                .Append(" (from its gates now)\n");
            foreach (var gate in secret.Gates)
            {
                b.Append("  - ").Append(GateLine(gate)).Append('\n');
            }
        }

        if (a.History is { Count: > 0 } history)
        {
            b.Append('\n').Append(sub).Append("History (newest first)\n");
            foreach (var batch in history)
            {
                HistoryMarkdown.BatchLines(b, batch, maxChanges: 8, campaignSlug);
            }

            More(b, history.Count, a.HistoryTotal ?? history.Count, EntityHistoryHint(entityRef, campaignSlug));
        }
    }

    /// <summary>
    /// A call that reads more in the same view of the same campaign, for a non-author view's "… and N more" hints. A hint
    /// without the perspective sends the model to the author's read (campaign_get and campaign_search default to the author
    /// view): followed on a disguised entity it prints the true name and the secret text, which a model drafting for that
    /// view must never see. Without the campaign it reads whichever campaign is current. A read as of a session keeps the
    /// session (review LR02), or the hint reads what the view learned after it; campaign_session list takes no session, and
    /// its hint (<see cref="SessionListCall"/>) keeps only the perspective.
    /// </summary>
    private static string ViewCall(CampaignView view, string campaignSlug, string tool, string arguments) =>
        $"{tool} {{\"campaign\": \"{campaignSlug}\", {arguments}, {view.ViewArguments}}}";

    // campaign_session list in the same view: it takes no as_of_session (it lists sessions as they are).
    private static string SessionListCall(CampaignView view, string campaignSlug) =>
        $"campaign_session {{\"campaign\": \"{campaignSlug}\", \"action\": \"list\", \"perspective\": \"{view.Perspective.Text}\"}}";

    // The author is pointed at the ledger (an author-facing grid); another view at a search of its own view.
    private static string KnowledgeHint(CampaignView view, string campaignSlug) => view.AuthorView
        ? "campaign_knowledge ledger shows them as a grid"
        : ViewCall(view, campaignSlug, "campaign_search", "\"query\": ...") + " finds the rest by words";

    // The call that pages through one entry's history (it names the campaign: history is looked up in the campaign the call resolves to).
    private static string EntityHistoryHint(string entryRef, string campaignSlug) =>
        $"campaign_history {{\"action\": \"entity\", \"ref\": \"{entryRef}\", \"campaign\": \"{campaignSlug}\"}} pages through the rest";

    private static void Fact(StringBuilder b, FactDetail f, int level, bool full, string campaignSlug, CampaignView view)
    {
        Heading(b, level).Append("Fact ").Append(FactRef(f.Ref, f.Code)).Append('\n');

        b.Append("> ").Append(full ? OneLine(f.Text) : CampaignMarkdownText.Excerpt(OneLine(f.Text), 1_000)).Append('\n');
        if (f.Links.Count > 0)
        {
            b.Append("- **About:** ")
                .Append(string.Join(", ", f.Links.Select(l => l.Role == CampaignValues.FactLinkRoles.About ? Link(l.Entity) : $"{Link(l.Entity)} ({l.Role})")))
                .Append('\n');
        }

        var sub = new string('#', level + 1) + " ";
        if (f.Knowledge is { Count: > 0 } knowledge)
        {
            b.Append('\n').Append(sub).Append("Knowledge\n");
            foreach (var k in knowledge.Take(MaxListed * 2))
            {
                b.Append("- ").Append(Knowledge(k)).Append('\n');
            }

            More(b, MaxListed * 2, knowledge.Count, KnowledgeHint(view, campaignSlug));
        }

        if (f.Author is { } author)
        {
            b.Append('\n').Append(sub).Append("Author\n");
            b.Append("- ").Append(AuthorFactLine(author.Fact)).Append('\n');
            if (!string.IsNullOrWhiteSpace(author.Fact.Source))
            {
                b.Append("- source: ").Append(OneLine(author.Fact.Source)).Append('\n');
            }

            if (author.Dependents.Count > 0)
            {
                b.Append("- rests on it (a supersession makes these stale): ").Append(string.Join(", ", author.Dependents.Select(d => $"`{d}`"))).Append('\n');
            }

            if (author.History is { Count: > 0 } history)
            {
                b.Append('\n').Append(sub).Append("History (newest first)\n");
                foreach (var batch in history)
                {
                    HistoryMarkdown.BatchLines(b, batch, maxChanges: 8, campaignSlug);
                }

                More(b, history.Count, author.HistoryTotal ?? history.Count, EntityHistoryHint(f.Ref, campaignSlug));
            }
        }
    }

    // "type secret · truth true · canon played · visibility party · established S3 · known by party, character:belmakor · gate {…}"
    private static string AuthorFactLine(AuthorFact a)
    {
        var parts = new List<string>
        {
            "type " + a.FactType,
            "truth " + a.Truth,
            "canon " + a.CanonStatus,
            "confidence " + a.Confidence,
            "visibility " + a.Visibility,
        };
        if (a.EstablishedSession is { } established)
        {
            parts.Add("established S" + Number(established));
        }

        if (a.SupersededBy is not null)
        {
            parts.Add($"superseded by `{a.SupersededBy}`");
        }

        parts.Add(a.KnownBy.Count == 0 ? "known by no one yet" : "known by " + string.Join(", ", a.KnownBy));
        if (a.DependsOn.Count > 0)
        {
            parts.Add("depends on " + string.Join(", ", a.DependsOn.Select(d => $"`{d}`")));
        }

        if (a.Gate is not null)
        {
            parts.Add("gate `" + FactGates.Serialize(a.Gate) + "`");
        }
        else if (a.GateUnreadable)
        {
            parts.Add("gate unreadable (ignored until rewritten)");
        }

        return string.Join(" · ", parts);
    }

    private static string GateLine(GateStatusView g)
    {
        if (g.Unreadable)
        {
            return $"`{g.Fact}`: gate unreadable (ignored until rewritten)";
        }

        var parts = new List<string> { $"`{g.Fact}`: " + (g.KnownToParty ? "known to the party" : g.Ready ? "ready to reveal" : "not ready") };
        if (g.UnmetAfter.Count > 0)
        {
            parts.Add("after unmet: " + string.Join(", ", g.UnmetAfter));
        }

        if (g.Routes.Count > 0)
        {
            parts.Add($"routes {Number(g.RoutesComplete)}/{Number(g.MinRoutes)} (" +
                      string.Join(", ", g.Routes.Select(r => $"{r.Id} {Number(r.Known)}/{Number(r.Needed)}{(r.Complete ? " complete" : string.Empty)}")) + ")");
        }

        if (g.UnmetPrefer.Count > 0)
        {
            parts.Add("prefer unmet: " + string.Join(", ", g.UnmetPrefer));
        }

        if (g.MustLandWith.Count > 0)
        {
            parts.Add("lands with " + string.Join(", ", g.MustLandWith));
        }

        if (g.ForbiddenActive)
        {
            parts.Add("forbidden words active");
        }

        if (g.Seeded)
        {
            parts.Add("seeded");
        }

        if (g.ReachableBeforeGate)
        {
            parts.Add("reachable before the gate");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>"member_of → The party (`faction:party`) · current · since S1".</summary>
    public static string Relation(RelationView r)
    {
        var builder = new StringBuilder();
        builder.Append(r.Direction == RelationDirections.In ? "← " : string.Empty).Append(r.Rel)
            .Append(r.Direction == RelationDirections.In ? " from " : " → ").Append(Link(r.Other));
        if (!string.IsNullOrWhiteSpace(r.Label))
        {
            builder.Append(" “").Append(OneLine(r.Label)).Append('”');
        }

        builder.Append(" · ").Append(r.Status);
        if (r.SinceSession is { } since)
        {
            builder.Append(" · since S").Append(Number(since));
        }

        if (r.UntilSession is { } until)
        {
            builder.Append(" · until S").Append(Number(until));
        }

        if (r.Attitude is { } attitude)
        {
            builder.Append(" · attitude ").Append(attitude > 0 ? "+" : string.Empty).Append(Number(attitude));
        }

        if (r.Symmetric)
        {
            builder.Append(" · both ways");
        }

        if (r.Visibility is not null)
        {
            builder.Append(" · ").Append(r.Visibility);
        }

        return builder.ToString();
    }

    /// <summary>
    /// "party: knows as “the old king” (S3) — the party met it in S3". Author rows add how, via, note and until; a non-author
    /// line carries only what the reader gave it (never the via name or note, which are author-only).
    /// </summary>
    public static string Knowledge(KnowledgeLine k)
    {
        var builder = new StringBuilder();
        builder.Append(k.Target).Append(" · ").Append(k.Knower).Append(": ").Append(k.Standing.Replace('_', ' '));
        if (k.State is not null && k.State != k.Standing)
        {
            builder.Append(" (").Append(k.State).Append(')');
        }

        if (k.KnownAs is not null)
        {
            builder.Append(" as “").Append(OneLine(k.KnownAs)).Append('”');
        }

        if (k.LearnedSession is { } learned)
        {
            builder.Append(" · S").Append(Number(learned));
        }

        if (k.Author is { } a)
        {
            if (!string.IsNullOrWhiteSpace(a.How))
            {
                builder.Append(" · ").Append(OneLine(a.How));
            }

            if (a.Via is not null)
            {
                builder.Append(" via `").Append(a.Via).Append('`');
            }

            if (a.ValidUntilSession is { } until)
            {
                builder.Append(" · until S").Append(Number(until));
            }

            if (!string.IsNullOrWhiteSpace(a.LearnedIngame))
            {
                builder.Append(" · in-game ").Append(OneLine(a.LearnedIngame));
            }

            if (!string.IsNullOrWhiteSpace(a.Note))
            {
                builder.Append(" · note: ").Append(OneLine(a.Note));
            }
        }

        if (!string.IsNullOrWhiteSpace(k.Explanation))
        {
            builder.Append(" — ").Append(OneLine(k.Explanation));
        }

        return builder.ToString();
    }

    /// <summary>"The party (`faction:party`)".</summary>
    public static string Link(EntityLink link) => $"{link.Name} (`{link.Ref}`)";

    /// <summary>"`f:12` (F36)" or "`f:12`".</summary>
    public static string FactRef(string reference, string? code) => code is null ? $"`{reference}`" : $"`{reference}` ({code})";

    /// <summary>Text quoted as an Obsidian-style secret callout (author view only).</summary>
    public static string SecretBlock(string text) =>
        "> [!secret]\n" + string.Join("\n", text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n').Select(l => "> " + l));

    // A body cut to max characters at a line or word boundary, saying so (and how to see more, when there is a way).
    private static string Body(string text, int max, Cut cut)
    {
        var trimmed = text.Trim();
        if (trimmed.Length <= max)
        {
            return trimmed;
        }

        var at = trimmed.LastIndexOf('\n', max - 1);
        if (at < max / 2)
        {
            at = trimmed.LastIndexOf(' ', max - 1);
        }

        var shown = trimmed[..(at > max / 2 ? at : max)].TrimEnd();
        var more = Number(trimmed.Length - shown.Length);
        return shown + cut switch
        {
            Cut.Concise => $"\n\n_… {more} more characters; detail \"full\" shows them._",
            Cut.Shared => $"\n\n_… {more} more characters; get this ref on its own to read more of it._",
            Cut.SingleBesideIncludes =>
                $"\n\n_… {more} more characters that do not fit in one result beside the rest of this entry; include [] leaves out the " +
                "relations, facts and other sections to make room._",
            _ => $"\n\n_… {more} more characters that do not fit in one result._",
        };
    }

    // A blank line before every heading but the first line of the result.
    private static StringBuilder Heading(StringBuilder b, int level)
    {
        if (b.Length > 0)
        {
            b.Append('\n');
        }

        return b.Append(new string('#', level)).Append(' ');
    }

    private static void More(StringBuilder b, int shown, int total, string hint)
    {
        if (CampaignMarkdownText.More(shown, total, hint) is { } line)
        {
            b.Append(line).Append('\n');
        }
    }

    private static void AddIf(List<string> parts, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add(value);
        }
    }

    private static string OneLine(string text) =>
        string.Join(' ', text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string Number(long value) => value.ToString("N0", Invariant);
}
