using System.Globalization;
using System.Text.Json.Nodes;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Characters;

/// <summary>
/// <c>campaign_character update</c> in pure form: a sheet created from, or patched with, a <see cref="SheetSpec"/> and an
/// optional sim_profile, with the derivations of contract §7.2.
///
/// <para>
/// <b>Patch semantics.</b> Only the fields given change. Abilities, save bonuses and resources merge key by key (a
/// resource is matched by the slug of its name; <c>remove: true</c> drops it); classes, the four defence lists, the slot
/// maxima and the feats/features/spells/languages lists are replaced whole when given (an entry that stays keeps what
/// the sheet held about it: a feat's ref, a class's unknown keys). An empty string clears a text field.
/// </para>
/// <para>
/// <b>Derivations</b>, each only when the field is absent from both the spec and the sheet, and each said in a note:
/// <c>max_hp</c> by the fixed values (<see cref="LevelHitPoints"/>; needs every class's hit die and the Con score: feats
/// such as Tough are not known, so the note says to give max_hp); <c>hp</c> = the effective maximum; the saving throw
/// proficiencies of the starting SRD class, when the sheet is created or its starting class changes (so an explicit
/// <c>save_proficiencies: []</c> stays empty). On every change of the classes or levels, and of the edition the sheet
/// follows: Hit Dice maxima from the classes, and the spell slots the tables give (<see cref="DeriveSlots"/>: a
/// single-class SRD caster's table; a warlock's Pact Magic by the warlock level even when multiclassed; never the
/// per-level slots of a multiclass sheet, D18; never anything for a sheet whose classes do not cast, so an Eldritch
/// Knight's given slots stay). Never derived: AC, initiative (read as the Dex modifier), resources.
/// </para>
/// <para>
/// <b>Never re-derived.</b> A stored max_hp is never recomputed, derived or given (feats such as Tough are not known): a
/// change of the classes or levels without max_hp keeps it and reminds that it was not recomputed (level_up adds a
/// level's hit points; max_hp sets it).
/// </para>
/// <para>
/// <b>Refusals</b> are a <see cref="DndInputException"/> listing up to five problems, in two stages: the spec's own
/// problems (<see cref="SheetSpecValidator"/>), together; then, once the spec is valid, the problems that need the sheet,
/// together (a level on a sheet with classes, a new homebrew class without its hit_die, hp above the maximum, used above
/// a resource's maximum, a sim_profile on a sheet without a level). The second stage applies the spec, which an invalid
/// spec cannot be. Nothing is half-applied.
/// </para>
/// </summary>
public static class SheetUpdate
{
    /// <summary>
    /// The sheet after the update, whether it was created, what changed, notes and reminders.
    /// </summary>
    /// <param name="existing">The stored sheet, or null to create one.</param>
    /// <param name="spec">The fields to set (null: none).</param>
    /// <param name="simProfile">A sim_profile to validate at the sheet's level and store canonical (null: unchanged).</param>
    /// <param name="edition">The campaign's ruleset ("2014"/"2024"; a mixed campaign passes its default), used when the sheet names none.</param>
    /// <param name="entityId">The character entity's id: required when <paramref name="existing"/> is null.</param>
    /// <exception cref="DndInputException">Every problem with the input (up to five listed).</exception>
    /// <exception cref="ArgumentException">A host bug: no entity id for a new sheet, or an edition that is not 2014/2024.</exception>
    public static SheetUpdateResult Apply(CharacterSheet? existing, SheetSpec? spec, BuildSpec? simProfile, string edition, string? entityId = null)
    {
        CheckEdition(edition);
        if (existing is null && string.IsNullOrWhiteSpace(entityId))
        {
            throw new ArgumentException("A new sheet needs its character's entity id.", nameof(entityId));
        }

        if (spec is not null)
        {
            SheetSpecValidator.ThrowIfInvalid(spec);
        }

        var before = existing;
        var sheet = existing ?? CharacterSheet.New(entityId!);
        var notes = new List<string>();
        var reminders = new List<SheetReminder>();
        var problems = new List<string>();
        var levelBefore = sheet.Level;
        var conBefore = sheet.Modifier(V.Abilities.Con);
        var maxHpBefore = sheet.MaxHp;
        var classesChanged = false;

        if (spec is not null)
        {
            sheet = ApplyText(sheet, spec);
            (sheet, classesChanged) = ApplyClasses(sheet, spec, problems);
            sheet = ApplyNumbers(sheet, spec);
            sheet = ApplySavesAndDefenses(sheet, spec);
            sheet = ApplySlots(sheet, spec);
            sheet = ApplyResources(sheet, spec, problems);
            sheet = ApplyLists(sheet, spec);
        }

        var sheetEdition = sheet.EditionOr(edition);
        var editionChanged = before is not null && before.EditionOr(edition) != sheetEdition;
        if (classesChanged || editionChanged || (before is null && sheet.Classes.Count > 0))
        {
            sheet = DeriveHitDice(sheet);
            sheet = DeriveSlots(sheet, sheetEdition, notes, reminders, perLevel: spec?.Slots is null, pact: spec?.Pact is null);
        }

        sheet = DeriveSaves(sheet, before, spec, notes);
        sheet = DeriveHitPoints(sheet, spec, sheetEdition, notes);
        if (before is not null && classesChanged && spec?.MaxHp is null && maxHpBefore is { } kept)
        {
            reminders.Add(MaxHpNotRecomputed(before, sheet, kept));
        }
        CheckHitPoints(sheet, spec, sheetEdition, problems, notes, out sheet);

        if (simProfile is not null)
        {
            if (sheet.Level is not { } level)
            {
                problems.Add("sim_profile needs the sheet's level (the simulator resolves the build at it): give sheet.classes or sheet.level first, or with it.");
            }
            else if (problems.Count == 0)
            {
                var prepared = SimProfile.Prepare(simProfile, level);
                sheet = sheet with { SimProfile = prepared.Json };
                notes.AddRange(prepared.Notes);
            }
        }
        else if (sheet.SimProfile is { } stored && sheet.Level is { } level && level != levelBefore && SimProfile.ProblemAt(stored, level) is { } problem)
        {
            reminders.Add(new SheetReminder(
                SheetValues.ReminderKinds.SimProfile,
                $"The stored sim_profile does not work at level {N(level)}: {problem} Give one that does.",
                "update",
                new Dictionary<string, object?> { ["sim_profile"] = new ReminderPlaceholder("{…}") }));
        }

        DslProblems.ThrowIfAny(problems, SheetSpecValidator.Subject);

        ConReminder(sheet, spec, conBefore, reminders);
        if (spec?.Xp is { } xp && sheet.Level is { } current && Advancement.LevelForXp(xp) > current)
        {
            reminders.Add(SheetXp.LevelDue(xp, current, sheet.Classes.Count > 1, levelOnly: sheet.Classes.Count == 0));
        }

        var diff = SheetDiff.Between(before, sheet);
        return new SheetUpdateResult(sheet, before is null, diff, notes, reminders);
    }

