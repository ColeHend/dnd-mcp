using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DndMcp.Domain.Simulation;
using DndMcp.Domain.Simulation.Archetypes;
using DndMcp.Tests.Srd.Combatants;
using Xunit;

namespace DndMcp.Tests.Simulation;

/// <summary>
/// Invariant: a fight that is not seeded from a live state (no <see cref="CombatantStart"/>, no <see cref="FightResume"/>,
/// not in a lair) runs exactly as it did before Phase 7 added seeding (contract §11.5 "an unseeded spec is byte-identical
/// to today"). Each scenario's serialized reports — every probability, mean, histogram, resource count and the replayed
/// fight's full combat log, which records every die rolled — are hashed, and the hashes were taken on the stage-1 base
/// BEFORE any seeding code existed. Seeding touches the hot path (Begin, the first round, the tallies' start HP), so a
/// shifted random draw or a changed tally anywhere shows up here.
///
/// <para>
/// <b>When a deliberate engine change moves these</b>, run the tests, check that the change explains every moved hash, and
/// paste the new values from the failure messages; never update them to make an unexplained difference pass.
/// </para>
/// </summary>
public sealed class SimulationGoldenTests
{
    /// <summary>The hashes taken on the stage-1 base (before seeding), per scenario.</summary>
    private static readonly Dictionary<string, string> Golden = new(StringComparer.Ordinal)
    {
        ["skirmish"] = "B05A9EA33139665BB4A0BC9785824398D6D366EDCD8067E55BB3E57B981F7F03",
        ["dragon"] = "82D026DD2BD4C78903B2BA3E058BCB657F117C256C4C2CDA84201F99C6CBB222",
        ["compare"] = "AD23DB29351CE01AEDDA48BB053C800F79244F5EE7E838A5A34F091C2FBA423F",
        ["surprise-precision"] = "9A0A884400D11AEEF490BF24B5F0E2493D3ECD5697EEB16CCAF7F7A90E5D48EE",
        ["archetypes"] = "7D52307A4EF40C2823CCE70314D0C542BEFB099D2867B85336B0270FCEE41641",
        ["srd-2014"] = "88F0CFD39AA344F2927374AF13E938FF95E6326905B9B3EE2AB185376B77C401",
        ["srd-2024"] = "70F26065E8154F7ABAE15516F3F4BBF7BAB1AF1A912125557BDCF3A7AA9EA32F",
    };

    public static IEnumerable<object[]> Scenarios() => Golden.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Run_UnseededSpec_ByteIdenticalToThePhase6Engine(string scenario)
    {
        var hash = Hash(Reports(scenario));
        Assert.True(Golden[scenario] == hash, $"{scenario}: the serialized reports hash to {hash}, not {Golden[scenario]}.");
    }

