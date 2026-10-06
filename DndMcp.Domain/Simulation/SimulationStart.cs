using DndMcp.Domain.Features;

namespace DndMcp.Domain.Simulation;

/// <summary>
/// One creature's state where a live fight is picked up (balance_simulate <c>from_state</c>, contract §6.11), laid over the
/// fresh creature its <see cref="CombatantSpec"/> compiles to. A null or default field keeps the fresh-fight value.
///
/// <para>
/// <b>Domain-only, never a tool parameter</b> (contract X1): it rides on <see cref="SimulationCombatant.Start"/>, built by
/// the host from the tracker's rows. On <see cref="CombatantSpec"/> it would cost balance_simulate's published schema
/// (pinned under 24,000 characters) a nested state object, offer the model hand-typed seeds, and be copied by every host
/// <c>with</c> of the entry.
/// </para>
/// <para>
/// <b>Applied without a die</b>, compiled once into the run's read-only setup and set on every fight's creature the same
/// way, in creature order, so a fight stays a pure function of (seed, i) and a resumed run is byte-identical at any thread
/// count. HP is never rolled for a seeded creature (<c>enemy_hp: roll</c> skips it).
/// </para>
/// <para>
/// <b>One creature per entry</b>: an entry with a start has count 1 (per-creature state cannot be shared by copies).
/// </para>
/// <para>
/// <b>Names are matched, never refused.</b> <see cref="Concentration"/>, <see cref="UsesLeft"/>, <see cref="Spent"/> and
/// <see cref="ActiveSetups"/> name things by the simulation's own labels — a build modifier's label (its <c>name</c> in
/// the sim_profile or the archetype's build, else "kind #n"), a stat block action's name or shared recharge pool, a 2014
/// slot pool ("slot:3") — matched forgivingly: case, spaces, hyphens and underscores are ignored ("spirit-guardians" is
/// "Spirit Guardians"), the same key the DSL's value sets use. A name that matches nothing on this creature is left at its
/// fresh value and named in the report's assumptions ("Torch: the live state's "Bladesong" matches none of its uses,
/// recharges, slot pools or setups here, so it starts as in a fresh fight."), NEVER an exception: a sheet's resources
/// and the tracker's named effects ("Bladesong") need not be the sim_profile's modifiers ("Hex"), and refusing would make
/// most encounter simulations impossible. (A concentration that names none of the build's modifiers is not "unmatched":
/// it is a concentration the build does not model, held all the same.) A loader that writes its own "not simulated" notes
/// asks <see cref="SimulationStartNames.Match"/> first, so the two never say the same thing twice.
/// </para>
/// </summary>
public sealed record CombatantStart
{
    /// <summary>
    /// Current hit points, 0 to the maximum (the entry's hp, else the stat block's average); the maximum stays the real
    /// one, because massive damage, damage at 0 HP, healing's cap, Bloodied and the healing policies all read it. 0 starts a
    /// creature that makes death saves down (unconscious, prone, dying or <see cref="Stable"/>) and a troll that regenerates
    /// from 0 down waiting to; anything else dies at 0 HP and is refused (leave it out, or make it a
    /// <see cref="Placeholder"/>). Null: the maximum.
    /// </summary>
    public int? Hp { get; init; }

    /// <summary>
    /// Temporary hit points, which REPLACE the build's start-of-fight temp_hp grant (it was spent or kept in the live fight
    /// already; granting it again would hand the creature hit points it does not have). Null: the fresh grant.
    /// </summary>
    public int? TempHp { get; init; }

    /// <summary>
    /// Dead: valid only with <see cref="Placeholder"/>, which already means dead, so a loader never needs to set it (it is
    /// kept because the from_state design lists it, SIM §3.4). Set without a placeholder it is refused: a dead creature that
    /// holds no place would only add a party death the simulation did not deal (P(a party member dies) = 100%); leave it
    /// out instead.
    /// </summary>
    public bool Dead { get; init; }

    /// <summary>At 0 HP and stable (no death saves until damaged). Ignored above 0 HP, where there are none.</summary>
    public bool Stable { get; init; }

    /// <summary>Death save successes so far, 0-2, at 0 HP (ignored above it).</summary>
    public int DeathSuccesses { get; init; }

