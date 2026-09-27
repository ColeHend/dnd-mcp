using DndMcp.Domain.Dice;
using DndMcp.Domain.Dpr;
using DndMcp.Domain.Features;

namespace DndMcp.Tests.Dpr;

/// <summary>
/// Runs builds through the whole pipeline the tools use — DSL JSON → <see cref="DslJson"/> → validation →
/// <see cref="BuildResolver"/> and <see cref="TargetResolver"/> → <see cref="DprEngine"/> — so a golden that passes here
/// passes for the model's JSON too, not only for a hand-built resolved record.
/// </summary>
internal static class DprTestKit
{
    public static BuildSpec Build(string json) => DslJson.Deserialize<BuildSpec>(json, "build");

    public static TargetSpec? Target(string? json) => json is null ? null : DslJson.Deserialize<TargetSpec>(json, "target");

    public static RulingsSpec? Rulings(string? json) => json is null ? null : DslJson.Deserialize<RulingsSpec>(json, "rulings");

    /// <summary>The build (at its own level unless <paramref name="level"/>) and the target at that level.</summary>
    public static (ResolvedBuild Build, ResolvedTarget Target) Resolve(string buildJson, string? targetJson = null, string? rulingsJson = null, int? level = null)
    {
        var spec = Build(buildJson);
        var build = BuildResolver.Resolve(spec, level ?? spec.Level ?? 1, Rulings(rulingsJson));
        return (build, TargetResolver.Resolve(Target(targetJson), build.Level));
    }

    public static DprResult Evaluate(string buildJson, string? targetJson = null, DprOptions? options = null, string? rulingsJson = null, int? level = null)
    {
        var (build, target) = Resolve(buildJson, targetJson, rulingsJson, level);
        return DprEngine.Evaluate(build, target, options ?? DprOptions.Round1, WorkMeter.Unlimited);
    }

    /// <summary>Round 1's expected damage (setup costs paid, the round's reaction included).</summary>
    public static double Round1(string buildJson, string? targetJson = null, string? rulingsJson = null) =>
        Evaluate(buildJson, targetJson, DprOptions.Round1, rulingsJson).DamagePerRound;

    public static RiderReport Rider(DprResult result, string name) => result.Riders.Single(r => r.Name == name);

    /// <summary>A one-attack build at level 5 (Str 18, +7 to hit) with modifiers spliced in.</summary>
    public static string Level5(string attacksJson, string modifiersJson = "[]", string extra = "") =>
        $$"""{ "name": "Test", "edition": "2024", "level": 5, "abilities": {"str": 18, "dex": 18}, {{extra}} "attacks": {{attacksJson}}, "modifiers": {{modifiersJson}} }""";

    public const string Longsword = """{ "name": "Longsword", "damage": "1d8", "damage_type": "slashing", "properties": ["melee", "versatile"] }""";

    public const string Longsword2 = """{ "name": "Longsword", "count": 2, "damage": "1d8", "damage_type": "slashing", "properties": ["melee", "versatile"] }""";
}
