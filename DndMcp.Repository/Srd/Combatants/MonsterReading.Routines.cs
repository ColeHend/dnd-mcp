using System.Globalization;
using System.Text.RegularExpressions;
using DndMcp.Domain.Simulation;
using DndMcp.Repository.Srd.Models;

namespace DndMcp.Repository.Srd.Combatants;

internal sealed partial class MonsterReading
{
    /// <summary>Spells the monster knows that are not cast (no combat effect, or not modelled): name → why.</summary>
    private readonly Dictionary<string, string> _uncastSpells = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The Multiattack action → routines. Fixed steps plus a choice: a choice whose every option is one use of one
    /// action stays a choice (<see cref="MultiattackRoutine.Choose"/> of <see cref="MultiattackRoutine.Options"/>);
    /// anything else (options worth several uses, bundles, a capped option such as "one of which can be a bite") is
    /// expanded into one routine per allowed combination, which is exact because the policy picks the best routine.
    /// </summary>
    private void ReadMultiattacks()
    {
        foreach (var ma in _m.Actions.Where(a => a.IsMultiattack))
        {
            var where = "Multiattack";
            var fixedSteps = new List<(string Name, int Count)>();
            foreach (var step in ma.MultiattackActions ?? [])
            {
                fixedSteps.Add((step.ActionName, Count(step.Count, step.ActionName, where)));
            }

            var combos = new List<List<(string Name, int Count)>>();
            var choose = 0;
            var chooseOptions = new List<string>();
            if (ma.MultiattackOptions is { } choice)
            {
                var options = choice.From.Options.Select(o => Uses(o, where)).ToList();
                var singles = options.All(o => o.Count == 1);
                if (singles && options.All(o => o[0].Count == 1))
                {
                    choose = choice.Choose;
                    chooseOptions.AddRange(options.Select(o => o[0].Name));
                    combos.Add([]);
                }
                else if (singles && choice.Choose > 1 && options.All(o => o[0].Count >= choice.Choose))
                {
                    // "Any combination": choose N, each option offered N times.
                    choose = choice.Choose;
                    chooseOptions.AddRange(options.Select(o => o[0].Name));
                    combos.Add([]);
                }
                else if (choice.Choose == 1)
                {
                    combos.AddRange(options);
                }
                else
                {
                    combos.AddRange(Multisets(options, choice.Choose));
                }
            }
            else
            {
                combos.Add([]);
            }

            foreach (var combo in combos)
            {
                AddRoutines(fixedSteps.Concat(combo).ToList(), choose, chooseOptions, where);
            }
        }

        for (var i = 0; i < _routines.Count; i++)
        {
            _routines[i] = _routines[i] with
            {
                Label = _routines.Count == 1 ? "Multiattack" : string.Create(CultureInfo.InvariantCulture, $"Multiattack (option {i + 1})"),
            };
        }
    }

    private List<(string Name, int Count)> Uses(MultiattackOption option, string where) =>
        option.OptionType == "multiple"
            ? (option.Items ?? []).SelectMany(i => Uses(i, where)).ToList()
            : [(option.ActionName ?? string.Empty, option.Count is { } c ? Count(c, option.ActionName ?? "?", where) : 1)];

    // Every way to pick `k` uses from options, each option usable up to its count (a bundle once), as step lists.
    private static IEnumerable<List<(string Name, int Count)>> Multisets(IReadOnlyList<List<(string Name, int Count)>> options, int k)
    {
        var caps = options.Select(o => o.Count == 1 ? o[0].Count : 1).ToArray();
        var weight = options.Select(o => o.Count == 1 ? 1 : o.Sum(x => x.Count)).ToArray();
        var picked = new int[options.Count];
        var results = new List<List<(string, int)>>();
        Recurse(0, k);
        return results;

        void Recurse(int index, int left)
        {
            if (left == 0)
            {
                var steps = new List<(string, int)>();
                for (var i = 0; i < options.Count; i++)
                {
                    if (picked[i] == 0)
                    {
                        continue;
                    }

                    steps.AddRange(options[i].Count == 1 ? [(options[i][0].Name, picked[i])] : options[i]);
                }

                results.Add(steps);
                return;
            }

            if (index == options.Count)
            {
                return;
            }

            for (var n = Math.Min(caps[index], left / weight[index]); n >= 0; n--)
            {
                picked[index] = n;
                Recurse(index + 1, left - n * weight[index]);
            }

            picked[index] = 0;
        }
    }

