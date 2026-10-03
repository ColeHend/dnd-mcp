using System.Reflection;
using System.Text.Json;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using Xunit;

namespace DndMcp.Tests.Campaign;

/// <summary>
/// Invariant: the per-op field table covers every property of <see cref="CampaignOpSpec"/> under its wire name, every
/// field belongs to at least one op, and a field given to an op that does not take it is always refused (never silently
/// ignored).
/// </summary>
public sealed class CampaignOpFieldsTests
{
    [Fact]
    public void All_IsEveryCampaignOpSpecPropertyUnderItsSnakeCaseName()
    {
        var properties = typeof(CampaignOpSpec).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => JsonNamingPolicy.SnakeCaseLower.ConvertName(p.Name))
            .ToList();

        Assert.Equal(properties, CampaignOpFields.All.Select(f => f.Name));
    }

    [Fact]
    public void ByOp_CoversEveryOpAndNamesOnlyRealFieldsOnce()
    {
        Assert.Equal(CampaignValues.OpKinds.Set.Values.Order(), CampaignOpFields.ByOp.Keys.Order());
        var names = CampaignOpFields.All.Select(f => f.Name).ToHashSet();
        Assert.All(CampaignOpFields.ByOp.Values.SelectMany(f => f).Concat(CampaignOpFields.Common), f => Assert.Contains(f, names));
        Assert.All(CampaignOpFields.ByOp.Values, fields => Assert.Equal(fields.Count, fields.Distinct().Count()));
    }

    [Fact]
    public void EveryField_BelongsToAtLeastOneOp()
    {
        Assert.All(CampaignOpFields.All, f =>
            Assert.True(CampaignOpFields.ByOp.Keys.Any(op => CampaignOpFields.Takes(op, f.Name)), f.Name));
    }

    [Fact]
    public void IsGiven_SeesEveryFieldOfAFullSpecAndNoneOfAnEmptyOne()
    {
        var full = Bind(FullSpecJson);

        Assert.All(CampaignOpFields.All, f => Assert.True(f.IsGiven(full), f.Name));
        Assert.All(CampaignOpFields.All, f => Assert.False(f.IsGiven(new CampaignOpSpec()), f.Name));
    }

    [Theory]
    [InlineData("link", "from, rel, to, label, attitude, symmetric, visibility, status, since, until, mode, data, note")]
    [InlineData("unlink", "from, rel, to")]
    [InlineData("tick", "ref, amount")]
    [InlineData("delete", "ref")]
    public void Describe_ListsTheOpsFields(string op, string described)
    {
        Assert.Equal(described, CampaignOpFields.Describe(op));
    }

    public static TheoryData<string, string> EveryRefusedField()
    {
        var data = new TheoryData<string, string>();
        foreach (var op in CampaignValues.OpKinds.Set.Values)
        {
            foreach (var field in CampaignOpFields.All.Select(f => f.Name).Where(f => !CampaignOpFields.Takes(op, f)))
            {
                data.Add(op, field);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryRefusedField))]
    public void Validate_FieldTheOpDoesNotTake_IsAlwaysRefused(string op, string field)
    {
        using var full = JsonDocument.Parse(FullSpecJson);
        var value = full.RootElement.GetProperty(field).GetRawText();
        var spec = Bind($$"""{ "op": "{{op}}", "{{field}}": {{value}} }""");

        var ex = Assert.Throws<DndInputException>(() => CampaignOpValidation.Validate([spec]));

        Assert.Contains($"does not take \"{field}\"; {op} takes ", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>One valid-looking value for every field, as a model would send them.</summary>
    internal const string FullSpecJson = """
        {
          "op": "upsert", "ref": "character:iron-guts", "kind": "character", "name": "Iron Guts", "subtype": "npc", "slug": "iron-guts",
          "code": "C13", "summary": "s", "body_md": "b", "secret_md": "x", "status": "alive", "visibility": "party",
          "canon_status": "canon", "confidence": "confirmed", "parent": "location:flotsam", "sort_key": 1.5, "source": "notes.md:3",
          "introduced_session": 2, "aliases": [{"alias": "Guts", "visibility": "party"}], "remove_aliases": ["Old Guts"],
          "tags": ["salvage"], "remove_tags": ["old"], "data": {"rank": "captain"},
          "clock": {"segments": 6, "filled": 1, "unit": "day", "on_fill_md": "boom", "shown_to_players": true, "front": "front:void"},
          "from": "character:belmakor", "rel": "member_of", "to": "faction:the-party", "label": "bandmate", "attitude": 50,
          "symmetric": false, "since": 1, "until": 3, "mode": "all_of", "note": "n", "statement": "Iron Guts owes the band.",
          "fact_type": "canon", "truth": "true", "about": ["character:iron-guts"], "links": [{"ref": "character:iron-guts", "role": "about"}],
          "gate": {"after": ["f:1"]}, "auto_code": "F", "known_by": [{"who": "party", "state": "knows"}], "depends_on": ["f:2"],
          "supersedes": "f:3", "superseded_by": "f:4", "established_session": 3, "objective": 1, "text": "Find the cage",
          "progress": 1, "progress_max": 5, "amount": 1, "answer_md": "a", "answered_by": "f:5"
        }
        """;

    internal static CampaignOpSpec Bind(string json) => DslJson.Deserialize<CampaignOpSpec>(json, "op");
}
