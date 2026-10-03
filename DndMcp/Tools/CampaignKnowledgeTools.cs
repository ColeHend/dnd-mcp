using System.ComponentModel;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using DndMcp.Formatting.Campaign;
using DndMcp.Hosting;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Repository.Campaign.Write;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace DndMcp.Tools;

/// <summary>
/// <c>campaign_knowledge</c>: who knows what. <c>record</c>, <c>reveal</c> and <c>retract</c> write knowledge rows through
/// <see cref="KnowledgeWriter"/> (the same rows, gate checks and secret-status derivation as <c>campaign_write</c>'s
/// <c>known_by</c>); <c>check</c> runs <see cref="KnowledgeCheck"/> over a draft for a speaker; <c>ledger</c> renders
/// <see cref="KnowledgeLedger"/>. Thin by design: every rule and message lives in the repository services.
///
/// <para>
/// <b>check and ledger are author tools.</b> Their <c>perspective</c> is who is being checked, not who is reading: the
/// output names what the speaker must not say, true names included, because the author needs exactly that to fix a lyric.
/// The formatters say so in the result so the model does not paste a flag's text into the draft. The player-safe view of
/// one character's knowledge is <c>campaign_search</c> / <c>campaign_get</c> with that perspective, or the
/// <c>campaign://&lt;slug&gt;/knowledge/&lt;perspective&gt;</c> resource.
/// </para>
/// <para>
/// <b>One tool, five actions, one flat parameter list</b> (the house pattern for action tools): each action takes a
/// subset, and <see cref="ActionArgumentCheck"/> refuses an argument the action does not take instead of ignoring it (a
/// <c>text</c> sent with <c>reveal</c> would otherwise vanish, and the model would believe it had been checked).
/// </para>
/// <para>
/// <b>Annotations: destructive</b> (retract deletes rows), not idempotent (each write is a new batch), closed-world.
/// </para>
/// </summary>
public sealed class CampaignKnowledgeTools
{
    internal const string Example =
        "{\"action\": \"check\", \"campaign\": \"belmakor\", \"perspective\": \"character:belmakor\", " +
        "\"text\": \"Old king, come down\", \"diegetic\": true}";

    private const string Record = "record";
    private const string Reveal = "reveal";
    private const string Retract = "retract";
    private const string Check = "check";
    private const string Ledger = "ledger";

    private static readonly ActionArgumentCheck Actions = new(
        "campaign_knowledge",
        new DslValueSet("campaign_knowledge action", [Record, Reveal, Check, Ledger, Retract]),
        new Dictionary<string, ActionArguments>(StringComparer.Ordinal)
        {
            [Record] = new(["targets", "knowers", "session", "reason", "dry_run", "campaign"],
                "{\"action\": \"record\", \"targets\": [\"f:12\"], \"knowers\": [{\"who\": \"character:serif\", \"state\": \"suspects\"}]}"),
            [Reveal] = new(["facts", "secret", "handout", "to", "how", "session", "reason", "dry_run", "campaign"],
                "{\"action\": \"reveal\", \"facts\": [\"f:12\"], \"to\": [\"party\"], \"how\": \"told\", \"dry_run\": true}"),
            [Retract] = new(["targets", "who", "session", "reason", "dry_run", "campaign"],
                "{\"action\": \"retract\", \"targets\": [\"f:12\"], \"who\": [\"party\"]}"),
            [Check] = new(["text", "perspective", "diegetic", "audience", "as_of_session", "campaign"], Example),
            [Ledger] = new(["about", "facts", "perspectives", "as_of_session", "campaign"],
                "{\"action\": \"ledger\", \"about\": [\"character:the-old-king\"], \"perspectives\": [\"party\", \"character:belmakor\"]}"),
        },
        Example);

    private readonly CampaignService _campaigns;

    public CampaignKnowledgeTools(CampaignService campaigns)
    {
        _campaigns = campaigns;
    }

