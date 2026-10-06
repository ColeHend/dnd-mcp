using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using DndMcp.Domain.Characters;
using DndMcp.Domain.Core;
using DndMcp.Domain.Dice;
using DndMcp.Domain.Features;
using DndMcp.Formatting.Campaign;
using DndMcp.Hosting;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Characters;
using DndMcp.Repository.Campaign.Write;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace DndMcp.Tools;

/// <summary>
/// <c>campaign_character</c>: a campaign character's sheet (contract §7.1): read it (<c>get</c>, in any view), and change it
/// one action at a time: <c>update</c> (create or patch, with the <c>sim_profile</c> balance_simulate fights it with),
/// <c>damage</c>, <c>heal</c>, <c>temp_hp</c>, <c>use</c> (slots and resources), <c>rest</c>, <c>condition</c>,
/// <c>level_up</c>, <c>xp</c>, <c>inventory</c> and <c>currency</c>. Thin by design: refuse what the action does not take,
/// resolve the campaign, call <see cref="SheetReader"/> or one <see cref="CharacterWriter"/> method, render with
/// <see cref="SheetMarkdown"/> or <see cref="SheetWriteMarkdown"/>.
///
/// <para>
/// <b>One flat parameter list, refused per action.</b> Every action's arguments are top-level parameters (contract §15 H1:
/// a nested <c>args</c> object would bind a misspelt field and drop it), so the schema cannot say that <c>kind</c> belongs
/// to <c>rest</c>. <see cref="ActionArgumentCheck"/> refuses a given argument the action does not take, with the list it
/// does take and an example. Those two calls are the method's first two statements: nothing is parsed, resolved, looked
/// up or rolled before them (ServerSurfaceTests sends every simple argument to every action on a server with no
/// campaigns.db and asserts the refusal created none). <c>perspective</c> is taken by <c>get</c> alone (contract D10): a
/// write in a player's view means nothing, and a write result is the author's.
/// </para>
/// <para>
/// <b>Each write is one logged batch</b> (tool <c>campaign_character/&lt;action&gt;</c>, the call's session, reason and
/// dry_run), printed with its id and undo call only when a batch was logged: a routed action and a call that changed
/// nothing log nothing (<see cref="SheetWriteMarkdown"/>).
/// </para>
/// <para>
/// <b>The live fight</b> (contract D5). The <see cref="ICombatRouter"/> the container registers (the combat layer's) is
/// handed to the writer: while the character is a sheet-seeded combatant of its campaign's active encounter, damage,
/// heal, temp_hp, use and condition change the fight instead of the sheet ("Applied to the live fight …", no batch), and a
/// rest is refused. With no router registered (a host without combat, the tests) every action acts on the sheet.
/// </para>
/// <para>
/// <b>Every call a refusal prints names the campaign LAST</b> (fix F2, review UR02). A routed action is refused by the
/// tracker itself ("concentration is not a condition: … end it with combat {…, "drop": true}"), and the Domain never knows
/// the slug, so its calls name no campaign; sent while another campaign is current, such a call went there ("No combat is
/// running in other"). Once the campaign is resolved, every refusal goes through the completion pass <c>combat</c> uses
/// (<see cref="CombatMarkdown.CampaignLastIn"/>): a call that names this campaign is reordered, one that names another is
/// left as it is.
/// </para>
/// <para>
/// <b><c>sim_profile</c> is published untyped</b> (<see cref="CheckedAsAttribute"/> as <see cref="BuildSpec"/>, the
/// <c>balance_simulate compare</c> precedent): a typed build would add about 14,000 characters of schema to every load of
/// this tool, and the argument guard still checks every field of it as a typed build. It is read back with
/// <see cref="DslJson"/> and validated at the sheet's level by the sheet rules (<see cref="SheetUpdate"/>).
/// </para>
/// <para>
/// <b>Hints:</b> not read-only; destructive, because an inventory call that takes a holding to 0 deletes its row (the house
/// rule: a tool that deletes rows is destructive); not idempotent (a second damage is more damage); closed-world.
/// </para>
/// </summary>
public sealed class CampaignCharacterTools
{
    internal const string Tool = "campaign_character";

    // The campaign named last, as in every call the server prints (fix F1: one order everywhere).
    internal const string Example =
        "{\"action\": \"damage\", \"character\": \"character:belmakor\", \"amount\": 14, \"damage_type\": \"fire\", \"campaign\": \"belmakor\"}";

    private const string Character = "character";
    private const string CampaignArgument = "campaign";
    private const string Session = "session";
    private const string Reason = "reason";
    private const string DryRun = "dry_run";

