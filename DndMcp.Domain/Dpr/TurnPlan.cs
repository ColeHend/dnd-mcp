using DndMcp.Domain.Core;
using DndMcp.Domain.Dice;
using DndMcp.Domain.Features;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Dpr;

/// <summary>
/// How an attack is made this turn. In-process only (results use the <see cref="AttackUses"/> strings). The kind decides
/// what the attack sees and changes, and whether it is "part of the Attack action" (contract §4.2).
/// </summary>
internal enum AttackKind
{
    /// <summary>An attack of the Attack action.</summary>
    Action,

    /// <summary>An attack whose own action is the Bonus Action (an offhand attack, Spiritual Weapon).</summary>
    BonusAction,

    /// <summary>A second Attack action's attack (Action Surge): part of the Attack action.</summary>
    Surge,

    /// <summary>A Bonus Action extra attack (Hew, 2014 GWM): not part of the Attack action.</summary>
    Hew,

    /// <summary>Cleave's attack against a second creature: never changes the main target's state.</summary>
    Cleave,

    /// <summary>An attack on another creature's turn: fresh once-per-turn riders, no resources, none of this turn's state.</summary>
    Reaction,
}

internal static class AttackKindRules
{
    /// <summary>Against the main target: consumes and grants Vex, triggers Topple, Cleave and conditions on hit.</summary>
    public static bool IsMainTarget(this AttackKind kind) => kind is not (AttackKind.Cleave or AttackKind.Reaction);

    /// <summary>
    /// Sees the target's starting condition. Cleave's second creature does not have it (the target spec describes one
    /// creature); a reaction is against the same creature at some other point of the round, still in its condition.
    /// </summary>
    public static bool SeesInitialCondition(this AttackKind kind) => kind != AttackKind.Cleave;

    /// <summary>Sees conditions imposed this turn (Topple, Stunning Strike): only this turn's attacks on the main target.</summary>
    public static bool SeesAppliedConditions(this AttackKind kind) => kind.IsMainTarget();

    /// <summary>Uses the turn's advantage-rate sample: everything but the reaction, which happens on another turn.</summary>
    public static bool UsesAdvantageSources(this AttackKind kind) => kind != AttackKind.Reaction;

    public static string Use(this AttackKind kind) => kind switch
    {
        AttackKind.Action => AttackUses.AttackAction,
        AttackKind.BonusAction => AttackUses.BonusAction,
        AttackKind.Surge => AttackUses.ActionSurge,
        AttackKind.Hew => AttackUses.BonusActionExtra,
        AttackKind.Cleave => AttackUses.Cleave,
        AttackKind.Reaction => AttackUses.Reaction,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not an attack kind."),
    };
}

/// <summary>One attack made one way: the unit the turn's queues hold and the results break down.</summary>
internal sealed class AttackLine
{
    public required int Index { get; init; }

    public required int AttackIndex { get; init; }

    public required ResolvedAttack Attack { get; init; }

    public required AttackKind Kind { get; init; }

    /// <summary>The extra_attack that grants it (Surge, Hew, Reaction), or −1.</summary>
    public required int ExtraIndex { get; init; }

    /// <summary>
    /// attack_action_only FLAT parts (2024 GWM's +PB) count: the Attack action and Action Surge; Cleave under ruling
    /// cleave_part_of_attack_action; Hew under ruling hew_gets_pb (which is about the flat bonus only).
    /// </summary>
    public required bool FlatAttackAction { get; init; }

    /// <summary>attack_action_only RIDERS apply: the Attack action and Action Surge; Cleave under its ruling. Never Hew.</summary>
    public required bool RiderAttackAction { get; init; }

    /// <summary>The AC it rolls against: the target's, plus cover unless the attack ignores cover.</summary>
    public required int ArmorClass { get; init; }

    /// <summary>The attack roll's bonus dice (Bless, Bane) convolved, or null.</summary>
    public required Pmf<double>? BonusDice { get; init; }

    /// <summary>Advantage sources (bits over the build's advantage list) that apply to it, by direction.</summary>
    public required ulong AdvantageSources { get; init; }

    public required ulong DisadvantageSources { get; init; }

    /// <summary>Power attacks (bits over the build's list) whose filter includes it.</summary>
    public required int PowerAttacks { get; init; }

