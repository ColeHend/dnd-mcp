using DndMcp.Domain.Campaign.Ops;
using DndMcp.Repository.Campaign.Characters;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tests.CampaignScenarios;
using DndMcp.Tests.CampaignWrite;
using Xunit;

namespace DndMcp.Tests.CampaignCharacters;

/// <summary>
/// <see cref="PartyRoster"/> (contract D8): who is in the party now, the dead and departed named apart, every other way of
/// not being a member (left, former, deleted, not a character) simply absent, and the party as of a session.
/// </summary>
public sealed class PartyRosterTests
{
    [Fact]
    public void Read_BelmakorWorld_IsTheSixLivingMembersByNameAndTristanExcluded()
    {
        using var world = new BelmakorScenario();
        FixtureSheets.Belmakor(world);

        var roster = PartyRoster.Read(world.F.Db.Database, world.Campaign);

        Assert.Equal(["Aiden Ironstar", "Belmakor Silverwind", "Ignis", "Lieutenant James Torch", "Serif", "Vars Nocturne"], roster.Members.Select(m => m.Name));
        Assert.Equal("character:tristan", Assert.Single(roster.Excluded).Handle);
        Assert.Equal("dead", roster.Excluded[0].Reason);
        Assert.All(roster.Members, m => Assert.NotNull(m.Sheet));
        Assert.Equal(12, roster.Members.Single(m => m.Handle == "character:belmakor").Level);
        Assert.Empty(roster.WithoutLevel);
    }

    [Fact]
    public void Read_MembersWithoutSheets_AreListedWithoutLevel()
    {
        using var world = SheetWorld.Dm();
        world.Update("character:hero", """{ "level": 4 }""");

        var roster = PartyRoster.Read(world.Database, world.Campaign);

        Assert.Equal(["character:hero", "character:sidekick"], roster.Members.Select(m => m.Handle));
        Assert.Equal("character:sidekick", Assert.Single(roster.WithoutLevel).Handle);
        Assert.Equal("character:fallen", Assert.Single(roster.Excluded).Handle);
        Assert.True(roster.Contains(world.Id("character:hero")));
        Assert.False(roster.Contains(world.Id("character:villain")));
    }

    [Theory]
    [InlineData("former", null, false, false)]
    [InlineData("current", "until", false, false)]
    [InlineData("current", null, true, false)]
    [InlineData("rumored", null, false, false)]
    [InlineData("current", "departed", false, true)]
    [InlineData("current", "deleted", false, false)]
    public void Read_HowTheLinkStands_DecidesMembership(string status, string? extra, bool member, bool excluded)
    {
        using var world = SheetWorld.Dm();
        world.F.Played(world.Campaign, 1);
        world.F.Apply(world.Campaign,
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Newcomer", Subtype = "pc", Visibility = "party", Status = extra == "departed" ? "departed" : null },
            new CampaignOpSpec { Op = "link", From = "character:newcomer", Rel = "member_of", To = "faction:the-party", Status = status, Until = extra == "until" ? 1 : null });
        if (extra == "deleted")
        {
            world.F.Apply(world.Campaign, new CampaignOpSpec { Op = "delete", Ref = "character:newcomer" });
        }

        var roster = PartyRoster.Read(world.Database, world.Campaign);

        Assert.Equal(member, roster.Members.Any(m => m.Handle == "character:newcomer"));
        Assert.Equal(excluded, roster.Excluded.Any(m => m.Handle == "character:newcomer"));
    }

    [Fact]
    public void Read_AsOfASessionBeforeTheMemberJoined_LeavesItOut()
    {
        using var world = SheetWorld.Dm();
        world.F.Played(world.Campaign, 1);
        world.F.Played(world.Campaign, 2);
        world.F.Apply(world.Campaign, WriteContext.For(2),
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Newcomer", Subtype = "pc", Visibility = "party" },
            Op.Link("character:newcomer", "member_of", "faction:the-party"));
        world.Update("character:newcomer", """{ "level": 2 }""", WriteContext.For(2));

        using var connection = world.Open();
        var asOf1 = PartyRoster.Read(connection, world.Campaign, 1);
        var asOf2 = PartyRoster.Read(connection, world.Campaign, 2);

        Assert.DoesNotContain(asOf1.Members, m => m.Handle == "character:newcomer");
        Assert.Equal(2, asOf2.Members.Single(m => m.Handle == "character:newcomer").Level);
    }

    [Fact]
    public void Read_AsOfTheSessionAMemberLeftIn_LeavesItOut()
    {
        // A member_of whose until is session 2: a member at the end of session 1, not at the end of session 2, nor now.
        using var world = SheetWorld.Dm();
        world.F.Played(world.Campaign, 1);
        world.F.Played(world.Campaign, 2);
        world.F.Apply(world.Campaign,
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Leaver", Subtype = "pc", Visibility = "party" },
            new CampaignOpSpec { Op = "link", From = "character:leaver", Rel = "member_of", To = "faction:the-party", Status = "current", Until = 2 });

        using var connection = world.Open();

        Assert.Contains(PartyRoster.Read(connection, world.Campaign, 1).Members, m => m.Handle == "character:leaver");
        Assert.DoesNotContain(PartyRoster.Read(connection, world.Campaign, 2).Members, m => m.Handle == "character:leaver");
        Assert.DoesNotContain(PartyRoster.Read(connection, world.Campaign).Members, m => m.Handle == "character:leaver");
    }

