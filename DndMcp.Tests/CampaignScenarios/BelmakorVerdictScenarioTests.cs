using DndMcp.Domain.Campaign;
using DndMcp.Repository.Campaign.Read;
using Xunit;

namespace DndMcp.Tests.CampaignScenarios;

/// <summary>
/// Invariant: <c>understand-belmakor.md</c> §3 rows 1-19 (who knows what, under which name, as of when) hold for the
/// Belmakor world as the write path stores it, read through campaign_knowledge ledger and campaign_get include:[knowledge]
/// exactly as a tool renders them. W's fixture test proves the verdicts from the loader; Q's tests prove the readers on
/// seeded rows; only here does a knower, a learned session or an attendance row written by one stage get read by the
/// other. If they disagree (an attendance row the reader cannot see, a party row filed under the wrong session), Serif
/// would "know" how Tristan died and a song would sing it.
/// </summary>
public sealed class BelmakorVerdictScenarioTests(BelmakorScenario world) : IClassFixture<BelmakorScenario>
{
    /// <summary>Rows 1-16: the ledger cell (standing and its wording) for each golden query.</summary>
    public static TheoryData<int, string, string, string, string> LedgerRows() => new()
    {
        { 1, "f:1", "character:belmakor", Standings.DoesNotKnow, "unaware" },
        { 2, "item:thing-he-wants", "character:belmakor", Standings.Knows, "aware as “the thing he wants” (S3)" },
        { 3, "character:old-king", "character:belmakor", Standings.Knows, "met as “the old king” (S3)" },
        { 4, "f:2", "character:belmakor", Standings.DoesNotKnow, "unaware" },
        { 5, "f:1", "party", Standings.DoesNotKnow, "unaware" },
        { 5, "item:thing-he-wants", "party", Standings.Knows, "aware as “the thing he wants” (S3)" },
        { 5, "character:old-king", "party", Standings.Knows, "met as “the old king” (S3)" },
        { 6, "f:1", "author", Standings.Knows, "knows" },
        { 6, "f:2", "author", Standings.Knows, "knows" },
        { 7, "f:2", "dm", Standings.Knows, "knows" },
        { 8, "f:1", "dm", Standings.NoRecord, "no record" },
        { 9, "f:3", "character:ignis", Standings.Knows, "knows (S3)" },
        { 10, "item:thing-he-wants", "character:ignis", Standings.Knows, "aware as “the thing he wants” (S3)" },
        { 11, "f:7", "character:serif", Standings.Uncertain, "uncertain: the party learned it in S1; Serif was absent" },
        { 12, "f:7", "character:aiden-ironstar", Standings.Uncertain, "uncertain: the party learned it in S1; no attendance recorded for Aiden Ironstar in S1" },
        { 13, "f:7", "character:belmakor", Standings.Knows, "knows (S1)" },
        { 14, "f:5", "character:belmakor", Standings.Knows, "knows" },
        { 15, "f:5", "character:vars", Standings.DoesNotKnow, "unaware" },
        { 16, "f:5", "party", Standings.DoesNotKnow, "unaware" },
        { 16, "f:5", "public", Standings.NoRecord, "no record" },
    };

    [Theory]
    [MemberData(nameof(LedgerRows))]
    public void Ledger_Rows1To16_GiveTheGoldenStandingInItsWording(int row, string target, string perspective, string standing, string text)
    {
        var cell = world.Reads.Cell(world.Campaign, target, perspective);

        Assert.True((standing, text) == (cell.Standing, cell.Text), $"row {row}: {target} for {perspective} is {cell.Standing} / {cell.Text}");
    }

    /// <summary>
    /// Contract §3.3's character rules 4-5 on the scenario's one non-member character perspective: the old king has no
    /// knowledge rows and no party membership, so what is party-visible and known to the party (the errand, the elegy, the
    /// party's PCs, even the old king's own entity) is "no record" ("not met" for an entity) to him, never the "knows" a
    /// member (Belmakor) gets. Were non-members given the party default, every NPC voiced with
    /// <c>character:&lt;npc&gt;</c> would "know" everything the party knows, and an in-character line would pass a check
    /// it should fail.
    /// </summary>
    [Theory]
    [InlineData("f:3", "no record")]
    [InlineData("f:7", "no record")]
    [InlineData("f:9", "no record")]
    [InlineData("character:vars", "not met")]
    [InlineData("character:old-king", "not met")]
    public void Ledger_ANonMemberCharacterWithNoRowsOfHisOwn_HasNoRecordOfWhatThePartyKnows(string target, string text)
    {
        var cell = world.Reads.Cell(world.Campaign, target, "character:old-king");
        var member = world.Reads.Cell(world.Campaign, target, "character:belmakor");

        Assert.Equal((Standings.NoRecord, text), (cell.Standing, cell.Text));
        Assert.Equal(Standings.Knows, member.Standing);
    }

