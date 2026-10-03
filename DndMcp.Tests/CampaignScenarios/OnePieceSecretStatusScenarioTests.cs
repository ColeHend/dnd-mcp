using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Repository.Campaign.Write;
using Xunit;

namespace DndMcp.Tests.CampaignScenarios;

/// <summary>
/// Invariant: <c>understand-onepiece.md</c> §3 rows 16-25, the secret's routes and status across the scenario, with the
/// two halves of the system agreeing at every step: the status the WRITE path derived and stored (contract §3.4: written
/// as an ordinary logged update in the batch that changed the knowledge) and the status the READ path derives from the
/// gates when the author opens the secret. hidden (as of S5) → seeded (the seed, S6) → seeded (T1) → partial (T2: one
/// route) → partial (T3: routes 2/2, after unmet) → partial and ready (T4) → revealed (T5); undoing T5 puts it back to
/// partial and ready. If the two halves drifted, the summary would show one status and the secret's page another.
/// </summary>
public sealed class OnePieceSecretStatusScenarioTests : IDisposable
{
    private readonly OnePieceScenario _world = OnePieceScenario.Build();

    public void Dispose() => _world.Dispose();

    private static GateStatusView Gate(SecretStatusView secret) => secret.Gates.Single();

    private static IReadOnlyList<(string Id, int Known, int Needed)> Routes(SecretStatusView secret) =>
        Gate(secret).Routes.Select(r => (r.Id, r.Known, r.Needed)).ToList();

    [Fact]
    public void Get_Row16AsOfSession5_IsHiddenWithNoRouteProgress()
    {
        var secret = _world.SecretView(asOf: 5);

        Assert.Equal(("hidden", "hidden"), (secret.StoredStatus, secret.DerivedStatus));
        Assert.Equal([("testimonies", 0, 4), ("illusion", 0, 1), ("temple-arithmetic", 0, 2)], Routes(secret));
        Assert.Equal((0, 2), (Gate(secret).RoutesComplete, Gate(secret).MinRoutes));
    }

    [Fact]
    public void Get_Row17Baseline_IsSeededFromTheSeedLearnedInSession6()
    {
        var secret = _world.SecretView();

        Assert.Equal(("seeded", "seeded"), (secret.StoredStatus, secret.DerivedStatus));
        Assert.True(Gate(secret).Seeded);
        Assert.Equal((0, 2), (Gate(secret).RoutesComplete, Gate(secret).MinRoutes));
    }

    [Fact]
    public void Get_Row18AfterT1_SeededWithOneTestimonyOfFour()
    {
        _world.T1();

        var secret = _world.SecretView();

        Assert.Equal(("seeded", "seeded"), (secret.StoredStatus, secret.DerivedStatus));
        Assert.Equal(("testimonies", 1, 4), Routes(secret)[0]);
        Assert.Equal(0, Gate(secret).RoutesComplete);
    }

    [Fact]
    public void Get_Row19AfterT2_PartialWithTheIllusionRouteAndNotReady()
    {
        _world.T1();
        _world.T2();

        var secret = _world.SecretView();

        Assert.Equal(("partial", "partial"), (secret.StoredStatus, secret.DerivedStatus));
        Assert.Equal((1, 2), (Gate(secret).RoutesComplete, Gate(secret).MinRoutes));
        Assert.Equal(["illusion"], Gate(secret).CompleteRouteIds);
        Assert.False(Gate(secret).Ready);
    }

    [Fact]
    public void Get_Row21AfterT3_RoutesSatisfiedButNotReadyBecauseTheAxeIsNotInPlay()
    {
        _world.T1();
        _world.T2();
        _world.T3();

        var secret = _world.SecretView();

        Assert.Equal(("partial", "partial"), (secret.StoredStatus, secret.DerivedStatus));
        Assert.Equal(["illusion", "temple-arithmetic"], Gate(secret).CompleteRouteIds);
        Assert.True(Gate(secret).RoutesComplete >= Gate(secret).MinRoutes);
        Assert.Equal([_world.Facts.AxeAssembled], Gate(secret).UnmetAfter);
        Assert.False(Gate(secret).Ready);
        Assert.True(Gate(secret).ReachableBeforeGate);
    }

    [Fact]
    public void Get_Row22AfterT4_PartialReadyAndMustLandWithNadarsPlan()
    {
        _world.ThroughT4();

        var secret = _world.SecretView();

        Assert.Equal(("partial", "partial"), (secret.StoredStatus, secret.DerivedStatus));
        Assert.True(Gate(secret).Ready);
        Assert.Empty(Gate(secret).UnmetAfter);
        Assert.Equal([_world.Facts.NadarPlan], Gate(secret).MustLandWith);
        Assert.False(Gate(secret).ForbiddenActive);
    }

