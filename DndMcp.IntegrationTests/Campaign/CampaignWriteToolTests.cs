using System.Text.Json;
using System.Text.RegularExpressions;
using DndMcp.Formatting.Campaign;
using DndMcp.Hosting;
using DndMcp.IntegrationTests.Infrastructure;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Invariant: <c>campaign_write</c> applies a batch of typed ops through the real server and says exactly what happened:
/// the batch id in full with the undo call, what each op did, the warnings (gate conditions with their severity,
/// supersession dependents with depth and via, name twins) and the consequences; a dry run is labelled as one, shows the
/// codes the real run will assign, and writes nothing.
///
/// <para>
/// Why it fails silently: every layer here can go wrong while the repository tests stay green. The schema (a field lost
/// from <c>CampaignOpSpec</c>'s published shape is simply never sent), the argument guard (an unknown field inside an op
/// would bind and vanish), the result text (a dropped undo line leaves a batch nobody can reverse; a dry run that reads
/// like an applied batch gets reported to the author as done), and the output cap (a batch id cut off by it). Each test
/// drives the tool through the MCP client and asserts on the text the model receives.
/// </para>
/// </summary>
public sealed partial class CampaignWriteToolTests : IClassFixture<McpServerHarness>
{
    private readonly McpServerHarness _server;

    public CampaignWriteToolTests(McpServerHarness server)
    {
        _server = server;
    }

    [GeneratedRegex("Batch `([0-9a-f-]{36})`")]
    private static partial Regex BatchIdRegex();

    /// <summary>
    /// The JSON arguments of every <c>&lt;tool&gt; {…}</c> call a result prints, braces balanced, so a call whose
    /// arguments hold objects (a record call's knowers) is copied whole, exactly as the model would send it.
    /// </summary>
    private static IReadOnlyList<string> PrintedJsonCalls(string result, string tool)
    {
        var calls = new List<string>();
        for (var at = result.IndexOf(tool + " {", StringComparison.Ordinal); at >= 0; at = result.IndexOf(tool + " {", at + 1, StringComparison.Ordinal))
        {
            var start = at + tool.Length + 1;
            var depth = 0;
            var inString = false;
            for (var i = start; i < result.Length; i++)
            {
                var ch = result[i];
                if (inString)
                {
                    if (ch == '\\')
                    {
                        i++;
                    }
                    else if (ch == '"')
                    {
                        inString = false;
                    }
                }
                else if (ch == '"')
                {
                    inString = true;
                }
                else if (ch == '{')
                {
                    depth++;
                }
                else if (ch == '}' && --depth == 0)
                {
                    calls.Add(result[start..(i + 1)]);
                    break;
                }
            }
        }

        return calls;
    }

    [Fact]
    public async Task ToolSurface_CampaignWrite_HasTheContractsAnnotationsTitleAndDescribedSchema()
    {
        var tool = await CampaignWriteSetup.ToolAsync(_server, "campaign_write");
        var annotations = tool.ProtocolTool.Annotations!;

        Assert.Equal((false, true, false, false),
            (annotations.ReadOnlyHint!.Value, annotations.DestructiveHint!.Value, annotations.IdempotentHint!.Value, annotations.OpenWorldHint!.Value));
        Assert.Equal("Write to a campaign", tool.ProtocolTool.Title);
        CampaignWriteSetup.AssertDescribedAtEveryDepth("campaign_write", tool.JsonSchema.GetProperty("properties"));
    }

    [Fact]
    public async Task ToolDescription_CampaignWrite_NamesEveryArgumentAndEachOpsRequirementsWithinTheLimit()
    {
        var tool = await CampaignWriteSetup.ToolAsync(_server, "campaign_write");
        var description = tool.Description!;

        Assert.True(description.Length <= CampaignWriteSetup.DescriptionLimit, $"{description.Length} characters");
        foreach (var property in tool.JsonSchema.GetProperty("properties").EnumerateObject())
        {
            Assert.Contains(property.Name, description, StringComparison.Ordinal);
        }

        foreach (var op in new[] { "upsert:", "delete, restore:", "link, unlink:", "fact:", "status:", "tick:", "answer:", "objective:" })
        {
            Assert.Contains(op, description, StringComparison.Ordinal);
        }

        Assert.Contains("\nExample: {", description, StringComparison.Ordinal);
        Assert.EndsWith(CampaignWriteTools.Example, description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolInputSchema_CampaignWrite_TypesOpsAndStaysUnderTheSchemaBudget()
    {
        // ops is published typed (every op field, the gate, the knowers) so the guard checks each item against it; the
        // contract allows that only while the whole input schema stays under 24,000 characters. Measured 13,581 when
        // written. Past the budget, publish ops untyped with [CheckedAs(typeof(CampaignOpSpec[]))]; the upper pin of 16,000
        // makes growth toward it a deliberate change.
        var tool = await CampaignWriteSetup.ToolAsync(_server, "campaign_write");
        var schema = tool.JsonSchema.GetRawText();
        var ops = tool.JsonSchema.GetProperty("properties").GetProperty("ops");

        Assert.True(schema.Length < 16_000, $"campaign_write's input schema is {schema.Length} characters (budget 24,000).");
        Assert.True(schema.Length > 12_000, $"campaign_write's input schema is {schema.Length} characters: is ops still typed?");
        var fields = ops.GetProperty("items").GetProperty("properties");
        foreach (var field in new[] { "op", "ref", "statement", "known_by", "gate", "supersedes", "answered_by" })
        {
            Assert.True(fields.TryGetProperty(field, out _), $"ops items have no {field}.");
        }

        Assert.True(fields.GetProperty("gate").TryGetProperty("properties", out var gate) && gate.TryGetProperty("routes", out _));
        Assert.True(fields.GetProperty("known_by").GetProperty("items").GetProperty("properties").TryGetProperty("known_as", out _));
    }

    [Fact]
    public async Task CallTool_TheDescriptionsExample_IsADryRunThatWritesNothing()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);
        using var example = JsonDocument.Parse(CampaignWriteTools.Example);
        var arguments = example.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        arguments["campaign"] = slug;

        var preview = _server.SuccessText(await _server.Client.CallToolAsync("campaign_write", arguments));
        arguments["dry_run"] = false;
        var applied = _server.SuccessText(await _server.Client.CallToolAsync("campaign_write", arguments));

        Assert.StartsWith($"# Dry run: campaign_write, 2 ops ({slug}): nothing written\n\n**Dry run: nothing was written and no batch exists.**", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("campaign_history", preview, StringComparison.Ordinal);
        // The real run creates both: the dry run left nothing behind.
        Assert.Contains("| 1 | upsert | character:iron-guts | created |", applied, StringComparison.Ordinal);
        Assert.Matches(@"\| 2 \| fact \| f:\d+ \| created \|", applied);
    }

    [Fact]
    public async Task CallTool_TypicalBatch_PrintsTheBatchIdInFullWithTheUndoCallAndEveryOp()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);

