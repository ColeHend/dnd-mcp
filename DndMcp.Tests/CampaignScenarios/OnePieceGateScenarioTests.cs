using DndMcp.Domain.Campaign.Ops;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tests.CampaignWrite;
using Xunit;

namespace DndMcp.Tests.CampaignScenarios;

/// <summary>
/// Invariant: <c>understand-onepiece.md</c> §3 rows 9-15, 20 and 23, the seal gate's "warn and apply, never refuse"
/// (contract §3.4, §11), end to end: the write path applies every gated reveal and returns the warnings (unmet after,
/// unmet or separately-landed with — symmetric —, too few routes, the advisory prefer, reachable before the gate), stores
/// them in the batch's change_log reason, and the read path then shows the party knowing exactly what was written. A
/// refusal here would leave the database contradicting what was said at the table; a warning lost here would let a
/// session-recap batch reveal the twist early without anyone reading why.
/// </summary>
public sealed class OnePieceGateScenarioTests : IDisposable
{
    private readonly OnePieceScenario _world = OnePieceScenario.Build();

    public void Dispose() => _world.Dispose();

    private OnePieceFacts Facts => _world.Facts;

    private static IReadOnlyList<GateWarning> Gates(WriteResult result, string? condition = null) =>
        result.Warnings.OfType<GateWarning>().Where(w => condition is null || w.Gate == condition).ToList();

    /// <summary>The gate warnings of severity "warning" (the advisory prefer excluded), as (condition, unmet) pairs in order.</summary>
    private static IReadOnlyList<(string Gate, string Unmet)> Hard(WriteResult result) =>
        Gates(result).Where(g => g.Severity == WarningSeverities.Warning).Select(g => (g.Gate, string.Join(",", g.Unmet))).ToList();

    /// <summary>
    /// Until T4 nothing makes @no-uneaten-fruits true (it is planned), so every reveal of the seal also carries the
    /// advisory "prefer" (row 15's golden): "probably" is a soft condition, reported but never counted as a gate warning.
    /// </summary>
    private void AssertOnlyAdvisoryPrefer(WriteResult result)
    {
        var prefer = Assert.Single(Gates(result, GateConditions.Prefer));
        Assert.Equal((WarningSeverities.Advisory, Facts.Seal), (prefer.Severity, prefer.Fact));
        Assert.Equal([Facts.NoUneatenFruits], prefer.Unmet);
    }

    private LedgerCell Party(string fact) => _world.Reads.Cell(_world.Campaign, fact, "party");

    private void ThroughT3()
    {
        _world.T1();
        _world.T2();
        _world.T3();
    }

    [Fact]
    public void Reveal_Row9BeforeTheAxe_IsWrittenWithAfterAndWithWarningsInTheReason()
    {
        ThroughT3();

        var result = _world.Reveal(10, false, Facts.Seal);

        var cell = Party(Facts.Seal);
        Assert.Equal((Standings.Knows, 10), (cell.Standing, cell.LearnedSession));
        var after = Assert.Single(Gates(result, GateConditions.After));
        Assert.Equal((Facts.Seal, "party", 10), (after.Fact, after.Knower, after.RevealSession));
        Assert.Equal([Facts.AxeAssembled], after.Unmet);
        Assert.DoesNotContain(Facts.HoldsFruitAndShard, after.Unmet);
        Assert.Equal([Facts.NadarPlan], Assert.Single(Gates(result, GateConditions.With)).Unmet);
        Assert.Empty(Gates(result, GateConditions.Routes));
        Assert.Equal([(GateConditions.After, Facts.AxeAssembled), (GateConditions.With, Facts.NadarPlan)], Hard(result));
        AssertOnlyAdvisoryPrefer(result);
        Assert.Empty(result.Warnings.OfType<ReachableBeforeGateWarning>());
        var reasons = _world.F.Log(result.BatchId).Select(r => r.Reason).ToList();
        Assert.NotEmpty(reasons);
        Assert.All(reasons, r =>
        {
            Assert.Contains(after.Message, r);
            Assert.Contains(Gates(result, GateConditions.With)[0].Message, r);
        });
    }

