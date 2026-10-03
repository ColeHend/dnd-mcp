using DndMcp.Domain.Campaign;
using DndMcp.Repository.Campaign.Read;
using Xunit;

namespace DndMcp.Tests.CampaignRead;

/// <summary>
/// Invariant: a player-side search of the One Piece campaign finds Silk Isle and the question about it but nothing of the
/// withheld answer (G.O.D.S. Co.'s founders, the rope, the fleet), never the Protector by his true name, and never the
/// seal secret before the party is told; <c>understand-onepiece.md</c> §3 rows 31-40. Each of those words sits in a
/// column or row the table must not read, so a search that reached it would hand the players the next arc.
/// </summary>
public sealed class OnePieceSearchTests(OnePieceFixture fixture) : IClassFixture<OnePieceFixture>
{
    private static readonly string[] SilkIsleSecrets = ["G.O.D.S.", "rope en masse", "founders", "fleet", "withheld"];

    private SearchResult Search(string? query, string perspective = "author", IReadOnlyList<string>? statuses = null, IReadOnlyList<string>? kinds = null) =>
        new CampaignSearch(fixture.Db.Database).Search(fixture.CampaignRow,
            new SearchRequest(query, Kinds: kinds, Statuses: statuses, Perspective: Perspective.Parse(perspective), Limit: 50));

    [Theory]
    [InlineData("party")]
    [InlineData("table")]
    public void Search_Rows31And36SilkIsle_FindsTheIsleAndTheQuestionWithoutTheAnswer(string perspective)
    {
        var result = Search("silk isle", perspective);

        Assert.Equal(["location:silk-isle", "question:q7"], result.Entities.Select(e => e.Ref).Order(StringComparer.Ordinal));
        Assert.Equal(SearchTiers.Exact, result.Entities.Single(e => e.Ref == "location:silk-isle").MatchTier);
        Assert.Equal("open", result.Entities.Single(e => e.Ref == "question:q7").Status);
        Assert.Empty(result.Facts);
        LeakAssert.Clean(result, SilkIsleSecrets, $"row 31 as {perspective}");
    }

    /// <summary>
    /// Row 32 as the contract decides it (§3.9, §11): nothing visible matches BOTH words, so the any-word retry runs and
    /// finds the question through the one word its visible body has ("rope-work"); it is flagged partial, and nothing of
    /// the secret answer (founders, fleet) comes with it. The golden's "empty" predates the partial-match rule.
    /// </summary>
    [Fact]
    public void Search_Row32RopeFleetAsParty_MatchesOnlyPartiallyThroughVisibleText()
    {
        var result = Search("rope fleet", "party");

        Assert.True(result.PartialMatch);
        var hit = Assert.Single(result.Entities);
        Assert.Equal("question:q7", hit.Ref);
        Assert.Equal("body", hit.SnippetFrom);
        Assert.Empty(result.Facts);
        LeakAssert.Clean(result, ["founders", "G.O.D.S.", "rope en masse"], "row 32");
    }

    [Fact]
    public void Search_Row32RopeFleetAsAuthor_MatchesEveryWordThroughTheSecrets()
    {
        var result = Search("rope fleet");

        Assert.False(result.PartialMatch);
        Assert.Contains(result.Entities, e => e.Ref == "question:q7");
        Assert.Contains(result.Entities, e => e.Ref == "faction:gods-co" && e.SnippetFrom == "secret");
        Assert.Contains(result.Facts, f => f.Ref == fixture.Q7Answer.SeqHandle);
    }

    [Fact]
    public void Search_Row33GodsFoundersAsAuthor_FindsTheFactionTheQuestionAndTheAnswer()
    {
        var result = Search("G.O.D.S. founders");

        Assert.False(result.PartialMatch);
        Assert.Contains(result.Entities, e => e.Ref == "faction:gods-co");
        Assert.Contains(result.Entities, e => e.Ref == "question:q7");
        Assert.Contains(result.Facts, f => f.Ref == fixture.Q7Answer.SeqHandle && f.CanonStatus == "lean");
    }

    /// <summary>Row 33 for the party: "founders" is secret-only, so no result matches both words; the retry finds the faction by its name alone.</summary>
    [Fact]
    public void Search_Row33GodsFoundersAsParty_NeverMatchesTheSecretWord()
    {
        var result = Search("G.O.D.S. founders", "party");

        Assert.True(result.PartialMatch);
        Assert.All(result.Entities, e => Assert.NotEqual(SearchTiers.List, e.MatchTier));
        Assert.DoesNotContain(result.Facts, f => f.Ref == fixture.Q7Answer.SeqHandle);
        LeakAssert.Clean(result, ["founders", "rope en masse", "fleet"], "row 33");
    }

    [Theory]
    [InlineData("seal")]
    [InlineData("lineage")]
    [InlineData("protector")]
    [InlineData("Keras")]
    [InlineData("Baal")]
    public void Search_Rows34And38And39SecretOrDisguisedAsParty_FindsNothing(string query)
    {
        var result = Search(query, "party");

        Assert.Empty(result.Entities);
        Assert.Empty(result.Facts);
    }

