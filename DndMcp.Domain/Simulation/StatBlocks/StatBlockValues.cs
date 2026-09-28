using DndMcp.Domain.Features;

namespace DndMcp.Domain.Simulation;

/// <summary>
/// The vocabulary of a normalized monster stat block (<see cref="StatBlock"/>): every discriminator the normalizer
/// writes and the simulator, the combatant view and the DPR target switch on, as string constants.
///
/// <para>
/// <b>Strings, not enums</b>, as in <see cref="DslValues"/>: stat blocks are rendered to the model
/// (<c>rules_get format "combatant"</c>), echoed in simulation results and, from Phase 7, snapshotted into prepared
/// encounters. A stored snapshot must mean the same thing after a refactor. Add values; never rename one.
/// </para>
/// <para>
/// Abilities and damage types are the DSL's (<see cref="DslValues.Abilities"/>, <see cref="DslValues.DamageTypes"/>),
/// so a PC's build and a monster's stat block can never disagree about what "fire" or "dex" is.
/// </para>
/// </summary>
public static class StatBlockValues
{
    /// <summary>What an action does when the simulator uses it (<see cref="StatBlockAction.Kind"/>).</summary>
    public static class ActionKinds
    {
        /// <summary>One attack roll (weapon or spell) against one creature: <see cref="StatBlockAction.AttackBonus"/>, damage on a hit, <see cref="StatBlockAction.OnHit"/> riders.</summary>
        public const string Attack = "attack";

        /// <summary>A saving throw against one or more creatures (breath weapons, most spells): <see cref="StatBlockAction.Save"/>, damage on a failed save.</summary>
        public const string Save = "save";

        /// <summary>Damage with no roll to hit and no save (Magic Missile's darts), one creature per <see cref="StatBlockAction.Targets"/>.</summary>
        public const string AutoHit = "auto_hit";

        /// <summary>Restores hit points to one ally (or itself) per <see cref="StatBlockAction.Targets"/>: <see cref="StatBlockAction.Healing"/>.</summary>
        public const string Heal = "heal";

        /// <summary>
        /// A reaction that adds <see cref="StatBlockAction.AcBonus"/> to AC against one attack that would hit (Parry, the
        /// Shield spell). Only meaningful in <see cref="StatBlock.Reactions"/>.
        /// </summary>
        public const string Parry = "parry";

        /// <summary>
        /// Uses other actions by name (<see cref="StatBlockAction.Uses"/>): a legendary action that "makes one Rend attack"
        /// or "uses Spellcasting to cast Scorching Ray". Multiattack is NOT this kind; it is <see cref="StatBlock.Multiattacks"/>.
        /// </summary>
        public const string UseActions = "use_actions";

        /// <summary>
        /// Something the simulator does not do: every such action carries a <see cref="NormalizationWarning"/> (or, when it
        /// has no effect in a fight without a grid, a note) saying what is left out. Never silently dropped.
        /// </summary>
        public const string NotModelled = "not_modelled";

        public static readonly IReadOnlyList<string> All = [Attack, Save, AutoHit, Heal, Parry, UseActions, NotModelled];
    }

    /// <summary>Which of the stat block's lists an action came from (<see cref="StatBlockAction.Slot"/>).</summary>
    public static class ActionSlots
    {
        public const string Action = "action";
        public const string BonusAction = "bonus_action";
        public const string Reaction = "reaction";
        public const string Legendary = "legendary";

        public static readonly IReadOnlyList<string> All = [Action, BonusAction, Reaction, Legendary];
    }

    /// <summary>How an attack reaches its target. There is no grid: melee attacks need the target in reach of an engagement.</summary>
    public static class AttackRanges
    {
        public const string Melee = "melee";
        public const string Ranged = "ranged";

        public static readonly IReadOnlyList<string> All = [Melee, Ranged];
    }

    /// <summary>
    /// Areas of effect. The first five are the DSL's (<see cref="DslValues.Shapes"/>); <see cref="Emanation"/> is 2024's
    /// "N-foot Emanation" (an area around the creature), counted like a sphere of that radius.
    /// </summary>
    public static class Shapes
    {
        public const string Cone = DslValues.Shapes.Cone;
        public const string Cube = DslValues.Shapes.Cube;
        public const string Cylinder = DslValues.Shapes.Cylinder;
        public const string Line = DslValues.Shapes.Line;
        public const string Sphere = DslValues.Shapes.Sphere;
        public const string Emanation = "emanation";

