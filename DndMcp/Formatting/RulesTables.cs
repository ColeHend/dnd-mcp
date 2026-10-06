using System.Globalization;
using System.Text;
using DndMcp.Domain.Characters;
using DndMcp.Domain.Core;
using DndMcp.Domain.Encounters;
using DndMcp.Formatting.Srd;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;

namespace DndMcp.Formatting;

/// <summary>
/// The rules tables this server serves whole: the encounter-building tables of both editions, the tables keyed by
/// Challenge Rating, character advancement (XP and proficiency bonus by level, <see cref="Advancement"/>: the one part of
/// the 2024 Character Creation chapter served, which the rules tools' descriptions except from the missing chapters), and
/// the tables the DPR tools cite (<see cref="BalanceRulesTables"/>). Each is a
/// <c>rules://tables/&lt;slug&gt;</c> resource and is also reachable through <c>rules_get</c> (by that URI or by the
/// table's name), because Claude Desktop attaches resources only by hand.
///
/// <para>
/// <b>Rendered from the Domain tables the maths uses</b> (<see cref="ChallengeRatingTables"/>,
/// <see cref="EncounterTables2014"/>, <see cref="EncounterTables2024"/>), never typed a second time: a table the model is
/// shown and the arithmetic <c>encounter_difficulty</c> does cannot disagree. The multiplier table's shifted columns are
/// computed by the same <see cref="EncounterTables2014.Multiplier"/> call, for the same reason.
/// </para>
/// <para>
/// <b>Every table names its source</b>, and the 2014 ones say they are not SRD 5.1 text: they are the 2014 DMG's (also
/// in the free 2014 Basic Rules), and a model told only "2014 rules" would quote them as SRD. The names a table answers to
/// are pinned against every SRD name so that looking one up can never hide an SRD entry.
/// </para>
/// </summary>
internal static class RulesTables
{
    public const string UriPrefix = "rules://tables/";

    /// <summary>The URI that lists every table.</summary>
    public const string IndexUri = "rules://tables";

    private const string Dmg2014 = "Dungeon Master's Guide (2014)";

    private const string NotSrd51 = "also in the free 2014 Basic Rules; not in SRD 5.1";

    // Longer than any table URI; an unknown one is echoed back cut to this.
    private const int MaxEchoLength = 80;

    public static IReadOnlyList<RulesTable> All { get; } =
    [
        new(
            "cr-xp",
            "Experience Points and Proficiency Bonus by Challenge Rating",
            "2014 and 2024",
            "SRD 5.2.1, Monsters › Challenge Rating; the same values as SRD 5.1 and the " + Dmg2014 + ", p. 275",
            "XP and proficiency bonus for every Challenge Rating from 0 to 30, the same in the 2014 and 2024 rules.",
            ["Experience Points by Challenge Rating", "Proficiency Bonus by Challenge Rating", "XP by Challenge Rating", "XP by CR", "CR to XP"],
            CrXp),
        new(
            "xp-budget-2024",
            "XP Budget per Character",
            "2024",
            "SRD 5.2.1, Gameplay Toolbox › Combat Encounter Difficulty",
            "The 2024 XP budget per character for Low, Moderate and High encounters, levels 1–20.",
            ["XP Budget per Character", "XP Budget", "Encounter XP Budget"],
            XpBudget2024),
        new(
            "xp-thresholds-2014",
            "XP Thresholds by Character Level",
            "2014",
            $"{Dmg2014}, p. 82; {NotSrd51}",
            "The 2014 Easy, Medium, Hard and Deadly XP thresholds per character, levels 1–20 (from the DMG, not SRD 5.1).",
            ["XP Thresholds by Character Level", "XP Thresholds", "Encounter XP Thresholds"],
            XpThresholds2014),
        new(
            "encounter-multipliers-2014",
            "Encounter Multipliers",
            "2014",
            $"{Dmg2014}, pp. 82–83; {NotSrd51}",
            "The 2014 multipliers on monster XP for the number of monsters, with the party-size shift (from the DMG, not SRD 5.1).",
            ["Encounter Multipliers", "Encounter Multiplier", "Group Multipliers"],
            Multipliers2014),
        new(
            "adventuring-day-xp-2014",
            "Adventuring Day XP",
            "2014",
            $"{Dmg2014}, p. 84; {NotSrd51}",
            "The 2014 adjusted XP per character a party can handle in a day before a long rest (from the DMG, not SRD 5.1).",
            ["Adventuring Day XP", "Adventuring Day"],
            AdventuringDay2014),
        new(
            "monster-stats-by-cr-2014",
            "Monster Statistics by Challenge Rating",
            "2014",
            $"{Dmg2014}, pp. 274–275; not in either SRD, and the 2024 DMG has no equivalent",
            "The 2014 DMG's expected AC, hit points, attack bonus, damage per round and save DC for each CR (not in either SRD).",
            ["Monster Statistics by Challenge Rating", "Monster Statistics by CR", "Monster Stats by CR"],
            MonsterStats2014),
        new(
            EmpiricalSlug,
            "SRD Monster Statistics by Challenge Rating (Empirical)",
            "2014 and 2024",
            "SRD 5.1 and SRD 5.2.1 monster stat blocks, medians computed by this server (not a table in either SRD); the DMG " +
            $"columns: {Dmg2014}, pp. 274–275",
            "What the SRD's own monsters of each CR are, per edition: median AC, hit points, best attack bonus, best save DC " +
            "and mean save bonus, with how many monsters each row counts, beside the 2014 DMG's design targets.",
            ["SRD Monster Statistics by Challenge Rating", "Empirical Monster Statistics", "Empirical Monster Stats by CR",
             "Monster Stats by CR (Empirical)"],
            MonsterStatsEmpiricalPage),
        new(
            AdvancementSlug,
            "Character Advancement",
            "2014 and 2024",
            Advancement.Source,
            "The XP each character level needs (1–20) and the proficiency bonus at each level, the same in the 2014 and 2024 " +
            "rules: the one part of the 2024 Character Creation chapter this server has.",
            ["Character Advancement", "Character Advancement Table", "Level Advancement", "XP by Level", "Experience Points by Level",
             "Proficiency Bonus by Level"],
            CharacterAdvancement),
        .. BalanceRulesTables.All,
    ];

