using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Dice;
using DndMcp.Repository.Campaign;
using DndMcp.Tests.Dice;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.Tests.CampaignDb;

/// <summary>
/// Invariant: table rolls are appended to dice_roll as they are made, never through change_log (undo must never un-roll
/// dice), and a session's rolls read back in order with secret rolls only for the view allowed to see them. Phase 7: a
/// roll the combat tracker makes carries its encounter (with or without a session), reads back per encounter under the
/// same secrecy rule, and keeps the detail shape the dice_roll tool writes, so a session's roll log reads one way.
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

    [Fact]
    public void Append_EncounterRoll_IsStoredWithItsEncounter()
    {
        var encounter = Encounter("The crypt");

        DiceRollLog.Append(_connection, null, Roll("1d20+5", total: 17, label: "Torch: initiative") with { EncounterId = encounter });

        var stored = Assert.Single(DiceRollLog.ForEncounter(_connection, encounter, includeSecret: true, max: 10));
        Assert.Equal((encounter, _session.EntityId), (stored.EncounterId, stored.SessionId));
        Assert.Equal(stored, Assert.Single(DiceRollLog.ForSession(_connection, _session.EntityId, includeSecret: true, max: 10)));
        Assert.Equal(0, _connection.ExecuteScalar<long>("SELECT count(*) FROM change_log"));
    }

    /// <summary>
    /// D11: a fight's rolls are logged even when no session is live (the schema allows a NULL session); they are found
    /// by encounter, never by a session listing.
    /// </summary>
    [Fact]
    public void Append_EncounterRollWithNoSession_IsFoundByItsEncounterOnly()
    {
        var encounter = Encounter("Sandbox");

        DiceRollLog.Append(_connection, null, Roll("1d20") with { SessionId = null, EncounterId = encounter });

        Assert.Null(Assert.Single(DiceRollLog.ForEncounter(_connection, encounter, includeSecret: true, max: 10)).SessionId);
        Assert.Empty(DiceRollLog.ForSession(_connection, _session.EntityId, includeSecret: true, max: 10));
    }

    /// <summary>The per-encounter read: only that fight's rolls, the latest first-in-order, secret ones only when allowed.</summary>
    [Theory]
    [InlineData(true, 10, new long[] { 1, 2, 3, 4 })]
    [InlineData(false, 10, new long[] { 1, 3 })]
    [InlineData(true, 2, new long[] { 3, 4 })]
    [InlineData(false, 1, new long[] { 3 })]
    [InlineData(true, 0, new long[0])]
    public void ForEncounter_SecretAndMax_ReturnTheLatestVisibleRollsInOrder(bool includeSecret, int max, long[] totals)
    {
        var encounter = Encounter("The crypt");
        var other = Encounter("The dark station", status: CampaignValues.EncounterStatuses.Ended);
        for (var total = 1; total <= 4; total++)
        {
            DiceRollLog.Append(_connection, null, Roll("1d20", total: total, secret: total % 2 == 0) with { EncounterId = encounter });
        }

        DiceRollLog.Append(_connection, null, Roll("1d20", total: 99) with { EncounterId = other });
        DiceRollLog.Append(_connection, null, Roll("1d20", total: 98));

        Assert.Equal(totals, DiceRollLog.ForEncounter(_connection, encounter, includeSecret, max).Select(r => r.Total));
    }

    /// <summary>The encounter column is a real foreign key: a roll cannot name a fight that does not exist.</summary>
    [Fact]
    public void Append_UnknownEncounter_IsRefusedBySqlite() =>
        Assert.Throws<SqliteException>(() => DiceRollLog.Append(_connection, null, Roll("1d6") with { EncounterId = CampaignDatabase.NewId() }));

    /// <summary>
    /// The combat tracker builds its rolls' detail with the same Domain function the dice_roll tool uses (moved out of the
    /// host for it), and the stored text is that function's output byte for byte: a session listing renders both alike.
    /// The golden is the shape the host wrote before the move (v1: source, call, one group per dice term with its
    /// term/label/value/dice; the flat +5 is in the total, not a group).
    /// </summary>
    [Fact]
    public void Append_DetailFromDiceLogDetail_IsStoredByteForByteInTheHostsShape()
    {
        var encounter = Encounter("The crypt");
        var expression = DiceExpression.Parse("4d6kh3+5");
        var roll = DiceEvaluator.Roll(expression, ScriptedDiceRoller.Sequence(6, 1, 4, 3));
        var detail = DiceLogDetail.Json(roll, 0, 1, "crypto");

        DiceRollLog.Append(_connection, null, Roll(expression.Text, total: roll.Total) with { Detail = detail, EncounterId = encounter });

        Assert.Equal(18, roll.Total);
        Assert.Equal(
            "{\"v\":1,\"source\":\"crypto\",\"call\":{\"roll\":1,\"of\":1},\"groups\":[{\"term\":\"4d6kh3\",\"label\":null,\"value\":13," +
            "\"dice\":[{\"faces\":[{\"face\":6}],\"raw\":6,\"value\":6},{\"faces\":[{\"face\":1}],\"raw\":1,\"value\":1,\"dropped\":true}," +
            "{\"faces\":[{\"face\":4}],\"raw\":4,\"value\":4},{\"faces\":[{\"face\":3}],\"raw\":3,\"value\":3}]}]}",
            detail);
        Assert.Equal(detail, Assert.Single(DiceRollLog.ForEncounter(_connection, encounter, includeSecret: true, max: 1)).Detail);
    }

    private string Encounter(string name, string status = CampaignValues.EncounterStatuses.Active) =>
        new CampaignSeed(_connection).Encounter(_campaign.Id, name, status: status, sessionId: _session.EntityId);

    private DiceRollRow Roll(string expression, long total = 10, long? outcome = null, string? label = null, bool secret = false) =>
        new(0, string.Empty, _campaign.Id, _session.EntityId, expression, label, total, outcome, "{\"rolls\":[12]}", secret ? 1L : 0L,
            _db.Database.Now(), EncounterId: null);
}
