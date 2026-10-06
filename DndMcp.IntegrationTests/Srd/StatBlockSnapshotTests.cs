using System.Collections;
using System.Reflection;
using DndMcp.Domain.Encounters;
using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation;
using DndMcp.Hosting;
using DndMcp.IntegrationTests.Infrastructure;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DndMcp.IntegrationTests.Srd;

/// <summary>
/// Invariant: every SRD monster of both editions, resolved through the server's <see cref="StatBlockService"/> on the
/// shared srd index (as <c>combat add {srd}</c> will resolve it), survives the tracker's snapshot
/// (<see cref="StatBlockSnapshotJson"/>) field by field — lists by sequence, formulas by their terms, Challenge Ratings by
/// value — and re-serialises to the same text; the largest snapshot is pinned (contract §9: at most 40,000 characters).
/// </summary>
/// <remarks>
/// A field the snapshot drops or a type that cannot read back (a formula without its converter, a CR read as 0) would
/// otherwise surface only when a stored fight is resumed, awarding the wrong XP or simulating a different monster.
/// </remarks>
public sealed class StatBlockSnapshotTests : IClassFixture<McpServerHarness>
{
    /// <summary>The contract's ceiling for one snapshot.</summary>
    private const int MaxSnapshotChars = 40_000;

    /// <summary>The largest snapshot today (measured): a visible diff when the normalizer or the data changes it.</summary>
    private const string LargestRef = "2014/monster/mummy-lord";

    private const int LargestChars = 13_408;

    private readonly StatBlockService _service;

    public StatBlockSnapshotTests(McpServerHarness server)
    {
        _service = server.Services.GetRequiredService<StatBlockService>();
    }

    public static TheoryData<string> Editions => new(SrdEdition.All);

    [Theory]
    [MemberData(nameof(Editions))]
    public async Task Snapshot_EverySrdMonster_RoundTripsFieldByField(string edition)
    {
        var monsters = VendoredSrdLookup.Instance.OfKind(edition, SrdKinds.Monster);
        Assert.True(monsters.Count > 300);
        var failures = new List<string>();
        foreach (var doc in monsters)
        {
            var (block, _) = await _service.ResolveAsync(doc.Ref.ToString(), edition, null, CancellationToken.None);
            Assert.Equal(doc.Ref.ToString(), block.Ref);
            var json = StatBlockSnapshotJson.Serialize(block);
            var back = StatBlockSnapshotJson.Deserialize(json);
            var difference = FirstDifference(block, back, "$");
            if (difference is not null)
            {
                failures.Add($"{doc.Ref}: {difference}");
            }
            else if (StatBlockSnapshotJson.Serialize(back) != json)
            {
                failures.Add($"{doc.Ref}: re-serialises differently");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(10)));
    }

    [Fact]
    public async Task Snapshot_TheLargestSrdMonster_IsPinnedUnderTheCeiling()
    {
        var sizes = new List<(string Ref, int Chars)>();
        foreach (var edition in SrdEdition.All)
        {
            foreach (var doc in VendoredSrdLookup.Instance.OfKind(edition, SrdKinds.Monster))
            {
                var (block, _) = await _service.ResolveAsync(doc.Ref.ToString(), edition, null, CancellationToken.None);
                sizes.Add((doc.Ref.ToString(), StatBlockSnapshotJson.Serialize(block).Length));
            }
        }

        var largest = sizes.MaxBy(s => s.Chars);
        Assert.True(largest.Chars <= MaxSnapshotChars, $"{largest.Ref}: {largest.Chars} characters.");
        Assert.Equal((LargestRef, LargestChars), largest);
    }

    [Fact]
    public async Task Snapshot_AMummyLord_KeepsItsXpCrAndFormulas()
    {
        var (block, _) = await _service.ResolveAsync("2014/monster/mummy-lord", "2014", null, CancellationToken.None);
        var back = StatBlockSnapshotJson.Deserialize(StatBlockSnapshotJson.Serialize(block));

        Assert.Equal(ChallengeRating.Parse("15"), back.ChallengeRating);
        Assert.Equal(13_000, back.Xp);
        Assert.Equal("13d8+39", back.HitDice.Text);
        Assert.Contains(back.Vulnerabilities, v => v.DamageType == "fire");
        Assert.Contains(back.Immunities, i => i.Qualifier == StatBlockValues.DamageQualifiers.Nonmagical);
    }

    /// <summary>The path of the first member that differs, or null: records member by member, lists by sequence, dictionaries by key.</summary>
    private static string? FirstDifference(object? expected, object? actual, string path)
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null ? null : $"{path}: {Show(expected)} vs {Show(actual)}";
        }

        switch (expected)
        {
            case string or bool or int or long or double or Enum or DamageFormula or ChallengeRating or DiceTerm:
                return expected.Equals(actual) && (expected is not DamageFormula formula || formula.Dice.SequenceEqual(((DamageFormula)actual).Dice))
                    ? null
                    : $"{path}: {Show(expected)} vs {Show(actual)}";
            case IDictionary dictionary:
            {
                var other = (IDictionary)actual;
                if (dictionary.Count != other.Count)
                {
                    return $"{path}: {dictionary.Count} entries vs {other.Count}";
                }

                foreach (DictionaryEntry entry in dictionary)
                {
                    if (!other.Contains(entry.Key))
                    {
                        return $"{path}[{entry.Key}]: missing";
                    }

                    if (FirstDifference(entry.Value, other[entry.Key], $"{path}[{entry.Key}]") is { } inner)
                    {
                        return inner;
                    }
                }

                return null;
            }

            case IEnumerable sequence:
            {
                var left = sequence.Cast<object?>().ToList();
                var right = ((IEnumerable)actual).Cast<object?>().ToList();
                if (left.Count != right.Count)
                {
                    return $"{path}: {left.Count} items vs {right.Count}";
                }

                for (var i = 0; i < left.Count; i++)
                {
                    if (FirstDifference(left[i], right[i], $"{path}[{i}]") is { } inner)
                    {
                        return inner;
                    }
                }

                return null;
            }
        }

        if (expected.GetType() != actual.GetType())
        {
            return $"{path}: {expected.GetType().Name} vs {actual.GetType().Name}";
        }

        foreach (var property in expected.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            if (FirstDifference(property.GetValue(expected), property.GetValue(actual), $"{path}.{property.Name}") is { } inner)
            {
                return inner;
            }
        }

        return null;
    }

    private static string Show(object? value) => value switch
    {
        null => "null",
        string s => $"\"{s}\"",
        _ => value.ToString() ?? value.GetType().Name,
    };
}
