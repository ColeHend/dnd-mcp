using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Core;
using CV = DndMcp.Domain.Campaign.CampaignValues;
using St = DndMcp.Domain.Campaign.CampaignValues.Statuses;

namespace DndMcp.Repository.Campaign.Write;

/// <summary>
/// The entity ops of campaign_write: upsert, delete / restore, status, objective, tick and answer (contract §6).
///
/// <para>
/// <b>Upsert finds before it creates.</b> With <c>ref</c> the entity must exist (a typo must not create a twin); with
/// kind and name it is found by name (<see cref="CampaignText.Key"/>, then without a leading article), so a second
/// "Iron Guts" updates the first instead of creating <c>iron-guts-2</c>. The kind never changes, and sessions belong to
/// campaign_session. A new entity's slug is made from its name once and never changes with a rename; a taken slug (a
/// soft-deleted entity keeps its slug) gets <c>-2</c>, <c>-3</c> … and a warning naming the holder, since the author may
/// have meant to restore it.
/// </para>
/// <para>
/// Every change goes through the batch's recorder, so each is logged and undoable; "changed fields" are the columns (and
/// data keys, aliases, tags, clock fields, knowers) whose stored values actually changed.
/// </para>
/// </summary>
internal static class EntityOps
{
    /// <summary>The deepest an entity may sit under parents (a root is 0).</summary>
    public const int MaxDepth = 8;

    public static void Upsert(WriteBatch b, OpScope o)
    {
        var s = o.Spec;
        EntityRow? existing;
        string kind;
        if (s.Ref is not null)
        {
            existing = b.RequireEntity(OpScope.Subject, o.Where, "ref", s.Ref,
                notFoundHint: "To create an entity, give kind and name without ref.");
            kind = existing.Kind;
        }
        else
        {
            kind = Canonical.Of(CV.Kinds.Set, s.Kind!);
            try
            {
                existing = b.Resolver.EntityByName(kind, s.Name!);
            }
            catch (DndInputException ex)
            {
                throw o.Fail(ex.Message);
            }
        }

        o.Check(new ResolvedKinds(Ref: existing?.Kind ?? kind));
        if (existing is null)
        {
            Create(b, o, kind);
        }
        else
        {
            if (s.Slug is not null && !string.Equals(s.Slug.Trim(), existing.Slug, StringComparison.OrdinalIgnoreCase))
            {
                throw o.Fail(
                    $"a {kind} with this name exists as {existing.SeqHandle}; slug is only for creating and never changes. " +
                    $"To update it, leave slug out; to create a second {kind} with the same name, give a different name.");
            }

            Update(b, o, existing);
        }
    }

    private static void Create(WriteBatch b, OpScope o, string kind)
    {
        var s = o.Spec;
        var name = s.Name!.Trim();
        var subtype = s.Subtype is null ? null : Canonical.Of(CV.Subtypes.ByKind[kind], s.Subtype);
        var slug = ChooseSlug(b, o, kind, name, s.Slug);
        var visibility = s.Visibility is null ? b.DefaultEntityVisibility(kind) : Canonical.Of(CV.Visibilities.Set, s.Visibility);
        var status = s.Status is null ? St.Defaults.GetValueOrDefault(kind) : Canonical.Of(St.ByKind[kind], s.Status);
        var canon = Canonical.OrNull(CV.CanonStatuses.Set, s.CanonStatus) ?? CV.CanonStatuses.Canon;
        var confidence = Canonical.OrNull(CV.Confidences.Set, s.Confidence) ?? CV.Confidences.Confirmed;
        var code = b.AssignCode(OpScope.Subject, o.Where, s.Code, s.AutoCode, canon, null, null);
        var parentId = s.Parent is null ? null : Parent(b, o, null, s.Parent).Id;
        var introduced = s.IntroducedSession is { } number ? b.RequireSession(OpScope.Subject, o.Where, "introduced_session", number) : null;
        var data = s.Data is null ? new JsonObject() : WriteBatch.MergeData(OpScope.Subject, o.Where, "data", null, s.Data);
        if (kind == CV.Kinds.Rule && subtype == CV.Subtypes.RevealRule)
        {
            CheckRevealRule(b, o, data);
        }

        if (kind == CV.Kinds.Clock && s.Clock?.Segments is null)
        {
            throw o.Fail("clock.segments is required to create a clock, e.g. {\"clock\": {\"segments\": 6, \"unit\": \"day\"}}.");
        }

        var row = b.Recorder.Insert("entity", new Dictionary<string, object?>
        {
            ["campaign_id"] = b.Campaign.Id,
            ["kind"] = kind,
            ["subtype"] = subtype,
            ["slug"] = slug,
            ["code"] = code,
            ["name"] = name,
            ["summary"] = s.Summary?.Trim() ?? string.Empty,
            ["body_md"] = s.BodyMd ?? string.Empty,
            ["secret_md"] = s.SecretMd ?? string.Empty,
            ["status"] = status,
            ["visibility"] = visibility,
            ["canon_status"] = canon,
            ["confidence"] = confidence,
            ["parent_id"] = parentId,
            ["sort_key"] = s.SortKey,
            ["data"] = data,
            ["introduced_session_id"] = introduced,
            ["source"] = s.Source?.Trim(),
        }, o.Op);
        var entity = b.EntityById((string)row["id"]!)!;
        b.PlayerText.EntityCreated(entity.Id, o.Index);
        var fields = CampaignOpFields.All.Where(f => f.IsGiven(s) && f.Name is not ("op" or "known_by" or "aliases" or "remove_aliases" or "tags" or "remove_tags" or "clock"))
            .Select(f => f.Name).ToList();
        fields.AddRange(Aliases(b, o, entity));
        fields.AddRange(Tags(b, o, entity));
        fields.AddRange(Clock(b, o, entity));
        fields.AddRange(KnownBy(b, o, KnowledgeTarget.Of(entity)));
        if (kind == CV.Kinds.Secret && s.Status is not null)
        {
            b.SecretStatusSetByHand[entity.Id] = status!;
        }

        b.Applied.Add(new AppliedOp(o.Index, o.Op, WriteBatch.Ref(entity), WriteOutcomes.Created, fields.Distinct().ToList(), code));
    }

