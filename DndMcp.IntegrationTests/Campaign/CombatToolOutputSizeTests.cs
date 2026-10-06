using System.Text.RegularExpressions;
using DndMcp.Domain.Dice;
using DndMcp.Formatting.Campaign;
using DndMcp.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Invariant: a typical <c>combat</c> step stays under 8,000 characters, and a step or state too long for the
/// 24,000-character cap gives up its initiative table's rows (then its "what changed" lines), never a reminder or its
/// resolving call, with a note that says how many rows are shown and points at <c>combat state</c> only when state does
/// show the whole table (contract §0: one "largest" and one "typical" size test per new tool; stage-4 ruling 1). Every
/// step prints the table, so a fight this size is where a missing cut would show.
/// </summary>
public sealed class CombatToolOutputSizeTests : IAsyncLifetime
{
    private readonly McpServerHarness _server = McpServerHarness.WithExtraTools(builder =>
        builder.Services.AddSingleton<IDiceRoller>(new ScriptedDiceRoller()));

    public async Task InitializeAsync()
    {
        await _server.InitializeAsync();
        await CharacterToolSetup.CreateDeepAsync(_server);
    }

    public Task DisposeAsync() => _server.DisposeAsync();

    private Task<string> Combat(string argumentsJson) => ScenarioCalls.Call(_server, "combat", argumentsJson);

