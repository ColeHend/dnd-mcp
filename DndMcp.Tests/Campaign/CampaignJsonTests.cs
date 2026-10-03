using System.Text.Json;
using DndMcp.Domain.Campaign;
using Xunit;

namespace DndMcp.Tests.Campaign;

/// <summary>
/// Invariant: stored campaign JSON is written one way (snake_case, nulls omitted, non-ASCII and quotes readable) so that
/// change_log's "did this change?" comparison is a text comparison; <see cref="CampaignJson.Options"/> reads a later
/// version's unknown fields without failing and <see cref="CampaignJson.Strict"/> refuses them.
/// </summary>
public sealed class CampaignJsonTests
{
    private sealed record Sample(string? DisplayName, int? SortOrder, string? Missing);

    [Fact]
    public void Serialize_SnakeCaseNullsOmittedRelaxedEscaping()
    {
        var json = JsonSerializer.Serialize(new Sample("Björn \"the Bear\" <3", 2, null), CampaignJson.Options);

        Assert.Equal("""{"display_name":"Björn \"the Bear\" <3","sort_order":2}""", json);
    }

    [Fact]
    public void Deserialize_UnknownField_IsIgnoredByOptionsAndRefusedByStrict()
    {
        const string json = """{"display_name":"x","sort_order":"3","later_field":true}""";

        var read = JsonSerializer.Deserialize<Sample>(json, CampaignJson.Options)!;

        Assert.Equal(("x", 3), (read.DisplayName, read.SortOrder));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Sample>(json, CampaignJson.Strict));
    }

    [Fact]
    public void Options_AreReadOnly()
    {
        Assert.True(CampaignJson.Options.IsReadOnly);
        Assert.True(CampaignJson.Strict.IsReadOnly);
    }
}
