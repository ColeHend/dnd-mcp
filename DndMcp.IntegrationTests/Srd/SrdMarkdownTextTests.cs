using System.Text.Json;
using DndMcp.Formatting.Srd;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.IntegrationTests.Srd;

/// <summary>
/// Invariant: the shared building blocks read the same in every formatter. A linked record keeps the SRD's qualifier
/// next to its name and still shows the ref that fetches it, and a record with nothing to show says so in one wording,
/// whichever kind it is.
/// </summary>
public sealed class SrdMarkdownTextTests
{
    // Upstream narrows a reference with a note: the acolyte's origin feat is Magic Initiate for the Cleric list only, and the
    // archmage is immune to Charmed only while Mind Blank lasts. Dropping the note would overstate both.
    [Theory]
    [InlineData("2024/background/acolyte", "feat", "Magic Initiate (Cleric) (`2024/feat/magic-initiate`)")]
    [InlineData("2024/monster/archmage", "condition_immunities", "Charmed (with Mind Blank) (`2024/condition/charmed`)")]
    [InlineData("2024/monster/vampire-familiar", "condition_immunities", "Charmed (except from its vampire master) (`2024/condition/charmed`)")]
    public void LinkWithNote_RealReferenceWithANote_ShowsTheNoteBetweenNameAndRef(string reference, string property, string expected)
    {
        var root = VendoredSrdLookup.Instance.Require(reference).Root;
        var apiReference = root.GetProperty(property) is { ValueKind: JsonValueKind.Array } array ? array[0] : root.GetProperty(property);

        Assert.Equal(expected, SrdMarkdownText.LinkWithNote(apiReference));
    }

    [Theory]
    [InlineData("""{"name":"Book","note":"Prayers","url":"/api/2024/equipment/book"}""", null, "Book (Prayers) (`2024/equipment/book`)")]
    // Upstream pads some notes; the padding must not reach the parentheses.
    [InlineData("""{"name":"Book","note":"  Prayers ","url":"/api/2024/equipment/book"}""", null, "Book (Prayers) (`2024/equipment/book`)")]
    // No note, or a blank one (2024 languages carry "note": ""), is the plain link.
    [InlineData("""{"name":"Common","note":"","url":"/api/2024/languages/common"}""", null, "Common (`2024/language/common`)")]
    [InlineData("""{"name":"Common","url":"/api/2024/languages/common"}""", null, "Common (`2024/language/common`)")]
    // A note the caller worked out replaces the reference's own; blank means none.
    [InlineData("""{"name":"Command","url":"/api/2024/spells/command"}""", " level 2 version ", "Command (level 2 version) (`2024/spell/command`)")]
    [InlineData("""{"name":"Command","note":"ignored","url":"/api/2024/spells/command"}""", " ", "Command (`2024/spell/command`)")]
    // No resolvable URL: the name and note alone, never a ref that fetches nothing.
    [InlineData("""{"name":"Holy Symbol","note":"amulet"}""", null, "Holy Symbol (amulet)")]
    [InlineData("""{"index":"holy-symbol","note":"amulet","url":"/not/an/api/url"}""", null, "holy-symbol (amulet)")]
    public void LinkWithNote_Reference_ShowsNameNoteAndRefWhereEachExists(string json, string? note, string expected)
    {
        using var document = JsonDocument.Parse(json);
        var reference = document.RootElement;

        var shown = note is null ? SrdMarkdownText.LinkWithNote(reference) : SrdMarkdownText.LinkWithNote(reference, note);

        Assert.Equal(expected, shown);
    }

    // Link is the note-free form: fields that list plain records (properties, categories) must not grow parentheses.
    [Fact]
    public void Link_ReferenceWithANote_LeavesTheNoteOut()
    {
        using var document = JsonDocument.Parse("""{"name":"Book","note":"Prayers","url":"/api/2024/equipment/book"}""");

        Assert.Equal("Book (`2024/equipment/book`)", SrdMarkdownText.Link(document.RootElement));
    }

    // Every formatter, and the fallback for a kind without one, words an empty record the same way, so a model (and a
    // test) can tell "the SRD has no text" from a formatter bug in one check.
    [Theory]
    [InlineData(SrdEdition.Edition2014, SrdKinds.Rule)]
    [InlineData(SrdEdition.Edition2024, SrdKinds.Condition)]
    [InlineData(SrdEdition.Edition2024, SrdKinds.Equipment)]
    [InlineData(SrdEdition.Edition2024, SrdKinds.MagicItem)]
    [InlineData(SrdEdition.Edition2024, "no-such-kind")]
    public void Body_RecordWithNoText_SaysNoDescriptionInTheOneSharedWording(string edition, string kind)
    {
        var doc = new SrdDocument { Edition = edition, Kind = kind, Slug = "empty", Name = "Empty", Json = "{}" };

        Assert.Equal("*No description in the SRD data.*", SrdMarkdownText.NoDescription);
        Assert.Equal(SrdMarkdownText.NoDescription, SrdMarkdown.Body(doc, VendoredSrdLookup.Instance));
    }
}
