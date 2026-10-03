using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Core;
using Xunit;
using static DndMcp.Tests.Campaign.CampaignOpFieldsTests;

namespace DndMcp.Tests.Campaign;

/// <summary>
/// Invariant: a batch that cannot be right is refused before anything is written, with one message that locates each
/// problem as <c>ops item N (&lt;op&gt; &lt;ref&gt;)</c>, says what is wrong and what is accepted, lists up to five, and
/// never echoes a statement or secret body; a batch that could be right passes.
/// </summary>
public sealed class CampaignOpValidationTests
{
    public static TheoryData<string> ValidOps() => new(
    [
        """{"op": "upsert", "kind": "character", "name": "Iron Guts", "subtype": "npc", "visibility": "restricted"}""",
        """{"op": "Upsert", "kind": "Character", "name": "The Old King", "slug": "old-king", "status": "unknown", "aliases": [{"alias": "the sorcerer king", "visibility": "party"}, {"alias": "Keras", "visibility": "author"}], "secret_md": "Cole's old PC.", "known_by": [{"who": "party", "state": "met", "known_as": "the old king", "session": 3}]}""",
        """{"op": "upsert", "ref": "character:iron-guts", "status": "dead", "tags": ["salvage"], "remove_tags": ["alive"], "data": {"rank": null}}""",
        """{"op": "upsert", "ref": "e:12", "summary": "Now a rival."}""",
        """{"op": "upsert", "kind": "clock", "name": "Three days to the blood moon", "clock": {"segments": 6, "unit": "day"}}""",
        """{"op": "upsert", "kind": "rule", "subtype": "reveal_rule", "name": "No name, no timespan", "visibility": "author", "data": {"forbidden_terms": ["Keras"], "forbidden_patterns": ["<number>-year-old"]}}""",
        """{"op": "upsert", "kind": "character", "name": "Invented bosun", "canon_status": "proposed"}""",
        """{"op": "upsert", "kind": "lore", "name": "The white lines", "sort_key": -2.5, "auto_code": "C"}""",
        """{"op": "delete", "ref": "f:12"}""",
        """{"op": "restore", "ref": "character:iron-guts"}""",
        """{"op": "link", "from": "character:belmakor", "rel": "Member Of", "to": "faction:the-party", "since": 1}""",
        """{"op": "link", "from": "character:old-king", "rel": "same_as", "to": "one-piece/character:keras", "note": "Cole's old PC"}""",
        """{"op": "link", "from": "beat:the-statue", "rel": "leads_to", "to": "beat:the-fight", "mode": "any_of"}""",
        """{"op": "unlink", "from": "character:belmakor", "rel": "ally_of", "to": "character:vars"}""",
        """{"op": "fact", "statement": "Eating a devil fruit breaks part of Baal's seal.", "fact_type": "secret", "visibility": "author", "links": [{"ref": "secret:fruits-are-the-seal", "role": "about"}], "gate": {"after": ["f:31", "f:32"], "with": ["f:2"], "prefer": ["f:33"], "forbidden_terms": ["seal"], "forbidden_until": ["f:31"], "routes": [{"id": "illusion", "clues": ["f:45"]}, {"id": "temple-arithmetic", "clues": ["f:46", "f:47"]}], "min_routes": 2}}""",
        """{"op": "fact", "ref": "f:20", "canon_status": "superseded", "superseded_by": "f:21"}""",
        """{"op": "fact", "ref": "F36", "canon_status": "accepted"}""",
        // FD6 (review C11): a register code typed in capitals with its suffix is accepted, as a ref and as a new code.
        """{"op": "fact", "ref": "F56A", "canon_status": "accepted"}""",
        """{"op": "fact", "statement": "The second clue.", "code": "F56B"}""",
        """{"op": "fact", "statement": "The fragments have run down for 400 years.", "depends_on": ["f:20"], "about": ["question:q22", "Q22"]}""",
        """{"op": "fact", "statement": "The party learned of Tristan's death.", "known_by": [{"who": "party"}], "established_session": 1, "canon_status": "played", "gate": {}}""",
        """{"op": "status", "ref": "quest:old-kings-errand", "status": "Resolved"}""",
        """{"op": "status", "ref": "e:4", "status": "met"}""",
        """{"op": "objective", "ref": "quest:war-gods-axe", "text": "Assemble the War God's axe", "progress": 2}""",
        """{"op": "objective", "ref": "quest:war-gods-axe", "objective": 1, "status": "done"}""",
        """{"op": "objective", "ref": "thread:old-kings-errand", "text": "Find out what the old king wants"}""",
        """{"op": "objective", "ref": "e:9", "objective": 2, "progress": 1}""",
        """{"op": "link", "from": "character:vars", "rel": "ally_of", "to": "character:serif", "since": 3, "until": 3, "status": "former"}""",
        """{"op": "link", "from": "location:the-deck", "rel": "leads_to", "to": "location:the-hold", "label": "a ladder"}""",
        """{"op": "link", "from": "e:4", "rel": "leads_to", "to": "beat:the-fight", "mode": "all_of"}""",
        """{"op": "upsert", "kind": "lore", "name": "Before the first session", "introduced_session": 0, "data": {"dm_pronouns": "she", "hit-points": 12, "Rank2": null}}""",
        """{"op": "fact", "statement": "Session zero set the tone.", "established_session": 0}""",
        """{"op": "tick", "ref": "clock:three-days"}""",
        """{"op": "tick", "ref": "clock:three-days", "amount": -1}""",
        """{"op": "answer", "ref": "Q7", "answer_md": "The founders of G.O.D.S. Co."}""",
        """{"op": "answer", "ref": "question:q21", "answered_by": "f:40"}""",
    ]);

