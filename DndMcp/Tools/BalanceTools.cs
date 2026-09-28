using System.ComponentModel;
using System.Text.Json;
using DndMcp.Domain.Core;
using DndMcp.Domain.Dpr;
using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation;
using DndMcp.Formatting;
using DndMcp.Hosting;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DndMcp.Tools;

/// <summary>
/// <c>balance_dpr</c> and <c>balance_compare</c>: exact damage per round for a build in the feature DSL, and the delta a
/// feature adds to a baseline with its level-equivalent and balance band. Thin by design, like <see cref="EncounterTools"/>:
/// bind the DSL spec classes, hand the arguments to the Domain (<see cref="DprAnalysis"/>, <see cref="DprComparison"/>),
/// render with <see cref="BalanceDprMarkdown"/> / <see cref="BalanceCompareMarkdown"/>.
///
/// <para>
/// <b>Every check lives in the Domain</b>, where <c>DndMcp.Tests</c> pins its message: the build, baseline, feature,
/// variant and target validators, the horizon and rest settings, <c>ac_range</c> and the levels. The host adds only the
/// argument guard's schema checks (unknown fields anywhere inside a build, JSON types, integers beyond their CLR type),
/// which run before this code. A second copy of a rule here would drift from the Domain's, and the model would be told
/// two different things about one mistake.
/// </para>
/// <para>
/// <b>The spec classes ARE the input schema.</b> The SDK builds each parameter's schema from <see cref="BuildSpec"/>,
/// <see cref="TargetSpec"/>, <see cref="FeatureSpec"/> and <see cref="RulingsSpec"/> and their descriptions; step values
/// (<c>object?</c>) are untyped there, so a number or a <c>{"1": 1, "5": 2}</c> map both bind. The one exception is
/// <c>variant</c>: a second full copy of the build schema made <c>balance_compare</c>'s definition 38 KB, so it is published
/// untyped and validated against <c>baseline</c>'s schema by the guard (<see cref="SameShapeAsAttribute"/>).
/// </para>
/// <para>
/// <b>target.monster is the one thing only the host can do</b>: find the SRD stat block a name or ref means
/// (<see cref="StatBlockService"/>, with <c>encounter_difficulty</c>'s resolution: refs, names, a shapechanger's forms, the
/// other edition's counterpart, close names when nothing matches) and pass it to the Domain as
/// <see cref="DprRequest.TargetMonster"/>. A bare name is looked up in the build's edition (the baseline's for
/// balance_compare), so a 2014 build fights the 2014 ogre; a ref is used as given, its own edition winning, with a note.
/// The target's fields are validated first, so "monster and cr" or a blank monster is the Domain's message, not an index
/// wait followed by a lookup error. The lookup's notes (a form chosen, another edition's stat block used) are rendered at
/// the head of the result's notes: they are about which creature the numbers are for, which the Domain cannot know.
/// </para>
/// <para>
/// Async only for that lookup (it may wait for the rules index, reporting progress like every rules tool). The analysis
/// itself is CPU-bound and bounded by the Domain's one work budget per call (a few seconds at most); it runs on the thread
/// pool so a long grid never holds the SDK's message loop, and the token is observed throughout the engine.
/// </para>
/// </summary>
public sealed class BalanceTools
{
    // The example build of balance_dpr's description and of the null-build message: the contract's level 5 fighter.
    private const string FighterExample =
        "{\"name\": \"Fighter 5\", \"level\": 5, \"abilities\": {\"str\": 18}, \"attacks\": [{\"name\": \"Greatsword\", " +
        "\"count\": 2, \"damage\": \"2d6\", \"damage_type\": \"slashing\", \"properties\": [\"melee\", \"heavy\", \"two-handed\"]}]}";

    private const string FeatureExample = "{\"name\": \"Savage Attacker\", \"modifiers\": [{\"kind\": \"reroll_damage_take_best\"}]}";

    private readonly StatBlockService _statBlocks;

    public BalanceTools(StatBlockService statBlocks)
    {
        _statBlocks = statBlocks;
    }

