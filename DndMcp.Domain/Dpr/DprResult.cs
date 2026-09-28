using DndMcp.Domain.Dice;
using DndMcp.Domain.Features;

namespace DndMcp.Domain.Dpr;

/// <summary>
/// One evaluation of one build against one target over one horizon: the headline damage per round and everything a
/// reader needs to check it (research A2–A9, contract §4). Every "per round" number is an expectation over the horizon:
/// for round1 it is the single turn; for a fight of R rounds it is the total over R rounds ÷ R, so the parts add up to
/// <see cref="DamagePerRound"/>.
///
/// <para>
/// <b>Why so much besides the headline:</b> a DPR number with no breakdown cannot be argued with. The attack lines show
/// the hit chances actually used (Vex, Topple and rate sources move them turn by turn), the riders show what each
/// optional resource bought per use, and the notes say what the model deliberately leaves out (kill triggers, sap,
/// reaction resources) so a reader does not mistake a convention for a measurement.
/// </para>
/// </summary>
public sealed record DprResult
{
    public required string BuildName { get; init; }

    public required string Edition { get; init; }

    public required int Level { get; init; }

    /// <summary>The build as evaluated (to-hit parts, damage parts, resolved modifiers), for "the build as read".</summary>
    public required ResolvedBuild Build { get; init; }

    /// <summary>The target as evaluated, with where its AC and saves came from ("DMG 2014 row for CR 5").</summary>
    public required ResolvedTarget Target { get; init; }

    /// <summary><see cref="DprHorizons"/>: round1 or fight.</summary>
    public required string Horizon { get; init; }

    /// <summary>Rounds averaged (1 for round1).</summary>
    public required int Rounds { get; init; }

    /// <summary>The headline: E[damage] per round over the horizon.</summary>
    public required double DamagePerRound { get; init; }

    /// <summary>E[damage] in the first round (setup costs paid): the "nova" figure, whatever the horizon.</summary>
    public required double Round1Damage { get; init; }

    /// <summary>E[damage] in each round of the horizon, in order (one entry for round1).</summary>
    public required IReadOnlyList<double> DamageByRound { get; init; }

    /// <summary>The target's AC before cover; each <see cref="AttackReport.TargetArmorClass"/> is the AC that line rolled against.</summary>
    public required int TargetArmorClass { get; init; }

    /// <summary>One line per attack and way of making it, in turn order: Attack action, Action Surge, Bonus Action, Hew, Cleave, reaction.</summary>
    public required IReadOnlyList<AttackReport> Attacks { get; init; }

    /// <summary>extra_damage riders and damage rerolls (Savage Attacker), in build order.</summary>
    public required IReadOnlyList<RiderReport> Riders { get; init; }

    public required IReadOnlyList<ExtraAttackReport> ExtraAttacks { get; init; }

    /// <summary>condition_on_hit modifiers.</summary>
    public required IReadOnlyList<ConditionReport> Conditions { get; init; }

    public required IReadOnlyList<SaveEffectReport> SaveEffects { get; init; }

    public required IReadOnlyList<PowerAttackReport> PowerAttacks { get; init; }

    /// <summary>
    /// Every resource-limited modifier the turn tracks, with the uses it spent per round: what the day horizon's
    /// "expected uses per round" reads (run with that modifier in <see cref="DprOptions.UnlimitedResources"/>).
    /// </summary>
    public required IReadOnlyList<ResourceUse> Resources { get; init; }

    /// <summary>What the Bonus Action went to (each option and "none"), when the build has anything to spend it on; empty otherwise.</summary>
    public required IReadOnlyList<BonusActionChoice> BonusActionChoices { get; init; }

    /// <summary>The reaction attack taken each round, or null when the build has none.</summary>
    public ReactionReport? Reaction { get; init; }

    /// <summary>
    /// The round-1 turn's damage distribution (the round's reaction included), or null when not requested. Its P(≥ HP) is
    /// given only when the turn's damage is one creature's (no save effect with several targets).
    /// </summary>
    public DamageDistribution? Round1Distribution { get; init; }

