using System.Text.Json.Nodes;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using DndMcp.Domain.Rules;
using C = DndMcp.Domain.Simulation.StatBlockValues.Conditions;
using D = DndMcp.Domain.Combat.CombatValues.Durations;
using E = DndMcp.Domain.Combat.CombatValues.ExpiryPoints;
using K = DndMcp.Domain.Combat.CombatValues.ReminderKinds;
using L = DndMcp.Domain.Campaign.CampaignValues.CombatLogKinds;
using P = DndMcp.Domain.Combat.CombatValues.Purposes;

namespace DndMcp.Domain.Combat;

public static partial class CombatTracker
{
    /// <summary>The name <c>condition</c> refuses: hiding is a combatant flag (<c>set {hidden}</c>), not a condition.</summary>
    public const string HiddenName = "hidden";

    /// <summary>The longest condition or effect name.</summary>
    public const int MaxConditionName = 80;

    /// <summary>The effect whose start reminds a concentrating creature to drop its concentration (§5.8).</summary>
    public const string RageName = "rage";

    /// <summary>
    /// The name <c>condition</c> refuses to add (review C06): concentration is the combatant's own state (the
    /// <c>concentration</c> action), and an effect of that name would persist to the sheet, where
    /// <c>remove: ["concentration"]</c> means the sheet's concentration column and could never remove it.
    /// </summary>
    public const string ConcentrationName = "concentration";

    /// <summary>The highest round an <c>end of round</c> duration may name (as a duration's rounds_left is capped, review C16).</summary>
    public const int MaxRound = 10_000;

    // -------------------------------------------------------------------------------------------------------------------
    // condition