    private static void Update(WriteBatch b, OpScope o, EntityRow e)
    {
        var s = o.Spec;
        b.PlayerText.Entity(e.Id, o.Index);
        var changes = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (s.Name is not null)
        {
            var name = s.Name.Trim();
            changes["name"] = name;
            if (CampaignText.Key(name) != CampaignText.Key(e.Name) && CampaignSlugs.From(name, e.Kind) != e.Slug)
            {
                b.Warnings.Add(new WriteWarning(WarningKinds.SlugKept, WarningSeverities.Warning,
                    $"{e.Handle} is renamed; its slug stays {e.Slug} (handles never change with a name, so notes that cite it keep working).",
                    o.Index));
            }

            if (NameTwins(b, e, name) is { Count: > 0 } twins)
            {
                b.Warnings.Add(new WriteWarning(WarningKinds.NameTwin, WarningSeverities.Warning,
                    $"{e.SeqHandle} now shares its name with {string.Join(", ", twins)} (another {e.Kind}): an upsert by kind and name can " +
                    "match both and be refused as ambiguous, so refer to them by ref (or rename one).", o.Index));
            }
        }

        var subtype = s.Subtype is null ? e.Subtype : Canonical.Of(CV.Subtypes.ByKind[e.Kind], s.Subtype);
        if (s.Subtype is not null)
        {
            changes["subtype"] = subtype;
        }

        if (s.Summary is not null)
        {
            changes["summary"] = s.Summary.Trim();
        }

        if (s.BodyMd is not null)
        {
            changes["body_md"] = s.BodyMd;
        }

        if (s.SecretMd is not null)
        {
            changes["secret_md"] = s.SecretMd;
        }

        string? status = null;
        if (s.Status is not null)
        {
            status = Canonical.Of(St.ByKind[e.Kind], s.Status);
            changes["status"] = status;
        }

        if (s.Visibility is not null)
        {
            changes["visibility"] = Canonical.Of(CV.Visibilities.Set, s.Visibility);
        }

        var canon = Canonical.OrNull(CV.CanonStatuses.Set, s.CanonStatus) ?? e.CanonStatus;
        if (s.CanonStatus is not null)
        {
            changes["canon_status"] = canon;
        }

        if (s.Confidence is not null)
        {
            changes["confidence"] = Canonical.Of(CV.Confidences.Set, s.Confidence);
        }

        var code = b.AssignCode(OpScope.Subject, o.Where, s.Code, s.AutoCode, canon, e.Code, e.SeqHandle);
        if (code != e.Code)
        {
            changes["code"] = code;
        }

        if (s.Parent is not null)
        {
            changes["parent_id"] = Parent(b, o, e, s.Parent).Id;
        }

        if (s.SortKey is not null)
        {
            changes["sort_key"] = s.SortKey;
        }

        if (s.Source is not null)
        {
            changes["source"] = s.Source.Trim();
        }

        if (s.IntroducedSession is { } introduced)
        {
            changes["introduced_session_id"] = b.RequireSession(OpScope.Subject, o.Where, "introduced_session", introduced);
        }

        var data = s.Data is null ? null : WriteBatch.MergeData(OpScope.Subject, o.Where, "data", e.Data, s.Data);
        if (data is not null)
        {
            changes["data"] = data;
        }

        if (e.Kind == CV.Kinds.Rule && subtype == CV.Subtypes.RevealRule && (s.Data is not null || s.Subtype is not null))
        {
            CheckRevealRule(b, o, data ?? (JsonNode.Parse(e.Data) as JsonObject ?? new JsonObject()));
        }

        var beatsBefore = e.Kind == CV.Kinds.Beat && status is not null ? Beats(b) : null;
        var fields = b.Update("entity", e.Id, changes, o.Op).ToList();
        fields.AddRange(Aliases(b, o, e));
        fields.AddRange(Tags(b, o, e));
        fields.AddRange(Clock(b, o, e));
        var updated = b.EntityById(e.Id)!;
        fields.AddRange(KnownBy(b, o, KnowledgeTarget.Of(updated)));
        if (status is not null)
        {
            StatusSideEffects(b, o, e, status, beatsBefore, byStatusOp: false);
        }

        b.Applied.Add(new AppliedOp(o.Index, o.Op, WriteBatch.Ref(updated), fields.Count == 0 ? WriteOutcomes.Unchanged : WriteOutcomes.Updated,
            fields.Distinct().ToList(), updated.Code));
    }

