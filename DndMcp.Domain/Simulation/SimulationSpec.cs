using System.ComponentModel;
using DndMcp.Domain.Features;

namespace DndMcp.Domain.Simulation;

/// <summary>
/// One entry of <c>balance_simulate</c>'s <c>party</c> or <c>enemies</c> list (the same shape on both sides): an SRD
/// monster, a DSL build (a PC, an NPC, a homebrew creature) or a named party archetype, and how many copies.
///
/// <para>
/// <b>These classes ARE the tool's input schema</b>, like <see cref="BuildSpec"/>: every property is an optional,
/// nullable <c>init</c> property with a <see cref="DescriptionAttribute"/> the model reads, bound with the host's
/// snake_case options. Which fields are required and every range are checked by <see cref="Simulator"/> (one
/// <see cref="Core.DndInputException"/> naming the item as the argument guard does, "party item 2 (Fighter): …"), never by
/// the binder, whose own message would only say "an error occurred".
/// </para>
/// <para>
/// <b><c>monster</c> is resolved by the host</b> (refs, names, forms, the other edition's fallback — the same resolution as
/// <c>encounter_difficulty</c>) into a <see cref="StatBlock"/>, which reaches the Domain beside this spec in
/// <see cref="SimulationCombatant"/>. The Domain never looks a name up.
/// </para>
/// <para>
/// <b>A record, for <c>with</c> only</b>, as <see cref="BuildSpec"/>: the host fills an archetype's edition from the active
/// campaign's ruleset by copying the entry. The published schema is unchanged (a sealed record adds no public property).
/// </para>
/// </summary>
public sealed record CombatantSpec
{
    [Description("A label (default: the build's or monster's name). Copies get \" 2\", \" 3\"...")]
    public string? Name { get; init; }

    [Description("An SRD monster by ref or name, e.g. \"2024/monster/ogre\" or \"Ogre\". Give exactly one of monster, build, archetype.")]
    public string? Monster { get; init; }

    [Description("A DSL build (as balance_dpr takes): a PC, NPC or homebrew creature. Needs hp and ac.")]
    public BuildSpec? Build { get; init; }

    [Description("A named party archetype (a simple class build for a level); give level (and edition).")]
    public string? Archetype { get; init; }

    [Description("The level: required with archetype; with build, resolves the build at this level instead of its own.")]
    public int? Level { get; init; }

    [Description("\"2014\" or \"2024\": the archetype's rules, or the edition to find a monster in. A build carries its own.")]
    public string? Edition { get; init; }

    [Description("Hit points 1-5000. Required with build; overrides a monster's.")]
    public int? Hp { get; init; }

    [Description("Armor Class 1-40. Required with build; overrides a monster's.")]
    public int? Ac { get; init; }

    [Description("With build: proficient saving throws, e.g. [\"str\", \"con\"] (save = modifier + proficiency bonus).")]
    public IReadOnlyList<string>? SaveProficiencies { get; init; }

    [Description("Save bonuses to set, per ability, -5 to 20, e.g. {\"wis\": 4}. Overrides everything else.")]
    public SavesSpec? Saves { get; init; }

    [Description("Initiative bonus -10 to 20. Default: Dex modifier (build) or the stat block's.")]
    public int? InitiativeBonus { get; init; }

    [Description("\"front\" (in melee) or \"back\". Default: front when it has a melee attack, else back.")]
    public string? Position { get; init; }

    [Description("Copies of this entry, 1-20 (default 1). Copies share one initiative roll.")]
    public int? Count { get; init; }

    [Description("Makes death saves at 0 HP instead of dying (a named NPC). Party builds and archetypes always do.")]
    public bool? DeathSaves { get; init; }
}

/// <summary>
/// How each side fights. Every field is optional; the defaults are the common table assumption (the party focuses fire,
/// monsters spread their attacks, Legendary Resistance is saved for conditions and killing blows, healers only pick up the
/// fallen).
/// </summary>
public sealed class PolicySpec
{
    [Description("Party targeting: \"focus_fire\" (default: lowest HP), \"spread\" (random) or \"threat\" (most dangerous).")]
    public string? Party { get; init; }

    [Description("Enemy targeting: \"spread\" (default), \"focus_fire\", \"threat\", \"healer_first\" or \"break_concentration\".")]
    public string? Enemies { get; init; }

    [Description("Legendary Resistance: \"conditions\" (default: vs conditions and killing blows), \"always\" or \"never\".")]
    public string? LegendaryResistance { get; init; }

    [Description("Healing: \"downed\" (default: only allies at 0 HP), \"below_half\" or \"never\".")]
    public string? Healing { get; init; }

    [Description("Enemies keep attacking party members at 0 HP (death save failures). Default false.")]
    public bool? FinishDowned { get; init; }

    [Description("Initiative ties go to the party. Default false (higher modifier, then a roll-off).")]
    public bool? PcsWinTies { get; init; }
}

/// <summary>
/// A common-random-numbers comparison: the same fights (the same seeds) with one party member's build changed by a
/// feature, reporting the PAIRED difference, which needs far fewer fights than two separate runs to resolve a small
/// change.
/// </summary>
public sealed class CompareSpec
{
    [Description("Required. The party member to change: its 1-based position in party (a build or an archetype).")]
    public int? Member { get; init; }

    [Description("Required. The feature to add to that member's build, as balance_compare's feature: name, attacks, modifiers, abilities, fighting_style.")]
    public FeatureSpec? Feature { get; init; }
}