    /// <summary>Death save failures so far, 0-2, at 0 HP (ignored above it).</summary>
    public int DeathFailures { get; init; }

    /// <summary>Exhaustion level 0-6, each level one engine exhaustion condition for the whole fight; 6 kills on the seed, as it kills in a fight.</summary>
    public int Exhaustion { get; init; }

    /// <summary>The conditions it starts with, in the engine's duration vocabulary (<see cref="StartCondition"/>).</summary>
    public IReadOnlyList<StartCondition> Conditions { get; init; } = [];

    /// <summary>
    /// What it concentrates on, by label: its build's concentration modifier (Hex, Spirit Guardians, Hold Monster), which
    /// is then up (its setup paid), or anything else (a monster's spell, a spell the build does not model), which holds
    /// the conditions marked <see cref="StartCondition.HeldBySourceConcentration"/> and can be broken like any other.
    /// Null: not concentrating — a build's no-setup concentration modifier is then down for good (it was lost), unlike a
    /// fresh fight, where it starts up. Ignored at 0 HP (the drop ended it).
    /// </summary>
    public string? Concentration { get; init; }

    /// <summary>
    /// Uses left by name: a build's resource-limited modifier (its label), a monster's per-day action or spell, or a 2014
    /// caster's slot pool ("slot:3"); a count above the fresh one is capped at it (the live state may have gained what the
    /// simulation cannot hold). A recharge action named here with 0 starts spent (any other count leaves it ready). Others
    /// start full. A blank name is refused.
    /// </summary>
    public IReadOnlyDictionary<string, int>? UsesLeft { get; init; }

    /// <summary>
    /// Limited actions spent by name: a recharge action (or a shared recharge: "Breath Weapons") starts not ready and rolls
    /// to recharge at the start of its turns, as in any fight; a per-day action starts with no uses. A null or blank name
    /// is refused (it names nothing, and would otherwise reach the report as "").
    /// </summary>
    public IReadOnlyList<string> Spent { get; init; } = [];

    /// <summary>
    /// Legendary actions left this round, 0 or more; a count above the creature's (the in-lair one when
    /// <see cref="SimulationSpec.Lair"/>) is capped at it, so a seed can never hand it more legendary actions than its stat
    /// block allows. Reset at the start of its turn as always. Null: the full count.
    /// </summary>
    public int? LegendaryActionsLeft { get; init; }

    /// <summary>Legendary Resistance uses left, 0 or more, capped at its count (the in-lair one in a lair). Null: all of them.</summary>
    public int? LegendaryResistanceLeft { get; init; }

    /// <summary>
    /// A build's modifiers with a setup cost already paid, by label: how a named effect running in the live fight (Rage,
    /// Bladesong as a sim_profile modifier with a setup) reaches the simulation. Each name is matched forgivingly against
    /// the labels of the build's modifiers that have a <c>setup</c> (the sim_profile's or the archetype's: the archetype
    /// barbarian's is "Rage"); a match is up from the start and its setup is not paid again. A name that matches no setup
    /// modifier (a monster has none) is an assumption line, never an exception — a modifier without a setup is up from the
    /// start anyway. The concentration modifier follows <see cref="Concentration"/> instead: named here it is matched (no
    /// assumption line) but NOT raised, because a concentration up without a concentration to break would never end.
    /// <see cref="SimulationStartNames.Match"/> says beforehand which names match. A null or blank name is refused.
    /// </summary>
    public IReadOnlyList<string> ActiveSetups { get; init; } = [];

    /// <summary>It has taken a turn this fight: a lost concentration setup is then paid again only when it looks worth its cost, as after a build's first turn.</summary>
    public bool HasActed { get; init; }

    /// <summary>
    /// Spells it cannot cast in this fight, by label: a build's modifier (Spirit Guardians, Call Lightning, Hex) or attack
    /// (Spiritual Weapon) — a spell cast once a fight from a slot, with no slot of its level left in the live fight (review
    /// CR03; the loader decides which, <c>TrackerSimulation</c>). It is down from the start: a setup is never paid, a save
    /// effect never cast, an attack never made. One it is <see cref="Concentration"/> on keeps running until that
    /// concentration ends, then is not set up or cast again. Matched forgivingly like the other names; a name that matches
    /// nothing is an assumption line. A null or blank name is refused.
    /// </summary>
    public IReadOnlyList<string> Unavailable { get; init; } = [];

