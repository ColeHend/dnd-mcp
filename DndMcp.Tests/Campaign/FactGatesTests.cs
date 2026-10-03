using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using Xunit;

namespace DndMcp.Tests.Campaign;

/// <summary>
/// Invariant: a gate is validated as the model wrote it (fact handles, bounded lists, slug route ids, sensible
/// min_clues / min_routes, usable terms and patterns, no fact both before and with), stored as canonical JSON that
/// round-trips, and evaluated with the documented defaults (min_clues = all clues, min_routes = all routes).
/// </summary>
public sealed class FactGatesTests
{
    [Fact]
    public void Validate_TheOnePieceSealGateWithHandles_HasNoProblems()
    {
        var gate = new GateSpec
        {
            After = ["f:31", "F12"],
            With = ["f:2"],
            Prefer = ["f:33"],
            Seeds = ["f:40"],
            Routes =
            [
                new RouteSpec { Id = "testimonies", Clues = ["f:41", "f:42", "f:43", "f:44"], MinClues = 4 },
                new RouteSpec { Id = "illusion", Clues = ["f:45"] },
                new RouteSpec { Id = "temple-arithmetic", Clues = ["f:46", "f:47"] },
            ],
            MinRoutes = 2,
            ForbiddenTerms = ["seal"],
            ForbiddenPatterns = ["<number> years"],
            ForbiddenUntil = ["f:31"],
            PreferredTerms = ["shell", "what keeps it in"],
            Note = "Reveal order",
        };

        Assert.Empty(FactGates.Validate(gate, "gate"));
    }

