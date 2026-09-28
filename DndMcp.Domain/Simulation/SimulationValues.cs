using DndMcp.Domain.Features;

namespace DndMcp.Domain.Simulation;

/// <summary>
/// Every wire value of <c>balance_simulate</c>'s own arguments (positions, targeting and healing policies, surprise,
/// enemy HP), as string constants grouped by the field that takes them, each with a <see cref="DslValueSet"/> so the model's
/// spelling is matched the way the build DSL's is ("Focus Fire", "focus-fire" and "focus_fire" are one value).
///
/// <para>
/// <b>Strings, not enums</b>, as in <see cref="DslValues"/>: results echo them ("policies: enemies spread — …"), and from
/// Phase 7 a prepared encounter stores them. Add values; never rename one.
/// </para>
/// </summary>
public static class SimulationValues
{
    /// <summary>Which line of its side a combatant stands in (there is no grid; see <see cref="Simulator"/>).</summary>
    public static class Positions
    {
        /// <summary>Engaged in melee: melee attacks can reach it while it stands.</summary>
        public const string Front = "front";

        /// <summary>Behind the front line: melee attacks reach it only when its side has no standing front-liner (or the attacker flies).</summary>
        public const string Back = "back";

        public static readonly DslValueSet Set = new("position", [Front, Back]);
    }

    /// <summary>Who a side attacks (<see cref="PolicySpec"/>).</summary>
    public static class Targeting
    {
        /// <summary>The reachable enemy with the lowest current HP (ties: lowest max HP, then listing order).</summary>
        public const string FocusFire = "focus_fire";

        /// <summary>A reachable enemy chosen uniformly at random for each attack.</summary>
        public const string Spread = "spread";

        /// <summary>The reachable enemy with the highest expected damage per round (computed once per run).</summary>
        public const string Threat = "threat";

        /// <summary>Enemies that can heal first, then spread (enemies side only).</summary>
        public const string HealerFirst = "healer_first";

        /// <summary>Concentrating enemies first, then spread (enemies side only).</summary>
        public const string BreakConcentration = "break_concentration";

        /// <summary>The party's choices.</summary>
        public static readonly DslValueSet PartySet = new("party targeting policy", [FocusFire, Spread, Threat]);

        /// <summary>The enemies' choices.</summary>
        public static readonly DslValueSet EnemiesSet = new("enemies targeting policy", [Spread, FocusFire, Threat, HealerFirst, BreakConcentration]);

        /// <summary>One line per policy for the assumptions a result lists.</summary>
        public static string Meaning(string policy) => policy switch
        {
            FocusFire => "the reachable enemy with the lowest current HP",
            Spread => "a reachable enemy at random for each attack",
            Threat => "the reachable enemy with the highest expected damage per round",
            HealerFirst => "enemies that can heal first, then at random",
            BreakConcentration => "concentrating enemies first, then at random",
            _ => policy,
        };
    }

    /// <summary>When a creature with Legendary Resistance turns a failed save into a success.</summary>
    public static class LegendaryResistance
    {
        /// <summary>When the failed save would impose a condition that matters, or its damage would drop the creature to 0 HP.</summary>
        public const string Conditions = "conditions";

        /// <summary>On every failed save while uses last.</summary>
        public const string Always = "always";

        /// <summary>Never (the uses are ignored).</summary>
        public const string Never = "never";

        public static readonly DslValueSet Set = new("legendary resistance policy", [Conditions, Always, Never]);

        public static string Meaning(string policy) => policy switch
        {
            Conditions => "spent when a failed save would impose a condition or drop it to 0 HP",
            Always => "spent on every failed save while uses last",
            Never => "never spent",
            _ => policy,
        };
    }

    /// <summary>When a build with a heal uses it.</summary>
    public static class Healing
    {
        /// <summary>Heal only allies at 0 HP (dying). A self-only heal (Second Wind) is used at half HP or less.</summary>
        public const string Downed = "downed";

        /// <summary>Heal allies (and itself) at half HP or less, the lowest first.</summary>
        public const string BelowHalf = "below_half";

        /// <summary>Never heal.</summary>
        public const string Never = "never";

        public static readonly DslValueSet Set = new("healing policy", [Downed, BelowHalf, Never]);

        public static string Meaning(string policy) => policy switch
        {
            Downed => "heals only allies at 0 HP (a self-only heal at half HP or less)",
            BelowHalf => "heals allies at half HP or less, the lowest first",
            Never => "never heals",
            _ => policy,
        };
    }

    /// <summary>Who is surprised when the fight starts.</summary>
    public static class Surprise
    {
        public const string None = "none";

        /// <summary>The party is surprised.</summary>
        public const string Party = "party";

        /// <summary>The enemies are surprised.</summary>
        public const string Enemies = "enemies";

        public static readonly DslValueSet Set = new("surprise", [None, Party, Enemies]);
    }

    /// <summary>How monsters' hit points are set at the start of each fight.</summary>
    public static class EnemyHp
    {
        /// <summary>The stat block's average.</summary>
        public const string Average = "average";

        /// <summary>Rolled from the hit dice each fight (at least 1).</summary>
        public const string Roll = "roll";

        public static readonly DslValueSet Set = new("enemy_hp", [Average, Roll]);
    }

    /// <summary>How one fight ended.</summary>
    public static class Outcomes
    {
        /// <summary>Every enemy is at 0 HP (or dead) while a party member still stands.</summary>
        public const string PartyWins = "party_wins";

        /// <summary>Every party member is at 0 HP: dying, stable or dead.</summary>
        public const string PartyDefeated = "party_defeated";

        /// <summary>The round cap came first.</summary>
        public const string Draw = "draw";
    }
}
