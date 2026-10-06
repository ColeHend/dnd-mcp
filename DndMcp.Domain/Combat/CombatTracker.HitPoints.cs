using System.Text.Json.Nodes;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using DndMcp.Domain.Rules;
using C = DndMcp.Domain.Simulation.StatBlockValues.Conditions;
using K = DndMcp.Domain.Combat.CombatValues.ReminderKinds;
using L = DndMcp.Domain.Campaign.CampaignValues.CombatLogKinds;
using P = DndMcp.Domain.Combat.CombatValues.Purposes;

namespace DndMcp.Domain.Combat;

public static partial class CombatTracker
{
    // -------------------------------------------------------------------------------------------------------------------
    // damage

    private sealed record HitPart(int? Amount, DamageFormula? Dice, string? Type, string? RollKey);

    private sealed record DamagePlan(
        IReadOnlyList<CombatantState> Targets, IReadOnlySet<string> Half, CombatantState? Source, IReadOnlyList<HitPart> Parts, string? Subject);

    private static IReadOnlyList<RollNeed> DamageNeeds(EncounterState state, DamageOp op)
    {
        var plan = PlanDamage(state, op);
        var purpose = op.Critical ? P.DamageCritical : P.Damage;
        return plan.Parts.Where(p => p.Dice is not null).Select(p => new RollNeed(p.RollKey!, plan.Subject, purpose, p.Dice!.Text)).ToList();
    }

    private static DamagePlan PlanDamage(EncounterState state, DamageOp op)
    {
        RequireActive(state, "damage");
        var targets = CombatAddressing.ResolveMany(state.Combatants, op.Targets, "targets");
        var half = new HashSet<string>(StringComparer.Ordinal);
        if (op.Half is { Count: > 0 })
        {
            foreach (var c in CombatAddressing.ResolveMany(state.Combatants, op.Half, "half"))
            {
                if (!targets.Any(t => t.Id == c.Id))
                {
                    throw new DndInputException($"half names {c.Name}, which is not one of the targets.");
                }

                half.Add(c.Id);
            }
        }

        var source = op.Source is null ? null : CombatAddressing.Resolve(state.Combatants, op.Source, "source");
        var given = (op.Amount is not null ? 1 : 0) + (op.Dice is not null ? 1 : 0) + (op.Parts is not null ? 1 : 0);
        if (given != 1)
        {
            throw new DndInputException("damage needs exactly one of amount (a number), dice (\"2d6+5\", rolled here) or parts ([{amount or dice, type}]).");
        }

        var parts = new List<HitPart>();
        if (op.Parts is { } list)
        {
            if (op.DamageType is not null)
            {
                throw new DndInputException("damage_type goes with amount or dice; with parts, give each part its own type.");
            }

            if (list.Count == 0)
            {
                throw new DndInputException("parts is empty; give at least one {amount or dice, type}.");
            }

            EachItem(list.Count, "damage", i =>
            {
                var part = list[i];
                var where = $"parts item {N(i + 1)}";
                if ((part.Amount is null) == (part.Dice is null))
                {
                    throw new DndInputException($"{where} needs exactly one of amount or dice.");
                }

                parts.Add(Part(part.Amount, part.Dice, DamageType(part.Type, $"{where} type"), op.Critical, $"{where}", list.Count == 1 ? "damage" : $"damage:{N(i + 1)}"));
            });
        }
        else
        {
            parts.Add(Part(op.Amount, op.Dice, DamageType(op.DamageType, "damage_type"), op.Critical, "damage", "damage"));
        }

        return new DamagePlan(targets, half, source, parts, DamageSubject(state, source, targets));
    }

    /// <summary>
    /// The subject of a damage roll (contract §6.10 as amended by F1, review L01): the <c>source</c>; else, while a turn is
    /// running (round 1 or later), the turn-holder — the §6.4 actor, who rolls the damage; else the single target; else
    /// none. Secrecy and the label follow the subject: the target default made a hidden or DM-campaign enemy's own roll
    /// on its turn open under the party target's name, and a PC's swing at an enemy secret under the enemy's. A heal keeps
    /// the target (<see cref="HealSubject"/>).
    /// </summary>
    private static string? DamageSubject(EncounterState state, CombatantState? source, IReadOnlyList<CombatantState> targets) =>
        source?.Id ?? (state.Round >= 1 && state.TurnHolder is { } actor ? actor.Id : null) ?? (targets.Count == 1 ? targets[0].Id : null);

