using System.Globalization;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Simulation;

/// <summary>One entry as compiled: its label, side, where it came from, and its creatures' ids.</summary>
/// <param name="Where">The entry as messages name it ("party item 2 (Fighter)"), for a problem found after compiling (the comparison's variant).</param>
internal sealed record PreparedEntry(string Label, int Side, string Source, int[] Ids, bool DeathSaves, string Where);

/// <summary>A validated, compiled run: what every fight shares, and what the report echoes.</summary>
internal sealed record PreparedRun(
    FightSetup Setup,
    FightSetup? Variant,
    IReadOnlyList<PreparedEntry> Entries,
    int Iterations,
    string EnemyHp,
    double? Precision,
    int? Replay,
    IReadOnlyList<PolicyEcho> Policies,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<StatBlockWarnings> Warnings,
    int CompareMember,
    string? CompareMemberName,
    string? CompareFeature)
{
    public int Combatants => Setup.Templates.Length;
}

/// <summary>
/// Validates a <see cref="SimulationSpec"/> and compiles it (contract §5.1, §5.3). Field problems are collected — up to five
/// in one <see cref="DndInputException"/>, each starting with the item as the host's argument guard names it
/// ("party item 2 (Fighter): hp is required with a build …") — before anything is resolved; a build that fails its own
/// validation is reported by the resolver, with the item in its subject.
/// </summary>
internal static class SimulationPreparation
{
    public static PreparedRun Prepare(SimulationSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var problems = new List<string>();
        var party = spec.Party ?? [];
        var enemies = spec.Enemies ?? [];
        if (party.Count == 0)
        {
            problems.Add("party is empty; give at least one combatant, e.g. [{\"build\": {…}, \"hp\": 44, \"ac\": 18}].");
        }

        if (enemies.Count == 0)
        {
            problems.Add("enemies is empty; give at least one combatant, e.g. [{\"monster\": \"Ogre\", \"count\": 3}].");
        }

        CheckTopLevel(spec, problems);
        var entries = party.Select((c, i) => (Side: 0, Item: i, Combatant: c))
            .Concat(enemies.Select((c, i) => (Side: 1, Item: i, Combatant: c)))
            .ToList();
        var expanded = new List<CombatantSpec>();
        var fightEdition = Match(V.Editions.Set, spec.Edition);
        foreach (var (side, item, combatant) in entries)
        {
            expanded.Add(CheckEntry(side, item, combatant, fightEdition, problems));
        }

        var total = expanded.Sum(e => Math.Clamp(e.Count ?? 1, 1, SimulationLimits.MaxCount));
        if (total > SimulationLimits.MaxCombatants)
        {
            problems.Add($"the fight has {Number(total)} combatants (counting copies); at most {SimulationLimits.MaxCombatants} are simulated. Lower some counts.");
        }

        CheckCompare(spec, party, expanded, problems);
        DslProblems.ThrowIfAny(problems, "simulation");

        var policies = spec.Policies ?? new PolicySpec();
        var partyTargeting = Match(SimulationValues.Targeting.PartySet, policies.Party) ?? SimulationValues.Targeting.FocusFire;
        var enemyTargeting = Match(SimulationValues.Targeting.EnemiesSet, policies.Enemies) ?? SimulationValues.Targeting.Spread;
        var legendary = Match(SimulationValues.LegendaryResistance.Set, policies.LegendaryResistance) ?? SimulationValues.LegendaryResistance.Conditions;
        var healing = Match(SimulationValues.Healing.Set, policies.Healing) ?? SimulationValues.Healing.Downed;
        var surprise = Match(SimulationValues.Surprise.Set, spec.Surprise) ?? SimulationValues.Surprise.None;
        var enemyHp = Match(SimulationValues.EnemyHp.Set, spec.EnemyHp) ?? SimulationValues.EnemyHp.Average;

        // Resolve builds (each throws its own exception, with the item in its subject) and compile every creature.
        var templates = new List<CombatantTemplate>();
        var prepared = new List<PreparedEntry>();
        var labels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var resolved = new ResolvedBuild?[expanded.Count];
        for (var e = 0; e < expanded.Count; e++)
        {
            var (side, item, combatant) = entries[e];
            var entry = expanded[e];
            var where = Where(side, item, combatant.Spec);
            if (entry.Build is { } build)
            {
                resolved[e] = BuildResolver.Resolve(build, entry.Level ?? build.Level ?? 1, spec.Rulings, where + " build", BuildUse.Simulation);
            }

            // An archetype's expansion always has a name (the catalogue's, "Wizard"), so it never falls through to its spelling.
            var baseLabel = OneLine(entry.Name) ?? (combatant.Monster is { } block ? block.Name : entry.Build?.Name ?? "combatant");
            var count = entry.Count ?? 1;
            var ids = new int[count];
            var deathSaves = entry.Build is not null ? side == 0 || entry.DeathSaves == true : entry.DeathSaves == true;
            for (var copy = 0; copy < count; copy++)
            {
                var label = UniqueLabel(labels, baseLabel);
                var id = templates.Count;
                ids[copy] = id;
                templates.Add(combatant.Monster is { } monster
                    ? CombatantCompiler.FromStatBlock(monster, entry, id, side, label, enemyHp == SimulationValues.EnemyHp.Roll && side == 1)
                    : CombatantCompiler.FromBuild(resolved[e]!, entry, id, side, label, deathSaves));
            }

            var source = combatant.Monster is { } m
                ? $"monster {m.Ref}"
                : combatant.Spec.Archetype is { } archetype
                    ? $"archetype {PartyArchetypes.Canonical(archetype) ?? archetype} (level {Number(resolved[e]!.Level)}, {resolved[e]!.Edition})"
                    : $"build \"{resolved[e]!.Name}\" (level {Number(resolved[e]!.Level)}, {resolved[e]!.Edition})";
            prepared.Add(new PreparedEntry(baseLabel, side, source, ids, deathSaves || (combatant.Monster is not null && entry.DeathSaves == true), where));
        }

        var edition = Match(V.Editions.Set, spec.Edition) ?? EditionOf(entries[0].Combatant, resolved[0]);
        var setup = new FightSetup
        {
            Templates = templates.ToArray(),
            Entries = prepared.Select(p => p.Ids).ToArray(),
            Edition = edition,
            Surprise = surprise,
            RoundCap = spec.RoundCap,
            PartyTargeting = partyTargeting,
            EnemyTargeting = enemyTargeting,
            LegendaryResistance = legendary,
            Healing = healing,
            FinishDowned = policies.FinishDowned == true,
            PcsWinTies = policies.PcsWinTies == true,
        };
        SetThreat(templates);

        FightSetup? variant = null;
        string? memberName = null;
        string? featureName = null;
        var member = spec.Compare?.Member ?? 0;
        if (spec.Compare is { } compare)
        {
            var e = member - 1;
            var entry = expanded[e];
            var merged = FeatureMerge.Merge(entry.Build!, compare.Feature!);
            var variantBuild = BuildResolver.Resolve(merged, entry.Level ?? merged.Level ?? 1, spec.Rulings, prepared[e].Where + " variant", BuildUse.Simulation);
            var variantTemplates = templates.ToArray();
            foreach (var id in prepared[e].Ids)
            {
                variantTemplates[id] = CombatantCompiler.FromBuild(variantBuild, entry, id, 0, templates[id].Label, prepared[e].DeathSaves);
                variantTemplates[id].Threat = templates[id].Threat;
            }

            variant = new FightSetup
            {
                Templates = variantTemplates,
                Entries = setup.Entries,
                Edition = setup.Edition,
                Surprise = setup.Surprise,
                RoundCap = setup.RoundCap,
                PartyTargeting = setup.PartyTargeting,
                EnemyTargeting = setup.EnemyTargeting,
                LegendaryResistance = setup.LegendaryResistance,
                Healing = setup.Healing,
                FinishDowned = setup.FinishDowned,
                PcsWinTies = setup.PcsWinTies,
            };
            memberName = prepared[e].Label;
            featureName = compare.Feature!.Name;
        }

        var echo = new List<PolicyEcho>
        {
            new("party", partyTargeting, SimulationValues.Targeting.Meaning(partyTargeting)),
            new("enemies", enemyTargeting, SimulationValues.Targeting.Meaning(enemyTargeting)),
            new("legendary_resistance", legendary, SimulationValues.LegendaryResistance.Meaning(legendary)),
            new("healing", healing, SimulationValues.Healing.Meaning(healing)),
            new("finish_downed", setup.FinishDowned ? "true" : "false", setup.FinishDowned ? "enemies keep attacking party members at 0 HP" : "creatures at 0 HP are not attacked (an area with room to spare still catches them)"),
            new("pcs_win_ties", setup.PcsWinTies ? "true" : "false", setup.PcsWinTies ? "initiative ties go to the party" : "initiative ties: higher modifier, then a roll-off"),
        };

        return new PreparedRun(
            setup,
            variant,
            prepared,
            spec.Iterations,
            enemyHp,
            spec.Precision,
            spec.Replay,
            echo,
            [
                .. Assumptions(templates, setup, enemyHp, surprise),
                .. PartyArchetypes.ReportAssumptions(entries
                    .Select((entry, e) => (entry.Combatant.Spec.Archetype, Build: resolved[e]))
                    .Where(a => a.Archetype is not null)
                    .Select(a => (a.Archetype!, a.Build!.Level, a.Build.Edition))),
            ],
            Warnings(entries.Select(e => (e.Side, e.Combatant.Monster)).ToList()),
            member,
            memberName,
            featureName);
    }

