using DndMcp.Repository.Campaign.Read;
using Xunit;

namespace DndMcp.Tests.CampaignScenarios;

/// <summary>
/// Invariant: <c>understand-belmakor.md</c> §3 rows 36-46, the diegetic checks, on the world the write path built, split
/// into hard flags and things to review (<see cref="CheckFlags"/>). "Old king, come down" passes; "the Axiom Cage" in a
/// song is another name for what Belmakor calls "the thing he wants" and a name the party never heard; "Keras" breaks the
/// no-name rule the write path stored as a reveal_rule entity; a "nine-hundred-year-old" timespan breaks its pattern; his
/// ambition in a song is a secret at risk, the oblique "this could be a kingdom" line is not a hard flag; the author is
/// never flagged. A check that passed row 38 would let a lyric name the Cage in front of the table.
/// </summary>
public sealed class BelmakorCheckScenarioTests(BelmakorScenario world) : IClassFixture<BelmakorScenario>
{
    private const string HaulTheCage = "We'll haul the Axiom Cage back up to the old king";
    private const string DeadWorld = "I'll take back the ground below and make the dead world a kingdom";

    private CheckResult Check(string text, string speaker, bool diegetic) => world.Reads.Check(world.Campaign, text, speaker, diegetic);

    /// <summary>Every row: "pass" is exactly "no hard flag", so the flag the author reads and the verdict cannot disagree.</summary>
    [Theory]
    [InlineData("Old king, come down", "character:belmakor", true)]
    [InlineData("Old king came down to a knee", "character:belmakor", true)]
    [InlineData(HaulTheCage, "character:belmakor", true)]
    [InlineData(HaulTheCage, "party", true)]
    [InlineData(HaulTheCage, "author", false)]
    [InlineData("Keras, come down", "character:belmakor", true)]
    [InlineData("a nine-hundred-year-old sorcerer", "character:belmakor", true)]
    [InlineData(DeadWorld, "character:belmakor", true)]
    [InlineData(DeadWorld, "character:belmakor", false)]
    [InlineData("I thought, Lord, this could be a kingdom / If somebody stayed around.", "character:belmakor", true)]
    [InlineData("Tristan went down protecting Sky", "character:belmakor", true)]
    public void Check_Rows36To46_PassIsExactlyNoHardFlag(string text, string speaker, bool diegetic)
    {
        var result = Check(text, speaker, diegetic);

        Assert.Equal(CheckFlags.Hard(result).Count == 0, result.Pass);
    }

    [Theory]
    [InlineData(36, "Old king, come down")]
    [InlineData(37, "Old king came down to a knee")]
    public void Check_Rows36And37TheKingByTheNameBelmakorUses_Passes(int row, string text)
    {
        var result = Check(text, "character:belmakor", diegetic: true);

        Assert.True(result.Pass, $"row {row}: {string.Join(", ", CheckFlags.Hard(result))}");
        var mention = Assert.Single(result.Names);
        Assert.Equal(("Old king", NameClasses.Ok, NameClasses.Ok), (mention.Matched, mention.Classification, mention.AudienceClassification));
        var king = Assert.Single(mention.Candidates);
        Assert.Equal(("character:old-king", "The Old King"), (king.Ref, king.SpeakerName));
        Assert.Empty(result.Forbidden);
    }

    [Theory]
    [InlineData(38, "character:belmakor")]
    [InlineData(39, "party")]
    public void Check_Rows38And39AxiomCageInASong_IsFlaggedAsAnotherNameForTheThingHeWants(int row, string speaker)
    {
        var result = Check(HaulTheCage, speaker, diegetic: true);

        var hard = CheckFlags.Hard(result);
        Assert.False(result.Pass, $"row {row}");
        Assert.Contains("other_name:Axiom Cage", hard);
        Assert.Contains("reveals_to_audience:Axiom Cage", hard);
        var cage = result.Names.Single(n => n.Matched == "Axiom Cage");
        var item = Assert.Single(cage.Candidates, c => c.Ref == "item:thing-he-wants");
        Assert.Equal((NameMatchKinds.Alias, "author", "the thing he wants"), (item.MatchedAs, item.MatchedVisibility, item.SpeakerName));
        Assert.Contains(cage.Candidates, c => c.Classification == NameClasses.CrossCampaign && c.Ref == "one-piece/item:axiom-cage");
        Assert.Equal(NameClasses.Ok, result.Names.Single(n => n.Matched == "old king").Classification);
        var f1 = Assert.Single(result.UnknownFacts, f => f.Ref == "f:1");
        Assert.Equal(["author"], f1.KnownBy);
        Assert.Equal("unaware", f1.State);
        Assert.Contains("unknown:f:1", CheckFlags.ToReview(result));
    }

    [Fact]
    public void Check_Row40SameTextAsTheAuthor_HasNoFlagAtAll()
    {
        var result = Check(HaulTheCage, "author", diegetic: false);

        Assert.True(result.Pass);
        Assert.Empty(CheckFlags.Hard(result));
        Assert.All(result.Names, n => Assert.Equal(NameClasses.Ok, n.Classification));
        Assert.Empty(result.UnknownFacts);
    }