    // balance_compare's example baseline scales (ASIs at 4 and 6, Extra Attack at 5 and 11), so its own level-equivalents
    // divide by the baseline's slope in every tier rather than falling back to RPGBOT's.
    private const string CompareBaselineExample =
        "{\"name\": \"Fighter\", \"level\": 5, \"abilities\": {\"str\": {\"1\": 16, \"4\": 18, \"6\": 20}}, \"attacks\": " +
        "[{\"name\": \"Greatsword\", \"count\": {\"1\": 1, \"5\": 2, \"11\": 3}, \"damage\": \"2d6\", \"damage_type\": " +
        "\"slashing\", \"properties\": [\"melee\", \"heavy\", \"two-handed\"]}]}";

    // Idempotent and closed-world: an exact computation, a pure function of the arguments.
    [McpServerTool(Name = "balance_dpr", Title = "Damage per round", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Damage per round (DPR) of a D&D 5e build, 2014 or 2024 rules, computed exactly over every die outcome: " +
        "round 1, a fight and an adventuring day; per attack and rider the hit and crit chances, uses and damage per use; the " +
        "round-1 damage spread; power-attack choices; save effects with kill chances; and every assumption used. For what a " +
        "feature adds to a build, use balance_compare.\n" +
        "- build (required): name, level 1-20, edition (\"2024\" default or \"2014\"), abilities {str..cha}, fighting_style, attacks " +
        "[{name, count, damage \"2d6\", damage_type, to_hit, properties, mastery, cantrip}] and modifiers [{kind, ...}]: to_hit, " +
        "extra_damage (smites, Hex, Sneak Attack), bonus_damage, crit_range, advantage, lucky, elven_accuracy, damage_die_remap, " +
        "reroll_damage_take_best (Savage Attacker), extra_attack (Action Surge, bonus or reaction attacks), power_attack (2014 " +
        "-5/+10), save_effect (Fireball; resource for its slots), condition_on_hit, ignore_cover, ac, resistance, temp_hp, heal " +
        "(simulator). A number that changes with level takes a step map {\"1\": 1, \"5\": 2}. Or {name, level, preset: " +
        "\"warlock_baseline\"}.\n" +
        "- target: {monster (SRD ref or name), ac, cr, profile, save_bonus, saves, hp, resistances, condition, cover, ...}; " +
        "default: the CR = level row of profile \"dmg2014\" (DMG table) or \"mm2024\"/\"mm2014\" (SRD medians).\n" +
        "- levels: e.g. [1, 5, 11, 17] for a curve (default: the build's level); ac_range: [low, high] for a level x AC table.\n" +
        "- horizon: \"fight\" (default: the mean per round of a fight; rounds: its length, default 3), \"round1\" (the nova) or \"day\" " +
        "(limited uses spread over an adventuring day: rest_preset \"dmg2014\" (default) or \"light\", or encounters_per_day and " +
        "short_rests).\n" +
        "- rulings: {hew_gets_pb, cleave_part_of_attack_action, gwf_on_riders, savage_attacker_on_crit_dice}, default false.\n" +
        "Example: {\"build\": " + FighterExample + ", \"target\": {\"ac\": 15}}")]
    public async Task<string> Dpr(
        [Description("The build: name, level, attacks and modifiers (see the tool description), or a preset.")] BuildSpec build,
        [Description("The creature attacked, e.g. {\"ac\": 15} or {\"monster\": \"ogre\"}. Default: the DMG 2014 row for CR = each level evaluated.")] TargetSpec? target = null,
        [Description("Levels to evaluate, 1-20, e.g. [1, 5, 11, 17]. Default: the build's own level.")] int[]? levels = null,
        [Description("A level x AC table: [low, high] target ACs, at most 16, e.g. [13, 19].")][AIParameterName("ac_range")] int[]? acRange = null,
        [Description("\"fight\" (default), \"round1\" or \"day\".")] string? horizon = DprHorizons.Fight,
        [Description("Rounds in a fight, 1-10. Default 3.")] int? rounds = DprLimits.DefaultRounds,
        [Description("The day horizon's day: \"dmg2014\" (default: 6-8 encounters, 2 short rests), \"light\" (3-4 and 1; unofficial) or \"custom\".")]
        [AIParameterName("rest_preset")] string? restPreset = RestPresets.Default,
        [Description("Encounters per adventuring day, 1-20 (3.5 means 3-4); overrides the preset.")][AIParameterName("encounters_per_day")] double? encountersPerDay = null,
        [Description("Short rests per adventuring day, 0-5; overrides the preset.")][AIParameterName("short_rests")] int? shortRests = null,
        [Description("Table rulings, each false by default, e.g. {\"hew_gets_pb\": true}.")] RulingsSpec? rulings = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // The argument guard refuses a null build before this runs; the check keeps the Domain's non-null contract honest if
        // a caller ever bypasses the guard.
        var spec = build ?? throw new DndInputException($"build is null; give a build, e.g. {FighterExample}.");
        var (monster, notes) = await TargetMonster(target, spec, progress, cancellationToken);
        var request = new DprRequest
        {
            Build = spec,
            Target = target,
            TargetMonster = monster,
            Rulings = rulings,
            Levels = levels,
            AcRange = acRange,
            Horizon = horizon,
            Rounds = rounds,
            RestPreset = restPreset,
            EncountersPerDay = encountersPerDay,
            ShortRests = shortRests,
        };
        var report = await Task.Run(() => DprAnalysis.Analyze(request, cancellationToken), cancellationToken);
        return BalanceDprMarkdown.Format(report, notes);
    }

