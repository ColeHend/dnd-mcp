using System.Globalization;
using System.Text.Json.Nodes;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Rules;
using C = DndMcp.Domain.Simulation.StatBlockValues.Conditions;
using D = DndMcp.Domain.Combat.CombatValues.Durations;
using K = DndMcp.Domain.Combat.CombatValues.ReminderKinds;
using L = DndMcp.Domain.Campaign.CampaignValues.CombatLogKinds;

namespace DndMcp.Domain.Combat;

/// <summary>
/// One step's working copy of an encounter: the combatants as they change, the round and turn, and what the step
/// records (combat_log rows, reminders, notes). Every operation of <see cref="CombatTracker"/> mutates one of these and
/// returns <see cref="Finish"/>; nothing outside the step ever sees it, so the tracker stays a pure function of its
/// inputs. The shared mechanics live here so that every operation applies them the same way: a drop, a death, a wake,
/// an incapacitation (concentration and grapples), the end of a concentration and what it holds, a defeat.
/// </summary>
internal sealed class CombatWork
{
    private readonly OrderedDictionary<string, CombatantState> _combatants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TurnRestore> _restore = new(StringComparer.Ordinal);
    private readonly List<string> _automatic = [];

    public CombatWork(EncounterState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Before = state;
        foreach (var c in state.Combatants)
        {
            _combatants[c.Id] = c;
        }

        Round = state.Round;
        Turn = state.TurnCombatantId;
    }

    /// <summary>The state the step started from.</summary>
    public EncounterState Before { get; }

    /// <summary>"2014" or "2024".</summary>
    public string Edition => Before.Ruleset;

    public bool Is2024 => Edition == Features.DslValues.Editions.E2024;

    public int Round { get; set; }

    public string? Turn { get; set; }

    public List<CombatChange> Changes { get; } = [];

    public List<CombatReminder> Reminders { get; } = [];

    public List<string> Notes { get; } = [];

    /// <summary>
    /// Record each touched combatant's automatically-changed fields before the first change (a <c>next</c> does, so a
    /// following <c>prev</c> can restore them exactly).
    /// </summary>
    public bool TrackRestore { get; set; }

    /// <summary>The automatic changes as lines, for a turn row.</summary>
    public IReadOnlyList<string> AutomaticLines => _automatic;

    /// <summary>The before-values of the combatants a tracked step changed.</summary>
    public IReadOnlyList<TurnRestore> Restores => [.. _restore.Values];

    public IReadOnlyList<CombatantState> All => [.. _combatants.Values];

    public CombatantState? TurnHolder => Turn is null ? null : _combatants.GetValueOrDefault(Turn);

    public IReadOnlyList<CombatantState> Order => CombatOrder.Of(_combatants.Values);

    public CombatantState Get(string id) => _combatants[id];

    public CombatantState? Find(string? id) => id is null ? null : _combatants.GetValueOrDefault(id);

    /// <summary>Replaces (or adds) a combatant.</summary>
    public void Put(CombatantState c)
    {
        if (TrackRestore && _combatants.TryGetValue(c.Id, out var old) && !_restore.ContainsKey(c.Id))
        {
            _restore[c.Id] = new TurnRestore(old.Id, old.Conditions, old.Concentration, old.Legendary, old.ReactionUsed, old.Surprised);
        }

        _combatants[c.Id] = c;
    }

    /// <summary>The address calls use for a combatant (<see cref="CombatantState.Address"/>).</summary>
    public string Address(string id) => Get(id).Address;

    /// <summary>The name a line or reminder shows.</summary>
    public string Name(string? id) => id is not null && _combatants.TryGetValue(id, out var c) ? c.Name : "someone";

    /// <summary>A combat_log row in the current round and turn.</summary>
    public void Log(string kind, string? actor, string? target, int? amount, JsonObject? detail, string? rollKey = null) =>
        Changes.Add(new CombatChange(kind, Round, Turn, actor, target, amount, detail is null ? null : CombatJson.Serialize(detail), rollKey));

    public void Remind(string kind, string? combatantId, string text, string? call = null) =>
        Reminders.Add(new CombatReminder(kind, combatantId, text, call));

