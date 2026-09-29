using System.Globalization;
using System.Text.RegularExpressions;
using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation;

namespace DndMcp.Repository.Srd.Combatants;

internal sealed partial class MonsterReading
{
    /// <summary>Legendary actions that carry their own spellcasting block (2024): the combat spells each one casts.</summary>
    private readonly Dictionary<string, List<string>> _legendarySpells = new(StringComparer.Ordinal);

    /// <summary>
    /// Named spellcasting actions (2024 succubus "Charm" casts Dominate Person; ice devil "Ice Wall" casts Wall of
    /// Ice): block name → the combat spells it added, so a multiattack's "uses Charm" resolves to them.
    /// </summary>
    private readonly Dictionary<string, List<string>> _blockSpells = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every spellcasting block → combat spells in <see cref="StatBlock.Spells"/> (Shield-like spells in
    /// <see cref="StatBlock.Reactions"/>), slots in <see cref="StatBlock.SpellSlots"/>, and one note and at most one
    /// warning per block naming the spells that are not cast. A block with its own usage shares it among its spells, and
    /// one that casts several spells in one use gets routines for that (<see cref="ShareBlockUsage"/>).
    /// </summary>
    private void ReadSpellcasting()
    {
        if (Is2014)
        {
            foreach (var trait in _m.Traits.Where(t => t.Spellcasting is not null && IsSpellcastingName(t.Name)))
            {
                ReadBlock(trait, StatBlockValues.ActionSlots.Action);
            }

            return;
        }

        foreach (var (list, slot) in new[]
                 {
                     (_m.Actions, StatBlockValues.ActionSlots.Action), (_m.BonusActions, StatBlockValues.ActionSlots.BonusAction),
                     (_m.Reactions, StatBlockValues.ActionSlots.Reaction), (_m.Legendary, StatBlockValues.ActionSlots.Legendary),
                 })
        {
            foreach (var entry in list.Where(e => e.Spellcasting is not null))
            {
                var names = ReadBlock(entry, slot);
                if (slot == StatBlockValues.ActionSlots.Legendary)
                {
                    _legendarySpells[entry.Name] = names;
                }
            }
        }

        foreach (var trait in _m.Traits.Where(t => t.Spellcasting is not null))
        {
            _log.Note($"Trait {trait.Name}: its spells are cast only under the trait's conditions and are not simulated.");
        }
    }

    /// <summary>
    /// Spellcasting actions that cast several spells in one use (2024 pit fiend "Hellfire Spellcasting (Recharge 4-6).
    /// The pit fiend casts Fireball (level 5 version) twice …"): block name → the use_actions routines that do it, put
    /// in the action list where the block stands (<see cref="ReadActionList"/>).
    /// </summary>
    private readonly Dictionary<string, List<StatBlockAction>> _spellRoutines = new(StringComparer.Ordinal);

    /// <summary>One block's spells; returns the names of the combat spells it added.</summary>
    private List<string> ReadBlock(RecordAction block, string slot)
    {
        var sc = block.Spellcasting!;
        var where = block.Name;
        var ability = ProseText.AbilityKey(sc.Ability);
        if (ability is null)
        {
            _log.Unparsed(where, $"The spellcasting ability \"{sc.Ability}\" is not an ability; Intelligence is used for its DC and attack bonus where the data gives none.");
            ability = DslValues.Abilities.Int;
        }

        var modifier = _m.Abilities.Modifier(ability);
        var dc = sc.Dc ?? 8 + _m.ProficiencyBonus + modifier;
        var attack = sc.Modifier ?? _m.ProficiencyBonus + modifier;
        var cantripLevel = sc.CasterLevel ?? Math.Max(1, (int)Math.Floor(_m.ChallengeRating));
        if (sc.Slots is { } slots)
        {
            foreach (var (level, count) in slots)
            {
                _slots[level] = count;
            }
        }

        var added = new List<string>();
        var none = new List<string>();
        var notModelled = new List<string>();
        var attackComputed = false;
        var castsCantrip = false;
        UsageSpec? blockUsage = null;
        var takeBlockUsage = new List<int>();
        foreach (var spell in sc.Spells)
        {
            var document = _lookup.Get(Edition, SrdKinds.Spell, spell.Slug);
            if (document is null)
            {
                _log.Unresolved(where, $"The spell {spell.Name} ({Edition}/spell/{spell.Slug}) is not in the rules data; it is not cast.");
                continue;
            }

            var profile = _normalizer.Profile(document);
            if (!profile.IsCombat)
            {
                _uncastSpells.TryAdd(spell.Name, profile.Reason ?? profile.Kind);
                (profile.Kind == SpellProfileKinds.NoCombatEffect ? none : notModelled).Add(
                    profile.Kind == SpellProfileKinds.NoCombatEffect ? spell.Name : $"{spell.Name} ({profile.Reason})");
                continue;
            }

            var castLevel = spell.LevelIsCastLevel ? Math.Max(spell.Level, profile.Level) : profile.Level;
            var takesBlockUsage = spell.Usage is null && block.Usage is not null;
            var usage = takesBlockUsage
                ? blockUsage ??= Usage(block.Usage, where)
                : SpellUsage(sc, spell, profile, castLevel, where);
            var action = SpellNormalizer.Cast(profile, spell.Name, castLevel, cantripLevel, dc, attack, modifier, slot, usage);
            if (spell.Notes is { } dataNote)
            {
                action = action with { Notes = [.. action.Notes, $"The stat block notes: {dataNote}."] };
            }

            if (sc.Dc is null && action.Save is not null)
            {
                action = action with { Notes = [.. action.Notes, string.Create(CultureInfo.InvariantCulture, $"Save DC {dc} computed as 8 + proficiency + {ability} (the data gives none).")] };
            }

            attackComputed |= sc.Modifier is null && action.Kind == StatBlockValues.ActionKinds.Attack;
            castsCantrip |= profile.Level == 0;
            if (profile.Approximation is { } approximation)
            {
                _log.Approximated(spell.Name, approximation);
            }

            if (action.Kind == StatBlockValues.ActionKinds.Parry)
            {
                _reactions.Add(action);
            }
            else
            {
                if (takesBlockUsage)
                {
                    takeBlockUsage.Add(_spells.Count);
                }

                _spells.Add(action);
                added.Add(spell.Name);
            }
        }

        if (blockUsage is not null)
        {
            ShareBlockUsage(block, slot, blockUsage, takeBlockUsage, added);
        }

        if (attackComputed)
        {
            _log.Note(string.Create(CultureInfo.InvariantCulture, $"{where}: spell attack bonus +{attack} computed as proficiency + {ability} (the data gives none)."));
        }

        if (sc.CasterLevel is null && castsCantrip)
        {
            _log.Note(string.Create(CultureInfo.InvariantCulture, $"{where}: cantrips scale as for a level-{cantripLevel} caster (its CR; the stat block gives no caster level)."));
        }

        if (none.Count > 0)
        {
            _log.Note($"{where}: no combat effect in the simulator: {string.Join(", ", none)}.");
        }

        if (notModelled.Count > 0)
        {
            _log.NotModelled(where, $"Spells not cast by the simulator: {string.Join("; ", notModelled)}.");
        }

        _blockSpells[block.Name] = added;
        return added;
    }

    /// <summary>
    /// A spellcasting action with its own usage (2024 "Hellfire Spellcasting (Recharge 4-6)") spends that one usage
    /// whichever of its spells it casts. Two or more combat spells taking a recharge share it through the pool
    /// <c>recharge:{block}</c>, as a 2014 dragon's breaths do; without it the pit fiend would cast Fireball, Hold Monster
    /// and Wall of Fire each on a recharge of its own. An action that casts several spells in one use ("casts Fireball
    /// (level 5 version) twice … It can replace one Fireball with Hold Monster (level 7 version) or Wall of Fire") also
    /// becomes use_actions routines with that same usage, one per way to fill it; the engine spends the usage once for
    /// the routine and casts every spell it names. Uses per day cannot be shared (the engine keeps one count per action),
    /// so a block whose uses would be shared says so in a warning.
    /// </summary>
    private void ShareBlockUsage(RecordAction block, string slot, UsageSpec usage, List<int> taking, List<string> added)
    {
        var text = ProseText.Normalize(block.Desc);
        var twice = CastsTwice().Match(text);
        var repeated = twice.Success && slot is StatBlockValues.ActionSlots.Action or StatBlockValues.ActionSlots.BonusAction
            ? added.FirstOrDefault(n => n.Equals(twice.Groups["spell"].Value, StringComparison.OrdinalIgnoreCase))
            : null;
        if (taking.Count < 2 && repeated is null)
        {
            return;
        }

        switch (usage.Kind)
        {
            case StatBlockValues.UsageKinds.Recharge:
                usage = usage with { Pool = $"recharge:{block.Name}" };
                foreach (var i in taking)
                {
                    _spells[i] = _spells[i] with { Usage = usage, Notes = [.. _spells[i].Notes, $"One of {block.Name}'s spells; they share its one recharge."] };
                }

                break;
            case StatBlockValues.UsageKinds.PerDay:
                _log.Approximated(block.Name, "Its uses per day are one count for all its spells in the text; the simulator counts them for each spell separately.");
                break;
        }

        if (repeated is null)
        {
            return;
        }

        StatBlockAction Routine(string name, IReadOnlyList<ActionUse> uses) => new()
        {
            Name = name,
            Kind = StatBlockValues.ActionKinds.UseActions,
            Slot = slot,
            Uses = uses,
            Usage = usage,
            Text = text,
            Notes = [$"Casts {string.Join(" and ", uses.Select(u => u.Count == 2 ? $"{u.ActionName} twice" : u.ActionName))} as one action."],
        };

        var routines = new List<StatBlockAction> { Routine(block.Name, [new ActionUse(repeated, 2)]) };
        if (ReplacesOne().Match(text) is { Success: true } replace && replace.Groups["spell"].Value.Equals(repeated, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var alternative in AlternativeSeparator().Split(replace.Groups["others"].Value).Select(ProseText.StripParentheticals))
            {
                if (added.FirstOrDefault(n => n.Equals(alternative, StringComparison.OrdinalIgnoreCase) && n != repeated) is { } other)
                {
                    routines.Add(Routine($"{block.Name} ({other})", [new ActionUse(repeated, 1), new ActionUse(other, 1)]));
                }
            }
        }

        _spellRoutines[block.Name] = routines;
    }

    private UsageSpec SpellUsage(RecordSpellcasting sc, RecordSpell spell, SpellProfile profile, int castLevel, string where)
    {
        if (sc.Slots is { } slots && profile.Level > 0 && spell.Usage is null)
        {
            if (!slots.ContainsKey(profile.Level))
            {
                _log.Unparsed(where, string.Create(CultureInfo.InvariantCulture, $"{spell.Name} is a level {profile.Level} spell but the stat block has no level {profile.Level} slots; it is not limited."));
                return UsageSpec.AtWill;
            }

            return new UsageSpec(StatBlockValues.UsageKinds.Pool, Pool: string.Create(CultureInfo.InvariantCulture, $"slot:{profile.Level}"));
        }

        return Usage(spell.Usage, $"{where} ({spell.Name})");
    }

    [GeneratedRegex(@"\bcasts (?<spell>[A-Z][A-Za-z' ]+?)(?: \(level \d+ version\))? twice\b")]
    private static partial Regex CastsTwice();

    [GeneratedRegex(@"\bcan replace one (?<spell>[A-Z][A-Za-z' ]+?) with (?<others>[^.]+)\.")]
    private static partial Regex ReplacesOne();

    [GeneratedRegex(@"\s*,\s*(?:or\s+)?|\s+or\s+")]
    private static partial Regex AlternativeSeparator();
}