    // Each action's example works verbatim, in order, on the sheet the update example makes (a 12th-level wizard: its slots
    // are derived), so a model that copies them in turn is never refused (CampaignCharacterToolTests pins it).
    private static readonly ActionArgumentCheck Actions = new(
        Tool,
        CharacterActions.Set,
        new Dictionary<string, ActionArguments>(StringComparer.Ordinal)
        {
            [CharacterActions.Get] = new(["perspective", Character, CampaignArgument],
                "{\"action\": \"get\", \"character\": \"character:belmakor\", \"perspective\": \"party\"}"),
            [CharacterActions.Update] = new(Writes("sheet", "sim_profile"),
                "{\"action\": \"update\", \"character\": \"character:belmakor\", \"sheet\": {\"classes\": [{\"class\": \"wizard\", \"level\": 12}], \"ac\": 17, \"max_hp\": 110}}"),
            [CharacterActions.Damage] = new(Writes("amount", "damage_type"), Example),
            [CharacterActions.Heal] = new(Writes("amount"), "{\"action\": \"heal\", \"character\": \"character:belmakor\", \"amount\": 10}"),
            [CharacterActions.TempHp] = new(Writes("amount"), "{\"action\": \"temp_hp\", \"character\": \"character:belmakor\", \"amount\": 7}"),
            [CharacterActions.Use] = new(Writes("slot_level", "pact", "resource", "amount"),
                "{\"action\": \"use\", \"character\": \"character:belmakor\", \"slot_level\": 3}"),
            [CharacterActions.Rest] = new(Writes("kind", "hit_dice", "rolls"),
                "{\"action\": \"rest\", \"character\": \"character:belmakor\", \"kind\": \"short\", \"hit_dice\": 2}"),
            [CharacterActions.Condition] = new(Writes("add", "remove", "level"),
                "{\"action\": \"condition\", \"character\": \"character:belmakor\", \"add\": [\"poisoned\"]}"),
            [CharacterActions.LevelUp] = new(Writes("class", "amount"),
                "{\"action\": \"level_up\", \"character\": \"character:belmakor\", \"class\": \"wizard\"}"),
            [CharacterActions.Xp] = new(Writes("amount"), "{\"action\": \"xp\", \"character\": \"character:belmakor\", \"amount\": 2400}"),
            [CharacterActions.Inventory] = new(Writes("items"),
                "{\"action\": \"inventory\", \"character\": \"character:belmakor\", \"items\": [{\"item\": \"Potion of Healing\", \"qty\": 2}]}"),
            [CharacterActions.Currency] = new(Writes("coins"),
                "{\"action\": \"currency\", \"character\": \"character:belmakor\", \"coins\": {\"gp\": 120}}"),
        },
        Example);

    private readonly CampaignService _campaigns;
    private readonly IDiceRoller _roller;
    private readonly ICombatRouter? _router;

    /// <param name="router">The combat layer's router (contract D5); null when none is registered: every action acts on the sheet.</param>
    public CampaignCharacterTools(CampaignService campaigns, IDiceRoller roller, ICombatRouter? router = null)
    {
        _campaigns = campaigns;
        _roller = roller;
        _router = router;
    }