    [Fact]
    public async Task Step_TypicalFight_StaysUnder8000Characters()
    {
        await Combat("""{"action": "start", "name": "Reef", "campaign": "deep", "combatants": [{"srd": "2024/monster/ogre", "count": 3}, {"srd": "2024/monster/aboleth"}]}""");
        await Combat("""
            {"action": "initiative", "rolls": [{"combatant": "bjorn-mountainfell", "face": 15}, {"combatant": "kaz", "total": 12},
              {"combatant": "ogre", "total": 10}, {"combatant": "aboleth", "total": 8}]}
            """);
        await Combat("""{"action": "condition", "targets": ["bjorn-mountainfell"], "add": ["Rage"], "effect": {"resist": ["all"], "except": ["psychic"]}}""");
        await Combat("""{"action": "condition", "targets": ["ogre-2"], "add": ["frightened"], "source": "bjorn-mountainfell"}""");

        var text = await Combat("""{"action": "damage", "targets": ["ogre", "ogre-3"], "amount": 20, "damage_type": "slashing", "source": "bjorn-mountainfell"}""");

        Assert.InRange(text.Length, 500, 7_999);
        Assert.StartsWith("# Reef — round 1 · Björn Mountainfell's turn\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Step_LargestFight_StaysWithinTheCap_CuttingTableRowsNeverReminders()
    {
        var a = new string('A', 77);
        var b = new string('B', 77);
        await Combat($$"""
            {"action": "start", "name": "{{new string('F', 80)}}", "campaign": "deep", "add_party": false, "combatants": [
              {"name": "{{a}}", "count": 20, "hp": 4000, "ac": 20, "init_bonus": 1},
              {"name": "{{b}}", "count": 20, "hp": 4000, "ac": 20, "init_bonus": 2, "side": "ally"}]}
            """);
        var rolls = string.Join(", ", new[] { a, b }.Select((n, i) => $$"""{"combatant": "{{n}}", "total": {{20 - i}}}"""));
        await Combat($$"""{"action": "initiative", "rolls": [{{rolls}}]}""");
        foreach (var n in Enumerable.Range(1, 5))
        {
            var effect = $"{new string((char)('a' + n), 70)} {n}";
            await Combat($$"""{"action": "condition", "targets": ["{{a}}*", "{{b}}*"], "add": ["{{effect}}"], "source": "{{b}}"}""");
        }

        await Combat($$"""{"action": "concentration", "targets": ["{{b}}"], "spell": "Bless", "duration": "1 minute"}""");
        var text = await Combat($$"""{"action": "damage", "targets": ["{{a}}*", "{{b}}"], "amount": 1}""");
        var state = await Combat("""{"action": "state"}""");

        // The table is longer than any one result: the step and state both cut its rows (saying how many are shown), and
        // neither cuts a reminder; the step does not send the reader to a state that could not show the rest either.
        Assert.InRange(text.Length, 20_000, CampaignMarkdownText.MaxChars);
        Assert.StartsWith("# " + new string('F', 80) + " — round 1 · ", text, StringComparison.Ordinal);
        Assert.Matches(@"\n_Initiative table cut to keep this result within 24,000 characters: \d+ rows of 40 shown; the whole table is longer than one " +
                       @"result holds, and combat state shows as many rows as fit\._\n\n## Reminders\n", text);
        Assert.Contains("concentration save DC 10 to keep Bless", text, StringComparison.Ordinal);
        Assert.EndsWith("\"total\": …, \"campaign\": \"deep\"}\n", text, StringComparison.Ordinal);
        Assert.Equal(21, Regex.Matches(text, "^- [AB]{77}( \\d+)?: 1 untyped", RegexOptions.Multiline).Count);

        Assert.InRange(state.Length, 20_000, CampaignMarkdownText.MaxChars);
        Assert.Matches(@"\n_Initiative table cut to keep this result within 24,000 characters: \d+ rows of 40 shown; every combatant is still in the fight\._\n", state);
        Assert.DoesNotContain("Output cut", text + state, StringComparison.Ordinal);
    }

    /// <summary>
    /// The checker's reproduction (stage-4 check, finding 1): 39 combatants with 70-character names under five effects each,
    /// three of them concentrating, and one damage call over 22 of them. The step passes the cap: its table rows go, never a
    /// concentration save's reminder or its call, and the note's "combat state shows the whole table" is true (state shows
    /// every row, uncut).
    /// </summary>
    [Fact]
    public async Task Step_ThirtyNineCombatantsWithPendingSaves_KeepsEveryReminderAndCallAndStateShowsTheWholeTable()
    {
        var a = "A" + new string('a', 69);
        var b = "B" + new string('b', 69);
        await Combat($$"""
            {"action": "start", "name": "Big fight", "campaign": "deep", "add_party": false, "combatants": [
              {"name": "{{a}}", "count": 20, "hp": 400, "ac": 15, "side": "ally"},
              {"name": "{{b}}", "count": 19, "hp": 400, "ac": 15}]}
            """);
        await Combat($$"""{"action": "initiative", "rolls": [{"combatant": "{{a}}", "total": 15}, {"combatant": "{{b}}", "total": 10}]}""");
        foreach (var n in Enumerable.Range(1, EffectsEach))
        {
            await Combat($$"""{"action": "condition", "targets": ["{{a}}*", "{{b}}*"], "add": ["{{EffectName(n)}}"], "source": "{{a}}"}""");
        }

        foreach (var target in new[] { a, a + " 2", a + " 3" })
        {
            await Combat($$"""{"action": "concentration", "targets": ["{{target}}"], "spell": "Bless", "duration": "1 minute"}""");
        }

        var text = await Combat($$"""{"action": "damage", "targets": ["{{a}}", "{{a}} 2", "{{a}} 3", "{{b}}*"], "amount": 12}""");
        var state = await Combat("""{"action": "state"}""");

        Assert.InRange(text.Length, 20_000, CampaignMarkdownText.MaxChars);
        Assert.Matches(@"\n_Initiative table cut to keep this result within 24,000 characters: \d+ rows of 39 shown; combat state shows the whole table\._\n", text);
        var reminders = text[text.IndexOf("\n## Reminders\n", StringComparison.Ordinal)..];
        foreach (var who in new[] { a, a + " 2", a + " 3" })
        {
            Assert.True(Regex.IsMatch(reminders, $"\\n- {who}: concentration save DC 10 to keep Bless \\(Con save [^)]+\\) " +
                                                 "combat \\{\"action\": \"concentration\", \"targets\": \\[\"[a-z0-9-]+\"\\], \"total\": …, \"campaign\": \"deep\"\\}\\n"),
                reminders);
        }

        Assert.Equal(22, Regex.Matches(text, "^- [AB][ab]{69}( \\d+)?: 12 untyped", RegexOptions.Multiline).Count);
        Assert.True(state.Length <= CampaignMarkdownText.MaxChars, $"{state.Length} characters");
        Assert.DoesNotContain("cut", state, StringComparison.Ordinal);
        Assert.Equal(39, Regex.Matches(state, "^\\| (▶)? \\| \\d+ \\| ", RegexOptions.Multiline).Count);
    }

    private const int EffectsEach = 5;

    private static string EffectName(int n) => $"Effect {n} " + new string((char)('a' + n), 3);
}
