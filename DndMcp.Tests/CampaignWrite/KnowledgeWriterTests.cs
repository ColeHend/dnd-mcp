using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Write;
using Xunit;

namespace DndMcp.Tests.CampaignWrite;

/// <summary>
/// Invariant: campaign_knowledge record / reveal / retract write one row per knower per target (updating, never
/// duplicating), default the learned session to the session context but never move a row that already knew, never
/// clear a recorded learned session when a state changes with no session context, refuse knowers and targets that do
/// not resolve with nothing written, and warn (not fail) on a retract of nothing. Moving a learned session would change
/// who was "present" for a reveal, and clearing one makes the row apply at every session; a duplicate row would make the
/// verdict a coin toss.
/// </summary>
public sealed class KnowledgeWriterTests : IDisposable
{
    private readonly WriteFixture _f = new();
    private readonly CampaignRow _c;

    public KnowledgeWriterTests()
    {
        _c = _f.Campaign("Belmakor", role: "player", myCharacter: "Belmakor Silverwind");
        _f.Apply(_c, Op.Upsert("character", "Ignis", "pc"), Op.Upsert("character", "The Old King", "npc"),
            Op.Fact("Tristan died on the ocean job.", visibility: "party"), Op.Fact("The thing is the Axiom Cage.", visibility: "author"));
        _f.Played(_c, 1);
        _f.Played(_c, 2);
    }

    public void Dispose() => _f.Dispose();

    private KnowledgeRow Row(string factOrEntity, string knowerKind, string? knowerId = null)
    {
        var column = factOrEntity.StartsWith("f:", StringComparison.Ordinal) ? "fact_id" : "entity_id";
        var id = column == "fact_id" ? _f.Fact(_c, factOrEntity).Id : _f.Entity(_c, factOrEntity).Id;
        return Assert.Single(_f.Query<KnowledgeRow>(
            $"SELECT {KnowledgeRow.Columns} FROM knowledge WHERE {column} = @id AND knower_kind = @knowerKind AND knower_id IS @knowerId",
            new { id, knowerKind, knowerId }));
    }

    private string Session(int number) => _f.Entity(_c, "session:" + number).Id;

    [Fact]
    public void Record_NoSessionGiven_LearnedInTheLiveSession()
    {
        _f.Sessions.Start(_c, 3);

        var result = _f.Knowledge.Record(_c, ["f:1"], [Op.Knower("party")], WriteContext.Default);

        Assert.Equal(3, result.SessionNumber);
        Assert.Equal((Session(3), "knows"), (Row("f:1", "party").LearnedSessionId, Row("f:1", "party").State));
        var applied = Assert.Single(result.Applied);
        Assert.Equal(("record", "f:1", WriteOutcomes.Created, "party"), (applied.Op, applied.Ref, applied.Outcome, applied.Knower));
    }