    /// <summary>Riders (bits over the build's rider list) that apply to it: the filter, and attack_action_only.</summary>
    public required ulong Riders { get; init; }

    /// <summary>Whether a crit with it is "a crit with a melee weapon" (the 2014 GWM bonus attack's and Hew's trigger).</summary>
    public bool IsMeleeWeapon => Attack.IsMelee && Attack.IsWeapon;
}

internal enum HitOptionKind
{
    Rider,
    SavageAttacker,
    Condition,
}

/// <summary>
/// A choice the turn makes on a hit: spend an optional rider, use a damage reroll, impose a condition. Each has a
/// policy (<see cref="DslValues.Policies"/>); the fixed ones are applied by rule and the optimal ones by backward
/// induction on E[damage] − use_value × uses.
/// </summary>
internal sealed class HitOption
{
    public required int Index { get; init; }

    public required HitOptionKind Kind { get; init; }

    /// <summary>Index into the build's riders, damage rerolls or conditions on hit.</summary>
    public required int SourceIndex { get; init; }

    public required string Policy { get; init; }

    /// <summary>λ: the damage one use is worth elsewhere; charged against the objective when spent.</summary>
    public required double UseValue { get; init; }

    /// <summary>The resource slot it spends, or −1.</summary>
    public required int ResourceSlot { get; init; }

    public required bool BonusActionCost { get; init; }

    /// <summary>The spent-this-turn bit when it is once per turn, or −1.</summary>
    public required int OnceBit { get; init; }

    /// <summary>For a condition: the condition bit it imposes.</summary>
    public required int ConditionBit { get; init; }

    /// <summary>Per line: whether it can apply to an attack made that way.</summary>
    public required bool[] AppliesTo { get; init; }
}

/// <summary>A resource-limited modifier's uses left, tracked through the turn and carried between rounds.</summary>
/// <param name="Unlimited">Treated as never running out (<see cref="DprOptions.UnlimitedResources"/>).</param>
internal sealed record ResourceSlot(ModifierRef Source, ResolvedResource Resource, bool Unlimited);

internal enum SegmentKind
{
    /// <summary>The turn's start: setup costs (first round), the Action's choice, Action Surge.</summary>
    Start,

    /// <summary>A queue of attacks, then <see cref="Segment.Next"/>.</summary>
    Attacks,

    /// <summary>One save effect cast, then <see cref="Segment.Next"/>.</summary>
    Save,

    /// <summary>The Bonus Action's decision.</summary>
    BonusActionPhase,

    /// <summary>The turn is over.</summary>
    End,
}

/// <summary>A stretch of the turn: an attack queue (line indices), one save effect, the Bonus Action's choice, the start or the end.</summary>
/// <param name="SaveIndex">For <see cref="SegmentKind.Save"/>: the save effect, else −1.</param>
/// <param name="Next">The segment that follows (−1 for the start and the end, whose successors are decided elsewhere).</param>
internal sealed record Segment(int Id, SegmentKind Kind, int[] Queue, int SaveIndex, int Next);

internal enum BonusActionOptionKind
{
    Attacks,
    ExtraAttack,
    SaveEffect,
}

/// <summary>A use of the Bonus Action the turn can choose, and the segment that carries it out.</summary>
internal sealed record BonusActionOption(int Index, string Label, BonusActionOptionKind Kind, int Segment, int ExtraIndex, int SaveIndex, int ResourceSlot);

/// <summary>
/// Everything about a turn that does not change from state to state: the attack lines, the queues, the optional hit
/// choices, the resource slots, the Bonus Action options. Built once per evaluation and shared by every advantage
/// sample and power-attack choice, so their memo tables speak the same state vocabulary.
///
/// <para>
/// <b>The turn's order</b> (contract §4.2): setup costs (first round), then the Action — the Action save effect if the
/// build has one, otherwise the Attack action's attacks in list order, each <c>count</c> times, followed by every
/// available Action Surge's attacks — then the Bonus Action's choice, then any save effect that costs no action, then
/// the end. The reaction is a separate mini-turn of its own (<see cref="ReactionSegments"/>).
/// </para>
/// </summary>
internal sealed class TurnPlan
{
    public const int ProneBit = 0;
    public const int RestrainedBit = 1;
    public const int BlindedBit = 2;
    public const int StunnedBit = 3;
    public const int ParalyzedBit = 4;
    public const int UnconsciousBit = 5;
    public const int DodgingBit = 6;

