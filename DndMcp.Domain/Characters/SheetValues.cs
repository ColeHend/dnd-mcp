using DndMcp.Domain.Features;

namespace DndMcp.Domain.Characters;

/// <summary>
/// The closed vocabularies a character sheet stores: how a resource recharges, the two rests, what a sheet condition's
/// duration is, the SRD conditions, a class's kind of spellcasting, where a sheet came from, and the kinds of reminder the
/// sheet operations return.
///
/// <para>
/// <b>These strings are stored data</b> (inside the JSON columns of <c>character_sheet</c>), read back by every later
/// version and by Phase 8's import and export. They are string constants, never CLR enums, for the reason
/// <c>CampaignValues</c> gives: a stored value must mean the same thing after a refactor. Add values; never rename one
/// without rewriting the rows that hold it.
/// </para>
/// <para>
/// Every set is a <see cref="DslValueSet"/>, so what a model types ("Short Rest", "short-rest") matches forgivingly and
/// what is stored is always the canonical value.
/// </para>
/// </summary>
public static class SheetValues
{
    /// <summary>
    /// When a counted resource (<c>resources.&lt;slug&gt;.recharge</c>) comes back. A resource with no recharge stored reads
    /// as <see cref="LongRest"/>, the default <see cref="SheetUpdate"/> writes.
    /// </summary>
    public static class Recharges
    {
        /// <summary>Every use back on a short or a long rest (2014 Channel Divinity, Action Surge, Ki).</summary>
        public const string ShortRest = "short_rest";

        /// <summary>One use back on a short rest, every use on a long rest (2024 Rage, Channel Divinity, Second Wind).</summary>
        public const string ShortRestOne = "short_rest_one";

        /// <summary>Every use back on a long rest only (Bladesong, Lay on Hands, 2014 Rage).</summary>
        public const string LongRest = "long_rest";

        /// <summary>"Regains all expended charges daily at dawn": treated as a long rest, the only clock the sheet has.</summary>
        public const string Dawn = "dawn";

        /// <summary>Never automatically: the caller restores it with <c>use</c> and a negative amount.</summary>
        public const string None = "none";

        /// <summary>What <see cref="SheetUpdate"/> stores for a counted resource given without a recharge.</summary>
        public const string Default = LongRest;

        public static readonly DslValueSet Set = new("recharge", [ShortRest, ShortRestOne, LongRest, Dawn, None]);

        /// <summary>Whether a long rest restores every use (everything but <see cref="None"/>).</summary>
        public static bool BackOnLongRest(string? recharge) => (recharge ?? Default) != None;
    }

    /// <summary>The two rests (<c>campaign_character rest {kind}</c>).</summary>
    public static class RestKinds
    {
        public const string Short = "short";
        public const string Long = "long";

        private static readonly Dictionary<string, string> Aliases = new()
        {
            ["short rest"] = Short,
            ["long rest"] = Long,
        };

        public static readonly DslValueSet Set = new("rest", [Short, Long], Aliases);
    }

    /// <summary>
    /// How long a condition or named effect stored ON THE SHEET lasts (<c>conditions[].duration</c>): the two timings that
    /// outlive a fight (contract §6.7). Everything else a fight tracks ends with it, so it never reaches the sheet.
    /// </summary>
    public static class Durations
    {
        /// <summary>Never automatically: a curse, mummy rot, Mucus Cloud. The default of <c>campaign_character condition</c>.</summary>
        public const string UntilRemoved = "until_removed";

        /// <summary>A count of rounds left (<c>remaining_rounds</c>; 1 minute = 10, 1 hour = 600); a rest may end it.</summary>
        public const string Rounds = "rounds";

        public static readonly DslValueSet Set = new("duration", [UntilRemoved, Rounds]);
    }

    /// <summary>
    /// The 15 SRD conditions, identical in name in the 2014 and 2024 rules (the 5e data's condition indexes): what
    /// <c>defenses.condition_immune</c> takes and what a sheet condition is matched against. Built from
    /// <see cref="DslValues.Conditions"/>'s two lists, so the DSL and the sheet cannot disagree on the names.
    /// </summary>
    public static class Conditions
    {
        /// <summary>Exhaustion is a column with a level (0-6), never an entry in <c>conditions</c> (contract D4).</summary>
        public const string Exhaustion = "exhaustion";

