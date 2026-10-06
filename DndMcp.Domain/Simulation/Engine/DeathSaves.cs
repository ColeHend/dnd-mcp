namespace DndMcp.Domain.Simulation;

/// <summary>What one death saving throw leaves a dying creature as.</summary>
public enum DeathSaveState
{
    /// <summary>Still dying: roll again next turn.</summary>
    Dying,

    /// <summary>Three successes: stable at 0 HP, no more saves (until it takes damage).</summary>
    Stable,

    /// <summary>Three failures.</summary>
    Dead,

    /// <summary>A natural 20: it regains 1 hit point (and acts this turn).</summary>
    Revived,
}

/// <summary>
/// The death saving throw (both editions, the same rule): d20 against DC 10, no modifiers; 10 or higher is a success,
/// lower a failure; a natural 1 counts as two failures; a natural 20 restores 1 hit point. Three successes make the
/// creature stable, three failures kill it.
///
/// <para>
/// One pure step function, so the exit-criterion test can enumerate its Markov chain exactly (P(death) = 0.404875,
/// P(stable) = 0.41375, P(natural-20 revive) = 0.181375 for a creature left alone) as well as sampling it through the
/// engine: a wrong threshold or a missing "nat 1 = two failures" fails the enumeration to 1e-12.
/// </para>
/// <para>
/// Public from Phase 7 so the tracker and the sheet share it: <c>CombatRules.DeathSave</c> wraps it with the totals,
/// the 2024 Exhaustion penalty and the reset on becoming stable that the rule adds. <see cref="Step"/> itself is unchanged
/// (it leaves the counts on <see cref="DeathSaveState.Stable"/>; the engine and the wrapper reset them).
/// </para>
/// </summary>
public static class DeathSaves
{
    public const int Dc = 10;

    /// <summary>The counts and state after one save with the natural <paramref name="face"/> (1–20).</summary>
    public static (int Successes, int Failures, DeathSaveState State) Step(int successes, int failures, int face)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(face, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(face, 20);

        if (face == 20)
        {
            return (0, 0, DeathSaveState.Revived);
        }

        if (face >= Dc)
        {
            successes++;
        }
        else
        {
            failures += face == 1 ? 2 : 1;
        }

        return failures >= 3 ? (successes, failures, DeathSaveState.Dead)
            : successes >= 3 ? (successes, failures, DeathSaveState.Stable)
            : (successes, failures, DeathSaveState.Dying);
    }
}
