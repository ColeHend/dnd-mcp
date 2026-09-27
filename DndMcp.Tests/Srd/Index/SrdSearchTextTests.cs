using System.Text.Json;
using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.Tests.Srd.Index;

/// <summary>
/// <see cref="SrdSearchText"/> on hand-made records, where each rule can be seen on its own: description first,
/// every other string after, links and machinery left out, markdown markers stripped. The import tests check the same
/// rules hold across all 4,602 real records.
/// </summary>
public sealed class SrdSearchTextTests
{
    [Fact]
    public void Build_Record_PutsTheDescriptionFirstThenOtherStringsInOrder()
    {
        var text = Build("""
            {"index": "x", "name": "Fireball", "school": {"index": "evocation", "name": "Evocation", "url": "/api/2014/magic-schools/evocation"},
             "desc": ["A bright streak.", "It explodes."], "range": "150 feet"}
            """);

        Assert.Equal("A bright streak.\nIt explodes.\nEvocation\n150 feet", text);
    }

    [Fact]
    public void Build_DescriptionAsAString_IsFirstToo()
    {
        Assert.Equal("Prone rules.\nCondition", Build("""{"name": "Prone", "category": "Condition", "description": "Prone rules."}"""));
    }

    // Identifiers, links, images, Choice discriminators and the glossary's provenance are not rules text.
    [Fact]
    public void Build_Machinery_IsLeftOut()
    {
        var text = Build("""
            {"id": "c000abd1", "index": "acolyte", "url": "/api/2024/backgrounds/acolyte", "image": "/api/images/x.png", "source": "SRD 5.2",
             "equipment_options": [{"choose": 1, "type": "equipment", "from": {"option_set_type": "options_array",
               "options": [{"option_type": "counted_reference", "count": 1, "of": {"index": "book", "name": "Book"}}]}}],
             "class_levels": "/api/2014/classes/fighter/levels"}
            """);

        Assert.Equal("equipment\nBook", text);
    }

    [Fact]
    public void Build_NestedNameKeys_AreKeptOnlyTheRecordsOwnNameIsNot()
    {
        Assert.Equal("Wizard", Build("""{"name": "Fireball", "classes": [{"name": "Wizard"}]}"""));
    }

    [Theory]
    [InlineData("**_Restricted Movement._** Your only movement", "Restricted Movement. Your only movement")]
    [InlineData("#### Size Categories\n\nText", "Size Categories\nText")]
    [InlineData("| Size | Space |\n|------|-------|\n| Tiny | 2½ by 2½ ft. |", "Size Space\nTiny 2½ by 2½ ft.")]
    [InlineData("  spaced   out  ", "spaced out")]
    [InlineData("a\r\nb", "a\nb")]
    [InlineData("***", "")]
    public void Clean_Markdown_KeepsWordsAndDropsMarkers(string input, string expected)
    {
        Assert.Equal(expected, SrdSearchText.Clean(input));
    }

    [Fact]
    public void Build_NotAnObject_IsEmpty()
    {
        Assert.Equal(string.Empty, Build("[\"a\"]"));
    }

    private static string Build(string json)
    {
        using var document = JsonDocument.Parse(json);
        return SrdSearchText.Build(document.RootElement);
    }
}