    /// <summary>
    /// The subject of a heal or temporary hit points roll (contract §6.10): the <c>source</c>, else the single target — the
    /// creature healed, usually on its own side — never the turn-holder: fixture B's B16 heals the Aboleth on the monk's
    /// turn with no source and must stay secret as "Aboleth: heal" (§16).
    /// </summary>
    private static string? HealSubject(CombatantState? source, IReadOnlyList<CombatantState> targets) =>
        source?.Id ?? (targets.Count == 1 ? targets[0].Id : null);

    private static HitPart Part(int? amount, string? dice, string? type, bool critical, string where, string key)
    {
        if (amount is { } a)
        {
            Range($"{where} amount", a, 0, CombatRules.MaxDamagePart);
            return new HitPart(a, null, type, null);
        }

        var formula = DamageFormula.ParseDamage(dice, $"{where} dice");
        if (!formula.HasDice)
        {
            throw new DndInputException($"{where} dice \"{DslText.Echo(dice)}\" has no dice; give the number as amount instead.");
        }

        // A server roll's critical doubles the dice ("2d6+5" rolls "4d6+5"); a given amount is taken as given (§6.4).
        return new HitPart(null, critical ? formula.ScaleDice(2) : formula, type, key);
    }

    private static CombatStepResult Damage(EncounterState state, DamageOp op, IReadOnlyDictionary<string, RolledValue> rolls)
    {
        var plan = PlanDamage(state, op);
        var w = new CombatWork(state);
        var amounts = plan.Parts.Select(p => (Amount: p.Amount ?? Math.Max(0, Rolled(rolls, p.RollKey!).Total), p.Type, p.RollKey)).ToList();
        var rollKey = amounts.Select(a => a.RollKey).FirstOrDefault(k => k is not null);
        var given = rollKey is null;
        var actor = plan.Source ?? w.TurnHolder;

        // The turn-holder's lines are already in every step's context (§6.3); a source acting outside its turn (a
        // legendary action, a reaction) gets its own here, so each line is said once.
        if (actor is not null && actor.Id != state.TurnCombatantId)
        {
            ActorLines(w, actor);
        }

        foreach (var target in plan.Targets)
        {
            var current = w.Get(target.Id);
            if (current.Removed)
            {
                w.Notes.Add($"no effect: {current.Name} left");
                continue;
            }

            if (current.Dead)
            {
                w.Notes.Add($"no effect: {current.Name} is dead");
                continue;
            }

            if (current.Conditions.Any(c => CombatConditions.MakesCrits(c.Name)))
            {
                w.Remind(K.UnconsciousCrit, current.Id, $"{current.Name} is {(current.Has(C.Unconscious) ? C.Unconscious : C.Paralyzed)}: {CombatConditions.UnconsciousCrit(w.Edition)}");
            }

            var request = new DamageRequest
            {
                Parts = amounts.Select(a => new DamageInstancePart(a.Amount, a.Type)).ToList(),
                Critical = op.Critical,
                Magical = op.Magical,
                Half = plan.Half.Contains(current.Id),
                Raw = op.Raw,
                KnockOut = op.KnockOut,
            };

            // Each rolled part names the roll it came from ("roll": its RollNeed key): the row cites the first part's roll
            // (roll_id), and every other part's roll is cited by its own note row (CiteRolls), so no dice_roll goes unlinked.
            var detail = new JsonObject
            {
                ["parts"] = new JsonArray(amounts.Select(a => (JsonNode)PartDetail(a.Amount, a.Type, a.RollKey)).ToArray()),
                ["given"] = given,
            };
            Flag(detail, "critical", op.Critical);
            Flag(detail, "magical", op.Magical);
            Flag(detail, "half", request.Half);
            Flag(detail, "raw", op.Raw);
            Flag(detail, "knock_out", op.KnockOut);

            if (current.HitPoints(w.Edition) is not { } points)
            {
                UnknownHpDamage(w, current, request, detail, actor?.Id, rollKey);
                continue;
            }

            var interceptors = current.StatBlock is { } block ? StatBlockFacts.DeathInterceptors(block, RelentlessUsed(current)) : [];
            var outcome = CombatRules.Damage(points, request, w.Adjustments(current), interceptors);
            detail["arithmetic"] = outcome.Arithmetic;
            detail["total"] = outcome.Total;
            if (outcome.TempAbsorbed > 0)
            {
                detail["temp_absorbed"] = outcome.TempAbsorbed;
            }

            if (outcome.Rulings.Count > 0)
            {
                detail["rulings"] = new JsonArray(outcome.Rulings.Select(r => (JsonNode)r).ToArray());
            }

            w.Log(L.Damage, actor?.Id, current.Id, outcome.Total, detail, rollKey);
            w.Notes.Add($"{current.Name}: {outcome.Arithmetic}");
            ApplyDamage(w, current.Id, outcome);
        }

        return w.Finish();
    }

