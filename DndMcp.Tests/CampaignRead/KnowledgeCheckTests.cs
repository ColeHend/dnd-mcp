using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign.Read;
using Xunit;

namespace DndMcp.Tests.CampaignRead;

/// <summary>
/// Invariant: the knowledge check flags exactly the Belmakor diegetic goldens (<c>understand-belmakor.md</c> §3 rows
/// 36-46): "Old king, come down" passes; "the Axiom Cage" is another name for the thing Belmakor knows as "the thing he
/// wants"; "Keras" is both an author alias and a name from the other campaign, and the no-name rule's term; a
/// "nine-hundred-year-old" timespan breaks the rule's pattern; his ambition put in a song is a secret at risk; the author
/// is never flagged. A check that passed row 38 would let a lyric name the Cage.
/// </summary>
public sealed class BelmakorCheckTests(BelmakorFixture fixture) : IClassFixture<BelmakorFixture>
{
    private CheckResult Check(string text, string speaker, bool diegetic, string? audience = null) =>
        new KnowledgeCheck(fixture.Db.Database).Check(fixture.CampaignRow,
            new CheckRequest(text, Perspective.Parse(speaker), diegetic, audience is null ? null : Perspective.Parse(audience)));

    [Theory]
    [InlineData("Old king, come down")]
    [InlineData("Old king came down to a knee")]
    public void Check_Rows36And37TheKingByTheNameBelmakorUses_Passes(string text)
    {
        var result = Check(text, "character:belmakor", diegetic: true);

        Assert.True(result.Pass);
        var mention = Assert.Single(result.Names);
        Assert.Equal(("Old king", NameClasses.Ok, NameClasses.Ok), (mention.Matched, mention.Classification, mention.AudienceClassification));
        var king = Assert.Single(mention.Candidates);
        Assert.Equal(("character:old-king", "The Old King"), (king.Ref, king.SpeakerName));
        Assert.Empty(result.Forbidden);
        Assert.DoesNotContain(result.SecretsAtRisk, r => r.Reason == RiskReasons.RelatedToText);
    }

    [Theory]
    [InlineData("character:belmakor")]
    [InlineData("party")]
    public void Check_Rows38And39AxiomCageInASong_IsAnotherNameForTheThingHeWants(string speaker)
    {
        var result = Check("We'll haul the Axiom Cage back up to the old king", speaker, diegetic: true);

        Assert.False(result.Pass);
        var cage = Assert.Single(result.Names, n => n.Matched == "Axiom Cage");
        Assert.Equal(NameClasses.OtherName, cage.Classification);
        var item = Assert.Single(cage.Candidates, c => c.Ref == "item:thing-he-wants");
        Assert.Equal(("alias", "author", "the thing he wants"), (item.MatchedAs, item.MatchedVisibility, item.SpeakerName));
        Assert.Contains(cage.Candidates, c => c.Classification == NameClasses.CrossCampaign && c.Ref == "one-piece/item:axiom-cage");
        Assert.Equal(NameClasses.Ok, result.Names.Single(n => n.Matched == "old king").Classification);
        var f1 = Assert.Single(result.UnknownFacts, f => f.Ref == fixture.F1.SeqHandle);
        Assert.Equal(["author"], f1.KnownBy);
        Assert.Equal("unaware", f1.State);
    }

    [Fact]
    public void Check_Row40SameTextAsAuthor_HasNoFlag()
    {
        var result = Check("We'll haul the Axiom Cage back up to the old king", "author", diegetic: false);

        Assert.True(result.Pass);
        Assert.All(result.Names, n => Assert.Equal(NameClasses.Ok, n.Classification));
        Assert.Empty(result.Forbidden);
        Assert.Empty(result.UnknownFacts);
    }

    [Fact]
    public void Check_Row41KerasComeDown_FlagsTheAliasTheFactAndTheRule()
    {
        var result = Check("Keras, come down", "character:belmakor", diegetic: true);

        Assert.False(result.Pass);
        var keras = Assert.Single(result.Names);
        Assert.Equal(NameClasses.OtherName, keras.Classification);
        Assert.Equal("The Old King", keras.Candidates.Single(c => c.Ref == "character:old-king").SpeakerName);
        Assert.Contains(keras.Candidates, c => c.Ref == "one-piece/character:keras" && c.Classification == NameClasses.CrossCampaign);
        Assert.Contains(result.UnknownFacts, f => f.Ref == fixture.F2.SeqHandle);
        var rule = Assert.Single(result.Forbidden);
        Assert.Equal(("rule:old-king-no-name-no-timespan", "Keras", "Keras"), (rule.Source, rule.TermOrPattern, rule.Matched));
    }

    [Fact]
    public void Check_Row42NineHundredYearOldSorcerer_BreaksTheTimespanPattern()
    {
        var result = Check("a nine-hundred-year-old sorcerer", "character:belmakor", diegetic: true);

        Assert.False(result.Pass);
        var hit = Assert.Single(result.Forbidden);
        Assert.Equal(("rule:old-king-no-name-no-timespan", "<n>-year-old", "nine-hundred-year-old"), (hit.Source, hit.TermOrPattern, hit.Matched));
    }

    [Fact]
    public void Check_Row43HisAmbitionInASong_IsASecretAtRisk()
    {
        var result = Check("I'll take back the ground below and make the dead world a kingdom", "character:belmakor", diegetic: true);

        Assert.False(result.Pass);
        var risk = Assert.Single(result.SecretsAtRisk, r => r.Fact.Ref == fixture.F5.SeqHandle);
        Assert.Equal((RiskReasons.RelatedToText, Standings.DoesNotKnow), (risk.Reason, risk.AudienceStanding));
    }

    [Fact]
    public void Check_Row44SameTextNotDiegetic_HeKnowsItSoItIsNotUnknown()
    {
        var result = Check("I'll take back the ground below and make the dead world a kingdom", "character:belmakor", diegetic: false);

        Assert.DoesNotContain(result.UnknownFacts, f => f.Ref == fixture.F5.SeqHandle);
        Assert.Contains(result.Related, f => f.Ref == fixture.F5.SeqHandle);
        Assert.Empty(result.SecretsAtRisk);
        Assert.Null(result.Audience);
    }

