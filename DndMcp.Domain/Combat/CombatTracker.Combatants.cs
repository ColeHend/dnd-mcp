using System.Text.Json.Nodes;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using DndMcp.Domain.Rules;
using DndMcp.Domain.Simulation;
using C = DndMcp.Domain.Simulation.StatBlockValues.Conditions;
using K = DndMcp.Domain.Combat.CombatValues.ReminderKinds;
using L = DndMcp.Domain.Campaign.CampaignValues.CombatLogKinds;
using P = DndMcp.Domain.Combat.CombatValues.Purposes;
using S = DndMcp.Domain.Campaign.CampaignValues.CombatSides;

namespace DndMcp.Domain.Combat;

public static partial class CombatTracker
{
    /// <summary>The most copies one <c>add</c> entry makes.</summary>
    public const int MaxCount = 20;

    /// <summary>The most hit points a combatant may have (the sheet's and the simulator's cap).</summary>
    public const int MaxHp = 5000;

    /// <summary>The longest tracker name.</summary>
    public const int MaxNameLength = 80;

    // -------------------------------------------------------------------------------------------------------------------
    // add

    private sealed record AddPlanItem(int Entry, AddEntry Source, string? NewId, string Name, string? Group, double OrderKey, CombatantState? Rejoin);

    private static IReadOnlyList<RollNeed> AddNeeds(EncounterState state, AddOp op) =>
        PlanAdd(state, op)
            .Where(i => i.Rejoin is null && i.Source.Monster is not null && i.Source.Hp?.Kind == CombatValues.HpChoices.Roll)
            .Select(i => new RollNeed($"hp:{i.NewId}", i.NewId, P.HitPoints, i.Source.Monster!.HitDice.Text))
            .ToList();

    private static List<AddPlanItem> PlanAdd(EncounterState state, AddOp op)
    {
        RequireNotEnded(state, "add");
        ArgumentNullException.ThrowIfNull(op.Entries);
        ArgumentNullException.ThrowIfNull(op.NewIds);
        if (op.Entries.Count == 0)
        {
            throw new DndInputException("combatants is empty; give a list of combatants to add, e.g. [{\"srd\": \"2014/monster/mummy\", \"count\": 2}].");
        }

        // Every entry checked first, each entry's problem reported (up to five): nothing is planned from a bad call.
        var entities = new HashSet<string>(StringComparer.Ordinal);
        EachItem(op.Entries.Count, "add", e =>
        {
            var entry = op.Entries[e];
            var where = $"combatants item {N(e + 1)}";
            ValidateEntry(state, entry, where);
            if (entry.EntityId is { } entityId)
            {
                if (!entities.Add(entityId))
                {
                    throw new DndInputException($"{where} adds {entry.EntityName ?? entry.EntityHandle} a second time in one call.");
                }

                if (state.Combatants.FirstOrDefault(c => c.EntityId == entityId) is { Removed: false } existing)
                {
                    throw new DndInputException($"{where}: {entry.EntityHandle ?? entry.EntityName} is already in the fight as {existing.Name}.");
                }
            }
        });

        var items = new List<AddPlanItem>();
        var names = state.Combatants.Select(c => c.Name).ToList();
        var nextKey = state.Combatants.Count == 0 ? 1 : Math.Floor(state.Combatants.Max(c => c.OrderKey)) + 1;
        var ids = 0;
        for (var e = 0; e < op.Entries.Count; e++)
        {
            var entry = op.Entries[e];
            if (entry.EntityId is { } entityId && state.Combatants.FirstOrDefault(c => c.EntityId == entityId) is { } existing)
            {
                // It left earlier: it re-joins as itself (contract §6.2), never as a second combatant.
                items.Add(new AddPlanItem(e, entry, null, existing.Name, null, existing.OrderKey, existing));
                continue;
            }

            var baseName = (entry.Name ?? entry.Monster?.Name ?? entry.EntityName ?? string.Empty).Trim();
            var copies = Copies(names, baseName, entry.Count);
            string? group = null;
            for (var i = 0; i < entry.Count; i++)
            {
                if (ids >= op.NewIds.Count)
                {
                    throw new ArgumentException($"add needs at least {N(ids + 1)} new ids; {N(op.NewIds.Count)} were given.", nameof(op));
                }

                var id = op.NewIds[ids++];
                group ??= entry.Count > 1 ? id : null;
                names.Add(copies[i]);
                items.Add(new AddPlanItem(e, entry, id, copies[i], group, nextKey++, null));
            }
        }

        return items;
    }

