using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign.Read;
using Xunit;

namespace DndMcp.Tests.CampaignRead;

/// <summary>
/// Invariant: campaign_get of the One Piece campaign shows the withheld question as open with nothing of its answer (row
/// 37), the Protector only as "the advisor in Serret" (row 39), and, for the author, the seal secret's routes and status
/// as the gates give them at each step of the scenario (rows 16-24, read side): hidden → seeded → partial → ready →
/// revealed. The status is derived from the same verdicts the reads use, so a wrong "party knows" shows up here.
/// </summary>
public sealed class OnePieceGetTests
{
    private static GetResult Get(OnePieceFixture world, string perspective, EntityIncludes includes, int? asOf, params string[] refs) =>
        new EntityReader(world.Db.Database).Get(world.CampaignRow, refs, includes, Perspective.Parse(perspective), asOf);

    [Theory]
    [InlineData("party")]
    [InlineData("table")]
    [InlineData("character:bjorn-mountainfell")]
    public void Get_Row37WithheldQuestionAsAPlayerView_IsOpenWithNoAnswer(string perspective)
    {
        using var world = new OnePieceFixture();

        var result = Get(world, perspective, EntityIncludes.All, null, "question:q7");

        var q7 = Assert.Single(result.Entities);
        Assert.Equal("open", q7.Status);
        Assert.Equal("Q7", q7.Code);
        Assert.Null(q7.Author);
        Assert.Empty(q7.Facts!);
        LeakAssert.Clean(result, ["withheld", "founders", "rope en masse", "fleet", "Direction", "lean"], $"row 37 as {perspective}");
    }

    [Fact]
    public void Get_Row39ProtectorAsParty_IsTheAdvisorInSerretAndHisSlugFindsNothing()
    {
        using var world = new OnePieceFixture();

        var bySeq = Get(world, "party", EntityIncludes.All, null, "e:" + world.Protector.Seq);
        var bySlug = Assert.Throws<DndInputException>(() => Get(world, "party", EntityIncludes.All, null, "character:protector"));

        var advisor = Assert.Single(bySeq.Entities);
        Assert.Equal("the advisor in Serret", advisor.DisplayName);
        Assert.Null(advisor.Summary);
        var knowledge = Assert.Single(advisor.Knowledge!);
        Assert.Equal(("met", "the advisor in Serret", 1), (knowledge.State, knowledge.KnownAs, knowledge.LearnedSession));
        Assert.Equal("the party's own record: met, S1", knowledge.Explanation);
        LeakAssert.Clean(bySeq, ["Protector", "protector", "Keras", "fragment", "Battle Advisor", "unrecognized"], "row 39");
        LeakAssert.CleanMessage(bySlug.Message, "character:protector", ["advisor", "Serret", "e:"], "row 39 by slug");
    }

    [Fact]
    public void Get_Row16SecretAsOfSession5_IsHiddenWithNoRouteProgress()
    {
        using var world = new OnePieceFixture();

        var secret = Secret(world, asOf: 5);

        Assert.Equal(SecretStatus.Hidden, secret.DerivedStatus);
        var gate = Assert.Single(secret.Gates);
        Assert.Equal([("testimonies", 0, 4), ("illusion", 0, 1), ("temple-arithmetic", 0, 2)], gate.Routes.Select(r => (r.Id, r.Known, r.Needed)));
        Assert.Equal((0, 2), (gate.RoutesComplete, gate.MinRoutes));
    }

    [Fact]
    public void Get_Rows17To24SecretAcrossTheScenario_FollowsHiddenSeededPartialReadyRevealed()
    {
        using var world = new OnePieceFixture();

        var baseline = Secret(world);
        world.PartyLearns(world.TProtector, 8);
        var t1 = Secret(world);
        world.PartyLearns(world.IllusionRewatched, 9);
        var t2 = Secret(world);
        world.PartyLearns(world.BreachesClimbing, 10);
        world.PartyLearns(world.FruitFalling, 10);
        var t3 = Secret(world);
        world.AssembleAxe();
        var t4 = Secret(world);
        world.PartyLearns(world.Seal, 12);
        world.PartyLearns(world.NadarPlan, 12);
        var t5 = Secret(world);

        Assert.Equal(SecretStatus.Seeded, baseline.DerivedStatus);
        Assert.Equal((0, 2), (baseline.Gates[0].RoutesComplete, baseline.Gates[0].MinRoutes));
        Assert.Equal(SecretStatus.Seeded, t1.DerivedStatus);
        Assert.Equal(1, t1.Gates[0].Routes.Single(r => r.Id == "testimonies").Known);
        Assert.Equal(SecretStatus.Partial, t2.DerivedStatus);
        Assert.Equal(["illusion"], t2.Gates[0].CompleteRouteIds);
        Assert.False(t2.Gates[0].Ready);
        Assert.Equal(SecretStatus.Partial, t3.DerivedStatus);
        Assert.Equal(["illusion", "temple-arithmetic"], t3.Gates[0].CompleteRouteIds);
        Assert.Equal([world.AxeAssembled.SeqHandle], t3.Gates[0].UnmetAfter);
        Assert.True(t3.Gates[0].ReachableBeforeGate);
        Assert.True(t4.Gates[0].Ready);
        Assert.Equal([world.NadarPlan.SeqHandle], t4.Gates[0].MustLandWith);
        Assert.False(t4.Gates[0].ForbiddenActive);
        Assert.Equal(SecretStatus.Revealed, t5.DerivedStatus);
        Assert.True(t5.Gates[0].KnownToParty);
    }

