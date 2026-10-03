using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Invariant: understand-belmakor.md §3 rows 36-46, the diegetic checks, through <c>campaign_knowledge check</c> on the
/// world the tools built, read as the author reads them: "Old king, come down" passes; "the Axiom Cage" in a lyric is
/// another name for what Belmakor calls "the thing he wants" (other_name) and a name the party never heard; "Keras" is an
/// author alias, a name from the other campaign and a word the no-name rule forbids; "a nine-hundred-year-old sorcerer"
/// breaks the rule's timespan pattern; his ambition sung aloud is a secret at risk, while the oblique "this could be a
/// kingdom" line is only listed for review; the author is never flagged; the elegy for Tristan passes for Belmakor, who
/// was there.
///
/// <para>
/// Why it fails silently: the check is the one guard between a draft and the table. A flag that dropped to "to review"
/// (or a reveal rule the write path stored where the check does not read it) would let the song name the Cage and still
/// print "pass", and nothing else would notice.
/// </para>
/// </summary>
public sealed class ScenarioBelmakorCheckTests : IClassFixture<ScenarioBelmakorWorld>
{
    private const string HaulTheCage = "We'll haul the Axiom Cage back up to the old king";
    private const string DeadWorld = "I'll take back the ground below and make the dead world a kingdom";
    private const string Kingdom = "I thought, Lord, this could be a kingdom / If somebody stayed around.";
    private const string NothingToFlag = "author view: nothing to flag — pass perspective to check a character's, the party's or the public's knowledge";
    private const string Author = "_Author-facing: this names what the speaker must not say; never paste it into the draft._\n";
    private const string NoFlags = "## Hard flags\nNone: every name is one the speaker uses and the audience knows, no forbidden words, no secret at risk.\n";

    private readonly ScenarioBelmakorWorld _w;

    public ScenarioBelmakorCheckTests(ScenarioBelmakorWorld world)
    {
        _w = world;
    }

    /// <summary>Every row's verdict as the title states it: "pass" exactly when the golden says the text passes.</summary>
    [Theory]
    [InlineData(36, "Old king, come down", "character:belmakor", true, "pass")]
    [InlineData(37, "Old king came down to a knee", "character:belmakor", true, "pass")]
    [InlineData(38, HaulTheCage, "character:belmakor", true, "2 hard flags")]
    [InlineData(39, HaulTheCage, "party", true, "2 hard flags")]
    [InlineData(40, HaulTheCage, "author", false, NothingToFlag)]
    [InlineData(41, "Keras, come down", "character:belmakor", true, "3 hard flags")]
    [InlineData(42, "a nine-hundred-year-old sorcerer", "character:belmakor", true, "1 hard flag")]
    [InlineData(43, DeadWorld, "character:belmakor", true, "1 hard flag")]
    [InlineData(44, DeadWorld, "character:belmakor", false, "pass")]
    [InlineData(45, Kingdom, "character:belmakor", true, "pass")]
    [InlineData(46, "Tristan went down protecting Sky", "character:belmakor", true, "pass")]
    public async Task Check_Rows36To46_TheTitleGivesTheGoldensVerdict(int row, string text, string speaker, bool diegetic, string verdict)
    {
        var result = await Check(text, speaker, diegetic);

        Assert.True(result.StartsWith($"# Knowledge check: {verdict} (belmakor)\n", StringComparison.Ordinal), $"row {row}:\n{result}");
        Assert.Equal(verdict is "pass" or NothingToFlag, result.Contains("\n## Hard flags\nNone:", StringComparison.Ordinal));
    }

