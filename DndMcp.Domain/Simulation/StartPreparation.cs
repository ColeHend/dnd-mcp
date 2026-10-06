using System.Globalization;
using DndMcp.Domain.Features;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Simulation;

/// <summary>
/// Checks and compiles the live-state seeds of a <see cref="SimulationSpec"/> (contract §11.2-3): each entry's
/// <see cref="CombatantStart"/> and the spec's <see cref="FightResume"/>. Two stages, as builds have: the shape (ranges,
/// condition names and durations, sources and order in range, one creature per seeded entry, no surprise when resuming) is
/// checked with the other entry problems before anything is compiled; what needs the compiled creature (whether it may lie
/// at 0 HP, which build modifier a label is) is resolved after, by <see cref="Compile"/>. Problems are sentences naming the
/// entry as the host's argument guard does ("party item 2 (Torch): start hp is 50 …"), collected for
/// <see cref="DslProblems"/>.
///
/// <para>
/// These inputs come from the tracker, not the model, so a refusal here is mostly a loader's bug; it is still a
/// <see cref="Core.DndInputException"/> with a sentence a person can act on, never a crash in the middle of a fight.
/// </para>
/// </summary>
internal static class StartPreparation
{
    /// <summary>The highest round a resume may name: far past any fight, small enough that counting from it cannot overflow.</summary>
    public const int MaxResumeRound = 10_000;

    /// <summary>The most turn ends a seeded round count may run (1 hour = 600 rounds, with room to spare).</summary>
    public const int MaxRoundsLeft = 10_000;

    private static readonly DslValueSet Durations = new("duration", StatBlockValues.Durations.All);

    private static readonly ConditionTemplate ExhaustionLevel = new() { Condition = Cond.Exhaustion, Duration = DurationKind.Fight };

    /// <summary>One exhaustion level for the whole fight (a seeded level is one of these, as an imposed one is).</summary>
    public static ConditionTemplate Exhaustion => ExhaustionLevel;

    /// <summary>Whether the entry is only a placeholder (dead, holding its place).</summary>
    public static bool IsPlaceholder(SimulationCombatant? combatant) => combatant?.Start?.Placeholder == true;

    /// <summary>
    /// The shape of every start and of the resume: problems for <see cref="DslProblems"/>, before anything is compiled.
    /// </summary>
    /// <param name="entries">Party entries first, then enemies: the indexes <see cref="StartCondition.SourceEntry"/> and <see cref="FightResume.Order"/> use.</param>
    /// <param name="expanded">Each entry as checked (an archetype expanded), for its count and hit point maximum.</param>
    public static void Check(SimulationSpec spec, IReadOnlyList<(int Side, int Item, SimulationCombatant Combatant)> entries,
                             IReadOnlyList<CombatantSpec> expanded, List<string> problems)
    {
        for (var e = 0; e < entries.Count; e++)
        {
            var (side, item, combatant) = entries[e];
            if (combatant?.Start is not { } start)
            {
                continue;
            }

            var where = SimulationPreparation.Where(side, item, combatant.Spec);
            var count = expanded[e].Count ?? 1;
            if (count != 1)
            {
                problems.Add($"{where}: count is {Number(count)}; a start state is one creature's, so give count 1 (one entry per creature).");
            }

            if (start.Placeholder)
            {
                continue;
            }

            if (start.Dead)
            {
                problems.Add($"{where}: start dead is only for a placeholder (a dead creature that keeps its place so what it imposed still ends on time); leave a dead creature out otherwise.");
            }

            var max = expanded[e].Hp ?? combatant.Monster?.HitPoints;
            if (start.Hp is { } hp && (hp < 0 || (max is { } m && hp > m)))
            {
                problems.Add(max is { } most
                    ? $"{where}: start hp is {Number(hp)}; it is 0 to its hit point maximum, {Number(most)}."
                    : $"{where}: start hp is {Number(hp)}; it is 0 or more.");
            }

            InRange(where, "start temp_hp", start.TempHp, 0, SimulationLimits.MaxHp, problems);
            InRange(where, "start death_successes", start.DeathSuccesses, 0, 2, problems);
            InRange(where, "start death_failures", start.DeathFailures, 0, 2, problems);
            InRange(where, "start exhaustion", start.Exhaustion, 0, 6, problems);
            InRange(where, "start legendary_actions_left", start.LegendaryActionsLeft, 0, int.MaxValue, problems);
            InRange(where, "start legendary_resistance_left", start.LegendaryResistanceLeft, 0, int.MaxValue, problems);
            if (start.Concentration is { } concentration && (concentration.Trim().Length == 0 || !DslText.IsOneLine(concentration.Trim(), DslLimits.MaxBuildNameLength)))
            {
                problems.Add($"{where}: start concentration must be a label of one line, at most {DslLimits.MaxBuildNameLength} characters (or none).");
            }

            foreach (var (name, uses) in start.UsesLeft ?? new Dictionary<string, int>())
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    problems.Add($"{where}: start uses_left has a blank name.");
                }
                else if (uses < 0)
                {
                    problems.Add($"{where}: start uses_left for \"{DslText.Echo(name)}\" is {Number(uses)}; it is 0 or more.");
                }
            }