    [Fact]
    public void Check_Row45TheObliqueKingdomLine_HasNoHardFlag()
    {
        var result = Check("I thought, Lord, this could be a kingdom / If somebody stayed around.", "character:belmakor", diegetic: true);

        Assert.True(result.Pass);
        Assert.Empty(result.Forbidden);
        Assert.All(result.SecretsAtRisk, r => Assert.Equal(RiskReasons.AboutSpeaker, r.Reason));
        Assert.Contains(result.SecretsAtRisk, r => r.Fact.Ref == fixture.F5.SeqHandle);
    }

    [Fact]
    public void Check_Row46TristansElegy_PassesBecauseThePartyKnowsIt()
    {
        var result = Check("Tristan went down protecting Sky", "character:belmakor", diegetic: true);

        Assert.True(result.Pass);
        Assert.Equal(NameClasses.Ok, Assert.Single(result.Names).Classification);
        Assert.Contains(result.Related, f => f.Ref == fixture.F7.SeqHandle);
        Assert.DoesNotContain(result.UnknownFacts, f => f.Ref == fixture.F7.SeqHandle);
        Assert.Equal(["Sky"], result.PossibleInventions);
    }

    /// <summary>A name the speaker knows but the audience does not use is flagged for a diegetic text only.</summary>
    [Fact]
    public void Check_BelmakorsOwnNameForTheItemToThePublic_RevealsToTheAudience()
    {
        var result = Check("the thing he wants", "character:belmakor", diegetic: true, audience: "public");

        var mention = Assert.Single(result.Names);
        Assert.Equal((NameClasses.Ok, NameClasses.RevealsToAudience), (mention.Classification, mention.AudienceClassification));
        Assert.False(result.Pass);
    }

    [Fact]
    public void Check_TheVisibleNameOfAHiddenEntity_IsUnknownToTheSpeaker()
    {
        var result = Check("the Belmakor's ambition song", "character:vars", diegetic: false);

        var mention = Assert.Single(result.Names);
        Assert.Equal(NameClasses.UnknownEntity, mention.Classification);
        Assert.Null(mention.Candidates[0].SpeakerName);
    }

    /// <summary>
    /// Row 41, the audience side: "Keras" in a song is a name from the other campaign, which no audience of this one uses
    /// (the firewall), so it reveals to the audience whoever sings it, the author included.
    /// </summary>
    [Theory]
    [InlineData("character:belmakor")]
    [InlineData("author")]
    public void Check_Row41CrossCampaignNameInASong_RevealsToTheAudience(string speaker)
    {
        var result = Check("Keras, come down", speaker, diegetic: true);

        var keras = Assert.Single(result.Names);
        Assert.Equal(NameClasses.RevealsToAudience, keras.AudienceClassification);
        var other = Assert.Single(keras.Candidates, c => c.Campaign == "one-piece");
        Assert.Equal(("one-piece/character:keras", NameClasses.RevealsToAudience), (other.Ref, other.AudienceClassification));
        Assert.False(result.Pass);
    }

