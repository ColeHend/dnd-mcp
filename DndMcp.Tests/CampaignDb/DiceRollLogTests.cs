using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Repository.Campaign;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.Tests.CampaignDb;

/// <summary>
/// Invariant: table rolls are appended to dice_roll as they are made, never through change_log (undo must never un-roll
/// dice), and a session's rolls read back in order with secret rolls only for the view allowed to see them.
/// </summary>
public sealed class DiceRollLogTests : IDisposable
{
    private readonly CampaignTestDb _db = new();
    private readonly SqliteConnection _connection;
    private readonly SeededCampaign _campaign;
    private readonly SeededSession _session;

    public DiceRollLogTests()
    {
        _connection = _db.Open();
        var seed = new CampaignSeed(_connection);
        _campaign = seed.Campaign();
        _session = seed.Session(_campaign.Id, 12, status: CampaignValues.SessionStatuses.Live);
    }

    public void Dispose()
    {
        _connection.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void Append_Roll_IsStoredWithANewIdAndNotChangeLogged()
    {
        var seq = DiceRollLog.Append(_connection, null, Roll("1d20+5>=15", total: 17, outcome: 1, label: "Perception"));

        var stored = Assert.Single(DiceRollLog.ForSession(_connection, _session.EntityId, includeSecret: true, max: 10));
        Assert.Equal(seq, stored.Seq);
        Assert.Matches("^[0-9a-f-]{36}$", stored.Id);
        Assert.Equal(("1d20+5>=15", 17L, 1L, "Perception"), (stored.Expression, stored.Total, stored.Outcome, stored.Label));
        Assert.Equal("{\"rolls\":[12]}", stored.Detail);
        Assert.Equal(0, _connection.ExecuteScalar<long>("SELECT count(*) FROM change_log"));
    }

    /// <summary>Appends work inside a batch's write transaction too (the host logs a roll alongside nothing else).</summary>
    [Fact]
    public void Append_InsideAWriteTransaction_CommitsWithIt()
    {
        _db.Database.Write((connection, transaction) => DiceRollLog.Append(connection, transaction, Roll("2d6")));

        Assert.Single(DiceRollLog.ForSession(_connection, _session.EntityId, includeSecret: false, max: 10));
    }

    [Fact]
    public void ForSession_SecretRolls_AreOnlyReturnedWhenAllowed()
    {
        DiceRollLog.Append(_connection, null, Roll("1d20", total: 3));
        DiceRollLog.Append(_connection, null, Roll("1d20", total: 19, secret: true));

        Assert.Equal(new[] { 3L, 19L }, DiceRollLog.ForSession(_connection, _session.EntityId, includeSecret: true, max: 10).Select(r => r.Total));
        Assert.Equal(new[] { 3L }, DiceRollLog.ForSession(_connection, _session.EntityId, includeSecret: false, max: 10).Select(r => r.Total));
    }

    /// <summary>The latest rolls, oldest first: a long session shows how it ended, in reading order.</summary>
    [Theory]
    [InlineData(3, new long[] { 5, 6, 7 })]
    [InlineData(10, new long[] { 1, 2, 3, 4, 5, 6, 7 })]
    [InlineData(0, new long[0])]
    public void ForSession_Max_ReturnsTheLatestInOrder(int max, long[] totals)
    {
        for (var total = 1; total <= 7; total++)
        {
            DiceRollLog.Append(_connection, null, Roll("1d8", total: total));
        }

        Assert.Equal(totals, DiceRollLog.ForSession(_connection, _session.EntityId, includeSecret: true, max: max).Select(r => r.Total));
    }

    [Fact]
    public void ForSession_OtherSessionsRolls_AreNotIncluded()
    {
        var other = new CampaignSeed(_connection).Session(_campaign.Id, 13);
        DiceRollLog.Append(_connection, null, Roll("1d4") with { SessionId = other.EntityId });

        Assert.Empty(DiceRollLog.ForSession(_connection, _session.EntityId, includeSecret: true, max: 10));
    }

    /// <summary>The schema's CHECKs still apply: the detail must be a JSON object.</summary>
    [Fact]
    public void Append_DetailNotAnObject_IsRefusedBySqlite() =>
        Assert.Throws<SqliteException>(() => DiceRollLog.Append(_connection, null, Roll("1d6") with { Detail = "[1]" }));

    private DiceRollRow Roll(string expression, long total = 10, long? outcome = null, string? label = null, bool secret = false) =>
        new(0, string.Empty, _campaign.Id, _session.EntityId, expression, label, total, outcome, "{\"rolls\":[12]}", secret ? 1L : 0L,
            _db.Database.Now());
}
