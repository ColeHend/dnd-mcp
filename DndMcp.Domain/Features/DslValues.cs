namespace DndMcp.Domain.Features;

/// <summary>
/// Every wire value of the feature DSL, as string constants grouped by the field that takes them.
///
/// <para>
/// <b>Strings, not enums</b>, because the DSL is stored (Phase 7 keeps it as <c>character_sheet.sim_profile</c>) and a
/// stored build must mean the same thing after the code is refactored: an enum's number or name can drift, a string
/// constant in a published vocabulary does not. In-process code compares against these constants; the resolved build
/// only ever carries the canonical spelling (<see cref="DslValueSet.TryMatch"/> normalises what the model wrote).
/// </para>
/// <para>
/// Renaming a value here is a breaking change to stored data and to the tool schema the model reads. Add values; never
/// rename or remove one without a migration.
/// </para>
/// </summary>
public static class DslValues
{
    /// <summary>The two rule sets a build follows. Sugar (Great Weapon Fighting) and notes depend on it.</summary>
    public static class Editions
    {
        public const string E2014 = "2014";
        public const string E2024 = "2024";

        /// <summary>The default when a build names none, matching the rules tools' default.</summary>
        public const string Default = E2024;

        public static readonly DslValueSet Set = new("edition", [E2014, E2024]);
    }

    /// <summary>Ability keys. <see cref="None"/> is valid only as an attack's to_hit ability.</summary>
    public static class Abilities
    {
        public const string Str = "str";
        public const string Dex = "dex";
        public const string Con = "con";
        public const string Int = "int";
        public const string Wis = "wis";
        public const string Cha = "cha";

        /// <summary>An attack roll with no ability modifier (a flat-bonus attack given by to_hit.total, a trap).</summary>
        public const string None = "none";

        public static readonly IReadOnlyList<string> All = [Str, Dex, Con, Int, Wis, Cha];

        private static readonly Dictionary<string, string> FullNames = new()
        {
            ["strength"] = Str,
            ["dexterity"] = Dex,
            ["constitution"] = Con,
            ["intelligence"] = Int,
            ["wisdom"] = Wis,
            ["charisma"] = Cha,
        };

        /// <summary>The six abilities (saving throws, DC abilities, amounts).</summary>
        public static readonly DslValueSet Set = new("ability", All, FullNames);

        /// <summary>An attack roll's ability: the six, or none.</summary>
        public static readonly DslValueSet ToHitSet = new("ability", [.. All, None], FullNames);

        /// <summary>
        /// Elven Accuracy works only on attack rolls "using Dexterity, Intelligence, Wisdom, or Charisma" (XGE 2014).
        /// </summary>
        public static readonly IReadOnlyList<string> ElvenAccuracyAbilities = [Dex, Int, Wis, Cha];

        /// <summary>"Str", "Cha": how results name an ability's part of a bonus.</summary>
        public static string Display(string ability) => ability switch
        {
            Str => "Str",
            Dex => "Dex",
            Con => "Con",
            Int => "Int",
            Wis => "Wis",
            Cha => "Cha",
            None => "none",
            _ => throw new ArgumentOutOfRangeException(nameof(ability), ability, "Not an ability key."),
        };
    }

    /// <summary>The 13 damage types, the same in both editions.</summary>
    public static class DamageTypes
    {
        public static readonly DslValueSet Set = new(
            "damage type",
            ["acid", "bludgeoning", "cold", "fire", "force", "lightning", "necrotic", "piercing", "poison", "psychic", "radiant", "slashing", "thunder"]);
    }

    /// <summary>
    /// Attack properties. Weapon properties plus <see cref="Melee"/>/<see cref="Ranged"/> (how the attack is made, which
    /// decides Prone's advantage or disadvantage, Archery, Dueling, GWF) and <see cref="Spell"/> (a spell attack, which is
    /// not a weapon: Savage Attacker and power attacks default to weapons only).
    /// </summary>
    public static class Properties
    {
        public const string Melee = "melee";
        public const string Ranged = "ranged";
        public const string Spell = "spell";
        public const string Heavy = "heavy";
        public const string Light = "light";
        public const string Finesse = "finesse";
        public const string TwoHanded = "two-handed";
        public const string Versatile = "versatile";
        public const string Reach = "reach";
        public const string Thrown = "thrown";

        public static readonly DslValueSet Set = new(
            "attack property", [Melee, Ranged, Spell, Heavy, Light, Finesse, TwoHanded, Versatile, Reach, Thrown]);
    }

