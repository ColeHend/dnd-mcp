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
    public async Task RulesGet_EveryTable_HasEveryRow(string slug, int rows)
    {
        var text = await Get(new() { ["ref"] = RulesTables.UriPrefix + slug });
        var tableRows = text.Split('\n').Where(l => l.StartsWith("| ", StringComparison.Ordinal)).ToList();

        Assert.Equal(rows + 1, tableRows.Count);
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

        Assert.Equal(4, notSrd.Count);
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
