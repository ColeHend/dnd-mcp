using System.Globalization;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Characters;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation;
using DndMcp.Domain.Simulation.Archetypes;
using C = DndMcp.Domain.Simulation.StatBlockValues.Conditions;
using D = DndMcp.Domain.Combat.CombatValues.Durations;
using E = DndMcp.Domain.Combat.CombatValues.ExpiryPoints;
using R = DndMcp.Domain.Combat.CombatValues.ResourceKeys;
using S = DndMcp.Domain.Campaign.CampaignValues.CombatSides;
using SD = DndMcp.Domain.Simulation.StatBlockValues.Durations;

namespace DndMcp.Domain.Combat;

/// <summary>A current party member for an encounter with no party-side combatants (D8's roster): its entity, name and sheet.</summary>
public sealed record PartyMemberSheet(string EntityId, string Name, CharacterSheet? Sheet);

/// <summary>
/// What <see cref="TrackerSimulation.Build"/> reads besides the encounter: the CURRENT sheets of its sheet-seeded
/// combatants' characters (by entity id), the campaign's current party (D8) for a planned encounter with no party-side
/// combatants, whether to resume from the live state, and the rulings the run will use.
/// </summary>
public sealed record TrackerSimulationInputs
{
    public bool FromState { get; init; }

    public IReadOnlyDictionary<string, CharacterSheet> Sheets { get; init; } = new Dictionary<string, CharacterSheet>();

    public IReadOnlyList<PartyMemberSheet> CampaignParty { get; init; } = [];

    public RulingsSpec? Rulings { get; init; }

    /// <summary>The campaign's slug, for the calls the notes print (named last); null: they name no campaign.</summary>
    public string? CampaignSlug { get; init; }
}

/// <summary>
/// The encounter as a simulation (contract §6.11): the party and enemies entries, the resume point under
/// <c>from_state</c>, the lair flag, the notes only the encounter form says (exclusions with their fix, neutral and left
/// combatants omitted, where the party came from, what is not simulated), and the assumption lines
/// <see cref="SheetSimulation.Entry"/> gave (which the equivalent explicit <c>character</c> call gets too).
/// </summary>
public sealed record TrackerSimulationResult(
    IReadOnlyList<SimulationCombatant> Party,
    IReadOnlyList<SimulationCombatant> Enemies,
    FightResume? Resume,
    bool Lair,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Assumptions);

/// <summary>
/// Turns an encounter into the simulator's input (contract §6.11, S's types): the party from sheets (<see cref="SheetSimulation.Entry"/>,
/// D7) and srd allies from their snapshots, the enemies from their snapshots (identical ones grouped, except under
/// <c>from_state</c>), and, with <c>from_state</c>, one entry per creature with its live state (<see cref="CombatantStart"/>)
/// and the tracker's order and turn (<see cref="FightResume"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The duration mapping</b> (the tracker's RAW timings onto the engine's vocabulary; D12 accepts the half-turn
/// difference): <c>rounds</c> → engine <c>rounds</c> with <c>rounds_left = expires.round − round − (1 if the anchor has
/// acted this round)</c> on the anchor (0 is legal); <c>until_removed</c>/<c>fight</c> → <c>fight</c>; <c>until_stands</c>
/// → <c>until_stands</c>; <c>zero_hp</c> → nothing (the engine's own down state from hp 0); <c>end_of_round R</c> →
/// <c>rounds</c> anchored on the last creature in the order; <c>concentration</c> → held by the source's seeded
/// concentration; the turn-anchored kinds, <c>until_escape</c> and <c>save_ends</c> → the engine kind of the same name;
/// a timed or fight duration with a save → <c>save_ends</c> (capped for <c>rounds</c>). A condition imposed during the
/// turn-holder's current turn carries <see cref="StartCondition.ImposedDuringResumedTurn"/>.
/// </para>
/// <para>
/// <b>Names.</b> A named effect reaches the simulation only as an active setup of the entry's build (Bladesong, Rage),
/// resources as uses left, recharges as spent: each only when <see cref="SimulationStartNames.Match"/> says the entry
/// knows the name; the rest is a "not simulated" note, so the report's own unmatched-names line never repeats it.
/// </para>
/// <para>
/// <b>Spell slots</b> (review C03/U01). A sheet's <c>slot:N</c> and <c>pact</c> counts are the uses left of the build's
/// slot-funded modifiers (<see cref="SlotFundedUse"/>: an archetype's Fireball is every slot of 3rd level or higher, its
/// Healing Word the 1st-level slots; a sim_profile modifier named as one of those spells likewise), Pact Magic slots of
/// a funding level included: a wizard who spent every 3rd-level slot in the fight casts no Fireball in the resumed one.
/// A slot no modifier spends, and a 2014 monster's pool its simulation does not cast from, is said in a
/// "spell slots are not simulated" note by level and what is left — never dropped silently, never by its key.
/// </para>
/// <para>
/// <b>Spells cast once a fight</b> (review CR03): Spirit Guardians, Spiritual Weapon, Call Lightning, Hex and the 2014
/// Hunter's Mark (<see cref="SlotCastSpell"/>; a sim_profile modifier or attack of such a name likewise) have no uses to
/// count down, so a caster with every slot of their level spent cast them anyway. Each takes one slot of its level or
/// higher, Pact Magic included; with none left it is <see cref="CombatantStart.Unavailable"/> and a note names it — unless
/// the fight is already concentrating on it (or carries its named effect), when it keeps running and only cannot be cast
/// again once it ends.
/// </para>
/// <para>
/// <b>Shared slots</b> (review CR04): the build's slot spells draw on the slots left in the build's order, its main spell
/// first (an archetype's once-a-fight spells, then its slot-funded uses; a sim_profile's modifiers, then its attacks), each
/// funded use taking at most its fresh uses, lowest level first: one 3rd-level slot left funds a sim_profile's Fireball
/// and leaves its Lightning Bolt none, where each used to count every slot. A split that leaves a use short is noted. No
/// shared pool is added to the engine.
/// </para>
/// </remarks>
public static class TrackerSimulation
{
    /// <summary>
    /// The simulation of <paramref name="state"/>. A side may come back EMPTY (or, under <c>from_state</c>, only
    /// placeholders), with the notes saying why: the caller appends the call's explicit <c>party</c>/<c>enemies</c> first
    /// (contract §6.11: "Explicit party/enemies are APPENDED … the run is refused only when a side ends up empty"), then
    /// refuses with <see cref="RequireBothSides"/>. Refusing here would make "what if they meet X" (a prepared fight with
    /// only its party, plus <c>enemies</c> in the call) impossible.
    /// </summary>
    /// <exception cref="DndInputException">
    /// <c>from_state</c> on a fight that is not running, or an enemy with unknown hit points under <c>from_state</c>.
    /// </exception>
    public static TrackerSimulationResult Build(EncounterState state, TrackerSimulationInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(inputs);
        return inputs.FromState ? FromState(state, inputs) : Fresh(state, inputs);
    }