    /// <summary>Row 9's read side: once written, the party's campaign_get of the seal fact works (restricted + a party row), gate or no gate.</summary>
    [Fact]
    public void Reveal_Row9ReadBack_ThePartyCanOpenTheFactItWasTold()
    {
        ThroughT3();
        Assert.Null(_world.Reads.TryGet(_world.Campaign, "party", Facts.Seal));

        _world.Reveal(10, false, Facts.Seal);

        var fact = _world.Reads.Get(_world.Campaign, "party", new EntityIncludes(Knowledge: true), null, Facts.Seal).Facts.Single();
        Assert.Equal("Eating a devil fruit breaks part of Baal's seal.", fact.Text);
        Assert.Null(fact.Author);
    }

    [Fact]
    public void Reveal_Row10DryRun_GivesTheSameWarningsWritesNothingAndLeavesNoBatch()
    {
        ThroughT3();
        var before = _world.F.Dump();
        var batches = _world.Reads.BatchCount(_world.Campaign);

        var dry = _world.Reveal(10, true, Facts.Seal);
        var real = _world.Reveal(10, false, Facts.Seal);

        Assert.True(dry.DryRun);
        Assert.Null(dry.BatchId);
        Assert.Equal(Gates(real).Select(g => (g.Gate, g.Severity, g.Message)), Gates(dry).Select(g => (g.Gate, g.Severity, g.Message)));
        Assert.Equal([(GateConditions.After, Facts.AxeAssembled), (GateConditions.With, Facts.NadarPlan)], Hard(dry));
        AssertOnlyAdvisoryPrefer(dry);
        Assert.NotEqual(before, _world.F.Dump());
        Assert.Equal(batches + 1, _world.Reads.BatchCount(_world.Campaign));
    }

    [Fact]
    public void Reveal_Row10DryRunAlone_ChangesNoRowAndNoHistory()
    {
        ThroughT3();
        var before = _world.F.Dump();
        var log = _world.F.Log().Count;
        var batches = _world.Reads.BatchCount(_world.Campaign);

        _world.Reveal(10, true, Facts.Seal);

        Assert.Equal(before, _world.F.Dump());
        Assert.Equal(log, _world.F.Log().Count);
        Assert.Equal(batches, _world.Reads.BatchCount(_world.Campaign));
        Assert.Equal(Standings.NoRecord, Party(Facts.Seal).Standing);
    }

    /// <summary>Row 11: campaign_write's known_by goes through the same gate evaluator as campaign_knowledge.</summary>
    [Fact]
    public void Write_Row11KnownByOnAFactOp_GivesTheSameTwoWarningsAsTheReveal()
    {
        ThroughT3();
        var viaReveal = _world.Reveal(10, true, Facts.Seal);

        var viaWrite = _world.F.Apply(_world.Campaign, WriteContext.For(10),
            new CampaignOpSpec { Op = "fact", Ref = Facts.Seal, KnownBy = [Op.Knower("party", "knows")] });

        Assert.Equal(Gates(viaReveal).Select(g => (g.Gate, g.Severity, g.Message)), Gates(viaWrite).Select(g => (g.Gate, g.Severity, g.Message)));
        Assert.Equal([(GateConditions.After, Facts.AxeAssembled), (GateConditions.With, Facts.NadarPlan)], Hard(viaWrite));
        AssertOnlyAdvisoryPrefer(viaWrite);
        Assert.Equal((Standings.Knows, 10), (Party(Facts.Seal).Standing, Party(Facts.Seal).LearnedSession));
    }

    [Fact]
    public void Reveal_Row12AfterT4TheSealAlone_OnlyTheWithCouplingWarns()
    {
        _world.ThroughT4();

        var result = _world.Reveal(12, false, Facts.Seal);

        var warning = Assert.Single(Gates(result), g => g.Severity == WarningSeverities.Warning);
        Assert.Equal((GateConditions.With, Facts.Seal), (warning.Gate, warning.Fact));
        Assert.Equal([Facts.NadarPlan], warning.Unmet);
        Assert.Empty(Gates(result, GateConditions.After));
    }

