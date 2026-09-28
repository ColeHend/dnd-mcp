using DndMcp.Domain.Dice;
using DndMcp.Domain.Features;
using DndMcp.Domain.Probability;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Simulation;

/// <summary>
/// Dice, d20s, saving throws and the advantage rules. The simulator ROLLS every die (it never samples a distribution);
/// the closed-form odds (<see cref="AttackRoll.Odds"/>) appear here only to rank choices (power attack, which action),
/// never to decide an outcome.
/// </summary>
internal sealed partial class Fight
{
    private static readonly D20Options[] OptionTable = BuildOptions();

    private readonly Dictionary<long, (double Hit, double Crit)> _odds;

    private int D(int sides) => (int)_rng.NextBounded((uint)sides) + 1;

    /// <summary>A uniform double in [0, 1): 53 random bits.</summary>
    private double NextDouble() => (_rng.NextUInt64() >> 11) * (1.0 / (1UL << 53));

    private bool Chance(double p) => p >= 1 || (p > 0 && NextDouble() < p);

    private int RollFormula(DamageFormula formula) => RollTerms(formula.Dice, 1, null, false) + formula.Flat;

    private int RollTerms(IReadOnlyList<DiceTerm> dice, int times, string? remap, bool elementalAdept)
    {
        var total = 0;
        for (var t = 0; t < dice.Count; t++)
        {
            var term = dice[t];
            var n = term.Count * times;
            for (var i = 0; i < n; i++)
            {
                var face = RollDie(term.Sides, remap, elementalAdept);
                total += term.Negative ? -face : face;
            }
        }

        return total;
    }

    /// <summary>
    /// One die after its remaps, exactly as the closed form's per-die distribution (<c>DamageDice.Die</c>): Great Weapon
    /// Fighting 2014 rerolls a 1 or 2 once and keeps the new roll; 2024 counts a 1 or 2 as 3; Elemental Adept then counts
    /// a 1 as 2.
    /// </summary>
    private int RollDie(int sides, string? remap, bool elementalAdept)
    {
        var face = D(sides);
        if (remap == V.Remaps.Gwf2014 && face <= 2)
        {
            face = D(sides);
        }
        else if (remap == V.Remaps.Gwf2024 && face <= 2)
        {
            face = 3;
        }

        if (elementalAdept && face == 1)
        {
            face = 2;
        }

        return face;
    }

    /// <summary>
    /// The kept natural face of a d20 roll: one die, the higher or lower of two, or the highest of three (Elven Accuracy,
    /// only with Advantage). Lucky rerolls ONE die showing a 1 and must use the new roll (the 2024 wording; 2014 read the
    /// same way), which is the same rule <see cref="D20"/> enumerates.
    /// </summary>
    private int RollD20(D20Mode mode, bool lucky, bool elvenAccuracy, out string faces)
    {
        var count = mode == D20Mode.Normal ? 1 : mode == D20Mode.Advantage && elvenAccuracy ? 3 : 2;
        var a = D(20);
        var b = count > 1 ? D(20) : 0;
        var c = count > 2 ? D(20) : 0;
        var rerolled = 0;
        if (lucky)
        {
            if (a == 1)
            {
                rerolled = a = D(20);
            }
            else if (b == 1)
            {
                rerolled = b = D(20);
            }
            else if (c == 1)
            {
                rerolled = c = D(20);
            }
        }

        var kept = mode switch
        {
            D20Mode.Advantage => Math.Max(a, Math.Max(b, c)),
            D20Mode.Disadvantage => Math.Min(a, b),
            _ => a,
        };

        faces = _log is null
            ? string.Empty
            : count == 1
                ? $"{kept}{(rerolled > 0 ? " (Lucky reroll)" : "")}"
                : $"{(mode == D20Mode.Advantage ? "adv" : "dis")} {a}/{b}{(count > 2 ? "/" + c : "")}{(rerolled > 0 ? " (Lucky reroll)" : "")} → {kept}";
        return kept;
    }

    private int RollSignedDice(DiceTerm[] dice) => dice.Length == 0 ? 0 : RollTerms(dice, 1, null, false);

    /// <summary>The fight's exhaustion penalty on a d20 test: 2024 −2 per level.</summary>
    private int ExhaustionPenalty(Creature c) => _setup.Is2024 ? 2 * c.Exhaustion : 0;

    /// <summary>2014 exhaustion: level 3 or more gives Disadvantage on attack rolls and saves.</summary>
    private bool ExhaustionDisadvantage(Creature c) => !_setup.Is2024 && c.Exhaustion >= 3;

    /// <summary>
    /// A saving throw: d20 + bonus (+ the harness's save dice and cover) ≥ DC, no natural 1 or 20 rule. Paralyzed, Stunned,
    /// Unconscious and Petrified fail Str and Dex saves; Magic Resistance gives Advantage against magical effects; Dodging
    /// Advantage and Restrained Disadvantage on Dex saves; exhaustion per the fight's edition.
    /// </summary>
    private bool SavingThrow(Creature c, string ability, int dc, bool magical, out string text)
    {
        var index = CombatantTemplate.AbilityIndex(ability);
        var strDex = index <= 1;
        if (strDex && (c.Has(Cond.Paralyzed) || c.Has(Cond.Stunned) || c.Unconscious || c.Has(Cond.Petrified)))
        {
            text = _log is null ? string.Empty : $"{ability} save vs DC {dc}: fails automatically";
            return false;
        }

        var advantage = (magical && c.T.MagicResistance) || (index == 1 && c.IsDodging);
        var disadvantage = (index == 1 && c.Has(Cond.Restrained)) || ExhaustionDisadvantage(c);
        var face = RollD20(D20.Resolve(advantage, disadvantage), false, false, out var faces);
        var bonus = c.T.Saves[index] + (index == 1 ? c.T.CoverBonus : 0) - ExhaustionPenalty(c);
        var dice = RollSignedDice(c.T.SaveDice);
        var total = face + bonus + dice;
        text = _log is null ? string.Empty : $"{ability} save d20 {faces}{Signed(bonus)}{(c.T.SaveDice.Length > 0 ? $" {Signed(dice)} (dice)" : "")} = {total} vs DC {dc}";
        return total >= dc;
    }

