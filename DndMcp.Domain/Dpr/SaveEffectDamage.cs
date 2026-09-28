using DndMcp.Domain.Dice;
using DndMcp.Domain.Features;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Dpr;

/// <summary>
/// One save effect's damage against the target (research A6, A8): <b>one damage roll shared by every target</b>, an
/// independent save per target.
///
/// <para>
/// For a rolled total x, a target that fails takes dF(x) and one that succeeds dS(x): dF is x adjusted (halved first for a
/// target with Evasion against a Dex "half" effect), dS is half of x adjusted for a "half" effect (0 with Evasion, or for
/// a "none" effect). Given x and k failures among n targets the effect deals k·dF(x) + (n − k)·dS(x) in all, which is
/// what the turn's distribution adds up. Rolling x per target instead would make the kills independent; they are not —
/// a low roll spares every target at once — and P(all four goblins die) to Fireball is 0.99933, not 0.99877.
/// </para>
/// <para>
/// Halving happens on the rolled total, before resistance, and each rounds down (a resisted success takes
/// floor(floor(x/2)/2) = floor(x/4)), which is why this is computed per value of x and never from a mean.
/// </para>
/// <para>
/// <b>The first target is the main target</b>, the one the turn's attacks hit: a condition imposed on it earlier in the
/// turn (Stunning Strike) changes ITS save and, on a 2024 build, its Evasion, while every other creature in the area
/// keeps the target spec's own condition. So the first target may fail with its own chance and take damage from another
/// instance (<c>first</c>: Evasion off while it is Incapacitated); both instances are built from the same roll, so the
/// shared roll x lines up index by index.
/// </para>
/// </summary>
internal sealed class SaveEffectDamage
{
    private readonly long[] _rolls;
    private readonly double[] _chances;
    private readonly long[] _onFail;
    private readonly long[] _onSuccess;
    private readonly Dictionary<(double Fail, int FirstTarget, bool OwnFirst), Pmf<double>> _totals = [];

    /// <param name="evasion">
    /// Whether the target's Evasion is available to this instance: the caller decides, since 2024 Evasion does not work
    /// while the creature is Incapacitated and 2014 Evasion has no such clause.
    /// </param>
    public SaveEffectDamage(ResolvedSaveEffect effect, Pmf<double> roll, ResolvedTarget target, bool evasion)
    {
        Effect = effect;
        Targets = effect.Targets;
        Evasion = evasion && effect.Ability == V.Abilities.Dex && effect.OnSuccess == V.OnSuccess.Half;
        var half = effect.OnSuccess == V.OnSuccess.Half;

        _rolls = roll.Values.ToArray();
        _chances = new double[_rolls.Length];
        _onFail = new long[_rolls.Length];
        _onSuccess = new long[_rolls.Length];
        // A magical effect overcomes "from nonmagical attacks"; a save effect is never silvered or adamantine.
        var properties = DamageProperties.Of(effect);
        for (var i = 0; i < _rolls.Length; i++)
        {
            _chances[i] = roll.Weights[i] / roll.Total;
            _onFail[i] = DamageAdjustment.Apply(_rolls[i], effect.DamageType, target, halve: Evasion, properties);
            _onSuccess[i] = half && !Evasion ? DamageAdjustment.Apply(_rolls[i], effect.DamageType, target, halve: true, properties) : 0;
            MeanOnFail += _chances[i] * _onFail[i];
            MeanOnSuccess += _chances[i] * _onSuccess[i];
        }
    }

    public ResolvedSaveEffect Effect { get; }

    public int Targets { get; }

    /// <summary>The target's Evasion applies: a Dex save against a "half" effect.</summary>
    public bool Evasion { get; }

    /// <summary>E[damage to a target that fails].</summary>
    public double MeanOnFail { get; }

    /// <summary>E[damage to a target that succeeds].</summary>
    public double MeanOnSuccess { get; }

    /// <summary>E[damage to one target] when each fails with probability <paramref name="fail"/>.</summary>
    public double MeanPerTarget(double fail) => (fail * MeanOnFail) + ((1 - fail) * MeanOnSuccess);

    /// <summary>E[total over all targets].</summary>
    public double MeanTotal(double fail) => Targets * MeanPerTarget(fail);

    /// <summary>E[total over all targets | the first target fails (or succeeds)]: the split "P(lands)" is tracked by.</summary>
    public double MeanTotalGivenFirst(double fail, bool firstFails) =>
        (firstFails ? MeanOnFail : MeanOnSuccess) + ((Targets - 1) * MeanPerTarget(fail));