    private static JsonObject PartDetail(int amount, string? type, string? rollKey)
    {
        var part = new JsonObject { ["amount"] = amount, ["type"] = type };
        if (rollKey is not null)
        {
            part["roll"] = rollKey;
        }

        return part;
    }

    private static void Flag(JsonObject detail, string key, bool value)
    {
        if (value)
        {
            detail[key] = true;
        }
    }

    /// <summary>
    /// The attacker's attack-affecting conditions (contract §6.4): blinded, frightened, poisoned, prone, restrained, 2024
    /// grappled, and exhaustion (2014 level 3 or more: Disadvantage; 2024: −2 per level).
    /// </summary>
    private static void ActorLines(CombatWork w, CombatantState actor)
    {
        var state = w.State();
        foreach (var condition in actor.Conditions.Where(c => CombatConditions.AttackAffecting(w.Edition).Contains(c.Name)))
        {
            w.Remind(K.ConditionEffects, actor.Id, CombatContext.ConditionLine(state, actor, condition, w.Edition));
        }

        if (actor.Exhaustion > 0 && (w.Is2024 || actor.Exhaustion >= 3))
        {
            w.Remind(K.ExhaustionEffects, actor.Id, $"{actor.Name} — {CombatRules.Exhaustion(actor.Exhaustion, w.Edition).Text}");
        }
    }

    /// <summary>The hit points of the stand-in state an unknown-HP target's damage runs through: far above any total, so it never drops.</summary>
    private const int UnknownHpStandIn = int.MaxValue / 2;