    // Destructive: retract deletes knowledge rows. Not idempotent: every write call is a new batch. Closed-world.
    [McpServerTool(Name = "campaign_knowledge", Title = "Who knows what", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "Who knows what in a campaign: record it, reveal facts at the table, check a draft (a lyric, journal, in-character " +
        "line) against what its speaker knows, and see the ledger. Actions and their arguments:\n" +
        "- record: targets (fact or entity handles) and knowers [{who, state, known_as, how, via, session, note}]; who is " +
        "party, table, public, dm, author or character:<slug>; state knows (default), suspects, believes, misbelieves, heard, " +
        "met, aware, unrecognized, unaware or forgot.\n" +
        "- reveal: facts, secret (a secret entity: its gated facts) or handout; to (default [\"party\"]); how. Written as " +
        "knows in the session. A gated fact whose gate is not met is revealed anyway, with warnings.\n" +
        "- retract: targets and who (a list): deletes those rows. To say someone does not know, record state unaware.\n" +
        "- check: text (the draft, required) and perspective (the speaker, e.g. \"character:belmakor\"); diegetic true for a " +
        "song or anything said aloud in the world, to an audience (default party); as_of_session. Hard flags first (names the " +
        "speaker does not use, unknown entities, other campaigns' names, forbidden words, secrets the audience must not hear), " +
        "then facts to review. Author-facing.\n" +
        "- ledger: about (entity handles) and/or facts; perspectives (default: every knower on record plus party, table and " +
        "public); as_of_session. A grid: no record, not met, not in play and unaware stay distinct.\n" +
        "record, reveal and retract take session (default: the live session), reason and dry_run, and give a batch id for " +
        "campaign_history undo. Every action takes campaign (default: the active one).\n" +
        "Example: " + Example)]
    public string Knowledge(
        [Description("record, reveal, check, ledger or retract.")] string action,
        [Description("The campaign's slug, e.g. \"belmakor\". Default: the one chosen with campaign use, else the only one.")]
        string? campaign = null,
        [Description("record, retract: fact or entity handles, e.g. [\"f:12\", \"character:old-king\"].")]
        string[]? targets = null,
        [Description("record: who knows the targets and how, e.g. [{\"who\": \"character:belmakor\", \"state\": \"met\", \"known_as\": \"the old king\"}].")]
        KnowerSpec[]? knowers = null,
        [Description("reveal: fact handles to reveal, e.g. [\"f:12\"]. ledger: fact handles to show as rows.")]
        string[]? facts = null,
        [Description("reveal: a secret entity, e.g. \"secret:fruits-are-the-seal\": reveals its gated facts and makes the knowers aware of it.")]
        string? secret = null,
        [Description("reveal: a handout entity: reveals the facts it is about and marks it delivered.")]
        string? handout = null,
        [Description("reveal: who learns it, e.g. [\"party\"] or [\"character:serif\"]. Default [\"party\"].")]
        string[]? to = null,
        [Description("reveal: how they learned it, e.g. \"told\", \"read\", \"witnessed\".")]
        string? how = null,
        [Description("retract: whose rows to delete, e.g. [\"party\"] or [\"character:belmakor\"].")]
        string[]? who = null,
        [Description("check: the draft to check, verbatim (at most 50,000 characters).")]
        string? text = null,
        [Description("check: the speaker: \"author\" (default), \"dm\", \"table\", \"party\", \"public\" or \"character:<slug>\".")]
        string? perspective = null,
        [Description("check: true when the text is said in the world to an audience (a song is a public statement). Default false.")]
        bool? diegetic = null,
        [Description("check, diegetic: who hears it, e.g. \"party\" (default) or \"public\".")]
        string? audience = null,
        [Description("ledger: entity handles; each is a row followed by its facts, e.g. [\"character:protector\"].")]
        string[]? about = null,
        [Description("ledger: the columns, e.g. [\"party\", \"character:serif\"] (at most 12).")]
        string[]? perspectives = null,
        [Description("check, ledger: as of the end of this session number, e.g. 10.")]
        [AIParameterName("as_of_session")] int? asOfSession = null,
        [Description("record, reveal, retract: the session number it happened in. Default: the live session, else none.")]
        int? session = null,
        [Description("record, reveal, retract: why, kept in the history.")]
        string? reason = null,
        [Description("record, reveal, retract: true to preview; nothing is written. Default false.")]
        [AIParameterName("dry_run")] bool? dryRun = null)
    {
        var name = Actions.Action(action);
        Actions.Refuse(name,
            ("targets", targets is not null), ("knowers", knowers is not null), ("facts", facts is not null),
            ("secret", secret is not null), ("handout", handout is not null), ("to", to is not null), ("how", how is not null),
            ("who", who is not null), ("text", text is not null), ("perspective", perspective is not null),
            ("diegetic", diegetic is not null), ("audience", audience is not null), ("about", about is not null),
            ("perspectives", perspectives is not null), ("as_of_session", asOfSession is not null), ("session", session is not null),
            ("reason", reason is not null), ("dry_run", dryRun is not null));

        var row = _campaigns.Resolve(campaign);
        var context = WriteContext.For(session, reason, dryRun ?? false);
        switch (name)
        {
            case Record:
                return KnowledgeMarkdown.FormatWrite(row, name,
                    new KnowledgeWriter(_campaigns.Database).Record(row, Strings(targets), knowers ?? [], context));
            case Reveal:
                return KnowledgeMarkdown.FormatWrite(row, name,
                    new KnowledgeWriter(_campaigns.Database).Reveal(row, facts is null ? null : Strings(facts), secret, handout,
                        to is null ? null : Strings(to), how, context));
            case Retract:
                return KnowledgeMarkdown.FormatWrite(row, name,
                    new KnowledgeWriter(_campaigns.Database).Retract(row, Strings(targets), Strings(who), context));
            case Check:
                if (diegetic == false && audience is not null)
                {
                    throw new DndInputException(
                        "audience is who hears a diegetic text; with diegetic false there is no audience. Set diegetic true, or leave audience out.");
                }

                var speaker = Perspective.Parse(perspective);
                var heard = audience is null ? null : Perspective.Parse(audience);
                var isDiegetic = diegetic ?? heard is not null;
                var result = new KnowledgeCheck(_campaigns.Database).Check(row, new CheckRequest(text ?? string.Empty, speaker, isDiegetic, heard, asOfSession));
                return CheckMarkdown.Format(row, result, isDiegetic, asOfSession);
            default:
                var ledger = new KnowledgeLedger(_campaigns.Database).Build(row, about is null ? null : Strings(about),
                    facts is null ? null : Strings(facts), perspectives is null ? null : Strings(perspectives), asOfSession);
                return LedgerMarkdown.Format(row, ledger);
        }
    }