    /// <exception cref="ArgumentException"><paramref name="edition"/> is not "2014" or "2024" (a host bug).</exception>
    internal static void CheckEdition(string edition)
    {
        if (edition is not (V.Editions.E2014 or V.Editions.E2024))
        {
            throw new ArgumentException($"The fallback edition must be \"2014\" or \"2024\", not \"{edition}\".", nameof(edition));
        }
    }

    private static CharacterSheet ApplyText(CharacterSheet sheet, SheetSpec spec) => sheet with
    {
        Player = Text(spec.Player, sheet.Player),
        Ruleset = spec.Ruleset is null ? sheet.Ruleset : V.Editions.Set.TryMatch(spec.Ruleset, out var ruleset) ? ruleset : sheet.Ruleset,
        Species = Text(spec.Species, sheet.Species),
        Lineage = Text(spec.Lineage, sheet.Lineage),
        Background = Text(spec.Background, sheet.Background),
        NotesMd = spec.Notes is null ? sheet.NotesMd : spec.Notes.Trim(),
        SheetSource = Text(spec.SheetSource, sheet.SheetSource),
    };

    private static (CharacterSheet Sheet, bool Changed) ApplyClasses(CharacterSheet sheet, SheetSpec spec, List<string> problems)
    {
        if (spec.Classes is { } given)
        {
            var classes = new List<SheetClass>();
            for (var i = 0; i < given.Count; i++)
            {
                var c = given[i];
                var srd = ClassTable.Find(c.Class);
                var name = srd?.Index ?? c.Class!.Trim();
                var kept = sheet.Classes.FirstOrDefault(old => SameClass(old.Class, name));

                // A homebrew class the sheet already has keeps its stored hit die when the update leaves it out.
                var hitDie = srd?.HitDie ?? c.HitDie ?? kept?.HitDie;
                if (hitDie is null)
                {
                    problems.Add(
                        $"classes item {N(i + 1)} ({DslText.Echo(name)}): hit_die is required for a class that is not an SRD class " +
                        $"({ClassTable.List}) and not on the sheet: 4, 6, 8, 10 or 12.");
                }

                classes.Add(new SheetClass(name, string.IsNullOrWhiteSpace(c.Subclass) ? null : c.Subclass.Trim(), c.Level!.Value, hitDie)
                {
                    Extra = kept?.Extra,
                });
            }

            var changed = !Composition(classes).SequenceEqual(Composition(sheet.Classes));
            return (sheet with { Classes = classes, Level = classes.Sum(c => c.Level) }, changed);
        }

        if (spec.Level is { } level)
        {
            if (sheet.Classes.Count > 0)
            {
                problems.Add(
                    $"level: this sheet has classes ({sheet.ClassSummary}), so its level is their sum; give classes with the new levels, or use level_up.");
                return (sheet, false);
            }

            return (sheet with { Level = level }, level != sheet.Level);
        }

        return (sheet, false);
    }

