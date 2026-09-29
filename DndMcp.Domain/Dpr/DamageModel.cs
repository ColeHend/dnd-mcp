using DndMcp.Domain.Dice;
using DndMcp.Domain.Features;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Dpr;

/// <summary>A damage distribution with the two numbers the expected-value pass reads from it.</summary>
internal sealed class DamageSummary
{
    /// <summary>
    /// How close to 0 or 1 <see cref="ZeroChance"/> may come before it IS 0 or 1. A double PMF's weights sum to 1 only up
    /// to rounding: a hit whose every type the target is immune to reads P(0) = 0.9999999999999999, and that opened a
    /// "the hit dealt damage" Vex branch of probability 1e-16 with no damage in it to condition on, so balance_dpr failed
    /// with the SDK's bare error instead of answering 0. A real chance this close to certain moves no reported digit.
    /// </summary>
    internal const double CertaintyTolerance = 1e-12;

    public DamageSummary(Pmf<double> pmf)
    {
        Pmf = pmf;
        Mean = DamageDice.Mean(pmf);
        var zero = DamageDice.ProbabilityOf(pmf, 0);
        ZeroChance = zero < CertaintyTolerance ? 0 : zero > 1 - CertaintyTolerance ? 1 : zero;
    }

    public Pmf<double> Pmf { get; }

    public double Mean { get; }

    /// <summary>
    /// P(no damage): a Vex hit that deals none grants no Advantage. Exactly 0 or 1 within <see cref="CertaintyTolerance"/>,
    /// so the Vex split (which compares it with 0 and 1) opens two branches only when both outcomes can happen.
    /// </summary>
    public double ZeroChance { get; }

    public static DamageSummary None { get; } = new(Pmf<double>.Point(0));
}

/// <summary>
/// The damage of one hit, one miss or one save effect, as an adjusted integer PMF (contract §4.1, research A2 and A7),
/// cached per distinct case: a turn asks for the same hit (same attack line, crit or not, same riders, Savage Attacker,
/// power attack) from many states.
///
/// <para>
/// <b>A hit</b>: the weapon's dice (remapped per die by Great Weapon Fighting and Elemental Adept), rolled twice on a
/// crit; Savage Attacker takes the better of two rolls of those dice (on a crit, of one set plus the extra set rolled once
/// — or, under ruling savage_attacker_on_crit_dice, of the whole doubled set); flat parts once (the formula's flat part,
/// the ability modifier except on Cleave when positive, attack_action_only parts only as part of the Attack action,
/// Dueling, a power attack's bonus); every rider in the mask with its dice doubled on a crit when crit_doubles, on_crit
/// dice added once, rider dice remapped by GWF only under ruling gwf_on_riders. Then per damage type: the type's dice and
/// flats summed, adjusted (<see cref="DamageAdjustment"/>), and the types convolved.
/// </para>
/// <para>
/// <b>A rider with no type deals the attack's type</b> (settled; 2024 Sneak Attack: "The extra damage's type is the same
/// as the weapon's type"), decided here per attack line rather than in the resolver: one rider may land with a piercing
/// and a fire attack, and the hit and miss caches are keyed by line, so each gets its own. Elemental Adept then sees the
/// inherited type too. A save effect's missing type stays typeless (<see cref="SaveRoll"/>).
/// </para>
/// <para>
/// <b>A miss</b>: Graze (the attack's ability modifier when positive, the weapon's type) and on_miss riders, as one
/// instance of damage per type.
/// </para>
/// <para>
/// <b>A hit, a miss and every rider on them carry the ATTACK's properties</b>
/// (<see cref="DamageProperties.Of(ResolvedAttack)"/>) into a qualified adjustment: a werewolf resists a mundane
/// longsword's slashing and the untyped Hunter's Mark riding on it, and neither when the sword is magical or silvered. A
/// radiant smite on that sword is radiant, which the werewolf's B/P/S qualifier never covers anyway. The caches stay keyed
/// by line, since a line is one attack and so one set of properties.
/// </para>
/// </summary>
internal sealed class DamageModel
{
    private readonly TurnPlan _plan;
    private readonly WorkMeter _meter;
    private readonly Dictionary<(int Line, bool Crit, ulong Riders, bool Savage, int PowerBonus), DamageSummary> _hits = [];
    private readonly Dictionary<(int Line, ulong Riders), DamageSummary> _misses = [];
    private readonly Dictionary<(IReadOnlyList<DiceTerm> Dice, string? Remap, bool ElementalAdept, int Times), Pmf<double>> _dice = [];

