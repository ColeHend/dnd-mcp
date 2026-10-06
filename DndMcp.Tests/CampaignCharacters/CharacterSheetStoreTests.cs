using Dapper;
using DndMcp.Domain.Characters;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Characters;
using DndMcp.Repository.Campaign.Write;
using Xunit;

namespace DndMcp.Tests.CampaignCharacters;

/// <summary>
/// <see cref="CharacterSheetStore"/>: the row ↔ model round trip, the column lists agreeing, reads as of a session, and a
/// stored value of the wrong type refused as a store problem that names the column and never quotes the stored text.
/// </summary>
public sealed class CharacterSheetStoreTests
{
    [Fact]
    public void Columns_CatalogueRowRecordAndSheetModel_NameTheSameColumnsInOrder()
    {
        var catalogue = CampaignTables.CharacterSheet.Columns.Select(c => c.Name).ToList();
        var record = CharacterSheetRow.Columns.Split(',').Select(c => c.Trim()).ToList();

        Assert.Equal(SheetColumns.All, catalogue);
        Assert.Equal(SheetColumns.All, record);
        Assert.Equal(SheetColumns.PerKey.Order(StringComparer.Ordinal),
            CampaignTables.CharacterSheet.Columns.Where(c => c.LoggedPerKey).Select(c => c.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Incapacitating_SheetAndCombatRules_NameTheSameConditions()
    {
        // P's sheet list and R's tracker list decide one rule (contract §5.8: these end concentration); they must not drift.
        Assert.Equal(DndMcp.Domain.Rules.CombatRules.IncapacitatingConditions.Order(StringComparer.Ordinal),
            SheetValues.Conditions.Incapacitating.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Read_SheetWrittenByUpdate_RoundTripsEveryColumn()
    {
        using var world = SheetWorld.Dm();
        world.Update("character:hero", FixtureSheets.BjornJson);

        using var connection = world.Open();
        var read = CharacterSheetStore.Read(connection, world.Id("character:hero"))!;
        var row = connection.QuerySingle<CharacterSheetRow>($"SELECT {CharacterSheetRow.Columns} FROM character_sheet WHERE entity_id = @id",
            new { id = world.Id("character:hero") });
        var fromRow = CharacterSheetStore.FromRow(row);

        foreach (var column in SheetColumns.All)
        {
            Assert.Equal(SheetJson.Column(fromRow, column), SheetJson.Column(read, column));
        }

        Assert.Equal(85, read.MaxHp);
        Assert.Equal(85, read.Hp);
        Assert.Equal("fixture", read.SheetSource);
        Assert.NotNull(read.CreatedAt);
    }

    [Fact]
    public void Read_NoSheet_IsNull()
    {
        using var world = SheetWorld.Dm();
        using var connection = world.Open();

        Assert.Null(CharacterSheetStore.Read(connection, world.Id("character:hero")));
        Assert.Empty(CharacterSheetStore.ReadMany(connection, [world.Id("character:hero"), world.Id("character:sidekick")]));
    }

    [Theory]
    [InlineData("classes", "'{\"class\":\"wizard\"}'")]
    [InlineData("spell_slots", "'[1,2]'")]
    [InlineData("death_saves", "'{\"successes\":\"two\",\"failures\":0,\"stable\":false}'")]
    [InlineData("resources", "'{\"bladesong\":{\"name\":\"Bladesong\",\"max\":\"four\"}}'")]
    public void Read_StoredValueOfTheWrongType_IsAStoreRefusalNamingTheColumnNotTheText(string column, string sqlValue)
    {
        using var world = SheetWorld.Dm();
        world.Update("character:hero", """{ "level": 3 }""");
        using (var connection = world.Open())
        {
            // A row written by something other than this server: the CHECKs allow it (valid JSON of the column's kind or not).
            connection.Execute("PRAGMA ignore_check_constraints = ON");
            connection.Execute($"UPDATE character_sheet SET {column} = {sqlValue} WHERE entity_id = @id", new { id = world.Id("character:hero") });
        }

        using var read = world.Open();
        var ex = Assert.Throws<CampaignStoreUnavailableException>(() => CharacterSheetStore.Read(read, world.Id("character:hero")));

        Assert.Contains(column, ex.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be read", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("four", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("two", ex.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidDataException>(ex.InnerException);
    }

    /// <summary>
    /// R04: one unreadable sheet keeps refusing every party-wide read (the roster, the party list, add_party), but the
    /// refusal says whose sheet it is and in which campaign, so the author can repair or remove that one row: the author
    /// handle and its e:&lt;n&gt; for the author, the e:&lt;n&gt; alone for any other view.
    /// </summary>
    [Fact]
    public void UnreadableSheet_TheRefusalNamesWhoseSheetAndTheCampaign_TheHandleOnlyForTheAuthor()
    {
        using var world = SheetWorld.Dm();
        world.Update("character:hero", """{ "level": 3, "max_hp": 20 }""");
        world.Update("character:sidekick", """{ "level": 3, "max_hp": 20 }""");
        world.F.Db.WriteBehindTheServer($"UPDATE character_sheet SET conditions = '[1,2]' WHERE entity_id = '{world.Id("character:sidekick")}'");
        var seq = world.F.Entity(world.Campaign, "character:sidekick").SeqHandle;

        var author = new[]
        {
            Assert.Throws<CampaignStoreUnavailableException>(() => world.Reader.Party(world.Campaign)).Message,
            Assert.Throws<CampaignStoreUnavailableException>(() => PartyRoster.Read(world.Database, world.Campaign)).Message,
            Assert.Throws<CampaignStoreUnavailableException>(() => world.Writer.Damage(world.Campaign, "character:sidekick", 1, null, WriteContext.Default)).Message,
        };
        var party = Assert.Throws<CampaignStoreUnavailableException>(() => world.Reader.Party(world.Campaign, DndMcp.Domain.Campaign.Perspective.Parse("party"))).Message;

        Assert.All(author, m => Assert.StartsWith($"The character sheet of character:sidekick ({seq}) in campaign sea cannot be read (", m, StringComparison.Ordinal));
        Assert.StartsWith($"The character sheet of {seq} in campaign sea cannot be read (", party, StringComparison.Ordinal);
        Assert.DoesNotContain("character:sidekick", party, StringComparison.Ordinal);
        Assert.All(author.Append(party), m => Assert.Contains("conditions column", m, StringComparison.Ordinal));
        Assert.Equal("Hero Prime", Assert.Single(world.Reader.Get(world.Campaign, "character:hero").Characters).Name);
    }

    [Fact]
    public void Write_UnreadableSheet_IsRefusedAndNothingIsLogged()
    {
        using var world = SheetWorld.Dm();
        world.Update("character:hero", """{ "level": 3, "max_hp": 20 }""");
        using (var connection = world.Open())
        {
            connection.Execute("UPDATE character_sheet SET classes = '[{\"class\":\"wizard\",\"level\":\"x\"}]' WHERE entity_id = @id", new { id = world.Id("character:hero") });
        }

        var before = world.ChangeRows();
        Assert.Throws<CampaignStoreUnavailableException>(() => world.Writer.Damage(world.Campaign, "character:hero", 3, null, WriteContext.Default));
        Assert.Equal(before, world.ChangeRows());
    }

    [Fact]
    public void AsOf_ChangedInALaterSession_ReadsTheEarlierValue()
    {
        using var world = SheetWorld.Dm();
        world.F.Played(world.Campaign, 1);
        world.F.Played(world.Campaign, 2);
        world.Update("character:hero", """{ "level": 3, "max_hp": 20 }""", WriteContext.For(1));
        world.Writer.Damage(world.Campaign, "character:hero", 5, null, WriteContext.For(2));

        using var connection = world.Open();
        var id = world.Id("character:hero");

        Assert.Equal(20, CharacterSheetStore.AsOf(connection, id, 1)!.Hp);
        Assert.Equal(15, CharacterSheetStore.AsOf(connection, id, 2)!.Hp);
        Assert.Null(CharacterSheetStore.AsOf(connection, id, 0));
        Assert.Equal(15, CharacterSheetStore.Read(connection, id)!.Hp);
    }
}
