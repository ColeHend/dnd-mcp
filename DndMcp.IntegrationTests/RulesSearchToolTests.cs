using System.Text.Json;
using System.Text.RegularExpressions;
using DndMcp.IntegrationTests.Infrastructure;
using Xunit;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: <c>rules_search</c> through the real client returns ranked SRD entries as a numbered list in which every
/// entry names its kind and edition and carries a ref that <c>rules_get</c> accepts verbatim; it says when it fell back
/// to matching any word, and a search that finds nothing is an answer with next steps, not an error.
///
/// <para>
/// The model acts on this text: it copies a ref into <c>rules_get</c>, and it reads "matched some of the words" as a
/// warning that the hits are loose. A ref printed without its edition would fetch the default edition's entry, a
/// fallback reported as a precise match would be quoted as the answer, and an error for zero hits would make the model
/// retry the same call. Ranking and snippets belong to <c>SrdIndex</c> (pinned in DndMcp.Tests); these tests pin what
/// the tool adds: argument handling, the edition and kind filters as the model spells them, and the shape of the text.
/// </para>
/// </summary>
public sealed partial class RulesSearchToolTests : IClassFixture<McpServerHarness>
{
    private const string Tool = "rules_search";
    private const string Prefix = "An error occurred invoking 'rules_search': ";

    private readonly McpServerHarness _server;

    public RulesSearchToolTests(McpServerHarness server)
    {
        _server = server;
    }

    // "3. **Fireball** — spell · 2024 · `2024/spell/fireball`"
    [GeneratedRegex(@"^(?<n>\d+)\. \*\*(?<name>[^*]+)\*\* — (?<kind>[a-z-]+) · (?<edition>\d{4}) · `(?<ref>(?<refEdition>\d{4})/(?<refKind>[a-z-]+)/[^`]+)`$")]
    private static partial Regex HitLineRegex();

    [Theory]
    [InlineData("fireball", "2024", "2024/spell/fireball")]
    [InlineData("fireball", "2014", "2014/spell/fireball")]
    [InlineData("Grappled", "2014", "2014/condition/grappled")]
    // By alias: 2014's name for the renamed 2024 monster.
    [InlineData("thug", "2024", "2024/monster/tough")]
    [InlineData("tough", "2014", "2014/monster/thug")]
    public async Task CallTool_EntryName_RanksThatEntryFirst(string query, string edition, string expectedRef)
    {
        var hits = Hits(await SearchAsync(new() { ["query"] = query, ["edition"] = edition }));

        Assert.Equal(expectedRef, hits[0].Ref);
    }

    [Fact]
    public async Task CallTool_Fireball_HeaderCountsEntriesAndNamesTheEdition()
    {
        var text = await SearchAsync(new() { ["query"] = "fireball", ["limit"] = 3 });

        var lines = text.Split('\n');
        Assert.Equal("**3 entries** for \"fireball\" in the 2024 SRD:", lines[0]);
        Assert.Equal("1. **Fireball** — spell · 2024 · `2024/spell/fireball`", lines[2]);
        Assert.Equal(
            "Showing the first 3; there may be more (limit goes up to 50). Pass a ref to rules_get for the whole entry.",
            lines[^1]);
    }

