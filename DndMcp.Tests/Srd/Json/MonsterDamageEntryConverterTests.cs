using System.Text.Json;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Models;
using Xunit;

namespace DndMcp.Tests.Srd.Json;

/// <summary>
/// Pins how a 2014 monster <c>damage[]</c> element is read and written. A roll is a roll and a Choice is a Choice,
/// whatever the property order. Anything that is neither (a string, an array, <c>null</c>, a Choice of something other
/// than damage, a roll without dice) throws instead of becoming an empty roll or a null entry that silently removes
/// damage from the DPR engine. An error inside an entry reports where that entry is in the FILE, so a re-vendor
/// failure names the monster's position rather than <c>$</c>.
/// </summary>
public sealed class MonsterDamageEntryConverterTests
{
    private const string Slashing = """{"index":"slashing","name":"Slashing","url":"/api/2014/damage-types/slashing"}""";
    private const string Lightning = """{"index":"lightning","name":"Lightning","url":"/api/2014/damage-types/lightning"}""";
    private const string Thunder = """{"index":"thunder","name":"Thunder","url":"/api/2014/damage-types/thunder"}""";
    private const string Con = """{"index":"con","name":"CON","url":"/api/2014/ability-scores/con"}""";

    private const string ChoiceFrom =
        $$"""{"option_set_type":"options_array","options":[{"option_type":"damage","damage_type":{{Lightning}},"damage_dice":"1d6"},{"option_type":"damage","damage_type":{{Thunder}},"damage_dice":"1d6"}]}""";

    [Fact]
    public void Read_DamageRoll_IsARollNotAChoice()
    {
        var entry = Deserialize($$"""{"damage_type":{{Slashing}},"damage_dice":"2d6+5"}""");

        Assert.False(entry.IsChoice);
        Assert.Null(entry.Choice);
        Assert.Equal("2d6+5", entry.Damage!.DamageDice);
        Assert.Equal("slashing", entry.Damage.DamageType.Index);
        Assert.Null(entry.Damage.Dc);
    }

    [Fact]
    public void Read_DamageRollWithItsOwnSave_KeepsTheSave()
    {
        var entry = Deserialize(
            $$"""{"dc":{"dc_type":{{Con}},"dc_value":15,"success_type":"half"},"damage_type":{{Slashing}},"damage_dice":"7d6"}""");

        Assert.Equal(15, entry.Damage!.Dc!.DcValue);
        Assert.Equal(SaveSuccessTypes.Half, entry.Damage.Dc.SuccessType);
    }

    [Theory]
    [InlineData($$"""{"choose":1,"type":"damage","from":{{ChoiceFrom}}}""")]
    [InlineData($$"""{"from":{{ChoiceFrom}},"type":"damage","choose":1}""")]
    public void Read_DamageChoice_IsAChoiceWhateverThePropertyOrder(string json)
    {
        var entry = Deserialize(json);

        Assert.True(entry.IsChoice);
        Assert.Null(entry.Damage);
        Assert.Equal(1, entry.Choice!.Choose);
        Assert.Equal(ChoiceTypes.Damage, entry.Choice.Type);
        Assert.Equal(["lightning", "thunder"], entry.Choice.From.Options.Select(o => o.DamageType.Index));
    }

    [Theory]
    [InlineData("null", "found null")]
    [InlineData("\"2d6\"", "found a string")]
    [InlineData("[]", "found an array")]
    [InlineData("42", "found a number")]
    [InlineData("true", "found a boolean")]
    [InlineData($$"""{"choose":1,"type":"action","from":{{ChoiceFrom}}}""", "but its type is \"action\"")]
    [InlineData($$"""{"choose":1,"from":{{ChoiceFrom}}}""", "but its type is missing")]
    [InlineData($$"""{"choose":1,"type":7,"from":{{ChoiceFrom}}}""", "but its type is 7")]
    [InlineData($$"""{"choose":1,"type":{"index":"damage"},"from":{{ChoiceFrom}}}""", "but its type is an object")]
    [InlineData($$"""{"damage_type":{{Slashing}}}""", "damage_dice")]
    [InlineData("""{"damage_dice":"2d6"}""", "damage_type")]
    public void Read_MalformedEntry_ThrowsJsonExceptionNamingTheProblem(string json, string expectedFragment)
    {
        var ex = Assert.Throws<JsonException>(() => Deserialize(json));

        Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
    }

    // attack_bonus comes AFTER damage[]: a converter that leaves the reader anywhere but the end of each entry would
    // misread the next entry or lose the bonus.
    [Fact]
    public void Read_EntriesInAnActionsDamageArray_KeepOrderAndKindAndLeaveTheReaderAfterTheEntry()
    {
        var json = $$"""{"name":"Scimitar","desc":"d","damage":[{"damage_type":{{Slashing}},"damage_dice":"2d6+5"},{"choose":1,"type":"damage","from":{{ChoiceFrom}}},{"damage_type":{{Thunder}},"damage_dice":"1d4"}],"attack_bonus":9}""";

        var action = JsonSerializer.Deserialize<MonsterAction2014>(json, SrdTestContent.StrictOptions)!;

        var damage = action.Damage!;
        Assert.Equal([false, true, false], damage.Select(d => d.IsChoice));
        Assert.Equal("1d4", damage[2].Damage!.DamageDice);
        Assert.Equal(9, action.AttackBonus);
    }

