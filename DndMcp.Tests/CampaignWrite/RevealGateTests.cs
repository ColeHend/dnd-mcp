using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Write;
using Xunit;

namespace DndMcp.Tests.CampaignWrite;

/// <summary>
/// Invariant: a gated fact reaching a non-author knower is written and warned about, never refused (One Piece §3 "warn
/// and apply"), through campaign_knowledge and campaign_write's known_by alike; the warnings name the unmet after, the
/// unmet or separately-landed with (symmetric), too few routes, the advisory prefer, and "reachable before the gate" on
/// the write that completes the routes; the secret's derived status follows the party's knowledge and is logged in the
/// same batch (filed under the session the party learned in, so replay as of an earlier session gives the earlier status),
/// undoing the reveal puts it back, and undoing an earlier reveal re-derives it in the undo batch. One Piece rows 9-25 are
/// pinned here by number.
/// </summary>
public sealed class RevealGateTests : IDisposable
{
    private readonly WriteFixture _f = new();
    private readonly OnePieceFixture _op;

    public RevealGateTests()
    {
        _op = OnePieceFixture.Build(_f);
    }

    public void Dispose() => _f.Dispose();

    private OnePieceFacts Facts => _op.Facts;

    private static IReadOnlyList<GateWarning> Gates(WriteResult result, string? condition = null) =>
        result.Warnings.OfType<GateWarning>().Where(w => condition is null || w.Gate == condition).ToList();

    private string PartyState(string fact) =>
        _f.Scalar<string?>("SELECT k.state FROM knowledge k JOIN fact f ON f.id = k.fact_id WHERE f.seq = @seq AND k.knower_kind = 'party'",
            new { seq = long.Parse(fact[2..], System.Globalization.CultureInfo.InvariantCulture) }) ?? "(none)";

    private int? PartyLearned(string fact) =>
        _f.Scalar<int?>("SELECT s.number FROM knowledge k JOIN fact f ON f.id = k.fact_id JOIN session s ON s.entity_id = k.learned_session_id " +
                        "WHERE f.seq = @seq AND k.knower_kind = 'party'",
            new { seq = long.Parse(fact[2..], System.Globalization.CultureInfo.InvariantCulture) });

    [Fact]
    public void Baseline_SecretIsSeededFromTheSeed_Row17()
    {
        Assert.Equal("seeded", _op.SecretStatus());
    }

    [Fact]
    public void AfterT1_StillSeeded_Row18()
    {
        _op.T1();

        Assert.Equal("seeded", _op.SecretStatus());
    }

    [Fact]
    public void AfterT2_OneRouteComplete_IsPartial_Row19()
    {
        _op.T1();

        var t2 = _op.T2();

        Assert.Equal("partial", _op.SecretStatus());
        var consequence = Assert.Single(t2.Consequences);
        Assert.Equal((ConsequenceKinds.SecretStatus, OnePieceFixture.Secret), (consequence.Kind, consequence.Ref));
        Assert.Contains("seeded → partial", consequence.Message);
        Assert.Single(_f.Log(t2.BatchId), r => r.TargetTable == "entity" && r.FieldPath == "status" && r.Action == "secret_status");
    }

    [Fact]
    public void T3_CompletingTheSecondRouteBeforeTheAxe_WarnsReachableBeforeTheGate_Row20()
    {
        _op.T1();
        _op.T2();

        var t3 = _op.T3();

        Assert.Equal("knows", PartyState(Facts.BreachesClimbing));
        Assert.Equal("knows", PartyState(Facts.FruitAppearancesFalling));
        var warning = Assert.Single(t3.Warnings.OfType<ReachableBeforeGateWarning>());
        Assert.Equal((OnePieceFixture.Secret, Facts.Seal, 2, 2), (warning.Secret, warning.Fact, warning.RoutesComplete, warning.MinRoutes));
        Assert.Equal([Facts.AxeAssembled], warning.AfterUnmet);
        Assert.All(_f.Log(t3.BatchId), r => Assert.Contains("reachable before the gate", r.Reason));
        Assert.Equal("partial", _op.SecretStatus());
    }

    [Fact]
    public void T3_WhenARouteWasAlreadyEnough_DoesNotWarnAgain()
    {
        _op.T1();
        _op.T2();
        _op.T3();

        var again = _op.Record(10, Facts.TPeaceful);

        Assert.Empty(again.Warnings.OfType<ReachableBeforeGateWarning>());
    }

