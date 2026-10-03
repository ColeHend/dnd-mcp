using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.Tests.CampaignDb;

/// <summary>
/// Invariant: every handle form names the row contract §3.1 says it does, in the right campaign, and nothing else: a
/// kind that does not match is "not found" rather than another entity, soft-deleted rows appear only when asked for,
/// names compare forgivingly (case, diacritics, apostrophes, a leading article) and ambiguity is refused rather than
/// guessed. A handle that resolves to the wrong row writes to the wrong place; suggestions and errors name only what the
/// caller says the reader may see.
/// </summary>
public sealed class HandleResolverTests : IDisposable
{
    private readonly CampaignTestDb _db = new();
    private readonly SqliteConnection _connection;
    private readonly SeededCampaign _campaign;
    private readonly SeededCampaign _other;
    private readonly SeededEntity _keras;
    private readonly SeededEntity _oldKing;
    private readonly SeededEntity _bjorn;
    private readonly SeededEntity _deleted;
    private readonly SeededEntity _codeQuestion;
    private readonly SeededFact _fact;
    private readonly SeededFact _codedFact;
    private readonly SeededSession _s1;
    private readonly SeededSession _s2;
    private readonly SeededSession _live;
    private readonly HandleResolver _resolver;

    public HandleResolverTests()
    {
        _connection = _db.Open();
        var seed = new CampaignSeed(_connection);
        _campaign = seed.Campaign(name: "Belmakor", slug: "belmakor");
        _other = seed.Campaign(name: "One Piece", slug: "one-piece");
        _keras = seed.Entity(_other.Id, CampaignValues.Kinds.Character, "Keras", visibility: CampaignValues.Visibilities.Author);
        _oldKing = seed.Entity(_campaign.Id, CampaignValues.Kinds.Character, "The Old King");
        _bjorn = seed.Entity(_campaign.Id, CampaignValues.Kinds.Character, "Björn Mountainfell");
        seed.Entity(_campaign.Id, CampaignValues.Kinds.Location, "Serret");
        _deleted = seed.Entity(_campaign.Id, CampaignValues.Kinds.Character, "Tristan", deletedAt: "2026-09-01T12:00:00.000Z");
        _codeQuestion = seed.Entity(_campaign.Id, CampaignValues.Kinds.Question, "Who sealed it?", code: "Q22");
        seed.Entity(_campaign.Id, CampaignValues.Kinds.Note, "B12", slug: "b12");
        _fact = seed.Fact(_campaign.Id, "The old king sleeps.");
        _codedFact = seed.Fact(_campaign.Id, "The seal holds.", code: "F36");
        _s1 = seed.Session(_campaign.Id, 1);
        _s2 = seed.Session(_campaign.Id, 2);
        seed.Session(_campaign.Id, 3, status: CampaignValues.SessionStatuses.Planned);
        _live = seed.Session(_campaign.Id, 4, status: CampaignValues.SessionStatuses.Live);
        _resolver = new HandleResolver(_connection, _campaign.Id);
    }

    public void Dispose()
    {
        _connection.Dispose();
        _db.Dispose();
    }

    [Theory]
    [InlineData("character:the-old-king", "The Old King")]
    [InlineData("the-old-king", "The Old King")]
    [InlineData("Character:The-Old-King", "The Old King")]
    [InlineData("The Old King", "The Old King")]
    [InlineData("location:serret", "Serret")]
    [InlineData("Q22", "Who sealed it?")]
    [InlineData("q22", "Who sealed it?")]
    [InlineData("b12", "B12")]
    [InlineData("session:2", "Session 2")]
    [InlineData("session:live", "Session 4")]
    [InlineData("session:last", "Session 2")]
    [InlineData("session:session-1", "Session 1")]
    [InlineData("character:bjorn-mountainfell", "Björn Mountainfell")]
    public void TryEntity_EveryHandleForm_FindsTheEntity(string handle, string name)
    {
        var entity = _resolver.TryEntity(CampaignHandle.Parse(handle));

        Assert.Equal(name, entity?.Name);
    }

