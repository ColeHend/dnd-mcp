using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Characters;

/// <summary>
/// <c>campaign_character rest {kind, hit_dice, rolls}</c>: a short or a long rest on the sheet (contract §5.15), outside a
/// fight (the Repository refuses a rest for a character in the active encounter, D5).
///
/// <para>
/// <b>Short rest.</b> 2024 needs at least 1 hit point (2014 states no minimum, but a dying character must be stabilised
/// first). Hit Dice are spent largest die first; each gives the face + the Con modifier (2014 floored at 0, since no
/// minimum is stated; 2024 "minimum of 1"), up to the effective maximum. Resources that recharge on a short rest come
/// back (<c>short_rest</c> all, <c>short_rest_one</c> one), the Pact Magic slots come back, and an hour passes: timed
/// effects and a concentration with 600 rounds or fewer left end, longer ones lose 600 rounds.
/// </para>
/// <para>
/// <b>Long rest.</b> Needs at least 1 hit point (both editions). Exhaustion drops by 1 FIRST (2014 only with food and
/// drink), then the maximum reduction clears and hit points return to the effective maximum at the new exhaustion level;
/// temporary hit points end; Hit Dice: 2014 regains half the total (at least 1) of the spent dice, largest first, 2024 all
/// of them; every spell slot; every resource but <c>none</c>; every timed effect and the concentration end, while
/// <c>until_removed</c> conditions stay, each with a reminder to check whether it stops this recovery (2024 mummy rot
/// does: "Hit Points can't be regained"); death saves reset.
/// </para>
/// <para>
/// <b>Dice.</b> The Domain rolls nothing. <see cref="Needs"/> says which Hit Dice to roll (one need per die size, in
/// spending order); the Repository rolls and logs each and passes the faces to <see cref="Apply"/> in that order, or the
/// caller's <c>rolls</c> are used as given.
/// </para>
/// </summary>
public static class SheetRest
{
    /// <summary>The purpose of a Hit Dice roll (the dice log's label is "&lt;name&gt;: hit dice").</summary>
    public const string HitDicePurpose = "hit dice";

    /// <summary>The Hit Dice the rest needs rolled: none for a long rest, or when every face is given.</summary>
    /// <exception cref="DndInputException">The request is refused (see <see cref="Apply"/>).</exception>
    public static IReadOnlyList<SheetRollNeed> Needs(CharacterSheet sheet, RestRequest request, string edition)
    {
        var plan = Plan(sheet, request, edition);
        if (request.Rolls is not null || plan.Count == 0)
        {
            return [];
        }

        return plan.GroupBy(sides => sides)
            .Select(g => new SheetRollNeed($"hit-dice-d{N(g.Key)}", HitDicePurpose, $"{N(g.Count())}d{N(g.Key)}", g.Key, g.Count()))
            .ToList();
    }

    /// <summary>
    /// The sheet after the rest. For a short rest that spends Hit Dice, <paramref name="rolledFaces"/> are the faces of
    /// <see cref="Needs"/> in need order when the request gave no rolls.
    /// </summary>
    /// <exception cref="DndInputException">The rest is refused: dead, 0 hit points (2024 short; any long), dying, more Hit
    /// Dice than are left, faces that do not fit the dice, hit_dice and rolls that disagree, hit points not tracked.</exception>
    /// <exception cref="ArgumentException">A host bug: Hit Dice to spend and no faces given or rolled.</exception>
    public static SheetRestResult Apply(CharacterSheet sheet, RestRequest request, string edition, IReadOnlyList<int>? rolledFaces = null)
    {
        var plan = Plan(sheet, request, edition);
        var kind = Kind(request.Kind);
        return kind == SheetValues.RestKinds.Long
            ? Long(sheet, sheet.EditionOr(edition), request.AteAndDrank)
            : Short(sheet, sheet.EditionOr(edition), plan, request.Rolls ?? rolledFaces);
    }

