using DndMcp.Domain.Campaign;
using Xunit;

namespace DndMcp.Tests.Campaign;

/// <summary>
/// Invariant (heuristic, pinned on representative text): runs of capitalised words are proper-noun candidates, with
/// of/the/de/von-style connectors inside; a common word opening a sentence or line is dropped; "I" never is one; each
/// candidate appears once, by name key, in order of first appearance.
/// </summary>
public sealed class ProperNounsTests
{
    [Theory]
    [InlineData("We sailed to the Isle of Craftsmen at dawn.", "Isle of Craftsmen")]
    [InlineData("They met Björn Mountainfell there.", "Björn Mountainfell")]
    [InlineData("a shrine to the Temple of the Moon", "Temple of the Moon")]
    [InlineData("Keras came down.", "Keras")]
    [InlineData("When Keras came, we ran.", "Keras")]
    [InlineData("The Old King sent us off.", "Old King")]
    [InlineData("the party met Nadar's herald", "Nadar")]
    [InlineData("a deal with G.O.D.S. Co. went bad", "G.O.D.S. Co")]
    [InlineData("the crowd chanted Silverwind twice", "Silverwind")]
    public void Candidates_Prose_FindsTheName(string text, string expected)
    {
        Assert.Equal([expected], ProperNouns.Candidates(text));
    }

    [Theory]
    [InlineData("I thought, Lord, this could be a kingdom / If somebody stayed around.", "Lord")]
    [InlineData("Old king, come down\nCome down to a knee", "")]
    [InlineData("The seals are breaking. Then the lines go dark.", "")]
    [InlineData("I'm here and I'll stay.", "")]
    [InlineData("and so the end of the story", "")]
    // A lone common word is dropped mid-sentence too (a line break the punctuation did not show).
    [InlineData("we sailed on and Then it sank", "")]
    [InlineData("the drums, the bass, And the crowd", "")]
    [InlineData("she sang \"Rock on, Silverwind\" twice", "Rock|Silverwind")]
    public void Candidates_LyricsAndCommonWords_SkipSentenceStartsAndI(string text, string expected)
    {
        var candidates = ProperNouns.Candidates(text);

        Assert.Equal(expected.Length == 0 ? [] : expected.Split('|'), candidates);
    }

    /// <summary>
    /// FD5 (review U04): an ordinary word that opens a sentence is not a name. The session checklist listed "Afterwards"
    /// and "Meanwhile" as unknown names to strike from the recap (a model rewrote the user's recap to clear one), the check
    /// listed "Worse" as a possible invention, and the smoke run "That" (from "That's", whose key is "thats").
    /// </summary>
    [Theory]
    [InlineData("The party beat the king. Afterwards he sent them away. Meanwhile the ship burned.")]
    [InlineData("Worse, the ship sank. Later we drank. Finally the bell rang. Suddenly, silence.")]
    [InlineData("Eventually it ended. Instead we sang. Still, it rained. Soon it stopped. Once more. Now go.")]
    [InlineData("Then we ran. Also, we hid. Besides, nobody saw. Unfortunately it saw us. Luckily it was blind.")]
    [InlineData("Somehow we lived. Anyway, onward. Yet it came. Thus it ended. Hence the song. Therefore we sing.")]
    [InlineData("Moreover, it burned. Nevertheless we stayed. Otherwise we drown. Perhaps not. Maybe so.")]
    [InlineData("Apparently it worked. Clearly not. Obviously so. Fortunately, no. Ultimately, yes.")]
    [InlineData("Initially calm. Originally a mill. Previously a ruin. Recently a tavern.")]
    [InlineData("Tonight we ride. Today we rest. Yesterday we fled. Tomorrow we fight.")]
    [InlineData("That ended it. This is it. These fell. Those ran. What now? Which way? Who knows? Why not? How so?")]
    [InlineData("Where to? There it is. Here we are. Afterward we slept. However, it woke. Indeed it did.")]
    [InlineData("That's the end of it.")]
    [InlineData("It's over. What's left? There's nothing. Here's the thing. Where's the boat? Who's there? How's that?")]
    [InlineData("Didn't we sail? Couldn't stop. Doesn't matter. Isn't it? Won't stop. Can't stay. Ain't right. Shan't go.")]
    [InlineData("We'll see. They're gone. You'd know. She's here. He'd go. It'll pass. That'll do. Let's go. Y'all wait.")]
    [InlineData("'Tis done. 'Twas night. 'Cause we can. 'Til dawn. O'er the hills.")]
    [InlineData("Needn't go. Mightn't stay. Thats it. Everyone's here.")]
    [InlineData("That’s the end. It’s over.")]
    // "n't" after a stoplisted word that is not itself listed as a contraction ("oughtnt" is not; "ought" is).
    [InlineData("Oughtn't we go? Shouldn't we stay? Wouldn't it be grand?")]
    [InlineData("EVERYONE'S HERE. NOBODY'D KNOW.")]
    // Openers that are also words a name may be made of stay common with no article in front.
    [InlineData("Behold the king! Farewell, old friend. Within the walls we waited. One more round.")]
    public void Candidates_SentenceOpeningWordsAndContractions_AreNotNames(string text)
    {
        Assert.Empty(ProperNouns.Candidates(text));
    }