        public static readonly IReadOnlyList<string> All = [Cone, Cube, Cylinder, Line, Sphere, Emanation];
    }

    /// <summary>What a successful save does. A save that "ends the effect" or has no damage is <see cref="None"/>.</summary>
    public static class OnSuccess
    {
        public const string Half = DslValues.OnSuccess.Half;
        public const string None = DslValues.OnSuccess.None;

        public static readonly IReadOnlyList<string> All = [Half, None];
    }

    /// <summary>How often an action can be used (<see cref="UsageSpec.Kind"/>).</summary>
    public static class UsageKinds
    {
        public const string AtWill = "at_will";

        /// <summary>Recharge on a d6 roll of <see cref="UsageSpec.RechargeMin"/> or higher at the start of each of its turns.</summary>
        public const string Recharge = "recharge";

        /// <summary><see cref="UsageSpec.Uses"/> per day, which in one fight means that many uses in total.</summary>
        public const string PerDay = "per_day";

        /// <summary>Uses a shared pool (<see cref="UsageSpec.Pool"/>): a 2014 caster's spell slots of one level.</summary>
        public const string Pool = "pool";

        public static readonly IReadOnlyList<string> All = [AtWill, Recharge, PerDay, Pool];
    }

    /// <summary>
    /// Every SRD condition, both editions. <see cref="Mechanical"/> are the ones the simulator applies; the others are
    /// accepted from the data and reported, with no effect on the fight (a <see cref="NormalizationWarning"/> says so
    /// where an action imposes one).
    /// </summary>
    public static class Conditions
    {
        public const string Blinded = "blinded";
        public const string Charmed = "charmed";
        public const string Deafened = "deafened";
        public const string Exhaustion = "exhaustion";
        public const string Frightened = "frightened";
        public const string Grappled = "grappled";
        public const string Incapacitated = "incapacitated";
        public const string Invisible = "invisible";
        public const string Paralyzed = "paralyzed";
        public const string Petrified = "petrified";
        public const string Poisoned = "poisoned";
        public const string Prone = "prone";
        public const string Restrained = "restrained";
        public const string Stunned = "stunned";
        public const string Unconscious = "unconscious";

        public static readonly IReadOnlyList<string> All =
        [
            Blinded, Charmed, Deafened, Exhaustion, Frightened, Grappled, Incapacitated, Invisible, Paralyzed, Petrified,
            Poisoned, Prone, Restrained, Stunned, Unconscious,
        ];

        /// <summary>Conditions that change attacks, saves or turns in the simulator. Deafened has no combat effect without a grid.</summary>
        public static readonly IReadOnlyList<string> Mechanical =
        [
            Blinded, Charmed, Exhaustion, Frightened, Grappled, Incapacitated, Invisible, Paralyzed, Petrified, Poisoned,
            Prone, Restrained, Stunned, Unconscious,
        ];
    }

    /// <summary>
    /// How long an imposed condition lasts (<see cref="ConditionEffect.Duration"/>). "Source" is the creature that
    /// imposed it; "target" the creature that has it.
    /// </summary>
    public static class Durations
    {
        /// <summary>Until the start of the source's next turn (2024 Stunning Strike, many 2024 riders).</summary>
        public const string UntilStartOfSourceTurn = "until_start_of_source_turn";

        /// <summary>Until the end of the source's next turn (2014 Stunning Strike).</summary>
        public const string UntilEndOfSourceTurn = "until_end_of_source_turn";

        /// <summary>
        /// The target repeats the save (<see cref="ConditionEffect.SaveEnds"/>) at the end of each of its turns, ending the
        /// condition on a success. With <see cref="ConditionEffect.Rounds"/> it also ends after that many rounds.
        /// </summary>
        public const string SaveEnds = "save_ends";

        /// <summary>Lasts <see cref="ConditionEffect.Rounds"/> rounds (1 minute = 10), counted at the end of the source's turn.</summary>
        public const string Rounds = "rounds";

        /// <summary>Grappled or Restrained until the target escapes (<see cref="ConditionEffect.EscapeDc"/>) or the source is incapacitated or dies.</summary>
        public const string UntilEscape = "until_escape";

        /// <summary>Until the target stands up (Prone): it stands at the start of its turn if its Speed is not 0.</summary>
        public const string UntilStands = "until_stands";

        /// <summary>For the rest of the fight (no stated end, or longer than any fight).</summary>
        public const string Fight = "fight";

