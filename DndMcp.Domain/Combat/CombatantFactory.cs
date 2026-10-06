using DndMcp.Domain.Campaign;
using DndMcp.Domain.Characters;
using DndMcp.Domain.Rules;
using DndMcp.Domain.Simulation;
using C = DndMcp.Domain.Simulation.StatBlockValues.Conditions;
using D = DndMcp.Domain.Combat.CombatValues.Durations;
using E = DndMcp.Domain.Combat.CombatValues.ExpiryPoints;
using R = DndMcp.Domain.Combat.CombatValues.ResourceKeys;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Combat;

/// <summary>
/// The three ways a combatant enters a fight (contract §6.2, §12.3): seeded from a sheet (snapshot and write-back), from a
/// stat block (snapshotted whole), or custom (a name, with HP and AC given or unknown). <c>add</c> uses these; the
/// Repository's <c>start</c> re-seeds a planned fight's sheet-seeded combatants with <see cref="FromSheet"/> (D19).
/// </summary>
public static class CombatantFactory
{
    /// <summary>
    /// A sheet-seeded combatant (contract §6.2): HP, temporary HP, maximum reduction, base AC, the sheet's persisting
    /// conditions and concentration (their <c>remaining_rounds</c> counted from the fight's next round start on its own
    /// turn), death saves, exhaustion, the slots and resources mirror, the initiative bonus (the sheet's, else the Dex
    /// modifier), makes death saves, and the <see cref="SheetSnapshot"/> the write-back compares against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A sheet with no <c>max_hp</c> gives a combatant with unknown hit points (D17: it writes back no HP unless <c>set</c>
    /// gives it some); a sheet with a maximum and no current HP starts at the effective maximum. A sheet at 0 HP and not
    /// dead seeds the tracker's down state (Unconscious <c>zero_hp</c>, Prone <c>until_stands</c>) with its stored tallies.
    /// </para>
    /// <para>
    /// <b>The sheet keeps Unconscious only</b> (review UR03): <c>end</c> and <c>campaign_character damage</c> write a drop
    /// to 0 as Unconscious alone, because a Prone stored "until removed" would outlive every heal and rest. So the Prone of
    /// the down state is added here, as the tracker's <c>until_stands</c> (it ends when the creature stands, or with the
    /// fight); a Prone the sheet does hold is the author's own and stays exactly as stored, never doubled.
    /// </para>
    /// <para>
    /// <b>Above 0 HP only a knock-out wakes on a heal</b> (reviews CR06, F2R04): a 2024 knock-out that <c>end</c> wrote
    /// (noted <see cref="CombatEnd.KnockedOutNote"/>) "remains Unconscious until it regains any Hit Points" (SRD 5.2.1), so
    /// it comes back as the tracker's knock-out (its <c>until_stands</c> Prone with it), which a heal or first aid ends. Any
    /// other stored Unconscious above 0 HP — a Sleep, an author's condition — is no knock-out: it comes in
    /// <c>until_removed</c> (as F1 seeded it), ended by <c>condition remove</c> (or, out of the fight, the sheet's own heal
    /// and rest rule). Seeded as a knock-out it was simulated at 0 HP and woken by a heal that regained nothing.
    /// </para>
    /// </remarks>
    /// <param name="round">The fight's round now: a timed sheet effect with N rounds left ends at the start of the combatant's own turn in round max(round, 1) + N.</param>
    public static CombatantState FromSheet(CharacterSheet sheet, string id, string name, string edition, int round = 0, double orderKey = 1)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var ruleset = sheet.EditionOr(edition);
        int? max = sheet.MaxHp;
        int? hp = max is null ? null : sheet.Hp ?? sheet.EffectiveMaxHp(ruleset);
        if (hp is { } h && sheet.EffectiveMaxHp(ruleset) is { } effective && h > effective)
        {
            hp = effective;
        }

        var start = Math.Max(round, 1);

