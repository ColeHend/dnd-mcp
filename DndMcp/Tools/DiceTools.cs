using System.ComponentModel;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DndMcp.Domain.Core;
using DndMcp.Domain.Dice;
using DndMcp.Formatting;
using DndMcp.Hosting;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Write;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DndMcp.Tools;

/// <summary>
/// <c>dice_roll</c> and <c>dice_odds</c>: one grammar (<see cref="DiceExpression"/>), two questions — "roll it"
/// and "how likely is it". Thin by design: validate the arguments, call Domain, render markdown.
///
/// <para>
/// <b>Table rolls are logged</b> (contract §3.11): while the resolved campaign (this process's current one, else the
/// active one, else the only one) has a live session, each roll of an unseeded call becomes one <c>dice_roll</c> row
/// through <see cref="DiceLogWriter"/>, and the result's last line says where ("Logged to belmakor, session 12."). A
/// seeded call is a replay, never a table roll, so it is not logged. Nothing is logged, and nothing is said, when no
/// session is live, unless the caller asked for a <c>secret</c> roll: a flag that silently did nothing would mislead
/// (PLAN dropped <c>secret</c> once for exactly that), so the result then says "Not logged: …". Every roll says so,
/// though, whenever another campaign has a live session that the roll did not reach: when several campaigns exist and
/// none is chosen, and when the current campaign has nothing live while another one does (its session started in another
/// Claude session, say). The DM running that session expects the table's rolls in its log, so the line names it and the
/// <c>campaign use</c> call that sends the next roll there; "no session is live" would be false.
/// </para>
/// <para>
/// <b>A roll never fails because of the log.</b> The dice are rolled before anything is written; a missing, locked or
/// damaged campaigns.db turns into a "Not logged: …" line under the dice, never an error, because an error reads as
/// "the roll failed" and invites a re-roll, which is exactly what a logged table roll must never cause. The warning with
/// the detail goes to the server log; the line gives the store's user-facing reason when it has one (its first sentence
/// only: the rest can say "try again"), else a fixed "could not be read / written; this roll stands".
/// </para>
/// <para>
/// <b>Never creates campaigns.db.</b> A user who only rolls dice must not find a database made for them, so the log is
/// consulted only when the file already exists; the live-session check is a read, so a roll with no live session takes no
/// write lock and no daily backup.
/// </para>
/// </summary>
public sealed class DiceTools
{
    public const int MaxTimes = 100;

    /// <summary>The roll's own label (the <c>label</c> argument), not a term label like 2d6[fire].</summary>
    public const int MaxRollLabelLength = 100;

    /// <summary>"Not logged: …" for a seeded call while a session is live: replays are not table rolls.</summary>
    public const string SeededNotLogged = "a seeded roll is a replay, not a table roll";

    /// <summary>
    /// "Not logged: …" when several campaigns exist and none is chosen for this process or as the active one. The use call
    /// names a campaign, as every call a campaign result prints does (use refuses one without it): the one campaign with a
    /// live session, where the table's rolls belong, so sent as printed it makes the next roll log there; else
    /// <c>&lt;slug&gt;</c>, to be filled from campaign {"action": "list"} (none live, or several).
    /// </summary>
    public static string NoCampaignChosen(string? liveSlug) =>
        $"no campaign is chosen (campaign {{\"action\": \"use\", \"campaign\": \"{liveSlug ?? "<slug>"}\"}} chooses one)";

    /// <summary>
    /// "Not logged: …" when the campaign calls without <c>campaign</c> go to (<paramref name="currentSlug"/>) has no live
    /// session but another one does: the night being played is in <paramref name="liveSlugs"/>, and its DM expects the
    /// table's rolls in its log. The use call names the live campaign when there is one (sent as printed, the next roll
    /// logs there), else <c>&lt;slug&gt;</c> for the model to fill from the list it was given.
    /// </summary>
    public static string LiveElsewhere(IReadOnlyList<string> liveSlugs, string currentSlug) =>
        (liveSlugs.Count == 1 ? $"{liveSlugs[0]} has a live session" : $"{string.Join(" and ", liveSlugs)} have live sessions") +
        $" but {currentSlug} is the current campaign: campaign {{\"action\": \"use\", \"campaign\": \"{(liveSlugs.Count == 1 ? liveSlugs[0] : "<slug>")}\"}}";

