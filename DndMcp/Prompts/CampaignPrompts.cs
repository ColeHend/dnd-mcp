using System.ComponentModel;
using System.Globalization;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Formatting.Campaign;
using DndMcp.Hosting;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Read;
using ModelContextProtocol.Server;

namespace DndMcp.Prompts;

/// <summary>
/// The campaign prompts: <c>session_recap</c>, <c>session_prep</c>, <c>knowledge_check</c>, <c>continuity_check</c>,
/// <c>in_character</c> and <c>homebrew_review</c> (in Claude Code, commands such as <c>/mcp__dnd__session_recap</c> for a
/// server registered as "dnd"). Each returns one user-turn message of instructions that drive the tools in order; none
/// embeds campaign content.
///
/// <para>
/// <b>Why instructions, never data.</b> Anything pasted into a prompt skips the perspective filter and the output caps: a
/// prompt that inlined "what Belmakor knows" from the author's view would put "the Axiom Cage" in front of the model that is
/// about to write his lyric. So a prompt names the campaign and character by handle and tells the model which tool (with
/// which perspective) to read them through, and the tools do the filtering. The only database access here is checking that
/// the campaign and the character exist, so a typo fails at once with the campaigns (or characters) to choose from,
/// instead of after the model has started working.
/// </para>
/// <para>
/// <b>Arguments are single tokens, in the order a user types them.</b> Claude Code splits <c>/mcp__dnd__knowledge_check
/// belmakor</c> on whitespace and maps tokens to arguments by position, dropping extras; so every argument is one slug or
/// number, free text (the draft, the recap notes) comes from the conversation, and the argument used most comes first. The
/// only required ones are the character of knowledge_check and in_character, which Claude Code then checks before calling.
/// A session number typed where the campaign goes (<c>/mcp__dnd__session_recap 12</c>, with the campaign chosen) is read as
/// the session when no campaign has that slug. Values arrive as strings, so the session is a <c>string?</c> parsed here:
/// an <c>int?</c> would fail on "twelve" inside the SDK with the bare "An error occurred.".
/// </para>
/// <para>
/// <b>Every campaign call names the campaign.</b> A prompt resolves the campaign the user named but does not make it the
/// current one (fetching a prompt must not change state), so a tool call without <c>campaign</c> would go to the active
/// campaign: <c>/mcp__dnd__session_recap one-piece</c> while belmakor is active would write the recap into belmakor. Every
/// tool-call template therefore carries <c>"campaign": "&lt;slug&gt;"</c>, and each prompt says so once for the calls it
/// names in prose.
/// </para>
/// <para>
/// <b>A default session is resolved here, not left to the tool.</b> <c>session_prep</c> with no session prepares the next
/// session to play (the lowest planned or prepped one after the last played, else a new one: the rule <c>start</c> uses)
/// and passes its number to <c>plan</c>, whose own default is always a new session: left out, revising next week's prep
/// would create the week after instead. <c>session_recap</c> cannot know whether "the session just played" was already
/// ended, so it tells the model to decide from what <c>get</c> shows.
/// </para>
/// <para>
/// <b>Errors</b> are <see cref="DndInputException"/>s (no campaign, several and none chosen, an unknown campaign or
/// character, a session that is not a number or is out of range); the get-prompt filter turns them into a JSON-RPC error carrying the
/// message.
/// </para>
/// <para>
/// <b>Tools are named bare</b> (<c>campaign_write</c>, never <c>mcp__dnd__campaign_write</c>), and the usage lines name the
/// prompt without a server. The server cannot know the name the client registered it under: a dev build registered as
/// "dnd-dev" beside the installed "dnd" serves these same prompts, and a prompt naming <c>mcp__dnd__</c> tools would steer
/// the model from the dev server's prompt to the installed server's tools, writing into the user's real campaigns.db.
/// Each prompt says once that its tools are the ones of the server it came from (<see cref="ThisServersTools"/>), and
/// Claude Code's tool search finds a tool by its bare name.
/// </para>
/// </summary>
public sealed class CampaignPrompts
{
    private readonly CampaignService _campaigns;