    /// <summary>Its reaction is spent until the start of its next turn.</summary>
    public bool ReactionUsed { get; init; }

    /// <summary>Its Relentless (once per rest) is spent.</summary>
    public bool RelentlessUsed { get; init; }

    /// <summary>
    /// Dead, and kept only for its place in the order: the conditions it imposed until the start or end of its next turn,
    /// or for rounds counted on its turns, still end where its turns would be (a dead lich's Paralyzing Touch), as for any
    /// creature that dies mid-fight. It never acts, never keeps its side in the fight, and is left out of the outcomes (a
    /// party placeholder is no death the simulation dealt) and of the report's combatants. Every other field is ignored;
    /// the entry may even give no monster, build, archetype or character (only a name), when the dead creature has no
    /// simulation route of its own.
    /// </summary>
    public bool Placeholder { get; init; }
}

/// <summary>
/// One condition a creature starts with, in the engine's duration vocabulary — exactly the public constants of
/// <see cref="StatBlockValues.Durations"/> (<see cref="StatBlockValues.Durations.All"/> lists them:
/// <c>until_start_of_source_turn</c>, <c>until_end_of_source_turn</c>, <c>save_ends</c>, <c>rounds</c>,
/// <c>until_escape</c>, <c>until_stands</c>, <c>fight</c>, <c>until_end_of_target_turn</c>,
/// <c>until_start_of_target_turn</c>), the same strings stat blocks use, so nothing is translated twice. The tracker maps
/// its RAW timings onto them (contract D12, §6.11); its own <c>until_removed</c>, <c>end_of_round</c> and
/// <c>concentration</c> are not accepted here (map them to <c>fight</c>, to <c>rounds</c> on the last creature in the
/// order, and to <see cref="HeldBySourceConcentration"/>). Durations match forgivingly (case, spaces, hyphens and
/// underscores ignored: "Until End Of Target Turn"); condition names are <see cref="StatBlockValues.Conditions.All"/>,
/// case ignored.
/// </summary>
/// <param name="Condition">An SRD condition other than exhaustion (<see cref="StatBlockValues.Conditions.All"/>; exhaustion is <see cref="CombatantStart.Exhaustion"/>). Prone always lasts until the creature stands, whatever the duration says, as everywhere in the engine.</param>
/// <param name="Duration">
/// A <see cref="StatBlockValues.Durations"/> value. The source-turn kinds, <c>rounds</c> and <c>until_escape</c> need
/// <paramref name="SourceEntry"/> (their ends are counted on the source's turns, and a condition with no source would never
/// end); <c>rounds</c> needs <paramref name="RoundsLeft"/>; <c>save_ends</c> needs <paramref name="SaveAbility"/> and
/// <paramref name="SaveDc"/>; <c>until_escape</c> needs <paramref name="EscapeDc"/>.
/// </param>
/// <param name="SourceEntry">The creature that imposed it: its entry's 0-based index over party then enemies (an entry of count 1), or null.</param>
/// <param name="RoundsLeft">
/// For <c>rounds</c> (or a cap on <c>save_ends</c>), 0 to 10,000: the source's turn ends still to pass before it ends (the
/// engine ticks it at each). A turn-holder that is the source has its current turn's end still to pass, so its own turn
/// counts. <b>0 is legal</b>: no counted turn end is left, and RAW ends the duration at the start of the source's next turn
/// (contract §5.13: "the START of the anchor's turn in round applied + N"), so it is compiled as
/// <c>until_start_of_source_turn</c> on the same source — a save_ends with a cap of 0 too, its save still repeated at the
/// end of each of the target's turns until then. That is what the tracker's formula
/// (<c>expires.round − round − (1 if the anchor has acted this round)</c>, §6.11) gives in a duration's last round once
/// its anchor has acted, so refusing it would refuse ordinary fights.
/// </param>
/// <param name="SaveAbility">For <c>save_ends</c>: the ability of the save repeated at the end of each of its turns.</param>
/// <param name="SaveDc">For <c>save_ends</c>: that save's DC.</param>
/// <param name="EscapeDc">For <c>until_escape</c>: the escape DC.</param>
/// <param name="HeldBySourceConcentration">It ends with its source's concentration (which the source's start must name); if that concentration did not survive the seed (the source starts down or incapacitated), the condition is not applied.</param>
/// <param name="ImposedDuringResumedTurn">
/// It was imposed during the very turn the fight resumes at: the loader sets it exactly when the condition's applied turn is
/// the turn-holder's current turn in the current round (<see cref="FightResume.StartAt"/> in <see cref="FightResume.Round"/>),
/// whatever its duration; it is refused without a <see cref="SimulationSpec.Resume"/>. The Domain decides what it changes,
/// by the engine's own rule for a condition imposed mid-turn (<c>Fight.AddCondition</c>): only when the creature that
/// anchors the duration is that turn-holder — the source for the source-turn kinds (and a rounds_left of 0), the target
/// for the target-turn kinds — does that turn not count. Until-END kinds then skip the turn-holder's current turn end
/// ("its NEXT turn", §5.13); until-START kinds survive the replayed start of that turn (the resume plays the turn's start
/// again, which would otherwise end at once what was imposed after it — a Stunning Strike "until the start of your next
/// turn"). Any other duration, or another anchor, is unchanged: their next turn is still ahead.
/// </param>
public sealed record StartCondition(
    string Condition,
    string Duration,
    int? SourceEntry = null,
    int? RoundsLeft = null,
    string? SaveAbility = null,
    int? SaveDc = null,
    int? EscapeDc = null,
    bool HeldBySourceConcentration = false,
    bool ImposedDuringResumedTurn = false);

