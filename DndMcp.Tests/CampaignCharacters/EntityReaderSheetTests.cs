using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign.Characters;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tests.CampaignWrite;
using Xunit;

namespace DndMcp.Tests.CampaignCharacters;

/// <summary>
/// <c>campaign_get include: ["sheet"]</c> (contract §13.1): the include parses, the author's detail carries the author view,
/// another view's the public line of a current member it is shown, and every other entity none; as of a session, the
/// membership and the sheet of that session.
/// </summary>
public sealed class EntityReaderSheetTests
{
    private static GetResult Get(SheetWorld world, string perspective, EntityIncludes includes, int? asOf, params string[] refs) =>
        new EntityReader(world.Database).Get(world.Campaign, refs, includes, Perspective.Parse(perspective), asOf);

    private static readonly EntityIncludes WithSheet = new(Sheet: true);

    [Fact]
    public void Parse_Sheet_IsTheLastIncludeAndListedInTheRefusal()
    {
        Assert.True(EntityIncludes.Parse(["Sheet"]).Sheet);
        Assert.False(EntityIncludes.Default.Sheet);
        Assert.True(EntityIncludes.All.Sheet);
        var ex = Assert.Throws<DndInputException>(() => EntityIncludes.Parse(["bogus"]));
        Assert.Contains("use relations, facts, knowledge, children, sessions, history, sheet.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Get_AuthorWithSheet_CarriesTheAuthorViewOnly()
    {
        using var world = SheetWorld.Dm();
        world.Update("character:hero", """{ "level": 3, "max_hp": 24, "player": "Sam" }""");

        var detail = Get(world, "author", WithSheet, null, "character:hero", "location:the-harbor").Entities;

        Assert.Equal("Sam", detail[0].Sheet!.Author!.Sheet.Player);
        Assert.Null(detail[0].Sheet!.Line);
        Assert.Null(detail[1].Sheet);
    }

    [Fact]
    public void Get_WithoutTheInclude_HasNoSheet()
    {
        using var world = SheetWorld.Dm();
        world.Update("character:hero", """{ "level": 3 }""");

        Assert.Null(Get(world, "author", EntityIncludes.Default, null, "character:hero").Entities.Single().Sheet);
    }

    [Fact]
    public void Get_PartyWithSheet_IsTheLineOfAMemberAndNoneForAnyoneElse()
    {
        using var world = SheetWorld.Dm();
        world.F.Apply(world.Campaign, new CampaignOpSpec { Op = "upsert", Ref = "character:villain", Visibility = "party" });
        world.Update("character:hero", """{ "level": 3, "max_hp": 24, "player": "Sam" }""");
        world.Update("character:villain", """{ "level": 9, "max_hp": 90 }""");

        var details = Get(world, "party", WithSheet, null, world.F.Entity(world.Campaign, "character:hero").SeqHandle, "character:villain", "character:sidekick").Entities;

        Assert.Equal(24, details[0].Sheet!.Line!.Hp);
        Assert.Null(details[0].Sheet!.Author);
        Assert.Null(details[1].Sheet);
        Assert.Null(details[2].Sheet);
        DndMcp.Tests.CampaignRead.LeakAssert.Clean(details, ["Sam", "90"], "the party reads the members' lines only");
    }

    [Fact]
    public void Get_DisguisedMember_HasNoSheetForThatView()
    {
        using var world = SheetWorld.Dm();
        world.Update("character:hero", """{ "level": 3, "max_hp": 24 }""");
        world.F.Knowledge.Record(world.Campaign, ["character:hero"], [new KnowerSpec { Who = "party", State = "met", KnownAs = "the masked one" }], WriteContext.Default);
        var seq = world.F.Entity(world.Campaign, "character:hero").SeqHandle;

        var detail = Get(world, "party", WithSheet, null, seq).Entities.Single();

        Assert.Equal("the masked one", detail.DisplayName);
        Assert.Null(detail.Sheet);
    }

    [Fact]
    public void Get_AsOfASessionBeforeTheSheetOrTheMembership_ReadsThen()
    {
        using var world = SheetWorld.Dm();
        world.F.Played(world.Campaign, 1);
        world.F.Played(world.Campaign, 2);
        world.F.Apply(world.Campaign, WriteContext.For(2),
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Newcomer", Subtype = "pc", Visibility = "party" },
            Op.Link("character:newcomer", "member_of", "faction:the-party"));
        world.Update("character:hero", """{ "level": 3, "max_hp": 24 }""", WriteContext.For(1));
        world.Update("character:newcomer", """{ "level": 2, "max_hp": 12 }""", WriteContext.For(2));
        world.Writer.Damage(world.Campaign, "character:hero", 4, null, WriteContext.For(2));

        var asOf1 = Get(world, "author", WithSheet, 1, "character:hero").Entities.Single();
        var hero = world.F.Entity(world.Campaign, "character:hero").SeqHandle;
        var party1 = Get(world, "party", WithSheet, 1, hero).Entities.Single();
        var party2 = Get(world, "party", WithSheet, 2, hero, "character:newcomer").Entities;

        Assert.Equal(24, asOf1.Sheet!.Author!.Sheet.Hp);
        Assert.Equal(24, party1.Sheet!.Line!.Hp);
        Assert.Equal(20, party2[0].Sheet!.Line!.Hp);
        Assert.Equal(12, party2[1].Sheet!.Line!.Hp);
    }

    [Fact]
    public void Get_AsOfTheSessionAMemberLeftIn_ReadsNoLineThenOrNow()
    {
        // §7.4: "With as_of_session: membership, visibility and the sheet as of that session." The member left in session 2
        // (its member_of has until 2); its sheet is timeless.
        using var world = SheetWorld.Dm();
        world.F.Played(world.Campaign, 1);
        world.F.Played(world.Campaign, 2);
        world.F.Apply(world.Campaign,
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Leaver", Subtype = "pc", Visibility = "party" },
            new CampaignOpSpec { Op = "link", From = "character:leaver", Rel = "member_of", To = "faction:the-party", Status = "current", Until = 2 });
        world.Update("character:leaver", """{ "level": 4, "max_hp": 30 }""");
        var leaver = world.F.Entity(world.Campaign, "character:leaver").SeqHandle;

        var asOf1 = Get(world, "party", WithSheet, 1, leaver).Entities.Single();
        var asOf2 = Get(world, "party", WithSheet, 2, leaver).Entities.Single();
        var now = Get(world, "party", WithSheet, null, leaver).Entities.Single();

        Assert.Equal(30, asOf1.Sheet!.Line!.Hp);
        Assert.Null(asOf2.Sheet);
        Assert.Null(now.Sheet);
    }

    [Fact]
    public void Get_AuthorAsOfASession_ShowsThatSessionsCoinsAndHoldings()
    {
        using var world = SheetWorld.Dm();
        world.F.Played(world.Campaign, 1);
        world.F.Played(world.Campaign, 2);
        world.Update("character:hero", """{ "level": 3 }""");
        world.Writer.Currency(world.Campaign, "character:hero", new Coins(Gp: 10), WriteContext.For(1));
        world.Writer.Currency(world.Campaign, "character:hero", new Coins(Gp: 5, Sp: -3), WriteContext.For(2));

        var asOf1 = Get(world, "author", WithSheet, 1, "character:hero").Entities.Single().Sheet!.Author!;
        var now = Get(world, "author", WithSheet, null, "character:hero").Entities.Single().Sheet!.Author!;

        Assert.Equal(new CoinsView(0, 0, 0, 10, 0), asOf1.Coins);
        Assert.Equal(new CoinsView(0, -3, 0, 15, 0), now.Coins);
    }
}