    public CampaignPrompts(CampaignService campaigns)
    {
        _campaigns = campaigns;
    }

    [McpServerPrompt(Name = "session_recap", Title = "Recap a session")]
    [Description(
        "Record a played session from your account of it in the conversation: react, connect it to open threads, then write it " +
        "back as dry runs to approve (the session, and one campaign_write batch of facts, knowers and progress). " +
        "Usage: session_recap [campaign] [session].")]
    public string SessionRecap(
        [Description("The campaign's slug, one word, e.g. belmakor. Default: the active campaign.")] string? campaign = null,
        [Description("The session number, one word, e.g. 12. Default: the live session, else the next to record.")] string? session = null)
    {
        var (row, number) = CampaignAndSession(campaign, session);
        var slug = row.Slug;
        var which = number is null ? "the session just played (the live one, if one is live)" : $"session {number}";
        var recorded = number is null ? "a session" : $"session {number}";
        var batchSession = number ?? "<the number record_past reported>";
        var pastSession = number is null
            ? "\n   - With no session number from me: if step 1 shows the session my account describes already recorded (played), " +
              "give record_past its \"session\" number, which corrects it; leave session out only for a session not recorded yet " +
              "(record_past then takes the next one to play)."
            : string.Empty;
        return
            $"Recap {which} of campaign `{slug}` from my account of it in this conversation. My account is the source of truth; " +
            "if there is none yet, ask me for it and stop.\n" +
            CampaignOnEveryCall(slug) + "\n\n" +
            $"1. Read what is recorded: campaign_session {{\"action\": \"get\"{SessionArg(number)}, \"campaign\": \"{slug}\"}} " +
            $"({{\"action\": \"list\", \"campaign\": \"{slug}\"}} if unsure which session), and campaign_search / " +
            "campaign_get for every person, place and thing my account names, so you use their existing handles instead " +
            "of creating duplicates.\n" +
            "2. React briefly, then connect what happened to open threads, quests and questions: campaign_search " +
            $"{{\"kinds\": [\"thread\", \"quest\", \"question\"], \"campaign\": \"{slug}\"}}.\n" +
            "3. Draft ONE campaign_write batch for what the session changed: fact ops for what was learned (statement, " +
            "about, known_by naming who learned it, source \"session N recap\"), status, objective, tick and answer ops for what " +
            "moved, upserts for new people, places and loot. Anything I did not say happened is canon_status \"proposed\" (it " +
            "gets an F code); list those under \"Inventions (accept / strike)\".\n" +
            "4. Write it back, every call as a dry run first (dry_run: true), showing me the result and its warnings, and for real " +
            "only after I approve:\n" +
            $"   - If {recorded} is live: campaign_write {{\"ops\": [...], \"campaign\": \"{slug}\"}} with no session (it " +
            "belongs to the live session), then campaign_session {\"action\": \"end\", \"recap_md\": ..., \"attendance\": [...], " +
            $"\"campaign\": \"{slug}\"}} LAST (ending it closes the session the writes belong to).\n" +
            $"   - Otherwise: campaign_session {{\"action\": \"record_past\"{SessionArg(number)}, " +
            "\"title\": ..., \"played_on\": ..., \"recap_md\": ..., \"attendance\": [...], " +
            $"\"campaign\": \"{slug}\"}} FIRST, and apply it once I approve " +
            "before dry-running the batch (a write can only name a session that exists); then campaign_write " +
            $"{{\"session\": {batchSession}, \"ops\": [...], \"campaign\": \"{slug}\"}}." +
            pastSession + "\n" +
            "5. Work through the checklist end or record_past returns with me, and give me the batch ids (each can be undone " +
            $"with campaign_history {{\"action\": \"undo\", \"batch_id\": ..., \"campaign\": \"{slug}\"}}).\n\n" +
            "Gate warnings mean a secret reached the table before its gate was met: they are applied anyway (the table is the " +
            "source of truth), so tell me about them rather than dropping the reveal. Never copy secret text into the recap.";
    }