    [Fact]
    public void TryEntity_BySeq_FindsTheEntity() =>
        Assert.Equal(_bjorn.Id, _resolver.TryEntity(CampaignHandle.Parse(_bjorn.SeqHandle))?.Id);

    /// <summary>A kind that does not match is "not found", never the other entity: a guess must not write to the wrong row.</summary>
    [Theory]
    [InlineData("location:the-old-king")]
    [InlineData("character:serret")]
    [InlineData("nobody")]
    [InlineData("character:keras")]
    [InlineData("f:1")]
    [InlineData("session:9")]
    [InlineData("e:9999")]
    [InlineData("Z99")]
    public void TryEntity_NoSuchEntityInThisCampaign_IsNull(string handle) =>
        Assert.Null(_resolver.TryEntity(CampaignHandle.Parse(handle)));

    /// <summary>Soft-deleted rows keep their handle (restore needs them) but are found only when asked for.</summary>
    [Fact]
    public void TryEntity_SoftDeleted_IsFoundOnlyWithIncludeDeleted()
    {
        Assert.Null(_resolver.TryEntity(CampaignHandle.Parse("character:tristan")));
        Assert.Equal(_deleted.Id, _resolver.TryEntity(CampaignHandle.Parse("character:tristan"), includeDeleted: true)?.Id);
    }

    [Fact]
    public void TryEntity_CrossCampaignHandle_ResolvesInTheNamedCampaign()
    {
        Assert.Equal(_keras.Id, _resolver.TryEntity(CampaignHandle.Parse("one-piece/character:keras"))?.Id);
        Assert.Null(_resolver.TryEntity(CampaignHandle.Parse("no-such-campaign/character:keras")));
    }

    [Theory]
    [InlineData("F36", true)]
    [InlineData("f36", true)]
    [InlineData("Q22", false)]
    [InlineData("character:serret", false)]
    public void TryFact_CodeOrSeq_FindsOnlyFacts(string handle, bool found)
    {
        Assert.Equal(found ? _codedFact.Id : null, _resolver.TryFact(CampaignHandle.Parse(handle))?.Id);
        Assert.Equal(_fact.Id, _resolver.TryFact(CampaignHandle.Parse(_fact.SeqHandle))?.Id);
    }

    [Fact]
    public void TryEntityOrFact_EachForm_PicksTheRightTable()
    {
        Assert.Equal((_codeQuestion.Id, (string?)null), Ids(_resolver.TryEntityOrFact(CampaignHandle.Parse("Q22"))));
        Assert.Equal(((string?)null, _codedFact.Id), Ids(_resolver.TryEntityOrFact(CampaignHandle.Parse("F36"))));
        Assert.Equal(((string?)null, _fact.Id), Ids(_resolver.TryEntityOrFact(CampaignHandle.Parse(_fact.SeqHandle))));
        Assert.Equal((_oldKing.Id, (string?)null), Ids(_resolver.TryEntityOrFact(CampaignHandle.Parse("the-old-king"))));
        Assert.Equal(((string?)null, (string?)null), Ids(_resolver.TryEntityOrFact(CampaignHandle.Parse("Z9"))));
    }