    /// <summary>
    /// D17 with the whole §6.4 pipeline: a combatant with unknown hit points takes damage exactly as one with known hit
    /// points would (its stat block's or sheet's defences, active effects, Petrified, <c>raw</c>, <c>magical</c>, half),
    /// its temporary hit points absorb first, and what gets past them is added to <c>damage_taken</c>; it is never defeated
    /// automatically, and a concentration save is owed at the DC of the total before temporary hit points
    /// (<c>concentration_on_pre_temp_damage</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the shared rules, with a stand-in.</b> Most unknown-HP combatants are a player campaign's PCs seeded from
    /// minimal sheets (fixture A's four) and its monsters: their defences and effects are known even when their hit points
    /// are not. Skipping the pipeline would make Rage halve nothing, a vulnerable mummy take half of what it should, the
    /// concentration DC come from the wrong total, and a temporary-HP buffer the hit consumed be written back to the
    /// sheet unspent. <see cref="CombatRules.Damage"/> needs a hit-point state, so it is given one far above any damage
    /// (it never drops, so no drop, death or knock-out rule can fire); only the adjusted total, the temporary hit points it
    /// absorbed and the concentration DC are read back. The stand-in's own "hp → hp" never reaches the arithmetic line.
    /// </para>
    /// <para>
    /// <b>What is said.</b> The author's line shows the arithmetic (adjustments included); no REMINDER names the
    /// adjustments (D17: the table knows its resistances, the tracker does not mention them).
    /// </para>
    /// </remarks>
    private static void UnknownHpDamage(CombatWork w, CombatantState c, DamageRequest request, JsonObject detail, string? actorId, string? rollKey)
    {
        var standIn = new HitPointState
        {
            Edition = w.Edition,
            Hp = UnknownHpStandIn,
            MaxHp = UnknownHpStandIn,
            TempHp = c.TempHp,
            MakesDeathSaves = c.MakesDeathSaves,
            Concentrating = c.Concentration is not null,
        };
        var outcome = CombatRules.Damage(standIn, request with { KnockOut = false }, w.Adjustments(c));
        var arithmetic = outcome.Arithmetic;
        var standInTail = $"; {N(outcome.Before.Hp)} → {N(outcome.After.Hp)}";
        if (arithmetic.EndsWith(standInTail, StringComparison.Ordinal))
        {
            arithmetic = arithmetic[..^standInTail.Length];
        }

        var taken = (int)Math.Min(MaxHp * 100L, (long)c.DamageTaken + outcome.HpLost);
        w.Put(c with { DamageTaken = taken, TempHp = outcome.After.TempHp });
        detail["arithmetic"] = arithmetic;
        detail["total"] = outcome.Total;
        if (outcome.TempAbsorbed > 0)
        {
            detail["temp_absorbed"] = outcome.TempAbsorbed;
        }

        if (outcome.Rulings.Count > 0)
        {
            detail["rulings"] = new JsonArray(outcome.Rulings.Select(r => (JsonNode)r).ToArray());
        }

        detail["damage_taken"] = taken;
        w.Log(L.Damage, actorId, c.Id, outcome.Total, detail, rollKey);
        w.Notes.Add($"{c.Name}: {arithmetic} (hit points not tracked: {N(taken)} damage taken so far).");
        if (outcome.ConcentrationDc is { } dc && w.Get(c.Id).Concentration is not null)
        {
            OweConcentrationSave(w, c.Id, dc);
        }
    }

