using DndMcp.IntegrationTests.Infrastructure;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Invariant: understand-onepiece.md §3 rows 29 and 30, the dated timeline correction, through <c>campaign_write</c> on
/// the world the tools built: superseding the old timeline applies (it reads "superseded by" the corrected one), and the
/// write's result lists every fact resting on it with its depth and the fact it rests on (the fight and the fragments'
/// running-down at depth 1, the long-lived lineage at depth 2 via the fight) and the entities to re-check, while the
/// dependents themselves are left exactly as they were (flag, don't fix); a later knowledge check of a line quoting "400
/// years" lists the fragments' figure as stale, resting on the superseded timeline.
///
/// <para>
/// Why it fails silently: the dependents are the whole point of the correction ("hold loosely until he confirms them");
/// a result that only said "updated" would leave every NPC line quoting four hundred years standing, and a write that
/// "fixed" the dependents would change canon the author never ruled on.
/// </para>
/// </summary>
public sealed class ScenarioOnePieceSupersessionTests : IAsyncLifetime
{
    private readonly McpServerHarness _server = new();
    private ScenarioOnePieceWorld _w = null!;

    public async Task InitializeAsync()
    {
        await _server.InitializeAsync();
        _w = await ScenarioOnePieceWorld.BuildAsync(_server);
    }

    public Task DisposeAsync() => _server.DisposeAsync();

    /// <summary>Row 29: the correction's result lists the three dependents by depth and via, and the entities to re-check.</summary>
    [Fact]
    public async Task Write_Row29SupersedingTheOldTimeline_ListsItsDependentsWithDepthAndVia()
    {
        var text = await Supersede();

        Assert.Contains($"\n| 1 | fact | {_w.TimelineOld} | updated | canon_status, superseded_by |\n", text, StringComparison.Ordinal);
        Assert.Contains(
            "\n## Warnings (1)\nApplied anyway; read them before the table does.\n" +
            $"- **warning** · superseded · ops item 1: {_w.TimelineOld} is superseded by {_w.Timeline20260830}; 3 facts rest on it, left unchanged for you to re-check:\n" +
            $"  - {_w.Fight500900}: depth 1, via {_w.TimelineOld}\n" +
            $"  - {_w.Fragments400}: depth 1, via {_w.TimelineOld}\n" +
            $"  - {_w.LineageCovers}: depth 2, via {_w.Fight500900}\n" +
            "  - Entities to re-check: character:arch-mage, question:q21, character:protector, question:q22\n", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Row 29's "flag, don't fix": the superseded fact reads superseded by the correction, and every dependent's author line
    /// (canon status, confidence, what it depends on) is exactly what it was before the correction.
    /// </summary>
    [Fact]
    public async Task Write_Row29SupersedingTheOldTimeline_LeavesEveryDependentAsItWas()
    {
        var before = await AuthorLines();

        await Supersede();

        var after = await AuthorLines();
        Assert.Equal(before[1..], after[1..]);
        Assert.Contains("canon canon", before[0], StringComparison.Ordinal);
        Assert.Contains($"canon superseded · confidence confirmed · visibility restricted · superseded by `{_w.Timeline20260830}`", after[0], StringComparison.Ordinal);
    }

    /// <summary>Row 30: after the correction a line quoting four hundred years is checked, and the fragments' figure is listed as stale, resting on the superseded timeline.</summary>
    [Fact]
    public async Task Check_Row30AfterTheCorrection_ListsTheFragmentsFigureAsStale()
    {
        await Supersede();

        var text = await _w.Call("campaign_knowledge", """
            {"campaign": "one-piece", "action": "check", "perspective": "table", "text": "'I've watched this city for 400 years,' the Protector says."}
            """);

        Assert.Contains("\n**Stale facts** (4)\n", text, StringComparison.Ordinal);
        Assert.Contains($"\n- {_w.Fragments400} \"The fragments have been running down for roughly 400 years.\": rests on superseded {_w.TimelineOld} (depth 1)\n",
            text, StringComparison.Ordinal);
        Assert.Contains($"\n- {_w.LineageCovers} \"The long-lived lineage covers the ~500–900 years since the Keras–Baal fight comfortably.\": " +
                        $"rests on superseded {_w.TimelineOld} (depth 2)\n", text, StringComparison.Ordinal);
    }

    private Task<string> Supersede() => _w.Call("campaign_write", $$"""
        {"campaign": "one-piece", "reason": "Corrected by Cole, 2026-08-30",
         "ops": [{"op": "fact", "ref": "{{_w.TimelineOld}}", "canon_status": "superseded", "superseded_by": "{{_w.Timeline20260830}}"}]}
        """);

    // The author line of the old timeline and of each dependent (type, truth, canon status, confidence, visibility, …).
    private async Task<string[]> AuthorLines()
    {
        var lines = new List<string>();
        foreach (var fact in new[] { _w.TimelineOld, _w.Fight500900, _w.Fragments400, _w.LineageCovers })
        {
            var page = await _w.Call("campaign_get", $$"""{"campaign": "one-piece", "refs": ["{{fact}}"]}""");
            lines.Add(page.Split('\n').Single(l => l.StartsWith("- type ", StringComparison.Ordinal)));
        }

        return [.. lines];
    }
}