        /// <summary>
        /// Until the end of the TARGET's own next turn (2024 "the target has the Incapacitated condition until the end
        /// of its next turn", 31 stat block clauses). Added by the normalizer (Phase 5): read as
        /// <see cref="UntilEndOfSourceTurn"/> it would outlast the target's turn by every creature acting between the two,
        /// and read as a round count it would depend on where the source sits in the initiative order. The condition
        /// covers the target's next turn exactly and ends when that turn ends.
        /// </summary>
        public const string UntilEndOfTargetTurn = "until_end_of_target_turn";

        /// <summary>
        /// Until the start of the TARGET's next turn after the one in which it was imposed: the auras that re-impose a
        /// condition each turn (2014 ghast Stench "poisoned until the start of its next turn", 2024 Fear Aura). An aura
        /// imposes it at the start of the target's turn, so it covers that whole turn and ends as the next one begins;
        /// imposed on another creature's turn, it ends before the target acts and matters only for what happens to the
        /// target before then.
        /// </summary>
        public const string UntilStartOfTargetTurn = "until_start_of_target_turn";

        /// <summary>Every duration, for tests and for whoever switches on them.</summary>
        public static readonly IReadOnlyList<string> All =
        [
            UntilStartOfSourceTurn, UntilEndOfSourceTurn, SaveEnds, Rounds, UntilEscape, UntilStands, Fight,
            UntilEndOfTargetTurn, UntilStartOfTargetTurn,
        ];
    }

    /// <summary>
    /// Resistance and immunity qualifiers (<see cref="DamageAdjustment.Qualifier"/>). 2014 writes them into the damage
    /// text ("bludgeoning, piercing, and slashing from nonmagical attacks that aren't silvered"); an unqualified entry
    /// always applies.
    /// </summary>
    public static class DamageQualifiers
    {
        /// <summary>Applies only to damage from nonmagical attacks.</summary>
        public const string Nonmagical = "nonmagical";

        /// <summary>Nonmagical attacks that aren't silvered (lycanthropes, some devils).</summary>
        public const string NonmagicalNotSilvered = "nonmagical_not_silvered";

        /// <summary>Nonmagical attacks that aren't adamantine (golems).</summary>
        public const string NonmagicalNotAdamantine = "nonmagical_not_adamantine";

        /// <summary>
        /// A qualifier the simulator does not read ("from magic weapons wielded by good creatures"): on a resistance or
        /// immunity it applies always, with a warning. The normalizer never emits it on a vulnerability (it leaves such an
        /// entry out with a warning), because applying it always would double every such hit.
        /// </summary>
        public const string Other = "other";

        public static readonly IReadOnlyList<string> All = [Nonmagical, NonmagicalNotSilvered, NonmagicalNotAdamantine, Other];
    }

    /// <summary>On-hit rider kinds (<see cref="ActionEffect.Kind"/>).</summary>
    public static class EffectKinds
    {
        /// <summary>Extra damage on every hit ("plus 7 (2d6) fire damage"), doubled dice on a crit like the attack's own.</summary>
        public const string Damage = "damage";

        /// <summary>The target makes a save: damage (half or none on a success) and/or a condition on a failure.</summary>
        public const string Save = "save";

        /// <summary>A condition with no save ("the target is grappled (escape DC 13)"), optionally limited by size.</summary>
        public const string Condition = "condition";

        public static readonly IReadOnlyList<string> All = [Damage, Save, Condition];
    }

    /// <summary>
    /// Trait kinds the simulator implements (<see cref="StatBlockTrait.Kind"/>), plus <see cref="NoCombatEffect"/> and
    /// <see cref="NotModelled"/>. The normalizer classifies EVERY trait into one of these; nothing is dropped unsaid.
    /// </summary>
    public static class TraitKinds
    {
        /// <summary>Advantage on saves against spells and other magical effects.</summary>
        public const string MagicResistance = "magic_resistance";

        /// <summary>Advantage on an attack when an ally is within 5 ft of the target (an ally in the same engagement).</summary>
        public const string PackTactics = "pack_tactics";

        /// <summary>Uses per day: turn a failed save into a success. Also on <see cref="StatBlock.LegendaryResistance"/>.</summary>
        public const string LegendaryResistance = "legendary_resistance";

        /// <summary>Regains <see cref="StatBlockTrait.Amount"/> HP at the start of its turn unless it took <see cref="StatBlockTrait.DamageTypes"/> damage since its last turn.</summary>
        public const string Regeneration = "regeneration";