    /// <summary>A code on both an entity and a fact is refused rather than guessed, naming only e:/f: handles.</summary>
    [Fact]
    public void TryEntityOrFact_CodeOnBothAnEntityAndAFact_IsRefusedNamingBothHandles()
    {
        var seed = new CampaignSeed(_connection);
        var entity = seed.Entity(_campaign.Id, CampaignValues.Kinds.Secret, "The Axiom Cage", code: "F7", visibility: CampaignValues.Visibilities.Author);
        var fact = seed.Fact(_campaign.Id, "The cage holds Keras.", code: "F7");

        var error = Assert.Throws<DndInputException>(() => _resolver.TryEntityOrFact(CampaignHandle.Parse("F7")));

        Assert.Contains(entity.SeqHandle, error.Message);
        Assert.Contains(fact.SeqHandle, error.Message);
        Assert.DoesNotContain("axiom", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Names compare by CampaignText.Key, then without a leading article on either side.</summary>
    [Theory]
    [InlineData("The Old King")]
    [InlineData("the old king")]
    [InlineData("  THE   OLD KING ")]
    [InlineData("Old King")]
    [InlineData("an old king")]
    public void EntityByName_ForgivingSpelling_FindsTheEntity(string name) =>
        Assert.Equal(_oldKing.Id, _resolver.EntityByName(CampaignValues.Kinds.Character, name)?.Id);

    [Theory]
    [InlineData("Bjorn Mountainfell")]
    [InlineData("björn mountainfell")]
    public void EntityByName_Diacritics_AreIgnored(string name) =>
        Assert.Equal(_bjorn.Id, _resolver.EntityByName(CampaignValues.Kinds.Character, name)?.Id);

    [Theory]
    [InlineData("character", "Serret")]
    [InlineData("character", "Tristan")]
    [InlineData("location", "The Old King")]
    [InlineData("character", "   ")]
    public void EntityByName_WrongKindDeletedOrBlank_IsNull(string kind, string name) =>
        Assert.Null(_resolver.EntityByName(kind, name));

    /// <summary>
    /// Two entities with one name: refused, listing both, never a silent pick. Only as <c>e:&lt;n&gt;</c> handles: a
    /// <c>kind:slug</c> ref spells a name, and the twin here is author-only under a slug that names what it really is.
    /// </summary>
    [Fact]
    public void EntityByName_TwoEntitiesShareTheName_IsRefusedListingBothBySeqHandleOnly()
    {
        var twin = new CampaignSeed(_connection).Entity(_campaign.Id, CampaignValues.Kinds.Character, "the old king",
            slug: "keras-in-disguise", visibility: CampaignValues.Visibilities.Author);

        var error = Assert.Throws<DndInputException>(() => _resolver.EntityByName(CampaignValues.Kinds.Character, "The Old King"));

        Assert.Contains(_oldKing.SeqHandle, error.Message);
        Assert.Contains(twin.SeqHandle, error.Message);
        Assert.DoesNotContain("character:", error.Message);
        Assert.DoesNotContain("keras", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("the-old-king", error.Message);
    }

    /// <summary>The exact-key tier wins before the article tier, so "The Old King" is not ambiguous with a bare "Old King".</summary>
    [Fact]
    public void EntityByName_ExactMatchAndArticleMatch_TheExactOneWins()
    {
        var bare = new CampaignSeed(_connection).Entity(_campaign.Id, CampaignValues.Kinds.Character, "Old King", slug: "old-king");

        Assert.Equal(_oldKing.Id, _resolver.EntityByName(CampaignValues.Kinds.Character, "The Old King")?.Id);
        Assert.Equal(bare.Id, _resolver.EntityByName(CampaignValues.Kinds.Character, "old king")?.Id);
    }

    [Fact]
    public void TrySession_EachForm_FindsTheSessionRow()
    {
        Assert.Equal(_s1.EntityId, _resolver.TrySession(CampaignHandle.Parse("session:1"))?.EntityId);
        Assert.Equal(_live.EntityId, _resolver.TrySession(CampaignHandle.Parse("session:live"))?.EntityId);
        Assert.Equal(_s2.EntityId, _resolver.TrySession(CampaignHandle.Parse("session:last"))?.EntityId);
        Assert.Equal(_s1.EntityId, _resolver.TrySession(CampaignHandle.Parse($"e:{_s1.Seq}"))?.EntityId);
        Assert.Null(_resolver.TrySession(CampaignHandle.Parse("character:the-old-king")));
        Assert.Equal(4L, _resolver.LiveSession()?.Number);
        Assert.Null(_resolver.SessionByNumber(7));
    }

    [Fact]
    public void TrySession_NoLiveOrPlayedSession_IsNull()
    {
        var empty = new HandleResolver(_connection, _other.Id);

        Assert.Null(empty.TrySession(CampaignHandle.Parse("session:live")));
        Assert.Null(empty.TrySession(CampaignHandle.Parse("session:last")));
    }

    [Theory]
    [InlineData("belmakor", "belmakor")]
    [InlineData("BELMAKOR", "belmakor")]
    [InlineData("One Piece", "one-piece")]
    [InlineData("one piece", "one-piece")]
    [InlineData("nothing", null)]
    [InlineData("", null)]
    public void TryCampaign_SlugOrName_FindsTheCampaign(string text, string? slug) =>
        Assert.Equal(slug, HandleResolver.TryCampaign(_connection, text)?.Slug);

    [Fact]
    public void TryCampaign_TwoCampaignsWithTheName_IsRefusedNamingBothSlugs()
    {
        new CampaignSeed(_connection).Campaign(name: "One Piece", slug: "one-piece-2");

        var error = Assert.Throws<DndInputException>(() => HandleResolver.TryCampaign(_connection, "One Piece"));

        Assert.Contains("one-piece, one-piece-2", error.Message);
    }

    [Fact]
    public void Campaigns_All_BySlug() =>
        Assert.Equal(new[] { "belmakor", "one-piece" }, HandleResolver.Campaigns(_connection).Select(c => c.Slug));

    [Theory]
    [InlineData("old kin", "character:the-old-king")]
    [InlineData("the old kign", "character:the-old-king")]
    [InlineData("bjorn", "character:bjorn-mountainfell")]
    [InlineData("mountainfel", "character:bjorn-mountainfell")]
    public void Suggest_CloseText_OffersTheHandle(string text, string handle) =>
        Assert.Equal(handle, _resolver.Suggest(CampaignValues.Kinds.Character, text, _ => true).FirstOrDefault());

    /// <summary>Suggestions come only from what the caller says is visible, so a "not found" never names a hidden entity.</summary>
    [Fact]
    public void Suggest_InvisibleEntities_AreNeverOffered()
    {
        var suggestions = _resolver.Suggest(null, "old king", e => e.Id != _oldKing.Id);

        Assert.DoesNotContain("character:the-old-king", suggestions);
    }

    /// <summary>
    /// The view overload matches only the name the reader knows: typing the true name of an entity the reader knows as
    /// "the old king" must not surface it (that would reveal the typed name is close to something hidden).
    /// </summary>
    [Fact]
    public void Suggest_WithAView_MatchesOnlyTheNameTheReaderKnows()
    {
        var hidden = new CampaignSeed(_connection).Entity(_campaign.Id, CampaignValues.Kinds.Character, "Keras Voidborn");
        SuggestionView? View(EntityRow e) => e.Id == hidden.Id ? new SuggestionView("the hooded stranger", "e:" + e.Seq) : null;

        Assert.Empty(_resolver.Suggest(CampaignValues.Kinds.Character, "Keras", View));
        Assert.Equal(new[] { hidden.SeqHandle }, _resolver.Suggest(CampaignValues.Kinds.Character, "hooded strangr", View));
    }

    [Fact]
    public void Suggest_CapsAtMax()
    {
        var seed = new CampaignSeed(_connection);
        for (var i = 0; i < 8; i++)
        {
            seed.Entity(_campaign.Id, CampaignValues.Kinds.Location, $"Dock {i}");
        }

        Assert.Equal(3, _resolver.Suggest(CampaignValues.Kinds.Location, "dock", _ => true, max: 3).Count);
    }

    private static (string?, string?) Ids((EntityRow? Entity, FactRow? Fact) found) => (found.Entity?.Id, found.Fact?.Id);
}
