using DndMcp.Domain.Probability;
using C = DndMcp.Domain.Simulation.StatBlockValues.Conditions;

namespace DndMcp.Domain.Rules;

public static partial class CombatRules
{
    /// <summary>
    /// The initiative count of a 2014 lair action ("On initiative count 20 (losing all initiative ties)", SRD 5.1 Monsters,
    /// not in this server's data). No SRD stat block of either edition has lair actions; only a DM-declared 2014 lair gets
    /// the reminder as the order passes 20, and a 2024 fight never does.
    /// </summary>
    public const int LairActionInitiative = 20;

    /// <summary>
    /// The server's initiative roll for one creature or init group (contract §5.11): d20 + its initiative bonus, in the
    /// mode its state gives, with 2024's −2 × Exhaustion in the modifier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 2024 (SRD 5.2 "Surprise": "Disadvantage on their Initiative roll"; SRD 5.2.1 Incapacitated "If you're Incapacitated
    /// when you roll Initiative, you have Disadvantage", Invisible "you have Advantage", Poisoned "Disadvantage on … ability
    /// checks", Exhaustion −2 × level on D20 Tests): surprised, any condition that is or includes Incapacitated, or Poisoned
    /// → Disadvantage; Invisible → Advantage. 2014 (SRD 5.1: initiative is a Dexterity check): Exhaustion 1 or more or
    /// Poisoned → Disadvantage; surprise costs the first turn instead (the tracker's reminder). Advantage and Disadvantage
    /// cancel (<see cref="D20.Resolve"/>). Frightened (both) gives Disadvantage only while the source is in sight, which
    /// the tracker cannot see: a reminder, not a mode.
    /// </para>
    /// <para>Roll <see cref="D20RollPlan.Expression"/> and give the KEPT face to <see cref="InitiativeTotal"/>.</para>
    /// </remarks>
    /// <param name="conditions">The creature's active condition names (canonical; effects and unknown names are ignored).</param>
    public static InitiativeRollPlan InitiativeRoll(int bonus, string edition, bool surprised, IEnumerable<string>? conditions, int exhaustion)
    {
        CheckExhaustion(exhaustion);
        var is2024 = Is2024(edition);
        var names = (conditions ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var advantage = new List<string>();
        var disadvantage = new List<string>();
        var reminders = new List<string>();
        if (is2024)
        {
            if (surprised)
            {
                disadvantage.Add(InitiativeReasons.Surprised);
            }

            disadvantage.AddRange(names.Where(IsIncapacitating).Select(n => n.ToLowerInvariant()).Order(StringComparer.Ordinal));
            if (names.Contains(C.Invisible))
            {
                advantage.Add(C.Invisible);
            }
        }
        else if (exhaustion >= 1)
        {
            disadvantage.Add($"{InitiativeReasons.Exhaustion} {Num(exhaustion)}");
        }

        if (names.Contains(C.Poisoned))
        {
            disadvantage.Add(C.Poisoned);
        }

        if (names.Contains(C.Frightened))
        {
            reminders.Add("frightened: Disadvantage on the roll while the source of its fear is in sight (give the face or total if it was)");
        }

        var mode = D20.Resolve(advantage.Count > 0, disadvantage.Count > 0);
        return new InitiativeRollPlan(D20RollPlan.Of(mode, bonus - ExhaustionD20Penalty(exhaustion, edition)), advantage, disadvantage, reminders);
    }

    /// <summary>
    /// An initiative total (contract §5.11): a given total is taken as is; a face (given, or the kept die of a server roll)
    /// gets the bonus and, in 2024, −2 × Exhaustion. A given face is the kept die, so no mode applies to it. B4: Björn's
    /// face 8 + 2 − 2 (Exhaustion 1, 2024) = 8.
    /// </summary>
    /// <exception cref="ArgumentException">Neither or both given, a face outside 1-20, or a total that is not a finite number.</exception>
    public static double InitiativeTotal(InitiativeValue value, int bonus, int exhaustion, string edition)
    {
        ArgumentNullException.ThrowIfNull(value);
        CheckExhaustion(exhaustion);
        _ = Is2024(edition);
        Check(value);
        return value.Total ?? value.Face!.Value + bonus - ExhaustionD20Penalty(exhaustion, edition);
    }

    /// <summary>
    /// The one value an init group (identical monsters from one <c>add</c> entry; SRD 5.1 "The GM makes one roll for an
    /// entire group of identical creatures", SRD 5.2 the same) takes: a face or total given for ANY member applies to
    /// every member; the same value given twice is fine; two different values are a <see cref="InitiativeGroupValue.Conflict"/>
    /// (the caller refuses). Null <see cref="InitiativeGroupValue.Value"/> with no conflict: nothing given, one server roll.
    /// </summary>
    public static InitiativeGroupValue GroupValue(IEnumerable<InitiativeValue?> given)
    {
        ArgumentNullException.ThrowIfNull(given);
        InitiativeValue? value = null;
        foreach (var each in given)
        {
            if (each is null)
            {
                continue;
            }

            Check(each);
            if (value is null)
            {
                value = each;
            }
            else if (value != each)
            {
                return new InitiativeGroupValue(null, Conflict: true);
            }
        }

        return new InitiativeGroupValue(value, Conflict: false);
    }

    /// <summary>
    /// The turn order (contract D14): total descending, then the higher initiative bonus, then insertion order
    /// (<c>order_key</c>) — no roll-off, so a scripted fight is exact (<see cref="RulingFlags.InitiativeTiesByBonusThenOrder"/>).
    /// Equal totals between DIFFERENT combatants are flagged as a tie (RAW the GM and players decide: "give totals such
    /// as 14.5 to reorder"); members of one init group act in <c>order_key</c> order and are never flagged among
    /// themselves. Entries without an initiative are the caller's to leave out.
    /// </summary>
    /// <remarks>
    /// An init group stays together: its members (one roll, contract §5.11) take the insertion place of the group's first
    /// member, so a tied combatant whose <c>order_key</c> falls between two members (keys reassigned after a re-join) acts
    /// before or after the whole group, never inside it.
    /// </remarks>
    /// <exception cref="ArgumentException">A total or order key that is not a finite number, or a repeated id.</exception>
    public static InitiativeOrder OrderInitiative(IEnumerable<InitiativeEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var list = entries.ToList();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in list)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (!double.IsFinite(entry.Total) || !double.IsFinite(entry.OrderKey))
            {
                throw new ArgumentException($"Initiative entry {entry.Id}: the total and order key must be finite numbers.", nameof(entries));
            }

            if (!ids.Add(entry.Id))
            {
                throw new ArgumentException($"Initiative entry {entry.Id} is listed twice.", nameof(entries));
            }
        }

