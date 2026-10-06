using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using DndMcp.Domain.Combat;
using DndMcp.Domain.Core;
using DndMcp.Domain.Dice;
using DndMcp.Domain.Features;
using DndMcp.Formatting.Campaign;
using DndMcp.Hosting;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Combat;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using DamagePart = DndMcp.Domain.Combat.DamagePartInput;
using Effect = DndMcp.Domain.Combat.EffectInput;
using InitiativeRoll = DndMcp.Domain.Combat.InitiativeRollInput;

namespace DndMcp.Tools;

/// <summary>
/// <c>combat</c>: the live fight at the table (contract §6): <c>prepare</c> and <c>start</c> a fight, <c>add</c>,
/// <c>set</c> and <c>leave</c> combatants, roll <c>initiative</c>, step turns (<c>next</c>, <c>prev</c>), apply
/// <c>damage</c>, <c>heal</c>, <c>condition</c>s, <c>concentration</c>, <c>use</c> of slots, resources and items,
/// <c>legendary</c> actions and <c>death_save</c>s, read the <c>state</c> (the author's table, or the party board for a
/// perspective), and <c>end</c> it, writing the sheet-seeded combatants back to their sheets as ONE undoable batch. Thin
/// by design: refuse what the action does not take, resolve the campaign and the <c>srd</c> monsters, map the arguments
/// onto the Repository's requests and the tracker's ops, render with <see cref="CombatMarkdown"/>.
///
/// <para>
/// <b>One flat parameter list, refused per action</b> (the house action-tool pattern, contract §6.0): every action's
/// arguments are top-level parameters, so the schema cannot say that <c>spell</c> belongs to <c>concentration</c>;
/// <see cref="ActionArgumentCheck"/> refuses a given argument the action does not take, with the list it does take and an
/// example. Those two calls are the method's first two statements: nothing is parsed, resolved, looked up or rolled before
/// them (ServerSurfaceTests sends every simple argument to every action on a server with no campaigns.db and asserts the
/// refusal created none). <c>perspective</c> is taken by <c>state</c> alone (D10): every other result is the author's.
/// </para>
/// <para>
/// <b>Monsters are resolved here, rolls are never made here.</b> An <c>srd</c> entry is resolved through
/// <see cref="StatBlockService"/> (the SRD index is the host's) in the FIGHT's edition (<see cref="CombatService.MonsterEdition"/>:
/// a planned fight's own ruleset, never the campaign's when they differ), with this tool's wording for a creature the SRD
/// lacks (add it by name with hp, ac and init_bonus), and handed to the Repository whole, which snapshots it. Every server
/// roll (initiative, damage and heal dice, saves, <c>hp: "roll"</c>) is the Repository's, made with the injected
/// <see cref="IDiceRoller"/> inside the step's transaction and logged there (D11, D22): a roll made here could not be
/// linked to the combat_log row that cites it, and a refused step must roll nothing.
/// </para>
/// <para>
/// <b>Outputs.</b> Every author step prints the encounter's heading, what changed (the tracker's lines with their
/// arithmetic), the rolls made, the initiative table and the reminders, each with the call that resolves it (campaign
/// named last, as every printed fix call is). <c>end</c> prints like a write result: the batch paragraph with its undo
/// call (only when a batch was logged), then what was written back. <c>state</c> with a perspective prints the board
/// from the Repository's whitelist model and nothing else (§6.12). All capped at <see cref="CampaignMarkdownText.MaxChars"/>.
/// </para>
/// <para>
/// <b><c>source</c> says whose roll it is</b> (fix F2, review LR03). A damage roll with no source belongs to the creature
/// whose turn it is (contract §6.10), and that decides whether the party sees it. The old text, "default the
/// turn-holder", read as "leave it out", so an enemy's legendary action after a PC's turn was logged, in the open, as
/// that PC's roll. The parameter text says to give it for every roll made outside the roller's own turn.
/// </para>
/// <para>
/// <b>Hints:</b> not read-only; not destructive (HP ticks are updates kept out of the history by design, <c>end</c>
/// writes a consumed item's quantity and never deletes a row, and its write-back is one undoable batch); not idempotent (a
/// second damage is more damage); closed-world (campaigns.db and the SRD this binary ships). The host's filter treats
/// <c>combat</c> as a campaign tool, so a read that meets a damaged campaigns.db gets the store's message.
/// </para>
/// </summary>
public sealed class CombatTools
{
    internal const string Tool = "combat";

    /// <summary>
    /// The description's example, also the one the refusals of an unknown action and of <c>damage</c> print. Placeholders,
    /// not a fixture's names (fix F1, X2-N): the refusals come before the campaign is resolved, so they cannot name its own
    /// combatants, and an example naming one campaign's characters ("torch" in "belmakor") was printed in every other
    /// campaign's refusals. The campaign is named last, as in every call the server prints.
    /// </summary>
    internal const string Example =
        "{\"action\": \"damage\", \"targets\": [\"<name>\"], \"parts\": [{\"amount\": 14, \"type\": \"bludgeoning\"}, " +
        "{\"amount\": 21, \"type\": \"necrotic\"}], \"campaign\": \"<slug>\"}";

