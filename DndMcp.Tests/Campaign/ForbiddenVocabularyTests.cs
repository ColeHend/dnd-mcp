using DndMcp.Domain.Campaign;
using Xunit;

namespace DndMcp.Tests.Campaign;

/// <summary>
/// Invariant: forbidden vocabulary is found as whole words, ignoring case, diacritics and apostrophes, with English
/// inflections on the last word only (seal → seals, Sealed, SEALING; never sealskin or unsealed), multi-word terms as word
/// sequences, <c>&lt;number&gt;</c> as digits or number words, and the longest match winning where terms overlap. These are
/// the One Piece "no one says seal" rows 1-5 and the Belmakor "no name, no timespan" rule (rows 41-42).
/// </summary>
public sealed class ForbiddenVocabularyTests
{
    private static readonly ForbiddenRule SealRule = new("f:1", ["seal"], [], ["shell", "wrapping", "what keeps it in"], "Reveal order");

    private static readonly ForbiddenRule OldKingRule = new("rule:old-king-no-name-no-timespan", ["Keras"], ["<number>-year-old", "<number> years"], [], null);

    [Fact]
    public void Scan_Row1WhiteLinesDialogue_FindsExactlyOneSeal()
    {
        var hits = ForbiddenVocabulary.Scan("The Peaceful One turns the fruit: \"See the white lines? That's the seal on it.\"", [SealRule]);

        var hit = Assert.Single(hits);
        Assert.Equal("seal", hit.TermOrPattern);
        Assert.Equal("seal", hit.Surface);
        Assert.Same(SealRule, hit.Rule);
        Assert.Equal(["shell", "wrapping", "what keeps it in"], hit.Rule.PreferredTerms);
    }

    [Theory]
    // Rows 2-5, and the inflections the contract names.
    [InlineData("The seals are breaking", "seals")]
    [InlineData("Sealed tight", "Sealed")]
    [InlineData("SEALING the breach", "SEALING")]
    [InlineData("Not bind it. Not seal it.", "seal")]
    [InlineData("in a sealed palanquin", "sealed")]
    [InlineData("a sealer of doors", "sealer")]
    [InlineData("the sealers came", "sealers")]
    [InlineData("the seal's edge", "seal's")]
    [InlineData("the “Seal” itself", "Seal")]
    public void Scan_SealInflections_AreFound(string text, string surface)
    {
        var hit = Assert.Single(ForbiddenVocabulary.Scan(text, [SealRule]));

        Assert.Equal(surface, hit.Surface);
        Assert.Equal(surface, text.Substring(hit.Start, hit.Length));
    }

    [Theory]
    [InlineData("The white lines are a shell — what keeps it in.")]
    [InlineData("a sealskin coat")]
    [InlineData("the jar was unsealed")]
    [InlineData("sea lions")]
    [InlineData("")]
    public void Scan_NotTheWord_FindsNothing(string text)
    {
        Assert.Empty(ForbiddenVocabulary.Scan(text, [SealRule]));
    }

    [Theory]
    [InlineData("win", "the wind rose", false)]
    [InlineData("ban", "the band played", false)]
    [InlineData("tim", "hard times", false)]
    [InlineData("bake", "baked", true)]
    [InlineData("bake", "baking", true)]
    [InlineData("bake", "baker", true)]
    [InlineData("box", "boxes", true)]
    [InlineData("spy", "spies", true)]
    [InlineData("spy", "spied", true)]
    [InlineData("stab", "stabbed", true)]
    [InlineData("stab", "stabbing", true)]
    [InlineData("seal", "sealsealing", false)]
    public void Scan_Inflection_FollowsTheEnglishRulesAndNoMore(string term, string text, bool found)
    {
        var rule = new ForbiddenRule("r", [term], [], [], null);

        Assert.Equal(found, ForbiddenVocabulary.Scan(text, [rule]).Count == 1);
    }

    [Theory]
    [InlineData("a nine-hundred-year-old sorcerer", "nine-hundred-year-old", "<number>-year-old")]
    [InlineData("a 900-year-old sorcerer", "900-year-old", "<number>-year-old")]
    [InlineData("a 1,000-year-old grudge", "1,000-year-old", "<number>-year-old")]
    [InlineData("he waited four hundred years", "four hundred years", "<number> years")]
    [InlineData("four hundred and twenty years", "four hundred and twenty years", "<number> years")]
    [InlineData("a thousand years of silence", "a thousand years", "<number> years")]
    [InlineData("ninety-nine years", "ninety-nine years", "<number> years")]
    [InlineData("for 400 years", "400 years", "<number> years")]
    [InlineData("Old king, Keras, come down", "Keras", "Keras")]
    [InlineData("Keras's crown", "Keras's", "Keras")]
    public void Scan_BelmakorNoNameNoTimespan_FlagsTheNameAndAnyTimespan(string text, string surface, string termOrPattern)
    {
        var hit = Assert.Single(ForbiddenVocabulary.Scan(text, [OldKingRule]));

        Assert.Equal(surface, hit.Surface);
        Assert.Equal(termOrPattern, hit.TermOrPattern);
    }

