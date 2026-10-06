using System.Text.Json.Nodes;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using DndMcp.Domain.Rules;
using K = DndMcp.Domain.Combat.CombatValues.ReminderKinds;
using L = DndMcp.Domain.Campaign.CampaignValues.CombatLogKinds;
using P = DndMcp.Domain.Combat.CombatValues.Purposes;
using R = DndMcp.Domain.Combat.CombatValues.ResourceKeys;

namespace DndMcp.Domain.Combat;

public static partial class CombatTracker
{
    /// <summary>The most uses one <c>use</c> spends or restores.</summary>
    public const int MaxUseAmount = 20;

    // -------------------------------------------------------------------------------------------------------------------
    // use

    private static CombatStepResult Use(EncounterState state, UseOp op)
    {
        RequireActive(state, "use");
        var target = One(state, op.Targets, "use");
        var given = (op.SlotLevel is not null ? 1 : 0) + (op.Pact ? 1 : 0) + (op.Resource is not null ? 1 : 0) + (op.Item is not null ? 1 : 0);
        if (given != 1)
        {
            throw new DndInputException("use needs exactly one of slot_level (1-9), pact (true), resource (its name) or item (from the combatant's own inventory), and amount (default 1; negative restores).");
        }

        // |amount| as a long: int.MinValue has no positive int, and Math.Abs would throw instead of refusing.
        if (op.Amount == 0 || Math.Abs((long)op.Amount) > MaxUseAmount)
        {
            throw new DndInputException($"amount is {N(op.Amount)}; give 1 to {N(MaxUseAmount)} uses spent, or a negative number to restore.");
        }

        var w = new CombatWork(state);
        if (target.Removed)
        {
            w.Notes.Add($"no effect: {target.Name} left");
            return w.Finish();
        }

        if (op.Item is { } itemName)
        {
            var (item, _) = FindItem(target, itemName, op.Holdings, op.Amount, "item");
            ConsumeItem(w, target.Id, item, op.Amount);
            return w.Finish();
        }

        if (op.Pact && !target.Resources.ContainsKey(R.Pact))
        {
            throw new DndInputException($"{target.Name} has no Pact Magic slots tracked; give the sheet its pact slots, or leave pact out.");
        }

        var key = op.Pact ? R.Pact : op.SlotLevel is { } level ? SlotKey(target, level) : FindResourceKey(target, op.Resource!, "resource");
        Spend(w, target.Id, key, op.Amount);
        return w.Finish();
    }

    /// <summary>The key of a spell slot level: the sheet's <c>"slot:N"</c>, else a 2014 monster's pool <c>"pool:slot:N"</c>.</summary>
    private static string SlotKey(CombatantState c, int level)
    {
        Range("slot_level", level, 1, 9);
        if (c.Resources.ContainsKey(R.Slot(level)))
        {
            return R.Slot(level);
        }

        if (c.Resources.ContainsKey(R.PoolSlot(level)))
        {
            return R.PoolSlot(level);
        }

        var slots = c.Resources.Keys.Select(SlotLevelOf).Where(l => l is not null).Select(l => $"{Characters.SheetUse.Ordinal(l!.Value)}-level").ToList();
        throw new DndInputException(
            $"{c.Name} has no {Characters.SheetUse.Ordinal(level)}-level slots tracked{(slots.Count == 0 ? string.Empty : $" (its slots: {string.Join(", ", slots)})")}; give the sheet its slots, or leave slot_level out.");
    }

