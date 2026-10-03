using DndMcp.Domain.Campaign;
using DndMcp.Repository.Campaign;
using Xunit;

namespace DndMcp.Tests.CampaignWrite;

/// <summary>
/// Invariant: the Belmakor fixture of understand-belmakor.md §2 can be built entirely through the write path, and what
/// it builds gives the golden verdicts of §3 rows 1-19 when read back through the stage-1 loader and verdict rules
/// (the read path's own tests cover rendering). If the write path stored a knower, a session or an attendance row
/// differently from what the loader expects, these rows would drift even though each writer's own tests pass.
/// </summary>
public sealed class BelmakorFixtureTests : IDisposable
{
    private readonly WriteFixture _f = new();
    private readonly BelmakorFixture _b;

    public BelmakorFixtureTests()
    {
        _b = BelmakorFixture.Build(_f);
    }

    public void Dispose() => _f.Dispose();

    private KnowledgeVerdict Verdict(string perspective, string target, int? asOf = null)
    {
        using var connection = _f.Open();
        var loader = new KnowledgeLoader(connection, _b.Belmakor);
        var who = loader.Resolve(Perspective.Parse(perspective));
        var resolver = new HandleResolver(connection, _b.Belmakor.Id);
        var handle = CampaignHandle.Parse(target);
        if (resolver.TryFact(handle) is { } fact)
        {
            var visibility = asOf is { } n
                ? (string)ChangeReplay.RowAsOf(connection, "fact", fact.Id, n)!["visibility"]!
                : fact.Visibility;
            return KnowledgeVerdicts.Evaluate(who, loader.EntriesForFacts([fact.Id])[fact.Id], visibility, loader.Attendance, asOf);
        }

        var entity = resolver.TryEntity(handle)!;
        return KnowledgeVerdicts.Evaluate(who, loader.EntriesForEntities([entity.Id])[entity.Id], entity.Visibility, loader.Attendance, asOf);
    }

    [Theory]
    [InlineData(1, "character:belmakor", "f:1", KnowledgeStanding.DoesNotKnow, null)]
    [InlineData(2, "character:belmakor", "item:thing-he-wants", KnowledgeStanding.Knows, "the thing he wants")]
    [InlineData(3, "character:belmakor", "character:old-king", KnowledgeStanding.Knows, "the old king")]
    [InlineData(4, "character:belmakor", "f:2", KnowledgeStanding.DoesNotKnow, null)]
    [InlineData(5, "party", "f:1", KnowledgeStanding.DoesNotKnow, null)]
    [InlineData(5, "party", "item:thing-he-wants", KnowledgeStanding.Knows, "the thing he wants")]
    [InlineData(5, "party", "character:old-king", KnowledgeStanding.Knows, "the old king")]
    [InlineData(6, "author", "f:1", KnowledgeStanding.Knows, null)]
    [InlineData(6, "author", "f:2", KnowledgeStanding.Knows, null)]
    [InlineData(7, "dm", "f:2", KnowledgeStanding.Knows, null)]
    [InlineData(8, "dm", "f:1", KnowledgeStanding.NoRecord, null)]
    [InlineData(9, "character:ignis", "f:3", KnowledgeStanding.Knows, null)]
    [InlineData(10, "character:ignis", "item:thing-he-wants", KnowledgeStanding.Knows, "the thing he wants")]
    [InlineData(11, "character:serif", "f:7", KnowledgeStanding.Uncertain, null)]
    [InlineData(12, "character:aiden-ironstar", "f:7", KnowledgeStanding.Uncertain, null)]
    [InlineData(13, "character:belmakor", "f:7", KnowledgeStanding.Knows, null)]
    [InlineData(14, "character:belmakor", "f:5", KnowledgeStanding.Knows, null)]
    [InlineData(15, "character:vars", "f:5", KnowledgeStanding.DoesNotKnow, null)]
    [InlineData(16, "party", "f:5", KnowledgeStanding.DoesNotKnow, null)]
    [InlineData(16, "public", "f:5", KnowledgeStanding.NoRecord, null)]
    public void FixtureBuiltThroughTheWritePath_GivesTheGoldenVerdicts(int row, string perspective, string target, KnowledgeStanding standing, string? knownAs)
    {
        var verdict = Verdict(perspective, target);

        Assert.True(standing == verdict.Standing, $"row {row}: expected {standing}, got {verdict.Standing} ({verdict.Explanation})");
        Assert.Equal(knownAs, verdict.KnownAs);
    }

    [Theory]
    [InlineData("character:serif", "Serif was absent")]
    [InlineData("character:aiden-ironstar", "no attendance recorded for Aiden Ironstar in S1")]
    public void AttendanceWrittenBySessions_DecidesTheUncertainRows_Rows11And12(string perspective, string expected)
    {
        Assert.Contains(expected, Verdict(perspective, "f:7").Explanation);
    }

    [Fact]
    public void Dm_WithOnlyAnUnawarePartyRow_ReadsNoRecord_Row8()
    {
        Assert.Equal(KnowledgeVerdicts.NoRecordText, Verdict("dm", "f:1").Explanation);
    }

    [Theory]
    [InlineData(17, "table", 1, KnowledgeStanding.NoRecord)]
    [InlineData(18, "table", 2, KnowledgeStanding.Knows)]
    [InlineData(19, "character:belmakor", 1, KnowledgeStanding.Knows)]
    public void TheContingencyAsOfASession_Rows17To19(int row, string perspective, int asOf, KnowledgeStanding standing)
    {
        var verdict = Verdict(perspective, "f:6", asOf);

        Assert.True(standing == verdict.Standing, $"row {row}: expected {standing}, got {verdict.Standing}");
    }

    [Fact]
    public void ContingencyVisibility_WasChangedInSessionTwo_AndIsInTheLog()
    {
        using var connection = _f.Open();
        var fact = _f.Fact(_b.Belmakor, "f:6");

        Assert.Equal("party", fact.Visibility);
        Assert.Equal("restricted", ChangeReplay.RowAsOf(connection, "fact", fact.Id, 1)!["visibility"]);
        Assert.Equal("party", ChangeReplay.RowAsOf(connection, "fact", fact.Id, 2)!["visibility"]);
    }

    [Fact]
    public void Fixture_HasTheCrossLinksTheSupersessionAndTheRule()
    {
        Assert.Equal(2, _f.Count("SELECT count(*) FROM cross_link"));
        Assert.Equal(("superseded", _f.Fact(_b.Belmakor, "f:9").Id), (_f.Fact(_b.Belmakor, "f:8").CanonStatus, _f.Fact(_b.Belmakor, "f:8").SupersededBy));
        Assert.Equal("{\"forbidden_terms\":[\"Keras\"],\"forbidden_patterns\":[\"<n>-year-old\",\"<n> years\"]}",
            _f.Entity(_b.Belmakor, "rule:old-king-no-name-no-timespan").Data);
        Assert.Equal(_f.Entity(_b.Belmakor, "character:belmakor").Id, _b.Belmakor.MyCharacterId);
        Assert.Equal(_f.Entity(_b.Belmakor, "faction:party").Id, _b.Belmakor.PartyId);
    }
}