    [Fact]
    public void Search_Row35MoonGoddessProtectingAsParty_FindsTheFactThePartyKnows()
    {
        var result = Search("moon goddess protecting", "party");

        var fact = Assert.Single(result.Facts);
        Assert.Equal(fixture.ShieldedChild.SeqHandle, fact.Ref);
        Assert.Null(fact.KnownBy);
    }

    /// <summary>Row 39's other half: the disguised Protector is found by the name the party knows, never by his own.</summary>
    [Theory]
    [InlineData("advisor")]
    [InlineData("advis*")]
    [InlineData("the advisor in Serret")]
    public void Search_TheNameThePartyKnowsTheProtectorBy_FindsHimAsSeqRef(string query)
    {
        var result = Search(query, "party");

        var hit = Assert.Single(result.Entities);
        Assert.Equal("e:" + fixture.Protector.Seq, hit.Ref);
        Assert.Equal("the advisor in Serret", hit.DisplayName);
        Assert.Null(hit.Status);
        Assert.Null(hit.Snippet);
        LeakAssert.Clean(result, ["Protector", "Keras", "fragment", "protector"], "the disguised Protector");
    }

    [Fact]
    public void Search_AdvisorAsAuthor_FindsTheProtectorThroughThePartysName()
    {
        var result = Search("advisor");

        var hit = Assert.Single(result.Entities, e => e.Ref == "character:protector");
        Assert.Equal("The Protector", hit.DisplayName);
    }

    /// <summary>
    /// Row 40 as the contract decides it: the fixture's @seal and @nadar-plan are restricted (§8), so once the party learns
    /// them a party search returns both; the same facts with author visibility, which no knowledge row can open to another
    /// view (§3.2: "author visibility is absolute"), are not returned even then. The secret entity stays hidden either way.
    /// </summary>
    [Fact]
    public void Search_Row40SealAfterThePartyLearnsIt_ReturnsTheRestrictedFactNeverTheAuthorOne()
    {
        using var world = new OnePieceFixture();
        world.PartyLearns(world.Seal, 12);
        world.PartyLearns(world.NadarPlan, 12);
        var restricted = new CampaignSearch(world.Db.Database).Search(world.CampaignRow, new SearchRequest("seal", Perspective: Perspective.Parse("party")));
        Dapper.SqlMapper.Execute(world.Connection, "UPDATE fact SET visibility = 'author' WHERE id IN @ids", new { ids = new[] { world.Seal.Id, world.NadarPlan.Id } });

        var author = new CampaignSearch(world.Db.Database).Search(world.CampaignRow, new SearchRequest("seal", Perspective: Perspective.Parse("party")));

        Assert.Equal([world.Seal.SeqHandle, world.NadarPlan.SeqHandle], restricted.Facts.Select(f => f.Ref).Order(StringComparer.Ordinal));
        Assert.Empty(restricted.Entities);
        Assert.Empty(author.Facts);
        Assert.Empty(author.Entities);
    }

    [Fact]
    public void Search_Row37WithheldStatusFilterAsParty_FindsNothingButOpenFindsTheQuestion()
    {
        var withheld = Search(null, "party", statuses: ["withheld"]);
        var open = Search(null, "party", statuses: ["open"], kinds: ["question"]);

        Assert.Empty(withheld.Entities);
        var q7 = Assert.Single(open.Entities);
        Assert.Equal("question:q7", q7.Ref);
        Assert.Equal("open", q7.Status);
    }

    [Fact]
    public void Search_WithheldStatusFilterAsAuthor_FindsTheWithheldQuestion()
    {
        var result = Search(null, statuses: ["withheld"]);

        Assert.Equal(["question:q7"], result.Entities.Select(e => e.Ref));
        Assert.Equal("withheld", result.Entities[0].Status);
    }

    /// <summary>A planned fact is not in play: a party-visible "the axe is fully assembled" is not something the party knows yet.</summary>
    [Fact]
    public void Search_PlannedFactAsParty_IsNotShown()
    {
        var party = Search("axe assembled", "party");
        var author = Search("axe assembled");

        Assert.DoesNotContain(party.Facts, f => f.Ref == fixture.AxeAssembled.SeqHandle);
        Assert.Contains(author.Facts, f => f.Ref == fixture.AxeAssembled.SeqHandle && f.CanonStatus == "planned");
    }

    public static TheoryData<string, string> LeakCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var perspective in new[] { "party", "table", "public", "character:bjorn-mountainfell" })
        {
            foreach (var query in new[] { "silk", "isle", "rope", "fleet", "founders", "seal", "protector", "advisor", "Keras", "fragment", "lineage", "moon", "question" })
            {
                data.Add(perspective, query);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(LeakCases))]
    public void Search_AnyQueryAsAPlayerView_NeverShowsTheSecrets(string perspective, string query)
    {
        var result = Search(query, perspective);

        LeakAssert.Clean(result, [.. SilkIsleSecrets, "Keras", "fragment", "Baal", "Protector", "Mistaken", "Dutiful", "lineage"],
            $"search \"{query}\" as {perspective}");
    }
}