    /// <summary>
    /// Review UR5: the rest of the sentence-opening adverbs are common words too. A run's check listed "Somewhere" (the
    /// class of the stoplisted "Somehow") as a possible invention, and the model told the user the checker was wrong; the
    /// session checklist listed it as a name to add. Participles stay candidates ("Defeated, we went home", below).
    /// </summary>
    [Theory]
    [InlineData("Somewhere a bell rang. Nowhere to run. Everywhere the sky. Anywhere but here. Elsewhere, silence.")]
    [InlineData("Somewhat tired, we slept. Altogether a good night. Second, we ate. Third, we left.")]
    [InlineData("Carefully we climbed. Silently we waited.")]
    [InlineData("Overall it held. Together we sang. Again it rang. Almost home. Already gone. Always so. Never again. Sometimes not.")]
    [InlineData("Often yes. Usually no. Rarely so. Seldom seen. Afterward we slept. Beforehand we ate. Meanwhile it burned.")]
    [InlineData("Nonetheless we went. Regardless, it rained. Accordingly we left. Consequently it fell. Indeed it did. Instead we sang.")]
    [InlineData("Likewise the drums. Similarly the bass. Additionally a horn. Furthermore a bell. Next we ran. Last we hid. First we ate.")]
    [InlineData("Lastly we slept. Briefly, no. Honestly, yes. Frankly, no. Hopefully not. Sadly so. Thankfully not.")]
    [InlineData("Unsurprisingly it fell. Surprisingly it held. Naturally. Certainly not. Probably so. Possibly. Definitely.")]
    [InlineData("Quickly we ran. Slowly we walked. Quietly we sang. Gradually it rose. Immediately it fell. Abruptly it stopped.")]
    public void Candidates_SentenceOpeningAdverbs_AreNotNames(string text)
    {
        Assert.Empty(ProperNouns.Candidates(text));
    }

    [Theory]
    [InlineData("That's Keras. Afterwards Serif left.", "Keras|Serif")]
    // A participle is an open class, and a world names things after them: it stays a candidate (review UR5's decision).
    // A stoplisted adverb inside a run that does not open the sentence is part of the name.
    [InlineData("Somewhere a bell rang. Defeated, we went home.", "Defeated")]
    [InlineData("we sang Somewhere Beyond the Sea", "Somewhere Beyond the Sea")]
    [InlineData("Meanwhile Vars Nocturne sang.", "Vars Nocturne")]
    [InlineData("Worse, Nadar's axe broke.", "Nadar")]
    [InlineData("O'Brien laughed; Keras's crown fell.", "O'Brien|Keras")]
    [InlineData("Afterwards the Isle of Craftsmen burned.", "Isle of Craftsmen")]
    [InlineData("we sang of Later Days", "Later Days")]
    // Bare words a world may name things after stay candidates; only the clipped "'Cause" is "because", and not when it
    // is quoted in apostrophes.
    [InlineData("we joined the Cause and dug the Mine", "Cause|Mine")]
    [InlineData("'Cause the Cause called", "Cause")]
    [InlineData("a faction called 'Cause'", "Cause")]
    public void Candidates_NamesBesideSentenceOpenersAndContractions_AreStillFound(string text, string expected)
    {
        Assert.Equal(expected.Split('|'), ProperNouns.Candidates(text));
    }

