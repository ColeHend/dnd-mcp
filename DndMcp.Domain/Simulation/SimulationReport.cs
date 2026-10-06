using System.Text.Json.Serialization;

namespace DndMcp.Domain.Simulation;

/// <summary>
/// Everything <c>balance_simulate</c> renders (contract §5.7): the run's parameters echoed, the outcome proportions with
/// Wilson 95% intervals, the rounds (mean with its CLT 95% interval), per-entry statistics, the stat blocks' warnings,
/// the assumptions, and optionally the replay log and the comparison.
///
/// <para>
/// <b>An entry's copies are not independent samples.</b> They fight the same fight (three ogres fall together to one
/// Fireball), so an interval over fights × copies would be too narrow by up to √copies. A per-entry mean's CLT interval
/// therefore takes each fight's total over the copies as its one sample; a per-entry share (<see cref="CreatureShare"/>)
/// carries no interval at all, as nothing printed one and the right one is not a Wilson interval over fights × copies.
/// </para>
///
/// <para>
/// <b>Deterministic.</b> A report is a pure function of the spec and the seed: the same seed gives the same report at any
/// thread count (every figure comes from integer sums), which is what "pass the seed back to reproduce" promises.
/// Nothing here depends on the machine, the thread count or the time taken.
/// </para>
/// </summary>
public sealed record SimulationReport
{
    /// <summary>The master seed: given, or drawn by the host; the same seed and spec give this report again.</summary>
    public required ulong Seed { get; init; }

    /// <summary>Fights run (with precision: the batches run).</summary>
    public required int Iterations { get; init; }

    /// <summary>Rounds before a fight still going is a draw.</summary>
    public required int RoundCap { get; init; }

    /// <summary>The fight's rules edition.</summary>
    public required string Edition { get; init; }

    /// <summary>A <see cref="SimulationValues.Surprise"/> value.</summary>
    public required string Surprise { get; init; }

    /// <summary>A <see cref="SimulationValues.EnemyHp"/> value.</summary>
    public required string EnemyHp { get; init; }

    /// <summary>Each policy in force, with its value and what it means.</summary>
    public required IReadOnlyList<PolicyEcho> Policies { get; init; }

    /// <summary>No enemy above 0 HP (all dead, or a troll down) before the round cap.</summary>
    public required Proportion PartyWins { get; init; }

    /// <summary>Every party member at 0 HP (dying, stable or dead).</summary>
    public required Proportion PartyDefeated { get; init; }

    /// <summary>The round cap came first.</summary>
    public required Proportion Draw { get; init; }

    /// <summary>
    /// At least one party member dead at the end of the fight: the deaths the fight dealt. A member still dying then is
    /// <see cref="AnyPartyDying"/>, not counted here.
    /// </summary>
    public required Proportion AnyPartyDeath { get; init; }

    /// <summary>
    /// At least one party member still dying at the end: at 0 HP, neither stable nor dead. A fight stops the moment a
    /// side has nobody above 0 HP, win or lose, so their remaining death saves are never rolled; each of these fights may
    /// yet add a death to <see cref="AnyPartyDeath"/>, which the report does not guess.
    /// </summary>
    public required Proportion AnyPartyDying { get; init; }

    /// <summary>How long the fights lasted.</summary>
    public required RoundsSummary Rounds { get; init; }

    /// <summary>Per entry (identical copies grouped), party entries first, in the order given.</summary>
    public required IReadOnlyList<CombatantReport> Combatants { get; init; }

    /// <summary>Distinct normalization warnings per stat block in the fight (enemies first).</summary>
    public required IReadOnlyList<StatBlockWarnings> Warnings { get; init; }

    /// <summary>What the numbers assume: the model's simplifications, the policies, anything approximated this run.</summary>
    public required IReadOnlyList<string> Assumptions { get; init; }

    /// <summary>Precision mode: the requested 95% half-width of P(party wins), or null.</summary>
    public double? Precision { get; init; }

    /// <summary>Precision mode: whether the half-width was reached within <see cref="PrecisionMaxFights"/> fights.</summary>
    public bool? PrecisionReached { get; init; }

    /// <summary>
    /// Precision mode: the most fights it could run here, or null. <see cref="SimulationLimits.MaxIterations"/>, or fewer
    /// whole batches when a fight this size would take the run past <see cref="SimulationLimits.WorkBudget"/> sooner: a
    /// precision not reached below 100,000 fights stopped at the work limit, and a lower round cap, fewer combatants or
    /// no comparison would let it run on.
    /// </summary>
    public int? PrecisionMaxFights { get; init; }