        var groupPlace = list
            .Where(e => e.Group is not null)
            .GroupBy(e => (e.Group, e.Total, e.Bonus))
            .ToDictionary(g => g.Key, g => g.Min(e => e.OrderKey));
        var order = list
            .OrderByDescending(e => e.Total)
            .ThenByDescending(e => e.Bonus)
            .ThenBy(e => e.Group is null ? e.OrderKey : groupPlace[(e.Group, e.Total, e.Bonus)])
            .ThenBy(e => e.OrderKey)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToList();
        var ties = order
            .GroupBy(e => e.Total)
            .Where(g => g.Select(e => e.Group ?? "\0" + e.Id).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => new InitiativeTie(g.Key, g.Select(e => e.Id).ToList()))
            .ToList();
        return new InitiativeOrder(order, ties, ties.Count > 0 ? [RulingFlags.InitiativeTiesByBonusThenOrder] : []);
    }

    private static void Check(InitiativeValue value)
    {
        if ((value.Face is null) == (value.Total is null))
        {
            throw new ArgumentException("An initiative value is a face or a total, exactly one.", nameof(value));
        }

        if (value.Face is { } face && face is < 1 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(value), face, "A d20 face is 1-20.");
        }

        if (value.Total is { } total && !double.IsFinite(total))
        {
            throw new ArgumentException("An initiative total must be a finite number.", nameof(value));
        }
    }
}

/// <summary>
/// The reasons <see cref="InitiativeRollPlan"/> gives for its mode, on the wire (the roll's combat_log detail). The
/// conditions are their canonical names (<see cref="Simulation.StatBlockValues.Conditions"/>: "poisoned", "invisible",
/// "stunned", …).
/// </summary>
public static class InitiativeReasons
{
    /// <summary>2024: a surprised creature has Disadvantage on its Initiative roll (SRD 5.2 "Surprise").</summary>
    public const string Surprised = "surprised";

    /// <summary>2014: Exhaustion 1 or more gives Disadvantage on ability checks; the reason reads "exhaustion N".</summary>
    public const string Exhaustion = "exhaustion";
}

/// <summary>A server initiative roll (<see cref="CombatRules.InitiativeRoll"/>) and why its mode is what it is.</summary>
/// <param name="Advantage">What gave Advantage ("invisible").</param>
/// <param name="Disadvantage">What gave Disadvantage ("surprised", "poisoned", "stunned", "exhaustion 1").</param>
/// <param name="Reminders">Conditional effects the tracker cannot decide (Frightened).</param>
public sealed record InitiativeRollPlan(D20RollPlan Roll, IReadOnlyList<string> Advantage, IReadOnlyList<string> Disadvantage, IReadOnlyList<string> Reminders);

/// <summary>A given initiative value: the kept d20 face, or the total, exactly one (decimals such as 14.5 reorder ties).</summary>
public sealed record InitiativeValue(int? Face = null, double? Total = null);

/// <summary>An init group's one value, or a conflict (two members given different values).</summary>
public sealed record InitiativeGroupValue(InitiativeValue? Value, bool Conflict);

/// <summary>One combatant in the order: its total, initiative bonus, insertion order and init group (null when alone).</summary>
public sealed record InitiativeEntry(string Id, double Total, int Bonus, double OrderKey, string? Group = null);

/// <summary>Combatants of different groups at one total, in the order they act.</summary>
public sealed record InitiativeTie(double Total, IReadOnlyList<string> Ids);

/// <summary>The turn order, its flagged ties, and the ruling that broke them.</summary>
public sealed record InitiativeOrder(IReadOnlyList<InitiativeEntry> Order, IReadOnlyList<InitiativeTie> Ties, IReadOnlyList<string> Rulings);