    /// <summary>
    /// Row 15's coupling with no session at all (a reveal written between sessions, with nothing live): "a batch that
    /// reveals both counts" (contract §3.4) is what makes it land together, since there is no session number to compare.
    /// Both facts are applied, undated, and nothing but the advisory prefer is reported. Were the batch's own reveals not
    /// counted, every sessionless reveal of a coupled pair would warn that each landed without the other.
    /// </summary>
    [Fact]
    public void Reveal_Row15TheCoupledPairInOneBatchWithNoSession_LandsTogether()
    {
        _world.ThroughT4();

        var result = _world.F.Knowledge.Reveal(_world.Campaign, [Facts.Seal, Facts.NadarPlan], null, null, ["party"], null, WriteContext.Default);

        Assert.Empty(Gates(result, GateConditions.With));
        Assert.Empty(Hard(result));
        AssertOnlyAdvisoryPrefer(result);
        Assert.Equal((Standings.Knows, (int?)null), (Party(Facts.Seal).Standing, Party(Facts.Seal).LearnedSession));
        Assert.Equal((Standings.Knows, (int?)null), (Party(Facts.NadarPlan).Standing, Party(Facts.NadarPlan).LearnedSession));
    }

    [Fact]
    public void Reveal_Row13NadarsPlanAlone_WarnsTheOtherWay_TheCouplingIsSymmetric()
    {
        _world.ThroughT4();

        var result = _world.Reveal(12, false, Facts.NadarPlan);

        var warning = Assert.Single(Gates(result, GateConditions.With));
        Assert.Equal((Facts.NadarPlan, "party"), (warning.Fact, warning.Knower));
        Assert.Equal([Facts.Seal], warning.Unmet);
    }

    [Fact]
    public void Reveal_Row14CoupledFactLearnedInSession8_LandedSeparately()
    {
        _world.ThroughT4();
        _world.Reveal(8, false, Facts.NadarPlan);

        var result = _world.Reveal(12, false, Facts.Seal);

        var warning = Assert.Single(Gates(result, GateConditions.With));
        Assert.Empty(warning.Unmet);
        Assert.Equal([new LandedSeparatelyItem(Facts.NadarPlan, 8)], warning.LandedSeparately);
        Assert.Equal(12, warning.RevealSession);
        Assert.Equal(8, Party(Facts.NadarPlan).LearnedSession);
        Assert.Equal(12, Party(Facts.Seal).LearnedSession);
    }

    [Fact]
    public void Reveal_Row15T5_OnlyTheAdvisoryPreferAndTheSecretIsRevealed()
    {
        _world.ThroughT4();

        var t5 = _world.T5();

        var prefer = Assert.Single(Gates(t5));
        Assert.Equal((GateConditions.Prefer, WarningSeverities.Advisory, Facts.Seal), (prefer.Gate, prefer.Severity, prefer.Fact));
        Assert.Equal([Facts.NoUneatenFruits], prefer.Unmet);
        Assert.Equal("Reveal order — do not break this (canon-core.md:172-195)", prefer.Note);
        Assert.DoesNotContain(t5.Warnings, w => w.Severity == WarningSeverities.Warning);
        Assert.Equal("revealed", _world.StoredSecretStatus());
        Assert.Equal("revealed", _world.SecretView().DerivedStatus);
        Assert.All(_world.F.Log(t5.BatchId), r => Assert.Contains(prefer.Message, r.Reason));
    }

    [Fact]
    public void Record_Row20T3_CompletesTheSecondRouteAndWarnsReachableBeforeTheGate()
    {
        _world.T1();
        _world.T2();

        var t3 = _world.T3();

        var warning = Assert.Single(t3.Warnings.OfType<ReachableBeforeGateWarning>());
        Assert.Equal((OnePieceScenario.Secret, Facts.Seal, 2, 2), (warning.Secret, warning.Fact, warning.RoutesComplete, warning.MinRoutes));
        Assert.Equal([Facts.AxeAssembled], warning.AfterUnmet);
        Assert.Equal(Standings.Knows, Party(Facts.BreachesClimbing).Standing);
        Assert.Equal(Standings.Knows, Party(Facts.FruitAppearancesFalling).Standing);
        Assert.All(_world.F.Log(t3.BatchId), r => Assert.Contains("reachable before the gate", r.Reason));
        Assert.True(_world.SecretView().Gates.Single().ReachableBeforeGate);
    }