    // Files are read from a stream in blocks, and the converter's lookahead must work on a partial block (Skip throws
    // there; TrySkip does not). A 1-byte buffer makes every block partial. Nested objects and arrays inside the
    // entries are what the lookahead has to skip over.
    [Fact]
    public void Read_FromAStreamInTinyBlocks_ReadsRollsAndChoices()
    {
        var json = $$"""[{"name":"Scimitar","desc":"d","damage":[{"damage_type":{{Slashing}},"damage_dice":"2d6+5"},{"from":{{ChoiceFrom}},"type":"damage","choose":1}],"attack_bonus":9}]""";
        var options = new JsonSerializerOptions(SrdTestContent.StrictOptions) { DefaultBufferSize = 1 };

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var action = Assert.Single(JsonSerializer.Deserialize<List<MonsterAction2014>>(stream, options)!);

        var damage = action.Damage!;
        Assert.Equal([false, true], damage.Select(d => d.IsChoice));
        Assert.Equal(["lightning", "thunder"], damage[1].Choice!.From.Options.Select(o => o.DamageType.Index));
        Assert.Equal(9, action.AttackBonus);
    }

    // The serializer stores a null list element without calling a converter unless the converter opts in (HandleNull).
    // Without it, damage[0] would be a null entry and the DPR engine would throw far from the data.
    [Fact]
    public void Read_NullInsideAnActionsDamageArray_Throws()
    {
        const string json = """{"name":"Bite","desc":"d","attack_bonus":4,"damage":[null]}""";

        var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MonsterAction2014>(json, SrdJson.Options));

        Assert.Contains("found null", ex.Message, StringComparison.Ordinal);
        Assert.Equal("$.damage[0]", ex.Path);
    }

    // Only a TOP-LEVEL "choose" makes a Choice. A lookahead that matched "choose" at any depth would misread this roll.
    [Fact]
    public void Read_ChooseNestedInsideARoll_IsStillARoll()
    {
        const string json = """{"damage_type":{"index":"x","name":"X","url":"/api/2014/damage-types/x","choose":1},"damage_dice":"1d6"}""";

        var entry = Deserialize(json);

        Assert.False(entry.IsChoice);
        Assert.Equal("1d6", entry.Damage!.DamageDice);
    }

    // The failure a re-vendor produces: a new field (strict options) or a missing one inside a damage entry. The
    // exception must say which entry of which action in the file, not "$" or "$.new_field" at line 0.
    [Theory]
    [InlineData($$"""{"damage_type":{{Slashing}},"damage_dice":"1d6","new_field":1}""", true, "new_field")]
    [InlineData($$"""{"damage_type":{{Slashing}}}""", false, "damage_dice")]
    [InlineData($$$"""{"choose":1,"type":"damage","from":{"option_set_type":"options_array","options":[{"option_type":"damage","damage_type":{{{Thunder}}},"damage_dice":"1d6","new_field":2}]}}""", true, "new_field")]
    public void Read_ErrorInsideADamageEntry_ReportsTheEntrysLocationInTheFile(string entryJson, bool strict, string expectedFragment)
    {
        var json = "[\n" +
                   """{"name":"Claw","desc":"d"}""" + ",\n" +
                   """{"name":"Bite","desc":"d","damage":[""" + $$"""{"damage_type":{{Slashing}},"damage_dice":"1d4"},""" + "\n" +
                   entryJson + "]}\n]";

        var ex = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<List<MonsterAction2014>>(json, strict ? SrdTestContent.StrictOptions : SrdJson.Options));

        Assert.Equal("$[1].damage[1]", ex.Path);
        Assert.Equal(3, ex.LineNumber);
        Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData($$"""{"damage_type":{{Slashing}},"damage_dice":"2d6+5"}""")]
    [InlineData($$"""{"choose":1,"type":"damage","from":{{ChoiceFrom}}}""")]
    public void Write_AfterRead_ReproducesTheOriginalObject(string json)
    {
        var written = JsonSerializer.SerializeToElement(Deserialize(json), SrdJson.Options);

        using var original = JsonDocument.Parse(json);
        Assert.Null(JsonDiff.FirstDifference(original.RootElement, written));
    }

    [Fact]
    public void FromDamageAndFromChoice_Null_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => MonsterDamageEntry2014.FromDamage(null!));
        Assert.Throws<ArgumentNullException>(() => MonsterDamageEntry2014.FromChoice(null!));
    }

    private static MonsterDamageEntry2014 Deserialize(string json) =>
        JsonSerializer.Deserialize<MonsterDamageEntry2014>(json, SrdJson.Options)!;
}
