using DndMcp.Domain.Campaign.Ops;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Repository.Campaign.Write;
using Xunit;

namespace DndMcp.Tests.CampaignScenarios;

/// <summary>
/// Invariant: <c>understand-onepiece.md</c> §3 rows 26-30, the incomplete account, the ledger and supersession, on the
/// world the write path built. The Mistaken One's missing half is an explicit "unaware" (not an absence), his "too slow"
/// belief is shown as a partial truth, the ledger says the party met two fragments unrecognised and has not met the other
/// two, and superseding the old timeline (a campaign_write fact op) returns exactly its dependents with depth and via,
/// changes none of them, and makes the check list the stale "400 years" figure. The PLAN exit list's "superseding a fact
/// returns its dependents" is pinned here from both directions of the op.
/// </summary>
public sealed class OnePieceAccountScenarioTests : IDisposable
{
    private readonly OnePieceScenario _world = OnePieceScenario.Build();

    public void Dispose() => _world.Dispose();

    private CheckResult Check(string text, string speaker) => _world.Reads.Check(_world.Campaign, text, speaker);

    [Fact]
    public void Check_Row26TheMistakenOneOnHisHelper_ListsTheExplicitlyUnawareHalf()
    {
        var result = Check("I wasn't alone that day. Someone stood with me.", "character:mistaken-one");

        var helped = Assert.Single(result.UnknownFacts, f => f.Ref == _world.Facts.HadHelp);
        Assert.Equal(("unaware", Standings.DoesNotKnow), (helped.State, helped.Standing));
    }

    [Fact]
    public void Check_Row27TheMistakenOnesOwnAccount_HasNoUnknownFactAndShowsThePartialBelief()
    {
        var result = Check("It broke because I was slow.", "character:mistaken-one");

        Assert.Empty(result.UnknownFacts);
        var belief = Assert.Single(result.MistakenBeliefs);
        Assert.Equal((_world.Facts.TotalFault, "believes", "partial"), (belief.Ref, belief.State, belief.Truth));
        Assert.Contains($"mistaken:{_world.Facts.TotalFault}", CheckFlags.ToReview(result));
    }

    /// <summary>
    /// Row 28, in the design's wording: a met entity's cell names the state, the name the party knows it by and the
    /// session ("met, unrecognized as “the advisor in Serret” (S1)"; the golden's "met, unrecognized (S1)" plus the
    /// known_as), and an entity with no party row reads "not met" (the golden's "not yet met": the absence of a row, never
    /// "no record" and never "does not know").
    /// </summary>
    [Fact]
    public void Ledger_Row28TheFourFragmentsForTheParty_TwoMetUnrecognizedTwoNotMet()
    {
        var ledger = _world.Reads.Ledger(_world.Campaign,
            ["character:protector", "character:peaceful-one", "character:mistaken-one", "character:dutiful-one"], null, ["party"]);

        var cells = ledger.Rows.Where(r => r.IsEntity).ToDictionary(r => r.Ref, r => r.Cells.Single().Text);
        Assert.Equal("met, unrecognized as “the advisor in Serret” (S1)", cells["character:protector"]);
        Assert.Equal("met, unrecognized as “the man napping under the tree” (S5)", cells["character:peaceful-one"]);
        Assert.Equal("not met", cells["character:mistaken-one"]);
        Assert.Equal("not met", cells["character:dutiful-one"]);
    }

    // ---- supersession (row 29) and its dependents -----------------------------------------------------------------------

    private WriteResult SupersedeTimeline() => _world.F.Apply(_world.Campaign, new WriteContext { Reason = "Corrected by Cole, 2026-08-30" },
        new CampaignOpSpec { Op = "fact", Ref = _world.Facts.TimelineOld, CanonStatus = "superseded", SupersededBy = _world.Facts.Timeline20260830 });

    private IReadOnlyList<(string CanonStatus, string Confidence)> DependentStates() =>
        new[] { _world.Facts.Fight500900, _world.Facts.Fragments400, _world.Facts.LineageCovers }
            .Select(h => _world.F.Fact(_world.Campaign, h)).Select(f => (f.CanonStatus, f.Confidence)).ToList();

    [Fact]
    public void Supersede_Row29_ReturnsTheDependentsWithDepthAndViaAndLeavesThemUnchanged()
    {
        var before = DependentStates();

        var result = SupersedeTimeline();

        var old = _world.F.Fact(_world.Campaign, _world.Facts.TimelineOld);
        Assert.Equal(("superseded", _world.F.Fact(_world.Campaign, _world.Facts.Timeline20260830).Id), (old.CanonStatus, old.SupersededBy));
        var warning = Assert.Single(result.Warnings.OfType<SupersessionWarning>());
        Assert.Equal((_world.Facts.TimelineOld, _world.Facts.Timeline20260830), (warning.Superseded, warning.SupersededBy));
        Assert.Equal(
            [
                new DependentItem(_world.Facts.Fight500900, 1, _world.Facts.TimelineOld),
                new DependentItem(_world.Facts.Fragments400, 1, _world.Facts.TimelineOld),
                new DependentItem(_world.Facts.LineageCovers, 2, _world.Facts.Fight500900),
            ],
            warning.Dependents);
        Assert.Subset(warning.EntitiesToRecheck.ToHashSet(), new HashSet<string> { "question:q21", "question:q22", "character:arch-mage", "character:protector" });
        Assert.Equal(before, DependentStates());
        Assert.All(_world.F.Log(result.BatchId), r => Assert.Equal("Corrected by Cole, 2026-08-30", r.Reason));
    }

