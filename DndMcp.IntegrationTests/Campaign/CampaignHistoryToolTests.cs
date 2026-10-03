using System.Text.RegularExpressions;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Write;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Invariant: <c>campaign_history</c> shows what changed (since, entity, batch), entries as they stood after a session
/// (as_of), and reverses exactly one batch through the write path's <see cref="HistoryWriter"/> (undo): a dry run
/// changes nothing and shows the batch it would reverse, a real undo prints the new batch id and how to redo, refusals
/// (already undone, later batches build on it) name the batches, and the secret-status consequences are rendered. Every
/// call a result prints (undo, redo, "lists them all") names the campaign and works as printed while another campaign is
/// current; the undo of a campaign's creation says the campaign is gone instead of promising a redo.
///
/// <para>
/// Why it fails silently: an undo rendered only as "1 logged change reversed" tells the user nothing about what came back,
/// so a dry run that was meant to be checked before the real one becomes a blind confirmation; a dry run that wrote would
/// leave the batch undone with the model believing it only looked; and a history that printed a shortened batch id would
/// make the model retype a prefix that can stop being unique.
/// </para>
/// </summary>
public sealed partial class CampaignHistoryToolTests : IAsyncLifetime
{
    private const string Accepted =
        "campaign_history accepts: action (string, required), since (string, optional), session (integer, optional), targets (array of string, optional), " +
        "ref (string, optional), refs (array of string, optional), detail (string, optional), batch_id (string, optional), dry_run (boolean, optional), " +
        "reason (string, optional), limit (integer, optional), cursor (string, optional), campaign (string, optional).";

    private CampaignTestServer _s = null!;
    private CampaignRow _c = null!;

    public async Task InitializeAsync()
    {
        _s = await CampaignTestServer.StartAsync();
        _c = await _s.Create("Sky");
    }

    public async Task DisposeAsync() => await _s.DisposeAsync();

    [GeneratedRegex("^batch `[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}` · by claude", RegexOptions.Multiline)]
    private static partial Regex FullBatchIdRegex();