        // At 0 HP and alive the sheet is down: the tracker's own down state (§6.2 as amended by F1, reviews C08/CV01) —
        // Unconscious until hit points rise above 0 (zero_hp: a heal wakes it) and Prone until it stands — never the
        // sheet's until_removed copy, which no heal in the fight would end. A stored Unconscious IS that state (one entry,
        // not two); a stored Prone stays as stored (UR03: the sheet never writes the down state's Prone, so one there is
        // the author's). Above 0 HP a stored Unconscious noted "knocked out" is the tracker's knock-out, which a heal wakes
        // (CR06); any other comes in until_removed (F2R04).
        var down = hp == 0 && !sheet.IsDead(ruleset);
        var knockedOut = !down && hp > 0 && !sheet.IsDead(ruleset) && sheet.Conditions.Any(c => IsUnconscious(c) && IsKnockOut(c));
        var conditions = new List<CombatCondition>();
        string NextId() => "c" + (conditions.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var condition in sheet.Conditions)
        {
            var stored = CombatConditions.TryMatch(condition.Name, out var canonical) ? canonical : condition.Name;
            if ((down || knockedOut) && stored == C.Unconscious)
            {
                if (!conditions.Any(c => c.Name == stored))
                {
                    conditions.Add(DownCondition(NextId(), stored) with { KnockOut = knockedOut });
                }

                continue;
            }

            var timed = condition.IsTimed && condition.RemainingRounds is not null;
            conditions.Add(new CombatCondition(NextId(), stored, timed ? D.Rounds : D.UntilRemoved)
            {
                SourceNote = condition.Source,
                Expires = timed ? new ConditionExpiry(start + condition.RemainingRounds!.Value, E.Start, id) : null,
                Effect = condition.Effect is null ? null : CombatJson.ReadEffect(condition.Effect),
                Note = condition.Note,
                Extra = condition.Extra,
            });
        }

        foreach (var missing in new[] { C.Unconscious, C.Prone }.Where(n => (down || knockedOut) && !conditions.Any(c => c.Name == n)))
        {
            conditions.Add(DownCondition(NextId(), missing));
        }

        var concentration = sheet.Concentration is { } held
            ? new CombatConcentration(held.Display)
            {
                Level = held.Level,
                Duration = held.RemainingRounds is null ? null : D.Rounds,
                Expires = held.RemainingRounds is { } left ? new ConditionExpiry(start + left, E.Start, id) : null,
                Note = held.Note,
                Extra = held.Extra,
            }
            : null;

