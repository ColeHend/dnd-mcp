using DndMcp.Domain.Simulation;
using DndMcp.Formatting;
using DndMcp.IntegrationTests.Infrastructure;
using DndMcp.IntegrationTests.Srd;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Combatants;
using Xunit;
using Xunit.Abstractions;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: a <c>balance_simulate</c> result stays readable — a typical fight under 8,000 characters (about 2,000
/// tokens) and the largest fight the limits allow, with every optional section at its longest, under
/// <see cref="SimulationMarkdown.MaxChars"/> (24,000, about 6,000 tokens; Claude Code warns at 10,000 tokens).
///
/// <para>
/// The worst case is built, not guessed: the 40 combatants the limit allows, as 40 different entries (one table row
/// each), the SRD stat blocks with the most normalization warnings (the most warnings to condense), a dozen distinct
/// archetypes (the longest assumptions), monsters from the other edition than the fight's (a resolution note each),
/// a comparison, and a replay of a 100-round-cap fight (the longest log). If a section grows past its cap, this fails
/// before a user's context does.
/// </para>
/// </summary>
public sealed class SimulateOutputSizeTests : IClassFixture<McpServerHarness>
{
    private readonly McpServerHarness _server;
    private readonly ITestOutputHelper _output;

    public SimulateOutputSizeTests(McpServerHarness server, ITestOutputHelper output)
    {
        _server = server;
        _output = output;
    }

    [Theory]
    [InlineData("""{"party": PARTY, "enemies": [{"monster": "Ogre", "count": 3}], "seed": 1}""")]
    [InlineData("""{"party": PARTY, "enemies": [{"monster": "Adult Red Dragon"}], "seed": 1}""")]
    [InlineData("""{"party": PARTY, "enemies": [{"monster": "Lich"}, {"monster": "Zombie", "count": 6}], "seed": 1, "surprise": "party"}""")]
    [InlineData("""{"party": PARTY, "enemies": [{"monster": "Goblin", "count": 8}, {"monster": "Goblin Boss", "edition": "2014"}], "seed": 1, "enemy_hp": "roll"}""")]
    public async Task CallTool_TypicalFight_StaysUnder8000Characters(string template)
    {
        var text = await SimulateToolTests.SimulateAsync(_server, template.Replace("PARTY", SimulateToolTests.FighterParty("2024"), StringComparison.Ordinal));

        _output.WriteLine($"{text.Length} characters");
        Assert.True(text.Length < 8_000, $"{text.Length} characters:\n{text}");
    }

    [Fact]
    public async Task CallTool_LargestFightWithEveryOptionalSection_StaysUnderTheCeiling()
    {
        var monsters = MostWarned(27);
        var archetypes = new[] { "fighter", "barbarian", "paladin", "ranger", "rogue", "monk", "cleric", "druid", "wizard", "sorcerer", "warlock", "bard" };
        var party = new List<string>
        {
            $$"""{"name": "Fighter", "build": {{SimulateToolTests.Fighter("2024")}}, "hp": 44, "ac": 18}""",
        };
        party.AddRange(archetypes.Select((a, i) => $$"""{"archetype": "{{a}}", "level": {{20 - i}}, "edition": "{{(i % 2 == 0 ? "2024" : "2014")}}"}"""));
        party.AddRange(monsters.Take(7).Select(m => $$"""{"monster": "{{m.Ref}}"}"""));
        var enemies = monsters.Skip(7).Take(20).Select(m => $$"""{"monster": "{{m.Ref}}"}""").ToList();
        Assert.Equal(20, party.Count);
        Assert.Equal(20, enemies.Count);

        var text = await SimulateToolTests.SimulateAsync(
            _server,
            $$"""{"party": [{{string.Join(", ", party)}}], "enemies": [{{string.Join(", ", enemies)}}], "iterations": 100, "round_cap": 100, """ +
            "\"seed\": 99, \"replay\": 1, \"compare\": {\"member\": 1, \"feature\": {\"name\": \"Plus Two\", \"modifiers\": " +
            "[{\"kind\": \"to_hit\", \"amount\": 2}]}}}");

        _output.WriteLine($"{text.Length} characters");
        Assert.True(text.Length <= SimulationMarkdown.MaxChars, $"{text.Length} characters:\n{text}");
        Assert.Contains("### Compare: ", text, StringComparison.Ordinal);
        Assert.Contains("Summary of fight 1: ", text, StringComparison.Ordinal);
        Assert.Contains("Also warned: ", text, StringComparison.Ordinal);
        Assert.Contains("- … and ", text, StringComparison.Ordinal);
        Assert.EndsWith("```\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_FortyEntriesWithTheLongestNamesAndAReplay_StaysUnderTheCeiling()
    {
        // Names are up to 80 characters; 40 of them lengthen both the table and the replay's summary past what the
        // per-section caps allow for, so the result's final cap has to hold (and close the replay's code block).
        string Name(string side, int i) => $"{side} {i:00} " + new string('n', 80 - side.Length - 4);
        var party = Enumerable.Range(1, 20).Select(i =>
            $$"""{"name": "{{Name("Hero", i)}}", "build": {{SimulateToolTests.Fighter("2024")}}, "hp": 44, "ac": 18}""");
        var enemies = Enumerable.Range(1, 20).Select(i => $$"""{"name": "{{Name("Foe", i)}}", "monster": "Ogre"}""");

        var text = await SimulateToolTests.SimulateAsync(
            _server,
            $$"""{"party": [{{string.Join(", ", party)}}], "enemies": [{{string.Join(", ", enemies)}}], "iterations": 50, "round_cap": 100, "seed": 3, "replay": 2}""");

        _output.WriteLine($"{text.Length} characters");
        Assert.True(text.Length <= SimulationMarkdown.MaxChars, $"{text.Length} characters.");
        Assert.StartsWith("# Fight simulation: ", text, StringComparison.Ordinal);
        Assert.Equal(0, text.Split('\n').Count(l => l.StartsWith("```", StringComparison.Ordinal)) % 2);
    }

    /// <summary>The SRD stat blocks with the most warnings, both editions, most first (ties: the longer name first).</summary>
    private static IReadOnlyList<StatBlock> MostWarned(int count)
    {
        var root = VendoredSrdLookup.ContentRoot;
        var normalizer = new MonsterNormalizer(MonsterOverrides.Load(root), SpellOverlay.Load(root));
        return SrdEdition.All
            .SelectMany(e => VendoredSrdLookup.Instance.OfKind(e, SrdKinds.Monster))
            .Select(d => normalizer.Normalize(d, VendoredSrdLookup.Instance))
            .OrderByDescending(b => b.Warnings.Count)
            .ThenByDescending(b => b.Name.Length)
            .Take(count)
            .ToList();
    }
}
