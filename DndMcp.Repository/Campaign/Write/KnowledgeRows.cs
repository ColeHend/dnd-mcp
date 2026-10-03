using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Core;
using K = DndMcp.Domain.Campaign.CampaignValues.KnowerKinds;
using S = DndMcp.Domain.Campaign.CampaignValues.KnowledgeStates;

namespace DndMcp.Repository.Campaign.Write;

/// <summary>What a knowledge row is about: a fact or an entity, with the ref and visibility the warnings need.</summary>
/// <param name="FactId">The fact, when the target is a fact.</param>
/// <param name="EntityId">The entity, when the target is an entity (awareness: met, heard, unrecognized, …).</param>
/// <param name="Ref">The author-view ref (<c>f:12</c>, <c>character:old-king</c>).</param>
/// <param name="Visibility">The target's visibility after the op that writes the row.</param>
internal sealed record KnowledgeTarget(string? FactId, string? EntityId, string Ref, string Visibility)
{
    public static KnowledgeTarget Of(FactRow fact) => new(fact.Id, null, WriteBatch.Ref(fact), fact.Visibility);

    public static KnowledgeTarget Of(EntityRow entity) => new(null, entity.Id, WriteBatch.Ref(entity), entity.Visibility);
}

/// <summary>A resolved knower: its kind, its character id (for a character), and how the author view prints it.</summary>
internal sealed record Knower(string Kind, string? CharacterId, string Text);

/// <summary>One knowledge row a batch wrote, for the reveal checks.</summary>
/// <param name="Target">What it is about.</param>
/// <param name="Knower">Who.</param>
/// <param name="State">The row's state after the write.</param>
/// <param name="LearnedSession">The row's learned session number after the write.</param>
/// <param name="OpIndex">The op (or list item) that wrote it.</param>
internal sealed record KnowledgeChange(KnowledgeTarget Target, Knower Knower, string State, int? LearnedSession, int OpIndex)
{
    public bool Aware => S.AwareStates.Contains(State);
}

/// <summary>
/// Writes knowledge rows for a batch: the ONE helper behind an op's <c>known_by</c> and campaign_knowledge record,
/// reveal and retract (contract §3.4), so the reveal checks, the author-visibility warning and the learned-session default
/// cannot differ between the two tools. One Belmakor-sized mistake here (a reveal through campaign_write that skipped the
/// gate check campaign_knowledge runs) would be a secret reaching the table with no warning.
///
/// <para>
/// <b>One row per knower per target</b> (the schema's two partial unique indexes): a second write for the same knower
/// updates the row. Only what is given changes (known_as, how, note, via); <c>state</c> defaults to <c>knows</c>. The
/// learned session is the one given, else, for a new row or one whose state changes (learned, confirmed or forgotten
/// tonight), the batch's session context; a row re-recorded in the same state keeps the session it learned in
/// (re-recording must not move a reveal to tonight, which would change who was "present" for it). A state change made
/// with no session context (between sessions) keeps the recorded session too (review C02): writing NULL there would
/// erase a recorded session, and a NULL learned session applies at every session, so the party would have "known" the
/// fact as of every earlier session and an absent member would know it outright.
/// </para>
/// </summary>
internal sealed class KnowledgeRows
{
    private readonly WriteBatch _batch;

    public KnowledgeRows(WriteBatch batch) => _batch = batch;

    /// <summary>Every row written (not deleted) by this batch, in order.</summary>
    public List<KnowledgeChange> Changes { get; } = [];

    /// <summary>Resolves <c>who</c> (party, table, public, dm, author, character:&lt;handle&gt;) to a knower of this campaign.</summary>
    public Knower ResolveKnower(string subject, string where, string? who)
    {
        string kind;
        CampaignHandle? character;
        try
        {
            (kind, character) = KnowerSpecs.Parse(who);
        }
        catch (DndInputException ex)
        {
            throw WriteBatch.Problem(subject, $"{where}: {ex.Message}");
        }

        if (kind != K.Character)
        {
            return new Knower(kind, null, kind);
        }

        var entity = _batch.RequireEntity(subject, where, "who", character!.Text, CampaignValues.Kinds.Character);
        return new Knower(K.Character, entity.Id, CampaignValues.PerspectiveKinds.CharacterPrefix + entity.Slug);
    }