        var resources = new OrderedDictionary<string, CombatResource>(StringComparer.Ordinal);
        foreach (var (key, slot) in sheet.SpellSlots)
        {
            if (key == SpellSlotEntry.PactKey)
            {
                resources[R.Pact] = new CombatResource { Level = slot.Level, Max = slot.Max, Used = slot.Used };
            }
            else if (int.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var level))
            {
                resources[R.Slot(level)] = new CombatResource { Max = slot.Max, Used = slot.Used };
            }
        }

        foreach (var (key, resource) in sheet.Resources)
        {
            if (key.Length == 0 || key.Contains(':') || key == R.Pact)
            {
                continue;
            }

            resources[key] = new CombatResource
            {
                Name = resource.Name,
                Max = resource.Max,
                Used = resource.Max is null ? resource.Used : resource.Used ?? 0,
                State = resource.State,
            };
        }

        var conSave = CombatRules.SaveBonus(V.Abilities.Con, sheet.Abilities, sheet.Saves.Abilities, sheet.Saves.Bonus, sheet.ProficiencyBonus);
        return new CombatantState
        {
            Id = id,
            EntityId = sheet.EntityId,
            Name = name,
            Side = CampaignValues.CombatSides.Party,
            Ac = sheet.Ac,
            MaxHp = max,
            MaxHpReduction = sheet.MaxHpReduction,
            Hp = hp,
            TempHp = sheet.TempHp,
            Conditions = conditions,
            Concentration = concentration,
            DeathSaves = new DeathSaveTally(sheet.DeathSaves.Successes, sheet.DeathSaves.Failures, sheet.DeathSaves.Stable),
            MakesDeathSaves = true,
            Exhaustion = sheet.Exhaustion,
            Resources = resources,
            SheetSnapshot = SheetSnapshot.Of(sheet, conSave),
            InitBonus = sheet.InitiativeOrDex,
            Dead = sheet.IsDead(ruleset),
            OrderKey = orderKey,
        };
    }

    /// <summary>The tracker's down state at 0 HP, as a drop adds it (<c>CombatWork.FallUnconscious</c>), come in from the sheet (no <c>applied</c>).</summary>
    private static CombatCondition DownCondition(string id, string name) =>
        new(id, name, name == C.Unconscious ? D.ZeroHp : D.UntilStands);

    private static bool IsUnconscious(SheetCondition condition) =>
        CombatConditions.TryMatch(condition.Name, out var canonical) && canonical == C.Unconscious;

    // The Unconscious end writes for a knock-out still out (CombatEnd.KnockedOutNote), as stored.
    private static bool IsKnockOut(SheetCondition condition) =>
        string.Equals(condition.Note?.Trim(), CombatEnd.KnockedOutNote, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>start</c> naming a planned encounter (contract §6.1, D19): the sheet-seeded combatant's state and snapshot taken
    /// again from its CURRENT sheet (the sheet may have changed since <c>prepare</c>), its place in the fight kept (id, name,
    /// side, init group, initiative, insertion order, hidden, surprised, the entity it is linked to).
    /// </summary>
    public static CombatantState Reseed(CombatantState existing, CharacterSheet sheet, string edition, int round = 0)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(sheet);
        return FromSheet(sheet, existing.Id, existing.Name, edition, round, existing.OrderKey) with
        {
            Side = existing.Side,
            InitGroup = existing.InitGroup,
            Initiative = existing.Initiative,
            Hidden = existing.Hidden,
            Surprised = existing.Surprised,
            EntityHandle = existing.EntityHandle,
            EntitySubtype = existing.EntitySubtype,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = existing.UpdatedAt,
        };
    }

    /// <summary>
    /// A combatant from a stat block (contract §6.2): HP by <paramref name="hp"/> (the average, a rolled value, unknown,
    /// or a number), AC and initiative bonus (2024 from the overrides, through <see cref="StatBlock.InitiativeBonus"/>),
    /// legendary counts in or out of the lair, and the limited uses (<c>"limited:&lt;name&gt;"</c>: recharge or per day) and
    /// 2014 slot pools (<c>"pool:slot:N"</c>). The snapshot is stored whole, the lair NOT applied (the encounter says).
    /// </summary>
    /// <param name="rolledHp">The rolled Hit Dice total when <paramref name="hp"/> is "roll".</param>
    public static CombatantState FromStatBlock(StatBlock block, HpChoice hp, bool lair, string id, string name, int? rolledHp = null, double orderKey = 1)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(hp);
        int? points = hp.Kind switch
        {
            CombatValues.HpChoices.Avg => block.HitPoints,
            CombatValues.HpChoices.Roll => Math.Max(1, rolledHp ?? throw new ArgumentException("A rolled HP needs the roll.", nameof(rolledHp))),
            CombatValues.HpChoices.Unknown => null,
            _ => hp.Value ?? throw new ArgumentException("A number of hit points needs the number.", nameof(hp)),
        };

        var resources = new OrderedDictionary<string, CombatResource>(StringComparer.Ordinal);
        foreach (var action in block.Actions.Concat(block.BonusActions).Concat(block.Reactions).Concat(block.Spells).Concat(block.Legendary?.Actions ?? []))
        {
            var usage = action.Usage;
            if (usage.Kind == StatBlockValues.UsageKinds.Recharge)
            {
                var name2 = usage.Pool is { } pool && pool.StartsWith("recharge:", StringComparison.Ordinal) ? pool["recharge:".Length..] : action.Name;
                resources.TryAdd(R.Limited(name2), new CombatResource { Name = name2, Kind = CombatValues.ResourceKinds.Recharge, Min = usage.RechargeMin, Ready = true });
            }
            else if (usage.Kind == StatBlockValues.UsageKinds.PerDay && usage.Uses is { } uses)
            {
                resources.TryAdd(R.Limited(action.Name), new CombatResource { Name = action.Name, Kind = CombatValues.ResourceKinds.PerDay, Max = uses, Used = 0 });
            }
        }

        foreach (var (level, count) in block.SpellSlots.OrderBy(p => p.Key))
        {
            if (count > 0)
            {
                resources[R.PoolSlot(level)] = new CombatResource { Max = count, Used = 0 };
            }
        }

        var actions = StatBlockFacts.LegendaryActionUses(block, lair);
        var resistance = StatBlockFacts.LegendaryResistanceUses(block, lair);
        return new CombatantState
        {
            Id = id,
            Name = name,
            Side = CampaignValues.CombatSides.Enemy,
            SrdRef = block.Ref,
            StatBlock = block,
            Ac = block.ArmorClass,
            MaxHp = points,
            Hp = points,
            InitBonus = block.InitiativeBonus,
            Legendary = actions > 0 || resistance > 0 ? new LegendaryState(actions, 0, resistance, 0) : null,
            Resources = resources,
            OrderKey = orderKey,
        };
    }

    /// <summary>A custom combatant: a name, with hit points and AC given or unknown.</summary>
    public static CombatantState Custom(string id, string name, int? hp, int? ac, int initBonus, double orderKey = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new CombatantState
        {
            Id = id,
            Name = name,
            Side = CampaignValues.CombatSides.Enemy,
            MaxHp = hp,
            Hp = hp,
            Ac = ac,
            InitBonus = initBonus,
            OrderKey = orderKey,
        };
    }
}