    private static void ValidateEntry(EncounterState state, AddEntry entry, string where)
    {
        if (entry.Monster is null && entry.EntityId is null && string.IsNullOrWhiteSpace(entry.Name))
        {
            throw new DndInputException($"{where} gives no source; give srd (a monster), character (a campaign character) or name (a custom combatant).");
        }

        if (entry.Count < 1 || entry.Count > MaxCount)
        {
            throw new DndInputException($"{where} count is {N(entry.Count)}; it is 1 to {N(MaxCount)}.");
        }

        if (entry.EntityId is not null && entry.Count != 1)
        {
            throw new DndInputException($"{where} adds a character {N(entry.Count)} times; a character is added once (count is for copies of a monster or a custom combatant).");
        }

        if (entry.Name is { } name && (name.Trim().Length == 0 || name.Length > MaxNameLength || name.Any(char.IsControl)))
        {
            throw new DndInputException($"{where} name must be one line of 1 to {N(MaxNameLength)} characters.");
        }

        if (entry.Name is { } typed && typed.Trim().EndsWith(CombatAddressing.AllCopies, StringComparison.Ordinal))
        {
            throw new DndInputException($"{where} name may not end in \"*\" (that addresses every copy).");
        }

        // A name is addressed and numbered by its key (letters and digits): one with none ("!!!", "—") has the empty key,
        // which every such name shares, so a second one became a "copy" of the first and damage to "???" landed on "!!!"
        // (review C02).
        if (entry.Name is { } keyless && CampaignText.Key(keyless).Length == 0)
        {
            throw new DndInputException(NoLetterOrDigit($"{where} name", keyless, where));
        }

        if (entry.Side is { } side && !S.Set.TryMatch(side, out _))
        {
            throw new DndInputException($"{where} side \"{DslText.Echo(side)}\" is not a side; give party, ally, enemy or neutral.");
        }

        Range($"{where} ac", entry.Ac, 0, 50);
        Range($"{where} init_bonus", entry.InitBonus, SimulationLimits.MinInitiativeBonus, SimulationLimits.MaxInitiativeBonus);
        var sheetSeeded = entry.EntityId is not null && entry.Sheet is not null && entry.Monster is null;
        if (entry.Hp is { } hp)
        {
            if (sheetSeeded)
            {
                throw new DndInputException(
                    $"{where}: {entry.EntityHandle ?? entry.EntityName} is seeded from its sheet, which gives its hit points; leave out hp (change them with set after adding).");
            }

            if (hp.Kind == CombatValues.HpChoices.Number)
            {
                Range($"{where} hp", hp.Value, 1, MaxHp);
            }
            else if (hp.Kind is CombatValues.HpChoices.Avg or CombatValues.HpChoices.Roll && entry.Monster is null)
            {
                throw new DndInputException($"{where} hp \"{hp.Kind}\" needs a stat block (srd); give a number of hit points or \"unknown\".");
            }
        }
    }

    /// <summary>The refusal of a name with no letter or digit: "give &lt;field&gt; a name with a letter or digit" (the wording the item writer shares).</summary>
    internal static string NoLetterOrDigit(string what, string name, string field) =>
        $"{what} \"{DslText.Echo(name.Trim())}\" has no letter or digit; give {field} a name with a letter or digit.";

    /// <summary>"Mummy", "Mummy 2" … : <paramref name="count"/> names for a base, continuing the numbering of those already in the fight.</summary>
    private static List<string> Copies(IReadOnlyList<string> existing, string baseName, int count)
    {
        var stem = CampaignText.Key(baseName);
        var highest = 0;
        foreach (var name in existing)
        {
            var key = CampaignText.Key(name);
            if (key == stem)
            {
                highest = Math.Max(highest, 1);
            }
            else if (CombatAddressing.IsCopyOf(name, stem) && int.TryParse(key[(stem.Length + 1)..], out var n))
            {
                highest = Math.Max(highest, n);
            }
        }

        var names = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var number = highest + 1 + i;
            names.Add(number == 1 ? baseName : $"{baseName} {N(number)}");
        }