    private const string CampaignArgument = "campaign";
    private const string EncounterArgument = "encounter";

    /// <summary>The action vocabulary, the Repository's constants (contract §6.0's 17, in its order).</summary>
    internal static readonly DslValueSet ActionSet = new("action", CombatActions.All);

    // Each action's arguments (contract §6.0) and an example of it, with placeholders for the fight's own names (<name>, a
    // combatant's tracker name; character:<slug>; see Example). Every action takes campaign; all but prepare take encounter.
    private static readonly ActionArgumentCheck Actions = new(
        Tool,
        ActionSet,
        new Dictionary<string, ActionArguments>(StringComparer.Ordinal)
        {
            [CombatActions.Prepare] = new(["name", "lair", "edition", "combatants", CampaignArgument],
                "{\"action\": \"prepare\", \"name\": \"<name>\", \"combatants\": [{\"srd\": \"Ogre\", \"count\": 2}]}"),
            [CombatActions.Start] = new(Takes("name", "add_party", "lair", "edition", "combatants", "surprised"),
                "{\"action\": \"start\", \"encounter\": \"<name>\"}"),
            [CombatActions.Add] = new(Takes("combatants"),
                "{\"action\": \"add\", \"combatants\": [{\"srd\": \"Ogre\"}]}"),
            [CombatActions.Set] = new(Takes("combatants"),
                "{\"action\": \"set\", \"combatants\": [{\"character\": \"character:<slug>\", \"hp\": 30}]}"),
            [CombatActions.Leave] = new(Takes("targets"), "{\"action\": \"leave\", \"targets\": [\"<name>\"]}"),
            [CombatActions.Initiative] = new(Takes("rolls", "surprised", "secret"),
                "{\"action\": \"initiative\", \"rolls\": [{\"combatant\": \"<name>\", \"face\": 17}, {\"combatant\": \"<other name>\", \"total\": 25}]}"),
            [CombatActions.Next] = new(Takes("from"), "{\"action\": \"next\", \"from\": \"<name>\"}"),
            [CombatActions.Prev] = new(Takes(), "{\"action\": \"prev\"}"),
            [CombatActions.Damage] = new(
                Takes("targets", "amount", "dice", "parts", "damage_type", "critical", "magical", "half", "raw", "knock_out", "source", "secret"),
                Example),
            [CombatActions.Heal] = new(Takes("targets", "amount", "dice", "temp", "item", "source", "secret"),
                "{\"action\": \"heal\", \"targets\": [\"<name>\"], \"dice\": \"2d4+2\", \"item\": \"Potion of Healing\"}"),
            [CombatActions.Condition] = new(
                Takes("targets", "add", "remove", "duration", "source", "dc", "ability", "level", "round", "effect", "resource"),
                "{\"action\": \"condition\", \"targets\": [\"<name>\"], \"add\": [\"frightened\"], \"source\": \"<other name>\", \"duration\": \"until_end_of_source_turn\"}"),
            [CombatActions.Concentration] = new(Takes("targets", "spell", "slot_level", "duration", "drop", "total", "face", "secret"),
                "{\"action\": \"concentration\", \"targets\": [\"<name>\"], \"spell\": \"Bless\", \"slot_level\": 1, \"duration\": \"1 minute\"}"),
            [CombatActions.Use] = new(Takes("targets", "slot_level", "pact", "resource", "item", "amount"),
                "{\"action\": \"use\", \"targets\": [\"<name>\"], \"slot_level\": 3}"),
            [CombatActions.Legendary] = new(Takes("source", "amount", "name", "resistance"),
                "{\"action\": \"legendary\", \"source\": \"<name>\", \"name\": \"Attack\"}"),
            [CombatActions.DeathSave] = new(Takes("targets", "face", "total", "stable", "secret"),
                "{\"action\": \"death_save\", \"targets\": [\"<name>\"], \"face\": 14}"),
            [CombatActions.State] = new(Takes("perspective"), "{\"action\": \"state\", \"perspective\": \"party\"}"),
            [CombatActions.End] = new(Takes("outcome", "xp", "loot", "currency", "discard", "force", "dry_run", "reason"),
                "{\"action\": \"end\", \"outcome\": \"The party won.\", \"loot\": [{\"item\": \"Potion of Healing\"}]}"),
        },
        Example);

    private readonly CampaignService _campaigns;
    private readonly StatBlockService _statBlocks;
    private readonly IDiceRoller _roller;

    /// <param name="campaigns">campaigns.db and the process's current campaign.</param>
    /// <param name="statBlocks">The SRD stat blocks an <c>srd</c> entry is resolved to (snapshotted by the Repository).</param>
    /// <param name="roller">The server's dice, handed to the Repository, which makes and logs every roll (never this tool).</param>
    public CombatTools(CampaignService campaigns, StatBlockService statBlocks, IDiceRoller roller)
    {
        _campaigns = campaigns;
        _statBlocks = statBlocks;
        _roller = roller;
    }