    [Fact]
    public void Record_ExplicitSession_IsUsedAndMustExist()
    {
        _f.Knowledge.Record(_c, ["f:1"], [Op.Knower("party", session: 1)], WriteContext.Default);

        var ex = Assert.Throws<DndInputException>(() => _f.Knowledge.Record(_c, ["f:1"], [Op.Knower("table", session: 9)], WriteContext.Default));

        Assert.Equal(Session(1), Row("f:1", "party").LearnedSessionId);
        Assert.Contains("targets item 1 (f:1): knowers item 1: session: no session 9 in this campaign", ex.Message);
        // Review U08: the hint fits a session played now (start), already (record_past) or still to come (plan).
        Assert.EndsWith("no session 9 in this campaign. Start it if it is being played now: campaign_session {\"action\": \"start\", " +
                        $"\"session\": 9, \"campaign\": \"{_c.Slug}\"}}. If it was played already, send the same call with \"record_past\" " +
                        "instead of \"start\"; if it is still to come, with \"plan\".", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Record_AgainWhileStillAware_KeepsTheSessionItWasLearnedIn()
    {
        _f.Knowledge.Record(_c, ["character:the-old-king"], [Op.Knower("party", "met", "the old king")], WriteContext.For(1));

        var result = _f.Knowledge.Record(_c, ["character:the-old-king"], [Op.Knower("party", "met", "the sorcerer king")], WriteContext.For(2));

        var row = Row("character:the-old-king", "party");
        Assert.Equal((Session(1), "the sorcerer king"), (row.LearnedSessionId, row.KnownAs));
        var applied = Assert.Single(result.Applied);
        Assert.Equal(WriteOutcomes.Updated, applied.Outcome);
        Assert.Equal(["known_as"], applied.ChangedFields);
        Assert.Equal(1, _f.Count("SELECT count(*) FROM knowledge WHERE knower_kind = 'party'"));
    }

    [Fact]
    public void Record_FromUnawareToKnowing_TakesTheNewSession()
    {
        _f.Knowledge.Record(_c, ["f:2"], [Op.Knower("party", "unaware")], WriteContext.For(1));

        _f.Knowledge.Record(_c, ["f:2"], [Op.Knower("party")], WriteContext.For(2));

        Assert.Equal((Session(2), "knows"), (Row("f:2", "party").LearnedSessionId, Row("f:2", "party").State));
    }

    [Fact]
    public void Record_FromKnowingToForgetting_TakesTheNewSession()
    {
        _f.Knowledge.Record(_c, ["f:1"], [Op.Knower("character:ignis")], WriteContext.For(1));

        _f.Knowledge.Record(_c, ["f:1"], [Op.Knower("character:ignis", "forgot")], WriteContext.For(2));

        var row = Row("f:1", "character", _f.Entity(_c, "character:ignis").Id);
        Assert.Equal((Session(2), "forgot"), (row.LearnedSessionId, row.State));
    }

    /// <summary>
    /// Review C02: a state change written between sessions (no session context) keeps the session the row records. NULL
    /// would apply at every session, so the party would have "known" the fact as of every earlier one.
    /// </summary>
    [Theory]
    [InlineData("suspects", "knows")]
    [InlineData("unaware", "knows")]
    [InlineData("knows", "forgot")]
    public void Record_StateChangeWithNoSessionContext_KeepsTheRecordedLearnedSession(string first, string then)
    {
        _f.Knowledge.Record(_c, ["f:1"], [Op.Knower("party", first)], WriteContext.For(2));

        var result = _f.Knowledge.Record(_c, ["f:1"], [Op.Knower("party", then)], WriteContext.Default);

        Assert.Null(result.SessionNumber);
        Assert.Equal((Session(2), then), (Row("f:1", "party").LearnedSessionId, Row("f:1", "party").State));
        Assert.Equal(["state"], Assert.Single(result.Applied).ChangedFields);
    }

    [Fact]
    public void Record_StateChangeWithNoSessionContext_LeavesThePartyNotKnowingItAsOfAnEarlierSession()
    {
        _f.Knowledge.Record(_c, ["f:1"], [Op.Knower("party", "suspects")], WriteContext.For(2));

        _f.Knowledge.Record(_c, ["f:1"], [Op.Knower("party", "knows")], WriteContext.Default);

        using var connection = _f.Open();
        var loader = new KnowledgeLoader(connection, _c);
        var fact = _f.Fact(_c, "f:1");
        var asOfOne = KnowledgeVerdicts.Evaluate(loader.Resolve(Perspective.Parse("party")), loader.EntriesForFacts([fact.Id])[fact.Id],
            fact.Visibility, loader.Attendance, asOfSession: 1);
        Assert.Equal(KnowledgeStanding.NoRecord, asOfOne.Standing);
    }

    [Fact]
    public void Record_StateChangeWithAnExplicitSessionAndNoContext_TakesTheExplicitSession()
    {
        _f.Knowledge.Record(_c, ["f:1"], [Op.Knower("party", "suspects")], WriteContext.For(2));

        _f.Knowledge.Record(_c, ["f:1"], [Op.Knower("party", "knows", session: 1)], WriteContext.Default);

        Assert.Equal(Session(1), Row("f:1", "party").LearnedSessionId);
    }

    [Fact]
    public void Record_SameAgain_IsUnchangedAndLogsNothing()
    {
        _f.Knowledge.Record(_c, ["f:1"], [Op.Knower("party")], WriteContext.For(1));

        var result = _f.Knowledge.Record(_c, ["f:1"], [Op.Knower("party")], WriteContext.For(1));

        Assert.Equal(WriteOutcomes.Unchanged, Assert.Single(result.Applied).Outcome);
        Assert.Empty(_f.Log(result.BatchId));
    }

    [Fact]
    public void Record_TwoTargetsTwoKnowers_WritesFourRows()
    {
        var result = _f.Knowledge.Record(_c, ["f:1", "character:the-old-king"],
            [Op.Knower("character:belmakor-silverwind", "knows", "the old king"), new KnowerSpec { Who = "character:ignis", Via = "character:the-old-king", How = "told", Note = "at the statue" }],
            WriteContext.For(2));

        Assert.Equal(4, result.Applied.Count);
        Assert.Equal(["character:belmakor-silverwind", "character:ignis", "character:belmakor-silverwind", "character:ignis"], result.Applied.Select(a => a.Knower));
        var ignis = Row("f:1", "character", _f.Entity(_c, "character:ignis").Id);
        Assert.Equal((_f.Entity(_c, "character:the-old-king").Id, "told", "at the statue"), (ignis.ViaEntityId, ignis.How, ignis.Note));
    }

    [Theory]
    [InlineData("character:nobody", "who: no character nobody in this campaign")]
    [InlineData("character:the-party", "who: no character the-party in this campaign")]
    [InlineData("the guild", "is not a knower")]
    public void Record_KnowerThatIsNotACharacterHere_IsRefused(string who, string expected)
    {
        var ex = Assert.Throws<DndInputException>(() => _f.Knowledge.Record(_c, ["f:1"], [Op.Knower(who)], WriteContext.Default));

        Assert.Contains(expected, ex.Message);
        Assert.Equal(0, _f.Count("SELECT count(*) FROM knowledge"));
    }

    [Theory]
    [InlineData(new string[0], "targets is required")]
    [InlineData(new[] { "f:1", "f:1" }, "targets item 2: f:1 is listed twice")]
    [InlineData(new[] { "session:1" }, "is a session")]
    [InlineData(new[] { "other/character:x" }, "is another campaign's")]
    public void Record_BadTargets_AreRefusedBeforeWriting(string[] targets, string expected)
    {
        var ex = Assert.Throws<DndInputException>(() => _f.Knowledge.Record(_c, targets, [Op.Knower("party")], WriteContext.Default));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Record_UnknownTarget_IsRefused()
    {
        var ex = Assert.Throws<DndInputException>(() => _f.Knowledge.Record(_c, ["f:1", "f:40"], [Op.Knower("party")], WriteContext.Default));

        // f:<n> numbers are shared by every campaign in the file (review UR3); campaign_knowledge makes no facts, so the
        // advice to give a fact made earlier in the batch a code is campaign_write's alone.
        Assert.Contains("targets item 2 (f:40): fact: no fact f:40 in this campaign; give f:<n> or its code. f:<n> numbers are shared by all campaigns.",
            ex.Message);
        Assert.DoesNotContain("earlier in this batch", ex.Message);
        Assert.Equal(0, _f.Count("SELECT count(*) FROM knowledge"));
    }

    /// <summary>
    /// Review UR1: a blank known_as is the natural guess for "they use the true name now", and it is refused, because
    /// leaving known_as out keeps the current one; the refusal says what does it instead (the true name as known_as), and
    /// that works: the party's row then names the entity by its own name.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Record_BlankKnownAs_IsRefusedSayingTheTrueNameEndsADisguise(string blank)
    {
        _f.Knowledge.Record(_c, ["character:the-old-king"], [Op.Knower("party", "met", "the old king")], WriteContext.Default);

        var ex = Assert.Throws<DndInputException>(() =>
            _f.Knowledge.Record(_c, ["character:the-old-king"], [Op.Knower("party", "met", blank)], WriteContext.Default));
        _f.Knowledge.Record(_c, ["character:the-old-king"], [Op.Knower("party", "met", "The Old King")], WriteContext.Default);

        Assert.Equal("Invalid knowledge: knowers item 1 (party): known_as is blank; give text or leave it out; to record that they now use the true " +
                     "name, give the true name as known_as.", ex.Message);
        Assert.Equal(("met", "The Old King"), (Row("character:the-old-king", "party").State, Row("character:the-old-king", "party").KnownAs));
    }

    [Fact]
    public void Record_NoKnowers_IsRefused()
    {
        var ex = Assert.Throws<DndInputException>(() => _f.Knowledge.Record(_c, ["f:1"], [], WriteContext.Default));

        Assert.Contains("knowers is required", ex.Message);
    }

    [Fact]
    public void Record_DryRun_WritesNothing()
    {
        var result = _f.Knowledge.Record(_c, ["f:1"], [Op.Knower("party")], new WriteContext { DryRun = true });

        Assert.Null(result.BatchId);
        Assert.Equal(WriteOutcomes.Created, Assert.Single(result.Applied).Outcome);
        Assert.Equal(0, _f.Count("SELECT count(*) FROM knowledge"));
    }

    [Fact]
    public void Record_AwareRowOnAnAuthorFact_WarnsButStores()
    {
        var result = _f.Knowledge.Record(_c, ["f:2"], [Op.Knower("table")], WriteContext.Default);

        Assert.Equal(WarningKinds.AuthorVisibility, Assert.Single(result.Warnings).Kind);
        Assert.Equal("knows", Row("f:2", "table").State);
    }

    [Fact]
    public void Record_DmRowInAPlayerCampaign_IsANonAuthorKnower()
    {
        var result = _f.Knowledge.Record(_c, ["f:2"], [Op.Knower("dm")], WriteContext.Default);

        Assert.Equal(WarningKinds.AuthorVisibility, Assert.Single(result.Warnings).Kind);
    }

    [Fact]
    public void Reveal_DefaultsToThePartyKnowingInTheContextSession()
    {
        var result = _f.Knowledge.Reveal(_c, ["f:1"], null, null, null, "witnessed", WriteContext.For(1));

        var row = Row("f:1", "party");
        Assert.Equal(("knows", Session(1), "witnessed"), (row.State, row.LearnedSessionId, row.How));
        Assert.Equal("reveal", Assert.Single(result.Applied).Op);
        Assert.All(_f.Log(result.BatchId), r => Assert.Equal(("reveal", "campaign_knowledge/reveal"), (r.Action, r.Tool)));
    }

    [Fact]
    public void Reveal_Handout_RevealsItsFactsAndIsDelivered()
    {
        _f.Apply(_c, Op.Upsert("handout", "The ship's log"),
            new CampaignOpSpec { Op = "fact", Statement = "The log names the harbour.", About = ["handout:the-ships-log"] });

        var result = _f.Knowledge.Reveal(_c, null, null, "handout:the-ships-log", ["party", "character:ignis"], "read", WriteContext.For(2));

        Assert.Equal("knows", Row("f:3", "party").State);
        Assert.Equal("delivered", _f.Entity(_c, "handout:the-ships-log").Status);
        Assert.Equal(["f:3", "f:3", "handout:the-ships-log"], result.Applied.Select(a => a.Ref));
    }

    [Fact]
    public void Reveal_Nothing_IsRefused()
    {
        var ex = Assert.Throws<DndInputException>(() => _f.Knowledge.Reveal(_c, null, null, null, null, null, WriteContext.Default));

        Assert.Contains("give facts (fact handles), secret (a secret entity) or handout", ex.Message);
    }

    [Fact]
    public void Reveal_SecretThatIsNotASecret_IsRefused()
    {
        var ex = Assert.Throws<DndInputException>(() => _f.Knowledge.Reveal(_c, null, "character:ignis", null, null, null, WriteContext.Default));

        Assert.Contains("no secret", ex.Message);
    }

    [Fact]
    public void Retract_DeletesTheRowAndLogsItWhole()
    {
        _f.Knowledge.Record(_c, ["f:1"], [Op.Knower("party", knownAs: "the drowning")], WriteContext.For(1));

        var result = _f.Knowledge.Retract(_c, ["f:1"], ["party"], WriteContext.Default);

        Assert.Equal(0, _f.Count("SELECT count(*) FROM knowledge"));
        var row = Assert.Single(_f.Log(result.BatchId));
        Assert.Equal(("delete", "retract", "knowledge"), (row.Op, row.Action, row.TargetTable));
        Assert.Contains("\"known_as\":\"the drowning\"", row.OldValue);
    }

    [Fact]
    public void Retract_Missing_WarnsAndChangesNothing()
    {
        var result = _f.Knowledge.Retract(_c, ["f:1"], ["character:ignis"], WriteContext.Default);

        Assert.Equal(WriteOutcomes.Unchanged, Assert.Single(result.Applied).Outcome);
        Assert.Contains("character:ignis has no row on f:1; nothing to retract.", Assert.Single(result.Warnings).Message);
    }

    [Fact]
    public void Retract_NoWho_IsRefused()
    {
        var ex = Assert.Throws<DndInputException>(() => _f.Knowledge.Retract(_c, ["f:1"], [], WriteContext.Default));

        Assert.Contains("who is required", ex.Message);
    }

    [Fact]
    public void Reveal_SecretWithGatedAndUngatedAboutFacts_RevealsOnlyTheGatedOnes()
    {
        _f.Apply(_c, Op.Upsert("secret", "The thing he wants"),
            new CampaignOpSpec { Op = "fact", Statement = "It is the Cage.", About = ["secret:the-thing-he-wants"], Gate = new GateSpec { After = ["f:1"] } },
            new CampaignOpSpec { Op = "fact", Statement = "He wants something from the island.", About = ["secret:the-thing-he-wants"] });

        var result = _f.Knowledge.Reveal(_c, null, "secret:the-thing-he-wants", null, null, "deduced", WriteContext.Default);

        Assert.Equal(["f:3", "secret:the-thing-he-wants"], result.Applied.Select(a => a.Ref));
        Assert.Equal("knows", Row("f:3", "party").State);
        Assert.Equal(0, _f.Count("SELECT count(*) FROM knowledge WHERE fact_id = @id", new { id = _f.Fact(_c, "f:4").Id }));
    }

    [Fact]
    public void Reveal_SecretWithNoGatedFact_RevealsAllItsAboutFacts()
    {
        _f.Apply(_c, Op.Upsert("secret", "The thing he wants"),
            new CampaignOpSpec { Op = "fact", Statement = "He wants something from the island.", About = ["secret:the-thing-he-wants"] },
            new CampaignOpSpec { Op = "fact", Statement = "It is heavy.", About = ["secret:the-thing-he-wants"] });

        var result = _f.Knowledge.Reveal(_c, null, "secret:the-thing-he-wants", null, null, null, WriteContext.Default);

        Assert.Equal(["f:3", "f:4", "secret:the-thing-he-wants"], result.Applied.Select(a => a.Ref));
    }
}