    private static CharacterSheet ApplyNumbers(CharacterSheet sheet, SheetSpec spec)
    {
        var abilities = sheet.Abilities;
        foreach (var (ability, score) in spec.Abilities?.Given() ?? [])
        {
            abilities = SheetMaps.With(abilities, ability, score);
        }

        return sheet with
        {
            Xp = spec.Xp ?? sheet.Xp,
            Abilities = Reorder(abilities),
            Ac = spec.Ac ?? sheet.Ac,
            MaxHp = spec.MaxHp ?? sheet.MaxHp,
            MaxHpReduction = spec.MaxHpReduction ?? sheet.MaxHpReduction,
            Hp = spec.Hp ?? sheet.Hp,
            TempHp = spec.TempHp ?? sheet.TempHp,
            Speed = spec.Speed ?? sheet.Speed,
            InitiativeBonus = spec.InitiativeBonus ?? sheet.InitiativeBonus,
            PassivePerception = spec.PassivePerception ?? sheet.PassivePerception,
            SpellSaveDc = spec.SpellSaveDc ?? sheet.SpellSaveDc,
            SpellAttack = spec.SpellAttack ?? sheet.SpellAttack,
            Inspiration = spec.Inspiration ?? sheet.Inspiration,
        };
    }

    private static CharacterSheet ApplySavesAndDefenses(CharacterSheet sheet, SheetSpec spec)
    {
        var saves = sheet.Saves;
        if (spec.SaveProficiencies is { } proficient)
        {
            saves = saves with { Proficient = Canonical(proficient, V.Abilities.Set) };
        }

        if (spec.SaveBonus is { } bonusSpec)
        {
            var bonus = saves.Bonus;
            foreach (var (ability, amount) in bonusSpec.Given())
            {
                bonus = amount == 0 ? SheetMaps.Without(bonus, ability) : SheetMaps.With(bonus, ability, amount);
            }

            saves = saves with { Bonus = Reorder(bonus) };
        }

        var defenses = sheet.Defenses;
        if (spec.Defenses is { } d)
        {
            defenses = defenses with
            {
                Resist = d.Resist is null ? defenses.Resist : Canonical(d.Resist, V.DamageTypes.Set),
                Immune = d.Immune is null ? defenses.Immune : Canonical(d.Immune, V.DamageTypes.Set),
                Vulnerable = d.Vulnerable is null ? defenses.Vulnerable : Canonical(d.Vulnerable, V.DamageTypes.Set),
                ConditionImmune = d.ConditionImmune is null ? defenses.ConditionImmune : Canonical(d.ConditionImmune, SheetValues.Conditions.Set),
            };
        }

        return sheet with { Saves = saves, Defenses = defenses };
    }

    private static CharacterSheet ApplySlots(CharacterSheet sheet, SheetSpec spec)
    {
        var slots = sheet.SpellSlots;
        if (spec.Slots is { } maxima)
        {
            slots = WithSpellLevels(slots, maxima);
        }

        if (spec.Pact is { } pact)
        {
            slots = pact.Max is 0 or null
                ? SheetMaps.Without(slots, SpellSlotEntry.PactKey)
                : SetSlot(slots, SpellSlotEntry.PactKey, pact.Max.Value, pact.Level);
        }

        return sheet with { SpellSlots = slots };
    }

