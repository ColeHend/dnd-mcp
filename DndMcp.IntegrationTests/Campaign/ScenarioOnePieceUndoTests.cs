using System.Text.RegularExpressions;
using DndMcp.IntegrationTests.Infrastructure;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Invariant: understand-onepiece.md §3 row 25 and PLAN's "undo works", through <c>campaign_history</c> on the world the
/// tools built: undoing T5 (the reveal made during the live session 12) by the batch id its result printed shows first,
/// as a dry run, the batch it would reverse and changes nothing; then reverses exactly that batch as a new one: the party
/// no longer knows the seal or Nadar's plan (the ledger has no record, its search finds nothing), the secret is back to
/// partial and ready to reveal, and everything the earlier steps wrote stands, with the history keeping T5 and gaining
/// only the undo. A later batch that changed a row T5 wrote makes the undo refused, naming that batch and what it
/// changed, with nothing written; undoing that batch first clears the way. An undo is itself undoable (redo), and a batch
/// is undone once.
///
/// <para>
/// Why it fails silently: an undo that clobbered the later edit, or reversed more or less than its batch, would leave the
/// campaign saying the party knows (or does not know) something no one decided, and nothing but a careful reread of the
/// history would show it.
/// </para>
/// </summary>
public sealed class ScenarioOnePieceUndoTests : IAsyncLifetime
{
    private readonly McpServerHarness _server = new();
    private ScenarioOnePieceWorld _w = null!;

    public async Task InitializeAsync()
    {
        await _server.InitializeAsync();
        _w = await ScenarioOnePieceWorld.BuildAsync(_server);
    }

    public Task DisposeAsync() => _server.DisposeAsync();

    /// <summary>Row 25: the dry run changes nothing, then the undo takes the party's two rows away and puts the secret back to partial and ready.</summary>
    [Fact]
    public async Task Undo_Row25T5ThroughCampaignHistory_ThePartyNoLongerKnowsAndTheSecretIsPartialAndReadyAgain()
    {
        await _w.ThroughT4();
        var t5 = ScenarioCalls.BatchId(await _w.T5());
        var batches = await _w.BatchCount();

        var dry = await Undo(t5, dryRun: true);
        Assert.StartsWith($"# Dry run: undo of batch {t5}\n\nNothing was changed. Without dry_run this reverses the batch's 3 logged changes as a new batch.\n",
            dry, StringComparison.Ordinal);
        Assert.Equal(("knows (S12)", batches), (await _w.Cell(_w.Seal), await _w.BatchCount()));

        var undo = await Undo(t5, dryRun: false);

        var undoBatch = Regex.Match(undo, "as a new batch `([0-9a-f-]{36})`").Groups[1].Value;
        Assert.StartsWith($"# Undo of batch {t5}\n\nReversed the batch's 3 logged changes as a new batch `{undoBatch}`.\n" +
                          $"To put them back (redo): campaign_history {{\"action\": \"undo\", \"batch_id\": \"{undoBatch}\", \"campaign\": \"one-piece\"}}.\n",
            undo, StringComparison.Ordinal);
        Assert.Contains($"\n- knowledge of party about {_w.Seal} created: state knows, learned session:12\n" +
                        $"- knowledge of party about {_w.NadarPlan} created: state knows, learned session:12\n" +
                        "- secret:fruits-are-the-seal status: partial → revealed\n", undo, StringComparison.Ordinal);
        Assert.Equal(("no record", "no record"), (await _w.Cell(_w.Seal), await _w.Cell(_w.NadarPlan)));
        var secret = await _w.SecretPage();
        Assert.Contains("\n- secret status: partial (stored), partial (from its gates now)\n", secret, StringComparison.Ordinal);
        Assert.Contains($"\n  - `{_w.Seal}`: ready to reveal · ", secret, StringComparison.Ordinal);
        Assert.EndsWith("\n\nNothing matches. Try fewer or different words, a prefix such as \"sorcer*\", or leave query out to list by kinds.\n",
            await _w.Call("campaign_search", """{"campaign": "one-piece", "perspective": "party", "query": "seal"}"""), StringComparison.Ordinal);
        Assert.Equal(
            $"An error occurred invoking 'campaign_get': Invalid refs: refs item 1: \"{_w.Seal}\": nothing by that handle for this perspective. " +
            "campaign_search finds entities and facts by name.",
            await _w.Fail("campaign_get", $$"""{"campaign": "one-piece", "perspective": "party", "refs": ["{{_w.Seal}}"]}"""));
    }

