using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Write;
using Xunit;

namespace DndMcp.Tests.CampaignWrite;

/// <summary>
/// Invariant: a link is stored as what its handles make it (a cross_link for same_as to another campaign, ordered
/// a &lt; b; a beat_edge for leads_to between beats; else one relation per (from, rel, to), updated on a second link),
/// with §3.7's default visibility and session numbers stored as session ids; a character joining the party during a
/// session joins as of that session unless since says otherwise; unlink removes exactly that link (a symmetric relation
/// from either end) and warns when there is none.
/// </summary>
public sealed class LinkOpTests : IDisposable
{
    private readonly WriteFixture _f = new();
    private readonly CampaignRow _dm;

    public LinkOpTests()
    {
        _dm = _f.Campaign("One Piece");
        _f.Apply(_dm, Op.Upsert("character", "Bjorn", "pc"), Op.Upsert("character", "Nadar", "deity"));
    }

    public void Dispose() => _f.Dispose();

    private RelationRow MemberOf(string member) =>
        Assert.Single(_f.Query<RelationRow>($"SELECT {RelationRow.Columns} FROM relation WHERE rel = 'member_of' AND from_id = @id",
            new { id = _f.Entity(_dm, member).Id }));

    /// <summary>
    /// Review C04: a character linked into the party during a live session with no since joins as of that session, and the
    /// result says so. With no since he read as an original member, and knew what the party learned before he joined
    /// wherever attendance was not recorded.
    /// </summary>
    [Fact]
    public void Link_CharacterJoiningThePartyDuringALiveSession_JoinsAsOfThatSessionAndSaysSo()
    {
        _f.Sessions.Start(_dm, 1);
        _f.Apply(_dm, new CampaignOpSpec { Op = "fact", Statement = "The vault code is 7-3-9.", KnownBy = [Op.Knower("party")] });
        _f.Sessions.End(_dm, "We learned the code.");
        _f.Sessions.Start(_dm, 2);

        var result = _f.Apply(_dm, Op.Upsert("character", "Serif", "pc"), Op.Link("character:serif", "member_of", "faction:the-party"));

        Assert.Equal(_f.Entity(_dm, "session:2").Id, MemberOf("character:serif").SinceSessionId);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal((WarningKinds.MemberSince, WarningSeverities.Advisory, (int?)1), (warning.Kind, warning.Severity, warning.OpIndex));
        Assert.Equal("character:serif joins faction:the-party as of S2, the session of this write; pass since to change it.", warning.Message);
        Assert.Equal(["since"], result.Applied[1].ChangedFields);
        using var connection = _f.Open();
        var loader = new KnowledgeLoader(connection, _dm);
        var fact = _f.Fact(_dm, "f:1");
        var serif = KnowledgeVerdicts.Evaluate(loader.Resolve(Perspective.Parse("character:serif")), loader.EntriesForFacts([fact.Id])[fact.Id],
            fact.Visibility, loader.Attendance);
        Assert.Equal(KnowledgeStanding.Uncertain, serif.Standing);
    }

    [Fact]
    public void Link_CharacterJoiningThePartyInAWriteFiledUnderASession_JoinsAsOfIt()
    {
        _f.Played(_dm, 3);

        var result = _f.Apply(_dm, WriteContext.For(3), Op.Link("character:bjorn", "member_of", "faction:the-party"));

        Assert.Equal(_f.Entity(_dm, "session:3").Id, MemberOf("character:bjorn").SinceSessionId);
        Assert.Equal(WarningKinds.MemberSince, Assert.Single(result.Warnings).Kind);
    }

