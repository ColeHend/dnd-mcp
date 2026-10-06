using DndMcp.Domain.Campaign.Ops;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tests.CampaignScenarios;
using Xunit;

namespace DndMcp.Tests.CampaignCharacters;

/// <summary>
/// <see cref="SessionSafeNames"/> (contract §6.10): the name a dice label may carry is one every reader of the session's
/// dice may be shown. Entity-linked subjects keep the party's name only when nobody reads them disguised; stat-block
/// subjects are their monster's name whatever was typed; custom party or ally names must pass the party's view-text check
/// with no possible inventions.
/// </summary>
public sealed class SessionSafeNamesTests
{
    private static IReadOnlyDictionary<string, string> Names(BelmakorScenario world, params SafeNameSubject[] subjects)
    {
        using var connection = world.F.Open();
        return SessionSafeNames.For(connection, world.Campaign, subjects);
    }

    [Fact]
    public void For_BelmakorSubjects_AreNamedAsEveryReaderMayBeShown()
    {
        using var world = new BelmakorScenario();
        string Id(string handle) => world.F.Entity(world.Campaign, handle).Id;

        var names = Names(world,
            new SafeNameSubject("king", EntityId: Id("character:old-king"), MonsterName: "Lich"),
            new SafeNameSubject("torch", EntityId: Id("character:torch")),
            new SafeNameSubject("thing", EntityId: Id("item:thing-he-wants"), MonsterName: "Mimic"),
            new SafeNameSubject("thing-bare", EntityId: Id("item:thing-he-wants")),
            new SafeNameSubject("typed", MonsterName: "Lich", Name: "Keras", Side: "enemy"),
            new SafeNameSubject("ally", Name: "Torch", Side: "ally"),
            new SafeNameSubject("ally-keras", Name: "Keras", Side: "ally"),
            new SafeNameSubject("ally-new", Name: "Wraith Blade", Side: "party"),
            new SafeNameSubject("enemy", Name: "Torch", Side: "enemy"),
            new SafeNameSubject("nothing"));

        Assert.Equal("The Old King", names["king"]);
        Assert.Equal("Lieutenant James Torch", names["torch"]);
        Assert.Equal("Mimic", names["thing"]);
        Assert.Equal(SessionSafeNames.CombatantFallback, names["thing-bare"]);
        Assert.Equal("Lich", names["typed"]);
        Assert.Equal("Torch", names["ally"]);
        Assert.Equal(SessionSafeNames.CombatantFallback, names["ally-keras"]);
        Assert.Equal(SessionSafeNames.CombatantFallback, names["ally-new"]);
        Assert.Equal(SessionSafeNames.CombatantFallback, names["enemy"]);
        Assert.Equal(SessionSafeNames.CombatantFallback, names["nothing"]);
    }

    [Theory]
    [InlineData("character:vars")]
    [InlineData("dm")]
    [InlineData("table")]
    public void For_AReaderWhoKnowsThemDisguised_ForcesTheFallback(string reader)
    {
        using var world = new BelmakorScenario();
        world.F.Knowledge.Record(world.Campaign, ["character:ignis"], [new KnowerSpec { Who = reader, State = "met", KnownAs = "the masked bard" }], WriteContext.Default);
        var ignis = world.F.Entity(world.Campaign, "character:ignis").Id;

        var names = Names(world, new SafeNameSubject("ignis", EntityId: ignis), new SafeNameSubject("ignis-block", EntityId: ignis, MonsterName: "Bard"));

        Assert.Equal(SessionSafeNames.CombatantFallback, names["ignis"]);
        Assert.Equal("Bard", names["ignis-block"]);
    }

    [Fact]
    public void For_AReaderWhoCannotSeeTheEntity_DoesNotForceTheFallback()
    {
        using var world = new BelmakorScenario();
        world.F.Knowledge.Record(world.Campaign, ["character:ignis"], [new KnowerSpec { Who = "character:vars", State = "unaware" }], WriteContext.Default);

        var names = Names(world, new SafeNameSubject("ignis", EntityId: world.F.Entity(world.Campaign, "character:ignis").Id));

        Assert.Equal("Ignis", names["ignis"]);
    }

    [Fact]
    public void For_OnePieceEntitiesThePartyDoesNotKnowOrKnowsDisguised_FallBack()
    {
        using var world = OnePieceScenario.Build();
        FixtureSheets.OnePiece(world);
        using var connection = world.F.Open();
        string Id(string handle) => world.F.Entity(world.Campaign, handle).Id;

        var names = SessionSafeNames.For(connection, world.Campaign,
        [
            new SafeNameSubject("nester", EntityId: Id("character:the-nester"), MonsterName: "Aboleth"),
            new SafeNameSubject("protector", EntityId: Id("character:protector")),
            new SafeNameSubject("bjorn", EntityId: Id("character:bjorn-mountainfell")),
            new SafeNameSubject("custom", Name: "sealed guardian", Side: "ally"),
        ]);

        Assert.Equal("Aboleth", names["nester"]);
        Assert.Equal(SessionSafeNames.CombatantFallback, names["protector"]);
        Assert.Equal("Björn Mountainfell", names["bjorn"]);
        Assert.Equal(SessionSafeNames.CombatantFallback, names["custom"]);
        Assert.Equal("a character", SessionSafeNames.ForCharacter(connection, world.Campaign, Id("character:the-nester")));
        Assert.Equal("The fishman monk", SessionSafeNames.ForCharacter(connection, world.Campaign, Id("character:fishman-monk")));
    }