    [McpServerTool(Name = "balance_compare", Title = "Compare builds (DPR)", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "What a homebrew feature is worth in damage: a baseline build against the same build with the feature, both computed " +
        "exactly (as balance_dpr) under identical assumptions. Gives ΔDPR and % per level, the level-equivalent (Δ ÷ the DPR the " +
        "baseline gains per level in that tier; RPGBOT's slope when the baseline does not scale) and its band on the " +
        "homebrew-balance scale: Under ≤ -0.25 < On budget < 0.25 ≤ Creeping < 0.5 ≤ Over < 1 ≤ Breaking. The LE is damage " +
        "only, relative to the baseline: for a verdict, give the official option as baseline. A feat at an ASI level replaces " +
        "the ASI: judge it against the ASI (the result's ASI line). Also all three horizons, Bonus Action and Reaction " +
        "collisions, and how often condition effects land.\n" +
        "- baseline (required): a build, as balance_dpr's build, or {name, level, preset: \"warlock_baseline\"}.\n" +
        "- exactly one of:\n" +
        "  - feature: {name, attacks, modifiers, abilities, fighting_style}, what it adds: attacks and modifiers are appended (an " +
        "attack named like a baseline attack replaces it), abilities SET scores ({\"str\": 19} for a feat's +1 on 18; with a " +
        "stepped baseline give the whole step map), fighting_style replaces;\n" +
        "  - variant: the whole changed build, same fields as baseline (e.g. an ASI build to compare with a feat build).\n" +
        "- levels: e.g. [1, 5, 11, 17] (default: the baseline's level). Give the builds step values so a curve means something.\n" +
        "- target ({monster, ac, cr, profile, ...}), horizon (\"fight\" default, \"round1\" or \"day\": judge uses per rest on " +
        "\"day\"), rounds, rest_preset, encounters_per_day, short_rests, rulings: as balance_dpr.\n" +
        "Example: {\"baseline\": " + CompareBaselineExample + ", \"feature\": " + FeatureExample + ", \"levels\": [1, 5, 11]}")]
    public async Task<string> Compare(
        [Description("The build without the feature, as balance_dpr's build, or a preset.")] BuildSpec baseline,
        [Description("The whole changed build, with the same fields as baseline. Give variant or feature, not both.")]
        [SameShapeAs("baseline")] object? variant = null,
        [Description("What the feature adds to the baseline, e.g. " + FeatureExample + ". Give feature or variant, not both.")] FeatureSpec? feature = null,
        [Description("The creature attacked, as balance_dpr's target. Default: the DMG 2014 row for CR = each level.")] TargetSpec? target = null,
        [Description("Levels to compare at, 1-20, e.g. [1, 5, 11, 17]. Default: the baseline's own level.")] int[]? levels = null,
        [Description("\"fight\" (default), \"round1\" or \"day\".")] string? horizon = DprHorizons.Fight,
        [Description("Rounds in a fight, 1-10. Default 3.")] int? rounds = DprLimits.DefaultRounds,
        [Description("The day horizon's day: \"dmg2014\" (default), \"light\" (unofficial) or \"custom\".")]
        [AIParameterName("rest_preset")] string? restPreset = RestPresets.Default,
        [Description("Encounters per adventuring day, 1-20; overrides the preset.")][AIParameterName("encounters_per_day")] double? encountersPerDay = null,
        [Description("Short rests per adventuring day, 0-5; overrides the preset.")][AIParameterName("short_rests")] int? shortRests = null,
        [Description("Table rulings, each false by default, e.g. {\"savage_attacker_on_crit_dice\": true}.")] RulingsSpec? rulings = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var spec = baseline ?? throw new DndInputException($"baseline is null; give a build, e.g. {FighterExample}.");
        var (monster, notes) = await TargetMonster(target, spec, progress, cancellationToken);
        var request = new CompareRequest
        {
            Baseline = spec,
            Variant = Variant(variant),
            Feature = feature,
            Target = target,
            TargetMonster = monster,
            Rulings = rulings,
            Levels = levels,
            Horizon = horizon,
            Rounds = rounds,
            RestPreset = restPreset,
            EncountersPerDay = encountersPerDay,
            ShortRests = shortRests,
        };
        var report = await Task.Run(() => DprComparison.Compare(request, cancellationToken), cancellationToken);
        return BalanceCompareMarkdown.Format(report, notes);
    }