    // Destructive: an inventory call that takes a holding to 0 deletes its row. Not idempotent: a second damage is more
    // damage. Closed-world: campaigns.db only.
    [McpServerTool(Name = Tool, Title = "Character sheets", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "A campaign character's sheet: HP, slots, resources, conditions, level, XP, inventory, coins, and the sim_profile " +
        "balance_simulate fights it with. Each change is one batch (campaign_history undoes it). While the character is in the " +
        "active combat, damage, heal, temp_hp, use and condition act on the fight (written back at its end) and rest is " +
        "refused. Actions and their arguments:\n" +
        "- get: perspective (a non-author view shows only the public line); in a DM campaign with no character, the whole party; a past " +
        "sheet: campaign_get include [\"sheet\"] with as_of_session.\n" +
        "- update: sheet {player, ruleset, species, background, classes [{class, subclass, level, hit_die}], level, xp, " +
        "abilities, ac, max_hp, hp, speed, initiative_bonus, save_proficiencies, defenses, slots, pact, resources [{name, max, " +
        "used, recharge}], feats, spells, notes, …}, sim_profile (a build as balance_dpr takes it: how balance_simulate fights " +
        "them): creates the sheet the first time, else patches the fields given.\n" +
        "- damage: amount, damage_type: resistances apply, temp HP first; at 0 HP, death saves.\n" +
        "- heal: amount (up to max HP).\n" +
        "- temp_hp: amount (the higher stays).\n" +
        "- use: slot_level, pact or resource, amount (default 1; negative restores).\n" +
        "- rest: kind (\"short\" or \"long\"), hit_dice (short: dice to spend), rolls (their faces; else rolled).\n" +
        "- condition: add, remove (names, e.g. [\"poisoned\"]; [\"concentration\"] ends the sheet's concentration), level " +
        "(exhaustion levels).\n" +
        "- level_up: class (needed when multiclassed), amount (HP gained, Con included; default the fixed value).\n" +
        "- xp: amount (signed).\n" +
        "- inventory: items [{item, qty (signed), srd, equipped, attuned, notes}].\n" +
        "- currency: coins {cp, sp, ep, gp, pp} (signed).\n" +
        "Every action takes character (default: your character in a player campaign) and campaign (default: the active " +
        "one); all but get take session, reason and dry_run.\n" +
        "Example: " + Example)]
    public string CampaignCharacter(
        [Description("get, update, damage, heal, temp_hp, use, rest, condition, level_up, xp, inventory or currency.")]
        string action,
        [Description("The character's handle or slug, e.g. \"character:belmakor\". Default: your character in a player campaign; a DM campaign needs it (get without it lists the party).")]
        string? character = null,
        [Description("The campaign's slug. Default: the current campaign.")]
        string? campaign = null,
        [Description("get: whose view: author (default), dm, table, party, public or character:<slug>. Other views see only the public line.")]
        string? perspective = null,
        [Description("update: the sheet fields to set; fields left out stay as they are.")]
        SheetSpec? sheet = null,
        [Description(
            "update: how balance_simulate fights this character: a build as balance_dpr takes it (name, level, abilities, " +
            "attacks, modifiers, fighting_style, …). Needs a sheet level.")]
        [CheckedAs(typeof(BuildSpec))][AIParameterName("sim_profile")] object? simProfile = null,
        [Description("damage, heal, temp_hp: hit points; xp: XP, negative takes away; use: uses spent (default 1; negative restores); level_up: HP gained.")]
        int? amount = null,
        [Description("damage: its type, e.g. fire (the sheet's resistances apply). Omit for untyped damage.")]
        [AIParameterName("damage_type")] string? damageType = null,
        [Description("use: a spell slot level, 1-9.")]
        [AIParameterName("slot_level")] int? slotLevel = null,
        [Description("use: true for a Pact Magic slot.")]
        bool? pact = null,
        [Description("use: a resource by name, e.g. Bladesong.")]
        string? resource = null,
        [Description("rest: short or long.")]
        string? kind = null,
        [Description("rest: Hit Dice to spend on a short rest, largest first. Default none.")]
        [AIParameterName("hit_dice")] int? hitDice = null,
        [Description("rest: the faces of the Hit Dice rolled at the table, in order; omitted, the server rolls them.")]
        int[]? rolls = null,
        [Description("condition: condition or effect names to add, a list even for one, e.g. [\"poisoned\"]; exhaustion with level.")]
        string[]? add = null,
        [Description("condition: names to remove; concentration ends the sheet's concentration.")]
        string[]? remove = null,
        [Description("condition: exhaustion levels to add or remove. Default 1.")]
        int? level = null,
        [Description("level_up: the class that gains the level (needed when multiclassed; a new SRD class starts at 1).")]
        [AIParameterName("class")] string? className = null,
        [Description("inventory: what to add (qty positive) or take away (qty negative), or a held item's equipped, attuned, notes or srd to change (no qty).")]
        InventoryItemInput[]? items = null,
        [Description("currency: the coins gained (positive) or spent (negative).")]
        CoinsInput? coins = null,
        [Description("The session the change belongs to, a number. Default: the live session.")]
        int? session = null,
        [Description("Why, kept in the history.")]
        string? reason = null,
        [Description("true to preview; nothing is written. Default false.")]
        [AIParameterName("dry_run")] bool? dryRun = null)
    {
        var name = Actions.Action(action);
        Actions.Refuse(name,
            ("perspective", perspective is not null), ("sheet", sheet is not null), ("sim_profile", Given(simProfile)), ("amount", amount is not null),
            ("damage_type", damageType is not null), ("slot_level", slotLevel is not null), ("pact", pact is not null),
            ("resource", resource is not null), ("kind", kind is not null), ("hit_dice", hitDice is not null), ("rolls", rolls is not null),
            ("add", add is not null), ("remove", remove is not null), ("level", level is not null), ("class", className is not null),
            ("items", items is not null), ("coins", coins is not null), (Session, session is not null), (Reason, reason is not null),
            (DryRun, dryRun is not null));

        var row = _campaigns.Resolve(campaign);
        try
        {
            return Run();
        }
        catch (DndInputException ex) when (CombatMarkdown.CampaignLastIn(ex.Message, row.Slug) is var message && message != ex.Message)
        {
            // A refusal the live fight gives (D5: the tracker's own, whose calls name no campaign) prints every call naming
            // this campaign last, as combat's refusals do (fix F2, review UR02; class summary).
            throw new DndInputException(message, ex);
        }

        // The action, past the refusal of what it does not take and with its campaign resolved.
        string Run()
        {
            if (name == CharacterActions.Get)
            {
                var view = CampaignView.Resolve(_campaigns.Database, row, perspective);
                var read = new SheetReader(_campaigns.Database).Get(row, Blank(character), view.Perspective);
                return SheetMarkdown.FormatGet(row, read, view.WithLiveFight(_campaigns.Database, row));
            }

            var writer = new CharacterWriter(_campaigns.Database, _router);
            var context = WriteContext.For(session, reason, dryRun ?? false);
            var who = Blank(character);
            var result = name switch
            {
                CharacterActions.Update => writer.Update(row, who, sheet, ProfileOf(simProfile), context),
                CharacterActions.Damage => writer.Damage(row, who, amount, damageType, context),
                CharacterActions.Heal => writer.Heal(row, who, amount, context),
                CharacterActions.TempHp => writer.TempHp(row, who, amount, context),
                CharacterActions.Use => writer.Use(row, who, slotLevel, pact ?? false, resource, amount, context),
                CharacterActions.Rest => writer.Rest(row, who, kind, hitDice, rolls, _roller, context),
                CharacterActions.Condition => writer.Condition(row, who, Strings(add), Strings(remove), level, context),
                CharacterActions.LevelUp => writer.LevelUp(row, who, className, amount, context),
                CharacterActions.Xp => writer.Xp(row, who, amount, context),
                CharacterActions.Inventory => writer.Inventory(row, who, ItemsOf(items), context),
                _ => writer.Currency(row, who, CoinsOf(coins), context),
            };
            return SheetWriteMarkdown.Format(row, result);
        }
    }

