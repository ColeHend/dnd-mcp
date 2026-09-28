using DndMcp.Domain.Dice;
using DndMcp.Domain.Dpr;
using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation;

namespace DndMcp.Tests.Simulation;

/// <summary>
/// Shared plumbing for the simulator's tests: DSL JSON → validation → resolution (for the simulator, and for the closed
/// form where a test compares the two), and small spec builders. Kept apart from the DPR tests' kit so a change there
/// cannot silently move these.
/// </summary>
internal static class SimKit
{
    public static BuildSpec Build(string json) => DslJson.Deserialize<BuildSpec>(json, "build");

    public static TargetSpec? Target(string? json) => json is null ? null : DslJson.Deserialize<TargetSpec>(json, "target");

    public static RulingsSpec? Rulings(string? json) => json is null ? null : DslJson.Deserialize<RulingsSpec>(json, "rulings");

    public static FeatureSpec Feature(string json) => DslJson.Deserialize<FeatureSpec>(json, "feature");

    /// <summary>The build at its own level for the closed form (DPR validation) and the target at that level.</summary>
    public static (ResolvedBuild Build, ResolvedTarget Target) Resolve(string buildJson, string? targetJson = null, string? rulingsJson = null)
    {
        var spec = Build(buildJson);
        var build = BuildResolver.Resolve(spec, spec.Level ?? 1, Rulings(rulingsJson));
        return (build, TargetResolver.Resolve(Target(targetJson), build.Level));
    }

    /// <summary>The closed form's fight horizon (R rounds; its round 1 is <see cref="DprResult.Round1Damage"/>).</summary>
    public static DprResult Exact(ResolvedBuild build, ResolvedTarget target, int rounds = 3) =>
        DprEngine.Evaluate(build, target, DprOptions.Fight(rounds), WorkMeter.Unlimited);

    /// <summary>The closed form's round 1 with its exact distribution.</summary>
    public static DprResult ExactRound1(ResolvedBuild build, ResolvedTarget target) =>
        DprEngine.Evaluate(build, target, DprOptions.Round1, WorkMeter.Unlimited);

    /// <summary>A party member from build JSON.</summary>
    public static SimulationCombatant Pc(string buildJson, int hp, int ac, string? name = null, string[]? saves = null, int? count = null, string? position = null) =>
        new(new CombatantSpec { Name = name, Build = Build(buildJson), Hp = hp, Ac = ac, SaveProficiencies = saves, Count = count, Position = position });

    public static SimulationCombatant Monster(StatBlock block, int? count = null, string? name = null, int? hp = null, bool? deathSaves = null) =>
        new(new CombatantSpec { Name = name, Monster = block.Name, Count = count, Hp = hp, DeathSaves = deathSaves }, block);

    public static SimulationSpec Spec(IReadOnlyList<SimulationCombatant> party, IReadOnlyList<SimulationCombatant> enemies, int iterations = 2_000,
                                      int roundCap = 20, string? edition = null, PolicySpec? policies = null, string? surprise = null, int? replay = null,
                                      CompareSpec? compare = null, string? enemyHp = null, double? precision = null) => new()
    {
        Party = party,
        Enemies = enemies,
        Iterations = iterations,
        RoundCap = roundCap,
        Edition = edition,
        Policies = policies,
        Surprise = surprise,
        Replay = replay,
        Compare = compare,
        EnemyHp = enemyHp,
        Precision = precision,
    };

    /// <summary>A simple 2024 level 5 fighter: Str 18, greatsword 2d6 ×2 (+7, +4 damage).</summary>
    public const string Fighter2024 =
        """
        { "name": "Fighter", "edition": "2024", "level": 5, "abilities": {"str": 18, "dex": 12, "con": 16},
          "attacks": [{ "name": "Greatsword", "count": 2, "damage": "2d6", "damage_type": "slashing", "properties": ["melee", "heavy", "two-handed"] }] }
        """;

    /// <summary>A commoner-grade build: +2 club 1d4.</summary>
    public const string Commoner =
        """
        { "name": "Commoner", "edition": "2014", "level": 1, "abilities": {"str": 10},
          "attacks": [{ "name": "Club", "to_hit": {"total": 2}, "damage": "1d4", "damage_type": "bludgeoning", "properties": ["melee"] }] }
        """;
}
