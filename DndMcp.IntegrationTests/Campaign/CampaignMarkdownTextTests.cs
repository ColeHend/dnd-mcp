using DndMcp.Formatting.Campaign;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Invariant: the text guards every campaign result shares (<see cref="CampaignMarkdownText"/>) hold: the output cap keeps
/// a result under its ceiling with a note that says so, cutting at a line break unless that would drop most of the room,
/// then inside the line; every cut (the cap, an excerpt, an echo, a bounded session section) keeps whole characters; an echo
/// of user text shows control characters escaped; a table cell keeps its row whole.
///
/// <para>
/// Why it fails silently: each guard is used by seven tools and the resources, and each failure looks like ordinary output.
/// A cap that only cuts at line breaks turns a 40,000-character one-paragraph recap into "## Recap" and a note claiming the
/// output was cut at 24,000 characters; an unescaped escape sequence in an echoed action name reaches the client raw; a
/// "|" in a campaign name splits the campaign list's row into extra columns.
/// </para>
/// </summary>
public sealed class CampaignMarkdownTextTests
{
    private const string Note = "\n\n_Output cut at 1,000 characters; ask for less._";

    /// <summary>
    /// FH7 (C06): a single line longer than the room is cut inside itself, so the result still shows its start; before, the
    /// cap cut at the last line break before the room and dropped the whole line.
    /// </summary>
    [Fact]
    public void Cap_OneLineLongerThanTheRoom_IsCutInsideTheLine()
    {
        var markdown = "## Recap\n" + string.Concat(Enumerable.Repeat("the band played on ", 200));

        var capped = CampaignMarkdownText.Cap(markdown, "ask for less", max: 1_000);

        Assert.StartsWith("## Recap\nthe band played on the band played on", capped, StringComparison.Ordinal);
        Assert.EndsWith(Note, capped, StringComparison.Ordinal);
        Assert.True(capped.Length > 900, $"{capped.Length} characters: the long line was dropped");
        Assert.True(capped.Length <= 1_000, $"{capped.Length} characters");
    }

    /// <summary>A line break past half the room is still where the cut falls: whole lines (whole table rows) stay whole.</summary>
    [Fact]
    public void Cap_LineBreakPastHalfTheRoom_CutsThereAndKeepsLinesWhole()
    {
        var lines = Enumerable.Range(1, 100).Select(i => $"| row {i:D3} | {new string('x', 40)} |").ToList();
        var markdown = string.Join('\n', lines) + "\n";

        var capped = CampaignMarkdownText.Cap(markdown, "ask for less", max: 1_000);

        var kept = capped[..^Note.Length].Split('\n');
        Assert.All(kept, line => Assert.Contains(line, lines));
        Assert.EndsWith(Note, capped, StringComparison.Ordinal);
        Assert.True(capped.Length > 500 && capped.Length <= 1_000, $"{capped.Length} characters");
    }

    /// <summary>
    /// MR04: the threshold is half the room, on both sides. The two tests above put the last line break near the start of
    /// the room or near its end, so moving the threshold to a quarter or three quarters went unseen: at a quarter, a break
    /// at 40% of the room is cut there, dropping 60% of the room; at three quarters, a break at 60% is ignored and the cut
    /// lands mid-line, which the line rule exists to avoid for table rows. 40%: cut inside the long line; 60%: at the break.
    /// </summary>
    [Theory]
    [InlineData(0.4, false)]
    [InlineData(0.6, true)]
    public void Cap_LastLineBreakEitherSideOfHalfTheRoom_CutsThereOnlyPastHalf(double at, bool cutAtTheBreak)
    {
        var room = 1_000 - Note.Length;
        var head = (int)(room * at);
        var markdown = new string('a', head) + "\n" + new string('b', 2_000);

        var capped = CampaignMarkdownText.Cap(markdown, "ask for less", max: 1_000);

        Assert.EndsWith(Note, capped, StringComparison.Ordinal);
        Assert.Equal(cutAtTheBreak ? new string('a', head) : markdown[..room], capped[..^Note.Length]);
    }

    /// <summary>A cut inside a line never splits a surrogate pair: half an emoji is not text.</summary>
    [Fact]
    public void Cap_CutInsideALineAtASurrogatePair_KeepsWholeCharacters()
    {
        var room = 1_000 - Note.Length;
        var markdown = new string('a', room - 1) + string.Concat(Enumerable.Repeat("🎲", 100));

        var capped = CampaignMarkdownText.Cap(markdown, "ask for less", max: 1_000);

        var kept = capped[..^Note.Length];
        Assert.False(char.IsHighSurrogate(kept[^1]), "the cut left half a surrogate pair");
        Assert.Equal(room - 1, kept.Length);
    }

    /// <summary>
    /// The other cutters keep whole characters too (FH7): an excerpt (checklist values, the session page's live-log notes),
    /// an echo of user text, and the session pages' bounded recap and prep never end between the halves of a surrogate pair
    /// when the cut point falls inside an emoji. Each row puts the cut exactly there; a lone high surrogate is not text.
    /// </summary>
    [Theory]
    [InlineData("excerpt")]
    [InlineData("echo")]
    [InlineData("bounded")]
    public void Cutter_CutPointInsideASurrogatePair_KeepsWholeCharacters(string cutter)
    {
        var dice = string.Concat(Enumerable.Repeat("🎲", 50));
        var echoed = CampaignMarkdownText.MaxEchoLength - 1;

        var (cut, expected) = cutter switch
        {
            // An excerpt keeps max - 1 characters and adds "…": 398 letters, then the cut falls inside the first die.
            "excerpt" => (CampaignMarkdownText.Excerpt(new string('b', 398) + dice, 400), new string('b', 398) + "…"),
            "echo" => (CampaignMarkdownText.Echo(new string('e', echoed) + dice), new string('e', echoed) + "…"),
            // With no line break, Bounded keeps max characters, then says how much of how much it kept.
            _ => (SessionMarkdown.Bounded(new string('a', 399) + dice, 400, null), new string('a', 399) + "\n\n_… cut at 400 of 499 characters._"),
        };

        Assert.Equal(expected, cut);
    }

    [Fact]
    public void Cap_ResultThatFits_IsUntouched()
    {
        var markdown = "# Short\n" + new string('x', 900);

        Assert.Same(markdown, CampaignMarkdownText.Cap(markdown, "ask for less", max: 1_000));
    }

    /// <summary>
    /// Kills N03 (FH10, M19): user text echoed into a message has its control characters escaped, so a pasted escape
    /// sequence or bell is shown as text, not passed through into the result.
    /// </summary>
    [Theory]
    [InlineData("li\u001bst", "li\\u001bst")]
    [InlineData("bell\u0007", "bell\\u0007")]
    [InlineData("two\nlines", "two\\u000alines")]
    public void Echo_ControlCharacters_AreEscaped(string text, string echoed)
    {
        Assert.Equal(echoed, CampaignMarkdownText.Echo(text));
    }

    /// <summary>
    /// Kills N04 (FH10, M19): a "|" in user text is escaped inside a table cell, so it cannot split the row into extra
    /// columns; line breaks become spaces, and an empty cell shows a dash.
    /// </summary>
    [Theory]
    [InlineData("Pipe | World", "Pipe \\| World")]
    [InlineData("two\r\nlines", "two lines")]
    [InlineData("  ", "—")]
    public void Cell_UserText_StaysInsideItsCell(string text, string cell)
    {
        Assert.Equal(cell, CampaignMarkdownText.Cell(text));
    }
}
