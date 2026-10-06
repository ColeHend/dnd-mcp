using DndMcp.Formatting;
using DndMcp.Hosting;
using DndMcp.IntegrationTests.Infrastructure;
using DndMcp.Repository.Srd;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: every rules table is a <c>rules://tables/&lt;slug&gt;</c> resource and the same text through
/// <c>rules_get</c> (by URI and by each of its names), every one names its source (the 2014 ones as the DMG, not SRD
/// 5.1), and no table name can hide an SRD entry of the same name.
/// </summary>
public sealed class RulesTablesTests : IClassFixture<McpServerHarness>
{
    private readonly McpServerHarness _server;

    public RulesTablesTests(McpServerHarness server)
    {
        _server = server;
    }

    public static TheoryData<string> Slugs => new(RulesTables.All.Select(t => t.Slug));

    public static TheoryData<string, string> Names
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var table in RulesTables.All)
            {
                foreach (var name in table.Names)
                {
                    data.Add(table.Slug, name);
                }
            }

            return data;
        }
    }

    private async Task<string> Get(Dictionary<string, object?> arguments) =>
        _server.SuccessText(await _server.Client.CallToolAsync("rules_get", arguments));

    [Theory]
    [MemberData(nameof(Slugs))]
    public async Task Resource_EveryTable_IsTheSameTextRulesGetReturns(string slug)
    {
        var uri = RulesTables.UriPrefix + slug;
        var resource = await _server.Client.ReadResourceAsync(uri);
        var text = Assert.IsType<TextResourceContents>(Assert.Single(resource.Contents));

        Assert.Equal("text/markdown", text.MimeType);
        Assert.Equal(await Get(new() { ["ref"] = uri }), text.Text);
        Assert.Contains($"`{uri}`", text.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("cr-xp", 34)]
    [InlineData("xp-budget-2024", 20)]
    [InlineData("xp-thresholds-2014", 20)]
    [InlineData("encounter-multipliers-2014", 6)]
    [InlineData("adventuring-day-xp-2014", 20)]
    [InlineData("monster-stats-by-cr-2014", 34)]
    [InlineData("monster-stats-by-cr-empirical", 34)]
    [InlineData("dpr-targets-by-level", 20)]
    [InlineData("character-advancement", 20)]
    public async Task RulesGet_EveryTable_HasEveryRow(string slug, int rows)
    {
        var text = await Get(new() { ["ref"] = RulesTables.UriPrefix + slug });
        var tableRows = text.Split('\n').Where(l => l.StartsWith("| ", StringComparison.Ordinal)).ToList();

        Assert.Equal(rows + 1, tableRows.Count);
    }

    [Theory]
    // gwf-expected-values: d4–d12, then 6 weapon dice × (plain, GWF 2014, GWF 2024). aoe-targets: 5 shapes, then 6 spells.
    [InlineData("gwf-expected-values", 5, 18)]
    [InlineData("aoe-targets", 5, 6)]
    public async Task RulesGet_TwoTablePage_HasEveryRowOfBoth(string slug, int first, int second)
    {
        var text = await Get(new() { ["ref"] = RulesTables.UriPrefix + slug });
        var tableRows = text.Split('\n').Where(l => l.StartsWith("| ", StringComparison.Ordinal)).ToList();

        Assert.Equal(first + 1 + second + 1, tableRows.Count);
    }

    [Theory]
    [InlineData("xp-thresholds-2014", "| 3 | 75 | 150 | 225 | 400 |")]
    [InlineData("encounter-multipliers-2014", "| 15+ | × 4 | × 5 | × 3 |")]
    [InlineData("encounter-multipliers-2014", "| 1 | × 1 | × 1.5 | × 0.5 |")]
    [InlineData("xp-budget-2024", "| 20 | 6,400 | 13,200 | 22,000 |")]
    [InlineData("cr-xp", "| 0 | 0 or 10 | +2 |")]
    [InlineData("cr-xp", "| 1/8 | 25 | +2 |")]
    [InlineData("cr-xp", "| 30 | 155,000 | +9 |")]
    [InlineData("adventuring-day-xp-2014", "| 5 | 3,500 |")]
    [InlineData("monster-stats-by-cr-2014", "| 0 | +2 | ≤ 13 | 1–6 | ≤ +3 | 0–1 | ≤ 13 |")]
    [InlineData("monster-stats-by-cr-2014", "| 25 | +8 | 19 | 581–625 | +12 | 213–230 | 21 |")]
    // The CR = level row, the typical save, both reference curves (contract §8.11) and the warlock's odds.
    [InlineData("dpr-targets-by-level", "| 1 | +2 | 13 | 85 | +3 | 13 | +0 | 7.08 | 6.30 | +5 (65%) |")]
    [InlineData("dpr-targets-by-level", "| 5 | +3 | 15 | 145 | +6 | 15 | +2 | 12.08 | 17.80 | +7 (65%) |")]
    [InlineData("dpr-targets-by-level", "| 9 | +4 | 16 | 205 | +7 | 16 | +4 | 17.08 | 20.50 | +9 (70%) |")]
    [InlineData("dpr-targets-by-level", "| 20 | +6 | 19 | 400 | +10 | 19 | +9 | 33.33 | 38.20 | +11 (65%) |")]
    // Research A2: d6 3.5 / 25/6 / 4; d12 6.5 / 22/3 / 6.75; Savage Attacker's best of two d12 is 1222/144.
    [InlineData("gwf-expected-values", "| d6 | 3.5 | 4.1667 | +0.6667 | 4 | +0.5 | 4.4722 |")]
    [InlineData("gwf-expected-values", "| d12 | 6.5 | 7.3333 | +0.8333 | 6.75 | +0.25 | 8.4861 |")]
    // Contract §8.4's Savage Attacker table: 1d8 4.5 → 5.8125, crit 9 → 10.8457 with the ruling; 2d6 GWF 2024 8 → 8.9105, 16 → 17.3.
    [InlineData("gwf-expected-values", "| 1d8 | — | 4.5 | 5.8125 | 9 | 10.3125 | 10.8457 |")]
    [InlineData("gwf-expected-values", "| 2d6 | 2024 | 8 | 8.9105 | 16 | 16.9105 | 17.3 |")]
    [InlineData("aoe-targets", "| sphere | radius | radius ÷ 5, rounded up (at least 1) |")]
    [InlineData("aoe-targets", "| Fireball | 20-ft sphere | 4 |")]
    [InlineData("aoe-targets", "| Lightning Bolt | 100-ft line | 4 |")]
    [InlineData("aoe-targets", "| Cone of Cold | 60-ft cone | 6 |")]
    // MonsterStatsEmpirical's pinned medians beside the DMG row (both editions have 41-42 CR 2 monsters).
    [InlineData("monster-stats-by-cr-empirical", "| 2 | 41 | 13 | 45 | +5 | 12 (14) | +0.50 | 42 | 13 | 45 | +5 | 12 (16) | +0.67 | 13 | 86–100 | +3 | 13 |")]
    // CR 19: one monster per edition, neither with a save DC.
    [InlineData("monster-stats-by-cr-empirical", "| 19 | 1 | 19 | 262 | +14 | — | +9.00 | 1 | 19 | 287 | +14 | — | +7.00 | 19 | 341–355 | +10 | 19 |")]
    // CR 18: no SRD monster in either edition, so every value is interpolated and marked, with no count.
    [InlineData("monster-stats-by-cr-empirical", "| 18 | — | ~19 | ~259 | ~+13.75 | ~20.67 | ~+8.50 | — | ~19 |")]
    // Character advancement (SRD 5.1 "Beyond 1st Level", SRD 5.2 "Character Creation"): the first, a tier's first, and the last row.
    [InlineData("character-advancement", "| 1 | 0 | +2 |")]
    [InlineData("character-advancement", "| 5 | 6,500 | +3 |")]
    [InlineData("character-advancement", "| 12 | 100,000 | +4 |")]
    [InlineData("character-advancement", "| 20 | 355,000 | +6 |")]
    public async Task RulesGet_Table_RendersTheDomainValues(string slug, string row)
    {
        Assert.Contains(row, await Get(new() { ["ref"] = RulesTables.UriPrefix + slug }), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("xp-thresholds-2014")]
    [InlineData("encounter-multipliers-2014")]
    [InlineData("adventuring-day-xp-2014")]
    public async Task RulesGet_2014EncounterTable_SaysItIsTheDmgNotSrd51(string slug)
    {
        var line = (await Get(new() { ["ref"] = RulesTables.UriPrefix + slug })).Split('\n')[2];

        Assert.StartsWith("*2014 rules · Dungeon Master's Guide (2014), p", line, StringComparison.Ordinal);
        Assert.Contains("also in the free 2014 Basic Rules; not in SRD 5.1", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dpr-targets-by-level", "not in either SRD")]
    [InlineData("dpr-targets-by-level", "The Finished Book (tomedunn), not a DMG table")]
    [InlineData("dpr-targets-by-level", "community conventions")]
    [InlineData("aoe-targets", "Dungeon Master's Guide (2014), p. 249; not in either SRD")]
    public async Task RulesGet_DprTable_NamesItsNonSrdSources(string slug, string source)
    {
        // A model quoting "the DMG's typical save bonus" or "the official DPR target" would be repeating an invention.
        var line = (await Get(new() { ["ref"] = RulesTables.UriPrefix + slug })).Split('\n')[2];

        Assert.Contains(source, line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RulesGet_DprTargets_WarlockColumnIsThePublishedCurve()
    {
        // Contract §8.11, computed by the engine from the preset rather than typed in: the table and the tools cannot differ.
        double[] published = [6.30, 8.25, 8.25, 8.90, 17.80, 17.80, 17.80, 19.10, 20.50, 19.10, 28.65, 28.65, 28.65, 28.65, 28.65, 28.65, 38.20, 38.20, 38.20, 38.20];
        var rows = (await Get(new() { ["ref"] = RulesTables.UriPrefix + "dpr-targets-by-level" }))
            .Split('\n').Where(l => l.StartsWith("| ", StringComparison.Ordinal)).Skip(1).Select(l => l.Split(" | ")).ToList();

        Assert.Equal(published.Select(p => p.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)), rows.Select(r => r[8]));
    }

    [Fact]
    public async Task RulesGet_EmpiricalMonsterStats_SaysItIsComputedFromTheSrdAndTheAttributionSaysSo()
    {
        // Derived from SRD data rather than copied, beside a DMG table: both facts are stated where the table is and in the
        // attribution, so a model quotes neither the medians as SRD text nor the DMG columns as SRD.
        var line = (await Get(new() { ["ref"] = RulesTables.UriPrefix + RulesTables.EmpiricalSlug })).Split('\n')[2];
        var attribution = await Get(new() { ["ref"] = "rules://attribution" });

        Assert.Contains("medians computed by this server (not a table in either SRD)", line, StringComparison.Ordinal);
        Assert.Contains("the DMG columns: Dungeon Master's Guide (2014), pp. 274–275", line, StringComparison.Ordinal);
        Assert.Contains(
            "`rules://tables/monster-stats-by-cr-empirical` is computed by this server from the SRD 5.1 and SRD 5.2.1 monster stat blocks",
            attribution, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RulesGet_EmpiricalMonsterStats_EveryRowMatchesTheDomainTable()
    {
        // Each cell the page prints for a census row is MonsterStatsEmpirical's own value, never retyped.
        var rows = (await Get(new() { ["ref"] = RulesTables.UriPrefix + RulesTables.EmpiricalSlug }))
            .Split('\n').Where(l => l.StartsWith("| ", StringComparison.Ordinal)).Skip(1).Select(l => l.Split(" | ")).ToList();

        foreach (var edition in new[] { ("2014", 1), ("2024", 7) })
        {
            foreach (var data in DndMcp.Domain.Encounters.MonsterStatsEmpirical.Rows(edition.Item1))
            {
                var row = rows.Single(r => r[0] == "| " + data.ChallengeRating);
                Assert.Equal(data.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), row[edition.Item2]);
                Assert.Equal(data.ArmorClass.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), row[edition.Item2 + 1]);
                Assert.Equal(data.HitPoints.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), row[edition.Item2 + 2]);
            }
        }
    }

    [Fact]
    public async Task RulesGet_CharacterAdvancement_EveryRowIsTheDomainTableTheSheetsUse()
    {
        // The page and campaign_character's level-due reminders read one table (Advancement), never two typings of it.
        var rows = (await Get(new() { ["ref"] = RulesTables.UriPrefix + RulesTables.AdvancementSlug }))
            .Split('\n').Where(l => l.StartsWith("| ", StringComparison.Ordinal)).Skip(1).ToList();

        Assert.Equal(
            DndMcp.Domain.Characters.Advancement.Rows.Select(r =>
                $"| {r.Level.ToString(System.Globalization.CultureInfo.InvariantCulture)} | {r.Xp.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} | +{r.ProficiencyBonus.ToString(System.Globalization.CultureInfo.InvariantCulture)} |"),
            rows);
    }

    [Fact]
    public async Task RulesGet_CharacterAdvancement_NamesBothSrdChaptersAndQuotesTheRule()
    {
        // Contract D21: the one part of the 2024 Character Creation chapter this server serves, quoted (CC-BY) from both SRDs.
        var text = await Get(new() { ["name"] = "Character Advancement" });
        var line = text.Split('\n')[2];

        Assert.StartsWith("# Character Advancement\n", text, StringComparison.Ordinal);
        Assert.Equal(
            "*2014 and 2024 rules · SRD 5.1 \"Beyond 1st Level\" (2014) and SRD 5.2 \"Character Creation\" › Level Advancement (2024), CC-BY-4.0; " +
            "the same in both editions · `rules://tables/character-advancement`*",
            line);
        Assert.Contains(
            "\"When your XP total equals or exceeds a number in the Experience Points column, you reach the corresponding level\" (SRD 5.2).", text,
            StringComparison.Ordinal);
        Assert.Contains("campaign_character keeps a sheet's XP (action \"xp\")", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RulesGet_MonsterStats_SaysNeitherSrdHasIt()
    {
        var line = (await Get(new() { ["ref"] = RulesTables.UriPrefix + "monster-stats-by-cr-2014" })).Split('\n')[2];

        Assert.Contains("not in either SRD, and the 2024 DMG has no equivalent", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attribution_NamesEveryTableThatIsNotSrdText()
    {
        // The CC-BY statements cover SRD text only; each DMG table must be named as outside them.
        var attribution = await Get(new() { ["ref"] = "rules://attribution" });
        var notSrd = RulesTables.All.Where(t => !t.Source.StartsWith("SRD", StringComparison.Ordinal)).Select(t => t.Uri).ToList();

        Assert.Equal(6, notSrd.Count);
        Assert.All(notSrd, uri => Assert.Contains($"`{uri}`", attribution, StringComparison.Ordinal));
        Assert.Contains("## Not SRD text", attribution, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task RulesGet_EveryTableName_ReturnsThatTable(string slug, string name)
    {
        var byName = await Get(new() { ["name"] = name.ToUpperInvariant(), ["edition"] = "2014" });

        Assert.Equal(await Get(new() { ["ref"] = RulesTables.UriPrefix + slug }), byName);
    }

    public static TheoryData<string> NamesOnly => new(RulesTables.All.SelectMany(t => t.Names));

    [Theory]
    [MemberData(nameof(NamesOnly))]
    public async Task TableName_NoSrdEntryInEitherEdition_AnswersToIt(string name)
    {
        // rules_get checks table names before the index, so a table named like an SRD entry would hide that entry.
        var index = await _server.Services.GetRequiredService<SrdIndexService>().GetIndexAsync(null, CancellationToken.None);

        Assert.All(SrdEdition.All, edition => Assert.Empty(index.FindByName(name, edition).Matches));
    }

    [Theory]
    [InlineData("rules://tables")]
    [InlineData("rules://tables/")]
    [InlineData(" `rules://tables` ")]
    public async Task RulesGet_TablesIndex_ListsEveryTable(string uri)
    {
        var text = await Get(new() { ["ref"] = uri });

        Assert.StartsWith("# Rules tables", text, StringComparison.Ordinal);
        Assert.All(RulesTables.All, t => Assert.Contains($"- `{t.Uri}`: {t.Title}", text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RulesGet_UnknownTable_IsAnErrorListingTheTables()
    {
        var result = await _server.Client.CallToolAsync("rules_get", new Dictionary<string, object?> { ["ref"] = "rules://tables/xp-budget" });

        Assert.Equal(
            "An error occurred invoking 'rules_get': No rules table is at `rules://tables/xp-budget`. The tables: " +
            string.Join(", ", RulesTables.All.Select(t => $"`{t.Uri}`")) + "; `rules://tables` describes them.",
            _server.ErrorText(result));
    }

    [Fact]
    public async Task RulesGet_TableNameWithKind_IsAnSrdLookupNotATable()
    {
        // kind narrows an SRD name; a caller who gives one is not asking for a table.
        var result = await _server.Client.CallToolAsync(
            "rules_get", new Dictionary<string, object?> { ["name"] = "Adventuring Day", ["kind"] = "rule" });

        Assert.DoesNotContain("# Adventuring Day XP", _server.SingleText(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Experience Points", "2024")]
    [InlineData("Proficiency Bonus", "2014")]
    [InlineData("Encounter", "2024")]
    public async Task RulesGet_SrdNameThatStartsATableName_IsTheSrdEntry(string name, string edition)
    {
        var text = _server.SuccessText(await _server.Client.CallToolAsync("rules_get", new Dictionary<string, object?> { ["name"] = name, ["edition"] = edition }));

        Assert.StartsWith($"# {name}\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RulesGet_TablesIndex_DescribesEveryTable()
    {
        var text = _server.SuccessText(await _server.Client.CallToolAsync("rules_get", new Dictionary<string, object?> { ["ref"] = "rules://tables" }));

        Assert.All(RulesTables.All, t => Assert.Contains($"- `{t.Uri}`: {t.Title} ({t.Edition}). {t.Description}\n", text, StringComparison.Ordinal));
    }
}