    /// <summary>
    /// L05: the current roster is the as-of window at NOW (the live session, else the last played one): a member whose
    /// since session has not come yet is not a member (for the roster, the non-author list form and add_party), and one
    /// whose until session has not come yet still is; once that session is live, both turn.
    /// </summary>
    [Theory]
    [InlineData(3, null, false, true)]
    [InlineData(null, 3, true, false)]
    [InlineData(2, null, true, true)]
    [InlineData(null, 2, false, false)]
    public void Read_ASinceOrUntilSessionStillToCome_IsJudgedAtTheLiveOrLastPlayedSession(int? since, int? until, bool memberNow, bool memberOnceItIsLive)
    {
        using var world = SheetWorld.Dm();
        world.F.Played(world.Campaign, 1);
        world.F.Played(world.Campaign, 2);
        world.F.Sessions.Plan(world.Campaign, 3);
        world.F.Apply(world.Campaign,
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Newcomer", Subtype = "npc", Visibility = "party" },
            new CampaignOpSpec { Op = "link", From = "character:newcomer", Rel = "member_of", To = "faction:the-party", Status = "current", Since = since, Until = until });
        world.Update("character:newcomer", """{ "classes": [{ "class": "wizard", "level": 17 }], "max_hp": 90, "ac": 12 }""");
        bool Member() => PartyRoster.Read(world.Database, world.Campaign).Members.Any(m => m.Handle == "character:newcomer");
        bool Listed() => world.Reader.Party(world.Campaign, DndMcp.Domain.Campaign.Perspective.Parse("party")).Characters.Any(c => c.Name == "Newcomer");

        var now = (Member(), Listed());
        world.F.Sessions.Start(world.Campaign, 3);
        world.Reload();
        var live = (Member(), Listed());

        Assert.Equal((memberNow, memberNow), now);
        Assert.Equal((memberOnceItIsLive, memberOnceItIsLive), live);
    }

    [Fact]
    public void Read_AMemberPlannedToJoinLater_IsNotSeatedByAddParty()
    {
        using var world = DndMcp.Tests.CampaignCombat.CombatWorld.Dm();
        world.F.Played(world.Campaign, 1);
        world.F.Sessions.Plan(world.Campaign, 2);
        world.Apply(
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Arch mage", Subtype = "npc", Visibility = "party" },
            new CampaignOpSpec { Op = "link", From = "character:arch-mage", Rel = "member_of", To = "faction:the-party", Since = 2 });
        world.Sheet("character:arch-mage", """{ "classes": [{ "class": "wizard", "level": 17 }], "max_hp": 90, "ac": 12 }""");

        world.Combat.Start(world.Campaign, new DndMcp.Repository.Campaign.Combat.StartRequest { Name = "Tonight" });

        Assert.Equal(["Hero Prime", "Sidekick"], world.State("Tonight").Combatants.Select(c => c.Name));
    }

    /// <summary>
    /// LR02 (F2, amending L05): before any session is played or live, "now" is session 1. Members linked since 1 while the
    /// first session is still planned are the party being prepared for: the roster (and so encounter_difficulty's
    /// party: "campaign"), the non-author list form and add_party all have them, where L05's rule told the author to link
    /// characters who already were. A since 2 still waits for session 2.
    /// </summary>
    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void Read_NoSessionPlayedYet_NowIsTheFirstSession(int since, bool member)
    {
        using var world = DndMcp.Tests.CampaignCombat.CombatWorld.Dm();
        world.F.Sessions.Plan(world.Campaign, 1);
        world.F.Sessions.Plan(world.Campaign, 2);
        world.Apply(
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Ana", Subtype = "pc", Visibility = "party" },
            new CampaignOpSpec { Op = "link", From = "character:ana", Rel = "member_of", To = "faction:the-party", Since = since });
        world.Sheet("character:ana", """{ "classes": [{ "class": "fighter", "level": 3 }], "max_hp": 28, "ac": 16 }""");

        var roster = PartyRoster.Read(world.Database, world.Campaign).Members.Any(m => m.Handle == "character:ana");
        var listed = new SheetReader(world.Database).Party(world.Campaign, DndMcp.Domain.Campaign.Perspective.Parse("party")).Characters.Any(c => c.Name == "Ana");
        world.Combat.Start(world.Campaign, new DndMcp.Repository.Campaign.Combat.StartRequest { Name = "Prep" });
        var seated = world.State("Prep").Combatants.Any(c => c.Name == "Ana");

        Assert.Equal((member, member, member), (roster, listed, seated));
    }

    [Fact]
    public void Read_PartyRef_IsThePartyFactionsAuthorHandle()
    {
        using var dm = SheetWorld.Dm();
        using var belmakor = new BelmakorScenario();

        Assert.Equal("faction:the-party", PartyRoster.Read(dm.Database, dm.Campaign).PartyRef);
        Assert.Equal("faction:party", PartyRoster.Read(belmakor.F.Db.Database, belmakor.Campaign).PartyRef);
        var none = PartyRoster.Read(dm.Database, dm.Campaign with { PartyId = null });
        Assert.Null(none.PartyRef);
        Assert.Empty(none.Members);
    }

    [Fact]
    public void Read_DmCampaignFixture_IsTheThreePcsWithSheets()
    {
        using var world = OnePieceScenario.Build();
        FixtureSheets.OnePiece(world);

        var roster = PartyRoster.Read(world.F.Db.Database, world.Campaign);

        Assert.Equal(["character:bjorn-mountainfell", "character:dragon-slayer", "character:fishman-monk"], roster.Members.Select(m => m.Handle));
        Assert.Equal([8, 8, 8], roster.Members.Select(m => m.Level ?? 0));
        Assert.DoesNotContain(roster.Members, m => m.Handle == "character:the-nester");
        Assert.Empty(roster.Excluded);
    }
}