    /// <summary>
    /// The conditions that include Incapacitated: stunned, paralyzed, unconscious (both editions' condition text). They
    /// auto-fail Str and Dex saves, end Dodge's benefits, and (2024) switch off Evasion: "You don't benefit from this
    /// feature if you have the Incapacitated condition."
    /// </summary>
    public const int IncapacitatedConditions = (1 << StunnedBit) | (1 << ParalyzedBit) | (1 << UnconsciousBit);

    /// <summary>The spent bit that marks Cleave used this turn (options use the bits below it).</summary>
    public const int CleaveBit = 62;

    private static readonly string[] ConditionNames =
        [V.Conditions.Prone, V.Conditions.Restrained, V.Conditions.Blinded, V.Conditions.Stunned, V.Conditions.Paralyzed, V.Conditions.Unconscious, V.Conditions.Dodging];

    private readonly List<AttackLine> _lines = [];
    private readonly Dictionary<(int Attack, AttackKind Kind, int Extra), AttackLine> _lineIndex = [];
    private readonly List<Segment> _segments = [];
    private readonly Dictionary<(bool Action, int Surges), int> _actionSegments = [];
    private readonly Dictionary<int, Pmf<double>?> _bonusDice = [];

    public TurnPlan(ResolvedBuild build, ResolvedTarget target, DprOptions options, WorkMeter meter)
    {
        Build = build;
        Target = target;
        Rulings = build.Rulings;
        Meter = meter;

        var removed = options.RemovedModifiers.ToHashSet();
        CheckRemovable(build, removed);
        bool Kept(ModifierRef source) => !removed.Contains(source.Number);

        Riders = build.Riders.Where(r => Kept(r.Source)).ToList();
        ExtraAttacks = build.ExtraAttacks.Where(e => Kept(e.Source)).ToList();
        SaveEffects = build.SaveEffects.Where(s => Kept(s.Source)).ToList();
        ConditionsOnHit = build.ConditionsOnHit.Where(c => Kept(c.Source)).ToList();
        AdvantageSources = build.AdvantageSources.Where(a => Kept(a.Source)).ToList();
        PowerAttacks = build.PowerAttacks.Where(p => Kept(p.Source)).ToList();
        DamageRerolls = build.DamageRerolls.Where(d => Kept(d.Source)).ToList();
        SetupCosts = build.SetupCosts.Where(s => Kept(s.Source)).ToList();

        var unlimited = options.UnlimitedResources.ToHashSet();
        var slots = new List<ResourceSlot>();
        int Slot(ModifierRef source, ResolvedResource? resource)
        {
            if (resource is null)
            {
                return -1;
            }

            slots.Add(new ResourceSlot(source, resource, unlimited.Contains(source.Number)));
            return slots.Count - 1;
        }

        RiderSlots = Riders.Select(r => Slot(r.Source, r.Resource)).ToArray();
        ExtraSlots = ExtraAttacks.Select(e => Slot(e.Source, e.Resource)).ToArray();
        SaveSlots = SaveEffects.Select(s => Slot(s.Source, s.Resource)).ToArray();
        ConditionSlots = ConditionsOnHit.Select(c => Slot(c.Source, c.Resource)).ToArray();
        Resources = slots;
        if (slots.Count > TurnState.MaxResourceSlots)
        {
            throw new DndInputException(
                $"this build has {slots.Count} resource-limited modifiers active at level {DslText.Number(build.Level)}; the engine tracks at most " +
                $"{TurnState.MaxResourceSlots}. Give the others no resource (always available) or evaluate them in separate builds.");
        }

        InitialConditions = InitialConditionBits(target);
        CreateLines();
        Options = CreateOptions();
        if (Options.Count >= CleaveBit)
        {
            throw new InvalidOperationException("More optional hit choices than the spent-bit mask holds; the DSL limits prevent this.");
        }

        CreateSegments();
        TracksSap = build.Attacks.Any(a => a.Mastery == V.Masteries.Sap);
        ToppleBlocked = target.IsImmuneToCondition(V.Conditions.Prone);

        // A condition the target is immune to never lands, so there is nothing to track (its report says "immune").
        TrackedSaveConditions = SaveEffects.Select((s, i) => (s, i))
            .Where(p => p.s.Condition is { } condition && !target.IsImmuneToCondition(condition))
            .Select(p => p.i)
            .ToArray();
        SaveLandedBits = new int[SaveEffects.Count];
        Array.Fill(SaveLandedBits, -1);
        for (var i = 0; i < TrackedSaveConditions.Length; i++)
        {
            SaveLandedBits[TrackedSaveConditions[i]] = i;
        }
    }

