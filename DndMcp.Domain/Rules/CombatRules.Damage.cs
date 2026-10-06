using System.Globalization;
using System.Text;
using DndMcp.Domain.Features;
using K = DndMcp.Domain.Simulation.StatBlockValues;

namespace DndMcp.Domain.Rules;

/// <summary>
/// The shared combat rules: what the live tracker (T), the character-sheet writer (C) and the simulator agree on, as pure
/// functions of a <see cref="HitPointState"/> and the event. Each statement names its source (contract §5, research
/// RULES): SRD 5.1 (2014) and SRD 5.2/5.2.1 (2024); where the two editions differ the function takes the edition.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one class.</b> The tracker applies damage to a combatant, the sheet applies it to a character out of combat,
/// and the simulator's engine applies it a million times a run. Three copies would disagree on the edges (temporary
/// hit points at 0 HP, the DC on absorbed damage, a knock-out instead of massive damage), and the end-of-combat
/// write-back would then carry numbers the sheet's own rules could not have produced. The engine keeps its own
/// optimised arithmetic (it is not edited in Phase 7); the agreement tests run both over the same inputs, including every
/// SRD stat block's damage adjustments.
/// </para>
/// <para>
/// <b>No dice, no clock.</b> Every roll is an input (a face or a total); a function that would need a server roll says
/// what to roll instead (<see cref="DeathSaveRoll"/>, <see cref="ConcentrationRoll"/>, <see cref="InitiativeRoll"/>).
/// Bad arguments (a negative HP, an unknown edition) are host bugs and throw <see cref="ArgumentException"/>: the
/// callers validate what the user typed and word the refusal.
/// </para>
/// </remarks>
public static partial class CombatRules
{
    /// <summary>The largest amount one damage part may carry (a guard against arithmetic overflow, far above any roll).</summary>
    public const int MaxDamagePart = 1_000_000;

    /// <summary>The damage type Undead Fortitude does not survive (the canonical <see cref="DslValues.DamageTypes"/> name).</summary>
    private const string RadiantDamage = "radiant";

