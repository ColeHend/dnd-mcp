using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Write;
using Xunit;

namespace DndMcp.Tests.CampaignWrite;

/// <summary>
/// Invariant: facts are created and updated with their links, gates (stored with fact ids, printed with handles),
/// dependencies (acyclic) and supersession (dependents listed with depth and via, left unchanged); register codes follow
/// §3.1 (one F sequence per campaign shared by entities and facts, max + 1 over struck and deleted rows, suffixes
/// ignored, never reused or changed, the same in a dry run). One Piece rows 29 and 41-48 are pinned here.
/// </summary>
public sealed class FactOpTests : IDisposable
{
    private readonly WriteFixture _f = new();
    private readonly CampaignRow _dm;

    public FactOpTests()
    {
        _dm = _f.Campaign("One Piece");
    }

    public void Dispose() => _f.Dispose();

    // ---- register codes (One Piece rows 41-48) ------------------------------------------------------------------------

    private void SeedRegister()
    {
        _f.Apply(_dm,
            Op.Fact("INVENTED accepted invention", "accepted", code: "F1"),
            Op.Fact("INVENTED struck invention", "struck", code: "F2"));
    }

    [Fact]
    public void Fact_ProposedWithNoCode_GetsTheNextF_Row41()
    {
        SeedRegister();

        var result = _f.Apply(_dm, Op.Fact("The harbourmaster owes Nadar a favour.", "proposed"));

        Assert.Equal("F3", Assert.Single(result.Applied).Code);
    }

    [Fact]
    public void Upsert_ProposedAfterAFact_SharesTheSequence_Row42()
    {
        SeedRegister();
        _f.Apply(_dm, Op.Fact("One.", "proposed"));

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Harbourmaster", CanonStatus = "proposed" });

        Assert.Equal("F4", Assert.Single(result.Applied).Code);
    }