    [Fact]
    public void Check_EmptyOrOverlongText_IsRefused()
    {
        var empty = Assert.Throws<DndInputException>(() => Check("  ", "author", false));
        var tooLong = Assert.Throws<DndInputException>(() => Check(new string('a', 50_001), "author", false));

        Assert.Contains("text is required", empty.Message, StringComparison.Ordinal);
        Assert.Contains("50,000", tooLong.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// Invariant: the One Piece vocabulary and incomplete-account goldens (<c>understand-onepiece.md</c> §3 rows 1-8, 26, 27,
/// 30): "seal" and its inflections are forbidden for every non-author speaker until the axe is assembled (and again when
/// checking as of an earlier session), never for the author; the Mistaken One's missing half is an unknown fact while his
/// partial belief is shown as such; a fact resting on the superseded timeline is stale.
/// </summary>
public sealed class OnePieceCheckTests
{
    private const string WhiteLines = "The Peaceful One turns the fruit: \"See the white lines? That's the seal on it.\"";

    private static CheckResult Check(OnePieceFixture world, string text, string speaker = "table", int? asOf = null) =>
        new KnowledgeCheck(world.Db.Database).Check(world.CampaignRow, new CheckRequest(text, Perspective.Parse(speaker), AsOfSession: asOf));

    [Fact]
    public void Check_Row1SealAtTheTable_FlagsTheGatedTermWithItsLiftAndPreferredWords()
    {
        using var world = new OnePieceFixture();

        var result = Check(world, WhiteLines);

        var hit = Assert.Single(result.Forbidden);
        Assert.Equal((world.Seal.SeqHandle, "seal", "seal"), (hit.Source, hit.TermOrPattern, hit.Matched));
        Assert.Equal([world.AxeAssembled.SeqHandle], hit.Until);
        Assert.Equal(["shell", "wrapping", "what keeps it in"], hit.PreferredTerms);
        Assert.False(result.Pass);
    }

    [Theory]
    [InlineData("The seals are breaking", "seals")]
    [InlineData("Sealed tight", "Sealed")]
    [InlineData("Not bind it. Not seal it.", "seal")]
    public void Check_Rows2And5InflectionsAndOtherReferents_AreFlagged(string text, string matched)
    {
        using var world = new OnePieceFixture();

        Assert.Equal(matched, Assert.Single(Check(world, text).Forbidden).Matched);
    }

    [Theory]
    [InlineData("The white lines are a shell — what keeps it in.")]
    [InlineData("a sealskin coat")]
    public void Check_Rows3And4PreferredWordsAndCompounds_AreNotFlagged(string text)
    {
        using var world = new OnePieceFixture();

        Assert.Empty(Check(world, text).Forbidden);
    }

    [Theory]
    [InlineData("author")]
    [InlineData("dm")]
    public void Check_Row6AuthorView_IsNeverFlaggedForVocabulary(string speaker)
    {
        using var world = new OnePieceFixture();

        Assert.Empty(Check(world, WhiteLines, speaker).Forbidden);
    }

    [Fact]
    public void Check_Rows7And8AfterTheAxe_TheTermIsLiftedButAsOfSession10ItIsNot()
    {
        using var world = new OnePieceFixture();
        world.AssembleAxe();

        Assert.Empty(Check(world, WhiteLines).Forbidden);
        Assert.Single(Check(world, WhiteLines, asOf: 10).Forbidden);
    }

    /// <summary>
    /// "In play" is judged at the point in time: a fact established (timelessly, with no change_log) in session 11 was not
    /// in play in session 10, so the vocabulary rule it lifts still held then.
    /// </summary>
    [Fact]
    public void Check_FactEstablishedInALaterSession_IsNotInPlayAsOfAnEarlierOne()
    {
        using var world = new OnePieceFixture();
        Dapper.SqlMapper.Execute(world.Connection, "UPDATE fact SET canon_status = 'played', established_session_id = @s WHERE id = @id",
            new { s = world.Sessions[11].EntityId, id = world.AxeAssembled.Id });

        Assert.Empty(Check(world, WhiteLines).Forbidden);
        Assert.Single(Check(world, WhiteLines, asOf: 10).Forbidden);
    }

    /// <summary>
    /// A party-visible PLANNED fact is hidden from every player view's reads (not in play), so the check must not call it
    /// known to them either: a line stating it before it happens is listed as not in play, never as known context.
    /// </summary>
    [Theory]
    [InlineData("party")]
    [InlineData("table")]
    [InlineData("character:bjorn-mountainfell")]
    public void Check_PlannedFactStatedByAPlayerView_IsNotInPlayNeverKnown(string speaker)
    {
        using var world = new OnePieceFixture();

        var result = Check(world, "The War God's axe is fully assembled.", speaker);

        var axe = Assert.Single(result.UnknownFacts, f => f.Ref == world.AxeAssembled.SeqHandle);
        Assert.Equal((Standings.NotInPlay, "planned"), (axe.Standing, axe.CanonStatus));
        Assert.StartsWith("not in play (planned)", axe.Explanation, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Related, f => f.Ref == world.AxeAssembled.SeqHandle);
        Assert.DoesNotContain(result.MistakenBeliefs, f => f.Ref == world.AxeAssembled.SeqHandle);
    }

    [Theory]
    [InlineData("party", null, Standings.Knows)]
    [InlineData("party", 10, Standings.NotInPlay)]
    [InlineData("author", null, Standings.Knows)]
    public void Check_PlannedFactOnceItIsPlayed_IsKnownFromThatSessionOn(string speaker, int? asOf, string standing)
    {
        using var world = new OnePieceFixture();
        world.AssembleAxe();

        var result = Check(world, "The War God's axe is fully assembled.", speaker, asOf);

        var axe = Assert.Single(result.Related.Concat(result.UnknownFacts), f => f.Ref == world.AxeAssembled.SeqHandle);
        Assert.Equal(standing, axe.Standing);
    }

    /// <summary>
    /// Sung to the public, a planned fact is not a secret the singer is giving away: nobody on the player side knows it
    /// yet. Once it is played the party knows it and the public does not, so the same song puts it at risk.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Check_PlannedFactSungToThePublic_IsASecretAtRiskOnlyOncePlayed(bool played, bool atRisk)
    {
        using var world = new OnePieceFixture();
        if (played)
        {
            world.AssembleAxe();
        }

        var result = new KnowledgeCheck(world.Db.Database).Check(world.CampaignRow, new CheckRequest("The War God's axe is fully assembled.",
            Perspective.Parse("character:bjorn-mountainfell"), Diegetic: true, Audience: Perspective.Parse("public")));

        Assert.Equal(atRisk, result.SecretsAtRisk.Any(r => r.Fact.Ref == world.AxeAssembled.SeqHandle && r.Reason == RiskReasons.RelatedToText));
    }

    [Fact]
    public void Check_PlannedFactForTheAuthor_IsKnown()
    {
        using var world = new OnePieceFixture();

        var result = Check(world, "The War God's axe is fully assembled.", "author");

        Assert.Contains(result.Related, f => f.Ref == world.AxeAssembled.SeqHandle && f.Standing == Standings.Knows);
        Assert.DoesNotContain(result.UnknownFacts, f => f.Ref == world.AxeAssembled.SeqHandle);
    }

    [Fact]
    public void Check_Row26TheMistakenOneOnHisHelper_ListsTheExplicitlyUnknownHalf()
    {
        using var world = new OnePieceFixture();

        var result = Check(world, "I wasn't alone that day. Someone stood with me.", "character:mistaken-one");

        var helped = Assert.Single(result.UnknownFacts, f => f.Ref == world.HadHelp.SeqHandle);
        Assert.Equal(("unaware", Standings.DoesNotKnow), (helped.State, helped.Standing));
    }

    [Fact]
    public void Check_Row27TheMistakenOnesOwnAccount_HasNoUnknownFactsAndShowsThePartialBelief()
    {
        using var world = new OnePieceFixture();

        var result = Check(world, "It broke because I was slow.", "character:mistaken-one");

        Assert.Empty(result.UnknownFacts);
        var belief = Assert.Single(result.MistakenBeliefs);
        Assert.Equal((world.TotalFault.SeqHandle, "believes", "partial"), (belief.Ref, belief.State, belief.Truth));
    }

    [Fact]
    public void Check_Row30FourHundredYearsAfterTheCorrection_ListsTheStaleFact()
    {
        using var world = new OnePieceFixture();
        world.SupersedeTimeline();

        var result = Check(world, "'I've watched this city for 400 years,' the Protector says.");

        var stale = Assert.Single(result.Stale, s => s.Ref == world.Fragments400.SeqHandle);
        Assert.Equal((world.TimelineOld.SeqHandle, 1), (stale.Superseded, stale.Depth));
    }

    [Fact]
    public void Check_ProtectorNamedAtTheTable_IsAnotherNameForTheAdvisor()
    {
        using var world = new OnePieceFixture();

        var result = Check(world, "The Protector smiles.");

        var mention = Assert.Single(result.Names);
        Assert.Equal(NameClasses.OtherName, mention.Classification);
        Assert.Equal("the advisor in Serret", mention.Candidates[0].SpeakerName);
    }

    [Fact]
    public void Check_AnInventedPlace_IsAPossibleInventionAndAKnownOneIsNot()
    {
        using var world = new OnePieceFixture();

        var result = Check(world, "They sailed from Silk Isle to Port Varra.", "author");

        Assert.Equal(["Port Varra"], result.PossibleInventions);
    }

    [Fact]
    public void Check_UnknownSpeaker_IsRefused()
    {
        using var world = new OnePieceFixture();

        Assert.Throws<DndInputException>(() => Check(world, "hello", "character:nobody"));
    }
}

/// <summary>
/// Invariant: the check's classification details that the goldens do not reach: a name two entities share is fine when
/// the speaker has one legitimate reading of it, and a fact of author visibility is known to no non-author view whatever
/// its knowledge rows say (contract §3.2: author visibility is absolute): never related context the speaker "knows",
/// never a mistaken belief, never a secret the speaker can give away; it is an unknown fact standing "author only".
/// </summary>
public sealed class KnowledgeCheckTests : IDisposable
{
    private readonly DndMcp.Tests.CampaignDb.CampaignTestDb _db = new();
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly DndMcp.Tests.CampaignDb.CampaignSeed _seed;
    private readonly DndMcp.Tests.CampaignDb.SeededCampaign _campaign;
    private readonly DndMcp.Tests.CampaignDb.SeededEntity _bard;

    public KnowledgeCheckTests()
    {
        _connection = _db.Open();
        _seed = new DndMcp.Tests.CampaignDb.CampaignSeed(_connection);
        _campaign = _seed.Campaign(name: "Test", role: CampaignValues.Roles.Player);
        _bard = _seed.Entity(_campaign.Id, "character", "Bard", subtype: "pc");
        _seed.MemberOf(_campaign.Id, _bard.Id, _campaign.Party!.Id);
    }

    public void Dispose()
    {
        _connection.Dispose();
        _db.Dispose();
    }

    private CheckResult Check(string text, bool diegetic = true, string? audience = null) =>
        new KnowledgeCheck(_db.Database).Check(_seed.LoadCampaign(_campaign.Id),
            new CheckRequest(text, Perspective.Parse("character:bard"), diegetic, audience is null ? null : Perspective.Parse(audience)));

    [Fact]
    public void Check_ANameTwoEntitiesShare_IsOkWhenTheSpeakerUsesItForOneOfThem()
    {
        _seed.Entity(_campaign.Id, "location", "The Cage");
        var relic = _seed.Entity(_campaign.Id, "item", "The relic");
        _seed.Alias(relic.Id, "the Cage", CampaignValues.Visibilities.Author);

        var mention = Assert.Single(Check("We drank at the Cage").Names);

        Assert.Equal(NameClasses.Ok, mention.Classification);
        Assert.Equal(2, mention.Candidates.Count);
        Assert.Contains(mention.Candidates, c => c.Classification == NameClasses.OtherName && c.Ref == "item:the-relic");
    }

    /// <summary>
    /// Rows that say the bard and the party know an author-only fact do not make it known: the bard's reads never show it,
    /// so a line touching it is not him giving away a secret (he does not know it), and it is not context he knows either.
    /// It is listed for review as unknown, "author only", with the rows' state and who holds them kept for the author.
    /// </summary>
    [Fact]
    public void Check_AnAuthorVisibilityFactTheSpeakerAndThePartyHaveRowsFor_IsAuthorOnlyNeverKnownNorAtRisk()
    {
        var plan = _seed.Fact(_campaign.Id, "The bard plans to steal the crown.", visibility: CampaignValues.Visibilities.Author);
        _seed.FactLink(plan.Id, _bard.Id);
        _seed.Knowledge(_campaign.Id, "character", _bard.Id, factId: plan.Id);
        _seed.Knowledge(_campaign.Id, "party", factId: plan.Id);

        var result = Check("I'll steal the crown tonight");

        Assert.Empty(result.SecretsAtRisk);
        Assert.DoesNotContain(result.Related, f => f.Ref == plan.SeqHandle);
        var unknown = Assert.Single(result.UnknownFacts);
        Assert.Equal((plan.SeqHandle, Standings.AuthorOnly, CampaignValues.KnowledgeStates.Knows),
            (unknown.Ref, unknown.Standing, unknown.State));
        Assert.Equal(["character:bard", "party"], unknown.KnownBy);
        Assert.Equal("its knowledge rows do not make it known outside the author view", unknown.Explanation);
        Assert.True(result.Pass);
    }

    /// <summary>
    /// The about-speaker half of the same rule: an author-only fact about the bard that the text does not touch is not a
    /// secret of his to keep out (he does not know it), while a restricted one he knows still is.
    /// </summary>
    [Theory]
    [InlineData(CampaignValues.Visibilities.Author, false)]
    [InlineData(CampaignValues.Visibilities.Restricted, true)]
    public void Check_AFactAboutTheSpeakerHisRowSaysHeKnows_IsASecretToKeepOnlyWhenItIsNotAuthorOnly(string visibility, bool listed)
    {
        var past = _seed.Fact(_campaign.Id, "The bard was born below the clouds.", visibility: visibility);
        _seed.FactLink(past.Id, _bard.Id);
        _seed.Knowledge(_campaign.Id, "character", _bard.Id, factId: past.Id);

        var result = Check("Sing of the open road");

        Assert.Equal(listed, result.SecretsAtRisk.Any(r => r.Fact.Ref == past.SeqHandle && r.Reason == RiskReasons.AboutSpeaker));
    }

    /// <summary>
    /// A mistaken belief is a belief the speaker holds: an author-only fact his row says he misbelieves is not one he holds
    /// in any read of his, so it is an unknown fact "author only", not a mistaken belief.
    /// </summary>
    [Fact]
    public void Check_AnAuthorOnlyFactTheSpeakerMisbelieves_IsAuthorOnlyNotAMistakenBelief()
    {
        var rumor = _seed.Fact(_campaign.Id, "The duke is loyal to the crown.", visibility: CampaignValues.Visibilities.Author, truth: "false");
        _seed.Knowledge(_campaign.Id, "character", _bard.Id, CampaignValues.KnowledgeStates.Misbelieves, factId: rumor.Id);

        var result = Check("The duke is loyal.", diegetic: false);

        Assert.Empty(result.MistakenBeliefs);
        Assert.Equal((Standings.AuthorOnly, CampaignValues.KnowledgeStates.Misbelieves),
            (Assert.Single(result.UnknownFacts).Standing, result.UnknownFacts[0].State));
    }

    /// <summary>
    /// A record that already says the speaker does not know an author-only fact is kept as it is (Belmakor row 1's
    /// "unaware"): "author only" replaces only a verdict that would claim knowledge.
    /// </summary>
    [Fact]
    public void Check_AnAuthorOnlyFactTheSpeakerIsRecordedUnawareOf_KeepsTheRecordedUnaware()
    {
        var plan = _seed.Fact(_campaign.Id, "The bard plans to steal the crown.", visibility: CampaignValues.Visibilities.Author);
        _seed.Knowledge(_campaign.Id, "character", _bard.Id, CampaignValues.KnowledgeStates.Unaware, factId: plan.Id);

        var unknown = Assert.Single(Check("I'll steal the crown tonight").UnknownFacts);

        Assert.Equal((Standings.DoesNotKnow, CampaignValues.KnowledgeStates.Unaware), (unknown.Standing, unknown.State));
    }

    /// <summary>
    /// "May know" is a claim too: a party row on an author-only fact leaves the bard, absent when the party learned it,
    /// "uncertain" by the verdict, and the check lists the fact for him as "author only", as it does "knows", never as
    /// "uncertain: … absent" (he may know nothing of what no read of his will ever show). On a restricted fact the same
    /// rows keep the verdict's "uncertain".
    /// </summary>
    [Theory]
    [InlineData(CampaignValues.Visibilities.Author, Standings.AuthorOnly)]
    [InlineData(CampaignValues.Visibilities.Restricted, Standings.Uncertain)]
    public void Check_AFactThePartyLearnedWhileTheSpeakerWasAbsent_IsAuthorOnlyWhenTheFactIs(string visibility, string standing)
    {
        var s1 = _seed.Session(_campaign.Id, 1);
        _seed.Attendance(s1.EntityId, _bard.Id, present: false);
        var crown = _seed.Fact(_campaign.Id, "The duke hides a crown.", visibility: visibility);
        _seed.Knowledge(_campaign.Id, "party", factId: crown.Id, learnedSessionId: s1.EntityId);

        var unknown = Assert.Single(Check("The duke hides a crown.", diegetic: false).UnknownFacts);

        Assert.Equal((crown.SeqHandle, standing), (unknown.Ref, unknown.Standing));
        Assert.Equal(standing == Standings.Uncertain, unknown.Explanation.Contains("absent", StringComparison.Ordinal));
    }

    /// <summary>
    /// A fact both not in play and author-only is "not in play" for the speaker whose rows claim it: that holds for every
    /// player-side view whatever the rows and the visibility say, so it is the more decisive reason (the ledger orders the
    /// two the same way).
    /// </summary>
    [Fact]
    public void Check_AnAuthorOnlyPlannedFactTheSpeakersRowSaysHeKnows_IsNotInPlay()
    {
        var plan = _seed.Fact(_campaign.Id, "The bard will betray the crown.", visibility: CampaignValues.Visibilities.Author,
            canonStatus: CampaignValues.CanonStatuses.Planned);
        _seed.Knowledge(_campaign.Id, "character", _bard.Id, factId: plan.Id);
        _seed.Knowledge(_campaign.Id, "party", factId: plan.Id);

        var result = Check("I will betray the crown");

        var unknown = Assert.Single(result.UnknownFacts);
        Assert.Equal((plan.SeqHandle, Standings.NotInPlay, CampaignValues.CanonStatuses.Planned), (unknown.Ref, unknown.Standing, unknown.CanonStatus));
        Assert.Empty(result.SecretsAtRisk);
        Assert.Empty(result.Related);
    }

    /// <summary>Contract §7: a belief the speaker holds is a mistaken one when its truth is partial OR false.</summary>
    [Theory]
    [InlineData("misbelieves", "false", true)]
    [InlineData("believes", "false", true)]
    [InlineData("believes", "partial", true)]
    [InlineData("misbelieves", "partial", true)]
    [InlineData("believes", "true", false)]
    [InlineData("knows", "false", false)]
    public void Check_ABeliefTheSpeakerHolds_IsMistakenOnlyWhenPartialOrFalse(string state, string truth, bool mistaken)
    {
        var rumor = _seed.Fact(_campaign.Id, "The duke is loyal to the crown.", truth: truth);
        _seed.Knowledge(_campaign.Id, "character", _bard.Id, state, factId: rumor.Id);

        var result = Check("The duke is loyal.", diegetic: false);

        Assert.Equal(mistaken, result.MistakenBeliefs.Any(f => f.Ref == rumor.SeqHandle));
        Assert.Equal(!mistaken, result.Related.Any(f => f.Ref == rumor.SeqHandle));
        Assert.DoesNotContain(result.UnknownFacts, f => f.Ref == rumor.SeqHandle);
    }

    /// <summary>
    /// Contract §3.4: a reveal rule is active while any of its until facts is not in play (established in a session and
    /// of an in-play canon status); once they all are, its words are free.
    /// </summary>
    [Theory]
    [InlineData(CampaignValues.CanonStatuses.Played, true, false)]
    [InlineData(CampaignValues.CanonStatuses.Planned, true, true)]
    [InlineData(CampaignValues.CanonStatuses.Played, false, true)]
    public void Check_RevealRuleWithAnUntilFact_IsActiveUntilThatFactIsInPlay(string canonStatus, bool established, bool flagged)
    {
        var s1 = _seed.Session(_campaign.Id, 1);
        var lift = _seed.Fact(_campaign.Id, "The king told them his name.", visibility: CampaignValues.Visibilities.Party,
            canonStatus: canonStatus, establishedSessionId: established ? s1.EntityId : null);
        _seed.Entity(_campaign.Id, "rule", "No name", subtype: CampaignValues.Subtypes.RevealRule, visibility: CampaignValues.Visibilities.Author,
            data: "{\"forbidden_terms\":[\"Keras\"],\"until\":[\"" + lift.SeqHandle + "\"]}");

        var result = Check("Keras, come down", diegetic: false);

        Assert.Equal(flagged, result.Forbidden.Any(f => f.Source == "rule:no-name" && f.Matched == "Keras"));
    }

    [Fact]
    public void Check_RevealRuleWithNoUntil_IsAlwaysActive()
    {
        _seed.Entity(_campaign.Id, "rule", "No name", subtype: CampaignValues.Subtypes.RevealRule, visibility: CampaignValues.Visibilities.Author,
            data: "{\"forbidden_terms\":[\"Keras\"]}");

        var hit = Assert.Single(Check("Keras, come down", diegetic: false).Forbidden);

        Assert.Equal(("rule:no-name", "Keras"), (hit.Source, hit.Matched));
    }
    /// <summary>
    /// A capitalised word of a name the speaker does not use is a hard flag on its own (review U02, fix FQ4): the bard knows
    /// the Axiom Cage only as "what the old king sent us for", and its fact's gate forbids "Axiom Cage". The scanner
    /// matches whole names, so "haul your Cage back" passed with "Cage" listed as a possible invention.
    /// </summary>
    [Theory]
    [InlineData("Old king, come down\nwe'll haul your Cage back up the mountain", "Cage")]
    [InlineData("The Cage's door swings open", "Cage's")]
    [InlineData("AXIOM rising", "AXIOM")]
    public void Check_ACapitalisedWordOfANameTheSpeakerDoesNotUse_IsAPartialNameHardFlag(string text, string word)
    {
        AxiomCage();

        var result = Check(text);

        Assert.False(result.Pass);
        var partial = Assert.Single(result.PartialNames);
        Assert.Equal((word, "character:bard", "Axiom Cage", "item:axiom-cage", PartialNameSources.Name, "what the old king sent us for"),
            (partial.Matched, partial.Perspective, partial.FullName, partial.Source, partial.SourceKind, partial.KnownAs));
        Assert.Equal(word, text.Substring(partial.Start, partial.Length));
        Assert.DoesNotContain(result.PossibleInventions, p => p.StartsWith("Cage", StringComparison.Ordinal) || p == "AXIOM");
    }

    /// <summary>
    /// Only a capitalised word alone is a partial name: an ordinary lower-case word is English ("a cage of ribs"), the whole
    /// name is the scanner's (other_name, flagged once), and the author may say anything.
    /// </summary>
    [Theory]
    [InlineData("we'll haul your cage back up the mountain", "character:bard", false)]
    [InlineData("We'll haul the Axiom Cage back", "character:bard", true)]
    [InlineData("we'll haul your Cage back up the mountain", "author", false)]
    public void Check_ALowerCaseWordTheWholeNameOrTheAuthor_IsNoPartialName(string text, string speaker, bool otherName)
    {
        AxiomCage();

        var result = new KnowledgeCheck(_db.Database).Check(_seed.LoadCampaign(_campaign.Id), new CheckRequest(text, Perspective.Parse(speaker)));

        Assert.Empty(result.PartialNames);
        Assert.Equal(otherName, result.Names.Any(n => n.Classification == NameClasses.OtherName));
    }

    /// <summary>
    /// A word that is also a word of a name the speaker uses gives nothing away (the old king the bard knows is a king too);
    /// an author alias and a forbidden term are names whatever kind their entity is; a word of a quest's title is not.
    /// </summary>
    [Fact]
    public void Check_PartialNames_ComeFromNamesAliasesAndTermsTheSpeakerDoesNotUse()
    {
        var keras = _seed.Entity(_campaign.Id, "character", "Keras", visibility: CampaignValues.Visibilities.Restricted);
        _seed.Alias(keras.Id, "the Sorcerer King", CampaignValues.Visibilities.Author);
        _seed.Entity(_campaign.Id, "character", "The Old King");
        var secret = _seed.Entity(_campaign.Id, "secret", "The plan", visibility: CampaignValues.Visibilities.Author);
        _seed.Alias(secret.Id, "the Thornwood Pact", CampaignValues.Visibilities.Restricted);
        _seed.Entity(_campaign.Id, "quest", "Errand of Ashes", visibility: CampaignValues.Visibilities.Author);
        _seed.Entity(_campaign.Id, "rule", "No timespans", subtype: CampaignValues.Subtypes.RevealRule, visibility: CampaignValues.Visibilities.Author,
            data: "{\"forbidden_terms\":[\"Silent Throne\"]}");

        var result = Check("King of nothing, Thornwood burns; Errand bells ring beneath the Throne", diegetic: false);

        Assert.Equal(["Thornwood the Thornwood Pact secret:the-plan", "Throne Silent Throne rule:no-timespans"],
            result.PartialNames.Select(p => $"{p.Matched} {p.FullName} {p.Source}"));
        Assert.Equal([PartialNameSources.Alias, PartialNameSources.ForbiddenTerm], result.PartialNames.Select(p => p.SourceKind));
    }

    /// <summary>
    /// A word that opens a line or a sentence is capitalised by its place, not because it is a name (review of fix FQ4):
    /// "Peaceful days ahead, my friends" is plain English that happens to be a word of "The Peaceful One". It is listed for
    /// review with the name it may give away, and the check passes; hard-flagged, every lyric line that opens with such a
    /// word failed. The same word mid-sentence, or written in capitals, is marked as a name and stays a hard flag.
    /// </summary>
    [Theory]
    [InlineData("Peaceful days ahead, my friends", false)]
    [InlineData("We sailed on. Peaceful days ahead", false)]
    [InlineData("Old king, come down\n  — Peaceful, he said", false)]
    [InlineData("\"Peaceful,\" she said", false)]
    [InlineData("- Peaceful days ahead", false)]
    [InlineData("Days ahead are Peaceful, my friends", true)]
    [InlineData("Days ahead, Peaceful and calm", true)]
    [InlineData("PEACEFUL days ahead", true)]
    public void Check_AWordOfANameThatOpensALineOrSentence_IsForReviewNotAHardFlag(string text, bool hard)
    {
        _seed.Entity(_campaign.Id, "character", "The Peaceful One", subtype: "npc", visibility: CampaignValues.Visibilities.Author);

        var result = Check(text);

        Assert.Equal(!hard, result.Pass);
        var partial = Assert.Single(hard ? result.PartialNames : result.PossiblePartialNames);
        Assert.Empty(hard ? result.PossiblePartialNames : result.PartialNames);
        Assert.Equal(("The Peaceful One", "character:the-peaceful-one", PartialNameSources.Name),
            (partial.FullName, partial.Source, partial.SourceKind));
        Assert.Equal("peaceful", CampaignText.Key(partial.Matched));
        Assert.DoesNotContain(result.PossibleInventions, p => CampaignText.Key(p) == "peaceful");
    }

    /// <summary>
    /// A title or rank alone names no one (review of fix FQ4): "Captain Vexmoor" is given away by "Vexmoor", not by "my
    /// Captain", and "Lady Morrow" not by "the Lady", whatever their visibility or canon status. A title that is all a name
    /// has is its giveaway ("the Warden" of "The Old Warden").
    /// </summary>
    [Fact]
    public void Check_ATitleOfANameWithAnotherWord_IsNoPartialName()
    {
        _seed.Entity(_campaign.Id, "character", "Captain Vexmoor", subtype: "npc", canonStatus: CampaignValues.CanonStatuses.Proposed);
        _seed.Entity(_campaign.Id, "character", "Lady Morrow", subtype: "npc", visibility: CampaignValues.Visibilities.Author);
        _seed.Entity(_campaign.Id, "character", "The Old Warden", subtype: "npc", visibility: CampaignValues.Visibilities.Author);

        var titles = Check("We sail for my Captain and the Lady, far from home");
        var names = Check("We sail for Vexmoor and the Warden, far from home");

        Assert.True(titles.Pass);
        Assert.Empty(titles.PartialNames.Concat(titles.PossiblePartialNames));
        Assert.Equal(["Vexmoor Captain Vexmoor", "Warden The Old Warden"], names.PartialNames.Select(p => $"{p.Matched} {p.FullName}"));
    }

    /// <summary>
    /// A diegetic text is checked against the audience's names too: a word of a party alias said to the public, which knows
    /// only the public name, is a partial name for the audience, though the bard (a member) uses the alias.
    /// </summary>
    [Fact]
    public void Check_AWordOfAPartyAliasSungToThePublic_IsAPartialNameForTheAudience()
    {
        var duke = _seed.Entity(_campaign.Id, "character", "The Masked Duke", slug: "duke-orsino", visibility: CampaignValues.Visibilities.Public);
        _seed.Alias(duke.Id, "Duke Orsino", CampaignValues.Visibilities.Party);

        var toCrowd = Check("Tonight Orsino dances", audience: "public");
        var toParty = Check("Tonight Orsino dances", audience: "party");

        var partial = Assert.Single(toCrowd.PartialNames);
        Assert.Equal(("Orsino", "public", "Duke Orsino", PartialNameSources.Alias, "The Masked Duke"),
            (partial.Matched, partial.Perspective, partial.FullName, partial.SourceKind, partial.KnownAs));
        Assert.Empty(toParty.PartialNames);
    }

    /// <summary>
    /// The names a view uses are its view's (review U01 and L11, fix FQ3): a party alias of an entity the party knows by a
    /// known_as is the party's own name ("the old king" for the ancient sorcerer king), fine to say and to sing to the party;
    /// a party alias of a public entity is no name of the public's, so a crowd that hears it is told something.
    /// </summary>
    [Fact]
    public void Check_PartyAliases_AreUsedNamesOfThePartyAndItsMembersButNotOfThePublic()
    {
        var keras = _seed.Entity(_campaign.Id, "character", "Keras", subtype: "npc");
        _seed.Alias(keras.Id, "the old king", CampaignValues.Visibilities.Party);
        _seed.Alias(keras.Id, "the sorcerer king", CampaignValues.Visibilities.Party);
        _seed.Knowledge(_campaign.Id, "party", state: "met", entityId: keras.Id, knownAs: "the ancient sorcerer king");
        var duke = _seed.Entity(_campaign.Id, "character", "The Masked Duke", slug: "duke-orsino", visibility: CampaignValues.Visibilities.Public);
        _seed.Alias(duke.Id, "Duke Orsino", CampaignValues.Visibilities.Party);

        var oldKing = Check("Old king, come down");
        var toCrowd = Check("Duke Orsino dances at the masquerade tonight!", audience: "public");
        var toParty = Check("Duke Orsino dances at the masquerade tonight!");

        Assert.True(oldKing.Pass);
        var king = Assert.Single(oldKing.Names);
        Assert.Equal((NameClasses.Ok, NameClasses.Ok, "the ancient sorcerer king"), (king.Classification, king.AudienceClassification, king.Candidates[0].SpeakerName));
        var orsino = Assert.Single(toCrowd.Names);
        Assert.Equal((NameClasses.Ok, NameClasses.RevealsToAudience), (orsino.Classification, orsino.AudienceClassification));
        Assert.False(toCrowd.Pass);
        Assert.True(toParty.Pass);
    }

    /// <summary>
    /// As of a session, the names are those of the entities that existed then (review C10, fix FQ7): the docks deleted in S3
    /// are a name as of S2, not a possible invention; today they are not a name. A name learned later (a known_as recorded in
    /// S3) is still a name the check reads, so an anachronism is flagged rather than passed as unknown words.
    /// </summary>
    [Fact]
    public void Check_AsOfASession_ReadsTheNamesOfEntitiesThatExistedThen()
    {
        _seed.Session(_campaign.Id, 1);
        _seed.Session(_campaign.Id, 2);
        var s3 = _seed.Session(_campaign.Id, 3);
        var docks = _seed.Entity(_campaign.Id, "location", "Docks");
        _db.Batch(_campaign.Id, r => r.SoftDelete("entity", docks.Id), sessionId: s3.EntityId);
        var bram = _seed.Entity(_campaign.Id, "character", "Bram");
        _seed.Knowledge(_campaign.Id, "party", state: "met", entityId: bram.Id, knownAs: "the big man", learnedSessionId: s3.EntityId);
        var request = new CheckRequest("We met the big man at the Docks.", Perspective.Parse("character:bard"), AsOfSession: 2);

        var then = new KnowledgeCheck(_db.Database).Check(_seed.LoadCampaign(_campaign.Id), request);
        var now = new KnowledgeCheck(_db.Database).Check(_seed.LoadCampaign(_campaign.Id), request with { AsOfSession = null });

        Assert.Equal(NameClasses.Ok, Assert.Single(then.Names, n => n.Matched == "Docks").Classification);
        Assert.Empty(then.PossibleInventions);
        Assert.NotEqual(NameClasses.Ok, Assert.Single(then.Names, n => n.Matched == "big man").Classification);
        Assert.Equal(["Docks"], now.PossibleInventions);
    }

    /// <summary>
    /// A candidate made of a name the speaker uses is no invention (review UR01): the candidate finder joins a line's
    /// opening word to the name after it ("Hail Belmakor", "Sing of the Crumbling Statue", "Beware the Crumbling Statue"), and
    /// a lone word of a name the speaker uses ("King", of the party's "the old king") is that name's word. The check listed
    /// them as possible inventions while the same result named the name under "names used as the speaker uses them"; the
    /// session checklist, by the same shared rule (KnownNames), lists none of them. A real unknown name beside an opening
    /// word, or beside a known name, is still listed: a word beside a known name that is not a sentence's opening word was
    /// typed with its capital on purpose ("Belmakor Shadowfang", "Old King Zanzibar", "we met Rolf of the Crumbling
    /// Statue"), and read as "holds a known name" the check stopped listing it. A name the speaker does not use counts as
    /// known here too, once the scanner found it: "Hail Keras" is a hard flag for "Keras", not also an unknown "Hail Keras".
    /// </summary>
    [Theory]
    [InlineData("Hail Belmakor, hail the band.", "", true)]
    [InlineData("Sing of the Crumbling Statue.", "", true)]
    [InlineData("Beware the Crumbling Statue", "", true)]
    [InlineData("Tonight we sing for the King of the sky.", "", true)]
    [InlineData("Hail Keras, come down.", "", false)]
    [InlineData("Hail Zanzibar, hail the band.", "Hail Zanzibar", true)]
    [InlineData("Beware the Crumbling Statue and the Ashen Gate", "Ashen Gate", true)]
    [InlineData("We met Rolf of the Crumbling Statue at dawn.", "Rolf of the Crumbling Statue", true)]
    [InlineData("And Rolf of the Crumbling Statue sang.", "Rolf of the Crumbling Statue", true)]
    [InlineData("Then Old King Zanzibar spoke to us.", "Old King Zanzibar", true)]
    [InlineData("We drank with Belmakor Shadowfang, his brother.", "Belmakor Shadowfang", true)]
    [InlineData("we sailed with Zanzibar of the Crumbling Statue", "Zanzibar of the Crumbling Statue", true)]
    public void Check_ACandidateMadeOfANameTheSpeakerUses_IsNoPossibleInvention(string text, string inventions, bool pass)
    {
        var belmakor = _seed.Entity(_campaign.Id, "character", "Belmakor Silverwind", subtype: "pc");
        _seed.Alias(belmakor.Id, "Belmakor", CampaignValues.Visibilities.Public);
        _seed.MemberOf(_campaign.Id, belmakor.Id, _campaign.Party!.Id);
        _seed.Entity(_campaign.Id, "location", "Crumbling Statue");
        var keras = _seed.Entity(_campaign.Id, "character", "Keras", subtype: "npc");
        _seed.Alias(keras.Id, "the old king");
        _seed.Knowledge(_campaign.Id, "party", state: "met", entityId: keras.Id, knownAs: "the ancient sorcerer king");

        var result = Check(text);

        Assert.Equal(inventions.Length == 0 ? [] : inventions.Split('|'), result.PossibleInventions);
        Assert.Equal(pass, result.Pass);
    }

    // The U02 shape: an item named with its secret name, known to the party by a phrase, and a gate forbidding the name.
    private void AxiomCage()
    {
        var cage = _seed.Entity(_campaign.Id, "item", "Axiom Cage");
        _seed.Knowledge(_campaign.Id, "party", state: "heard", entityId: cage.Id, knownAs: "what the old king sent us for");
        var fact = _seed.Fact(_campaign.Id, "What the old king sent the party to fetch is the Axiom Cage.",
            gate: FactGates.Serialize(new GateSpec { ForbiddenTerms = ["Axiom Cage"] }));
        _seed.FactLink(fact.Id, cage.Id);
        _seed.Knowledge(_campaign.Id, "party", state: "unaware", factId: fact.Id);
    }
}
