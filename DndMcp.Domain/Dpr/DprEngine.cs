using DndMcp.Domain.Core;
using DndMcp.Domain.Dice;
using DndMcp.Domain.Features;

namespace DndMcp.Domain.Dpr;

/// <summary>
/// Damage per round, exactly (contract §4, research A1–A9): one resolved build against one resolved target over the
/// round1 or fight horizon. The day horizon (<see cref="HorizonEvaluator"/>), level curves and AC grids
/// (<see cref="DprAnalysis"/>) and comparisons (<see cref="DprComparison"/>) are built on top of this.
///
/// <para>
/// <b>How.</b> Each turn is an exact dynamic programme over a small state (<see cref="TurnState"/>): once-per-turn
/// riders, uses left, Vex, conditions imposed this turn, the Bonus Action, the triggers seen. Advantage-rate sources are
/// a mixture over their on/off combinations, sampled once per turn; power attacks set to auto are switched on per sample
/// and carried state to maximise the turn's expected damage. A fight chains turns as a Markov chain over what carries
/// (Vex pending, uses left), each turn's decisions maximising that turn (use_value prices a resource's other uses).
/// </para>
/// <para>
/// <b>What it refuses to guess.</b> Kill triggers (GWM's "reduce a creature to 0 hit points") need the target's HP track
/// and are left to the simulator; a reaction spends no resources; Sap, Nick, Push and Slow report notes rather than
/// damage. Each shows up in <see cref="DprResult.Notes"/> when the build has it (Nick only while the Light weapon's extra
/// attack is still made with the Bonus Action, whichever Light weapon carries Nick. A build whose Attack action already
/// holds that attack, read as two different Light weapon attacks there or an offhand one other than the Nick weapon's,
/// has modelled it, and a bonus_action Light attack beside it (Dual Wielder's) is not noted; see
/// <see cref="ResolvedBuild.UnmodelledNickNote"/>), so a reader never mistakes a convention for a measurement.
/// </para>
/// </summary>
public static class DprEngine
{
    /// <summary>Evaluates with a fresh budgeted meter observing <paramref name="cancellationToken"/>.</summary>
    /// <exception cref="DndInputException">Bad options, or a build too large to compute exactly (with what to reduce).</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static DprResult Evaluate(ResolvedBuild build, ResolvedTarget target, DprOptions? options, CancellationToken cancellationToken) =>
        Evaluate(build, target, options, new WorkMeter(DprLimits.WorkBudget, cancellationToken));

    /// <summary>
    /// The build's damage per round against the target over <see cref="DprOptions.Horizon"/>.
    /// </summary>
    /// <param name="meter">The work budget and cancellation (default: <see cref="DprLimits.WorkBudget"/>, not cancellable).</param>
    /// <exception cref="DndInputException">Bad options, or a build too large to compute exactly (with what to reduce).</exception>
    /// <exception cref="OperationCanceledException">The meter's token was cancelled.</exception>
    public static DprResult Evaluate(ResolvedBuild build, ResolvedTarget target, DprOptions? options = null, WorkMeter? meter = null)
    {
        try
        {
            return Run(build, target, options, meter ?? new WorkMeter(DprLimits.WorkBudget));
        }
        catch (WorkBudgetExceededException ex)
        {
            throw DprLimits.BuildTooLarge(ex);
        }
    }

    /// <summary>
    /// <see cref="Evaluate(ResolvedBuild, ResolvedTarget, DprOptions?, WorkMeter?)"/> without translating an overrun: a
    /// call that runs the engine many times on one meter (a grid, a comparison, the day horizon) says what to reduce in
    /// its own terms, since "reduce the build" is the wrong advice when the fix is fewer levels.
    /// </summary>
    /// <exception cref="WorkBudgetExceededException">The meter ran out.</exception>
    internal static DprResult Run(ResolvedBuild build, ResolvedTarget target, DprOptions? options, WorkMeter meter)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(meter);
        options ??= new DprOptions();
        options.Validate();
        return new DprEvaluation(build, target, options, meter).Run();
    }
}