    /// <summary>"Not logged: …" when campaigns.db exists but cannot be read (the server log has the reason).</summary>
    public const string CannotRead = "the campaign database could not be read; this roll stands";

    /// <summary>"Not logged: …" when the write itself failed (the server log has the reason).</summary>
    public const string CannotWrite = "the campaign database could not be written; this roll stands";

    /// <summary>SQLITE_BUSY: another process held the lock past busy_timeout.</summary>
    private const int SqliteBusy = 5;

    private readonly IDiceRoller _roller;
    private readonly CampaignService _campaigns;
    private readonly ILogger<DiceTools> _logger;

    public DiceTools(IDiceRoller roller, CampaignService campaigns, ILogger<DiceTools> logger)
    {
        _roller = roller;
        _campaigns = campaigns;
        _logger = logger;
    }

    // Annotations are set explicitly on every tool: when they are left unset the MCP spec reads them as
    // destructive = true and openWorld = true, which is false for everything this server does locally.
    // Not ReadOnly: while a campaign session is live a roll appends to campaigns.db (never destructive: it only adds
    // rows). Not Idempotent: two identical calls give different answers.
    [McpServerTool(Name = "dice_roll", Title = "Roll dice", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Roll dice for the user with cryptographically secure randomness. The result shows the dice, including ones dropped, " +
        "rerolled or exploded, so it can be checked (very large rolls list the first dice and a subtotal). Use it for any roll " +
        "the user wants made; never make up results. " +
        "For probabilities use dice_odds rather than rolling many times.\n" +
        "Syntax: NdM (d20, 4d6, d% = d100), + and - between terms, * or / by a whole number (/ rounds down), parentheses. " +
        "Shorthands: adv = 2d20kh1, dis = 2d20kl1, ea = 3d20kh1 (Elven Accuracy).\n" +
        "Modifiers go straight after the dice, no space:\n" +
        "- kh3 / kl1 keep highest / lowest (k3 = kh3); dh1 / dl1 drop highest / lowest\n" +
        "- r1 or r<3 reroll until it no longer matches; ro<=2 reroll once\n" +
        "- ! explode on the highest face (!>=5 on 5 or more), !! compounding, !p penetrating\n" +
        "- min10 / max5 raise or lower each die\n" +
        "- cs>=8 count successes instead of summing; cf=1 also subtracts failures\n" +
        "Label a term with [text]: 8d6[fire]+2d6[radiant]. A trailing comparison checks the total: 1d20+7>=15 answers yes or no.\n" +
        "Examples: 4d6dl1 (ability score), adv+7, 2d6ro<=2+5 (2014 Great Weapon Fighting), 1d20min10+9 (Reliable Talent), " +
        "10d10cs>=8, 1d6!.\n" +
        "While the active campaign has a live session, each roll is logged to it (a seeded roll is not) and the result's " +
        "last line says so; secret: true logs it as a roll behind the DM's screen.\n" +
        "Limits: 1000 dice and 1000 sides per expression, 100 rolls per call.")]
    public string Roll(
        [Description("Dice expression, e.g. \"4d6kh3\", \"adv+5\", \"8d6[fire]\" or \"1d20+7>=15\".")] string expression,
        [Description("How many times to roll the whole expression (1-100). Default 1.")] int times = 1,
        [Description("Optional name shown with the result, e.g. \"Stealth\" or \"Fireball damage\". Logged with a roll made while a session is live; an open roll's label is shown to the players' views of that session, so name an NPC as they know it, or roll secret.")] string? label = null,
        [Description("true for a roll behind the DM's screen: logged to the live session as secret, never shown to player views. Default false.")] bool? secret = null,
        [Description("Optional whole-number seed for reproducible pseudo-random rolls (tests, replays). Omit for real, cryptographically random rolls.")] long? seed = null)
    {
        if (times is < 1 or > MaxTimes)
        {
            throw new DndInputException($"times must be between 1 and {MaxTimes} (got {times}).");
        }

        label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        if (label is not null && (label.Length > MaxRollLabelLength || label.Any(char.IsControl)))
        {
            throw new DndInputException($"label must be one line of at most {MaxRollLabelLength} characters, e.g. \"Stealth\".");
        }

        var parsed = DiceExpression.Parse(expression);
        IDiceRoller roller = seed is { } s ? new SeededDiceRoller(s) : _roller;

        // One budget for the whole call, so times = 100 cannot multiply a pathological expression's cost.
        var budget = new DiceRollBudget();
        var rolls = new List<DiceRoll>(times);
        for (var i = 0; i < times; i++)
        {
            rolls.Add(DiceEvaluator.Roll(parsed, roller, budget));
        }

        var note = LogNote(parsed, rolls, label, roller.Source, secret == true, seeded: seed is not null);
        return DiceRollMarkdown.Format(parsed, rolls, label, roller.Source, note);
    }