    /// <summary>The replayed fight (1-based), or null.</summary>
    public int? ReplayIteration { get; init; }

    /// <summary>The replayed fight's full log (capped), ending with its summary.</summary>
    public string? ReplayLog { get; init; }

    /// <summary>
    /// The replayed fight's outcome and rounds (identical to that fight in the full run, or, past the fights precision mode
    /// ran, in any run of this seed that reaches it).
    /// </summary>
    public string? ReplayOutcome { get; init; }

    /// <summary>The replayed fight's rounds.</summary>
    public int? ReplayRounds { get; init; }

    /// <summary>The comparison, when one was asked for.</summary>
    public CompareReport? Compare { get; init; }

    /// <summary>
    /// The fight was picked up from a live state (<see cref="SimulationSpec.Resume"/>, or an entry's
    /// <see cref="SimulationCombatant.Start"/>): each combatant's <see cref="CombatantReport.MaxHp"/> is its HP at the start
    /// rather than its maximum (the host heads the column "HP at start"), hit points lost are counted from there, and with a
    /// resume the rounds count from the resumed round as round 1 (the assumptions say where it resumed). Left out of the
    /// JSON when false, so a fresh fight's report serializes exactly as it did before seeding existed
    /// (<c>SimulationGoldenTests</c> pins that byte for byte).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Resumed { get; init; }
}

/// <summary>One policy echoed: its field, its value and what the value means.</summary>
public sealed record PolicyEcho(string Name, string Value, string Meaning);

/// <summary>How long fights lasted: the mean (CLT 95%), median, 90th percentile, and the count of fights ending in each round.</summary>
/// <param name="Histogram">Index r − 1 counts the fights that ended in round r (draws in the cap's round).</param>
public sealed record RoundsSummary(MeanEstimate Mean, long P50, long P90, IReadOnlyList<long> Histogram);

/// <summary>
/// One entry's statistics, per creature (copies of an entry are pooled: "each ogre"); a mean's interval treats each fight
/// as one sample (see <see cref="SimulationReport"/>). Damage is raw (after resistances) and effective (capped at the hit
/// points it actually removed, temporary ones included).
/// </summary>
public sealed record CombatantReport
{
    /// <summary>The entry's label: its name, else the monster's, build's or archetype's.</summary>
    public required string Name { get; init; }

    /// <summary>"party" or "enemies".</summary>
    public required string Side { get; init; }

    /// <summary>Copies of the entry in the fight.</summary>
    public required int Count { get; init; }

    /// <summary>"monster 2024/monster/ogre", "build \"L5 Fighter\" (level 5)".</summary>
    public required string Source { get; init; }

    /// <summary>
    /// Hit points at the start of a fight (the average when enemy HP is rolled): the maximum in a fresh fight, the live HP
    /// in a resumed one (<see cref="SimulationReport.Resumed"/>).
    /// </summary>
    public required double MaxHp { get; init; }

    /// <summary>Armor class as compiled (a build's ac modifiers included, cover not).</summary>
    public required int ArmorClass { get; init; }

    /// <summary>Makes death saves at 0 HP.</summary>
    public required bool DeathSaves { get; init; }

    /// <summary>Dropped to 0 HP at least once in the fight (in a resumed fight, starting at 0 HP counts).</summary>
    public required CreatureShare DroppedToZero { get; init; }

    /// <summary>Dead at the end of the fight.</summary>
    public required CreatureShare DeadAtEnd { get; init; }

    /// <summary>
    /// Still dying at the end (at 0 HP, neither stable nor dead; only a creature that makes death saves can be): the fight
    /// stopped before its death saves were done, and the report does not guess them (<see cref="SimulationReport.AnyPartyDying"/>).
    /// </summary>
    public required CreatureShare DyingAtEnd { get; init; }

    /// <summary>Hit points lost by the end (all of them when dead), per fight, from the HP it started with.</summary>
    public required MeanEstimate HpLost { get; init; }

    /// <summary>The median of <see cref="HpLost"/>.</summary>
    public required long HpLostP50 { get; init; }

    /// <summary>The 90th percentile of <see cref="HpLost"/>.</summary>
    public required long HpLostP90 { get; init; }

    /// <summary>Damage dealt per fight, after the targets' resistances.</summary>
    public required MeanEstimate DamageDealt { get; init; }

    /// <summary>Damage dealt per fight that removed hit points, temporary ones included (none past 0 HP).</summary>
    public required MeanEstimate DamageDealtEffective { get; init; }