    [Theory]
    [InlineData("an old king")]
    [InlineData("many years ago")]
    [InlineData("a year-old grudge")]
    [InlineData("and years went by")]
    [InlineData("a thousand-yard stare")]
    public void Scan_NoNumberOrName_IsNotFlagged(string text)
    {
        Assert.Empty(ForbiddenVocabulary.Scan(text, [OldKingRule]));
    }

    [Fact]
    public void Scan_MultiWordTerm_MatchesTheWordSequenceWithTheLastWordInflected()
    {
        var rule = new ForbiddenRule("r", ["Axiom Cage"], [], [], null);

        Assert.Equal(["Axiom Cage", "axiom cages", "Axiom-Cage"],
            ForbiddenVocabulary.Scan("the Axiom Cage, two axiom cages, the Axiom-Cage", [rule]).Select(h => h.Surface));
        Assert.Empty(ForbiddenVocabulary.Scan("the axioms caged", [rule]));
        Assert.Empty(ForbiddenVocabulary.Scan("the cage axiom", [rule]));
    }

    [Fact]
    public void Scan_OverlappingTerms_LongestWinsAndTheScanResumesAfterIt()
    {
        var rule = new ForbiddenRule("r", ["seal", "the seal of Baal"], [], [], null);

        var hits = ForbiddenVocabulary.Scan("They broke the seal of Baal, then another seal.", [rule]);

        Assert.Equal(["the seal of Baal", "seal"], hits.Select(h => h.Surface));
        Assert.Equal(["the seal of Baal", "seal"], hits.Select(h => h.TermOrPattern));
    }

    [Fact]
    public void Scan_TwoTermsOfOneRuleStartingAtOneWord_TheLongerWins()
    {
        var rule = new ForbiddenRule("r", ["seal", "seal of Baal"], [], [], null);

        var hits = ForbiddenVocabulary.Scan("the seal of Baal, then another seal", [rule]);

        Assert.Equal(["seal of Baal", "seal"], hits.Select(h => h.Surface));
    }

    [Fact]
    public void Scan_TwoRulesWithTheSameLongestMatch_BothAreReported()
    {
        var other = new ForbiddenRule("rule:no-seal", ["seal"], [], ["lid"], null);

        var hits = ForbiddenVocabulary.Scan("the seal", [SealRule, other]);

        Assert.Equal(["f:1", "rule:no-seal"], hits.Select(h => h.Rule.Source));
    }

    [Fact]
    public void Scan_ShorterRuleInsideALongerRulesMatch_IsNotReportedThere()
    {
        var longer = new ForbiddenRule("rule:long", ["seal of Baal"], [], [], null);

        var hits = ForbiddenVocabulary.Scan("the seal of Baal", [SealRule, longer]);

        Assert.Equal("rule:long", Assert.Single(hits).Rule.Source);
    }

    [Theory]
    [InlineData("Björn", "BJORN's axe", "BJORN's")]
    [InlineData("Bjorn", "Björn Mountainfell", "Björn")]
    [InlineData("Nadar's axe", "Nadar’s Axe", "Nadar’s Axe")]
    [InlineData("naïve", "a NAIVE plan", "NAIVE")]
    public void Scan_DiacriticsCaseAndApostrophes_AreIgnored(string term, string text, string surface)
    {
        var hit = Assert.Single(ForbiddenVocabulary.Scan(text, [new ForbiddenRule("r", [term], [], [], null)]));

        Assert.Equal(surface, hit.Surface);
    }

    [Fact]
    public void Scan_HitsAreInTextOrderWithPositions()
    {
        const string text = "Keras sealed it 900 years ago.";

        var hits = ForbiddenVocabulary.Scan(text, [SealRule, OldKingRule]);

        Assert.Equal(["Keras", "sealed", "900 years"], hits.Select(h => h.Surface));
        Assert.Equal([0, 6, 16], hits.Select(h => h.Start));
    }

    [Fact]
    public void Scan_UnusableStoredTermsAndPatterns_AreSkippedNotThrown()
    {
        var rule = new ForbiddenRule("r", ["", "<x>", "seal"], ["<number>", "<bogus> years", "a < b"], [], null);

        Assert.Equal("seal", Assert.Single(ForbiddenVocabulary.Scan("the seal, 9 years", [rule])).Surface);
    }

    [Theory]
    [InlineData("<number> years", true)]
    [InlineData("<n>-year-old", true)]
    [InlineData("< Number > winters", true)]
    [InlineData("<number>", false)]
    [InlineData("<count> years", false)]
    [InlineData("years>", false)]
    [InlineData("one two three four five six seven eight nine", false)]
    public void IsUsablePattern_PlaceholdersAndLength_AreChecked(string pattern, bool usable)
    {
        Assert.Equal(usable, ForbiddenVocabulary.IsUsablePattern(pattern, out var problem));
        Assert.Equal(usable, problem.Length == 0);
    }
}