    /// <summary>Applies one damage outcome of the shared rules to a combatant: every flag becomes state and reminders.</summary>
    private static void ApplyDamage(CombatWork w, string id, DamageOutcome outcome)
    {
        var before = w.Get(id);
        w.SetHitPoints(id, outcome.After);
        if (outcome.Died)
        {
            w.Die(id, outcome.DeathCause!);
            return;
        }

        if (outcome.Before.KnockedOut && !outcome.After.KnockedOut)
        {
            // A 2024 knock-out dropped to 0: its Unconscious now lasts until it regains hit points.
            var c = w.Get(id);
            w.Put(c with { Conditions = c.Conditions.Select(x => x.KnockOut ? x with { KnockOut = false } : x).ToList() });
        }

        if (outcome.KnockedOut)
        {
            w.FallUnconscious(id, knockOut2024: w.Is2024);
            var how = w.Is2024 ? "knocked out at 1 HP: unconscious, not dying; it wakes when healed or given first aid" : "knocked out at 0 HP: unconscious and stable";
            w.Remind(K.Dropped, id, $"{before.Name} is {how}");
            w.Incapacitated(id, "knocked out");
            w.Defeat(id, "knocked out");
        }
        else if (outcome.Intercepted)
        {
            foreach (var trait in outcome.Interceptors)
            {
                var dc = trait.SaveDc(outcome.Total) is { } save ? $" (Constitution save DC {N(save)})" : string.Empty;
                // Each call once (review C13): the text names the one for a failure; the reminder's own call, printed after
                // the text, is the one for "it holds".
                var holds = CombatCalls.Combat("set", ("combatants", new[] { CombatCalls.Object(("name", before.Name), ("hp", 1)) }));
                var fails = before.MakesDeathSaves
                    ? CombatCalls.Combat("condition", ("targets", new[] { before.Address }), ("add", new[] { C.Unconscious }))
                    : CombatCalls.Combat("damage", ("targets", new[] { before.Address }), ("amount", 1), ("raw", true));
                w.Remind(
                    K.DeathInterceptor,
                    id,
                    $"{before.Name} drops to 0 HP, but {trait.Name} may keep it up{dc}: {trait.Text} If it fails: {fails}; if it holds:",
                    holds);
            }

            w.Defeat(id, $"held at 0 HP by {string.Join(", ", outcome.Interceptors.Select(i => i.Name))}");
        }
        else if (outcome.FellUnconscious)
        {
            w.FallUnconscious(id, knockOut2024: false);
            w.Remind(K.Dropped, id, $"{before.Name} drops to 0 HP: unconscious, dying ({CombatContext.Tallies(w.Get(id).DeathSaves)}). {CombatContext.DeathSaveProcedure(w.Edition)}");
            w.Incapacitated(id, "unconscious");
            w.Defeat(id, "at 0 HP");
        }
        else if (outcome.Dropped)
        {
            w.Remind(K.Dropped, id, $"{before.Name} drops to 0 HP");
            w.Defeat(id, "at 0 HP");
        }

        if (outcome.DeathSaveFailures > 0)
        {
            w.Remind(
                K.Dying,
                id,
                $"{before.Name}: 0 HP, {CombatContext.Tallies(w.Get(id).DeathSaves)} ({(outcome.DeathSaveFailures == 2 ? "a critical hit: two death save failures" : "one death save failure")})");
        }

        if (outcome.StableLost)
        {
            w.Remind(K.Dying, id, $"{before.Name} is no longer stable: dying again");
        }

        if (outcome.ConcentrationBroken && w.Get(id).Concentration is not null)
        {
            w.EndConcentration(id, K.ConcentrationBroken, "it dropped");
        }

        if (outcome.ConcentrationDc is { } concentrationDc && w.Get(id).Concentration is not null)
        {
            OweConcentrationSave(w, id, concentrationDc);
        }

        if (outcome.BecameBloodied)
        {
            var c = w.Get(id);
            w.Remind(K.Bloodied, id, $"{c.Name} is Bloodied ({N(c.Hp ?? 0)}/{N(c.EffectiveMaxHp(w.Edition) ?? 0)})");
        }
    }

    /// <summary>A concentration save is due: its DC joins the pending list and the reminder carries the resolving call.</summary>
    private static void OweConcentrationSave(CombatWork w, string id, int dc)
    {
        var c = w.Get(id);
        var concentration = c.Concentration!;
        w.Put(c with { Concentration = concentration with { Pending = [.. concentration.Pending, dc] } });
        var bonus = CombatWork.ConSaveBonus(c);
        w.Remind(
            K.ConcentrationSave,
            id,
            $"{c.Name}: concentration save DC {N(dc)} to keep {concentration.Spell} (Con save {(bonus >= 0 ? "+" : "−")}{N(Math.Abs(bonus))})",
            CombatCalls.Combat("concentration", ("targets", new[] { c.Address }), ("total", CombatCalls.Fill)));
    }

    // -------------------------------------------------------------------------------------------------------------------
    // heal

    private sealed record HealPlan(IReadOnlyList<CombatantState> Targets, CombatantState? Source, int? Amount, DamageFormula? Dice, CombatItem? Item, CombatantState? Holder, string? Subject);

    private static IReadOnlyList<RollNeed> HealNeeds(EncounterState state, HealOp op)
    {
        var plan = PlanHeal(state, op);
        return plan.Dice is null ? [] : [new RollNeed("heal", plan.Subject, op.Temp ? P.TemporaryHitPoints : P.Heal, plan.Dice.Text)];
    }