    // What a write action takes: its own arguments, then the character and campaign every action takes, then session,
    // reason and dry_run (every action but get).
    private static IReadOnlyList<string> Writes(params string[] own) => [.. own, Character, CampaignArgument, Session, Reason, DryRun];

    // An untyped argument binds as a JsonElement; null sent explicitly is "not given", as for every other argument.
    private static bool Given(object? value) => value is not null and not JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined };

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    /// <summary>
    /// The <c>sim_profile</c> as a build: the argument guard has already checked it field by field as a
    /// <see cref="BuildSpec"/> (<see cref="CheckedAsAttribute"/>), so this reads it with the DSL's own options (unknown fields
    /// refused), as the stored profile will be read back.
    /// </summary>
    private static BuildSpec? ProfileOf(object? simProfile)
    {
        if (!Given(simProfile))
        {
            return null;
        }

        if (simProfile is not JsonElement { ValueKind: JsonValueKind.Object } element)
        {
            throw new DndInputException(
                "sim_profile must be an object: a build as balance_dpr takes it, e.g. {\"name\": \"Belmakor\", \"level\": 12, \"attacks\": [...]}.");
        }

        return DslJson.Deserialize<BuildSpec>(element.GetRawText(), "sim_profile");
    }

    // A null item (JSON null inside the list) becomes an empty name, which the sheet rules refuse by position.
    private static IReadOnlyList<string>? Strings(string?[]? values) => values?.Select(v => v ?? string.Empty).ToList();

    /// <summary>The inventory entries as the writer takes them; a quantity that is not a finite number is refused here.</summary>
    private static IReadOnlyList<InventoryItem>? ItemsOf(InventoryItemInput?[]? items)
    {
        if (items is null)
        {
            return null;
        }

        var mapped = new List<InventoryItem>(items.Length);
        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i] ?? throw new DndInputException(
                $"items item {(i + 1).ToString(CultureInfo.InvariantCulture)}: is null; give an object such as {{\"item\": \"Rope\", \"qty\": 1}}.");
            if (item.Qty is { } qty && !double.IsFinite(qty))
            {
                throw new DndInputException(
                    $"items item {(i + 1).ToString(CultureInfo.InvariantCulture)}: qty must be a finite number (positive adds, negative takes away).");
            }

            mapped.Add(new InventoryItem
            {
                Item = item.Item, Qty = item.Qty, Srd = item.Srd, Equipped = item.Equipped, Attuned = item.Attuned, Notes = item.Notes,
            });
        }

        return mapped;
    }

    private static Coins? CoinsOf(CoinsInput? coins) =>
        coins is null ? null : new Coins(coins.Cp ?? 0, coins.Sp ?? 0, coins.Ep ?? 0, coins.Gp ?? 0, coins.Pp ?? 0);
}