    /// <summary>
    /// The default is only for a character joining the party, now, during a session: party set-up between sessions, an
    /// explicit since, a former member, a stay already over (until with no since), another faction, and a faction (not a
    /// character: only characters have a membership that verdicts read) keep since as given, without a note.
    /// </summary>
    [Theory]
    [InlineData(false, null, null, null, "character:bjorn", "faction:the-party", null)]
    [InlineData(true, 1, null, null, "character:bjorn", "faction:the-party", 1)]
    [InlineData(true, null, "former", null, "character:bjorn", "faction:the-party", null)]
    [InlineData(true, null, null, 1, "character:bjorn", "faction:the-party", null)]
    [InlineData(true, null, null, null, "character:bjorn", "faction:the-crew", null)]
    [InlineData(true, null, null, null, "faction:the-crew", "faction:the-party", null)]
    public void Link_MemberOfThatIsNotJoiningThePartyNow_KeepsSinceAsGivenWithoutANote(bool live, int? since, string? status, int? until, string from,
        string to, int? expected)
    {
        _f.Played(_dm, 1);
        _f.Apply(_dm, Op.Upsert("faction", "The Crew"));
        if (live)
        {
            _f.Sessions.Start(_dm, 2);
        }

        var result = _f.Apply(_dm, new CampaignOpSpec
        {
            Op = "link", From = from, Rel = "member_of", To = to, Since = since, Status = status, Until = until,
        });

        Assert.Equal(expected is { } n ? _f.Entity(_dm, $"session:{n}").Id : null, MemberOf(from).SinceSessionId);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("dm", "author")]
    [InlineData("player", "party")]
    public void Link_NewRelation_TakesTheRolesDefaultVisibility(string role, string expected)
    {
        var campaign = role == "dm" ? _dm : _f.Campaign("Player", role: role, myCharacter: "Belmakor");
        _f.Apply(campaign, Op.Upsert("faction", "Mythril Zeppelin", "band"));
        var from = role == "dm" ? "character:bjorn" : "character:belmakor";

        var result = _f.Apply(campaign, Op.Link(from, "Member Of", "faction:mythril-zeppelin"));

        var applied = Assert.Single(result.Applied);
        Assert.Equal(($"{from} member_of faction:mythril-zeppelin", WriteOutcomes.Linked), (applied.Ref, applied.Outcome));
        var relation = Assert.Single(_f.Query<RelationRow>($"SELECT {RelationRow.Columns} FROM relation WHERE rel = 'member_of' AND campaign_id = @id", new { id = campaign.Id }),
            r => r.ToId == _f.Entity(campaign, "faction:mythril-zeppelin").Id);
        Assert.Equal((expected, "current", 0L), (relation.Visibility, relation.Status, relation.Symmetric));
    }

    [Fact]
    public void Link_Again_UpdatesTheOneRelation()
    {
        _f.Played(_dm, 1);
        _f.Played(_dm, 3);
        _f.Apply(_dm, new CampaignOpSpec { Op = "link", From = "character:bjorn", Rel = "ally_of", To = "character:nadar", Attitude = 20, Since = 1 });

        var result = _f.Apply(_dm, new CampaignOpSpec
        {
            Op = "link", From = "character:bjorn", Rel = "ally-of", To = "character:nadar", Attitude = -40, Status = "former", Until = 3,
            Label = "sworn", Visibility = "party", Data = Op.Data("{\"oath\": \"axe\"}"),
        });

        var applied = Assert.Single(result.Applied);
        Assert.Equal(WriteOutcomes.Updated, applied.Outcome);
        Assert.Equal(["label", "attitude", "visibility", "status", "until_session_id", "data.oath"], applied.ChangedFields);
        var relation = Assert.Single(_f.Query<RelationRow>($"SELECT {RelationRow.Columns} FROM relation WHERE rel = 'ally_of'"));
        Assert.Equal((-40L, "former", "party", "sworn"), (relation.Attitude!.Value, relation.Status, relation.Visibility, relation.Label));
        Assert.Equal(_f.Entity(_dm, "session:1").Id, relation.SinceSessionId);
        Assert.Equal(_f.Entity(_dm, "session:3").Id, relation.UntilSessionId);
    }

    [Fact]
    public void Link_Unchanged_ReportsUnchanged()
    {
        _f.Apply(_dm, Op.Link("character:bjorn", "ally_of", "character:nadar"));

        var result = _f.Apply(_dm, Op.Link("character:bjorn", "ally_of", "character:nadar"));

        Assert.Equal(WriteOutcomes.Unchanged, Assert.Single(result.Applied).Outcome);
        Assert.Empty(_f.Log(result.BatchId));
    }

    [Fact]
    public void Link_SinceASessionThatDoesNotExist_IsRefused()
    {
        var ex = Assert.Throws<DndInputException>(() =>
            _f.Apply(_dm, new CampaignOpSpec { Op = "link", From = "character:bjorn", Rel = "ally_of", To = "character:nadar", Since = 4 }));

        Assert.Contains("since: no session 4 in this campaign", ex.Message);
    }

    [Fact]
    public void Link_SameAsAnotherCampaignsEntity_StoresOneOrderedCrossLink()
    {
        var belmakor = _f.Campaign("Belmakor", role: "player");
        _f.Apply(belmakor, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "The Old King", Slug = "old-king" });
        _f.Apply(_dm, Op.Upsert("character", "Keras"));