/// <summary>
/// One entry as the Domain receives it: the spec and, for a <see cref="CombatantSpec.Monster"/> entry, the stat block the
/// host resolved it to (null otherwise).
/// </summary>
public sealed record SimulationCombatant(CombatantSpec Spec, StatBlock? Monster = null);

/// <summary>
/// Everything <see cref="Simulator.Run"/> needs, minus host-only concerns: the seed is <see cref="Simulator.Run"/>'s own
/// argument (the host draws one from the OS when the caller gives none, and echoes it).
/// </summary>
public sealed class SimulationSpec
{
    public required IReadOnlyList<SimulationCombatant> Party { get; init; }

    public required IReadOnlyList<SimulationCombatant> Enemies { get; init; }

    /// <summary>Fights to run, 1–100,000. Ignored as a count when <see cref="Precision"/> is given (batches decide).</summary>
    public int Iterations { get; init; } = SimulationLimits.DefaultIterations;

    /// <summary>Rounds before the fight is called a draw, 1–100.</summary>
    public int RoundCap { get; init; } = SimulationLimits.DefaultRoundCap;

    /// <summary>The fight's rules (surprise, exhaustion, the concentration DC cap): "2014" or "2024". Default: the first party member's.</summary>
    public string? Edition { get; init; }

    /// <summary><see cref="SimulationValues.Surprise"/>; default none.</summary>
    public string? Surprise { get; init; }

    /// <summary><see cref="SimulationValues.EnemyHp"/>; default average.</summary>
    public string? EnemyHp { get; init; }

    /// <summary>
    /// Run batches of 10,000 fights until the 95% half-width of P(party wins) is at most this (0.001–0.5), up to 100,000
    /// fights, or fewer when the next batch would pass <see cref="SimulationLimits.WorkBudget"/> (the report then says the
    /// precision was not reached, and why). Null: exactly <see cref="Iterations"/>.
    /// </summary>
    public double? Precision { get; init; }

    /// <summary>A 1-based fight to replay alone with a full combat log, or null.</summary>
    public int? Replay { get; init; }

    public PolicySpec? Policies { get; init; }

    public CompareSpec? Compare { get; init; }

    /// <summary>Table rulings for every build in the fight (see <see cref="RulingsSpec"/>).</summary>
    public RulingsSpec? Rulings { get; init; }
}

/// <summary>The simulator's limits, in one place so messages, descriptions and tests agree.</summary>
public static class SimulationLimits
{
    public const int DefaultIterations = 10_000;
    public const int MaxIterations = 100_000;
    public const int DefaultRoundCap = 20;
    public const int MaxRoundCap = 100;
    public const int MaxCount = 20;
    public const int MaxCombatants = 40;
    public const int MaxHp = 5000;
    public const int MinInitiativeBonus = -10;
    public const int MaxInitiativeBonus = 20;

    /// <summary>
    /// At most this many fights per parallel chunk (and per progress report); a heavy fight's chunk is smaller
    /// (<see cref="ChunkWork"/>).
    /// </summary>
    public const int ChunkSize = 1024;

    /// <summary>
    /// A chunk's work at most, in the budget's measure (fights × combatants × round cap, × 2 with a comparison): a chunk of
    /// the heaviest fights is about a second of one thread's work (a million creature-turns a second over 15 threads,
    /// <see cref="WorkBudget"/>), so progress comes that often — measured on those runs, the first after 0.5 to 0.7 s and
    /// the rest at most 0.45 s apart — and a run of a few heavy fights still spreads over every thread.
    /// </summary>
    public const long ChunkWork = 65_536;

    /// <summary>
    /// Fights per batch in precision mode. The budget charges precision mode this first batch, the least it runs; a
    /// further batch runs only while the run's work stays within <see cref="WorkBudget"/>.
    /// </summary>
    public const int PrecisionBatch = 10_000;

    public const double MinPrecision = 0.001;
    public const double MaxPrecision = 0.5;

    /// <summary>
    /// The work cap: fights × combatants × round cap (× 2 with a comparison, which runs every fight twice) — an upper
    /// bound on creature-turns, sized so the worst accepted run takes about 20 s (contract §5.6). A creature-turn costs
    /// more the more creatures there are (a monster values its actions against every candidate), so the worst shape is the
    /// largest fight: 40 creatures that all stay standing to a 100-round cap. Measured on the development machine (16
    /// threads, 15 of them fighting), 5,000 such fights (20 million) took 15.8 s as fighter walls, 19.0 s as power-attack
    /// builds with spread targeting and 20.5 s as level 20 wizard archetypes against adult red dragons, about a million
    /// creature-turns a second; 3 against 3 runs 4.8 million a second, and a typical 4v3 × 10,000 run 0.05 s. At the 60
    /// million this cap used to be, the fighter walls took 43 s. Ordinary requests pass: 100,000 fights of a 4v3 at round
    /// cap 20 is 14 million, and the default 10,000 fights allow 40 combatants to round 50.
    /// <para>
    /// Precision mode is charged its first batch (<see cref="PrecisionBatch"/>), not the 100,000 fights it may run: ±1% or
    /// wider is always reached in that batch (the widest 95% half-width at 10,000 fights is ±0.98%), yet charged 100,000
    /// fights a 4v2 with a comparison, or 11 combatants, at round cap 20 was refused. Its later batches stop where the next
    /// would pass this cap, and the report says the precision was not reached at the work limit, so the worst case is the
    /// same 20 million.
    /// </para>
    /// </summary>
    public const long WorkBudget = 20_000_000;

    /// <summary>The replay log's size; past it the log is cut with a closing line, and the summary is still shown.</summary>
    public const int ReplayLogChars = 20_000;
}