    private void AddRoutines(List<(string Name, int Count)> steps, int choose, List<string> options, string where)
    {
        // A reference to Spellcasting expands to one routine per combat spell it can cast.
        var expanded = new List<List<ActionUse>> { new() };
        foreach (var (name, count) in steps)
        {
            var resolved = Resolve(name, where);
            if (resolved.Count == 0)
            {
                continue;
            }

            expanded = resolved.Count == 1
                ? expanded.Select(r => r.Append(new ActionUse(resolved[0], count)).ToList()).ToList()
                : expanded.SelectMany(r => resolved.Select(alt => r.Append(new ActionUse(alt, count)).ToList())).ToList();
        }

        var resolvedOptions = options.SelectMany(o => Resolve(o, where)).Distinct(StringComparer.Ordinal).Select(n => new ActionUse(n, 1)).ToList();
        foreach (var routine in expanded)
        {
            var merged = routine.GroupBy(u => u.ActionName, StringComparer.Ordinal).Select(g => new ActionUse(g.Key, g.Sum(u => u.Count))).ToList();
            var candidate = new MultiattackRoutine
            {
                Label = "Multiattack",
                Steps = merged,
                Choose = resolvedOptions.Count == 0 ? 0 : choose,
                Options = resolvedOptions.Count == 0 ? [] : resolvedOptions,
            };
            if (candidate.TotalUses == 0)
            {
                continue;
            }

            if (!_routines.Any(r => Same(r, candidate)))
            {
                _routines.Add(candidate);
            }
        }
    }

    private static bool Same(MultiattackRoutine a, MultiattackRoutine b) =>
        a.Choose == b.Choose &&
        a.Steps.OrderBy(s => s.ActionName, StringComparer.Ordinal).SequenceEqual(b.Steps.OrderBy(s => s.ActionName, StringComparer.Ordinal)) &&
        a.Options.Select(o => o.ActionName).Order(StringComparer.Ordinal).SequenceEqual(b.Options.Select(o => o.ActionName).Order(StringComparer.Ordinal));

    private int Count(MultiattackCount count, string action, string where)
    {
        if (count.Value is { } value)
        {
            return value;
        }

        if (count.Text.Equals("Number of Heads", StringComparison.OrdinalIgnoreCase))
        {
            _log.Approximated(where, $"It makes one {action} per head; the simulator uses its starting 5 heads (heads lost and grown are not tracked).");
            return 5;
        }

        if (count.Text.Trim().Equals("1d4", StringComparison.OrdinalIgnoreCase))
        {
            _log.Approximated(where, $"It makes 1d4 {action} attacks (2.5 on average); the simulator uses 2 every turn, as the 2024 stat block does.");
            return 2;
        }

        _log.Unparsed(where, $"The count \"{count.Text}\" for {action} could not be read; 1 is used.");
        return 1;
    }

    /// <summary>
    /// A name another action refers to → the stat block's action or spell names it means. Exact, then ignoring case,
    /// parentheticals ("Unarmed Strike (Vampire Form Only)") and plurals ("Claws" for Claw); "Spellcasting" means
    /// every combat spell its Spellcasting action casts. Empty (with a warning) when nothing matches.
    /// </summary>
    private IReadOnlyList<string> Resolve(string reference, string where)
    {
        var bare = ProseText.StripParentheticals(reference);
        if (IsSpellcastingName(bare))
        {
            var spells = _spells.Where(s => s.Slot == StatBlockValues.ActionSlots.Action).Select(s => s.Name).Distinct().ToList();
            if (spells.Count == 0)
            {
                _log.Note($"{where}: its use of {bare} casts no spell the simulator casts, so that step is left out.");
            }

            return spells;
        }

        if (_blockSpells.TryGetValue(bare, out var castBy) && !IsSpellcastingName(bare))
        {
            if (castBy.Count == 0)
            {
                _log.Note($"{where}: its use of {bare} casts no spell the simulator casts, so that step is left out.");
            }

            return castBy;
        }

        if (_families.TryGetValue(bare, out var members))
        {
            return members;
        }

        var candidates = _actions.Concat(_bonusActions).Concat(_spells).Select(a => a.Name).Distinct().ToList();
        foreach (var key in new Func<string, string>[] { s => s.ToLowerInvariant(), Key, s => Singular(Key(s)) })
        {
            var hit = candidates.FirstOrDefault(c => key(c) == key(reference));
            if (hit is not null)
            {
                return [hit];
            }
        }

        if (_uncastSpells.TryGetValue(bare, out var why))
        {
            _log.Note($"{where}: it uses {bare}, which the simulator does not cast ({why}); that step is left out.");
            return [];
        }

        _log.Unresolved(where, $"It refers to \"{reference}\", which is not one of its actions or spells; that step is left out.");
        return [];
    }

    private static string Key(string name) => Spaces().Replace(ProseText.StripParentheticals(name).ToLowerInvariant(), " ").Trim();

    private static string Singular(string key) => key.EndsWith('s') ? key[..^1] : key;