    [Fact]
    public void Get_Row24AfterT5_Revealed()
    {
        _world.ThroughT4();
        _world.T5();

        var secret = _world.SecretView();

        Assert.Equal(("revealed", "revealed"), (secret.StoredStatus, secret.DerivedStatus));
        Assert.True(Gate(secret).KnownToParty);
    }

    /// <summary>Rows 16-24 in one walk: the stored and the derived status agree after every step.</summary>
    [Fact]
    public void Statuses_Rows17To24EveryStep_StoredAndDerivedAgree()
    {
        var seen = new List<(string? Stored, string Derived)>();
        void Take()
        {
            var view = _world.SecretView();
            seen.Add((view.StoredStatus, view.DerivedStatus));
        }

        Take();
        _world.T1();
        Take();
        _world.T2();
        Take();
        _world.T3();
        Take();
        _world.T4();
        Take();
        _world.T5();
        Take();

        Assert.Equal(["seeded", "seeded", "partial", "partial", "partial", "revealed"], seen.Select(s => s.Derived));
        Assert.All(seen, s => Assert.Equal(s.Derived, s.Stored));
    }

    /// <summary>
    /// Row 16 as a point-in-time read after the whole scenario: the stored status replayed from change_log and the status
    /// derived from the knowledge as of that session agree for every session.
    /// </summary>
    [Theory]
    [InlineData(5, "hidden")]
    [InlineData(6, "seeded")]
    [InlineData(7, "seeded")]
    [InlineData(8, "seeded")]
    [InlineData(9, "partial")]
    [InlineData(10, "partial")]
    [InlineData(11, "partial")]
    [InlineData(12, "revealed")]
    public void Get_Row16AsOfEachSessionAfterT5_StoredAndDerivedFollowTheSessionsThePartyLearnedIn(int session, string expected)
    {
        _world.ThroughT4();
        _world.T5();

        var secret = _world.SecretView(asOf: session);

        Assert.Equal((expected, expected), (secret.StoredStatus, secret.DerivedStatus));
    }

    /// <summary>
    /// Rows 9 and 16's "established" rule as of a session: @holds-fruit-and-shard was written as prep (no session context)
    /// but established in session 5, so as of session 1 it is not yet in play and the gate lists it unmet beside the axe;
    /// from session 5 only the axe is unmet. A reader that judged "in play" by canon status alone would call the party
    /// fruit-and-shard holders in session 1.
    /// </summary>
    [Theory]
    [InlineData(1, true)]
    [InlineData(5, false)]
    [InlineData(7, false)]
    public void Get_Row16TheAfterFactsAsOfASession_AreInPlayFromTheirEstablishedSession(int session, bool holdsUnmet)
    {
        var unmet = Gate(_world.SecretView(asOf: session)).UnmetAfter;

        var expected = holdsUnmet ? new[] { _world.Facts.AxeAssembled, _world.Facts.HoldsFruitAndShard } : [_world.Facts.AxeAssembled];
        Assert.Equal(expected.Order(StringComparer.Ordinal), unmet.Order(StringComparer.Ordinal));
    }

    /// <summary>Row 22's ready flips with the point in time: as of 10 the axe was not in play, as of 11 it was.</summary>
    [Theory]
    [InlineData(10, false)]
    [InlineData(11, true)]
    public void Get_Row22AsOfTheSessionTheAxeWasAssembled_ReadyFollowsInPlay(int session, bool ready)
    {
        _world.ThroughT4();

        Assert.Equal(ready, Gate(_world.SecretView(asOf: session)).Ready);
    }

    [Fact]
    public void Undo_Row25T5_RemovesThePartyRowsAndPutsTheSecretBackToPartialAndReady()
    {
        _world.ThroughT4();
        var before = _world.F.Dump();
        var t5 = _world.T5();
        var logAfterT5 = _world.F.Log();

        var undo = _world.F.History.Undo(_world.Campaign, t5.BatchId!, WriteContext.Default);

        Assert.Equal(Standings.NoRecord, _world.Reads.Cell(_world.Campaign, _world.Facts.Seal, "party").Standing);
        Assert.Equal(Standings.NoRecord, _world.Reads.Cell(_world.Campaign, _world.Facts.NadarPlan, "party").Standing);
        var secret = _world.SecretView();
        Assert.Equal(("partial", "partial"), (secret.StoredStatus, secret.DerivedStatus));
        Assert.True(Gate(secret).Ready);
        Assert.Equal(before, _world.F.Dump());
        Assert.Equal(logAfterT5, _world.F.Log().Take(logAfterT5.Count));
        var compensating = _world.F.Log(undo.UndoBatchId);
        Assert.NotEmpty(compensating);
        Assert.All(compensating, r => Assert.Equal((t5.BatchId, "undo"), (r.UndoOf, r.Action)));
        Assert.Equal(logAfterT5.Count + compensating.Count, _world.F.Log().Count);
    }

