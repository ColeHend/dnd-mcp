using DndMcp.Domain.Features;

namespace DndMcp.Domain.Combat;

/// <summary>
/// The closed vocabularies of the live combat tracker: how long a condition lasts (contract §5.13), what a reminder is
/// about (§6.8), what a server roll is for (§6.10), the keys of a combatant's <c>resources</c> (§4), the hit-point
/// choices of <c>add</c> (D17), and the wire words of a combat_log row's detail.
///
/// <para>
/// <b>These strings are stored data and wire values.</b> They sit inside the <c>combatant</c> JSON columns and the
/// <c>combat_log.detail</c> objects, are read back by every later build (and by Phase 8's import of a fight in progress),
/// and the host prints and tests them. They are string constants, never CLR enums (the house rule <c>CampaignValues</c>
/// states): a renamed value would make old rows say something no build recognises. Add values; never rename one.
/// </para>
/// </summary>
public static class CombatValues
{
    /// <summary>
    /// How a condition or named effect on a combatant ends (contract §5.13): a superset of the simulator's
    /// <c>StatBlockValues.Durations</c> (the turn-anchored kinds, <c>save_ends</c>, <c>rounds</c>, <c>until_escape</c>,
    /// <c>until_stands</c>, <c>fight</c> are the same strings), plus the tracker's own <see cref="Concentration"/>,
    /// <see cref="ZeroHp"/>, <see cref="EndOfRound"/> and <see cref="UntilRemoved"/>. <c>TrackerSimulation</c> maps each onto
    /// the engine's vocabulary (§6.11); <c>CombatEnd</c> decides from it what persists to the sheet (§6.7).
    /// </summary>
    public static class Durations
    {
        /// <summary>Ends at the start of the source's next turn.</summary>
        public const string UntilStartOfSourceTurn = "until_start_of_source_turn";

        /// <summary>Ends at the end of the source's next turn (applied during the source's own turn: that turn's end is skipped).</summary>
        public const string UntilEndOfSourceTurn = "until_end_of_source_turn";

        /// <summary>Ends at the start of the target's (the holder's) next turn.</summary>
        public const string UntilStartOfTargetTurn = "until_start_of_target_turn";

        /// <summary>Ends at the end of the target's next turn (applied during the target's own turn: that turn's end is skipped).</summary>
        public const string UntilEndOfTargetTurn = "until_end_of_target_turn";

        /// <summary>A save at the end of each of the target's turns ends it; the tracker prompts and the caller removes it.</summary>
        public const string SaveEnds = "save_ends";

        /// <summary>N rounds: ends at the START of its anchor's turn in round <c>applied + N</c> ("1 minute" = 10).</summary>
        public const string Rounds = "rounds";

        /// <summary>Held by its source's concentration: ends when that concentration ends.</summary>
        public const string Concentration = "concentration";

        /// <summary>Until the target escapes (the caller removes it), or the source is incapacitated, dead or left (automatic).</summary>
        public const string UntilEscape = "until_escape";

        /// <summary>Until the creature stands (the caller removes it); ends at <c>end</c>. Prone.</summary>
        public const string UntilStands = "until_stands";

        /// <summary>The Unconscious the tracker adds at 0 HP: ends when hit points rise above 0.</summary>
        public const string ZeroHp = "zero_hp";

        /// <summary>Ends after the last turn of round R (<c>expires.round</c>).</summary>
        public const string EndOfRound = "end_of_round";

        /// <summary>Never automatically; persists to the sheet at <c>end</c> (a curse, Mucus Cloud, mummy rot).</summary>
        public const string UntilRemoved = "until_removed";

        /// <summary>Ends at <c>end</c>: the default for everything not listed in §5.13's defaults.</summary>
        public const string Fight = "fight";

        /// <summary>Every duration, in the order messages list them.</summary>
        public static readonly IReadOnlyList<string> All =
        [
            UntilStartOfSourceTurn, UntilEndOfSourceTurn, UntilStartOfTargetTurn, UntilEndOfTargetTurn, SaveEnds, Rounds,
            Concentration, UntilEscape, UntilStands, ZeroHp, EndOfRound, UntilRemoved, Fight,
        ];