    private static string Hash(IEnumerable<SimulationReport> reports)
    {
        var text = new StringBuilder();
        foreach (var report in reports)
        {
            text.Append(JsonSerializer.Serialize(report)).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static readonly SimulationCombatant TempHpBuild = SimKit.Pc("""
        { "name": "Warden", "edition": "2014", "level": 6, "abilities": {"str": 16, "con": 16},
          "attacks": [{ "name": "Longsword", "count": 2, "damage": "1d8", "damage_type": "slashing", "properties": ["melee"] }],
          "modifiers": [{ "kind": "temp_hp", "name": "Armor of Agathys", "amount": 10 }] }
        """, hp: 52, ac: 17, name: "Warden");

    private static readonly SimulationCombatant Hexer = SimKit.Pc("""{ "name": "Warlock", "preset": "warlock_baseline", "level": 5 }""", hp: 38, ac: 13, name: "Warlock");

    private static IEnumerable<SimulationReport> Reports(string scenario) => scenario switch
    {
        "skirmish" =>
        [
            Simulator.Run(SimKit.Spec(
                [SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "Fighter", count: 2)],
                [SimKit.Monster(TestStatBlocks.Goblin, count: 3), SimKit.Monster(TestStatBlocks.Wolf, count: 2), SimKit.Monster(TestStatBlocks.Troll)],
                iterations: 3_000, enemyHp: "roll", replay: 3), 42),
        ],
        "dragon" =>
        [
            Simulator.Run(SimKit.Spec(
                [
                    new SimulationCombatant(new CombatantSpec { Archetype = "fighter", Level = 15, Edition = "2014" }),
                    new SimulationCombatant(new CombatantSpec { Archetype = "cleric", Level = 15, Edition = "2014" }),
                    new SimulationCombatant(new CombatantSpec { Archetype = "wizard", Level = 15, Edition = "2014" }),
                    new SimulationCombatant(new CombatantSpec { Archetype = "barbarian", Level = 15, Edition = "2014" }),
                    TempHpBuild,
                ],
                [SimKit.Monster(TestStatBlocks.AdultRedDragon), SimKit.Monster(TestStatBlocks.Zombie, count: 2)],
                iterations: 1_500, replay: 1, edition: "2014",
                policies: new PolicySpec { Enemies = "break_concentration", LegendaryResistance = "always" }), 7),
        ],
        "compare" =>
        [
            Simulator.Run(SimKit.Spec(
                [SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "Fighter"), Hexer],
                [SimKit.Monster(TestStatBlocks.Ogre, count: 2)],
                iterations: 2_000, replay: 5,
                compare: new CompareSpec
                {
                    Member = 1,
                    Feature = SimKit.Feature("""{ "name": "Third attack", "attacks": [{ "name": "Greatsword", "count": 3, "damage": "2d6", "damage_type": "slashing", "properties": ["melee", "heavy", "two-handed"] }] }"""),
                }), 8),
        ],
        "surprise-precision" =>
        [
            Simulator.Run(SimKit.Spec(
                [SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "Fighter", count: 2), Hexer],
                [SimKit.Monster(TestStatBlocks.Goblin, count: 4), SimKit.Monster(TestStatBlocks.Ogre)],
                edition: "2014", surprise: "enemies", precision: 0.02, replay: 2), 11),
            Simulator.Run(SimKit.Spec(
                [SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "Fighter", count: 2)],
                [SimKit.Monster(TestStatBlocks.Goblin, count: 4)],
                edition: "2024", surprise: "party", iterations: 1_000, policies: new PolicySpec { PcsWinTies = true, FinishDowned = true }), 12),
        ],
        "archetypes" =>
            from name in ArchetypeCatalog.Names
            from edition in new[] { "2014", "2024" }
            from level in new[] { 1, 5, 11, 17 }
            select Simulator.Run(SimKit.Spec(
                [new SimulationCombatant(new CombatantSpec { Archetype = name, Level = level, Edition = edition, Count = 2 })],
                [SimKit.Monster(level < 5 ? TestStatBlocks.Goblin : TestStatBlocks.Troll, count: level < 5 ? 3 : 1 + (level / 6))],
                iterations: 40, roundCap: 12, replay: 1, enemyHp: "roll"), (ulong)(level * 31)),
        "srd-2014" => Srd("2014"),
        "srd-2024" => Srd("2024"),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
    };

    /// <summary>Every shipped SRD monster of an edition, a pair against a mixed party (temp HP, Hex, healing, a save effect held by concentration).</summary>
    private static IEnumerable<SimulationReport> Srd(string edition)
    {
        var party = new List<SimulationCombatant>
        {
            SimKit.Pc(SimKit.Fighter2024, hp: 90, ac: 18, name: "Fighter", count: 2),
            TempHpBuild,
            Hexer,
            SimKit.Pc("""
                { "name": "Cleric", "edition": "2024", "level": 9, "abilities": {"wis": 18},
                  "attacks": [{ "name": "Mace", "damage": "1d6", "damage_type": "bludgeoning", "properties": ["melee"] }],
                  "modifiers": [{ "kind": "heal", "name": "Healing Word", "dice": "2d4", "amount": "wis", "action_cost": "bonus_action", "resource": {"uses": 3, "per": "long_rest"} },
                                { "kind": "save_effect", "name": "Hold Monster", "ability": "wis", "dc": 16, "condition": "paralyzed", "concentration": true, "resource": {"uses": 1, "per": "long_rest"} }] }
                """, hp: 60, ac: 18, name: "Cleric"),
        };

        var seed = 1UL;
        foreach (var block in CorrectedSrd.Shipped.StatBlocks(edition))
        {
            yield return Simulator.Run(SimKit.Spec(party, [SimKit.Monster(block, count: 2)], iterations: 16, roundCap: 8, replay: 2, enemyHp: "roll",
                policies: new PolicySpec { Enemies = "healer_first", LegendaryResistance = "conditions" }), seed++);
        }
    }
}