    /// <summary>
    /// Review of FD5: the contraction rule reads the word in front of an apostrophe only before a contraction's ending
    /// ('s, 'd, 'll, 're, 've, 'm, n't), so a name whose first part is a stoplisted word ("Do", "An", "No", "So") is still
    /// a name; the base code found these and FD5 had dropped them. A name missed here is an invention that becomes canon
    /// unnoticed.
    /// </summary>
    [Theory]
    [InlineData("We met the house of Do'Urden.", "Do'Urden")]
    [InlineData("Do'Urden laughed.", "Do'Urden")]
    [InlineData("they feared No'Ruk and So'Lar", "No'Ruk|So'Lar")]
    [InlineData("we sailed with An'Kahet", "An'Kahet")]
    [InlineData("That's An'Kahet's ship.", "An'Kahet")]
    public void Candidates_ApostropheNameWhoseFirstPartIsACommonWord_IsStillAName(string text, string expected)
    {
        Assert.Equal(expected.Split('|'), ProperNouns.Candidates(text));
    }

    /// <summary>
    /// Review of FD5: an article marks what follows as a name, stoplisted or not. "The Within", "the One", "the Well" are
    /// a fantasy world's places and powers (the class keeps "Below" and "Watch" off the stoplist for the same reason),
    /// while the same words open sentences with no article in front and stay common there.
    /// </summary>
    [Theory]
    [InlineData("the Within stirred; the Beyond waited", "Within|Beyond")]
    [InlineData("The Within stirred.", "Within")]
    [InlineData("Then the One came down.", "One")]
    [InlineData("we drank from the Well and sailed for an Otherwise", "Well|Otherwise")]
    [InlineData("Within the walls the First waited.", "First")]
    [InlineData("we sailed with the Second Fleet to the Elsewhere", "Second Fleet|Elsewhere")]
    public void Candidates_StoplistedWordAfterAnArticle_IsAName(string text, string expected)
    {
        Assert.Equal(expected.Split('|'), ProperNouns.Candidates(text));
    }