    /// <summary>Soft-deletes or restores an entity or a fact (its handles, slug and code stay reserved).</summary>
    public static void DeleteOrRestore(WriteBatch b, OpScope o, bool delete)
    {
        var text = o.Spec.Ref!;
        CampaignHandle.TryParse(text, out var handle, out _);
        EntityRow? entity;
        FactRow? fact;
        try
        {
            (entity, fact) = b.Resolver.TryEntityOrFact(handle, includeDeleted: true);
        }
        catch (DndInputException ex)
        {
            throw o.Fail(ex.Message);
        }

        string reference;
        bool done;
        if (entity is not null)
        {
            if (entity.Id == b.Campaign.PartyId)
            {
                throw o.Fail($"{handle.Text} is the campaign's party faction, which party knowledge and membership hang on; it cannot be {(delete ? "deleted" : "restored")}.");
            }

            if (entity.Kind == CV.Kinds.Session)
            {
                throw o.Fail("a session cannot be deleted or restored: a played session is history (fix it with campaign_session record_past).");
            }

            reference = WriteBatch.Ref(entity);
            b.PlayerText.Entity(entity.Id, o.Index);
            done = delete ? b.Recorder.SoftDelete("entity", entity.Id) : b.Recorder.Restore("entity", entity.Id);
            if (entity.Kind == CV.Kinds.Secret)
            {
                b.DerivationNeeded = true;
            }
        }
        else if (fact is not null)
        {
            b.Reveals.EnsureBaseline();
            b.DerivationNeeded = true;
            reference = WriteBatch.Ref(fact);
            b.PlayerText.Fact(fact.Id, o.Index);
            done = delete ? b.Recorder.SoftDelete("fact", fact.Id) : b.Recorder.Restore("fact", fact.Id);
        }
        else
        {
            throw o.Fail($"no entity or fact {handle.Text} in this campaign.");
        }

        if (!done)
        {
            b.Warnings.Add(new WriteWarning(WarningKinds.AlreadyThere, WarningSeverities.Warning,
                delete ? $"{reference} was already deleted; nothing changed." : $"{reference} is not deleted; nothing to restore.", o.Index));
        }

        b.Applied.Add(new AppliedOp(o.Index, o.Op, reference,
            !done ? WriteOutcomes.Unchanged : delete ? WriteOutcomes.Deleted : WriteOutcomes.Restored,
            done ? ["deleted_at"] : []));
    }

    public static void Status(WriteBatch b, OpScope o)
    {
        var e = b.RequireEntity(OpScope.Subject, o.Where, "ref", o.Spec.Ref!);
        o.Check(new ResolvedKinds(Ref: e.Kind));
        var status = Canonical.Of(St.ByKind[e.Kind], o.Spec.Status!);
        var beatsBefore = e.Kind == CV.Kinds.Beat ? Beats(b) : null;
        var changed = b.Update("entity", e.Id, new Dictionary<string, object?> { ["status"] = status }, o.Op);
        StatusSideEffects(b, o, e, status, beatsBefore, byStatusOp: true);
        b.Applied.Add(new AppliedOp(o.Index, o.Op, WriteBatch.Ref(e), changed.Count == 0 ? WriteOutcomes.Unchanged : WriteOutcomes.Updated, changed, e.Code));
    }