    /// <summary>
    /// The stat block <c>target.monster</c> names, looked up in the build's edition (2024 when it names none), and the
    /// lookup's notes; (null, []) without a monster. The target's fields are checked first (the Domain's messages, before any
    /// wait for the index): a blank monster, monster with cr or profile, and every other field problem.
    /// </summary>
    private async Task<(StatBlock? Monster, IReadOnlyList<string> Notes)> TargetMonster(
        TargetSpec? target, BuildSpec build, IProgress<ProgressNotificationValue>? progress, CancellationToken cancellationToken)
    {
        if (target?.Monster is not { } name)
        {
            return (null, []);
        }

        TargetResolver.Validate(target);
        var edition = build.Edition is { } given && DslValues.Editions.Set.TryMatch(given, out var canonical) ? canonical : DslValues.Editions.Default;
        var resolved = await _statBlocks.ResolveAsync(name, edition, progress, cancellationToken, LookupWording);
        return (resolved.Block, resolved.Notes);
    }

    /// <summary>The lookup's words: "target monster: no monster … is named …", used "in this 2024 calculation", and what to do instead.</summary>
    private static readonly MonsterLookupWording LookupWording =
        StatBlockService.Wording("target monster", "calculation", "For a monster not in the SRD, give the target's ac and saves instead.");

    /// <summary>
    /// <c>variant</c> as a build. It is published untyped (<see cref="SameShapeAsAttribute"/>: a second copy of the build
    /// schema would add some 12 KB to the tool's definition) and binds as a <see cref="JsonElement"/>; the argument guard
    /// has already checked it against <c>baseline</c>'s schema and test-bound it as a <see cref="BuildSpec"/>, so this reads
    /// it with the options the SDK would have bound the typed parameter with.
    /// </summary>
    private static BuildSpec? Variant(object? variant)
    {
        if (variant is null || variant is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
        {
            return null;
        }

        if (variant is not JsonElement { ValueKind: JsonValueKind.Object } element)
        {
            throw new DndInputException($"variant must be a build object like baseline, e.g. {FighterExample}.");
        }

        try
        {
            return element.Deserialize<BuildSpec>(McpJson.Options);
        }
        catch (JsonException ex)
        {
            // Unreachable while the guard test-binds the same type first; kept so a gap there names the field, not the SDK's
            // generic error.
            throw new DndInputException($"variant could not be read as a build at '{ex.Path}'; it takes the same fields as baseline.", ex);
        }
    }
}