    private static void CheckTopLevel(SimulationSpec spec, List<string> problems)
    {
        if (spec.Precision is null && spec.Iterations is < 1 or > SimulationLimits.MaxIterations)
        {
            problems.Add($"iterations is {Number(spec.Iterations)}; it is 1 to {SimulationLimits.MaxIterations:N0} (default {SimulationLimits.DefaultIterations:N0}).");
        }

        if (spec.RoundCap is < 1 or > SimulationLimits.MaxRoundCap)
        {
            problems.Add($"round_cap is {Number(spec.RoundCap)}; it is 1 to {SimulationLimits.MaxRoundCap} (default {SimulationLimits.DefaultRoundCap}).");
        }

        Known(V.Editions.Set, "edition", spec.Edition, problems);
        Known(SimulationValues.Surprise.Set, "surprise", spec.Surprise, problems);
        Known(SimulationValues.EnemyHp.Set, "enemy_hp", spec.EnemyHp, problems);
        if (spec.Precision is { } precision && (!double.IsFinite(precision) || precision < SimulationLimits.MinPrecision || precision > SimulationLimits.MaxPrecision))
        {
            problems.Add(FormattableString.Invariant(
                $"precision is {(double.IsFinite(precision) ? precision.ToString("0.#####", CultureInfo.InvariantCulture) : "not a finite number")}; it is the 95% half-width of P(party wins) to reach, {SimulationLimits.MinPrecision} to {SimulationLimits.MaxPrecision}, e.g. 0.01 for ±1%."));
        }

        var maxReplay = spec.Precision is null ? spec.Iterations : SimulationLimits.MaxIterations;
        if (spec.Replay is { } replay && (replay < 1 || replay > Math.Max(1, maxReplay)))
        {
            problems.Add($"replay is {Number(replay)}; give the number of one fight, 1 to {Number(Math.Max(1, maxReplay))}.");
        }

        if (spec.Policies is { } policies)
        {
            Known(SimulationValues.Targeting.PartySet, "policies party", policies.Party, problems);
            Known(SimulationValues.Targeting.EnemiesSet, "policies enemies", policies.Enemies, problems);
            Known(SimulationValues.LegendaryResistance.Set, "policies legendary_resistance", policies.LegendaryResistance, problems);
            Known(SimulationValues.Healing.Set, "policies healing", policies.Healing, problems);
        }
    }

    /// <param name="fightEdition">The fight's edition when the spec gives one: an archetype entry without its own follows it.</param>
    private static CombatantSpec CheckEntry(int side, int item, SimulationCombatant? combatant, string? fightEdition, List<string> problems)
    {
        var spec = combatant?.Spec;
        var where = Where(side, item, spec);
        if (combatant is null || spec is null)
        {
            problems.Add($"{where}: is null; give an object with monster, build or archetype.");
            return new CombatantSpec();
        }

        var sources = (spec.Monster is not null ? 1 : 0) + (spec.Build is not null ? 1 : 0) + (spec.Archetype is not null ? 1 : 0);
        if (sources != 1)
        {
            problems.Add(sources == 0
                ? $"{where}: give exactly one of monster (an SRD monster, e.g. \"Ogre\"), build (a DSL build with hp and ac) or archetype."
                : $"{where}: give only one of monster, build and archetype.");
            return spec;
        }

        if (spec.Monster is not null && combatant.Monster is null)
        {
            throw new ArgumentException($"{where}: monster \"{spec.Monster}\" reached the simulator without its stat block; the host resolves monsters first.", nameof(combatant));
        }

        var expanded = spec;
        if (spec.Archetype is { } archetype)
        {
            if (spec.Level is null)
            {
                problems.Add($"{where}: archetype needs a level, e.g. \"level\": 5.");
                return spec;
            }

            try
            {
                expanded = Overlay(PartyArchetypes.Expand(archetype, spec.Level.Value, spec.Edition ?? fightEdition), spec);
            }
            catch (DndInputException ex)
            {
                problems.Add($"{where}: {ex.Message}");
                return spec;
            }
        }

        if (spec.Build is not null)
        {
            if (spec.Hp is null)
            {
                problems.Add($"{where}: hp is required with a build, e.g. \"hp\": 44.");
            }

            if (spec.Ac is null)
            {
                problems.Add($"{where}: ac is required with a build, e.g. \"ac\": 18.");
            }

            if (spec.Edition is not null)
            {
                problems.Add($"{where}: edition goes inside the build (build.edition); the entry's edition is for archetypes and monsters.");
            }
        }
        else if (spec.SaveProficiencies is not null && spec.Archetype is null)
        {
            problems.Add($"{where}: save_proficiencies needs a build; a monster's saves come from its stat block (override one with saves).");
        }

        if (spec.Name is { } name && !DslText.IsOneLine(name, DslLimits.MaxBuildNameLength))
        {
            problems.Add($"{where}: name must be one line of at most {DslLimits.MaxBuildNameLength} characters.");
        }

        InRange(where, "level", spec.Level, DslLimits.MinLevel, DslLimits.MaxLevel, problems);
        InRange(where, "hp", spec.Hp, 1, SimulationLimits.MaxHp, problems);
        InRange(where, "ac", spec.Ac, DslLimits.MinTargetAc, DslLimits.MaxTargetAc, problems);
        InRange(where, "count", spec.Count, 1, SimulationLimits.MaxCount, problems);
        InRange(where, "initiative_bonus", spec.InitiativeBonus, SimulationLimits.MinInitiativeBonus, SimulationLimits.MaxInitiativeBonus, problems);
        Known(SimulationValues.Positions.Set, $"{where}: position", spec.Position, problems);
        Known(V.Editions.Set, $"{where}: edition", spec.Edition, problems);
        foreach (var ability in spec.SaveProficiencies ?? [])
        {
            if (!V.Abilities.Set.TryMatch(ability, out _))
            {
                problems.Add($"{where}: save_proficiencies has \"{DslText.Echo(ability)}\", which is not an ability; they are {V.Abilities.Set.List}.");
            }
        }

        if (spec.Saves is { } saves)
        {
            foreach (var ability in V.Abilities.All)
            {
                InRange(where, $"saves {ability}", saves.Get(ability), DslLimits.MinSaveBonus, DslLimits.MaxSaveBonus, problems);
            }
        }

        return expanded;
    }

    /// <summary>
    /// An archetype's expansion with the fields the caller gave laid over it. Saves merge per ability (a given
    /// <c>{"wis": 9}</c> must not drop a paladin's aura on the other five); the archetype's computed saves were made from
    /// its own proficiencies, so given save_proficiencies retire them (else the new proficiencies would change nothing).
    /// </summary>
    private static CombatantSpec Overlay(CombatantSpec archetype, CombatantSpec given) => new()
    {
        Name = given.Name ?? archetype.Name,
        Build = archetype.Build,
        Level = given.Level ?? archetype.Level,
        Hp = given.Hp ?? archetype.Hp,
        Ac = given.Ac ?? archetype.Ac,
        SaveProficiencies = given.SaveProficiencies ?? archetype.SaveProficiencies,
        Saves = MergeSaves(given.Saves, given.SaveProficiencies is null ? archetype.Saves : null),
        InitiativeBonus = given.InitiativeBonus ?? archetype.InitiativeBonus,
        Position = given.Position ?? archetype.Position,
        Count = given.Count ?? archetype.Count,
        DeathSaves = given.DeathSaves ?? archetype.DeathSaves,
    };

    private static SavesSpec? MergeSaves(SavesSpec? given, SavesSpec? archetype) =>
        given is null ? archetype
        : archetype is null ? given
        : new SavesSpec
        {
            Str = given.Str ?? archetype.Str,
            Dex = given.Dex ?? archetype.Dex,
            Con = given.Con ?? archetype.Con,
            Int = given.Int ?? archetype.Int,
            Wis = given.Wis ?? archetype.Wis,
            Cha = given.Cha ?? archetype.Cha,
        };

    private static void CheckCompare(SimulationSpec spec, IReadOnlyList<SimulationCombatant> party, List<CombatantSpec> expanded, List<string> problems)
    {
        if (spec.Compare is not { } compare)
        {
            return;
        }

        if (compare.Member is not { } member)
        {
            problems.Add("compare: member is required: the 1-based position in party of the member to change, e.g. 1.");
        }
        else if (member < 1 || member > party.Count)
        {
            problems.Add($"compare: member is {Number(member)}; the party has {Number(party.Count)} {(party.Count == 1 ? "entry" : "entries")}, so give 1 to {Number(Math.Max(1, party.Count))}.");
        }
        else if (expanded[member - 1].Build is null)
        {
            problems.Add($"compare: member {Number(member)} is a monster; the feature needs a build or an archetype to add to.");
        }

        if (compare.Feature is null)
        {
            problems.Add("compare: feature is required, e.g. {\"name\": \"Great Weapon Master\", \"modifiers\": [...]}.");
        }
    }

    private static string EditionOf(SimulationCombatant first, ResolvedBuild? build) =>
        first.Monster?.Edition ?? build?.Edition ?? (first.Spec.Edition is { } e && V.Editions.Set.TryMatch(e, out var c) ? c : V.Editions.Default);

    internal static string Where(int side, int item, CombatantSpec? spec)
    {
        var list = side == 0 ? "party" : "enemies";
        var name = spec is null ? null : OneLine(spec.Name) ?? OneLine(spec.Monster) ?? OneLine(spec.Build?.Name) ?? OneLine(spec.Archetype);
        return name is null ? $"{list} item {Number(item + 1)}" : $"{list} item {Number(item + 1)} ({DslText.Echo(name)})";
    }

    private static string? OneLine(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string UniqueLabel(Dictionary<string, int> labels, string label)
    {
        if (!labels.TryGetValue(label, out var seen))
        {
            labels[label] = 1;
            return label;
        }

        while (true)
        {
            seen++;
            var candidate = $"{label} {Number(seen)}";
            if (!labels.ContainsKey(candidate))
            {
                labels[label] = seen;
                labels[candidate] = 1;
                return candidate;
            }
        }
    }

    private static string? Match(DslValueSet set, string? text) => text is not null && set.TryMatch(text, out var canonical) ? canonical : null;

    private static void Known(DslValueSet set, string field, string? text, List<string> problems)
    {
        if (text is not null && !set.TryMatch(text, out _))
        {
            problems.Add($"{field} \"{DslText.Echo(text)}\" is not {Problems<object>.Article(set.What)} {set.What}; give {Problems<object>.Or(set)}.");
        }
    }

    private static void InRange(string where, string field, int? value, int min, int max, List<string> problems)
    {
        if (value is { } v && (v < min || v > max))
        {
            problems.Add($"{where}: {field} is {Number(v)}; it is {Number(min)} to {Number(max)}.");
        }
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Each creature's expected damage per round against the other side (for "threat"): its best option's expected damage
    /// against an average opponent — a quick estimate with the opponents' mean AC and saves, at normal odds.
    /// </summary>
    private static void SetThreat(List<CombatantTemplate> templates)
    {
        foreach (var t in templates)
        {
            var opponents = templates.Where(o => o.Side != t.Side).ToList();
            if (opponents.Count == 0)
            {
                continue;
            }

            var ac = (int)Math.Round(opponents.Average(o => o.ArmorClass));
            if (t.Pc is { } pc)
            {
                var action = pc.ActionQueue.Sum(a => AttackMean(pc.Attacks[a], ac));
                var saves = pc.ActionSaves.Select(s => pc.SaveDamage[s]?.Mean * Math.Min(pc.SaveEffects[s].Targets, opponents.Count) * 0.6 ?? 0).DefaultIfEmpty(0).Max();
                t.Threat = Math.Max(action, saves) + pc.BonusQueue.Sum(a => AttackMean(pc.Attacks[a], ac));
            }
            else
            {
                var best = 0.0;
                foreach (var routine in t.Multiattacks)
                {
                    best = Math.Max(best, routine.Steps.Sum(s => s.Count * ActionMean(s.Action, ac, opponents.Count)) +
                                          (routine.Choose * routine.Options.Select(o => ActionMean(o, ac, opponents.Count)).DefaultIfEmpty(0).Max()));
                }

                foreach (var action in t.Actions)
                {
                    best = Math.Max(best, ActionMean(action, ac, opponents.Count));
                }

                t.Threat = best;
            }
        }
    }

    private static double HitChance(int bonus, int ac) => Math.Clamp((21 + bonus - ac) / 20.0, 0.05, 0.95);

    private static double AttackMean(PcAttack attack, int ac) =>
        HitChance(attack.AttackBonus, ac) * (attack.DiceMean + attack.Flat[(int)LineKind.Action]);

    private static double ActionMean(MonsterAction action, int ac, int opponents) =>
        action.IsAttack ? HitChance(action.AttackBonus, ac) * action.MeanDamage
        : action.IsSave ? action.MeanDamage * 0.6 * Math.Min(opponents, action.AreaCount > 0 ? action.AreaCount : action.Targets)
        : action.IsAutoHit ? action.MeanDamage * action.Targets
        : 0;

    private static IReadOnlyList<string> Assumptions(List<CombatantTemplate> templates, FightSetup setup, string enemyHp, string surprise)
    {
        var list = new List<string>
        {
            "No grid, movement, cover, light, terrain or morale; creatures never flee or surrender.",
            "Engagement: each side has a front line (melee combatants) and a back line; melee attacks reach a standing enemy front-liner (anyone once none stands), flying melee creatures reach the back line, ranged attacks and spells reach anyone.",
            "No opportunity attacks except a build's reaction extra attacks (at their per-round trigger probability). A monster's only reactions are Parry and Shield, each used when its AC bonus turns a hit (not a critical hit) into a miss: a Parry covers that one attack (a Parry whose text says melee attack covers only a melee attack), Shield (+5 AC) lasts until the start of the caster's next turn.",
            "No lair actions: the fight is not in a lair (legendary action and Legendary Resistance counts are the non-lair ones).",
            "Monster spells are cast at their own level (no upcasting); a monster does not start a concentration spell while concentrating.",
            "Frightened and charmed ignore line of sight; frightened gives Disadvantage while its source lives.",
            "Monsters choose by expected damage (greedy) against the targets their side's policy picks, heal an ally at 25% HP or less, and Dodge when nothing is usable. A condition an action imposes adds its worth times the chance it lands (an attack's hit, and a failed save where there is one): the target's own damage per round for one that takes its turns (paralyzed, stunned, incapacitated), a quarter of that for one that hampers it (restrained, frightened, prone …), a tenth for exhaustion, nothing for deafened.",
            "Areas catch the DMG's typical number of creatures (\"Targets in Areas of Effect\", ±1d3 not modelled), never an ally: the standing ones first, front line before back line, then, while the count has room, those lying at 0 HP, who take it by the rules for damage at 0 HP (a dying creature fails a death save). The damage is rolled once for all of them.",
            "A creature restrained by something with an escape DC spends its Action trying to escape every turn.",
        };

        var builds = templates.Where(t => t.Pc is not null).Select(t => t.Pc!.Build).DistinctBy(b => b.Name).ToList();
        if (builds.Any(b => b.SetupCosts.Count > 0 || b.Riders.Any(r => r.Concentration) || b.ExtraAttacks.Any(e => e.Concentration) ||
                            b.SaveEffects.Any(e => e.Concentration) || b.ConditionsOnHit.Any(c => c.Concentration)))
        {
            list.Add("Builds: setup costs are paid on the first turn; a concentration modifier lost to damage is set up again when it looks worth its cost, and one with no setup cost stays lost. Concentration is tracked for riders, extra attacks, save effects and conditions on hit (to_hit, bonus_damage and advantage modifiers marked concentration stay on).");
        }

        if (builds.Any(b => b.Riders.Any(r => r.IsOptional && r.Policy == V.Policies.Optimal) || b.DamageRerolls.Any(r => r.Policy == V.Policies.Optimal) ||
                            b.ConditionsOnHit.Any(c => c.Policy == V.Policies.Optimal)))
        {
            list.Add("Builds: the optimal policy is approximated turn by turn (spend now if that beats holding for the turn's remaining attacks), where the closed form optimises exactly.");
        }

        if (builds.Any(b => b.PowerAttacks.Any(p => p.Policy == V.PowerAttackPolicies.Auto)))
        {
            list.Add("Builds: power attack auto is switched on per turn when it raises the turn's expected damage against the current target.");
        }

        foreach (var build in builds)
        {
            // Nick only moves the Light weapon's extra attack from the Bonus Action into the Attack action: noted while the
            // build still makes that attack with the Bonus Action, by balance_dpr's rule and in its words
            // (ResolvedBuild.UnmodelledNickNote; the 2024 rogue archetype's scimitar has modelled it).
            if (build.UnmodelledNickNote() is { } nick)
            {
                list.Add($"{build.Name}: {nick}");
            }

            foreach (var attack in build.Attacks.Where(a => a.Mastery is V.Masteries.Push or V.Masteries.Slow))
            {
                list.Add($"{build.Name}: {attack.Name}'s {attack.Mastery} does nothing without a grid.");
            }
        }

        if (builds.Any(b => b.Attacks.Any(a => a.Mastery == V.Masteries.Sap)))
        {
            list.Add("Sap: a sapped creature has Disadvantage on its next attack roll before the build's next turn.");
        }

        list.Add(setup.Edition == V.Editions.E2024
            ? "The fight follows the 2024 rules: surprise is Disadvantage on initiative, exhaustion is −2 per level on d20 tests, the concentration DC is capped at 30, Grappled gives Disadvantage on attacks against others."
            : "The fight follows the 2014 rules: surprised creatures lose their first turn and have no reactions until it ends, exhaustion level 3 gives Disadvantage on attacks and saves, the concentration DC has no cap.");
        if (surprise != SimulationValues.Surprise.None)
        {
            list.Add($"Surprised: {(surprise == SimulationValues.Surprise.Party ? "the party" : "the enemies")}" +
                     (setup.Edition == V.Editions.E2024 ? "." : " (a surprised creature also takes no legendary actions until its first turn ends)."));
        }

        list.Add(enemyHp == SimulationValues.EnemyHp.Roll
            ? "Enemy hit points are rolled from their hit dice each fight."
            : "Enemy hit points are the stat blocks' averages.");
        // The engine aims a save or auto-hit kill that is not an area at a standing creature it kills (Fight.KillableAmong),
        // which the monsters line's "the targets their side's policy picks" does not say.
        if (templates.Any(t => MonsterActions(t).Any(a => a.KillAtOrBelowHp is not null && a.AreaCount == 0 && (a.IsSave || a.IsAutoHit))))
        {
            list.Add("An outright kill by hit points (Power Word Kill, the 2024 solar's Slaying Bow) is aimed at a standing creature it kills, the side's policy choosing among those, and counts as the hit points it takes when the monster chooses what to do; with no such creature, the policy picks as usual.");
        }

        if (templates.Any(t => t.RegenerationAmount > 0 && t.RegeneratesFromZero))
        {
            list.Add("A regenerating creature at 0 HP (the troll) gets up at the start of its turn unless acid or fire stopped its regeneration, but the fight ends when a side has nobody above 0 HP, so a troll down with its allies counts as beaten (finish it with fire or acid afterwards).");
        }

        if (templates.Any(t => t.Pc?.Heals.Any(h => h.SelfOnly) == true))
        {
            list.Add("Self-only heals (Second Wind) are used at half HP or less.");
        }

        return list;
    }

    /// <summary>
    /// Everything a monster template can do: its actions, bonus actions and legendary actions, and the actions its
    /// Multiattacks name (the 2024 solar's "It can replace one attack with a use of Slaying Bow"), which are resolved by
    /// name and so are scanned too, in case one is reached only that way. Empty for a party build.
    /// </summary>
    private static IEnumerable<MonsterAction> MonsterActions(CombatantTemplate template) =>
        template.Actions
            .Concat(template.BonusActions)
            .Concat(template.LegendaryActions)
            .Concat(template.Multiattacks.SelectMany(p => p.Steps.Select(s => s.Action).Concat(p.Options)));

    private static IReadOnlyList<StatBlockWarnings> Warnings(IReadOnlyList<(int Side, StatBlock? Monster)> entries)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<StatBlockWarnings>();
        foreach (var (side, monster) in entries.OrderByDescending(e => e.Side))
        {
            if (monster is null || !seen.Add(monster.Ref) || monster.Warnings.Count == 0)
            {
                continue;
            }

            list.Add(new StatBlockWarnings(monster.Name, monster.Ref, side == 0 ? "party" : "enemies", monster.Warnings.Distinct().ToList()));
        }

        return list;
    }
}
