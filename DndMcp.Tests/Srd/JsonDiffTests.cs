using System.Text.Json;
using Xunit;

namespace DndMcp.Tests.Srd;

/// <summary>
/// Pins the comparison the round-trip tests rely on. If <see cref="JsonDiff.FirstDifference"/> ever reported "equal"
/// for different documents, every monster and spell round-trip test would pass however much data the models
/// dropped. So it must catch each kind of loss (a missing property, an extra one, a changed value, a changed type, a
/// shorter array) and ignore only what JSON itself ignores (property order and number formatting).
/// </summary>
public sealed class JsonDiffTests
{
    [Theory]
    [InlineData("""{"a":1,"b":[1,2]}""", """{"b":[1,2],"a":1}""")]
    [InlineData("""{"cr":17}""", """{"cr":17.0}""")]
    [InlineData("""{"cr":0.125}""", """{"cr":1.25e-1}""")]
    [InlineData("""{"hover":true}""", """{"hover":true}""")]
    [InlineData("""[]""", """[]""")]
    public void FirstDifference_EquivalentJson_IsNull(string expected, string actual)
    {
        Assert.Null(Compare(expected, actual));
    }

    [Theory]
    [InlineData("""{"a":1,"gear":"x"}""", """{"a":1}""", "$.gear: missing after round trip")]
    [InlineData("""{"a":1}""", """{"a":1,"b":null}""", "$.b: not in the vendored JSON")]
    [InlineData("""{"a":{"b":[1,2,3]}}""", """{"a":{"b":[1,2]}}""", "$.a.b: expected 3 items, got 2")]
    [InlineData("""{"a":[{"count":"2"}]}""", """{"a":[{"count":2}]}""", "$.a[0].count: expected String")]
    [InlineData("""{"a":"x"}""", """{"a":"y"}""", "$.a: expected \"x\", got \"y\"")]
    [InlineData("""{"a":0.125}""", """{"a":0.25}""", "$.a: expected 0.125, got 0.25")]
    [InlineData("""{"a":true}""", """{"a":false}""", "$.a: expected true, got false")]
    [InlineData("""{"a":[]}""", """{"a":{}}""", "$.a: expected Array")]
    public void FirstDifference_DifferentJson_NamesThePath(string expected, string actual, string expectedPrefix)
    {
        var difference = Compare(expected, actual);

        Assert.NotNull(difference);
        Assert.StartsWith(expectedPrefix, difference, StringComparison.Ordinal);
    }

    private static string? Compare(string expected, string actual)
    {
        using var expectedDocument = JsonDocument.Parse(expected);
        using var actualDocument = JsonDocument.Parse(actual);
        return JsonDiff.FirstDifference(expectedDocument.RootElement, actualDocument.RootElement);
    }
}