    /// <summary>Slot maxima for levels 1-9 (index 0 = 1st): a level beyond the list or with 0 is removed; used is kept, capped at the new maximum.</summary>
    internal static IReadOnlyDictionary<string, SpellSlotEntry> WithSpellLevels(IReadOnlyDictionary<string, SpellSlotEntry> slots, IReadOnlyList<int> maxima)
    {
        for (var level = 1; level <= SheetLimits.MaxSpellLevel; level++)
        {
            var max = level <= maxima.Count ? maxima[level - 1] : 0;
            var key = SpellSlotEntry.KeyOf(level);
            slots = max == 0 ? SheetMaps.Without(slots, key) : SetSlot(slots, key, max, null);
        }

        return slots;
    }

    private static IReadOnlyDictionary<string, SpellSlotEntry> SetSlot(IReadOnlyDictionary<string, SpellSlotEntry> slots, string key, int max, int? level)
    {
        var entry = slots.TryGetValue(key, out var old)
            ? old with { Max = max, Used = Math.Min(old.Used, max), Level = level ?? old.Level }
            : new SpellSlotEntry(max, 0, level);
        return SheetMaps.With(slots, key, entry);
    }

    private static CharacterSheet ApplyResources(CharacterSheet sheet, SheetSpec spec, List<string> problems)
    {
        if (spec.Resources is not { } given)
        {
            return sheet;
        }

        var resources = sheet.Resources;
        for (var i = 0; i < given.Count; i++)
        {
            var item = given[i];
            var key = SheetResource.KeyOf(item.Name!);
            var name = item.Name!.Trim();
            var where = $"resources item {N(i + 1)} ({DslText.Echo(name)})";
            if (item.Remove == true)
            {
                if (!resources.ContainsKey(key))
                {
                    problems.Add($"{where}: there is no resource of that name to remove.");
                }

                resources = SheetMaps.Without(resources, key);
                continue;
            }

            if (resources.TryGetValue(key, out var old))
            {
                var countedFields = item.Max is not null || item.Used is not null || item.Recharge is not null;
                if (old.IsCounted ? item.State is not null : countedFields)
                {
                    problems.Add(
                        $"{where}: it is a {(old.IsCounted ? "counted resource (max, used, recharge)" : "tracker (state)")}; to change its kind, " +
                        "remove it first ({\"name\": …, \"remove\": true}), then add it again.");
                    continue;
                }

                var max = old.IsCounted ? item.Max ?? old.Max : null;
                var state = old.IsCounted ? null : item.State?.Trim() ?? old.State;

                var used = item.Used ?? (max is null ? null : Math.Min(old.Used ?? 0, max.Value));
                if (max is { } m && used > m)
                {
                    problems.Add($"{where}: used is {N(used.Value)}, more than max {N(m)}.");
                    continue;
                }

                resources = SheetMaps.With(resources, key, old with
                {
                    Name = name,
                    Max = max,
                    Used = max is null ? old.Used : used ?? 0,
                    Recharge = max is null ? null : Recharge(item.Recharge) ?? old.Recharge ?? SheetValues.Recharges.Default,
                    State = state,
                    Note = item.Note is null ? old.Note : item.Note.Trim().Length == 0 ? null : item.Note.Trim(),
                });
            }
            else
            {
                if (item.Max is null && item.State is null)
                {
                    problems.Add($"{where}: a new resource needs max (counted, e.g. {{\"name\": \"{DslText.Echo(name)}\", \"max\": 4, \"recharge\": \"long_rest\"}}) or state (a tracker, e.g. {{\"name\": \"{DslText.Echo(name)}\", \"state\": \"set\"}}).");
                    continue;
                }

                resources = SheetMaps.With(resources, key, new SheetResource(
                    name,
                    item.Max,
                    item.Max is null ? null : item.Used ?? 0,
                    item.Max is null ? null : Recharge(item.Recharge) ?? SheetValues.Recharges.Default,
                    item.State?.Trim(),
                    string.IsNullOrWhiteSpace(item.Note) ? null : item.Note.Trim()));
            }
        }

        return sheet with { Resources = resources };
    }