    [McpServerPrompt(Name = "session_prep", Title = "Prepare a session")]
    [Description(
        "Prepare the next session as a run-sheet: reachable and locked beats, clocks, what the NPCs want, open questions and " +
        "gates, with encounter difficulty for each fight and a list of inventions to accept or strike; saved as the session's " +
        "prep after you approve. Usage: session_prep [campaign] [session].")]
    public string SessionPrep(
        [Description("The campaign's slug, one word, e.g. one-piece. Default: the active campaign.")] string? campaign = null,
        [Description("The session number to prepare, one word, e.g. 13. Default: the next session to play.")] string? session = null)
    {
        var (row, given) = CampaignAndSession(campaign, session);
        var slug = row.Slug;
        var number = given ?? NextSessionToPlay(row);
        var which = given is null ? $"the next session to play, session {number}," : $"session {number}";
        return
            $"Prepare {which} of campaign `{slug}` with me, in my run-sheet format. Use what I have said in this conversation " +
            "about what I want from it.\n" +
            CampaignOnEveryCall(slug) + "\n\n" +
            $"1. Where things stand: campaign {{\"action\": \"summary\", \"campaign\": \"{slug}\"}}, and the last " +
            $"session's recap and next hooks: campaign_session {{\"action\": \"get\", \"campaign\": \"{slug}\"}}.\n" +
            $"2. What is in play: campaign_search {{\"kinds\": [\"beat\"], \"campaign\": \"{slug}\"}} (which are reachable " +
            "now and which are locked behind which), then kinds [\"clock\"] (running clocks) and [\"thread\", \"quest\", " +
            "\"question\"] (open ones), and campaign_get for the NPCs involved (what they want).\n" +
            "3. Before planning any reveal, campaign_get {\"refs\": [<the gated fact or secret>], \"include\": [\"knowledge\"], " +
            $"\"campaign\": \"{slug}\"}}: its gate (after, with, routes) and whether it is ready. Do not plan a reveal whose gate " +
            "is closed without telling me.\n" +
            "4. Draft the run-sheet scene by scene: a read-aloud hook, what the NPCs want, likely player actions, the mechanical " +
            "spine (DCs, stat blocks, clocks) and the beat it must land.\n" +
            $"5. For each fight, encounter_difficulty {{\"party\": \"campaign\", \"monsters\": [...], \"campaign\": \"{slug}\"}} (the " +
            "party's levels from their sheets). For the dangerous ones, offer to store the fight for the night with combat " +
            $"{{\"action\": \"prepare\", \"name\": ..., \"combatants\": [...], \"campaign\": \"{slug}\"}} and to run it with " +
            $"balance_simulate {{\"encounter\": <its name>, \"campaign\": \"{slug}\"}} (the party fights from their sheets).\n" +
            "6. End with \"Inventions (accept / strike)\": every name, place or claim you made up.\n" +
            $"7. When I approve, save it: campaign_session {{\"action\": \"plan\"{SessionArg(number)}, " +
            $"\"title\": ..., \"prep_md\": <the run-sheet>, \"dry_run\": true, \"campaign\": \"{slug}\"}}, then the same call " +
            "without dry_run.";
    }

    [McpServerPrompt(Name = "knowledge_check", Title = "Does this character know that?")]
    [Description(
        "Check the latest draft in the conversation (a lyric, journal entry, in-character line or a recap read aloud) against " +
        "what one character knows: names they would not use, things they cannot know, forbidden words, secrets a song would " +
        "reveal. Hard flags first, then what to review. Usage: knowledge_check <character> [campaign].")]
    public string KnowledgeCheck(
        [Description("The character's slug, one word, e.g. belmakor.")] string character,
        [Description("The campaign's slug, one word. Default: the active campaign.")] string? campaign = null)
    {
        var row = Campaign(campaign);
        var who = Character(row, character);
        return
            $"Check the most recent draft in this conversation against what {who} knows in campaign `{row.Slug}`. If there is " +
            "no draft yet, ask me for it and stop.\n" +
            CampaignOnEveryCall(row.Slug) + "\n\n" +
            $"1. Call campaign_knowledge {{\"action\": \"check\", \"perspective\": \"{who}\", " +
            $"\"text\": <the draft, verbatim>, \"diegetic\": true, \"campaign\": \"{row.Slug}\"}}. Use diegetic true for a song or " +
            "anything said aloud in the world (a lyric is a public statement; audience defaults to the party, give \"public\" for a " +
            "crowd); false for a private journal.\n" +
            "2. Report the hard flags first, quoting the draft line for each: names the speaker does not use (other_name), " +
            "things they cannot know (unknown_entity), names from another campaign (cross_campaign), forbidden words, names the " +
            "audience does not know (reveals_to_audience) and secrets at risk. Then the things to review, sorted into knows / " +
            "suspects or heard / must not know.\n" +
            "3. For each hard flag, offer the closest version in their own words: the names the check says they use, and " +
            "imprecision instead of invention for what they cannot know (\"an old king\", not a name or an age).\n\n" +
            "The check is author-facing: it names true names and secrets so you can avoid them. Never put its words into the draft.";
    }

