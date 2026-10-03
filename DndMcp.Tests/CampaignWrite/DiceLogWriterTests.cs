using DndMcp.Domain.Campaign;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tests.CampaignDb;
using Xunit;

namespace DndMcp.Tests.CampaignWrite;

/// <summary>
/// Invariant: dice are logged only to a campaign with a live session, one dice_roll row per roll (ids and seqs
/// returned), never into change_log, and every reason not to log (no database, no campaign, nothing live) comes back
/// as a reason rather than an exception: the dice were already rolled, and an error would invite a re-roll.
/// </summary>
public sealed class DiceLogWriterTests : IDisposable
{
    private readonly WriteFixture _f = new();
    private readonly CampaignRow _c;

    public DiceLogWriterTests()
    {
        _c = _f.Campaign("Belmakor", role: "player");
    }

    public void Dispose() => _f.Dispose();

    private static DiceLogRoll Roll(long total, bool? outcome = null, string? label = "Stealth") =>
        new("1d20+5>=15", label, total, outcome, "{\"v\":1,\"groups\":[{\"term\":\"1d20\",\"value\":12}]}");

    [Fact]
    public void TryLog_NoLiveSession_IsNotLoggedWithTheReason()
    {
        var result = _f.Dice.TryLog(_c.Id, [Roll(17)], secret: true);

        Assert.False(result.Logged);
        Assert.Equal((DiceLogWriter.NoLiveSession, "belmakor", true), (result.NotLoggedReason, result.CampaignSlug, result.Secret));
        Assert.Empty(result.Rolls);
        Assert.Equal(0, _f.Count("SELECT count(*) FROM dice_roll"));
    }

    [Fact]
    public void TryLog_LiveSession_WritesOneRowPerRollAndReturnsTheirIds()
    {
        _f.Sessions.Start(_c, 12);
        var logBefore = _f.Log().Count;

        var result = _f.Dice.TryLog(_c.Id, [Roll(17, true), Roll(9, false, null), Roll(12)], secret: false);

        Assert.True(result.Logged);
        Assert.Equal(("belmakor", 12), (result.CampaignSlug, result.SessionNumber));
        Assert.Equal(3, result.Rolls.Count);
        var rows = _f.Query<DiceRollRow>($"SELECT {DiceRollRow.Columns} FROM dice_roll ORDER BY seq");
        Assert.Equal(result.Rolls.Select(r => (r.Seq, r.Id)), rows.Select(r => (r.Seq, r.Id)));
        Assert.Equal([(17L, (long?)1L, "Stealth"), (9L, 0L, null), (12L, null, "Stealth")], rows.Select(r => (r.Total, r.Outcome, r.Label)));
        Assert.All(rows, r =>
        {
            Assert.Equal((_f.Entity(_c, "session:12").Id, 0L, "1d20+5>=15"), (r.SessionId, r.Secret, r.Expression));
            Assert.Equal(_f.Db.Database.Now(), r.At);
        });
        Assert.Equal(logBefore, _f.Log().Count);
    }

    /// <summary>
    /// A blank label is stored as NULL and a padded one trimmed, whatever the caller passes (review M26, mutant H04): the
    /// session readers print "label: " before a labelled roll, so a stored "   " would print an empty label. dice_roll
    /// normalises first, so only a direct caller reaches this.
    /// </summary>
    [Fact]
    public void TryLog_BlankOrPaddedLabel_IsStoredAsNullOrTrimmed()
    {
        _f.Sessions.Start(_c, 1);

        var result = _f.Dice.TryLog(_c.Id, [Roll(3, label: "   "), Roll(4, label: "  Stealth ")], secret: false);

        Assert.True(result.Logged);
        Assert.Equal([null, "Stealth"], _f.Query<DiceRollRow>($"SELECT {DiceRollRow.Columns} FROM dice_roll ORDER BY seq").Select(r => r.Label));
    }

    [Fact]
    public void TryLog_Secret_IsStoredAsSecret()
    {
        _f.Sessions.Start(_c, 1);

        _f.Dice.TryLog(_c.Id, [Roll(3)], secret: true);

        Assert.Equal(1, _f.Scalar<long>("SELECT secret FROM dice_roll"));
    }

    [Fact]
    public void TryLog_NoDatabase_IsNotLoggedAndCreatesNoFile()
    {
        using var empty = new CampaignTestDb(create: false);

        var result = new DiceLogWriter(empty.Database).TryLog("0199aaaa-0000-7000-8000-000000000000", [Roll(3)], secret: false);

        Assert.False(result.Logged);
        Assert.Equal("there is no campaign database", result.NotLoggedReason);
        Assert.False(File.Exists(empty.DatabasePath));
    }

    [Fact]
    public void TryLog_CampaignGone_IsNotLogged()
    {
        var result = _f.Dice.TryLog("0199aaaa-0000-7000-8000-000000000000", [Roll(3)], secret: false);

        Assert.Equal((false, "the campaign no longer exists"), (result.Logged, result.NotLoggedReason));
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    public void TryLog_DetailNotAnObject_IsAHostBug(string detail)
    {
        Assert.Throws<ArgumentException>(() => _f.Dice.TryLog(_c.Id, [new DiceLogRoll("1d6", null, 3, null, detail)], secret: false));
    }

    [Fact]
    public void TryLog_NoRolls_IsAHostBug()
    {
        Assert.Throws<ArgumentException>(() => _f.Dice.TryLog(_c.Id, [], secret: false));
    }

    [Fact]
    public void Undo_NeverUnrollsDice()
    {
        _f.Sessions.Start(_c, 1);
        var batch = _f.Apply(_c, Op.Upsert("character", "Tristan")).BatchId!;
        _f.Dice.TryLog(_c.Id, [Roll(3)], secret: false);

        _f.History.Undo(_c, batch, WriteContext.Default);

        Assert.Equal(1, _f.Count("SELECT count(*) FROM dice_roll"));
    }
}