    /// <summary>
    /// Logs the rolls when the resolved campaign has a live session and the call is unseeded, and returns the result's last
    /// line: where they were logged, why not, or null when there is nothing to say (nothing live and no secret asked for,
    /// which keeps every campaign-free result byte-identical to before). Never throws for the store (class summary).
    /// </summary>
    private string? LogNote(DiceExpression parsed, IReadOnlyList<DiceRoll> rolls, string? label, string source, bool secret, bool seeded)
    {
        CampaignDatabase database;
        try
        {
            database = _campaigns.Database;
        }
        catch (CampaignStoreUnavailableException)
        {
            // No path for campaigns.db at all (no home directory): there is no campaign to log to.
            return secret ? NotLogged(DiceLogWriter.NoLiveSession) : null;
        }

        // Opening is where campaigns.db is migrated when it is behind and where CampaignDatabase checks the SQLite library
        // itself (an InvalidOperationException, possible only when the startup probe could not run; an
        // ObjectDisposedException when the server is shutting down), so here those are the store's failures, not bugs. The
        // catch is this narrow on purpose: an InvalidOperationException from the reads below is a bug and must surface.
        SqliteConnection? connection;
        try
        {
            connection = database.TryOpenExisting();
        }
        catch (Exception ex) when (ex is CampaignStoreUnavailableException or SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ReadFailed(ex);
        }

        if (connection is null)
        {
            // No campaigns.db, and a roll never creates one.
            return secret ? NotLogged(DiceLogWriter.NoLiveSession) : null;
        }

        string campaignId;
        using (connection)
        {
            try
            {
                var campaigns = HandleResolver.Campaigns(connection);
                if (campaigns.Count == 0)
                {
                    return secret ? NotLogged(DiceLogWriter.NoLiveSession) : null;
                }

                CampaignRow campaign;
                try
                {
                    campaign = _campaigns.Resolve(null);
                }
                catch (DndInputException)
                {
                    // Several campaigns and none chosen: which one's session the roll belongs to is unknown. Said for a secret
                    // roll, and for any roll while one of them has a live session: the DM running it would otherwise find the
                    // table's rolls missing from the log with nothing ever having said so.
                    var liveCampaigns = campaigns.Where(c => new HandleResolver(connection, c.Id).LiveSession() is not null).ToList();
                    return secret || liveCampaigns.Count > 0
                        ? NotLogged(NoCampaignChosen(liveCampaigns.Count == 1 ? liveCampaigns[0].Slug : null))
                        : null;
                }

                var live = new HandleResolver(connection, campaign.Id).LiveSession();
                if (live is null)
                {
                    // Nothing live in the current campaign, but a session of another one may be live (started in another
                    // Claude session, or here before that campaign was made current): every roll says so, with the use call
                    // that sends the next roll there. A seeded roll would not be logged even there, so it says why not.
                    var liveElsewhere = campaigns.Where(c => c.Id != campaign.Id && new HandleResolver(connection, c.Id).LiveSession() is not null)
                        .Select(c => c.Slug).ToList();
                    return liveElsewhere.Count > 0 ? NotLogged(seeded ? SeededNotLogged : LiveElsewhere(liveElsewhere, campaign.Slug))
                        : secret ? NotLogged(DiceLogWriter.NoLiveSession) : null;
                }

                if (seeded)
                {
                    return NotLogged(SeededNotLogged);
                }

                campaignId = campaign.Id;
            }
            catch (Exception ex) when (ex is CampaignStoreUnavailableException or SqliteException or IOException or UnauthorizedAccessException)
            {
                return ReadFailed(ex);
            }
        }

        var entries = rolls.Select((roll, i) => new DiceLogRoll(
            parsed.Text, label, roll.Total, roll.ComparisonMet, DiceLogDetail.Json(roll, i, rolls.Count, source))).ToList();
        var result = new DiceLogWriter(database, _logger).TryLog(campaignId, entries, secret);
        if (result is { Logged: true, CampaignSlug: { } slug, SessionNumber: { } number })
        {
            return $"Logged to {slug}, session {number.ToString(System.Globalization.CultureInfo.InvariantCulture)}{(secret ? " (secret)" : string.Empty)}.";
        }

        // The session ended between the check and the write: the same answer as "nothing live". Any other reason is a store
        // failure (DiceLogWriter has logged it), most often another process holding the write lock; the lasting causes (a
        // newer, damaged or unreadable file) already failed the open above, which names them.
        return result.NotLoggedReason == DiceLogWriter.NoLiveSession
            ? secret ? NotLogged(DiceLogWriter.NoLiveSession) : null
            : NotLogged(CannotWrite);
    }