    [Fact]
    public void StruckCodes_AreNeverReused_AndKeepTheirCode_Row43()
    {
        SeedRegister();
        _f.Apply(_dm, Op.Fact("One.", "proposed"));
        _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Harbourmaster", CanonStatus = "proposed" });

        _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Ref = "F4", CanonStatus = "struck" });
        var next = _f.Apply(_dm, Op.Fact("Two.", "proposed"));

        Assert.Equal("F5", Assert.Single(next.Applied).Code);
        Assert.Equal("F4", _f.Entity(_dm, "character:harbourmaster").Code);
    }

    [Fact]
    public void Accepting_KeepsTheCode_Row44()
    {
        SeedRegister();
        _f.Apply(_dm, Op.Fact("One.", "proposed"));

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "fact", Ref = "F3", CanonStatus = "accepted" });

        var fact = _f.Fact(_dm, "F3");
        Assert.Equal(("accepted", "F3"), (fact.CanonStatus, fact.Code));
        Assert.Equal(["canon_status"], Assert.Single(result.Applied).ChangedFields);
    }

    [Theory]
    [InlineData("canon")]
    [InlineData("played")]
    [InlineData("ruled")]
    [InlineData("lean")]
    public void Fact_NotProposed_GetsNoCode_Row45(string canonStatus)
    {
        var result = _f.Apply(_dm, Op.Fact("A fact.", canonStatus));

        Assert.Null(Assert.Single(result.Applied).Code);
    }

    [Fact]
    public void Fact_ExplicitCodeTaken_IsRefusedNamingTheHolderAndTheNextCode_Row46()
    {
        SeedRegister();
        _f.Apply(_dm, Op.Fact("One.", "proposed"));
        _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Harbourmaster", CanonStatus = "proposed" });
        _f.Apply(_dm, Op.Fact("Two.", "proposed"));

        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, Op.Fact("Three.", "proposed", code: "F3")));

        Assert.Contains("F3 is taken (by f:3); omit code to auto-number (next F6)", ex.Message);
    }

    [Fact]
    public void Fact_ExplicitCodeHeldByAnEntity_IsRefused()
    {
        _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Kind = "question", Name = "How long ago?", Code = "q22" });

        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, Op.Fact("A fact.", code: "Q22")));

        Assert.Contains("Q22 is taken (by e:2); omit code to auto-number (next Q23)", ex.Message);
    }

    [Fact]
    public void DryRun_ShowsTheSameCodeTheRealRunAssigns_Row47()
    {
        SeedRegister();

        var dry = _f.Apply(_dm, new WriteContext { DryRun = true }, Op.Fact("One.", "proposed"));
        var real = _f.Apply(_dm, Op.Fact("One.", "proposed"));

        Assert.True(dry.DryRun);
        Assert.Null(dry.BatchId);
        Assert.Equal("F3", Assert.Single(dry.Applied).Code);
        Assert.Equal("F3", Assert.Single(real.Applied).Code);
    }

    [Fact]
    public void Codes_ArePerCampaign_Row48()
    {
        var other = _f.Campaign("Belmakor", role: "player");
        SeedRegister();

        var here = _f.Apply(_dm, Op.Fact("Here.", "proposed"));
        var there = _f.Apply(other, Op.Fact("There.", "proposed"));

        Assert.Equal("F3", Assert.Single(here.Applied).Code);
        Assert.Equal("F1", Assert.Single(there.Applied).Code);
    }

    [Fact]
    public void Codes_CountSoftDeletedRowsAndIgnoreSuffixes()
    {
        _f.Apply(_dm, Op.Fact("Suffixed.", "accepted", code: "F56a"), Op.Fact("Deleted.", "proposed", code: "F60"));
        _f.Apply(_dm, new CampaignOpSpec { Op = "delete", Ref = "F60" });

        var result = _f.Apply(_dm, Op.Fact("Next.", "proposed"));

        Assert.Equal("F61", Assert.Single(result.Applied).Code);
    }

    [Fact]
    public void Codes_CountASoftDeletedEntitysCode()
    {
        _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Struck bard", CanonStatus = "proposed", Code = "F9" });
        _f.Apply(_dm, new CampaignOpSpec { Op = "delete", Ref = "F9" });

        var result = _f.Apply(_dm, Op.Fact("Next.", "proposed"));

        Assert.Equal("F10", Assert.Single(result.Applied).Code);
    }

    [Fact]
    public void Codes_TwoProposedInOneBatch_GetConsecutiveCodes()
    {
        var result = _f.Apply(_dm, Op.Fact("One.", "proposed"), Op.Fact("Two.", "proposed"),
            new CampaignOpSpec { Op = "upsert", Kind = "note", Name = "Three", CanonStatus = "proposed" });

        Assert.Equal(["F1", "F2", "F3"], result.Applied.Select(a => a.Code));
    }

    [Fact]
    public void AutoCode_TakesTheNextOfItsLetter()
    {
        _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Kind = "note", Name = "Callback", Code = "C13" });

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "fact", Statement = "A callback.", AutoCode = "c" });

        Assert.Equal("C14", Assert.Single(result.Applied).Code);
    }

    [Theory]
    [InlineData("F9")]
    [InlineData(null)]
    public void Code_NeverChangesOnceAssigned(string? code)
    {
        _f.Apply(_dm, Op.Fact("One.", "proposed"));

        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, new CampaignOpSpec
        {
            Op = "fact", Ref = "f:1", Code = code, AutoCode = code is null ? "F" : null,
        }));

        Assert.Contains("never changes once assigned", ex.Message);
    }

    [Fact]
    public void Code_SameAsItsOwn_IsNotAChange()
    {
        _f.Apply(_dm, Op.Fact("One.", "proposed"));

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "fact", Ref = "f:1", Code = "f1" });

        Assert.Equal(WriteOutcomes.Unchanged, Assert.Single(result.Applied).Outcome);
    }

    // ---- links, gates, dependencies -----------------------------------------------------------------------------------

    [Fact]
    public void Fact_AboutAndLinks_AreAddedOnceWithTheirRoles()
    {
        _f.Apply(_dm, Op.Upsert("secret", "Fruits are the seal"), Op.Upsert("character", "Nadar"));

        var result = _f.Apply(_dm, new CampaignOpSpec
        {
            Op = "fact", Statement = "A clue.", About = ["character:nadar"],
            Links = [new FactLinkSpec { Ref = "secret:fruits-are-the-seal", Role = "Clue For" }, new FactLinkSpec { Ref = "character:nadar" }],
        });

        Assert.Equal(["statement", "about", "links"], Assert.Single(result.Applied).ChangedFields);
        var links = _f.Query<FactLinkRow>($"SELECT {FactLinkRow.Columns} FROM fact_link ORDER BY role");
        Assert.Equal(["about", "clue_for"], links.Select(l => l.Role));
    }

    [Fact]
    public void Fact_Gate_IsStoredWithFactIdsAndPrintedBackWithHandles()
    {
        _f.Apply(_dm, Op.Fact("The axe is assembled.", "planned"), new CampaignOpSpec { Op = "fact", Statement = "Nadar's plan.", Code = "S2" });

        var result = _f.Apply(_dm, new CampaignOpSpec
        {
            Op = "fact", Statement = "Eating a fruit breaks the seal.",
            Gate = new GateSpec
            {
                After = ["f:1"], With = ["S2"], ForbiddenTerms = ["seal"], PreferredTerms = ["shell"],
                Routes = [new RouteSpec { Id = "illusion", Clues = ["f:1"] }], Note = "Reveal order.",
            },
        });

        var stored = FactGates.Parse(_f.Fact(_dm, "f:3").Gate)!;
        Assert.Equal([_f.Fact(_dm, "f:1").Id], stored.After);
        Assert.Equal([_f.Fact(_dm, "f:2").Id], stored.With);
        var printed = Assert.Single(result.Applied).Gate!;
        Assert.Equal(["f:1"], printed.After);
        Assert.Equal(["f:2"], printed.With);
        Assert.Equal(["f:1"], printed.Routes![0]!.Clues);
        Assert.Equal(["seal"], printed.ForbiddenTerms);
        using var connection = _f.Open();
        Assert.Equal(["f:1"], CampaignGates.ToHandles(connection, _dm.Id, _f.Fact(_dm, "f:3").Gate)!.After);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{\"after\": \"f:1\"}")]
    public void CampaignGates_NoGateOrAnUnreadableOne_PrintsNothing(string? stored)
    {
        using var connection = _f.Open();

        Assert.Null(CampaignGates.ToHandles(connection, _dm.Id, stored));
    }

    [Fact]
    public void Fact_EmptyGate_RemovesTheGate()
    {
        _f.Apply(_dm, Op.Fact("Before."), new CampaignOpSpec { Op = "fact", Statement = "Gated.", Gate = new GateSpec { After = ["f:1"] } });

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "fact", Ref = "f:2", Gate = new GateSpec() });

        Assert.Null(_f.Fact(_dm, "f:2").Gate);
        Assert.Equal(["gate"], Assert.Single(result.Applied).ChangedFields);
    }

    [Theory]
    [InlineData("f:9", "gate: no fact f:9 in this campaign")]
    [InlineData("F1", "gate names f:1, which is this fact")]
    public void Fact_GateNamingAnUnknownFactOrItself_IsRefused(string handle, string expected)
    {
        _f.Apply(_dm, Op.Fact("Coded.", "accepted", code: "F1"));

        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, new CampaignOpSpec { Op = "fact", Ref = "f:1", Gate = new GateSpec { After = [handle] } }));

        Assert.Contains(expected, ex.Message);
    }

    /// <summary>
    /// Review UR3: f:&lt;n&gt; numbers come from one sequence shared by every campaign in the file, so an f:&lt;n&gt; that names
    /// no fact here is refused saying so, and what names a fact made earlier in the batch (a code). A code is the
    /// campaign's own: a missing one is refused without that sentence, which would send the model hunting a numbering
    /// problem it does not have.
    /// </summary>
    [Theory]
    [InlineData("f:9", "gate: no fact f:9 in this campaign; give f:<n> or its code. f:<n> numbers are shared by all campaigns; to use a fact " +
                       "made earlier in this batch, give it a code (code \"A1\") and use that.")]
    [InlineData("Z9", "gate: no fact Z9 in this campaign; give f:<n> or its code.")]
    public void Fact_GateNamingNoFactOfThisCampaign_SaysNumbersAreSharedOnlyForAnFNumber(string handle, string expected)
    {
        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm,
            Op.Fact("The axe is assembled.", "planned"),
            new CampaignOpSpec { Op = "fact", Statement = "The seal holds.", Gate = new GateSpec { After = [handle] } }));

        Assert.EndsWith(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fact_GateNamingAFactCreatedEarlierInTheBatch_Resolves()
    {
        var result = _f.Apply(_dm,
            new CampaignOpSpec { Op = "fact", Statement = "The axe is assembled.", Code = "S1", CanonStatus = "planned" },
            new CampaignOpSpec { Op = "fact", Statement = "Gated.", Gate = new GateSpec { After = ["S1"] } });

        Assert.Equal(["f:1"], result.Applied[1].Gate!.After);
    }

    [Fact]
    public void Fact_GateNamingOneFactUnderTwoHandlesInAfterAndWith_IsRefused()
    {
        _f.Apply(_dm, Op.Fact("Coded.", "accepted", code: "F1"));

        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, new CampaignOpSpec
        {
            Op = "fact", Statement = "Gated.", Gate = new GateSpec { After = ["f:1"], With = ["F1"] },
        }));

        Assert.Contains("f:1 is in both after and with", ex.Message);
    }

    [Fact]
    public void Fact_DependsOnThatClosesACycle_IsRefused()
    {
        _f.Apply(_dm, Op.Fact("A."), new CampaignOpSpec { Op = "fact", Statement = "B.", DependsOn = ["f:1"] },
            new CampaignOpSpec { Op = "fact", Statement = "C.", DependsOn = ["f:2"] });

        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, new CampaignOpSpec { Op = "fact", Ref = "f:1", DependsOn = ["f:3"] }));

        Assert.Contains("depends_on item 1: f:3 already depends on f:1; depending on it would close a cycle.", ex.Message);
    }

    [Fact]
    public void Fact_PlayedWithoutEstablishedSession_TakesTheBatchSession()
    {
        _f.Played(_dm, 7);

        var result = _f.Apply(_dm, WriteContext.For(7), Op.Fact("The party holds a fruit and a shard.", "played"));

        Assert.Empty(result.Warnings);
        Assert.Equal(_f.Entity(_dm, "session:7").Id, _f.Fact(_dm, "f:1").EstablishedSessionId);
    }

    [Fact]
    public void Fact_PlayedWithNoSessionAnywhere_WarnsItIsNotInPlay()
    {
        var result = _f.Apply(_dm, Op.Fact("Played somewhere.", "played"));

        Assert.Contains(result.Warnings, w => w.Kind == WarningKinds.NotInPlay && w.Message.Contains("f:1 is played but has no established session"));
    }

    private void AxeGate()
    {
        _f.Apply(_dm, Op.Fact("The War God's axe is fully assembled.", "planned", visibility: "party"));
        _f.Apply(_dm, new CampaignOpSpec
        {
            Op = "fact", Statement = "Eating a devil fruit breaks part of Baal's seal.",
            Gate = new GateSpec { After = ["f:1"], ForbiddenTerms = ["seal"], ForbiddenUntil = ["f:1"] },
        });
    }

    /// <summary>
    /// Review U14: only played gives a fact its established session (§3.6), so a fact a gate waits for that is set to
    /// canon, ruled or accepted ("that's canon now") is never in play: the gate stays closed and its words forbidden. The
    /// write says so, in a session or out of one, naming what waits for it.
    /// </summary>
    [Theory]
    [InlineData("canon", false)]
    [InlineData("ruled", false)]
    [InlineData("accepted", false)]
    [InlineData("canon", true)]
    public void Fact_ThatAGateWaitsForSetToCanonWithNoEstablishedSession_WarnsItIsNotInPlay(string canonStatus, bool live)
    {
        AxeGate();
        if (live)
        {
            _f.Sessions.Start(_dm, 8);
        }

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "fact", Ref = "f:1", CanonStatus = canonStatus });

        var warning = Assert.Single(result.Warnings, w => w.Kind == WarningKinds.NotInPlay);
        Assert.Equal((WarningSeverities.Warning, (int?)0), (warning.Severity, warning.OpIndex));
        Assert.Equal($"f:1 is {canonStatus} but has no established session, so it is not in play for gates (f:2's after and forbidden_until); " +
                     "mark it played or give established_session.", warning.Message);
    }

    /// <summary>
    /// Prep writes the fact and the gate that waits for it in one call, the fact canon by default: the fact is new, so it
    /// is checked with no canon_status given, and every gate field that needs it in play is named (prefer too: "probably
    /// when" it is in play, which it never will be).
    /// </summary>
    [Theory]
    [InlineData("after")]
    [InlineData("prefer")]
    [InlineData("forbidden_until")]
    public void Fact_CreatedCanonInTheBatchWhoseGateWaitsForIt_WarnsItIsNotInPlay(string field)
    {
        var gate = field switch
        {
            "after" => new GateSpec { After = ["f:1"] },
            "prefer" => new GateSpec { Prefer = ["f:1"] },
            _ => new GateSpec { ForbiddenTerms = ["seal"], ForbiddenUntil = ["f:1"] },
        };

        var result = _f.Apply(_dm, Op.Fact("Baal is bound."), new CampaignOpSpec { Op = "fact", Statement = "The fruits are the seal.", Gate = gate });

        var warning = Assert.Single(result.Warnings, w => w.Kind == WarningKinds.NotInPlay);
        Assert.Equal((int?)0, warning.OpIndex);
        Assert.Equal($"f:1 is canon but has no established session, so it is not in play for gates (f:2's {field}); mark it played or give established_session.",
            warning.Message);
    }

    [Fact]
    public void Fact_ThatARevealRuleWaitsForSetToCanonWithNoEstablishedSession_WarnsItIsNotInPlay()
    {
        _f.Apply(_dm, Op.Fact("The old king tells the party his name.", "planned"),
            new CampaignOpSpec
            {
                Op = "upsert", Kind = "rule", Subtype = "reveal_rule", Name = "No name for the old king", Data = Op.Data("{\"forbidden_terms\": [\"Keras\"], \"until\": [\"f:1\"]}"),
            });

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "fact", Ref = "f:1", CanonStatus = "canon" });

        Assert.Contains(result.Warnings, w => w.Kind == WarningKinds.NotInPlay &&
            w.Message == "f:1 is canon but has no established session, so it is not in play for reveal rules (rule:no-name-for-the-old-king's until); " +
                         "mark it played or give established_session.");
    }

    /// <summary>
    /// Nothing to say when the fact is (or becomes, later in the same call) established, is played, is not in an in-play
    /// status, or nothing waits for it to be in play (a route clue counts when the party knows it, in play or not).
    /// </summary>
    [Theory]
    [InlineData("played", true, false, false)]
    [InlineData("canon", false, true, false)]
    [InlineData("canon", false, false, true)]
    [InlineData("lean", false, false, false)]
    public void Fact_SetToCanonWhereNothingWaitsForItToBeInPlay_DoesNotWarn(string canonStatus, bool live, bool established, bool clueOnly)
    {
        _f.Played(_dm, 3);
        _f.Apply(_dm, Op.Fact("The axe is assembled.", "planned"));
        _f.Apply(_dm, new CampaignOpSpec
        {
            Op = "fact", Statement = "The seal.",
            Gate = clueOnly
                ? new GateSpec { Routes = [new RouteSpec { Id = "clue", Clues = ["f:1"] }] }
                : new GateSpec { After = ["f:1"] },
        });
        if (live)
        {
            _f.Sessions.Start(_dm, 4);
        }

        CampaignOpSpec[] ops = established
            ? [new CampaignOpSpec { Op = "fact", Ref = "f:1", CanonStatus = canonStatus }, new CampaignOpSpec { Op = "fact", Ref = "f:1", EstablishedSession = 3 }]
            : [new CampaignOpSpec { Op = "fact", Ref = "f:1", CanonStatus = canonStatus }];
        var result = _f.Apply(_dm, ops);

        Assert.DoesNotContain(result.Warnings, w => w.Kind == WarningKinds.NotInPlay);
    }

    [Fact]
    public void Fact_ExplicitEstablishedSession_MustExist()
    {
        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, new CampaignOpSpec { Op = "fact", Statement = "X.", EstablishedSession = 11 }));

        Assert.Contains("established_session: no session 11 in this campaign", ex.Message);
    }

    [Fact]
    public void Fact_UpdateNothing_IsUnchanged()
    {
        _f.Apply(_dm, Op.Fact("X.", visibility: "party"));

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "fact", Ref = "f:1", Statement = "X.", Visibility = "party" });

        Assert.Equal(WriteOutcomes.Unchanged, Assert.Single(result.Applied).Outcome);
        Assert.Empty(_f.Log(result.BatchId));
    }

    [Fact]
    public void Fact_Defaults_AreRestrictedCanonTrueConfirmed()
    {
        _f.Apply(_dm, Op.Fact("Defaults."));

        var fact = _f.Fact(_dm, "f:1");
        Assert.Equal(("restricted", "canon", "true", "canon", "confirmed"), (fact.Visibility, fact.FactType, fact.Truth, fact.CanonStatus, fact.Confidence));
    }

    // ---- supersession (One Piece row 29) ------------------------------------------------------------------------------

    [Fact]
    public void Supersede_ListsDependentsWithDepthAndViaAndEntitiesToRecheck_LeavingThemUnchanged_Row29()
    {
        _f.Apply(_dm,
            Op.Upsert("question", "How rare is the long-lived lineage?"), Op.Upsert("question", "How long ago was the fight?"),
            Op.Upsert("character", "Arch mage"), Op.Upsert("character", "The Protector"));
        _f.Apply(_dm,
            Op.Fact("Sky-world is ~100-200 years after the first campaign.", "canon"),
            new CampaignOpSpec { Op = "fact", Statement = "Sky-world is at least 1,000 years after.", CanonStatus = "ruled", Confidence = "approximate" },
            new CampaignOpSpec
            {
                Op = "fact", Statement = "The fight was ~500-900 years ago.", DependsOn = ["f:1"],
                About = ["question:how-rare-is-the-long-lived-lineage", "character:arch-mage"],
            },
            new CampaignOpSpec
            {
                Op = "fact", Statement = "The fragments have run down for roughly 400 years.", DependsOn = ["f:1"],
                About = ["question:how-long-ago-was-the-fight", "character:the-protector"],
            },
            new CampaignOpSpec
            {
                Op = "fact", Statement = "The lineage covers the ~500-900 years.", DependsOn = ["f:3"],
                About = ["question:how-rare-is-the-long-lived-lineage"],
            });
        var before = _f.Query<(string CanonStatus, string Confidence)>("SELECT canon_status, confidence FROM fact WHERE seq IN (3, 4, 5) ORDER BY seq");

        var result = _f.Apply(_dm, new WriteContext { Reason = "Corrected by Cole, 2026-08-30" },
            new CampaignOpSpec { Op = "fact", Ref = "f:1", CanonStatus = "superseded", SupersededBy = "f:2" });

        var old = _f.Fact(_dm, "f:1");
        Assert.Equal(("superseded", _f.Fact(_dm, "f:2").Id), (old.CanonStatus, old.SupersededBy));
        var warning = Assert.IsType<SupersessionWarning>(Assert.Single(result.Warnings));
        Assert.Equal(("f:1", "f:2"), (warning.Superseded, warning.SupersededBy));
        Assert.Equal(
            [new DependentItem("f:3", 1, "f:1"), new DependentItem("f:4", 1, "f:1"), new DependentItem("f:5", 2, "f:3")],
            warning.Dependents);
        Assert.Equal(
            ["question:how-rare-is-the-long-lived-lineage", "character:arch-mage", "question:how-long-ago-was-the-fight", "character:the-protector"],
            warning.EntitiesToRecheck);
        Assert.Equal(before, _f.Query<(string, string)>("SELECT canon_status, confidence FROM fact WHERE seq IN (3, 4, 5) ORDER BY seq"));
        Assert.All(_f.Log(result.BatchId), r => Assert.Equal("Corrected by Cole, 2026-08-30", r.Reason));
    }

    [Fact]
    public void Supersedes_MarksTheOtherFactAndListsItsDependents()
    {
        _f.Apply(_dm, Op.Fact("Belmakor is level 11."), new CampaignOpSpec { Op = "fact", Statement = "He is a veteran.", DependsOn = ["f:1"] });

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "fact", Statement = "Belmakor is level 12.", Supersedes = "f:1" });

        var old = _f.Fact(_dm, "f:1");
        Assert.Equal(("superseded", _f.Fact(_dm, "f:3").Id), (old.CanonStatus, old.SupersededBy));
        var warning = Assert.IsType<SupersessionWarning>(Assert.Single(result.Warnings));
        Assert.Equal(("f:1", "f:3"), (warning.Superseded, warning.SupersededBy));
        Assert.Equal([new DependentItem("f:2", 1, "f:1")], warning.Dependents);
        Assert.Contains("supersedes", Assert.Single(result.Applied).ChangedFields);
    }

    [Fact]
    public void Fact_CreatedAlreadySuperseded_SetsSupersededByWithoutAWarning()
    {
        _f.Apply(_dm, Op.Fact("Keras had help."));

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "fact", Statement = "Near best case.", SupersededBy = "f:1" });

        var fact = _f.Fact(_dm, "f:2");
        Assert.Equal(("superseded", _f.Fact(_dm, "f:1").Id), (fact.CanonStatus, fact.SupersededBy));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Fact_RefNotFoundOrDeleted_IsRefused()
    {
        _f.Apply(_dm, Op.Fact("Gone."));
        _f.Apply(_dm, new CampaignOpSpec { Op = "delete", Ref = "f:1" });

        var deleted = Assert.Throws<DndInputException>(() => _f.Apply(_dm, new CampaignOpSpec { Op = "fact", Ref = "f:1", Statement = "Back?" }));
        var missing = Assert.Throws<DndInputException>(() => _f.Apply(_dm, new CampaignOpSpec { Op = "fact", Ref = "f:7", Statement = "?" }));

        Assert.Contains("ref f:1 is deleted (f:1); restore it first", deleted.Message);
        Assert.Contains("ref: no fact f:7 in this campaign", missing.Message);
    }

    [Theory]
    [InlineData("superseded_by")]
    [InlineData("supersedes")]
    public void Fact_SupersedingItselfUnderAnotherHandle_IsRefused(string field)
    {
        _f.Apply(_dm, Op.Fact("The fight was 500 years ago.", code: "T1"));
        var before = _f.Dump();

        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, new CampaignOpSpec
        {
            Op = "fact", Ref = "T1", SupersededBy = field == "superseded_by" ? "f:1" : null, Supersedes = field == "supersedes" ? "f:1" : null,
        }));

        Assert.Contains("a fact cannot supersede itself (f:1)", ex.Message);
        Assert.Equal(before, _f.Dump());
    }

    [Fact]
    public void Supersede_DeletedDependentsAndDeletedEntities_AreNotListed()
    {
        _f.Apply(_dm, Op.Upsert("character", "Arch mage"), Op.Upsert("character", "Gone mage"));
        _f.Apply(_dm,
            Op.Fact("Old timeline."),
            Op.Fact("New timeline."),
            new CampaignOpSpec { Op = "fact", Statement = "Live dependent.", DependsOn = ["f:1"], About = ["character:arch-mage", "character:gone-mage"] },
            new CampaignOpSpec { Op = "fact", Statement = "Deleted dependent.", DependsOn = ["f:1"], About = ["character:arch-mage"] });
        _f.Apply(_dm, new CampaignOpSpec { Op = "delete", Ref = "f:4" }, new CampaignOpSpec { Op = "delete", Ref = "character:gone-mage" });

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "fact", Ref = "f:1", SupersededBy = "f:2" });

        var warning = Assert.IsType<SupersessionWarning>(Assert.Single(result.Warnings));
        Assert.Equal([new DependentItem("f:3", 1, "f:1")], warning.Dependents);
        Assert.Equal(["character:arch-mage"], warning.EntitiesToRecheck);
    }

    [Theory]
    [InlineData("superseded_by")]
    [InlineData("supersedes")]
    public void Supersession_RepointedToAnotherFact_IsAppliedWithAWarning(string field)
    {
        _f.Apply(_dm, Op.Fact("Old."), Op.Fact("Newer."), Op.Fact("Newest."));
        _f.Apply(_dm, new CampaignOpSpec { Op = "fact", Ref = "f:1", SupersededBy = "f:2" });

        var result = field == "superseded_by"
            ? _f.Apply(_dm, new CampaignOpSpec { Op = "fact", Ref = "f:1", SupersededBy = "f:3" })
            : _f.Apply(_dm, new CampaignOpSpec { Op = "fact", Ref = "f:3", Supersedes = "f:1" });

        Assert.Equal(("superseded", _f.Fact(_dm, "f:3").Id), (_f.Fact(_dm, "f:1").CanonStatus, _f.Fact(_dm, "f:1").SupersededBy));
        var warning = Assert.Single(result.Warnings);
        Assert.Equal((WarningKinds.SupersessionRepointed, WarningSeverities.Warning), (warning.Kind, warning.Severity));
        Assert.Contains("f:1 was superseded by f:2; it is now superseded by f:3 instead", warning.Message);
    }

    [Fact]
    public void Supersession_SameReplacementAgain_IsNoRepoint()
    {
        _f.Apply(_dm, Op.Fact("Old."), Op.Fact("New."));
        _f.Apply(_dm, new CampaignOpSpec { Op = "fact", Ref = "f:1", SupersededBy = "f:2" });

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "fact", Ref = "f:2", Supersedes = "f:1" });

        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("canon", null)]
    [InlineData("lean", null)]
    [InlineData("superseded", "f:2")]
    public void Fact_LeavingSuperseded_ClearsSupersededBy(string canonStatus, string? supersededBy)
    {
        _f.Apply(_dm, Op.Fact("Old."), Op.Fact("New."));
        _f.Apply(_dm, new CampaignOpSpec { Op = "fact", Ref = "f:1", SupersededBy = "f:2" });

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "fact", Ref = "f:1", CanonStatus = canonStatus });

        var fact = _f.Fact(_dm, "f:1");
        Assert.Equal((canonStatus, supersededBy is null ? null : _f.Fact(_dm, supersededBy).Id), (fact.CanonStatus, fact.SupersededBy));
        string[] changed = supersededBy is null ? ["canon_status", "superseded_by"] : [];
        Assert.Equal(changed, Assert.Single(result.Applied).ChangedFields);
    }
}