    public ResolvedBuild Build { get; }

    public ResolvedTarget Target { get; }

    public ResolvedRulings Rulings { get; }

    public WorkMeter Meter { get; }

    public IReadOnlyList<ResolvedRider> Riders { get; }

    public IReadOnlyList<ResolvedExtraAttack> ExtraAttacks { get; }

    public IReadOnlyList<ResolvedSaveEffect> SaveEffects { get; }

    public IReadOnlyList<ResolvedConditionOnHit> ConditionsOnHit { get; }

    public IReadOnlyList<ResolvedAdvantageSource> AdvantageSources { get; }

    public IReadOnlyList<ResolvedPowerAttack> PowerAttacks { get; }

    public IReadOnlyList<ResolvedDamageReroll> DamageRerolls { get; }

    public IReadOnlyList<ResolvedSetupCost> SetupCosts { get; }

    public IReadOnlyList<ResourceSlot> Resources { get; }

    public int[] RiderSlots { get; }

    public int[] ExtraSlots { get; }

    public int[] SaveSlots { get; }

    public int[] ConditionSlots { get; }

    public IReadOnlyList<AttackLine> Lines => _lines;

    public IReadOnlyList<HitOption> Options { get; }

    public IReadOnlyList<Segment> Segments => _segments;

    public IReadOnlyList<BonusActionOption> BonusActionOptions { get; private set; } = [];

    /// <summary>The reaction extra attacks' segments, by extra-attack index (−1 for other kinds).</summary>
    public int[] ReactionSegments { get; private set; } = [];

    public int StartSegment { get; private set; }

    public int BonusActionSegment { get; private set; }

    public int EndSegment { get; private set; }

    /// <summary>The save effect the Action casts instead of attacking, or −1 (a build is one turn's routine).</summary>
    public int ActionSaveIndex { get; private set; } = -1;

    /// <summary>The target's starting condition as condition bits (Unconscious includes Prone).</summary>
    public int InitialConditions { get; }

    /// <summary>Whether the build has a Sap weapon, whose P(≥ 1 hit) per turn is reported.</summary>
    public bool TracksSap { get; }

    /// <summary>
    /// The target is immune to Prone (a stat block's condition immunity), so Topple forces no save and never knocks it
    /// down: "no attempt", as a condition_on_hit it is immune to is never attempted (<see cref="CreateOptions"/>).
    /// </summary>
    public bool ToppleBlocked { get; }

    /// <summary>Save effects with a condition, whose landing on the first target is tracked for "P(lands)".</summary>
    public int[] TrackedSaveConditions { get; }

    /// <summary>Per save effect: its landed bit, or −1 when it has no condition.</summary>
    public int[] SaveLandedBits { get; }

    /// <summary>The extra attacks with action "action" (Action Surge), by extra-attack index.</summary>
    public IReadOnlyList<int> SurgeExtras { get; private set; } = [];

    /// <summary>The Action's save-effect segment, or −1.</summary>
    public int ActionSaveSegment { get; private set; } = -1;

    /// <summary>
    /// A bonus_action attack is offhand (the Light weapon's extra attack), so the turn tracks whether the Attack action was
    /// taken (<see cref="TurnState.AttackActionFlag"/>): 2014 "When you take the Attack action and attack with a light
    /// melee weapon …", 2024 Light "When you take the Attack action on your turn and attack with a Light weapon".
    /// </summary>
    public bool GatesOffhand { get; private set; }

    /// <summary>
    /// The bonus_action attacks without the offhand ones, for a turn without the Attack action (Spiritual Weapon still
    /// swings), or −1 when every bonus_action attack is offhand.
    /// </summary>
    public int NonOffhandBonusAttackSegment { get; private set; } = -1;