    /// <summary>
    /// The "Not logged: …" line for a campaigns.db that could not be read, after a warning with the detail in the server log.
    /// A <see cref="CampaignStoreUnavailableException"/> is written for the user (what is wrong, which file; never SQL or
    /// campaign text), so its first sentence is shown: "campaigns.db … was written by a newer version of dnd-mcp" is
    /// something the user can act on, and the server log is somewhere they never look. Only the first sentence, because the
    /// rest can say "try again", which next to a roll reads as "roll again". Two cases keep the fixed wording: another
    /// process holding the lock (its first sentence says "this call did nothing", untrue of a roll that stands) and any
    /// other exception, whose text is SQLite's or the runtime's, not the user's.
    /// </summary>
    private string ReadFailed(Exception ex)
    {
        _logger.LogWarning(ex, "dice_roll could not read campaigns.db to find a live session; the roll was not logged.");
        return ex is CampaignStoreUnavailableException { InnerException: not SqliteException { SqliteErrorCode: SqliteBusy } } store &&
               FirstSentence(store.Message) is { Length: > 0 } reason
            ? NotLogged($"{reason}; this roll stands")
            : NotLogged(CannotRead);
    }

    // Up to the first ". " (a path's dots are almost never followed by a space; a cut there only shortens the reason),
    // on one line, without its full stop.
    private static string FirstSentence(string message)
    {
        var line = string.Join(' ', message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var end = line.IndexOf(". ", StringComparison.Ordinal);
        return (end < 0 ? line : line[..end]).TrimEnd('.', ' ');
    }

    private static string NotLogged(string reason) => $"Not logged: {reason}.";

    // Idempotent: exact answers are pure functions of the expression, and the Monte Carlo fallback uses a fixed seed.
    [McpServerTool(Name = "dice_odds", Title = "Dice odds", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Exact probabilities for a dice expression, computed rather than rolled. Use it for \"what are the odds…\", \"how likely…\", " +
        "\"what's the average…\" questions instead of estimating.\n" +
        "Same syntax as dice_roll: keep/drop, rerolls, exploding dice, min/max, success counting, adv/dis/ea.\n" +
        "- With a trailing comparison it answers that question: 8d6>=30 gives P(total ≥ 30); 1d20+5>=15 is the chance to hit " +
        "AC 15 with +5; adv+5>=15 the same with advantage. The exact fraction is shown when it is short.\n" +
        "- Without one it gives the mean, standard deviation, range, percentiles and, when there are at most 60 possible totals, " +
        "a table of P(=), P(≥) and P(≤).\n" +
        "Examples: 4d6kh3, 2d20kl1+3>=12, 8d6>=30, 10d10cs>=8>=3 (at least 3 successes), 1d6!.\n" +
        "It is dice arithmetic only: table rules such as a natural 20 always hitting are not applied.\n" +
        "Very large pools, and ! or !p exploding dice combined with keep/drop, are estimated by seeded Monte Carlo instead; " +
        "the result says so and gives 95% intervals.")]
    public string Odds(
        [Description("Dice expression, optionally ending in a comparison: \"8d6>=30\", \"4d6kh3\", \"adv+5>=15\".")] string expression,
        CancellationToken cancellationToken)
    {
        var parsed = DiceExpression.Parse(expression);
        var odds = DiceDistribution.Compute(parsed, cancellationToken: cancellationToken);
        return DiceOddsMarkdown.Format(odds);
    }
}

/// <summary>
/// The <c>dice_roll.detail</c> JSON of one roll (contract §3.11; <c>understand-change-sites.md</c> §1.4): enough to
/// re-render the roll exactly with <see cref="DiceRollMarkdown"/> after re-parsing the row's <c>expression</c>.
///
/// <para>
/// <b>Facts, never enums.</b> Only the rolled facts are stored (each group's term text, label and value; each die's faces,
/// raw and value, and the flags that are true); the rules (keep, reroll, explode, the comparison) come back from parsing
/// the stored expression text. A stored <see cref="DiceComparison"/> or <see cref="ExplodeKind"/> number would silently
/// change meaning the day an enum member is added. <c>groups[i]</c> pairs with <c>DiceExpression.Parse(expression).Groups[i]</c>
/// (evaluation order); <c>term</c> is the group's lower-cased text, a drift check for a reader.
/// </para>
/// <para>
/// <b>Bounded.</b> Every face is stored up to <see cref="MaxFacesStored"/> physical faces per roll. Past that (a 1000d6!
/// that exploded, a 1000d2r1) the groups keep their values only and <c>faces_omitted</c> counts the faces left out:
/// storing every face of a 100-roll call of exploding dice would put megabytes into one call's rows, in the database
/// every backup copies.
/// </para>
/// </summary>
internal static class DiceLogDetail
{
    /// <summary>The detail shape's version, so a later reader can tell this shape from a changed one.</summary>
    public const int Version = 1;