    private static SheetRestResult Short(CharacterSheet sheet, string edition, IReadOnlyList<int> plan, IReadOnlyList<int>? faces)
    {
        var notes = new List<string>();
        var spent = new List<HitDieRoll>();
        var after = sheet;
        var regained = 0;
        if (plan.Count > 0)
        {
            if (faces is null || faces.Count != plan.Count)
            {
                throw new ArgumentException($"A short rest spending {plan.Count} Hit Dice needs {plan.Count} faces, rolled or given.", nameof(faces));
            }

            var con = sheet.Modifier(V.Abilities.Con);
            if (con is null)
            {
                notes.Add("No Con score on the sheet: each Hit Die adds +0.");
            }

            for (var i = 0; i < plan.Count; i++)
            {
                var gain = faces[i] + (con ?? 0);
                gain = edition == V.Editions.E2024 ? Math.Max(1, gain) : Math.Max(0, gain);
                spent.Add(new HitDieRoll(plan[i], faces[i], gain));
            }

            var dice = sheet.HitDice;
            foreach (var group in plan.GroupBy(s => s))
            {
                var key = SheetJson.DieKey(group.Key);
                dice = SheetMaps.With(dice, key, dice[key] with { Used = dice[key].Used + group.Count() });
            }

            var effective = sheet.EffectiveMaxHp(edition)!.Value;
            var hp = Math.Min(effective, sheet.Hp!.Value + spent.Sum(s => s.Gain));
            regained = hp - sheet.Hp.Value;
            after = after with { HitDice = dice, Hp = hp };
            if (sheet.Hp == 0 && hp > 0)
            {
                after = after with { DeathSaves = SheetDeathSaves.Reset };
            }

            notes.Add($"Hit Dice spent: {string.Join(", ", spent.Select(s => $"d{N(s.Sides)} {N(s.Face)}"))}, " +
                      $"{(con is { } c ? $"{Signed(c)} Con each" : "+0 each")} → +{N(spent.Sum(s => s.Gain))} HP; " +
                      $"HP {N(sheet.Hp.Value)} → {N(hp)}{(hp == effective ? " (max)" : string.Empty)}.");
        }

        after = Recover(after, SheetValues.RestKinds.Short, notes);
        var (conditions, concentration) = ShortenTimed(after, notes);
        after = after with { Conditions = conditions, Concentration = concentration };
        return new SheetRestResult(after, SheetDiff.Between(sheet, after), notes, [], spent, regained);
    }

    private static SheetRestResult Long(CharacterSheet sheet, string edition, bool ateAndDrank)
    {
        var notes = new List<string>();
        var reminders = new List<SheetReminder>();
        var exhaustion = sheet.Exhaustion;
        if (exhaustion > 0 && (edition == V.Editions.E2024 || ateAndDrank))
        {
            exhaustion--;
            notes.Add($"Exhaustion {N(sheet.Exhaustion)} → {N(exhaustion)}.");
        }
        else if (exhaustion > 0)
        {
            notes.Add("Exhaustion unchanged: no food and drink (2014).");
        }

        var after = sheet with { Exhaustion = exhaustion, MaxHpReduction = 0, TempHp = 0, DeathSaves = SheetDeathSaves.Reset };
        if (sheet.MaxHpReduction > 0)
        {
            notes.Add($"Hit point maximum reduction {N(sheet.MaxHpReduction)} cleared.");
        }

        if (after.EffectiveMaxHp(edition) is { } effective)
        {
            after = after with { Hp = effective };
            if (sheet.Hp != effective)
            {
                notes.Add($"HP {(sheet.Hp is { } hp ? N(hp) : "untracked")} → {N(effective)} (max).");
            }
        }

        if (sheet.TempHp > 0)
        {
            notes.Add($"Temporary hit points {N(sheet.TempHp)} → 0.");
        }

        after = after with { HitDice = RegainHitDice(sheet, edition, notes) };
        after = after with { SpellSlots = SheetMaps.Select(after.SpellSlots, (_, slot) => slot with { Used = 0 }) };
        if (sheet.SpellSlots.Values.Any(s => s.Used > 0))
        {
            notes.Add("Every spell slot restored.");
        }

        after = Recover(after, SheetValues.RestKinds.Long, notes);
        foreach (var ended in sheet.Conditions.Where(c => c.IsTimed))
        {
            notes.Add($"{ended.Name} ended.");
        }

        if (sheet.Concentration is { } held)
        {
            notes.Add($"Concentration on {held.Display} ended.");
        }

        var kept = sheet.Conditions.Where(c => !c.IsTimed).ToList();
        foreach (var condition in kept)
        {
            reminders.Add(new SheetReminder(
                SheetValues.ReminderKinds.CheckCondition,
                $"{condition.Name} stays (until removed): check whether it stops this rest's recovery (2024 mummy rot: hit points can't be regained)."));
        }

        after = after with { Conditions = kept, Concentration = null };
        return new SheetRestResult(after, SheetDiff.Between(sheet, after), notes, reminders, [], (after.Hp ?? 0) - (sheet.Hp ?? after.Hp ?? 0));
    }

