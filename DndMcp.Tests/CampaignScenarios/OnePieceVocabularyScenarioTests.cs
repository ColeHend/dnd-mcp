using DndMcp.Repository.Campaign.Read;
using Xunit;

namespace DndMcp.Tests.CampaignScenarios;

/// <summary>
/// Invariant: <c>understand-onepiece.md</c> §3 rows 1-8, the forbidden vocabulary of the seal gate as the write path
/// stored it (the gate's forbidden_terms, forbidden_until and preferred_terms, with fact ids rewritten to handles on the
/// way out). "Seal" and its inflections are flagged for every non-author speaker until the axe is in play, never
/// "sealskin", never for the author; T4 (the axe assembled in session 11, through a campaign_write fact op) lifts it, and
/// checking as of session 10 brings it back. A gate stored with a stale id, or an "in play" read that ignored
/// established_session, would let an NPC say "seal" at the table a session early.
/// </summary>
public sealed class OnePieceVocabularyScenarioTests : IDisposable
{
    private const string WhiteLines = "The Peaceful One turns the fruit: \"See the white lines? That's the seal on it.\"";

    private readonly OnePieceScenario _world = OnePieceScenario.Build();

    public void Dispose() => _world.Dispose();

    private CheckResult Check(string text, string speaker = "table", int? asOf = null) => _world.Reads.Check(_world.Campaign, text, speaker, asOf: asOf);

    [Fact]
    public void Check_Row1SealAtTheTable_HasExactlyOneEntryWithItsFactLiftAndPreferredWords()
    {
        var result = Check(WhiteLines);

        var hit = Assert.Single(result.Forbidden);
        Assert.Equal((_world.Facts.Seal, "seal", "seal"), (hit.Source, hit.TermOrPattern, hit.Matched));
        Assert.Equal([_world.Facts.AxeAssembled], hit.Until);
        Assert.Equal(["shell", "wrapping", "what keeps it in"], hit.PreferredTerms);
        Assert.Equal("Reveal order — do not break this (canon-core.md:172-195)", hit.Note);
        Assert.False(result.Pass);
    }

    [Theory]
    [InlineData("The seals are breaking", "seals")]
    [InlineData("Sealed tight", "Sealed")]
    [InlineData("SEALING the hold", "SEALING")]
    public void Check_Row2InflectionsAndCase_AreFlaggedAsWritten(string text, string matched)
    {
        Assert.Equal(matched, Assert.Single(Check(text).Forbidden).Matched);
    }

    [Theory]
    [InlineData("The white lines are a shell — what keeps it in.")]
    [InlineData("A wrapping, nothing more.")]
    public void Check_Row3ThePreferredWords_AreNotFlagged(string text)
    {
        Assert.Empty(Check(text).Forbidden);
    }

    [Theory]
    [InlineData("a sealskin coat")]
    [InlineData("the jar came unsealed")]
    public void Check_Row4AnotherWordThatContainsTheTerm_IsNotFlagged(string text)
    {
        Assert.Empty(Check(text).Forbidden);
    }

    /// <summary>Row 5: DFF:191's "Not seal it." refers to Baal, not the white lines; the check retrieves, the model judges, so it is flagged.</summary>
    [Fact]
    public void Check_Row5TheWordWithAnotherReferent_IsStillFlagged()
    {
        Assert.Equal("seal", Assert.Single(Check("Not bind it. Not seal it. Not stash it somewhere clever and call that a victory.").Forbidden).Matched);
    }

    /// <summary>Row 6: the author may use the word (and in a DM campaign the dm is the author view).</summary>
    [Theory]
    [InlineData("author")]
    [InlineData("dm")]
    public void Check_Row6TheAuthorView_IsNeverFlaggedForVocabulary(string speaker)
    {
        Assert.Empty(Check(WhiteLines, speaker).Forbidden);
    }

    /// <summary>Row 1 holds for every non-author speaker, player-side or NPC.</summary>
    [Theory]
    [InlineData("party")]
    [InlineData("public")]
    [InlineData("character:bjorn-mountainfell")]
    [InlineData("character:nadar")]
    [InlineData("character:peaceful-one")]
    public void Check_Row1AnyNonAuthorSpeaker_IsFlagged(string speaker)
    {
        Assert.Single(Check(WhiteLines, speaker).Forbidden);
    }

    [Fact]
    public void Check_Row7AfterT4_TheTermIsLiftedBeforeTheReveal()
    {
        _world.ThroughT4();

        Assert.Empty(Check(WhiteLines).Forbidden);
        Assert.Empty(_world.F.Query<string>("SELECT k.state FROM knowledge k JOIN fact f ON f.id = k.fact_id WHERE f.seq = @seq AND k.knower_kind = 'party'",
            new { seq = long.Parse(_world.Facts.Seal[2..], System.Globalization.CultureInfo.InvariantCulture) }));
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(11, false)]
    [InlineData(12, false)]
    public void Check_Row8AfterT4AsOfASession_IsFlaggedUntilTheSessionTheAxeWasAssembled(int asOf, bool flagged)
    {
        _world.ThroughT4();

        Assert.Equal(flagged, Check(WhiteLines, asOf: asOf).Forbidden.Count == 1);
    }

    /// <summary>
    /// Row 8 when T4 is written as prep between sessions (no session context, so change_log files it under no session
    /// and point-in-time replay never reverses it): the lift still waits for the session the axe was established in,
    /// because "in play at n" reads established_session (contract §3.4), not only when the write happened.
    /// </summary>
    [Theory]
    [InlineData(10, true)]
    [InlineData(11, false)]
    public void Check_Row8AxeRecordedOutsideASessionAsOfASession_FollowsItsEstablishedSession(int asOf, bool flagged)
    {
        _world.F.Apply(_world.Campaign,
            new Domain.Campaign.Ops.CampaignOpSpec { Op = "fact", Ref = _world.Facts.AxeAssembled, CanonStatus = "played", EstablishedSession = 11 });

        Assert.Equal(flagged, Check(WhiteLines, asOf: asOf).Forbidden.Count == 1);
        Assert.Empty(Check(WhiteLines).Forbidden);
    }

    /// <summary>
    /// The lift is forbidden_until (the axe), not the reveal: revealing the seal to the party before T4 (warn and apply)
    /// does not free the word, because the gate names what lifts it.
    /// </summary>
    [Fact]
    public void Check_RevealBeforeTheAxe_DoesNotLiftTheVocabulary()
    {
        _world.T1();
        _world.T2();
        _world.T3();
        _world.Reveal(10, false, _world.Facts.Seal, _world.Facts.NadarPlan);

        Assert.Single(Check(WhiteLines).Forbidden);
    }
}