    /// <summary>Damage taken per fight, after its own resistances.</summary>
    public required MeanEstimate DamageTaken { get; init; }

    /// <summary>Damage taken per fight that removed hit points, temporary ones included (none past 0 HP).</summary>
    public required MeanEstimate DamageTakenEffective { get; init; }

    /// <summary>
    /// The other side's creatures it killed, per fight: by damage, by an outright kill (Power Word Kill, the Slaying Bow or
    /// Slaying Longbow) with no damage dealt, or by a sixth level of exhaustion it imposed. A death from failed death saves,
    /// or from regeneration stopped at 0 HP, has no source and is credited to no one, so the party's deaths can far
    /// outnumber the enemies' kills.
    /// </summary>
    public required MeanEstimate Kills { get; init; }

    /// <summary>Limited resources: mean uses per fight.</summary>
    public required IReadOnlyList<ResourceUsage> Resources { get; init; }

    /// <summary>Mean Legendary Resistance uses spent per fight, or null for a creature without it.</summary>
    public double? LegendaryResistanceSpent { get; init; }

    /// <summary>Mean legendary actions taken per fight, or null.</summary>
    public double? LegendaryActions { get; init; }
}

/// <summary>
/// How often something happened to one creature of an entry: <see cref="Count"/> of <see cref="Total"/> creature-fights
/// (fights × copies). No interval: copies of an entry fight the same fight and are not independent samples.
/// </summary>
public sealed record CreatureShare(long Count, long Total)
{
    /// <summary>Count ÷ total (0 with no fights).</summary>
    public double Estimate => Total == 0 ? 0 : (double)Count / Total;
}

/// <summary>A limited resource and how much of it a fight used on average.</summary>
/// <param name="Available">Uses per fight ("recharge" for a recharge action: 0); in a resumed fight, what was left at the resume.</param>
public sealed record ResourceUsage(string Name, int Available, double MeanUsed);

/// <summary>A stat block's normalization warnings, shown with every result that uses it.</summary>
public sealed record StatBlockWarnings(string Name, string Ref, string Side, IReadOnlyList<NormalizationWarning> Warnings);

/// <summary>
/// A common-random-numbers comparison: the same fights (same seeds) with and without the feature. The differences are
/// PAIRED — per fight, variant minus baseline — so their intervals are far narrower than the two runs' own.
/// </summary>
public sealed record CompareReport
{
    /// <summary>The changed party entry's 1-based position.</summary>
    public required int Member { get; init; }

    /// <summary>The changed entry's label.</summary>
    public required string MemberName { get; init; }

    /// <summary>The feature's name.</summary>
    public required string Feature { get; init; }

    /// <summary>P(party wins) without the feature (the headline's).</summary>
    public required Proportion BaselineWins { get; init; }

    /// <summary>P(party wins) with it, on the same seeds.</summary>
    public required Proportion VariantWins { get; init; }

    /// <summary>E[win(variant) − win(baseline)] per fight, with its CLT interval.</summary>
    public required MeanEstimate WinDifference { get; init; }

    /// <summary>Mean rounds without the feature.</summary>
    public required MeanEstimate BaselineRounds { get; init; }

    /// <summary>Mean rounds with it.</summary>
    public required MeanEstimate VariantRounds { get; init; }

    /// <summary>E[rounds(variant) − rounds(baseline)] per fight, with its CLT interval.</summary>
    public required MeanEstimate RoundsDifference { get; init; }

    /// <summary>P(a party member dies) without the feature.</summary>
    public required Proportion BaselineAnyDeath { get; init; }

    /// <summary>P(a party member dies) with it.</summary>
    public required Proportion VariantAnyDeath { get; init; }

    /// <summary>E[death(variant) − death(baseline)] per fight, with its CLT interval.</summary>
    public required MeanEstimate AnyDeathDifference { get; init; }

    /// <summary>
    /// P(a party member is left dying) without the feature (<see cref="SimulationReport.AnyPartyDying"/>). Read with the
    /// deaths: a feature that ends fights sooner leaves the fallen fewer rounds to fail death saves, so fewer deaths may
    /// only be more left dying.
    /// </summary>
    public required Proportion BaselineAnyDying { get; init; }

    /// <summary>P(a party member is left dying) with it.</summary>
    public required Proportion VariantAnyDying { get; init; }

    /// <summary>E[dying(variant) − dying(baseline)] per fight, with its CLT interval.</summary>
    public required MeanEstimate AnyDyingDifference { get; init; }
}