    /// <summary>
    /// Contract §3.4: a gated fact the party suspects makes the secret partial, never revealed (a suspicion is not
    /// knowledge; the write path stores it the same way); knowing it reveals it.
    /// </summary>
    [Theory]
    [InlineData(CampaignValues.KnowledgeStates.Suspects, SecretStatus.Partial, false)]
    [InlineData(CampaignValues.KnowledgeStates.Knows, SecretStatus.Revealed, true)]
    public void Get_SecretWhoseGatedFactThePartySuspectsOrKnows_IsPartialOrRevealed(string state, string derived, bool knownToParty)
    {
        using var world = new OnePieceFixture();
        new DndMcp.Tests.CampaignDb.CampaignSeed(world.Connection).Knowledge(world.Campaign.Id, "party", state: state, factId: world.Seal.Id,
            learnedSessionId: world.Sessions[7].EntityId);

        var secret = Secret(world);

        Assert.Equal(derived, secret.DerivedStatus);
        Assert.Equal(knownToParty, secret.Gates.Single().KnownToParty);
    }

    /// <summary>The write path's partial: knowing one gated fact of several is part of the secret, not all of it.</summary>
    [Fact]
    public void Get_SecretWithTwoGatedFactsOneKnown_IsPartialThenRevealed()
    {
        using var world = new OnePieceFixture();
        var seed = new DndMcp.Tests.CampaignDb.CampaignSeed(world.Connection);
        seed.FactLink(world.NadarPlan.Id, world.Secret.Id);

        world.PartyLearns(world.Seal, 7);
        var one = Secret(world);
        world.PartyLearns(world.NadarPlan, 7);
        var both = Secret(world);

        Assert.Equal(SecretStatus.Partial, one.DerivedStatus);
        Assert.Equal(SecretStatus.Revealed, both.DerivedStatus);
    }

    [Fact]
    public void Get_SealFactAsAuthor_PrintsItsGateWithFactHandlesNeverIds()
    {
        using var world = new OnePieceFixture();

        var result = Get(world, "author", EntityIncludes.All, null, world.Seal.SeqHandle);

        var gate = Assert.Single(result.Facts).Author!.Fact.Gate!;
        Assert.Equal([world.AxeAssembled.SeqHandle, world.HoldsFruit.SeqHandle], gate.After);
        Assert.Equal([world.NadarPlan.SeqHandle], gate.With);
        Assert.Equal(4, gate.Routes![0]!.Clues!.Count);
        Assert.All(gate.Routes!.SelectMany(r => r!.Clues!), c => Assert.StartsWith("f:", c, StringComparison.Ordinal));
        Assert.DoesNotContain(world.AxeAssembled.Id, LeakAssert.Serialize(result), StringComparison.Ordinal);
    }

    /// <summary>A stored gate that does not read (a hand edit) is reported to the author and ignored; no read fails on it.</summary>
    [Fact]
    public void Get_FactWithAnUnreadableGate_IsReadAndFlaggedForTheAuthorOnly()
    {
        using var world = new OnePieceFixture();
        Dapper.SqlMapper.Execute(world.Connection, "UPDATE fact SET gate = '{\"after\": \"f:1\"}' WHERE id = @id", new { id = world.Seal.Id });

        var author = Get(world, "author", EntityIncludes.All, null, world.Seal.SeqHandle, "secret:fruits-are-the-seal");

        Assert.True(author.Facts[0].Author!.Fact.GateUnreadable);
        Assert.Null(author.Facts[0].Author!.Fact.Gate);
        Assert.True(author.Entities[0].Author!.Secret!.Gates[0].Unreadable);
    }

    [Fact]
    public void Get_QuestAsParty_ShowsItsObjectiveProgress()
    {
        using var world = new OnePieceFixture();

        var axe = Assert.Single(Get(world, "party", EntityIncludes.Default, null, "quest:war-gods-axe").Entities);

        var objective = Assert.Single(axe.Objectives);
        Assert.Equal((1, "Assemble the War God's axe", 2L, (long?)null), (objective.Index, objective.Text, objective.Progress, objective.ProgressMax));
        Assert.Null(objective.Visibility);
    }

    private static SecretStatusView Secret(OnePieceFixture world, int? asOf = null) =>
        Assert.Single(Get(world, "author", EntityIncludes.Default, asOf, "secret:fruits-are-the-seal").Entities).Author!.Secret!;

    private static class SecretStatus
    {
        public const string Hidden = "hidden";
        public const string Seeded = "seeded";
        public const string Partial = "partial";
        public const string Revealed = "revealed";
    }
}