    /// <summary>
    /// Row 20: the warning fires on the call that completes the second route, and on no other step: not before it, not
    /// on later knowledge batches while the state still holds (routes 2/2 with the axe unmet: another route clue, a dry-run
    /// reveal of the seal), and not after T4. Were it repeated while the state holds, every later batch's change_log reason
    /// would carry it, and the one batch that made the secret reachable early could no longer be told from the rest.
    /// </summary>
    [Fact]
    public void Record_Row20TheOtherSteps_DoNotWarnReachableBeforeTheGate()
    {
        var before = new[] { _world.T1(), _world.T2() };
        _world.T3();
        var whileReachable = new[] { _world.Record(10, Facts.TPeaceful), _world.Reveal(10, true, Facts.Seal) };
        var stillReachable = _world.SecretView().Gates.Single().ReachableBeforeGate;
        var after = new[] { _world.T4(), _world.Record(11, Facts.TMistaken) };

        Assert.True(stillReachable, "the steps between T3 and T4 ran while the secret was reachable before its gate");
        Assert.All(before.Concat(whileReachable).Concat(after), s => Assert.Empty(s.Warnings.OfType<ReachableBeforeGateWarning>()));
        Assert.All(_world.F.Log(whileReachable[0].BatchId), r => Assert.DoesNotContain("reachable before the gate", r.Reason ?? string.Empty));
    }

    /// <summary>
    /// Contract §3.4: a gate's <c>after</c> is judged "in play at the reveal's session", not now. After T4 the axe is in
    /// play from session 11, but a reveal recorded into session 10 (the DM writing up an earlier session late) happened
    /// before the axe was assembled, so it warns the unmet <c>after</c> for session 10 and is applied, with the reveal
    /// dated S10. Judged "now", it would pass silently and the history would say the order was kept when it was not.
    /// </summary>
    [Fact]
    public void Reveal_IntoASessionBeforeTheAxeWasAssembled_WarnsAfterAtThatSessionAndApplies()
    {
        _world.ThroughT4();

        var result = _world.Reveal(10, false, Facts.Seal, Facts.NadarPlan);

        var after = Assert.Single(Gates(result, GateConditions.After));
        Assert.Equal((Facts.Seal, "party", 10), (after.Fact, after.Knower, after.RevealSession));
        Assert.Equal([Facts.AxeAssembled], after.Unmet);
        Assert.Equal([(GateConditions.After, Facts.AxeAssembled)], Hard(result));
        Assert.Equal((Standings.Knows, 10), (Party(Facts.Seal).Standing, Party(Facts.Seal).LearnedSession));
    }

    [Fact]
    public void Reveal_Row23T5WithOnlyOneRoute_WarnsTooFewRoutesAndStillApplies()
    {
        _world.T1();
        _world.T2();

        var t5 = _world.T5();

        var routes = Assert.Single(Gates(t5, GateConditions.Routes));
        Assert.Equal((1, 2), (routes.RoutesComplete, routes.MinRoutes));
        Assert.Equal(["illusion"], routes.CompleteRouteIds);
        Assert.Single(Gates(t5, GateConditions.After));
        Assert.Empty(Gates(t5, GateConditions.With));
        Assert.Equal(Standings.Knows, Party(Facts.Seal).Standing);
        Assert.Equal(Standings.Knows, Party(Facts.NadarPlan).Standing);
        Assert.Equal("revealed", _world.StoredSecretStatus());
    }

    /// <summary>
    /// "Warn and apply" for every non-author knower: a character and the table get the same gate checks as the party,
    /// and the write is applied for each.
    /// </summary>
    [Theory]
    [InlineData("character:bjorn-mountainfell")]
    [InlineData("table")]
    [InlineData("public")]
    public void Reveal_BeforeTheGateToAnyNonAuthorKnower_IsAppliedWithTheAfterWarning(string who)
    {
        ThroughT3();

        var result = _world.F.Knowledge.Reveal(_world.Campaign, [Facts.Seal], null, null, [who], "told", WriteContext.For(10));

        var after = Assert.Single(Gates(result, GateConditions.After));
        Assert.Equal(who, after.Knower);
        Assert.Equal(Standings.Knows, _world.Reads.Cell(_world.Campaign, Facts.Seal, who).Standing);
    }
}