    /// <summary>
    /// The refusal of a run whose side ended up with nobody to fight (contract §6.11), called by the host AFTER it appended
    /// the call's explicit entries (never under <c>from_state</c>, which takes none): a side that is empty, or holds only
    /// placeholders (dead creatures kept for their place), is refused with the encounter form's notes (who was left out and
    /// why) and the fix.
    /// </summary>
    /// <param name="fromState">The run resumes the live state: the fix cannot be "give it in the call".</param>
    /// <exception cref="DndInputException">The party or the enemies have nobody who can fight.</exception>
    public static void RequireBothSides(
        IReadOnlyList<SimulationCombatant> party, IReadOnlyList<SimulationCombatant> enemies, IReadOnlyList<string> notes, bool fromState)
    {
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(enemies);
        ArgumentNullException.ThrowIfNull(notes);
        static bool Nobody(IReadOnlyList<SimulationCombatant> side) => side.All(c => c.Start?.Placeholder == true);
        var side = Nobody(party) ? "party" : Nobody(enemies) ? "enemies" : null;
        if (side is null)
        {
            return;
        }

        var why = notes.Count == 0 ? string.Empty : " (" + string.Join(" ", notes) + ")";
        var fix = side == "party"
            ? "add party members with sheets (combat add with character)" + (fromState ? "." : ", or give party in the call.")
            : "add enemies from stat blocks (combat add with srd)" + (fromState ? "." : ", or give enemies in the call.");
        throw new DndInputException($"The encounter has no {side} to simulate{why}: {fix}");
    }

    // -------------------------------------------------------------------------------------------------------------------
    // The encounter as a fresh fight

    private static TrackerSimulationResult Fresh(EncounterState state, TrackerSimulationInputs inputs)
    {
        var notes = new List<string>();
        var assumptions = new List<string>();
        var party = new List<SimulationCombatant>();
        var members = state.Combatants.Where(c => c.Side is S.Party or S.Ally && !c.Removed).OrderBy(c => c.OrderKey).ToList();
        if (members.Count > 0)
        {
            foreach (var c in members)
            {
                if (PartyEntry(state, c, inputs, notes, assumptions) is { } entry)
                {
                    party.Add(entry);
                }
            }
        }
        else if (inputs.CampaignParty.Count > 0)
        {
            notes.Add($"The party is the campaign's current party ({string.Join(", ", inputs.CampaignParty.Select(m => m.Name))}): the encounter has no party combatants yet.");
            foreach (var member in inputs.CampaignParty)
            {
                if (member.Sheet is null)
                {
                    notes.Add($"{member.Name} is left out: no sheet (give it one with campaign_character update).");
                    continue;
                }

                if (SheetEntry(member.Sheet, member.Name, state.Ruleset, notes, assumptions) is { } entry)
                {
                    party.Add(new SimulationCombatant(entry));
                }
            }
        }

        var groups = new List<(CombatantSpec Spec, StatBlock Block, int Count)>();
        foreach (var c in state.Combatants.Where(c => c.Side == S.Enemy && !c.Removed).OrderBy(c => c.OrderKey))
        {
            if (c.StatBlock is not { } block)
            {
                notes.Add($"{c.Name} is left out: a custom combatant has no stat block (add it with srd to simulate it).");
                continue;
            }

            var spec = MonsterSpec(c, block);
            var index = groups.FindIndex(g => g.Spec.Monster == spec.Monster && g.Spec.Hp == spec.Hp && g.Spec.Ac == spec.Ac && g.Count < SimulationLimits.MaxCount);
            if (index >= 0)
            {
                groups[index] = (groups[index].Spec, groups[index].Block, groups[index].Count + 1);
            }
            else
            {
                groups.Add((spec, block, 1));
            }
        }

        var enemies = groups.Select(g => new SimulationCombatant(g.Count > 1 ? g.Spec with { Count = g.Count } : g.Spec, g.Block)).ToList();
        Omitted(state, notes, fromState: false);
        return new TrackerSimulationResult(party, enemies, null, state.Lair, notes, assumptions);
    }