    /// <summary>The Attack action's own queue holds a weapon attack (not only spell attacks, which use the casting action).</summary>
    public bool ActionMakesWeaponAttacks { get; private set; }

    /// <summary>Per <see cref="SurgeExtras"/> entry: its attacks are weapon attacks, so the surge is a second Attack action.</summary>
    public IReadOnlyList<bool> SurgeIsAttackAction { get; private set; } = [];

    /// <summary>The condition bit of a mechanical condition (or dodging).</summary>
    public static int ConditionBit(string condition)
    {
        var index = Array.IndexOf(ConditionNames, condition);
        return index >= 0 ? index : throw new ArgumentOutOfRangeException(nameof(condition), condition, "Not a mechanical condition.");
    }

    /// <summary>
    /// The conditions a creature effectively has. Unconscious includes Prone (both editions: an unconscious creature
    /// "falls prone" / "has the Incapacitated and Prone conditions"), which matters for a ranged attack (Advantage from
    /// Unconscious and Disadvantage from Prone cancel) and for Topple (already prone). Dodging ends once the creature is
    /// Incapacitated or its Speed is 0 (restrained): 2014 "You lose this benefit if you are incapacitated … or if your
    /// speed drops to 0", 2024 "You lose these benefits if you have the Incapacitated condition or if your Speed is 0".
    /// A blinded dodger keeps Dodge but cannot see its attacker, which only the attack roll reads (<see cref="TurnEvaluator"/>).
    /// </summary>
    public static int Effective(int conditions)
    {
        if ((conditions & (1 << UnconsciousBit)) != 0)
        {
            conditions |= 1 << ProneBit;
        }

        const int losesDodge = IncapacitatedConditions | (1 << RestrainedBit);
        return (conditions & losesDodge) != 0 ? conditions & ~(1 << DodgingBit) : conditions;
    }

    public AttackLine Line(int attackIndex, AttackKind kind, int extraIndex = -1) => _lineIndex[(attackIndex, kind, extraIndex)];

    /// <summary>The Action segment for a turn: with or without the Attack action's own attacks, plus the Action Surges in the mask.</summary>
    public int ActionSegment(bool actionAvailable, int surgeMask) => _actionSegments[(actionAvailable, surgeMask)];

    private static void CheckRemovable(ResolvedBuild build, HashSet<int> removed)
    {
        if (removed.Count == 0)
        {
            return;
        }

        var removable = build.Riders.Select(r => r.Source.Number)
            .Concat(build.ExtraAttacks.Select(e => e.Source.Number))
            .Concat(build.SaveEffects.Select(s => s.Source.Number))
            .Concat(build.ConditionsOnHit.Select(c => c.Source.Number))
            .Concat(build.AdvantageSources.Select(a => a.Source.Number))
            .Concat(build.PowerAttacks.Select(p => p.Source.Number))
            .Concat(build.DamageRerolls.Select(d => d.Source.Number))
            .Concat(build.Defensive.Select(d => d.Source.Number))
            .ToHashSet();
        var bad = removed.Where(n => !removable.Contains(n)).Order().ToList();
        if (bad.Count > 0)
        {
            throw new ArgumentException(
                $"Modifiers {string.Join(", ", bad)} cannot be removed from an evaluation: they are not active per-turn modifiers of this build.",
                nameof(DprOptions.RemovedModifiers));
        }
    }

    private static int InitialConditionBits(ResolvedTarget target) =>
        target.Condition is { } condition ? Effective(1 << ConditionBit(condition)) : 0;

    private void CreateLines()
    {
        var attacks = Build.Attacks;
        for (var a = 0; a < attacks.Count; a++)
        {
            AddLine(a, attacks[a].Action == V.AttackActions.Action ? AttackKind.Action : AttackKind.BonusAction, -1);
            if (attacks[a].Mastery == V.Masteries.Cleave && attacks[a].IsMelee)
            {
                AddLine(a, AttackKind.Cleave, -1);
            }
        }

        for (var e = 0; e < ExtraAttacks.Count; e++)
        {
            var extra = ExtraAttacks[e];
            var attack = IndexOfAttack(extra.Attack);
            var kind = extra.Action switch
            {
                V.ExtraAttackActions.Action => AttackKind.Surge,
                V.ExtraAttackActions.BonusAction => AttackKind.Hew,
                V.ExtraAttackActions.Reaction => AttackKind.Reaction,
                _ => throw new InvalidOperationException($"Unknown extra attack action \"{extra.Action}\"."),
            };
            AddLine(attack, kind, e);
        }
    }

