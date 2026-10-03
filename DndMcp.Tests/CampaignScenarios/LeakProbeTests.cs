using Xunit;
using Xunit.Sdk;

namespace DndMcp.Tests.CampaignScenarios;

/// <summary>
/// The told-fact exception of <see cref="LeakProbe.CleanExceptToldFacts"/> is as narrow as it claims: only the text of the
/// named fact is excused. Were it wider (every fact's text, a whole object that has the ref, a nested entity), the dm's
/// leak checks would pass an author alias or a ref naming Keras, which is what they exist to catch.
/// </summary>
public sealed class LeakProbeTests
{
    private static readonly string[] Forbidden = ["Keras"];

    public static TheoryData<string, object> Leaks() => new()
    {
        { "an author alias beside the told fact", new { Ref = "character:old-king", Aliases = new[] { new { Alias = "Keras" } }, Facts = new[] { new { Ref = "f:2", Text = "Keras" } } } },
        { "a ref inside the told fact's links", new { Ref = "f:2", Text = "Keras", Links = new[] { new { Ref = "character:keras" } } } },
        { "another fact's text", new[] { new { Ref = "f:2", Text = "Keras" }, new { Ref = "f:3", Text = "Keras sent them" } } },
        { "an explanation on the told fact", new { Ref = "f:2", Text = "Keras", Explanation = "known as Keras" } },
    };

    [Theory]
    [MemberData(nameof(Leaks))]
    public void CleanExceptToldFacts_TheWordOutsideTheToldFactsText_Fails(string where, object result)
    {
        Assert.ThrowsAny<XunitException>(() => LeakProbe.CleanExceptToldFacts(result, Forbidden, ["f:2"], where));
    }

    [Fact]
    public void CleanExceptToldFacts_TheWordOnlyInTheToldFactsTextAndSnippet_Passes()
    {
        var result = new { Facts = new[] { new { Ref = "f:2", Text = "The old king's name is Keras.", Snippet = "…is Keras." } }, Ref = "character:old-king" };

        LeakProbe.CleanExceptToldFacts(result, Forbidden, ["f:2"], "the told fact");
        Assert.ThrowsAny<XunitException>(() => LeakProbe.CleanExceptToldFacts(result, Forbidden, [], "nothing told"));
    }
}