    /// <summary>
    /// "Undo reverses exactly one batch": T1-T4 stand after T5 is undone (the party still knows the clues, the axe is in
    /// play), and the history keeps T5 and gains exactly the one undo batch, marked as undoing it.
    /// </summary>
    [Fact]
    public async Task Undo_Row25T5_ReversesOnlyThatBatchAndTheHistoryKeepsIt()
    {
        await _w.ThroughT4();
        var t5 = ScenarioCalls.BatchId(await _w.T5());
        var batches = await _w.BatchCount();

        await Undo(t5, dryRun: false);

        Assert.Equal(($"knows as “{ScenarioOnePieceWorld.TestimonyAsThePartyHeardIt}” (S8)", "knows (S9)", "knows (S10)"),
            (await _w.Cell(_w.TProtector), await _w.Cell(_w.IllusionRewatched), await _w.Cell(_w.BreachesClimbing)));
        Assert.Equal("knows", await _w.Cell(_w.Axe));
        Assert.Equal(batches + 1, await _w.BatchCount());
        Assert.Contains($"\nbatch `{t5}` · by claude\n", await _w.Call("campaign_history", $$"""{"campaign": "one-piece", "action": "batch", "batch_id": "{{t5}}"}"""),
            StringComparison.Ordinal);
        Assert.Contains($" · by claude · undoes `{t5}`\n", await _w.Call("campaign_history", """{"campaign": "one-piece", "action": "since", "session": 12}"""),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A conflicting undo is refused: after T5 the party's own words for the seal were recorded (a later batch changing a
    /// row T5 wrote). Undoing T5 would throw that away, so it is refused, naming the later batch and what it changed and
    /// saying to undo it first, and nothing is written; undoing the later batch, then T5, works.
    /// </summary>
    [Fact]
    public async Task Undo_Row25T5AfterALaterBatchChangedItsRow_IsRefusedNamingThatBatchUntilItIsUndone()
    {
        await _w.ThroughT4();
        var t5 = ScenarioCalls.BatchId(await _w.T5());
        var later = ScenarioCalls.BatchId(await _w.Call("campaign_knowledge", $$"""
            {"campaign": "one-piece", "action": "record", "targets": ["{{_w.Seal}}"], "knowers": [{"who": "party", "known_as": "what the fruits really are"}], "session": 12}
            """));
        var batches = await _w.BatchCount();

        var refusal = await _w.Fail("campaign_history", $$"""{"campaign": "one-piece", "action": "undo", "batch_id": "{{t5}}"}""");

        Assert.StartsWith($"An error occurred invoking 'campaign_history': Batch {t5} cannot be undone: later changes build on what it changed. " +
                          $"Undo these first, newest first, then undo {t5} again:\n- {later} (", refusal, StringComparison.Ordinal);
        Assert.EndsWith(", campaign_knowledge/record): changes a knowledge row known_as", refusal, StringComparison.Ordinal);
        Assert.Equal(("knows as “what the fruits really are” (S12)", batches), (await _w.Cell(_w.Seal), await _w.BatchCount()));
        Assert.Contains("\nsecret · status revealed\n", await _w.SecretPage(), StringComparison.Ordinal);

        await Undo(later, dryRun: false);
        await Undo(t5, dryRun: false);

        Assert.Equal("no record", await _w.Cell(_w.Seal));
        Assert.Contains("\nsecret · status partial\n", await _w.SecretPage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A batch is undone once: undoing T5 again is refused as already undone, naming the undo and how to redo; undoing the
    /// undo is that redo, and the party knows the seal again, the secret revealed.
    /// </summary>
    [Fact]
    public async Task Undo_Row25T5Twice_IsRefusedAsAlreadyUndoneAndUndoingTheUndoRedoesIt()
    {
        await _w.ThroughT4();
        var t5 = ScenarioCalls.BatchId(await _w.T5());
        var undo = Regex.Match(await Undo(t5, dryRun: false), "as a new batch `([0-9a-f-]{36})`").Groups[1].Value;

        var again = await _w.Fail("campaign_history", $$"""{"campaign": "one-piece", "action": "undo", "batch_id": "{{t5}}"}""");
        var redo = await Undo(undo, dryRun: false);

        Assert.Equal($"An error occurred invoking 'campaign_history': Batch {t5} was already undone by batch {undo}; undo {undo} to redo it.", again);
        Assert.StartsWith($"# Redo (undo of an undo) of batch {undo}\n", redo, StringComparison.Ordinal);
        Assert.Equal(("knows (S12)", "knows (S12)"), (await _w.Cell(_w.Seal), await _w.Cell(_w.NadarPlan)));
        Assert.Contains("\nsecret · status revealed\n", await _w.SecretPage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The contract §8 decision "undo batches re-derive secret statuses", as the undo's result shows it: undoing T2 (the
    /// illusion) while T3 stands (it wrote other rows, so no conflict) reverses T2's own status change back to seeded, and
    /// the undo batch then re-derives from what still stands (the temple-arithmetic route is complete), says so under "What
    /// followed", and leaves the stored status agreeing with the gates.
    /// </summary>
    [Fact]
    public async Task Undo_T2WhileT3Stands_ReDerivesTheSecretsStatusAndSaysWhatFollowed()
    {
        await _w.T1();
        var t2 = ScenarioCalls.BatchId(await _w.T2());
        await _w.T3();

        var undo = await Undo(t2, dryRun: false);

        Assert.EndsWith("\n## What followed\n- secret:fruits-are-the-seal: seeded → partial.\n", undo, StringComparison.Ordinal);
        Assert.Equal("no record", await _w.Cell(_w.IllusionRewatched));
        Assert.Contains($"\n- secret status: partial (stored), partial (from its gates now)\n  - `{_w.Seal}`: not ready · after unmet: {_w.Axe} · " +
                        "routes 1/2 (testimonies 1/4, illusion 0/1, temple-arithmetic 2/2 complete) · ", await _w.SecretPage(), StringComparison.Ordinal);
    }

    private Task<string> Undo(string batchId, bool dryRun) => _w.Call("campaign_history", $$"""
        {"campaign": "one-piece", "action": "undo", "batch_id": "{{batchId}}"{{(dryRun ? ", \"dry_run\": true" : string.Empty)}}}
        """);
}