    [McpServerPrompt(Name = "continuity_check", Title = "Continuity check")]
    [Description(
        "The DM's 5-step continuity pass over the latest draft in the conversation (prep, a scene, a handout, NPC lines): name " +
        "the canon objects, confirm them, check them against what was played, check who could know this (reveal gates and " +
        "forbidden words included), and flag rather than fix. Usage: continuity_check [campaign].")]
    public string ContinuityCheck(
        [Description("The campaign's slug, one word, e.g. one-piece. Default: the active campaign.")] string? campaign = null)
    {
        var row = Campaign(campaign);
        return
            $"Run the 5-step continuity pass on the most recent draft in this conversation for campaign `{row.Slug}`. If there is " +
            "no draft yet, ask me for it and stop.\n" +
            CampaignOnEveryCall(row.Slug) + "\n\n" +
            "1. Name the canon objects: every person, place, item, faction and claim the draft relies on.\n" +
            $"2. Confirm each: campaign_search {{\"query\": ..., \"campaign\": \"{row.Slug}\"}} then campaign_get " +
            $"{{\"refs\": [...], \"include\": [\"facts\"], \"campaign\": \"{row.Slug}\"}}. Note what matches, what differs, and what is " +
            "not recorded at all (a possible invention).\n" +
            "3. Check against what was played: campaign_session {\"action\": \"get\" or \"recap\", \"session\": <n>, " +
            $"\"campaign\": \"{row.Slug}\"}} for the sessions involved, and campaign_history {{\"action\": \"since\", \"session\": <n>, " +
            $"\"campaign\": \"{row.Slug}\"}} if something may have changed. A superseded fact, or one resting on it, is stale.\n" +
            "4. Check who could know this: campaign_knowledge {\"action\": \"check\", " +
            "\"perspective\": <the speaker, e.g. \"character:<slug>\" or \"party\">, \"text\": <the draft>, \"diegetic\": true, " +
            $"\"campaign\": \"{row.Slug}\"}} (diegetic true for anything said aloud in the world). It also reports forbidden words " +
            "of active reveal gates and reveal rules. For any " +
            "reveal in the draft, read the gated fact or secret with campaign_get, include [\"knowledge\"], and check its gate " +
            "(after, with, routes).\n" +
            "5. Flag, don't fix: list each problem with the draft line and what the record says; propose a fix only when I ask. " +
            "List anything new under \"Inventions (accept / strike)\".";
    }

