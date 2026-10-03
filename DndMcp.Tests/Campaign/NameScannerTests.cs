using DndMcp.Domain.Campaign;
using Xunit;

namespace DndMcp.Tests.Campaign;

/// <summary>
/// Invariant: the name scanner finds every known name in text the way people write it: case, diacritics and apostrophes
/// ignored, a leading article optional on either side, a possessive or plural s on the last word, whole words only, the
/// longest name winning where names overlap, and every entry that shares the matched name reported (the caller decides
/// between two entities called the same).
/// </summary>
public sealed class NameScannerTests
{
    private static readonly NameScanner Belmakor = new(
    [
        new NameEntry("The Old King", "character:old-king"),
        new NameEntry("the sorcerer king", "character:old-king (alias)"),
        new NameEntry("Keras", "character:old-king (author alias)"),
        new NameEntry("The thing the old king wants", "item:thing-he-wants"),
        new NameEntry("the thing he wants", "item:thing-he-wants (alias)"),
        new NameEntry("Axiom Cage", "item:thing-he-wants (author alias)"),
        new NameEntry("The old king's errand", "thread:old-kings-errand"),
        new NameEntry("Belmakor Silverwind", "character:belmakor"),
        new NameEntry("Belmakor", "character:belmakor (alias)"),
        new NameEntry("Björn Mountainfell", "character:bjorn"),
        new NameEntry("Björn", "character:bjorn (alias)"),
    ]);

    [Theory]
    [InlineData("Old king, come down", "Old king", "character:old-king")]
    [InlineData("Old king came down to a knee", "Old king", "character:old-king")]
    [InlineData("THE OLD KING laughed", "OLD KING", "character:old-king")]
    [InlineData("the old king's errand took us south", "old king's errand", "thread:old-kings-errand")]
    [InlineData("We'll haul the Axiom Cage back up", "Axiom Cage", "item:thing-he-wants (author alias)")]
    [InlineData("Keras, come down", "Keras", "character:old-king (author alias)")]
    [InlineData("Bjorn swings", "Bjorn", "character:bjorn (alias)")]
    [InlineData("BJÖRN MOUNTAINFELL's axe", "BJÖRN MOUNTAINFELL's", "character:bjorn")]
    [InlineData("the sorcerer kings fell", "sorcerer kings", "character:old-king (alias)")]
    [InlineData("a sorcerer king", "sorcerer king", "character:old-king (alias)")]
    public void Scan_OneName_IsFoundAsWritten(string text, string matched, string payload)
    {
        var hit = Assert.Single(Belmakor.Scan(text));

        Assert.Equal(matched, hit.Matched);
        Assert.Equal(matched, text.Substring(hit.Start, hit.Length));
        Assert.Equal(payload, Assert.Single(hit.Entries).Payload);
    }

    [Fact]
    public void Scan_TwoNamesInALyric_AreBothFoundInOrder()
    {
        var hits = Belmakor.Scan("We'll haul the Axiom Cage back up to the old king");

        Assert.Equal(["Axiom Cage", "old king"], hits.Select(h => h.Matched));
    }

    [Fact]
    public void Scan_LongerNameStartingWithAShorterOne_WinsAndHidesTheShorter()
    {
        var hits = Belmakor.Scan("Belmakor Silverwind sings; Belmakor bows");

        Assert.Equal(["Belmakor Silverwind", "Belmakor"], hits.Select(h => h.Matched));
        Assert.Equal(["character:belmakor", "character:belmakor (alias)"], hits.Select(h => h.Entries.Single().Payload));
    }

    [Theory]
    [InlineData("the kingdom of old")]
    [InlineData("an old kingfisher")]
    [InlineData("Kerasine oil")]
    [InlineData("the thing")]
    [InlineData("")]
    public void Scan_PartialWordsAndFragments_AreNotNames(string text)
    {
        Assert.Empty(Belmakor.Scan(text));
    }

    [Fact]
    public void Scan_TwoEntitiesWithTheSameName_AreBothReported()
    {
        var scanner = new NameScanner([new NameEntry("Serif", 1), new NameEntry("serif", 2), new NameEntry("The Serif", 3)]);

        var hit = Assert.Single(scanner.Scan("Serif laughed"));

        Assert.Equal([1, 2, 3], hit.Entries.Select(e => (int)e.Payload));
    }

    [Fact]
    public void Scan_ExactLastWordBeatsAPluralReading()
    {
        var scanner = new NameScanner([new NameEntry("King", "king"), new NameEntry("Kings", "kings")]);

        Assert.Equal("kings", Assert.Single(Assert.Single(scanner.Scan("the Kings arrived")).Entries).Payload);
        Assert.Equal("king", Assert.Single(Assert.Single(scanner.Scan("the King arrived")).Entries).Payload);
    }

    [Fact]
    public void Scan_NameThatIsOnlyAnArticle_IsKeptAsItself()
    {
        var scanner = new NameScanner([new NameEntry("The", "the"), new NameEntry("!!!", "nothing")]);

        Assert.Equal("the", Assert.Single(Assert.Single(scanner.Scan("The end")).Entries).Payload);
    }

    [Fact]
    public void Scan_EveryTextPosition_IsMatchedAtMostOnce()
    {
        var hits = Belmakor.Scan("the old king's errand, the old king, the thing he wants and the thing the old king wants");

        Assert.Equal(["old king's errand", "old king", "thing he wants", "thing the old king wants"], hits.Select(h => h.Matched));
        for (var i = 1; i < hits.Count; i++)
        {
            Assert.True(hits[i].Start >= hits[i - 1].Start + hits[i - 1].Length);
        }
    }
}
