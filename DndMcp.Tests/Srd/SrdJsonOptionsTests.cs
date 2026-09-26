using System.Text.Json;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Models;
using Xunit;

namespace DndMcp.Tests.Srd;

/// <summary>
/// Pins the documented split between production and test strictness in <see cref="SrdJson.Options"/>.
///
/// <list type="bullet">
/// <item>An unknown upstream field is skipped in production, so a new field never breaks a user's rules lookup. It
/// fails under the tests' strict options, so a re-vendor still surfaces it.</item>
/// <item>A missing required field or a null where a value is promised fails in BOTH. Accepting either would move the
/// failure to a NullReferenceException far from the data.</item>
/// <item>Writing omits nulls, which the round-trip tests depend on.</item>
/// </list>
/// </summary>
public sealed class SrdJsonOptionsTests
{
    private const string ReferenceWithNewField =
        """{"index":"fire","name":"Fire","url":"/api/2024/damage-types/fire","added_upstream_later":true}""";

    [Fact]
    public void Deserialize_UnknownField_ProductionSkipsIt()
    {
        var reference = JsonSerializer.Deserialize<ApiReference>(ReferenceWithNewField, SrdJson.Options)!;

        Assert.Equal("fire", reference.Index);
    }

    [Fact]
    public void Deserialize_UnknownField_StrictTestOptionsThrow()
    {
        var ex = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ApiReference>(ReferenceWithNewField, SrdTestContent.StrictOptions));

        Assert.Contains("added_upstream_later", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"name":"Fire","url":"/api/2024/damage-types/fire"}""", "index")]
    [InlineData("""{"index":"fire","name":"Fire"}""", "url")]
    public void Deserialize_MissingRequiredField_ThrowsInProduction(string json, string missing)
    {
        var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ApiReference>(json, SrdJson.Options));

        Assert.Contains(missing, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"index":null,"name":"Fire","url":"/api/2024/damage-types/fire"}""")]
    [InlineData("""{"index":"fire","name":null,"url":"/api/2024/damage-types/fire"}""")]
    public void Deserialize_NullForNonNullableProperty_ThrowsInProduction(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ApiReference>(json, SrdJson.Options));
    }

    [Fact]
    public void Deserialize_NullForNullableProperty_IsAccepted()
    {
        var reference = JsonSerializer.Deserialize<ApiReference>(
            """{"index":"charmed","name":"Charmed","url":"/api/2024/conditions/charmed","note":null}""", SrdJson.Options)!;

        Assert.Null(reference.Note);
    }

    [Fact]
    public void Serialize_NullOptionalProperty_IsOmitted()
    {
        var reference = new ApiReference { Index = "fire", Name = "Fire", Url = "/api/2024/damage-types/fire" };

        var json = JsonSerializer.Serialize(reference, SrdJson.Options);

        Assert.Equal("""{"index":"fire","name":"Fire","url":"/api/2024/damage-types/fire"}""", json);
    }

    // Rules text is full of apostrophes and typographic quotes. The default encoder would write can’t as can’t.
    [Fact]
    public void Serialize_TypographicText_IsNotEscaped()
    {
        var reference = new ApiReference { Index = "x", Name = "The dragon can’t — won't", Url = "/api/2024/x" };

        var json = JsonSerializer.Serialize(reference, SrdJson.Options);

        Assert.Contains("The dragon can’t — won't", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_Shared_AreReadOnly()
    {
        Assert.True(SrdJson.Options.IsReadOnly);
    }
}