    [McpServerPrompt(Name = "in_character", Title = "Write in character")]
    [Description(
        "Draft what the conversation asks for (a journal entry, lyric, letter, in-character reply) in one character's voice, " +
        "from only what that character knows: their knowledge view first, then the draft, then a knowledge check before you " +
        "see it. Usage: in_character <character> [campaign].")]
    public string InCharacter(
        [Description("The character's slug, one word, e.g. belmakor.")] string character,
        [Description("The campaign's slug, one word. Default: the active campaign.")] string? campaign = null)
    {
        var row = Campaign(campaign);
        var who = Character(row, character);
        return
            $"Write the piece I ask for in this conversation in the voice of {who}, campaign `{row.Slug}`, using only what they " +
            "know. If I have not said what to write yet, ask me and stop.\n" +
            CampaignOnEveryCall(row.Slug) + "\n\n" +
            $"1. Before drafting, read their view: the resource campaign://{row.Slug}/knowledge/{who} (ReadMcpResourceTool), or " +
            $"campaign_search / campaign_get with perspective \"{who}\". Use only the names and facts that view " +
            "shows: not your own knowledge of the campaign, not my notes, not earlier author-view results in this conversation.\n" +
            "2. Draft. Where they cannot know something, stay vague rather than inventing (\"an old king\", not a name or an age).\n" +
            $"3. Before showing me, run campaign_knowledge {{\"action\": \"check\", \"perspective\": \"{who}\", \"text\": <the draft>, " +
            $"\"diegetic\": true, \"campaign\": \"{row.Slug}\"}} (diegetic true for a song or anything performed), fix every " +
            "hard flag, and tell me what it listed to review.\n" +
            "4. Mark anything new that could be quoted back as fact (a place, a person, a claim) as an invention for me to accept " +
            "or strike.";
    }

    [McpServerPrompt(Name = "homebrew_review", Title = "Review homebrew balance")]
    [Description(
        "Measure the homebrew in the conversation (a feat, spell, item, subclass feature, boon) with balance_compare against the " +
        "official option it replaces, and report where it lands on the homebrew-balance band scale (Under, On budget, Creeping, " +
        "Over, Breaking), with the smallest fix. Usage: homebrew_review [campaign].")]
    public string HomebrewReview(
        [Description("The campaign's slug, one word, whose house balance standards apply. Default: the active campaign, if any.")]
        string? campaign = null)
    {
        var slug = OptionalCampaign(campaign);
        var standards = slug is not null
            ? "1. Read the house balance standards: campaign_search {\"kinds\": [\"rule\"], \"query\": \"balance\", " +
              $"\"campaign\": \"{slug}\"}}; apply any you find (a target band, an effective-level rule).\n"
            : _campaigns.Store.List().Count == 0
                ? "1. There is no campaign to take house standards from; use the published rules.\n"
                : "1. No campaign is chosen, so no house standards apply; use the published rules. To apply a campaign's, run " +
                  "this prompt again with its slug (homebrew_review <campaign>; campaign {\"action\": \"list\"} lists them).\n";
        return
            "Review the homebrew in this conversation for balance. If there is none yet, ask me for it and stop.\n" +
            (slug is null ? ThisServersTools : CampaignOnEveryCall(slug)) + "\n\n" +
            standards +
            "2. Model it in the feature DSL and call balance_compare with the OFFICIAL option it replaces or competes " +
            "with as the baseline (a feat: the ASI it replaces; a subclass feature: the official subclass's feature at that " +
            "level), at the levels that matter, e.g. [1, 5, 11, 17].\n" +
            "3. Report the band on the homebrew-balance scale (Under, On budget, Creeping, Over, Breaking) with the ΔDPR, the " +
            "level-equivalent and what drives it. Say what the numbers do not cover (utility, defence, action economy beyond the " +
            "collision warnings).\n" +
            "4. Suggest the smallest change that lands it On budget, and re-run balance_compare to confirm it.\n" +
            (slug is null
                ? "5. Keep the verdict in the conversation; with no campaign chosen there is none to record it in."
                : "5. Record it only after I approve: campaign_write {\"ops\": [{\"op\": \"upsert\", " +
                  "\"kind\": \"homebrew\", \"name\": ..., \"status\": \"approved\" or \"needs_nerf\", \"body_md\": <the verdict with its " +
                  $"numbers>}}], \"dry_run\": true, \"campaign\": \"{slug}\"}}, then the same call without dry_run.");
    }

    // The campaign, and the session: a number typed where the campaign goes is the session when no campaign has that slug.
    private (CampaignRow Row, string? Session) CampaignAndSession(string? campaign, string? session)
    {
        if (session is null && campaign is { } token && IsNumber(token) && _campaigns.Store.TryGet(token.Trim()) is null)
        {
            return (Campaign(null), Session(token));
        }

        return (Campaign(campaign), session is null ? null : Session(session));
    }