    [Fact]
    public async Task UndoDryRun_ShowsTheBatchItWouldReverseAndChangesNothing()
    {
        var upsert = Upsert("Iron Guts", "alive");
        var logBefore = await _s.Call("campaign_history", """{"action":"since","limit":50}""");

        var text = await _s.Call("campaign_history", $$"""{"action":"undo","batch_id":"{{upsert.BatchId}}","dry_run":true}""");

        Assert.StartsWith(
            $"# Dry run: undo of batch {upsert.BatchId}\n\nNothing was changed. Without dry_run this reverses the batch's 1 logged change as a new batch.\n\n" +
            "## The batch it would reverse\n",
            text, StringComparison.Ordinal);
        Assert.Contains("- character:iron-guts created: name \"Iron Guts\", status alive, visibility restricted\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("redo", text, StringComparison.Ordinal);
        Assert.Equal(logBefore, await _s.Call("campaign_history", """{"action":"since","limit":50}"""));
        Assert.NotNull(Entity("character:iron-guts"));
    }

    [Fact]
    public async Task Undo_ReversesExactlyThatBatchAndPrintsTheRedo()
    {
        var first = Upsert("Iron Guts", "alive");
        Upsert("Serif", "alive");

        var text = await _s.Call("campaign_history", $$"""{"action":"undo","batch_id":"{{first.BatchId}}","reason":"typo"}""");

        var undoId = Regex.Match(text, "as a new batch `([0-9a-f-]{36})`").Groups[1].Value;
        Assert.StartsWith($"# Undo of batch {first.BatchId}\n\nReversed the batch's 1 logged change as a new batch `{undoId}`.\n" +
                          $"To put them back (redo): campaign_history {{\"action\": \"undo\", \"batch_id\": \"{undoId}\", \"campaign\": \"sky\"}}.\n\n" +
                          "## The batch it reversed\n",
            text, StringComparison.Ordinal);
        Assert.Contains("\n- character:iron-guts created: name \"Iron Guts\", status alive, visibility restricted\n", text, StringComparison.Ordinal);
        Assert.Null(Entity("character:iron-guts"));
        Assert.NotNull(Entity("character:serif"));

        var redo = await _s.Call("campaign_history", $$"""{"action":"undo","batch_id":"{{undoId}}"}""");

        Assert.StartsWith($"# Redo (undo of an undo) of batch {undoId}\n", redo, StringComparison.Ordinal);
        Assert.NotNull(Entity("character:iron-guts"));
    }

    [Fact]
    public async Task Undo_OfABatchThatCreatedAndLinkedAnEntity_ShowsTheEntityByItsHandleNotAsDeleted()
    {
        var batch = _s.Apply(_c, WriteContext.Default,
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Iron Guts" },
            new CampaignOpSpec { Op = "link", From = "character:iron-guts", Rel = "member_of", To = "faction:the-party" });

        var text = await _s.Call("campaign_history", $$"""{"action":"undo","batch_id":"{{batch.BatchId}}"}""");

        Assert.Contains("- character:iron-guts member_of faction:the-party created: status current", text, StringComparison.Ordinal);
        Assert.DoesNotContain("deleted entity", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Undo_NoSuchBatch_IsRefusedAndWritesNothing()
    {
        var text = await _s.Error("campaign_history", """{"action":"undo","batch_id":"00000000-0000-7000-8000-000000000000"}""");

        Assert.StartsWith("An error occurred invoking 'campaign_history': ", text, StringComparison.Ordinal);
        Assert.Contains("00000000-0000-7000-8000-000000000000", text, StringComparison.Ordinal);
        Assert.StartsWith("# History of sky\n\n1 batch ", await _s.Call("campaign_history", """{"action":"since"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Undo_ByAUniquePrefix_ReversesTheWholeBatch()
    {
        var upsert = Upsert("Iron Guts", "alive");

        var text = await _s.Call("campaign_history", $$"""{"action":"undo","batch_id":"{{upsert.BatchId![..30]}}"}""");

        Assert.StartsWith($"# Undo of batch {upsert.BatchId}\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Undo_APrefixSeveralBatchesShare_IsRefusedNamingThemAndWritesNothing()
    {
        // Batch ids are UUIDv7: the first 8 characters are a timestamp that changes about once a minute, so two batches
        // written together share them. Why every result prints the id in full.
        var first = Upsert("Iron Guts", "alive");
        var second = Upsert("Serif", "alive");
        Assert.Equal(first.BatchId![..8], second.BatchId![..8]);

        var text = await _s.Error("campaign_history", $$"""{"action":"undo","batch_id":"{{first.BatchId[..8]}}"}""");

        Assert.StartsWith($"An error occurred invoking 'campaign_history': batch_id \"{first.BatchId[..8]}\" matches ", text, StringComparison.Ordinal);
        Assert.Contains(first.BatchId, text, StringComparison.Ordinal);
        Assert.Contains(second.BatchId, text, StringComparison.Ordinal);
        Assert.NotNull(Entity("character:iron-guts"));
    }

    [Fact]
    public async Task Undo_AlreadyUndone_IsRefusedNamingTheUndoBatch()
    {
        var upsert = Upsert("Iron Guts", "alive");
        var undo = await _s.Call("campaign_history", $$"""{"action":"undo","batch_id":"{{upsert.BatchId}}"}""");
        var undoId = Regex.Match(undo, "as a new batch `([0-9a-f-]{36})`").Groups[1].Value;

        var text = await _s.Error("campaign_history", $$"""{"action":"undo","batch_id":"{{upsert.BatchId}}"}""");

        Assert.Equal(
            $"An error occurred invoking 'campaign_history': Batch {upsert.BatchId} was already undone by batch {undoId}; undo {undoId} to redo it.",
            text);
    }

    [Fact]
    public async Task Undo_ALaterBatchBuildsOnIt_IsRefusedListingThatBatchAndWritesNothing()
    {
        var created = Upsert("Iron Guts", "alive");
        var died = _s.Apply(_c, WriteContext.Default, new CampaignOpSpec { Op = "status", Ref = "character:iron-guts", Status = "dead" });

        var text = await _s.Error("campaign_history", $$"""{"action":"undo","batch_id":"{{created.BatchId}}"}""");

        Assert.StartsWith(
            $"An error occurred invoking 'campaign_history': Batch {created.BatchId} cannot be undone: later changes build on what it changed. " +
            $"Undo these first, newest first, then undo {created.BatchId} again:\n- {died.BatchId} (",
            text, StringComparison.Ordinal);
        Assert.Equal("dead", Entity("character:iron-guts")!.Status);
    }

    [Fact]
    public async Task Undo_OfAKnowledgeBatchALaterClueStillSupports_RendersTheRederivedSecretStatus()
    {
        _s.Apply(_c, WriteContext.Default,
            new CampaignOpSpec { Op = "upsert", Kind = "secret", Name = "The heir" },
            new CampaignOpSpec { Op = "fact", Statement = "The coronation happened." },
            new CampaignOpSpec { Op = "fact", Statement = "The smith's hands are too fine.", Links = [new FactLinkSpec { Ref = "secret:the-heir", Role = "clue_for" }] });
        _s.Apply(_c, WriteContext.Default,
            new CampaignOpSpec { Op = "fact", Statement = "The heir is the smith.", About = ["secret:the-heir"], Gate = new GateSpec { After = ["f:1"] } });
        var knowledge = new KnowledgeWriter(_s.Database);
        var reveal = knowledge.Record(_c, ["f:3"], [new KnowerSpec { Who = "party" }], WriteContext.Default);
        knowledge.Record(_c, ["f:2"], [new KnowerSpec { Who = "party" }], WriteContext.Default);

        var dryRun = await _s.Call("campaign_history", $$"""{"action":"undo","batch_id":"{{reveal.BatchId}}","dry_run":true}""");
        var text = await _s.Call("campaign_history", $$"""{"action":"undo","batch_id":"{{reveal.BatchId}}"}""");

        // The reveal's own row put the secret back to hidden, but the clue the party learned afterwards still seeds it.
        Assert.EndsWith("\n## What would follow\n- secret:the-heir: hidden → seeded.\n", dryRun, StringComparison.Ordinal);
        Assert.Contains("- secret:the-heir status: hidden → revealed\n", text, StringComparison.Ordinal);
        Assert.EndsWith("\n## What followed\n- secret:the-heir: hidden → seeded.\n", text, StringComparison.Ordinal);
        Assert.Equal("seeded", Entity("secret:the-heir")!.Status);
    }

    [Fact]
    public async Task Undo_OfTheCampaignsCreation_RemovesItAndAnnouncesTheResourceList()
    {
        var other = await _s.Call("campaign", """{"action":"create","name":"Short-lived","role":"dm","ruleset":"2024"}""");
        var batch = Regex.Match(other, "Batch `([0-9a-f-]{36})`").Groups[1].Value;
        await _s.WaitForListChanged(2);

        var text = await _s.Call("campaign_history", $$"""{"action":"undo","batch_id":"{{batch}}","campaign":"short-lived"}""");

        Assert.Null(new CampaignStore(_s.Database).TryGet("short-lived"));
        await _s.WaitForListChanged(3);
        Assert.Contains("\nThat batch created the campaign short-lived itself, so the campaign is gone", text, StringComparison.Ordinal);
        Assert.DoesNotContain("(redo)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UndoDryRun_OfTheCampaignsCreation_SaysTheCampaignWouldBeRemovedAndKeepsIt()
    {
        var other = await _s.Call("campaign", """{"action":"create","name":"Short-lived","role":"dm","ruleset":"2024"}""");
        var batch = Regex.Match(other, "Batch `([0-9a-f-]{36})`").Groups[1].Value;

        var text = await _s.Call("campaign_history", $$"""{"action":"undo","batch_id":"{{batch}}","campaign":"short-lived","dry_run":true}""");

        Assert.Contains(
            "\nThat batch created the campaign short-lived itself, so the undo removes the campaign; it cannot be redone afterwards, only created " +
            "again with campaign {\"action\": \"create\"}.\n",
            text, StringComparison.Ordinal);
        Assert.NotNull(new CampaignStore(_s.Database).TryGet("short-lived"));
    }

    [Fact]
    public async Task Undo_ReasonAndSession_AreKeptWithTheUndoBatchWhichNamesTheBatchItUndoes()
    {
        new SessionWriter(_s.Database).RecordPast(_c, 1, "One");
        var upsert = Upsert("Iron Guts", "alive");

        var text = await _s.Call("campaign_history", $$"""{"action":"undo","batch_id":"{{upsert.BatchId}}","reason":"typo","session":1}""");
        var undoId = Regex.Match(text, "as a new batch `([0-9a-f-]{36})`").Groups[1].Value;
        var batch = await _s.Call("campaign_history", $$"""{"action":"batch","batch_id":"{{undoId}}"}""");

        Assert.Matches(new Regex($"^# Batch {undoId}\n\n\\S+ · \\S+ · S1\nbatch `{undoId}` · by claude · undoes `{upsert.BatchId}`\nreason: typo\n"), batch);
    }

    [Fact]
    public async Task Undo_OfABatchInACampaignThatIsNotCurrent_TheRedoAsPrintedWorks()
    {
        var upsert = Upsert("Iron Guts", "alive");
        await _s.Create("Other");

        var undo = await _s.Call("campaign_history", $$"""{"action":"undo","batch_id":"{{upsert.BatchId}}","campaign":"sky"}""");
        var redo = await _s.Call("campaign_history", Regex.Match(undo, "\\(redo\\): campaign_history (\\{[^}]*\\})").Groups[1].Value);

        Assert.StartsWith("# Redo (undo of an undo) of batch ", redo, StringComparison.Ordinal);
        Assert.NotNull(Entity("character:iron-guts"));
    }

    [Fact]
    public async Task Since_InACampaignThatIsNotCurrent_TheUndoCallAsPrintedWorksWithTheBatchIdFilledIn()
    {
        var upsert = Upsert("Iron Guts", "alive");
        await _s.Create("Other");

        var page = await _s.Call("campaign_history", """{"action":"since","campaign":"sky"}""");
        var call = Regex.Match(page, "_To reverse one batch: campaign_history (\\{[^}]*\\}) shows").Groups[1].Value
            .Replace("<batch id>", upsert.BatchId, StringComparison.Ordinal);
        var dryRun = await _s.Call("campaign_history", call);

        Assert.StartsWith($"# Dry run: undo of batch {upsert.BatchId}\n", dryRun, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Since_ABatchWithMoreChangesThanListed_TheCallAsPrintedListsThemAllWhileAnotherCampaignIsCurrent()
    {
        var batch = _s.Apply(_c, WriteContext.Default, Enumerable.Range(1, 40).Select(i => new CampaignOpSpec { Op = "upsert", Kind = "character", Name = $"Crew {i:D2}" }).ToArray());
        await _s.Create("Other");

        var page = await _s.Call("campaign_history", """{"action":"since","campaign":"sky"}""");
        var all = await _s.Call("campaign_history", Regex.Match(page, "more changes; campaign_history (\\{[^}]*\\}) lists them all").Groups[1].Value);

        Assert.StartsWith($"# Batch {batch.BatchId}\n\n", all, StringComparison.Ordinal);
        Assert.Contains("character:crew-40 created", all, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Since_ListsBatchesOldestFirstWithWholeIdsAndPagesWithTheCursor()
    {
        Upsert("Iron Guts", "alive");
        Upsert("Serif", "alive");

        var first = await _s.Call("campaign_history", """{"action":"since","limit":2}""");
        var cursor = Regex.Match(first, "pass cursor \"([^\"]+)\"").Groups[1].Value;
        var second = await _s.Call("campaign_history", $$"""{"action":"since","limit":2,"cursor":"{{cursor}}"}""");

        Assert.StartsWith("# History of sky\n\n2 of 3 batches on this page (one tool call that wrote is one batch).\n\n### ", first, StringComparison.Ordinal);
        Assert.Equal(2, FullBatchIdRegex().Matches(first).Count);
        Assert.True(first.IndexOf("campaign/create", StringComparison.Ordinal) < first.IndexOf("iron-guts", StringComparison.Ordinal), first);
        Assert.Contains("character:serif created", second, StringComparison.Ordinal);
        Assert.DoesNotContain("pass cursor", second, StringComparison.Ordinal);
        Assert.EndsWith(HistoryUndoHint + "\n", second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Since_ASessionNumber_StartsAtThatSessionsFirstChange()
    {
        Upsert("Before", "alive");
        new SessionWriter(_s.Database).RecordPast(_c, 1, "One", recapMd: "It happened.");
        _s.Apply(_c, WriteContext.For(1), new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "During" });

        var text = await _s.Call("campaign_history", """{"action":"since","session":1}""");

        Assert.StartsWith("# History of sky since session 1\n", text, StringComparison.Ordinal);
        Assert.Contains("character:during created", text, StringComparison.Ordinal);
        Assert.DoesNotContain("character:before", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Since_Targets_KeepsOnlyChangesToThoseEntries()
    {
        Upsert("Iron Guts", "alive");
        Upsert("Serif", "alive");

        var text = await _s.Call("campaign_history", """{"action":"since","targets":["character:serif"]}""");

        Assert.Contains("character:serif created", text, StringComparison.Ordinal);
        Assert.DoesNotContain("iron-guts", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Since_NothingMatches_SaysSo()
    {
        var text = await _s.Call("campaign_history", """{"action":"since","since":"2999-01-01"}""");

        Assert.Equal("# History of sky since 2999-01-01\n\nNo changes match.\n", text);
    }

    [Fact]
    public async Task Entity_ListsTheBatchesThatChangedItNewestFirst()
    {
        Upsert("Iron Guts", "alive");
        _s.Apply(_c, WriteContext.Default, new CampaignOpSpec { Op = "status", Ref = "character:iron-guts", Status = "dead" });

        var text = await _s.Call("campaign_history", """{"action":"entity","ref":"character:iron-guts"}""");

        Assert.StartsWith("# History of character:iron-guts in sky (newest first)\n\n2 batches (one tool call that wrote is one batch).\n", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("status: alive → dead", StringComparison.Ordinal) < text.IndexOf("created: name", StringComparison.Ordinal), text);
    }

    [Fact]
    public async Task Entity_ANameInsteadOfAHandle_IsRefusedAboutRefWithTheHandleItMeans()
    {
        _s.Apply(_c, WriteContext.Default, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "The Old King", Slug = "old-king" });

        var text = await _s.Error("campaign_history", """{"action":"entity","ref":"The Old King"}""");

        Assert.Equal("An error occurred invoking 'campaign_history': ref \"The Old King\": nothing in this campaign has that handle. Did you mean character:old-king?",
            text);
    }

    /// <summary>
    /// Kills Q06 (FH10, M18): a mistyped kind:slug ref ("character:old-kin") is refused with the handle it is close to,
    /// matched on the slug part of what was typed. Matched on the whole text, kind prefix included, it suggested nothing;
    /// the bare-name test above cannot tell the two apart.
    /// </summary>
    [Fact]
    public async Task Entity_AMistypedKindSlug_SuggestsTheHandleItIsCloseTo()
    {
        _s.Apply(_c, WriteContext.Default, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Old King" });

        var text = await _s.Error("campaign_history", """{"action":"entity","ref":"character:old-kin"}""");

        Assert.Equal("An error occurred invoking 'campaign_history': ref \"character:old-kin\": nothing in this campaign has that handle. " +
                     "Did you mean character:old-king?", text);
    }

    [Fact]
    public async Task Entity_ADeletedEntry_StillShowsItsHistory()
    {
        Upsert("Iron Guts", "alive");
        _s.Apply(_c, WriteContext.Default, new CampaignOpSpec { Op = "delete", Ref = "character:iron-guts" });

        var text = await _s.Call("campaign_history", """{"action":"entity","ref":"character:iron-guts"}""");

        Assert.StartsWith("# History of character:iron-guts in sky (newest first)\n\n2 batches ", text, StringComparison.Ordinal);
        Assert.Contains("character:iron-guts deleted", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Batch_Largest_IsCutAtTheCapAndSaysHowToNarrowIt()
    {
        var batch = _s.Apply(_c, WriteContext.Default, Enumerable.Range(1, 50).Select(i => new CampaignOpSpec
        {
            Op = "upsert", Kind = "character", Name = $"Crew member {i:D2} " + string.Join(' ', Enumerable.Repeat("of the long voyage", 8)),
            Aliases = Enumerable.Range(1, 10).Select(a => new AliasSpec { Alias = $"Alias {a:D2} of crew member {i:D2} " + new string('a', 60), Visibility = "party" }).ToArray(),
        }).ToArray());

        var text = await _s.Call("campaign_history", $$"""{"action":"batch","batch_id":"{{batch.BatchId}}"}""");

        Assert.True(text.Length <= 24_000, $"batch is {text.Length} characters");
        Assert.EndsWith(
            "_Output cut at 24,000 characters; campaign_history {\"action\": \"since\", \"targets\": [\"<handle>\"], \"campaign\": \"sky\"} lists the " +
            "changes to the entries you name._",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Batch_ListsEveryChangeOfOneBatch()
    {
        var ops = Enumerable.Range(1, 40).Select(i => new CampaignOpSpec { Op = "upsert", Kind = "character", Name = $"Crew {i:D2}" }).ToArray();
        var batch = _s.Apply(_c, WriteContext.Default, ops);

        var page = await _s.Call("campaign_history", """{"action":"since"}""");
        var text = await _s.Call("campaign_history", $$"""{"action":"batch","batch_id":"{{batch.BatchId}}"}""");

        Assert.Contains(
            $"- _… and 15 more changes; campaign_history {{\"action\": \"batch\", \"batch_id\": \"{batch.BatchId}\", \"campaign\": \"sky\"}} lists them all._", page,
            StringComparison.Ordinal);
        Assert.StartsWith($"# Batch {batch.BatchId}\n\n", text, StringComparison.Ordinal);
        Assert.Contains("character:crew-40 created", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsOf_ShowsTheEntryAsItStoodAfterThatSession()
    {
        var sessions = new SessionWriter(_s.Database);
        sessions.RecordPast(_c, 1, "One");
        _s.Apply(_c, WriteContext.For(1), new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Iron Guts", Status = "alive", Summary = "A dwarf." });
        sessions.RecordPast(_c, 2, "Two");
        _s.Apply(_c, WriteContext.For(2), new CampaignOpSpec { Op = "status", Ref = "character:iron-guts", Status = "dead" });

        var then = await _s.Call("campaign_history", """{"action":"as_of","session":1,"refs":["character:iron-guts"]}""");
        var now = await _s.Call("campaign_get", """{"refs":["character:iron-guts"]}""");

        // The author's headings carry the e:<n> every view accepts beside the kind:slug (review U05, fix FQ14).
        Assert.Matches(@"\A# sky as of the end of session 1\n\n## Iron Guts \(`character:iron-guts` · `e:\d+`\)\ncharacter · status alive\n", then);
        Assert.Matches(@"\A# Iron Guts \(`character:iron-guts` · `e:\d+`\)\ncharacter · status dead\n", now);
    }

    /// <summary>
    /// Review CR01: as_of's refusals speak campaign_history's own arguments. It shared campaign_get's "leave out
    /// as_of_session" hint and range wording, but campaign_history has no as_of_session (sending it is refused as an unknown
    /// argument) and its as_of needs session, so the hint followed either way was refused. campaign_get keeps its wording.
    /// </summary>
    [Fact]
    public async Task AsOf_AnEntryMadeLaterOrASessionOutOfRange_IsRefusedInCampaignHistorysOwnArguments()
    {
        var sessions = new SessionWriter(_s.Database);
        sessions.RecordPast(_c, 1, "One");
        sessions.RecordPast(_c, 2, "Two");
        _s.Apply(_c, WriteContext.For(2), new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Bram" });

        var later = await _s.Error("campaign_history", """{"action":"as_of","session":1,"refs":["character:bram"]}""");
        var range = await _s.Error("campaign_history", """{"action":"as_of","session":-1,"refs":["character:bram"]}""");
        var getLater = await _s.Error("campaign_get", """{"refs":["character:bram"],"as_of_session":1}""");
        var getRange = await _s.Error("campaign_get", """{"refs":["character:bram"],"as_of_session":-1}""");

        Assert.Equal("An error occurred invoking 'campaign_history': Invalid refs: refs item 1: \"character:bram\" did not exist as of S1: " +
                     "it was made in S2. Read it as it is now with campaign_get, or give a later session.", later);
        Assert.Equal("An error occurred invoking 'campaign_history': session is -1; give a session number from 0 to 100000, e.g. 3.", range);
        Assert.DoesNotContain("as_of_session", later + range, StringComparison.Ordinal);
        Assert.EndsWith("did not exist as of S1: it was made in S2. Leave out as_of_session to read it as it is now.", getLater, StringComparison.Ordinal);
        Assert.EndsWith("as_of_session is -1; give a session number from 0 to 100000, e.g. 3.", getRange, StringComparison.Ordinal);
    }

    /// <summary>
    /// Review CR02: the description's Example runs as written on any campaign. It was an undo with an elided batch id, a
    /// template refused as "not a batch id" on every campaign. The undo template stays the undo's own, in its line of the
    /// action list (and in the undo's refusal), where it reads as the shape of that action.
    /// </summary>
    [Fact]
    public async Task ToolDescription_Example_RunsAsWrittenOnAnyCampaign()
    {
        var tool = (await _s.Server.Client.ListToolsAsync()).Single(t => t.Name == "campaign_history");
        var example = Regex.Match(tool.Description, "\nExample: (\\{.*\\})\\z").Groups[1].Value;

        var text = await _s.Call("campaign_history", example);

        Assert.Equal("{\"action\": \"since\", \"limit\": 5}", example);
        Assert.StartsWith("# History of sky\n", text, StringComparison.Ordinal);
        var undo = Assert.Single(tool.Description.Split('\n'), l => l.StartsWith("- action \"undo\"", StringComparison.Ordinal));
        Assert.EndsWith(" are kept with the undo. Its shape: {\"action\": \"undo\", \"batch_id\": \"0199a1b2-…\", \"dry_run\": true}", undo,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"action":"since","batch_id":"01a0"}""", "action \"since\" does not take batch_id; since takes campaign, since, session, targets, limit, cursor.")]
    [InlineData("""{"action":"entity","ref":"x","dry_run":true}""", "action \"entity\" does not take dry_run; entity takes campaign, ref, limit, cursor.")]
    [InlineData("""{"action":"undo","batch_id":"01a0efd2","limit":3}""", "action \"undo\" does not take limit; undo takes campaign, batch_id, dry_run, reason, session.")]
    [InlineData("""{"action":"batch","batch_id":"01a0efd2","refs":["x"]}""", "action \"batch\" does not take refs; batch takes campaign, batch_id.")]
    public async Task Action_ArgumentItDoesNotTake_IsRefusedWithWhatItTakes(string arguments, string problem)
    {
        var text = await _s.Error("campaign_history", arguments);

        Assert.Equal("An error occurred invoking 'campaign_history': Invalid campaign_history call: " + problem, text);
    }

    [Theory]
    [InlineData("""{"action":"entity"}""", "action \"entity\" needs ref. Example: {\"action\": \"entity\", \"ref\": \"character:iron-guts\"}")]
    [InlineData("""{"action":"as_of","refs":["x"]}""", "action \"as_of\" needs session. Example: {\"action\": \"as_of\", \"session\": 3, \"refs\": [\"character:old-king\"]}")]
    [InlineData("""{"action":"undo"}""", "action \"undo\" needs batch_id. Example: {\"action\": \"undo\", \"batch_id\": \"0199a1b2-…\", \"dry_run\": true}")]
    [InlineData("""{"action":"batch","batch_id":" "}""", "action \"batch\" needs batch_id. Example: {\"action\": \"batch\", \"batch_id\": \"0199a1b2-…\"}")]
    public async Task Action_MissingRequiredArgument_NamesItAndGivesAnExample(string arguments, string problem)
    {
        var text = await _s.Error("campaign_history", arguments);

        Assert.Equal("An error occurred invoking 'campaign_history': Invalid campaign_history call: " + problem, text);
    }

    [Theory]
    [InlineData("""{"action":"undo","batch_id":"zz"}""",
        "batch_id \"zz\" is too short: give the batch id printed after the write (at least 8 characters of it), e.g. \"0199a1b2-…\".")]
    [InlineData("""{"action":"since","since":"yesterday"}""",
        "since \"yesterday\" is not a date; give a UTC date or time such as \"2026-09-19\" or \"2026-09-19T20:00:00Z\".")]
    [InlineData("""{"action":"since","since":"2026-01-01","session":3}""", "Give since (a date) or session (a number), not both.")]
    [InlineData("""{"action":"since","limit":51}""", "limit is 51; give 1 to 50 (default 15), and page on with the cursor a result returns.")]
    [InlineData("""{"action":"since","targets":["character:nobody","f:9"]}""",
        "Invalid history targets (2 problems):\n- targets item 1 (character:nobody): nothing in this campaign has that handle.\n" +
        "- targets item 2 (f:9): nothing in this campaign has that handle.")]
    public async Task Action_BadValue_IsRefusedWithWhatIsAccepted(string arguments, string message)
    {
        var text = await _s.Error("campaign_history", arguments);

        Assert.Equal("An error occurred invoking 'campaign_history': " + message, text);
    }

    [Theory]
    [InlineData("rollback")]
    [InlineData("redo")]
    public async Task Action_Unknown_ListsTheActions(string action)
    {
        var text = await _s.Error("campaign_history", $$"""{"action":"{{action}}"}""");

        Assert.StartsWith(
            $"An error occurred invoking 'campaign_history': Invalid campaign_history call: action \"{action}\" is not one of since, entity, as_of, batch, undo.",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_MissingAction_GuardNamesItAndListsTheArguments()
    {
        var text = await _s.Error("campaign_history", "{}");

        Assert.Equal("An error occurred invoking 'campaign_history': Invalid arguments: missing required argument 'action'. " + Accepted, text);
    }

    [Fact]
    public async Task CallTool_NullForEveryOptionalArgument_MeansTheDefault()
    {
        var text = await _s.Call("campaign_history",
            """{"action":"since","since":null,"session":null,"targets":null,"ref":null,"refs":null,"detail":null,"batch_id":null,""" +
            """ "dry_run":null,"reason":null,"limit":null,"cursor":null,"campaign":null}""");

        Assert.StartsWith("# History of sky\n\n1 batch (one tool call that wrote is one batch).\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Since_LargestPage_StaysUnderTheCap()
    {
        for (var batch = 0; batch < 12; batch++)
        {
            var ops = Enumerable.Range(1, 50).Select(i => new CampaignOpSpec
            {
                Op = "upsert", Kind = "character", Name = $"A very long crew member name number {batch:D2}-{i:D2} to fill the change log",
                Summary = new string('x', 900),
            }).ToArray();
            _s.Apply(_c, new WriteContext { Reason = new string('r', 1_000) }, ops);
        }

        var text = await _s.Call("campaign_history", """{"action":"since","limit":50}""");

        Assert.True(text.Length <= 24_000, $"history page is {text.Length} characters");
        Assert.EndsWith("_Output cut at 24,000 characters; pass a smaller limit, targets, or read one batch with action \"batch\"._", text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Since_TypicalPage_IsUnderTheTypicalBudget()
    {
        for (var i = 0; i < 10; i++)
        {
            _s.Apply(_c, WriteContext.Default,
                new CampaignOpSpec { Op = "upsert", Kind = "character", Name = $"Crew {i}", Summary = "A sailor of the sky." },
                new CampaignOpSpec { Op = "link", From = $"character:crew-{i}", Rel = "member_of", To = "faction:the-party" });
        }

        var text = await _s.Call("campaign_history", """{"action":"since"}""");

        Assert.True(text.Length < 8_000, $"history page is {text.Length} characters");
    }

    private const string HistoryUndoHint =
        "_To reverse one batch: campaign_history {\"action\": \"undo\", \"batch_id\": \"<batch id>\", \"campaign\": \"sky\", \"dry_run\": true} shows " +
        "what it would change; run it again without dry_run to apply it. An undo is itself a batch (undo it to redo)._";

    private WriteResult Upsert(string name, string status) =>
        _s.Apply(_c, WriteContext.Default, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = name, Status = status });

    private EntityRow? Entity(string handle)
    {
        using var connection = _s.Database.OpenRead();
        return new HandleResolver(connection, _c.Id).TryEntity(CampaignHandle.Parse(handle));
    }
}
