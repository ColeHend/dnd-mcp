using System.ComponentModel;
using System.Text.Json;

namespace DndMcp.Domain.Campaign.Ops;

/// <summary>
/// One operation of a <c>campaign_write</c> batch: a flat union of every op's fields, discriminated by <see cref="Op"/>
/// (the <c>ModifierSpec</c> precedent).
///
/// <para>
/// <b>Flat rather than one class per op</b> because the MCP SDK builds one schema per parameter type and has no
/// discriminated union a model reliably fills, and the host's argument guard validates each array item against exactly
/// one item schema (it cannot branch on a discriminator; an <c>anyOf</c> would switch its checks off). Separate typed
/// arrays per op would lose the order inside the batch, and an op may reference an entity an earlier op created. The
/// price is that the schema allows every field on every op, so <see cref="CampaignOpFields"/> holds the per-op table and
/// <see cref="CampaignOpValidation"/> refuses a field the op does not take ("upsert does not take \"statement\"").
/// Silently ignoring it would let the model believe it wrote something it did not.
/// </para>
/// <para>
/// Every property is nullable so "not given" is visible to that check and to the write path, which applies defaults
/// (and distinguishes "leave the summary alone" from "set it to empty"). Wire names are snake_case.
/// </para>
/// </summary>
public sealed class CampaignOpSpec
{
    [Description("Required: upsert, delete, restore, link, unlink, fact, status, objective, tick or answer.")]
    public string? Op { get; init; }

    [Description("The entity or fact the op acts on: kind:slug, a slug, e:<n>, f:<n> or a code such as \"Q22\". upsert: omit it to find or create by kind and name.")]
    public string? Ref { get; init; }

    [Description("upsert: the entity kind, e.g. character, location, faction, item, quest, thread, question, secret, beat, clock, rule, work, note.")]
    public string? Kind { get; init; }

    [Description("upsert: the entity's name, one line; with kind and no ref it finds the entity by name or creates it.")]
    public string? Name { get; init; }

    [Description("upsert: the subtype for the kind, e.g. character: pc, npc, creature, deity, companion; faction: party, band, organization.")]
    public string? Subtype { get; init; }

    [Description("upsert, on create only: the handle's slug, e.g. \"old-king\". Default: made from the name. It never changes afterwards.")]
    public string? Slug { get; init; }

    [Description("upsert, fact: a register code such as \"Q22\" or \"F36\". A proposed entity or fact with no code gets the next F code.")]
    public string? Code { get; init; }

    [Description("upsert: a one-line summary, at most 1,000 characters.")]
    public string? Summary { get; init; }

    [Description("upsert: the full description in markdown (what player views may see).")]
    public string? BodyMd { get; init; }

    [Description("upsert: author-only markdown; never shown to any other perspective.")]
    public string? SecretMd { get; init; }

    [Description("upsert, status: the kind's status (quest: open, active, resolved…; character: alive, dead…). link: current, former, planned or rumored. objective: open, done, failed, skipped or hidden.")]
    public string? Status { get; init; }

    [Description("Who may see it: public, party, restricted (only knowers named in known_by, and the author) or author. link and objective: public, party or author.")]
    public string? Visibility { get; init; }

    [Description("upsert, fact: canon, played, ruled, lean, planned, proposed, accepted, struck or superseded.")]
    public string? CanonStatus { get; init; }

    [Description("upsert, fact: confirmed, approximate, reconstructed or unverified.")]
    public string? Confidence { get; init; }

    [Description("upsert: the parent entity's handle (a location's region, a beat's arc).")]
    public string? Parent { get; init; }

    [Description("upsert: a number that orders siblings (lower first).")]
    public double? SortKey { get; init; }

    [Description("upsert, fact: where it comes from, e.g. \"party-and-band.md:19\" or \"session 12 recap\".")]
    public string? Source { get; init; }

    [Description("upsert: the session number the entity was introduced in.")]
    public int? IntroducedSession { get; init; }

    [Description("upsert: aliases to add or update, e.g. [{\"alias\": \"the sorcerer king\", \"visibility\": \"party\"}].")]
    public IReadOnlyList<AliasSpec?>? Aliases { get; init; }

    [Description("upsert: aliases to remove.")]
    public IReadOnlyList<string>? RemoveAliases { get; init; }

    [Description("upsert: tags to add, e.g. [\"void\", \"arc-3\"].")]
    public IReadOnlyList<string>? Tags { get; init; }

    [Description("upsert: tags to remove.")]
    public IReadOnlyList<string>? RemoveTags { get; init; }

    [Description("upsert, link (a relation): extra fields as a merge patch: keys given are set, a key set to null is removed, others are kept. Keys are letters, digits, _ and -, e.g. \"dm_pronouns\".")]
    public Dictionary<string, JsonElement>? Data { get; init; }

    [Description("upsert with kind clock: the clock's segments and settings; segments is required when creating one.")]
    public ClockSpec? Clock { get; init; }

    [Description("link, unlink: the entity the relation starts from, e.g. \"character:belmakor\".")]
    public string? From { get; init; }

    [Description("link, unlink: the relation in snake_case, e.g. member_of, ally_of, leads_to (beats), same_as (another campaign's entity).")]
    public string? Rel { get; init; }