        var text = await CampaignWriteSetup.WriteAsync(_server, slug, """
            [{"op": "upsert", "kind": "character", "name": "Iron Guts", "subtype": "npc", "visibility": "party"},
             {"op": "upsert", "kind": "location", "name": "Sky Fair", "visibility": "party"},
             {"op": "link", "from": "character:iron-guts", "rel": "located_in", "to": "location:sky-fair"},
             {"op": "fact", "statement": "Iron Guts owes the band a favour.", "about": ["character:iron-guts"], "known_by": [{"who": "party"}]}]
            """, "\"reason\": \"Session 3 recap\"");

        var id = BatchIdRegex().Match(text).Groups[1].Value;
        Assert.StartsWith($"# campaign_write: 4 ops applied ({slug})\n\nBatch `{id}`.", text, StringComparison.Ordinal);
        Assert.Contains($"To undo it: campaign_history {{\"action\": \"undo\", \"batch_id\": \"{id}\", \"campaign\": \"{slug}\"}}.", text, StringComparison.Ordinal);
        Assert.Contains("| 1 | upsert | character:iron-guts | created |", text, StringComparison.Ordinal);
        Assert.Contains("| 3 | link | character:iron-guts located_in location:sky-fair | linked |", text, StringComparison.Ordinal);
        Assert.Matches(@"\| 4 \| fact \| f:\d+ \| created \| statement, about, known_by party \|", text);
        Assert.DoesNotContain("## Warnings", text, StringComparison.Ordinal);
        Assert.True(text.Length < 2_000, $"A typical batch result is {text.Length} characters.");
        // The whole text, so nothing else (an empty section, an "… and 0 more" line) creeps into every result.
        var fact = CampaignWriteSetup.Refs(text)[3];
        Assert.Equal(
            $"# campaign_write: 4 ops applied ({slug})\n\n" +
            $"Batch `{id}`. To undo it: campaign_history {{\"action\": \"undo\", \"batch_id\": \"{id}\", \"campaign\": \"{slug}\"}}.\n\n" +
            "| # | Op | Ref | Outcome | Changed |\n|---|---|---|---|---|\n" +
            "| 1 | upsert | character:iron-guts | created | kind, name, subtype, visibility |\n" +
            "| 2 | upsert | location:sky-fair | created | kind, name, visibility |\n" +
            "| 3 | link | character:iron-guts located_in location:sky-fair | linked | — |\n" +
            $"| 4 | fact | {fact} | created | statement, about, known_by party |\n",
            text);
    }

    [Fact]
    public async Task CallTool_DryRunOfAProposedFact_ShowsTheCodeTheRealRunAssignsAndPersistsNothing()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);
        const string Ops = """[{"op": "fact", "statement": "The harbourmaster is a retired pirate.", "canon_status": "proposed"}]""";

        var first = await CampaignWriteSetup.WriteAsync(_server, slug, Ops, "\"dry_run\": true");
        var second = await CampaignWriteSetup.WriteAsync(_server, slug, Ops, "\"dry_run\": true");
        var real = await CampaignWriteSetup.WriteAsync(_server, slug, Ops);

        Assert.Matches(@"\| 1 \| fact \| f:\d+ · F1 \| created \|", first);
        Assert.Equal(first, second);
        Assert.Matches(@"\| 1 \| fact \| f:\d+ · F1 \| created \|", real);
        Assert.Matches(BatchIdRegex(), real);
        Assert.DoesNotMatch(BatchIdRegex(), first);
    }

    /// <summary>
    /// The undo call a write prints (campaign_write's, and campaign_knowledge's through the same batch line) names its
    /// campaign, so sent exactly as printed it undoes that batch while another campaign is current: a write that named its
    /// campaign, then a <c>use</c> of another. Printed without <c>campaign</c> it failed there ("No batch … in this campaign").
    /// </summary>
    [Theory]
    [InlineData("campaign_write")]
    [InlineData("campaign_knowledge")]
    public async Task CallTool_UndoCallAsPrinted_UndoesTheBatchWhileAnotherCampaignIsCurrent(string tool)
    {
        await CampaignWriteSetup.WithServerAsync(async server =>
        {
            var slug = CampaignWriteSetup.CreateCampaign(server);
            var other = CampaignWriteSetup.CreateCampaign(server);
            var created = await CampaignWriteSetup.WriteAsync(server, slug, """[{"op": "upsert", "kind": "character", "name": "Iron Guts"}]""");
            var text = tool == "campaign_write"
                ? created
                : await CampaignWriteSetup.CallAsync(server, "campaign_knowledge",
                    $$"""{"campaign": "{{slug}}", "action": "record", "targets": ["character:iron-guts"], "knowers": [{"who": "party", "state": "met"}]}""");
            var campaigns = server.Services.GetRequiredService<CampaignService>();
            campaigns.Use(other);

            var undo = await CampaignWriteSetup.CallAsync(server, "campaign_history", Assert.Single(CampaignWriteSetup.PrintedCalls(text, "campaign_history")));

            // Found and reversed in its own campaign (the current one has no such batch), and the current one is untouched.
            Assert.StartsWith("# Undo of batch " + BatchIdRegex().Match(text).Groups[1].Value, undo, StringComparison.Ordinal);
            Assert.Equal(other, campaigns.Resolve(null).Slug);
        });
    }

    [Fact]
    public async Task CallTool_UpsertThatChangesNothing_SaysSoWithoutAnUndoHint()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);
        const string Ops = """[{"op": "upsert", "kind": "character", "name": "Iron Guts", "summary": "A dwarf smith."}]""";
        await CampaignWriteSetup.WriteAsync(_server, slug, Ops);

        var again = await CampaignWriteSetup.WriteAsync(_server, slug, Ops);

        Assert.Contains("Nothing changed: everything already matched what is stored, so there is no batch to undo.", again, StringComparison.Ordinal);
        Assert.Contains("| 1 | upsert | character:iron-guts | unchanged |", again, StringComparison.Ordinal);
        Assert.DoesNotContain("campaign_history", again, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_GatedFactGivenToTheParty_IsAppliedWithSeverityLabelledWarningsAndTheSecretsNewStatus()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);
        var setup = CampaignWriteSetup.Refs(await CampaignWriteSetup.WriteAsync(_server, slug, """
            [{"op": "upsert", "kind": "secret", "name": "Fruits are the seal", "visibility": "author"},
             {"op": "fact", "statement": "The axe is assembled.", "canon_status": "planned", "visibility": "party"},
             {"op": "fact", "statement": "Nadar routes the fruits.", "visibility": "restricted"},
             {"op": "fact", "statement": "The party has no uneaten fruits.", "canon_status": "planned"}]
            """));
        var (axe, nadar, fruits) = (setup[1], setup[2], setup[3]);
        var created = await CampaignWriteSetup.WriteAsync(_server, slug, $$"""
            [{"op": "fact", "statement": "Eating a fruit breaks the seal.", "about": ["secret:fruits-are-the-seal"],
              "gate": {"after": ["{{axe}}"], "with": ["{{nadar}}"], "prefer": ["{{fruits}}"], "forbidden_terms": ["seal"], "note": "Reveal order: do not break it"} }]
            """);
        var seal = CampaignWriteSetup.Refs(created)[0];
        // The gate comes back as stored, with fact handles, so the author can see it was read as meant.
        Assert.Contains($"\nGates as stored:\n- {seal}: after {axe} · with {nadar} · prefer {fruits} · forbidden_terms seal · note: Reveal order: do not break it\n", created, StringComparison.Ordinal);

        var text = await CampaignWriteSetup.WriteAsync(_server, slug, $$"""
            [{"op": "fact", "ref": "{{seal}}", "known_by": [{"who": "party", "state": "knows"}]}]
            """);

        Assert.Contains($"| 1 | fact | {seal} | updated | known_by party |", text, StringComparison.Ordinal);
        var warnings = text[text.IndexOf("## Warnings (3)\nApplied anyway; read them before the table does.\n", StringComparison.Ordinal)..];
        Assert.Contains(
            $"- **warning** · gate (after) · ops item 1 · {seal} → party: {seal} reached party before its gate's after is met: {axe} not in play yet (applied anyway).\n" +
            "  - Gate note: Reveal order: do not break it\n", warnings, StringComparison.Ordinal);
        Assert.Contains($"- **warning** · gate (with) · ops item 1 · {seal} → party: {seal} must land with {nadar}", warnings, StringComparison.Ordinal);
        Assert.Contains($"- **advisory** · gate (prefer) · ops item 1 · {seal} → party: Advisory: {seal}'s gate would rather {fruits} were in play first", warnings, StringComparison.Ordinal);
        // Warnings before advisories, whatever order the writer found them in.
        Assert.True(warnings.LastIndexOf("**warning**", StringComparison.Ordinal) < warnings.IndexOf("**advisory**", StringComparison.Ordinal));
        Assert.True(text.Contains("## Consequences (1)\n- secret status: secret:fruits-are-the-seal: hidden → revealed.\n", StringComparison.Ordinal), text);
    }

    /// <summary>
    /// Kills S10 (FH10, M16): a write result lists its warnings before its advisories whatever order the ops raised them in
    /// (here op 1 raises the advisory "use answer" and op 2 the warning that a character died). The gate case above has the
    /// writer raise them in that order already, so it cannot tell the sort from no sort.
    /// </summary>
    [Fact]
    public async Task CallTool_AdvisoryRaisedBeforeAWarning_IsListedAfterIt()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);
        await CampaignWriteSetup.WriteAsync(_server, slug,
            """[{"op": "upsert", "kind": "question", "name": "Who sank the Wavecutter?"}, {"op": "upsert", "kind": "character", "name": "Tristan"}]""");

        var text = await CampaignWriteSetup.WriteAsync(_server, slug,
            """[{"op": "status", "ref": "question:who-sank-the-wavecutter", "status": "answered"}, {"op": "status", "ref": "character:tristan", "status": "dead"}]""");

        var warning = text.IndexOf("- **warning** · character dead", StringComparison.Ordinal);
        var advisory = text.IndexOf("- **advisory** · use answer", StringComparison.Ordinal);
        Assert.True(warning >= 0 && advisory >= 0, text);
        Assert.True(warning < advisory, text);
    }

    [Fact]
    public async Task CallTool_SupersedingAFact_ListsItsDependentsWithDepthAndViaAndTheEntitiesToRecheck()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);
        var facts = CampaignWriteSetup.Refs(await CampaignWriteSetup.WriteAsync(_server, slug, """
            [{"op": "upsert", "kind": "question", "name": "How old is the lineage?", "slug": "q21"},
             {"op": "fact", "statement": "The fight was 500 years ago.", "canon_status": "canon"},
             {"op": "fact", "statement": "The fight was 1,000 years ago.", "canon_status": "ruled"}]
            """));
        var (old, corrected) = (facts[1], facts[2]);
        var near = CampaignWriteSetup.Refs(await CampaignWriteSetup.WriteAsync(_server, slug,
            $$"""[{"op": "fact", "statement": "The lineage covers the gap.", "depends_on": ["{{old}}"], "about": ["question:q21"]}]"""))[0];
        var far = CampaignWriteSetup.Refs(await CampaignWriteSetup.WriteAsync(_server, slug,
            $$"""[{"op": "fact", "statement": "Every heir is long-lived.", "depends_on": ["{{near}}"]}]"""))[0];

        var text = await CampaignWriteSetup.WriteAsync(_server, slug, $$"""[{"op": "fact", "ref": "{{old}}", "superseded_by": "{{corrected}}"}]""");

        Assert.Contains(
            $"- **warning** · superseded · ops item 1: {old} is superseded by {corrected}; 2 facts rest on it, left unchanged for you to re-check:\n" +
            $"  - {near}: depth 1, via {old}\n" +
            $"  - {far}: depth 2, via {near}\n" +
            "  - Entities to re-check: question:q21\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_RenameOntoAnotherEntitysName_WarnsOfTheNameTwin()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);
        await CampaignWriteSetup.WriteAsync(_server, slug, """
            [{"op": "upsert", "kind": "character", "name": "Tomas"}, {"op": "upsert", "kind": "character", "name": "Tobias"}]
            """);

        var text = await CampaignWriteSetup.WriteAsync(_server, slug, """[{"op": "upsert", "ref": "character:tobias", "name": "Tomas"}]""");

        Assert.Matches(@"- \*\*warning\*\* · name twin · ops item 1: e:\d+ now shares its name with e:\d+ \(another character\)", text);
        Assert.Contains("- **warning** · slug kept · ops item 1:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_TickingAClockFull_ReportsTheClockFilledConsequence()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);
        await CampaignWriteSetup.WriteAsync(_server, slug, """
            [{"op": "upsert", "kind": "clock", "name": "Blood moon", "clock": {"segments": 2, "on_fill_md": "The moon rises red."}}]
            """);

        var text = await CampaignWriteSetup.WriteAsync(_server, slug, """[{"op": "tick", "ref": "clock:blood-moon", "amount": 3}]""");

        Assert.Contains("| 1 | tick | clock:blood-moon | ticked |", text, StringComparison.Ordinal);
        Assert.Contains("- **warning** · clock clamped · ops item 1:", text, StringComparison.Ordinal);
        Assert.Contains("- clock filled: Clock Blood moon (clock:blood-moon) filled: The moon rises red.", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""[{"op": "upsert", "kind": "character", "name": "X", "stauts": "dead"}]""", "argument 'ops' item 1 has unknown field 'stauts' (fields: op, ref, kind, name, ")]
    [InlineData("""[{"op": "fact", "statement": "X", "known_by": [{"who": "party", "knownas": "the king"}]}]""", "argument 'ops' item 1 field 'known_by' item 1 has unknown field 'knownas'")]
    [InlineData("""[{"op": "fact", "statement": "X", "gate": {"afterr": ["f:1"]}}]""", "argument 'ops' item 1 field 'gate' has unknown field 'afterr'")]
    [InlineData("""[{"op": "upsert", "kind": "character", "name": "X", "sort_key": "high"}]""", "argument 'ops' item 1 field 'sort_key' should be number")]
    [InlineData("""{"op": "upsert"}""", "argument 'ops' should be array")]
    public async Task CallTool_OpsTheSchemaRefuses_GuardNamesTheItemAndField(string ops, string problem)
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);

        var text = _server.ErrorText(await _server.CallToolJsonAsync("campaign_write", $$"""{"campaign": "{{slug}}", "ops": {{ops}}}"""));

        Assert.StartsWith("An error occurred invoking 'campaign_write': Invalid arguments: ", text, StringComparison.Ordinal);
        Assert.Contains(problem, text, StringComparison.Ordinal);
        Assert.Contains("campaign_write accepts: ops (array of object, required), campaign (string, optional), session (integer, optional), reason (string, optional), dry_run (boolean, optional).", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""[{"op": "upsert", "kind": "character", "name": "Iron Guts", "statement": "X"}]""",
        "Invalid ops: ops item 1 (upsert character \"Iron Guts\"): does not take \"statement\"; upsert takes ref, kind, name, ")]
    [InlineData("""[{"op": "link", "from": "character:a", "rel": "ally_of", "to": "character:b", "gate": {"after": ["f:1"]}}]""",
        "Invalid ops: ops item 1 (link character:a ally_of character:b): does not take \"gate\"; link takes from, rel, to, ")]
    [InlineData("""[{"kind": "character", "name": "X"}]""", "Invalid ops: ops item 1: op is required")]
    [InlineData("""[{"op": "frobnicate"}]""", "Invalid ops: ops item 1: op \"frobnicate\" is not an op; ops are upsert, delete, restore, link, unlink, fact, status, objective, tick, answer.")]
    [InlineData("""[{"op": "fact", "statement": "X", "canon_status": "maybe"}, {"op": "tick"}]""", "Invalid ops (2 problems):\n- ops item 1 (fact): canon_status \"maybe\" is not a canon status; give canon, played, ruled, lean, planned, proposed, accepted, struck or superseded.\n- ops item 2 (tick): ref is required")]
    public async Task CallTool_OpsTheDomainRefuses_ComeBackNumberedAndNothingIsWritten(string ops, string start)
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);

        var text = _server.ErrorText(await _server.CallToolJsonAsync("campaign_write", $$"""{"campaign": "{{slug}}", "ops": {{ops}}}"""));

        Assert.StartsWith("An error occurred invoking 'campaign_write': " + start, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_ThirdOpFails_TheWholeBatchIsRefusedAndNothingIsWritten()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);

        var text = _server.ErrorText(await _server.CallToolJsonAsync("campaign_write", $$"""
            {"campaign": "{{slug}}", "ops": [
              {"op": "upsert", "kind": "character", "name": "Iron Guts"},
              {"op": "upsert", "kind": "location", "name": "Sky Fair"},
              {"op": "status", "ref": "character:nobody", "status": "dead"}]}
            """));
        var after = await CampaignWriteSetup.WriteAsync(_server, slug, """[{"op": "upsert", "kind": "character", "name": "Iron Guts"}]""");

        Assert.Contains("ops item 3", text, StringComparison.Ordinal);
        Assert.Contains("| 1 | upsert | character:iron-guts | created |", after, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"campaign\": null, \"session\": null, \"reason\": null, \"dry_run\": null")]
    [InlineData("\"session\": null")]
    public async Task CallTool_NullForEveryOptionalArgument_MeansTheDefaults(string rest)
    {
        // A harness of its own: with exactly one campaign, no campaign argument means that one.
        await CampaignWriteSetup.WithServerAsync(async server =>
        {
            var slug = CampaignWriteSetup.CreateCampaign(server);

            var text = server.SuccessText(await server.CallToolJsonAsync("campaign_write",
                $$"""{"ops": [{"op": "upsert", "kind": "character", "name": "Iron Guts"}], {{rest}}}"""));

            Assert.StartsWith($"# campaign_write: 1 op applied ({slug})", text, StringComparison.Ordinal);
            Assert.Matches(BatchIdRegex(), text);
        });
    }

    [Fact]
    public async Task CallTool_NoCampaignsYet_SaysHowToCreateOne()
    {
        await CampaignWriteSetup.WithServerAsync(async server =>
        {
            var text = server.ErrorText(await server.CallToolJsonAsync("campaign_write", """{"ops": [{"op": "upsert", "kind": "character", "name": "X"}]}"""));

            Assert.StartsWith("An error occurred invoking 'campaign_write': There are no campaigns yet. Create one with campaign {", text, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(server.DataDirectory, "campaigns.db")), "A refused write must not create campaigns.db.");
        });
    }

    [Fact]
    public async Task CallTool_SessionThatDoesNotExist_IsRefusedRatherThanFiledNowhere()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);

        var text = _server.ErrorText(await _server.CallToolJsonAsync("campaign_write",
            $$"""{"campaign": "{{slug}}", "session": 7, "ops": [{"op": "upsert", "kind": "character", "name": "X"}]}"""));

        // Review U08: a missing session is usually tonight's, so start is offered first, then record_past and plan.
        Assert.Equal("An error occurred invoking 'campaign_write': session 7: no such session in this campaign. Start it if it is being " +
            $"played now: campaign_session {{\"action\": \"start\", \"session\": 7, \"campaign\": \"{slug}\"}}. If it was played already, send " +
            "the same call with \"record_past\" instead of \"start\"; if it is still to come, with \"plan\".", text);
    }

    /// <summary>
    /// A write's session refusals print the campaign_session call that fixes them, naming the session and the campaign the
    /// write named: sent as printed while another campaign is current, the start call starts that session in that campaign,
    /// for a write's session context and for a fact's established_session alike, and (FH3) makes the write's campaign the
    /// current one, so the write sent again without campaign goes there too. Printed without campaign it acted on the
    /// session in the current campaign, and the write, sent again, still failed.
    /// </summary>
    [Theory]
    [InlineData("\"session\": 7", """[{"op": "upsert", "kind": "character", "name": "X"}]""", "# Session 7 started")]
    [InlineData(null, """[{"op": "fact", "statement": "The bridge fell.", "established_session": 7}]""", "# Session 7 started")]
    public async Task CallTool_SessionRefusalsCallAsPrinted_ActsOnTheWritesCampaignWhileAnotherIsCurrent(string? session, string ops, string outcome)
    {
        await CampaignWriteSetup.WithServerAsync(async server =>
        {
            var slug = CampaignWriteSetup.CreateCampaign(server);
            var other = CampaignWriteSetup.CreateCampaign(server);
            var campaigns = server.Services.GetRequiredService<CampaignService>();
            campaigns.Use(other);

            var refusal = await CampaignWriteSetup.FailAsync(server, "campaign_write",
                $$"""{"campaign": "{{slug}}", {{(session is null ? string.Empty : session + ", ")}}"ops": {{ops}}}""");
            var text = await CampaignWriteSetup.CallAsync(server, "campaign_session", Assert.Single(CampaignWriteSetup.PrintedCalls(refusal, "campaign_session")));

            Assert.StartsWith($"{outcome} ({slug})\n", text, StringComparison.Ordinal);
            Assert.StartsWith($"# Sessions: {other} (0)\n", await CampaignWriteSetup.CallAsync(server, "campaign_session",
                $$"""{"campaign": "{{other}}", "action": "list"}"""), StringComparison.Ordinal);
            Assert.Equal(slug, campaigns.Resolve(null).Slug);
        });
    }

    /// <summary>
    /// An undo of a batch the campaign does not have (it is another campaign's) is refused with a since call naming the
    /// campaign searched, so sent as printed it lists that campaign's batches whichever is current, and the refusal says the
    /// other campaign's batch is reached by naming that campaign. Printed unquoted and without campaign, the call was not
    /// JSON and listed the current campaign's batches.
    /// </summary>
    [Fact]
    public async Task CallTool_UndoOfAnotherCampaignsBatch_PrintsASinceCallForTheCampaignItSearched()
    {
        await CampaignWriteSetup.WithServerAsync(async server =>
        {
            var slug = CampaignWriteSetup.CreateCampaign(server);
            var other = CampaignWriteSetup.CreateCampaign(server);
            await CampaignWriteSetup.WriteAsync(server, slug, """[{"op": "upsert", "kind": "character", "name": "Iron Guts"}]""");
            var theirs = BatchIdRegex().Match(await CampaignWriteSetup.WriteAsync(server, other, """[{"op": "upsert", "kind": "character", "name": "Serif"}]""")).Groups[1].Value;
            server.Services.GetRequiredService<CampaignService>().Use(other);

            var refusal = await CampaignWriteSetup.FailAsync(server, "campaign_history", $$"""{"campaign": "{{slug}}", "action": "undo", "batch_id": "{{theirs}}"}""");
            var since = await CampaignWriteSetup.CallAsync(server, "campaign_history", Assert.Single(CampaignWriteSetup.PrintedCalls(refusal, "campaign_history")));

            Assert.Contains($"No batch {theirs} in this campaign. campaign_history ", refusal, StringComparison.Ordinal);
            Assert.EndsWith("a batch of another campaign is found by passing that campaign's slug as campaign.", refusal, StringComparison.Ordinal);
            Assert.StartsWith($"# History of {slug}", since, StringComparison.Ordinal);
            Assert.Contains("character:iron-guts", since, StringComparison.Ordinal);
            Assert.DoesNotContain("character:serif", since, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Review UR3: f:&lt;n&gt; numbers are shared by every campaign in the file, so a new campaign's first fact is not
    /// f:1, and a batch that gates on the fact it just made by a guessed number names another campaign's fact and is
    /// refused. The refusal says the numbers are shared and what works instead, a code given to the fact, and the same
    /// batch with a code is applied with its gate pointing at the fact it made.
    /// </summary>
    [Fact]
    public async Task CallTool_GateOnAnotherCampaignsFactNumber_RefusalSaysNumbersAreSharedAndToUseACode()
    {
        var first = CampaignWriteSetup.CreateCampaign(_server);
        var theirs = CampaignWriteSetup.Refs(await CampaignWriteSetup.WriteAsync(_server, first, """[{"op": "fact", "statement": "One."}]"""))[0];
        var second = CampaignWriteSetup.CreateCampaign(_server);

        var refusal = await CampaignWriteSetup.FailAsync(_server, "campaign_write", $$$"""
            {"campaign": "{{{second}}}", "ops": [{"op": "fact", "statement": "The axe is assembled.", "canon_status": "planned"},
              {"op": "fact", "statement": "The seal holds.", "gate": {"after": ["{{{theirs}}}"]}}]}
            """);
        var coded = await CampaignWriteSetup.WriteAsync(_server, second, """
            [{"op": "fact", "statement": "The axe is assembled.", "canon_status": "planned", "code": "A1"},
             {"op": "fact", "statement": "The seal holds.", "gate": {"after": ["A1"]}}]
            """);

        Assert.Equal($"An error occurred invoking 'campaign_write': Invalid ops: ops item 2 (fact): gate: no fact {theirs} in this campaign; give f:<n> " +
                     "or its code. f:<n> numbers are shared by all campaigns; to use a fact made earlier in this batch, give it a code (code \"A1\") and " +
                     "use that.", refusal);
        var made = CampaignWriteSetup.Refs(coded);
        Assert.Contains($"\nGates as stored:\n- {made[1]}: after {made[0]}\n", coded, StringComparison.Ordinal);
    }

    /// <summary>
    /// Review UR1: telling the party the true name of an NPC it knows under another name warns with the record call that
    /// makes the party's known_as the true name, naming the campaign and keeping the party's state; sent exactly as
    /// printed it ends the disguise, and a party member may then say the name (the check passes). Telling the fact alone
    /// leaves the check failing. Before, the warning only advised rewording the fact, and nothing said how to end a
    /// disguise: a blank known_as is refused and leaving it out keeps the old one.
    /// </summary>
    [Fact]
    public async Task CallTool_TellingThePartyADisguisedNpcsTrueName_PrintsTheRecordCallThatLetsThePartySayIt()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server, role: "player", myCharacter: "Belmakor");
        await CampaignWriteSetup.WriteAsync(_server, slug, """
            [{"op": "upsert", "kind": "character", "name": "Keras", "visibility": "party",
              "known_by": [{"who": "party", "state": "met", "known_as": "the ancient sorcerer king"}]}]
            """);
        var check = $$"""{"campaign": "{{slug}}", "action": "check", "perspective": "character:belmakor", "diegetic": true, "text": "Keras, come down"}""";

        var told = await CampaignWriteSetup.WriteAsync(_server, slug, """
            [{"op": "fact", "statement": "The ancient sorcerer king's name is Keras.", "about": ["character:keras"], "known_by": [{"who": "party"}]}]
            """);
        var before = await CampaignWriteSetup.CallAsync(_server, "campaign_knowledge", check);
        var call = Assert.Single(PrintedJsonCalls(told, "campaign_knowledge"));
        await CampaignWriteSetup.CallAsync(_server, "campaign_knowledge", call);
        var after = await CampaignWriteSetup.CallAsync(_server, "campaign_knowledge", check);

        Assert.Contains("- **warning** · hidden name · ops item 1: ", told, StringComparison.Ordinal);
        Assert.Equal($$"""{"action": "record", "campaign": "{{slug}}", "targets": ["character:keras"], "knowers": [{"who": "party", "state": "met", "known_as": "Keras"}]}""",
            call);
        Assert.DoesNotContain(": pass", before.Split('\n')[0], StringComparison.Ordinal);
        Assert.StartsWith($"# Knowledge check: pass ({slug})\n", after, StringComparison.Ordinal);
    }

    /// <summary>
    /// Review UR1: known_as's description says how to end a disguise, because leaving it out keeps the current one and a
    /// blank one is refused: give the true name. campaign_write's known_by and campaign_knowledge's knowers share it.
    /// </summary>
    [Theory]
    [InlineData("campaign_write")]
    [InlineData("campaign_knowledge")]
    public async Task ToolInputSchema_KnownAs_SaysTheTrueNameEndsADisguise(string tool)
    {
        var schema = (await CampaignWriteSetup.ToolAsync(_server, tool)).JsonSchema.GetProperty("properties");
        var knower = tool == "campaign_write"
            ? schema.GetProperty("ops").GetProperty("items").GetProperty("properties").GetProperty("known_by").GetProperty("items")
            : schema.GetProperty("knowers").GetProperty("items");

        Assert.Equal("The name or phrasing this knower uses, e.g. \"the old king\". Omit it to keep the current one (a new row: when theirs is the true " +
                     "one); when the knower now uses the true name, give the true name, e.g. \"Keras\", which ends the disguise.",
            knower.GetProperty("properties").GetProperty("known_as").GetProperty("description").GetString());
    }

    /// <summary>
    /// Review U09 (undo): an undo that shows the players text a gate forbids carries the same Warnings section a write's
    /// result does, in its dry run and when it is applied: undoing the batch that gave the party its name for Keras shows
    /// the party "Keras" while a gate forbids it. Before, the undo said nothing, and the party's search then found Keras.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallTool_UndoThatShowsThePartyAForbiddenName_WarnsAsAWriteDoes(bool dryRun)
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server, role: "player", myCharacter: "Belmakor");
        await CampaignWriteSetup.WriteAsync(_server, slug, """[{"op": "upsert", "kind": "character", "name": "Keras", "visibility": "party"}]""");
        var gate = CampaignWriteSetup.Refs(await CampaignWriteSetup.WriteAsync(_server, slug, """
            [{"op": "fact", "statement": "The ancient sorcerer king's name is Keras.", "about": ["character:keras"],
              "gate": {"forbidden_terms": ["Keras"]}, "known_by": [{"who": "party", "state": "unaware"}]}]
            """))[0];
        var mask = await CampaignWriteSetup.CallAsync(_server, "campaign_knowledge", $$"""
            {"campaign": "{{slug}}", "action": "record", "targets": ["character:keras"], "knowers": [{"who": "party", "state": "met", "known_as": "the ancient sorcerer king"}]}
            """);
        var batch = BatchIdRegex().Match(mask).Groups[1].Value;

        var undo = await CampaignWriteSetup.CallAsync(_server, "campaign_history",
            $$"""{"campaign": "{{slug}}", "action": "undo", "batch_id": "{{batch}}", "dry_run": {{(dryRun ? "true" : "false")}}}""");

        Assert.DoesNotContain("## Warnings", mask, StringComparison.Ordinal);
        Assert.StartsWith(dryRun ? $"# Dry run: undo of batch {batch}\n" : $"# Undo of batch {batch}\n", undo, StringComparison.Ordinal);
        Assert.Contains("\n\n## Warnings (1)\nApplied anyway; read them before the table does.\n- **warning** · forbidden word: character:keras's name uses " +
                        $"\"Keras\", forbidden by {gate}'s gate while it holds: rename it to what the players call it and keep this name as an author alias.\n",
            undo, StringComparison.Ordinal);
        // Ahead of the reversed batch, whose list the output cap would cut first.
        Assert.True(undo.IndexOf("## Warnings", StringComparison.Ordinal) < undo.IndexOf("## The batch it", StringComparison.Ordinal), undo);
    }

    [Fact]
    public async Task CallTool_FiftyOpsTheLargestBatch_StaysWellUnderTheCap()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);
        var ops = string.Join(", ", Enumerable.Range(1, 50).Select(i =>
            $$"""{"op": "upsert", "kind": "character", "name": "Sailor number {{i}} of the long crew list", "summary": "{{new string('x', 900)}}", "tags": ["crew", "deck"]}"""));

        var text = await CampaignWriteSetup.WriteAsync(_server, slug, "[" + ops + "]");

        Assert.Contains("| 50 | upsert | character:sailor-number-50-of-the-long-crew-list | created |", text, StringComparison.Ordinal);
        Assert.True(text.Length < 10_000, $"50 ops rendered as {text.Length} characters.");
    }

    [Theory]
    [InlineData("campaign_write", """{"campaign": "@S", "ops": [{"op": "@X"}]}""")]
    [InlineData("campaign_write", """{"campaign": "@S", "ops": [{"op": "upsert", "kind": "character", "name": "@X"}]}""")]
    [InlineData("campaign_write", """{"campaign": "@X", "ops": [{"op": "upsert", "kind": "character", "name": "X"}]}""")]
    [InlineData("campaign_knowledge", """{"campaign": "@S", "action": "@X"}""")]
    [InlineData("campaign_knowledge", """{"campaign": "@S", "action": "check", "text": "@X"}""")]
    [InlineData("campaign_knowledge", """{"campaign": "@S", "action": "record", "targets": ["@X"], "knowers": [{"who": "@X"}]}""")]
    [InlineData("campaign_session", """{"campaign": "@S", "action": "@X"}""")]
    [InlineData("campaign_session", """{"campaign": "@S", "action": "plan", "title": "@X"}""")]
    public async Task CallTool_AHundredThousandCharacterArgument_GivesAShortError(string tool, string arguments)
    {
        // Every echo of the caller's text is shortened: a pasted document in the wrong field must not come back whole.
        var slug = CampaignWriteSetup.CreateCampaign(_server);

        var text = await CampaignWriteSetup.FailAsync(_server, tool,
            arguments.Replace("@S", slug, StringComparison.Ordinal).Replace("@X", new string('x', 100_000), StringComparison.Ordinal));

        Assert.StartsWith($"An error occurred invoking '{tool}': ", text, StringComparison.Ordinal);
        Assert.True(text.Length < 1_500, $"{text.Length} characters: {text[..Math.Min(300, text.Length)]}");
    }

    [Fact]
    public async Task CallTool_UpsertOfAHugeDataObject_KeepsItsChangedCellShortAndCountsTheRest()
    {
        // 400 data keys are 400 changed paths (data.<key> each); the row names the first few and counts the rest.
        var slug = CampaignWriteSetup.CreateCampaign(_server);
        var data = string.Join(", ", Enumerable.Range(0, 400).Select(i => $"\"key_number_{i:D3}\": {i}"));

        await CampaignWriteSetup.WriteAsync(_server, slug, """[{"op": "upsert", "kind": "note", "name": "Ledger"}]""");

        var text = await CampaignWriteSetup.WriteAsync(_server, slug, $$"""[{"op": "upsert", "ref": "note:ledger", "data": { {{data}} } }]""");

        var row = text.Split('\n').Single(l => l.StartsWith("| 1 | upsert | note:ledger | updated |", StringComparison.Ordinal));
        Assert.True(row.Length < 400, $"The row is {row.Length} characters.");
        Assert.Matches(@", … and \d+ more \|$", row);
        Assert.Contains("data.key_number_000", row, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("campaign_write")]
    [InlineData("campaign_knowledge")]
    public void Format_AWarningLongerThanEveryBudget_IsCutAtTheCapWithTheCutSaid(string tool)
    {
        // Each list stops adding once past its own budget, but the entry that crosses it is added whole, and a warning's
        // message is the writer's text, which the formatter does not shorten (W's are one line today). The cap is the last
        // line of defence for whatever a writer adds later: it cuts at a line and says so, and never touches the batch line.
        var applied = Enumerable.Range(0, 60).Select(i => new AppliedOp(0, "fact", $"f:{i}", WriteOutcomes.Updated, ["canon_status"])).ToList();
        var warnings = new List<WriteWarning> { new(WarningKinds.Nothing, WarningSeverities.Warning, new string('w', 30_000), 0) };
        var result = new WriteResult("0199aaaa-bbbb-7ccc-8ddd-eeeeffff0000", false, null, applied, warnings, []);

        var text = tool == "campaign_write"
            ? WriteMarkdown.Format(CampaignWriteSetup.Row("big"), result, 60)
            : KnowledgeMarkdown.FormatWrite(CampaignWriteSetup.Row("big"), "record", result);

        Assert.True(text.Length <= CampaignMarkdownText.MaxChars, $"{text.Length} characters.");
        Assert.Contains("Batch `0199aaaa-bbbb-7ccc-8ddd-eeeeffff0000`. To undo it:", text, StringComparison.Ordinal);
        Assert.Matches(@"\n\n_Output cut at 24,000 characters; [^\n]+\._$", text);
    }

    [Fact]
    public void Format_ClocksWithHugeOnFillText_ShortenEachConsequenceSoAllOfThemFit()
    {
        // A filled clock's consequence carries its on-fill markdown (up to 50,000 characters); three of them must not
        // push the result past the cap or crowd each other out.
        var consequences = Enumerable.Range(1, 3)
            .Select(i => new Consequence(ConsequenceKinds.ClockFilled, $"Clock {i} (clock:c{i}) filled: " + new string('m', 30_000), $"clock:c{i}", []))
            .ToList();
        var result = new WriteResult("0199aaaa-bbbb-7ccc-8ddd-eeeeffff0000", false, null,
            [new AppliedOp(0, "tick", "clock:c1", WriteOutcomes.Ticked, ["filled"])], [], consequences);

        var text = WriteMarkdown.Format(CampaignWriteSetup.Row("big"), result, 1);

        Assert.True(text.Length < 5_000, $"{text.Length} characters.");
        for (var i = 1; i <= 3; i++)
        {
            Assert.Matches($"\n- clock filled: Clock {i} \\(clock:c{i}\\) filled: m{{900,}}…\n", text);
        }
    }

    [Fact]
    public void Format_WorstCaseResult_IsCappedWithTheBatchLineIntactAndSaysWhatWasCut()
    {
        // 1,500 rows (a knowledge write's maximum) and 300 long warnings: far past the cap before the per-list limits.
        var applied = Enumerable.Range(0, 1_500)
            .Select(i => new AppliedOp(i, "fact", $"f:{i + 1}", WriteOutcomes.Created, ["statement", "known_by", "gate", "about"], "F" + i))
            .ToList();
        var warnings = Enumerable.Range(0, 300)
            .Select(i => (WriteWarning)new GateWarning(new string('w', 400), i, "after", $"f:{i}", "party", ["f:1"], [], 12, null, null, [], new string('n', 400)))
            .ToList();
        var result = new WriteResult("0199aaaa-bbbb-7ccc-8ddd-eeeeffff0000", false, 12, applied, warnings, []);

        var text = WriteMarkdown.Format(CampaignWriteSetup.Row("big"), result, 1_500);

        Assert.True(text.Length <= CampaignMarkdownText.MaxChars, $"{text.Length} characters.");
        Assert.Contains("Batch `0199aaaa-bbbb-7ccc-8ddd-eeeeffff0000`. Session context: session 12. To undo it:", text, StringComparison.Ordinal);
        Assert.Contains("_… and 1440 more rows (every op above and below the cut was applied as reported)._", text, StringComparison.Ordinal);
        // The warnings list stops at its own budget (long gate notes fill it before the 40-warning limit): what is shown
        // and what is counted add up to all 300, so nothing is dropped without saying so.
        var more = Regex.Match(text, @"_… and (\d+) more warnings\._");
        Assert.True(more.Success, "The cut warnings are not counted.");
        var shown = Regex.Matches(text, @"^- \*\*warning\*\* · gate \(after\)", RegexOptions.Multiline).Count;
        Assert.True(shown > 0, "No warning survived the cut.");
        Assert.Equal(300, shown + int.Parse(more.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// Shared setup for the write-path tool tests (campaign_write, campaign_knowledge, campaign_session, the prompts): a
/// campaign of their own in the harness's campaigns.db, created through the host's <see cref="CampaignService"/> exactly
/// as <c>campaign create</c> does, and the schema checks the surface tests make for the eight older tools. Every test makes
/// its own campaign with a fresh slug and names it on every call, so tests sharing a class's harness never see each
/// other's entities, sessions or active campaign.
/// </summary>
internal static class CampaignWriteSetup
{
    /// <summary>Claude Code truncates tool and prompt descriptions here.</summary>
    public const int DescriptionLimit = 2048;

    /// <summary>Runs <paramref name="test"/> against a server of its own (its own campaigns.db), disposed afterwards.</summary>
    public static async Task WithServerAsync(Func<McpServerHarness, Task> test)
    {
        var server = new McpServerHarness();
        await server.InitializeAsync();
        try
        {
            await test(server);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    /// <summary>A new campaign with a unique slug; returns the slug.</summary>
    public static string CreateCampaign(McpServerHarness server, string role = "dm", string ruleset = "2024", string? slug = null,
        string? myCharacter = null, string? myCharacterSlug = null)
    {
        slug ??= "c-" + Guid.NewGuid().ToString("N")[..10];
        server.Services.GetRequiredService<CampaignService>().Store.Create("Test " + slug, role, ruleset, slug: slug,
            myCharacter: myCharacter, myCharacterSlug: myCharacterSlug);
        return slug;
    }

    /// <summary>campaign_write with <paramref name="opsJson"/> in <paramref name="slug"/>; the success text.</summary>
    public static async Task<string> WriteAsync(McpServerHarness server, string slug, string opsJson, string? rest = null) =>
        server.SuccessText(await server.CallToolJsonAsync("campaign_write",
            $$"""{"campaign": "{{slug}}", "ops": {{opsJson}}{{(rest is null ? string.Empty : ", " + rest)}}}"""));

    /// <summary>
    /// The Ref column of a write result's table, in row order, without a code or knower suffix: the handles a later call
    /// must use. Fact and entity seqs are numbered across every campaign in the file, so tests never guess them.
    /// </summary>
    public static IReadOnlyList<string> Refs(string result) =>
        result.Split('\n')
            .Where(l => l.StartsWith("| ", StringComparison.Ordinal) && !l.StartsWith("| #", StringComparison.Ordinal) &&
                        !l.StartsWith("| Target", StringComparison.Ordinal) && !l.StartsWith("|---", StringComparison.Ordinal))
            .Select(l => l.Split(" | ")[l.StartsWith("| ", StringComparison.Ordinal) && char.IsAsciiDigit(l[2]) ? 2 : 0].TrimStart('|', ' '))
            .Select(r => r.Split(" · ")[0].Split(" → ")[0])
            .ToList();

    /// <summary>A tool call with raw JSON arguments; the success text.</summary>
    public static async Task<string> CallAsync(McpServerHarness server, string tool, string argumentsJson) =>
        server.SuccessText(await server.CallToolJsonAsync(tool, argumentsJson));

    /// <summary>A tool call with raw JSON arguments that must fail; the error text.</summary>
    public static async Task<string> FailAsync(McpServerHarness server, string tool, string argumentsJson) =>
        server.ErrorText(await server.CallToolJsonAsync(tool, argumentsJson));

    /// <summary>
    /// The JSON arguments of every <c>&lt;tool&gt; {…}</c> call a result prints, in order, exactly as the model would copy
    /// them (the calls results print are flat objects), so a test can send each one as printed.
    /// </summary>
    public static IReadOnlyList<string> PrintedCalls(string result, string tool) =>
        Regex.Matches(result, Regex.Escape(tool) + @" (\{[^{}]*\})").Select(m => m.Groups[1].Value).ToList();

    public static async Task<McpClientTool> ToolAsync(McpServerHarness server, string name) =>
        (await server.Client.ListToolsAsync()).Single(t => t.Name == name);

    /// <summary>A campaign row for formatter tests (no database).</summary>
    public static CampaignRow Row(string slug, string role = "dm") =>
        new("id-" + slug, slug, "Test " + slug, role, "2024", "active", null, null, null, null, null, "{}", string.Empty,
            "2026-01-01T00:00:00.000Z", "2026-01-01T00:00:00.000Z");

    /// <summary>
    /// Every property at every depth has a valid name and a description (ServerSurfaceTests' rule for the older tools):
    /// the description is the only thing that tells the model a field's format.
    /// </summary>
    public static void AssertDescribedAtEveryDepth(string path, JsonElement properties)
    {
        foreach (var property in properties.EnumerateObject())
        {
            Assert.Matches("^[a-zA-Z0-9_.-]{1,64}$", property.Name);
            Assert.True(property.Value.TryGetProperty("description", out var description) && !string.IsNullOrWhiteSpace(description.GetString()),
                $"{path}.{property.Name} has no description.");
            foreach (var nested in new[] { property.Value, property.Value.TryGetProperty("items", out var items) ? items : default })
            {
                if (nested.ValueKind == JsonValueKind.Object && nested.TryGetProperty("properties", out var fields) && fields.ValueKind == JsonValueKind.Object)
                {
                    AssertDescribedAtEveryDepth($"{path}.{property.Name}", fields);
                }
            }
        }
    }
}