    /// <summary>
    /// The character advancement table's slug (contract D21): XP thresholds and proficiency bonus by level, served from
    /// <see cref="Advancement"/>, the table <c>campaign_character</c>'s xp and level_up reminders and a sheet's next XP
    /// threshold use, so what the model reads and what the sheet says cannot differ.
    /// </summary>
    public const string AdvancementSlug = "character-advancement";

    /// <summary>The empirical monster statistics' slug (<see cref="MonsterStatsEmpirical"/>).</summary>
    public const string EmpiricalSlug = "monster-stats-by-cr-empirical";

    /// <summary>
    /// What <c>rules_get</c> answers for a <c>rules://tables</c> URI: the table, or for <see cref="IndexUri"/> the list.
    /// An unknown table is an error that lists the real ones: answering it with the list read as success, and the model
    /// quoted a table it never received.
    /// </summary>
    /// <exception cref="DndInputException">No table has that URI.</exception>
    public static string Answer(string uri)
    {
        var clean = Clean(uri);
        if (clean.Equals(IndexUri, StringComparison.OrdinalIgnoreCase))
        {
            return Index();
        }

        return FindByUri(clean)?.Render() ?? throw new DndInputException(
            $"No rules table is at `{(clean.Length <= MaxEchoLength ? clean : clean[..MaxEchoLength] + "…")}`. The tables: " +
            $"{string.Join(", ", All.Select(t => $"`{t.Uri}`"))}; `{IndexUri}` describes them.");
    }

    /// <summary>The table at <c>rules://tables/&lt;slug&gt;</c>, or null.</summary>
    public static RulesTable? FindByUri(string uri)
    {
        var clean = Clean(uri);
        return clean.StartsWith(UriPrefix, StringComparison.OrdinalIgnoreCase)
            ? All.FirstOrDefault(t => string.Equals(t.Slug, clean[UriPrefix.Length..], StringComparison.OrdinalIgnoreCase))
            : null;
    }

    /// <summary>
    /// True for a URI under <c>rules://tables</c>, whether or not a table has it: the caller answers a known one with the
    /// table and an unknown one with the list, instead of reading it as an SRD ref.
    /// </summary>
    public static bool IsTablesUri(string text)
    {
        var clean = Clean(text);
        return clean.Equals(IndexUri, StringComparison.OrdinalIgnoreCase) ||
               clean.StartsWith(UriPrefix, StringComparison.OrdinalIgnoreCase);
    }