    private static HealPlan PlanHeal(EncounterState state, HealOp op)
    {
        RequireActive(state, "heal");
        var targets = CombatAddressing.ResolveMany(state.Combatants, op.Targets, "targets");
        var source = op.Source is null ? null : CombatAddressing.Resolve(state.Combatants, op.Source, "source");
        if ((op.Amount is null) == (op.Dice is null))
        {
            throw new DndInputException("heal needs exactly one of amount (a number) or dice (\"2d4+2\", rolled here).");
        }

        Range("amount", op.Amount, 0, CombatRules.MaxDamagePart);
        DamageFormula? dice = null;
        if (op.Dice is { } text)
        {
            dice = DamageFormula.ParseDamage(text, "dice");
            if (!dice.HasDice)
            {
                throw new DndInputException($"dice \"{DslText.Echo(text)}\" has no dice; give the number as amount instead.");
            }
        }

        CombatItem? item = null;
        CombatantState? holder = null;
        if (op.Item is { } itemName)
        {
            if (targets.Count != 1)
            {
                throw new DndInputException("heal with item takes exactly one target (one potion heals one creature).");
            }

            if (op.Temp)
            {
                throw new DndInputException("heal with item heals; leave out temp.");
            }

            holder = source is { IsSheetSeeded: true } ? source : targets[0];
            (item, _) = FindItem(holder, itemName, op.Holdings, 1, "item");
        }

        return new HealPlan(targets, source, op.Amount, dice, item, holder, HealSubject(source, targets));
    }

    private static CombatStepResult Heal(EncounterState state, HealOp op, IReadOnlyDictionary<string, RolledValue> rolls)
    {
        var plan = PlanHeal(state, op);
        var w = new CombatWork(state);
        var rollKey = plan.Dice is null ? null : "heal";
        var amount = plan.Amount ?? Math.Max(0, Rolled(rolls, "heal").Total);
        var actor = plan.Source ?? w.TurnHolder;
        var applied = false;
        foreach (var target in plan.Targets)
        {
            var c = w.Get(target.Id);
            if (c.Removed)
            {
                w.Notes.Add($"no effect: {c.Name} left");
                continue;
            }

            if (!c.Dead)
            {
                applied = true;
            }

            var detail = new JsonObject { ["amount"] = amount, ["given"] = rollKey is null };
            if (op.Temp)
            {
                if (c.Dead)
                {
                    w.Notes.Add($"no effect: {c.Name} is dead");
                    continue;
                }

                var granted = CombatRules.GrantTempHp(c.TempHp, amount);
                w.Put(c with { TempHp = granted });
                if (c.TempHp > 0)
                {
                    detail["rulings"] = new JsonArray(RulingFlags.TempHpKeepHigher);
                }

                w.Log(L.TempHp, actor?.Id, c.Id, amount, detail, rollKey);
                w.Notes.Add($"{c.Name}: temporary HP {N(c.TempHp)} → {N(granted)}{(c.TempHp > 0 ? " (the higher kept)" : string.Empty)}.");
                continue;
            }

            if (c.Dead)
            {
                w.Notes.Add($"no effect: {c.Name} is dead (a dead creature regains no hit points until it is revived)");
                continue;
            }

            if (c.HitPoints(w.Edition) is not { } points)
            {
                var taken = Math.Max(0, c.DamageTaken - amount);
                w.Put(c with { DamageTaken = taken });
                detail["damage_taken"] = taken;
                w.Log(L.Heal, actor?.Id, c.Id, amount, detail, rollKey);
                w.Notes.Add($"{c.Name}: heals {N(amount)} (hit points not tracked: {N(taken)} damage taken now).");
                continue;
            }

            var outcome = CombatRules.Heal(points, amount);
            var wasZero = c.Hp == 0;
            w.SetHitPoints(c.Id, outcome.After);
            if (wasZero && outcome.After.Hp > 0)
            {
                MarkRelentlessUsed(w, c.Id);
            }

            if (outcome.Woke || outcome.EndedKnockOut)
            {
                w.Wake(c.Id);
                w.Remind(K.Revived, c.Id, $"{c.Name} regains consciousness at {N(outcome.After.Hp)} HP (still prone)");
            }
            else
            {
                w.Undefeat(c.Id);
            }

            detail["regained"] = outcome.Regained;
            w.Log(L.Heal, actor?.Id, c.Id, outcome.Regained, detail, rollKey);
            w.Notes.Add($"{c.Name}: heals {N(amount)}{(outcome.Regained < amount ? $" ({N(outcome.Regained)} regained, the maximum is {N(outcome.After.EffectiveMaxHp)})" : string.Empty)}; {N(points.Hp)} → {N(outcome.After.Hp)}");
        }

        // The potion is used up only when it healed someone: poured on a corpse or on someone who left, it stays in the
        // inventory (written at end as current − used, so a phantom use would cost the sheet an item).
        if (plan.Item is { } item)
        {
            if (applied)
            {
                ConsumeItem(w, plan.Holder!.Id, item, 1);
            }
            else
            {
                w.Notes.Add($"{item.Name} was not used: nobody it could heal.");
            }
        }

        return w.Finish();
    }