    /// <summary>How an attack is used each turn.</summary>
    public static class AttackActions
    {
        /// <summary>Part of the Attack action (counts for "as part of the Attack action" bonuses).</summary>
        public const string Action = "action";

        /// <summary>Made every turn with the Bonus Action (2014 two-weapon fighting's offhand attack).</summary>
        public const string BonusAction = "bonus_action";

        public static readonly DslValueSet Set = new("attack action", [Action, BonusAction]);
    }

    /// <summary>The eight 2024 weapon masteries.</summary>
    public static class Masteries
    {
        public const string Graze = "graze";
        public const string Vex = "vex";
        public const string Topple = "topple";
        public const string Sap = "sap";
        public const string Cleave = "cleave";
        public const string Nick = "nick";
        public const string Push = "push";
        public const string Slow = "slow";

        public static readonly DslValueSet Set = new("weapon mastery", [Graze, Vex, Topple, Sap, Cleave, Nick, Push, Slow]);
    }

    /// <summary>
    /// Cantrip scaling by character level (both editions): ×1 at 1–4, ×2 at 5–10, ×3 at 11–16, ×4 at 17–20.
    /// </summary>
    public static class Cantrips
    {
        /// <summary>The damage dice multiply (Fire Bolt 1d10 → 2d10 at 5).</summary>
        public const string Dice = "dice";

        /// <summary>The number of attacks multiplies (Eldritch Blast: one beam → two at 5).</summary>
        public const string Beams = "beams";

        public static readonly DslValueSet Set = new("cantrip scaling", [Dice, Beams]);

        /// <summary>1, 2, 3 or 4: the cantrip multiplier at character level <paramref name="level"/> (1–20).</summary>
        public static int Multiplier(int level) => level switch
        {
            < 1 or > 20 => throw new ArgumentOutOfRangeException(nameof(level), level, "Character levels are 1 to 20."),
            < 5 => 1,
            < 11 => 2,
            < 17 => 3,
            _ => 4,
        };
    }

    /// <summary>Modifier kinds. <see cref="ModifierFields"/> says which fields each one takes.</summary>
    public static class Kinds
    {
        public const string ToHit = "to_hit";
        public const string ExtraDamage = "extra_damage";
        public const string BonusDamage = "bonus_damage";
        public const string CritRange = "crit_range";
        public const string Advantage = "advantage";
        public const string Lucky = "lucky";
        public const string ElvenAccuracy = "elven_accuracy";
        public const string DamageDieRemap = "damage_die_remap";
        public const string RerollDamageTakeBest = "reroll_damage_take_best";
        public const string ExtraAttack = "extra_attack";
        public const string PowerAttack = "power_attack";
        public const string SaveEffect = "save_effect";
        public const string ConditionOnHit = "condition_on_hit";
        public const string IgnoreCover = "ignore_cover";
        public const string Ac = "ac";
        public const string Resistance = "resistance";
        public const string TempHp = "temp_hp";

        public static readonly DslValueSet Set = new(
            "modifier kind",
            [
                ToHit, ExtraDamage, BonusDamage, CritRange, Advantage, Lucky, ElvenAccuracy, DamageDieRemap, RerollDamageTakeBest,
                ExtraAttack, PowerAttack, SaveEffect, ConditionOnHit, IgnoreCover, Ac, Resistance, TempHp,
            ]);

        /// <summary>
        /// Defensive kinds: accepted (the Phase 5 simulator uses them), never change damage dealt, and results say so.
        /// </summary>
        public static readonly IReadOnlyList<string> Defensive = [Ac, Resistance, TempHp];
    }

    /// <summary>When a rider (extra_damage, condition_on_hit) applies.</summary>
    public static class When
    {
        public const string EveryHit = "every_hit";
        public const string FirstHitPerTurn = "first_hit_per_turn";

        /// <summary>Only on a crit, added once (not doubled): Brutal Critical style.</summary>
        public const string OnCrit = "on_crit";

        /// <summary>On a miss (never a crit), e.g. a homebrew "even on a miss" rider.</summary>
        public const string OnMiss = "on_miss";

        public static readonly DslValueSet ExtraDamageSet = new("when", [EveryHit, FirstHitPerTurn, OnCrit, OnMiss]);

        public static readonly DslValueSet ConditionOnHitSet = new("when", [EveryHit, FirstHitPerTurn]);
    }

    /// <summary>
    /// When an OPTIONAL rider is spent (a first_hit_per_turn rider, an every-hit rider with a resource or an action
    /// cost, Savage Attacker, a condition on hit). A rider that is always applied has no policy.
    /// </summary>
    public static class Policies
    {
        /// <summary>Spend on a hit whenever allowed (the default).</summary>
        public const string AnyHit = "any_hit";