    /// <summary>
    /// A party-side combatant's entry: a sheet-seeded one from its CURRENT sheet (D7; refused sheets are left out with
    /// the fix), an srd ally from its snapshot, anything else left out with a note.
    /// </summary>
    private static SimulationCombatant? PartyEntry(EncounterState state, CombatantState c, TrackerSimulationInputs inputs, List<string> notes, List<string> assumptions)
    {
        if (c.IsSheetSeeded)
        {
            if (c.EntityId is null || !inputs.Sheets.TryGetValue(c.EntityId, out var sheet))
            {
                notes.Add($"{c.Name} is left out: its sheet no longer exists.");
                return null;
            }

            return SheetEntry(sheet, c.Name, state.Ruleset, notes, assumptions) is { } spec ? new SimulationCombatant(spec) : null;
        }

        if (c.StatBlock is { } block)
        {
            return new SimulationCombatant(MonsterSpec(c, block), block);
        }

        notes.Add(c.EntityHandle is { } handle
            ? $"{c.Name} is left out: it has no sheet in the fight and no stat block ({Reseed(handle, inputs)}; or add it with srd)."
            : $"{c.Name} is left out: a custom combatant has no stat block (add it with srd to simulate it).");
        return null;
    }

    /// <summary>
    /// The fix for a member in the fight without its sheet (review C04): <c>add {character}</c> of an entity already in the
    /// fight unseeded re-seeds that combatant from the sheet it now has, so the note prints that call, the campaign last.
    /// </summary>
    private static string Reseed(string handle, TrackerSimulationInputs inputs) =>
        $"give {handle} a sheet with campaign_character update, then re-seed it: " +
        CombatCalls.Combat("add", ("combatants", new[] { CombatCalls.Object(("character", handle)) }), ("campaign", inputs.CampaignSlug));