    /// <summary>Physical faces stored per roll: the grammar's dice cap, so any roll without explosions or rerolls keeps them all.</summary>
    public const int MaxFacesStored = DiceLimits.MaxDice;

    private static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Roll <paramref name="index"/> (0-based) of a call of <paramref name="count"/> rolls, as a JSON object.</summary>
    public static string Json(DiceRoll roll, int index, int count, string source)
    {
        var faces = roll.Groups.Sum(g => g.Dice.Sum(d => (long)d.Faces.Count));
        var withFaces = faces <= MaxFacesStored;
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, Options))
        {
            json.WriteStartObject();
            json.WriteNumber("v", Version);
            json.WriteString("source", source);
            json.WriteStartObject("call");
            json.WriteNumber("roll", index + 1);
            json.WriteNumber("of", count);
            json.WriteEndObject();
            if (!withFaces)
            {
                json.WriteNumber("faces_omitted", faces);
            }

            json.WriteStartArray("groups");
            foreach (var group in roll.Groups)
            {
                json.WriteStartObject();
                json.WriteString("term", group.Group.Text);
                json.WriteString("label", group.Group.Label);
                json.WriteNumber("value", group.Value);
                if (withFaces)
                {
                    json.WriteStartArray("dice");
                    foreach (var die in group.Dice)
                    {
                        WriteDie(json, die);
                    }

                    json.WriteEndArray();
                }

                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteDie(Utf8JsonWriter json, RolledDie die)
    {
        json.WriteStartObject();
        json.WriteStartArray("faces");
        foreach (var face in die.Faces)
        {
            json.WriteStartObject();
            json.WriteNumber("face", face.Face);
            if (face.Rerolled)
            {
                json.WriteBoolean("rerolled", true);
            }

            if (face.Exploded)
            {
                json.WriteBoolean("exploded", true);
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteNumber("raw", die.Raw);
        json.WriteNumber("value", die.Value);
        if (die.Exploded)
        {
            json.WriteBoolean("exploded", true);
        }

        if (die.Penetrated)
        {
            json.WriteBoolean("penetrated", true);
        }

        if (die.Dropped)
        {
            json.WriteBoolean("dropped", true);
        }

        if (die.Score != 0)
        {
            json.WriteNumber("score", die.Score);
        }

        json.WriteEndObject();
    }
}
