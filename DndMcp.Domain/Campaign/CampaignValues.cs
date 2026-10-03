using DndMcp.Domain.Features;

namespace DndMcp.Domain.Campaign;

/// <summary>
/// Every closed vocabulary of campaign tracking: the wire values the tools accept, the values campaigns.db stores, and the
/// per-kind subtype and status lists.
///
/// <para>
/// <b>These strings are stored data.</b> campaigns.db's CHECK constraints (<c>Campaign/Migrations/0001_init.sql</c>) list
/// the same values, and rows written today are read by every later version. Renaming a value here without a migration that
/// rewrites the rows (and the CHECK) leaves old rows the code no longer recognises, which is why these are string constants
/// and never CLR enums (stored data outlives enum renumbering). <c>CampaignValuesTests</c> pins that each set here equals
/// the CHECK list in the migration, so the two cannot drift.
/// </para>
/// <para>
/// Every set is a <see cref="DslValueSet"/>, so a model's spelling is matched forgivingly (case, spaces, hyphens and
/// underscores are ignored: "All Of", "allOf" and "all-of" are <c>all_of</c>) and what is stored is always the canonical
/// value. A refusal over a separator costs a round trip that teaches the model nothing.
/// </para>
/// </summary>
public static class CampaignValues
{
    /// <summary>Whose campaign it is: one Cole plays in (another person DMs), or one he runs.</summary>
    public static class Roles
    {
        public const string Player = "player";
        public const string Dm = "dm";

        public static readonly DslValueSet Set = new("role", [Player, Dm]);
    }

    /// <summary>
    /// The rules a campaign plays by. <c>mixed</c> gives no edition default: tools that default to the campaign's ruleset
    /// fall back to 2024 for it, as they do with no campaign.
    /// </summary>
    public static class Rulesets
    {
        public const string R2014 = "2014";
        public const string R2024 = "2024";
        public const string Mixed = "mixed";

        public static readonly DslValueSet Set = new("ruleset", [R2014, R2024, Mixed]);
    }

    public static class CampaignStatuses
    {
        public const string Active = "active";
        public const string Hiatus = "hiatus";
        public const string Ended = "ended";

        public static readonly DslValueSet Set = new("campaign status", [Active, Hiatus, Ended]);
    }

    /// <summary>
    /// Entity kinds. One polymorphic entity table holds everything that is graphed, tagged, searched or known about (PLAN §6
    /// principle 1); typed side tables exist only where there are mechanics (session, clock, objective, beat_edge).
    /// </summary>
    public static class Kinds
    {
        public const string Character = "character";
        public const string Location = "location";
        public const string Faction = "faction";
        public const string Item = "item";
        public const string Lore = "lore";
        public const string Rule = "rule";
        public const string Homebrew = "homebrew";
        public const string Quest = "quest";
        public const string Thread = "thread";
        public const string Question = "question";
        public const string Secret = "secret";
        public const string Arc = "arc";
        public const string Beat = "beat";
        public const string Scene = "scene";
        public const string Session = "session";
        public const string Event = "event";
        public const string Handout = "handout";
        public const string Work = "work";
        public const string Clock = "clock";
        public const string Front = "front";
        public const string Note = "note";

        public static readonly DslValueSet Set = new("kind",
        [
            Character, Location, Faction, Item, Lore, Rule, Homebrew, Quest, Thread, Question, Secret, Arc, Beat, Scene,
            Session, Event, Handout, Work, Clock, Front, Note,
        ]);
    }

    /// <summary>
    /// Who may see a row by default. <c>restricted</c> means "consult the knowledge rows"; <c>author</c> is Cole alone (the
    /// DM in a DM campaign, the player out of character in a player campaign). The strictest of a row and its endpoints
    /// wins. Relations and objectives take only <see cref="RowSet"/>: they have no knowledge rows, so restricted could
    /// never be resolved for them.
    /// </summary>
    public static class Visibilities
    {
        public const string Public = "public";
        public const string Party = "party";
        public const string Restricted = "restricted";
        public const string Author = "author";

        public static readonly DslValueSet Set = new("visibility", [Public, Party, Restricted, Author]);

