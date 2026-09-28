using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DndMcp.Domain.Encounters;
using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation;
using DndMcp.Tests.Encounters;
using Xunit;

namespace DndMcp.Tests.Srd.Combatants;

/// <summary>
/// A stat block as canonical JSON: every member, formulas as their text, CRs as "1/4". Stat blocks are records whose
/// lists compare by reference, so "did this change anything" (an override removed, the index's document instead of the
/// test's) is asked of this text.
/// </summary>
internal static class StatBlockJson
{
    private static readonly JsonSerializerOptions Options = Create();

    public static string Canonical(StatBlock block) => JsonSerializer.Serialize(block, Options);

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new FormulaConverter());
        options.Converters.Add(new CrConverter());
        return options;
    }

    private sealed class FormulaConverter : JsonConverter<DamageFormula>
    {
        public override DamageFormula Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, DamageFormula value, JsonSerializerOptions options) => writer.WriteStringValue(value.Text);
    }

    private sealed class CrConverter : JsonConverter<ChallengeRating>
    {
        public override ChallengeRating Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, ChallengeRating value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }
}

/// <summary>
/// A <c>[Fact]</c> that runs only where the serving-solid-characters repository is checked out beside this one (its SRD
/// markdown and its SRD JSON), as on the machine the overrides were extracted on; the Fact twin of
/// <see cref="SiblingSrdMarkdownTheoryAttribute"/>.
/// </summary>
public sealed class SiblingRepoFactAttribute : FactAttribute
{
    public SiblingRepoFactAttribute()
    {
        if (SiblingSrdMarkdownTheoryAttribute.DocsDirectory is null)
        {
            Skip = "The serving-solid-characters repository is not checked out beside this one.";
        }
    }

    /// <summary>The sibling repository's root, or null.</summary>
    public static string? RepositoryRoot =>
        SiblingSrdMarkdownTheoryAttribute.DocsDirectory is { } docs ? Path.GetDirectoryName(docs) : null;

    /// <summary>The SRD 5.2 markdown source directory (<c>dndsrd5.2_markdown-main/src</c>).</summary>
    public static string Srd52 => Path.Combine(SiblingSrdMarkdownTheoryAttribute.DocsDirectory!, "dndsrd5.2_markdown-main", "src");
}