    /// <summary>
    /// E[total] when the first (main) target takes <paramref name="first"/>'s damage and fails with
    /// <paramref name="firstFail"/>, and every other target takes this instance's and fails with <paramref name="othersFail"/>.
    /// The same as <see cref="MeanTotal(double)"/> when the two agree.
    /// </summary>
    public double MeanTotal(SaveEffectDamage first, double firstFail, double othersFail) =>
        ReferenceEquals(first, this) && firstFail == othersFail
            ? MeanTotal(firstFail)
            : first.MeanPerTarget(firstFail) + ((Targets - 1) * MeanPerTarget(othersFail));

    /// <summary>E[total | the first (main) target fails or succeeds], its damage from <paramref name="first"/>, the others' from this.</summary>
    public double MeanTotalGivenFirst(SaveEffectDamage first, double othersFail, bool firstFails) =>
        (firstFails ? first.MeanOnFail : first.MeanOnSuccess) + ((Targets - 1) * MeanPerTarget(othersFail));

    /// <summary>
    /// The distribution of the total over all targets, with the shared roll: Σ_x P(x) Σ_k Binomial(n, F)(k)·δ(k·dF(x) +
    /// (n − k)·dS(x)). With <paramref name="firstTarget"/> 1 (0) the first target is known to fail (succeed) and the
    /// binomial runs over the other n − 1.
    /// </summary>
    /// <param name="firstTarget">−1: unconditioned; 1: the first target fails; 0: it succeeds.</param>
    /// <param name="first">Whose damage the known first target takes (the main target's instance); null: this one's.</param>
    public Pmf<double> Total(double fail, int firstTarget, WorkMeter meter, SaveEffectDamage? first = null)
    {
        first ??= this;
        var key = (fail, firstTarget, ReferenceEquals(first, this));
        if (_totals.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var others = firstTarget < 0 ? Targets : Targets - 1;
        var binomial = DoubleArithmetic.BinomialRow(others, fail);
        meter.Spend((long)_rolls.Length * (others + 1));
        var totals = new Dictionary<long, double>();
        for (var i = 0; i < _rolls.Length; i++)
        {
            var firstDamage = firstTarget switch
            {
                1 => first._onFail[i],
                0 => first._onSuccess[i],
                _ => 0,
            };
            for (var k = 0; k <= others; k++)
            {
                // A zero binomial weight here is an impossible count (F exactly 0 or 1), not an underflow: skipping it keeps
                // "all fail" from listing totals that cannot happen.
                if ((fail == 0 && k > 0) || (fail == 1 && k < others))
                {
                    continue;
                }

                var total = firstDamage + (k * _onFail[i]) + ((others - k) * _onSuccess[i]);
                totals[total] = totals.GetValueOrDefault(total) + (_chances[i] * binomial[k]);
            }
        }

        var pmf = Pmf<double>.FromPairs(totals, 1.0);
        _totals[key] = pmf;
        return pmf;
    }

    /// <summary>
    /// The HP-dependent figures for one cast (contract §4.3), over the shared roll: q(x) = F·[dF(x) ≥ hp] + (1 − F)·[dS(x)
    /// ≥ hp] is P(a given target dies | x), and the kill count given x is Binomial(n, q(x)).
    /// </summary>
    public KillFigures Kills(double fail, int hitPoints)
    {
        var effective = 0.0;
        var each = 0.0;
        var all = 0.0;
        var distribution = new double[Targets + 1];
        for (var i = 0; i < _rolls.Length; i++)
        {
            var p = _chances[i];
            effective += p * ((fail * Math.Min(_onFail[i], hitPoints)) + ((1 - fail) * Math.Min(_onSuccess[i], hitPoints)));
            var q = (fail * (_onFail[i] >= hitPoints ? 1 : 0)) + ((1 - fail) * (_onSuccess[i] >= hitPoints ? 1 : 0));
            each += p * q;
            all += p * Math.Pow(q, Targets);
            var row = DoubleArithmetic.BinomialRow(Targets, q);
            for (var k = 0; k <= Targets; k++)
            {
                distribution[k] += p * row[k];
            }
        }

        return new KillFigures(Targets * effective, each, all, Targets * each, distribution);
    }
}

/// <summary>A save effect's per-cast figures against a target with HP (see <see cref="SaveEffectDamage.Kills"/>).</summary>
/// <param name="EffectiveDamage">E[Σ over targets of min(damage, hp)].</param>
/// <param name="EachDies">P(a given target dies).</param>
/// <param name="AllDie">P(every target dies).</param>
/// <param name="ExpectedKills">E[targets that die] = targets × <paramref name="EachDies"/>.</param>
/// <param name="Distribution">P(exactly k die), k = 0..targets.</param>
internal sealed record KillFigures(double EffectiveDamage, double EachDies, double AllDie, double ExpectedKills, IReadOnlyList<double> Distribution);
