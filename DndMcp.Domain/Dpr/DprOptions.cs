using System.Globalization;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;

namespace DndMcp.Domain.Dpr;

/// <summary>
/// Horizon wire values (research A9). Strings, not an enum: the host echoes them and Phase 7 stores them with a sheet.
/// </summary>
public static class DprHorizons
{
    /// <summary>One fresh turn: setup costs paid, every resource available, the round's reaction included.</summary>
    public const string Round1 = "round1";

    /// <summary>R rounds from fresh resources; Vex and uses left carry from turn to turn; the mean per round.</summary>
    public const string Fight = "fight";

    /// <summary>
    /// An adventuring day's average, built from fight-horizon runs by <see cref="HorizonEvaluator"/> (not evaluated by
    /// <see cref="DprEngine"/> itself, which refuses it).
    /// </summary>
    public const string Day = "day";

    public static readonly IReadOnlyList<string> All = [Round1, Fight, Day];

    /// <summary>The <c>horizon</c> parameter's vocabulary, matched as the DSL's values are ("Round 1", "nova" and "round-1" all work).</summary>
    public static readonly DslValueSet Set = new(
        "horizon",
        All,
        new Dictionary<string, string> { ["nova"] = Round1, ["encounter"] = Fight, ["combat"] = Fight, ["adventuring day"] = Day });
}

/// <summary>
/// How one evaluation runs. Everything about the build, the target and the rulings is already in the
/// <see cref="ResolvedBuild"/> and <see cref="ResolvedTarget"/>; these are the knobs of the engine itself.
/// </summary>
public sealed record DprOptions
{
    /// <summary><see cref="DprHorizons.Round1"/> or <see cref="DprHorizons.Fight"/>.</summary>
    public string Horizon { get; init; } = DprHorizons.Fight;

    /// <summary>Rounds in a fight (1–10). Ignored by round1, which is one turn.</summary>
    public int Rounds { get; init; } = DprLimits.DefaultRounds;

    /// <summary>
    /// Whether to build the round-1 damage distribution (percentiles, P(0), P(≥ HP)). It costs a second pass that
    /// convolves damage PMFs; a level × AC grid needs only the means and turns it off.
    /// </summary>
    public bool IncludeDistribution { get; init; } = true;

    /// <summary>
    /// Modifiers (by <see cref="ModifierRef.Number"/>) whose resource is treated as unlimited: the day horizon's
    /// "that feature's uses unlimited" run, which measures uses per round and damage per use without running out. A
    /// number without a tracked resource changes nothing.
    /// </summary>
    public IReadOnlyCollection<int> UnlimitedResources { get; init; } = [];

    /// <summary>
    /// Modifiers (by <see cref="ModifierRef.Number"/>) left out of this evaluation: the day horizon's base run "with every
    /// resource-limited feature removed". Only modifiers the engine reads per turn can be removed (riders, extra attacks,
    /// save effects, conditions on hit, advantage sources, power attacks, damage rerolls, and the defensive kinds, whose
    /// removal changes only a setup cost); anything folded into the attacks at resolution (to_hit, bonus_damage,
    /// crit_range, remaps, …) cannot, and is refused as a caller bug.
    /// </summary>
    public IReadOnlyCollection<int> RemovedModifiers { get; init; } = [];

    /// <summary>
    /// Distinct states one turn may reach (<see cref="DprLimits.MaxTurnStates"/>); lowered only by tests, which reach the
    /// limit's message without building a pathological build.
    /// </summary>
    internal int TurnStateLimit { get; init; } = DprLimits.MaxTurnStates;

    public static DprOptions Round1 { get; } = new() { Horizon = DprHorizons.Round1 };

    public static DprOptions Fight(int rounds = DprLimits.DefaultRounds) => new() { Horizon = DprHorizons.Fight, Rounds = rounds };

    /// <summary>The rounds this horizon averages over: 1 for round1.</summary>
    public int RoundsEvaluated => Horizon == DprHorizons.Round1 ? 1 : Rounds;

    /// <summary>Checks the options a caller (ultimately the model) chose.</summary>
    /// <exception cref="DndInputException">An unknown horizon, the day horizon (built on top of this engine), or rounds outside 1–10.</exception>
    internal void Validate()
    {
        if (Horizon == DprHorizons.Day)
        {
            throw new DndInputException(
                "horizon \"day\" is an average over fights: evaluate the fight horizon with and without each limited feature and " +
                "combine them (the day horizon does this); the engine itself evaluates \"round1\" or \"fight\".");
        }

        if (Horizon is not (DprHorizons.Round1 or DprHorizons.Fight))
        {
            throw new DndInputException(
                $"horizon \"{DslText.Echo(Horizon)}\" is not a horizon; use \"round1\" (one turn), \"fight\" (the mean over rounds) or \"day\".");
        }

        if (Horizon == DprHorizons.Fight && Rounds is < DprLimits.MinRounds or > DprLimits.MaxRounds)
        {
            throw new DndInputException(string.Create(CultureInfo.InvariantCulture,
                $"rounds is {Rounds}; a fight is {DprLimits.MinRounds} to {DprLimits.MaxRounds} rounds (3 is the DMG's convention)."));
        }
    }
}