    [Fact]
    public async Task CallTool_OneHit_SaysEntryInTheSingularAndOffersNoMore()
    {
        var text = await SearchAsync(new() { ["query"] = "thug" });

        Assert.StartsWith("**1 entry** for \"thug\" in the 2024 SRD:\n\n1. **Tough** — monster · 2024 · `2024/monster/tough`\n", text, StringComparison.Ordinal);
        Assert.EndsWith("\n\nPass a ref to rules_get for the whole entry.", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2024", new[] { "2024" })]
    [InlineData("2014", new[] { "2014" })]
    [InlineData("both", new[] { "2014", "2024" })]
    [InlineData("Both", new[] { "2014", "2024" })]
    [InlineData(" 2014 ", new[] { "2014" })]
    public async Task CallTool_Edition_EveryHitIsFromThoseEditionsAndItsRefSaysSo(string edition, string[] expected)
    {
        var hits = Hits(await SearchAsync(new() { ["query"] = "fire", ["edition"] = edition, ["limit"] = 50 }));

        Assert.Equal(50, hits.Count);
        Assert.Equal(expected, hits.Select(h => h.Edition).Distinct().Order());
        Assert.All(hits, h => Assert.StartsWith(h.Edition + "/" + h.Kind + "/", h.Ref, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CallTool_EditionOmitted_Searches2024Only()
    {
        var hits = Hits(await SearchAsync(new() { ["query"] = "fire", ["limit"] = 50 }));

        Assert.All(hits, h => Assert.Equal("2024", h.Edition));
    }

    [Fact]
    public async Task CallTool_BothEditions_ShowsTheSameSpellFromEach()
    {
        var hits = Hits(await SearchAsync(new() { ["query"] = "fireball", ["edition"] = "both", ["limit"] = 2 }));

        Assert.Equal(["2014/spell/fireball", "2024/spell/fireball"], hits.Select(h => h.Ref).Order());
    }

    [Theory]
    [InlineData("2024", new[] { "monster" }, new[] { "monster" })]
    [InlineData("2024", new[] { "Spells", "MAGIC ITEM" }, new[] { "spell", "magic-item" })]
    // race is the 2014 name for what 2024 calls species; each edition gets its own.
    [InlineData("2024", new[] { "race" }, new[] { "species" })]
    [InlineData("2014", new[] { "species" }, new[] { "race" })]
    [InlineData("both", new[] { "races" }, new[] { "race", "species" })]
    // A kind only one edition has is still a valid filter across both.
    [InlineData("both", new[] { "poison", "spell" }, new[] { "poison", "spell" })]
    public async Task CallTool_Kinds_EveryHitIsOfThoseKindsAsTheEditionNamesThem(string edition, string[] kinds, string[] expectedKinds)
    {
        var text = await SearchAsync(new() { ["query"] = "e*", ["edition"] = edition, ["kinds"] = kinds, ["limit"] = 50 });
        var hits = Hits(text);

        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Contains(h.Kind, expectedKinds));
        Assert.Contains($" (kinds: {string.Join(", ", expectedKinds)})", text.Split('\n')[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"query":"fire","kinds":[]}""")]
    [InlineData("""{"query":"fire","kinds":null}""")]
    public async Task CallTool_EmptyOrNullKinds_SearchesEverything(string argumentsJson)
    {
        var text = _server.SuccessText(await _server.CallToolJsonAsync(Tool, argumentsJson));

        Assert.DoesNotContain("(kinds:", text, StringComparison.Ordinal);
        Assert.True(Hits(text).Select(h => h.Kind).Distinct().Count() > 1, text);
    }

    [Fact]
    public async Task CallTool_NoEntryHasEveryWord_FallsBackToAnyWordAndSaysSo()
    {
        var text = await SearchAsync(new() { ["query"] = "fireball zzqx" });

        Assert.StartsWith(
            "**10 entries** for \"fireball zzqx\" in the 2024 SRD. No entry has every word, so these match some of them:\n\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains("2024/spell/fireball", Hits(text).Select(h => h.Ref));
    }

    [Fact]
    public async Task CallTool_EveryWordMatches_DoesNotClaimAFallback()
    {
        var text = await SearchAsync(new() { ["query"] = "grapple escape" });

        Assert.DoesNotContain("some of them", text, StringComparison.Ordinal);
        Assert.EndsWith(" in the 2024 SRD:", text.Split('\n')[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_Snippet_IsIndentedUnderItsEntryWithMatchesInBold()
    {
        var lines = (await SearchAsync(new() { ["query"] = "grapple escape", ["limit"] = 50 })).Split('\n');

        // Every hit line is followed by its snippet, indented to the marker so markdown keeps it inside the list item.
        var hitLines = lines.Select((line, i) => (line, i)).Where(p => HitLineRegex().IsMatch(p.line)).ToList();
        Assert.NotEmpty(hitLines);
        foreach (var (line, i) in hitLines)
        {
            var indent = new string(' ', line.IndexOf(' ') + 1);
            Assert.StartsWith(indent, lines[i + 1], StringComparison.Ordinal);
            Assert.NotEqual(' ', lines[i + 1][indent.Length]);
        }

        Assert.Contains(lines, l => l.Contains("**escape**", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(50)]
    public async Task CallTool_Limit_ReturnsExactlyThatManyNumberedInOrder(int limit)
    {
        var hits = Hits(await SearchAsync(new() { ["query"] = "fire", ["limit"] = limit }));

        Assert.Equal(Enumerable.Range(1, limit), hits.Select(h => h.Number));
    }

    [Fact]
    public async Task CallTool_FewerHitsThanLimit_DoesNotSayThereMayBeMore()
    {
        var text = await SearchAsync(new() { ["query"] = "fireball", ["edition"] = "2014", ["kinds"] = new[] { "spell" } });

        Assert.Equal(4, Hits(text).Count);
        Assert.DoesNotContain("Showing the first", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_FiftyHits_StaysWellUnderTheOutputBudget()
    {
        // Claude Code warns at 10,000 tokens; the plan's budget for a typical result is 8,000 (about 4 characters each).
        var text = await SearchAsync(new() { ["query"] = "fire", ["edition"] = "both", ["limit"] = 50 });

        Assert.True(text.Length / 4 < 8_000, $"{text.Length} characters.");
    }

    [Fact]
    public async Task CallTool_NothingMatches_IsAnAnswerWithNextStepsNotAnError()
    {
        var result = await _server.Client.CallToolAsync(Tool, new Dictionary<string, object?> { ["query"] = "zzqx" });

        Assert.Equal(
            "No entries match \"zzqx\" in the 2024 SRD. Try edition \"both\" (the 2014 SRD may have it) or other words for the same thing.",
            _server.SuccessText(result));
    }

    [Theory]
    [InlineData("""{"query":"zzqx qqzz","kinds":["spell"],"edition":"both"}""",
        "No entries match \"zzqx qqzz\" in the 2014 and 2024 SRDs (kinds: spell). Try no kinds filter, fewer words or other words for the same thing.")]
    [InlineData("""{"query":"zzqxzz","edition":"2014"}""",
        "No entries match \"zzqxzz\" in the 2014 SRD. Try edition \"both\" (the 2024 SRD may have it) or a prefix such as zzqx*.")]
    [InlineData("""{"query":"zzqxzz*","edition":"both"}""",
        "No entries match \"zzqxzz*\" in the 2014 and 2024 SRDs. Try other words for the same thing.")]
    public async Task CallTool_NothingMatches_TipsFitWhatWasAsked(string argumentsJson, string expected)
    {
        var result = await _server.CallToolJsonAsync(Tool, argumentsJson);

        Assert.Equal(expected, _server.SuccessText(result));
    }

    [Theory]
    [InlineData("a:b \"c NEAR( -x")]
    [InlineData("fire OR water")]
    [InlineData("(fire")]
    [InlineData("^fire")]
    [InlineData("fire\" AND \"")]
    public async Task CallTool_QueryWithFtsSyntax_IsSearchedAsWords(string query)
    {
        // User text is never FTS5 syntax: every word is quoted. A leak surfaces as the generic error (an FTS5 syntax
        // error is not an input exception), which SuccessText reports with the server log.
        var result = await _server.Client.CallToolAsync(Tool, new Dictionary<string, object?> { ["query"] = query });

        Assert.StartsWith("**", _server.SuccessText(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("\" ) ( *")]
    [InlineData("   ")]
    public async Task CallTool_QueryWithoutAWord_SaysSoWithAnExample(string query)
    {
        var result = await _server.Client.CallToolAsync(Tool, new Dictionary<string, object?> { ["query"] = query });

        Assert.Equal(
            Prefix + $"The query needs at least one word to search for (letters or digits); \"{query}\" has none. " +
            "Example: \"fireball\", \"grapple escape\" or a prefix like \"fire*\".",
            _server.ErrorText(result));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(51)]
    public async Task CallTool_LimitOutOfRange_ReturnsTheAcceptedRange(int limit)
    {
        var result = await _server.Client.CallToolAsync(Tool, new Dictionary<string, object?> { ["query"] = "fire", ["limit"] = limit });

        Assert.Equal(Prefix + $"limit must be between 1 and 50 (got {limit}).", _server.ErrorText(result));
    }

    [Theory]
    [InlineData("5e")]
    [InlineData("2024 rules")]
    [InlineData("2023")]
    public async Task CallTool_UnknownEdition_ListsTheEditions(string edition)
    {
        var result = await _server.Client.CallToolAsync(Tool, new Dictionary<string, object?> { ["query"] = "fire", ["edition"] = edition });

        Assert.Equal(Prefix + $"edition must be \"2014\", \"2024\" or \"both\" (got \"{edition}\").", _server.ErrorText(result));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task CallTool_BlankEdition_UsesTheDefault(string edition)
    {
        var text = await SearchAsync(new() { ["query"] = "fireball", ["edition"] = edition });

        Assert.All(Hits(text), h => Assert.Equal("2024", h.Edition));
    }

    [Theory]
    [InlineData("2014", new[] { "poison" }, "Kind 'poison' is not in the 2014 SRD; only the 2024 SRD has it. Pass edition 2024 (or both).")]
    [InlineData("2024", new[] { "spell", "dragons" },
        "Unknown kind 'dragons'. Kinds in the 2024 SRD: ability-score, alignment, background, class, condition, damage-type, " +
        "equipment, equipment-category, feat, feature, language, level, magic-item, magic-school, monster, poison, proficiency, " +
        "rule, skill, species, spell, subclass, subspecies, trait, weapon-mastery, weapon-property.")]
    [InlineData("2024", new[] { "" }, "The kind is empty. Kinds in the 2024 SRD: ability-score, alignment, background, class, " +
        "condition, damage-type, equipment, equipment-category, feat, feature, language, level, magic-item, magic-school, " +
        "monster, poison, proficiency, rule, skill, species, spell, subclass, subspecies, trait, weapon-mastery, weapon-property.")]
    public async Task CallTool_KindTheEditionLacks_SaysWhichKindsItHas(string edition, string[] kinds, string message)
    {
        var result = await _server.Client.CallToolAsync(
            Tool, new Dictionary<string, object?> { ["query"] = "fire", ["edition"] = edition, ["kinds"] = kinds });

        Assert.Equal(Prefix + message, _server.ErrorText(result));
    }

    [Theory]
    [InlineData("level")]
    [InlineData("levels")]
    public async Task CallTool_LevelKind_ExplainsLevelsAreFetchedNotSearched(string kind)
    {
        // Level records are left out of the full-text index, so this filter could only ever return nothing, which reads
        // as "no such level" rather than "wrong tool".
        var result = await _server.Client.CallToolAsync(
            Tool, new Dictionary<string, object?> { ["query"] = "fighter", ["kinds"] = new[] { kind } });

        Assert.Equal(
            Prefix + "Level records are not searchable (a Fighter 5 row would match every search). Get a class's level table " +
            "with rules_get name \"Fighter\", or one level with ref \"2014/level/fighter-5\".",
            _server.ErrorText(result));
    }

    [Fact]
    public async Task CallTool_EveryRefInTheResults_FetchesThatEntryWithRulesGet()
    {
        // The whole point of printing refs: each one, copied as is, is a valid rules_get call for that very entry.
        var hits = Hits(await SearchAsync(new() { ["query"] = "shield", ["edition"] = "both", ["limit"] = 20 }));

        foreach (var hit in hits)
        {
            var result = await _server.Client.CallToolAsync("rules_get", new Dictionary<string, object?> { ["ref"] = $"`{hit.Ref}`" });
            var text = _server.SuccessText(result);
            Assert.StartsWith($"# {hit.Name}\n", text, StringComparison.Ordinal);
            Assert.Contains($"`{hit.Ref}`", text.Split('\n')[1], StringComparison.Ordinal);
        }
    }

    private async Task<string> SearchAsync(Dictionary<string, object?> arguments)
    {
        var result = await _server.Client.CallToolAsync(Tool, arguments);
        return _server.SuccessText(result);
    }

    private static List<Hit> Hits(string text) =>
        text.Split('\n')
            .Select(line => HitLineRegex().Match(line))
            .Where(m => m.Success)
            .Select(m =>
            {
                Assert.Equal(m.Groups["edition"].Value, m.Groups["refEdition"].Value);
                Assert.Equal(m.Groups["kind"].Value, m.Groups["refKind"].Value);
                return new Hit(int.Parse(m.Groups["n"].Value), m.Groups["name"].Value, m.Groups["kind"].Value, m.Groups["edition"].Value, m.Groups["ref"].Value);
            })
            .ToList();

    private sealed record Hit(int Number, string Name, string Kind, string Edition, string Ref);
}