    /// <summary>
    /// The one rule for "a candidate matches no name" (review UR01), shared by the session checklist and the knowledge
    /// check: a candidate is known when it is a name (article optional) or a whole-word run inside one, or holds names and
    /// nothing beside them but connectors, titles ("Captain Belmakor"), stoplisted words ("Old Belmakor") and the opening
    /// word of a run that opens a sentence or line ("Hail Belmakor").
    /// Any other word beside a name was typed with its capital on purpose, and the candidate is a new name ("Belmakor
    /// Shadowfang", "Old King Zanzibar", "we met Rolf of the Crumbling Statue", "And Rolf …" once "And" is dropped); read
    /// as "holds a name, so known", the check stopped listing them (UR01's recheck). Part of a word is not a name's word,
    /// and a name the list does not have is unknown. A sentence-opening word before a name stays known, even a new name
    /// ("Rolf of the Crumbling Statue sang."): nothing tells it from "Sing of the Crumbling Statue".
    /// </summary>
    [Theory]
    [InlineData("Belmakor sang.", "")]
    [InlineData("We met the Old King.", "")]
    [InlineData("Hail Belmakor, hail the band.", "")]
    [InlineData("Hail Belmakor! Hail Belmakor!", "")]
    [InlineData("Sing of the Crumbling Statue.", "")]
    [InlineData("Old king,\nBeware the Crumbling Statue", "")]
    [InlineData("Rolf of the Crumbling Statue sang.", "")]
    [InlineData("We sang for the King. Silverwind smiled.", "")]
    [InlineData("We praised the Old King's Belmakor.", "")]
    [InlineData("We met Captain Belmakor, then Lord Belmakor of the Crumbling Statue.", "")]
    [InlineData("We met Old Belmakor.", "")]
    [InlineData("...", "")]
    [InlineData("we cried Hail Belmakor", "Hail Belmakor")]
    [InlineData("Hail Belmakor! Then we cried Hail Belmakor.", "Hail Belmakor")]
    [InlineData("We met Rolf of the Crumbling Statue.", "Rolf of the Crumbling Statue")]
    [InlineData("And Rolf of the Crumbling Statue sang.", "Rolf of the Crumbling Statue")]
    [InlineData("Then Old King Zanzibar spoke.", "Old King Zanzibar")]
    [InlineData("We drank with Belmakor Shadowfang.", "Belmakor Shadowfang")]
    [InlineData("Belmakor Shadowfang sang.", "Belmakor Shadowfang")]
    [InlineData("We sat in the Crumbling Statue's Shadow.", "Crumbling Statue's Shadow")]
    [InlineData("We met Kin. Silver rang.", "Kin|Silver")]
    [InlineData("Hail Zanzibar, King Zanzibar.", "Hail Zanzibar|King Zanzibar")]
    public void KnownNames_Unknown_ListsTheCandidatesThatAreNoNameAndHoldNoNameAlone(string text, string unknown)
    {
        var names = new KnownNames(["Belmakor Silverwind", "Belmakor", "The Old King", "Crumbling statue", " "]);

        Assert.Equal(unknown.Length == 0 ? [] : unknown.Split('|'), names.Unknown(text));
    }

    /// <summary>
    /// A run opens a sentence when its first word is the text's, or follows a sentence end, a line break or an opening
    /// quote, everywhere the text has it, and nothing was dropped from its front (KnownNames reads its first capital as
    /// maybe the sentence's): "And Rolf" without the stoplisted "And" is a capital typed on purpose.
    /// </summary>
    [Theory]
    [InlineData("Hail Belmakor, hail the band.", true)]
    [InlineData("We sang.\nHail Belmakor", true)]
    [InlineData("we cried Hail Belmakor", false)]
    [InlineData("And Hail Belmakor rang out.", false)]
    [InlineData("Hail Belmakor! We cried Hail Belmakor.", false)]
    public void Runs_FirstWordOfASentence_OpensItEverywhereOrNot(string text, bool opens)
    {
        var run = Assert.Single(ProperNouns.Runs(text), r => r.Surface == "Hail Belmakor");

        Assert.Equal(opens, run.OpensSentence);
    }

    [Fact]
    public void Candidates_RepeatedName_AppearsOnceInFirstAppearanceOrder()
    {
        var candidates = ProperNouns.Candidates("Serif met Ignis. Ignis met SERIF. Then Vars Nocturne arrived with Serif.");

        Assert.Equal(["Serif", "Ignis", "Vars Nocturne"], candidates);
    }

    [Fact]
    public void Candidates_ConnectorAtTheEnd_IsNotPartOfTheName()
    {
        Assert.Equal(["Isle"], ProperNouns.Candidates("to the Isle of despair"));
    }

    [Fact]
    public void Candidates_CommaBetweenCapitalisedWords_SplitsTheRun()
    {
        Assert.Equal(["Belmakor", "Ignis", "Vars"], ProperNouns.Candidates("with Belmakor, Ignis, Vars and the rest"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nothing capitalised here")]
    public void Candidates_NoCapitals_IsEmpty(string? text)
    {
        Assert.Empty(ProperNouns.Candidates(text));
    }
}