    /// <summary>
    /// Row 8's wording rule: "no record" never claims the dm does not know (she may; nobody wrote it down). The same holds
    /// for every NoRecord cell the goldens reach.
    /// </summary>
    [Theory]
    [InlineData("f:1", "dm")]
    [InlineData("f:5", "public")]
    public void Ledger_Rows8And16NoRecord_NeverSaysDoesNotKnow(string target, string perspective)
    {
        var cell = world.Reads.Cell(world.Campaign, target, perspective);

        Assert.DoesNotContain("not know", cell.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unaware", cell.Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Rows 2, 3, 9, 10, 13 and 14 through campaign_get include:[knowledge] as the perspective itself: its own verdict line
    /// on the target carries the same standing, state, name and session as the ledger.
    /// </summary>
    [Theory]
    [InlineData(2, "e:{thing}", "character:belmakor", "aware", "the thing he wants", 3)]
    [InlineData(3, "character:old-king", "character:belmakor", "met", "the old king", 3)]
    [InlineData(9, "f:3", "character:ignis", "knows", null, 3)]
    [InlineData(10, "e:{thing}", "character:ignis", "aware", "the thing he wants", 3)]
    [InlineData(13, "f:7", "character:belmakor", "knows", null, 1)]
    [InlineData(14, "f:5", "character:belmakor", "knows", null, null)]
    public void Get_KnownRowsAsThePerspective_ShowItsOwnVerdictLine(int row, string handle, string perspective, string state, string? knownAs, int? learned)
    {
        handle = handle.Replace("{thing}", world.F.Entity(world.Campaign, "item:thing-he-wants").Seq.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

        var result = world.Reads.Get(world.Campaign, perspective, new EntityIncludes(Knowledge: true), null, handle);

        var lines = result.Entities.SelectMany(e => e.Knowledge!).Concat(result.Facts.SelectMany(f => f.Knowledge!)).ToList();
        var own = lines[0];
        Assert.True((Standings.Knows, state, knownAs, learned) == (own.Standing, own.State, own.KnownAs, own.LearnedSession),
            $"row {row}: {own.Standing} {own.State} {own.KnownAs} {own.LearnedSession}");
        Assert.All(lines, l => Assert.Null(l.Author));
    }

    /// <summary>
    /// Rows 1, 4, 8, 11, 12, 15 and 16: what a perspective does not know it cannot open either. campaign_get answers
    /// exactly like a handle that names nothing, so the refusal itself says nothing about what is behind the handle.
    /// </summary>
    [Theory]
    [InlineData(1, "f:1", "character:belmakor")]
    [InlineData(4, "f:2", "character:belmakor")]
    [InlineData(8, "f:1", "dm")]
    [InlineData(11, "f:7", "character:serif")]
    [InlineData(12, "f:7", "character:aiden-ironstar")]
    [InlineData(15, "f:5", "character:vars")]
    [InlineData(16, "f:5", "party")]
    [InlineData(16, "f:5", "public")]
    public void Get_RowsNotKnown_AreNotFoundForThatPerspective(int row, string handle, string perspective)
    {
        var ex = Assert.Throws<Domain.Core.DndInputException>(() =>
            world.Reads.Get(world.Campaign, perspective, EntityIncludes.All, null, handle));

        Assert.True(ex.Message.Contains("nothing by that handle for this perspective", StringComparison.Ordinal), $"row {row}: {ex.Message}");
    }

    /// <summary>
    /// Row 6: the author knows both facts, and the author's own rows name the item "the Axiom Cage" and the king "Keras"
    /// (k6, k3): the author view of campaign_get lists every knower's row, the author's included.
    /// </summary>
    [Fact]
    public void Get_Row6AsAuthor_ShowsBothFactsKnownAndTheAuthorsOwnNames()
    {
        var result = world.Reads.Get(world.Campaign, "author", new EntityIncludes(Facts: true, Knowledge: true), null,
            "character:old-king", "item:thing-he-wants", "f:1", "f:2");

        var king = result.Entities.Single(e => e.Ref == "character:old-king");
        var item = result.Entities.Single(e => e.Ref == "item:thing-he-wants");
        Assert.Contains(king.Knowledge!, k => k.Knower == "author" && k.Target == "character:old-king" && k.KnownAs == "Keras");
        Assert.Contains(item.Knowledge!, k => k.Knower == "author" && k.Target == "item:thing-he-wants" && k.KnownAs == "the Axiom Cage");
        Assert.All(result.Facts, f => Assert.Contains(f.Knowledge!, k => k.Knower == "author" && k.Standing == Standings.Knows));
    }

    /// <summary>Row 7 through campaign_get: the dm opens f:2 and her line says she knows it (k12, told).</summary>
    [Fact]
    public void Get_Row7AsDm_OpensF2AndKnowsIt()
    {
        var fact = world.Reads.Get(world.Campaign, "dm", new EntityIncludes(Knowledge: true), null, "f:2").Facts.Single();

        Assert.Equal("The old king's name is Keras.", fact.Text);
        Assert.Equal((Standings.Knows, "knows"), (fact.Knowledge![0].Standing, fact.Knowledge[0].State));
    }

    /// <summary>
    /// Rows 11 and 12 come from attendance, not from a knowledge row: Serif's attendance row says absent, Aiden has none for
    /// S1 while others do (NotListed). Both are Uncertain, never Knows: a missing row must not read as present.
    /// </summary>
    [Theory]
    [InlineData("character:serif", "Serif was absent")]
    [InlineData("character:aiden-ironstar", "no attendance recorded for Aiden Ironstar in S1")]
    public void Ledger_Rows11And12AttendanceWrittenByTheSessionWriter_DecidesUncertain(string perspective, string because)
    {
        var cell = world.Reads.Cell(world.Campaign, "f:7", perspective);

        Assert.Equal(Standings.Uncertain, cell.Standing);
        Assert.Contains(because, cell.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rows 17-19, point in time: the table learned of the Contingency in S2 (and f:6 was restricted until the session-2
    /// batch made it party-visible), so as of S1 the table has no record of it and as of S2 it knows; Belmakor always knew
    /// (his row has no session). Asked through the ledger and through campaign_get.
    /// </summary>
    [Theory]
    [InlineData(17, "table", 1, Standings.NoRecord)]
    [InlineData(18, "table", 2, Standings.Knows)]
    [InlineData(19, "character:belmakor", 1, Standings.Knows)]
    public void Ledger_Rows17To19ContingencyAsOfASession_FollowsTheLearnedSessions(int row, string perspective, int asOf, string standing)
    {
        var cell = world.Reads.Cell(world.Campaign, "f:6", perspective, asOf);
        var get = world.Reads.TryGet(world.Campaign, perspective, "f:6", asOf: asOf);

        Assert.True(standing == cell.Standing, $"row {row}: {cell.Standing} / {cell.Text}");
        Assert.Equal(standing == Standings.Knows, get is not null);
    }

    /// <summary>
    /// Row 17's other half: today the table knows f:6. The as_of answer is the past, not a different rule: without it the
    /// same ledger cell says knows.
    /// </summary>
    [Fact]
    public void Ledger_Row17WithoutAsOf_TheTableKnowsTheContingencyToday()
    {
        Assert.Equal(Standings.Knows, world.Reads.Cell(world.Campaign, "f:6", "table").Standing);
    }
}

/// <summary>
/// Invariant: the contract's stage-1 decision for the table and the dm (the rule Belmakor row 8 rests on), where it bites:
/// a party-visible fact the party is recorded as NOT knowing. The table and the dm contain the party, so the party's
/// "unaware" row decides for them as "no record", never the party-visible default "knows"; the party and a member with no
/// row of his own do not know it. Without the rule the table's view would show what the party is recorded as not knowing,
/// and a recap read aloud from it would tell them.
/// </summary>
public sealed class BelmakorContainedKnowerScenarioTests : IDisposable
{
    private readonly BelmakorScenario _world = new();

    public void Dispose() => _world.Dispose();

    [Theory]
    [InlineData("table", Standings.NoRecord)]
    [InlineData("dm", Standings.NoRecord)]
    [InlineData("party", Standings.DoesNotKnow)]
    [InlineData("character:ignis", Standings.DoesNotKnow)]
    public void Ledger_APartyVisibleFactThePartyIsUnawareOf_IsKnownToNoViewThatContainsTheParty(string perspective, string standing)
    {
        var fact = _world.F.Apply(_world.Campaign, Repository.Campaign.Write.WriteContext.For(3),
            new Domain.Campaign.Ops.CampaignOpSpec
            {
                Op = "fact", Statement = "The kraken had a second head nobody saw.", CanonStatus = "played", EstablishedSession = 3, Visibility = "party",
                KnownBy = [new Domain.Campaign.Ops.KnowerSpec { Who = "party", State = "unaware" }],
            }).Applied[0].Ref;

        var cell = _world.Reads.Cell(_world.Campaign, fact, perspective);

        Assert.Equal(standing, cell.Standing);
        Assert.Null(_world.Reads.TryGet(_world.Campaign, perspective, fact));
        Assert.Empty(_world.Reads.Search(_world.Campaign, "second head", perspective).Facts);
    }
}