    [Theory]
    [MemberData(nameof(ValidOps))]
    public void Validate_PlausibleOp_Passes(string json)
    {
        Assert.Empty(CampaignOpValidation.Problems([Bind(json)]));
    }

    [Theory]
    // The item itself and the op.
    [InlineData("null", "ops item 1 is null")]
    [InlineData("""{"ref": "f:1"}""", "ops item 1: op is required; ops are upsert, delete, restore, link, unlink, fact, status, objective, tick, answer.")]
    [InlineData("""{"op": "progress", "ref": "q"}""", "ops item 1: op \"progress\" is not an op; ops are upsert")]
    // Required fields per op.
    [InlineData("""{"op": "upsert"}""", "ops item 1 (upsert): give ref (an existing entity) or kind and name")]
    [InlineData("""{"op": "upsert", "kind": "character"}""", "ops item 1 (upsert character): name is required with kind when there is no ref")]
    [InlineData("""{"op": "upsert", "name": "Iron Guts"}""", "ops item 1 (upsert): kind is required with name when there is no ref")]
    [InlineData("""{"op": "delete"}""", "ops item 1 (delete): ref is required: the entity or fact to delete")]
    [InlineData("""{"op": "restore"}""", "ops item 1 (restore): ref is required")]
    [InlineData("""{"op": "link", "from": "character:belmakor"}""", "ops item 1 (link character:belmakor): from, rel and to are required (missing rel, to)")]
    [InlineData("""{"op": "unlink", "rel": "ally_of"}""", "(missing from, to)")]
    [InlineData("""{"op": "fact"}""", "ops item 1 (fact): give ref (a fact to update) or statement (a new fact)")]
    [InlineData("""{"op": "status", "ref": "quest:x"}""", "ops item 1 (status quest:x): status is required")]
    [InlineData("""{"op": "objective", "ref": "quest:x"}""", "text is required to add an objective")]
    [InlineData("""{"op": "objective", "ref": "quest:x", "objective": 2}""", "give what changes: text, status, progress, progress_max or visibility.")]
    [InlineData("""{"op": "tick"}""", "ops item 1 (tick): ref is required: the clock to tick")]
    [InlineData("""{"op": "answer", "ref": "Q7"}""", "ops item 1 (answer Q7): give answer_md (the answer) or answered_by")]
    // Vocabularies, per kind where they depend on it.
    [InlineData("""{"op": "upsert", "kind": "wizard", "name": "x"}""", "kind \"wizard\" is not a kind; give character, location")]
    [InlineData("""{"op": "upsert", "kind": "character", "name": "x", "subtype": "song"}""", "subtype \"song\" is not a character subtype; give pc, npc, creature, deity or companion.")]
    [InlineData("""{"op": "upsert", "kind": "quest", "name": "x", "subtype": "side"}""", "kind quest takes no subtype; leave it out.")]
    [InlineData("""{"op": "upsert", "ref": "e:3", "subtype": "nope"}""", "subtype \"nope\" is not a subtype of any kind")]
    [InlineData("""{"op": "upsert", "kind": "character", "name": "x", "status": "open"}""", "status \"open\" is not a character status; give alive")]
    [InlineData("""{"op": "upsert", "kind": "lore", "name": "x", "status": "open"}""", "kind lore takes no status; leave it out.")]
    [InlineData("""{"op": "upsert", "ref": "e:3", "status": "sideways"}""", "status \"sideways\" is not a status of any kind")]
    [InlineData("""{"op": "status", "ref": "beat:x", "status": "done"}""", "status \"done\" is not a beat status; give pending, met or cut.")]
    [InlineData("""{"op": "upsert", "kind": "item", "name": "x", "visibility": "secret"}""", "visibility \"secret\" is not a visibility; give public, party, restricted or author.")]
    [InlineData("""{"op": "upsert", "kind": "item", "name": "x", "canon_status": "promoted"}""", "canon_status \"promoted\" is not a canon status")]
    [InlineData("""{"op": "upsert", "kind": "item", "name": "x", "confidence": "sure"}""", "confidence \"sure\" is not a confidence")]
    [InlineData("""{"op": "link", "from": "a", "rel": "ally_of", "to": "b", "visibility": "restricted"}""", "is not a relation visibility; give public, party or author")]
    [InlineData("""{"op": "link", "from": "a", "rel": "ally_of", "to": "b", "status": "past"}""", "status \"past\" is not a relation status")]
    [InlineData("""{"op": "objective", "ref": "quest:x", "text": "t", "visibility": "restricted"}""", "is not an objective visibility")]
    [InlineData("""{"op": "objective", "ref": "quest:x", "text": "t", "status": "running"}""", "status \"running\" is not an objective status")]
    [InlineData("""{"op": "fact", "statement": "s", "fact_type": "lie"}""", "fact_type \"lie\" is not a fact type")]
    [InlineData("""{"op": "fact", "statement": "s", "truth": "maybe"}""", "truth \"maybe\" is not a truth")]
    [InlineData("""{"op": "fact", "statement": "s", "links": [{"ref": "character:x", "role": "mentions"}]}""", "links item 1: role \"mentions\" is not a fact link role")]
    [InlineData("""{"op": "upsert", "kind": "clock", "name": "c", "clock": {"segments": 4, "unit": "minute"}}""", "clock: unit \"minute\" is not a clock unit")]
    [InlineData("""{"op": "upsert", "kind": "item", "name": "x", "aliases": [{"alias": "y", "visibility": "hidden"}]}""", "aliases item 1: visibility \"hidden\" is not a visibility")]
    // Handles, and whether they can name what the field needs.
    [InlineData("""{"op": "upsert", "ref": "f:3", "summary": "s"}""", "ref \"f:3\" is a fact; give an entity handle")]
    [InlineData("""{"op": "fact", "ref": "character:old-king", "truth": "true"}""", "ref \"character:old-king\" is not a fact handle; give f:<n> or a register code")]
    [InlineData("""{"op": "fact", "statement": "s", "depends_on": ["character:x"]}""", "depends_on item 1 \"character:x\" is not a fact handle")]
    [InlineData("""{"op": "fact", "statement": "s", "about": ["f:3"]}""", "about item 1 \"f:3\" is a fact")]
    [InlineData("""{"op": "link", "from": "one-piece/character:keras", "rel": "same_as", "to": "character:old-king"}""", "from \"one-piece/character:keras\" names another campaign's entity")]
    [InlineData("""{"op": "link", "from": "character:old-king", "rel": "ally_of", "to": "one-piece/character:keras"}""", "to \"one-piece/character:keras\" names another campaign's entity")]
    [InlineData("""{"op": "upsert", "ref": "wizard:x", "summary": "s"}""", "ref: \"wizard:x\": \"wizard\" is not a kind.")]
    [InlineData("""{"op": "upsert", "kind": "character", "name": "x", "parent": "e:0"}""", "parent: \"e:0\": expected a positive whole number")]
    [InlineData("""{"op": "fact", "statement": "s", "known_by": [{"who": "character:x", "via": "f:3"}]}""", "known_by item 1 (character:x): via \"f:3\" must be an entity of this campaign")]
    // Rules between fields.
    [InlineData("""{"op": "upsert", "ref": "character:x", "kind": "item"}""", "kind item differs from the ref's kind character; an entity's kind never changes.")]
    [InlineData("""{"op": "upsert", "kind": "session", "name": "Session 3"}""", "sessions are written with campaign_session")]
    [InlineData("""{"op": "upsert", "ref": "session:3", "summary": "s"}""", "sessions are written with campaign_session")]
    [InlineData("""{"op": "upsert", "ref": "character:x", "slug": "y"}""", "slug is only for creating (a slug never changes)")]
    [InlineData("""{"op": "upsert", "kind": "character", "name": "x", "slug": "Not A Slug!"}""", "slug \"Not A Slug!\" must be lower-case letters and digits")]
    [InlineData("""{"op": "upsert", "kind": "character", "name": "x", "code": "F-3"}""", "code \"F-3\" is not a register code")]
    [InlineData("""{"op": "upsert", "kind": "character", "name": "x", "auto_code": "F1"}""", "auto_code \"F1\" must be 1-4 letters")]
    [InlineData("""{"op": "upsert", "kind": "character", "name": "x", "code": "F3", "auto_code": "F"}""", "give code (an explicit code) or auto_code")]
    [InlineData("""{"op": "upsert", "kind": "item", "name": "x", "clock": {"segments": 4}}""", "clock is only for kind clock, not item.")]
    [InlineData("""{"op": "upsert", "kind": "clock", "name": "c", "clock": {"segments": 101}}""", "clock: segments is 101; it is 1 to 100.")]
    [InlineData("""{"op": "upsert", "kind": "clock", "name": "c", "clock": {"segments": 4, "filled": 5}}""", "clock: filled is 5; it is 0 to 4 (at most segments).")]
    [InlineData("""{"op": "upsert", "ref": "character:x", "parent": "character:x"}""", "an entity cannot be its own parent")]
    [InlineData("""{"op": "link", "from": "a", "rel": "ally of!", "to": "b"}""", "rel \"ally of!\" must be a relation name in snake_case")]
    [InlineData("""{"op": "link", "from": "a", "rel": "ally_of", "to": "b", "mode": "all_of"}""", "mode is only for rel leads_to (a beat's prerequisites), not ally_of.")]
    [InlineData("""{"op": "link", "from": "a", "rel": "leads_to", "to": "b", "mode": "some_of"}""", "mode \"some_of\" is not a beat edge mode")]
    [InlineData("""{"op": "link", "from": "a", "rel": "ally_of", "to": "b", "note": "n"}""", "note is kept only on a same_as link")]
    // A link's shape: what it is stored as decides what it keeps.
    [InlineData("""{"op": "link", "from": "character:old-king", "rel": "same_as", "to": "one-piece/character:keras", "label": "l", "attitude": 5, "data": {"a": 1}}""",
        "ops item 1 (link character:old-king same_as one-piece/character:keras): does not take \"data\" or \"label\" or \"attitude\" here: rel same_as to another campaign's entity is a cross-link, which keeps only note.")]
    [InlineData("""{"op": "link", "from": "character:old-king", "rel": "same_as", "to": "one-piece/character:keras", "status": "former", "since": 1, "symmetric": true, "visibility": "author", "until": 2, "mode": "any_of"}""",
        "does not take \"status\" or \"visibility\" or \"symmetric\" or \"since\" or \"until\" or \"mode\" here: rel same_as to another campaign's entity is a cross-link")]
    [InlineData("""{"op": "link", "from": "character:a", "rel": "same_as", "to": "character:b", "note": "n"}""",
        "note is kept only on a same_as link to another campaign's entity (the cross-link's note); a same_as inside this campaign is a relation")]
    [InlineData("""{"op": "link", "from": "beat:a", "rel": "leads_to", "to": "beat:b", "mode": "all_of", "label": "l", "visibility": "author", "data": {"k": 1}}""",
        "ops item 1 (link beat:a leads_to beat:b): does not take \"visibility\" or \"data\" or \"label\" here: rel leads_to between two beats is a beat edge, which keeps only mode.")]
    [InlineData("""{"op": "link", "from": "beat:a", "rel": "leads_to", "to": "beat:b", "status": "current", "since": 1, "until": 2, "attitude": 1, "symmetric": false, "note": "n", "mode": "any_of"}""",
        "does not take \"status\" or \"attitude\" or \"symmetric\" or \"since\" or \"until\" or \"note\" here: rel leads_to between two beats is a beat edge")]
    [InlineData("""{"op": "link", "from": "beat:a", "rel": "Leads To", "to": "beat:b"}""", "mode is required with rel leads_to between two beats: all_of or any_of")]
    [InlineData("""{"op": "link", "from": "beat:a", "rel": "leads_to", "to": "beat:b", "mode": "some_of"}""", "mode \"some_of\" is not a beat edge mode")]
    [InlineData("""{"op": "link", "from": "location:a", "rel": "leads_to", "to": "beat:b", "mode": "all_of"}""", "mode is only for leads_to between two beats (a beat's prerequisites), not with a location")]
    [InlineData("""{"op": "link", "from": "beat:a", "rel": "leads_to", "to": "scene:b", "mode": "all_of"}""", "not with a scene; a leads_to between other entities is a plain relation.")]
    [InlineData("""{"op": "link", "from": "beat:a", "rel": "leads_to", "to": "item:b", "mode": "all_of"}""", "not with an item; a leads_to between other entities is a plain relation.")]
    [InlineData("""{"op": "link", "from": "a", "rel": "ally_of", "to": "A"}""", "from and to are the same entity")]
    [InlineData("""{"op": "link", "from": "a", "rel": "ally_of", "to": "b", "since": 5, "until": 2}""", "until (session 2) is before since (session 5).")]
    [InlineData("""{"op": "link", "from": "a", "rel": "ally_of", "to": "b", "attitude": 101}""", "attitude is 101; it is -100 to 100.")]
    [InlineData("""{"op": "fact", "statement": "s", "supersedes": "f:1", "superseded_by": "f:2"}""", "give supersedes (this fact replaces that one) or superseded_by")]
    [InlineData("""{"op": "fact", "ref": "f:1", "superseded_by": "f:1"}""", "a fact cannot supersede itself (f:1).")]
    [InlineData("""{"op": "fact", "ref": "f:1", "superseded_by": "f:2", "canon_status": "canon"}""", "superseded_by makes this fact superseded; canon_status canon contradicts it")]
    [InlineData("""{"op": "fact", "ref": "f:1", "depends_on": ["f:2", "F:1"]}""", "depends_on names f:1 itself")]
    [InlineData("""{"op": "fact", "ref": "f:1", "gate": {"after": ["f:1"]}}""", "gate names f:1 itself")]
    [InlineData("""{"op": "fact", "statement": "s", "gate": {"routes": [{"id": "a", "clues": ["f:1"], "min_clues": 2}]}}""", "gate routes item 1: min_clues is 2; it is 1 to 1 (the number of clues).")]
    [InlineData("""{"op": "objective", "ref": "character:x", "text": "t"}""", "objectives belong to a quest or a thread, not to kind character.")]
    [InlineData("""{"op": "link", "from": "a", "rel": "ally_of", "to": "b", "since": 3, "until": 2}""", "until (session 2) is before since (session 3).")]
    [InlineData("""{"op": "link", "from": "a", "rel": "ally_of", "to": "b", "since": -1}""", "since is -1; it is 0 to 100000 (a session number).")]
    [InlineData("""{"op": "objective", "ref": "quest:x", "text": "t", "progress": 6, "progress_max": 5}""", "progress 6 is more than progress_max 5.")]
    [InlineData("""{"op": "objective", "ref": "quest:x", "objective": 0, "status": "done"}""", "objective is 0; it is 1 to 1000 (the objective's 1-based position; omit it to add one).")]
    [InlineData("""{"op": "tick", "ref": "quest:x"}""", "only a clock is ticked, not kind quest.")]
    [InlineData("""{"op": "tick", "ref": "clock:x", "amount": 0}""", "amount 0 changes nothing")]
    [InlineData("""{"op": "tick", "ref": "clock:x", "amount": 101}""", "amount is 101; it is -100 to 100.")]
    [InlineData("""{"op": "answer", "ref": "quest:x", "answer_md": "a"}""", "only a question is answered, not kind quest.")]
    // Lengths, counts and ranges.
    [InlineData("""{"op": "upsert", "kind": "item", "name": "two\nlines"}""", "name must be one line of at most 200 characters.")]
    [InlineData("""{"op": "upsert", "kind": "item", "name": "   "}""", "name is blank")]
    [InlineData("""{"op": "upsert", "kind": "item", "name": "x", "introduced_session": -1}""", "introduced_session is -1; it is 0 to 100000 (a session number).")]
    [InlineData("""{"op": "fact", "statement": "s", "established_session": 100001}""", "established_session is 100001; it is 0 to 100000")]
    [InlineData("""{"op": "upsert", "kind": "item", "name": "x", "aliases": []}""", "aliases is empty")]
    [InlineData("""{"op": "upsert", "kind": "item", "name": "x", "aliases": [{"alias": "Guts"}, {"alias": "guts"}]}""", "aliases item 2 repeats item 1.")]
    [InlineData("""{"op": "upsert", "kind": "item", "name": "x", "aliases": [{"alias": "a"}, {"alias": "Guts"}], "remove_aliases": ["GUTS"]}""", "remove_aliases item 1 is also aliases item 2; add it or remove it, not both.")]
    [InlineData("""{"op": "upsert", "kind": "item", "name": "x", "tags": ["a"], "remove_tags": ["A"]}""", "remove_tags item 1 is also tags item 1; add it or remove it, not both.")]
    [InlineData("""{"op": "upsert", "kind": "item", "name": "x", "tags": ["a", "b", "B"]}""", "tags item 3 repeats item 2.")]
    [InlineData("""{"op": "upsert", "kind": "item", "name": "x", "data": {"a.b": 1}}""", "data key \"a.b\" must be letters, digits, \"_\" and \"-\"")]
    [InlineData("""{"op": "upsert", "kind": "item", "name": "x", "data": {"c[0]": 1}}""", "data key \"c[0]\" must be letters, digits")]
    [InlineData("""{"op": "upsert", "kind": "item", "name": "x", "data": {"$d": 1}}""", "data key \"$d\" must be letters, digits")]
    [InlineData("""{"op": "upsert", "kind": "item", "name": "x", "data": {"hit points": 1}}""", "data key \"hit points\" must be letters, digits")]
    [InlineData("""{"op": "upsert", "kind": "item", "name": "x", "data": {"say \"hi\"": 1}}""", "must be letters, digits, \"_\" and \"-\"")]
    [InlineData("""{"op": "upsert", "kind": "item", "name": "x", "aliases": [null]}""", "aliases item 1 is null")]
    [InlineData("""{"op": "upsert", "kind": "item", "name": "x", "data": {"": 1}}""", "data has a blank key")]
    [InlineData("""{"op": "fact", "statement": "s", "known_by": []}""", "known_by is empty")]
    [InlineData("""{"op": "fact", "statement": "s", "known_by": [{"who": "party"}, {"who": "Party"}]}""", "known_by item 2 (party): party is listed twice; one row per knower.")]
    [InlineData("""{"op": "fact", "statement": "s", "known_by": [{"who": "everyone"}]}""", "known_by item 1: who \"everyone\" is not a knower")]
    [InlineData("""{"op": "fact", "statement": "s", "known_by": [{"who": "party", "state": "remembers"}]}""", "known_by item 1 (party): state \"remembers\" is not a knowledge state")]
    [InlineData("""{"op": "fact", "statement": "s", "about": ["a", "a"]}""", "about item 2: a is listed twice.")]
    [InlineData("""{"op": "fact", "statement": "s", "links": []}""", "links is empty")]
    [InlineData("""{"op": "fact", "statement": "s", "links": [{"role": "about"}]}""", "links item 1: ref is required")]
    public void Validate_BadOp_IsRefusedWithWhereWhatAndWhatIsAccepted(string json, string expected)
    {
        var problems = CampaignOpValidation.Problems([json == "null" ? null : Bind(json)]);

        Assert.Contains(problems, p => p.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_NonFiniteSortKey_IsRefused()
    {
        var spec = new CampaignOpSpec { Op = "upsert", Kind = "note", Name = "x", SortKey = double.PositiveInfinity };

        Assert.Contains("ops item 1 (upsert note \"x\"): sort_key must be a finite number, e.g. 1.5.", CampaignOpValidation.Problems([spec]));
    }

    [Fact]
    public void Validate_DataValueUndefined_IsRefused()
    {
        var spec = new CampaignOpSpec { Op = "upsert", Kind = "note", Name = "x", Data = new() { ["k"] = default } };

        Assert.Contains(CampaignOpValidation.Problems([spec]), p => p.Contains("data \"k\" has no value", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_DataOverTwentyThousandCharacters_IsRefused()
    {
        var spec = Bind($$"""{"op": "upsert", "kind": "note", "name": "x", "data": {"notes": "{{new string('a', 20_000)}}"} }""");

        Assert.Contains(CampaignOpValidation.Problems([spec]), p => p.Contains("characters as JSON; at most 20000", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("summary", 1_001, "summary is 1001 characters; at most 1000.")]
    [InlineData("body_md", 50_001, "body_md is 50001 characters; at most 50000.")]
    [InlineData("secret_md", 50_001, "secret_md is 50001 characters; at most 50000.")]
    public void Validate_TextOverItsLimit_IsRefused(string field, int length, string expected)
    {
        var spec = Bind($$"""{"op": "upsert", "kind": "note", "name": "x", "{{field}}": "{{new string('a', length)}}"}""");

        Assert.Contains(CampaignOpValidation.Problems([spec]), p => p.EndsWith(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_StatementOverItsLimit_IsRefusedWithoutEchoingIt()
    {
        var statement = "SECRET " + new string('a', 4_000);
        var spec = new CampaignOpSpec { Op = "fact", Statement = statement };

        var ex = Assert.Throws<DndInputException>(() => CampaignOpValidation.Validate([spec]));

        Assert.Equal("Invalid ops: ops item 1 (fact): statement is 4007 characters; at most 4000.", ex.Message);
    }

    [Fact]
    public void Validate_TooManyItemsInLists_AreRefused()
    {
        var many = string.Join(", ", Enumerable.Range(1, 21).Select(i => $"\"t{i}\""));
        var knowers = string.Join(", ", Enumerable.Range(1, 31).Select(i => $"{{\"who\": \"character:c{i}\"}}"));
        var spec = Bind($$"""{"op": "upsert", "kind": "note", "name": "x", "tags": [{{many}}], "known_by": [{{knowers}}]}""");

        var problems = CampaignOpValidation.Problems([spec]);

        Assert.Contains(problems, p => p.EndsWith("tags has 21 items; at most 20.", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.EndsWith("known_by has 31 knowers; at most 30.", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_EmptyOrMissingBatch_IsRefused()
    {
        Assert.Equal("Invalid ops: ops is required: at least one op, e.g. [{\"op\": \"upsert\", \"kind\": \"character\", \"name\": \"Iron Guts\"}].",
            Assert.Throws<DndInputException>(() => CampaignOpValidation.Validate([])).Message);
        Assert.Throws<DndInputException>(() => CampaignOpValidation.Validate(null));
    }

    [Fact]
    public void Validate_FiftyOneOps_IsTooManyForOneBatch()
    {
        var ops = Enumerable.Range(0, 51).Select(_ => new CampaignOpSpec { Op = "tick", Ref = "clock:x" }).ToList<CampaignOpSpec?>();

        Assert.Contains(CampaignOpValidation.Problems(ops), p => p.StartsWith("ops has 51 items; at most 50 per call", StringComparison.Ordinal));
        Assert.Empty(CampaignOpValidation.Problems(ops.Take(50).ToList()));
    }

    [Fact]
    public void Validate_ManyProblems_ListsFiveThenCountsTheRest()
    {
        var ops = Enumerable.Range(0, 7).Select(_ => (CampaignOpSpec?)new CampaignOpSpec { Op = "delete" }).ToList();

        var ex = Assert.Throws<DndInputException>(() => CampaignOpValidation.Validate(ops));

        Assert.StartsWith("Invalid ops (7 problems):\n- ops item 1 (delete): ref is required", ex.Message, StringComparison.Ordinal);
        Assert.Contains("- ops item 5 (delete)", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ops item 6", ex.Message, StringComparison.Ordinal);
        Assert.EndsWith("- … and 2 more; fix these and send it again to see them.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ItemLabels_NameTheOpByRefOrKindAndName()
    {
        CampaignOpSpec?[] ops =
        [
            new CampaignOpSpec { Op = "upsert", Ref = "character:iron-guts", Statement = "x" },
            new CampaignOpSpec { Op = "upsert", Kind = "Character", Name = "Iron Guts", Statement = "x" },
            new CampaignOpSpec { Op = "link", From = "character:belmakor", Rel = "member_of", To = "faction:the-party", Statement = "x" },
            new CampaignOpSpec { Op = "fact", Statement = "The secret statement", Name = "x" },
        ];

        var problems = CampaignOpValidation.Problems(ops);

        Assert.StartsWith("ops item 1 (upsert character:iron-guts): does not take \"statement\"", problems[0], StringComparison.Ordinal);
        Assert.StartsWith("ops item 2 (upsert character \"Iron Guts\"): does not take \"statement\"", problems[1], StringComparison.Ordinal);
        Assert.StartsWith("ops item 3 (link character:belmakor member_of faction:the-party): does not take \"statement\"", problems[2], StringComparison.Ordinal);
        Assert.StartsWith("ops item 4 (fact): does not take \"name\"", problems[3], StringComparison.Ordinal);
        Assert.DoesNotContain(problems, p => p.Contains("secret statement", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RepeatedAuthorAlias_IsNamedByPositionNeverQuoted()
    {
        var spec = Bind("""{"op": "upsert", "kind": "item", "name": "x", "aliases": [{"alias": "Axiom Cage", "visibility": "author"}, {"alias": "axiom cage", "visibility": "author"}], "remove_aliases": ["AXIOM CAGE"]}""");

        var problems = CampaignOpValidation.Problems([spec]);

        Assert.Equal(
            [
                "ops item 1 (upsert item \"x\"): aliases item 2 repeats item 1.",
                "ops item 1 (upsert item \"x\"): remove_aliases item 1 is also aliases item 1; add it or remove it, not both.",
            ],
            problems);
        Assert.DoesNotContain(problems, p => p.Contains("axiom", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Ops whose kind-dependent checks the batch pass cannot make (an e:&lt;n&gt; or code ref), each with the kinds the write
    /// path resolved and the problem those kinds reveal. Each op passes the batch check.
    /// </summary>
    [Theory]
    [InlineData("""{"op": "upsert", "ref": "e:3", "clock": {"segments": 4}}""", "character", null, null, "ops item 2 (upsert e:3): clock is only for kind clock, not character.")]
    [InlineData("""{"op": "upsert", "ref": "e:3", "subtype": "npc"}""", "item", null, null, "ops item 2 (upsert e:3): subtype \"npc\" is not an item subtype")]
    [InlineData("""{"op": "upsert", "ref": "e:3", "status": "dead"}""", "quest", null, null, "ops item 2 (upsert e:3): status \"dead\" is not a quest status")]
    [InlineData("""{"op": "upsert", "ref": "e:8", "summary": "s"}""", "session", null, null, "ops item 2 (upsert e:8): sessions are written with campaign_session")]
    [InlineData("""{"op": "upsert", "ref": "e:3", "kind": "item"}""", "character", null, null, "ops item 2 (upsert e:3): kind item differs from the ref's kind character; an entity's kind never changes.")]
    [InlineData("""{"op": "status", "ref": "e:4", "status": "done"}""", "beat", null, null, "ops item 2 (status e:4): status \"done\" is not a beat status; give pending, met or cut.")]
    [InlineData("""{"op": "tick", "ref": "e:5"}""", "quest", null, null, "ops item 2 (tick e:5): only a clock is ticked, not kind quest.")]
    [InlineData("""{"op": "objective", "ref": "e:5", "text": "t"}""", "character", null, null, "ops item 2 (objective e:5): objectives belong to a quest or a thread, not to kind character.")]
    [InlineData("""{"op": "answer", "ref": "C12", "answer_md": "a"}""", "quest", null, null, "ops item 2 (answer C12): only a question is answered, not kind quest.")]
    [InlineData("""{"op": "link", "from": "e:1", "rel": "leads_to", "to": "e:2", "label": "l"}""", null, "beat", "beat",
        "ops item 2 (link e:1 leads_to e:2): does not take \"label\" here: rel leads_to between two beats is a beat edge, which keeps only mode.")]
    [InlineData("""{"op": "link", "from": "e:1", "rel": "leads_to", "to": "e:2"}""", null, "beat", "beat", "ops item 2 (link e:1 leads_to e:2): mode is required with rel leads_to between two beats")]
    [InlineData("""{"op": "link", "from": "e:1", "rel": "leads_to", "to": "beat:b", "mode": "any_of"}""", null, "character", "beat",
        "ops item 2 (link e:1 leads_to beat:b): mode is only for leads_to between two beats (a beat's prerequisites), not with a character")]
    public void ResolvedProblems_KindOnlyTheWritePathKnows_IsRefusedWithTheBatchChecksMessage(string json, string? refKind, string? fromKind, string? toKind,
        string expected)
    {
        var spec = Bind(json);

        Assert.Empty(CampaignOpValidation.Problems([spec]));
        Assert.Contains(CampaignOpValidation.ResolvedProblems(1, spec, new ResolvedKinds(refKind, fromKind, toKind)),
            p => p.StartsWith(expected, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"op": "upsert", "ref": "e:3", "clock": {"segments": 4}, "status": "running"}""", "clock", null, null)]
    [InlineData("""{"op": "upsert", "ref": "e:3", "subtype": "npc", "status": "dead"}""", "character", null, null)]
    [InlineData("""{"op": "tick", "ref": "e:5"}""", "clock", null, null)]
    [InlineData("""{"op": "objective", "ref": "e:9", "text": "t"}""", "thread", null, null)]
    [InlineData("""{"op": "answer", "ref": "Q7", "answer_md": "a"}""", "question", null, null)]
    [InlineData("""{"op": "link", "from": "e:1", "rel": "leads_to", "to": "e:2", "mode": "all_of"}""", null, "beat", "beat")]
    [InlineData("""{"op": "link", "from": "e:1", "rel": "leads_to", "to": "e:2", "label": "a ladder"}""", null, "location", "location")]
    [InlineData("""{"op": "delete", "ref": "e:1"}""", "character", null, null)]
    public void ResolvedProblems_KindsThatFit_HaveNoProblems(string json, string? refKind, string? fromKind, string? toKind)
    {
        Assert.Empty(CampaignOpValidation.ResolvedProblems(0, Bind(json), new ResolvedKinds(refKind, fromKind, toKind)));
    }

    [Fact]
    public void ResolvedProblems_StaticProblems_AreStillReported()
    {
        var problems = CampaignOpValidation.ResolvedProblems(0, new CampaignOpSpec { Op = "tick", Ref = "e:5", Amount = 0 }, new ResolvedKinds("clock"));

        Assert.Equal(["ops item 1 (tick e:5): amount 0 changes nothing; give a whole number such as 1, or -1 to untick."], problems);
    }

    [Theory]
    [InlineData("member_of", "member_of")]
    [InlineData("Member Of", "member_of")]
    [InlineData("memberOf", "member_of")]
    [InlineData("MEMBER-OF", "member_of")]
    [InlineData("  ally  of ", "ally_of")]
    [InlineData("leads_to", "leads_to")]
    [InlineData("rival2", "rival2")]
    [InlineData("ally of!", null)]
    [InlineData("2nd_cousin", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("a_very_long_relation_name_that_goes_on_forever", null)]
    public void NormalizeRel_SpellingsOfOneRelation_AreOneSnakeCaseName(string? rel, string? normalized)
    {
        Assert.Equal(normalized, CampaignOpValidation.NormalizeRel(rel));
    }
}