    [Description("link, unlink: the entity the relation points to; same_as may name another campaign's entity as \"one-piece/character:keras\".")]
    public string? To { get; init; }

    [Description("link: a short label for the relation.")]
    public string? Label { get; init; }

    [Description("link: how from feels about to, -100 (hostile) to 100 (devoted).")]
    public int? Attitude { get; init; }

    [Description("link: the relation holds both ways (allies, siblings). Default false.")]
    public bool? Symmetric { get; init; }

    [Description("link: the session number the relation began in.")]
    public int? Since { get; init; }

    [Description("link: the session number the relation ended in.")]
    public int? Until { get; init; }

    [Description("link with rel leads_to between two beats (required there, and the only other field such a link keeps): all_of (every prerequisite) or any_of (at least one).")]
    public string? Mode { get; init; }

    [Description("link with rel same_as to another campaign's entity (a cross-link, which keeps only this note): why the two entities are the same.")]
    public string? Note { get; init; }

    [Description("fact: the claim, at most 4,000 characters; required to create a fact.")]
    public string? Statement { get; init; }

    [Description("fact: canon, ruling, secret, rumor, belief, clue, theory or meta.")]
    public string? FactType { get; init; }

    [Description("fact: whether it is true in the world: true, false, partial or unknown.")]
    public string? Truth { get; init; }

    [Description("fact: handles of the entities it is about, e.g. [\"character:old-king\"].")]
    public IReadOnlyList<string>? About { get; init; }

    [Description("fact: links with a role, e.g. [{\"ref\": \"secret:fruits-are-the-seal\", \"role\": \"clue_for\"}].")]
    public IReadOnlyList<FactLinkSpec?>? Links { get; init; }

    [Description("fact: its reveal gate (conditions, routes, forbidden words); an empty object removes it.")]
    public GateSpec? Gate { get; init; }

    [Description("upsert, fact: give the next code of this letter, e.g. \"C\" for C14.")]
    public string? AutoCode { get; init; }

    [Description("upsert, fact: who knows it, e.g. [{\"who\": \"party\", \"state\": \"knows\"}, {\"who\": \"character:belmakor\", \"state\": \"unaware\"}].")]
    public IReadOnlyList<KnowerSpec?>? KnownBy { get; init; }

    [Description("fact: handles of the facts this one rests on; superseding one of them flags this fact for a re-check.")]
    public IReadOnlyList<string>? DependsOn { get; init; }

    [Description("fact: the fact this one replaces; that fact becomes superseded and its dependents are listed.")]
    public string? Supersedes { get; init; }

    [Description("fact: the fact that replaces this one; this fact becomes superseded and its dependents are listed.")]
    public string? SupersededBy { get; init; }

    [Description("fact: the session number it was established in. Default: the batch's session when canon_status is played.")]
    public int? EstablishedSession { get; init; }

    [Description("objective: the 1-based position of the quest's objective to update; omit it to add a new objective.")]
    public int? Objective { get; init; }

    [Description("objective: the objective's text, one line.")]
    public string? Text { get; init; }

    [Description("objective: progress so far, 0 to progress_max.")]
    public int? Progress { get; init; }

    [Description("objective: the progress that completes it, 1 or more.")]
    public int? ProgressMax { get; init; }

    [Description("tick: segments to fill, -100 to 100 but not 0 (negative unticks). Default 1.")]
    public int? Amount { get; init; }

    [Description("answer: the question's answer in markdown.")]
    public string? AnswerMd { get; init; }

    [Description("answer: the entity or fact that answers the question.")]
    public string? AnsweredBy { get; init; }
}

/// <summary>One alias of an entity, with who may see it.</summary>
public sealed class AliasSpec
{
    [Description("Required. Another name for the entity, one line, e.g. \"the sorcerer king\".")]
    public string? Alias { get; init; }

    [Description("Who may see the alias: public, party (default), restricted or author. Players find an entity by its public and party aliases; one they know under another name (known_as) only by that name and its party aliases (a public epithet would give the identity away).")]
    public string? Visibility { get; init; }
}

/// <summary>A clock's mechanics (a <c>clock</c> entity's side table).</summary>
public sealed class ClockSpec
{
    [Description("Segments, 1-100; required when creating a clock.")]
    public int? Segments { get; init; }

    [Description("Segments filled so far, 0 to segments. Default 0.")]
    public int? Filled { get; init; }

    [Description("What one segment is: segment (default), round, hour, day, session or week.")]
    public string? Unit { get; init; }

    [Description("What happens when the clock fills, in markdown.")]
    public string? OnFillMd { get; init; }

    [Description("The players can see the clock. Default false.")]
    public bool? ShownToPlayers { get; init; }

    [Description("The front the clock belongs to, e.g. \"front:the-void\".")]
    public string? Front { get; init; }
}

/// <summary>A fact's tie to an entity, with its role.</summary>
public sealed class FactLinkSpec
{
    [Description("Required. The entity's handle.")]
    public string? Ref { get; init; }

    [Description("about (default), source, location, clue_for, evidence or contradicts.")]
    public string? Role { get; init; }
}