    [Theory]
    [InlineData("after", "gate after is empty")]
    [InlineData("routes", "gate routes is empty")]
    [InlineData("forbidden_terms", "gate forbidden_terms is empty")]
    public void Validate_EmptyList_IsRefused(string field, string expected)
    {
        var gate = field switch
        {
            "after" => new GateSpec { After = [] },
            "routes" => new GateSpec { Routes = [] },
            _ => new GateSpec { ForbiddenTerms = [] },
        };

        Assert.Contains(FactGates.Validate(gate, "gate"), p => p.StartsWith(expected, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("character:old-king", "gate after item 1: \"character:old-king\" is not a fact handle")]
    [InlineData("old-king", "gate after item 1: \"old-king\" is not a fact handle")]
    [InlineData("e:3", "gate after item 1: \"e:3\" is not a fact handle")]
    [InlineData("", "gate after item 1 is blank")]
    [InlineData("f:0", "gate after item 1: \"f:0\": expected a positive whole number")]
    public void Validate_AfterItemThatIsNotAFactHandle_IsRefusedByPosition(string item, string expected)
    {
        var problems = FactGates.Validate(new GateSpec { After = [item] }, "gate");

        Assert.Contains(problems, p => p.StartsWith(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_TwentyOneItems_IsTooMany()
    {
        var gate = new GateSpec { Seeds = Enumerable.Range(1, 21).Select(i => $"f:{i}").ToList() };

        Assert.Contains("gate seeds has 21 items; at most 20.", FactGates.Validate(gate, "gate"));
    }

    [Fact]
    public void Validate_TwentyItemsInEveryList_IsExactlyTheLimit()
    {
        IReadOnlyList<string> Facts(int from) => Enumerable.Range(from, 20).Select(i => $"f:{i}").ToList();
        IReadOnlyList<string> Words(string stem) => Enumerable.Range(1, 20).Select(i => $"{stem}{(char)('a' + i)}").ToList();
        var gate = new GateSpec
        {
            After = Facts(1),
            With = Facts(101),
            Prefer = Facts(201),
            Seeds = Facts(301),
            ForbiddenUntil = Facts(401),
            Routes = Enumerable.Range(1, 20).Select(i => (RouteSpec?)new RouteSpec { Id = $"route-{i}", Clues = [$"f:{1000 + i}"] }).ToList(),
            ForbiddenTerms = Words("seal"),
            PreferredTerms = Words("shell"),
        };

        Assert.Empty(FactGates.Validate(gate, "gate"));
    }

    [Fact]
    public void Validate_SameFactTwiceInOneList_IsRefused()
    {
        var problems = FactGates.Validate(new GateSpec { After = ["f:3", " F:3 "] }, "gate");

        Assert.Contains("gate after item 2: f:3 is listed twice.", problems);
    }

    [Fact]
    public void Validate_RepeatedTerm_IsNamedByPositionNeverQuoted()
    {
        var problems = FactGates.Validate(new GateSpec { ForbiddenTerms = ["Axiom Cage", "seal", "axiom cage"] }, "gate");

        Assert.Equal(["gate forbidden_terms item 3 repeats item 1."], problems);
    }

    [Fact]
    public void Validate_FactInBothAfterAndWith_IsRefused()
    {
        var problems = FactGates.Validate(new GateSpec { After = ["f:3"], With = ["f:3"] }, "gate");

        Assert.Contains(problems, p => p.StartsWith("gate: f:3 is in both after and with", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, "gate routes item 1: id is required")]
    [InlineData("Testimonies!", "gate routes item 1: id \"Testimonies!\" must be a slug")]
    public void Validate_RouteId_MustBeASlug(string? id, string expected)
    {
        var problems = FactGates.Validate(new GateSpec { Routes = [new RouteSpec { Id = id, Clues = ["f:1"] }] }, "gate");

        Assert.Contains(problems, p => p.StartsWith(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_DuplicateRouteIdsNullRouteAndMissingClues_AreEachReported()
    {
        var gate = new GateSpec
        {
            Routes = [new RouteSpec { Id = "a", Clues = ["f:1"] }, new RouteSpec { Id = "a", Clues = ["f:2"] }, null, new RouteSpec { Id = "b" }],
        };

        var problems = FactGates.Validate(gate, "gate");

        Assert.Contains("gate routes item 2: id \"a\" is used by another route.", problems);
        Assert.Contains(problems, p => p.StartsWith("gate routes item 3 is null", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("gate routes item 4: clues is required", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void Validate_MinClues_IsOneToTheNumberOfClues(int minClues, bool refused)
    {
        var gate = new GateSpec { Routes = [new RouteSpec { Id = "r", Clues = ["f:1", "f:2"], MinClues = minClues }] };

        Assert.Equal(refused, FactGates.Validate(gate, "gate").Any(p => p.Contains("min_clues", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void Validate_MinRoutes_IsOneToTheNumberOfRoutes(int minRoutes, bool refused)
    {
        var gate = new GateSpec
        {
            Routes = [new RouteSpec { Id = "a", Clues = ["f:1"] }, new RouteSpec { Id = "b", Clues = ["f:2"] }],
            MinRoutes = minRoutes,
        };

        Assert.Equal(refused, FactGates.Validate(gate, "gate").Any(p => p.Contains("min_routes", StringComparison.Ordinal)));
    }

    [Fact]
    public void Validate_MinRoutesWithoutRoutes_IsRefused()
    {
        Assert.Contains("gate: min_routes needs routes; give routes or leave min_routes out.", FactGates.Validate(new GateSpec { MinRoutes = 1 }, "gate"));
    }

    [Theory]
    [InlineData("forbidden_terms", "a-sixty-one-character-term-that-is-far-too-long-for-a-term-xx", "is longer than 60 characters")]
    [InlineData("forbidden_terms", "   ", "is blank")]
    [InlineData("forbidden_terms", "!!!", "has no letters or digits")]
    [InlineData("forbidden_terms", "<number> years", "a term has no placeholders")]
    [InlineData("forbidden_patterns", "<years> old", "which is not a placeholder")]
    [InlineData("forbidden_patterns", "<number>", "needs at least one word beside <number>")]
    [InlineData("forbidden_patterns", "a < b", "has an unclosed")]
    [InlineData("preferred_terms", "", "is blank")]
    public void Validate_UnusableTermOrPattern_IsRefusedWithWhy(string field, string text, string expected)
    {
        var gate = field switch
        {
            "forbidden_terms" => new GateSpec { ForbiddenTerms = [text] },
            "forbidden_patterns" => new GateSpec { ForbiddenPatterns = [text] },
            _ => new GateSpec { ForbiddenTerms = ["seal"], PreferredTerms = [text] },
        };

        var problems = FactGates.Validate(gate, "gate");

        Assert.Contains(problems, p => p.StartsWith($"gate {field} item 1", StringComparison.Ordinal) && p.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ForbiddenUntilWithoutVocabulary_IsRefused()
    {
        var problems = FactGates.Validate(new GateSpec { ForbiddenUntil = ["f:1"] }, "gate");

        Assert.Contains(problems, p => p.StartsWith("gate: forbidden_until lifts", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_NullOrEmptyGate_IsValid()
    {
        Assert.Empty(FactGates.Validate(null, "gate"));
        Assert.Empty(FactGates.Validate(new GateSpec(), "gate"));
        Assert.True(FactGates.IsEmpty(new GateSpec()));
        Assert.False(FactGates.IsEmpty(new GateSpec { Note = "x" }));
    }

    [Fact]
    public void Validate_Subject_PrefixesEveryProblem()
    {
        var problems = FactGates.Validate(new GateSpec { After = ["x"], MinRoutes = 1, ForbiddenTerms = [""] }, "ops item 2 (fact f:12): gate");

        Assert.Equal(3, problems.Count);
        Assert.All(problems, p => Assert.StartsWith("ops item 2 (fact f:12): gate", p, StringComparison.Ordinal));
    }

    [Fact]
    public void SerializeThenParse_RoundTripsInSnakeCaseWithNullsOmittedAndOrderKept()
    {
        var gate = new GateSpec
        {
            After = ["id-b", "id-a"],
            Routes = [new RouteSpec { Id = "illusion", Clues = ["id-c"] }],
            MinRoutes = 1,
            ForbiddenTerms = ["seal"],
            ForbiddenUntil = ["id-b"],
            PreferredTerms = ["shell"],
        };

        var json = FactGates.Serialize(gate);
        var parsed = FactGates.Parse(json)!;

        Assert.Equal(
            """{"after":["id-b","id-a"],"routes":[{"id":"illusion","clues":["id-c"]}],"min_routes":1,"forbidden_terms":["seal"],"forbidden_until":["id-b"],"preferred_terms":["shell"]}""",
            json);
        Assert.Equal(json, FactGates.Serialize(parsed));
    }

    [Fact]
    public void Serialize_NonAsciiAndQuotes_StayReadable()
    {
        var json = FactGates.Serialize(new GateSpec { Note = "Björn's \"seal\"" });

        Assert.Equal("""{"note":"Björn's \"seal\""}""", json);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NullOrBlank_IsNoGate(string? json)
    {
        Assert.Null(FactGates.Parse(json));
    }

    [Theory]
    [InlineData("[\"f:1\"]")]
    [InlineData("{\"after\": \"f:1\"}")]
    [InlineData("not json")]
    public void Parse_NotAGateObject_IsAnInputErrorNamingNoContent(string json)
    {
        var ex = Assert.Throws<DndInputException>(() => FactGates.Parse(json));

        Assert.StartsWith("gate: could not be read", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[\"f:1\"]")]
    [InlineData("{\"after\": \"f:1\"}")]
    [InlineData("{\"routes\": [{\"id\": \"a\", \"clues\": \"f:1\"}]}")]
    [InlineData("{\"min_routes\": \"two\"}")]
    [InlineData("not json")]
    [InlineData("null")]
    public void TryParse_UnreadableStoredGate_IsFalseInsteadOfThrowing(string json)
    {
        Assert.False(FactGates.TryParse(json, out var gate));
        Assert.Null(gate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void TryParse_NullOrBlank_IsReadableAndNoGate(string? json)
    {
        Assert.True(FactGates.TryParse(json, out var gate));
        Assert.Null(gate);
    }

    [Fact]
    public void TryParse_StoredGate_ReadsLikeParse()
    {
        const string json = """{"after":["a"],"routes":[{"id":"r","clues":["b","c"],"min_clues":1}],"forbidden_terms":["seal"]}""";

        Assert.True(FactGates.TryParse(json, out var gate));
        Assert.Equal(FactGates.Serialize(FactGates.Parse(json)!), FactGates.Serialize(gate!));
    }

    [Fact]
    public void Parse_UnknownField_IsIgnoredSoALaterVersionsGateStillLoads()
    {
        var gate = FactGates.Parse("""{"after":["a"],"after_min":{"facts":["b"],"min":1}}""")!;

        Assert.Equal(["a"], gate.After!);
    }

    [Fact]
    public void References_ListsEveryFactOnceInFieldOrder()
    {
        var gate = new GateSpec
        {
            After = ["a", "b"],
            With = ["c"],
            Prefer = ["d", "a"],
            Seeds = ["e"],
            Routes = [new RouteSpec { Id = "r", Clues = ["f", "c"] }, null],
            ForbiddenUntil = ["g"],
            ForbiddenTerms = ["seal"],
        };

        Assert.Equal(["a", "b", "c", "d", "e", "f", "g"], FactGates.References(gate));
    }

    [Fact]
    public void Rewrite_MapsEveryFactReferenceAndKeepsTheRest()
    {
        var gate = new GateSpec
        {
            After = ["f:1"],
            With = ["f:2"],
            Prefer = ["f:3"],
            Seeds = ["f:4"],
            Routes = [new RouteSpec { Id = "r", Clues = ["f:5"], MinClues = 1 }],
            MinRoutes = 1,
            ForbiddenTerms = ["seal"],
            ForbiddenPatterns = ["<number> years"],
            ForbiddenUntil = ["f:6"],
            PreferredTerms = ["shell"],
            Note = "n",
        };

        var rewritten = FactGates.Rewrite(gate, h => "id-" + h[2..]);

        Assert.Equal(
            """{"after":["id-1"],"with":["id-2"],"prefer":["id-3"],"seeds":["id-4"],"routes":[{"id":"r","clues":["id-5"],"min_clues":1}],"min_routes":1,"forbidden_terms":["seal"],"forbidden_patterns":["<number> years"],"forbidden_until":["id-6"],"preferred_terms":["shell"],"note":"n"}""",
            FactGates.Serialize(rewritten));
    }

    [Fact]
    public void Evaluate_MinCluesAndMinRoutesOmitted_DefaultToAll()
    {
        var gate = new GateSpec
        {
            Routes = [new RouteSpec { Id = "a", Clues = ["1", "2"] }, new RouteSpec { Id = "b", Clues = ["3"] }],
        };

        var one = FactGates.Evaluate(gate, _ => true, f => f is "1" or "3", false);
        var all = FactGates.Evaluate(gate, _ => true, f => f is "1" or "2" or "3", false);

        Assert.Equal(new RouteStatus("a", 1, 2, 2, false), one.Routes[0]);
        Assert.Equal(2, one.MinRoutes);
        Assert.False(one.RoutesMet);
        Assert.True(all.RoutesMet);
        Assert.True(all.Ready);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    public void Evaluate_PreferAndAfter_AreConditionsInPlayNotFactsThePartyKnows(bool inPlay, bool partyKnows, bool unmet)
    {
        var status = FactGates.Evaluate(new GateSpec { After = ["a"], Prefer = ["p"] }, _ => inPlay, _ => partyKnows, false);

        Assert.Equal(unmet, status.UnmetPrefer.Count == 1);
        Assert.Equal(unmet, status.UnmetAfter.Count == 1);
    }

    [Fact]
    public void Evaluate_NoRoutes_RoutesAreMetAndNothingIsSeeded()
    {
        var status = FactGates.Evaluate(new GateSpec { After = ["x"] }, _ => false, _ => true, false);

        Assert.Equal(0, status.MinRoutes);
        Assert.True(status.RoutesMet);
        Assert.False(status.Ready);
        Assert.False(status.Seeded);
        Assert.False(status.ReachableBeforeGate);
    }

    [Fact]
    public void Evaluate_ClueKnownTwice_CountsOnce()
    {
        var gate = new GateSpec { Routes = [new RouteSpec { Id = "a", Clues = ["1", "1", "2"], MinClues = 2 }] };

        Assert.False(FactGates.Evaluate(gate, _ => true, f => f == "1", false).Routes[0].Complete);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    public void Evaluate_ForbiddenUntilSeveralFacts_LiftsOnlyWhenAllAreInPlay(bool aInPlay, bool bInPlay, bool active)
    {
        var gate = new GateSpec { ForbiddenTerms = ["seal"], ForbiddenUntil = ["a", "b"] };

        var status = FactGates.Evaluate(gate, f => f == "a" ? aInPlay : f == "b" && bInPlay, _ => true, gatedFactKnownToParty: true);

        Assert.Equal(active, status.ForbiddenActive);
    }

    [Fact]
    public void Evaluate_RouteClueKnownWithNoSeedsAndNoRouteComplete_IsSeeded()
    {
        var gate = new GateSpec
        {
            Routes = [new RouteSpec { Id = "testimonies", Clues = ["c1", "c2", "c3"] }, new RouteSpec { Id = "illusion", Clues = ["c4"] }],
        };

        var status = FactGates.Evaluate(gate, _ => true, f => f == "c2", false);

        Assert.True(status.Seeded);
        Assert.Equal(0, status.RoutesComplete);
        Assert.Equal("seeded", SecretStatuses.Derive(false, status.RoutesComplete > 0, status.Seeded));
        Assert.False(FactGates.Evaluate(gate, _ => true, _ => false, false).Seeded);
    }

    [Fact]
    public void WithPartners_OwnGateOnly_AreTheOwnGatesWithFacts()
    {
        Assert.Equal(["b"], FactGates.WithPartners("a", new GateSpec { With = ["b"] }, [("b", null)]));
        Assert.Equal(["b", "c"], FactGates.WithPartners("a", new GateSpec { With = ["b", "c"] }, []));
    }

    [Fact]
    public void WithPartners_OwnGateAndBackReferences_MergeWithoutDuplicates()
    {
        var partners = FactGates.WithPartners(
            "a",
            new GateSpec { With = ["b", "c"] },
            [("b", new GateSpec { With = ["a"] }), ("d", new GateSpec { With = ["a", "x"] }), ("e", new GateSpec { With = ["x"] }), ("a", new GateSpec { With = ["a"] })]);

        Assert.Equal(["b", "c", "d"], partners);
    }

    [Fact]
    public void VocabularyRule_GateWithoutTerms_IsNull()
    {
        Assert.Null(FactGates.VocabularyRule(new GateSpec { After = ["x"] }, "f:1"));

        var rule = FactGates.VocabularyRule(new GateSpec { ForbiddenTerms = ["seal"], PreferredTerms = ["shell"], Note = "n" }, "f:1")!;
        Assert.Equal("f:1", rule.Source);
        Assert.Equal(["seal"], rule.Terms);
        Assert.Empty(rule.Patterns);
        Assert.Equal(["shell"], rule.PreferredTerms);
    }
}