    [Fact]
    public void RevealBeforeTheAxe_IsAppliedWithAfterAndWithWarnings_Row9()
    {
        _op.T1();
        _op.T2();
        _op.T3();

        var result = _op.Reveal(10, false, Facts.Seal);

        Assert.Equal("knows", PartyState(Facts.Seal));
        Assert.Equal(10, PartyLearned(Facts.Seal));
        var after = Assert.Single(Gates(result, GateConditions.After));
        Assert.Equal((Facts.Seal, "party", 10), (after.Fact, after.Knower, after.RevealSession));
        Assert.Equal([Facts.AxeAssembled], after.Unmet);
        var with = Assert.Single(Gates(result, GateConditions.With));
        Assert.Equal([Facts.NadarPlan], with.Unmet);
        Assert.Empty(Gates(result, GateConditions.Routes));
        Assert.All(_f.Log(result.BatchId), r =>
        {
            Assert.Contains($"{Facts.Seal} reached party in S10 before its gate's after is met: {Facts.AxeAssembled} not in play yet", r.Reason);
            Assert.Contains($"{Facts.Seal} must land with {Facts.NadarPlan}", r.Reason);
        });
    }

    [Fact]
    public void RevealDryRun_GivesTheSameWarningsAndWritesNothing_Row10()
    {
        _op.T1();
        _op.T2();
        _op.T3();
        var before = _f.Dump();
        var logBefore = _f.Log().Count;

        var dry = _f.Knowledge.Reveal(_op.Campaign, [Facts.Seal], null, null, ["party"], null, WriteContext.For(10, dryRun: true));

        Assert.Null(dry.BatchId);
        Assert.True(dry.DryRun);
        Assert.Single(Gates(dry, GateConditions.After));
        Assert.Single(Gates(dry, GateConditions.With));
        Assert.Equal(before, _f.Dump());
        Assert.Equal(logBefore, _f.Log().Count);
    }

    [Fact]
    public void RevealThroughCampaignWriteKnownBy_GivesTheSameWarnings_Row11()
    {
        _op.T1();
        _op.T2();
        _op.T3();

        var result = _f.Apply(_op.Campaign, WriteContext.For(10),
            new CampaignOpSpec { Op = "fact", Ref = Facts.Seal, KnownBy = [Op.Knower("party", "knows")] });

        Assert.Equal("knows", PartyState(Facts.Seal));
        Assert.Equal([Facts.AxeAssembled], Assert.Single(Gates(result, GateConditions.After)).Unmet);
        Assert.Equal([Facts.NadarPlan], Assert.Single(Gates(result, GateConditions.With)).Unmet);
        Assert.Equal(0, Assert.Single(Gates(result, GateConditions.After)).OpIndex);
    }

    [Fact]
    public void RevealAfterTheAxe_OnlyTheWithCouplingWarns_Row12()
    {
        _op.T1();
        _op.T2();
        _op.T3();
        _op.T4();

        var result = _op.Reveal(12, false, Facts.Seal);

        var warning = Assert.Single(Gates(result), w => w.Severity == WarningSeverities.Warning);
        Assert.Equal((GateConditions.With, Facts.Seal), (warning.Gate, warning.Fact));
        Assert.Equal([Facts.NadarPlan], warning.Unmet);
    }

    [Fact]
    public void RevealingNadarsPlanAlone_BreaksTheSymmetricCoupling_Row13()
    {
        _op.T1();
        _op.T2();
        _op.T3();
        _op.T4();

        var result = _op.Reveal(12, false, Facts.NadarPlan);

        var warning = Assert.Single(Gates(result, GateConditions.With));
        Assert.Equal(Facts.NadarPlan, warning.Fact);
        Assert.Equal([Facts.Seal], warning.Unmet);
    }

    [Fact]
    public void CoupledFactLearnedInAnEarlierSession_LandedSeparately_Row14()
    {
        _op.T1();
        _op.T2();
        _op.T3();
        _op.T4();
        _op.Reveal(8, false, Facts.NadarPlan);

        var result = _op.Reveal(12, false, Facts.Seal);

        var warning = Assert.Single(Gates(result, GateConditions.With));
        Assert.Empty(warning.Unmet);
        Assert.Equal([new LandedSeparatelyItem(Facts.NadarPlan, 8)], warning.LandedSeparately);
        Assert.Equal(12, warning.RevealSession);
    }