    private static CharacterSheet ApplyLists(CharacterSheet sheet, SheetSpec spec) => sheet with
    {
        Feats = spec.Feats is null ? sheet.Feats : Entries(spec.Feats, sheet.Feats),
        Features = spec.Features is null ? sheet.Features : Entries(spec.Features, sheet.Features),
        Spells = spec.Spells is null ? sheet.Spells : Entries(spec.Spells, sheet.Spells),
        Languages = spec.Languages is null ? sheet.Languages : Entries(spec.Languages, sheet.Languages),
    };

    /// <summary>The list as given; an entry the sheet already had (same name key) keeps its ref, source, prepared and unknown keys.</summary>
    private static IReadOnlyList<SheetEntry> Entries(IReadOnlyList<string> names, IReadOnlyList<SheetEntry> old) =>
        names.Select(n => n.Trim()).Select(name =>
            old.FirstOrDefault(e => CampaignText.Key(e.Name) == CampaignText.Key(name)) is { } kept
                ? kept with { Name = name }
                : new SheetEntry(name)).ToList();

    /// <summary>Hit Dice maxima from the classes (per die size); used kept, capped; a die no class gives any more is dropped.</summary>
    internal static CharacterSheet DeriveHitDice(CharacterSheet sheet)
    {
        if (sheet.Classes.Count == 0 || sheet.Classes.Any(c => c.Die is null))
        {
            return sheet;
        }

        var dice = SheetMaps.Empty<HitDiceEntry>();
        foreach (var group in sheet.Classes.GroupBy(c => c.Die!.Value).OrderByDescending(g => g.Key))
        {
            var key = SheetJson.DieKey(group.Key);
            var max = group.Sum(c => c.Level);
            dice = SheetMaps.With(dice, key, sheet.HitDice.TryGetValue(key, out var old)
                ? old with { Max = max, Used = Math.Min(old.Used, max) }
                : new HitDiceEntry(max, 0));
        }

        return sheet with { HitDice = dice };
    }

    /// <summary>
    /// The spell slots the class tables give (D18), only ever from a class that casts, so a sheet whose classes do not cast
    /// (an Eldritch Knight, an Arcane Trickster, a homebrew pact) keeps every slot it was given:
    /// <list type="bullet">
    /// <item>a single-class SRD caster: its table, per spell level and Pact Magic (a warlock has no per-level slots, the
    /// others no pact slots);</item>
    /// <item>a warlock on a multiclass sheet: the Pact Magic slots from the warlock table at the warlock level alone (Pact
    /// Magic is its own table, never combined);</item>
    /// <item>the per-level slots of a multiclass sheet with a Spellcasting class (full or half caster): never computed (the
    /// Multiclass Spellcaster table is in the multiclassing rules, which are not in this server's data); the sheet keeps
    /// the slots it is given and a reminder says to give them.</item>
    /// </list>
    /// Used is kept, capped at the new maximum.
    /// </summary>
    /// <param name="perLevel">Derive the slots of levels 1-9 (false when the caller gave them).</param>
    /// <param name="pact">Derive the Pact Magic slots (false when the caller gave them).</param>
    internal static CharacterSheet DeriveSlots(
        CharacterSheet sheet, string edition, List<string> notes, List<SheetReminder> reminders, bool perLevel = true, bool pact = true)
    {
        var slots = sheet.SpellSlots;
        if (sheet.Classes.Count == 1)
        {
            if (sheet.Classes[0].Srd is not { IsCaster: true } srd)
            {
                return sheet;
            }

            var level = sheet.Classes[0].Level;
            var row = SpellSlotTables.For(srd.CasterKind, level, edition);
            if (perLevel)
            {
                slots = WithSpellLevels(slots, row.Slots);
            }

            if (pact)
            {
                slots = row.Pact is { } p ? SetSlot(slots, SpellSlotEntry.PactKey, p.Count, p.Level) : SheetMaps.Without(slots, SpellSlotEntry.PactKey);
            }

            if (perLevel || (pact && row.Pact is not null))
            {
                notes.Add($"Spell slots from the {srd.Name} table at level {N(level)} ({edition}): {SlotText(slots)}.");
            }

            return sheet with { SpellSlots = slots };
        }

        var warlock = sheet.Classes.FirstOrDefault(c => c.Srd?.CasterKind == SheetValues.CasterKinds.Pact);
        if (pact && warlock is not null)
        {
            var p = SpellSlotTables.Pact(warlock.Level);
            slots = SetSlot(slots, SpellSlotEntry.PactKey, p.Count, p.Level);
            notes.Add($"Pact Magic slots from the Warlock table at warlock level {N(warlock.Level)}: pact {N(p.Count)} × level {N(p.Level)}.");
        }

        if (perLevel && sheet.Classes.Any(c => c.Srd is { IsCaster: true, CasterKind: not SheetValues.CasterKinds.Pact }))
        {
            reminders.Add(GiveSlots(sheet, warlock is not null));
        }

        return sheet with { SpellSlots = slots };
    }