    /// <summary>An automatic change: a reminder of its kind and a line of the turn row.</summary>
    public void Automatic(string kind, string? combatantId, string text, string? call = null)
    {
        Remind(kind, combatantId, text, call);
        _automatic.Add(text);
    }

    // -------------------------------------------------------------------------------------------------------------------
    // Conditions

    /// <summary>The next unused condition id of a combatant ("c4").</summary>
    public static string NextConditionId(CombatantState c)
    {
        var max = 0;
        foreach (var condition in c.Conditions)
        {
            if (condition.Id.Length > 1 && condition.Id[0] == 'c' &&
                int.TryParse(condition.Id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > max)
            {
                max = n;
            }
        }

        return "c" + (max + 1).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Adds a condition with the next id; the caller has decided it may be added.</summary>
    public CombatCondition AddCondition(string targetId, CombatCondition condition)
    {
        var target = Get(targetId);
        var added = condition with { Id = NextConditionId(target) };
        Put(target with { Conditions = [.. target.Conditions, added] });
        return added;
    }

    /// <summary>
    /// Removes the conditions <paramref name="which"/> selects from every combatant (or only <paramref name="onlyId"/>),
    /// reminding <paramref name="kind"/> for each ("frightened (Mummy) ended on Lieutenant James Torch"), and returns them.
    /// </summary>
    public List<(string HolderId, CombatCondition Condition)> EndConditions(
        Func<CombatantState, CombatCondition, bool> which, string kind, string why, string? onlyId = null, bool automatic = false)
    {
        var ended = new List<(string, CombatCondition)>();
        foreach (var holder in All)
        {
            if (onlyId is not null && holder.Id != onlyId)
            {
                continue;
            }

            var gone = holder.Conditions.Where(c => which(holder, c)).ToList();
            if (gone.Count == 0)
            {
                continue;
            }

            Put(holder with { Conditions = holder.Conditions.Where(c => !gone.Contains(c)).ToList() });
            foreach (var condition in gone)
            {
                ended.Add((holder.Id, condition));
                var text = $"{Describe(condition)} ended on {holder.Name}{(why.Length == 0 ? string.Empty : ": " + why)}";
                if (automatic)
                {
                    Automatic(kind, holder.Id, text);
                }
                else
                {
                    Remind(kind, holder.Id, text);
                }
            }
        }

        return ended;
    }

    /// <summary>"frightened (Mummy)", "Bladesong", "grappled (Aboleth)": a condition with its source's tracker name.</summary>
    public string Describe(CombatCondition condition)
    {
        var source = condition.Source is { } id && Find(id) is { } s ? s.Name : condition.SourceNote;
        return source is null ? condition.Name : $"{condition.Name} ({source})";
    }

    // -------------------------------------------------------------------------------------------------------------------
    // Concentration

    /// <summary>
    /// Ends a combatant's concentration and every condition it holds (contract §5.8: a failed save, incapacitation, death,
    /// leaving, a new concentration, its duration running out).
    /// </summary>
    public void EndConcentration(string id, string kind, string why, bool automatic = false)
    {
        var c = Get(id);
        if (c.Concentration is not { } concentration)
        {
            return;
        }

        Put(c with { Concentration = null });
        var text = $"{c.Name}'s concentration on {concentration.Spell} ended: {why}";
        if (automatic)
        {
            Automatic(kind, id, text);
        }
        else
        {
            Remind(kind, id, text);
        }

        EndConditions((_, cond) => cond.HeldBy == id, kind, $"{c.Name}'s concentration ended", automatic: automatic);
    }

    // -------------------------------------------------------------------------------------------------------------------
    // Hit points, incapacitation, death, defeat

    /// <summary>
    /// What follows a combatant becoming incapacitated (a drop, a knock-out, death, an incapacitating condition): its
    /// concentration ends (§5.8), and every grapple or restraint it holds (<c>until_escape</c>) ends (SRD "The condition
    /// ends if the grappler is incapacitated").
    /// </summary>
    public void Incapacitated(string id, string why)
    {
        var c = Get(id);
        if (c.Concentration is not null)
        {
            EndConcentration(id, K.ConcentrationBroken, why);
        }

        EndConditions((_, cond) => cond.Duration == D.UntilEscape && cond.Source == id, K.GrappleEnded, $"{c.Name} is {why}");
    }

    /// <summary>
    /// A combatant that regained hit points from 0, or whose knock-out ended, wakes: its <c>zero_hp</c> Unconscious (and a
    /// knock-out's) ends; Prone stays (SRD 5.2.1 "When this condition ends, you remain Prone"); a defeated enemy that is up
    /// again is no longer defeated.
    /// </summary>
    public void Wake(string id)
    {
        var c = Get(id);
        var gone = c.Conditions.Where(x => string.Equals(x.Name, C.Unconscious, StringComparison.Ordinal) && (x.Duration == D.ZeroHp || x.KnockOut)).ToList();
        if (gone.Count > 0)
        {
            Put(c with { Conditions = c.Conditions.Where(x => !gone.Contains(x)).ToList() });
        }

        Undefeat(id);
    }

    /// <summary>Clears <c>defeated</c> when the creature has hit points and is conscious again (D16).</summary>
    public void Undefeat(string id)
    {
        var c = Get(id);
        if (c.Defeated && !c.Dead && c.Hp > 0 && !c.Has(C.Unconscious))
        {
            Put(c with { Defeated = false });
            Notes.Add($"{c.Name} is back in the fight (no longer defeated).");
        }
    }

    /// <summary>
    /// Marks an enemy or neutral combatant defeated (D16) when it is not already: a <c>defeat</c> row, the reminder (unless
    /// <paramref name="remind"/> is false: a death says it in its own line), and each revival trait of its stat block quoted
    /// once (contract §6.8 <c>trait</c>). Returns whether it was newly defeated.
    /// </summary>
    public bool Defeat(string id, string how, bool remind = true)
    {
        var c = Get(id);
        if (c.Defeated || c.Side is not (CampaignValues.CombatSides.Enemy or CampaignValues.CombatSides.Neutral))
        {
            return false;
        }

        Put(c with { Defeated = true });
        Log(L.Defeat, Turn, id, null, new JsonObject { ["how"] = how });
        if (remind)
        {
            Remind(K.Defeated, id, $"{c.Name} is defeated ({how}).");
        }

        if (c.StatBlock is { } block)
        {
            foreach (var trait in StatBlockFacts.RevivalTraits(block))
            {
                Remind(K.Trait, id, $"{c.Name} — {trait.Name}: {trait.Text}");
            }
        }

        return true;
    }

    /// <summary>
    /// A combatant died (damage, three failures, exhaustion 6, a maximum of 0): dead, 0 HP, no temporary hit points,
    /// three failures for a death-save maker; it is incapacitated (concentration, grapples) and defeated.
    /// </summary>
    /// <remarks>
    /// <b>One line for one event</b> (review U13): an enemy that dies is defeated by it, so the <c>died</c> reminder says
    /// both ("Mummy 2 dies (dropped to 0 hit points) and is defeated."); a second "is defeated (dead)" line made every kill
    /// two. The <c>defeat</c> row is still written (§6.9), and a creature already defeated (held at 0 by a trait) only dies.
    /// </remarks>
    public void Die(string id, string cause)
    {
        var c = Get(id);
        Put(c with
        {
            Dead = true,
            Hp = c.Hp is null ? null : 0,
            TempHp = 0,
            DeathSaves = c.MakesDeathSaves ? new DeathSaveTally(0, 3, false) : DeathSaveTally.Zero,
        });
        var defeated = !c.Defeated && c.Side is CampaignValues.CombatSides.Enemy or CampaignValues.CombatSides.Neutral;
        Remind(K.Died, id, $"{c.Name} dies ({CauseText(cause)}){(defeated ? " and is defeated" : string.Empty)}.");
        Incapacitated(id, "dead");
        Defeat(id, "dead", remind: false);
    }

    /// <summary>Applies a rules result's hit-point state to a combatant (hp, temporary, tallies, dead).</summary>
    public void SetHitPoints(string id, HitPointState after)
    {
        var c = Get(id);
        Put(c with { Hp = after.Hp, TempHp = after.TempHp, DeathSaves = after.DeathSaves, Dead = after.Dead || c.Dead });
    }

    /// <summary>
    /// A creature that fell unconscious at 0 HP or was knocked out: Unconscious (until its hit points rise above 0, or for
    /// a 2024 knock-out until healed or given first aid) and Prone (until it stands), as the rules add them (§5.4).
    /// </summary>
    public void FallUnconscious(string id, bool knockOut2024)
    {
        var c = Get(id);
        if (!c.Has(C.Unconscious))
        {
            AddCondition(id, new CombatCondition(string.Empty, C.Unconscious, D.ZeroHp)
            {
                KnockOut = knockOut2024,
                Applied = new AppliedAt(Round, Turn),
            });
        }

        if (!Get(id).Has(C.Prone))
        {
            AddCondition(id, new CombatCondition(string.Empty, C.Prone, D.UntilStands) { Applied = new AppliedAt(Round, Turn) });
        }
    }

    /// <summary>
    /// The combatant's damage adjustments (contract §5.1): its stat block or its sheet's defences, every active effect,
    /// and Petrified.
    /// </summary>
    public DamageAdjustments Adjustments(CombatantState c)
    {
        var sets = new List<DamageAdjustments?>();
        if (c.StatBlock is { } block)
        {
            sets.Add(DamageAdjustments.FromStatBlock(block));
        }
        else if (c.SheetSnapshot is { } snapshot)
        {
            sets.Add(DamageAdjustments.FromSheetDefenses(snapshot.Defenses.Resist, snapshot.Defenses.Immune, snapshot.Defenses.Vulnerable));
        }

        foreach (var condition in c.Conditions)
        {
            if (condition.Effect is { } effect && (effect.Resist.Count + effect.Immune.Count + effect.Vulnerable.Count) > 0)
            {
                sets.Add(DamageAdjustments.FromEffect(condition.Name, effect.Resist, effect.Immune, effect.Vulnerable, effect.Except));
            }
        }

        if (c.Has(C.Petrified))
        {
            sets.Add(new DamageAdjustments { Petrified = true });
        }

        return DamageAdjustments.Combine(sets);
    }

    /// <summary>The conditions it cannot gain: its stat block's or sheet's immunities, and Poisoned while Petrified in 2024.</summary>
    public IReadOnlyList<string> ConditionImmunities(CombatantState c)
    {
        var list = new List<string>();
        if (c.StatBlock is { } block)
        {
            list.AddRange(block.ConditionImmunities);
        }
        else if (c.SheetSnapshot is { } snapshot)
        {
            list.AddRange(snapshot.Defenses.ConditionImmune);
        }

        if (Is2024 && c.Has(C.Petrified))
        {
            list.Add(C.Poisoned);
        }

        return list;
    }

    /// <summary>The Constitution save bonus (§5.8): the sheet's (snapshot facts), else the stat block's, else 0.</summary>
    public static int ConSaveBonus(CombatantState c) =>
        c.SheetSnapshot is { } snapshot ? snapshot.ConSaveBonus
        : c.StatBlock is { } block ? CombatRules.SaveBonus(Features.DslValues.Abilities.Con, block)
        : 0;

    // -------------------------------------------------------------------------------------------------------------------
    // Finishing

    /// <summary>The state as it is now.</summary>
    public EncounterState State() => Before with { Round = Round, TurnCombatantId = Turn, Combatants = All };

    /// <summary>
    /// The step's result: the next state, its rows, its own reminders followed by the turn's context reminders (unless
    /// <paramref name="context"/> is false), its notes, and the changed combatants.
    /// </summary>
    public CombatStepResult Finish(bool context = true)
    {
        var next = State();
        var reminders = new List<CombatReminder>(Reminders);
        if (context)
        {
            reminders.AddRange(CombatContext.Reminders(next));
        }

        return new CombatStepResult(next, Changes, reminders, Notes) { ChangedCombatants = EncounterState.ChangedCombatants(Before, next) };
    }

    private static string CauseText(string cause) => cause switch
    {
        DeathCauses.ZeroHp => "dropped to 0 hit points",
        DeathCauses.MassiveDamage => "massive damage: the damage left over was at least its hit point maximum",
        DeathCauses.DamageAtZero => "damage at 0 hit points",
        DeathCauses.DeathSaveFailures => "three failed death saves",
        DeathCauses.Exhaustion => "exhaustion 6",
        DeathCauses.MaxHpZero => "its hit point maximum reached 0",
        _ => cause,
    };
}
