using System.Text.Json;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using Xunit;

namespace DndMcp.Tests.Campaign;

/// <summary>
/// Invariant: a reveal rule's data is read only in its documented shape (forbidden_terms, forbidden_patterns,
/// preferred_terms, until, note), unknown keys are refused by name, it must forbid something, and it is active while any
/// until fact is not in play (always, with none). The Belmakor "no name, no timespan for the old king" rule is the
/// example.
/// </summary>
public sealed class RevealRuleDataTests
{
    private const string OldKing = """
        {"forbidden_terms": ["Keras"], "forbidden_patterns": ["<number>-year-old", "<number> years"],
         "preferred_terms": ["an old king"], "note": "No name, no timespan (Old King, Come Down)"}
        """;

    [Fact]
    public void Parse_TheOldKingRule_ReadsEveryField()
    {
        var rule = RevealRuleData.Parse(OldKing);

        Assert.Equal(["Keras"], rule.ForbiddenTerms);
        Assert.Equal(["<number>-year-old", "<number> years"], rule.ForbiddenPatterns);
        Assert.Equal(["an old king"], rule.PreferredTerms);
        Assert.Empty(rule.Until);
        Assert.Equal("No name, no timespan (Old King, Come Down)", rule.Note);
    }

    [Fact]
    public void ToRule_ScansAsAVocabularyRuleUnderItsSource()
    {
        var rule = RevealRuleData.Parse(OldKing).ToRule("rule:old-king-no-name-no-timespan");

        var hits = ForbiddenVocabulary.Scan("Keras, a nine-hundred-year-old sorcerer", [rule]);

        Assert.Equal(["Keras", "nine-hundred-year-old"], hits.Select(h => h.Surface));
        Assert.All(hits, h => Assert.Equal("rule:old-king-no-name-no-timespan", h.Rule.Source));
    }

    [Fact]
    public void Validate_UnknownKeys_AreRefusedByName()
    {
        using var data = JsonDocument.Parse("""{"forbidden_terms": ["Keras"], "applies_to": ["party"], "forbiden_patterns": []}""");

        var problem = Assert.Single(RevealRuleData.Validate(data.RootElement, "data"));

        Assert.Equal(
            "data has unknown keys \"applies_to\", \"forbiden_patterns\"; a reveal rule's data takes forbidden_terms, forbidden_patterns, preferred_terms, until, note.",
            problem);
    }

    [Theory]
    [InlineData("{}", "data: a reveal rule forbids words")]
    [InlineData("{\"note\": \"only a note\"}", "data: a reveal rule forbids words")]
    [InlineData("[\"Keras\"]", "data must be a JSON object")]
    [InlineData("{\"forbidden_terms\": \"Keras\"}", "data forbidden_terms must be an array of strings, not the string \"Keras\"")]
    [InlineData("{\"forbidden_terms\": [\"Keras\", 3]}", "data forbidden_terms item 2 must be a string, not the number 3")]
    [InlineData("{\"forbidden_terms\": []}", "data forbidden_terms is empty")]
    [InlineData("{\"forbidden_patterns\": [\"<number>\"]}", "data forbidden_patterns item 1 needs at least one word")]
    [InlineData("{\"forbidden_terms\": [\"Keras\"], \"until\": [\"character:keras\"]}", "data until item 1: \"character:keras\" is not a fact handle")]
    [InlineData("{\"forbidden_terms\": [\"Keras\"], \"note\": 5}", "data note must be a string, not the number 5")]
    public void Validate_BadData_IsRefusedWithWhere(string json, string expected)
    {
        using var data = JsonDocument.Parse(json);

        var problems = RevealRuleData.Validate(data.RootElement, "data");

        Assert.Contains(problems, p => p.StartsWith(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_BadData_ThrowsOneInputErrorListingTheProblems()
    {
        var ex = Assert.Throws<DndInputException>(() => RevealRuleData.Parse("""{"forbidden_terms": [], "until": ["x"]}"""));

        Assert.StartsWith("Invalid reveal rule (2 problems):", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_NotJson_IsAnInputError()
    {
        Assert.Throws<DndInputException>(() => RevealRuleData.Parse("{not json"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("{not json", false)]
    [InlineData("{}", false)]
    [InlineData(OldKing, true)]
    public void TryParse_StoredData_NeverThrows(string? json, bool ok)
    {
        Assert.Equal(ok, RevealRuleData.TryParse(json, out var rule));
        Assert.Equal(ok, rule is not null);
    }

    [Theory]
    [InlineData("", false, true)]
    [InlineData("f:3", false, true)]
    [InlineData("f:3", true, false)]
    [InlineData("f:3|F12", true, true)]
    public void IsActive_UntilFacts_LiftTheRuleOnlyWhenAllAreInPlay(string until, bool f3InPlay, bool active)
    {
        var rule = new RevealRuleData(["seal"], [], [], until.Length == 0 ? [] : until.Split('|'), null);

        Assert.Equal(active, rule.IsActive(f => f == "f:3" && f3InPlay));
    }
}