/// <summary>
/// Where a live fight is resumed (<see cref="SimulationSpec.Resume"/>): the tracker's order instead of an initiative roll
/// (rolling would draw the dice the first round's turns would have drawn, and reorder a fight already in progress), and the
/// turn to start at. The resume starts at the START of that turn, so a turn whose start the tracker already played is
/// played again (its recharge and death save rolled anew): a documented half turn of difference, as for durations (D12).
/// </summary>
/// <param name="Order">Every entry exactly once, by 0-based index over party then enemies, first to act first; an entry's copies act together in its place.</param>
/// <param name="StartAt">The 0-based position in <paramref name="Order"/> whose turn the fight resumes at (the turn-holder); the entries before it have acted this round, the turn-holder has not (its turn's end is still to come).</param>
/// <param name="Round">The tracker's round, 1 or more: the log counts from it, while the round cap and the reported rounds count the resumed round as 1.</param>
public sealed record FightResume(IReadOnlyList<int> Order, int StartAt, int Round);

/// <summary>
/// Which live-state names a combatant's simulation knows, by exactly the rule <see cref="CombatantStart"/> matches them
/// with (<see cref="Match"/>). The tracker's loader (contract §6.11) passes a named effect in
/// <see cref="CombatantStart.ActiveSetups"/> only when it is a setup modifier and writes its own "not simulated" note for
/// the rest; without this it would have to re-implement the rule (the forgiving key, archetype expansion, build
/// resolution, "kind #n" labels, setups only, the concentration modifier left out) or pass everything, and the report's
/// unmatched-names assumption and its notes would say the same thing twice, or contradict each other.
/// </summary>
public static class SimulationStartNames
{
    /// <summary>
    /// Matches <paramref name="names"/> against what <paramref name="combatant"/> compiles to — the same expansion and
    /// resolution as <see cref="Simulator.Run"/> (an archetype at its level and edition, a build at its level with
    /// <paramref name="rulings"/>, a monster's stat block) — and says, for each start field, which names it would set.
    /// The combatant's own <see cref="SimulationCombatant.Start"/> is not read (a name-only placeholder matches nothing).
    /// Null or blank names name nothing and are in no list (a start refuses them); the others keep their spelling and
    /// order, so the caller can find its own items.
    /// </summary>
    /// <param name="combatant">The entry as it will be simulated: a <c>character</c> already expanded (an unexpanded one is an <see cref="ArgumentException"/>, as in a run), a monster with its stat block.</param>
    /// <param name="names">The live state's names: resources, recharges, named effects, a concentration.</param>
    /// <param name="edition">The fight's edition, as <see cref="SimulationSpec.Edition"/> (an archetype without its own follows it).</param>
    /// <param name="rulings">The fight's rulings, as <see cref="SimulationSpec.Rulings"/> (a build resolves with them).</param>
    /// <exception cref="Core.DndInputException">The entry is one a run would refuse (named "combatant (its name)").</exception>
    public static StartNameMatches Match(SimulationCombatant combatant, IEnumerable<string?> names, string? edition = null, RulingsSpec? rulings = null)
    {
        ArgumentNullException.ThrowIfNull(combatant);
        ArgumentNullException.ThrowIfNull(names);
        var template = SimulationPreparation.CompileAlone(combatant, edition, rulings);
        var uses = new List<string>();
        var spent = new List<string>();
        var setups = new List<string>();
        var concentration = new List<string>();
        var unmatched = new List<string>();
        var unavailable = new List<string>();
        var fresh = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var any = false;
            if (StartPreparation.MatchesUses(template, name))
            {
                uses.Add(name);
                any = true;
                if (StartPreparation.FreshUses(template, name) is { } count)
                {
                    fresh[name] = count;
                }
            }

            if (StartPreparation.UnavailableNumbers(template, name) is { Modifiers.Count: > 0 } or { Attacks.Count: > 0 })
            {
                unavailable.Add(name);
                any = true;
            }

            if (StartPreparation.MatchesSpent(template, name))
            {
                spent.Add(name);
                any = true;
            }

            var setupNumbers = StartPreparation.SetupNumbers(template, name);
            if (setupNumbers.Any(n => n != (template.Pc?.ConcentrationNumber ?? 0)))
            {
                setups.Add(name);
            }

            any |= setupNumbers.Count > 0;
            if (StartPreparation.IsConcentrationModifier(template, name))
            {
                concentration.Add(name);
                any = true;
            }

            if (!any)
            {
                unmatched.Add(name);
            }
        }