    /// <summary>P(at least one hit with a Sap weapon) per turn: its defensive value, reported, never added to damage. Null without Sap.</summary>
    public double? SapChancePerTurn { get; init; }

    /// <summary>The table rulings that can change this build's numbers, echoed with their values.</summary>
    public required IReadOnlyList<RulingUsed> Rulings { get; init; }

    /// <summary>
    /// Plain-language notes: what the model leaves out for this build (kill triggers, reaction resources, masteries with
    /// no DPR), the build's and the target's own notes, and warnings such as typeless damage against a resistant target.
    /// </summary>
    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>Distinct turn states the exact dynamic programme visited (diagnostic: what the limits bound).</summary>
    public required int TurnStates { get; init; }
}

/// <summary>How an attack line was made: which part of the turn's action economy it used.</summary>
public static class AttackUses
{
    /// <summary>Part of the Attack action.</summary>
    public const string AttackAction = "attack_action";

    /// <summary>An attack whose own action is the Bonus Action (an offhand attack, Spiritual Weapon).</summary>
    public const string BonusAction = "bonus_action";

    /// <summary>A second Attack action's attacks (extra_attack with action "action").</summary>
    public const string ActionSurge = "action_surge";

    /// <summary>A Bonus Action extra attack (Hew, the 2014 GWM bonus attack): not part of the Attack action.</summary>
    public const string BonusActionExtra = "bonus_action_extra";

    /// <summary>Cleave's attack against a second creature.</summary>
    public const string Cleave = "cleave";

    /// <summary>An attack on another creature's turn (Opportunity Attack, Sentinel).</summary>
    public const string Reaction = "reaction";
}

/// <summary>One attack line: an attack made one way, with the odds and damage the turns actually saw.</summary>
/// <param name="Attack">The attack's name.</param>
/// <param name="Use">An <see cref="AttackUses"/> value.</param>
/// <param name="Source">The extra_attack that grants it (Action Surge, Hew, the reaction), or null.</param>
/// <param name="AttackBonus">The flat attack bonus before a power attack's penalty.</param>
/// <param name="CritMin">Crits on this natural face or higher.</param>
/// <param name="TargetArmorClass">The AC it rolls against: the target's AC plus cover unless it ignores cover.</param>
/// <param name="AttacksPerRound">Expected attacks made per round (a Bonus Action line only on the turns it is chosen).</param>
/// <param name="HitChance">P(hit | attack made), crits included, averaged over the states the attacks were made in.</param>
/// <param name="CritChance">P(crit | attack made).</param>
/// <param name="DamagePerRound">Expected damage per round from these attacks: hits with their riders, Graze and on-miss riders.</param>
public sealed record AttackReport(
    string Attack,
    string Use,
    string? Source,
    int AttackBonus,
    int CritMin,
    int TargetArmorClass,
    double AttacksPerRound,
    double HitChance,
    double CritChance,
    double DamagePerRound)
{
    /// <summary>Expected damage per attack made (0 when none is made).</summary>
    public double DamagePerAttack => AttacksPerRound > 0 ? DamagePerRound / AttacksPerRound : 0;
}

/// <summary>
/// A damage rider (extra_damage) or a damage reroll (Savage Attacker): what it added per round and per use.
/// <see cref="DamagePerRound"/> is the rider's MARGINAL damage: each hit's damage with it minus without it, which is its
/// own dice when nothing adjusts them, and exactly what it contributed when resistance rounds a shared type.
/// </summary>
/// <param name="Kind">"extra_damage" or "reroll_damage_take_best" (<see cref="DslValues.Kinds"/>).</param>
/// <param name="When">A <see cref="DslValues.When"/> value, or null for a damage reroll.</param>
/// <param name="Policy">The policy, when spending it is a choice; null for a rider applied whenever it can be.</param>
/// <param name="UsesPerRound">Hits (or misses, for on_miss) it was applied to per round; for a resource, uses spent.</param>
public sealed record RiderReport(
    string Name,
    string Kind,
    string? When,
    string? Policy,
    double? UseValue,
    ResolvedResource? Resource,
    double DamagePerRound,
    double UsesPerRound)
{
    /// <summary>Damage per use: what one use (a spell slot, a Savage Attacker reroll) bought; null when never used.</summary>
    public double? DamagePerUse => UsesPerRound > 0 ? DamagePerRound / UsesPerRound : null;
}

