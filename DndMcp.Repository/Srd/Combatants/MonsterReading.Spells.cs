using System.Globalization;
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
    /// warning per block naming the spells that are not cast.
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

    /// <summary>One block's spells; returns the names of the combat spells it added.</summary>
    private List<string> ReadBlock(RecordAction block, string slot)
    {
        var sc = block.Spellcasting!;
        var where = block.Name;
        var ability = ProseText.AbilityKey(sc.Ability) ?? "int";
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
            var usage = spell.Usage is null && block.Usage is not null
                ? Usage(block.Usage, where)
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
                _spells.Add(action);
                added.Add(spell.Name);
            }
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
}
