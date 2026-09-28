using DndMcp.Domain.Simulation;
using DndMcp.Tests.Srd.Combatants;
using Xunit;

namespace DndMcp.Tests.Simulation;

/// <summary>
/// Every normalized SRD monster of both editions (334 + 341, from the shipped corrected data through the shipped
/// normalizer) fights: as a group of enemies against a party, and as the party's ally. Nothing may throw, every fight
/// must end in one of the three outcomes, and a replay of each must render. This is what makes "a stat block the
/// normalizer writes is a valid combatant" true for the whole data set, not only for hand-built blocks.
/// </summary>
public sealed class SrdMonsterFightTests
{
    public static IEnumerable<object[]> Editions => [["2014"], ["2024"]];

    [Theory]
    [MemberData(nameof(Editions))]
    public void EveryMonster_FightsWithoutError(string edition)
    {
        var blocks = CorrectedSrd.Shipped.StatBlocks(edition);
        Assert.True(blocks.Count > 300);
        var party = new List<SimulationCombatant>
        {
            SimKit.Pc(SimKit.Fighter2024, hp: 90, ac: 18, name: "Fighter", count: 3),
            SimKit.Pc("""
                { "name": "Cleric", "edition": "2024", "level": 9, "abilities": {"wis": 18},
                  "attacks": [{ "name": "Mace", "damage": "1d6", "damage_type": "bludgeoning", "properties": ["melee"] }],
                  "modifiers": [{ "kind": "heal", "name": "Healing Word", "dice": "2d4", "amount": "wis", "action_cost": "bonus_action", "resource": {"uses": 3, "per": "long_rest"} },
                                { "kind": "save_effect", "name": "Hold Monster", "ability": "wis", "dc": 16, "condition": "paralyzed", "concentration": true, "resource": {"uses": 1, "per": "long_rest"} }] }
                """, hp: 60, ac: 18, name: "Cleric"),
        };

        var failures = new List<string>();
        foreach (var block in blocks)
        {
            try
            {
                var enemies = Simulator.Run(SimKit.Spec(party, [SimKit.Monster(block, count: 2)], iterations: 24, roundCap: 8, replay: 3, enemyHp: "roll",
                    policies: new PolicySpec { Enemies = "healer_first", LegendaryResistance = "always" }), 1);
                Assert.Equal(24, enemies.PartyWins.Count + enemies.PartyDefeated.Count + enemies.Draw.Count);
                Assert.Contains("Summary of fight 3:", enemies.ReplayLog);

                var ally = Simulator.Run(SimKit.Spec([SimKit.Monster(block), SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "Fighter")],
                    [SimKit.Monster(TestStatBlocks.Ogre, count: 2)], iterations: 8, roundCap: 6), 2);
                Assert.Equal(8, ally.Iterations);
            }
            catch (Exception ex)
            {
                failures.Add($"{block.Ref}: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }
}