    /// <summary>An escape from a grapple or restraint: d20 + the better of Str and Dex (Athletics or Acrobatics) against the DC.</summary>
    private bool EscapeCheck(Creature c, int dc, out string text)
    {
        var face = RollD20(!_setup.Is2024 && c.Exhaustion >= 1 ? D20Mode.Disadvantage : D20Mode.Normal, false, false, out var faces);
        var bonus = Math.Max(c.T.StrMod, c.T.DexMod) - ExhaustionPenalty(c);
        text = _log is null ? string.Empty : $"escape check d20 {faces}{Signed(bonus)} = {face + bonus} vs DC {dc}";
        return face + bonus >= dc;
    }

    /// <summary>
    /// The d20 mode of an attack from <paramref name="a"/> on <paramref name="t"/>: every source of Advantage and
    /// Disadvantage from both creatures' conditions and the engagement rules, plus the caller's own (traits, DSL rate
    /// sources, Vex); any of each cancel to a normal roll. <paramref name="consume"/> spends Sap's one-roll Disadvantage
    /// (false when only estimating).
    /// </summary>
    internal D20Mode AttackMode(Creature a, Creature t, bool melee, bool advantage, bool disadvantage, out bool autoCrit, bool consume = true)
    {
        if (a.Has(Cond.Poisoned) || a.Has(Cond.Blinded) || a.Has(Cond.Restrained) || a.Has(Cond.Prone) || ExhaustionDisadvantage(a))
        {
            disadvantage = true;
        }

        if (a.Has(Cond.Frightened) && FrightenedByLivingSource(a))
        {
            disadvantage = true;
        }

        if (a.Has(Cond.Invisible))
        {
            advantage = true;
        }

        if (a.SappedBy >= 0)
        {
            disadvantage = true;
            if (consume)
            {
                a.SappedBy = -1;
            }
        }

        if (_setup.Is2024 && a.Has(Cond.Grappled) && !HasFrom(a, Cond.Grappled, t.Id))
        {
            disadvantage = true;
        }

        if (!melee && !_setup.Dummy && a.T.Front && EngagedInMelee(a))
        {
            disadvantage = true;
        }

        if (t.Has(Cond.Prone) || t.Down)
        {
            if (melee)
            {
                advantage = true;
            }
            else
            {
                disadvantage = true;
            }
        }

        if (t.Has(Cond.Restrained) || t.Has(Cond.Stunned) || t.Has(Cond.Paralyzed) || t.Unconscious || t.Has(Cond.Blinded) || t.Has(Cond.Petrified) || t.RecklessActive)
        {
            advantage = true;
        }

        // Dodge works against attackers it can see: a blinded dodger gives it up for attack rolls (not for Dex saves).
        if (t.Has(Cond.Invisible) || (t.IsDodging && !t.Has(Cond.Blinded)))
        {
            disadvantage = true;
        }

        autoCrit = melee && (t.Has(Cond.Paralyzed) || t.Unconscious);
        return D20.Resolve(advantage, disadvantage);
    }

    private bool FrightenedByLivingSource(Creature c)
    {
        foreach (var active in c.Conditions)
        {
            if (active.Condition == Cond.Frightened && (active.Source < 0 || !_c[active.Source].Dead))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasFrom(Creature c, int condition, int source)
    {
        foreach (var active in c.Conditions)
        {
            if (active.Condition == condition && active.Source == source)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A front-liner facing a standing enemy front-liner is engaged in melee (a ranged attack has Disadvantage).</summary>
    private bool EngagedInMelee(Creature a)
    {
        foreach (var other in _c)
        {
            if (other.Side != a.Side && other.Up && other.T.Front)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Hit and crit chances for ranking choices (cached per thread), from the closed form's exact odds.</summary>
    private (double Hit, double Crit) Odds(int bonus, int ac, int critMin, D20Mode mode, bool lucky, bool elvenAccuracy, Pmf<double>? dice, int diceKey, bool autoCrit)
    {
        var key = ((long)diceKey << 40) | ((long)(bonus + 512) << 28) | ((long)(ac + 512) << 16) | ((long)critMin << 8) |
                  ((long)mode << 4) | (lucky ? 8L : 0) | (elvenAccuracy ? 4L : 0) | (autoCrit ? 2L : 0);
        if (!_odds.TryGetValue(key, out var odds))
        {
            var o = AttackRoll.Odds(bonus, ac, critMin, OptionTable[((int)mode * 4) + (lucky ? 2 : 0) + (elvenAccuracy ? 1 : 0)], dice, autoCrit);
            odds = (o.Hit, o.Crit);
            _odds[key] = odds;
        }

        return odds;
    }

    private static D20Options[] BuildOptions()
    {
        var table = new D20Options[12];
        foreach (var mode in new[] { D20Mode.Normal, D20Mode.Advantage, D20Mode.Disadvantage })
        {
            for (var flags = 0; flags < 4; flags++)
            {
                table[((int)mode * 4) + flags] = new D20Options(mode, (flags & 2) != 0, (flags & 1) != 0);
            }
        }

        return table;
    }
}