    public static void Objective(WriteBatch b, OpScope o)
    {
        var s = o.Spec;
        var quest = b.RequireEntity(OpScope.Subject, o.Where, "ref", s.Ref!);
        o.Check(new ResolvedKinds(Ref: quest.Kind));
        var objectives = b.Connection.Query<ObjectiveRow>(
            $"SELECT {ObjectiveRow.Columns} FROM objective WHERE quest_id = @id ORDER BY ordinal, id", new { id = quest.Id }, b.Transaction).ToList();
        var status = Canonical.OrNull(CV.ObjectiveStatuses.Set, s.Status);
        var resolved = status is CV.ObjectiveStatuses.Done or CV.ObjectiveStatuses.Failed or CV.ObjectiveStatuses.Skipped ? b.SessionId : null;
        if (s.Objective is null)
        {
            var visibility = s.Visibility is null ? DefaultObjectiveVisibility(quest) : Canonical.Of(CV.Visibilities.RowSet, s.Visibility);
            b.Recorder.Insert("objective", new Dictionary<string, object?>
            {
                ["quest_id"] = quest.Id,
                ["ordinal"] = (objectives.Count == 0 ? 0 : objectives.Max(x => x.Ordinal)) + 1,
                ["text"] = s.Text!.Trim(),
                ["status"] = status ?? CV.ObjectiveStatuses.Open,
                ["progress"] = s.Progress,
                ["progress_max"] = s.ProgressMax,
                ["visibility"] = visibility,
                ["resolved_session_id"] = resolved,
            }, o.Op);
            var number = objectives.Count + 1;
            b.Applied.Add(new AppliedOp(o.Index, o.Op, WriteBatch.Ref(quest), WriteOutcomes.Created, [$"objective {WriteBatch.Number(number)}"]));
            return;
        }

        var index = s.Objective.Value;
        if (index > objectives.Count)
        {
            throw o.Fail(
                $"{quest.SeqHandle} has {WriteBatch.Number(objectives.Count)} objective{(objectives.Count == 1 ? string.Empty : "s")}; there is no objective {WriteBatch.Number(index)} (omit objective to add one).");
        }

        var current = objectives[index - 1];
        var progress = s.Progress is { } p ? p : current.Progress;
        var max = s.ProgressMax is { } m ? m : current.ProgressMax;
        if (progress is { } finalProgress && max is { } finalMax && finalProgress > finalMax)
        {
            throw o.Fail($"objective {WriteBatch.Number(index)}: progress {WriteBatch.Number(finalProgress)} would be more than progress_max {WriteBatch.Number(finalMax)}.");
        }

        var changes = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (s.Text is not null)
        {
            changes["text"] = s.Text.Trim();
        }

        if (status is not null)
        {
            changes["status"] = status;
            if (status != current.Status)
            {
                changes["resolved_session_id"] = resolved;
            }
        }

        if (s.Progress is not null)
        {
            changes["progress"] = s.Progress;
        }

        if (s.ProgressMax is not null)
        {
            changes["progress_max"] = s.ProgressMax;
        }

        if (s.Visibility is not null)
        {
            changes["visibility"] = Canonical.Of(CV.Visibilities.RowSet, s.Visibility);
        }

        var changed = b.Update("objective", current.Id, changes, o.Op);
        b.Applied.Add(new AppliedOp(o.Index, o.Op, WriteBatch.Ref(quest), changed.Count == 0 ? WriteOutcomes.Unchanged : WriteOutcomes.Updated,
            changed.Select(c => $"objective {WriteBatch.Number(index)} {c}").ToList()));
    }

    public static void Tick(WriteBatch b, OpScope o)
    {
        var e = b.RequireEntity(OpScope.Subject, o.Where, "ref", o.Spec.Ref!);
        o.Check(new ResolvedKinds(Ref: e.Kind));
        var clock = b.Connection.QueryFirstOrDefault<ClockRow>(
                        $"SELECT {ClockRow.Columns} FROM clock WHERE entity_id = @id", new { id = e.Id }, b.Transaction) ??
                    throw o.Fail($"{e.SeqHandle} has no segments yet; give them with {{\"op\": \"upsert\", \"ref\": \"{e.SeqHandle}\", \"clock\": {{\"segments\": 6}}}}.");
        var amount = o.Spec.Amount ?? 1;
        var wanted = clock.Filled + amount;
        var filled = Math.Clamp(wanted, 0, clock.Segments);
        if (filled != wanted)
        {
            b.Warnings.Add(new WriteWarning(WarningKinds.ClockClamped, WarningSeverities.Warning,
                $"{e.Handle} has {WriteBatch.Number(clock.Segments)} segments: {WriteBatch.Number(clock.Filled)} {(amount < 0 ? "-" : "+")} {WriteBatch.Number(Math.Abs(amount))} " +
                $"is past its {(wanted < 0 ? "start" : "end")}, so filled is {WriteBatch.Number(filled)}.", o.Index));
        }

        var changed = b.Update("clock", e.Id, new Dictionary<string, object?> { ["filled"] = filled }, o.Op).Select(c => "clock." + c).ToList();
        changed.AddRange(ClockFill(b, o, e, clock.Filled, filled, clock.Segments, clock.OnFillMd));
        b.Applied.Add(new AppliedOp(o.Index, o.Op, WriteBatch.Ref(e), changed.Count == 0 ? WriteOutcomes.Unchanged : WriteOutcomes.Ticked, changed, e.Code));
    }