/// <summary>An extra_attack and how often it was taken.</summary>
/// <param name="Action">A <see cref="DslValues.ExtraAttackActions"/> value.</param>
/// <param name="UsesPerRound">Times taken per round (for a reaction: its trigger probability).</param>
/// <param name="DamagePerRound">Expected damage per round from its attacks.</param>
public sealed record ExtraAttackReport(string Name, string Attack, string Action, string Trigger, ResolvedResource? Resource, double UsesPerRound, double DamagePerRound);

/// <summary>
/// A condition_on_hit: how often it forced a save and how often the condition landed. <see cref="LandChancePerTurn"/> and
/// <see cref="LandChancePerFight"/> are about the condition on the target, whatever imposed it — two modifiers that impose
/// the same condition share them, and Topple's Prone counts for a prone rider — while attempts and landings are this
/// modifier's own.
/// </summary>
/// <param name="AttemptsPerRound">Saves it forced per round (uses spent, for a resource).</param>
/// <param name="LandsPerRound">Failed saves per round.</param>
/// <param name="LandChancePerTurn">P(the condition was imposed at least once in a turn), averaged over the rounds.</param>
/// <param name="LandChancePerFight">P(it was imposed at least once in the fight); null for round1.</param>
public sealed record ConditionReport(string Name, string Condition, string Ability, double AttemptsPerRound, double LandsPerRound, double LandChancePerTurn, double? LandChancePerFight)
{
    /// <summary>
    /// With Legendary Resistance on the target: (L + 1) / F attempts to land it past L refusals, F the chance the target
    /// fails against the DC of the first attack it applies to (the parity of <see cref="SaveEffectReport.ExpectedCastsToLand"/>).
    /// The landing chances above do not spend Legendary Resistance; this does. Null without it, and for an immune target.
    /// </summary>
    public double? ExpectedAttemptsToLand { get; init; }

    /// <summary>
    /// The target is immune to <see cref="Condition"/> (a stat block's condition immunity): it is never attempted, so its
    /// attempts, landings and landing chances are all 0, and the result says why rather than showing a bare 0%.
    /// </summary>
    public bool Immune { get; init; }
}