    private LegendaryActions? ReadLegendary()
    {
        if (_m.Legendary.Count == 0)
        {
            return null;
        }

        foreach (var leg in _m.Legendary)
        {
            var (name, cost) = Is2014 ? ProseText.LegendaryCost(leg.Name) : (leg.Name, 1);
            var text = ProseText.Normalize(leg.Desc);
            var once = ProseText.IsOncePerRound(text);
            foreach (var entry in LegendaryEntries(leg, name, text))
            {
                _legendary.Add(entry with { Slot = StatBlockValues.ActionSlots.Legendary, LegendaryCost = cost, OncePerRound = once });
            }
        }

        var uses = _override?.LegendaryUses ?? 3;
        int? lair = _override?.LegendaryUsesInLair ?? (_m.XpInLair is not null ? 4 : null);
        if (_override?.LegendaryUses is not null || _override?.LegendaryUsesInLair is not null)
        {
            _log.Note($"Legendary action uses from the overrides file ({_override.Note}).");
        }
        else if (Is2014)
        {
            _log.Note("Legendary action uses: 3 (SRD 5.1 gives 3 for every legendary monster; the data does not store it).");
        }
        else
        {
            _log.Note(lair is null
                ? "Legendary action uses: 3 (the data does not store it; it has no lair)."
                : "Legendary action uses: 3, or 4 in its lair (the data does not store them; the in-lair value is inferred from its in-lair XP).");
        }

        return new LegendaryActions(uses, lair, _legendary);
    }

    private IReadOnlyList<StatBlockAction> LegendaryEntries(RecordAction leg, string name, string text)
    {
        var where = $"legendary action {name}";
        StatBlockAction Use(string label, string used) => new()
        {
            Name = label,
            Kind = StatBlockValues.ActionKinds.UseActions,
            Slot = StatBlockValues.ActionSlots.Legendary,
            Uses = [new ActionUse(used, 1)],
            Text = text,
        };

        IReadOnlyList<StatBlockAction> Alternatives(IReadOnlyList<string> used)
        {
            if (MoveFirst().IsMatch(text))
            {
                _log.Note($"{Capitalize(where)}: its movement is ignored (no grid).");
            }

            return used.Count == 1 ? [Use(name, used[0])] : used.Select(u => Use($"{name} ({u})", u)).ToList();
        }

        if (leg.Spellcasting is not null)
        {
            var spells = _legendarySpells.GetValueOrDefault(leg.Name) ?? [];
            if (spells.Count == 0)
            {
                _log.NotModelled(where, "It casts only spells the simulator does not cast.");
                return [new StatBlockAction { Name = name, Kind = StatBlockValues.ActionKinds.NotModelled, Slot = StatBlockValues.ActionSlots.Legendary, Text = text }];
            }

            return Alternatives(spells);
        }

        var names = new List<string>(ProseText.UsedNames(text));
        if (CastsCantrip().IsMatch(text))
        {
            names.AddRange(_spells.Where(s => s.SpellLevel == 0 && s.Slot == StatBlockValues.ActionSlots.Action).Select(s => s.Name));
        }
        else if (CastsAnySpell().IsMatch(text))
        {
            names.AddRange(_spells.Where(s => s.Slot == StatBlockValues.ActionSlots.Action).Select(s => s.Name));
            _log.Approximated(where, "It may cast any of its spells; the simulator treats each combat spell as a separate choice and spends no slot for it.");
        }

        if (names.Count > 0)
        {
            var resolved = names.SelectMany(n => Resolve(n, where)).Distinct(StringComparer.Ordinal).ToList();
            if (resolved.Count > 0)
            {
                if (LegendaryExtra().IsMatch(text))
                {
                    _log.Approximated(where, $"Only the action it uses is simulated: {Excerpt(text)}");
                }

                if (ProseText.VersionLevel(text) is { } version &&
                    _spells.FirstOrDefault(s => s.Name == resolved[0]) is { SpellLevel: { } level } && level != version)
                {
                    _log.Approximated(where, string.Create(CultureInfo.InvariantCulture, $"The text casts {resolved[0]} at level {version}; the simulator casts it at level {level}, as its Spellcasting lists it."));
                }

                return Alternatives(resolved);
            }

            // Every name was a spell the simulator does not cast or a reference already warned about.
            if (names.Any(n => _uncastSpells.ContainsKey(ProseText.StripParentheticals(n))))
            {
                _log.NotModelled(where, $"It uses {string.Join(" or ", names)}, which the simulator does not cast.");
            }

            return [new StatBlockAction { Name = name, Kind = StatBlockValues.ActionKinds.NotModelled, Slot = StatBlockValues.ActionSlots.Legendary, Text = text }];
        }

        return ReadAction(leg, name, StatBlockValues.ActionSlots.Legendary);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\bcasts a cantrip\b", RegexOptions.IgnoreCase)]
    private static partial Regex CastsCantrip();

    [GeneratedRegex(@"\bcasts a spell\b", RegexOptions.IgnoreCase)]
    private static partial Regex CastsAnySpell();

    [GeneratedRegex(@"^The [a-z' -]+ (?:moves|can teleport|teleports|flies)", RegexOptions.IgnoreCase)]
    private static partial Regex MoveFirst();

    [GeneratedRegex(@"regains|Temporary Hit Points|takes? \d+ \(", RegexOptions.IgnoreCase)]
    private static partial Regex LegendaryExtra();
}
