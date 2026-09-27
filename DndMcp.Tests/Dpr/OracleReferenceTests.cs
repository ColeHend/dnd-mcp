using System.Collections.Concurrent;
using DndMcp.Domain.Dpr;
using Xunit;

namespace DndMcp.Tests.Dpr;

/// <summary>
/// Invariant: the oracle file's derived goldens hold for the engine too — each delta between two engine cases (GWM over
/// no feat and over the ASI, Savage Attacker's gain per AC and ruling, the GWF and Graze components, the advantage
/// calibration), the Great Weapon Fighting and Savage Attacker tables, and the power-attack toggle rule per AC.
/// </summary>
public sealed class OracleReferenceTests
{
    private static readonly ConcurrentDictionary<string, double> Dpr = new();

    private static double CaseDpr(string id) => Dpr.GetOrAdd(id, key => OracleCases.Evaluate(OracleCases.Case(key)).DamagePerRound);

    public static TheoryData<string> EngineDeltas()
    {
        var engine = OracleCases.All.Where(c => OracleCases.HorizonKind(c) != DprHorizons.Day).Select(OracleCases.Id).ToHashSet();
        var data = new TheoryData<string>();
        foreach (var delta in OracleCases.Root.GetProperty("deltas").EnumerateArray())
        {
            if (engine.Contains(delta.GetProperty("plus").GetString()!) && engine.Contains(delta.GetProperty("minus").GetString()!))
            {
                data.Add(delta.GetProperty("id").GetString()!);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EngineDeltas))]
    public void Evaluate_OracleDelta_MatchesTheOracle(string id)
    {
        var delta = OracleCases.Root.GetProperty("deltas").EnumerateArray().Single(d => d.GetProperty("id").GetString() == id);
        var plus = CaseDpr(delta.GetProperty("plus").GetString()!);
        var minus = CaseDpr(delta.GetProperty("minus").GetString()!);

        var actual = delta.GetProperty("kind").GetString() == "relative" ? (plus / minus) - 1 : plus - minus;

        Assert.Equal(OracleCases.Exact(delta.GetProperty("exact").GetString()!), actual, 1e-9);
    }

    [Fact]
    public void ExpectedValue_OracleGreatWeaponFightingTable_Matches()
    {
        foreach (var row in OracleCases.Root.GetProperty("reference").GetProperty("gwf_expected_per_die").EnumerateArray())
        {
            var sides = int.Parse(row.GetProperty("die").GetString()![1..], System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(OracleCases.Exact(row.GetProperty("plain").GetString()!), DamageDice.ExpectedValue(sides), 1e-12);
            Assert.Equal(OracleCases.Exact(row.GetProperty("gwf2014").GetString()!), DamageDice.ExpectedValue(sides, "gwf2014"), 1e-12);
            Assert.Equal(OracleCases.Exact(row.GetProperty("gwf2024").GetString()!), DamageDice.ExpectedValue(sides, "gwf2024"), 1e-12);
        }
    }

    [Fact]
    public void ExpectedBestOfTwo_OracleSavageAttackerTable_Matches()
    {
        foreach (var row in OracleCases.Root.GetProperty("reference").GetProperty("savage_attacker_table").EnumerateArray())
        {
            var text = row.GetProperty("dice").GetString()!.Split(' ');
            var dice = text[0].Split('d');
            var (count, sides) = (int.Parse(dice[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(dice[1], System.Globalization.CultureInfo.InvariantCulture));
            var remap = text.Length > 1 ? text[1] : null;

            Assert.Equal(OracleCases.Number(row.GetProperty("hit_with_sa")), DamageDice.ExpectedBestOfTwo(count, sides, remap), 1e-12);
            Assert.Equal(OracleCases.Number(row.GetProperty("crit_with_sa_crit_dice")), DamageDice.ExpectedBestOfTwo(2 * count, sides, remap), 1e-12);

            // Ruling off: the better of two rolls of one set, plus the crit's extra set rolled once.
            var oneSet = DamageDice.ExpectedBestOfTwo(count, sides, remap) + (count * DamageDice.ExpectedValue(sides, remap));
            Assert.Equal(OracleCases.Number(row.GetProperty("crit_with_sa_one_set")), oneSet, 1e-12);
        }
    }

    [Fact]
    public void Evaluate_Fighter2014PowerAttackToggles_MatchTheOracleRule()
    {
        foreach (var row in OracleCases.Root.GetProperty("reference").GetProperty("power_attack_toggle_rule_2014_l5").EnumerateArray())
        {
            var ac = row.GetProperty("ac").GetInt32();
            var result = OracleCases.Evaluate(OracleCases.Case($"gwm14-pa-auto-crit-ba-ac{ac}"));
            var toggle = Assert.Single(Assert.Single(result.PowerAttacks).Toggles);

            Assert.Equal(OracleCases.Exact(row.GetProperty("p").GetString()!), toggle.HitChance, 1e-12);
            Assert.Equal(OracleCases.Exact(row.GetProperty("p_power").GetString()!), toggle.HitChanceWithPenalty, 1e-12);
            Assert.Equal(row.GetProperty("threshold").GetDouble(), toggle.Threshold, 1e-12);
            Assert.Equal(row.GetProperty("rule_on").GetBoolean(), toggle.RuleSaysOn);
        }
    }
}