        var result = _f.Apply(belmakor, new CampaignOpSpec
        {
            Op = "link", From = "character:old-king", Rel = "same_as", To = "one-piece/character:keras", Note = "Cole's old PC.",
        });
        var again = _f.Apply(belmakor, new CampaignOpSpec { Op = "link", From = "character:old-king", Rel = "same_as", To = "one-piece/character:keras" });

        Assert.Equal(("character:old-king same_as one-piece/character:keras", WriteOutcomes.Linked),
            (result.Applied[0].Ref, result.Applied[0].Outcome));
        Assert.Equal(WriteOutcomes.Unchanged, Assert.Single(again.Applied).Outcome);
        var link = Assert.Single(_f.Query<CrossLinkRow>($"SELECT {CrossLinkRow.Columns} FROM cross_link"));
        Assert.True(string.CompareOrdinal(link.AId, link.BId) < 0);
        Assert.Equal(
            new[] { _f.Entity(belmakor, "character:old-king").Id, _f.Entity(_dm, "character:keras").Id }.Order(StringComparer.Ordinal),
            new[] { link.AId, link.BId });
        Assert.Equal("Cole's old PC.", link.Note);
        Assert.Equal(0, _f.Count("SELECT count(*) FROM relation WHERE rel = 'same_as'"));
    }

    [Theory]
    [InlineData("zeta/character:keras", "to: no campaign zeta")]
    [InlineData("one-piece/character:bjorn", "to one-piece/character:bjorn is in this campaign; name it without the campaign prefix")]
    [InlineData("belmakor/character:nobody", "to: no entity character:nobody in campaign belmakor.")]
    public void Link_CrossCampaignThatCannotResolve_IsRefused(string to, string expected)
    {
        _f.Campaign("Belmakor", role: "player");

        var ex = Assert.Throws<DndInputException>(() =>
            _f.Apply(_dm, new CampaignOpSpec { Op = "link", From = "character:bjorn", Rel = "same_as", To = to }));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Link_LeadsToBetweenBeats_StoresABeatEdge()
    {
        _f.Apply(_dm, Op.Upsert("beat", "Dock fight"), Op.Upsert("beat", "Blood moon"));

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "link", From = "beat:dock-fight", Rel = "leads_to", To = "beat:blood-moon", Mode = "Any Of" });

        Assert.Equal(WriteOutcomes.Linked, Assert.Single(result.Applied).Outcome);
        var edge = Assert.Single(_f.Query<BeatEdgeRow>($"SELECT {BeatEdgeRow.Columns} FROM beat_edge"));
        Assert.Equal("any_of", edge.Mode);
        Assert.Equal(0, _f.Count("SELECT count(*) FROM relation WHERE rel = 'leads_to'"));
    }

    [Fact]
    public void Link_LeadsToBetweenBeatsBySeqHandle_WithoutMode_IsRefusedAfterResolving()
    {
        _f.Apply(_dm, Op.Upsert("beat", "Dock fight"), Op.Upsert("beat", "Blood moon"));
        var from = _f.Entity(_dm, "beat:dock-fight").SeqHandle;
        var to = _f.Entity(_dm, "beat:blood-moon").SeqHandle;

        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, new CampaignOpSpec { Op = "link", From = from, Rel = "leads_to", To = to }));

        Assert.Contains("mode is required with rel leads_to between two beats", ex.Message);
    }

    [Fact]
    public void Link_LeadsToBetweenNonBeats_IsARelation()
    {
        _f.Apply(_dm, Op.Link("character:bjorn", "leads_to", "character:nadar"));

        Assert.Equal(1, _f.Count("SELECT count(*) FROM relation WHERE rel = 'leads_to'"));
    }

    [Fact]
    public void Link_FromAndToTheSameEntityUnderTwoHandles_IsRefused()
    {
        var seq = _f.Entity(_dm, "character:bjorn").SeqHandle;

        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, Op.Link("character:bjorn", "ally_of", seq)));

        Assert.Contains("from and to are the same entity", ex.Message);
    }

    [Fact]
    public void Unlink_Relation_DeletesItAndLogsTheWholeRow()
    {
        _f.Apply(_dm, Op.Link("character:bjorn", "ally_of", "character:nadar"));

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "unlink", From = "character:bjorn", Rel = "ally_of", To = "character:nadar" });

        Assert.Equal(WriteOutcomes.Unlinked, Assert.Single(result.Applied).Outcome);
        Assert.Equal(0, _f.Count("SELECT count(*) FROM relation WHERE rel = 'ally_of'"));
        var row = Assert.Single(_f.Log(result.BatchId));
        Assert.Equal(("delete", "relation"), (row.Op, row.TargetTable));
        Assert.Contains("\"rel\":\"ally_of\"", row.OldValue);
    }

    [Fact]
    public void Unlink_SymmetricRelationFromTheOtherEnd_RemovesIt()
    {
        _f.Apply(_dm, new CampaignOpSpec { Op = "link", From = "character:bjorn", Rel = "ally_of", To = "character:nadar", Symmetric = true });

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "unlink", From = "character:nadar", Rel = "ally_of", To = "character:bjorn" });

        Assert.Equal(WriteOutcomes.Unlinked, Assert.Single(result.Applied).Outcome);
        Assert.Equal(0, _f.Count("SELECT count(*) FROM relation WHERE rel = 'ally_of'"));
    }

    [Fact]
    public void Link_TheMirrorOfASymmetricRelation_UpdatesItInsteadOfAddingATwin()
    {
        _f.Apply(_dm, new CampaignOpSpec { Op = "link", From = "character:bjorn", Rel = "ally_of", To = "character:nadar", Symmetric = true });

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "link", From = "character:nadar", Rel = "ally_of", To = "character:bjorn", Attitude = 60 });

        var applied = Assert.Single(result.Applied);
        Assert.Equal(("character:bjorn ally_of character:nadar", WriteOutcomes.Updated), (applied.Ref, applied.Outcome));
        Assert.Equal(60, Assert.Single(_f.Query<long>("SELECT attitude FROM relation WHERE rel = 'ally_of'")));
    }

    [Fact]
    public void Link_TheMirrorOfAnAsymmetricRelation_IsASecondRelation()
    {
        _f.Apply(_dm, Op.Link("character:bjorn", "serves", "character:nadar"));

        _f.Apply(_dm, Op.Link("character:nadar", "serves", "character:bjorn"));

        Assert.Equal(2, _f.Count("SELECT count(*) FROM relation WHERE rel = 'serves'"));
    }

    [Fact]
    public void Unlink_AsymmetricRelationFromTheOtherEnd_IsNothingToDo()
    {
        _f.Apply(_dm, Op.Link("character:bjorn", "serves", "character:nadar"));

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "unlink", From = "character:nadar", Rel = "serves", To = "character:bjorn" });

        Assert.Equal(WriteOutcomes.Unchanged, Assert.Single(result.Applied).Outcome);
        Assert.Equal(1, _f.Count("SELECT count(*) FROM relation WHERE rel = 'serves'"));
    }

    [Fact]
    public void Unlink_Missing_WarnsAndChangesNothing()
    {
        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "unlink", From = "character:bjorn", Rel = "enemy_of", To = "character:nadar" });

        Assert.Equal(WriteOutcomes.Unchanged, Assert.Single(result.Applied).Outcome);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal(WarningKinds.Nothing, warning.Kind);
        Assert.Contains("there is no character:bjorn enemy_of character:nadar link", warning.Message);
        Assert.Empty(_f.Log(result.BatchId));
    }

    [Fact]
    public void Unlink_CrossLinkAndBeatEdge_AreRemoved()
    {
        var other = _f.Campaign("Belmakor", role: "player");
        _f.Apply(other, Op.Upsert("character", "The Old King"));
        _f.Apply(_dm, Op.Upsert("beat", "A"), Op.Upsert("beat", "B"),
            new CampaignOpSpec { Op = "link", From = "beat:a", Rel = "leads_to", To = "beat:b", Mode = "all_of" },
            Op.Link("character:bjorn", "same_as", "belmakor/character:the-old-king"));

        var result = _f.Apply(_dm,
            new CampaignOpSpec { Op = "unlink", From = "beat:a", Rel = "leads_to", To = "beat:b" },
            new CampaignOpSpec { Op = "unlink", From = "character:bjorn", Rel = "same_as", To = "belmakor/character:the-old-king" });

        Assert.All(result.Applied, a => Assert.Equal(WriteOutcomes.Unlinked, a.Outcome));
        Assert.Equal(0, _f.Count("SELECT count(*) FROM beat_edge"));
        Assert.Equal(0, _f.Count("SELECT count(*) FROM cross_link"));
    }
}
