using DndMcp.Domain.Campaign;
using Xunit;

namespace DndMcp.Tests.Campaign;

/// <summary>
/// Invariant: the scanners' word splitter folds a word exactly as <see cref="CampaignText.Key"/> folds a name (so a name
/// and its mention compare equal), keeps each word's span in the original text, treats apostrophes as part of the word
/// and a comma between digits as part of the number.
/// </summary>
public sealed class CampaignWordsTests
{
    [Theory]
    [InlineData("The Old King's errand", "the|old|kings|errand")]
    [InlineData("Björn Mountainfell", "bjorn|mountainfell")]
    [InlineData("nine-hundred-year-old", "nine|hundred|year|old")]
    [InlineData("1,000 years, 2 ships", "1000|years|2|ships")]
    [InlineData("rock'n'roll", "rocknroll")]
    [InlineData("Ｆｕｌｌ ﬁne", "full|fine")]
    [InlineData("zero​width", "zerowidth")]
    [InlineData("", "")]
    [InlineData("—!?", "")]
    public void Split_Text_FoldsLikeCampaignTextKey(string text, string keys)
    {
        var words = CampaignWords.Split(text);

        Assert.Equal(keys, string.Join('|', words.Select(w => w.Key)));
        Assert.Equal(CampaignText.Key(text).Replace(" ", "", StringComparison.Ordinal), string.Concat(words.Select(w => w.Key)).Replace(",", "", StringComparison.Ordinal));
    }

    [Fact]
    public void Split_Spans_CoverTheWordAsWritten()
    {
        const string text = "  Nadar’s AXE, Björn!";

        var words = CampaignWords.Split(text);

        Assert.Equal(["Nadar’s", "AXE", "Björn"], words.Select(w => text[w.Start..w.End]));
        Assert.Equal([true, true, true], words.Select(w => w.Capitalised));
        Assert.False(CampaignWords.Split("björn")[0].Capitalised);
    }

    /// <summary>
    /// FD7 (review U02): the words of a name that give it away on their own, for the read path's partial-name flag ("haul
    /// your Cage back" sings a word of "Axiom Cage"): at least four letters, and not a function word, a sentence opener
    /// or another common English word that names nothing alone (thing, wants, sent, third). Keys, in name order, once.
    /// </summary>
    [Theory]
    [InlineData("Axiom Cage", "axiom|cage")]
    [InlineData("the Axiom Cage", "axiom|cage")]
    [InlineData("the Cage", "cage")]
    [InlineData("The Old King", "king")]
    [InlineData("The thing the old king wants", "king")]
    [InlineData("what the sorcerer king sent us for", "sorcerer|king")]
    [InlineData("Morwen Vashkar", "morwen|vashkar")]
    [InlineData("The Masked Duke", "masked|duke")]
    [InlineData("Isle of Craftsmen", "isle|craftsmen")]
    [InlineData("Temple of the Moon", "temple|moon")]
    [InlineData("Nadar's Axe", "nadar")]
    [InlineData("Third Silence", "silence")]
    [InlineData("Björn Mountainfell", "bjorn|mountainfell")]
    [InlineData("Mar-Terin", "terin")]
    [InlineData("That's Life", "life")]
    // An apostrophe name whose first part is a stoplisted word is still distinctive (review of FD5).
    [InlineData("House Do'Urden", "house|dourden")]
    [InlineData("No'Ruk", "noruk")]
    [InlineData("the Cage of the Cage", "cage")]
    [InlineData("Keras", "keras")]
    [InlineData("R2D2 Unit 1138", "unit")]
    [InlineData("The Old One", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void DistinctiveWords_Name_KeepsTheWordsThatGiveItAwayAlone(string? name, string expected)
    {
        Assert.Equal(expected.Length == 0 ? [] : expected.Split('|'), CampaignWords.DistinctiveWords(name));
    }

    [Fact]
    public void Split_CombiningMarkAfterALetter_StaysInTheWordSpan()
    {
        const string text = "Björn rides";

        var word = CampaignWords.Split(text)[0];

        Assert.Equal("bjorn", word.Key);
        Assert.Equal("Björn", text[word.Start..word.End]);
    }
}
