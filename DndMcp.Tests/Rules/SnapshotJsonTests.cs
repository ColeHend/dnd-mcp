using System.Text.Json;
using System.Text.Json.Nodes;
using DndMcp.Domain.Encounters;
using DndMcp.Domain.Features;
using DndMcp.Domain.Rules;
using DndMcp.Domain.Simulation;
using DndMcp.Tests.Simulation;
using Xunit;

namespace DndMcp.Tests.Rules;

/// <summary>
/// Invariant: a stat block snapshot round-trips — formulas through their object form and <see cref="DamageFormula.Of"/>
/// (no DSL limits: the tarrasque's 33d20+330 reads back), Challenge Ratings as the SRD's strings — and the snapshot
/// options write snake_case, leave nulls and computed members out, and ignore unknown fields on read. Every SRD monster
/// is round-tripped in the integration tests (<c>StatBlockSnapshotTests</c>).
/// </summary>
public sealed class SnapshotJsonTests
{
    private sealed record Holder(DamageFormula? Formula, ChallengeRating? Cr);

    [Theory]
    [InlineData("2d6+3", """{"dice":[[2,6]],"flat":3}""")]
    [InlineData("1d4-1d4", """{"dice":[[1,4],[1,4,true]],"flat":0}""")]
    [InlineData("-1d4", """{"dice":[[1,4,true]],"flat":0}""")]
    [InlineData("1d10+1d6", """{"dice":[[1,10],[1,6]],"flat":0}""")]
    [InlineData("1d4-1", """{"dice":[[1,4]],"flat":-1}""")]
    public void DamageFormula_WritesTheObjectFormAndReadsItBack(string text, string json)
    {
        var formula = text.Contains("-1d4", StringComparison.Ordinal)
            ? DamageFormula.ParseBonusDice(text, "dice", "no flat")
            : DamageFormula.ParseDamage(text, "damage");

        Assert.Equal(json, JsonSerializer.Serialize(formula));
        var back = JsonSerializer.Deserialize<DamageFormula>(json)!;
        Assert.Equal(formula, back);
        Assert.Equal(formula.Dice, back.Dice);
        Assert.Equal(formula.Flat, back.Flat);
    }

    [Fact]
    public void DamageFormula_BeyondTheDslLimits_RoundTrips()
    {
        var tarrasque = DamageFormula.Of([new DiceTerm(33, 20)], 330);

        Assert.Equal("33d20+330", JsonSerializer.Deserialize<DamageFormula>(JsonSerializer.Serialize(tarrasque))!.Text);
        Assert.Equal(DamageFormula.Zero, JsonSerializer.Deserialize<DamageFormula>("""{"dice":[],"flat":0}"""));
        Assert.Equal(DamageFormula.Constant(5), JsonSerializer.Deserialize<DamageFormula>("""{"flat":5,"note":"ignored"}"""));
        Assert.Equal("2d6", JsonSerializer.Deserialize<DamageFormula>("""{"dice":[[1,6],[1,6]]}""")!.Text);
    }