    public static void Answer(WriteBatch b, OpScope o)
    {
        var s = o.Spec;
        var question = b.RequireEntity(OpScope.Subject, o.Where, "ref", s.Ref!);
        o.Check(new ResolvedKinds(Ref: question.Kind));
        var patch = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (s.AnswerMd is not null)
        {
            patch["answer_md"] = JsonSerializer.SerializeToElement(s.AnswerMd);
        }

        var fields = new List<string>();
        if (s.AnsweredBy is not null)
        {
            CampaignHandle.TryParse(s.AnsweredBy, out var handle, out _);
            EntityRow? entity;
            FactRow? fact;
            try
            {
                (entity, fact) = b.Resolver.TryEntityOrFact(handle);
            }
            catch (DndInputException ex)
            {
                throw o.Fail($"answered_by: {ex.Message}");
            }

            if (fact is not null)
            {
                patch["answered_by"] = JsonSerializer.SerializeToElement(WriteBatch.Ref(fact));
            }
            else if (entity is not null)
            {
                if (entity.Id == question.Id)
                {
                    throw o.Fail("answered_by is the question itself.");
                }

                if (LinkOps.EnsureRelation(b, o.Op, question, CV.Rels.AnsweredBy, entity))
                {
                    fields.Add("answered_by");
                }
            }
            else
            {
                throw o.Fail($"answered_by: no entity or fact {handle.Text} in this campaign.");
            }
        }

        var changes = new Dictionary<string, object?>(StringComparer.Ordinal) { ["status"] = St.QuestionAnswered };
        if (patch.Count > 0)
        {
            changes["data"] = WriteBatch.MergeData(OpScope.Subject, o.Where, "data", question.Data, patch);
        }

        fields.InsertRange(0, b.Update("entity", question.Id, changes, o.Op));
        b.Applied.Add(new AppliedOp(o.Index, o.Op, WriteBatch.Ref(question), fields.Count == 0 ? WriteOutcomes.Unchanged : WriteOutcomes.Answered,
            fields, question.Code));
    }

    // ---- shared pieces ----------------------------------------------------------------------------------------------