    // Not read-only, not destructive (nothing is deleted; end's write-back is one undoable batch), not idempotent (a second
    // damage is more damage), closed-world.
    [McpServerTool(Name = Tool, Title = "Live combat", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "A live fight. Each step returns the initiative table and reminders with the calls that resolve them. HP ticks stay out " +
        "of campaign_history; end writes the sheets back as one undoable batch. The server rolls what you don't give. Actions " +
        "and their arguments:\n" +
        "- prepare: name, lair, edition, combatants: a fight for later.\n" +
        "- start: name or encounter (a prepared fight), add_party, lair, edition, combatants, surprised.\n" +
        "- add: combatants [{srd, character or name; count, hp (\"avg\", \"roll\", \"unknown\", 45), ac, init_bonus, side, hidden, death_saves}].\n" +
        "- set: combatants already in (hp, ac, init_bonus, side, hidden, death_saves, max_hp_reduction).\n" +
        "- leave: targets (out of the fight).\n" +
        "- initiative: rolls [{combatant, face or total}], surprised; the rest are rolled.\n" +
        "- next: from (the expected turn-holder).\n" +
        "- prev: one turn back.\n" +
        "- damage: targets, amount, dice or parts [{amount or dice, type}], damage_type, critical, magical, half (saved), raw, knock_out, source.\n" +
        "- heal: targets, amount or dice, temp (temporary HP), item, source.\n" +
        "- condition: targets, add or remove (conditions, exhaustion, effects), duration (\"1 minute\", \"save ends\", \"until removed\"), " +
        "source, dc, ability, level, round, effect {ac, resist, immune, vulnerable, except}, resource.\n" +
        "- concentration: targets, spell, slot_level, duration; drop; total or face for the pending save.\n" +
        "- use: targets, slot_level, pact, resource or item, amount (negative restores).\n" +
        "- legendary: source (the legendary creature), amount, name; resistance (Legendary Resistance).\n" +
        "- death_save: targets, face or total, stable.\n" +
        "- state: perspective (\"party\", \"character:<slug>\").\n" +
        "- end: outcome, xp, loot [{item, srd, qty, to}], currency [{to, gp, …}], discard, force, dry_run, reason.\n" +
        "Every action takes campaign; all but prepare take encounter (default \"current\"). damage, heal, initiative, concentration " +
        "and death_save take secret.\n" +
        "Example: " + Example)]
    public async Task<string> Combat(
        [Description("prepare, start, add, set, leave, initiative, next, prev, damage, heal, condition, concentration, use, legendary, death_save, state or end.")]
        string action,
        [Description("The campaign's slug. Default: the current campaign.")]
        string? campaign = null,
        [Description("Which fight: current (the active one; the default), last (the last ended) or its name. start: a prepared fight to begin.")]
        string? encounter = null,
        [Description("prepare, start: the fight's name (start with a prepared fight's name begins it). legendary: the action's name, e.g. Lash.")]
        string? name = null,
        [Description("start: add every current party member, played from their sheets. Default true.")]
        [AIParameterName("add_party")] bool? addParty = null,
        [Description("prepare, start: fought in a lair (in-lair legendary uses and XP). Default false.")]
        bool? lair = null,
        [Description("prepare, start: 2014 or 2024. Default: the campaign's ruleset (needed in a mixed campaign).")]
        string? edition = null,
        [Description("prepare, start, add: who joins. set: combatants already in, each by character or name, with the fields to change.")]
        CombatantInput[]? combatants = null,
        [Description("Who: tracker names (Mummy 2), handles or slugs; mummy* is every copy. A list, even for one.")]
        string[]? targets = null,
        [Description("damage, heal: hit points, taken as given. use: uses spent (default 1; negative restores). legendary: its cost (default the action's).")]
        int? amount = null,
        [Description("damage, heal: dice the server rolls once for every target, e.g. 2d6+5. Give the normal dice; critical doubles them.")]
        string? dice = null,
        [Description("damage: the type of amount or dice, e.g. fire. Omit for untyped.")]
        [AIParameterName("damage_type")] string? damageType = null,
        [Description("damage: several damage types at once, each with amount or dice and its type.")]
        DamagePartInput[]? parts = null,
        [Description("damage: a critical hit doubles the dice of a server roll (a given amount is taken as given); at 0 HP, two death save failures.")]
        bool? critical = null,
        [Description("damage: from a magical attack (for resistances to nonmagical damage).")]
        bool? magical = null,
        [Description("damage: the targets that take half (they saved).")]
        string[]? half = null,
        [Description("damage: apply as given, ignoring resistance, immunity and vulnerability.")]
        bool? raw = null,
        [Description("damage: a melee attack that drops a creature to 0 knocks it out instead.")]
        [AIParameterName("knock_out")] bool? knockOut = null,
        [Description(
            "damage, heal: who rolled it; give it for any roll made outside that creature's own turn (legendary actions, reactions, opportunity attacks): " +
            "without it, damage belongs to the creature whose turn it is. condition: who imposes it. legendary: the legendary creature.")]
        string? source = null,
        [Description("The server's rolls of this call: true keeps them from the party, false shows them. Default: by who rolls.")]
        bool? secret = null,
        [Description("heal: grant temporary hit points instead (the higher stays).")]
        bool? temp = null,
        [Description("heal: an item consumed, e.g. Potion of Healing (the source's, else the target's). use: an item the target spends.")]
        string? item = null,
        [Description("condition: names to add, a list even for one: an SRD condition, exhaustion (with level) or any effect.")]
        string[]? add = null,
        [Description("condition: names to remove.")]
        string[]? remove = null,
        [Description("condition, concentration: 1 minute, 10 minutes, 3 rounds, save ends, until escape, end of round 2, until removed, fight, until the end of its next turn (the target's) or your next turn (the source's). Default per condition.")]
        string? duration = null,
        [Description("condition: the save or escape DC.")]
        int? dc = null,
        [Description("condition: the saving throw's ability, e.g. wis.")]
        string? ability = null,
        [Description("condition: exhaustion levels to add or remove. Default 1.")]
        int? level = null,
        [Description("condition: the round of an end of round duration. Default the current one.")]
        int? round = null,
        [Description("condition: what a named effect changes while it lasts.")]
        EffectInput? effect = null,
        [Description("condition: a resource each target spends as the effect starts, e.g. Rage. use: the resource spent.")]
        string? resource = null,
        [Description("concentration: the spell concentrated on.")]
        string? spell = null,
        [Description("concentration, use: the spell slot level spent, 1-9.")]
        [AIParameterName("slot_level")] int? slotLevel = null,
        [Description("use: true for a Pact Magic slot.")]
        bool? pact = null,
        [Description("concentration: true ends it, and what it holds.")]
        bool? drop = null,
        [Description("concentration, death_save: the save's final number. With neither total nor face, the server rolls it.")]
        int? total = null,
        [Description("concentration, death_save: the d20 as rolled; the modifiers are added here.")]
        int? face = null,
        [Description("death_save: true stabilises the creature (first aid).")]
        bool? stable = null,
        [Description("legendary: true spends a Legendary Resistance.")]
        bool? resistance = null,
        [Description("initiative: given values; everyone else without one is rolled. One member of a group sets the group.")]
        InitiativeRollInput[]? rolls = null,
        [Description("start, initiative: the combatants who are surprised.")]
        string[]? surprised = null,
        [Description("next: the turn-holder you expect; refused if the turn already moved (a retried call).")]
        string? from = null,
        [Description("state: whose view: author (default), dm, table, party, public or character:slug. Other views see only the party board.")]
        string? perspective = null,
        [Description("end: what happened, kept with the fight (author-only).")]
        string? outcome = null,
        [Description("end: XP shared by the party sheets. Default: the defeated and left enemies' XP when a party sheet tracks XP; 0 for none.")]
        int? xp = null,
        [Description("end: items found, created as holdings.")]
        LootInput[]? loot = null,
        [Description("end: coins found, one entry per recipient.")]
        CurrencyInput[]? currency = null,
        [Description("end: end with nothing written back.")]
        bool? discard = null,
        [Description("end: write back over sheet fields changed since the fight began.")]
        bool? force = null,
        [Description("end: true to preview the write-back; nothing is written. Default false.")]
        [AIParameterName("dry_run")] bool? dryRun = null,
        [Description("end: why, kept in the history.")]
        string? reason = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var act = Actions.Action(action);
        Actions.Refuse(act,
            (EncounterArgument, encounter is not null), ("name", name is not null), ("add_party", addParty is not null), ("lair", lair is not null),
            ("edition", edition is not null), ("combatants", combatants is not null), ("targets", targets is not null), ("amount", amount is not null),
            ("dice", dice is not null), ("damage_type", damageType is not null), ("parts", parts is not null), ("critical", critical is not null),
            ("magical", magical is not null), ("half", half is not null), ("raw", raw is not null), ("knock_out", knockOut is not null),
            ("source", source is not null), ("secret", secret is not null), ("temp", temp is not null), ("item", item is not null),
            ("add", add is not null), ("remove", remove is not null), ("duration", duration is not null), ("dc", dc is not null),
            ("ability", ability is not null), ("level", level is not null), ("round", round is not null), ("effect", effect is not null),
            ("resource", resource is not null), ("spell", spell is not null), ("slot_level", slotLevel is not null), ("pact", pact is not null),
            ("drop", drop is not null), ("total", total is not null), ("face", face is not null), ("stable", stable is not null),
            ("resistance", resistance is not null), ("rolls", rolls is not null), ("surprised", surprised is not null), ("from", from is not null),
            ("perspective", perspective is not null), ("outcome", outcome is not null), ("xp", xp is not null), ("loot", loot is not null),
            ("currency", currency is not null), ("discard", discard is not null), ("force", force is not null), ("dry_run", dryRun is not null),
            ("reason", reason is not null));

        var row = _campaigns.Resolve(campaign);
        try
        {
            return await RunAsync();
        }
        catch (DndInputException ex) when (CombatMarkdown.CampaignLastIn(ex.Message, row.Slug) is var message && message != ex.Message)
        {
            // The tracker's refusals print calls with no campaign, the Repository's with it second: every call a refusal
            // prints names this campaign last, as every call in a result does (CombatMarkdown.CampaignLastIn).
            throw new DndInputException(message, ex);
        }

        // The action, past the refusal of what it does not take and with its campaign resolved.
        async Task<string> RunAsync()
        {
            var service = new CombatService(_campaigns.Database, _roller);
            var fight = Blank(encounter);
            switch (act)
            {
                case CombatActions.State:
                    return State(row, fight, perspective);
                case CombatActions.End:
                {
                    var (lootRequests, currencyRequests) = EndEntries(loot, currency);
                    return CombatMarkdown.FormatEnd(service.End(row, fight, new EndRequest
                    {
                        Outcome = outcome,
                        Xp = xp,
                        Loot = lootRequests,
                        Currency = currencyRequests,
                        Discard = discard ?? false,
                        Force = force ?? false,
                        DryRun = dryRun ?? false,
                        Reason = reason,
                    }));
                }

                case CombatActions.Prepare:
                {
                    var entries = Additions(combatants);
                    var monsterEdition = entries.Any(e => e.Input.Srd is not null)
                        ? service.MonsterEdition(row, CombatActions.Prepare, edition: edition)
                        : string.Empty;
                    var requests = await RequestsAsync(entries, monsterEdition, progress, cancellationToken);
                    return CombatMarkdown.FormatStep(service.Prepare(row, new PrepareRequest(name ?? string.Empty)
                    {
                        Lair = lair ?? false,
                        Edition = edition,
                        Combatants = requests,
                    }));
                }

                case CombatActions.Start:
                {
                    var entries = Additions(combatants);
                    var monsterEdition = entries.Any(e => e.Input.Srd is not null)
                        ? service.MonsterEdition(row, CombatActions.Start, fight, name, edition)
                        : string.Empty;
                    var requests = await RequestsAsync(entries, monsterEdition, progress, cancellationToken);
                    var started = service.Start(row, new StartRequest
                    {
                        Name = name,
                        Encounter = fight,
                        AddParty = addParty ?? true,
                        Lair = lair,
                        Edition = edition,
                        Combatants = requests,
                        Surprised = Strings(surprised),
                    });
                    return CombatMarkdown.FormatStep(started, MakeCurrent(row, named: !string.IsNullOrWhiteSpace(campaign)));
                }

                case CombatActions.Add:
                {
                    var entries = Additions(combatants);
                    var monsterEdition = entries.Any(e => e.Input.Srd is not null)
                        ? service.MonsterEdition(row, CombatActions.Add, fight)
                        : string.Empty;
                    return CombatMarkdown.FormatStep(service.Add(row, fight, await RequestsAsync(entries, monsterEdition, progress, cancellationToken)));
                }

                default:
                    return CombatMarkdown.FormatStep(service.Step(row, fight, Op(act, combatants, targets, amount, dice, damageType, parts, critical, magical,
                        half, raw, knockOut, source, temp, item, add, remove, duration, dc, ability, level, round, effect, resource, spell, slotLevel, pact,
                        drop, total, face, stable, resistance, rolls, surprised, from, name), secret));
            }
        }
    }