    /// <summary>Rows 36 and 37: "old king" is the name Belmakor uses and the party knows; no flag, and the name is read as his.</summary>
    [Theory]
    [InlineData(36, "Old king, come down")]
    [InlineData(37, "Old king came down to a knee")]
    public async Task Check_Rows36And37TheOldKingByTheNameBelmakorUses_Passes(int row, string text)
    {
        var result = await Check(text, "character:belmakor", diegetic: true);

        Assert.True(result.StartsWith("# Knowledge check: pass (belmakor)\n\nSpeaker: character:belmakor · diegetic, audience: party.\n" + Author + "\n" + NoFlags,
            StringComparison.Ordinal), $"row {row}:\n{result}");
        Assert.EndsWith("\nNames used as the speaker uses them: \"Old king\" → character:old-king.\n", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rows 38 and 39: "Axiom Cage" in a lyric, by Belmakor or by the party (an Ignis-fronted song), is the author alias of
    /// the thing the speaker calls "the thing he wants" (other_name), a name from the other campaign, and a name the
    /// audience does not use; the fact it is about is listed as unknown to the speaker, recorded unaware; "old king" passes.
    /// </summary>
    [Theory]
    [InlineData(38, "character:belmakor")]
    [InlineData(39, "party")]
    public async Task Check_Rows38And39AxiomCageInALyric_FlagsOtherNameAndTheAudience(int row, string speaker)
    {
        var result = await Check(HaulTheCage, speaker, diegetic: true);

        Assert.True(result.Contains(
            "\n## Hard flags (2)\n" +
            "- **other_name** \"Axiom Cage\": item:thing-he-wants (The thing the old king wants; author alias): " + speaker + " knows it as \"the thing he wants\"; " +
            "one-piece/item:axiom-cage (The Axiom Cage; its name): a name from campaign one-piece; nobody here knows it.\n" +
            "- **reveals_to_audience** \"Axiom Cage\": item:thing-he-wants: party knows it as \"the thing he wants\"; one-piece/item:axiom-cage: party does not know it.\n\n",
            StringComparison.Ordinal), $"row {row}:\n{result}");
        Assert.Contains($"\n- {_w.AxiomFact} \"The thing the old king wants fetched is the Axiom Cage.\": unaware (recorded) · known by author · why: about item:thing-he-wants\n",
            result, StringComparison.Ordinal);
        Assert.EndsWith("\nNames used as the speaker uses them: \"old king\" → character:old-king.\n", result, StringComparison.Ordinal);
    }

    /// <summary>Row 40: the same lyric from the author, not diegetic, has no flag at all (the author may use any name and word).</summary>
    [Fact]
    public async Task Check_Row40TheSameLyricAsTheAuthor_FlagsNothing()
    {
        var result = await Check(HaulTheCage, "author", diegetic: false);

        Assert.StartsWith($"# Knowledge check: {NothingToFlag} (belmakor)\n\nSpeaker: author.\n" + Author +
                          "\n## Hard flags\nNone: every name is one the speaker uses, no forbidden words, no secret at risk.\n", result, StringComparison.Ordinal);
        Assert.DoesNotContain("**", result[..result.IndexOf("## To review", StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    /// <summary>
    /// Row 41: "Keras" is the old king's author alias (Belmakor knows him as the Old King), One Piece's Keras, a word the
    /// no-name reveal rule forbids (with its preferred wording and note), and a name the party does not use; f:2 (his name)
    /// is listed as unknown to Belmakor.
    /// </summary>
    [Fact]
    public async Task Check_Row41KerasComeDown_FlagsTheAliasTheOtherCampaignTheRuleAndTheAudience()
    {
        var result = await Check("Keras, come down", "character:belmakor", diegetic: true);

        Assert.Contains(
            "\n## Hard flags (3)\n" +
            "- **other_name** \"Keras\": character:old-king (The Old King; author alias): character:belmakor knows it as \"The Old King\"; " +
            "one-piece/character:keras (Keras; its name): a name from campaign one-piece; nobody here knows it.\n" +
            "- **forbidden** \"Keras\": rule:old-king-no-name-no-timespan forbids it; say instead: \"an old king\"; note: Belmakor never learns his name or age.\n" +
            "- **reveals_to_audience** \"Keras\": character:old-king: party knows it as \"The Old King\"; one-piece/character:keras: party does not know it.\n\n",
            result, StringComparison.Ordinal);
        Assert.Contains($"\n- {_w.NameFact} \"{ScenarioBelmakorWorld.NameStatement}\": unaware (recorded) · known by author, dm · why: about character:old-king\n",
            result, StringComparison.Ordinal);
    }

    /// <summary>Row 42: the reveal rule's timespan pattern catches a spelled-out age, and nothing else in the line is a flag.</summary>
    [Fact]
    public async Task Check_Row42ANineHundredYearOldSorcerer_FlagsTheRevealRulesTimespan()
    {
        var result = await Check("a nine-hundred-year-old sorcerer", "character:belmakor", diegetic: true);

        Assert.Contains(
            "\n## Hard flags (1)\n" +
            "- **forbidden** \"nine-hundred-year-old\" (matches \"<n>-year-old\"): rule:old-king-no-name-no-timespan forbids it; say instead: \"an old king\"; " +
            "note: Belmakor never learns his name or age.\n\n",
            result, StringComparison.Ordinal);
    }

    /// <summary>
    /// Row 43: his ambition in a song is a secret at risk: he knows it, the party is recorded not knowing it, and a lyric
    /// is a public statement.
    /// </summary>
    [Fact]
    public async Task Check_Row43HisAmbitionSung_IsASecretAtRisk()
    {
        var result = await Check(DeadWorld, "character:belmakor", diegetic: true);

        Assert.Contains(
            $"\n## Hard flags (1)\n- **secret at risk** {_w.AmbitionFact} \"{ScenarioBelmakorWorld.AmbitionStatement}\": character:belmakor knows it; " +
            "party: does not know (recorded). The text touches it (shares words with the text); a song or speech is a public statement.\n\n",
            result, StringComparison.Ordinal);
    }

    /// <summary>Row 44: the same text in his private journal: he knows it, so it is neither unknown nor at risk, only related.</summary>
    [Fact]
    public async Task Check_Row44HisAmbitionNotDiegetic_IsOnlyARelatedFactHeKnows()
    {
        var result = await Check(DeadWorld, "character:belmakor", diegetic: false);

        Assert.Contains($"\n- {_w.AmbitionFact} \"{ScenarioBelmakorWorld.AmbitionStatement}\": knows · why: shares words with the text\n", result, StringComparison.Ordinal);
        Assert.Contains("\n## Hard flags\nNone: every name is one the speaker uses, no forbidden words, no secret at risk.\n", result, StringComparison.Ordinal);
        Assert.DoesNotContain("**secret at risk**", result, StringComparison.Ordinal);
        Assert.DoesNotContain("does not know", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// Row 45: the catalog's oblique line has no hard flag; the ambition is listed as a secret about the speaker to keep
    /// out, so the author judges whether the line is oblique enough.
    /// </summary>
    [Fact]
    public async Task Check_Row45TheObliqueKingdomLine_PassesAndListsTheAmbitionToKeepOut()
    {
        var result = await Check(Kingdom, "character:belmakor", diegetic: true);

        Assert.Contains("\n" + NoFlags, result, StringComparison.Ordinal);
        Assert.Contains(
            $"\n**Secrets about the speaker (not in the text; keep them out)** (1)\n- {_w.AmbitionFact} \"{ScenarioBelmakorWorld.AmbitionStatement}\": " +
            "party: does not know (recorded)\n", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// Row 46: Belmakor was present in session 1, so Tristan's death is known to him and the elegy passes; in Serif's
    /// mouth (absent from session 1) the same line leaves the death uncertain, for review.
    /// </summary>
    [Fact]
    public async Task Check_Row46TristansElegy_PassesForBelmakorAndIsUncertainForSerif()
    {
        var belmakor = await Check("Tristan went down protecting Sky", "character:belmakor", diegetic: true);
        var serif = await Check("Tristan went down protecting Sky", "character:serif", diegetic: false);

        Assert.Contains($"\n- {_w.TristanFact} \"Tristan died on the ocean job, dragged into the deep by the void octopus while protecting Sky.\": knows · " +
                        "why: about character:tristan\n", belmakor, StringComparison.Ordinal);
        Assert.EndsWith("\nNames used as the speaker uses them: \"Tristan\" → character:tristan.\n", belmakor, StringComparison.Ordinal);
        Assert.StartsWith("# Knowledge check: pass (belmakor)\n", serif, StringComparison.Ordinal);
        Assert.Contains($"\n**Facts character:serif has no record of knowing** (1)\n- {_w.TristanFact} \"Tristan died on the ocean job, dragged into the deep by the void octopus " +
                        "while protecting Sky.\": uncertain: the party learned it in S1; Serif was absent · known by party · why: about character:tristan\n",
            serif, StringComparison.Ordinal);
    }

    private Task<string> Check(string text, string speaker, bool diegetic) => _w.Call("campaign_knowledge", $$"""
        {"campaign": "belmakor", "action": "check", "perspective": "{{speaker}}", "text": {{System.Text.Json.JsonSerializer.Serialize(text)}},
         "diegetic": {{(diegetic ? "true" : "false")}}}
        """);
}

/// <summary>
/// Invariant: understand-belmakor.md §3 rows 1-19, who knows what under which name and as of when, as the author reads
/// them in <c>campaign_knowledge ledger</c> on the world the tools built: each cell is that perspective's verdict in the
/// ledger's own wording, which keeps apart an explicit "unaware", a "no record" (never worded as "does not know"), an
/// attendance-based "uncertain" (Serif absent, Aiden not listed), and a point-in-time answer.
///
/// <para>
/// Why it fails silently: every row here is decided by something one tool wrote and another reads: an attendance row
/// written by <c>campaign_session record_past</c>, a party row with its learned session written by
/// <c>campaign_knowledge record</c>, a visibility change filed under session 2 by <c>campaign_write</c>. If a tool
/// dropped one of them (a missing attendance row read as present), Serif would "know" how Tristan died, and a song
/// checked for him would pass.
/// </para>
/// </summary>
public sealed class ScenarioBelmakorLedgerTests : IClassFixture<ScenarioBelmakorWorld>
{
    private readonly ScenarioBelmakorWorld _w;

    public ScenarioBelmakorLedgerTests(ScenarioBelmakorWorld world)
    {
        _w = world;
    }

    /// <summary>
    /// Rows 1-16. Row 6 is pinned as the design answers it (contract §3.3): the author's verdict is always "knows" with no
    /// name of its own, so the author column reads "knows"; the author's names for the two ("Keras", "the Axiom Cage") are
    /// the author's own knowledge rows, shown on the author's page (row 31's test).
    /// </summary>
    [Theory]
    [InlineData(1, "axiom", "character:belmakor", "unaware")]
    [InlineData(2, "item:thing-he-wants", "character:belmakor", "aware as “the thing he wants” (S3)")]
    [InlineData(3, "character:old-king", "character:belmakor", "met as “the old king” (S3)")]
    [InlineData(4, "name", "character:belmakor", "unaware")]
    [InlineData(5, "axiom", "party", "unaware")]
    [InlineData(5, "item:thing-he-wants", "party", "aware as “the thing he wants” (S3)")]
    [InlineData(5, "character:old-king", "party", "met as “the old king” (S3)")]
    [InlineData(6, "axiom", "author", "knows")]
    [InlineData(6, "name", "author", "knows")]
    [InlineData(7, "name", "dm", "knows")]
    [InlineData(8, "axiom", "dm", "no record")]
    [InlineData(9, "errand", "character:ignis", "knows (S3)")]
    [InlineData(10, "item:thing-he-wants", "character:ignis", "aware as “the thing he wants” (S3)")]
    [InlineData(11, "tristan", "character:serif", "uncertain: the party learned it in S1; Serif was absent")]
    [InlineData(12, "tristan", "character:aiden-ironstar", "uncertain: the party learned it in S1; no attendance recorded for Aiden Ironstar in S1")]
    [InlineData(13, "tristan", "character:belmakor", "knows (S1)")]
    [InlineData(14, "ambition", "character:belmakor", "knows")]
    [InlineData(15, "ambition", "character:vars", "unaware")]
    [InlineData(16, "ambition", "party", "unaware")]
    [InlineData(16, "ambition", "public", "no record")]
    public async Task Ledger_Rows1To16_EachCellIsTheGoldensVerdictInTheLedgersWording(int row, string target, string perspective, string expected)
    {
        var cell = await Cell(target, perspective, asOf: null);

        Assert.True(cell == expected, $"row {row}: {target} for {perspective} reads \"{cell}\", not \"{expected}\"");
    }

    /// <summary>
    /// Rows 17-19, point in time: the table learned of the Contingency in session 2 (and the fact was made party-visible in
    /// that session), so as of session 1 it has no record of it and as of session 2 it knows; Belmakor always knew it (his
    /// row has no session).
    /// </summary>
    [Theory]
    [InlineData(17, "table", 1, "no record")]
    [InlineData(18, "table", 2, "knows (S2)")]
    [InlineData(19, "character:belmakor", 1, "knows")]
    public async Task Ledger_Rows17To19TheContingencyAsOfASession_FollowsTheSessionsItWasLearnedIn(int row, string perspective, int asOf, string expected)
    {
        var cell = await Cell("contingency", perspective, asOf);

        Assert.True(cell == expected, $"row {row}: as of session {asOf}, {perspective} reads \"{cell}\", not \"{expected}\"");
    }

    /// <summary>Row 8's wording rule: "no record" never claims the dm (or the public) does not know; nobody wrote it down.</summary>
    [Theory]
    [InlineData("axiom", "dm")]
    [InlineData("ambition", "public")]
    public async Task Ledger_Rows8And16NoRecord_NeverSaysDoesNotKnow(string target, string perspective)
    {
        var cell = await Cell(target, perspective, asOf: null);

        Assert.DoesNotContain("not know", cell, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unaware", cell, StringComparison.OrdinalIgnoreCase);
    }

    // One cell of the author's ledger: an entity handle as a row of about, a fact (by its golden name) as a row of facts.
    private async Task<string> Cell(string target, string perspective, int? asOf)
    {
        var handle = target switch
        {
            "axiom" => _w.AxiomFact,
            "name" => _w.NameFact,
            "errand" => _w.ErrandFact,
            "ambition" => _w.AmbitionFact,
            "contingency" => _w.ContingencyFact,
            "tristan" => _w.TristanFact,
            _ => target,
        };
        var rows = handle.StartsWith("f:", StringComparison.Ordinal) ? $"\"facts\": [\"{handle}\"]" : $"\"about\": [\"{handle}\"]";
        var ledger = await _w.Call("campaign_knowledge", $$"""
            {"campaign": "belmakor", "action": "ledger", {{rows}}, "perspectives": ["{{perspective}}"]{{(asOf is { } n ? $", \"as_of_session\": {n}" : string.Empty)}}}
            """);
        return ScenarioLeak.LedgerCells(ledger)[handle];
    }
}