    /// <summary>
    /// A holding of <paramref name="holder"/>'s character by name (its key, then a unique start), with enough left after the
    /// uses already spent in this fight. Sheet-seeded combatants only (the holdings are the sheet's character's).
    /// </summary>
    private static (CombatItem Item, CombatResource? Used) FindItem(CombatantState holder, string name, IReadOnlyList<CombatItem> holdings, int amount, string field)
    {
        if (!holder.IsSheetSeeded || holder.EntityId is not { } entityId)
        {
            throw new DndInputException($"{field}: {holder.Name} is not seeded from a sheet, so the tracker has no inventory of it; spend the item with campaign_character inventory.");
        }

        var own = holdings.Where(h => h.HolderEntityId == entityId && h.Quantity > 0).ToList();
        var key = CampaignText.Key(name);
        var matches = own.Where(h => CampaignText.Key(h.Name) == key).ToList();
        if (matches.Count == 0)
        {
            matches = own.Where(h => CampaignText.Key(h.Name).StartsWith(key, StringComparison.Ordinal) && key.Length > 0).ToList();
        }

        if (matches.Count != 1)
        {
            var list = own.Count == 0 ? "none" : string.Join(", ", own.Select(h => h.Name));
            throw new DndInputException(matches.Count == 0
                ? $"{field} \"{DslText.Echo(name)}\" is not in {holder.Name}'s inventory (its items: {list})."
                : $"{field} \"{DslText.Echo(name)}\" matches several of {holder.Name}'s items: {string.Join(", ", matches.Select(h => h.Name))}; give the full name.");
        }

        var item = matches[0];
        holder.Resources.TryGetValue(CombatValues.ResourceKeys.Item(item.HoldingId), out var used);
        var left = item.Quantity - (used?.Used ?? 0);
        if (amount > 0 && left < amount)
        {
            throw new DndInputException($"{field}: {holder.Name} has {N(Math.Max(0, left))} {item.Name} left in this fight; cannot use {N(amount)}.");
        }

        if (amount < 0 && (used?.Used ?? 0) < -amount)
        {
            throw new DndInputException($"{field}: {holder.Name} has used {N(used?.Used ?? 0)} {item.Name} in this fight; cannot restore {N(-amount)}.");
        }

        return (item, used);
    }

    /// <summary>Records a consumed holding on the consumer's <c>"item:&lt;holding id&gt;"</c> resource (written at <c>end</c>).</summary>
    private static void ConsumeItem(CombatWork w, string holderId, CombatItem item, int amount)
    {
        var holder = w.Get(holderId);
        var key = CombatValues.ResourceKeys.Item(item.HoldingId);
        holder.Resources.TryGetValue(key, out var existing);
        var used = (existing?.Used ?? 0) + amount;
        w.Put(holder with { Resources = Copy(holder.Resources, key, new CombatResource { Name = item.Name, Used = used }) });
        w.Log(L.Resource, holderId, holderId, amount, new JsonObject { ["key"] = key, ["name"] = item.Name, ["used"] = used });
        w.Notes.Add($"{holder.Name}: {item.Name} {(amount > 0 ? "used" : "restored")} ({N(item.Quantity - used)} left; the inventory is written when the fight ends).");
    }
}
