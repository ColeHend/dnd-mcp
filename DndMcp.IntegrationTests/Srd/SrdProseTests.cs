using System.Text.Json;
using System.Text.RegularExpressions;
using DndMcp.Formatting.Srd;
using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.IntegrationTests.Srd;

/// <summary>
/// Invariant: <see cref="SrdProse.Join"/> changes only whitespace. It keeps a table's rows and a list's items on
/// consecutive lines, makes every other source line its own paragraph, and never cuts a sentence in two where upstream
/// merely wrapped a line (2024 Long Jump: "…up to your Strength score if you\nmove at least 10 feet…").
///
/// <para>
/// Every formatter runs SRD prose through this one joiner, so a regression here reshapes the text of every kind at once;
/// the whole-data tests below are what catch it.
/// </para>
/// </summary>
public sealed class SrdProseTests
{
    [Theory]
    // 2014 shape: a table split one row per element stays one table; the text around it stays separate.
    [InlineData(new[] { "Intro.", "| A | B |", "|---|---|", "| 1 | 2 |", "After." }, "Intro.\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\nAfter.")]
    [InlineData(new[] { "- one", "- two", "Text." }, "- one\n- two\n\nText.")]
    [InlineData(new[] { "1. one", "2) two" }, "1. one\n2) two")]
    // 2024 shape: single newlines between paragraphs, padded in magic items, tables touching the text around them.
    [InlineData(new[] { "Ring  \n Text one.  \n Text two." }, "Ring\n\nText one.\n\nText two.")]
    [InlineData(new[] { "Text:\n| A |\n|---|\n| 1 |\nMore." }, "Text:\n\n| A |\n|---|\n| 1 |\n\nMore.")]
    [InlineData(new[] { "First.\r\nSecond." }, "First.\n\nSecond.")]
    // A blank line ends a table or list even when the next line is of the same kind.
    [InlineData(new[] { "| A |\n\n| B |" }, "| A |\n\n| B |")]
    [InlineData(new[] { "- a", "", "- b" }, "- a\n\n- b")]
    // Different kinds never merge; emphasis and "1 - x" are text, not list items.
    [InlineData(new[] { "- item", "| row |" }, "- item\n\n| row |")]
    [InlineData(new[] { "*Mechanical trap*", "**Speed 0.** Text." }, "*Mechanical trap*\n\n**Speed 0.** Text.")]
    [InlineData(new[] { "1 - Disadvantage", "2 - Speed halved" }, "1 - Disadvantage\n\n2 - Speed halved")]
    [InlineData(new[] { "-5 penalty", "-" }, "-5 penalty\n\n-")]
    // A number with its punctuation and nothing after it is text: the list check must not read past the line's end.
    [InlineData(new[] { "1." }, "1.")]
    [InlineData(new[] { "12)" }, "12)")]
    [InlineData(new[] { "1.", "2." }, "1.\n\n2.")]
    [InlineData(new[] { "  ", "", "\n" }, "")]
    public void Join_SrdText_KeepsTablesAndListsTogetherAndEverythingElseApart(string[] paragraphs, string expected)
    {
        Assert.Equal(expected, SrdProse.Join(paragraphs));
    }

    [Theory]
    // A line that stops mid-sentence and a next line starting in lower case: one sentence wrapped, not two paragraphs.
    [InlineData("up to your Strength score if you\nmove at least 10 feet.", "up to your Strength score if you move at least 10 feet.")]
    [InlineData("from yourself at the  \n end of each turn.", "from yourself at the end of each turn.")]
    // A comma or an article cannot end a paragraph, whatever case the next word is in.
    [InlineData("Acrobatics, Intimidation, Perception,\nStealth, or Survival.", "Acrobatics, Intimidation, Perception, Stealth, or Survival.")]
    [InlineData("that doesn't require a\nReaction to cast.", "that doesn't require a Reaction to cast.")]
    [InlineData("roll an\nInitiative check.", "roll an Initiative check.")]
    [InlineData("ignore the\nDifficult Terrain.", "ignore the Difficult Terrain.")]
    // Several wrapped lines in a row make one paragraph.
    [InlineData("one line\nwraps and\nwraps again.\nNext paragraph.", "one line wraps and wraps again.\n\nNext paragraph.")]
    public void Join_TextLineWrappedMidSentence_ContinuesTheParagraph(string text, string expected)
    {
        Assert.Equal(expected, SrdProse.Join([text]));
    }