        /// <summary>Relations and objectives: no <c>restricted</c>.</summary>
        public static readonly DslValueSet RowSet = new("visibility", [Public, Party, Author]);
    }

    /// <summary>
    /// Where a claim stands. <c>proposed</c> is an invention awaiting accept or strike (it is auto-numbered F&lt;n&gt;);
    /// <c>lean</c> is the DM's direction, not locked; <c>superseded</c> is set when a later fact replaces this one.
    /// </summary>
    public static class CanonStatuses
    {
        public const string Canon = "canon";
        public const string Played = "played";
        public const string Ruled = "ruled";
        public const string Lean = "lean";
        public const string Planned = "planned";
        public const string Proposed = "proposed";
        public const string Accepted = "accepted";
        public const string Struck = "struck";
        public const string Superseded = "superseded";

        public static readonly DslValueSet Set = new("canon status",
            [Canon, Played, Ruled, Lean, Planned, Proposed, Accepted, Struck, Superseded]);
    }

    /// <summary>How sure the record is, apart from what it claims ("the middle of the sequence is reconstructed").</summary>
    public static class Confidences
    {
        public const string Confirmed = "confirmed";
        public const string Approximate = "approximate";
        public const string Reconstructed = "reconstructed";
        public const string Unverified = "unverified";

        public static readonly DslValueSet Set = new("confidence", [Confirmed, Approximate, Reconstructed, Unverified]);
    }

    public static class FactTypes
    {
        public const string Canon = "canon";
        public const string Ruling = "ruling";
        public const string Secret = "secret";
        public const string Rumor = "rumor";
        public const string Belief = "belief";
        public const string Clue = "clue";
        public const string Theory = "theory";
        public const string Meta = "meta";

        public static readonly DslValueSet Set = new("fact type", [Canon, Ruling, Secret, Rumor, Belief, Clue, Theory, Meta]);
    }

    /// <summary>Whether a claim is true in the world (a rumour can be false; a fragment's memory partial).</summary>
    public static class Truths
    {
        public const string True = "true";
        public const string False = "false";
        public const string Partial = "partial";
        public const string Unknown = "unknown";

        public static readonly DslValueSet Set = new("truth", [True, False, Partial, Unknown]);
    }

    /// <summary>
    /// Who a knowledge row is about. <c>party</c> is the party in character (expanded to its members through membership and
    /// attendance); <c>table</c> the players out of character; <c>author</c> Cole; <c>dm</c> the other human DM of a player
    /// campaign; <c>public</c> in-world common belief. Only <c>character</c> rows name an entity.
    /// </summary>
    public static class KnowerKinds
    {
        public const string Character = "character";
        public const string Party = "party";
        public const string Table = "table";
        public const string Author = "author";
        public const string Dm = "dm";
        public const string Public = "public";

        public static readonly DslValueSet Set = new("knower", [Character, Party, Table, Author, Dm, Public]);
    }

    /// <summary>
    /// What a knower's relation to a fact or entity is. <see cref="AwareStates"/> are the states in which the knower knows
    /// of it (a fact they hold, an entity they have met or heard of, possibly under another name); <c>unaware</c> and
    /// <c>forgot</c> are explicit "does not know", which beats a party row (an explicit character row always wins).
    /// </summary>
    public static class KnowledgeStates
    {
        public const string Knows = "knows";
        public const string Suspects = "suspects";
        public const string Believes = "believes";
        public const string Misbelieves = "misbelieves";
        public const string Heard = "heard";
        public const string Met = "met";
        public const string Aware = "aware";
        public const string Unrecognized = "unrecognized";
        public const string Unaware = "unaware";
        public const string Forgot = "forgot";

        public static readonly DslValueSet Set = new("knowledge state",
            [Knows, Suspects, Believes, Misbelieves, Heard, Met, Aware, Unrecognized, Unaware, Forgot]);

        /// <summary>The states that mean "knows of it" (everything but unaware and forgot).</summary>
        public static readonly IReadOnlySet<string> AwareStates = new HashSet<string>(StringComparer.Ordinal)
        {
            Knows, Suspects, Believes, Misbelieves, Heard, Met, Aware, Unrecognized,
        };
    }

    public static class RelationStatuses
    {
        public const string Current = "current";
        public const string Former = "former";
        public const string Planned = "planned";
        public const string Rumored = "rumored";