    [Fact]
    public void RevealInAnEarlierSessionThanTheAxe_JudgesAfterAtTheRevealSession()
    {
        _op.T1();
        _op.T2();
        _op.T3();
        _op.T4();

        // Recorded late: the reveal happened in session 10, before the axe was assembled in 11.
        var result = _op.Reveal(10, false, Facts.Seal, Facts.NadarPlan);

        Assert.Equal([Facts.AxeAssembled], Assert.Single(Gates(result, GateConditions.After)).Unmet);
    }

    [Fact]
    public void T5_BothTogetherAfterTheGate_OnlyThePreferAdvisory_AndRevealed_Row15()
    {
        _op.T1();
        _op.T2();
        _op.T3();
        _op.T4();

        var t5 = _op.T5();

        var gates = Gates(t5);
        var prefer = Assert.Single(gates);
        Assert.Equal((GateConditions.Prefer, WarningSeverities.Advisory, Facts.Seal), (prefer.Gate, prefer.Severity, prefer.Fact));
        Assert.Equal([Facts.NoUneatenFruits], prefer.Unmet);
        Assert.Equal("Reveal order — do not break this (canon-core.md:172-195)", prefer.Note);
        Assert.All(_f.Log(t5.BatchId), r => Assert.Contains(prefer.Message, r.Reason));
        Assert.Empty(t5.Warnings.OfType<ReachableBeforeGateWarning>());
        Assert.Equal("revealed", _op.SecretStatus());
        Assert.Contains(t5.Consequences, c => c.Kind == ConsequenceKinds.SecretStatus && c.Message.Contains("partial → revealed"));
    }

    [Fact]
    public void T5WithOnlyOneRoute_WarnsTooFewRoutes_Row23()
    {
        _op.T1();
        _op.T2();

        var t5 = _op.T5();

        var routes = Assert.Single(Gates(t5, GateConditions.Routes));
        Assert.Equal((1, 2), (routes.RoutesComplete, routes.MinRoutes));
        Assert.Equal(["illusion"], routes.CompleteRouteIds);
        Assert.All(_f.Log(t5.BatchId), r => Assert.Contains(routes.Message, r.Reason));
        Assert.Single(Gates(t5, GateConditions.After));
        Assert.Empty(Gates(t5, GateConditions.With));
    }

    [Fact]
    public void Statuses_ThroughEveryStep_Rows17To24()
    {
        var statuses = new List<string?> { _op.SecretStatus() };
        _op.T1();
        statuses.Add(_op.SecretStatus());
        _op.T2();
        statuses.Add(_op.SecretStatus());
        _op.T3();
        statuses.Add(_op.SecretStatus());
        _op.T4();
        statuses.Add(_op.SecretStatus());
        _op.T5();
        statuses.Add(_op.SecretStatus());

        Assert.Equal(["seeded", "seeded", "partial", "partial", "partial", "revealed"], statuses);
    }

    [Fact]
    public void UndoT5_RemovesThePartyRowsAndPutsTheStatusBack_Row25()
    {
        _op.T1();
        _op.T2();
        _op.T3();
        _op.T4();
        var before = _f.Dump();
        var t5 = _op.T5();
        var logAfterT5 = _f.Log();

        var undo = _f.History.Undo(_op.Campaign, t5.BatchId!, WriteContext.Default);

        Assert.Equal("(none)", PartyState(Facts.Seal));
        Assert.Equal("(none)", PartyState(Facts.NadarPlan));
        Assert.Equal("partial", _op.SecretStatus());
        Assert.Equal(before, _f.Dump());
        Assert.Equal(logAfterT5, _f.Log().Take(logAfterT5.Count));
        Assert.All(_f.Log(undo.UndoBatchId), r => Assert.Equal((t5.BatchId, "undo"), (r.UndoOf, r.Action)));
        Assert.Contains(undo.Changes, c => c.StartsWith("entity secret:fruits-are-the-seal status", StringComparison.Ordinal));
    }

    [Fact]
    public void RecordingTheSameRevealAgain_DoesNotRepeatTheWarnings()
    {
        _op.T1();
        _op.T2();
        _op.T3();
        _op.Reveal(10, false, Facts.Seal);

        var again = _op.Reveal(10, false, Facts.Seal);

        Assert.Empty(Gates(again));
        Assert.Equal(WriteOutcomes.Unchanged, Assert.Single(again.Applied).Outcome);
    }