        /// <summary>The canonical names, matched forgivingly (case, spaces, hyphens and underscores ignored).</summary>
        public static readonly DslValueSet Set = new("duration", All);

        /// <summary>The kinds anchored on the SOURCE's turn: they need a source that is a combatant.</summary>
        public static bool IsSourceTurn(string duration) => duration is UntilStartOfSourceTurn or UntilEndOfSourceTurn;

        /// <summary>The kinds anchored on the target's (holder's) turn.</summary>
        public static bool IsTargetTurn(string duration) => duration is UntilStartOfTargetTurn or UntilEndOfTargetTurn;
    }

    /// <summary>
    /// Where a timed duration's expiry sits (<c>expires.at</c>): at the START of the <c>of</c> combatant's turn in round
    /// <c>expires.round</c> (<see cref="Start"/>, the <c>rounds</c> kind and timed concentration), or after the last turn of
    /// round <c>expires.round</c> (<see cref="RoundEnd"/>, the <c>end_of_round</c> kind).
    /// </summary>
    public static class ExpiryPoints
    {
        public const string Start = "start";
        public const string RoundEnd = "round_end";
    }

    /// <summary>
    /// What a reminder is about (contract §6.8), so the host and the tests pick one out without its wording. Each carries
    /// the exact call that resolves it where one exists.
    /// </summary>
    public static class ReminderKinds
    {
        public const string ConcentrationSave = "concentration_save";
        public const string DeathSaveDue = "death_save_due";
        public const string Dying = "dying";
        public const string Dropped = "dropped";
        public const string Died = "died";
        public const string Defeated = "defeated";
        public const string DeathInterceptor = "death_interceptor";
        public const string Stable = "stable";
        public const string Revived = "revived";
        public const string Expired = "expired";
        public const string Expiring = "expiring";
        public const string SaveEnds = "save_ends";
        public const string LegendaryAvailable = "legendary_available";
        public const string LegendaryReset = "legendary_reset";
        public const string Recharge = "recharge";
        public const string Regeneration = "regeneration";
        public const string Surprised = "surprised";
        public const string LairAction = "lair_action";
        public const string Bloodied = "bloodied";
        public const string GrappleEnded = "grapple_ended";
        public const string ConcentrationBroken = "concentration_broken";
        public const string NoHp = "no_hp";
        public const string NoInitiative = "no_initiative";
        public const string AllEnemiesDown = "all_enemies_down";
        public const string ExhaustionEffects = "exhaustion_effects";
        public const string ConditionEffects = "condition_effects";
        public const string UnconsciousCrit = "unconscious_crit";
        public const string Tie = "tie";
        public const string Trait = "trait";
        public const string Round = "round";
        public const string LevelUp = "level_up";

        /// <summary>The write-back left a sheet-seeded combatant dying at 0 HP (§6.7 "X is dying at 0 HP").</summary>
        public const string DyingAtEnd = "dying_at_end";

        /// <summary>
        /// A creature acts outside its own turn (a legendary action): its damage call names it as <c>source</c>, since a
        /// roll with none is the turn-holder's (§6.10) — a PC's open roll in a DM campaign for an enemy's Lash (review LR03).
        /// </summary>
        public const string OffTurnRoll = "off_turn_roll";

        /// <summary>Every kind.</summary>
        public static readonly IReadOnlyList<string> All =
        [
            ConcentrationSave, DeathSaveDue, Dying, Dropped, Died, Defeated, DeathInterceptor, Stable, Revived, Expired, Expiring,
            SaveEnds, LegendaryAvailable, LegendaryReset, Recharge, Regeneration, Surprised, LairAction, Bloodied, GrappleEnded,
            ConcentrationBroken, NoHp, NoInitiative, AllEnemiesDown, ExhaustionEffects, ConditionEffects, UnconsciousCrit, Tie,
            Trait, Round, LevelUp, DyingAtEnd, OffTurnRoll,
        ];
    }

