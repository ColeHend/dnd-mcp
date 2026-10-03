using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Read;
using Xunit;

namespace DndMcp.Tests.CampaignRead;

/// <summary>
/// Invariant: a "did you mean" suggestion names a session as <c>session:&lt;n&gt;</c>, the form every reader prints and
/// every tool takes, whichever path built it: the author's reader refusal, the resolver's author overload (which prints
/// <c>kind:slug</c>) and a caller's own view that hands back <c>kind:slug</c> (the write path's). Belmakor's session 1 and
/// its arc share the title "The Iron Guts job", so a typo of it suggests both.
///
/// <para>
/// Why it fails silently: <c>session:session-1</c> resolves (it is the entity's <c>kind:slug</c>), so nothing breaks; it
/// only reads like a typo of <c>session:1</c> in the one message meant to fix a typo.
/// </para>
/// </summary>
public sealed class SessionSuggestionTests(BelmakorFixture fixture) : IClassFixture<BelmakorFixture>
{
    [Theory]
    [InlineData("author")]
    [InlineData("party")]
    [InlineData("character:belmakor")]
    public void Get_ATypoOfASessionsTitle_SuggestsItAsSessionNumber(string perspective)
    {
        var ex = Assert.Throws<DndInputException>(() => new EntityReader(fixture.Db.Database).Get(fixture.CampaignRow, ["iron-guts-jb"],
            EntityIncludes.Default, Perspective.Parse(perspective)));

        Assert.EndsWith(" Did you mean arc:iron-guts-job, session:1?", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("session:session-", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Suggest_EitherOverloadGivenASessionsKindSlug_PrintsSessionNumber()
    {
        var resolver = new HandleResolver(fixture.Connection, fixture.Campaign.Id);

        var author = resolver.Suggest(null, "iron guts jb", _ => true);
        var ownView = resolver.Suggest(null, "iron guts jb", e => new SuggestionView(e.Name, e.Handle));
        var disguised = resolver.Suggest(CampaignValues.Kinds.Session, "iron guts jb", e => new SuggestionView(e.Name, e.SeqHandle));

        Assert.Equal(["arc:iron-guts-job", "session:1"], author);
        Assert.Equal(["arc:iron-guts-job", "session:1"], ownView);
        // A handle the caller chose for another reason (e:<n> for a disguise) is not rewritten.
        Assert.Equal(["e:" + fixture.S1.Seq], disguised);
    }
}