    private static CombatStepResult Condition(EncounterState state, ConditionOp op)
    {
        RequireActive(state, "condition");
        var targets = CombatAddressing.ResolveMany(state.Combatants, op.Targets, "targets");
        var adding = op.Add is { Count: > 0 };
        var removing = op.Remove is { Count: > 0 };
        if (adding == removing)
        {
            throw new DndInputException("condition needs add (a list of names to add) or remove (a list to remove), one of the two.");
        }

        Range("level", op.Level, 1, CombatRules.ExhaustionDeathLevel);
        Range("dc", op.Dc, 1, 40);
        string? ability = null;
        if (op.Ability is { } a && !DslValues.Abilities.Set.TryMatch(a, out ability))
        {
            throw new DndInputException($"ability \"{DslText.Echo(a)}\" is not an ability; give str, dex, con, int, wis or cha.");
        }

        var w = new CombatWork(state);
        if (removing)
        {
            RemoveConditions(w, targets, op);
            return w.Finish();
        }

        var names = op.Add!.Select((n, i) => ConditionName(n, $"add item {N(i + 1)}")).ToList();
        if (names.Count != names.Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            throw new DndInputException("add names the same condition twice.");
        }

        if (names.Any(n => CampaignText.Key(n) == ConcentrationName))
        {
            var target = new[] { targets[0].Address };
            throw new DndInputException(
                $"concentration is not a condition: start one with {CombatCalls.Combat("concentration", ("targets", target), ("spell", CombatCalls.Fill))} " +
                $"and end it with {CombatCalls.Combat("concentration", ("targets", target), ("drop", true))}.");
        }

        // No source given: the turn-holder imposes it, as a combat call means; a sheet's condition routed into the fight
        // (NoSource) has none, since nobody in the fight imposed it (review F2R09).
        var (sourceCombatant, sourceNote) = op.Source is { } s
            ? CombatAddressing.ResolveSource(state.Combatants, s, "source")
            : op.NoSource ? (null, null) : (state.TurnHolder, null);
        if (op.Resource is not null && names.All(n => n == C.Exhaustion))
        {
            throw new DndInputException("resource is spent while adding an effect; exhaustion spends none.");
        }

        // Every condition planned first: a refusal must come before anything changes.
        var planned = names.Where(n => n != C.Exhaustion)
            .Select(n => PlanCondition(w, n, op, ability, sourceCombatant, sourceNote))
            .ToList();
        foreach (var target in targets)
        {
            foreach (var condition in planned)
            {
                if (w.Get(target.Id).Conditions.Any(x => CampaignText.Key(x.Name) == CampaignText.Key(condition.Name) && x.Source == condition.Source && x.SourceNote == condition.SourceNote))
                {
                    var from = condition.Source is { } id ? $" from {w.Name(id)}" : condition.SourceNote is { } note ? $" from {note}" : string.Empty;
                    throw new DndInputException($"{target.Name} already has {condition.Name}{from}; remove it first to change it.");
                }
            }
        }

        var resourceKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        if (op.Resource is { } resource)
        {
            foreach (var target in targets.Where(t => !t.Removed && !t.Dead))
            {
                resourceKeys[target.Id] = FindResourceKey(target, resource, "resource");
            }
        }

        var noted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            if (target.Removed)
            {
                w.Notes.Add($"no effect: {target.Name} left");
                continue;
            }

            if (target.Dead)
            {
                w.Notes.Add($"no effect: {target.Name} is dead");
                continue;
            }

            if (resourceKeys.TryGetValue(target.Id, out var key))
            {
                Spend(w, target.Id, key, 1);
            }

            var added = new JsonArray();
            if (names.Contains(C.Exhaustion))
            {
                AddExhaustion(w, target.Id, op.Level ?? 1);
            }

            foreach (var given in planned)
            {
                var current = w.Get(target.Id);
                if (current.Dead)
                {
                    break;
                }

                // Unconscious given at 0 HP with no duration is the drop's own (a death-save maker whose Undead Fortitude
                // or Relentless failed: the interceptor reminder's call): zero_hp, so healing wakes it as at any drop. A
                // "fight" Unconscious would outlast the healing (Wake ends only the drop's).
                var condition = given.Name == C.Unconscious && op.Duration is null && current.Hp == 0 && current.MaxHp is not null
                    ? given with { Duration = D.ZeroHp }
                    : given;

                if (condition.IsSrdCondition && CombatRules.IsImmuneToCondition(condition.Name, w.ConditionImmunities(current)))
                {
                    w.Notes.Add($"no effect: {current.Name} is immune to {condition.Name}");
                    continue;
                }

                var skip = condition.Duration switch
                {
                    D.UntilEndOfSourceTurn => state.Round >= 1 && condition.Source == state.TurnCombatantId,
                    D.UntilEndOfTargetTurn => state.Round >= 1 && current.Id == state.TurnCombatantId,
                    _ => false,
                };
                w.AddCondition(current.Id, condition with { SkipEnd = skip });
                added.Add(condition.Name);
                if (!condition.IsSrdCondition && noted.Add(condition.Name))
                {
                    w.Notes.Add($"{condition.Name} is not an SRD condition: tracked as an effect.");
                }

                w.Notes.Add($"{current.Name}: {w.Describe(condition)}, {DurationText(w, condition, skip)}.");
                if (condition.Name == C.Unconscious && !w.Get(current.Id).Has(C.Prone))
                {
                    w.AddCondition(current.Id, new CombatCondition(string.Empty, C.Prone, D.UntilStands) { Applied = condition.Applied });
                    added.Add(C.Prone);
                }

                if (CombatRules.IsIncapacitating(condition.Name))
                {
                    w.Incapacitated(current.Id, condition.Name);
                }
                else if (CampaignText.Key(condition.Name) == RageName && w.Get(current.Id).Concentration is { } held)
                {
                    // §5.8: starting Rage is a reminder, never automatic (a raging creature can't concentrate).
                    w.Remind(K.ConcentrationBroken, current.Id,
                        $"{current.Name} rages while concentrating on {held.Spell}: a raging creature can't concentrate, so drop it",
                        CombatCalls.Combat("concentration", ("targets", new[] { current.Address }), ("drop", true)));
                }

                if (condition.Name == C.Unconscious && w.Get(current.Id) is { } fallen && fallen.Side is CampaignValues.CombatSides.Enemy or CampaignValues.CombatSides.Neutral)
                {
                    w.Defeat(current.Id, "unconscious");
                }
            }

            if (added.Count > 0)
            {
                var detail = new JsonObject { ["added"] = added, ["duration"] = planned.FirstOrDefault()?.Duration };
                if (sourceNote is not null)
                {
                    detail["source_note"] = sourceNote;
                }

                w.Log(L.Condition, sourceCombatant?.Id ?? w.Turn, target.Id, null, detail);
            }
        }

