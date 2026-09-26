using System.Text.Json;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Models;
using Xunit;

namespace DndMcp.Tests.Srd.Json;

/// <summary>
/// Pins how a multiattack count is read and written. Numeric strings and JSON numbers both become a fixed count.
/// Descriptive counts ("Number of Heads", "1d4") keep their text and have NO fixed value, so the simulator can
/// never mistake them for 0 or 1. Every malformed token throws a <see cref="JsonException"/> that names the ACTUAL
/// problem (a too-large count is not called a fraction), and the string and number forms describe the same problem the
/// same way. Writing reproduces the token kind that was read.
/// </summary>
public sealed class MultiattackCountConverterTests
{
    [Theory]
    [InlineData("\"2\"", 2, "2")]
    [InlineData("\"1\"", 1, "1")]
    [InlineData("\"0\"", 0, "0")]
    [InlineData("\" 3 \"", 3, " 3 ")]
    [InlineData("3", 3, "3")]
    [InlineData("0", 0, "0")]
    public void Read_NumericStringOrNumber_IsFixedCount(string json, int expected, string expectedText)
    {
        var count = Deserialize(json);

        Assert.True(count.IsFixed);
        Assert.Equal(expected, count.Value);
        Assert.Equal(expectedText, count.Text);
    }

    [Theory]
    [InlineData("\"Number of Heads\"")]
    [InlineData("\"1d4\"")]
    [InlineData("\"2 or 3\"")]
    public void Read_DescriptiveString_KeepsTextWithNoFixedValue(string json)
    {
        var count = Deserialize(json);

        Assert.False(count.IsFixed);
        Assert.Null(count.Value);
        Assert.Equal(JsonSerializer.Deserialize<string>(json), count.Text);
        Assert.Equal(count.Text, count.ToString());
    }

    [Theory]
    [InlineData("true", "found a boolean")]
    [InlineData("{}", "found an object")]
    [InlineData("[\"2\"]", "found an array")]
    [InlineData("\"\"", "cannot be an empty string")]
    [InlineData("\"   \"", "cannot be an empty string")]
    [InlineData("-1", "cannot be negative, but found -1")]
    [InlineData("-99999999999", "cannot be negative, but found -99999999999")]
    [InlineData("1.5", "must be a whole number, but found 1.5")]
    [InlineData("2.0", "must be written as a plain integer, but found 2.0")]
    [InlineData("2e0", "must be written as a plain integer, but found 2e0")]
    [InlineData("99999999999", "The multiattack count 99999999999 is too large")]
    [InlineData("1e400", "The multiattack count 1e400 is out of range")]
    [InlineData("\"99999999999\"", "The multiattack count \"99999999999\" is too large")]
    public void Read_MalformedToken_ThrowsJsonExceptionNamingTheProblem(string json, string expectedFragment)
    {
        var ex = Assert.Throws<JsonException>(() => Deserialize(json));

        Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
    }

    // The converter is registered on the type, so it must apply everywhere a count appears. The research said
    // action_options used numbers while actions[] used strings; both positions must accept both forms.
    [Theory]
    [InlineData("""{"option_type":"action","action_name":"Rend","count":"1","type":"melee"}""")]
    [InlineData("""{"option_type":"action","action_name":"Rend","count":1,"type":"melee"}""")]
    public void Read_CountInsideActionOption_AcceptsStringOrNumber(string json)
    {
        var option = JsonSerializer.Deserialize<MultiattackOption>(json, SrdJson.Options)!;

        Assert.Equal(1, option.Count!.Value);
    }

    [Fact]
    public void Read_NullCountOnARoutineStep_Throws()
    {
        const string json = """{"action_name":"Bite","count":null,"type":"melee"}""";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MultiattackAction>(json, SrdJson.Options));
    }

    [Theory]
    [InlineData("\"2\"")]
    [InlineData("2")]
    [InlineData("\"Number of Heads\"")]
    [InlineData("\"1d4\"")]
    public void Write_AfterRead_ReproducesTheOriginalToken(string json)
    {
        var written = JsonSerializer.Serialize(Deserialize(json), SrdJson.Options);

        Assert.Equal(json, written);
    }

    [Fact]
    public void FromText_EmptyText_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => MultiattackCount.FromText(" "));
    }

    [Fact]
    public void FromNumber_Negative_ThrowsArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MultiattackCount.FromNumber(-1));
    }

    private static MultiattackCount Deserialize(string json) =>
        JsonSerializer.Deserialize<MultiattackCount>(json, SrdJson.Options)!;
}