        public static readonly DslValueSet Set = new("relation status", [Current, Former, Planned, Rumored]);
    }

    /// <summary>
    /// A session's life: <c>planned</c> → <c>prepped</c> (a run-sheet exists) → <c>live</c> (at the table; at most one per
    /// campaign) → <c>played</c>; or <c>cancelled</c>. <c>record_past</c> writes a played session directly.
    /// </summary>
    public static class SessionStatuses
    {
        public const string Planned = "planned";
        public const string Prepped = "prepped";
        public const string Live = "live";
        public const string Played = "played";
        public const string Cancelled = "cancelled";

        public static readonly DslValueSet Set = new("session status", [Planned, Prepped, Live, Played, Cancelled]);
    }

    /// <summary>How precise a real-world play date is ("late August 2026" is <c>approx</c>).</summary>
    public static class DatePrecisions
    {
        public const string Day = "day";
        public const string Month = "month";
        public const string Approx = "approx";
        public const string Unknown = "unknown";

        public static readonly DslValueSet Set = new("date precision", [Day, Month, Approx, Unknown]);
    }

    public static class ObjectiveStatuses
    {
        public const string Open = "open";
        public const string Done = "done";
        public const string Failed = "failed";
        public const string Skipped = "skipped";
        public const string Hidden = "hidden";

        public static readonly DslValueSet Set = new("objective status", [Open, Done, Failed, Skipped, Hidden]);
    }

    public static class ClockUnits
    {
        public const string Segment = "segment";
        public const string Round = "round";
        public const string Hour = "hour";
        public const string Day = "day";
        public const string Session = "session";
        public const string Week = "week";

        public static readonly DslValueSet Set = new("clock unit", [Segment, Round, Hour, Day, Session, Week]);
    }

    /// <summary>
    /// How a beat edge counts toward its target: every <c>all_of</c> prerequisite must be met, and at least one
    /// <c>any_of</c> prerequisite when there are any (the PWA's Story Web semantics).
    /// </summary>
    public static class BeatEdgeModes
    {
        public const string AllOf = "all_of";
        public const string AnyOf = "any_of";

        public static readonly DslValueSet Set = new("beat edge mode", [AllOf, AnyOf]);
    }

    /// <summary>
    /// How a fact is tied to an entity. <c>about</c> is what it is about (a secret's gated reveal fact is <c>about</c> the
    /// secret entity); <c>clue_for</c> marks a clue toward a secret.
    /// </summary>
    public static class FactLinkRoles
    {
        public const string About = "about";
        public const string Source = "source";
        public const string Location = "location";
        public const string ClueFor = "clue_for";
        public const string Evidence = "evidence";
        public const string Contradicts = "contradicts";

        public static readonly DslValueSet Set = new("fact link role", [About, Source, Location, ClueFor, Evidence, Contradicts]);
    }

    /// <summary>
    /// The relation names the tools give meaning to. Any other snake_case name is accepted and stored as given (relations
    /// are open-ended: "trained", "rival_of"). <see cref="SameAs"/> between entities of two campaigns is stored as a
    /// cross_link, never as a relation; <see cref="LeadsTo"/> between two beats is stored as a beat_edge.
    /// </summary>
    public static class Rels
    {
        public const string MemberOf = "member_of";
        public const string SameAs = "same_as";
        public const string LeadsTo = "leads_to";
        public const string AnsweredBy = "answered_by";
        public const string LocatedIn = "located_in";

        /// <summary>Named in descriptions as examples; not a closed set.</summary>
        public static readonly IReadOnlyList<string> Suggested =
        [
            MemberOf, "leads", "serves", "ally_of", "enemy_of", "family_of", LocatedIn, "owns", "created", "performed_by",
            "about", "calls_back", "involves", "takes_place_at", "advances", "appears_in", "gives_quest", AnsweredBy,
            "fragment_of",
        ];
    }

    /// <summary>
    /// The perspectives every read takes. <c>character:&lt;handle&gt;</c> names a character entity. In a DM campaign
    /// <c>dm</c> is the author (Cole is the DM).
    /// </summary>
    public static class PerspectiveKinds
    {
        public const string Author = "author";
        public const string Dm = "dm";
        public const string Table = "table";
        public const string Party = "party";
        public const string Public = "public";
        public const string Character = "character";

