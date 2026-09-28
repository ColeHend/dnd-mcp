namespace DndMcp.Domain.Simulation;

/// <summary>
/// Everything <c>balance_simulate</c> renders (contract §5.7): the run's parameters echoed, the outcome proportions with
/// Wilson 95% intervals, the rounds, per-entry statistics with CLT 95% intervals, the stat blocks' warnings, the
/// assumptions, and optionally the replay log and the comparison.
///
/// <para>
/// <b>Deterministic.</b> A report is a pure function of the spec and the seed: the same seed gives the same report at any
/// thread count (every figure comes from integer sums), which is what "pass the seed back to reproduce" promises.
/// Nothing here depends on the machine, the thread count or the time taken.
/// </para>
/// </summary>
public sealed record SimulationReport
{
    public required ulong Seed { get; init; }

    /// <summary>Fights run (with precision: the batches run).</summary>
    public required int Iterations { get; init; }

    public required int RoundCap { get; init; }

    /// <summary>The fight's rules edition.</summary>
    public required string Edition { get; init; }

    /// <summary>A <see cref="SimulationValues.Surprise"/> value.</summary>
    public required string Surprise { get; init; }

    /// <summary>A <see cref="SimulationValues.EnemyHp"/> value.</summary>
    public required string EnemyHp { get; init; }

    /// <summary>Each policy in force, with its value and what it means.</summary>
    public required IReadOnlyList<PolicyEcho> Policies { get; init; }

    public required Proportion PartyWins { get; init; }

    /// <summary>Every party member at 0 HP (dying, stable or dead).</summary>
    public required Proportion PartyDefeated { get; init; }

    /// <summary>The round cap came first.</summary>
    public required Proportion Draw { get; init; }

    /// <summary>At least one party member dead at the end of the fight.</summary>
    public required Proportion AnyPartyDeath { get; init; }

    public required RoundsSummary Rounds { get; init; }

    /// <summary>Per entry (identical copies grouped), party entries first, in the order given.</summary>
    public required IReadOnlyList<CombatantReport> Combatants { get; init; }

    /// <summary>Distinct normalization warnings per stat block in the fight (enemies first).</summary>
    public required IReadOnlyList<StatBlockWarnings> Warnings { get; init; }

    /// <summary>What the numbers assume: the model's simplifications, the policies, anything approximated this run.</summary>
    public required IReadOnlyList<string> Assumptions { get; init; }

    /// <summary>Precision mode: the requested 95% half-width of P(party wins), or null.</summary>
    public double? Precision { get; init; }

    /// <summary>Precision mode: whether the half-width was reached before 100,000 fights.</summary>
    public bool? PrecisionReached { get; init; }

    /// <summary>The replayed fight (1-based), or null.</summary>
    public int? ReplayIteration { get; init; }

    /// <summary>The replayed fight's full log (capped), ending with its summary.</summary>
    public string? ReplayLog { get; init; }

    /// <summary>The replayed fight's outcome and rounds (identical to that fight in the full run).</summary>
    public string? ReplayOutcome { get; init; }

    public int? ReplayRounds { get; init; }

    public CompareReport? Compare { get; init; }
}

/// <summary>One policy echoed: its field, its value and what the value means.</summary>
public sealed record PolicyEcho(string Name, string Value, string Meaning);

/// <summary>How long fights lasted: the mean (CLT 95%), median, 90th percentile, and the count of fights ending in each round.</summary>
/// <param name="Histogram">Index r − 1 counts the fights that ended in round r (draws in the cap's round).</param>
public sealed record RoundsSummary(MeanEstimate Mean, long P50, long P90, IReadOnlyList<long> Histogram);

/// <summary>
/// One entry's statistics, per creature (copies of an entry are pooled: "each ogre"). Damage is raw (after resistances)
/// and effective (capped at the hit points it actually removed, temporary ones included).
/// </summary>
public sealed record CombatantReport
{
    public required string Name { get; init; }

    /// <summary>"party" or "enemies".</summary>
    public required string Side { get; init; }

    public required int Count { get; init; }

    /// <summary>"monster 2024/monster/ogre", "build \"L5 Fighter\" (level 5)".</summary>
    public required string Source { get; init; }

    /// <summary>Hit points at the start of a fight (the average when enemy HP is rolled).</summary>
    public required double MaxHp { get; init; }

    public required int ArmorClass { get; init; }

    /// <summary>Makes death saves at 0 HP.</summary>
    public required bool DeathSaves { get; init; }

    public required Proportion DroppedToZero { get; init; }

    public required Proportion DeadAtEnd { get; init; }

    public required MeanEstimate HpLost { get; init; }

    public required long HpLostP50 { get; init; }

    public required long HpLostP90 { get; init; }

    public required MeanEstimate DamageDealt { get; init; }

    public required MeanEstimate DamageDealtEffective { get; init; }

    public required MeanEstimate DamageTaken { get; init; }

    public required MeanEstimate DamageTakenEffective { get; init; }

    public required MeanEstimate Kills { get; init; }

    /// <summary>Limited resources: mean uses per fight.</summary>
    public required IReadOnlyList<ResourceUsage> Resources { get; init; }

    /// <summary>Mean Legendary Resistance uses spent per fight, or null for a creature without it.</summary>
    public double? LegendaryResistanceSpent { get; init; }

    /// <summary>Mean legendary actions taken per fight, or null.</summary>
    public double? LegendaryActions { get; init; }
}

/// <summary>A limited resource and how much of it a fight used on average.</summary>
/// <param name="Available">Uses per fight ("recharge" for a recharge action: 0).</param>
public sealed record ResourceUsage(string Name, int Available, double MeanUsed);

/// <summary>A stat block's normalization warnings, shown with every result that uses it.</summary>
public sealed record StatBlockWarnings(string Name, string Ref, string Side, IReadOnlyList<NormalizationWarning> Warnings);

/// <summary>
/// A common-random-numbers comparison: the same fights (same seeds) with and without the feature. The differences are
/// PAIRED — per fight, variant minus baseline — so their intervals are far narrower than the two runs' own.
/// </summary>
public sealed record CompareReport
{
    public required int Member { get; init; }

    public required string MemberName { get; init; }

    public required string Feature { get; init; }

    public required Proportion BaselineWins { get; init; }

    public required Proportion VariantWins { get; init; }

    /// <summary>E[win(variant) − win(baseline)] per fight, with its CLT interval.</summary>
    public required MeanEstimate WinDifference { get; init; }

    public required MeanEstimate BaselineRounds { get; init; }

    public required MeanEstimate VariantRounds { get; init; }

    public required MeanEstimate RoundsDifference { get; init; }

    public required Proportion BaselineAnyDeath { get; init; }

    public required Proportion VariantAnyDeath { get; init; }

    public required MeanEstimate AnyDeathDifference { get; init; }
}