    // A URI as a model sends it, without surrounding whitespace, quotes or backticks, or a trailing slash.
    private static string Clean(string uri) => uri.Trim().Trim('`', '"', '\'').Trim().TrimEnd('/');

    /// <summary>The table one of whose names is <paramref name="name"/> (compared by <see cref="SrdNames.Key"/>), or null.</summary>
    public static RulesTable? FindByName(string name)
    {
        var key = SrdNames.Key(name);
        return key.Length == 0 ? null : All.FirstOrDefault(t => t.Names.Any(n => SrdNames.Key(n) == key));
    }

    /// <summary>Every table with its URI: what <see cref="IndexUri"/> and an unknown table URI answer with.</summary>
    public static string Index()
    {
        var text = new StringBuilder("# Rules tables\n\nPass one of these to rules_get as ref, or read it as a resource:\n\n");
        foreach (var table in All)
        {
            text.Append(CultureInfo.InvariantCulture, $"- `{table.Uri}`: {table.Title} ({table.Edition}). {table.Description}\n");
        }

        return text.ToString().TrimEnd() + "\n";
    }

    /// <summary>A table's page: its title, the edition and source line with its URI, the body, then each note.</summary>
    internal static string Page(RulesTable table, string body, params string[] notes)
    {
        var text = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"# {table.Title}\n\n")
            .Append(CultureInfo.InvariantCulture, $"*{table.Edition} rules · {table.Source} · `{table.Uri}`*\n\n")
            .Append(body).Append('\n');
        foreach (var note in notes)
        {
            text.Append('\n').Append(note).Append('\n');
        }