    [Theory]
    // A finished sentence, heading or label ends the paragraph even when the next line starts in lower case.
    [InlineData("It ends here.\nlower case starts a paragraph.")]
    [InlineData("A question?\nlower")]
    [InlineData("Shout!\nlower")]
    [InlineData("Choose one of the following:\nfirst option")]
    [InlineData("**Speed 0.**\nlower")]
    [InlineData("***Legendary Resistance.***\nlower")]
    [InlineData("He said \"stop.\"\nlower")]
    [InlineData("(See the table.)\nlower")]
    [InlineData("It uses **_Curse._**\nlower")]
    // An upper-case line after an unfinished one is taken for a heading or a new paragraph: "Wondrous Item" above the
    // text, "Arid Land" above its table.
    [InlineData("Wondrous Item\nYour Constitution is 19.")]
    [InlineData("Life Domain Spells\nCleric Level")]
    // "a", "an" and "the" count only as whole words.
    [InlineData("Draw a card from Deck A\nShuffle it back.")]
    [InlineData("Lathe\nTurn it.")]
    public void Join_TextLineAfterAFinishedOrHeadingLine_StartsANewParagraph(string text)
    {
        var lines = text.Split('\n');

        Assert.Equal(lines[0] + "\n\n" + lines[1], SrdProse.Join([text]));
    }

    [Theory]
    // 2014 keeps one paragraph per array element: an element boundary is a paragraph break in the source, never a wrap.
    [InlineData(new[] { "up to your Strength score if you", "move at least 10 feet." }, "up to your Strength score if you\n\nmove at least 10 feet.")]
    [InlineData(new[] { "Perception,", "Stealth." }, "Perception,\n\nStealth.")]
    // A blank line is a paragraph break too.
    [InlineData(new[] { "if you\n\nmove" }, "if you\n\nmove")]
    // Only text continues text: a list item or table row stays apart from the line after it, and the line before it.
    [InlineData(new[] { "- an item without a full stop\nlower-case text" }, "- an item without a full stop\n\nlower-case text")]
    [InlineData(new[] { "| cell |\nlower-case text" }, "| cell |\n\nlower-case text")]
    [InlineData(new[] { "Choose the\n- first" }, "Choose the\n\n- first")]
    public void Join_LineAfterAParagraphBreakOrNonTextLine_IsNeverJoinedToIt(string[] paragraphs, string expected)
    {
        Assert.Equal(expected, SrdProse.Join(paragraphs));
    }

    // The real records the review found cut mid-sentence: the glossary's Long Jump and 2024 class features.
    [Theory]
    [InlineData("2024/rule/long-jump", "a number of feet up to your Strength score if you move at least 10 feet immediately before the jump.")]
    [InlineData("2024/feature/barbarian-reckless-attack", "until the start of your next turn, but attack rolls against you have Advantage during that time.")]
    [InlineData("2024/feature/monk-self-restoration", "from yourself at the end of each of your turns: Charmed, Frightened, or Poisoned.\n\nIn addition,")]
    [InlineData("2024/feature/barbarian-primal-knowledge", "Acrobatics, Intimidation, Perception, Stealth, or Survival.")]
    [InlineData("2024/feature/cleric-divine-intervention", "that doesn't require a Reaction to cast.")]
    public void Join_RealRecordWrappedMidSentence_KeepsTheSentenceInOneParagraph(string reference, string expectedFragment)
    {
        var doc = VendoredSrdLookup.Instance.Require(reference);

        Assert.Contains(expectedFragment, SrdProse.Join(doc.Root.Description()), StringComparison.Ordinal);
    }

