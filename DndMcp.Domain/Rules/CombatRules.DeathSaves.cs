using DndMcp.Domain.Probability;
using DndMcp.Domain.Simulation;
using DeathSaveStep = DndMcp.Domain.Simulation.DeathSaves;

namespace DndMcp.Domain.Rules;

public static partial class CombatRules
{
    /// <summary>
    /// The server's roll for a death save (contract §5.6): one d20, with Disadvantage (2d20, lower) at 2014 Exhaustion 3
    /// or more ("Disadvantage on … saving throws", SRD 5.1 Conditions), and 2024's −2 × Exhaustion in the expression (a
    /// death save is a D20 Test, SRD 5.2.1 Exhaustion). Roll <see cref="D20RollPlan.Expression"/>, then pass the KEPT
    /// d20 face to <see cref="DeathSave"/>, which applies the modifier itself.
    /// </summary>
    public static D20RollPlan DeathSaveRoll(HitPointState target)
    {
        Check(target);
        var mode = ExhaustionDisadvantageOnAttacksAndSaves(target.Exhaustion, target.Edition) ? D20Mode.Disadvantage : D20Mode.Normal;
        return D20RollPlan.Of(mode, -ExhaustionD20Penalty(target.Exhaustion, target.Edition));
    }

    /// <summary>
    /// One death saving throw for a dying creature (contract §5.6; SRD 5.1 "Death Saving Throws", SRD 5.2 the same), over
    /// the simulator's <see cref="DeathSaveStep.Step"/> so the two cannot disagree on the tally.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The d20 FACE decides 1 and 20: a 20 regains 1 hit point and the creature is conscious (still Prone), tallies 0/0;
    /// a 1 counts as two failures. Otherwise the save succeeds on a total of 10 or more, where the total is
    /// <paramref name="total"/> when given (Bless and the like: the caller adds them), else the face in 2014 and the face
    /// − 2 × Exhaustion in 2024. With only a total (no face), no natural 1 or 20 applies. Three successes make it stable
    /// with BOTH tallies reset to 0 (SRD 5.1 "The number of both is reset to zero when you … become stable"; the step
    /// function leaves the counts, the engine resets them, and so does this wrapper); three failures kill it.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The creature is not dying (the caller refuses the call), or neither a face nor a total is given.</exception>
    public static DeathSaveOutcome DeathSave(HitPointState target, int? face, int? total = null)
    {
        Check(target);
        if (!target.Dying)
        {
            throw new ArgumentException("Only a dying creature (0 HP, making death saves, not stable, not dead) makes a death save.", nameof(target));
        }

        if (face is null && total is null)
        {
            throw new ArgumentException("A death save needs the d20 face or the total.", nameof(face));
        }

        if (face is { } f)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(f, 1, nameof(face));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(f, 20, nameof(face));
        }

        var value = total ?? face!.Value - ExhaustionD20Penalty(target.Exhaustion, target.Edition);
        var natural = face is 1 or 20;
        var success = face == 20 || (!natural && value >= DeathSaveStep.Dc);

        // The step function decides by face; a total-decided save is fed a face that means the same (10: a success, 2:
        // one failure), so the tally logic stays the engine's.
        var stepFace = natural ? face!.Value : success ? DeathSaveStep.Dc : 2;
        var tally = target.DeathSaves;
        var (successes, failures, state) = DeathSaveStep.Step(tally.Successes, tally.Failures, stepFace);
        var after = state switch
        {
            DeathSaveState.Revived => target with { Hp = Math.Max(target.Hp, Math.Min(1, target.EffectiveMaxHp)), DeathSaves = DeathSaveTally.Zero },
            DeathSaveState.Stable => target with { DeathSaves = DeathSaveTally.Stabilized },
            DeathSaveState.Dead => target with
            {
                Hp = 0, TempHp = 0, Dead = true, KnockedOut = false, Concentrating = false, DeathSaves = new DeathSaveTally(0, 3, false),
            },
            _ => target with { DeathSaves = new DeathSaveTally(successes, failures, false) },
        };

