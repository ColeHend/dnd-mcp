using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Simulation;

/// <summary>
/// Everything about a run that every fight shares, compiled once and read-only across threads: the creatures, the
/// entries they came from, the fight's rules and the policies.
/// </summary>
internal sealed class FightSetup
{
    public required CombatantTemplate[] Templates { get; init; }

    /// <summary>Per entry (party entries first): the ids of its creatures. Identical creatures of one entry share an initiative roll.</summary>
    public required int[][] Entries { get; init; }

    /// <summary>"2014" or "2024": surprise, exhaustion, the concentration DC cap and 2024 Grappled's penalty follow it.</summary>
    public required string Edition { get; init; }

    public bool Is2024 => Edition == V.Editions.E2024;

    public required string Surprise { get; init; }

    public required int RoundCap { get; init; }

    public required string PartyTargeting { get; init; }

    public required string EnemyTargeting { get; init; }

    public required string LegendaryResistance { get; init; }

    public required string Healing { get; init; }

    public required bool FinishDowned { get; init; }

    public required bool PcsWinTies { get; init; }

    /// <summary>
    /// A resumed fight's turn order (creature ids, <see cref="FightResume"/>), used instead of rolling initiative; null rolls
    /// it. Skipping the roll entirely (not just its result) matters: the roll draws a die and a tiebreak per entry, so a
    /// half skip would shift every later draw.
    /// </summary>
    public int[]? FixedOrder { get; init; }

    /// <summary>The position in the order whose turn the first round starts at (a resume's turn-holder; 0 in a fresh fight).</summary>
    public int StartAt { get; init; }

    /// <summary>The round the fight starts in, for the log (the round cap and the reported rounds count it as 1).</summary>
    public int StartRound { get; init; } = 1;

    /// <summary>
    /// Some creature starts from a live state (<see cref="CombatantTemplate.Start"/>) or the order is fixed: the fight is
    /// checked for a side already beaten before its first turn. A fresh fight never is, so its first turn is untouched.
    /// </summary>
    public bool Seeded { get; init; }

    /// <summary>The dummy harness: fixed turn order, the build always attacks dummy 0, the fight never ends early.</summary>
    public bool Dummy { get; init; }

    /// <summary>The harness: the chance Cleave finds its second creature on a turn (the closed form's second_target_rate).</summary>
    public double SecondTargetRate { get; init; }

    /// <summary>The harness: the id of Cleave's second creature (no starting condition), or −1.</summary>
    public int CleaveDummy { get; init; } = -1;

    /// <summary>The harness: how many dummies share the target's stats (a save effect's target count).</summary>
    public int MainDummies { get; init; } = 1;

    public string Targeting(int side) => side == 0 ? PartyTargeting : EnemyTargeting;
}

/// <summary>How one fight ended.</summary>
internal readonly record struct FightOutcome(string Outcome, int Rounds);
