using System.Text.Json;
using System.Text.RegularExpressions;
using DndMcp.Domain.Encounters;
using DndMcp.Domain.Simulation;
using DndMcp.Repository.Srd;
using Xunit;

namespace DndMcp.Tests.Srd.Combatants;

/// <summary>
/// The oracle (PLAN "Tests that carry real weight"): Cole's SRD JSON (serving-solid-characters'
/// <c>SolidCharacters.Repository/data/srd/{2014,2024}/monsters.json</c>, generated from the SRD markdown) derives every
/// attack bonus and save DC from ability scores and proficiency, and those derived values matched the prose 100% in the
/// Phase 0 audit. An independent source that agrees with the text is what the normalizer's numbers are held to.
///
/// <para>
/// <b>Matching rule.</b> Monsters by name, compared as lower-case letters and digits with parentheticals removed
/// ("Deep Gnome (Svirfneblin)" → "deepgnome"); a Cole name with a comma also tries the part after it and the two parts
/// swapped ("Elf, Drow" → "drow"; "Gnome, Deep" → "deepgnome"); a shapechanger (Cole's one "Werewolf") matches all of
/// our form records ("Werewolf, Hybrid Form" …), each action looked up in the forms in turn. Actions by the same name
/// key, in actions, bonus actions, reactions, spells and legendary actions.
/// </para>
/// <para>
/// <b>Derived values.</b> Attack bonus: <c>to_hit_override</c>, else the ability's modifier plus proficiency (by CR)
/// when proficient. Save DC: <c>dc_override</c>, else 8 + proficiency + the <c>dc_ability</c> modifier. Every value
/// we have must equal it: there is no excluded mismatch. What cannot be compared is counted and pinned: Cole's attacks
/// whose action we read as something other than an attack (a 2014 swallow read as its bite), and Cole's saves on actions
/// whose save we do not simulate (lycanthropy, mummy rot, a pull), so a change in what is compared is visible too.
/// </para>
/// </summary>
public sealed partial class SrdOracleTests
{
    [SiblingRepoFact]
    public void AttackBonusesAndSaveDcs_EveryMonsterInBothSources_EqualTheOraclesDerivedValues()
    {
        var mismatches = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        void Count(string what) => counts[what] = counts.GetValueOrDefault(what) + 1;

        foreach (var edition in SrdEdition.All)
        {
            var ours = CorrectedSrd.Shipped.StatBlocks(edition)
                .GroupBy(b => Key(b.Name.EndsWith(" Form", StringComparison.Ordinal) ? b.Name[..b.Name.IndexOf(',')] : b.Name))
                .ToDictionary(g => g.Key, g => g.ToList());
            var path = Path.Combine(SiblingRepoFactAttribute.RepositoryRoot!, "SolidCharacters.Repository", "data", "srd", edition, "monsters.json");
            using var cole = JsonDocument.Parse(File.ReadAllBytes(path));
            foreach (var monster in cole.RootElement.EnumerateArray())
            {
                var name = monster.GetProperty("name").GetString()!;
                var candidates = new List<string> { Key(name) };
                if (name.Split(", ", 2) is [var first, var second])
                {
                    candidates.Add(Key(second));
                    candidates.Add(Key(second + " " + first));
                }

                var forms = candidates.Select(c => ours.GetValueOrDefault(c)).FirstOrDefault(f => f is not null);
                if (forms is null)
                {
                    mismatches.Add($"{edition} {name}: no monster of that name");
                    continue;
                }

                Count($"{edition} monsters");
                var cr = ChallengeRating.Parse(monster.GetProperty("challenge_rating").GetString());
                var proficiency = ChallengeRatingTables.ProficiencyBonus(cr);
                var stats = monster.GetProperty("stats");
                int Mod(string ability) => (stats.GetProperty(ability).GetInt32() - 10) >> 1;
                foreach (var attack in monster.GetProperty("attacks").EnumerateArray())
                {
                    var actionName = attack.GetProperty("name").GetString()!;
                    var action = forms.Select(f => Find(f, actionName)).FirstOrDefault(a => a is not null);
                    if (attack.TryGetProperty("ability", out var ability) && attack.TryGetProperty("attack_type", out _))
                    {
                        var expected = attack.TryGetProperty("to_hit_override", out var over)
                            ? over.GetInt32()
                            : Mod(ability.GetString()!) + (attack.TryGetProperty("proficient", out var p) && p.GetBoolean() ? proficiency : 0);
                        if (action?.Kind != StatBlockValues.ActionKinds.Attack)
                        {
                            Count($"{edition} attacks not compared");
                        }
                        else if (action.AttackBonus == expected)
                        {
                            Count($"{edition} attack bonuses equal");
                        }
                        else
                        {
                            mismatches.Add($"{edition} {name} {actionName}: +{action.AttackBonus}, oracle +{expected}");
                        }
                    }

                    if (attack.TryGetProperty("save", out var save))
                    {
                        int? expected = save.TryGetProperty("dc_override", out var dcOver) ? dcOver.GetInt32()
                            : save.TryGetProperty("dc_ability", out var dcAbility) ? 8 + proficiency + Mod(dcAbility.GetString()!) : null;
                        var dcs = action is null ? [] : action.OnHit.Select(e => e.Save).Append(action.Save).OfType<SaveSpec>().Select(s => s.Dc).ToList();
                        if (expected is null || dcs.Count == 0)
                        {
                            Count($"{edition} saves not compared");
                        }
                        else if (dcs.Contains(expected.Value))
                        {
                            Count($"{edition} save DCs equal");
                        }
                        else
                        {
                            mismatches.Add($"{edition} {name} {actionName}: DC {string.Join("/", dcs)}, oracle DC {expected}");
                        }
                    }
                }
            }
        }

        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
        var actual = string.Join(", ", counts.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}={k.Value}"));
        Assert.True(
            actual == string.Join(", ", ExpectedCounts.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}={k.Value}")),
            "Compared counts: " + actual);
    }

    private static readonly Dictionary<string, int> ExpectedCounts = new()
    {
        ["2014 monsters"] = 316, ["2014 attack bonuses equal"] = 500, ["2014 save DCs equal"] = 78,
        ["2014 attacks not compared"] = 1, ["2014 saves not compared"] = 31,
        ["2024 monsters"] = 329, ["2024 attack bonuses equal"] = 422, ["2024 save DCs equal"] = 137,
        ["2024 saves not compared"] = 12,
    };

    private static StatBlockAction? Find(StatBlock block, string name) =>
        block.Actions.Concat(block.BonusActions).Concat(block.Reactions).Concat(block.Spells).Concat(block.Legendary?.Actions ?? [])
            .FirstOrDefault(a => Key(a.Name) == Key(name));

    private static string Key(string name) =>
        NonAlphanumeric().Replace(Parenthetical().Replace(name.ToLowerInvariant().Replace('’', '\''), string.Empty), string.Empty);

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex Parenthetical();

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NonAlphanumeric();
}