    /// <summary>
    /// A new entity's slug: the explicit one (refused when taken, deleted rows included), else made from the name, with
    /// <c>-2</c>, <c>-3</c> … and a warning when that is taken.
    /// </summary>
    private static string ChooseSlug(WriteBatch b, OpScope o, string kind, string name, string? explicitSlug)
    {
        if (explicitSlug is not null)
        {
            var slug = explicitSlug.Trim().ToLowerInvariant();
            if (b.Resolver.TryEntity(new CampaignHandle.EntityBySlug(null, slug), includeDeleted: true) is { } holder)
            {
                throw o.Fail(
                    $"slug {slug} is taken by {holder.SeqHandle}{(holder.IsDeleted ? $" (deleted: restore it with {{\"op\": \"restore\", \"ref\": \"{holder.SeqHandle}\"}} instead?)" : string.Empty)}; " +
                    "give another slug, or leave slug out to derive one from the name.");
            }

            return slug;
        }

        var baseSlug = CampaignSlugs.From(name, kind);
        var taken = b.Resolver.TryEntity(new CampaignHandle.EntityBySlug(null, baseSlug), includeDeleted: true);
        if (taken is null)
        {
            return baseSlug;
        }

        for (var n = 2; ; n++)
        {
            var candidate = CampaignSlugs.WithSuffix(baseSlug, n);
            if (b.Resolver.TryEntity(new CampaignHandle.EntityBySlug(null, candidate), includeDeleted: true) is not null)
            {
                continue;
            }

            var restore = taken.IsDeleted
                ? $" It is deleted: to bring it back instead, restore it with {{\"op\": \"restore\", \"ref\": \"{taken.SeqHandle}\"}}."
                : string.Empty;
            b.Warnings.Add(new WriteWarning(WarningKinds.SlugCollision, WarningSeverities.Warning,
                $"slug {baseSlug} is taken by {taken.Handle} ({taken.SeqHandle}), so the new {kind} is {kind}:{candidate}.{restore}", o.Index));
            return candidate;
        }
    }

    /// <summary>
    /// Other live entities of <paramref name="e"/>'s kind that a by-name lookup of <paramref name="newName"/> would also
    /// find (same name key, a leading article ignored), as <c>e:&lt;n&gt;</c>. Upsert finds before it creates, so twins
    /// can only come from a rename, and they make every later by-name upsert of that name ambiguous.
    /// </summary>
    private static IReadOnlyList<string> NameTwins(WriteBatch b, EntityRow e, string newName)
    {
        var key = CampaignText.KeyWithoutArticle(newName);
        if (key.Length == 0 || key == CampaignText.KeyWithoutArticle(e.Name))
        {
            return [];
        }

        return b.Connection.Query<(long Seq, string Name)>(
                "SELECT seq, name FROM entity WHERE campaign_id = @campaignId AND kind = @kind AND id <> @id AND deleted_at IS NULL ORDER BY seq",
                new { campaignId = b.Campaign.Id, kind = e.Kind, id = e.Id }, b.Transaction)
            .Where(other => CampaignText.KeyWithoutArticle(other.Name) == key)
            .Select(other => "e:" + WriteBatch.Number(other.Seq))
            .ToList();
    }

    /// <summary>The parent an entity may take: in this campaign, not itself or a descendant, at most <see cref="MaxDepth"/> deep.</summary>
    private static EntityRow Parent(WriteBatch b, OpScope o, EntityRow? self, string text)
    {
        var parent = b.RequireEntity(OpScope.Subject, o.Where, "parent", text);
        if (self is not null && parent.Id == self.Id)
        {
            throw o.Fail("parent is the entity itself; an entity cannot be its own parent.");
        }

        var depth = 0;
        var at = parent;
        while (at.ParentId is { } up)
        {
            if (self is not null && up == self.Id)
            {
                throw o.Fail($"parent {parent.SeqHandle} is inside {self.SeqHandle}; that would make a cycle.");
            }

            at = b.EntityById(up) ?? throw new InvalidOperationException("A parent id names no entity.");
            depth++;
            if (depth > MaxDepth + 1)
            {
                break;
            }
        }

        var height = self is null ? 0 : SubtreeHeight(b, self.Id);
        if (depth + 1 + height > MaxDepth)
        {
            throw o.Fail($"under {parent.SeqHandle} the entity would sit {WriteBatch.Number(depth + 1 + height)} levels deep; at most {WriteBatch.Number(MaxDepth)}.");
        }

        return parent;
    }

    private static int SubtreeHeight(WriteBatch b, string id)
    {
        var level = new List<string> { id };
        var height = 0;
        while (height <= MaxDepth)
        {
            var children = b.Connection.Query<string>(
                "SELECT id FROM entity WHERE parent_id IN @ids AND campaign_id = @campaignId", new { ids = level, campaignId = b.Campaign.Id }, b.Transaction).ToList();
            if (children.Count == 0)
            {
                break;
            }

            height++;
            level = children;
        }

        return height;
    }

    /// <summary>A reveal rule's data must parse as one (forbidden words, fact handles in until that exist here).</summary>
    private static void CheckRevealRule(WriteBatch b, OpScope o, JsonObject data)
    {
        using var document = JsonDocument.Parse(CampaignLogJson.Serialize(data));
        var problems = RevealRuleData.Validate(document.RootElement, "data");
        if (problems.Count > 0)
        {
            throw WriteBatch.Problem(OpScope.Subject, $"{o.Where}: a reveal_rule's {string.Join("; ", problems)}");
        }

        foreach (var until in RevealRuleData.Parse(document.RootElement).Until)
        {
            b.RequireFact(OpScope.Subject, o.Where, "data until", until);
        }
    }

    private static string DefaultObjectiveVisibility(EntityRow quest) =>
        quest.Visibility is CV.Visibilities.Public or CV.Visibilities.Party ? quest.Visibility : CV.Visibilities.Author;

    private static List<string> Aliases(WriteBatch b, OpScope o, EntityRow e)
    {
        var s = o.Spec;
        var changed = new List<string>();
        if (s.Aliases is null && s.RemoveAliases is null)
        {
            return changed;
        }

        var existing = b.Connection.Query<AliasRow>(
            $"SELECT {AliasRow.Columns} FROM entity_alias WHERE entity_id = @id", new { id = e.Id }, b.Transaction).ToList();
        foreach (var alias in s.Aliases ?? [])
        {
            var text = alias!.Alias!.Trim();
            var key = CampaignText.Key(text);
            var visibility = Canonical.OrNull(CV.Visibilities.Set, alias.Visibility);
            var match = existing.FirstOrDefault(a => CampaignText.Key(a.Alias) == key);
            if (match is null)
            {
                b.Recorder.Insert("entity_alias", new Dictionary<string, object?>
                {
                    ["entity_id"] = e.Id,
                    ["alias"] = text,
                    ["visibility"] = visibility ?? CV.Visibilities.Party,
                }, o.Op);
                existing.Add(new AliasRow(e.Id, text, visibility ?? CV.Visibilities.Party));
                changed.Add("aliases");
            }
            else if (visibility is not null && visibility != match.Visibility)
            {
                b.Update("entity_alias", [e.Id, match.Alias], new Dictionary<string, object?> { ["visibility"] = visibility }, o.Op);
                changed.Add("aliases");
            }
        }

        var index = 0;
        foreach (var remove in s.RemoveAliases ?? [])
        {
            index++;
            var key = CampaignText.Key(remove);
            var match = existing.FirstOrDefault(a => CampaignText.Key(a.Alias) == key);
            if (match is null)
            {
                b.Warnings.Add(new WriteWarning(WarningKinds.Nothing, WarningSeverities.Warning,
                    $"remove_aliases item {WriteBatch.Number(index)} matches no alias of {e.Handle}; nothing removed.", o.Index));
                continue;
            }

            b.Recorder.Delete("entity_alias", [e.Id, match.Alias], o.Op);
            existing.Remove(match);
            changed.Add("aliases");
        }

        return changed;
    }

    private static List<string> Tags(WriteBatch b, OpScope o, EntityRow e)
    {
        var s = o.Spec;
        var changed = new List<string>();
        if (s.Tags is null && s.RemoveTags is null)
        {
            return changed;
        }

        var tags = b.Connection.Query<TagRow>(
            $"SELECT {TagRow.Columns} FROM tag WHERE campaign_id = @campaignId", new { campaignId = b.Campaign.Id }, b.Transaction).ToList();
        foreach (var tag in s.Tags ?? [])
        {
            var text = tag.Trim();
            var key = CampaignText.Key(text);
            var row = tags.FirstOrDefault(t => CampaignText.Key(t.Name) == key);
            if (row is null)
            {
                var inserted = b.Recorder.Insert("tag", new Dictionary<string, object?> { ["campaign_id"] = b.Campaign.Id, ["name"] = text }, o.Op);
                row = new TagRow((string)inserted["id"]!, b.Campaign.Id, text);
                tags.Add(row);
            }

            var link = new Dictionary<string, object?> { ["entity_id"] = e.Id, ["tag_id"] = row.Id };
            if (b.Recorder.FindKey("entity_tag", link) is null)
            {
                b.Recorder.Insert("entity_tag", link, o.Op);
                changed.Add("tags");
            }
        }

        var index = 0;
        foreach (var remove in s.RemoveTags ?? [])
        {
            index++;
            var key = CampaignText.Key(remove);
            var row = tags.FirstOrDefault(t => CampaignText.Key(t.Name) == key);
            if (row is null || !b.Recorder.Delete("entity_tag", [e.Id, row.Id], o.Op))
            {
                b.Warnings.Add(new WriteWarning(WarningKinds.Nothing, WarningSeverities.Warning,
                    $"remove_tags item {WriteBatch.Number(index)}: {e.Handle} has no such tag; nothing removed.", o.Index));
                continue;
            }

            changed.Add("tags");
        }

        return changed;
    }

    private static List<string> Clock(WriteBatch b, OpScope o, EntityRow e)
    {
        var spec = o.Spec.Clock;
        if (e.Kind != CV.Kinds.Clock)
        {
            return [];
        }

        var current = b.Connection.QueryFirstOrDefault<ClockRow>(
            $"SELECT {ClockRow.Columns} FROM clock WHERE entity_id = @id", new { id = e.Id }, b.Transaction);
        var frontId = spec?.Front is null ? null : b.RequireEntity(OpScope.Subject, o.Where, "clock.front", spec.Front, CV.Kinds.Front).Id;
        if (current is null)
        {
            if (spec?.Segments is null)
            {
                throw o.Fail("clock.segments is required: this clock has none yet, e.g. {\"clock\": {\"segments\": 6}}.");
            }

            b.Recorder.Insert("clock", new Dictionary<string, object?>
            {
                ["entity_id"] = e.Id,
                ["segments"] = spec.Segments,
                ["filled"] = spec.Filled ?? 0,
                ["unit"] = Canonical.OrNull(CV.ClockUnits.Set, spec.Unit) ?? CV.ClockUnits.Segment,
                ["front_id"] = frontId,
                ["shown_to_players"] = spec.ShownToPlayers ?? false,
                ["on_fill_md"] = spec.OnFillMd ?? string.Empty,
            }, o.Op);
            var created = new List<string> { "clock" };
            created.AddRange(ClockFill(b, o, e, 0, spec.Filled ?? 0, spec.Segments.Value, spec.OnFillMd ?? string.Empty));
            return created;
        }

        if (spec is null)
        {
            return [];
        }

        var segments = spec.Segments ?? checked((int)current.Segments);
        var filled = spec.Filled ?? checked((int)current.Filled);
        if (filled > segments)
        {
            throw o.Fail($"clock.filled would be {WriteBatch.Number(filled)}, more than its {WriteBatch.Number(segments)} segments.");
        }

        var changes = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (spec.Segments is not null)
        {
            changes["segments"] = spec.Segments;
        }

        if (spec.Filled is not null)
        {
            changes["filled"] = spec.Filled;
        }

        if (spec.Unit is not null)
        {
            changes["unit"] = Canonical.Of(CV.ClockUnits.Set, spec.Unit);
        }

        if (frontId is not null)
        {
            changes["front_id"] = frontId;
        }

        if (spec.ShownToPlayers is not null)
        {
            changes["shown_to_players"] = spec.ShownToPlayers;
        }

        if (spec.OnFillMd is not null)
        {
            changes["on_fill_md"] = spec.OnFillMd;
        }

        var changed = b.Update("clock", e.Id, changes, o.Op).Select(c => "clock." + c).ToList();
        changed.AddRange(ClockFill(b, o, e, current.Filled, filled, segments, spec.OnFillMd ?? current.OnFillMd));
        return changed;
    }

    /// <summary>
    /// A clock that reaches its last segment is done (its status, and a consequence carrying on_fill_md); one unticked
    /// below it runs again. Returns "status" when the status changed.
    /// </summary>
    private static List<string> ClockFill(WriteBatch b, OpScope o, EntityRow e, long before, long after, long segments, string onFill)
    {
        var current = b.EntityById(e.Id)!;
        string? status = null;
        if (after >= segments && before < segments)
        {
            status = St.ClockDone;
            b.Consequences.Add(new Consequence(ConsequenceKinds.ClockFilled,
                $"Clock {current.Name} ({current.Handle}) filled{(string.IsNullOrWhiteSpace(onFill) ? "." : ": " + onFill.Trim())}", current.Handle, []));
        }
        else if (after < segments && current.Status == St.ClockDone)
        {
            status = St.ClockRunning;
        }

        return status is null
            ? []
            : b.Update("entity", e.Id, new Dictionary<string, object?> { ["status"] = status }, o.Op).ToList();
    }

    private static List<string> KnownBy(WriteBatch b, OpScope o, KnowledgeTarget target)
    {
        var fields = new List<string>();
        var knowers = o.Spec.KnownBy;
        if (knowers is null)
        {
            return fields;
        }

        for (var i = 0; i < knowers.Count; i++)
        {
            var (outcome, _, knower) = b.Knowledge.Write(OpScope.Subject, $"{o.Where}: known_by item {WriteBatch.Number(i + 1)}", target, knowers[i]!, o.Op, o.Index);
            if (outcome != WriteOutcomes.Unchanged)
            {
                fields.Add("known_by " + knower.Text);
            }
        }

        return fields;
    }

    /// <summary>What a status change sets off: the derived-secret warning, a death's review note, newly reachable beats.</summary>
    private static void StatusSideEffects(WriteBatch b, OpScope o, EntityRow before, string status, IReadOnlyDictionary<string, BeatProgress>? beatsBefore, bool byStatusOp)
    {
        switch (before.Kind)
        {
            case CV.Kinds.Secret:
                b.SecretStatusSetByHand[before.Id] = status;
                break;
            case CV.Kinds.Character when status == St.CharacterDead && before.Status != St.CharacterDead:
                b.Warnings.Add(new WriteWarning(WarningKinds.CharacterDead, WarningSeverities.Warning,
                    $"{before.Handle} is dead now: review its member_of relation (status former, until) and what it knows (its knowledge rows are kept).",
                    o.Index));
                break;
            case CV.Kinds.Question when byStatusOp && status == St.QuestionAnswered:
                b.Warnings.Add(new WriteWarning(WarningKinds.UseAnswer, WarningSeverities.Advisory,
                    $"{before.Handle} is marked answered without an answer; {{\"op\": \"answer\", \"ref\": \"{before.Handle}\", \"answer_md\": …, \"answered_by\": …}} records what answered it.",
                    o.Index));
                break;
            case CV.Kinds.Beat when beatsBefore is not null:
                var newly = BeatReachability.NewlyReachable(beatsBefore, Beats(b));
                if (newly.Count > 0)
                {
                    var refs = newly.Select(b.EntityRefById).ToList();
                    b.Consequences.Add(new Consequence(ConsequenceKinds.BeatsReachable,
                        $"Now reachable: {string.Join(", ", refs)}.", WriteBatch.Ref(before), refs));
                }

                break;
        }
    }

    private static IReadOnlyDictionary<string, BeatProgress> Beats(WriteBatch b)
    {
        var beats = b.Connection.Query<(string Id, string Name, string? Status)>(
                "SELECT id, name, status FROM entity WHERE campaign_id = @campaignId AND kind = @beat AND deleted_at IS NULL ORDER BY seq",
                new { campaignId = b.Campaign.Id, beat = CV.Kinds.Beat }, b.Transaction)
            .Select(r => new BeatNode(r.Id, r.Name, r.Status ?? St.BeatPending)).ToList();
        var edges = b.Connection.Query<(string From, string To, string Mode)>(
                "SELECT from_beat_id, to_beat_id, mode FROM beat_edge WHERE campaign_id = @campaignId", new { campaignId = b.Campaign.Id }, b.Transaction)
            .Select(r => new BeatLink(r.From, r.To, r.Mode)).ToList();
        return BeatReachability.Compute(beats, edges);
    }
}