    // What an action takes besides its own arguments: campaign (every action) and encounter (all but prepare).
    private static IReadOnlyList<string> Takes(params string[] own) => [.. own, CampaignArgument, EncounterArgument];

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    // A null item (JSON null inside a list) becomes an empty name, which the tracker refuses by position.
    private static IReadOnlyList<string> Strings(string?[]? values) => values?.Select(v => v ?? string.Empty).ToList() ?? [];

    private static IReadOnlyList<string>? StringsOrNull(string?[]? values) => values?.Select(v => v ?? string.Empty).ToList();

    /// <summary>
    /// <c>state</c>: the author's table (or the no-fight listing), or, for any other view, the party board the Repository
    /// builds as a whitelist (§6.12): the formatter prints only its fields.
    /// </summary>
    private string State(CampaignRow row, string? fight, string? perspective)
    {
        var view = CampaignView.Resolve(_campaigns.Database, row, perspective);
        var reader = new CombatReader(_campaigns.Database);
        if (view.AuthorView)
        {
            return CombatMarkdown.FormatState(row, reader.State(row, fight));
        }

        return CombatMarkdown.FormatBoard(reader.Board(row, view.Perspective, fight), view.Banner);
    }

    /// <summary>
    /// After a start that named its campaign: that campaign becomes this process's current one (contract §6.1, as session
    /// start does); the sentence that says so, or null when it already was or the call named none.
    /// </summary>
    private string? MakeCurrent(CampaignRow row, bool named)
    {
        if (!named || _campaigns.CurrentCampaignId == row.Id)
        {
            return null;
        }

        _campaigns.SetCurrent(row.Id);
        return $"{row.Slug} is now the current campaign: calls without campaign use it.";
    }