            // A null or blank name names nothing: refused like a blank uses_left key (not a crash in the compile, not a "" in
            // the report's unmatched names).
            if ((start.Spent ?? []).Any(string.IsNullOrWhiteSpace))
            {
                problems.Add($"{where}: start spent has a blank name; give each spent action's name.");
            }

            if ((start.ActiveSetups ?? []).Any(string.IsNullOrWhiteSpace))
            {
                problems.Add($"{where}: start active_setups has a blank name; give each setup modifier's label.");
            }

            if ((start.Unavailable ?? []).Any(string.IsNullOrWhiteSpace))
            {
                problems.Add($"{where}: start unavailable has a blank name; give each spell's modifier or attack label.");
            }

            var conditions = start.Conditions ?? [];
            for (var k = 0; k < conditions.Count; k++)
            {
                CheckCondition($"{where}: start condition {Number(k + 1)}", conditions[k], entries, expanded, spec.Resume is not null, problems);
            }
        }

        if (entries.Count(e => e.Side == 0 && IsPlaceholder(e.Combatant)) == entries.Count(e => e.Side == 0) && entries.Any(e => e.Side == 0))
        {
            problems.Add("party has only placeholders; at least one party combatant must be able to fight.");
        }

        if (entries.Count(e => e.Side == 1 && IsPlaceholder(e.Combatant)) == entries.Count(e => e.Side == 1) && entries.Any(e => e.Side == 1))
        {
            problems.Add("enemies has only placeholders; at least one enemy must be able to fight.");
        }

        if (spec.Resume is { } resume)
        {
            CheckResume(spec, resume, entries.Count, problems);
        }
    }

    private static void CheckCondition(string where, StartCondition? condition, IReadOnlyList<(int Side, int Item, SimulationCombatant Combatant)> entries,
                                       IReadOnlyList<CombatantSpec> expanded, bool resumed, List<string> problems)
    {
        if (condition is null)
        {
            problems.Add($"{where} is null.");
            return;
        }

        var index = Cond.Of(condition.Condition?.Trim());
        if (index == Cond.Exhaustion)
        {
            problems.Add($"{where}: exhaustion is a level, given as start exhaustion, not as a condition.");
        }
        else if (index < 0)
        {
            problems.Add($"{where}: \"{DslText.Echo(condition.Condition)}\" is not a condition; they are {string.Join(", ", StatBlockValues.Conditions.All.Where(c => c != "exhaustion"))}.");
        }

        if (!Durations.TryMatch(condition.Duration, out var duration))
        {
            problems.Add($"{where}: duration \"{DslText.Echo(condition.Duration)}\" is not a simulator duration; give {Durations.List}.");
        }

        if (condition.SourceEntry is { } source)
        {
            if (source < 0 || source >= entries.Count)
            {
                problems.Add($"{where}: source_entry is {Number(source)}; it is an entry's 0-based index over party then enemies, 0 to {Number(entries.Count - 1)}.");
            }
            else if ((expanded[source].Count ?? 1) != 1)
            {
                problems.Add($"{where}: source_entry {Number(source)} has {Number(expanded[source].Count ?? 1)} copies; a source is one creature (an entry of count 1).");
            }
            else if (condition.HeldBySourceConcentration && (entries[source].Combatant?.Start is not { Placeholder: false } sourceStart || string.IsNullOrWhiteSpace(sourceStart.Concentration)))
            {
                problems.Add($"{where}: it is held by its source's concentration, but source_entry {Number(source)} starts concentrating on nothing.");
            }
        }
        else if (condition.HeldBySourceConcentration)
        {
            problems.Add($"{where}: it is held by its source's concentration; give source_entry.");
        }

        // 0 is legal: no counted turn end is left, and the duration ends at its source's next turn start (Compile).
        InRange(where, "rounds_left", condition.RoundsLeft, 0, MaxRoundsLeft, problems);
        if (condition.ImposedDuringResumedTurn && !resumed)
        {
            problems.Add($"{where}: imposed_during_resumed_turn marks a condition imposed during the turn a resumed fight starts at; give the resume, or leave it false.");
        }

        InRange(where, "save_dc", condition.SaveDc, DslLimits.MinDc, DslLimits.MaxDc, problems);
        InRange(where, "escape_dc", condition.EscapeDc, DslLimits.MinDc, DslLimits.MaxDc, problems);
        if (condition.SaveAbility is { } ability && !V.Abilities.Set.TryMatch(ability, out _))
        {
            problems.Add($"{where}: save_ability \"{DslText.Echo(ability)}\" is not an ability; they are {V.Abilities.Set.List}.");
        }

        if (duration is null || index == Cond.Prone)
        {
            // Prone lasts until it stands whatever the duration says (the engine's rule), so nothing else is needed.
            return;
        }

        var sourced = condition.SourceEntry is not null;
        switch (duration)
        {
            case StatBlockValues.Durations.UntilStartOfSourceTurn or StatBlockValues.Durations.UntilEndOfSourceTurn when !sourced:
                problems.Add($"{where}: {duration} ends on its source's turn; give source_entry.");
                break;
            case StatBlockValues.Durations.Rounds:
                if (!sourced || condition.RoundsLeft is null)
                {
                    problems.Add($"{where}: rounds needs rounds_left and source_entry (the rounds are counted at the end of the source's turns).");
                }

                break;
            case StatBlockValues.Durations.SaveEnds:
                if (condition.SaveAbility is null || condition.SaveDc is null)
                {
                    problems.Add($"{where}: save_ends needs save_ability and save_dc (the save repeated at the end of each of its turns).");
                }

                if (condition.RoundsLeft is not null && !sourced)
                {
                    problems.Add($"{where}: rounds_left on save_ends is counted on its source's turns; give source_entry.");
                }

                break;
            case StatBlockValues.Durations.UntilEscape:
                if (!sourced || condition.EscapeDc is null)
                {
                    problems.Add($"{where}: until_escape needs escape_dc and source_entry (it also ends when the source is incapacitated or dies).");
                }

                break;
        }
    }

    private static void CheckResume(SimulationSpec spec, FightResume resume, int entries, List<string> problems)
    {
        if (spec.Surprise is { } surprise && SimulationValues.Surprise.Set.TryMatch(surprise, out var canonical) && canonical != SimulationValues.Surprise.None)
        {
            problems.Add($"surprise is \"{canonical}\"; a resumed fight has none (surprise belongs to a fight's first round).");
        }

        var order = resume.Order ?? [];
        if (order.Count != entries || order.Any(i => i < 0 || i >= entries) || order.Distinct().Count() != order.Count)
        {
            problems.Add($"resume order must list each of the {Number(entries)} entries exactly once, by 0-based index over party then enemies; it is [{string.Join(", ", order.Select(Number))}].");
        }

        if (resume.StartAt < 0 || resume.StartAt >= Math.Max(1, entries))
        {
            problems.Add($"resume start_at is {Number(resume.StartAt)}; it is a position in the order, 0 to {Number(Math.Max(0, entries - 1))}.");
        }

        if (resume.Round is < 1 or > MaxResumeRound)
        {
            problems.Add($"resume round is {Number(resume.Round)}; it is 1 to {Number(MaxResumeRound)}.");
        }
    }

    /// <summary>
    /// The start compiled against its creature (after <see cref="Check"/> passed): HP and death state, uses and setups by
    /// name, the concentration as the engine would have started it, the conditions with their sources' creature ids.
    /// </summary>
    /// <param name="entryIds">Each entry's creature ids, party entries first.</param>
    /// <param name="resumedCreature">The creature whose turn a resume starts at (−1: no resume): the anchor that makes <see cref="StartCondition.ImposedDuringResumedTurn"/> skip a turn end or start.</param>
    /// <param name="unmatched">Receives the names that match nothing on this creature (left at their fresh value).</param>
    public static CompiledStart Compile(CombatantStart start, CombatantTemplate template, IReadOnlyList<int[]> entryIds, int resumedCreature, string where,
                                        List<string> problems, List<string> unmatched)
    {
        if (start.Placeholder)
        {
            return new CompiledStart { Hp = 0, Placeholder = true };
        }

        var hp = start.Hp ?? template.AverageHp;
        var down = hp == 0;
        if (down && !template.PcLike && !(template.RegeneratesFromZero && template.RegenerationAmount > 0))
        {
            problems.Add($"{where}: start hp is 0, but it makes no death saves and dies at 0 HP; leave it out, or keep it as a placeholder if what it imposed must still end on its turns.");
        }

        var pc = template.Pc;
        var pcUses = new List<(int, int)>();
        var limitedUses = new List<(int, int)>();
        var spent = new List<int>();
        var pools = new List<(int, int)>();
        foreach (var (name, uses) in start.UsesLeft ?? new Dictionary<string, int>())
        {
            if (!MatchesUses(template, name))
            {
                unmatched.Add(name.Trim());
                continue;
            }

            for (var i = 0; pc is not null && i < pc.Resources.Length; i++)
            {
                if (Same(pc.Resources[i].Label, name))
                {
                    pcUses.Add((i, Math.Min(uses, pc.Resources[i].Uses)));
                }
            }

            for (var i = 0; i < template.Limited.Length; i++)
            {
                if (!Same(template.LimitedNames[i], name))
                {
                    continue;
                }

                var usage = template.Limited[i].Source.Usage;
                if (usage.Kind == StatBlockValues.UsageKinds.PerDay)
                {
                    limitedUses.Add((i, Math.Min(uses, usage.Uses ?? 1)));
                }
                else if (uses == 0)
                {
                    spent.Add(i);
                }
            }

            for (var i = 0; i < template.PoolNames.Length; i++)
            {
                if (Same(template.PoolNames[i], name))
                {
                    pools.Add((i, Math.Min(uses, template.PoolSizes[i])));
                }
            }
        }

        foreach (var name in start.Spent ?? [])
        {
            if (!MatchesSpent(template, name))
            {
                unmatched.Add(name.Trim());
                continue;
            }

            for (var i = 0; i < template.Limited.Length; i++)
            {
                if (!Same(template.LimitedNames[i], name))
                {
                    continue;
                }

                if (template.Limited[i].Source.Usage.Kind == StatBlockValues.UsageKinds.PerDay)
                {
                    limitedUses.Add((i, 0));
                }
                else
                {
                    spent.Add(i);
                }
            }
        }

        var concentrationNumber = pc?.ConcentrationNumber ?? 0;
        var setups = new List<int>();
        foreach (var name in start.ActiveSetups ?? [])
        {
            var numbers = SetupNumbers(template, name);
            if (numbers.Count == 0)
            {
                unmatched.Add(name.Trim());
            }

            // The concentration modifier's setup is paid exactly when it is concentrated on (below).
            setups.AddRange(numbers.Where(n => n != concentrationNumber));
        }

        var blocked = new List<int>();
        var blockedAttacks = new List<int>();
        foreach (var name in start.Unavailable ?? [])
        {
            var (modifiers, attacks) = UnavailableNumbers(template, name);
            if (modifiers.Count + attacks.Count == 0)
            {
                unmatched.Add(name.Trim());
            }

            blocked.AddRange(modifiers);
            blockedAttacks.AddRange(attacks);
        }

        StartConcentration? concentration = null;
        if (!down && start.Concentration?.Trim() is { Length: > 0 } label)
        {
            if (IsConcentrationModifier(template, label))
            {
                // As the engine starts it: a setup (PaySetups) or a no-setup rider (StartOfFight) switches the modifier off
                // when lost; a cast save effect (CastSaveEffect) does not, it is cast again.
                var saveEffect = pc!.SaveEffects.Any(s => s.Source.Number == concentrationNumber);
                concentration = new StartConcentration(pc.ConcentrationLabel!, concentrationNumber, pc.Gated[concentrationNumber] || !saveEffect);
            }
            else
            {
                concentration = new StartConcentration(label, 0, false);
            }
        }

        var conditions = new List<StartConditionEntry>();
        foreach (var condition in start.Conditions ?? [])
        {
            Durations.TryMatch(condition.Duration, out var duration);
            var index = Cond.Of(condition.Condition.Trim());
            var kind = index == Cond.Prone ? DurationKind.UntilStands : Kind(duration!);
            var saves = kind == DurationKind.SaveEnds;
            if ((kind is DurationKind.Rounds or DurationKind.SaveEnds) && condition.RoundsLeft == 0)
            {
                // No counted turn end is left (its last one passed in the live fight): RAW ends it at the START of its
                // source's next turn (§5.13), which is this kind exactly; a save-ends keeps its save until then.
                kind = DurationKind.UntilStartOfSourceTurn;
            }

            V.Abilities.Set.TryMatch(condition.SaveAbility, out var ability);
            var compiled = new ConditionTemplate
            {
                Condition = index,
                Duration = kind,
                Rounds = kind is DurationKind.Rounds or DurationKind.SaveEnds ? condition.RoundsLeft ?? 0 : 0,
                EscapeDc = kind == DurationKind.UntilEscape ? condition.EscapeDc ?? 0 : 0,
                SaveAbility = saves ? ability : null,
                SaveDc = saves ? condition.SaveDc ?? 0 : 0,
            };
            var source = condition.SourceEntry is { } entry ? entryIds[entry][0] : -1;

            // Imposed during the turn the fight resumes at: that turn does not count for the creature that anchors the
            // duration, if it is the turn-holder (the engine's own rule for a condition imposed mid-turn, Fight.AddCondition).
            var anchor = kind is DurationKind.UntilEndOfTargetTurn or DurationKind.UntilStartOfTargetTurn ? template.Id : source;
            var skip = condition.ImposedDuringResumedTurn && anchor >= 0 && anchor == resumedCreature ? 1 : 0;
            conditions.Add(new StartConditionEntry(
                compiled,
                source,
                condition.HeldBySourceConcentration,
                kind is DurationKind.UntilEndOfSourceTurn or DurationKind.UntilEndOfTargetTurn ? skip : 0,
                kind is DurationKind.UntilStartOfSourceTurn or DurationKind.UntilStartOfTargetTurn ? skip : 0));
        }

        return new CompiledStart
        {
            Hp = hp,
            TempHp = start.TempHp,
            Down = down,
            Stable = down && start.Stable,
            DeathSuccesses = down ? start.DeathSuccesses : 0,
            DeathFailures = down ? start.DeathFailures : 0,
            Exhaustion = start.Exhaustion,
            Concentration = concentration,
            Conditions = conditions.ToArray(),
            PcUses = pcUses.ToArray(),
            LimitedUses = limitedUses.ToArray(),
            Spent = spent.Distinct().ToArray(),
            Pools = pools.ToArray(),
            LegendaryActionsLeft = start.LegendaryActionsLeft is { } left ? Math.Min(left, template.LegendaryUses) : null,
            LegendaryResistanceLeft = start.LegendaryResistanceLeft is { } resistance ? Math.Min(resistance, template.LegendaryResistance) : null,
            ActiveSetups = setups.Distinct().ToArray(),
            Unavailable = blocked.Distinct().ToArray(),
            UnavailableAttacks = blockedAttacks.Distinct().ToArray(),
            HasActed = start.HasActed,
            ReactionUsed = start.ReactionUsed,
            RelentlessUsed = start.RelentlessUsed,
        };
    }

    // ------------------------------------------------------------------------------------------------------------------
    // The name-matching rule (CombatantStart), in one place: Compile and SimulationStartNames.Match both read it, so what
    // the loader is told matches is exactly what the run matches.
    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>The forgiving comparison: the key the DSL's value sets use (case, spaces, hyphens and underscores ignored).</summary>
    private static bool Same(string a, string b) => DslValueSet.Key(a) == DslValueSet.Key(b);

    /// <summary>A <see cref="CombatantStart.UsesLeft"/> name: a build resource, a monster's limited action (or shared recharge), a slot pool.</summary>
    public static bool MatchesUses(CombatantTemplate template, string name) =>
        (template.Pc?.Resources.Any(r => Same(r.Label, name)) ?? false) || MatchesSpent(template, name) || template.PoolNames.Any(p => Same(p, name));

    /// <summary>A <see cref="CombatantStart.Spent"/> name: a monster's limited action (recharge or per-day) or shared recharge.</summary>
    public static bool MatchesSpent(CombatantTemplate template, string name) => template.LimitedNames.Any(n => Same(n, name));

    /// <summary>The build modifiers with a setup that an <see cref="CombatantStart.ActiveSetups"/> name matches (the concentration modifier's included).</summary>
    public static List<int> SetupNumbers(CombatantTemplate template, string name) =>
        template.Pc is { } pc ? pc.Setups.Where(s => Same(s.Source.Label, name)).Select(s => s.Source.Number).ToList() : [];

    /// <summary>
    /// What a <see cref="CombatantStart.Unavailable"/> name switches off: the build's modifiers of that label (every kind:
    /// a setup, a rider, a save effect, a heal) by number, and its attacks of that name by index.
    /// </summary>
    public static (List<int> Modifiers, List<int> Attacks) UnavailableNumbers(CombatantTemplate template, string name)
    {
        if (template.Pc is not { } pc)
        {
            return ([], []);
        }

        var sources = pc.Riders.Select(r => r.Source)
            .Concat(pc.Extras.Select(e => e.Source))
            .Concat(pc.SaveEffects.Select(s => s.Source))
            .Concat(pc.ConditionsOnHit.Select(c => c.Source))
            .Concat(pc.Advantage.Select(a => a.Source))
            .Concat(pc.PowerAttacks.Select(p => p.Source))
            .Concat(pc.Rerolls.Select(r => r.Source))
            .Concat(pc.Heals.Select(h => h.Source))
            .Concat(pc.Setups.Select(s => s.Source));
        var modifiers = sources.Where(s => Same(s.Label, name)).Select(s => s.Number).Distinct().ToList();
        var attacks = pc.Attacks.Where(a => Same(a.A.Name, name)).Select(a => a.Index).ToList();
        return (modifiers, attacks);
    }

    /// <summary>A build resource's uses in a fresh fight, by its label (<see cref="StartNameMatches.FreshUses"/>), or null.</summary>
    public static int? FreshUses(CombatantTemplate template, string name) =>
        template.Pc?.Resources.FirstOrDefault(r => Same(r.Label, name)) is { } resource ? resource.Uses : null;

    /// <summary>Whether a <see cref="CombatantStart.Concentration"/> label is the build's own concentration modifier.</summary>
    public static bool IsConcentrationModifier(CombatantTemplate template, string label) =>
        template.Pc is { ConcentrationNumber: > 0, ConcentrationLabel: { } own } && Same(own, label);

    /// <summary>
    /// The creature order of a resume: each entry's creatures in its place, and where in that order the resumed turn is.
    /// </summary>
    public static (int[] Order, int StartAt) Order(FightResume resume, IReadOnlyList<int[]> entryIds)
    {
        var order = new List<int>();
        var startAt = 0;
        for (var position = 0; position < resume.Order.Count; position++)
        {
            if (position == resume.StartAt)
            {
                startAt = order.Count;
            }

            order.AddRange(entryIds[resume.Order[position]]);
        }

        return (order.ToArray(), startAt);
    }

    /// <summary>The engine kind of a canonical duration (Prone is decided before): until_stands on anything but Prone has nothing to stand from, so it lasts the fight.</summary>
    private static DurationKind Kind(string duration) => duration switch
    {
        StatBlockValues.Durations.UntilStartOfSourceTurn => DurationKind.UntilStartOfSourceTurn,
        StatBlockValues.Durations.UntilEndOfSourceTurn => DurationKind.UntilEndOfSourceTurn,
        StatBlockValues.Durations.SaveEnds => DurationKind.SaveEnds,
        StatBlockValues.Durations.Rounds => DurationKind.Rounds,
        StatBlockValues.Durations.UntilEscape => DurationKind.UntilEscape,
        StatBlockValues.Durations.UntilEndOfTargetTurn => DurationKind.UntilEndOfTargetTurn,
        StatBlockValues.Durations.UntilStartOfTargetTurn => DurationKind.UntilStartOfTargetTurn,
        _ => DurationKind.Fight,
    };

    private static void InRange(string where, string field, int? value, int min, int max, List<string> problems)
    {
        if (value is { } v && (v < min || v > max))
        {
            problems.Add(max == int.MaxValue
                ? $"{where}: {field} is {Number(v)}; it is {Number(min)} or more."
                : $"{where}: {field} is {Number(v)}; it is {Number(min)} to {Number(max)}.");
        }
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