/// <summary>
/// A save effect (research A6, A8). The per-cast figures are for a cast against the target as given (its initial
/// condition); <see cref="DamagePerRound"/> is what it added over the horizon, casts per round included.
/// <para>
/// With <c>target.hp</c>, the kill figures use the SHARED damage roll: one roll x for every target, independent saves, so
/// P(a target dies | x) = F·[fail damage ≥ hp] + (1 − F)·[success damage ≥ hp] and the kill count is Binomial(n, that)
/// given x. Rolling per target instead would understate how correlated the kills are (Fireball on four goblins: P(all
/// die) 0.99933, not 0.99877).
/// </para>
/// </summary>
/// <param name="FailChance">P(one target fails the save), per cast against the target as given.</param>
/// <param name="DamagePerTarget">Expected damage one target takes per cast (half or none on a success; evasion; adjustments).</param>
/// <param name="RawDamage">Expected total damage per cast over all targets, uncapped (<see cref="DamagePerTarget"/> × targets).</param>
/// <param name="EffectiveDamage">The same with each target's damage capped at its HP (overkill removed); null without HP.</param>
/// <param name="KillDistribution">P(exactly k targets die), k = 0..targets; null without HP.</param>
/// <param name="ExpectedCastsToLand">(L + 1) / F casts to land its condition past L Legendary Resistances; +∞ when F = 0; null when not applicable.</param>
/// <param name="LandChancePerTurn">
/// P(its condition lands on the main (first) target in a turn): cast and failed, including conditions imposed on it earlier
/// in the turn; null without a condition. Legendary Resistance is not spent here (see <paramref name="ExpectedCastsToLand"/>).
/// </param>
/// <param name="LandChancePerFight">P(its condition lands on the main target at least once in the fight); null for round1 or without a condition.</param>
public sealed record SaveEffectReport(
    string Name,
    string Ability,
    int Dc,
    int Targets,
    bool TargetsFromArea,
    string? Condition,
    double FailChance,
    double DamagePerTarget,
    double RawDamage,
    double CastsPerRound,
    double DamagePerRound,
    double? EffectiveDamage,
    double? KillChanceEach,
    double? AllDieChance,
    double? ExpectedKills,
    IReadOnlyList<double>? KillDistribution,
    double? ExpectedCastsToLand,
    double? LandChancePerTurn,
    double? LandChancePerFight)
{
    /// <summary>
    /// The target is immune to <see cref="Condition"/>: the effect's damage still counts, its condition never lands (the
    /// landing chances are 0 and <see cref="ExpectedCastsToLand"/> is null), and the result says why.
    /// </summary>
    public bool ConditionImmune { get; init; }
}

/// <summary>
/// A power attack (2014 GWM/Sharpshooter −5/+10): the turn-level choice and, for transparency, the per-attack rule of
/// thumb it agrees with away from riders and triggers (research A4): on iff P′/P &gt; D/(D + bonus).
/// </summary>
/// <param name="OnChancePerRound">P(switched on) per round, over the advantage samples and the rounds.</param>
/// <param name="Round1Choices">Round 1's choice per advantage sample (one sample when the build has no rate sources).</param>
/// <param name="Toggles">The per-attack rule, for each attack it applies to, on a plain roll against the target as given.</param>
public sealed record PowerAttackReport(
    string Name,
    int Penalty,
    int Bonus,
    string Policy,
    double OnChancePerRound,
    IReadOnlyList<PowerAttackChoice> Round1Choices,
    IReadOnlyList<PowerAttackToggle> Toggles);

/// <summary>Round 1's power-attack choice when the named rate sources are present (with <see cref="Probability"/>).</summary>
public sealed record PowerAttackChoice(double Probability, IReadOnlyList<string> SourcesPresent, bool On);

/// <summary>
/// The per-attack toggle rule: with P the hit chance, P′ the hit chance at the penalty and D the average damage of a
/// normal hit, the power attack gains P′·(D + bonus) − P·D, so it is worth taking iff P′/P &gt; D/(D + bonus). Crit terms
/// cancel because the crit chance does not move.
/// </summary>
public sealed record PowerAttackToggle(string Attack, double HitChance, double HitChanceWithPenalty, double DamageOnHit, double Threshold, bool RuleSaysOn);

/// <summary>
/// A use of the Bonus Action and how often the turn chose it, per round. The choices add up to the chance the Bonus Action
/// was still free when the turn reached it: a rider that costs it (2024 Divine Smite) or a setup takes it earlier.
/// </summary>
/// <param name="Option">"bonus_action attacks (…)", an extra_attack's or save effect's label, or "none".</param>
public sealed record BonusActionChoice(string Option, double ChosenPerRound);

/// <summary>The reaction attack taken each round: the best by trigger probability × expected damage.</summary>
/// <param name="DamagePerRound">Trigger probability × its expected damage.</param>
/// <param name="Considered">Every reaction extra_attack with its trigger probability × expected damage, for the record.</param>
public sealed record ReactionReport(string Name, string Attack, double TriggerProbability, double DamageWhenTaken, double DamagePerRound, IReadOnlyList<NamedAmount> Considered);