    // -------------------------------------------------------------------------------------------------------------------
    // Combatants

    private sealed record Addition(int Index, CombatantInput Input, string Where, HpChoice? Hp);

    /// <summary>
    /// The <c>combatants</c> of prepare, start and add, checked for what only those take: a null item, a blank srd, an
    /// <c>hp</c> that is not a word or a number, and <c>max_hp_reduction</c> (<c>set</c> only: a combatant joins at its
    /// maximum). Every item's problems are reported together (up to five, <see cref="DslProblems"/>), before any monster is
    /// looked up.
    /// </summary>
    private static IReadOnlyList<Addition> Additions(CombatantInput?[]? combatants)
    {
        if (combatants is null)
        {
            return [];
        }

        var problems = new List<string>();
        var additions = new List<Addition>(combatants.Length);
        for (var i = 0; i < combatants.Length; i++)
        {
            var where = $"combatants item {N(i + 1)}";
            if (combatants[i] is not { } input)
            {
                problems.Add($"{where} is null; give an entry such as {{\"srd\": \"Ogre\"}}.");
                continue;
            }

            if (input.Srd is { } srd && string.IsNullOrWhiteSpace(srd))
            {
                problems.Add($"{where}: srd is empty; give an SRD monster's ref or name, or leave srd out for a custom combatant with name, hp and ac.");
            }

            if (input.MaxHpReduction is not null)
            {
                problems.Add($"{where}: max_hp_reduction is set only (a combatant joins at its maximum); add it, then set its max_hp_reduction.");
            }

            var (hp, hpProblem) = Hp(input.Hp, where);
            if (hpProblem is not null)
            {
                problems.Add(hpProblem);
            }

            additions.Add(new Addition(i, input, where, hp));
        }

        DslProblems.ThrowIfAny(problems, "combatants");
        return additions;
    }

