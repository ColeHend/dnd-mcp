namespace DndMcp.Repository.Campaign.Combat;

/// <summary>
/// The <c>combat</c> tool's actions as they travel on the wire (contract §6.0: 17 actions), shared by the host that binds
/// <c>action</c> and the Repository that names its results (<see cref="CombatOutcome.Action"/>) and picks a monster's
/// edition by action (<see cref="CombatService.MonsterEdition"/>). One spelling in one place: with a literal on each side,
/// a host that passed "Start" where the Repository compares "start" would resolve a planned fight's monsters in the
/// campaign's edition instead of the fight's, with no error to show it.
/// </summary>
public static class CombatActions
{
    /// <summary>A planned fight (no party).</summary>
    public const string Prepare = "prepare";

    /// <summary>A new active fight, or a planned or paused one made active.</summary>
    public const string Start = "start";

    /// <summary>Combatants added to a fight.</summary>
    public const string Add = "add";

    /// <summary>Combatants already in the fight changed (hp, ac, side, hidden…).</summary>
    public const string Set = "set";

    /// <summary>Combatants leave the fight.</summary>
    public const string Leave = "leave";

    /// <summary>Initiative given or rolled.</summary>
    public const string Initiative = "initiative";

    /// <summary>The next turn.</summary>
    public const string Next = "next";

    /// <summary>Back one turn.</summary>
    public const string Prev = "prev";

    /// <summary>Damage dealt.</summary>
    public const string Damage = "damage";

    /// <summary>Hit points (or temporary hit points) restored.</summary>
    public const string Heal = "heal";

    /// <summary>Conditions and named effects added or removed.</summary>
    public const string Condition = "condition";

    /// <summary>Concentration started, dropped or saved.</summary>
    public const string Concentration = "concentration";

    /// <summary>A slot, resource or item spent (or restored).</summary>
    public const string Use = "use";

    /// <summary>A legendary action or Legendary Resistance spent.</summary>
    public const string Legendary = "legendary";

    /// <summary>A death saving throw.</summary>
    public const string DeathSave = "death_save";

    /// <summary>The fight's state (the author's) or the party board (a perspective).</summary>
    public const string State = "state";

    /// <summary>The fight ends and its write-back is logged.</summary>
    public const string End = "end";

    /// <summary>Every action, in the order of contract §6.0.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Prepare, Start, Add, Set, Leave, Initiative, Next, Prev, Damage, Heal, Condition, Concentration, Use, Legendary, DeathSave, State, End,
    ];
}
