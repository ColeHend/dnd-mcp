using System.Text.Json;
using Dapper;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Repository.Campaign.Write;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.Tests.CampaignScenarios;

/// <summary>
/// Invariant: <c>understand-onepiece.md</c> §3 rows 41-49, the invention register and the gate's shape, in the One Piece
/// world (which already holds F1 accepted and F2 struck, and the question codes Q7, Q21, Q22 on entities): one F sequence
/// per campaign shared by facts and entities, max + 1 over struck rows, never reused, never changed by accepting, the same
/// number in a dry run as in the real run, independent per campaign; a taken code refused naming the next free one; a gate
/// that is not an object never stored. Cole refers to inventions by number ("F36 accepted"), so a renumbered or reused
/// code silently points his rulings at the wrong invention.
/// </summary>
public sealed class OnePieceRegisterScenarioTests : IDisposable
{
    private readonly OnePieceScenario _world = OnePieceScenario.Build();

    public void Dispose() => _world.Dispose();

    private WriteResult Propose(string statement, string? code = null, bool dryRun = false) =>
        _world.F.Apply(_world.Campaign, new WriteContext { DryRun = dryRun },
            new CampaignOpSpec { Op = "fact", Statement = statement, CanonStatus = "proposed", Code = code });

    private WriteResult ProposeCharacter(string name) =>
        _world.F.Apply(_world.Campaign, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = name, CanonStatus = "proposed" });

    [Fact]
    public void Fact_Row41AProposedFactAfterF1AndF2_GetsF3()
    {
        var result = Propose("The harbourmaster of Serret owes Nadar a favour.");

        Assert.Equal("F3", Assert.Single(result.Applied).Code);
        Assert.Equal("F3", _world.F.Fact(_world.Campaign, "F3").Code);
    }

    [Fact]
    public void Upsert_Row42AProposedCharacterAfterF3_GetsF4FromTheSameSequence()
    {
        Propose("The harbourmaster of Serret owes Nadar a favour.");

        var result = ProposeCharacter("Harbourmaster Quell");

        Assert.Equal("F4", Assert.Single(result.Applied).Code);
        Assert.Equal("F4", _world.F.Entity(_world.Campaign, "character:harbourmaster-quell").Code);
    }

    [Fact]
    public void Codes_Row43StrikeF4ThenProposeAgain_GetsF5AndF4KeepsItsCode()
    {
        Propose("The harbourmaster of Serret owes Nadar a favour.");
        ProposeCharacter("Harbourmaster Quell");

        _world.F.Apply(_world.Campaign, new CampaignOpSpec { Op = "upsert", Ref = "F4", CanonStatus = "struck" });
        var next = Propose("A second invented fact.");

        Assert.Equal("F5", Assert.Single(next.Applied).Code);
        var struck = _world.F.Entity(_world.Campaign, "character:harbourmaster-quell");
        Assert.Equal(("F4", "struck"), (struck.Code, struck.CanonStatus));
    }

    [Fact]
    public void Fact_Row44AcceptF3_KeepsTheCode()
    {
        Propose("The harbourmaster of Serret owes Nadar a favour.");

        var result = _world.F.Apply(_world.Campaign, new CampaignOpSpec { Op = "fact", Ref = "F3", CanonStatus = "accepted" });

        var fact = _world.F.Fact(_world.Campaign, "F3");
        Assert.Equal(("accepted", "F3"), (fact.CanonStatus, fact.Code));
        Assert.Equal(["canon_status"], Assert.Single(result.Applied).ChangedFields);
    }

    [Theory]
    [InlineData("canon")]
    [InlineData("played")]
    [InlineData("ruled")]
    [InlineData("lean")]
    public void Fact_Row45NotProposed_HasNoCode(string canonStatus)
    {
        var result = _world.F.Apply(_world.Campaign, WriteContext.For(7), new CampaignOpSpec { Op = "fact", Statement = "A fact.", CanonStatus = canonStatus });

        Assert.Null(Assert.Single(result.Applied).Code);
    }

    [Fact]
    public void Fact_Row46ATakenCode_IsRefusedNamingTheHolderByHandleAndTheNextFreeCode()
    {
        Propose("The harbourmaster of Serret owes Nadar a favour.");
        ProposeCharacter("Harbourmaster Quell");
        Propose("A second invented fact.");
        var before = _world.F.Dump();

        var ex = Assert.Throws<DndInputException>(() => Propose("A third.", code: "F3"));

        var holder = _world.F.Fact(_world.Campaign, "F3").Seq;
        Assert.Contains($"F3 is taken (by f:{holder}); omit code to auto-number (next F6)", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, _world.F.Dump());
    }

    [Fact]
    public void Fact_Row47DryRunThenReal_ShowTheSameCodeAndTheDryRunConsumesNothing()
    {
        var dry = Propose("The harbourmaster of Serret owes Nadar a favour.", dryRun: true);
        var real = Propose("The harbourmaster of Serret owes Nadar a favour.");

        Assert.Equal(("F3", true), (Assert.Single(dry.Applied).Code, dry.DryRun));
        Assert.Null(dry.BatchId);
        Assert.Equal("F3", Assert.Single(real.Applied).Code);
    }

    [Fact]
    public void Codes_Row48ASecondCampaign_NumbersIndependentlyFromF1()
    {
        var other = _world.F.Store.Create("Belmakor — sky-world", "player", "2014", slug: "belmakor", myCharacter: "Belmakor Silverwind").Campaign;

        var here = Propose("The harbourmaster of Serret owes Nadar a favour.");
        var there = _world.F.Apply(other, new CampaignOpSpec { Op = "fact", Statement = "A sky-world invention.", CanonStatus = "proposed" });
        var hereAgain = Propose("Another One Piece invention.");

        Assert.Equal("F3", Assert.Single(here.Applied).Code);
        Assert.Equal("F1", Assert.Single(there.Applied).Code);
        Assert.Equal("F4", Assert.Single(hereAgain.Applied).Code);
    }

    /// <summary>
    /// The register's codes are what the author searches by: the author's search lists a proposed fact with its code, and
    /// a player view never sees a proposed invention at all (not in play).
    /// </summary>
    [Fact]
    public void Search_Row41AProposedInvention_ShowsItsCodeToTheAuthorAndNothingToTheParty()
    {
        Propose("The harbourmaster of Serret owes Nadar a favour.");

        var author = _world.Reads.Search(_world.Campaign, "harbourmaster", "author");
        var party = _world.Reads.Search(_world.Campaign, "harbourmaster", "party");

        Assert.Contains(author.Facts, f => f.Code == "F3" && f.CanonStatus == "proposed");
        Assert.Empty(party.Facts);
    }

    /// <summary>
    /// Row 49 at the repository level: a gate is an object, so <c>gate: ["f:1"]</c> cannot even be bound to an op (the
    /// typed spec refuses it with a message naming the field; the host turns that into the tool error), and the schema's
    /// CHECK is the backstop that refuses a non-object gate reaching the table any other way. The tool-level message is
    /// the host's (stage 3b).
    /// </summary>
    [Fact]
    public void Gate_Row49AnArrayGate_CannotBeBoundAndCannotBeStored()
    {
        var bind = Assert.ThrowsAny<JsonException>(() =>
            JsonSerializer.Deserialize<CampaignOpSpec>("{\"op\": \"fact\", \"ref\": \"f:1\", \"gate\": [\"f:1\"]}", DslJson.Options));
        Assert.Contains("gate", bind.Message, StringComparison.OrdinalIgnoreCase);

        using var connection = _world.F.Open();
        var id = _world.F.Fact(_world.Campaign, _world.Facts.Seal).Id;
        var store = Assert.Throws<SqliteException>(() => connection.Execute("UPDATE fact SET gate = '[\"f:1\"]' WHERE id = @id", new { id }));
        Assert.Equal(19, store.SqliteErrorCode);
        Assert.NotNull(_world.Reads.Get(_world.Campaign, "author", EntityIncludes.Default, null, _world.Facts.Seal).Facts.Single().Author!.Fact.Gate);
    }
}
