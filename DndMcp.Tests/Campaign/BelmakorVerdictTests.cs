using System.Text.Json;
using DndMcp.Domain.Campaign;
using Xunit;
using static DndMcp.Tests.Campaign.Knowers;

namespace DndMcp.Tests.Campaign;

/// <summary>
/// Invariant: the Belmakor fixture's knowledge verdicts (understand-belmakor.md §3 rows 1-19) come out of the pure rules
/// exactly as the goldens say, from the fixture's knowledge rows (§2.5 k1-k22), attendance (§2.3) and visibilities
/// (§2.4). "The Axiom Cage vs the old king": Belmakor knows the king as "the old king" and the thing as "the thing he
/// wants", never Keras or the Cage; the DM's "no record" is not "does not know"; Serif (absent, or joined later) and
/// Aiden (no attendance row) are uncertain about Tristan's death; the Contingency's table knowledge is point-in-time.
/// </summary>
public sealed class BelmakorVerdictTests
{
    // Targets and their knowledge rows (k1-k22), with the fixture's visibilities.
    private static readonly Dictionary<string, (string Visibility, KnowledgeEntry[] Rows)> Targets = new()
    {
        ["old-king"] = ("party",
        [
            Row("character", "met", 3, "the old king", "belmakor"), // k1
            Row("party", "met", 3, "the old king"), // k2
            Row("author", "knows", knownAs: "Keras"), // k3
        ]),
        ["thing-he-wants"] = ("party",
        [
            Row("party", "aware", 3, "the thing he wants"), // k4
            Row("character", "aware", 3, "the thing he wants", "belmakor"), // k5
            Row("author", "knows", knownAs: "the Axiom Cage"), // k6
        ]),
        ["f1"] = ("author", [Row("character", "unaware", knowerId: "belmakor"), Row("party", "unaware"), Row("author", "knows")]), // k7-k9
        ["f2"] = ("restricted", [Row("party", "unaware"), Row("author", "knows"), Row("dm", "knows")]), // k10-k12
        ["f3"] = ("party", [Row("party", "knows", 3)]), // k13
        ["f4"] = ("party", [Row("party", "believes", 3)]), // k14
        ["f5"] = ("restricted",
            [Row("character", "knows", knowerId: "belmakor"), Row("dm", "knows"), Row("author", "knows"), Row("party", "unaware")]), // k15-k18
        ["f6"] = ("party", [Row("character", "knows", knowerId: "belmakor"), Row("table", "knows", 2), Row("party", "knows", 2)]), // k19-k21
        ["f7"] = ("party", [Row("party", "knows", 1)]), // k22
        // Session 1's own entity (its recap tells Tristan's death): party-visible, as every session is, with no rows.
        ["session-1"] = ("party", []),
        // f:7 recorded the other way: party-visible, established in S1, with no party row (FD1's equivalence).
        ["f7-by-visibility"] = ("party", []),
    };

    // The session each target entered the party's knowledge in by its visibility (FD1): a session entity's own number, a
    // fact's established session. Tristan died in S1.
    private static readonly Dictionary<string, int> Since = new()
    {
        ["session-1"] = 1,
        ["f7"] = 1,
        ["f7-by-visibility"] = 1,
    };

    private static readonly Dictionary<string, string> Names = new()
    {
        ["belmakor"] = "Belmakor Silverwind",
        ["vars"] = "Vars Nocturne",
        ["ignis"] = "Ignis",
        ["serif"] = "Serif",
        ["torch"] = "Lieutenant James Torch",
        ["aiden-ironstar"] = "Aiden Ironstar",
        ["tristan"] = "Tristan",
    };

    // §2.3: S1 lists Belmakor, Ignis, Tristan present and Serif absent; Aiden has no row. S2 and S3 as recorded.
    private static readonly FakeAttendance Attendance = new FakeAttendance()
        .Row(1, "belmakor", true).Row(1, "ignis", true).Row(1, "tristan", true).Row(1, "serif", false)
        .Row(2, "belmakor", true).Row(2, "serif", true)
        .Row(3, "vars", true).Row(3, "belmakor", true).Row(3, "aiden-ironstar", true).Row(3, "ignis", true).Row(3, "serif", true)
        .Row(3, "torch", true);

    private static readonly string[] Forbidden = ["Keras", "Axiom", "Cage", "Baal", "Third Silence", "one-piece"];