    /// <summary>
    /// The entries as the Repository takes them, each <c>srd</c> resolved to its stat block in <paramref name="edition"/>
    /// (one wait for the index, every unknown monster reported together).
    /// </summary>
    private async Task<IReadOnlyList<CombatantRequest>> RequestsAsync(
        IReadOnlyList<Addition> additions, string edition, IProgress<ProgressNotificationValue>? progress, CancellationToken cancellationToken)
    {
        var lookups = additions.Where(a => a.Input.Srd is not null).ToList();
        var blocks = new Dictionary<int, Domain.Simulation.StatBlock>();
        if (lookups.Count > 0)
        {
            var resolved = await _statBlocks.ResolveAsync(
                lookups.Select(a => new StatBlockRequest(a.Input.Srd!.Trim(), edition, Wording(a.Where))).ToList(), progress, cancellationToken);
            for (var i = 0; i < lookups.Count; i++)
            {
                blocks[lookups[i].Index] = resolved[i].Block;
            }
        }

        return additions.Select(a => new CombatantRequest
        {
            Monster = blocks.GetValueOrDefault(a.Index),
            Character = a.Input.Character,
            Name = a.Input.Name,
            Count = a.Input.Count ?? 1,
            Hp = a.Hp,
            Ac = a.Input.Ac,
            InitBonus = a.Input.InitBonus,
            Side = a.Input.Side,
            Hidden = a.Input.Hidden ?? false,
            DeathSaves = a.Input.DeathSaves,
        }).ToList();
    }

    /// <summary>
    /// combat's words around the shared monster lookup (contract §15 H2): the item as the guard counts it, the stat block
    /// serves "this 2014 fight", and a creature the SRD lacks is added by name with its own numbers.
    /// </summary>
    internal static MonsterLookupWording Wording(string where) => new(
        where,
        "fight",
        edition => $"in this {edition} fight",
        "Give a monster's ref (e.g. \"2024/monster/ogre\") or its name; for a creature the SRD lacks, add it by name with hp, ac and init_bonus instead of srd.",
        "For a creature not in the SRD, add it by name with hp, ac and init_bonus instead of srd.",
        name => "For a creature the SRD does not have (it has only some of the Monster Manual), add it by name with hp, ac and init_bonus " +
                $"instead of srd: {{\"name\": \"{name}\", \"hp\": 45, \"ac\": 15, \"init_bonus\": 2}}.");