    [Fact]
    public void SuspectsBecomingKnows_IsARevealInTheNewSession()
    {
        _op.T1();
        _op.T2();
        _op.T3();
        _f.Knowledge.Record(_op.Campaign, [Facts.Seal], [Op.Knower("party", "suspects")], WriteContext.For(9));

        var result = _op.Reveal(10, false, Facts.Seal);

        Assert.Equal(10, PartyLearned(Facts.Seal));
        Assert.Equal(10, Assert.Single(Gates(result, GateConditions.After)).RevealSession);
        Assert.Equal("revealed", _op.SecretStatus());
    }

    [Fact]
    public void RevealToACharacter_ChecksTheGateForThatKnower()
    {
        _op.T1();
        _op.T2();
        _op.T3();

        var result = _f.Knowledge.Reveal(_op.Campaign, [Facts.Seal], null, null, ["character:bjorn-mountainfell"], "told", WriteContext.For(10));

        var after = Assert.Single(Gates(result, GateConditions.After));
        Assert.Equal("character:bjorn-mountainfell", after.Knower);
        Assert.Equal("partial", _op.SecretStatus());
    }

    [Theory]
    [InlineData("author")]
    [InlineData("dm")]
    public void RevealToTheAuthorView_RunsNoGateChecks(string who)
    {
        var result = _f.Knowledge.Reveal(_op.Campaign, [Facts.Seal], null, null, [who], null, WriteContext.For(10));

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void UnawareRowOnAGatedFact_RunsNoGateChecks()
    {
        var result = _f.Knowledge.Record(_op.Campaign, [Facts.Seal], [Op.Knower("party", "unaware")], WriteContext.For(10));

        Assert.Empty(Gates(result));
    }

    [Fact]
    public void RevealTheSecretEntity_RevealsItsGatedFactsAndMakesThePartyAware()
    {
        _op.T1();
        _op.T2();
        _op.T3();
        _op.T4();

        var result = _f.Knowledge.Reveal(_op.Campaign, [Facts.NadarPlan], OnePieceFixture.Secret, null, null, "deduced", WriteContext.For(12));

        Assert.Equal("knows", PartyState(Facts.Seal));
        Assert.Equal("aware", _f.Scalar<string>(
            "SELECT k.state FROM knowledge k JOIN entity e ON e.id = k.entity_id WHERE e.slug = 'fruits-are-the-seal' AND k.knower_kind = 'party'"));
        Assert.DoesNotContain(Gates(result), g => g.Severity == WarningSeverities.Warning);
        Assert.Equal("revealed", _op.SecretStatus());
    }

    [Fact]
    public void SecretStatusSetByHand_IsRecomputedWithAWarning()
    {
        var result = _f.Apply(_op.Campaign, new CampaignOpSpec { Op = "status", Ref = OnePieceFixture.Secret, Status = "revealed" });

        Assert.Equal("seeded", _op.SecretStatus());
        var warning = Assert.Single(result.Warnings, w => w.Kind == WarningKinds.DerivedStatus);
        Assert.Contains("set to revealed and is recomputed as seeded", warning.Message);
        Assert.Equal(["status", "status"], _f.Log(result.BatchId).Select(r => r.FieldPath));
    }

    [Fact]
    public void RetractingTheSeed_DropsTheSecretToHidden()
    {
        var result = _f.Knowledge.Retract(_op.Campaign, [Facts.ShieldedChild], ["party"], WriteContext.Default);

        Assert.Equal("hidden", _op.SecretStatus());
        Assert.Equal(WriteOutcomes.Deleted, Assert.Single(result.Applied).Outcome);
    }

    [Fact]
    public void PartySuspectsTheGatedFact_IsPartialNotRevealed()
    {
        _f.Knowledge.Record(_op.Campaign, [Facts.Seal], [Op.Knower("party", "suspects")], WriteContext.For(10));

        Assert.Equal("partial", _op.SecretStatus());
    }

    [Theory]
    [InlineData(5, "hidden")]
    [InlineData(6, "seeded")]
    [InlineData(7, "seeded")]
    [InlineData(8, "seeded")]
    [InlineData(9, "partial")]
    [InlineData(11, "partial")]
    [InlineData(12, "revealed")]
    public void SecretStatusAsOfASession_ByReplay_FollowsTheSessionsThePartyLearnedIn_Row16(int session, string expected)
    {
        _op.T1();
        _op.T2();
        _op.T3();
        _op.T4();
        _op.T5();

        using var connection = _f.Open();
        var row = ChangeReplay.RowAsOf(connection, "entity", _f.Entity(_op.Campaign, OnePieceFixture.Secret).Id, session);

        Assert.Equal(expected, row!["status"]);
    }

    [Fact]
    public void UndoT2AfterT3_RederivesTheStatusInTheUndoBatch_AndRedoPutsEverythingBack()
    {
        _op.T1();
        var t2 = _op.T2();
        _op.T3();
        var before = _f.Dump();

        var undo = _f.History.Undo(_op.Campaign, t2.BatchId!, WriteContext.Default);

        // T2's own row put the status back to seeded, but the temple-arithmetic route T3 completed still stands.
        Assert.Equal("(none)", PartyState(Facts.IllusionRewatched));
        Assert.Equal("partial", _op.SecretStatus());
        var consequence = Assert.Single(undo.Consequences);
        Assert.Equal((ConsequenceKinds.SecretStatus, OnePieceFixture.Secret), (consequence.Kind, consequence.Ref));
        Assert.Contains("seeded → partial", consequence.Message);
        var log = _f.Log(undo.UndoBatchId);
        Assert.Equal(2, log.Count(r => r.TargetTable == "entity" && r.FieldPath == "status"));
        Assert.All(log, r => Assert.Equal((t2.BatchId, "undo"), (r.UndoOf, r.Action)));

        var redo = _f.History.Undo(_op.Campaign, undo.UndoBatchId!, WriteContext.Default);

        Assert.True(redo.WasRedo);
        Assert.Empty(redo.Consequences);
        Assert.Equal(before, _f.Dump());
    }

    [Fact]
    public void UndoOfTheLatestReveal_NeedsNoRederivation()
    {
        _op.T1();
        _op.T2();
        _op.T3();
        _op.T4();
        var t5 = _op.T5();

        var undo = _f.History.Undo(_op.Campaign, t5.BatchId!, WriteContext.Default);

        Assert.Empty(undo.Consequences);
        Assert.Equal("partial", _op.SecretStatus());
    }

    [Fact]
    public void RecordWithAnEarlierSessionInALaterContext_JudgesTheGateAtTheRowsOwnSession()
    {
        _op.T1();
        _op.T2();
        _op.T3();
        _op.T4();

        // Recorded in session 12's context, but learned in session 10, before the axe was assembled in 11.
        var result = _f.Knowledge.Record(_op.Campaign, [Facts.Seal], [new KnowerSpec { Who = "party", Session = 10 }], WriteContext.For(12));

        var after = Assert.Single(Gates(result, GateConditions.After));
        Assert.Equal(10, after.RevealSession);
        Assert.Equal([Facts.AxeAssembled], after.Unmet);
        Assert.Equal(10, PartyLearned(Facts.Seal));
    }

    [Fact]
    public void KnowsThenUnawareInOneBatch_IsNoRevealAndRaisesNoGateWarning()
    {
        var result = _f.Apply(_op.Campaign, WriteContext.For(10),
            new CampaignOpSpec { Op = "fact", Ref = Facts.Seal, KnownBy = [Op.Knower("party", "knows")] },
            new CampaignOpSpec { Op = "fact", Ref = Facts.Seal, KnownBy = [Op.Knower("party", "unaware")] });

        Assert.Empty(Gates(result));
        Assert.Empty(result.Warnings.OfType<ReachableBeforeGateWarning>());
        Assert.Equal("unaware", PartyState(Facts.Seal));
    }

    [Fact]
    public void RevealAfterTheCoupledFactWasDeleted_DoesNotAskForIt()
    {
        _op.T1();
        _op.T2();
        _op.T3();
        _op.T4();
        _f.Apply(_op.Campaign, new CampaignOpSpec { Op = "delete", Ref = Facts.NadarPlan });

        var result = _op.Reveal(12, false, Facts.Seal);

        Assert.Empty(Gates(result, GateConditions.With));
        Assert.Equal("revealed", _op.SecretStatus());
    }
}