    /// <summary>The Hit Dice to spend, largest first, after checking the rest may happen at all.</summary>
    private static IReadOnlyList<int> Plan(CharacterSheet sheet, RestRequest request, string edition)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(request);
        SheetUpdate.CheckEdition(edition);
        edition = sheet.EditionOr(edition);
        var kind = Kind(request.Kind);
        if (sheet.IsDead(edition))
        {
            throw new DndInputException("The character is dead: it cannot rest.");
        }

        if (sheet.Hp == 0)
        {
            if (kind == SheetValues.RestKinds.Long)
            {
                throw new DndInputException("A long rest needs at least 1 hit point; the character has 0.");
            }

            if (edition == V.Editions.E2024)
            {
                throw new DndInputException("A short rest needs at least 1 hit point (2024); the character has 0.");
            }

            if (!sheet.DeathSaves.Stable)
            {
                throw new DndInputException("The character is dying at 0 hit points: stabilise it first.");
            }
        }

        if (kind == SheetValues.RestKinds.Long)
        {
            if (request.HitDice is not null || request.Rolls is not null)
            {
                throw new DndInputException("hit_dice and rolls are for a short rest; a long rest restores Hit Dice by itself.");
            }

            return [];
        }

        var count = request.HitDice ?? request.Rolls?.Count ?? 0;
        if (request.HitDice is { } asked && request.Rolls is { } faces && faces.Count != asked)
        {
            throw new DndInputException($"hit_dice is {N(asked)} but rolls has {N(faces.Count)} faces; give one face per Hit Die spent.");
        }

        if (count < 0 || count > DslLimits.MaxLevel)
        {
            throw new DndInputException($"hit_dice is {N(count)}; spend 0 to {N(DslLimits.MaxLevel)} Hit Dice.");
        }

        if (count == 0)
        {
            return [];
        }

        if (sheet.Hp is null || sheet.MaxHp is null)
        {
            throw new DndInputException("The sheet tracks no hit points: give max_hp and hp with update before spending Hit Dice.");
        }

        var available = sheet.HitDice
            .Select(p => (Sides: SheetJson.DieOf(p.Key), p.Value.Left))
            .Where(d => d.Sides is not null && d.Left > 0)
            .OrderByDescending(d => d.Sides)
            .ToList();
        var left = available.Sum(d => d.Left);
        if (count > left)
        {
            var list = left == 0 ? "none" : string.Join(", ", available.Select(d => $"{N(d.Left)}d{N(d.Sides!.Value)}"));
            throw new DndInputException($"hit_dice is {N(count)}, but only {N(left)} Hit Dice are left ({list}).");
        }

        var plan = new List<int>();
        foreach (var (sides, available1) in available)
        {
            plan.AddRange(Enumerable.Repeat(sides!.Value, Math.Min(available1, count - plan.Count)));
        }

        for (var i = 0; i < (request.Rolls?.Count ?? 0); i++)
        {
            var face = request.Rolls![i];
            if (face < 1 || face > plan[i])
            {
                throw new DndInputException(
                    $"rolls item {N(i + 1)} is {N(face)}; it is the face of a d{N(plan[i])}, 1 to {N(plan[i])} (Hit Dice are spent largest first: " +
                    $"{string.Join(", ", plan.Select(s => $"d{N(s)}"))}).");
            }
        }