    [Theory]
    // # | perspective | target | as_of | standing | state | known_as | basis
    [InlineData(1, "character:belmakor", "f1", null, "DoesNotKnow", "unaware", null, "explicit")]
    [InlineData(2, "character:belmakor", "thing-he-wants", null, "Knows", "aware", "the thing he wants", "explicit")]
    [InlineData(3, "character:belmakor", "old-king", null, "Knows", "met", "the old king", "explicit")]
    [InlineData(4, "character:belmakor", "f2", null, "DoesNotKnow", "unaware", null, "party")]
    [InlineData(5, "party", "f1", null, "DoesNotKnow", "unaware", null, "explicit")]
    [InlineData(5, "party", "thing-he-wants", null, "Knows", "aware", "the thing he wants", "explicit")]
    [InlineData(5, "party", "old-king", null, "Knows", "met", "the old king", "explicit")]
    [InlineData(6, "author", "f1", null, "Knows", "knows", null, "author")]
    [InlineData(6, "author", "f2", null, "Knows", "knows", null, "author")]
    [InlineData(7, "dm", "f2", null, "Knows", "knows", null, "explicit")]
    [InlineData(8, "dm", "f1", null, "NoRecord", null, null, "none")]
    [InlineData(9, "character:ignis", "f3", null, "Knows", "knows", null, "party_present")]
    [InlineData(10, "character:ignis", "thing-he-wants", null, "Knows", "aware", "the thing he wants", "party_present")]
    [InlineData(11, "character:serif", "f7", null, "Uncertain", "knows", null, "party")]
    [InlineData(12, "character:aiden-ironstar", "f7", null, "Uncertain", "knows", null, "party")]
    [InlineData(13, "character:belmakor", "f7", null, "Knows", "knows", null, "party_present")]
    [InlineData(14, "character:belmakor", "f5", null, "Knows", "knows", null, "explicit")]
    [InlineData(15, "character:vars", "f5", null, "DoesNotKnow", "unaware", null, "party")]
    [InlineData(16, "party", "f5", null, "DoesNotKnow", "unaware", null, "explicit")]
    [InlineData(16, "public", "f5", null, "NoRecord", null, null, "none")]
    [InlineData(18, "table", "f6", 2, "Knows", "knows", null, "explicit")]
    [InlineData(19, "character:belmakor", "f6", 1, "Knows", "knows", null, "explicit")]
    public void Evaluate_BelmakorRow_MatchesTheGolden(
        int row,
        string perspective,
        string target,
        int? asOf,
        string standing,
        string? state,
        string? knownAs,
        string basis)
    {
        var verdict = Verdict(perspective, target, asOf);

        Assert.True(standing == verdict.Standing.ToString(), $"row {row}: {verdict}");
        Assert.Equal(state, verdict.State);
        Assert.Equal(knownAs, verdict.KnownAs);
        Assert.Equal(basis, verdict.Basis);
    }

    [Fact]
    public void Evaluate_Row8DmOnTheCageFact_SaysNoRecordNotDoesNotKnow()
    {
        var verdict = Verdict("dm", "f1", null);

        Assert.Equal("no record", verdict.Explanation);
    }

    [Fact]
    public void Evaluate_Row11SerifAbsentInS1_IsUncertainBecauseAbsent()
    {
        var verdict = Verdict("character:serif", "f7", null);

        Assert.Equal("the party learned it in S1; Serif was absent", verdict.Explanation);
    }

    [Fact]
    public void Evaluate_Row11SerifJoinedInS2_IsUncertainBecauseHeJoinedLater()
    {
        var who = Character("serif", "Serif", new PartyMembership(2, null));
        var (visibility, rows) = Targets["f7"];

        var verdict = KnowledgeVerdicts.Evaluate(who, rows, visibility, Attendance);

        Assert.Equal(KnowledgeStanding.Uncertain, verdict.Standing);
        Assert.Equal("the party learned it in S1, before Serif joined in S2", verdict.Explanation);
    }

    [Fact]
    public void Evaluate_Row12AidenHasNoAttendanceRowInS1_IsUncertainNeverPresent()
    {
        var verdict = Verdict("character:aiden-ironstar", "f7", null);

        Assert.Equal("the party learned it in S1; no attendance recorded for Aiden Ironstar in S1", verdict.Explanation);
    }