    /// <summary>
    /// Writes (inserts or updates) the row of <paramref name="spec"/>'s knower on <paramref name="target"/>. Adds the
    /// author-visibility warning, and notes an aware row for a non-author knower on a fact for the reveal checks.
    /// </summary>
    /// <returns>The outcome (created, updated, unchanged), the changed fields and the knower as printed.</returns>
    public (string Outcome, IReadOnlyList<string> Changed, Knower Knower) Write(
        string subject,
        string where,
        KnowledgeTarget target,
        KnowerSpec spec,
        string action,
        int opIndex)
    {
        var knower = ResolveKnower(subject, where, spec.Who);
        var state = spec.State is null ? S.Knows : S.Set.TryMatch(spec.State, out var canonical)
            ? canonical
            : throw WriteBatch.Problem(subject, $"{where}: state \"{WriteBatch.Echo(spec.State)}\" is not a knowledge state; give {S.Set.List}.");
        var viaId = spec.Via is null ? null : _batch.RequireEntity(subject, where, "via", spec.Via).Id;
        var explicitSession = spec.Session is { } number ? _batch.RequireSession(subject, where, "session", number) : null;
        if (target.FactId is not null)
        {
            _batch.Reveals.EnsureBaseline();
            _batch.DerivationNeeded = true;
        }

        NoteText(target, opIndex);
        var match = Match(target, knower);
        var key = _batch.Recorder.FindKey("knowledge", match);
        string outcome;
        IReadOnlyList<string> changed;
        string? learnedSessionId;
        string? previousState = null;
        if (key is null)
        {
            learnedSessionId = explicitSession ?? _batch.SessionId;
            var row = new Dictionary<string, object?>(match, StringComparer.Ordinal)
            {
                ["campaign_id"] = _batch.Campaign.Id,
                ["state"] = state,
                ["known_as"] = Clean(spec.KnownAs),
                ["learned_session_id"] = learnedSessionId,
                ["via_entity_id"] = viaId,
                ["how"] = Clean(spec.How),
                ["note"] = Clean(spec.Note),
            };
            _batch.Recorder.Insert("knowledge", row, action);
            outcome = WriteOutcomes.Created;
            changed = ["state"];
        }
        else
        {
            var current = _batch.Recorder.Read("knowledge", key)!;
            previousState = (string)current["state"]!;
            // The learned session is when the row's current state began: it moves with a change of state (learned,
            // confirmed or forgotten tonight) and stays when only the wording or provenance changes, or when the change is
            // made with no session to date it (class summary: never NULL a recorded session implicitly).
            learnedSessionId = explicitSession ??
                               (previousState != state && _batch.SessionId is not null ? _batch.SessionId : current["learned_session_id"] as string);
            var changes = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["state"] = state,
                ["learned_session_id"] = learnedSessionId,
            };
            if (spec.KnownAs is not null)
            {
                changes["known_as"] = Clean(spec.KnownAs);
            }

            if (spec.How is not null)
            {
                changes["how"] = Clean(spec.How);
            }

            if (spec.Note is not null)
            {
                changes["note"] = Clean(spec.Note);
            }

            if (viaId is not null)
            {
                changes["via_entity_id"] = viaId;
            }

            changed = _batch.Update("knowledge", key, changes, action);
            outcome = changed.Count == 0 ? WriteOutcomes.Unchanged : WriteOutcomes.Updated;
        }

        var learned = _batch.SessionNumberOf(learnedSessionId);
        Changes.Add(new KnowledgeChange(target, knower, state, learned, opIndex));
        // "Gives an aware row" (§3.4): a new row, or one whose state changed to an aware one (suspects → knows is the
        // reveal). Re-recording the same state, or only its known_as / how / note, reveals nothing new, so it neither
        // repeats the gate warnings nor the author-visibility one.
        var aware = S.AwareStates.Contains(state) && previousState != state;
        if (aware && _batch.IsNonAuthorKnower(knower.Kind) && target.Visibility == CampaignValues.Visibilities.Author)
        {
            _batch.Warnings.Add(new WriteWarning(WarningKinds.AuthorVisibility, WarningSeverities.Warning,
                $"{target.Ref} has author visibility: the row for {knower.Text} is stored, but author visibility hides it from every " +
                "view but the author's regardless of who knows it; give it visibility restricted to let its knowers see it.",
                opIndex));
        }

        if (aware && target.FactId is not null && _batch.IsNonAuthorKnower(knower.Kind))
        {
            _batch.Reveals.Note(Changes[^1]);
        }

        return (outcome, changed, knower);
    }

    /// <summary>Deletes the row of <paramref name="knower"/> on <paramref name="target"/>; false when there is none.</summary>
    /// <param name="opIndex">The list item (or op) it is for, for the warnings.</param>
    public bool Delete(KnowledgeTarget target, Knower knower, string action, int opIndex)
    {
        if (target.FactId is not null)
        {
            _batch.Reveals.EnsureBaseline();
            _batch.DerivationNeeded = true;
        }

        // Retracting a disguising row shows the knower the target under its own name: that text is new to it.
        NoteText(target, opIndex);
        var key = _batch.Recorder.FindKey("knowledge", Match(target, knower));
        return key is not null && _batch.Recorder.Delete("knowledge", key, action);
    }

    // What the knower reads of the target may change with its row: the player-text check snapshots it first.
    private void NoteText(KnowledgeTarget target, int opIndex)
    {
        if (target.FactId is not null)
        {
            _batch.PlayerText.Fact(target.FactId, opIndex);
        }
        else if (target.EntityId is not null)
        {
            _batch.PlayerText.Entity(target.EntityId, opIndex);
        }
    }

    private static Dictionary<string, object?> Match(KnowledgeTarget target, Knower knower) => new(StringComparer.Ordinal)
    {
        ["fact_id"] = target.FactId,
        ["entity_id"] = target.EntityId,
        ["knower_kind"] = knower.Kind,
        ["knower_id"] = knower.CharacterId,
    };

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