    [Theory]
    [InlineData("\"2d6\"")]
    [InlineData("""{"dice":[[0,6]],"flat":0}""")]
    [InlineData("""{"dice":[[2]],"flat":0}""")]
    [InlineData("""{"dice":[[2,6,true,1]],"flat":0}""")]
    [InlineData("""{"dice":[2,6],"flat":0}""")]
    [InlineData("""{"dice":[],"flat":1.5}""")]
    public void DamageFormula_Malformed_IsAJsonException(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DamageFormula>(json));
    }

    [Fact]
    public void ChallengeRating_EveryRow_RoundTripsAsTheSrdString()
    {
        foreach (var cr in ChallengeRating.All)
        {
            var json = JsonSerializer.Serialize(cr);
            Assert.Equal($"\"{cr}\"", json);
            Assert.Equal(cr, JsonSerializer.Deserialize<ChallengeRating>(json));
        }

        Assert.Equal("\"1/8\"", JsonSerializer.Serialize(ChallengeRating.Parse("0.125")));
        Assert.Equal(ChallengeRating.Parse("1/8"), JsonSerializer.Deserialize<ChallengeRating>("0.125"));
    }

    [Theory]
    [InlineData("\"3.5\"")]
    [InlineData("\"1/3\"")]
    [InlineData("3.5")]
    [InlineData("true")]
    public void ChallengeRating_NotATableValue_IsAJsonException(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ChallengeRating>(json));
    }

    [Fact]
    public void Converters_ApplyInsideRecordsAndToNullables()
    {
        var holder = new Holder(DamageFormula.ParseDamage("1d8+2", "d"), ChallengeRating.Parse("1/4"));
        var json = JsonSerializer.Serialize(holder, StatBlockSnapshotJson.Options);

        Assert.Equal("""{"formula":{"dice":[[1,8]],"flat":2},"cr":"1/4"}""", json);
        Assert.Equal(holder, JsonSerializer.Deserialize<Holder>(json, StatBlockSnapshotJson.Options));
        Assert.Equal("{}", JsonSerializer.Serialize(new Holder(null, null), StatBlockSnapshotJson.Options));
        Assert.Equal(new Holder(null, null), JsonSerializer.Deserialize<Holder>("{}", StatBlockSnapshotJson.Options));
    }

    [Fact]
    public void StatBlockSnapshotJson_AHandBuiltStatBlock_RoundTripsWithoutComputedMembers()
    {
        var block = TestStatBlocks.AdultRedDragon;
        var json = StatBlockSnapshotJson.Serialize(block);
        var back = StatBlockSnapshotJson.Deserialize(json);

        Assert.Equal(json, StatBlockSnapshotJson.Serialize(back));
        Assert.Contains("\"challenge_rating\":\"", json, StringComparison.Ordinal);
        Assert.Contains("\"hit_dice\":{\"dice\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("total_uses", json, StringComparison.Ordinal);
        Assert.DoesNotContain(":null", json, StringComparison.Ordinal);
        Assert.Equal(block.Multiattacks.Select(m => m.TotalUses), back.Multiattacks.Select(m => m.TotalUses));
        Assert.Equal(block.ChallengeRating, back.ChallengeRating);
        Assert.Equal(block.HitDice, back.HitDice);
    }

    [Fact]
    public void StatBlockSnapshotJson_UnknownFieldsAreIgnoredAndARequiredOneMissingIsRefused()
    {
        var json = StatBlockSnapshotJson.Serialize(TestStatBlocks.Ogre);
        var later = json.Insert(1, "\"written_by_a_later_build\":{\"x\":1},");

        Assert.Equal("Ogre", StatBlockSnapshotJson.Deserialize(later).Name);
        Assert.Throws<JsonException>(() => StatBlockSnapshotJson.Deserialize(json.Replace("\"name\":\"Ogre\",", "", StringComparison.Ordinal)));
        Assert.Throws<JsonException>(() => StatBlockSnapshotJson.Deserialize("null"));
    }

    [Theory]
    [InlineData("hit_dice")]
    [InlineData("traits")]
    [InlineData("actions")]
    [InlineData("save_bonuses")]
    [InlineData("abilities")]
    [InlineData("challenge_rating")]
    public void StatBlockSnapshotJson_ANeverNullMemberGivenAsNull_IsAJsonException(string member)
    {
        // A corrupt snapshot fails where it is read, not as a NullReferenceException in a later damage step.
        var node = JsonNode.Parse(StatBlockSnapshotJson.Serialize(TestStatBlocks.Ogre))!.AsObject();
        Assert.True(node.ContainsKey(member));
        node[member] = null;

        Assert.Throws<JsonException>(() => StatBlockSnapshotJson.Deserialize(node.ToJsonString()));
    }

    [Fact]
    public void StatBlockSnapshotJson_ANestedNeverNullMemberNullOrAConstructorArgumentMissing_IsAJsonException()
    {
        var json = StatBlockSnapshotJson.Serialize(TestStatBlocks.Ogre);
        var nullDice = JsonNode.Parse(json)!.AsObject();
        var roll = nullDice["actions"]![0]!["damage"]![0]!.AsObject();
        roll["dice"] = null;
        var missingDice = JsonNode.Parse(json)!.AsObject();
        missingDice["actions"]![0]!["damage"]![0]!.AsObject().Remove("dice");

        Assert.Throws<JsonException>(() => StatBlockSnapshotJson.Deserialize(nullDice.ToJsonString()));
        Assert.Throws<JsonException>(() => StatBlockSnapshotJson.Deserialize(missingDice.ToJsonString()));
    }

    [Fact]
    public void StatBlockSnapshotJson_ANullableMemberGivenAsNull_ReadsAsAbsent()
    {
        var node = JsonNode.Parse(StatBlockSnapshotJson.Serialize(TestStatBlocks.Ogre))!.AsObject();
        node["xp_in_lair"] = null;
        node["armor_class_note"] = null;

        var back = StatBlockSnapshotJson.Deserialize(node.ToJsonString());

        Assert.Null(back.XpInLair);
        Assert.Null(back.ArmorClassNote);
    }
}