    /// <summary>
    /// Applies one damage instance (contract §5.1-§5.5): the per-type pipeline, temporary hit points, the drop to 0 (a
    /// knock-out, massive damage, a monster's death or a death interceptor), damage at 0 HP, concentration and Bloodied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The pipeline, per damage type</b> (SRD 5.1 "Damage Resistance and Vulnerability": "Resistance and then
    /// vulnerability are applied after all other modifiers"; SRD 5.2 "adjustments … first; Resistance … second; and
    /// Vulnerability … third", rounding down): each part counts at 0 or more, and parts of one type are summed first (the
    /// simulator's per-type arrays; "damage of that type is halved" rounds once, so 3 fire + 3 fire resisted is 3, not 2).
    /// Then half on a successful save (round down); immune → 0; resistant or Petrified → half (round down) once; vulnerable
    /// → double once. A flat modifier (SRD 5.1's "reduced by 5") is the caller's: it gives the reduced amount. <see
    /// cref="DamageRequest.Raw"/> skips immunity, resistance, vulnerability and Petrified.
    /// </para>
    /// <para>
    /// <b>Temporary hit points</b> absorb first (SRD 5.1 "the temporary hit points are lost first"); they absorb damage at 0
    /// HP too, but never wake or stabilise. The DC and a death-save failure read the total before they absorb it
    /// (<see cref="RulingFlags.ConcentrationOnPreTempDamage"/>, echoed when it decided something).
    /// </para>
    /// <para>
    /// <b>Dropping to 0.</b> A knock-out (SRD 5.1 "Knocking a Creature Out": unconscious and stable at 0; SRD 5.2.1
    /// "Knocking Out a Creature": 1 HP and Unconscious) applies instead of massive damage and of a monster's death. Then,
    /// as the simulator does (<c>Fight.ApplyDamage</c>), a death interceptor of its stat block that can apply
    /// (<see cref="DeathInterceptor.CouldApply"/>) is read BEFORE massive damage and before the creature falls unconscious,
    /// for every creature, death-save makers included (a <c>death_saves: true</c> zombie keeps its Undead Fortitude);
    /// Regeneration at 0 only for one that does not make death saves (the engine's <c>DropToZero</c> sends a death-save
    /// maker to dying first). An intercepted creature is held at 0 HP, not dead, not unconscious, for the table to resolve
    /// (<see cref="DamageOutcome.Intercepted"/>; <see cref="DamageOutcome.IfTraitFails"/> is what happens when the trait
    /// does not save it). Otherwise a creature that makes death saves dies if the damage left over is at least its
    /// effective maximum (SRD 5.1 "Instant Death"), else falls unconscious and prone, dying, with its tallies reset; any
    /// other creature dies (SRD 5.2 "Monster Death").
    /// </para>
    /// <para>
    /// <b>Damage at 0 HP</b> (SRD 5.1 "Damage at 0 Hit Points", SRD 5.2 the same): a creature that makes death saves stops
    /// being stable; damage of at least its effective maximum kills it, else it takes one failure (two on a critical hit)
    /// and dies at three. Any other creature at 0 (a 2014 knock-out, an intercepted monster) dies, except one whose
    /// Regeneration works at 0: the simulator leaves that to the start of its turn, and so does this.
    /// </para>
    /// <para>
    /// <b>Concentration</b> (SRD 5.1 "Taking damage"; SRD 5.2.1 Concentration): a creature still conscious after the
    /// damage owes a save against <see cref="ConcentrationDc"/>; one that fell unconscious, was knocked out, dropped to 0 or
    /// died loses it. A creature held at 0 by Undead Fortitude or Relentless keeps it and owes the save on the same total,
    /// as the simulator rolls it once the trait leaves it at 1 HP; one held only by Regeneration is down at 0 and loses it
    /// (the engine's <c>OnIncapacitated</c>). <b>Bloodied</b> (2024 only): reported at each crossing from above half its
    /// effective maximum to half or less, a drop to 0 included, unless the creature died.
    /// </para>
    /// </remarks>
    /// <param name="adjustments">The target's adjustments (stat block, sheet defenses, active effects, Petrified); none when null.</param>
    /// <param name="interceptors">
    /// The target's <see cref="StatBlockFacts.DeathInterceptors"/> (with <c>relentlessUsed</c> once its Relentless has kept
    /// it alive since its last rest); none when null.
    /// </param>
    public static DamageOutcome Damage(
        HitPointState target, DamageRequest request, DamageAdjustments? adjustments = null, IReadOnlyList<DeathInterceptor>? interceptors = null)
    {
        Check(target);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Parts);
        adjustments ??= DamageAdjustments.None;
        interceptors ??= [];

        var parts = AdjustParts(request, adjustments);
        var total = parts.Sum(p => p.Amount);
        var radiant = parts.Any(p => p.DamageType == RadiantDamage && p.Amount > 0);
        var damageText = DamageText(parts, total);
        if (target.Dead)
        {
            return new DamageOutcome
            {
                Before = target, After = target, Parts = parts, Total = total, Radiant = radiant, NoEffect = true,
                Arithmetic = $"{damageText}; no effect: dead",
            };
        }

        if (total == 0)
        {
            return new DamageOutcome
            {
                Before = target, After = target, Parts = parts, Arithmetic = $"{damageText}; {Num(target.Hp)} → {Num(target.Hp)}",
            };
        }

        var is2024 = Is2024(target.Edition);
        var effectiveMax = target.EffectiveMaxHp;
        var absorbed = Math.Min(target.TempHp, total);
        var rest = total - absorbed;
        var hp = target.Hp;
        var tempHp = target.TempHp - absorbed;
        var tally = target.DeathSaves;
        var knockedOut = target.KnockedOut;
        var leftover = 0;
        var knockOut = false;
        var fell = false;
        var died = false;
        string? cause = null;
        var intercepted = false;
        var heldByTrait = false;
        IReadOnlyList<DeathInterceptor> applying = [];
        var failures = 0;
        var stableLost = false;
        var preTempDecided = false;