        return names;
    }

    private static CombatStepResult Add(EncounterState state, AddOp op, IReadOnlyDictionary<string, RolledValue> rolls)
    {
        var plan = PlanAdd(state, op);
        var w = new CombatWork(state);
        foreach (var item in plan)
        {
            var entry = item.Source;
            if (item.Rejoin is { } rejoin)
            {
                w.Put(w.Get(rejoin.Id) with { Removed = false, Initiative = null, Hidden = entry.Hidden || rejoin.Hidden });
                w.Log(L.Add, rejoin.Id, rejoin.Id, null, new JsonObject { ["rejoined"] = true });
                w.Notes.Add($"{rejoin.Name} re-joins the fight (no initiative until initiative gives it one).");
                continue;
            }

            var id = item.NewId!;
            var sheetSeeded = entry.EntityId is not null && entry.Sheet is not null && entry.Monster is null;
            CombatantState c;
            string? rollKey = null;
            if (entry.Monster is { } block)
            {
                var hp = entry.Hp ?? (state.PlayerCampaign ? HpChoice.Unknown : HpChoice.Avg);
                int? rolled = null;
                if (hp.Kind == CombatValues.HpChoices.Roll)
                {
                    rollKey = $"hp:{id}";
                    rolled = Rolled(rolls, rollKey).Total;
                }

                c = CombatantFactory.FromStatBlock(block, hp, state.Lair, id, item.Name, rolled, item.OrderKey);
            }
            else if (sheetSeeded)
            {
                c = CombatantFactory.FromSheet(entry.Sheet!, id, item.Name, state.Ruleset, state.Round, item.OrderKey);
            }
            else
            {
                var hp = entry.Hp is { Kind: CombatValues.HpChoices.Number } given ? given.Value : null;
                c = CombatantFactory.Custom(id, item.Name, hp, entry.Ac, entry.InitBonus ?? 0, item.OrderKey);
            }

            var pc = sheetSeeded || string.Equals(entry.EntitySubtype, "pc", StringComparison.OrdinalIgnoreCase);
            var defaultSide = entry.EntityId is not null && entry.Monster is null ? (pc ? S.Party : S.Ally) : S.Enemy;
            c = c with
            {
                EntityId = entry.EntityId ?? c.EntityId,
                EntityHandle = entry.EntityHandle,
                EntitySubtype = entry.EntitySubtype,
                Side = entry.Side is { } side && S.Set.TryMatch(side, out var canonical) ? canonical : defaultSide,
                InitGroup = item.Group,
                Ac = entry.Ac ?? c.Ac,
                InitBonus = entry.InitBonus ?? c.InitBonus,
                Hidden = entry.Hidden,
                MakesDeathSaves = entry.DeathSaves ?? (sheetSeeded || pc),
            };
            w.Put(c);

            var detail = new JsonObject { ["name"] = c.Name, ["side"] = c.Side };
            if (c.SrdRef is not null)
            {
                detail["srd_ref"] = c.SrdRef;
            }

            if (c.HpKnown)
            {
                detail["hp"] = c.Hp;
            }

            if (sheetSeeded)
            {
                detail["sheet_seeded"] = true;
            }

            if (c.InitGroup is not null)
            {
                detail["init_group"] = c.InitGroup;
            }

            w.Log(L.Add, id, id, c.Hp, detail, rollKey);
            w.Notes.Add(AddLine(c, state.Ruleset));
            if (!c.HpKnown && !(state.PlayerCampaign && c.Side is S.Enemy or S.Neutral))
            {
                w.Remind(
                    K.NoHp,
                    id,
                    $"{c.Name} has no hit points: give hp to track them",
                    CombatCalls.Combat("set", ("combatants", new[] { SetTarget(c, ("hp", CombatCalls.Fill)) })));
            }
        }

        return w.Finish();
    }

    /// <summary>"Mummy Lord: 97/97 HP, AC 17, initiative +0, legendary 3/3": what an add shows for a new combatant.</summary>
    private static string AddLine(CombatantState c, string edition)
    {
        var parts = new List<string>
        {
            c.HpKnown ? $"{N(c.Hp!.Value)}/{N(c.EffectiveMaxHp(edition)!.Value)} HP" : "HP not tracked",
        };
        if (c.TempHp > 0)
        {
            parts[0] += $" (+{N(c.TempHp)} temp)";
        }

        if (c.DisplayedAc is { } ac)
        {
            parts.Add($"AC {N(ac)}");
        }

        parts.Add($"initiative {(c.InitBonus >= 0 ? "+" : "−")}{N(Math.Abs(c.InitBonus))}");
        if (c.Legendary is { Actions: > 0 } l)
        {
            parts.Add($"legendary actions {N(l.Actions)}/{N(l.Actions)}");
        }

        if (c.Legendary is { Resistance: > 0 } r)
        {
            parts.Add($"Legendary Resistance {N(r.Resistance)}/{N(r.Resistance)}");
        }

        if (c.IsSheetSeeded)
        {
            parts.Add("from its sheet");
        }

        return $"{c.Name} ({c.Side}): {string.Join(", ", parts)}.";
    }

    /// <summary>A <c>set</c> entry naming a combatant: by its character handle when it has one, else its tracker name.</summary>
    private static IEnumerable<(string Name, object? Value)> SetTarget(CombatantState c, params (string Name, object? Value)[] fields) =>
        CombatCalls.Object([c.EntityHandle is { } handle && c.StatBlock is null ? ("character", handle) : ("name", c.Name), .. fields]);

    // -------------------------------------------------------------------------------------------------------------------
    // set

    private static CombatStepResult Set(EncounterState state, SetOp op)
    {
        RequireNotEnded(state, "set");
        if (op.Entries.Count == 0)
        {
            throw new DndInputException("combatants is empty; give the combatants to change, e.g. [{\"character\": \"character:vars\", \"hp\": 80}].");
        }

        // Every entry checked first, each entry's problem reported (up to five): nothing changes on a bad call.
        EachItem(op.Entries.Count, "set", e =>
        {
            var entry = op.Entries[e];
            var where = $"combatants item {N(e + 1)}";
            var c = CombatAddressing.Resolve(state.Combatants, entry.Combatant, where);
            Range($"{where} hp", entry.Hp, 0, MaxHp);
            Range($"{where} ac", entry.Ac, 0, 50);
            Range($"{where} init_bonus", entry.InitBonus, SimulationLimits.MinInitiativeBonus, SimulationLimits.MaxInitiativeBonus);
            Range($"{where} max_hp_reduction", entry.MaxHpReduction, 0, MaxHp);
            if (entry.Side is { } s && !S.Set.TryMatch(s, out _))
            {
                throw new DndInputException($"{where} side \"{DslText.Echo(s)}\" is not a side; give party, ally, enemy or neutral.");
            }

            if (entry.Hp == 0 && c.MaxHp is null)
            {
                throw new DndInputException($"{where}: {c.Name} has no hit point maximum; give hp ≥ 1 or leave it.");
            }
        });

        var w = new CombatWork(state);
        for (var e = 0; e < op.Entries.Count; e++)
        {
            var entry = op.Entries[e];
            var where = $"combatants item {N(e + 1)}";
            var c = CombatAddressing.Resolve(w.All, entry.Combatant, where);
            var notesAt = w.Notes.Count;
            string? side = null;
            if (entry.Side is { } s)
            {
                S.Set.TryMatch(s, out side);
            }

            var updated = new JsonObject();
            var changed = c with
            {
                Ac = entry.Ac ?? c.Ac,
                InitBonus = entry.InitBonus ?? c.InitBonus,
                Side = side ?? c.Side,
                Hidden = entry.Hidden ?? c.Hidden,
                MakesDeathSaves = entry.DeathSaves ?? c.MakesDeathSaves,
                MaxHpReduction = entry.MaxHpReduction ?? c.MaxHpReduction,
            };
            AddUpdated(updated, "ac", c.Ac, changed.Ac);
            AddUpdated(updated, "init_bonus", c.InitBonus, changed.InitBonus);
            AddUpdated(updated, "side", c.Side, changed.Side);
            AddUpdated(updated, "hidden", c.Hidden, changed.Hidden);
            AddUpdated(updated, "death_saves", c.MakesDeathSaves, changed.MakesDeathSaves);
            AddUpdated(updated, "max_hp_reduction", c.MaxHpReduction, changed.MaxHpReduction);
            w.Put(changed);
            if (entry.Hidden == false && c.Hidden)
            {
                w.Notes.Add($"{c.Name} is revealed (no longer hidden).");
            }

            if (entry.Hp is { } hp)
            {
                SetHp(w, c.Id, hp, updated);
            }

            if (entry.MaxHpReduction is not null && w.Get(c.Id).HitPoints(state.Ruleset) is { } points)
            {
                var max = CombatRules.ApplyMaximum(points);
                if (max.Died)
                {
                    w.Die(c.Id, max.DeathCause!);
                }
                else if (max.HpLost > 0)
                {
                    w.SetHitPoints(c.Id, max.After);
                    w.Notes.Add($"{c.Name}'s hit point maximum is now {N(max.After.EffectiveMaxHp)}: {N(max.Before.Hp)} → {N(max.After.Hp)} HP.");
                }
            }

            w.Log(L.Add, w.Turn, c.Id, null, new JsonObject { ["updated"] = updated });
            w.Notes.Insert(notesAt, updated.Count == 0 ? $"{c.Name}: nothing changed." : $"{c.Name}: {SetChanges(c, updated)}.");
        }

        return w.Finish();
    }

    /// <summary>
    /// What a <c>set</c> entry changed, field by field as the call names them, before → after (review U06: every step says
    /// what it changed; an <c>init_bonus</c> change showed nowhere else): "hp 10 → 7, ac 15 → 17, init_bonus +2 → +4".
    /// </summary>
    private static string SetChanges(CombatantState before, JsonObject updated)
    {
        var parts = new List<string>();
        void Field(string field, string was)
        {
            if (updated[field] is { } now)
            {
                parts.Add($"{field} {was} → {Text(field, now)}");
            }
        }

        Field("hp", before.Hp is { } hp ? N(hp) : "unknown");
        Field("max_hp", before.MaxHp is { } max ? N(max) : "unknown");
        Field("max_hp_reduction", N(before.MaxHpReduction));
        Field("exhaustion", N(before.Exhaustion));
        Field("ac", before.Ac is { } ac ? N(ac) : "unknown");
        Field("init_bonus", Bonus(before.InitBonus));
        Field("side", before.Side);
        Field("hidden", before.Hidden ? "true" : "false");
        Field("death_saves", before.MakesDeathSaves ? "true" : "false");
        return string.Join(", ", parts);

        // The values were stored by AddUpdated (ints, nullable ints, bools, a string): read back through their JSON text.
        static string Text(string field, JsonNode now) => field switch
        {
            "init_bonus" => Bonus(int.Parse(now.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture)),
            "side" => now.GetValue<string>(),
            _ => now.ToJsonString(),
        };

        static string Bonus(int value) => value >= 0 ? $"+{N(value)}" : $"−{N(-value)}";
    }

    private static void AddUpdated<T>(JsonObject updated, string field, T before, T after)
    {
        if (!EqualityComparer<T>.Default.Equals(before, after))
        {
            updated[field] = JsonValue.Create(after);
        }
    }

    /// <summary>
    /// <c>set {hp}</c> (contract §6.2): current HP, and the maximum when there was none or the new value exceeds it, then
    /// capped at the effective maximum. Up from 0 it wakes; down to 0 a death-save maker falls unconscious and dying,
    /// anything else dies. Hit points given to a combatant whose hit points were unknown (D17) replace its running
    /// <c>damage_taken</c>, which is then 0 (the given value already counts it).
    /// </summary>
    /// <remarks>
    /// <b>A dead combatant given hit points is revived</b> (the author's override, as for a Revivify the tracker does not
    /// model): alive, tallies reset, and with one Exhaustion level fewer (SRD 5.1 Exhaustion "being raised from the dead
    /// reduces a creature's exhaustion level by 1"; SRD 5.2.1 Death "If the creature died with any Exhaustion levels, it
    /// returns with 1 fewer level"), so a creature that died of Exhaustion 6 comes back at 5, never still dead by its own
    /// column (the write-back would otherwise give the sheet hit points AND a level that kills). Refused when its hit point
    /// maximum would still be 0 (lower <c>max_hp_reduction</c> first): it would die again at once.
    /// </remarks>
    private static void SetHp(CombatWork w, string id, int hp, JsonObject updated)
    {
        var c = w.Get(id);
        var reviving = c.Dead && hp > 0;
        var exhaustion = reviving && c.Exhaustion > 0 ? c.Exhaustion - 1 : c.Exhaustion;
        var max = c.MaxHp is null || hp > c.MaxHp ? hp : c.MaxHp.Value;
        var effective = HitPointMath.EffectiveMaxHp(max, c.MaxHpReduction, exhaustion, w.Edition);
        if (reviving && effective <= 0)
        {
            throw new DndInputException(
                $"{c.Name} is dead and its hit point maximum would still be 0 (a reduction of {N(c.MaxHpReduction)}); lower max_hp_reduction in the same entry first.");
        }

        var value = Math.Min(hp, effective);
        var wasDown = c.Hp is null or 0 || c.Dead;
        w.Put(c with { MaxHp = max, Hp = value, Dead = c.Dead && value == 0, Exhaustion = exhaustion, DamageTaken = 0 });
        AddUpdated(updated, "hp", c.Hp, (int?)value);
        AddUpdated(updated, "max_hp", c.MaxHp, (int?)max);
        AddUpdated(updated, "exhaustion", c.Exhaustion, exhaustion);
        if (c.DamageTaken > 0)
        {
            w.Notes.Add($"{c.Name}'s hit points are now tracked: the {N(c.DamageTaken)} damage taken so far is replaced by the hit points given.");
        }

        if (value > 0)
        {
            if (c.Dead)
            {
                w.Put(w.Get(id) with { DeathSaves = DeathSaveTally.Zero });
                w.Notes.Add($"{c.Name} is alive again at {N(value)} HP (set by the author: a revival)" +
                    (exhaustion < c.Exhaustion ? $"; exhaustion {N(c.Exhaustion)} → {N(exhaustion)} (raised from the dead with one level fewer)." : "."));

                // The new level's effects (the turn-holder's come with the turn's context already).
                if (exhaustion > 0 && id != w.Turn)
                {
                    w.Remind(K.ExhaustionEffects, id, $"{c.Name} — {CombatRules.Exhaustion(exhaustion, w.Edition).Text}");
                }
            }

            if (wasDown)
            {
                if (c.Hp == 0)
                {
                    MarkRelentlessUsed(w, id);
                    w.Put(w.Get(id) with { DeathSaves = DeathSaveTally.Zero });
                }

                w.Wake(id);
            }

            w.Undefeat(id);
        }
        else if (c.Hp is null or > 0 && !c.Dead)
        {
            if (c.MakesDeathSaves)
            {
                w.Put(w.Get(id) with { DeathSaves = DeathSaveTally.Zero });
                w.FallUnconscious(id, knockOut2024: false);
                w.Remind(K.Dropped, id, $"{c.Name} is at 0 HP: unconscious, dying. {CombatContext.DeathSaveProcedure(w.Edition)}");
                w.Incapacitated(id, "unconscious");
                w.Defeat(id, "at 0 HP");
            }
            else
            {
                w.Die(id, DeathCauses.ZeroHp);
            }
        }
    }

    /// <summary>
    /// A monster held at 0 by Relentless that is given hit points again has used its Relentless (once per rest): the
    /// tracker records it as a used limited trait, so the next drop is not intercepted (<c>StatBlockFacts.DeathInterceptors</c>'s
    /// <c>relentlessUsed</c>; R's open issue).
    /// </summary>
    private static void MarkRelentlessUsed(CombatWork w, string id)
    {
        var c = w.Get(id);
        if (c.MakesDeathSaves || c.StatBlock?.Trait(StatBlockValues.TraitKinds.Relentless) is not { } relentless || RelentlessUsed(c))
        {
            return;
        }

        var key = CombatValues.ResourceKeys.Limited(relentless.Name);
        w.Put(c with
        {
            Resources = Copy(c.Resources, key, new CombatResource { Name = relentless.Name, Kind = CombatValues.ResourceKinds.PerDay, Max = 1, Used = 1 }),
        });
        w.Notes.Add($"{c.Name}'s {relentless.Name} is used: the next drop to 0 kills it.");
    }

    /// <summary>
    /// The death interceptors still holding <paramref name="c"/> at 0 HP (D16): it dropped to 0 and a trait of its stat
    /// block (Undead Fortitude, Relentless, or Regeneration for a creature that makes no death saves) left it there, alive
    /// and not unconscious, for the table to resolve with <c>set</c> (it holds) or the reminder's other call (it fails).
    /// Empty for anything else. <c>end</c> counts such a combatant as defeated and proposes the entity's status (D19);
    /// <c>from_state</c> leaves it out as down.
    /// </summary>
    /// <remarks>
    /// No other path leaves a combatant at 0 HP, alive and conscious: <c>set {hp: 0}</c> kills or drops it unconscious, a
    /// knock-out makes it unconscious, a drop without a holding trait kills it or makes it unconscious.
    /// </remarks>
    public static IReadOnlyList<DeathInterceptor> HeldAtZeroBy(CombatantState c)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (c.Hp != 0 || c.MaxHp is null || c.Dead || c.Has(C.Unconscious) || c.StatBlock is not { } block)
        {
            return [];
        }

        return StatBlockFacts.DeathInterceptors(block, RelentlessUsed(c))
            .Where(i => !(c.MakesDeathSaves && i.Kind == StatBlockValues.TraitKinds.Regeneration))
            .ToList();
    }

    /// <summary>Its Relentless kept it alive since its last rest.</summary>
    internal static bool RelentlessUsed(CombatantState c) =>
        c.StatBlock?.Trait(StatBlockValues.TraitKinds.Relentless) is { } relentless &&
        c.Resources.TryGetValue(CombatValues.ResourceKeys.Limited(relentless.Name), out var used) && used.Used >= 1;

    /// <summary>A copy of the resources with one entry set (in place when present, else appended).</summary>
    internal static IReadOnlyDictionary<string, CombatResource> Copy(IReadOnlyDictionary<string, CombatResource> resources, string key, CombatResource value)
    {
        var copy = new OrderedDictionary<string, CombatResource>(StringComparer.Ordinal);
        foreach (var (k, v) in resources)
        {
            copy[k] = v;
        }

        copy[key] = value;
        return copy;
    }

    // -------------------------------------------------------------------------------------------------------------------
    // leave

    private static CombatStepResult Leave(EncounterState state, LeaveOp op)
    {
        RequireNotEnded(state, "leave");
        var targets = CombatAddressing.ResolveMany(state.Combatants, op.Targets, "targets");
        var w = new CombatWork(state);
        var turnLeft = false;
        foreach (var target in targets)
        {
            if (target.Removed)
            {
                w.Notes.Add($"no effect: {target.Name} left");
                continue;
            }

            w.Put(w.Get(target.Id) with { Removed = true });
            w.Log(L.Remove, w.Turn, target.Id, null, null);
            w.Notes.Add($"{target.Name} leaves the fight.");
            if (target.Concentration is not null)
            {
                w.EndConcentration(target.Id, K.ConcentrationBroken, "it left the fight");
            }

            w.EndConditions((_, c) => c.Duration == CombatValues.Durations.UntilEscape && c.Source == target.Id, K.GrappleEnded, $"{target.Name} left the fight");
            turnLeft |= target.Id == state.TurnCombatantId && state.Round >= 1;
        }

        if (turnLeft && !w.All.Any(CombatOrder.TakesTurns))
        {
            w.Notes.Add("No combatant is left to take a turn: end the fight (combat {\"action\": \"end\"}).");
        }
        else if (turnLeft)
        {
            var roundBefore = w.Round;
            var from = w.Turn;
            CombatTurns.EndOfTurn(w, from!, place: true);
            CombatTurns.Advance(w);
            w.Log(L.Turn, w.Turn, null, null, CombatTurns.TurnDetail(w, from, roundBefore));
            w.Notes.Add($"The turn moves to {w.Name(w.Turn)} (round {N(w.Round)}).");
        }

        return w.Finish();
    }
}