    /// <summary>The reminder for a multiclass sheet's per-level slots (D18).</summary>
    /// <param name="pactDerived">A warlock's Pact Magic slots were derived, so only the per-level slots are asked for.</param>
    internal static SheetReminder GiveSlots(CharacterSheet sheet, bool pactDerived) => new(
        SheetValues.ReminderKinds.GiveSlots,
        $"Multiclass spell slots are not computed ({sheet.ClassSummary}; the multiclassing rules are not in this server's data): " +
        (pactDerived ? "give the slots of levels 1-9 (the Pact Magic slots follow the warlock level)." : "give the slots."),
        "update",
        new Dictionary<string, object?> { ["sheet"] = new ReminderPlaceholder("{\"slots\": [4, 3, …]}") });

    /// <summary>The reminder that a change of the classes or level kept max_hp (contract §7.2: never re-derived).</summary>
    private static SheetReminder MaxHpNotRecomputed(CharacterSheet before, CharacterSheet after, int maxHp)
    {
        var change = before.Classes.Count > 0 || after.Classes.Count > 0
            ? $"The classes changed ({Summary(before)} → {Summary(after)})"
            : $"The level changed ({N(before.Level ?? 0)} → {N(after.Level ?? 0)})";
        return new SheetReminder(
            SheetValues.ReminderKinds.MaxHpNotRecomputed,
            $"{change}, but max_hp stays {N(maxHp)}: it is not recomputed (it may have been given, and feats such as Tough are " +
            "not known). level_up adds a level's hit points; give max_hp to set it.",
            "update",
            new Dictionary<string, object?> { ["sheet"] = new ReminderPlaceholder("{\"max_hp\": …}") });
    }

    private static string Summary(CharacterSheet sheet) => sheet.Classes.Count > 0 ? sheet.ClassSummary : "none";

    /// <summary>
    /// The starting class's saving throws, when the sheet has none and is new or its starting class changed: absent is not
    /// "none" only then, so a sheet given <c>save_proficiencies: []</c> keeps it through later updates.
    /// </summary>
    private static CharacterSheet DeriveSaves(CharacterSheet sheet, CharacterSheet? before, SheetSpec? spec, List<string> notes)
    {
        var startingChanged = before is null || StartingClass(before) != StartingClass(sheet);
        if (!startingChanged || spec?.SaveProficiencies is not null || sheet.Saves.Proficient.Count > 0 ||
            sheet.Classes.FirstOrDefault()?.Srd is not { } starting)
        {
            return sheet;
        }

        notes.Add($"Saving throw proficiencies from the starting class ({starting.Name}): {string.Join(", ", starting.Saves)}.");
        return sheet with { Saves = sheet.Saves with { Proficient = starting.Saves } };
    }

    private static CharacterSheet DeriveHitPoints(CharacterSheet sheet, SheetSpec? spec, string edition, List<string> notes)
    {
        if (sheet.MaxHp is null && spec?.MaxHp is null && sheet.Classes.Count > 0)
        {
            if (sheet.Modifier(V.Abilities.Con) is not { } con)
            {
                notes.Add("max_hp is not derived without a Con score: give abilities.con, or max_hp.");
            }
            else if (sheet.Classes.All(c => c.Die is not null))
            {
                var max = LevelHitPoints.FixedTotal(sheet.Classes.Select(c => (c.Die!.Value, c.Level)).ToList(), con, edition);
                sheet = sheet with { MaxHp = max };
                notes.Add($"max_hp {N(max)} derived (the fixed hit points per level, Con {Signed(con)}); give max_hp to set it: feats such as Tough are not counted.");
            }
        }

        if (sheet.Hp is null && spec?.Hp is null && sheet.EffectiveMaxHp(edition) is { } effective)
        {
            sheet = sheet with { Hp = effective };
            notes.Add($"hp set to the maximum, {N(effective)}.");
        }

        return sheet;
    }