        /// <summary>The prefix of a character perspective: <c>character:belmakor</c>.</summary>
        public const string CharacterPrefix = "character:";

        public static readonly DslValueSet Set = new("perspective", [Author, Dm, Table, Party, Public, Character]);

        /// <summary>For messages: every form a perspective may take.</summary>
        public const string List = "\"author\" (default), \"dm\", \"table\", \"party\", \"public\" or \"character:<slug>\"";
    }

    /// <summary>The operations one <c>campaign_write</c> batch may hold.</summary>
    public static class OpKinds
    {
        public const string Upsert = "upsert";
        public const string Delete = "delete";
        public const string Restore = "restore";
        public const string Link = "link";
        public const string Unlink = "unlink";
        public const string Fact = "fact";
        public const string Status = "status";
        public const string Objective = "objective";
        public const string Tick = "tick";
        public const string Answer = "answer";

        public static readonly DslValueSet Set = new("op", [Upsert, Delete, Restore, Link, Unlink, Fact, Status, Objective, Tick, Answer]);
    }

    /// <summary>
    /// change_log's mechanical operation. Undo and point-in-time replay need nothing else: a create is reversed by deleting
    /// the row, an update by writing the old value back, a delete by re-inserting the logged row. The semantic label
    /// ("reveal", "tick", "upsert") lives in change_log.action.
    /// </summary>
    public static class ChangeOps
    {
        public const string Create = "create";
        public const string Update = "update";
        public const string Delete = "delete";
    }

    /// <summary>Who made a change: the model through a tool, or the user through the CLI.</summary>
    public static class Actors
    {
        public const string Claude = "claude";
        public const string Cli = "cli";
    }

    /// <summary>
    /// The subtypes each kind takes. A kind absent from this table takes no subtype (it must be null). Closed per kind so
    /// that "every NPC" and "every song" are queries rather than guesses over free text.
    /// </summary>
    public static class Subtypes
    {
        public static readonly IReadOnlyDictionary<string, DslValueSet> ByKind = new Dictionary<string, DslValueSet>(StringComparer.Ordinal)
        {
            [Kinds.Character] = new("character subtype", ["pc", "npc", "creature", "deity", "companion"]),
            [Kinds.Location] = new("location subtype", ["region", "settlement", "site", "landmark", "plane", "ship", "building"]),
            [Kinds.Faction] = new("faction subtype", ["party", "band", "organization", "faith", "military", "company", "family"]),
            [Kinds.Item] = new("item subtype", ["magic", "artifact", "heirloom", "mundane", "devil_fruit", "homebrew"]),
            [Kinds.Lore] = new("lore subtype", ["canon", "history", "cosmology", "religion", "mechanic"]),
            [Kinds.Rule] = new("rule subtype", ["house_rule", "balance_standard", "reveal_rule", "table_rule"]),
            [Kinds.Homebrew] = new("homebrew subtype",
                ["spell", "feat", "subclass", "class", "species", "background", "item", "devil_fruit", "monster"]),
            [Kinds.Question] = new("question subtype", ["design", "gap", "build"]),
            [Kinds.Scene] = new("scene subtype", ["scene", "encounter", "reveal", "travel", "downtime", "handout", "untyped"]),
            [Kinds.Work] = new("work subtype", ["song", "poem", "chapter", "journal", "letter", "speech"]),
            [Kinds.Note] = new("note subtype", ["journal", "theory", "preference", "standing", "todo"]),
        };

        /// <summary>The PC subtype: party membership and the party-row expansion are about these.</summary>
        public const string Pc = "pc";

        /// <summary>The party faction's subtype; campaign.party_id points at one.</summary>
        public const string PartyFaction = "party";

        /// <summary>A rule entity of this subtype carries forbidden terms and patterns in its data.</summary>
        public const string RevealRule = "reveal_rule";
    }