        public const string CritsOnly = "crits_only";

        /// <summary>On a crit; on a normal hit only when it is the turn's last eligible attack.</summary>
        public const string CritOrLast = "crit_or_last";

        /// <summary>Backward induction maximising E[damage] − use_value × uses.</summary>
        public const string Optimal = "optimal";

        public static readonly DslValueSet Set = new("policy", [AnyHit, CritsOnly, CritOrLast, Optimal]);
    }

    /// <summary>damage_die_remap's remaps.</summary>
    public static class Remaps
    {
        /// <summary>2014 Great Weapon Fighting: reroll a 1 or 2 once, and use the new roll.</summary>
        public const string Gwf2014 = "gwf2014";

        /// <summary>2024 Great Weapon Fighting: a 1 or 2 counts as a 3.</summary>
        public const string Gwf2024 = "gwf2024";

        /// <summary>Elemental Adept: a 1 counts as a 2 on dice of its damage type.</summary>
        public const string ElementalAdept = "elemental_adept";

        public static readonly DslValueSet Set = new("remap", [Gwf2014, Gwf2024, ElementalAdept]);

        /// <summary>The Great Weapon Fighting remap of an edition.</summary>
        public static string GreatWeaponFighting(string edition) => edition == Editions.E2014 ? Gwf2014 : Gwf2024;

        public static bool IsGreatWeaponFighting(string remap) => remap is Gwf2014 or Gwf2024;
    }

    /// <summary>extra_attack's action economy.</summary>
    public static class ExtraAttackActions
    {
        /// <summary>A second Attack action's attacks (Action Surge); needs a resource.</summary>
        public const string Action = "action";

        public const string BonusAction = "bonus_action";

        /// <summary>An attack on another creature's turn (Opportunity Attack, Sentinel), with a per-round chance.</summary>
        public const string Reaction = "reaction";

        public static readonly DslValueSet Set = new("extra attack action", [Action, BonusAction, Reaction]);
    }

    /// <summary>What must happen earlier in the turn for a bonus_action extra_attack.</summary>
    public static class Triggers
    {
        public const string Always = "always";

        /// <summary>Any hit this turn.</summary>
        public const string Hit = "hit";

        /// <summary>A crit with a melee weapon this turn (2014 GWM's bonus attack, 2024 Hew).</summary>
        public const string Crit = "crit";

        public static readonly DslValueSet Set = new("trigger", [Always, Hit, Crit]);
    }

    /// <summary>power_attack's policy: decided per turn by expected damage, or fixed.</summary>
    public static class PowerAttackPolicies
    {
        public const string Auto = "auto";
        public const string Always = "always";
        public const string Never = "never";

        public static readonly DslValueSet Set = new("power attack policy", [Auto, Always, Never]);
    }

    /// <summary>A save effect's damage on a successful save.</summary>
    public static class OnSuccess
    {
        public const string Half = "half";
        public const string None = "none";

        public static readonly DslValueSet Set = new("on_success", [Half, None]);
    }

    /// <summary>
    /// Area shapes and the DMG 2014 p. 249 "Targets in Areas of Effect" rule of thumb (the only source; single-source,
    /// so results say "±1d3 for how bunched the creatures are").
    /// </summary>
    public static class Shapes
    {
        public const string Cone = "cone";
        public const string Cube = "cube";
        public const string Cylinder = "cylinder";
        public const string Line = "line";
        public const string Sphere = "sphere";

        public static readonly DslValueSet Set = new("shape", [Cone, Cube, Cylinder, Line, Sphere]);

        /// <summary>
        /// Creatures an area of <paramref name="sizeFeet"/> covers by the DMG table: cone length ÷ 10, cube side ÷ 5,
        /// cylinder radius ÷ 5, line length ÷ 30, sphere radius ÷ 5, rounded up, at least 1. Fireball (sphere 20) → 4.
        /// </summary>
        public static int Targets(string shape, int sizeFeet)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(sizeFeet, 1);
            var divisor = shape switch
            {
                Cone => 10,
                Cube or Cylinder or Sphere => 5,
                Line => 30,
                _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Not an area shape."),
            };

