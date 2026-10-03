using System.ComponentModel;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Formatting.Campaign;
using DndMcp.Hosting;
using DndMcp.Repository.Campaign.Write;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace DndMcp.Tools;

/// <summary>
/// <c>campaign_write</c>: every change to a campaign's entities, relations, facts and progress, as one batch of ops that
/// is applied together or not at all and undone together (<c>campaign_history undo</c>). Thin by design: resolve the
/// campaign, hand the ops to <see cref="CampaignWriter"/>, render with <see cref="WriteMarkdown"/>.
///
/// <para>
/// <b>ops is typed <see cref="CampaignOpSpec"/>[]</b>, the flat union of every op's fields (the <c>ModifierSpec</c>
/// precedent), so the argument guard checks every item against the published schema before this runs: an unknown field
/// anywhere inside an op (<c>"stauts"</c>, a misspelt knower field, a gate key) is refused with the item and field named,
/// where System.Text.Json would silently drop it and the model would believe it had written it. What the schema cannot
/// say, the Domain validator does (<see cref="CampaignOpValidation"/>, run by the writer before anything is written): a
/// field the op does not take ("upsert does not take \"statement\""), each op's required fields, every vocabulary, each
/// numbered <c>ops item N (…)</c>. The whole input schema is about 13,600 characters; it stays typed while it is under the
/// contract's 24,000-character budget (<c>CampaignWriteToolTests</c> pins the size), past which ops would be published
/// untyped with <see cref="CheckedAsAttribute"/> and the guard would check it the same way, but the model would then learn
/// the fields only from the description.
/// </para>
/// <para>
/// <b>Annotations: destructive, not idempotent.</b> The delete op soft-deletes, and a gated reveal or a supersession
/// changes what the table may be told; replaying a call makes a second batch (and a tick ticks twice). Closed-world: it
/// writes campaigns.db only.
/// </para>
/// <para>
/// <b>dry_run</b> runs the whole batch inside the write transaction and rolls it back, so the preview shows exactly the
/// outcomes, warnings and register codes the real call will produce, and nothing persists (no batch exists to undo).
/// </para>
/// </summary>
public sealed class CampaignWriteTools
{
    internal const string Example =
        "{\"ops\": [{\"op\": \"upsert\", \"kind\": \"character\", \"name\": \"Iron Guts\", \"subtype\": \"npc\", \"visibility\": \"party\"}, " +
        "{\"op\": \"fact\", \"statement\": \"Iron Guts owes the band a favour.\", \"about\": [\"character:iron-guts\"], " +
        "\"known_by\": [{\"who\": \"party\"}]}], \"reason\": \"Session 12 recap\", \"dry_run\": true}";

    private readonly CampaignService _campaigns;

    public CampaignWriteTools(CampaignService campaigns)
    {
        _campaigns = campaigns;
    }

    // Destructive (the delete op) and not idempotent (every call is a new batch; tick adds); closed-world: campaigns.db only.
    [McpServerTool(Name = "campaign_write", Title = "Write to a campaign", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "Change a campaign: create and update entities, relations, facts and progress in one batch of ops, applied in order " +
        "and together (one undo unit) or not at all. A gated fact reaching a knower before its gate is met, a superseded fact's " +
        "dependents, and text players can read that uses a forbidden word or a name they do not use are applied with " +
        "warnings, never refused. Preview anything large with dry_run first.\n" +
        "- ops (required, at most 50): each op names its op; what each requires:\n" +
        "  - upsert: ref, or kind and name (creates when no entity of that kind has the name); then any of subtype, summary, " +
        "body_md, secret_md, status, visibility, canon_status, parent, aliases, tags, data, clock, known_by, ...\n" +
        "  - delete, restore: ref (soft delete).\n" +
        "  - link, unlink: from, rel, to (leads_to between beats needs mode; same_as may name another campaign's entity).\n" +
        "  - fact: statement to create, ref to update; with fact_type, truth, canon_status, about, links, gate, known_by, " +
        "depends_on, supersedes or superseded_by, established_session, ...\n" +
        "  - status: ref and status. tick: ref of a clock, amount (default 1). answer: ref of a question, answer_md, answered_by.\n" +
        "  - objective: ref of a quest or thread and text to add one; objective (its number) to update it.\n" +
        "  Every field's description says which ops take it; a field the op does not take is refused.\n" +
        "- campaign: its slug; default the active campaign.\n" +
        "- session: the session number the batch belongs to; default the live session, else none. Knowledge written in the " +
        "batch is learned in it.\n" +
        "- reason: why, kept in the history.\n" +
        "- dry_run: true to preview: everything runs, nothing is written.\n" +
        "The result gives the batch id; campaign_history {\"action\": \"undo\", \"batch_id\": ...} reverses it.\n" +
        "Example: " + Example)]
    public string Write(
        [Description("The ops, applied in order as one batch (at most 50). An op may use an entity or fact an earlier op created: an " +
            "entity by kind:slug, a fact by the code you gave it (code \"A1\"); f:<n> numbers are shared by all campaigns, so a " +
            "new campaign's first fact is not f:1. Each field's description names the ops that take it.")]
        CampaignOpSpec[] ops,
        [Description("The campaign's slug (or exact name), e.g. \"belmakor\". Default: the one chosen with campaign use, else the only one.")]
        string? campaign = null,
        [Description("The session number the batch belongs to, e.g. 12. Default: the live session, else none.")]
        int? session = null,
        [Description("Why, one line, kept with the batch in the history, e.g. \"Session 12 recap\".")]
        string? reason = null,
        [Description("true: run the whole batch and report it, then write nothing (no batch exists). Default false.")]
        [AIParameterName("dry_run")] bool? dryRun = null)
    {
        var row = _campaigns.Resolve(campaign);
        var result = new CampaignWriter(_campaigns.Database).Apply(row, ops ?? [], WriteContext.For(session, reason, dryRun ?? false));
        return WriteMarkdown.Format(row, result, ops?.Length ?? 0);
    }
}