        if (hp > 0)
        {
            if (rest < hp)
            {
                hp -= rest;
            }
            else
            {
                leftover = rest - hp;
                knockedOut = false;

                // A creature already knocked out (2024, at 1 HP) is unconscious: it does not fall again.
                var conscious = !target.KnockedOut;
                if (request.KnockOut)
                {
                    knockOut = true;
                    fell = conscious;
                    hp = is2024 ? 1 : 0;
                    knockedOut = is2024;
                    tally = is2024 ? DeathSaveTally.Zero : DeathSaveTally.Stabilized;
                }
                else
                {
                    hp = 0;
                    applying = interceptors
                        .Where(i => !(target.MakesDeathSaves && i.Kind == K.TraitKinds.Regeneration) && i.CouldApply(total, request.Critical, radiant))
                        .ToList();
                    intercepted = applying.Count > 0;
                    heldByTrait = applying.Any(i => i.Kind != K.TraitKinds.Regeneration);
                    if (intercepted)
                    {
                        // Held for the table: a death-save maker's tallies reset as at any drop (it is dying if the trait fails).
                        tally = target.MakesDeathSaves ? DeathSaveTally.Zero : tally;
                    }
                    else if (target.MakesDeathSaves)
                    {
                        if (leftover >= effectiveMax)
                        {
                            died = true;
                            cause = DeathCauses.MassiveDamage;
                        }
                        else
                        {
                            fell = conscious;
                            tally = DeathSaveTally.Zero;
                        }
                    }
                    else
                    {
                        died = true;
                        cause = DeathCauses.ZeroHp;
                    }
                }
            }
        }
        else if (target.MakesDeathSaves)
        {
            stableLost = tally.Stable;
            preTempDecided = absorbed > 0;
            if (total >= effectiveMax)
            {
                died = true;
                cause = DeathCauses.DamageAtZero;
            }
            else
            {
                failures = request.Critical ? 2 : 1;
                tally = new DeathSaveTally(tally.Successes, Math.Min(3, tally.Failures + failures), false);
                if (tally.Failures >= 3)
                {
                    died = true;
                    cause = DeathCauses.DeathSaveFailures;
                }
            }
        }
        else if (!interceptors.Any(i => i.Kind == K.TraitKinds.Regeneration))
        {
            died = true;
            cause = DeathCauses.DamageAtZero;
        }

        if (died)
        {
            hp = 0;
            tempHp = 0;
            knockedOut = false;
            tally = target.MakesDeathSaves ? new DeathSaveTally(0, 3, false) : DeathSaveTally.Zero;
        }

        int? dc = null;
        var broken = false;
        if (target.Concentrating)
        {
            if (died || fell || knockOut || (hp == 0 && !heldByTrait))
            {
                broken = true;
            }
            else
            {
                dc = ConcentrationDc(total, target.Edition);
                preTempDecided |= absorbed > 0;
            }
        }

        var after = target with
        {
            Hp = hp,
            TempHp = tempHp,
            DeathSaves = tally,
            Dead = died,
            KnockedOut = knockedOut,
            Concentrating = target.Concentrating && !broken,
        };

        var arithmetic = new StringBuilder(damageText);
        if (absorbed > 0)
        {
            arithmetic.Append(CultureInfo.InvariantCulture, $"; temporary HP {target.TempHp} → {target.TempHp - absorbed}");
        }

