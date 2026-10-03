using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DndMcp.Formatting.Campaign;
using DndMcp.IntegrationTests.Infrastructure;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tools;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Invariant: <c>campaign_knowledge</c> writes knowledge through the real server with the same batch, undo hint, gate
/// warnings and consequences as <c>campaign_write</c>; its <c>check</c> renders for the author with the hard flags first
/// (names the speaker does not use, forbidden words, names the audience does not know) and everything retrieved for
/// judgement after them; its <c>ledger</c> is a table whose cells keep "no record", "not met", "not in play" and an
/// explicit "unaware" apart; and an argument the action does not take is refused, never dropped.
///
/// <para>
/// Why it fails silently: the repository services can be right while the text the model reads is wrong. A check that
/// printed its to-review list before the hard flags would bury "Axiom Cage is an author alias" under word overlaps; a
/// ledger that printed every "no" alike would hide the difference between a secret still holding and a session nobody
/// recorded; a <c>text</c> sent with <c>reveal</c> that vanished would let the model report a check it never ran. The
/// fixture is the Belmakor shape (the old king known by his party name, an author alias, an unaware fact, a
/// no-name-no-timespan reveal rule) built through the tools themselves.
/// </para>
/// </summary>
public sealed class CampaignKnowledgeToolTests : IClassFixture<McpServerHarness>
{
    private readonly McpServerHarness _server;

    public CampaignKnowledgeToolTests(McpServerHarness server)
    {
        _server = server;
    }

