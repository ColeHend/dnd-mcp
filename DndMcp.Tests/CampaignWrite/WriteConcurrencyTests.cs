using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Write;
using Xunit;

namespace DndMcp.Tests.CampaignWrite;

/// <summary>
/// Invariant: two writers at once (two server processes on one campaigns.db, or concurrent tool calls in one process)
/// both succeed and never hand out the same register code: codes are computed inside BEGIN IMMEDIATE, so the second
/// writer sees the first one's rows. A code computed outside the transaction would give two inventions "F7".
/// </summary>
public sealed class WriteConcurrencyTests : IDisposable
{
    private const int PerWriter = 12;

    private readonly WriteFixture _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task TwoProcesses_ProposingAtOnce_GetDistinctConsecutiveCodes()
    {
        var campaign = _f.Campaign();
        using var other = _f.Db.OtherProcess();
        var writers = new[] { _f.Writer, new CampaignWriter(other) };

        var tasks = writers.Select((writer, w) => Task.Run(() =>
        {
            var codes = new List<string>();
            for (var i = 0; i < PerWriter; i++)
            {
                codes.Add(writer.Apply(campaign, [Op.Fact($"Writer {w} fact {i}.", "proposed")], WriteContext.Default).Applied[0].Code!);
            }

            return codes;
        })).ToList();
        var results = await Task.WhenAll(tasks);

        var all = results.SelectMany(c => c).ToList();
        Assert.Equal(2 * PerWriter, all.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 2 * PerWriter).Select(n => "F" + n).Order(), all.Order());
        Assert.Equal(2 * PerWriter, _f.Count("SELECT count(*) FROM fact WHERE code IS NOT NULL"));
    }

    [Fact]
    public async Task OneProcess_ConcurrentCalls_AreSerialisedAndAllApplied()
    {
        var campaign = _f.Campaign();

        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            _f.Writer.Apply(campaign, [Op.Upsert("character", "Npc " + i), Op.Fact($"Fact {i}.", "proposed")], WriteContext.Default))).ToList();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(8, results.Select(r => r.BatchId).Distinct().Count());
        Assert.Equal(8, results.Select(r => r.Applied[1].Code).Distinct().Count());
        Assert.Equal(9, _f.Count("SELECT count(*) FROM entity WHERE kind = 'character' OR subtype = 'party'"));
    }
}