    // Every string in every record (descriptions, action and feature text, table cells), joined on its own: the words and
    // their order are upstream's, whatever Join did to the line breaks between them.
    [Fact]
    public void Join_EveryRealText_ChangesOnlyWhitespace()
    {
        var checkedTexts = 0;
        foreach (var (doc, text) in EveryMultiLineText())
        {
            Assert.True(
                Collapse(text) == Collapse(SrdProse.Join([text])),
                $"{doc.Ref}: Join changed more than whitespace in \"{Shorten(text)}\".");
            checkedTexts++;
        }

        Assert.True(checkedTexts > 900, $"Only {checkedTexts} multi-line texts found; the walk is broken.");
    }

    // No paragraph break that Join makes out of a single line break falls inside a sentence the way the review found them:
    // after a text line with no closing punctuation and before a lower-case word, or after a comma or a lone article.
    // Blank lines are upstream's own paragraph breaks and are kept even mid-sentence (2014 Wisdom has "make a\n\nWisdom
    // (Survival) check"), so each blank-line-separated piece of the source is checked on its own.
    [Fact]
    public void Join_EveryRealText_NeverBreaksAParagraphMidSentence()
    {
        var midSentenceBreak = new Regex(
            @"(?<=[^.!?:""'”’)\]*_\s])\n\n(?=[a-z])|(?<=,|\b(?:a|an|the))\n\n(?=[A-Za-z])",
            RegexOptions.CultureInvariant);

        foreach (var (doc, text) in EveryMultiLineText())
        {
            foreach (var sourceParagraph in Regex.Split(text, @"\n\s*\n"))
            {
                var joined = SrdProse.Join([sourceParagraph]);
                var match = midSentenceBreak.Match(joined);
                Assert.False(
                    match.Success,
                    $"{doc.Ref}: a paragraph break cuts a sentence at \"{Shorten(joined[Math.Max(0, match.Index - 40)..Math.Min(joined.Length, match.Index + 40)])}\".");
            }
        }
    }

    // Every line break Join turns into a space merges two source lines into one output line, so the drop in line count over
    // the vendored files (pinned by sha256, read here without the curated corrections) is the number of joins: the 17
    // wrapped sentences the review and a scan of every string found, and not one heading or label more.
    [Fact]
    public void Join_EveryVendoredText_JoinsExactlyTheSeventeenWrappedSentences()
    {
        var files = Directory
            .EnumerateFiles(Path.Combine(VendoredSrdLookup.ContentRoot, "5e-database"), "5e-SRD-*.json", SearchOption.AllDirectories)
            .Append(Path.Combine(VendoredSrdLookup.ContentRoot, "rules-glossary-2024.json"));

        var joins = 0;
        foreach (var file in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(file));
            foreach (var text in Strings(document.RootElement).Where(s => s.Contains('\n')))
            {
                joins += NonBlankLines(text) - NonBlankLines(SrdProse.Join([text]));
            }
        }

        Assert.Equal(17, joins);
    }

    private static int NonBlankLines(string text) => text.Split('\n').Count(line => !string.IsNullOrWhiteSpace(line));

    private static IEnumerable<(SrdDocument Doc, string Text)> EveryMultiLineText()
    {
        foreach (var doc in VendoredSrdLookup.Instance.All())
        {
            foreach (var text in Strings(doc.Root).Where(s => s.Contains('\n')))
            {
                yield return (doc, text);
            }
        }
    }

    private static IEnumerable<string> Strings(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => [element.GetString()!],
        JsonValueKind.Array => element.EnumerateArray().SelectMany(Strings),
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p => Strings(p.Value)),
        _ => [],
    };

    private static string Collapse(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    private static string Shorten(string text) => (text.Length > 120 ? text[..120] + "…" : text).Replace("\n", "\\n");
}