    /// <summary>
    /// What a server roll is for (contract §6.10): the label is <c>&lt;name&gt;: &lt;purpose&gt;</c>, and the Repository
    /// decides secrecy from the subject and the purpose ("hit points" of a non-party subject is always secret).
    /// </summary>
    public static class Purposes
    {
        public const string Initiative = "initiative";
        public const string Damage = "damage";
        public const string DamageCritical = "damage (critical)";
        public const string Heal = "heal";
        public const string DeathSave = "death save";
        public const string ConcentrationSave = "concentration save";
        public const string HitPoints = "hit points";
        public const string TemporaryHitPoints = "temporary hit points";

        /// <summary>Every purpose a combat step asks for.</summary>
        public static readonly IReadOnlyList<string> All =
            [Initiative, Damage, DamageCritical, Heal, DeathSave, ConcentrationSave, HitPoints, TemporaryHitPoints];
    }

    /// <summary>
    /// The keys of a combatant's <c>resources</c> object (contract §4) and the kinds of a stat block's limited use. The
    /// sheet's resources are keyed by their slug (<c>"bladesong"</c>), which never contains a colon, so the prefixed keys
    /// cannot collide with one.
    /// </summary>
    public static class ResourceKeys
    {
        /// <summary>A sheet's spell slots of one level: <c>"slot:1"</c> … <c>"slot:9"</c>, <c>{"max":4,"used":1}</c>.</summary>
        public const string SlotPrefix = "slot:";

        /// <summary>The sheet's Pact Magic slots: <c>{"level":3,"max":2,"used":1}</c>.</summary>
        public const string Pact = "pact";

        /// <summary>A stat block's limited use by its action (or shared recharge) name: <c>"limited:Fire Breath"</c>.</summary>
        public const string LimitedPrefix = "limited:";

        /// <summary>A 2014 monster's spell slots of one level: <c>"pool:slot:3"</c>, <c>{"max":3,"used":0}</c>.</summary>
        public const string PoolPrefix = "pool:";

        /// <summary>
        /// A consumed holding: <c>"item:&lt;holding id&gt;"</c>, <c>{"name":"Potion of Healing","used":1}</c>. Z's undo guard
        /// matches exactly this prefix, so it is never spelled another way.
        /// </summary>
        public const string ItemPrefix = "item:";

        /// <summary><c>"slot:3"</c>.</summary>
        public static string Slot(int level) => SlotPrefix + level.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary><c>"pool:slot:3"</c>.</summary>
        public static string PoolSlot(int level) => PoolPrefix + Slot(level);

        /// <summary><c>"limited:Fire Breath"</c>.</summary>
        public static string Limited(string name) => LimitedPrefix + name;

        /// <summary><c>"item:&lt;holding id&gt;"</c>.</summary>
        public static string Item(string holdingId) => ItemPrefix + holdingId;
    }

    /// <summary>The kinds of a stat block limited use (<c>resources["limited:…"].kind</c>).</summary>
    public static class ResourceKinds
    {
        /// <summary><c>{"kind":"recharge","min":5,"ready":true}</c>: spent until a d6 at the start of its turn rolls <c>min</c> or more.</summary>
        public const string Recharge = "recharge";

        /// <summary><c>{"kind":"per_day","max":2,"used":0}</c>.</summary>
        public const string PerDay = "per_day";
    }

    /// <summary>
    /// The hit points an <c>add</c> entry gives (contract §6.2): the stat block's average, a roll of its Hit Dice (K rolls
    /// it, logged "hit points"), unknown (the combatant accumulates <c>damage_taken</c>, D17), or a number.
    /// </summary>
    public static class HpChoices
    {
        public const string Avg = "avg";
        public const string Roll = "roll";
        public const string Unknown = "unknown";

        /// <summary>A given number (<see cref="HpChoice.Value"/>).</summary>
        public const string Number = "number";

        public static readonly DslValueSet Set = new("hp", [Avg, Roll, Unknown]);
    }
}