        arithmetic.Append(CultureInfo.InvariantCulture, $"; {target.Hp} → {hp}");
        return new DamageOutcome
        {
            Before = target,
            After = after,
            Parts = parts,
            Total = total,
            TempAbsorbed = absorbed,
            HpLost = target.Hp - hp,
            Leftover = target.Hp > 0 && (hp == 0 || knockOut) ? leftover : 0,
            Dropped = target.Hp > 0 && hp == 0,
            KnockedOut = knockOut,
            FellUnconscious = fell,
            Died = died,
            DeathCause = cause,
            Intercepted = intercepted,
            Interceptors = applying,
            IfTraitFails = intercepted ? Damage(target, request, adjustments, []) : null,
            DeathSaveFailures = failures,
            StableLost = stableLost,
            ConcentrationDc = dc,
            ConcentrationBroken = broken,
            BecameBloodied = !died && is2024 && IsBloodied(hp, effectiveMax, target.Edition) && !IsBloodied(target.Hp, effectiveMax, target.Edition),
            Radiant = radiant,
            Rulings = preTempDecided ? [RulingFlags.ConcentrationOnPreTempDamage] : [],
            Arithmetic = arithmetic.ToString(),
        };
    }

    /// <summary>
    /// Healing (contract §5.7; SRD 5.1 "Healing": "hit points can't exceed its hit point maximum … A creature that has died
    /// can't regain hit points"; SRD 5.2 the same, "14/20 + 8 → 20"). The dead are refused (<see cref="HealOutcome.Refused"/>,
    /// nothing changes). Otherwise <c>hp = min(effective max, hp + amount)</c> (never lowered, should hp already exceed a
    /// reduced maximum); from 0 HP the creature wakes, stops dying and its tallies reset (SRD 5.1 "This unconsciousness
    /// ends if you regain any hit points"); a 2024 knock-out ends on any healing that regains hit points (SRD 5.2.1 "until
    /// it regains any Hit Points"). Prone stays (SRD 5.2.1 Unconscious "When this condition ends, you remain Prone").
    /// Temporary hit points are never healed. A heal that regains nothing — an amount of 0 or less (a roll with a penalty),
    /// or a creature already at its maximum — wakes no one (review F2R04: a heal of 1 on a knocked-out creature at full
    /// hit points woke it, though it regained no hit point).
    /// </summary>
    public static HealOutcome Heal(HitPointState target, int amount)
    {
        Check(target);
        if (target.Dead)
        {
            return new HealOutcome(target, target, 0, Woke: false, Refused: true, EndedKnockOut: false);
        }

        if (amount <= 0)
        {
            return new HealOutcome(target, target, 0, Woke: false, Refused: false, EndedKnockOut: false);
        }

        var hp = Math.Max(target.Hp, (int)Math.Min(target.EffectiveMaxHp, (long)target.Hp + amount));
        var fromZero = target.Hp == 0 && hp > 0;
        var endsKnockOut = target.KnockedOut && hp > target.Hp;
        var after = target with
        {
            Hp = hp,
            KnockedOut = target.KnockedOut && !endsKnockOut,
            DeathSaves = fromZero ? DeathSaveTally.Zero : target.DeathSaves,
        };
        return new HealOutcome(target, after, hp - target.Hp, fromZero || endsKnockOut, Refused: false, EndedKnockOut: endsKnockOut);
    }

    /// <summary>
    /// Grants temporary hit points (contract §5.2): they never stack; by default the creature keeps the higher
    /// (<see cref="RulingFlags.TempHpKeepHigher"/>, echoed when it had some); <paramref name="replace"/> takes the new
    /// ones instead (RAW the creature chooses). Refused on the dead. Never changes hit points, never wakes or stabilises
    /// (SRD 5.1 "receiving temporary hit points doesn't restore you to consciousness or stabilize you").
    /// </summary>
    public static TempHpOutcome GrantTempHp(HitPointState target, int amount, bool replace = false)
    {
        Check(target);
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        if (target.Dead)
        {
            return new TempHpOutcome(target, target, Refused: true, Rulings: []);
        }

        var after = target with { TempHp = GrantTempHp(target.TempHp, amount, replace) };
        return new TempHpOutcome(target, after, Refused: false, Rulings: !replace && target.TempHp > 0 ? [RulingFlags.TempHpKeepHigher] : []);
    }

    /// <summary>The temporary hit points after a grant: the higher of the two (the simulator's <c>Fight.GainTempHp</c>), or the new ones when replacing.</summary>
    public static int GrantTempHp(int current, int gained, bool replace = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(current);
        ArgumentOutOfRangeException.ThrowIfNegative(gained);
        return replace ? gained : Math.Max(current, gained);
    }

    /// <summary>
    /// 2024 Bloodied (contract §5.10; SRD 5.2.1 Bloodied: "while it has half its Hit Points or fewer remaining"):
    /// <c>hp ≤ ⌊effective max / 2⌋</c>. 2014 has no such term: always false.
    /// </summary>
    public static bool IsBloodied(int hp, int effectiveMaxHp, string edition) =>
        Is2024(edition) && effectiveMaxHp > 0 && hp <= effectiveMaxHp / 2;

    /// <summary>
    /// Dead by the stored columns alone (the sheet has no dead column, contract §4): three death-save failures, Exhaustion
    /// 6, or an effective maximum of 0 (contract D16, §5.17).
    /// </summary>
    public static bool IsDead(DeathSaveTally deathSaves, int exhaustion, int effectiveMaxHp)
    {
        ArgumentNullException.ThrowIfNull(deathSaves);
        return deathSaves.Failures >= 3 || exhaustion >= ExhaustionDeathLevel || effectiveMaxHp <= 0;
    }

    /// <summary>
    /// Applies a changed maximum (a new <c>max_hp_reduction</c> or Exhaustion level the caller already set on the state;
    /// contract §5.9, §5.17): hit points above the new effective maximum are lost (2014 Exhaustion 4 halves it; a Life
    /// Drain lowers it), and the creature dies at Exhaustion 6 (both editions) or an effective maximum of 0 (SRD 5.2 "Hit
    /// Point Maximum of 0").
    /// </summary>
    public static MaximumOutcome ApplyMaximum(HitPointState target)
    {
        Check(target);
        if (target.Dead)
        {
            return new MaximumOutcome(target, target, 0, Died: false, DeathCause: null);
        }

        var effectiveMax = target.EffectiveMaxHp;
        var cause = target.Exhaustion >= ExhaustionDeathLevel ? DeathCauses.Exhaustion
            : effectiveMax <= 0 ? DeathCauses.MaxHpZero
            : null;
        if (cause is not null)
        {
            var dead = target with
            {
                Hp = 0, TempHp = 0, Dead = true, KnockedOut = false, Concentrating = false,
                DeathSaves = target.MakesDeathSaves ? new DeathSaveTally(0, 3, false) : DeathSaveTally.Zero,
            };
            return new MaximumOutcome(target, dead, target.Hp, Died: true, cause);
        }

        var hp = Math.Min(target.Hp, effectiveMax);
        return new MaximumOutcome(target, target with { Hp = hp }, target.Hp - hp, Died: false, DeathCause: null);
    }

    private static List<DamagePartOutcome> AdjustParts(DamageRequest request, DamageAdjustments adjustments)
    {
        var types = new List<string?>();
        var given = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var part in request.Parts)
        {
            ArgumentNullException.ThrowIfNull(part);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(part.Amount, MaxDamagePart);
            var type = CanonicalDamageType(part.DamageType);
            var key = type ?? string.Empty;
            if (!given.ContainsKey(key))
            {
                types.Add(type);
                given[key] = 0;
            }

            given[key] += Math.Max(0, part.Amount);
        }

        var result = new List<DamagePartOutcome>();
        foreach (var type in types)
        {
            var x = given[type ?? string.Empty];
            var start = x;
            var steps = new List<DamageStep>();
            if (x > 0 && request.Half)
            {
                steps.Add(new DamageStep(DamageStepKinds.Half, x, x / 2));
                x /= 2;
            }

            if (x > 0 && !request.Raw)
            {
                if (adjustments.Immunity(type, request.Magical, request.Silvered, request.Adamantine) is { } immunity)
                {
                    steps.Add(new DamageStep(DamageStepKinds.Immune, x, 0, immunity.Qualifier, immunity.Source));
                    x = 0;
                }
                else
                {
                    var resistance = adjustments.Resistance(type, request.Magical, request.Silvered, request.Adamantine);
                    if (resistance is not null || adjustments.Petrified)
                    {
                        steps.Add(new DamageStep(DamageStepKinds.Resistant, x, x / 2, resistance?.Qualifier, resistance is null ? DamageAdjustments.PetrifiedSource : resistance.Source));
                        x /= 2;
                    }

                    if (x > 0 && adjustments.Vulnerability(type, request.Magical, request.Silvered, request.Adamantine) is { } vulnerability)
                    {
                        steps.Add(new DamageStep(DamageStepKinds.Vulnerable, x, x * 2, vulnerability.Qualifier, vulnerability.Source));
                        x *= 2;
                    }
                }
            }

            result.Add(new DamagePartOutcome(type, start, x, steps));
        }

        return result;
    }

    private static string? CanonicalDamageType(string? type)
    {
        if (type is null)
        {
            return null;
        }

        return DslValues.DamageTypes.Set.TryMatch(type, out var canonical)
            ? canonical
            : throw new ArgumentException($"\"{type}\" is not a damage type.", nameof(type));
    }

    private static string DamageText(IReadOnlyList<DamagePartOutcome> parts, int total)
    {
        if (parts.Count == 0)
        {
            return "no damage";
        }

        var texts = parts.Select(p =>
        {
            var text = new StringBuilder().Append(CultureInfo.InvariantCulture, $"{p.Given} {p.DamageType ?? "untyped"}");
            foreach (var step in p.Steps)
            {
                text.Append(' ').Append(StepText(step));
            }

            if (p.Steps.Count > 0 && parts.Count > 1)
            {
                text.Append(CultureInfo.InvariantCulture, $" → {p.Amount}");
            }

            return text.ToString();
        }).ToList();

        if (parts.Count == 1)
        {
            return parts[0].Steps.Count > 0 ? $"{texts[0]} = {Num(total)}" : texts[0];
        }

        return $"{string.Join(" + ", texts)} = {Num(total)}";
    }

    private static string StepText(DamageStep step)
    {
        var why = new List<string>();
        if (step.Qualifier is { } qualifier)
        {
            why.Add(qualifier switch
            {
                K.DamageQualifiers.Nonmagical => "nonmagical",
                K.DamageQualifiers.NonmagicalNotSilvered => "nonmagical, not silvered",
                K.DamageQualifiers.NonmagicalNotAdamantine => "nonmagical, not adamantine",
                _ => "a qualifier to check",
            });
        }

        if (step.Source is { } source)
        {
            why.Add(source);
        }

        var detail = why.Count == 0 ? string.Empty : ": " + string.Join(", ", why);
        return step.Kind switch
        {
            DamageStepKinds.Half => "½ (save)",
            DamageStepKinds.Immune => $"×0 (immune{detail})",
            DamageStepKinds.Resistant => $"½ (resistant{detail})",
            _ => $"×2 (vulnerable{detail})",
        };
    }

    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether <paramref name="edition"/> is "2024"; throws for anything but the two editions (a host bug).</summary>
    private static bool Is2024(string edition) => edition switch
    {
        DslValues.Editions.E2024 => true,
        DslValues.Editions.E2014 => false,
        _ => throw new ArgumentException($"\"{edition}\" is not an edition (2014 or 2024).", nameof(edition)),
    };

    private static void Check(HitPointState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _ = Is2024(state.Edition);
        ArgumentOutOfRangeException.ThrowIfNegative(state.Hp);
        ArgumentOutOfRangeException.ThrowIfNegative(state.MaxHp);
        ArgumentOutOfRangeException.ThrowIfNegative(state.MaxHpReduction);
        ArgumentOutOfRangeException.ThrowIfNegative(state.TempHp);
        ArgumentOutOfRangeException.ThrowIfNegative(state.Exhaustion);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(state.Exhaustion, ExhaustionDeathLevel);
        ArgumentNullException.ThrowIfNull(state.DeathSaves);
        ArgumentOutOfRangeException.ThrowIfNegative(state.DeathSaves.Successes);
        ArgumentOutOfRangeException.ThrowIfNegative(state.DeathSaves.Failures);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(state.DeathSaves.Successes, 3);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(state.DeathSaves.Failures, 3);
    }
}

/// <summary>What one heal did. <see cref="Refused"/>: the target is dead and nothing changed ("revive first").</summary>
/// <param name="Regained">Hit points actually regained (after the cap).</param>
/// <param name="Woke">It was at 0 HP (or knocked out) and is conscious now: the caller ends Unconscious; Prone stays.</param>
/// <param name="EndedKnockOut">A 2024 knock-out ended.</param>
public sealed record HealOutcome(HitPointState Before, HitPointState After, int Regained, bool Woke, bool Refused, bool EndedKnockOut);

/// <summary>What one grant of temporary hit points did. <see cref="Refused"/>: the target is dead.</summary>
public sealed record TempHpOutcome(HitPointState Before, HitPointState After, bool Refused, IReadOnlyList<string> Rulings);

/// <summary>What a changed maximum did: hit points lost to the lower maximum, or death and its cause (<see cref="DeathCauses"/>).</summary>
public sealed record MaximumOutcome(HitPointState Before, HitPointState After, int HpLost, bool Died, string? DeathCause);