    [Theory]
    [InlineData("keras", null)]
    [InlineData("Keras", null)]
    [InlineData("third silence", null)]
    [InlineData("cage guard", null)]
    [InlineData("fleet captain", null)]
    [InlineData("the seal warden", null)]
    [InlineData("the protector", null)]
    [InlineData("void-touched thing", null)]
    [InlineData("goblin boss", null)]
    [InlineData("baalite cultist", null)]
    [InlineData("nesterling", null)]
    [InlineData("sealbreaker", null)]
    [InlineData("the advisor in Serret", "the advisor in Serret")]
    [InlineData("Björn", "Björn")]
    [InlineData("björn", "björn")]
    public void For_OnePieceCustomNamesInAnyCase_KeepOnlyNamesThePartyUses(string typed, string? kept)
    {
        // The stock fixture B (with FixtureSheets): in lower case the check lists no possible invention and no partial name,
        // so each name is also checked capitalised (§6.10: a custom label name needs no possible inventions).
        using var world = OnePieceScenario.Build();
        FixtureSheets.OnePiece(world);
        using var connection = world.F.Open();

        var names = SessionSafeNames.For(connection, world.Campaign, [new SafeNameSubject("ally", Name: typed, Side: "ally")]);

        Assert.Equal(kept ?? SessionSafeNames.CombatantFallback, names["ally"]);
    }

    [Theory]
    [InlineData("keras", null)]
    [InlineData("third silence", null)]
    [InlineData("cage guard", null)]
    [InlineData("axiom", null)]
    [InlineData("reclaim", null)]
    [InlineData("kerasian sentinel", null)]
    [InlineData("torch", "torch")]
    [InlineData("the old king", "the old king")]
    public void For_BelmakorCustomNamesInAnyCase_KeepOnlyNamesThePartyUses(string typed, string? kept)
    {
        using var world = new BelmakorScenario();

        Assert.Equal(kept ?? SessionSafeNames.CombatantFallback, Names(world, new SafeNameSubject("ally", Name: typed, Side: "party"))["ally"]);
    }

    [Theory]
    [InlineData("keras", "Keras")]
    [InlineData("third silence", "Third Silence")]
    [InlineData("bjorn's axe", "Bjorn's Axe")]
    [InlineData("the old-king", "The Old-King")]
    [InlineData("mummy 2", "Mummy 2")]
    [InlineData("ALREADY Up", "ALREADY Up")]
    public void Capitalised_EveryWord_StartsUpperCaseAndPossessivesStayOneWord(string text, string expected)
    {
        Assert.Equal(expected, SessionSafeNames.Capitalised(text));
    }

    [Fact]
    public void For_AFormerMemberWhoKnowsThemDisguised_ForcesTheFallback()
    {
        // A member who left still reads the sessions it was in, so its disguise of another member keeps the true name off
        // the labels too (deviation 4).
        using var world = SheetWorld.Dm();
        world.F.Apply(world.Campaign,
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Exile", Subtype = "pc", Visibility = "party" },
            new CampaignOpSpec { Op = "link", From = "character:exile", Rel = "member_of", To = "faction:the-party", Status = "former" });
        var hero = world.Id("character:hero");
        using (var before = world.Open())
        {
            Assert.Equal("Hero Prime", SessionSafeNames.For(before, world.Campaign, [new SafeNameSubject("hero", EntityId: hero)])["hero"]);
        }

        world.F.Knowledge.Record(world.Campaign, ["character:hero"], [new KnowerSpec { Who = "character:exile", State = "met", KnownAs = "the masked one" }], WriteContext.Default);
        using var connection = world.Open();

        Assert.Equal(SessionSafeNames.CombatantFallback, SessionSafeNames.For(connection, world.Campaign, [new SafeNameSubject("hero", EntityId: hero)])["hero"]);
        Assert.Equal(SessionSafeNames.CharacterFallback, SessionSafeNames.ForCharacter(connection, world.Campaign, hero));
    }

    [Fact]
    public void For_NoSubjects_IsEmpty()
    {
        using var world = new BelmakorScenario();

        Assert.Empty(Names(world));
    }

    [Fact]
    public void For_DeletedEntity_FallsBack()
    {
        using var world = new BelmakorScenario();
        var bram = world.F.Apply(world.Campaign, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Bram", Visibility = "party" });
        var id = world.F.Entity(world.Campaign, "character:bram").Id;
        world.F.Apply(world.Campaign, new CampaignOpSpec { Op = "delete", Ref = "character:bram" });

        Assert.NotNull(bram);
        Assert.Equal("Ogre", Names(world, new SafeNameSubject("bram", EntityId: id, MonsterName: "Ogre"))["bram"]);
    }
}