/// <summary>
/// A resource-limited modifier and its uses per round over the horizon: rider spends, extra attacks taken (Action Surge),
/// save effects cast, conditions attempted.
/// </summary>
/// <param name="Unlimited">Whether this evaluation treated its uses as unlimited (<see cref="DprOptions.UnlimitedResources"/>).</param>
public sealed record ResourceUse(ModifierRef Source, ResolvedResource Resource, bool Unlimited, double UsesPerRound);

/// <summary>A labelled number.</summary>
public sealed record NamedAmount(string Name, double Amount);

/// <summary>A table ruling that can change this build's numbers, and its value in this evaluation.</summary>
/// <param name="Name">The RulingsSpec field: hew_gets_pb, cleave_part_of_attack_action, gwf_on_riders, savage_attacker_on_crit_dice.</param>
public sealed record RulingUsed(string Name, bool Value, string Meaning);

/// <summary>P(damage = value).</summary>
public sealed record DamageProbability(long Damage, double Probability);

/// <summary>
/// A turn's damage distribution, exact over every outcome (not sampled): what "P(this kills an ogre in one round)" and the
/// percentiles are read from.
/// </summary>
public sealed record DamageDistribution
{
    private DamageDistribution(IReadOnlyList<DamageProbability> values, int? hitPoints)
    {
        Values = values;
        HitPoints = hitPoints;
        Mean = values.Sum(v => v.Damage * v.Probability);
        ZeroChance = values.Where(v => v.Damage == 0).Sum(v => v.Probability);
        AtLeastHitPointsChance = hitPoints is { } hp ? AtLeast(hp) : null;
    }

    /// <summary>Every reachable total, ascending, with its probability (sums to 1).</summary>
    public IReadOnlyList<DamageProbability> Values { get; }

    public double Mean { get; }

    /// <summary>P(no damage at all).</summary>
    public double ZeroChance { get; }

    /// <summary>The target's HP when given.</summary>
    public int? HitPoints { get; }

    /// <summary>P(damage ≥ HP): the chance the turn alone drops a target at full HP. Null without HP.</summary>
    public double? AtLeastHitPointsChance { get; }

    /// <summary>The 10th, 25th, 50th, 75th and 90th percentiles (<see cref="Percentile"/>): the spread results print.</summary>
    public IReadOnlyList<long> Quantiles => [Percentile(0.10), Percentile(0.25), Percentile(0.50), Percentile(0.75), Percentile(0.90)];

    public long Min => Values[0].Damage;

    public long Max => Values[^1].Damage;

    public static DamageDistribution From(Pmf<double> pmf, int? hitPoints)
    {
        ArgumentNullException.ThrowIfNull(pmf);

        var values = new List<DamageProbability>(pmf.Count);
        for (var i = 0; i < pmf.Count; i++)
        {
            values.Add(new DamageProbability(pmf.Values[i], pmf.Weights[i] / pmf.Total));
        }

        return new DamageDistribution(values, hitPoints);
    }

    /// <summary>P(damage ≥ <paramref name="damage"/>).</summary>
    public double AtLeast(long damage) => Values.Where(v => v.Damage >= damage).Sum(v => v.Probability);

    /// <summary>
    /// The smallest total whose cumulative probability reaches <paramref name="fraction"/> (0 &lt; fraction ≤ 1): the
    /// p-th percentile of a discrete distribution. A tiny tolerance absorbs rounding in the cumulative sum, so an exact
    /// 0.5 boundary reads as reached.
    /// </summary>
    public long Percentile(double fraction)
    {
        if (!double.IsFinite(fraction) || fraction is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(fraction), fraction, "A percentile is a fraction in (0, 1].");
        }

        var cumulative = 0.0;
        foreach (var value in Values)
        {
            cumulative += value.Probability;
            if (cumulative >= fraction - 1e-12)
            {
                return value.Damage;
            }
        }

        return Max;
    }
}