    private int IndexOfAttack(string name)
    {
        for (var a = 0; a < Build.Attacks.Count; a++)
        {
            if (Build.Attacks[a].Name == name)
            {
                return a;
            }
        }

        throw new InvalidOperationException($"Extra attack names \"{name}\", which is not active; the resolver validates this.");
    }

    private void AddLine(int attackIndex, AttackKind kind, int extraIndex)
    {
        // The Action's attacks and Action Surge's are part of the Attack action when they are weapon attacks; a spell
        // attack made with the Action is the spell's casting action (ResolvedAttack.IsPartOfAttackAction).
        var attack = Build.Attacks[attackIndex];
        var flatAttackAction = kind switch
        {
            AttackKind.Action or AttackKind.Surge => attack.IsWeapon,
            AttackKind.Cleave => Rulings.CleavePartOfAttackAction,
            AttackKind.Hew => Rulings.HewGetsPb,
            _ => false,
        };
        var riderAttackAction = kind switch
        {
            AttackKind.Action or AttackKind.Surge => attack.IsWeapon,
            AttackKind.Cleave => Rulings.CleavePartOfAttackAction,
            _ => false,
        };

        ulong advantage = 0, disadvantage = 0;
        for (var i = 0; i < AdvantageSources.Count; i++)
        {
            if (AdvantageSources[i].AppliesTo(attack))
            {
                if (AdvantageSources[i].Mode == V.AdvantageModes.Advantage)
                {
                    advantage |= 1UL << i;
                }
                else
                {
                    disadvantage |= 1UL << i;
                }
            }
        }

        var powerAttacks = 0;
        for (var i = 0; i < PowerAttacks.Count; i++)
        {
            if (PowerAttacks[i].AppliesTo(attack))
            {
                powerAttacks |= 1 << i;
            }
        }

        ulong riders = 0;
        for (var i = 0; i < Riders.Count; i++)
        {
            if (Riders[i].AppliesTo(attack) && (!Riders[i].AttackActionOnly || riderAttackAction))
            {
                riders |= 1UL << i;
            }
        }

        var line = new AttackLine
        {
            Index = _lines.Count,
            AttackIndex = attackIndex,
            Attack = attack,
            Kind = kind,
            ExtraIndex = extraIndex,
            FlatAttackAction = flatAttackAction,
            RiderAttackAction = riderAttackAction,
            ArmorClass = Target.ArmorClass + (attack.IgnoresCover ? 0 : Target.CoverBonus),
            BonusDice = AttackBonusDice(attackIndex, attack),
            AdvantageSources = advantage,
            DisadvantageSources = disadvantage,
            PowerAttacks = powerAttacks,
            Riders = riders,
        };
        _lines.Add(line);
        _lineIndex[(attackIndex, kind, extraIndex)] = line;
    }

    private Pmf<double>? AttackBonusDice(int attackIndex, ResolvedAttack attack)
    {
        if (_bonusDice.TryGetValue(attackIndex, out var cached))
        {
            return cached;
        }

        Pmf<double>? dice = null;
        foreach (var named in attack.ToHitDice)
        {
            var one = DamageDice.Sum(named.Dice.Dice, Meter);
            dice = dice is null ? one : dice.Convolve(one, Meter);
        }

        _bonusDice[attackIndex] = dice;
        return dice;
    }