    private CampaignRow Campaign(string? campaign) => _campaigns.Resolve(string.IsNullOrWhiteSpace(campaign) ? null : campaign.Trim());

    // homebrew_review works without a campaign: only a campaign that was named must exist.
    private string? OptionalCampaign(string? campaign)
    {
        if (!string.IsNullOrWhiteSpace(campaign))
        {
            return Campaign(campaign).Slug;
        }

        try
        {
            return _campaigns.Resolve(null).Slug;
        }
        catch (DndInputException)
        {
            return null;
        }
    }

    // "belmakor" or "character:belmakor" → "character:belmakor", refused (with suggestions the party may see) when this
    // campaign has no such character. The loader's message is the one every character perspective gets.
    private string Character(CampaignRow campaign, string character)
    {
        var text = (character ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            throw new DndInputException("character is required: the character's slug, one word, e.g. belmakor.");
        }

        var perspective = Perspective.Parse(text.StartsWith(CampaignValues.PerspectiveKinds.CharacterPrefix, StringComparison.OrdinalIgnoreCase)
            ? text
            : CampaignValues.PerspectiveKinds.CharacterPrefix + text);
        using var connection = _campaigns.Database.TryOpenExisting()
                               ?? throw new DndInputException("There are no campaigns yet. Create one with campaign {\"action\": \"create\"}.");
        new KnowledgeLoader(connection, campaign).Resolve(perspective);
        return perspective.Text;
    }

    private static string? Session(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("session:", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["session:".Length..];
        }

        if (!IsNumber(trimmed) || !int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            throw new DndInputException($"session \"{CampaignMarkdownText.Echo(text.Trim())}\" is not a session number; give one, e.g. 12.");
        }

        if (number > CampaignLimits.MaxSessionNumber)
        {
            throw new DndInputException(
                $"session {number.ToString(CultureInfo.InvariantCulture)} is out of range: session numbers are 0 to " +
                $"{CampaignLimits.MaxSessionNumber.ToString("N0", CultureInfo.InvariantCulture)}.");
        }

        return number.ToString(CultureInfo.InvariantCulture);
    }

    private static string SessionArg(string? number) => number is null ? string.Empty : $", \"session\": {number}";

    /// <summary>
    /// The line every prompt carries: the tools it names are the ones of the server that served it (class summary: the
    /// server cannot know its registered name, and a dev and an installed server can both be connected).
    /// </summary>
    internal const string ThisServersTools = "The tools named below are those of the MCP server this prompt came from.";

    // The lines every campaign prompt carries: whose tools, and that the calls it names in prose must name the campaign too.
    private static string CampaignOnEveryCall(string slug) =>
        ThisServersTools + $" Pass \"campaign\": \"{slug}\" on every call to its campaign tools, as the calls below do: without it " +
        "a call goes to the active campaign, which may be another.";

    /// <summary>
    /// The next session to play, as <c>start</c> picks it: the lowest planned or prepped session after the last played
    /// one, else one after the highest (1 when there are none).
    /// </summary>
    private string NextSessionToPlay(CampaignRow campaign)
    {
        var reader = new SessionReader(_campaigns.Database);
        var sessions = new List<SessionSummary>();
        string? cursor = null;
        do
        {
            var page = reader.List(campaign, null, CampaignLimits.MaxListLimit, cursor, Perspective.Author);
            sessions.AddRange(page.Sessions);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        var lastPlayed = sessions.Where(s => s.Status == CampaignValues.SessionStatuses.Played).Select(s => s.Number).DefaultIfEmpty(-1).Max();
        var next = sessions
            .Where(s => s.Number > lastPlayed && s.Status is CampaignValues.SessionStatuses.Planned or CampaignValues.SessionStatuses.Prepped)
            .Select(s => (int?)s.Number)
            .Min() ?? sessions.Select(s => s.Number).DefaultIfEmpty(0).Max() + 1;
        return next.ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsNumber(string text) => text.Trim().Length is > 0 and <= 9 && text.Trim().All(char.IsAsciiDigit);
}
