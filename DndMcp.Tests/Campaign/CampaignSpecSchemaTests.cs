using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Features;
using Xunit;

namespace DndMcp.Tests.Campaign;

/// <summary>
/// Invariant: the campaign op spec classes are a schema the model reads and a type the host binds straight into: every
/// property described in one short sentence, init-only, nullable (so "not given" survives binding), exported with
/// snake_case names, and bound from snake_case JSON with unknown fields refused (the host's argument guard refuses them
/// too).
/// </summary>
public sealed class CampaignSpecSchemaTests
{
    private static readonly Type[] SpecTypes =
    [
        typeof(CampaignOpSpec), typeof(AliasSpec), typeof(ClockSpec), typeof(FactLinkSpec), typeof(KnowerSpec), typeof(AttendanceSpec),
        typeof(GateSpec), typeof(RouteSpec),
    ];

    private const int MaxDescriptionLength = 320;

    public static TheoryData<string, PropertyInfo> AllProperties()
    {
        var data = new TheoryData<string, PropertyInfo>();
        foreach (var property in SpecTypes.SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)))
        {
            data.Add($"{property.DeclaringType!.Name}.{property.Name}", property);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllProperties))]
    public void EveryProperty_HasAShortSentenceDescription(string name, PropertyInfo property)
    {
        var description = property.GetCustomAttribute<DescriptionAttribute>()?.Description;

        Assert.False(string.IsNullOrWhiteSpace(description), name);
        Assert.True(description!.Length <= MaxDescriptionLength, $"{name}: {description.Length} characters");
        Assert.EndsWith(".", description, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", description, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AllProperties))]
    public void EveryProperty_IsPublicInitOnlyAndNullable(string name, PropertyInfo property)
    {
        var setter = property.SetMethod;

        Assert.NotNull(setter);
        Assert.True(setter!.IsPublic, name);
        Assert.Contains(typeof(IsExternalInit), setter.ReturnParameter.GetRequiredCustomModifiers());
        Assert.True(!property.PropertyType.IsValueType || Nullable.GetUnderlyingType(property.PropertyType) is not null, name);
    }

    [Theory]
    [InlineData(typeof(CampaignOpSpec), "body_md", "secret_md", "canon_status", "sort_key", "remove_aliases", "known_by", "superseded_by", "answered_by")]
    [InlineData(typeof(GateSpec), "after", "with", "min_routes", "forbidden_terms", "forbidden_patterns", "forbidden_until", "preferred_terms")]
    [InlineData(typeof(RouteSpec), "id", "clues", "min_clues")]
    [InlineData(typeof(ClockSpec), "segments", "on_fill_md", "shown_to_players", "front")]
    [InlineData(typeof(KnowerSpec), "who", "state", "known_as", "via", "session")]
    public void Schema_ExportsWithSnakeCaseNames(Type type, params string[] names)
    {
        var schema = DslJson.Options.GetJsonSchemaAsNode(type);

        var properties = Assert.IsType<JsonObject>(schema["properties"]);
        Assert.All(names, n => Assert.True(properties.ContainsKey(n), n));
    }

    [Fact]
    public void Bind_OpWithNestedSpecs_ReadsEverythingTheModelWrote()
    {
        var spec = CampaignOpFieldsTests.Bind(CampaignOpFieldsTests.FullSpecJson);

        Assert.Equal("upsert", spec.Op);
        Assert.Equal(1.5, spec.SortKey);
        Assert.Equal("Guts", spec.Aliases![0]!.Alias);
        Assert.Equal(6, spec.Clock!.Segments);
        Assert.True(spec.Clock.ShownToPlayers);
        Assert.Equal("captain", spec.Data!["rank"].GetString());
        Assert.Equal(["f:1"], spec.Gate!.After!);
        Assert.Equal("party", spec.KnownBy![0]!.Who);
        Assert.Equal("about", spec.Links![0]!.Role);
    }

    [Fact]
    public void Bind_DataNullValue_IsKeptAsANullElementSoTheMergePatchCanRemoveTheKey()
    {
        var spec = CampaignOpFieldsTests.Bind("""{"op": "upsert", "ref": "e:1", "data": {"rank": null, "Mixed_Case": 1}}""");

        Assert.Equal(JsonValueKind.Null, spec.Data!["rank"].ValueKind);
        Assert.True(spec.Data.ContainsKey("Mixed_Case"));
    }

    [Fact]
    public void Bind_UnknownNestedField_IsRefused()
    {
        var ex = Assert.Throws<Domain.Core.DndInputException>(() =>
            CampaignOpFieldsTests.Bind("""{"op": "fact", "statement": "s", "gate": {"aftr": ["f:1"]}}"""));

        Assert.Contains("aftr", ex.Message, StringComparison.Ordinal);
    }
}