    /// <summary>Row 25's read side: after the undo the party can no longer open the seal fact, and its search no longer finds it.</summary>
    [Fact]
    public void Undo_Row25ReadBack_ThePartyNoLongerSeesTheSeal()
    {
        _world.ThroughT4();
        var t5 = _world.T5();
        Assert.NotNull(_world.Reads.TryGet(_world.Campaign, "party", _world.Facts.Seal));

        _world.F.History.Undo(_world.Campaign, t5.BatchId!, WriteContext.Default);

        Assert.Null(_world.Reads.TryGet(_world.Campaign, "party", _world.Facts.Seal));
        Assert.Empty(_world.Reads.Search(_world.Campaign, "seal", "party").Facts);
    }

    /// <summary>
    /// The contract §8 decision "undo batches re-derive secret statuses": undoing T2 while T3 stands (no conflict: T3 wrote
    /// other rows) reverses T2's own status update (partial back to seeded), and the undo batch then re-derives from what
    /// still stands: the temple-arithmetic route is complete, so the stored status is partial, agrees with the derived one,
    /// and the undo reports the re-derivation. Without it the summary would say "seeded" while the secret's page said
    /// "partial" until some later batch happened to touch the secret's knowledge.
    /// </summary>
    [Fact]
    public void Undo_T2WhileT3Stands_ReDerivesTheStoredStatusFromWhatStillStands()
    {
        _world.T1();
        var t2 = _world.T2().BatchId!;
        _world.T3();

        var undo = _world.F.History.Undo(_world.Campaign, t2, WriteContext.Default);

        var secret = _world.SecretView();
        Assert.Equal(("partial", "partial"), (secret.StoredStatus, secret.DerivedStatus));
        Assert.Equal(["temple-arithmetic"], Gate(secret).CompleteRouteIds);
        Assert.Contains(undo.Consequences, c => c.Kind == ConsequenceKinds.SecretStatus && c.Ref == OnePieceScenario.Secret);
        Assert.Equal(Standings.NoRecord, _world.Reads.Cell(_world.Campaign, _world.Facts.IllusionRewatched, "party").Standing);
    }

    /// <summary>
    /// The replayed status as of session 12 after T5 was undone (outside any session) is revealed no longer: the undo is
    /// filed with no session, so as of any session it is "timeless" and never reversed (contract §3.5), and the knowledge
    /// rows it deleted are simply gone.
    /// </summary>
    [Fact]
    public void Undo_Row25AsOfSession12_ShowsTheStateAfterTheUndo()
    {
        _world.ThroughT4();
        var t5 = _world.T5();

        _world.F.History.Undo(_world.Campaign, t5.BatchId!, WriteContext.Default);

        var secret = _world.SecretView(asOf: 12);
        Assert.Equal(("partial", "partial"), (secret.StoredStatus, secret.DerivedStatus));
    }

    /// <summary>Contract §3.4 / the §8 stage-2 decision: a gated fact the party only suspects is partial, never revealed.</summary>
    [Fact]
    public void Record_PartySuspectsTheSeal_IsPartialNotRevealedInBothHalves()
    {
        _world.F.Knowledge.Record(_world.Campaign, [_world.Facts.Seal], [new Domain.Campaign.Ops.KnowerSpec { Who = "party", State = "suspects" }],
            WriteContext.For(10));

        var secret = _world.SecretView();

        Assert.Equal(("partial", "partial"), (secret.StoredStatus, secret.DerivedStatus));
        Assert.False(Gate(secret).KnownToParty);
    }

    /// <summary>The replay of the stored status and the change_log agree: the T5 batch holds the status update, logged in session 12.</summary>
    [Fact]
    public void Log_T5_CarriesTheDerivedStatusUpdateFiledUnderSession12()
    {
        _world.ThroughT4();
        var t5 = _world.T5();

        var secretId = _world.F.Entity(_world.Campaign, OnePieceScenario.Secret).Id;
        var row = Assert.Single(_world.F.Log(t5.BatchId), r => r.TargetTable == "entity" && r.TargetId == secretId && r.FieldPath == "status");
        Assert.Equal(_world.F.Entity(_world.Campaign, "session:12").Id, row.SessionId);
        using var connection = _world.F.Open();
        Assert.Equal("partial", ChangeReplay.RowAsOf(connection, "entity", secretId, 11)!["status"]);
    }
}