    private static CombatantSpec? SheetEntry(CharacterSheet sheet, string name, string edition, List<string> notes, List<string> assumptions)
    {
        try
        {
            var entry = SheetSimulation.Entry(sheet, name, edition);
            assumptions.AddRange(entry.Assumptions);
            return entry.Entry;
        }
        catch (DndInputException ex)
        {
            notes.Add($"{name} is left out: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// A monster entry from its snapshot: the ref, with HP only when the combatant's maximum differs from the snapshot's
    /// average and AC only when it differs from the snapshot's (so the equivalent explicit call is plain <c>monster</c>).
    /// </summary>
    private static CombatantSpec MonsterSpec(CombatantState c, StatBlock block) => new()
    {
        Monster = c.SrdRef ?? block.Ref,
        Hp = c.MaxHp is { } max && max != block.HitPoints ? Math.Min(max, SimulationLimits.MaxHp) : null,
        Ac = c.Ac is { } ac && ac != block.ArmorClass ? ac : null,
    };

    private static void Omitted(EncounterState state, List<string> notes, bool fromState)
    {
        var neutral = state.Combatants.Where(c => c.Side == S.Neutral && !c.Removed).Select(c => c.Name).ToList();
        if (neutral.Count > 0)
        {
            notes.Add($"Neutral combatants are left out: {string.Join(", ", neutral)}.");
        }

        var left = state.Combatants.Where(c => c.Removed).Select(c => c.Name).ToList();
        if (left.Count > 0)
        {
            notes.Add($"Combatants who left are left out: {string.Join(", ", left)}.");
        }

        if (!fromState && state.Lair)
        {
            notes.Add("Fought in a lair: in-lair legendary counts are simulated; lair actions are not.");
        }
    }

    // -------------------------------------------------------------------------------------------------------------------
    // from_state

    private static TrackerSimulationResult FromState(EncounterState state, TrackerSimulationInputs inputs)
    {
        if (state.Status != CampaignValues.EncounterStatuses.Active || state.Round < 1)
        {
            throw new DndInputException("from_state resumes a running fight (round 1 or later): roll initiative first, or simulate the encounter without from_state.");
        }

        var notes = new List<string>();
        var assumptions = new List<string>();
        var order = state.Order;
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var holder in state.Combatants.Where(c => !c.Removed))
        {
            foreach (var condition in holder.Conditions)
            {
                foreach (var id in new[] { condition.Source, condition.HeldBy, condition.Expires?.Of })
                {
                    if (id is not null && id != holder.Id)
                    {
                        sources.Add(id);
                    }
                }
            }
        }

        var unknown = state.Combatants.Where(c => c.Side == S.Enemy && !c.Removed && !c.Dead && !c.Defeated && !c.HpKnown).ToList();
        if (unknown.Count > 0)
        {
            throw new DndInputException(
                $"from_state needs the enemies' hit points; {string.Join(", ", unknown.Select(c => c.Name))} {(unknown.Count == 1 ? "has" : "have")} none: " +
                $"give hp with combat set ({CombatCalls.Combat("set", ("combatants", new[] { CombatCalls.Object(("name", unknown[0].Name), ("hp", CombatCalls.Fill)) }))}).");
        }

        // Who is in: the living, and the dead or defeated that are the source of something still running (placeholders).
        var chosen = new List<(CombatantState Combatant, bool Placeholder)>();
        foreach (var c in state.Combatants.OrderBy(c => c.OrderKey))
        {
            if (c.Side == S.Neutral || c.Removed)
            {
                continue;
            }

            if (c.Initiative is null)
            {
                notes.Add($"{c.Name} is left out: it has no initiative yet.");
                continue;
            }

            // Down: dead, defeated (enemy and neutral, D16), or — an ally or party monster, which is never "defeated" — at
            // 0 HP or knocked out without making death saves (a knock-out, a creature held at 0 by Undead Fortitude or
            // Relentless). The engine refuses such a creature at 0 HP (it dies at 0 unless it makes death saves), and an
            // engine Unconscious would never wake it, so it is treated like a defeated one: a placeholder when it is the
            // source of something still running, else left out.
            var heldOrOut = !c.Dead && !c.Defeated && !c.MakesDeathSaves && c.HpKnown && (c.Hp == 0 || c.KnockedOut);
            var down = c.Dead || c.Defeated || heldOrOut;
            if (down && !sources.Contains(c.Id))
            {
                if (heldOrOut)
                {
                    notes.Add($"{c.Name} is left out: it is down ({(c.KnockedOut || c.Has(C.Unconscious) ? "knocked out" : "held at 0 HP by a trait")}) and makes no death saves.");
                }

                continue;
            }

            chosen.Add((c, down));
        }

        var partySide = chosen.Where(x => x.Combatant.Side is S.Party or S.Ally).ToList();
        var enemySide = chosen.Where(x => x.Combatant.Side == S.Enemy).ToList();
        var specs = new Dictionary<string, (CombatantSpec Spec, StatBlock? Block)>(StringComparer.Ordinal);
        foreach (var (c, placeholder) in partySide.Concat(enemySide))
        {
            if (placeholder)
            {
                specs[c.Id] = (new CombatantSpec { Name = c.Name }, null);
                continue;
            }

            CombatantSpec? spec = null;
            StatBlock? block = null;
            if (c.IsSheetSeeded && c.EntityId is not null && inputs.Sheets.TryGetValue(c.EntityId, out var sheet))
            {
                spec = SheetEntry(sheet, c.Name, state.Ruleset, notes, assumptions);

                // A sheet with no maximum whose combatant was given hit points in the fight (D17): the live maximum, so
                // the live hit points fit it.
                if (spec is { Hp: null } && c.EffectiveMaxHp(state.Ruleset) is { } live and > 0)
                {
                    spec = spec with { Hp = live };
                }
            }
            else if (c.StatBlock is { } monster)
            {
                block = monster;
                spec = MonsterSpec(c, monster) with { Name = c.Name, DeathSaves = c.MakesDeathSaves ? true : null };
            }
            else
            {
                notes.Add(c.IsSheetSeeded
                    ? $"{c.Name} is left out: its sheet no longer exists."
                    : c.EntityHandle is { } handle
                        ? $"{c.Name} is left out: it has no sheet in the fight and no stat block ({Reseed(handle, inputs)})."
                        : $"{c.Name} is left out: it has no sheet in the fight and no stat block.");
            }

            if (spec is not null)
            {
                specs[c.Id] = (spec, block);
            }
        }

        var entries = partySide.Concat(enemySide).Where(x => specs.ContainsKey(x.Combatant.Id)).Select(x => x.Combatant).ToList();
        var index = entries.Select((c, i) => (c.Id, i)).ToDictionary(p => p.Id, p => p.i, StringComparer.Ordinal);
        var placeholders = chosen.Where(x => x.Placeholder).Select(x => x.Combatant.Id).ToHashSet(StringComparer.Ordinal);
        var lastInOrder = order.LastOrDefault(c => index.ContainsKey(c.Id));

        // Where the resume starts: the turn-holder's turn when it is simulated, else the next simulated creature's.
        var resumeOrder = order.Where(c => index.ContainsKey(c.Id)).Select(c => index[c.Id]).ToList();
        var (startAt, round, atTurnHolder) = StartAt(state, order, index, resumeOrder);

        var built = new List<SimulationCombatant>();
        foreach (var c in entries)
        {
            var (spec, block) = specs[c.Id];
            if (placeholders.Contains(c.Id))
            {
                built.Add(new SimulationCombatant(spec, null, new CombatantStart { Placeholder = true }));
                continue;
            }

            built.Add(new SimulationCombatant(spec, block, Start(state, c, spec, block, index, lastInOrder, atTurnHolder, inputs, notes)));
        }

        var party = built.Take(partySide.Count(x => specs.ContainsKey(x.Combatant.Id))).ToList();
        var enemies = built.Skip(party.Count).ToList();
        Omitted(state, notes, fromState: true);
        return new TrackerSimulationResult(party, enemies, new FightResume(resumeOrder, startAt, round), state.Lair, notes, assumptions);
    }

    /// <summary>
    /// The turn-holder's position in the resume order, or the next simulated creature's after it (wrapping a round), and
    /// whether the resume starts at the turn-holder itself. Only then is there a replayed turn for
    /// <see cref="StartCondition.ImposedDuringResumedTurn"/> to protect: when the turn-holder is not simulated (a neutral, a
    /// custom combatant, a member with no sim route), the resume starts at a creature whose turn has not begun, and the
    /// flag would make the simulator skip that creature's coming turn end or start, keeping the condition a round too long.
    /// </summary>
    private static (int StartAt, int Round, bool AtTurnHolder) StartAt(EncounterState state, IReadOnlyList<CombatantState> order, Dictionary<string, int> index, List<int> resumeOrder)
    {
        var turn = -1;
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i].Id == state.TurnCombatantId)
            {
                turn = i;
            }
        }