    public DamageModel(TurnPlan plan)
    {
        _plan = plan;
        _meter = plan.Meter;
    }

    /// <summary>The riders that apply to every hit of the line (every_hit riders that are not optional; on_crit riders on a crit).</summary>
    public ulong MandatoryRiders(AttackLine line, bool crit)
    {
        ulong mask = 0;
        for (var r = 0; r < _plan.Riders.Count; r++)
        {
            var bit = 1UL << r;
            if ((line.Riders & bit) == 0)
            {
                continue;
            }

            var rider = _plan.Riders[r];
            if ((rider.When == V.When.EveryHit && !rider.IsOptional) || (rider.When == V.When.OnCrit && crit))
            {
                mask |= bit;
            }
        }

        return mask;
    }

    /// <summary>The on_miss riders that apply to the line.</summary>
    public ulong MissRiders(AttackLine line)
    {
        ulong mask = 0;
        for (var r = 0; r < _plan.Riders.Count; r++)
        {
            if ((line.Riders & (1UL << r)) != 0 && _plan.Riders[r].When == V.When.OnMiss)
            {
                mask |= 1UL << r;
            }
        }

        return mask;
    }

    public DamageSummary Hit(AttackLine line, bool crit, ulong riders, bool savage, int powerBonus)
    {
        var key = (line.Index, crit, riders, savage, powerBonus);
        if (_hits.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var attack = line.Attack;
        var groups = new TypeGroups();

        var ownType = attack.DamageType;
        var ownElementalAdept = ownType is not null && attack.ElementalAdeptTypes.Contains(ownType);
        var one = Dice(attack.Damage.Dice, attack.WeaponRemap, ownElementalAdept, 1);
        Pmf<double> weapon;
        if (savage)
        {
            weapon = !crit ? DamageDice.MaxOfTwo(one)
                : _plan.Rulings.SavageAttackerOnCritDice ? DamageDice.MaxOfTwo(Dice(attack.Damage.Dice, attack.WeaponRemap, ownElementalAdept, 2))
                : DamageDice.MaxOfTwo(one).Convolve(one, _meter);
        }
        else
        {
            weapon = crit ? Dice(attack.Damage.Dice, attack.WeaponRemap, ownElementalAdept, 2) : one;
        }

        groups.Add(ownType, weapon, _meter);
        groups.AddFlat(ownType, FlatDamage(line) + powerBonus);

        for (var r = 0; r < _plan.Riders.Count; r++)
        {
            if ((riders & (1UL << r)) == 0)
            {
                continue;
            }

            var rider = _plan.Riders[r];
            var type = rider.DamageType ?? attack.DamageType;
            var times = crit && rider.CritDoubles ? 2 : 1;
            var remap = _plan.Rulings.GwfOnRiders ? attack.WeaponRemap : null;
            var elementalAdept = type is not null && attack.ElementalAdeptTypes.Contains(type);
            if (rider.Damage.HasDice)
            {
                groups.Add(type, Dice(rider.Damage.Dice, remap, elementalAdept, times), _meter);
            }

            groups.AddFlat(type, rider.Damage.Flat);
        }

        var summary = new DamageSummary(groups.Adjusted(_plan.Target, DamageProperties.Of(attack), _meter));
        _hits[key] = summary;
        return summary;
    }

    public DamageSummary Miss(AttackLine line, ulong riders)
    {
        var key = (line.Index, riders);
        if (_misses.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var attack = line.Attack;
        var groups = new TypeGroups();
        if (attack.Mastery == V.Masteries.Graze && attack.AbilityModifier > 0 && line.Kind != AttackKind.Cleave)
        {
            // "deal damage to that creature equal to the ability modifier you used to make the attack roll", the
            // weapon's type; nothing else (no PB, no rider) adds to it.
            groups.AddFlat(attack.DamageType, attack.AbilityModifier);
        }

        for (var r = 0; r < _plan.Riders.Count; r++)
        {
            if ((riders & (1UL << r)) == 0)
            {
                continue;
            }

            var rider = _plan.Riders[r];
            var type = rider.DamageType ?? attack.DamageType;
            if (rider.Damage.HasDice)
            {
                groups.Add(type, Dice(rider.Damage.Dice, null, type is not null && attack.ElementalAdeptTypes.Contains(type), 1), _meter);
            }

            groups.AddFlat(type, rider.Damage.Flat);
        }

        var summary = groups.IsEmpty ? DamageSummary.None : new DamageSummary(groups.Adjusted(_plan.Target, DamageProperties.Of(attack), _meter));
        _misses[key] = summary;
        return summary;
    }

    /// <summary>A save effect's rolled damage (dice with Elemental Adept, plus its flat amount), before any save.</summary>
    public Pmf<double> SaveRoll(ResolvedSaveEffect effect)
    {
        if (effect.Damage is null)
        {
            return Pmf<double>.Point(0);
        }

        var type = effect.DamageType;
        var elementalAdept = type is not null && effect.ElementalAdeptTypes.Contains(type);
        var dice = Dice(effect.Damage.Dice, null, elementalAdept, 1);
        var flat = effect.Damage.Flat;
        return flat == 0 ? dice : dice.Map(x => x + flat, _meter);
    }

    /// <summary>
    /// The attack's flat damage as this line makes it: the formula's own flat part, the ability part (dropped on Cleave
    /// when positive: "you don't add your ability modifier to the damage, unless that modifier is negative"),
    /// attack_action_only parts only as part of the Attack action, and every other part (Dueling, bonus_damage).
    /// </summary>
    public static int FlatDamage(AttackLine line)
    {
        var total = line.Attack.Damage.Flat;
        foreach (var part in line.Attack.DamageParts)
        {
            if (part.AttackActionOnly && !line.FlatAttackAction)
            {
                continue;
            }

            total += line.Kind == AttackKind.Cleave && part.Source == DamagePartSources.Ability && part.Value > 0 ? 0 : part.Value;
        }

        return total;
    }

    private Pmf<double> Dice(IReadOnlyList<DiceTerm> dice, string? remap, bool elementalAdept, int times)
    {
        var key = (dice, remap, elementalAdept, times);
        if (!_dice.TryGetValue(key, out var pmf))
        {
            pmf = DamageDice.Sum(dice, _meter, remap, elementalAdept, times);
            _dice[key] = pmf;
        }

        return pmf;
    }

    /// <summary>
    /// A hit's damage kept apart by type until each type is adjusted: typeless damage is its own group, never adjusted
    /// beyond the floor at 0.
    /// </summary>
    private sealed class TypeGroups
    {
        private const string Typeless = "";
        private readonly Dictionary<string, (Pmf<double> Dice, long Flat)> _groups = [];
        private readonly List<string> _order = [];

        public bool IsEmpty => _groups.Count == 0;

        public void Add(string? type, Pmf<double> dice, WorkMeter meter)
        {
            var key = type ?? Typeless;
            if (_groups.TryGetValue(key, out var group))
            {
                _groups[key] = (group.Dice.Convolve(dice, meter), group.Flat);
            }
            else
            {
                _groups[key] = (dice, 0);
                _order.Add(key);
            }
        }

        public void AddFlat(string? type, long flat)
        {
            var key = type ?? Typeless;
            if (_groups.TryGetValue(key, out var group))
            {
                _groups[key] = (group.Dice, group.Flat + flat);
            }
            else
            {
                _groups[key] = (Pmf<double>.Point(0), flat);
                _order.Add(key);
            }
        }

        /// <summary>Each type adjusted on its own (<paramref name="properties"/>: the attack's, for qualified adjustments), then convolved.</summary>
        public Pmf<double> Adjusted(ResolvedTarget target, DamageProperties properties, WorkMeter meter)
        {
            Pmf<double>? total = null;
            foreach (var key in _order)
            {
                var (dice, flat) = _groups[key];
                var type = key == Typeless ? null : key;
                var adjusted = dice.Map(x => DamageAdjustment.Apply(x + flat, type, target, properties: properties), meter);
                total = total is null ? adjusted : total.Convolve(adjusted, meter);
            }

            return total ?? Pmf<double>.Point(0);
        }
    }
}