    private List<HitOption> CreateOptions()
    {
        var options = new List<HitOption>();
        var onceBits = 0;
        bool[] Applies(Func<AttackLine, bool> rule) => _lines.Select(rule).ToArray();

        for (var r = 0; r < Riders.Count; r++)
        {
            var rider = Riders[r];
            if (!rider.IsOptional)
            {
                continue;
            }

            var bit = 1UL << r;
            options.Add(new HitOption
            {
                Index = options.Count,
                Kind = HitOptionKind.Rider,
                SourceIndex = r,
                Policy = rider.Policy,
                UseValue = rider.Policy == V.Policies.Optimal ? rider.UseValue : 0,
                ResourceSlot = RiderSlots[r],
                BonusActionCost = rider.ActionCost == V.ActionCosts.BonusAction,
                OnceBit = rider.When == V.When.FirstHitPerTurn ? onceBits++ : -1,
                ConditionBit = -1,
                AppliesTo = Applies(line => (line.Riders & bit) != 0),
            });
        }

        for (var s = 0; s < DamageRerolls.Count; s++)
        {
            var reroll = DamageRerolls[s];
            options.Add(new HitOption
            {
                Index = options.Count,
                Kind = HitOptionKind.SavageAttacker,
                SourceIndex = s,
                Policy = reroll.Policy,
                UseValue = reroll.Policy == V.Policies.Optimal ? reroll.UseValue : 0,
                ResourceSlot = -1,
                BonusActionCost = false,
                OnceBit = onceBits++, // "Once per turn when you hit a target with a weapon"
                ConditionBit = -1,
                AppliesTo = Applies(line => reroll.AppliesTo(line.Attack)),
            });
        }

        for (var c = 0; c < ConditionsOnHit.Count; c++)
        {
            // A condition the target is immune to is never attempted: it can never land, so spending a use (a ki point) on
            // it is never the better choice, and no policy should be able to spend one. The option applies to no attack,
            // which leaves its report at zero attempts, and the result says why.
            var condition = ConditionsOnHit[c];
            var immune = Target.IsImmuneToCondition(condition.Condition);
            options.Add(new HitOption
            {
                Index = options.Count,
                Kind = HitOptionKind.Condition,
                SourceIndex = c,
                Policy = condition.Policy,
                UseValue = condition.Policy == V.Policies.Optimal ? condition.UseValue : 0,
                ResourceSlot = ConditionSlots[c],
                BonusActionCost = false,
                OnceBit = condition.When == V.When.FirstHitPerTurn ? onceBits++ : -1,
                ConditionBit = ConditionBit(condition.Condition),
                AppliesTo = Applies(line => !immune && condition.AppliesTo(line.Attack) && line.Kind.IsMainTarget()),
            });
        }

        return options;
    }

    private int AddSegment(SegmentKind kind, int[] queue, int saveIndex, int next)
    {
        var segment = new Segment(_segments.Count, kind, queue, saveIndex, next);
        _segments.Add(segment);
        return segment.Id;
    }