        var round = state.Round;
        for (var steps = 0; steps < order.Count && turn >= 0; steps++)
        {
            var id = order[turn].Id;
            if (index.TryGetValue(id, out var entry) && resumeOrder.IndexOf(entry) is var position and >= 0)
            {
                return (position, round, steps == 0);
            }

            turn++;
            if (turn >= order.Count)
            {
                turn = 0;
                round++;
            }
        }

        return (0, round, false);
    }

    /// <summary>One creature's live state as a <see cref="CombatantStart"/>.</summary>
    /// <param name="atTurnHolder">The resume starts at the turn-holder's own turn (<see cref="StartAt"/>): only then can a condition be imposed during the resumed turn.</param>
    private static CombatantStart Start(
        EncounterState state, CombatantState c, CombatantSpec spec, StatBlock? block, Dictionary<string, int> index, CombatantState? lastInOrder,
        bool atTurnHolder, TrackerSimulationInputs inputs, List<string> notes)
    {
        var conditions = new List<StartCondition>();
        var notSimulated = new List<string>();
        var effects = new List<string>();

        // A 2024 knock-out of a creature that makes death saves (1 HP, Unconscious until healed): the engine has no
        // knock-out, and an Unconscious it seeded would never end. Its nearest state is stable at 0 HP — unconscious, no
        // death saves until hit, awake as soon as it is healed — so it starts there (one hit point lower, said in a note).
        var knockedOut = c.KnockedOut && c.MakesDeathSaves && c.HpKnown;
        if (knockedOut)
        {
            notes.Add($"{c.Name} is knocked out (unconscious at {(c.Hp ?? 0).ToString(CultureInfo.InvariantCulture)} HP): simulated as stable at 0 HP, so healing wakes it.");
        }

        foreach (var condition in c.Conditions)
        {
            if (!condition.IsSrdCondition)
            {
                effects.Add(condition.Name);
                continue;
            }

            // The drop's Unconscious (and a knock-out's) is the engine's own down state at 0 HP, never a seeded condition.
            if (condition.Name == C.Exhaustion || (condition.Name == C.Unconscious && condition.Duration == D.ZeroHp && (!condition.KnockOut || knockedOut)))
            {
                continue;
            }

            if (Map(state, condition, index, lastInOrder, atTurnHolder) is { } mapped)
            {
                conditions.Add(mapped);
            }
            else
            {
                notSimulated.Add(condition.Name);
            }
        }

        // Resources, recharges and effects reach the simulation only where its build knows the name.
        var uses = new Dictionary<string, int>(StringComparer.Ordinal);
        var spent = new List<string>();
        var pools = new Dictionary<string, (string Key, CombatResource Resource)>(StringComparer.Ordinal);
        foreach (var (key, resource) in c.Resources)
        {
            // The sheet's slots fund the build's slot-funded modifiers (below); items are never simulated.
            if (key.StartsWith(R.ItemPrefix, StringComparison.Ordinal) || key.StartsWith(R.SlotPrefix, StringComparison.Ordinal) || key == R.Pact)
            {
                continue;
            }

            if (resource.Kind == CombatValues.ResourceKinds.Recharge)
            {
                if (resource.Ready == false)
                {
                    spent.Add(resource.Name ?? key[R.LimitedPrefix.Length..]);
                }

                continue;
            }

            if (key.StartsWith(R.PoolPrefix, StringComparison.Ordinal))
            {
                uses[key[R.PoolPrefix.Length..]] = resource.Left ?? 0;
                pools[key[R.PoolPrefix.Length..]] = (key, resource);
                continue;
            }

            if (resource.Max is not null && resource.Name is { } name && !(key.StartsWith(R.LimitedPrefix, StringComparison.Ordinal) && block?.Trait(StatBlockValues.TraitKinds.Relentless)?.Name == name))
            {
                uses[name] = resource.Left ?? 0;
            }
        }

        // A sheet's slots (and Pact Magic) fund the build's slot spells (C03/U01, CR03/CR04): its slot-funded uses start at
        // the slots the fight has left, never the class table's, and a spell cast once a fight needs one of them.
        var slotSpells = block is null ? SlotSpells(spec, state.Ruleset) : [];
        var candidates = uses.Keys.Concat(spent).Concat(effects).Concat(slotSpells.Select(f => f.Name)).Distinct(StringComparer.Ordinal).ToList();
        var matches = candidates.Count == 0
            ? new StartNameMatches([], [], [], [], [])
            : SimulationStartNames.Match(new SimulationCombatant(spec, block), candidates, state.Ruleset, inputs.Rulings);
        var matchedUses = uses.Where(u => matches.UsesLeft.Contains(u.Key)).ToDictionary(u => u.Key, u => u.Value, StringComparer.Ordinal);
        var slots = c.Resources.Where(p => (p.Key == R.Pact || p.Key.StartsWith(R.SlotPrefix, StringComparison.Ordinal)) && p.Value.Max > 0).ToList();
        var shared = ShareSlots(c, slotSpells.Where(u => u.Cast ? matches.Unavailable.Contains(u.Name) : matches.UsesLeft.Contains(u.Name)).ToList(), slots, matches, effects);
        foreach (var (name, count) in shared.Uses)
        {
            matchedUses[name] = count;
        }

        var fundedKeys = shared.Keys;
        notes.AddRange(shared.Notes.Select(n => $"{c.Name}: {n}"));

        var matchedSpent = spent.Where(matches.Spent.Contains).ToList();
        var setups = effects.Where(matches.ActiveSetups.Contains).ToList();
        // A named effect that is one of the build's once-a-fight spells (a 2014 Spiritual Weapon kept as an effect) is
        // simulated: it keeps that spell running (ShareSlots).
        notSimulated.AddRange(effects.Where(e => !matches.ActiveSetups.Contains(e) && !matches.Concentration.Contains(e) &&
            !slotSpells.Any(u => u.Cast && DslValueSet.Key(u.Name) == DslValueSet.Key(e) && matches.Unavailable.Contains(u.Name))));
        // A resource named like an active setup (the sheet's Bladesong uses beside the Bladesong effect) is simulated as the setup.
        // A 2014 monster's slot pool its simulation does not cast from goes in the slots note instead.
        notSimulated.AddRange(uses.Keys.Concat(spent).Where(n => !matches.UsesLeft.Contains(n) && !matches.Spent.Contains(n) && !setups.Contains(n) && !pools.ContainsKey(n)));
        if (notSimulated.Count > 0)
        {
            notes.Add($"{c.Name}: {string.Join(", ", notSimulated.Distinct(StringComparer.Ordinal))} {(notSimulated.Count == 1 ? "is" : "are")} not simulated.");
        }

        // A slot key is never dropped silently: every slot no modifier spends here (and every pool the stat block's
        // simulation does not cast from) is said, by level and what the fight has left.
        var unfunded = slots.Where(p => !fundedKeys.Contains(p.Key)).Select(p => SlotText(p.Key, p.Value))
            .Concat(pools.Where(p => !matches.UsesLeft.Contains(p.Key)).Select(p => SlotText(p.Value.Key, p.Value.Resource)))
            .ToList();
        if (unfunded.Count > 0)
        {
            notes.Add($"{c.Name}: spell slots are not simulated: {string.Join(", ", unfunded)}.");
        }

        var down = c.Hp == 0 || knockedOut;
        return new CombatantStart
        {
            Hp = knockedOut ? 0 : c.Hp is { } hp && spec.Hp is { } max ? Math.Min(hp, max) : c.Hp,
            TempHp = c.TempHp,
            Stable = knockedOut || (down && c.DeathSaves.Stable),
            DeathSuccesses = down ? Math.Min(2, c.DeathSaves.Successes) : 0,
            DeathFailures = down ? Math.Min(2, c.DeathSaves.Failures) : 0,
            Exhaustion = c.Exhaustion,
            Conditions = conditions,
            Concentration = down ? null : c.Concentration?.Spell,
            UsesLeft = matchedUses.Count == 0 ? null : matchedUses,
            Spent = matchedSpent,
            LegendaryActionsLeft = c.Legendary is { Actions: > 0 } l ? l.ActionsLeft : null,
            LegendaryResistanceLeft = c.Legendary is { Resistance: > 0 } r ? r.ResistanceLeft : null,
            ActiveSetups = setups,
            Unavailable = shared.Unavailable,
            HasActed = state.Round > 1 || CombatEnd.AnchorActed(state, c.Id),
            ReactionUsed = c.ReactionUsed,
            RelentlessUsed = CombatTracker.RelentlessUsed(c),
        };
    }

    /// <summary>
    /// One spell of an entry that draws on its spell slots, in the build's order: a slot-funded use (<see cref="Funded"/>,
    /// <see cref="SlotFundedUse"/>: its uses ARE slots) or a spell cast once a fight (<see cref="SlotCastSpell"/>: one slot
    /// of <see cref="SlotLevel"/> or higher casts it).
    /// </summary>
    private sealed record SlotSpell(string Name, int SlotLevel, bool OrHigher, bool Cast, int ExtraUses)
    {
        public bool Funds(int level) => OrHigher ? level >= SlotLevel : level == SlotLevel;

        public static SlotSpell Funded(SlotFundedUse use) => new(use.Modifier, use.SlotLevel, use.OrHigher, Cast: false, use.ExtraUses);

        public static SlotSpell CastOnce(SlotCastSpell spell) => new(spell.Name, spell.SlotLevel, OrHigher: true, Cast: true, 0);
    }

    /// <summary>
    /// The spells of an entry that draw on its spell slots, in the build's order, its main spell first: an archetype's (by
    /// its class and edition), its once-a-fight spells and then its slot-funded uses; a sim_profile's by name — a modifier
    /// with limited uses named as a spell the archetypes fund from slots (Fireball, Lightning Bolt, Shatter, Divine Smite,
    /// Healing Word, Cure Wounds), or a modifier or attack named as one they cast once a fight (Spirit Guardians, Spiritual
    /// Weapon, Call Lightning, Hex, the 2014 Hunter's Mark), in the build's modifier order and then its attacks', since the
    /// build DSL cannot say that a modifier's uses are slots. Whether the entry has such a modifier or attack at its level
    /// is <see cref="SimulationStartNames"/>'s to say (a level 4 wizard casts no Fireball).
    /// </summary>
    private static IReadOnlyList<SlotSpell> SlotSpells(CombatantSpec spec, string ruleset)
    {
        if (spec.Archetype is { } archetype)
        {
            return
            [
                .. ArchetypeCatalog.SlotCast(archetype, spec.Edition).Select(SlotSpell.CastOnce),
                .. ArchetypeCatalog.SlotFunded(archetype, spec.Edition).Select(SlotSpell.Funded),
            ];
        }

        if (spec.Build is not { } build)
        {
            return [];
        }

        var edition = build.Edition ?? spec.Edition ?? ruleset;
        static bool Same(string a, string b) => DslValueSet.Key(a) == DslValueSet.Key(b);
        var spells = new List<SlotSpell>();
        foreach (var m in (build.Modifiers ?? []).Where(m => m.Name is not null))
        {
            var name = m.Name!.Trim();
            if (m.Resource is not null && ArchetypeCatalog.SlotFundedSpells.FirstOrDefault(u => Same(u.Modifier, name)) is { } funded)
            {
                spells.Add(SlotSpell.Funded(funded with { Modifier = name }));
            }
            else if (ArchetypeCatalog.SlotCastSpells(edition).FirstOrDefault(u => Same(u.Name, name)) is { } cast)
            {
                spells.Add(SlotSpell.CastOnce(cast with { Name = name }));
            }
        }

        foreach (var a in (build.Attacks ?? []).Where(a => a.Name is not null))
        {
            var name = a.Name!.Trim();
            if (ArchetypeCatalog.SlotCastSpells(edition).FirstOrDefault(u => Same(u.Name, name)) is { } cast && !spells.Any(s => Same(s.Name, name)))
            {
                spells.Add(SlotSpell.CastOnce(cast with { Name = name }));
            }
        }

        return spells;
    }

    /// <summary>What <see cref="ShareSlots"/> decided: each slot-funded use's uses left, the spells not to cast, the slot keys that fund or cast something, and the notes.</summary>
    private sealed record SlotShare(IReadOnlyList<(string Name, int Uses)> Uses, IReadOnlyList<string> Unavailable, HashSet<string> Keys, IReadOnlyList<string> Notes);

    /// <summary>
    /// Shares the slots the fight has left among the entry's slot spells in the build's order (reviews CR03, CR04): a spell
    /// cast once a fight takes one slot of its level or higher (the lowest that is left; none when the fight already holds
    /// it, by concentration or as a named effect), else it is unavailable (one held by concentration then only cannot be cast
    /// again); a slot-funded use counts every funding slot still left (plus its extra uses, as before)
    /// and takes from them at most its fresh uses, lowest level first, so the next use on the same slots gets only the rest.
    /// Every spell left with nothing to cast it from gets a note, a slot-funded use as well as a spell cast once a fight
    /// (review F2R05: "Divine Smite has no slots left (…)").
    /// </summary>
    private static SlotShare ShareSlots(
        CombatantState c, IReadOnlyList<SlotSpell> spells, IReadOnlyList<KeyValuePair<string, CombatResource>> slots, StartNameMatches matches, IReadOnlyList<string> effects)
    {
        static bool Same(string? a, string b) => a is not null && DslValueSet.Key(a) == DslValueSet.Key(b);
        var ordered = slots
            .Select(p => (p.Key, Level: SlotLevel(p.Key, p.Value)))
            .Where(p => p.Level is not null)
            .OrderBy(p => p.Level).ThenBy(p => p.Key == R.Pact ? 1 : 0)
            .Select(p => (p.Key, Level: p.Level!.Value))
            .ToList();
        var left = slots.ToDictionary(p => p.Key, p => p.Value.Left ?? 0, StringComparer.Ordinal);
        var uses = new List<(string Name, int Uses)>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var unavailable = new List<string>();
        var notCast = new List<SlotSpell>();
        var notAgain = new List<SlotSpell>();
        var empty = new List<SlotSpell>();
        var shortened = false;
        foreach (var spell in spells)
        {
            var funding = ordered.Where(p => spell.Funds(p.Level)).Select(p => p.Key).ToList();
            keys.UnionWith(funding);
            if (spell.Cast)
            {
                // Held by the fight's concentration: up now, cast again only if a slot is left once it ends. Kept as a named
                // effect with no concentration (a 2014 Spiritual Weapon): up for the fight, nothing to cast.
                var concentrating = Same(c.Concentration?.Spell, spell.Name);
                if (!concentrating && effects.Any(e => Same(e, spell.Name)))
                {
                    continue;
                }

                var slot = funding.FirstOrDefault(k => left[k] > 0);
                if (slot is null)
                {
                    unavailable.Add(spell.Name);
                    (concentrating ? notAgain : notCast).Add(spell);
                }
                else if (!concentrating)
                {
                    left[slot]--;
                }

                continue;
            }

            // At most its fresh uses: a sim_profile's Fireball of 1 use leaves the second 3rd-level slot to its Lightning Bolt.
            var available = funding.Sum(k => left[k]);
            var alone = funding.Sum(k => slots.First(p => p.Key == k).Value.Left ?? 0);
            if (alone + spell.ExtraUses == 0)
            {
                empty.Add(spell);
            }

            var cap = matches.FreshUses.TryGetValue(spell.Name, out var fresh) ? Math.Max(0, fresh - spell.ExtraUses) : int.MaxValue;
            shortened |= Math.Min(available, cap) < Math.Min(alone, cap);
            uses.Add((spell.Name, available + spell.ExtraUses));
            var take = Math.Min(available, cap);
            foreach (var k in funding)
            {
                var spend = Math.Min(take, left[k]);
                left[k] -= spend;
                take -= spend;
            }
        }

        var notes = new List<string>();
        if (notCast.Count > 0 && slots.Count == 0)
        {
            // A sheet that records no slots at all (a multiclass caster's are never derived, D18) has none to cast from, as
            // its slot-funded uses have none: said with the fix, so it does not read as slots the fight spent.
            notes.Add($"the sheet records no spell slots, so the resumed fight does not cast {string.Join(" or ", notCast.Select(Needs))} " +
                      "(give the sheet its slots with campaign_character update).");
        }
        else if (notCast.Count > 0)
        {
            notes.Add($"no spell slot is left for {string.Join(" or ", notCast.Select(Needs))}, so the resumed fight does not cast {(notCast.Count == 1 ? "it" : "them")}.");
        }

        foreach (var spell in notAgain)
        {
            notes.Add($"no spell slot is left to cast {spell.Name} again ({SheetUse.Ordinal(spell.SlotLevel)} level or higher): it runs while its concentration holds.");
        }

        // A slot-funded use with no slot to draw on (review F2R05): said like the spells above, so a paladin whose sheet
        // records no slots does not silently deal no Divine Smite in the resumed fight.
        foreach (var spell in empty)
        {
            notes.Add(slots.Count == 0
                ? $"{spell.Name} has no slots left (the sheet records no spell slots; give the sheet its slots with campaign_character update)."
                : $"{spell.Name} has no slots left ({SheetUse.Ordinal(spell.SlotLevel)} level{(spell.OrHigher ? " or higher" : string.Empty)}).");
        }

        if (shortened)
        {
            notes.Add($"the slots left are shared in the build's order: {string.Join(", ", uses.Select(u => $"{u.Name} {u.Uses.ToString(CultureInfo.InvariantCulture)}"))}.");
        }

        return new SlotShare(uses, unavailable, keys, notes);

        static string Needs(SlotSpell spell) => $"{spell.Name} ({SheetUse.Ordinal(spell.SlotLevel)} level or higher)";
    }

    /// <summary>The spell level of a sheet slot (<c>slot:3</c>) or Pact Magic (<c>pact</c>, its level), else null.</summary>
    private static int? SlotLevel(string key, CombatResource resource) =>
        key == R.Pact ? resource.Level : CombatTracker.SlotLevelOf(key);

    /// <summary>"3rd-level 1/3 left", "Pact Magic (3rd-level) 1/2 left": a slot as the author reads it, never its key.</summary>
    private static string SlotText(string key, CombatResource resource)
    {
        var level = key == R.Pact ? resource.Level : CombatTracker.SlotLevelOf(key);
        var ordinal = level is { } l ? $"{SheetUse.Ordinal(l)}-level" : null;
        var name = key == R.Pact ? ordinal is null ? "Pact Magic" : $"Pact Magic ({ordinal})" : ordinal ?? key;
        return $"{name} {(resource.Left ?? 0).ToString(CultureInfo.InvariantCulture)}/{(resource.Max ?? 0).ToString(CultureInfo.InvariantCulture)} left";
    }

    /// <summary>One condition in the engine's vocabulary, or null when it cannot be expressed (its anchor is not simulated).</summary>
    /// <param name="atTurnHolder">The resume starts at the turn-holder: a condition applied during its turn this round is then imposed during the resumed turn.</param>
    private static StartCondition? Map(EncounterState state, CombatCondition condition, Dictionary<string, int> index, CombatantState? lastInOrder, bool atTurnHolder)
    {
        int? Entry(string? id) => id is not null && index.TryGetValue(id, out var i) ? i : null;
        var source = Entry(condition.Source);
        var imposed = atTurnHolder && condition.Applied is { } applied && applied.Round == state.Round && applied.TurnOf is not null && applied.TurnOf == state.TurnCombatantId;
        var save = condition.Save;
        StartCondition Make(string duration, int? sourceEntry = null, int? roundsLeft = null, int? escape = null, bool held = false) =>
            new(condition.Name, duration, sourceEntry, roundsLeft, save?.Ability, save?.Dc, escape, held, imposed);

        if (condition.Name == C.Prone)
        {
            return Make(SD.UntilStands, source);
        }

        switch (condition.Duration)
        {
            case D.UntilRemoved or D.Fight or D.UntilStands or D.ZeroHp:
                return save is null ? Make(SD.Fight, source) : Make(SD.SaveEnds, source);
            case D.Rounds when condition.Expires is { At: E.Start, Of: { } anchor } expires:
                var anchorEntry = Entry(anchor);
                if (anchorEntry is null)
                {
                    return null;
                }

                var left = Math.Max(0, CombatEnd.RoundsLeft(state, expires));
                return Make(save is null ? SD.Rounds : SD.SaveEnds, anchorEntry, left);
            case D.EndOfRound when condition.Expires is { } end && lastInOrder is not null:
                var last = Entry(lastInOrder.Id)!.Value;
                return Make(save is null ? SD.Rounds : SD.SaveEnds, last, Math.Max(0, CombatEnd.RoundsLeft(state, new ConditionExpiry(end.Round + 1, E.Start, lastInOrder.Id))));
            case D.Concentration:
                return condition.HeldBy is { } heldBy && Entry(heldBy) is { } holderEntry ? Make(SD.Fight, holderEntry, held: true) : null;
            case D.UntilStartOfSourceTurn or D.UntilEndOfSourceTurn:
                return source is null ? null : Make(condition.Duration, source);
            case D.UntilStartOfTargetTurn or D.UntilEndOfTargetTurn:
                return Make(condition.Duration, source);
            case D.UntilEscape:
                return source is null ? null : condition.EscapeDc is { } dc ? Make(SD.UntilEscape, source, escape: dc) : Make(SD.Fight, source);
            case D.SaveEnds:
                return Make(SD.SaveEnds, source);
            default:
                return null;
        }
    }
}