    /// <summary>
    /// The statuses each kind takes, and the status a new entity of that kind starts in when none is given (null: none).
    /// A kind absent from <see cref="ByKind"/> has no status (sessions keep theirs in the session table). Closed per kind
    /// because summaries and searches filter on them ("open threads", "withheld questions").
    /// </summary>
    public static class Statuses
    {
        public static readonly IReadOnlyDictionary<string, DslValueSet> ByKind = new Dictionary<string, DslValueSet>(StringComparer.Ordinal)
        {
            [Kinds.Character] = new("character status", ["alive", "dead", "missing", "unknown", "transformed", "departed"]),
            [Kinds.Location] = new("location status", ["unvisited", "visited", "current", "destroyed"]),
            [Kinds.Faction] = new("faction status", ["active", "dormant", "disbanded", "destroyed"]),
            [Kinds.Item] = new("item status", ["held", "sought", "lost", "destroyed", "unknown"]),
            [Kinds.Rule] = new("rule status", ["active", "retired"]),
            [Kinds.Homebrew] = new("homebrew status", ["draft", "pitched", "near_final", "approved", "needs_nerf", "rejected"]),
            [Kinds.Quest] = new("quest status", ["open", "active", "blocked", "resolved", "failed", "abandoned", "dormant"]),
            [Kinds.Thread] = new("thread status", ["open", "active", "blocked", "resolved", "failed", "abandoned", "dormant"]),
            [Kinds.Question] = new("question status", ["open", "lean", "withheld", "answered", "dropped"]),
            [Kinds.Secret] = new("secret status", ["hidden", "seeded", "partial", "revealed"]),
            [Kinds.Arc] = new("arc status", ["planned", "current", "done", "abandoned"]),
            [Kinds.Beat] = new("beat status", ["pending", "met", "cut"]),
            [Kinds.Scene] = new("scene status", ["planned", "run", "cut"]),
            [Kinds.Event] = new("event status", ["planned", "happened", "averted"]),
            [Kinds.Handout] = new("handout status", ["draft", "ready", "delivered"]),
            [Kinds.Work] = new("work status", ["idea", "requested", "drafting", "written", "performed", "retired"]),
            [Kinds.Clock] = new("clock status", ["running", "paused", "done"]),
            [Kinds.Front] = new("front status", ["active", "dormant", "resolved"]),
            [Kinds.Note] = new("note status", ["open", "done"]),
        };

        /// <summary>The status a new entity starts in when the op gives none; kinds not listed start with none.</summary>
        public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Kinds.Quest] = "open",
            [Kinds.Thread] = "open",
            [Kinds.Question] = "open",
            [Kinds.Secret] = "hidden",
            [Kinds.Arc] = "planned",
            [Kinds.Beat] = "pending",
            [Kinds.Scene] = "planned",
            [Kinds.Homebrew] = "draft",
            [Kinds.Work] = "idea",
            [Kinds.Clock] = "running",
            [Kinds.Front] = "active",
        };

        /// <summary>A beat that has happened: reachability counts it as met.</summary>
        public const string BeatMet = "met";

        /// <summary>A beat taken out of the story: its edges neither satisfy nor block, and it is never reachable.</summary>
        public const string BeatCut = "cut";

        /// <summary>A question the DM has answered but must not surface in play; rendered as "open" to non-author perspectives.</summary>
        public const string QuestionWithheld = "withheld";

        public const string QuestionOpen = "open";
        public const string QuestionAnswered = "answered";

        public const string SecretHidden = "hidden";
        public const string SecretSeeded = "seeded";
        public const string SecretPartial = "partial";
        public const string SecretRevealed = "revealed";

        public const string ClockDone = "done";

        /// <summary>A clock still counting: a tick below its last segment puts a done clock back here.</summary>
        public const string ClockRunning = "running";

        /// <summary>A beat not yet met: what reachability assumes of a beat stored with no status.</summary>
        public const string BeatPending = "pending";

        /// <summary>A living character: the player character a player campaign is created with starts here.</summary>
        public const string CharacterAlive = "alive";

        /// <summary>A dead character: its membership and knowledge are kept, and the write path says to review them.</summary>
        public const string CharacterDead = "dead";

        /// <summary>A handout the table has received: revealing a handout's facts marks it so.</summary>
        public const string HandoutDelivered = "delivered";

        /// <summary>An active faction: the party faction every campaign is created with starts here.</summary>
        public const string FactionActive = "active";
    }
}