    [Fact]
    public void Check_Row41KerasComeDown_FlagsTheAuthorAliasTheOtherCampaignAndTheNoNameRule()
    {
        var result = Check("Keras, come down", "character:belmakor", diegetic: true);

        var hard = CheckFlags.Hard(result);
        Assert.False(result.Pass);
        Assert.Contains("other_name:Keras", hard);
        Assert.Contains("forbidden:Keras", hard);
        var keras = Assert.Single(result.Names);
        Assert.Equal("The Old King", keras.Candidates.Single(c => c.Ref == "character:old-king").SpeakerName);
        Assert.Contains(keras.Candidates, c => c.Ref == "one-piece/character:keras" && c.Classification == NameClasses.CrossCampaign);
        var rule = Assert.Single(result.Forbidden);
        Assert.Equal(("rule:old-king-no-name-no-timespan", "Keras"), (rule.Source, rule.TermOrPattern));
        Assert.Contains(result.UnknownFacts, f => f.Ref == "f:2");
    }

    [Fact]
    public void Check_Row42NineHundredYearOldSorcerer_BreaksTheRulesTimespanPattern()
    {
        var result = Check("a nine-hundred-year-old sorcerer", "character:belmakor", diegetic: true);

        Assert.False(result.Pass);
        var hit = Assert.Single(result.Forbidden);
        Assert.Equal(("rule:old-king-no-name-no-timespan", "<n>-year-old", "nine-hundred-year-old"), (hit.Source, hit.TermOrPattern, hit.Matched));
        Assert.Equal(["forbidden:nine-hundred-year-old"], CheckFlags.Hard(result));
    }

    [Fact]
    public void Check_Row43HisAmbitionInASong_IsASecretAtRiskFromThePartyAndThePublic()
    {
        var result = Check(DeadWorld, "character:belmakor", diegetic: true);

        Assert.False(result.Pass);
        Assert.Contains("secret_at_risk:f:5", CheckFlags.Hard(result));
        var risk = result.SecretsAtRisk.Single(r => r.Fact.Ref == "f:5");
        Assert.Equal((RiskReasons.RelatedToText, Standings.DoesNotKnow), (risk.Reason, risk.AudienceStanding));
    }

    [Fact]
    public void Check_Row44SameTextNotDiegetic_HeKnowsItSoItIsNeitherUnknownNorAtRisk()
    {
        var result = Check(DeadWorld, "character:belmakor", diegetic: false);

        Assert.DoesNotContain(result.UnknownFacts, f => f.Ref == "f:5");
        Assert.Contains(result.Related, f => f.Ref == "f:5" && f.Standing == Standings.Knows);
        Assert.Empty(result.SecretsAtRisk);
        Assert.Null(result.Audience);
    }

    /// <summary>
    /// Row 45: the catalog's oblique line has no hard flag; f:5 is surfaced only for review (a secret about the speaker),
    /// so the model judges whether the line is oblique enough, as the golden asks.
    /// </summary>
    [Fact]
    public void Check_Row45TheObliqueKingdomLine_HasNoHardFlagAndListsTheAmbitionForReview()
    {
        var result = Check("I thought, Lord, this could be a kingdom / If somebody stayed around.", "character:belmakor", diegetic: true);

        Assert.True(result.Pass, string.Join(", ", CheckFlags.Hard(result)));
        Assert.Empty(result.Forbidden);
        Assert.Contains("about_speaker:f:5", CheckFlags.ToReview(result));
    }

    /// <summary>
    /// Row 46: Belmakor was present in S1, so f:7 (Tristan's death) is known and the elegy passes. Pinned beyond the
    /// golden: "Sky" is listed as a possible invention for review. A possible invention is a proper noun matching no
    /// name, alias or known_as in the campaign, and f:7's statement mentions Sky but no entity is called Sky, so the check
    /// asks the author to confirm it. It is a thing to review, never a hard flag, so the pass is unaffected.
    /// </summary>
    [Fact]
    public void Check_Row46TristansElegy_PassesBecauseThePartyKnowsItFromSessionOne()
    {
        var result = Check("Tristan went down protecting Sky", "character:belmakor", diegetic: true);

        Assert.True(result.Pass, string.Join(", ", CheckFlags.Hard(result)));
        Assert.Equal(NameClasses.Ok, Assert.Single(result.Names).Classification);
        Assert.Contains(result.Related, f => f.Ref == "f:7");
        Assert.DoesNotContain(result.UnknownFacts, f => f.Ref == "f:7");
        Assert.Equal(["Sky"], result.PossibleInventions);
    }

    /// <summary>
    /// Row 46's precondition is attendance: Serif was absent in S1, so the same elegy in Serif's mouth leaves f:7 for
    /// review (uncertain), while Belmakor, present, knows it.
    /// </summary>
    [Fact]
    public void Check_Row46InSerifsMouth_TheDeathIsUncertainForReview()
    {
        var result = Check("Tristan went down protecting Sky", "character:serif", diegetic: false);

        var f7 = Assert.Single(result.UnknownFacts, f => f.Ref == "f:7");
        Assert.Equal(Standings.Uncertain, f7.Standing);
    }
}