    /// <summary>The spell level of a sheet slot key ("slot:3") or a 2014 monster's pool ("pool:slot:3"), else null.</summary>
    internal static int? SlotLevelOf(string key)
    {
        var rest = key.StartsWith(R.PoolPrefix, StringComparison.Ordinal) ? key[R.PoolPrefix.Length..] : key;
        return rest.StartsWith(R.SlotPrefix, StringComparison.Ordinal) &&
               int.TryParse(rest.AsSpan(R.SlotPrefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var level)
            ? level
            : null;
    }

    /// <summary>
    /// What the author calls a slot resource (U13): "3rd-level slot", "3rd-level Pact Magic slot". Its key ("slot:3",
    /// "pool:slot:3", "pact") is the stored shape, never a word to read.
    /// </summary>
    internal static string SlotName(string key, CombatResource resource) =>
        key == R.Pact
            ? resource.Level is { } pact ? $"{Characters.SheetUse.Ordinal(pact)}-level Pact Magic slot" : "Pact Magic slot"
            : SlotLevelOf(key) is { } level ? $"{Characters.SheetUse.Ordinal(level)}-level slot" : resource.Name ?? key;

    /// <summary>
    /// A resource by name: a sheet resource (its slug, its name's key, or a unique start of a slug), then a stat block's
    /// limited use by its action name. Slots, pact, pools and items are named by their own arguments.
    /// </summary>
    private static string FindResourceKey(CombatantState c, string name, string field)
    {
        var slug = Characters.SheetResource.KeyOf(name);
        if (slug.Length > 0 && c.Resources.ContainsKey(slug))
        {
            return slug;
        }

        var key = CampaignText.Key(name);
        var named = c.Resources.Where(p => !p.Key.Contains(':') && p.Key != R.Pact && CampaignText.Key(p.Value.Name) == key).ToList();
        if (named.Count == 1)
        {
            return named[0].Key;
        }

        var limited = c.Resources.Where(p => p.Key.StartsWith(R.LimitedPrefix, StringComparison.Ordinal) &&
            CampaignText.Key(p.Value.Name ?? p.Key[R.LimitedPrefix.Length..]) == key).ToList();
        if (limited.Count == 1)
        {
            return limited[0].Key;
        }

        var prefixed = slug.Length == 0 ? [] : c.Resources.Where(p => !p.Key.Contains(':') && p.Key != R.Pact && p.Key.StartsWith(slug, StringComparison.Ordinal)).ToList();
        if (prefixed.Count == 1)
        {
            return prefixed[0].Key;
        }

        var names = c.Resources.Where(p => !p.Key.StartsWith(R.ItemPrefix, StringComparison.Ordinal))
            .Select(p => p.Value.Name ?? p.Key).ToList();
        throw new DndInputException(prefixed.Count > 1
            ? $"{field} \"{DslText.Echo(name)}\" matches several of {c.Name}'s resources: {string.Join(", ", prefixed.Select(p => p.Value.Name ?? p.Key))}; give the full name."
            : $"{field} \"{DslText.Echo(name)}\" is not one of {c.Name}'s resources ({(names.Count == 0 ? "it has none" : string.Join(", ", names))}).");
    }

    /// <summary>
    /// Spends (negative: restores) uses of one resource (contract §6.6): never beyond what is left, never below none used.
    /// A recharge use is spent once (not ready until it recharges) and restored once.
    /// </summary>
    private static void Spend(CombatWork w, string id, string key, int amount)
    {
        var c = w.Get(id);
        var resource = c.Resources[key];
        var name = resource.Name ?? (key.StartsWith(R.LimitedPrefix, StringComparison.Ordinal) ? key[R.LimitedPrefix.Length..] : SlotName(key, resource));
        CombatResource after;
        string left;
        if (resource.Kind == CombatValues.ResourceKinds.Recharge)
        {
            if (Math.Abs(amount) != 1)
            {
                throw new DndInputException($"{name} recharges: spend it with amount 1, mark it recharged with amount -1.");
            }

            if (amount > 0 && resource.Ready == false)
            {
                throw new DndInputException($"{c.Name}'s {name} is spent until it recharges (roll a d6 at the start of its turn).");
            }

            after = resource with { Ready = amount < 0 };
            left = amount > 0 ? "spent until it recharges" : "recharged";
        }
        else if (resource.Max is { } max)
        {
            var used = (resource.Used ?? 0) + amount;
            if (used > max)
            {
                throw new DndInputException($"{c.Name}'s {name}: only {N(max - (resource.Used ?? 0))} of {N(max)} left; cannot spend {N(amount)}.");
            }

            if (used < 0)
            {
                throw new DndInputException($"{c.Name}'s {name}: only {N(resource.Used ?? 0)} used; cannot restore {N(-amount)}.");
            }

            after = resource with { Used = used };
            left = $"{N(max - used)}/{N(max)} left";
        }
        else
        {
            throw new DndInputException($"{c.Name}'s {name} is a tracker (state \"{resource.State}\"), not counted uses: change its state on the sheet.");
        }

        w.Put(c with { Resources = Copy(c.Resources, key, after) });
        w.Log(L.Resource, id, id, amount, new JsonObject { ["key"] = key, ["name"] = name, ["before"] = Used(resource), ["after"] = Used(after) });
        w.Notes.Add($"{c.Name}: {name} {(amount > 0 ? $"{N(amount)} used" : $"{N(-amount)} restored")}, {left}.");
    }

    private static JsonNode? Used(CombatResource r) => r.Kind == CombatValues.ResourceKinds.Recharge ? JsonValue.Create(r.Ready == true) : JsonValue.Create(r.Used ?? 0);

    // -------------------------------------------------------------------------------------------------------------------
    // legendary

    private static CombatStepResult Legendary(EncounterState state, LegendaryOp op)
    {
        RequireActive(state, "legendary");
        var c = CombatAddressing.Resolve(state.Combatants, op.Source, "source");
        var w = new CombatWork(state);
        if (c.Removed)
        {
            w.Notes.Add($"no effect: {c.Name} left");
            return w.Finish();
        }

        if (c.Dead)
        {
            throw new DndInputException($"{c.Name} is dead: it takes no legendary actions.");
        }

        if (op.Resistance)
        {
            if (op.Amount is not null && op.Amount != 1)
            {
                throw new DndInputException("resistance spends one Legendary Resistance; leave out amount.");
            }

            if (c.Legendary is not { Resistance: > 0 } l)
            {
                throw new DndInputException($"{c.Name} has no Legendary Resistance.");
            }

            if (l.ResistanceLeft == 0)
            {
                throw new DndInputException($"{c.Name} has no Legendary Resistance left (0/{N(l.Resistance)} today).");
            }

            var spent = l with { ResistanceUsed = l.ResistanceUsed + 1 };
            w.Put(c with { Legendary = spent });
            w.Log(L.Legendary, c.Id, c.Id, 1, new JsonObject { ["resistance"] = true, ["left"] = spent.ResistanceLeft });
            w.Notes.Add($"{c.Name} uses Legendary Resistance: it succeeds instead ({N(spent.ResistanceLeft)}/{N(spent.Resistance)} left).");
            return w.Finish();
        }

        if (c.Legendary is not { Actions: > 0 } legendary)
        {
            throw new DndInputException($"{c.Name} has no legendary actions.");
        }

        if (state.Round < 1)
        {
            throw new DndInputException("legendary actions are taken after another creature's turn; roll initiative first.");
        }

        if (c.Id == state.TurnCombatantId)
        {
            throw new DndInputException($"{c.Name} takes legendary actions only after ANOTHER creature's turn; it is {c.Name}'s own turn now.");
        }

        if (c.Incapacitated)
        {
            throw new DndInputException($"{c.Name} is incapacitated: it takes no legendary actions.");
        }

        if (c.Surprised && state.Ruleset == DslValues.Editions.E2014)
        {
            throw new DndInputException($"{c.Name} is surprised: no legendary actions until after its first turn.");
        }

        var action = op.Name is { } name ? c.StatBlock?.Legendary?.Actions.FirstOrDefault(a => CampaignText.Key(a.Name) == CampaignText.Key(name)) : null;
        var cost = op.Amount ?? action?.LegendaryCost ?? 1;
        Range("amount", cost, 1, Math.Max(1, legendary.Actions));
        if (cost > legendary.ActionsLeft)
        {
            throw new DndInputException($"{c.Name} has only {N(legendary.ActionsLeft)} of {N(legendary.Actions)} legendary actions left; this costs {N(cost)}.");
        }

        var after = legendary with { Used = legendary.Used + cost };
        w.Put(c with { Legendary = after });
        var detail = new JsonObject { ["left"] = after.ActionsLeft };
        if (op.Name is not null)
        {
            detail["name"] = op.Name;
        }

        w.Log(L.Legendary, c.Id, c.Id, cost, detail);
        w.Notes.Add($"{c.Name}: legendary action{(op.Name is null ? string.Empty : $" {op.Name}")} ({N(cost)}), {N(after.ActionsLeft)}/{N(after.Actions)} left.");

        // Its damage is rolled on another creature's turn: with no source the roll is the turn-holder's (§6.10), a PC's open
        // roll in a DM campaign for an enemy's Lash (review LR03). The call that follows names the creature.
        w.Remind(
            K.OffTurnRoll,
            c.Id,
            $"{c.Name} acts outside its turn (legendary action{(op.Name is null ? string.Empty : $" {op.Name}")}): give its damage \"source\", or the roll is " +
            $"{state.TurnHolder?.Name ?? "the turn-holder"}'s:",
            OffTurnDamageCall(c.Address));
        return w.Finish();
    }

    /// <summary>
    /// <c>combat {"action": "damage", "targets": […], "dice": …, "source": "aboleth"}</c>: the damage call of a creature acting
    /// outside its own turn (a legendary or lair action), naming it as the source (<paramref name="source"/> null: "…", the
    /// caller's to fill), so the roll is its own — secret and labelled with its safe name where its side or visibility
    /// asks — never the turn-holder's (review LR03).
    /// </summary>
    internal static string OffTurnDamageCall(string? source) =>
        CombatCalls.Combat("damage", ("targets", new[] { CombatCalls.Fill }), ("dice", CombatCalls.Fill), ("source", source ?? CombatCalls.Fill));

    // -------------------------------------------------------------------------------------------------------------------
    // death save

    private static IReadOnlyList<RollNeed> DeathSaveNeeds(EncounterState state, DeathSaveOp op)
    {
        var target = PlanDeathSave(state, op);
        return target.Removed || op.Stable || op.Face is not null || op.Total is not null
            ? []
            : [new RollNeed($"death_save:{target.Id}", target.Id, P.DeathSave, CombatRules.DeathSaveRoll(target.HitPoints(state.Ruleset)!).Expression)];
    }

    private static CombatantState PlanDeathSave(EncounterState state, DeathSaveOp op)
    {
        RequireActive(state, "death_save");
        var target = One(state, op.Targets, "death_save");
        if (target.Removed)
        {
            return target;
        }

        if (!target.HpKnown)
        {
            throw new DndInputException($"{target.Name}'s hit points are not tracked; give them with set first.");
        }

        if (op.Stable && (op.Face is not null || op.Total is not null))
        {
            throw new DndInputException("stable stabilises (first aid); leave out face and total.");
        }

        Range("face", op.Face, 1, 20);
        Range("total", op.Total, -30, 60);
        if (!op.Stable && !target.Dying)
        {
            throw new DndInputException($"{target.Name} is not dying (death saves are made at 0 HP, not stable, not dead).");
        }

        return target;
    }

    private static CombatStepResult DeathSave(EncounterState state, DeathSaveOp op, IReadOnlyDictionary<string, RolledValue> rolls)
    {
        var target = PlanDeathSave(state, op);
        var w = new CombatWork(state);
        if (target.Removed)
        {
            w.Notes.Add($"no effect: {target.Name} left");
            return w.Finish();
        }

        var points = target.HitPoints(state.Ruleset)!;
        if (op.Stable)
        {
            var outcome = CombatRules.Stabilize(points);
            w.SetHitPoints(target.Id, outcome.After);
            if (outcome.EndedKnockOut)
            {
                w.Wake(target.Id);
                w.Remind(K.Revived, target.Id, $"{target.Name} wakes (first aid ends the knock-out; still prone)");
            }
            else if (outcome.Stabilized)
            {
                w.Notes.Add($"{target.Name}: stabilised (first aid): death saves {CombatContext.Tallies(points.DeathSaves)} → {CombatContext.Tallies(outcome.After.DeathSaves)}.");
                w.Remind(K.Stable, target.Id, $"{target.Name} is stable at 0 HP (unconscious, no more death saves unless it takes damage)");
            }
            else
            {
                w.Notes.Add($"no effect: {target.Name} is {(target.Dead ? "dead" : target.Hp > 0 ? "conscious" : "already stable")}");
            }

            w.Log(L.DeathSave, w.Turn, target.Id, null, new JsonObject { ["stable"] = true, ["stabilized"] = outcome.Stabilized, ["ended_knock_out"] = outcome.EndedKnockOut });
            return w.Finish();
        }

        string? rollKey = null;
        int? face = op.Face;
        if (op.Face is null && op.Total is null)
        {
            rollKey = $"death_save:{target.Id}";
            face = D20Face(Rolled(rolls, rollKey), rollKey);
        }

        var save = CombatRules.DeathSave(points, face, op.Total);
        w.SetHitPoints(target.Id, save.After);
        var detail = new JsonObject { ["total"] = save.Total, ["success"] = save.Success, ["given"] = rollKey is null };
        if (face is { } f)
        {
            detail["face"] = f;
        }

        detail["successes"] = save.After.DeathSaves.Successes;
        detail["failures"] = save.After.DeathSaves.Failures;
        w.Log(L.DeathSave, target.Id, target.Id, save.Total, detail, rollKey);

        // Every outcome says what it changed (U06), the ones that end dying too: the field and both values, as other steps do.
        var said = $"{target.Name}: death save {(save.Success ? "succeeds" : face == 1 ? "fails twice" : "fails")} ({(save.Revived ? "a natural 20" : face == 1 ? "a natural 1" : N(save.Total))}): ";
        var tallies = $"death saves {CombatContext.Tallies(points.DeathSaves)} → {CombatContext.Tallies(save.Died ? new DeathSaveTally(0, 3, false) : save.After.DeathSaves)}";
        if (save.Died)
        {
            w.Notes.Add($"{said}{tallies}; dead.");
            w.Die(target.Id, DeathCauses.DeathSaveFailures);
        }
        else if (save.Revived)
        {
            w.Notes.Add($"{said}hp {N(points.Hp)} → {N(save.After.Hp)}; {tallies}.");
            w.Wake(target.Id);
            w.Remind(K.Revived, target.Id, $"{target.Name} rolls a natural 20: regains 1 HP and is conscious (still prone); death saves reset");
        }
        else if (save.Stabilized)
        {
            w.Notes.Add($"{said}{tallies}.");
            w.Remind(K.Stable, target.Id, $"{target.Name} is stable at 0 HP (three successes; unconscious until healed)");
        }
        else
        {
            w.Notes.Add($"{said}{CombatContext.Tallies(save.After.DeathSaves)}.");
        }

        return w.Finish();
    }
}