        return new DeathSaveOutcome(
            target,
            after,
            face,
            value,
            success,
            FailuresAdded: success ? 0 : face == 1 ? 2 : 1,
            Stabilized: state == DeathSaveState.Stable,
            Revived: state == DeathSaveState.Revived,
            Died: state == DeathSaveState.Dead,
            State: state);
    }

    /// <summary>
    /// First aid and the like (contract §5.4, §5.6; the Wisdom (Medicine) check, when one is needed, is the caller's).
    /// A creature at 0 HP becomes stable, both tallies 0, still unconscious (SRD 5.1 "Stabilizing a Creature"; SRD 5.2
    /// the same; also Spare the Dying and a Healer's Kit). A creature knocked out under the 2024 rules (1 HP, Unconscious)
    /// wakes instead: SRD 5.2.1 "Knocking Out a Creature" — it "remains Unconscious until it regains any Hit Points or
    /// until someone uses an action to administer first aid to it". Without this a tracker or a sheet could end that
    /// knock-out only by healing, and the record would keep an Unconscious the table had ended. Nothing changes for the
    /// dead or for a conscious creature.
    /// </summary>
    public static StabilizeOutcome Stabilize(HitPointState target)
    {
        Check(target);
        if (target.Dead)
        {
            return new StabilizeOutcome(target, target, Stabilized: false, EndedKnockOut: false);
        }

        if (target.KnockedOut)
        {
            return new StabilizeOutcome(target, target with { KnockedOut = false }, Stabilized: false, EndedKnockOut: true);
        }

        return target.Hp > 0
            ? new StabilizeOutcome(target, target, Stabilized: false, EndedKnockOut: false)
            : new StabilizeOutcome(target, target with { DeathSaves = DeathSaveTally.Stabilized }, Stabilized: !target.DeathSaves.Stable, EndedKnockOut: false);
    }
}

/// <summary>What first aid (<see cref="CombatRules.Stabilize"/>) did; neither flag when nothing changed.</summary>
/// <param name="Stabilized">It was dying at 0 HP and is stable now (still Unconscious).</param>
/// <param name="EndedKnockOut">A 2024 knock-out ended: it is conscious at its 1 HP; the caller ends Unconscious, and Prone stays.</param>
public sealed record StabilizeOutcome(HitPointState Before, HitPointState After, bool Stabilized, bool EndedKnockOut);

/// <summary>
/// What one death save did. <see cref="Total"/> is what was compared with 10 (the given total, or the face with 2024's
/// Exhaustion penalty); a natural 1 or 20 decides regardless.
/// </summary>
/// <param name="FailuresAdded">0 on a success, 1, or 2 on a natural 1.</param>
/// <param name="Stabilized">The third success: stable, tallies reset.</param>
/// <param name="Revived">A natural 20: 1 HP, conscious (the caller ends Unconscious; Prone stays), tallies reset.</param>
/// <param name="Died">The third failure.</param>
/// <param name="State">The simulator's state for the same step (<see cref="DeathSaveState"/>), for agreement.</param>
public sealed record DeathSaveOutcome(
    HitPointState Before, HitPointState After, int? Face, int Total, bool Success, int FailuresAdded, bool Stabilized, bool Revived, bool Died, DeathSaveState State);

/// <summary>
/// A d20 roll the server makes for a rule: the mode after Advantage and Disadvantage cancel, the flat modifier, and the
/// dice expression to roll and log (<c>1d20+5</c>, <c>2d20kh1+3</c>, <c>2d20kl1-2</c>), so the logged total is the rule's
/// total.
/// </summary>
public sealed record D20RollPlan(D20Mode Mode, int Modifier, string Expression)
{
    /// <summary>The plan for a mode and a modifier.</summary>
    public static D20RollPlan Of(D20Mode mode, int modifier) => new(mode, modifier, CombatRules.D20Expression(mode, modifier));
}