    /// <summary>
    /// An entry's <c>hp</c>: "avg", "roll", "unknown" or a whole number (published untyped, read here); null takes D17's
    /// default. A value that is none of those is the problem to report, naming the item.
    /// </summary>
    private static (HpChoice? Hp, string? Problem) Hp(object? hp, string where)
    {
        switch (hp)
        {
            case null:
            case JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }:
                return (null, null);
            case JsonElement { ValueKind: JsonValueKind.Number } number when number.TryGetInt32(out var value):
                return (HpChoice.Of(value), null);
            case JsonElement { ValueKind: JsonValueKind.String } text:
                try
                {
                    return (HpChoice.Parse(text.GetString()!), null);
                }
                catch (DndInputException ex)
                {
                    return (null, $"{where}: {ex.Message}");
                }

            default:
                return (null, $"{where}: hp must be \"avg\", \"roll\", \"unknown\" or a whole number of hit points, e.g. 45.");
        }
    }

    /// <summary>
    /// <c>set</c>'s entries: each names one combatant already in the fight (its <c>character</c> handle or its tracker
    /// <c>name</c>, never both: no combatant is renamed, so a second address could only be ignored, and an ignored name
    /// that names another combatant would set the wrong one's numbers) and the fields to change; a source (<c>srd</c>) or
    /// <c>count</c> belongs to <c>add</c>, and <c>hp</c> is a number here (the combatant's current hit points).
    /// </summary>
    private static IReadOnlyList<SetEntry> SetEntries(CombatantInput?[]? combatants)
    {
        var problems = new List<string>();
        var entries = new List<SetEntry>();
        foreach (var (input, i) in (combatants ?? []).Select((c, i) => (c, i)))
        {
            var where = $"combatants item {N(i + 1)}";
            if (input is null)
            {
                problems.Add($"{where} is null; give the combatant and what to change, e.g. {{\"character\": \"character:<slug>\", \"hp\": 30}}.");
                continue;
            }

            if (input.Srd is not null || input.Count is not null)
            {
                problems.Add($"{where}: set changes a combatant already in the fight, so it takes no {(input.Srd is not null ? "srd" : "count")}; " +
                             "add new combatants with add.");
                continue;
            }

            if (Blank(input.Character) is not null && Blank(input.Name) is not null)
            {
                problems.Add($"{where}: give the combatant to change as character (its handle) or name (its tracker name), not both; set renames no one.");
                continue;
            }

            var address = Blank(input.Character) ?? Blank(input.Name);
            if (address is null)
            {
                problems.Add($"{where}: give the combatant to change as character (its handle) or name (its tracker name), e.g. {{\"name\": \"Mummy 2\", \"hp\": 40}}.");
                continue;
            }

            int? hp = null;
            switch (input.Hp)
            {
                case null:
                case JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }:
                    break;
                case JsonElement { ValueKind: JsonValueKind.Number } number when number.TryGetInt32(out var value):
                    hp = value;
                    break;
                case JsonElement { ValueKind: JsonValueKind.String } text when int.TryParse(text.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed):
                    hp = parsed;
                    break;
                default:
                    problems.Add($"{where}: hp in set is the combatant's current hit points, a whole number, e.g. 40.");
                    continue;
            }

            entries.Add(new SetEntry(address)
            {
                Hp = hp,
                Ac = input.Ac,
                InitBonus = input.InitBonus,
                Side = input.Side,
                Hidden = input.Hidden,
                DeathSaves = input.DeathSaves,
                MaxHpReduction = input.MaxHpReduction,
            });
        }

        DslProblems.ThrowIfAny(problems, "set");
        return entries;
    }

    // -------------------------------------------------------------------------------------------------------------------
    // The tracker's steps

    /// <summary>The tracker op of a step action, its arguments mapped one to one (contract §6.0).</summary>
    private static CombatOp Op(
        string act, CombatantInput?[]? combatants, string?[]? targets, int? amount, string? dice, string? damageType, DamagePartInput?[]? parts,
        bool? critical, bool? magical, string?[]? half, bool? raw, bool? knockOut, string? source, bool? temp, string? item, string?[]? add,
        string?[]? remove, string? duration, int? dc, string? ability, int? level, int? round, EffectInput? effect, string? resource, string? spell,
        int? slotLevel, bool? pact, bool? drop, int? total, int? face, bool? stable, bool? resistance, InitiativeRollInput?[]? rolls,
        string?[]? surprised, string? from, string? name) => act switch
    {
        CombatActions.Set => new SetOp(SetEntries(combatants)),
        CombatActions.Leave => new LeaveOp(Strings(targets)),
        CombatActions.Initiative => new InitiativeOp { Rolls = RollsOf(rolls), Surprised = Strings(surprised) },
        CombatActions.Next => new NextOp(Blank(from)),
        CombatActions.Prev => new PrevOp(),
        CombatActions.Damage => new DamageOp(Strings(targets))
        {
            Amount = amount,
            Dice = dice,
            Parts = PartsOf(parts),
            DamageType = damageType,
            Critical = critical ?? false,
            Magical = magical ?? false,
            Half = StringsOrNull(half),
            Raw = raw ?? false,
            KnockOut = knockOut ?? false,
            Source = Blank(source),
        },
        CombatActions.Heal => new HealOp(Strings(targets)) { Amount = amount, Dice = dice, Temp = temp ?? false, Item = Blank(item), Source = Blank(source) },
        CombatActions.Condition => new ConditionOp(Strings(targets))
        {
            Add = StringsOrNull(add),
            Remove = StringsOrNull(remove),
            Duration = duration,
            Source = Blank(source),
            Dc = dc,
            Ability = ability,
            Level = level,
            Round = round,
            Effect = effect is null
                ? null
                : new Effect(effect.Ac, StringsOrNull(effect.Resist), StringsOrNull(effect.Immune), StringsOrNull(effect.Vulnerable), StringsOrNull(effect.Except)),
            Resource = Blank(resource),
        },
        CombatActions.Concentration => new ConcentrationOp(Strings(targets))
        {
            Spell = spell,
            SlotLevel = slotLevel,
            Duration = duration,
            Drop = drop ?? false,
            Total = total,
            Face = face,
        },
        CombatActions.Use => new UseOp(Strings(targets)) { SlotLevel = slotLevel, Pact = pact ?? false, Resource = Blank(resource), Item = Blank(item), Amount = amount ?? 1 },
        CombatActions.Legendary => new LegendaryOp(Blank(source) ?? throw new DndInputException(
            "legendary needs source: the legendary creature acting, e.g. {\"action\": \"legendary\", \"source\": \"<name>\", \"name\": \"Attack\"}."))
        {
            Amount = amount,
            Name = Blank(name),
            Resistance = resistance ?? false,
        },
        CombatActions.DeathSave => new DeathSaveOp(Strings(targets)) { Face = face, Total = total, Stable = stable ?? false },
        _ => throw new InvalidOperationException($"combat {act} is not a tracker step."),
    };

    /// <summary>The damage parts as the tracker takes them; every null part is reported by position, together.</summary>
    private static IReadOnlyList<DamagePart>? PartsOf(DamagePartInput?[]? parts)
    {
        if (parts is null)
        {
            return null;
        }

        var problems = parts.Select((p, i) => p is null
                ? $"parts item {N(i + 1)} is null; give {{\"amount\": 14, \"type\": \"bludgeoning\"}} or {{\"dice\": \"2d6\", \"type\": \"fire\"}}."
                : null)
            .OfType<string>()
            .ToList();
        DslProblems.ThrowIfAny(problems, "damage");
        return parts.Select(p => new DamagePart(p!.Amount, p.Dice, p.Type)).ToList();
    }

    /// <summary>The given initiatives; a missing combatant or a total that is not a finite number is refused by position.</summary>
    private static IReadOnlyList<InitiativeRoll> RollsOf(InitiativeRollInput?[]? rolls)
    {
        if (rolls is null)
        {
            return [];
        }

        var problems = new List<string>();
        var mapped = new List<InitiativeRoll>(rolls.Length);
        for (var i = 0; i < rolls.Length; i++)
        {
            var where = $"rolls item {N(i + 1)}";
            if (rolls[i] is not { } roll || string.IsNullOrWhiteSpace(roll.Combatant))
            {
                problems.Add($"{where}: give the combatant and its face or total, e.g. {{\"combatant\": \"<name>\", \"face\": 17}}.");
                continue;
            }

            if (roll.Total is { } t && !double.IsFinite(t))
            {
                problems.Add($"{where}: total must be a finite number, e.g. 14 or 14.5.");
                continue;
            }

            mapped.Add(new InitiativeRoll(roll.Combatant, roll.Face, roll.Total));
        }

        DslProblems.ThrowIfAny(problems, "initiative");
        return mapped;
    }

    // -------------------------------------------------------------------------------------------------------------------
    // end

    /// <summary>
    /// The loot and coins as the Repository takes them. A null item in either list is reported here by position, every
    /// one together (<see cref="DslProblems"/>, as <c>end</c>'s other problems are); a blank item, a quantity that is not a
    /// finite number above 0 or an entry of no coins is the Repository's refusal.
    /// </summary>
    private static (IReadOnlyList<LootRequest> Loot, IReadOnlyList<CurrencyRequest> Currency) EndEntries(LootInput?[]? loot, CurrencyInput?[]? currency)
    {
        var problems = new List<string>();
        problems.AddRange((loot ?? []).Select((l, i) => l is null
            ? $"loot item {N(i + 1)} is null; give an item, e.g. {{\"item\": \"Potion of Healing\", \"qty\": 2}}."
            : null).OfType<string>());
        problems.AddRange((currency ?? []).Select((c, i) => c is null ? $"currency item {N(i + 1)} is null; give coins, e.g. {{\"gp\": 120}}." : null)
            .OfType<string>());
        DslProblems.ThrowIfAny(problems, "end");
        return (
            (loot ?? []).Select(l => new LootRequest(l!.Item ?? string.Empty) { Srd = l.Srd, Qty = l.Qty ?? 1, To = Blank(l.To) }).ToList(),
            (currency ?? []).Select(c => new CurrencyRequest { To = Blank(c!.To), Cp = c.Cp ?? 0, Sp = c.Sp ?? 0, Ep = c.Ep ?? 0, Gp = c.Gp ?? 0, Pp = c.Pp ?? 0 })
                .ToList());
    }

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