        /// <summary>Damage that drops it to 0 HP: Con save DC 5 + damage taken (not radiant, not a crit) to drop to 1 HP instead.</summary>
        public const string UndeadFortitude = "undead_fortitude";

        /// <summary>Once per fight, damage of at most <see cref="StatBlockTrait.Amount"/> that would drop it to 0 HP leaves it at 1 HP.</summary>
        public const string Relentless = "relentless";

        /// <summary>A Dex save for half takes none on a success and half on a failure.</summary>
        public const string Evasion = "evasion";

        /// <summary>Once per turn, <see cref="StatBlockTrait.Dice"/> extra damage on a hit with Advantage or with an ally engaged with the target.</summary>
        public const string SneakAttack = "sneak_attack";

        /// <summary>Once per turn, <see cref="StatBlockTrait.Dice"/> extra damage on a hit when an ally is engaged with the target (2014 hobgoblin).</summary>
        public const string MartialAdvantage = "martial_advantage";

        /// <summary>Advantage on melee attacks against a creature missing any hit points.</summary>
        public const string BloodFrenzy = "blood_frenzy";

        /// <summary>Advantage on its own attacks while it has at most half its hit points (2024 "Bloodied" traits).</summary>
        public const string AdvantageWhileBloodied = "advantage_while_bloodied";

        /// <summary>Advantage on its melee attacks during its turn; attacks against it have Advantage until its next turn.</summary>
        public const string Reckless = "reckless";

        /// <summary>Its weapon attacks are magical (they overcome "nonmagical" resistance).</summary>
        public const string MagicWeapons = "magic_weapons";

        /// <summary>A creature that hits it with a melee attack takes <see cref="StatBlockTrait.Damage"/>.</summary>
        public const string RetaliationDamage = "retaliation_damage";

        /// <summary>
        /// Each enemy engaged with it takes <see cref="StatBlockTrait.Damage"/> (with <see cref="StatBlockTrait.Save"/> if any)
        /// at the start or end of its turn. An aura with no damage imposes <see cref="StatBlockTrait.Condition"/> on a failed
        /// <see cref="StatBlockTrait.Save"/> instead (Stench: poisoned; Fear Aura: frightened), within <see cref="StatBlockTrait.Area"/>.
        /// </summary>
        public const string AuraDamage = "aura_damage";

        /// <summary>When it dies: <see cref="StatBlockTrait.Save"/> + <see cref="StatBlockTrait.Damage"/> + <see cref="StatBlockTrait.Condition"/> on creatures in <see cref="StatBlockTrait.Area"/>.</summary>
        public const string DeathBurst = "death_burst";

        /// <summary>No effect in a fight without a grid, light, terrain or senses (Amphibious, Keen Smell, Spider Climb, …). A note, not a warning.</summary>
        public const string NoCombatEffect = "no_combat_effect";

        /// <summary>Changes a fight but is not simulated: carries a <see cref="NormalizationWarning"/> naming it.</summary>
        public const string NotModelled = "not_modelled";

        public static readonly IReadOnlyList<string> All =
        [
            MagicResistance, PackTactics, LegendaryResistance, Regeneration, UndeadFortitude, Relentless, Evasion, SneakAttack,
            MartialAdvantage, BloodFrenzy, AdvantageWhileBloodied, Reckless, MagicWeapons, RetaliationDamage, AuraDamage,
            DeathBurst, NoCombatEffect, NotModelled,
        ];
    }

    /// <summary>
    /// <see cref="NormalizationWarning.Code"/> values. Tests pin the count of each per edition, so a re-vendor or a
    /// normalizer change that alters what is understood shows up as a deliberate diff.
    /// </summary>
    public static class WarningCodes
    {
        /// <summary>A trait, action or rider that changes a fight is not simulated.</summary>
        public const string NotModelled = "not_modelled";

        /// <summary>Simulated, but simplified (a variable count taken as fixed, a movement-based trait applied once).</summary>
        public const string Approximated = "approximated";

        /// <summary>A multiattack or legendary action names an action or spell the stat block does not have.</summary>
        public const string UnresolvedReference = "unresolved_reference";

        /// <summary>The prose and the structured data disagree, and the data was used (or an override resolved it).</summary>
        public const string DataConflict = "data_conflict";

        /// <summary>A value the prose gives could not be read, so a default was used (named in the message).</summary>
        public const string Unparsed = "unparsed";

        public static readonly IReadOnlyList<string> All = [NotModelled, Approximated, UnresolvedReference, DataConflict, Unparsed];
    }
}