    /// <summary>
    /// "Superseding a fact returns its dependents" whichever end the op names: superseded_by on the old fact, or a new
    /// fact that supersedes it. Both give the same list; the dependents themselves are never touched.
    /// </summary>
    [Theory]
    [InlineData("superseded_by")]
    [InlineData("supersedes")]
    public void Supersede_EitherDirection_ReturnsTheSameDependents(string direction)
    {
        var op = direction == "supersedes"
            ? new CampaignOpSpec { Op = "fact", Statement = "The present is hundreds of years after sky-world.", CanonStatus = "ruled", Supersedes = _world.Facts.TimelineOld }
            : new CampaignOpSpec { Op = "fact", Ref = _world.Facts.TimelineOld, SupersededBy = _world.Facts.Timeline20260830 };

        var result = _world.F.Apply(_world.Campaign, op);

        var warning = Assert.Single(result.Warnings.OfType<SupersessionWarning>());
        Assert.Equal([_world.Facts.Fight500900, _world.Facts.Fragments400, _world.Facts.LineageCovers], warning.Dependents.Select(d => d.Fact));
        Assert.Equal([1, 1, 2], warning.Dependents.Select(d => d.Depth));
        Assert.Equal("superseded", _world.F.Fact(_world.Campaign, _world.Facts.TimelineOld).CanonStatus);
    }

    /// <summary>Row 29's read side: the author's campaign_get of the old timeline shows it superseded and lists what rests on it, nearest first.</summary>
    [Fact]
    public void Get_Row29ReadBack_TheAuthorSeesTheSupersessionAndTheDependents()
    {
        SupersedeTimeline();

        var fact = _world.Reads.Get(_world.Campaign, "author", EntityIncludes.All, null, _world.Facts.TimelineOld).Facts.Single();

        Assert.Equal(("superseded", _world.Facts.Timeline20260830), (fact.Author!.Fact.CanonStatus, fact.Author.Fact.SupersededBy));
        Assert.Equal([_world.Facts.Fight500900, _world.Facts.Fragments400, _world.Facts.LineageCovers], fact.Author.Dependents);
    }

    /// <summary>A superseded fact with no dependents gives an empty list, not a missing warning: "nothing rests on it" is an answer.</summary>
    [Fact]
    public void Supersede_AFactNothingRestsOn_WarnsWithNoDependents()
    {
        var result = _world.F.Apply(_world.Campaign,
            new CampaignOpSpec { Op = "fact", Ref = _world.Facts.TooSlow, SupersededBy = _world.Facts.HadHelp });

        var warning = Assert.Single(result.Warnings.OfType<SupersessionWarning>());
        Assert.Empty(warning.Dependents);
    }

    [Fact]
    public void Check_Row30FourHundredYearsAfterTheCorrection_ListsTheStaleFragmentsFact()
    {
        SupersedeTimeline();

        var result = Check("'I've watched this city for 400 years,' the Protector says.", "table");

        var stale = Assert.Single(result.Stale, s => s.Ref == _world.Facts.Fragments400);
        Assert.Equal((_world.Facts.TimelineOld, 1), (stale.Superseded, stale.Depth));
        Assert.Contains($"stale:{_world.Facts.Fragments400}", CheckFlags.ToReview(result));
    }

    /// <summary>
    /// Row 30's known gap, pinned as the design answers it: a text is related to a fact through the words they share (the
    /// fact index's porter tokenizer), so the digits "400" find the stale figure and the number words "four hundred" do
    /// not (they tokenize to "four", "hundr"). A line quoting the figure in words is caught only through another shared
    /// word ("years") or a named entity the fact is about (the Protector); the check never converts number words.
    /// </summary>
    [Theory]
    [InlineData("For 400 winters I've watched this city.", true)]
    [InlineData("For four hundred winters I've watched this city.", false)]
    [InlineData("For four hundred years I've watched this city.", true)]
    [InlineData("'Four hundred winters,' the Protector says.", true)]
    public void Check_Row30TheFigureInDigitsOrWords_IsStaleOnlyThroughASharedWordOrName(string text, bool listed)
    {
        SupersedeTimeline();

        var result = Check(text, "table");

        Assert.Equal(listed, result.Stale.Any(s => s.Ref == _world.Facts.Fragments400));
    }

    /// <summary>Row 30 before the correction: nothing is stale yet (the figure rests on a canon fact).</summary>
    [Fact]
    public void Check_Row30BeforeTheCorrection_NothingIsStale()
    {
        var result = Check("'I've watched this city for 400 years,' the Protector says.", "table");

        Assert.DoesNotContain(result.Stale, s => s.Ref == _world.Facts.Fragments400);
    }
}