    // A null item (JSON null inside the list) becomes an empty handle, which the service refuses by position.
    private static IReadOnlyList<string> Strings(string?[]? values) => values?.Select(v => v ?? string.Empty).ToList() ?? [];
}

/// <summary>What one action of an action tool takes, as a message lists it, and an example call of that action.</summary>
internal sealed record ActionArguments(IReadOnlyList<string> Takes, string Example);

/// <summary>
/// The action vocabulary of an action-discriminated tool (<c>campaign_knowledge</c>, <c>campaign_session</c>) and which
/// arguments each action takes.
///
/// <para>
/// <b>Why refuse instead of ignore.</b> One flat parameter list serves every action, so the schema cannot say that
/// <c>text</c> belongs to <c>check</c>; a <c>recap_md</c> sent with <c>start</c> would bind and be dropped, and the model
/// would report a recap it never wrote. The action is matched forgivingly (<see cref="DslValueSet"/>: "Record-Past" is
/// <c>record_past</c>), and a given argument the action does not take is refused with the list it does take and an example
/// of that action (contract §0: what was wrong, what is accepted, an example), so one retry fixes it. <c>campaign</c> is
/// listed per action like every other argument, so the list is complete.
/// </para>
/// </summary>
internal sealed class ActionArgumentCheck
{
    private readonly string _tool;
    private readonly DslValueSet _actions;
    private readonly IReadOnlyDictionary<string, ActionArguments> _takes;
    private readonly string _example;

    public ActionArgumentCheck(string tool, DslValueSet actions, IReadOnlyDictionary<string, ActionArguments> takes, string example)
    {
        _tool = tool;
        _actions = actions;
        _takes = takes;
        _example = example;
    }

    /// <summary>The canonical action.</summary>
    /// <exception cref="DndInputException">Blank or not one of the tool's actions.</exception>
    public string Action(string? action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            throw new DndInputException($"action is required: {_actions.List}. Example: {_example}");
        }

        if (!_actions.TryMatch(action, out var canonical))
        {
            throw new DndInputException(
                $"action \"{CampaignMarkdownText.Echo(action.Trim())}\" is not a {_tool} action; give {_actions.List}. Example: {_example}");
        }

        return canonical;
    }

    /// <summary>What <paramref name="action"/> takes, as a message lists it.</summary>
    public string Takes(string action) => string.Join(", ", _takes[action].Takes);

    /// <summary>Throws when an argument that was given is not one <paramref name="action"/> takes.</summary>
    /// <exception cref="DndInputException">Names every such argument, lists what the action takes and gives an example of it.</exception>
    public void Refuse(string action, params (string Name, bool Given)[] arguments)
    {
        var refused = arguments.Where(a => a.Given && !_takes[action].Takes.Contains(a.Name)).Select(a => $"\"{a.Name}\"").ToList();
        if (refused.Count == 0)
        {
            return;
        }

        var list = refused.Count == 1 ? refused[0] : string.Join(", ", refused.Take(refused.Count - 1)) + " or " + refused[^1];
        throw new DndInputException($"{_tool} {action} does not take {list}; {action} takes {Takes(action)}. Example: {_takes[action].Example}");
    }
}
