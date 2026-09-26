using System.Text.Json;
using DndMcp.Repository.Srd;
using Xunit;

namespace DndMcp.Tests.Srd.Json;

/// <summary>
/// Pins how description-like fields are read through <see cref="SrdEntry"/>, the model that reads all 49 vendored
/// files. A string becomes one paragraph, an array keeps its paragraphs, and anything else throws a
/// <see cref="JsonException"/> saying what was found. Nothing is ever coerced to text or dropped. Writing always
/// produces an array.
/// </summary>
public sealed class StringOrStringArrayConverterTests
{
    [Theory]
    [InlineData("\"One paragraph.\"", new[] { "One paragraph." })]
    [InlineData("\"\"", new[] { "" })]
    [InlineData("[\"First.\",\"Second.\"]", new[] { "First.", "Second." })]
    [InlineData("[]", new string[0])]
    public void Read_StringOrArray_ReturnsParagraphs(string descJson, string[] expected)
    {
        var entry = Entry("desc", descJson);

        Assert.Equal(expected, entry.Desc);
    }

    [Fact]
    public void Read_DescriptionProperty_UsesTheSameConverter()
    {
        var entry = Entry("description", "\"While you have the Prone condition…\"");

        Assert.Equal(["While you have the Prone condition…"], entry.Description);
        Assert.Null(entry.Desc);
    }

    [Fact]
    public void Read_NullOrAbsent_IsNull()
    {
        Assert.Null(Entry("desc", "null").Desc);
        Assert.Null(JsonSerializer.Deserialize<SrdEntry>("""{"index":"x","url":"/api/2014/x"}""", SrdJson.Options)!.Desc);
    }

    [Theory]
    [InlineData("1", "but found a number")]
    [InlineData("true", "but found a boolean")]
    [InlineData("{}", "but found an object")]
    [InlineData("[1]", "item 0 is a number")]
    [InlineData("[\"a\",null]", "item 1 is null")]
    [InlineData("[\"a\",[\"b\"]]", "item 1 is an array")]
    [InlineData("[{\"text\":\"a\"}]", "item 0 is an object")]
    public void Read_NotStringOrStringArray_ThrowsJsonExceptionNamingWhatWasFound(string descJson, string expectedFragment)
    {
        var ex = Assert.Throws<JsonException>(() => Entry("desc", descJson));

        Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_ValuesAfterTheArray_AreStillRead()
    {
        // A converter that leaves the reader in the wrong place corrupts every property after it.
        var entry = JsonSerializer.Deserialize<SrdEntry>(
            """{"index":"x","desc":["a","b"],"url":"/api/2014/x","name":"After"}""", SrdJson.Options)!;

        Assert.Equal(["a", "b"], entry.Desc);
        Assert.Equal("/api/2014/x", entry.Url);
        Assert.Equal("After", entry.Name);
    }

    [Theory]
    [InlineData("\"One.\"", """["One."]""")]
    [InlineData("[\"One.\",\"Two.\"]", """["One.","Two."]""")]
    public void Write_Paragraphs_AlwaysWritesAnArray(string descJson, string expectedJson)
    {
        var written = JsonSerializer.SerializeToElement(Entry("desc", descJson), SrdJson.Options);

        Assert.Equal(expectedJson, written.GetProperty("desc").GetRawText());
    }

    private static SrdEntry Entry(string property, string valueJson) =>
        JsonSerializer.Deserialize<SrdEntry>($$"""{"index":"x","url":"/api/2014/x","{{property}}":{{valueJson}}}""", SrdJson.Options)!;
}