    [Fact]
    public async Task ToolSurface_CampaignKnowledge_HasTheContractsAnnotationsTitleAndDescribedSchema()
    {
        var tool = await CampaignWriteSetup.ToolAsync(_server, "campaign_knowledge");
        var annotations = tool.ProtocolTool.Annotations!;

        // Destructive: retract deletes rows (contract §9's row).
        Assert.Equal((false, true, false, false),
            (annotations.ReadOnlyHint!.Value, annotations.DestructiveHint!.Value, annotations.IdempotentHint!.Value, annotations.OpenWorldHint!.Value));
        Assert.Equal("Who knows what", tool.ProtocolTool.Title);
        CampaignWriteSetup.AssertDescribedAtEveryDepth("campaign_knowledge", tool.JsonSchema.GetProperty("properties"));
        Assert.Equal(new[] { "action" }, tool.JsonSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public async Task ToolDescription_CampaignKnowledge_NamesEveryArgumentAndEachActionsArgumentsWithinTheLimit()
    {
        var tool = await CampaignWriteSetup.ToolAsync(_server, "campaign_knowledge");
        var description = tool.Description!;

        Assert.True(description.Length <= CampaignWriteSetup.DescriptionLimit, $"{description.Length} characters");
        foreach (var property in tool.JsonSchema.GetProperty("properties").EnumerateObject())
        {
            Assert.Contains(property.Name, description, StringComparison.Ordinal);
        }

        foreach (var action in new[] { "- record: targets", "- reveal: facts", "- retract: targets", "- check: text", "- ledger: about" })
        {
            Assert.Contains(action, description, StringComparison.Ordinal);
        }

        Assert.EndsWith("\nExample: " + CampaignKnowledgeTools.Example, description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_TheDescriptionsExample_RunsAsWritten()
    {
        await CampaignWriteSetup.WithServerAsync(async server =>
        {
            await KnowledgeScenario.BuildAsync(server, slug: "belmakor");
            using var example = JsonDocument.Parse(CampaignKnowledgeTools.Example);
            var arguments = example.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());

            var text = server.SuccessText(await server.Client.CallToolAsync("campaign_knowledge", arguments));

            Assert.StartsWith("# Knowledge check: pass (belmakor)\n", text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CallTool_Record_PrintsOneRowPerKnowerWithTheBatchAndUndoCall()
    {
        var scenario = await KnowledgeScenario.BuildAsync(_server);

        var text = await CampaignWriteSetup.CallAsync(_server, "campaign_knowledge", $$"""
            {"campaign": "{{scenario.Slug}}", "action": "record", "targets": ["{{scenario.ErrandFact}}", "item:the-thing-he-wants"],
             "knowers": [{"who": "table", "state": "knows"}, {"who": "public", "state": "heard"}], "reason": "Told at the fair"}
            """);

        var id = Regex.Match(text, "Batch `([0-9a-f-]{36})`").Groups[1].Value;
        Assert.StartsWith($"# campaign_knowledge record: 4 rows ({scenario.Slug})\n\nBatch `{id}`. To undo it: campaign_history {{\"action\": \"undo\", \"batch_id\": \"{id}\", " +
                          $"\"campaign\": \"{scenario.Slug}\"}}.\n", text, StringComparison.Ordinal);
        Assert.Contains("| Target | Knower | Outcome | Changed |\n|---|---|---|---|\n", text, StringComparison.Ordinal);
        Assert.Contains($"| {scenario.ErrandFact} | table | created | state |", text, StringComparison.Ordinal);
        Assert.Contains($"| {scenario.ErrandFact} | public | created | state |", text, StringComparison.Ordinal);
        Assert.Contains("| item:the-thing-he-wants | public | created | state |", text, StringComparison.Ordinal);
        Assert.True(text.Length < 2_000, $"A typical record result is {text.Length} characters.");
    }

    [Fact]
    public async Task CallTool_RevealOfAGatedFact_IsAppliedWithTheUnmetConditionsAndTheSecretsNewStatus()
    {
        var gate = await GateScenario.BuildAsync(_server);

        var text = await CampaignWriteSetup.CallAsync(_server, "campaign_knowledge",
            $$"""{"campaign": "{{gate.Slug}}", "action": "reveal", "facts": ["{{gate.Seal}}"], "how": "told"}""");

        Assert.StartsWith($"# campaign_knowledge reveal: 1 row ({gate.Slug})\n\nBatch `", text, StringComparison.Ordinal);
        Assert.Contains($"| {gate.Seal} | party | created |", text, StringComparison.Ordinal);
        Assert.Contains("## Warnings (2)\nApplied anyway; read them before the table does.\n", text, StringComparison.Ordinal);
        Assert.Contains($"- **warning** · gate (after) · {gate.Seal} → party: {gate.Seal} reached party before its gate's after is met: {gate.Axe} not in play yet (applied anyway).\n" +
                        "  - Gate note: Not before the axe.\n", text, StringComparison.Ordinal);
        Assert.Contains($"- **warning** · gate (with) · {gate.Seal} → party: {gate.Seal} must land with {gate.Nadar}", text, StringComparison.Ordinal);
        // The gate's note is printed once, under its first warning.
        Assert.Single(Regex.Matches(text, "Gate note: "));
        Assert.Contains("## Consequences (1)\n- secret status: secret:fruits-are-the-seal: hidden → revealed.\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_DryRunReveal_ShowsTheSameWarningsLabelledAsADryRunAndWritesNothing()
    {
        var gate = await GateScenario.BuildAsync(_server);
        var call = $$"""{"campaign": "{{gate.Slug}}", "action": "reveal", "facts": ["{{gate.Seal}}"], "dry_run": true}""";

        var preview = await CampaignWriteSetup.CallAsync(_server, "campaign_knowledge", call);
        var ledger = await CampaignWriteSetup.CallAsync(_server, "campaign_knowledge",
            $$"""{"campaign": "{{gate.Slug}}", "action": "ledger", "facts": ["{{gate.Seal}}"], "perspectives": ["party"]}""");

        Assert.StartsWith($"# Dry run: campaign_knowledge reveal, 1 row ({gate.Slug}): nothing written\n\n**Dry run: nothing was written and no batch exists.**", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("campaign_history", preview, StringComparison.Ordinal);
        Assert.Contains("- **warning** · gate (after) ·", preview, StringComparison.Ordinal);
        Assert.Contains("- **warning** · gate (with) ·", preview, StringComparison.Ordinal);
        Assert.Contains($"| {gate.Seal} Eating a fruit breaks the seal. | no record |", ledger, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_RevealOfASecret_WritesItsGatedFactsAndAwarenessOfTheSecret()
    {
        var gate = await GateScenario.BuildAsync(_server);

        var text = await CampaignWriteSetup.CallAsync(_server, "campaign_knowledge",
            $$"""{"campaign": "{{gate.Slug}}", "action": "reveal", "secret": "secret:fruits-are-the-seal", "to": ["party"]}""");

        Assert.Contains($"| {gate.Seal} | party | created |", text, StringComparison.Ordinal);
        Assert.Contains("| secret:fruits-are-the-seal | party | created |", text, StringComparison.Ordinal);
        Assert.Contains("- **warning** · author visibility:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_Retract_DeletesTheRowAndWarnsOfTheOneThatWasNotThere()
    {
        var scenario = await KnowledgeScenario.BuildAsync(_server);

        var text = await CampaignWriteSetup.CallAsync(_server, "campaign_knowledge", $$"""
            {"campaign": "{{scenario.Slug}}", "action": "retract", "targets": ["character:the-old-king"], "who": ["character:belmakor", "table"]}
            """);

        Assert.Contains("| character:the-old-king | character:belmakor | deleted | knowledge |", text, StringComparison.Ordinal);
        Assert.Contains("| character:the-old-king | table | unchanged | — |", text, StringComparison.Ordinal);
        Assert.Contains("- **warning** · nothing to do: table has no row on character:the-old-king; nothing to retract.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_CheckOfALineTheSpeakerMayUse_PassesAndListsTheContextToReview()
    {
        // Belmakor row 36: "Old king, come down" in Belmakor's mouth, sung to the party.
        var scenario = await KnowledgeScenario.BuildAsync(_server);

        var text = await Check(scenario.Slug, "Old king, come down", "character:belmakor", diegetic: true);

        Assert.StartsWith($"# Knowledge check: pass ({scenario.Slug})\n\nSpeaker: character:belmakor · diegetic, audience: party.\n" +
                          "_Author-facing: this names what the speaker must not say; never paste it into the draft._\n\n" +
                          "## Hard flags\nNone: every name is one the speaker uses and the audience knows, no forbidden words, no secret at risk.\n", text, StringComparison.Ordinal);
        Assert.Contains($"- {scenario.ErrandFact} \"The old king wants the thing brought up from below.\": knows · why: about character:the-old-king", text, StringComparison.Ordinal);
        Assert.Contains("Names used as the speaker uses them: \"Old king\" → character:the-old-king.", text, StringComparison.Ordinal);
        Assert.True(text.Length < 4_000, $"A typical check is {text.Length} characters.");
    }

    [Fact]
    public async Task CallTool_CheckOfALineWithAnAuthorAliasAndAForbiddenTimespan_PutsEveryHardFlagFirstInTheContractsOrder()
    {
        // Belmakor rows 38, 41, 42 in one line: an author alias, the true name, and the rule's timespan pattern.
        var scenario = await KnowledgeScenario.BuildAsync(_server);

        var text = await Check(scenario.Slug, "We'll haul the Axiom Cage back up to Keras, the nine-hundred-year-old sorcerer.", "character:belmakor", diegetic: true);

        Assert.StartsWith($"# Knowledge check: 6 hard flags ({scenario.Slug})\n", text, StringComparison.Ordinal);
        var flags = text[text.IndexOf("## Hard flags (6)\n", StringComparison.Ordinal)..text.IndexOf("## To review", StringComparison.Ordinal)];
        Assert.Equal(
            "## Hard flags (6)\n" +
            "- **other_name** \"Axiom Cage\": item:the-thing-he-wants (The thing he wants; author alias): character:belmakor knows it as \"The thing he wants\".\n" +
            "- **other_name** \"Keras\": character:the-old-king (The Old King; author alias): character:belmakor knows it as \"The Old King\".\n" +
            "- **forbidden** \"Keras\": rule:no-name-no-timespan forbids it; say instead: \"the old king\"; note: Belmakor never learns his name or age.\n" +
            "- **forbidden** \"nine-hundred-year-old\" (matches \"<number>-year-old\"): rule:no-name-no-timespan forbids it; say instead: \"the old king\"; note: Belmakor never learns his name or age.\n" +
            "- **reveals_to_audience** \"Axiom Cage\": item:the-thing-he-wants: party knows it as \"The thing he wants\".\n" +
            "- **reveals_to_audience** \"Keras\": character:the-old-king: party knows it as \"The Old King\".\n\n",
            flags);
        Assert.Contains($"- {scenario.AxiomFact} \"The thing he wants is the Axiom Cage.\": unaware (recorded) · known by nobody on record · why: about item:the-thing-he-wants", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_CheckOfAPlannedFactsWords_ListsItAsNotInPlayForReviewWithoutFailing()
    {
        var scenario = await KnowledgeScenario.BuildAsync(_server);

        var text = await Check(scenario.Slug, "Old king, your army marches.", "party", diegetic: false);

        Assert.StartsWith($"# Knowledge check: pass ({scenario.Slug})\n\nSpeaker: party.\n", text, StringComparison.Ordinal);
        Assert.Contains($"**Facts party has no record of knowing** (1)\n- {scenario.PlannedFact} \"The old king's army marches next spring.\": not in play (planned) · known by nobody on record", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Belmakor row 40: the author may use any name and any word, so the check has nothing to flag, and says that rather
    /// than "pass" (review U10): a draft full of secrets "passing" an author check read as safe to a model that named no
    /// speaker. The title says how to check a character instead.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("author")]
    public async Task CallTool_CheckFromTheAuthor_SaysThereIsNothingToFlagAndHowToCheckAView(string? perspective)
    {
        var scenario = await KnowledgeScenario.BuildAsync(_server);

        var text = await Check(scenario.Slug, "We'll haul the Axiom Cage back up to Keras, the nine-hundred-year-old sorcerer.", perspective, diegetic: null);

        Assert.StartsWith($"# Knowledge check: author view: nothing to flag — pass perspective to check a character's, the party's or the " +
                          $"public's knowledge ({scenario.Slug})\n\nSpeaker: author.\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("**forbidden**", text, StringComparison.Ordinal);
        Assert.DoesNotContain("# Knowledge check: pass", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The review section of facts the speaker is not shown is titled "has no record of knowing" (review U15, contract
    /// §3.3): it lists "no record" facts, and titled "does not know" it asserted what nobody recorded (Nadar's own plan).
    /// </summary>
    [Fact]
    public async Task CallTool_CheckOfAFactNothingRecordsTheSpeakerKnowing_NeverSaysTheSpeakerDoesNotKnowIt()
    {
        await CampaignWriteSetup.WithServerAsync(async server =>
        {
            await CampaignWriteSetup.CallAsync(server, "campaign", """{"action": "create", "name": "One Piece", "slug": "op", "role": "dm", "ruleset": "2024"}""");
            await CampaignWriteSetup.WriteAsync(server, "op", """
                [{"op": "upsert", "kind": "character", "name": "Nadar", "visibility": "restricted"},
                 {"op": "fact", "statement": "Routing fruits to the party is part of Nadar's plan.", "about": ["character:nadar"]}]
                """);

            var text = await CampaignWriteSetup.CallAsync(server, "campaign_knowledge",
                """{"campaign": "op", "action": "check", "perspective": "character:nadar", "text": "My plan is working."}""");

            Assert.Contains("\n**Facts character:nadar has no record of knowing** (1)\n- f:1 \"Routing fruits to the party is part of Nadar's plan.\": no record",
                text, StringComparison.Ordinal);
            Assert.DoesNotContain("does not know", text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CallTool_CheckWithAnAudienceButNotDiegetic_IsRefused()
    {
        var scenario = await KnowledgeScenario.BuildAsync(_server);

        var text = await CampaignWriteSetup.FailAsync(_server, "campaign_knowledge",
            $$"""{"campaign": "{{scenario.Slug}}", "action": "check", "text": "x", "diegetic": false, "audience": "public"}""");

        Assert.Equal("An error occurred invoking 'campaign_knowledge': audience is who hears a diegetic text; with diegetic false there is no " +
                     "audience. Set diegetic true, or leave audience out.", text);
    }

    [Fact]
    public async Task CallTool_Ledger_IsATableThatKeepsNoRecordUnawareNotMetAndNotInPlayApart()
    {
        var scenario = await KnowledgeScenario.BuildAsync(_server);

        var text = await CampaignWriteSetup.CallAsync(_server, "campaign_knowledge", $$"""
            {"campaign": "{{scenario.Slug}}", "action": "ledger", "about": ["character:the-old-king", "item:the-thing-he-wants"],
             "perspectives": ["party", "character:belmakor", "table", "public"]}
            """);

        Assert.StartsWith($"# Knowledge ledger: {scenario.Slug}\n\nRows use true names (author view).", text, StringComparison.Ordinal);
        Assert.Contains(
            "| Row | party | character:belmakor | table | public |\n|---|---|---|---|---|\n" +
            "| **character:the-old-king** The Old King | knows | met as “the old king” | knows | not met |\n" +
            $"| {scenario.ErrandFact} The old king wants the thing brought up from below. | knows | knows | knows | no record |\n" +
            $"| {scenario.PlannedFact} The old king's army marches next spring. | not in play (planned) | not in play (planned) | not in play (planned) | not in play (planned) |\n" +
            "| **item:the-thing-he-wants** The thing he wants | knows | knows | knows | not met |\n" +
            $"| {scenario.AxiomFact} The thing he wants is the Axiom Cage. | unaware | unaware | no record | no record |\n",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_LedgerWithNoPerspectives_ListsTheKnowersOnRecordThenPartyTableAndPublic()
    {
        var scenario = await KnowledgeScenario.BuildAsync(_server);

        var text = await CampaignWriteSetup.CallAsync(_server, "campaign_knowledge",
            $$"""{"campaign": "{{scenario.Slug}}", "action": "ledger", "about": ["character:the-old-king"]}""");

        Assert.Contains("| Row | character:belmakor | party | table | public |\n", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"campaign": "@S", "action": "check", "text": "x", "facts": ["f:1"]}""",
        "campaign_knowledge check does not take \"facts\"; check takes text, perspective, diegetic, audience, as_of_session, campaign.")]
    [InlineData("""{"campaign": "@S", "action": "reveal", "facts": ["f:1"], "text": "x", "knowers": []}""",
        "campaign_knowledge reveal does not take \"knowers\" or \"text\"; reveal takes facts, secret, handout, to, how, session, reason, dry_run, campaign.")]
    [InlineData("""{"campaign": "@S", "action": "ledger", "about": ["x"], "dry_run": true}""",
        "campaign_knowledge ledger does not take \"dry_run\"; ledger takes about, facts, perspectives, as_of_session, campaign.")]
    [InlineData("""{"campaign": "@S", "action": "frob"}""",
        "action \"frob\" is not a campaign_knowledge action; give record, reveal, check, ledger, retract. Example: {\"action\": \"check\"")]
    [InlineData("""{"campaign": "@S", "action": "  "}""", "action is required: record, reveal, check, ledger, retract. Example: {")]
    [InlineData("""{"campaign": "@S", "action": "record"}""",
        "Invalid knowledge (2 problems):\n- targets is required: fact or entity handles, e.g. [\"f:12\", \"character:old-king\"].\n- knowers is required:")]
    [InlineData("""{"campaign": "@S", "action": "check"}""", "text is required: the draft to check, e.g. a lyric or an in-character line.")]
    [InlineData("""{"campaign": "@S", "action": "ledger"}""", "Give about (entity handles, e.g. [\"character:protector\"]) or facts (fact handles, e.g. [\"f:12\"]), or both.")]
    public async Task CallTool_ArgumentsTheActionDoesNotTakeOrNeeds_AreRefusedWithWhatItTakes(string arguments, string start)
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);

        var text = await CampaignWriteSetup.FailAsync(_server, "campaign_knowledge", arguments.Replace("@S", slug, StringComparison.Ordinal));

        Assert.StartsWith("An error occurred invoking 'campaign_knowledge': " + start, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"campaign": "@S"}""", "missing required argument 'action'")]
    [InlineData("""{"campaign": "@S", "action": "record", "targets": ["f:1"], "knowers": [{"who": "party", "knownas": "x"}]}""",
        "argument 'knowers' item 1 has unknown field 'knownas' (fields: who, state, known_as, how, via, session, note)")]
    [InlineData("""{"campaign": "@S", "action": "ledger", "about": "character:x"}""", "argument 'about' should be array")]
    public async Task CallTool_ArgumentsTheSchemaRefuses_GuardNamesThem(string arguments, string problem)
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);

        var text = await CampaignWriteSetup.FailAsync(_server, "campaign_knowledge", arguments.Replace("@S", slug, StringComparison.Ordinal));

        Assert.StartsWith("An error occurred invoking 'campaign_knowledge': Invalid arguments: ", text, StringComparison.Ordinal);
        Assert.Contains(problem, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_NullForEveryOptionalArgument_MeansTheDefaults()
    {
        await CampaignWriteSetup.WithServerAsync(async server =>
        {
            var scenario = await KnowledgeScenario.BuildAsync(server);

            var text = server.SuccessText(await server.CallToolJsonAsync("campaign_knowledge", """
                {"action": "check", "text": "Old king, come down", "campaign": null, "targets": null, "knowers": null, "facts": null,
                 "secret": null, "handout": null, "to": null, "how": null, "who": null, "perspective": null, "diegetic": null,
                 "audience": null, "about": null, "perspectives": null, "as_of_session": null, "session": null, "reason": null, "dry_run": null}
                """));

            // No campaign: the only one. No perspective: the author (nothing to flag, and it says so). No diegetic: a private text.
            Assert.StartsWith($"# Knowledge check: {CheckMarkdown.AuthorNothingToFlag} ({scenario.Slug})\n\nSpeaker: author.\n", text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void FormatWrite_WorstCaseResult_IsCappedWithTheBatchLineIntactAndCountsTheRest()
    {
        // 50 targets × 30 knowers, the most one record call writes, with a gate warning on every row.
        var applied = Enumerable.Range(0, 1_500)
            .Select(i => new AppliedOp(i / 30, "record", $"f:{(i / 30) + 1}", WriteOutcomes.Created, ["state", "known_as", "how", "via"],
                Knower: $"character:crew-member-with-a-long-slug-{i % 30}"))
            .ToList();
        var warnings = Enumerable.Range(0, 400)
            .Select(i => (WriteWarning)new GateWarning(new string('w', 300), i, "with", $"f:{i}", "party", ["f:2"], [], 12, null, null, [], new string('n', 300)))
            .ToList();
        var result = new WriteResult("0199aaaa-bbbb-7ccc-8ddd-eeeeffff0000", false, 12, applied, warnings, []);

        var text = KnowledgeMarkdown.FormatWrite(CampaignWriteSetup.Row("big"), "record", result);

        Assert.True(text.Length <= CampaignMarkdownText.MaxChars, $"{text.Length} characters.");
        Assert.StartsWith("# campaign_knowledge record: 1500 rows (big)\n\nBatch `0199aaaa-bbbb-7ccc-8ddd-eeeeffff0000`. Session context: session 12. To undo it:", text, StringComparison.Ordinal);
        var rows = Regex.Matches(text, @"^\| f:\d+ \| character:", RegexOptions.Multiline).Count;
        Assert.Contains($"_… and {(1_500 - rows).ToString(CultureInfo.InvariantCulture)} more rows (all written as reported)._", text, StringComparison.Ordinal);
        Assert.Matches(@"_… and \d+ more warnings\._", text);
    }

    [Fact]
    public void FormatCheck_WorstCaseResult_StaysUnderTheCapWithTheHardFlagsFirst()
    {
        // A 50,000-character draft can name hundreds of things; every list is long and every statement is long.
        var candidate = new NameCandidate("character:someone", "Someone", NameMatchKinds.Alias, "author", NameClasses.OtherName, "the other one", null, null, null);
        var names = Enumerable.Range(0, 300)
            .Select(i => new NameMention($"Name number {i}", i * 20, 12, NameClasses.OtherName, NameClasses.RevealsToAudience, [candidate, candidate]))
            .ToList();
        var fact = new FactFinding("f:1", "F1", new string('s', 2_000), Standings.NoRecord, null, "true", "canon", ["party", "table"], "shares words with the text", "no record");
        var facts = Enumerable.Repeat(fact, 200).ToList();
        var forbidden = Enumerable.Range(0, 200)
            .Select(i => new ForbiddenFinding("rule:no-name", "seal", "seals", i, 5, ["f:9"], ["shell", "wrapping"], new string('n', 500)))
            .ToList();
        var result = new CheckResult("character:belmakor", "party", false, names, forbidden, facts, facts, [], [], facts,
            Enumerable.Range(0, 300).Select(i => $"Invented {i}").ToList());

        var text = CheckMarkdown.Format(CampaignWriteSetup.Row("big"), result, diegetic: true, asOfSession: 12);

        // 300 names, each other_name and reveals_to_audience, and one forbidden word 200 times: 601 problems in 800
        // places. The 25 lines are shared round-robin, so the one forbidden word is shown between two long lists.
        Assert.True(text.Length <= CampaignMarkdownText.MaxChars, $"{text.Length} characters.");
        Assert.StartsWith("# Knowledge check: 601 hard flags (big)\n\nSpeaker: character:belmakor · diegetic, audience: party · as of session 12.\n", text, StringComparison.Ordinal);
        Assert.Contains("\n## Hard flags (601; 800 places in the text)\n", text, StringComparison.Ordinal);
        Assert.Contains("\n_… and 288 more other_name flags._\n- **forbidden** \"seals\" (matches \"seal\") (200 times): rule:no-name forbids it; " +
                        "lifts when f:9 is in play; say instead: \"shell\", \"wrapping\"; note: " + new string('n', 500) + ".\n- **reveals_to_audience**", text, StringComparison.Ordinal);
        Assert.Contains("\n_… and 288 more reveals_to_audience flags._\n", text, StringComparison.Ordinal);
        Assert.Equal(25, Regex.Matches(text, @"^- \*\*(other_name|forbidden|reveals_to_audience)\*\*", RegexOptions.Multiline).Count);
        Assert.True(text.IndexOf("## Hard flags", StringComparison.Ordinal) < text.IndexOf("## To review", StringComparison.Ordinal) ||
                    !text.Contains("## To review", StringComparison.Ordinal));
    }

    [Fact]
    public void Format_ManyFlagsOfTwoKindsAndOneOfTwoOthers_KeepsEveryKindVisibleAndCountsEachKindsRest()
    {
        // 40 distinct other_name and reveals_to_audience flags would fill the 25 lines on their own; the forbidden word and
        // the secret at risk after them must still be seen.
        static NameMention Mention(int i) => new($"Name {i}", i * 10, 6, NameClasses.OtherName, NameClasses.RevealsToAudience,
            [new NameCandidate($"character:c{i}", $"C {i}", NameMatchKinds.Alias, "author", NameClasses.OtherName, $"the {i}", NameClasses.RevealsToAudience, null, null)]);
        var fact = new FactFinding("f:5", null, "He wants the surface back.", Standings.Knows, "knows", "true", "canon", ["character:belmakor"], "about character:belmakor", "knows");
        var result = new CheckResult("character:belmakor", "party", false, Enumerable.Range(0, 40).Select(Mention).ToList(),
            [new ForbiddenFinding("rule:no-name", "Keras", "Keras", 900, 5, [], [], null)], [], [],
            [new SecretAtRisk(fact, RiskReasons.RelatedToText, Standings.NoRecord)], [], [], []);

        var text = CheckMarkdown.Format(CampaignWriteSetup.Row("big"), result, diegetic: true, asOfSession: null);

        Assert.StartsWith("# Knowledge check: 82 hard flags (big)\n", text, StringComparison.Ordinal);
        Assert.Contains("\n_… and 28 more other_name flags._\n- **forbidden** \"Keras\": rule:no-name forbids it.\n- **reveals_to_audience** \"Name 0\":", text, StringComparison.Ordinal);
        Assert.Contains("\n_… and 29 more reveals_to_audience flags._\n- **secret at risk** f:5 \"He wants the surface back.\": character:belmakor knows it; " +
                        "party: no record. The text touches it (about character:belmakor); a song or speech is a public statement.\n", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Review UR2: with no hard flag but a possible partial name listed, neither the title nor the hard-flags line claims
    /// that every name is one the speaker uses; both point at the review item (counted once per word and name, as listed).
    /// With neither, the output is as before.
    /// </summary>
    [Theory]
    [InlineData(0, "pass", "None: every name is one the speaker uses and the audience knows, no forbidden words, no secret at risk.")]
    [InlineData(1, "pass, 1 possible partial name to review", "None certain: see Possible partial names under To review.")]
    [InlineData(2, "pass, 2 possible partial names to review", "None certain: see Possible partial names under To review.")]
    public void Format_NoHardFlagButPossiblePartialNames_PointsAtThemInsteadOfSayingEveryNameIsUsed(int words, string title, string hardFlags)
    {
        // "Cage" twice is one review line; "Axiom" is a second one.
        var partials = new[] { ("Cage", 0), ("Cage", 40), ("Axiom", 80) }.Take(words == 2 ? 3 : words == 1 ? 2 : 0)
            .Select(w => new PartialName(w.Item1, w.Item2, w.Item1.Length, "character:belmakor", "Axiom Cage", "item:axiom-cage", PartialNameSources.Name, null, null))
            .ToList();
        var result = new CheckResult("character:belmakor", "party", true, [], [], [], [], [], [], [], []) { PossiblePartialNames = partials };

        var text = CheckMarkdown.Format(CampaignWriteSetup.Row("big"), result, diegetic: true, asOfSession: null);

        Assert.StartsWith($"# Knowledge check: {title} (big)\n", text, StringComparison.Ordinal);
        Assert.Contains($"\n## Hard flags\n{hardFlags}\n", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A possible partial name changes only a result with no hard flag (review UR2). Beside a hard flag the title counts the
    /// flags and the section lists them: "None certain" there hid every hard flag behind the review item. An author speaker
    /// (no perspective, or dm in a DM campaign) keeps U10's title, with the count between its halves: the plain "pass, 1 …
    /// to review" was the bare author-view pass U10 removed, and it dropped the advice to pass a perspective.
    /// </summary>
    [Theory]
    [InlineData("character:belmakor", false, "1 hard flag", "## Hard flags (1)\n- **forbidden** \"Keras\": f:1 forbids it.\n")]
    [InlineData("author", true, "author view: nothing to flag, 1 possible partial name to review — pass perspective to check a character's, " +
                                "the party's or the public's knowledge", "## Hard flags\nNone certain: see Possible partial names under To review.\n")]
    [InlineData("dm", true, "author view: nothing to flag, 1 possible partial name to review — pass perspective to check a character's, " +
                            "the party's or the public's knowledge", "## Hard flags\nNone certain: see Possible partial names under To review.\n")]
    public void Format_APossiblePartialNameBesideAHardFlagOrForTheAuthor_KeepsTheirTitle(string speaker, bool pass, string title, string hardFlags)
    {
        var partial = new PartialName("Cage", 20, 4, "party", "Axiom Cage", "item:axiom-cage", PartialNameSources.Name, null, null);
        IReadOnlyList<ForbiddenFinding> forbidden = pass ? [] : [new ForbiddenFinding("f:1", "Keras", "Keras", 0, 5, [], [], null)];
        var result = new CheckResult(speaker, "party", pass, [], forbidden, [], [], [], [], [], []) { PossiblePartialNames = [partial] };

        var text = CheckMarkdown.Format(CampaignWriteSetup.Row("big"), result, diegetic: true, asOfSession: null);

        Assert.StartsWith($"# Knowledge check: {title} (big)\n", text, StringComparison.Ordinal);
        Assert.Contains("\n" + hardFlags, text, StringComparison.Ordinal);
        Assert.Equal(pass, text.Contains(CheckMarkdown.NoneCertain, StringComparison.Ordinal));
        Assert.Contains($"\n**{CheckMarkdown.PossiblePartialNamesTitle}** (1)\n- \"Cage\": ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_CheckOfAChorusThatRepeatsABadName_PrintsEachProblemOnceWithItsCount()
    {
        // A chorus sings "Keras" ten times: one problem per kind, not ten lines each, and the timespan after it stays visible.
        var scenario = await KnowledgeScenario.BuildAsync(_server);
        var chorus = string.Concat(Enumerable.Repeat("Keras, come down! ", 10)) + "A nine-hundred-year-old king.";

        var text = await Check(scenario.Slug, chorus, "character:belmakor", diegetic: true);

        Assert.StartsWith($"# Knowledge check: 4 hard flags ({scenario.Slug})\n", text, StringComparison.Ordinal);
        var flags = text[text.IndexOf("## Hard flags", StringComparison.Ordinal)..text.IndexOf("## To review", StringComparison.Ordinal)];
        Assert.Equal(
            "## Hard flags (4; 31 places in the text)\n" +
            "- **other_name** \"Keras\" (10 times): character:the-old-king (The Old King; author alias): character:belmakor knows it as \"The Old King\".\n" +
            "- **forbidden** \"Keras\" (10 times): rule:no-name-no-timespan forbids it; say instead: \"the old king\"; note: Belmakor never learns his name or age.\n" +
            "- **forbidden** \"nine-hundred-year-old\" (matches \"<number>-year-old\"): rule:no-name-no-timespan forbids it; say instead: \"the old king\"; note: Belmakor never learns his name or age.\n" +
            "- **reveals_to_audience** \"Keras\" (10 times): character:the-old-king: party knows it as \"The Old King\".\n\n",
            flags);
    }

    [Fact]
    public async Task CallTool_CheckWithAnAudienceAndNoDiegetic_IsDiegeticToThatAudience()
    {
        // Deviation 2: naming an audience means the text is heard, so diegetic defaults to true; the audience's flags come
        // with it. A default of false would silently check a crowd-facing lyric as a private journal.
        var scenario = await KnowledgeScenario.BuildAsync(_server);

        var text = _server.SuccessText(await _server.Client.CallToolAsync("campaign_knowledge", new Dictionary<string, object?>
        {
            ["campaign"] = scenario.Slug, ["action"] = "check", ["perspective"] = "character:belmakor", ["audience"] = "public",
            ["text"] = "We'll haul the Axiom Cage back up",
        }));

        Assert.StartsWith($"# Knowledge check: 2 hard flags ({scenario.Slug})\n\nSpeaker: character:belmakor · diegetic, audience: public.\n", text, StringComparison.Ordinal);
        Assert.Contains("\n- **reveals_to_audience** \"Axiom Cage\": item:the-thing-he-wants: public does not know it.\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_CheckOfAMistakenBelief_ListsItForReviewWithItsTruth()
    {
        // One Piece row 27's shape: the speaker believes a half-truth; the check shows the belief and its truth, not a flag.
        var scenario = await ReviewScenario.BuildAsync(_server);

        var text = await Check(scenario.Slug, "It broke because I was slow.", "character:belmakor", diegetic: false);

        Assert.StartsWith($"# Knowledge check: pass ({scenario.Slug})\n", text, StringComparison.Ordinal);
        Assert.Contains($"\n**Mistaken beliefs** (1)\n- {scenario.Mistaken} \"It broke because I was slow.\": believes, but its truth is partial · why: shares words with the text\n",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_CheckOfAFactRestingOnASupersededOne_ListsItAsStaleWithTheFactItRestsOnAndHowFar()
    {
        // One Piece row 30's shape: a fact that depends on a superseded one is stale, one step away from it.
        var scenario = await ReviewScenario.BuildAsync(_server);

        var text = await Check(scenario.Slug, "The tower's builders are all gone.", "party", diegetic: false);

        Assert.Contains($"\n**Stale facts** (2)\n- {scenario.Dependent} \"The tower's builders are all gone.\": rests on superseded {scenario.Old} (depth 1)\n" +
                        $"- {scenario.Old} \"The tower fell a thousand years ago.\": superseded\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_CheckOfAnotherCampaignsName_FlagsItAsCrossCampaign()
    {
        // Belmakor rows 26 and 41's shape: a name that exists only in the campaign a same_as link points to is the
        // firewall between campaigns, whoever says it.
        var scenario = await ReviewScenario.BuildAsync(_server);

        var text = await Check(scenario.Slug, "Baal, come down", "character:belmakor", diegetic: false);

        Assert.Contains($"\n## Hard flags (1)\n- **cross_campaign** \"Baal\": {scenario.Other}/character:baal (Baal; its name): a name from campaign " +
                        $"{scenario.Other}; nobody here knows it.\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_CheckOfAnEntityTheSpeakerHasNotMet_FlagsItAsUnknownToThem()
    {
        var scenario = await ReviewScenario.BuildAsync(_server);

        var text = await Check(scenario.Slug, "Captain Vortigern watched the fair.", "character:belmakor", diegetic: false);

        Assert.Contains("\n## Hard flags (1)\n- **unknown_entity** \"Captain Vortigern\": character:captain-vortigern (Captain Vortigern; its name): " +
                        "character:belmakor does not know this character.\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_CheckOfAnUnrecordedProperNoun_ListsItAsAPossibleInvention()
    {
        var scenario = await ReviewScenario.BuildAsync(_server);

        var text = await Check(scenario.Slug, "We sailed to Zanzibar.", "character:belmakor", diegetic: false);

        Assert.StartsWith($"# Knowledge check: pass ({scenario.Slug})\n", text, StringComparison.Ordinal);
        Assert.Contains("\n**Capitalised words that match no name here (add any that is a new name; ignore ordinary words)** (1)\n- \"Zanzibar\"\n",
            text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The check and the session checklist judge "a name that matches nothing" by one rule (review UR01): a candidate that
    /// contains a name the speaker uses (a line's opening word joined to it: "Hail Belmakor", "Sing of the Crumbling
    /// Statue") or is a word of one ("King", of the party's "the old king") is no unknown name in either. The check listed
    /// them as possible inventions "to accept or strike" while the record_past checklist of the same text listed nothing, and
    /// a run's model reported the check as a bug. A real unknown name is listed by both, a new word beside a known name too
    /// (UR01's recheck: "holds a known name, so known" hid "Belmakor Shadowfang" from the check as well as the checklist).
    /// </summary>
    [Theory]
    [InlineData("Hail Belmakor, hail the band.", "")]
    [InlineData("Sing of the Crumbling Statue.", "")]
    [InlineData("Beware the Crumbling Statue", "")]
    [InlineData("Tonight we sing for the King of the sky.", "")]
    [InlineData("Hail Zanzibar, hail the band.", "Hail Zanzibar")]
    [InlineData("We met Rolf of the Crumbling Statue at dawn.", "Rolf of the Crumbling Statue")]
    [InlineData("Then Old King Zanzibar spoke to us.", "Old King Zanzibar")]
    [InlineData("We drank with Belmakor Shadowfang, his brother.", "Belmakor Shadowfang")]
    public async Task CallTool_CheckOfACandidateMadeOfANameTheSpeakerUses_ListsNoUnknownNameAndAgreesWithTheChecklist(string text, string unknown)
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server, role: "player", myCharacter: "Belmakor Silverwind");
        await CampaignWriteSetup.WriteAsync(_server, slug, """
            [{"op": "upsert", "ref": "character:belmakor-silverwind", "aliases": [{"alias": "Belmakor", "visibility": "public"}]},
             {"op": "upsert", "kind": "location", "name": "Crumbling statue", "visibility": "party", "known_by": [{"who": "party"}]},
             {"op": "upsert", "kind": "character", "name": "Keras", "visibility": "party", "aliases": [{"alias": "the old king", "visibility": "party"}],
              "known_by": [{"who": "party", "state": "met", "known_as": "the ancient sorcerer king"}]}]
            """);

        var check = await CampaignWriteSetup.CallAsync(_server, "campaign_knowledge",
            $$"""{"campaign": "{{slug}}", "action": "check", "perspective": "character:belmakor-silverwind", "diegetic": true, "text": "{{text}}"}""");
        var checklist = await CampaignWriteSetup.CallAsync(_server, "campaign_session",
            $$"""{"campaign": "{{slug}}", "action": "record_past", "recap_md": "{{text}}", "dry_run": true}""");

        Assert.DoesNotContain("Possible inventions", check, StringComparison.Ordinal);
        if (unknown.Length == 0)
        {
            Assert.DoesNotContain("match no name here", check, StringComparison.Ordinal);
            Assert.DoesNotContain("Names that match no entry", checklist, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains($"\n**Capitalised words that match no name here (add any that is a new name; ignore ordinary words)** (1)\n- \"{unknown}\"\n",
                check, StringComparison.Ordinal);
            Assert.Contains($"Names that match no entry: \"{unknown}\" — ", checklist, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// One word of a secret two-word name, sung alone, is a hard flag naming the whole name it belongs to (review U02): run
    /// 3's chorus "we'll haul your Cage back" passed for Belmakor, who knows the Axiom Cage only as "what the sorcerer king
    /// sent us for", with "Cage" left under possible inventions.
    /// </summary>
    [Fact]
    public async Task CallTool_CheckOfOneWordOfASecretName_IsAPartialNameHardFlagWithTheWholeName()
    {
        await CampaignWriteSetup.WithServerAsync(async server =>
        {
            await CampaignWriteSetup.CallAsync(server, "campaign",
                """{"action": "create", "name": "sky", "slug": "sky", "role": "player", "ruleset": "2014", "my_character": "Belmakor Silverwind"}""");
            await CampaignWriteSetup.WriteAsync(server, "sky", """
                [{"op": "upsert", "kind": "item", "name": "Axiom Cage", "visibility": "party",
                  "known_by": [{"who": "party", "state": "heard", "known_as": "what the sorcerer king sent us for"}]},
                 {"op": "fact", "statement": "What the sorcerer king sent the party to fetch is the Axiom Cage.", "about": ["item:axiom-cage"],
                  "gate": {"forbidden_terms": ["Axiom Cage"]}, "known_by": [{"who": "party", "state": "unaware"}, {"who": "dm"}]}]
                """);

            var text = await CampaignWriteSetup.CallAsync(server, "campaign_knowledge",
                """{"campaign": "sky", "action": "check", "perspective": "character:belmakor-silverwind", "diegetic": true, "text": "Old king, come down\nwe'll haul your Cage back up the mountain"}""");

            Assert.StartsWith("# Knowledge check: 1 hard flag (sky)\n", text, StringComparison.Ordinal);
            Assert.Contains(
                "\n## Hard flags (1)\n- **partial_name** \"Cage\": a word of \"Axiom Cage\" (item:axiom-cage; its name), which " +
                "character:belmakor-silverwind knows as \"what the sorcerer king sent us for\".\n", text, StringComparison.Ordinal);
            Assert.DoesNotContain("- \"Cage\"", text, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// The same word opening a line is listed for review, not failed (review of fix FQ4): every line of a lyric starts with
    /// a capital, so a line that opens with a word of a secret name may be the plain word ("Cage of iron"), and hard flags
    /// on plain English teach the model to ignore the check. The review line still names the whole name it may give away.
    /// </summary>
    [Fact]
    public async Task CallTool_CheckOfALineOpeningWithOneWordOfASecretName_PassesAndListsItForReview()
    {
        await CampaignWriteSetup.WithServerAsync(async server =>
        {
            await CampaignWriteSetup.CallAsync(server, "campaign",
                """{"action": "create", "name": "sky", "slug": "sky", "role": "player", "ruleset": "2014", "my_character": "Belmakor Silverwind"}""");
            await CampaignWriteSetup.WriteAsync(server, "sky", """
                [{"op": "upsert", "kind": "item", "name": "Axiom Cage", "visibility": "party",
                  "known_by": [{"who": "party", "state": "heard", "known_as": "what the sorcerer king sent us for"}]}]
                """);

            var text = await CampaignWriteSetup.CallAsync(server, "campaign_knowledge",
                """{"campaign": "sky", "action": "check", "perspective": "character:belmakor-silverwind", "diegetic": true, "text": "Old king, come down\nCage of iron, carry us home"}""");

            // No hard flag, but the result never says every name is one the speaker uses while it lists a possible partial
            // name (review UR2): the title and the hard-flags line point at the review item instead.
            Assert.StartsWith("# Knowledge check: pass, 1 possible partial name to review (sky)\n", text, StringComparison.Ordinal);
            Assert.Contains("\n## Hard flags\nNone certain: see Possible partial names under To review.\n", text, StringComparison.Ordinal);
            Assert.DoesNotContain("every name is one the speaker uses", text, StringComparison.Ordinal);
            Assert.Contains(
                $"\n**{CheckMarkdown.PossiblePartialNamesTitle}** (1)\n- \"Cage\": a word of \"Axiom Cage\" (item:axiom-cage; its name), which " +
                "character:belmakor-silverwind knows as \"what the sorcerer king sent us for\"\n", text, StringComparison.Ordinal);
            Assert.DoesNotContain("**partial_name**", text, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// The dm of a DM campaign is the author's view, so its check has nothing to flag and says so like the author's
    /// (review U10); the dm of a player campaign is a player-side view whose clean draft passes.
    /// </summary>
    [Theory]
    [InlineData("dm", CheckMarkdown.AuthorNothingToFlag)]
    [InlineData("player", "pass")]
    public async Task CallTool_CheckFromTheDm_HasNothingToFlagOnlyWhereTheDmIsTheAuthor(string role, string title)
    {
        await CampaignWriteSetup.WithServerAsync(async server =>
        {
            await CampaignWriteSetup.CallAsync(server, "campaign", $$"""{"action": "create", "name": "Veil", "slug": "veil", "role": "{{role}}", "ruleset": "2024"}""");

            var text = await CampaignWriteSetup.CallAsync(server, "campaign_knowledge",
                """{"campaign": "veil", "action": "check", "perspective": "dm", "text": "The harbour is quiet tonight."}""");

            Assert.StartsWith($"# Knowledge check: {title} (veil)\n\nSpeaker: dm.\n", text, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// A party alias of an entity the party knows by a known_as is a name the party uses (review U01): run 1's model deleted
    /// the correct alias "the old king" because the check flagged it as other_name and reveals_to_audience, and the party's
    /// search found the entity by it only through "king".
    /// </summary>
    [Fact]
    public async Task CallTool_CheckOfAPartyAliasOfADisguisedEntity_PassesAndTheSearchFindsItByThatAlias()
    {
        await CampaignWriteSetup.WithServerAsync(async server =>
        {
            await CampaignWriteSetup.CallAsync(server, "campaign",
                """{"action": "create", "name": "sky", "slug": "sky", "role": "player", "ruleset": "2014", "my_character": "Belmakor Silverwind"}""");
            await CampaignWriteSetup.WriteAsync(server, "sky", """
                [{"op": "upsert", "kind": "character", "name": "Keras", "subtype": "npc", "visibility": "party",
                  "aliases": [{"alias": "the old king", "visibility": "party"}],
                  "known_by": [{"who": "party", "state": "met", "known_as": "the ancient sorcerer king"}]}]
                """);

            var check = await CampaignWriteSetup.CallAsync(server, "campaign_knowledge",
                """{"campaign": "sky", "action": "check", "perspective": "character:belmakor-silverwind", "diegetic": true, "text": "Old king, come down"}""");
            var search = await CampaignWriteSetup.CallAsync(server, "campaign_search", """{"campaign": "sky", "query": "old king", "perspective": "party"}""");

            Assert.StartsWith("# Knowledge check: pass (sky)\n", check, StringComparison.Ordinal);
            Assert.DoesNotContain("other_name", check, StringComparison.Ordinal);
            Assert.DoesNotContain("Nothing has every word", search, StringComparison.Ordinal);
            Assert.Matches(@"\n1\. \*\*the ancient sorcerer king\*\* · character · `e:\d+`\n", search);
            Assert.DoesNotContain("Keras", search, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CallTool_CheckOfASongThatLeavesTheSpeakersSecretOut_PassesAndListsTheSecretForReview()
    {
        // Belmakor rows 44 and 45's shape: his own ambition is a secret the party does not know, but a line that does not
        // touch it is no hard flag; it is listed to keep in mind.
        var scenario = await ReviewScenario.BuildAsync(_server);

        var text = await Check(scenario.Slug, "I thought this could be a kingdom", "character:belmakor", diegetic: true);

        Assert.StartsWith($"# Knowledge check: pass ({scenario.Slug})\n\nSpeaker: character:belmakor · diegetic, audience: party.\n", text, StringComparison.Ordinal);
        Assert.Contains("\n## Hard flags\nNone:", text, StringComparison.Ordinal);
        Assert.Contains($"\n**Secrets about the speaker (not in the text; keep them out)** (1)\n- {scenario.Ambition} \"Belmakor wants to rule the dead world below.\": " +
                        "party: no record\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_CheckOfAnAuthorOnlyFactInASong_SaysItIsAuthorOnlyNotThatTheAudienceKnowsIt()
    {
        // The rows say Belmakor and the party know them, but author visibility is absolute (contract §3.2): no player-side
        // view knows an author-only fact, so it is neither a secret Belmakor could give away nor one he must keep out. The
        // fact the text touches is listed as one he does not know, "author only" (the reader's wording), with who holds
        // rows on it; the one about him that the text leaves out is not listed at all. Nothing reads "party: knows".
        var scenario = await KnowledgeScenario.BuildAsync(_server);
        var facts = CampaignWriteSetup.Refs(await CampaignWriteSetup.WriteAsync(_server, scenario.Slug, """
            [{"op": "fact", "statement": "The king is nine hundred.", "visibility": "author", "known_by": [{"who": "party"}, {"who": "character:belmakor"}]},
             {"op": "fact", "statement": "Belmakor was born below.", "visibility": "author", "about": ["character:belmakor"],
              "known_by": [{"who": "party"}, {"who": "character:belmakor"}]}]
            """));

        var text = await Check(scenario.Slug, "The king is nine hundred.", "character:belmakor", diegetic: true);

        Assert.Contains($"\n- {facts[0]} \"The king is nine hundred.\": author only: its knowledge rows do not make it known outside the author view · " +
                        "known by character:belmakor, party · why: shares words with the text\n", text, StringComparison.Ordinal);
        Assert.Contains("\n## Hard flags\nNone:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("**secret at risk**", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Secrets about the speaker", text, StringComparison.Ordinal);
        Assert.DoesNotContain($"{facts[1]} \"Belmakor was born below.\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("party: knows", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_RevealOfAGatedFact_ReadsExactlyAsWritten()
    {
        var gate = await GateScenario.BuildAsync(_server);

        var text = await CampaignWriteSetup.CallAsync(_server, "campaign_knowledge",
            $$"""{"campaign": "{{gate.Slug}}", "action": "reveal", "facts": ["{{gate.Seal}}"], "how": "told"}""");

        var id = Regex.Match(text, "Batch `([0-9a-f-]{36})`").Groups[1].Value;
        Assert.Equal(
            $"# campaign_knowledge reveal: 1 row ({gate.Slug})\n\n" +
            $"Batch `{id}`. To undo it: campaign_history {{\"action\": \"undo\", \"batch_id\": \"{id}\", \"campaign\": \"{gate.Slug}\"}}.\n\n" +
            "| Target | Knower | Outcome | Changed |\n|---|---|---|---|\n" +
            $"| {gate.Seal} | party | created | state |\n\n" +
            "## Warnings (2)\nApplied anyway; read them before the table does.\n" +
            $"- **warning** · gate (after) · {gate.Seal} → party: {gate.Seal} reached party before its gate's after is met: {gate.Axe} not in play yet (applied anyway).\n" +
            "  - Gate note: Not before the axe.\n" +
            $"- **warning** · gate (with) · {gate.Seal} → party: {gate.Seal} must land with {gate.Nadar} (same knower, same session): {gate.Nadar} has not reached party (applied anyway).\n\n" +
            "## Consequences (1)\n- secret status: secret:fruits-are-the-seal: hidden → revealed.\n",
            text);
    }

    [Fact]
    public async Task CallTool_LedgerAsOfASession_SaysWhichSessionInItsTitle()
    {
        var scenario = await KnowledgeScenario.BuildAsync(_server);

        var text = await CampaignWriteSetup.CallAsync(_server, "campaign_knowledge",
            $$"""{"campaign": "{{scenario.Slug}}", "action": "ledger", "about": ["character:the-old-king"], "perspectives": ["party"], "as_of_session": 0}""");

        Assert.StartsWith($"# Knowledge ledger: {scenario.Slug}, as of session 0\n\nRows use true names (author view).", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_AnArgumentTheActionDoesNotTake_IsRefusedWithWhatItTakesAndAnExampleOfThatAction()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);

        var text = await CampaignWriteSetup.FailAsync(_server, "campaign_knowledge",
            $$"""{"campaign": "{{slug}}", "action": "reveal", "facts": ["f:1"], "text": "x"}""");

        Assert.Equal("An error occurred invoking 'campaign_knowledge': campaign_knowledge reveal does not take \"text\"; reveal takes facts, secret, " +
                     "handout, to, how, session, reason, dry_run, campaign. Example: {\"action\": \"reveal\", \"facts\": [\"f:12\"], \"to\": [\"party\"], " +
                     "\"how\": \"told\", \"dry_run\": true}", text);
    }

    [Fact]
    public void HardFlags_EveryKind_ComeInTheContractsOrderWhateverTheTextOrder()
    {
        // In the text: a cross-campaign name, an unknown entity, then another name; the audience flag rides on the first.
        // The contract's order is other_name, unknown_entity, cross_campaign, forbidden, reveals_to_audience, then secrets.
        static NameMention Mention(string matched, int start, string classification, string? audience = null) => new(matched, start, matched.Length,
            classification, audience, [new NameCandidate("character:x", "X", NameMatchKinds.Name, null, classification, "Y", audience, null, "one-piece")]);
        var names = new[]
        {
            Mention("Crossed", 0, NameClasses.CrossCampaign, NameClasses.RevealsToAudience),
            Mention("Unknown", 10, NameClasses.UnknownEntity),
            Mention("Other", 20, NameClasses.OtherName),
        };
        var fact = new FactFinding("f:5", null, "He wants the surface back.", Standings.Knows, "knows", "true", "canon", ["character:belmakor"], "about character:belmakor", "knows");
        var result = new CheckResult("character:belmakor", "party", false, names,
            [new ForbiddenFinding("rule:no-name", "Keras", "Keras", 30, 5, [], [], null)], [], [],
            [new SecretAtRisk(fact, RiskReasons.RelatedToText, Standings.NoRecord)], [], [], []);

        var flags = CheckMarkdown.HardFlags(result).Select(f => f[..f.IndexOf(':', StringComparison.Ordinal)]).ToArray();

        Assert.Equal(new[]
        {
            "**other_name** \"Other\"", "**unknown_entity** \"Unknown\"", "**cross_campaign** \"Crossed\"", "**forbidden** \"Keras\"",
            "**reveals_to_audience** \"Crossed\"", "**secret at risk** f",
        }, flags);
    }

    /// <summary>
    /// The legend names every kind of cell the reader prints, "author only" included (a row only the author view sees,
    /// whatever the column's knowledge rows say: contract §3.2). Without it the cell reads like a verdict nobody defined.
    /// </summary>
    [Fact]
    public void FormatLedger_AnAuthorOnlyCell_TheLegendSaysWhatItMeans()
    {
        var cell = new LedgerCell("party", Standings.AuthorOnly, "knows", null, null, "author only");
        var ledger = new Ledger(["party"], [new LedgerRow("f:1", "The plotter plans a coup.", false, [cell])], null, 1);

        var text = LedgerMarkdown.Format(CampaignWriteSetup.Row("big"), ledger);

        Assert.Contains("\"not in play\" a fact or entity that has not happened yet, \"author only\" a row only the author view sees, " +
                        "whatever that perspective's knowledge rows say.\n", text, StringComparison.Ordinal);
        Assert.Contains("| f:1 The plotter plans a coup. | author only |\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatLedger_WorstCaseResult_AddsWholeRowsWhileTheyFitAndCountsTheRest()
    {
        // 100 rows (the reader's cap) of 12 columns with long cells, and a reader that had 140 rows before it cut.
        var columns = Enumerable.Range(0, 12).Select(i => $"character:member-with-a-long-slug-{i}").ToList();
        var rows = Enumerable.Range(0, 100)
            .Select(i => new LedgerRow($"f:{i + 1}", new string('s', 200), false,
                columns.Select(c => new LedgerCell(c, Standings.Knows, "knows", "the one with the very long known-as phrasing", 12,
                    "knows as “the one with the very long known-as phrasing” (S12)")).ToList()))
            .ToList();

        var text = LedgerMarkdown.Format(CampaignWriteSetup.Row("big"), new Ledger(columns, rows, null, 140));

        Assert.True(text.Length <= CampaignMarkdownText.MaxChars, $"{text.Length} characters.");
        var shown = Regex.Matches(text, @"^\| f:\d+ ", RegexOptions.Multiline).Count;
        Assert.True(shown is > 0 and < 100, $"{shown} rows shown.");
        Assert.Contains($"_… and {(140 - shown).ToString(CultureInfo.InvariantCulture)} more rows; narrow with about, facts or perspectives._", text, StringComparison.Ordinal);
        Assert.EndsWith(" |\n\n_… and " + (140 - shown).ToString(CultureInfo.InvariantCulture) + " more rows; narrow with about, facts or perspectives._\n", text, StringComparison.Ordinal);
    }

    private async Task<string> Check(string slug, string text, string? perspective, bool? diegetic)
    {
        var arguments = new Dictionary<string, object?> { ["campaign"] = slug, ["action"] = "check", ["text"] = text };
        if (perspective is not null)
        {
            arguments["perspective"] = perspective;
        }

        if (diegetic is not null)
        {
            arguments["diegetic"] = diegetic;
        }

        return _server.SuccessText(await _server.Client.CallToolAsync("campaign_knowledge", arguments));
    }
}

/// <summary>
/// The Belmakor shape, built through campaign_write and campaign_knowledge: a player campaign whose PC is
/// <c>character:belmakor</c> (in the party); "The Old King" (party visibility, author alias "Keras", party alias "the
/// sorcerer king"), known to Belmakor as "the old king"; "The thing he wants" (author alias "Axiom Cage"); the fact that
/// it is the Axiom Cage, recorded unaware for the party and for Belmakor; the errand the party knows; a planned fact; and
/// the author-only reveal rule "no name, no timespan".
/// </summary>
internal static class KnowledgeScenario
{
    public sealed record Handles(string Slug, string AxiomFact, string ErrandFact, string PlannedFact);

    public static async Task<Handles> BuildAsync(McpServerHarness server, string? slug = null)
    {
        slug = CampaignWriteSetup.CreateCampaign(server, role: "player", slug: slug, myCharacter: "Belmakor Silverwind", myCharacterSlug: "belmakor");
        var refs = CampaignWriteSetup.Refs(await CampaignWriteSetup.WriteAsync(server, slug, """
            [{"op": "upsert", "kind": "character", "name": "The Old King", "visibility": "party",
              "aliases": [{"alias": "Keras", "visibility": "author"}, {"alias": "the sorcerer king", "visibility": "party"}]},
             {"op": "upsert", "kind": "item", "name": "The thing he wants", "visibility": "party", "aliases": [{"alias": "Axiom Cage", "visibility": "author"}]},
             {"op": "fact", "statement": "The thing he wants is the Axiom Cage.", "about": ["item:the-thing-he-wants"],
              "known_by": [{"who": "party", "state": "unaware"}, {"who": "character:belmakor", "state": "unaware"}]},
             {"op": "fact", "statement": "The old king wants the thing brought up from below.", "about": ["character:the-old-king"], "known_by": [{"who": "party"}]},
             {"op": "fact", "statement": "The old king's army marches next spring.", "about": ["character:the-old-king"], "canon_status": "planned", "visibility": "party"},
             {"op": "upsert", "kind": "rule", "subtype": "reveal_rule", "name": "No name, no timespan", "visibility": "author",
              "data": {"forbidden_terms": ["Keras"], "forbidden_patterns": ["<number>-year-old"], "preferred_terms": ["the old king"],
                       "note": "Belmakor never learns his name or age."}}]
            """));
        await CampaignWriteSetup.CallAsync(server, "campaign_knowledge", $$"""
            {"campaign": "{{slug}}", "action": "record", "targets": ["character:the-old-king"],
             "knowers": [{"who": "character:belmakor", "state": "met", "known_as": "the old king"}]}
            """);
        return new Handles(slug, refs[2], refs[3], refs[4]);
    }
}

/// <summary>
/// The One Piece seal gate in miniature: an author-only secret; a planned fact the gate waits for (<c>after</c>); a fact
/// it must land with (<c>with</c>); and the gated fact, restricted, linked about the secret.
/// </summary>
internal static class GateScenario
{
    public sealed record Handles(string Slug, string Axe, string Nadar, string Seal);

    public static async Task<Handles> BuildAsync(McpServerHarness server)
    {
        var slug = CampaignWriteSetup.CreateCampaign(server);
        var refs = CampaignWriteSetup.Refs(await CampaignWriteSetup.WriteAsync(server, slug, """
            [{"op": "upsert", "kind": "secret", "name": "Fruits are the seal", "visibility": "author"},
             {"op": "fact", "statement": "The axe is assembled.", "canon_status": "planned", "visibility": "party"},
             {"op": "fact", "statement": "Nadar routes the fruits."}]
            """));
        var seal = CampaignWriteSetup.Refs(await CampaignWriteSetup.WriteAsync(server, slug, $$"""
            [{"op": "fact", "statement": "Eating a fruit breaks the seal.", "about": ["secret:fruits-are-the-seal"],
              "gate": {"after": ["{{refs[1]}}"], "with": ["{{refs[2]}}"], "note": "Not before the axe."} }]
            """))[0];
        return new Handles(slug, refs[1], refs[2], seal);
    }
}

/// <summary>
/// The check's review sections, on the Belmakor shape: another campaign's "Baal" that the old king is linked
/// <c>same_as</c> to (the firewall); "Captain Vortigern", whom Belmakor has not met; a half-truth Belmakor believes; a
/// tower fact superseded by a correction, with a party fact resting on it; and Belmakor's own ambition, known only to him.
/// </summary>
internal static class ReviewScenario
{
    public sealed record Handles(string Slug, string Other, string Mistaken, string Old, string Corrected, string Dependent, string Ambition);

    public static async Task<Handles> BuildAsync(McpServerHarness server)
    {
        var belmakor = await KnowledgeScenario.BuildAsync(server);
        var other = CampaignWriteSetup.CreateCampaign(server);
        await CampaignWriteSetup.WriteAsync(server, other, """[{"op": "upsert", "kind": "character", "name": "Baal", "visibility": "party"}]""");
        var refs = CampaignWriteSetup.Refs(await CampaignWriteSetup.WriteAsync(server, belmakor.Slug, $$"""
            [{"op": "link", "from": "character:the-old-king", "rel": "same_as", "to": "{{other}}/character:baal"},
             {"op": "upsert", "kind": "character", "name": "Captain Vortigern", "visibility": "restricted"},
             {"op": "fact", "statement": "It broke because I was slow.", "truth": "partial", "known_by": [{"who": "character:belmakor", "state": "believes"}]},
             {"op": "fact", "statement": "The tower fell a thousand years ago.", "canon_status": "canon", "visibility": "party"},
             {"op": "fact", "statement": "The tower fell nine centuries ago.", "canon_status": "ruled", "visibility": "party"},
             {"op": "fact", "statement": "Belmakor wants to rule the dead world below.", "about": ["character:belmakor"], "known_by": [{"who": "character:belmakor"}]}]
            """));
        var dependent = CampaignWriteSetup.Refs(await CampaignWriteSetup.WriteAsync(server, belmakor.Slug, $$"""
            [{"op": "fact", "statement": "The tower's builders are all gone.", "depends_on": ["{{refs[3]}}"], "visibility": "party", "known_by": [{"who": "party"}]}]
            """))[0];
        await CampaignWriteSetup.WriteAsync(server, belmakor.Slug, $$"""[{"op": "fact", "ref": "{{refs[3]}}", "superseded_by": "{{refs[4]}}"}]""");
        return new Handles(belmakor.Slug, other, refs[2], refs[3], refs[4], dependent, refs[5]);
    }
}