        public static readonly DslValueSet Set = new("condition", [.. DslValues.Conditions.Mechanical, .. DslValues.Conditions.LabelOnly]);

        /// <summary>
        /// The conditions that are or include Incapacitated, which end concentration automatically (contract §5.8, the
        /// statement the tracker, the sheet and the simulator share): a sheet given one stops concentrating.
        /// </summary>
        public static readonly IReadOnlyList<string> Incapacitating = ["incapacitated", "paralyzed", "petrified", "stunned", "unconscious"];
    }

    /// <summary>
    /// A class's spellcasting, which decides the slot table (<see cref="SpellSlotTables"/>). The same for every SRD class
    /// in both editions; the editions differ only in a half caster's level 1 row.
    /// </summary>
    public static class CasterKinds
    {
        /// <summary>Bard, cleric, druid, sorcerer, wizard: slots of levels 1-9.</summary>
        public const string Full = "full";

        /// <summary>Paladin, ranger: slots of levels 1-5 (2014 none at class level 1; 2024 two).</summary>
        public const string Half = "half";

        /// <summary>Warlock: Pact Magic, all slots of one level, back on a short rest (the <c>pact</c> key).</summary>
        public const string Pact = "pact";

        /// <summary>Barbarian, fighter, monk, rogue (the SRD has no Eldritch Knight or Arcane Trickster).</summary>
        public const string None = "none";
    }

    /// <summary>
    /// Where a sheet came from (<c>sheet_source</c>). Free text, not a closed set: these are the values the server itself
    /// writes or documents; any other one-line text (e.g. "skill:belmakor-build.md") is stored as given.
    /// </summary>
    public static class SheetSources
    {
        /// <summary>Typed in through <c>campaign_character</c>.</summary>
        public const string Manual = "manual";

        /// <summary>A test fixture's invented sheet (the exit-criteria fixtures say so on the sheet).</summary>
        public const string Fixture = "fixture";

        /// <summary>Imported from the PWA's export (Phase 8).</summary>
        public const string Pwa = "pwa";
    }

    /// <summary>What a <see cref="SheetReminder"/> is about, so a formatter or a test can pick one out without its wording.</summary>
    public static class ReminderKinds
    {
        /// <summary>The XP total reaches a level the sheet does not have yet.</summary>
        public const string LevelDue = "level_due";

        /// <summary>The class level just gained grants an Ability Score Improvement (or a feat in its place).</summary>
        public const string AbilityScoreImprovement = "ability_score_improvement";

        /// <summary>The 2024 class level 19 feature: an Epic Boon feat.</summary>
        public const string EpicBoon = "epic_boon";

        /// <summary>Class resources (Rage, Ki, Bladesong …) are never derived: the caller updates their maxima.</summary>
        public const string UpdateResources = "update_resources";

        /// <summary>
        /// Spell slots that are not computed (a multiclass sheet with a Spellcasting class; D18): the caller gives them. The
        /// Pact Magic slots are computed even then, from the warlock level alone.
        /// </summary>
        public const string GiveSlots = "give_slots";

        /// <summary>
        /// The classes or level changed and max_hp was not given: it is never recomputed (it may have been given, and feats
        /// such as Tough are not known), so the caller sets it or levels up with <c>level_up</c>, which adds the hit points.
        /// </summary>
        public const string MaxHpNotRecomputed = "max_hp_not_recomputed";

        /// <summary>The stored sim_profile no longer validates at the sheet's level.</summary>
        public const string SimProfile = "sim_profile";

        /// <summary>The Constitution modifier changed: by the rules the maximum changes by 1 per level, never automatically here.</summary>
        public const string ConChanged = "con_changed";

        /// <summary>A long rest ran with an until_removed condition on the sheet that may stop its recovery (2024 mummy rot).</summary>
        public const string CheckCondition = "check_condition";

        /// <summary>Exhaustion 6 or a hit point maximum of 0: the character dies.</summary>
        public const string Died = "died";
    }
}