        return new StartNameMatches(uses, spent, setups, concentration, unmatched) { Unavailable = unavailable, FreshUses = fresh };
    }
}

/// <summary>
/// What <see cref="SimulationStartNames.Match"/> found: per start field, the given names that field would set on this
/// combatant. A name in a field's list passed in that field changes the simulation and is never in the report's
/// unmatched-names assumption; a name in no list (<see cref="Unmatched"/>) is something the simulation does not model for
/// this combatant — the loader's "not simulated" note, or leave it to the assumption line, not both.
/// </summary>
/// <param name="UsesLeft">Names <see cref="CombatantStart.UsesLeft"/> sets: a build's resource-limited modifiers, a monster's per-day or recharge actions (and shared recharges), a 2014 slot pool ("slot:3").</param>
/// <param name="Spent">Names <see cref="CombatantStart.Spent"/> sets: a monster's limited actions (recharge or per-day).</param>
/// <param name="ActiveSetups">Names <see cref="CombatantStart.ActiveSetups"/> raises: a build's modifiers with a setup, other than its concentration modifier.</param>
/// <param name="Concentration">Names that are the build's concentration modifier (<see cref="CombatantStart.Concentration"/> starts it up; any other concentration is held but models nothing of the build's).</param>
/// <param name="Unmatched">Names in none of the other lists.</param>
public sealed record StartNameMatches(
    IReadOnlyList<string> UsesLeft,
    IReadOnlyList<string> Spent,
    IReadOnlyList<string> ActiveSetups,
    IReadOnlyList<string> Concentration,
    IReadOnlyList<string> Unmatched)
{
    /// <summary>Names <see cref="CombatantStart.Unavailable"/> sets: a build's modifiers or attacks by label.</summary>
    public IReadOnlyList<string> Unavailable { get; init; } = [];

    /// <summary>
    /// For each name in <see cref="UsesLeft"/> that is a build resource, its uses in a fresh fight (the most a start can
    /// give it): what a loader sharing slots among several such modifiers allots each at most (review CR04).
    /// </summary>
    public IReadOnlyDictionary<string, int> FreshUses { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);
}