        return plan;
    }

    /// <summary>Resources and Pact Magic back by the rest's kind (a long rest also restores every slot, done by the caller).</summary>
    private static CharacterSheet Recover(CharacterSheet sheet, string kind, List<string> notes)
    {
        var resources = SheetMaps.Select(sheet.Resources, (_, r) =>
        {
            if (!r.IsCounted || (r.Used ?? 0) == 0)
            {
                return r;
            }

            var recharge = r.RechargeOrDefault;
            int? used = kind == SheetValues.RestKinds.Long
                ? SheetValues.Recharges.BackOnLongRest(recharge) ? 0 : null
                : recharge switch
                {
                    SheetValues.Recharges.ShortRest => 0,
                    SheetValues.Recharges.ShortRestOne => (r.Used ?? 0) - 1,
                    _ => null,
                };
            if (used is null)
            {
                return r;
            }

            notes.Add($"{r.Name}: {N(r.Left)}/{N(r.Max!.Value)} → {N(r.Max.Value - used.Value)}/{N(r.Max.Value)}.");
            return r with { Used = used };
        });

        var slots = sheet.SpellSlots;
        if (kind == SheetValues.RestKinds.Short && slots.TryGetValue(SpellSlotEntry.PactKey, out var pact) && pact.Used > 0)
        {
            slots = SheetMaps.With(slots, SpellSlotEntry.PactKey, pact with { Used = 0 });
            notes.Add("Pact Magic slots restored.");
        }

        return sheet with { Resources = resources, SpellSlots = slots };
    }

    /// <summary>A short rest is an hour: timed effects and concentration with 600 rounds or fewer left end, the rest lose 600.</summary>
    private static (IReadOnlyList<SheetCondition>, SheetConcentration?) ShortenTimed(CharacterSheet sheet, List<string> notes)
    {
        var conditions = new List<SheetCondition>();
        foreach (var c in sheet.Conditions)
        {
            if (!c.IsTimed)
            {
                conditions.Add(c);
            }
            else if ((c.RemainingRounds ?? 0) <= SheetLimits.ShortRestRounds)
            {
                notes.Add($"{c.Name} ended.");
            }
            else
            {
                conditions.Add(c with { RemainingRounds = c.RemainingRounds - SheetLimits.ShortRestRounds });
            }
        }

        var concentration = sheet.Concentration;
        if (concentration?.RemainingRounds is { } rounds)
        {
            if (rounds <= SheetLimits.ShortRestRounds)
            {
                notes.Add($"Concentration on {concentration.Display} ended.");
                concentration = null;
            }
            else
            {
                concentration = concentration with { RemainingRounds = rounds - SheetLimits.ShortRestRounds };
            }
        }

        return (conditions, concentration);
    }

    /// <summary>2024: every spent Hit Die; 2014: half the total (at least 1) of the spent ones, largest die first.</summary>
    private static IReadOnlyDictionary<string, HitDiceEntry> RegainHitDice(CharacterSheet sheet, string edition, List<string> notes)
    {
        var spent = sheet.HitDice.Values.Sum(d => Math.Min(d.Used, d.Max));
        if (spent == 0)
        {
            return sheet.HitDice;
        }

        var budget = edition == V.Editions.E2024 ? int.MaxValue : Math.Max(1, sheet.HitDice.Values.Sum(d => d.Max) / 2);
        var dice = sheet.HitDice;
        var regained = 0;
        foreach (var key in dice.Keys.OrderByDescending(k => SheetJson.DieOf(k) ?? 0).ToList())
        {
            var entry = dice[key];
            var back = Math.Min(Math.Max(0, entry.Used), budget - regained);
            if (back > 0)
            {
                dice = SheetMaps.With(dice, key, entry with { Used = entry.Used - back });
                regained += back;
            }
        }

        notes.Add($"Hit Dice regained: {N(regained)} of {N(spent)} spent.");
        return dice;
    }

    private static string Kind(string? kind) =>
        SheetValues.RestKinds.Set.TryMatch(kind, out var canonical)
            ? canonical
            : throw new DndInputException($"kind \"{DslText.Echo(kind)}\" is not a rest; give \"short\" or \"long\".");

    private static string N(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Signed(int value) => value >= 0 ? "+" + N(value) : N(value);
}

/// <summary>A rest as <c>campaign_character rest</c> takes it.</summary>
/// <param name="Kind">"short" or "long" (forgiving).</param>
/// <param name="HitDice">Short rest: how many Hit Dice to spend (largest die first). Default: as many as <paramref name="Rolls"/> has, else none.</param>
/// <param name="Rolls">Short rest: the faces of the Hit Dice spent, in spending order; without them the Repository rolls.</param>
/// <param name="AteAndDrank">2014 long rest: exhaustion drops only with food and drink (default true).</param>
public sealed record RestRequest(string Kind, int? HitDice = null, IReadOnlyList<int>? Rolls = null, bool AteAndDrank = true);