        return w.Finish();
    }

    /// <summary>The canonical SRD condition, "exhaustion", or the effect's name as typed; refusals for the reserved and the bad.</summary>
    /// <param name="field">"add item 2": where an added name sits, for the refusal of one with no letter or digit (null for a removal).</param>
    private static string ConditionName(string? text, string? field = null)
    {
        var name = text?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > MaxConditionName || name.Any(char.IsControl))
        {
            throw new DndInputException($"a condition or effect name is one line of 1 to {N(MaxConditionName)} characters.");
        }

        if (CampaignText.Key(name) == HiddenName)
        {
            throw new DndInputException("hidden is not a condition: hide or reveal a combatant with set {\"hidden\": true}.");
        }

        // Effects are told apart by their key: a name with no letter or digit has the empty key every such name shares
        // (review C02). A removal still finds one (a sheet keeps names as typed), by its exact spelling.
        if (field is not null && CampaignText.Key(name).Length == 0)
        {
            throw new DndInputException(NoLetterOrDigit(field, name, field));
        }

        return CombatConditions.TryMatch(name, out var canonical) ? canonical : name;
    }

    /// <summary>A condition entry is the one a removal names: the same key, or, for a name with no letter or digit, the same spelling.</summary>
    private static bool Names(CombatCondition condition, string name)
    {
        var key = CampaignText.Key(name);
        return key.Length > 0
            ? CampaignText.Key(condition.Name) == key
            : string.Equals(condition.Name.Trim(), name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One condition to add, its duration settled (contract §5.13): the default by name, the given phrase, the source
    /// (a combatant, or a note), the expiry, the save, the escape DC, the concentration that holds it, the effect.
    /// </summary>
    private static CombatCondition PlanCondition(CombatWork w, string name, ConditionOp op, string? ability, CombatantState? source, string? sourceNote)
    {
        var parsed = op.Duration is { } text ? CombatDurations.Parse(text) : new ParsedDuration(DefaultDuration(name, source));
        var duration = parsed.Kind;
        if (duration == D.ZeroHp)
        {
            throw new DndInputException("zero_hp is the Unconscious the tracker adds at 0 HP; give another duration.");
        }

        var applied = new AppliedAt(w.Round, w.Turn);
        ConditionExpiry? expires = null;
        ConditionSave? save = null;
        int? escape = null;
        string? heldBy = null;
        switch (duration)
        {
            case D.UntilStartOfSourceTurn or D.UntilEndOfSourceTurn:
                if (source is null)
                {
                    throw new DndInputException(sourceNote is not null
                        ? $"{duration} needs a source that is a combatant (its turn ends it); \"{DslText.Echo(sourceNote)}\" is not one."
                        : $"{duration} needs a source (its turn ends it): give source.");
                }

                break;
            case D.SaveEnds:
                if (op.Dc is null || ability is null)
                {
                    throw new DndInputException("save_ends needs dc and ability (the save repeated at the end of each of the target's turns).");
                }

                save = new ConditionSave(ability, op.Dc.Value);
                break;
            case D.Rounds:
                // The anchor is the creature whose turn it is; before initiative, the top of the order once it is rolled.
                expires = new ConditionExpiry(Math.Max(w.Round, 1) + parsed.Rounds!.Value, E.Start, w.Round >= 1 ? w.Turn : null);
                break;
            case D.EndOfRound:
                // The phrase's round and round must agree (review C16): "end of round 2" with round 5 used to take 5.
                if (op.Round is { } given && parsed.Round is { } named && given != named)
                {
                    throw new DndInputException($"duration \"{DslText.Echo(op.Duration!)}\" names round {N(named)}, but round is {N(given)}; give the round once.");
                }

                var round = op.Round ?? parsed.Round ?? Math.Max(w.Round, 1);
                Range("round", round, 1, MaxRound);
                if (round < w.Round)
                {
                    throw new DndInputException($"round {N(round)} is already over (it is round {N(w.Round)}); give this round or a later one.");
                }

                expires = new ConditionExpiry(round, E.RoundEnd, null);
                break;
            case D.Concentration:
                if (source is null)
                {
                    throw new DndInputException("concentration needs its source: the combatant concentrating (default the turn-holder).");
                }

                if (w.Get(source.Id).Concentration is null)
                {
                    throw new DndInputException($"{source.Name} is not concentrating: start it with concentration first ({CombatCalls.Combat("concentration", ("targets", new[] { source.Address }), ("spell", CombatCalls.Fill))}).");
                }

                heldBy = source.Id;
                break;
            case D.UntilEscape:
                escape = op.Dc;
                break;
        }

        if (op.Round is not null && duration != D.EndOfRound)
        {
            throw new DndInputException("round goes with the duration \"end of round\".");
        }

        if (save is null && op.Dc is not null && ability is not null && duration is D.Rounds or D.Fight or D.EndOfRound or D.UntilRemoved)
        {
            save = new ConditionSave(ability, op.Dc.Value);
        }

        return new CombatCondition(string.Empty, name, duration)
        {
            Source = source?.Id,
            SourceNote = source is null ? sourceNote : null,
            Expires = expires,
            Save = save,
            EscapeDc = escape,
            HeldBy = heldBy,
            Effect = Effect(op.Effect),
            Applied = applied,
        };
    }

    /// <summary>
    /// The default durations of <c>combat</c> (contract §5.13): grappled, and restrained with a source → until_escape;
    /// prone → until_stands; every other condition and named effect → fight.
    /// </summary>
    private static string DefaultDuration(string name, CombatantState? source) => name switch
    {
        C.Grappled or C.Restrained when source is not null => D.UntilEscape,
        C.Prone => D.UntilStands,
        _ => D.Fight,
    };

    private static CombatEffect? Effect(EffectInput? input)
    {
        if (input is null)
        {
            return null;
        }

        Range("effect ac", input.Ac, -20, 20);
        var effect = new CombatEffect(input.Ac, Types(input.Resist, "resist", true), Types(input.Immune, "immune", true),
            Types(input.Vulnerable, "vulnerable", true), Types(input.Except, "except", false));
        if (effect.Except.Count > 0 && !effect.Resist.Concat(effect.Immune).Concat(effect.Vulnerable).Contains(Rules.DamageAdjustmentEntry.AllTypes))
        {
            throw new DndInputException("effect except narrows an \"all\" entry: give resist, immune or vulnerable [\"all\"] with it.");
        }

        return effect.IsEmpty ? null : effect;

        static IReadOnlyList<string> Types(IReadOnlyList<string>? types, string field, bool allowAll) =>
            (types ?? []).Select(t => allowAll && string.Equals(t?.Trim(), Rules.DamageAdjustmentEntry.AllTypes, StringComparison.OrdinalIgnoreCase)
                ? Rules.DamageAdjustmentEntry.AllTypes
                : DamageType(t, $"effect {field}")!).Distinct(StringComparer.Ordinal).ToList();
    }

    private static string DurationText(CombatWork w, CombatCondition c, bool skip) => c.Duration switch
    {
        D.UntilStartOfSourceTurn => $"until the start of {w.Name(c.Source)}'s next turn",
        D.UntilEndOfSourceTurn => $"until the end of {w.Name(c.Source)}'s {(skip ? "next " : string.Empty)}turn",
        D.UntilStartOfTargetTurn => "until the start of its next turn",
        D.UntilEndOfTargetTurn => $"until the end of its {(skip ? "next " : string.Empty)}turn",
        D.SaveEnds => $"until it saves ({DslValues.Abilities.Display(c.Save!.Ability)} DC {N(c.Save.Dc)} at the end of each of its turns)",
        D.Rounds => c.Expires!.Of is null
            ? $"for {N(c.Expires.Round - 1)} rounds from round 1"
            : $"until the start of {w.Name(c.Expires.Of)}'s turn in round {N(c.Expires.Round)}",
        D.Concentration => $"while {w.Name(c.HeldBy)} concentrates",
        D.UntilEscape => c.EscapeDc is { } dc ? $"until it escapes (DC {N(dc)})" : "until it escapes",
        D.UntilStands => "until it stands",
        D.EndOfRound => $"until the end of round {N(c.Expires!.Round)}",
        D.UntilRemoved => "until removed (it stays on the sheet after the fight)",
        D.ZeroHp => "until it regains hit points (it is at 0 HP)",
        _ => "for the fight",
    } + (c.Save is { } save && c.Duration != D.SaveEnds ? $"; {DslValues.Abilities.Display(save.Ability)} save DC {N(save.Dc)} at the end of each of its turns" : string.Empty);

    /// <summary>
    /// Adds Exhaustion levels (a column, never a condition entry: D4): the maximum may fall (2014 level 4 halves it) and
    /// level 6 kills (§5.9); the call carries the new level's <c>exhaustion_effects</c> line at any round (§6.5).
    /// </summary>
    private static void AddExhaustion(CombatWork w, string id, int levels)
    {
        var c = w.Get(id);
        if (CombatRules.IsImmuneToCondition(C.Exhaustion, w.ConditionImmunities(c)))
        {
            w.Notes.Add($"no effect: {c.Name} is immune to exhaustion");
            return;
        }

        SetExhaustion(w, id, Math.Min(CombatRules.ExhaustionDeathLevel, c.Exhaustion + levels));
    }

    private static void SetExhaustion(CombatWork w, string id, int level)
    {
        var c = w.Get(id);
        var before = c.Exhaustion;
        w.Put(c with { Exhaustion = level });
        w.Log(L.Condition, w.Turn, id, level - before, new JsonObject { ["exhaustion"] = level, ["before"] = before });
        w.Notes.Add($"{c.Name}: exhaustion {N(before)} → {N(level)}.");
        var effects = CombatRules.Exhaustion(level, w.Edition);
        if (level > 0)
        {
            w.Remind(K.ExhaustionEffects, id, $"{c.Name} — {effects.Text}");
        }

        if (w.Get(id).HitPoints(w.Edition) is { } points)
        {
            var max = CombatRules.ApplyMaximum(points);
            if (max.Died)
            {
                w.Die(id, max.DeathCause!);
            }
            else if (max.HpLost > 0)
            {
                w.SetHitPoints(id, max.After);
                w.Notes.Add($"{c.Name}'s hit point maximum is now {N(max.After.EffectiveMaxHp)}: {N(max.Before.Hp)} → {N(max.After.Hp)} HP.");
            }
        }
        else if (level >= CombatRules.ExhaustionDeathLevel)
        {
            w.Die(id, DeathCauses.Exhaustion);
        }
    }

    private static void RemoveConditions(CombatWork w, IReadOnlyList<CombatantState> targets, ConditionOp op)
    {
        if (op.Duration is not null || op.Effect is not null || op.Resource is not null || op.Dc is not null || op.Round is not null)
        {
            throw new DndInputException("remove takes the names (and source, or level for exhaustion); duration, dc, effect, round and resource go with add.");
        }

        var names = op.Remove!.Select(n => ConditionName(n)).ToList();
        var (sourceCombatant, sourceNote) = op.Source is { } s ? CombatAddressing.ResolveSource(w.All, s, "source") : (null, null);
        foreach (var target in targets)
        {
            if (target.Removed)
            {
                w.Notes.Add($"no effect: {target.Name} left");
                continue;
            }

            var removed = new JsonArray();
            foreach (var name in names)
            {
                var c = w.Get(target.Id);
                if (name == C.Exhaustion)
                {
                    if (c.Exhaustion == 0)
                    {
                        w.Notes.Add($"no effect: {c.Name} has no exhaustion");
                        continue;
                    }

                    SetExhaustion(w, c.Id, Math.Max(0, c.Exhaustion - (op.Level ?? 1)));
                    removed.Add(C.Exhaustion);
                    continue;
                }

                var gone = c.Conditions.Where(x => Names(x, name) &&
                    (op.Source is null || (sourceCombatant is not null ? x.Source == sourceCombatant.Id : x.SourceNote == sourceNote))).ToList();

                // The sheet's stored condition of this name a re-seed left shadowed (SheetSnapshot.Shadowed, review F2R03):
                // removing the name without a source removes it too, so the write-back does not put back what was ended.
                var shadowed = op.Source is null && c.SheetSnapshot is { Shadowed.Count: > 0 } snapshot
                    ? snapshot.Shadowed.Where(n => CampaignText.Key(n) == CampaignText.Key(name)).ToList()
                    : [];
                if (gone.Count == 0 && shadowed.Count == 0)
                {
                    w.Notes.Add($"no effect: {c.Name} has no {name}{(op.Source is null ? string.Empty : $" from {op.Source}")}");
                    continue;
                }

                w.Put(c with
                {
                    Conditions = c.Conditions.Where(x => !gone.Contains(x)).ToList(),
                    SheetSnapshot = shadowed.Count == 0 ? c.SheetSnapshot : c.SheetSnapshot! with { Shadowed = c.SheetSnapshot.Shadowed.Except(shadowed).ToList() },
                });
                if (gone.Count == 0)
                {
                    removed.Add(name);
                    w.Notes.Add($"{name} (stored on {c.Name}'s sheet) removed: the sheet keeps it no more once the fight ends.");
                }

                foreach (var condition in gone)
                {
                    removed.Add(condition.Name);
                    w.Notes.Add($"{w.Describe(condition)} removed from {c.Name}.");
                }

                if (gone.Any(x => x.Name == C.Unconscious) && w.Get(c.Id) is { Hp: > 0 })
                {
                    w.Undefeat(c.Id);
                }
            }

            if (removed.Count > 0)
            {
                w.Log(L.Condition, w.Turn, target.Id, null, new JsonObject { ["removed"] = removed });
            }
        }
    }

    // -------------------------------------------------------------------------------------------------------------------
    // concentration

    private static IReadOnlyList<RollNeed> ConcentrationNeeds(EncounterState state, ConcentrationOp op)
    {
        var target = PlanConcentration(state, op);
        if (target.Removed || op.Spell is not null || op.Drop || op.Total is not null || op.Face is not null)
        {
            return [];
        }

        var plan = CombatRules.ConcentrationRoll(CombatWork.ConSaveBonus(target), target.Exhaustion, state.Ruleset);
        return [new RollNeed($"concentration:{target.Id}", target.Id, P.ConcentrationSave, plan.Expression)];
    }

    private static CombatantState PlanConcentration(EncounterState state, ConcentrationOp op)
    {
        RequireActive(state, "concentration");
        var target = One(state, op.Targets, "concentration");
        var modes = (op.Spell is not null ? 1 : 0) + (op.Drop ? 1 : 0) + (op.Total is not null || op.Face is not null ? 1 : 0);
        if (modes > 1)
        {
            throw new DndInputException("concentration does one thing: start (spell), drop (true), or resolve a save (total or face; neither: rolled here).");
        }

        if (op.Total is not null && op.Face is not null)
        {
            throw new DndInputException("give the save's total OR the kept d20 face, not both.");
        }

        if ((op.SlotLevel is not null || op.Duration is not null) && op.Spell is null)
        {
            throw new DndInputException("slot_level and duration go with spell (starting a concentration).");
        }

        Range("face", op.Face, 1, 20);
        Range("total", op.Total, -30, 70);
        if (target.Removed)
        {
            return target;
        }

        if (target.Dead)
        {
            throw new DndInputException($"{target.Name} is dead: it concentrates on nothing.");
        }

        if (op.Spell is { } spell)
        {
            if (spell.Trim().Length == 0 || spell.Length > MaxConditionName || spell.Any(char.IsControl))
            {
                throw new DndInputException($"spell is one line of 1 to {N(MaxConditionName)} characters.");
            }

            if (target.Incapacitated || target.Hp == 0)
            {
                throw new DndInputException($"{target.Name} is {(target.Hp == 0 ? "at 0 HP" : "incapacitated")}: it cannot concentrate.");
            }

            if (op.Duration is { } duration && CombatDurations.Parse(duration).Kind != D.Rounds)
            {
                throw new DndInputException("a concentration lasts a number of rounds: give duration as \"1 minute\", \"10 minutes\", \"1 hour\" or \"N rounds\" (or leave it out: it then ends with the fight).");
            }
        }
        else if (target.Concentration is not { } held)
        {
            throw new DndInputException($"{target.Name} is not concentrating{(op.Drop ? string.Empty : ": no concentration save is due")}.");
        }
        else if (!op.Drop && held.Pending.Count == 0)
        {
            throw new DndInputException($"no concentration save is due for {target.Name} ({held.Spell}): a save is owed after damage.");
        }

        return target;
    }

    private static CombatStepResult Concentration(EncounterState state, ConcentrationOp op, IReadOnlyDictionary<string, RolledValue> rolls)
    {
        var target = PlanConcentration(state, op);
        var w = new CombatWork(state);
        if (target.Removed)
        {
            w.Notes.Add($"no effect: {target.Name} left");
            return w.Finish();
        }

        if (op.Spell is { } spell)
        {
            if (op.SlotLevel is { } level)
            {
                Spend(w, target.Id, SlotKey(target, level), 1);
            }

            if (w.Get(target.Id).Concentration is not null)
            {
                w.EndConcentration(target.Id, K.ConcentrationBroken, $"it started concentrating on {spell.Trim()}");
            }

            var rounds = op.Duration is { } duration ? CombatDurations.Parse(duration).Rounds : null;
            var concentration = new CombatConcentration(spell.Trim())
            {
                Level = op.SlotLevel,
                Duration = rounds is null ? null : D.Rounds,
                Expires = rounds is { } n ? new ConditionExpiry(Math.Max(w.Round, 1) + n, E.Start, w.Round >= 1 ? w.Turn : null) : null,
                Applied = new AppliedAt(w.Round, w.Turn),
            };
            w.Put(w.Get(target.Id) with { Concentration = concentration });
            var detail = new JsonObject { ["spell"] = concentration.Spell };
            if (op.SlotLevel is { } slot)
            {
                detail["slot_level"] = slot;
            }

            if (rounds is { } r)
            {
                detail["rounds"] = r;
            }

            w.Log(L.Concentration, target.Id, target.Id, null, detail);
            w.Notes.Add(rounds is null
                ? $"{target.Name} concentrates on {concentration.Spell}; it has no duration, so it ends with the fight: give duration (\"10 minutes\") to keep it past the fight."
                : $"{target.Name} concentrates on {concentration.Spell} for {N(rounds.Value)} rounds (until the start of {w.Name(concentration.Expires!.Of ?? w.Turn)}'s turn in round {N(concentration.Expires.Round)}).");
            return w.Finish();
        }

        if (op.Drop)
        {
            w.Log(L.Concentration, target.Id, target.Id, null, new JsonObject { ["drop"] = true, ["spell"] = target.Concentration!.Spell });
            w.EndConcentration(target.Id, K.ConcentrationBroken, "it dropped it");
            return w.Finish();
        }

        var held = target.Concentration!;
        var dc = held.Pending[0];
        string? rollKey = null;
        int? face = op.Face;
        if (op.Total is null && op.Face is null)
        {
            rollKey = $"concentration:{target.Id}";
            face = D20Face(Rolled(rolls, rollKey), rollKey);
        }

        var bonus = CombatWork.ConSaveBonus(target);
        var save = CombatRules.ConcentrationSave(dc, face, op.Total, bonus, target.Exhaustion, state.Ruleset);
        w.Put(target with { Concentration = held with { Pending = held.Pending.Skip(1).ToList() } });
        var saveDetail = new JsonObject { ["save"] = true, ["dc"] = dc, ["total"] = save.Total, ["success"] = save.Success, ["given"] = rollKey is null };
        if (face is { } f)
        {
            saveDetail["face"] = f;
        }

        w.Log(L.Concentration, target.Id, target.Id, save.Total, saveDetail, rollKey);
        if (save.Success)
        {
            w.Notes.Add($"{target.Name} keeps concentrating on {held.Spell} ({N(save.Total)} against DC {N(dc)}).");
        }
        else
        {
            w.EndConcentration(target.Id, K.ConcentrationBroken, $"failed the save ({N(save.Total)} against DC {N(dc)})");
        }

        return w.Finish();
    }
}