        return text.ToString();
    }

    private static string CrXp(RulesTable table) => Page(
        table,
        SrdMarkdownText.Table(
            ["CR", "XP", "PB"],
            ChallengeRating.All.Select(cr => (IReadOnlyList<string>)
            [
                cr.ToString(),
                cr == ChallengeRating.Zero ? "0 or 10" : Number(ChallengeRatingTables.Xp(cr)),
                SrdMarkdownText.Signed(ChallengeRatingTables.ProficiencyBonus(cr)),
            ])),
        "A CR 0 creature is worth 0 XP when it has no effective attacks. A 2014 stat block says which; most 2024 stat blocks " +
        "print \"XP 0 or 10\" and leave it to the GM. A 2024 legendary " +
        "creature's stat block may also give its XP in its lair (\"XP 5,900, or 7,200 in lair\"); in the SRD 5.2.1 that is " +
        "always the next CR's XP.");

    private static string XpBudget2024(RulesTable table) => Page(
        table,
        SrdMarkdownText.Table(
            ["Level", "Low", "Moderate", "High"],
            Levels().Select(level =>
            {
                var budget = EncounterTables2024.XpBudget(level);
                return (IReadOnlyList<string>)[Number(level), Number(budget.Low), Number(budget.Moderate), Number(budget.High)];
            })),
        "Multiply the value for the party's level by the number of characters, then spend as much of that budget on monster " +
        "XP as you can without going over. There is no multiplier for groups. The SRD gives one party level; for mixed " +
        "levels, encounter_difficulty sums each character's value (a reading, not SRD text).");

    private static string XpThresholds2014(RulesTable table) => Page(
        table,
        SrdMarkdownText.Table(
            ["Level", "Easy", "Medium", "Hard", "Deadly"],
            Levels().Select(level =>
            {
                var thresholds = EncounterTables2014.XpThresholds(level);
                return (IReadOnlyList<string>)
                    [Number(level), Number(thresholds.Easy), Number(thresholds.Medium), Number(thresholds.Hard), Number(thresholds.Deadly)];
            })),
        "Sum each character's thresholds for the party's. An encounter's difficulty is the highest threshold its adjusted " +
        "XP (the monsters' XP times the encounter multiplier, `" + UriPrefix + "encounter-multipliers-2014`) reaches. " +
        "encounter_difficulty does this arithmetic for a party and a list of monsters.");

    private static string Multipliers2014(RulesTable table) => Page(
        table,
        SrdMarkdownText.Table(
            [
                "Monsters",
                $"{EncounterTables2014.SmallPartyBelow}–{EncounterTables2014.LargePartyFrom - 1} characters",
                $"Fewer than {EncounterTables2014.SmallPartyBelow} characters",
                $"{EncounterTables2014.LargePartyFrom} or more characters",
            ],
            EncounterTables2014.MultiplierRows.Select(row => (IReadOnlyList<string>)
            [
                row.Label,
                Times(EncounterTables2014.Multiplier(row.MinMonsters, EncounterTables2014.SmallPartyBelow).Value),
                Times(EncounterTables2014.Multiplier(row.MinMonsters, EncounterTables2014.SmallPartyBelow - 1).Value),
                Times(EncounterTables2014.Multiplier(row.MinMonsters, EncounterTables2014.LargePartyFrom).Value),
            ])),
        "Multiply the monsters' total XP by the multiplier to judge the encounter against the party's XP thresholds " +
        "(`" + UriPrefix + "xp-thresholds-2014`). The adjusted value is only for judging difficulty; the party earns the " +
        "monsters' plain XP. Don't count monsters whose challenge rating is significantly below the average of the others " +
        "unless they add real difficulty.");

    private static string AdventuringDay2014(RulesTable table) => Page(
        table,
        SrdMarkdownText.Table(
            ["Level", "Adjusted XP per character"],
            Levels().Select(level => (IReadOnlyList<string>)[Number(level), Number(EncounterTables2014.AdventuringDayXp(level))])),
        "Sum the party's values: roughly the adjusted XP of the encounters it can handle before a long rest. The DMG " +
        "expects about six to eight medium or hard encounters in a day, with two short rests. The 2024 rules have no " +
        "adventuring-day budget.");

    private static string MonsterStats2014(RulesTable table) => Page(
        table,
        SrdMarkdownText.Table(
            ["CR", "PB", "AC", "Hit Points", "Attack", "Damage/Round", "Save DC"],
            ChallengeRatingTables.AllMonsterStats.Select(row => (IReadOnlyList<string>)
            [
                row.ChallengeRating.ToString(),
                SrdMarkdownText.Signed(row.ProficiencyBonus),
                (row.IsCeiling ? "≤ " : string.Empty) + Number(row.ArmorClass),
                $"{Number(row.HitPointsMin)}–{Number(row.HitPointsMax)}",
                (row.IsCeiling ? "≤ " : string.Empty) + SrdMarkdownText.Signed(row.AttackBonus),
                $"{Number(row.DamagePerRoundMin)}–{Number(row.DamagePerRoundMax)}",
                (row.IsCeiling ? "≤ " : string.Empty) + Number(row.SaveDc),
            ])),
        "Damage per round is the average if every attack hits, over the first three rounds. The DMG builds a CR from this " +
        "table: a defensive CR from hit points, moved one step for every 2 points of AC above or below the row's; an " +
        "offensive CR from damage per round, moved likewise by attack bonus or save DC; the final CR is their average. " +
        "For CR 0 the AC, attack bonus and save DC are maxima.");

    /// <summary>
    /// Both editions' empirical rows side by side for every CR, with the DMG 2014 row for comparison. A CR with no SRD
    /// monster in an edition shows <see cref="MonsterStatsEmpirical.MonsterStats"/>'s interpolated values marked "~" and a
    /// count of "—", because those numbers are not a census and a model must not quote them as one.
    /// </summary>
    private static string MonsterStatsEmpiricalPage(RulesTable table)
    {
        var rows = ChallengeRating.All.Select(cr =>
        {
            var dmg = ChallengeRatingTables.MonsterStats(cr);
            var cells = new List<string> { cr.ToString() };
            foreach (var edition in new[] { SrdEdition.Edition2014, SrdEdition.Edition2024 })
            {
                var row = MonsterStatsEmpirical.MonsterStats(edition, cr);
                var mark = row.IsInterpolated ? "~" : string.Empty;
                cells.Add(row.IsInterpolated ? "—" : Number(row.Count));
                cells.Add(mark + Median(row.ArmorClass));
                cells.Add(mark + Median(row.HitPoints));
                cells.Add(row.AttackCount == 0 && !row.IsInterpolated ? "—" : mark + "+" + Median(row.AttackBonus));
                cells.Add(row.SaveDcCount == 0 && !row.IsInterpolated
                    ? "—"
                    : mark + Median(row.SaveDc) + (row.IsInterpolated ? string.Empty : $" ({Number(row.SaveDcCount)})"));
                cells.Add(mark + SignedMedian(row.MeanSaveBonus));
            }

            cells.Add((dmg.IsCeiling ? "≤ " : string.Empty) + Number(dmg.ArmorClass));
            cells.Add($"{Number(dmg.HitPointsMin)}–{Number(dmg.HitPointsMax)}");
            cells.Add((dmg.IsCeiling ? "≤ " : string.Empty) + SrdMarkdownText.Signed(dmg.AttackBonus));
            cells.Add((dmg.IsCeiling ? "≤ " : string.Empty) + Number(dmg.SaveDc));
            return (IReadOnlyList<string>)cells;
        });

        return Page(
            table,
            SrdMarkdownText.Table(
                ["CR", "2014 n", "2014 AC", "2014 HP", "2014 attack", "2014 DC (n)", "2014 save", "2024 n", "2024 AC", "2024 HP",
                 "2024 attack", "2024 DC (n)", "2024 save", "DMG AC", "DMG HP", "DMG attack", "DMG DC"],
                rows),
            "**Columns.** n is how many SRD monsters of that CR the row counts (a shapechanger's forms count once, as the form it " +
            "fights in). AC and HP are medians of the stat blocks' own values; attack is the median of each monster's best attack " +
            "bonus (spell attacks included); DC is the median of each monster's best save DC over the monsters that have one, " +
            "with their number in brackets; save is the median of each monster's mean save bonus (all six saves). A median of " +
            "an even count is the mean of the middle two, so a value can end in .5.",
            "**~ marks an interpolated row**: the SRD has no monster of that CR in that edition, so each value is interpolated " +
            "between the nearest CRs that have one (or taken from the nearest at either end). It is not a census; \"—\" means " +
            "no monster of the row has that value.",
            "**What the data says.** Against the DMG 2014 targets, the SRD's monsters have fewer hit points at nearly every CR, " +
            "higher attack bonuses from CR 1 up, and about the DMG's AC (lower at CR 1/2 and below, higher from CR 20). The 2024 " +
            "stat blocks' AC is the same as 2014's or higher at most CRs.",
            "The medians come from the stat blocks as this server normalizes them for balance_simulate (rules_get with format " +
            "\"combatant\" shows one), and a test recomputes every cell from the shipped data, so the table cannot drift from it. " +
            "The DMG columns are the 2014 design targets (`" + UriPrefix + "monster-stats-by-cr-2014`), not SRD text.");
    }

    /// <summary>
    /// Character Advancement (SRD 5.1 "Beyond 1st Level", SRD 5.2 "Character Creation", CC-BY): one row per level with its
    /// XP and proficiency bonus, from <see cref="Advancement.Rows"/>. Its notes say how a level is reached, that a class's
    /// own table gives its features, and where a sheet's XP is tracked; they quote no rule beyond the table.
    /// </summary>
    private static string CharacterAdvancement(RulesTable table) => Page(
        table,
        SrdMarkdownText.Table(
            ["Level", "Experience Points", "Proficiency Bonus"],
            Advancement.Rows.Select(row => (IReadOnlyList<string>)[Number(row.Level), Number(row.Xp), SrdMarkdownText.Signed(row.ProficiencyBonus)])),
        "\"When your XP total equals or exceeds a number in the Experience Points column, you reach the corresponding level\" " +
        "(SRD 5.2). A class's own table (rules_get, e.g. name \"Wizard\") gives what each level brings; a level counts every " +
        "class a character has.",
        "campaign_character keeps a sheet's XP (action \"xp\") and says when a level is due; level_up applies one.");

    // 14.5 → "14.5", 12 → "12", an interpolated 13.67 → "13.67".
    private static string Median(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string SignedMedian(double value) =>
        value < 0 ? "−" + (-value).ToString("0.00", CultureInfo.InvariantCulture) : "+" + value.ToString("0.00", CultureInfo.InvariantCulture);

    private static IEnumerable<int> Levels() => Enumerable.Range(EncounterLimits.MinLevel, EncounterLimits.MaxLevel - EncounterLimits.MinLevel + 1);

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Times(decimal multiplier) => "× " + multiplier.ToString("0.#", CultureInfo.InvariantCulture);
}

/// <summary>A rules table: its URI slug, what it is and where it comes from, the names it answers to, and its page.</summary>
internal sealed record RulesTable(
    string Slug,
    string Title,
    string Edition,
    string Source,
    string Description,
    IReadOnlyList<string> Names,
    Func<RulesTable, string> Renderer)
{
    public string Uri => RulesTables.UriPrefix + Slug;

    public string Render() => Renderer(this);
}