    private void CreateSegments()
    {
        EndSegment = AddSegment(SegmentKind.End, [], -1, -1);

        // Save effects that cost no action happen every turn after the Bonus Action, while their uses last. Nothing in
        // the turn depends on them (a save effect's condition changes no later attack, contract §4.3), so where they sit
        // changes no number; last keeps the Action and the Bonus Action decisions free of them.
        var afterBonusAction = EndSegment;
        for (var s = SaveEffects.Count - 1; s >= 0; s--)
        {
            if (SaveEffects[s].ActionCost == V.ActionCosts.None)
            {
                afterBonusAction = AddSegment(SegmentKind.Save, [], s, afterBonusAction);
            }
        }

        BonusActionSegment = AddSegment(SegmentKind.BonusActionPhase, [], -1, afterBonusAction);
        StartSegment = AddSegment(SegmentKind.Start, [], -1, -1);

        // The Action: a save effect, or the Attack action's attacks in list order, each `count` times.
        ActionSaveIndex = -1;
        for (var s = 0; s < SaveEffects.Count; s++)
        {
            if (SaveEffects[s].ActionCost == V.ActionCosts.Action)
            {
                ActionSaveIndex = s;
                break;
            }
        }

        if (ActionSaveIndex >= 0)
        {
            ActionSaveSegment = AddSegment(SegmentKind.Save, [], ActionSaveIndex, BonusActionSegment);
        }

        var actionQueue = new List<int>();
        for (var a = 0; a < Build.Attacks.Count; a++)
        {
            if (Build.Attacks[a].Action == V.AttackActions.Action)
            {
                actionQueue.AddRange(Enumerable.Repeat(Line(a, AttackKind.Action).Index, Build.Attacks[a].Count));
            }
        }

        var surges = new List<int>();
        for (var e = 0; e < ExtraAttacks.Count; e++)
        {
            if (ExtraAttacks[e].Action == V.ExtraAttackActions.Action)
            {
                surges.Add(e);
            }
        }

        SurgeExtras = surges;
        ActionMakesWeaponAttacks = actionQueue.Any(l => _lines[l].Attack.IsPartOfAttackAction);
        SurgeIsAttackAction = surges.Select(e => Build.Attacks[IndexOfAttack(ExtraAttacks[e].Attack)].IsWeapon).ToList();
        foreach (var actionAvailable in new[] { true, false })
        {
            for (var mask = 0; mask < 1 << surges.Count; mask++)
            {
                var queue = new List<int>();
                if (actionAvailable)
                {
                    queue.AddRange(actionQueue);
                }

                for (var i = 0; i < surges.Count; i++)
                {
                    if ((mask & (1 << i)) != 0)
                    {
                        var extra = ExtraAttacks[surges[i]];
                        queue.AddRange(Enumerable.Repeat(Line(IndexOfAttack(extra.Attack), AttackKind.Surge, surges[i]).Index, extra.Count));
                    }
                }

                _actionSegments[(actionAvailable, mask)] = AddSegment(SegmentKind.Attacks, queue.ToArray(), -1, BonusActionSegment);
            }
        }

        // The Bonus Action's options, in the order a tie goes to: the bonus_action attacks together, each bonus_action
        // extra attack, each bonus_action save effect (and, implicitly last, nothing).
        var options = new List<BonusActionOption>();
        var baQueue = new List<int>();
        for (var a = 0; a < Build.Attacks.Count; a++)
        {
            if (Build.Attacks[a].Action == V.AttackActions.BonusAction)
            {
                baQueue.AddRange(Enumerable.Repeat(Line(a, AttackKind.BonusAction).Index, Build.Attacks[a].Count));
            }
        }

        if (baQueue.Count > 0)
        {
            var names = Build.Attacks.Where(a => a.Action == V.AttackActions.BonusAction).Select(a => a.Name);
            var all = AddSegment(SegmentKind.Attacks, baQueue.ToArray(), -1, afterBonusAction);
            options.Add(new BonusActionOption(
                options.Count, $"bonus_action attacks ({string.Join(", ", names)})", BonusActionOptionKind.Attacks, all, -1, -1, -1));

            var withoutOffhand = baQueue.Where(l => !_lines[l].Attack.Offhand).ToArray();
            GatesOffhand = withoutOffhand.Length < baQueue.Count;
            NonOffhandBonusAttackSegment = !GatesOffhand ? all
                : withoutOffhand.Length > 0 ? AddSegment(SegmentKind.Attacks, withoutOffhand, -1, afterBonusAction)
                : -1;
        }

        var reactions = new int[ExtraAttacks.Count];
        Array.Fill(reactions, -1);
        for (var e = 0; e < ExtraAttacks.Count; e++)
        {
            var extra = ExtraAttacks[e];
            var attack = IndexOfAttack(extra.Attack);
            if (extra.Action == V.ExtraAttackActions.BonusAction)
            {
                var queue = Enumerable.Repeat(Line(attack, AttackKind.Hew, e).Index, extra.Count).ToArray();
                options.Add(new BonusActionOption(
                    options.Count, extra.Source.Label, BonusActionOptionKind.ExtraAttack, AddSegment(SegmentKind.Attacks, queue, -1, afterBonusAction),
                    e, -1, ExtraSlots[e]));
            }
            else if (extra.Action == V.ExtraAttackActions.Reaction)
            {
                var queue = Enumerable.Repeat(Line(attack, AttackKind.Reaction, e).Index, extra.Count).ToArray();
                reactions[e] = AddSegment(SegmentKind.Attacks, queue, -1, EndSegment);
            }
        }

        for (var s = 0; s < SaveEffects.Count; s++)
        {
            if (SaveEffects[s].ActionCost == V.ActionCosts.BonusAction)
            {
                options.Add(new BonusActionOption(
                    options.Count, SaveEffects[s].Source.Label, BonusActionOptionKind.SaveEffect, AddSegment(SegmentKind.Save, [], s, afterBonusAction),
                    -1, s, SaveSlots[s]));
            }
        }

        BonusActionOptions = options;
        ReactionSegments = reactions;
    }
}