    /// <summary>hp given above the effective maximum is refused; hp left above a lowered maximum is capped (noted).</summary>
    private static void CheckHitPoints(CharacterSheet sheet, SheetSpec? spec, string edition, List<string> problems, List<string> notes, out CharacterSheet result)
    {
        result = sheet;
        if (sheet.Hp is not { } hp || sheet.EffectiveMaxHp(edition) is not { } effective || hp <= effective)
        {
            return;
        }

        if (spec?.Hp is not null)
        {
            problems.Add($"hp is {N(hp)}, above the hit point maximum {N(effective)}{(effective != sheet.MaxHp ? " (after its reduction)" : string.Empty)}.");
            return;
        }

        result = sheet with { Hp = effective };
        notes.Add($"hp lowered to the new maximum, {N(effective)}.");
    }

    private static void ConReminder(CharacterSheet sheet, SheetSpec? spec, int? conBefore, List<SheetReminder> reminders)
    {
        var conAfter = sheet.Modifier(V.Abilities.Con);
        if (conBefore is not { } was || conAfter is not { } now || was == now || spec?.MaxHp is not null ||
            sheet.MaxHp is not { } max || sheet.Level is not { } level)
        {
            return;
        }

        var change = (now - was) * level;
        var target = Math.Max(SheetLimits.MinMaxHp, max + change);
        reminders.Add(new SheetReminder(
            SheetValues.ReminderKinds.ConChanged,
            $"The Con modifier changed from {Signed(was)} to {Signed(now)}: by the rules the hit point maximum changes by {Signed(change)} " +
            $"(1 per level), to {N(target)}; it was not changed. Give max_hp to apply it.",
            "update",
            new Dictionary<string, object?> { ["sheet"] = new JsonObject { ["max_hp"] = target } }));
    }

    private static string? Text(string? given, string? current) =>
        given is null ? current : given.Trim().Length == 0 ? null : given.Trim();

    private static string? Recharge(string? text) => text is not null && SheetValues.Recharges.Set.TryMatch(text, out var canonical) ? canonical : null;

    private static IReadOnlyList<string> Canonical(IReadOnlyList<string> values, DslValueSet set)
    {
        var list = new List<string>();
        foreach (var value in values)
        {
            if (set.TryMatch(value, out var canonical) && !list.Contains(canonical))
            {
                list.Add(canonical);
            }
        }

        return list;
    }

    private static IReadOnlyDictionary<string, int> Reorder(IReadOnlyDictionary<string, int> map) =>
        SheetMaps.Of(V.Abilities.All.Where(map.ContainsKey).Select(a => new KeyValuePair<string, int>(a, map[a]))
            .Concat(map.Where(p => !V.Abilities.All.Contains(p.Key))));

    private static string? StartingClass(CharacterSheet sheet) =>
        sheet.Classes.FirstOrDefault() is { } c ? ClassTable.Find(c.Class)?.Index ?? CampaignText.Key(c.Class) : null;

    private static IEnumerable<(string, int)> Composition(IEnumerable<SheetClass> classes) =>
        classes.Select(c => (ClassTable.Find(c.Class)?.Index ?? CampaignText.Key(c.Class), c.Level));

    private static bool SameClass(string a, string b) =>
        (ClassTable.Find(a)?.Index ?? CampaignText.Key(a)) == (ClassTable.Find(b)?.Index ?? CampaignText.Key(b));

    internal static string SlotText(IReadOnlyDictionary<string, SpellSlotEntry> slots) =>
        slots.Count == 0
            ? "none"
            : string.Join(", ", slots.Select(p => p.Key == SpellSlotEntry.PactKey
                ? $"pact {N(p.Value.Max)} × level {N(p.Value.Level ?? 0)}"
                : $"{Ordinal(p.Key)} {N(p.Value.Max)}"));

    private static string Ordinal(string key) => key switch
    {
        "1" => "1st",
        "2" => "2nd",
        "3" => "3rd",
        _ => key + "th",
    };

    internal static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    internal static string Signed(int value) => value >= 0 ? "+" + N(value) : N(value);
}