    /// <summary>
    /// FD1 (review L02a): session 1's recap tells Tristan's death, and session entities are party-visible with no rows.
    /// Its readers are exactly those f:7's party row lets know the death: present in S1 (Belmakor, Ignis, Tristan) knows;
    /// Serif (absent) and Aiden (no attendance row) are uncertain, as golden rows 11 and 12 are, so neither reads it.
    /// </summary>
    [Theory]
    [InlineData("character:belmakor", "Knows", "party-visible since S1 and Belmakor Silverwind was present")]
    [InlineData("character:ignis", "Knows", "party-visible since S1 and Ignis was present")]
    [InlineData("character:tristan", "Knows", "party-visible since S1 and Tristan was present")]
    [InlineData("character:serif", "Uncertain", "party-visible since S1; Serif was absent")]
    [InlineData("character:aiden-ironstar", "Uncertain", "party-visible since S1; no attendance recorded for Aiden Ironstar in S1")]
    [InlineData("party", "Knows", "party-visible since S1: the party knows it")]
    [InlineData("table", "Knows", "party-visible since S1: the party knows it")]
    [InlineData("public", "NoRecord", "no record")]
    public void Evaluate_Session1Recap_IsKnownOnlyToTheMembersPresentThatNight(string perspective, string standing, string explanation)
    {
        var verdict = Verdict(perspective, "session-1", null);

        Assert.Equal((standing, explanation), (verdict.Standing.ToString(), verdict.Explanation));
    }

    [Fact]
    public void Evaluate_Session1RecapForSerifWhoJoinedInS2_IsUncertainBecauseHeJoinedLater()
    {
        var who = Character("serif", "Serif", new PartyMembership(2, null));

        var verdict = KnowledgeVerdicts.Evaluate(who, [], "party", Attendance, targetSinceSession: 1);

        Assert.Equal((KnowledgeStanding.Uncertain, "party-visible since S1, before Serif joined in S2"), (verdict.Standing, verdict.Explanation));
    }

    /// <summary>
    /// FD1: f:7 recorded as party-visible alone (no party row) gives every character the verdict its party row gives:
    /// rows 11-13 do not depend on how the death was written down.
    /// </summary>
    [Theory]
    [InlineData("character:serif", "Uncertain")]
    [InlineData("character:aiden-ironstar", "Uncertain")]
    [InlineData("character:belmakor", "Knows")]
    [InlineData("character:ignis", "Knows")]
    [InlineData("character:tristan", "Knows")]
    [InlineData("character:vars", "Uncertain")]
    public void Evaluate_F7ByVisibilityAlone_GivesTheVerdictOfItsPartyRow(string perspective, string standing)
    {
        var byRow = Verdict(perspective, "f7", null);
        var byVisibility = Verdict(perspective, "f7-by-visibility", null);

        Assert.Equal(standing, byVisibility.Standing.ToString());
        Assert.Equal(byRow.Standing, byVisibility.Standing);
    }

    [Fact]
    public void Evaluate_Row17TableAsOfS1_DoesNotKnowTheContingencyYet()
    {
        // f:6 was restricted until session 2 (change_log): the caller replays the visibility as of S1.
        var (_, rows) = Targets["f6"];

        var verdict = KnowledgeVerdicts.Evaluate(Of("table"), rows, "restricted", Attendance, asOfSession: 1);

        Assert.False(verdict.Knows);
        Assert.Equal(KnowledgeStanding.NoRecord, verdict.Standing);
    }

    [Fact]
    public void Evaluate_EveryNonAuthorPerspectiveOnEveryTarget_NeverMentionsTheCageOrKeras()
    {
        var perspectives = new[] { "party", "table", "dm", "public" }.Concat(Names.Keys.Select(k => "character:" + k));
        foreach (var perspective in perspectives)
        {
            foreach (var target in Targets.Keys)
            {
                foreach (var asOf in new int?[] { null, 1, 2, 3 })
                {
                    var json = JsonSerializer.Serialize(Verdict(perspective, target, asOf));

                    Assert.All(Forbidden, word => Assert.DoesNotContain(word, json, StringComparison.OrdinalIgnoreCase));
                }
            }
        }
    }

    private static KnowledgeVerdict Verdict(string perspective, string target, int? asOf)
    {
        var (visibility, rows) = Targets[target];
        return KnowledgeVerdicts.Evaluate(Who(perspective), rows, visibility, Attendance, asOf,
            Since.TryGetValue(target, out var since) ? since : null);
    }

    private static PerspectiveContext Who(string perspective)
    {
        if (!perspective.StartsWith("character:", StringComparison.Ordinal))
        {
            return perspective == "author" ? Author() : Of(perspective);
        }

        var id = perspective["character:".Length..];
        return Character(id, Names[id], new PartyMembership(null, null));
    }
}