            return Math.Max(1, (sizeFeet + divisor - 1) / divisor);
        }
    }

    /// <summary>
    /// Conditions. Six change the maths (advantage, auto-crits, auto-failed saves); dodging is a target state only; the
    /// rest of the SRD conditions are accepted on a save effect as labels, with a note that nothing models them.
    /// </summary>
    public static class Conditions
    {
        public const string Prone = "prone";
        public const string Restrained = "restrained";
        public const string Blinded = "blinded";
        public const string Stunned = "stunned";
        public const string Paralyzed = "paralyzed";
        public const string Unconscious = "unconscious";

        /// <summary>The target took the Dodge action: attacks against it have Disadvantage, Dex saves Advantage.</summary>
        public const string Dodging = "dodging";

        public static readonly IReadOnlyList<string> Mechanical = [Prone, Restrained, Blinded, Stunned, Paralyzed, Unconscious];

        /// <summary>SRD conditions with no effect on damage dealt here; a save effect may name one as a label.</summary>
        public static readonly IReadOnlyList<string> LabelOnly =
            ["charmed", "deafened", "exhaustion", "frightened", "grappled", "incapacitated", "invisible", "petrified", "poisoned"];

        /// <summary>condition_on_hit: only conditions that change later attacks or saves.</summary>
        public static readonly DslValueSet OnHitSet = new("condition", Mechanical);

        /// <summary>save_effect: the mechanical six, or a label-only SRD condition.</summary>
        public static readonly DslValueSet SaveEffectSet = new("condition", [.. Mechanical, .. LabelOnly]);

        /// <summary>A target's starting condition.</summary>
        public static readonly DslValueSet TargetSet = new("condition", [.. Mechanical, Dodging]);

        public static bool IsMechanical(string condition) => Mechanical.Contains(condition);
    }

    /// <summary>A modifier's first-round cost (casting Hex, Bless).</summary>
    public static class Setup
    {
        public const string BonusAction = "bonus_action";
        public const string Action = "action";

        public static readonly DslValueSet Set = new("setup", [BonusAction, Action]);
    }

    /// <summary>What spending a rider or using a save effect costs.</summary>
    public static class ActionCosts
    {
        public const string Action = "action";
        public const string BonusAction = "bonus_action";
        public const string None = "none";

        /// <summary>extra_damage: spending it uses the Bonus Action (2024 Divine Smite); otherwise it costs nothing.</summary>
        public static readonly DslValueSet RiderSet = new("action_cost", [BonusAction]);

        public static readonly DslValueSet SaveEffectSet = new("action_cost", [Action, BonusAction, None]);
    }

    /// <summary>When a resource comes back.</summary>
    public static class Rests
    {
        public const string ShortRest = "short_rest";
        public const string LongRest = "long_rest";

        public static readonly DslValueSet Set = new("rest", [ShortRest, LongRest]);
    }

    /// <summary>fighting_style sugar (expanded by <see cref="BuildResolver"/>, edition-aware).</summary>
    public static class FightingStyles
    {
        public const string Gwf = "gwf";
        public const string Archery = "archery";
        public const string Dueling = "dueling";
        public const string Twf = "twf";

        private static readonly Dictionary<string, string> Names = new()
        {
            ["great weapon fighting"] = Gwf,
            ["two weapon fighting"] = Twf,
        };

        public static readonly DslValueSet Set = new("fighting style", [Gwf, Archery, Dueling, Twf], Names);

        /// <summary>The style's name as results show it.</summary>
        public static string Display(string style) => style switch
        {
            Gwf => "Great Weapon Fighting",
            Archery => "Archery",
            Dueling => "Dueling",
            Twf => "Two-Weapon Fighting",
            _ => throw new ArgumentOutOfRangeException(nameof(style), style, "Not a fighting style."),
        };
    }

    /// <summary>Ready-made builds (<see cref="BuildPresets"/>).</summary>
    public static class Presets
    {
        public const string WarlockBaseline = "warlock_baseline";

        public static readonly DslValueSet Set = new("preset", [WarlockBaseline]);
    }

    /// <summary>Target cover: +2 (half) or +5 (three-quarters) to AC and Dex saves.</summary>
    public static class Cover
    {
        public const string Half = "half";
        public const string ThreeQuarters = "three_quarters";

        public static readonly DslValueSet Set = new("cover", [Half, ThreeQuarters]);

        public static int Bonus(string? cover) => cover switch
        {
            null => 0,
            Half => 2,
            ThreeQuarters => 5,
            _ => throw new ArgumentOutOfRangeException(nameof(cover), cover, "Not a cover level."),
        };
    }

    /// <summary>An advantage modifier's direction.</summary>
    public static class AdvantageModes
    {
        public const string Advantage = "advantage";
        public const string Disadvantage = "disadvantage";

        public static readonly DslValueSet Set = new("mode", [Advantage, Disadvantage]);
    }
}
