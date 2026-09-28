using DndMcp.Domain.Encounters;
using DndMcp.Domain.Features;

namespace DndMcp.Domain.Simulation;

/// <summary>
/// A monster stat block as the simulator reads it: every number concrete, every action classified, every trait
/// either implemented, known to have no combat effect, or warned about. Produced by the Phase 5 normalizer
/// (<c>DndMcp.Repository.Srd.Combatants.MonsterNormalizer</c>) from a corrected srd.db document plus
/// <c>content/overrides/monsters.{edition}.json</c>; consumed by the simulator, by <c>rules_get format "combatant"</c>
/// and by <c>balance_dpr</c>'s <c>target.monster</c>.
///
/// <para>
/// <b>Why a separate model instead of the typed upstream records.</b> The upstream records keep each edition's quirks
/// (string multiattack counts, damage arrays mixing riders with on-hit damage, spells whose <c>level</c> means different
/// things per edition, legendary costs hidden in names, flat 2024 damage only in prose). Every consumer re-reading those
/// quirks would re-decide them, and differently. The normalizer decides each one once, records what it decided in
/// <see cref="Notes"/> and <see cref="Warnings"/>, and everything downstream reads one edition-neutral shape.
/// </para>
/// <para>
/// <b>Nothing is dropped silently.</b> An action or trait the simulator cannot run stays in its list with kind
/// <see cref="StatBlockValues.ActionKinds.NotModelled"/> / <see cref="StatBlockValues.TraitKinds.NotModelled"/> and a
/// warning, so the combatant view and every simulation result can say what the numbers leave out. A stat block whose
/// every action is not modelled is still a valid combatant (it takes the Dodge action).
/// </para>
/// </summary>
public sealed record StatBlock
{
    /// <summary>The srd.db ref, e.g. <c>2024/monster/adult-red-dragon</c>.</summary>
    public required string Ref { get; init; }

    public required string Name { get; init; }

    /// <summary><see cref="DslValues.Editions"/>: "2014" or "2024". Decides surprise, exhaustion, concentration's DC cap.</summary>
    public required string Edition { get; init; }

    /// <summary>As the data writes it: "Huge", "Medium or Small". Size limits on grapples ("Large or smaller") read it.</summary>
    public required string Size { get; init; }

    /// <summary>"dragon", "humanoid", …</summary>
    public required string CreatureType { get; init; }

    public required ChallengeRating ChallengeRating { get; init; }

    public required int Xp { get; init; }

    /// <summary>2024 in-lair XP; its presence is also the "has a lair" signal (Legendary Resistance and actions gain a use).</summary>
    public int? XpInLair { get; init; }

    public required int ProficiencyBonus { get; init; }

    /// <summary>The default AC (the first entry). Other ACs (with Mage Armor, while prone) are in <see cref="ArmorClassNote"/>.</summary>
    public required int ArmorClass { get; init; }

    /// <summary>"17 with Mage Armor" and similar, or null. The simulator uses <see cref="ArmorClass"/>.</summary>
    public string? ArmorClassNote { get; init; }

    /// <summary>The average hit points the stat block prints.</summary>
    public required int HitPoints { get; init; }

    /// <summary>The hit-point roll, e.g. <c>19d12+133</c> (no spaces), for rolled HP.</summary>
    public required DamageFormula HitDice { get; init; }

    public required ResolvedAbilities Abilities { get; init; }

    /// <summary>Total save bonus for every ability key (<see cref="DslValues.Abilities.All"/>): proficient ones from the data, the rest the modifier.</summary>
    public required IReadOnlyDictionary<string, int> SaveBonuses { get; init; }

    /// <summary>
    /// Initiative modifier. 2014: the Dex modifier. 2024 prints one (sometimes with proficiency added) that the data
    /// lacks; the overrides file carries it, read from the SRD 5.2 markdown.
    /// </summary>
    public required int InitiativeBonus { get; init; }

    /// <summary>Speeds in feet by mode (<c>walk</c>, <c>fly</c>, <c>swim</c>, <c>climb</c>, <c>burrow</c>); a 0 walk speed means it cannot move.</summary>
    public required IReadOnlyDictionary<string, int> Speeds { get; init; }

    public bool Hovers { get; init; }

    public required IReadOnlyList<DamageAdjustment> Resistances { get; init; }

    public required IReadOnlyList<DamageAdjustment> Immunities { get; init; }

    public required IReadOnlyList<DamageAdjustment> Vulnerabilities { get; init; }

    /// <summary><see cref="StatBlockValues.Conditions"/> values it cannot have.</summary>
    public required IReadOnlyList<string> ConditionImmunities { get; init; }

    /// <summary>Every trait, classified (<see cref="StatBlockValues.TraitKinds"/>), in stat-block order.</summary>
    public required IReadOnlyList<StatBlockTrait> Traits { get; init; }

    /// <summary>Actions other than Multiattack and Spellcasting, in stat-block order.</summary>
    public required IReadOnlyList<StatBlockAction> Actions { get; init; }

    public required IReadOnlyList<StatBlockAction> BonusActions { get; init; }

    public required IReadOnlyList<StatBlockAction> Reactions { get; init; }

    /// <summary>
    /// The combat spells its spellcasting offers, each an action (kind attack, save, auto_hit or heal) with
    /// <see cref="StatBlockAction.IsSpell"/> set, its <see cref="StatBlockAction.Slot"/> the list the Spellcasting
    /// entry sits in, and its usage (at will, per day, or a slot pool). Spells with no combat effect are not listed here;
    /// a note names them.
    /// </summary>
    public required IReadOnlyList<StatBlockAction> Spells { get; init; }

    /// <summary>2014 slot casters: spell slots per spell level (the pools <c>slot:1</c> … <c>slot:9</c>). Empty otherwise.</summary>
    public required IReadOnlyDictionary<int, int> SpellSlots { get; init; }

    /// <summary>
    /// The Multiattack action's routines, as alternatives (the policy takes the best each turn). Empty when it has none.
    /// Step names are resolved: each names an entry of <see cref="Actions"/> or <see cref="Spells"/>.
    /// </summary>
    public required IReadOnlyList<MultiattackRoutine> Multiattacks { get; init; }

    /// <summary>Legendary actions, or null for a creature without them.</summary>
    public LegendaryActions? Legendary { get; init; }

    /// <summary>Legendary Resistance uses per day (0 when it has none).</summary>
    public int LegendaryResistance { get; init; }

    /// <summary>Legendary Resistance uses in its lair (2024 "3/Day, or 4/Day in Lair"), or null when the same.</summary>
    public int? LegendaryResistanceInLair { get; init; }

    /// <summary>
    /// The refs of this creature's other stat blocks (a shapechanger's forms: 2014 and 2024 lycanthropes and vampires
    /// are three records each, linked by the data's <c>forms</c>). Empty for every other monster. Added by the normalizer
    /// (Phase 5): statistics over "all monsters" count each form group once (<c>MonsterStatsEmpirical</c>), and a
    /// combatant view can point at the other forms.
    /// </summary>
    public IReadOnlyList<string> Forms { get; init; } = [];

    /// <summary>What was decided about the data: overrides applied, defaults used, spells left out as having no combat effect.</summary>
    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>What the simulator leaves out or simplifies for this creature. Shown with every result that uses it.</summary>
    public required IReadOnlyList<NormalizationWarning> Warnings { get; init; }

    /// <summary>The modifier of an ability key.</summary>
    public int Modifier(string ability) => Abilities.Modifier(ability);

    /// <summary>The trait of a kind, or null.</summary>
    public StatBlockTrait? Trait(string kind) => Traits.FirstOrDefault(t => t.Kind == kind);

    public bool Has(string traitKind) => Traits.Any(t => t.Kind == traitKind);

    /// <summary>An action or spell by name (case ignored), searching actions, bonus actions, reactions, spells, then legendary actions.</summary>
    public StatBlockAction? FindAction(string name) =>
        Actions.Concat(BonusActions).Concat(Reactions).Concat(Spells).Concat(Legendary?.Actions ?? [])
            .FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// One resistance, immunity or vulnerability: a damage type and the qualifier the text puts on it
/// (<see cref="StatBlockValues.DamageQualifiers"/>, null when unqualified). "Bludgeoning, piercing, and slashing from
/// nonmagical attacks" is three entries, each qualified <c>nonmagical</c>. Splitting that text on commas without the
/// qualifier would make a werewolf immune to every weapon (a known bug in another dataset).
/// </summary>
/// <param name="Text">The data's own words for this entry, for display.</param>
public sealed record DamageAdjustment(string DamageType, string? Qualifier, string Text)
{
    /// <summary>
    /// Whether it applies to damage from an attack or effect with these properties. Unqualified and
    /// <see cref="StatBlockValues.DamageQualifiers.Other"/> entries always apply.
    /// </summary>
    public bool AppliesTo(bool magical, bool silvered, bool adamantine) => Qualifier switch
    {
        StatBlockValues.DamageQualifiers.Nonmagical => !magical,
        StatBlockValues.DamageQualifiers.NonmagicalNotSilvered => !magical && !silvered,
        StatBlockValues.DamageQualifiers.NonmagicalNotAdamantine => !magical && !adamantine,
        _ => true,
    };
}

/// <summary>A trait, classified. Only the fields its <see cref="Kind"/> uses are set.</summary>
public sealed record StatBlockTrait
{
    public required string Name { get; init; }

    /// <summary><see cref="StatBlockValues.TraitKinds"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>Regeneration's HP, Relentless's threshold.</summary>
    public int? Amount { get; init; }

    /// <summary>Sneak Attack's and Martial Advantage's extra damage dice.</summary>
    public DamageFormula? Dice { get; init; }

    /// <summary>Regeneration: the damage types that stop it for a turn.</summary>
    public IReadOnlyList<string> DamageTypes { get; init; } = [];

    /// <summary>Damage dealt by retaliation, aura and death-burst traits.</summary>
    public IReadOnlyList<DamageRoll> Damage { get; init; } = [];

    public SaveSpec? Save { get; init; }

    public AreaSpec? Area { get; init; }

    public ConditionEffect? Condition { get; init; }

    /// <summary>Uses per day (Legendary Resistance).</summary>
    public int? Uses { get; init; }

    /// <summary>The trait's text from the data, for the combatant view and for warnings.</summary>
    public required string Text { get; init; }
}

/// <summary>
/// One action, bonus action, reaction, legendary action or combat spell. Only the fields its <see cref="Kind"/> uses
/// are set; the normalizer's tests pin that every attack has an <see cref="AttackBonus"/> and every save a
/// <see cref="Save"/>.
/// </summary>
public sealed record StatBlockAction
{
    public required string Name { get; init; }

    /// <summary><see cref="StatBlockValues.ActionKinds"/>.</summary>
    public required string Kind { get; init; }

    /// <summary><see cref="StatBlockValues.ActionSlots"/>: which list it is in (what it costs to use).</summary>
    public required string Slot { get; init; }

    /// <summary>Attacks: the total to-hit bonus.</summary>
    public int? AttackBonus { get; init; }

    /// <summary>Attacks: <see cref="StatBlockValues.AttackRanges"/>. An attack usable both ways ("Melee or Ranged Attack Roll") is melee.</summary>
    public string? Range { get; init; }

    /// <summary>Attacks usable at range too (thrown weapons, "Melee or Ranged"): the policy may throw from the back line.</summary>
    public bool AlsoRanged { get; init; }

    /// <summary>
    /// Creatures affected: 1 for an attack; for a save or auto-hit action without an <see cref="Area"/>, how many it
    /// names ("up to three creatures", Magic Missile's darts); for an area, the DMG's typical count is the engine's job.
    /// </summary>
    public int Targets { get; init; } = 1;

    /// <summary>
    /// Attacks: the attack rolls one use makes, each against a creature the policy picks: Scorching Ray's three rays,
    /// Eldritch Blast's beams. 1 for every weapon attack (a multiattack repeats an attack through its steps instead).
    /// Added by the normalizer (Phase 5) rather than reusing <see cref="Targets"/>, which counts creatures a save or
    /// auto-hit action AFFECTS; one field with two meanings would let a policy read rays as an area.
    /// </summary>
    public int AttackRolls { get; init; } = 1;

    /// <summary>
    /// Attack: damage on a hit (every roll here applies on every hit; the first is the weapon's). Save: damage on a
    /// failed save. Auto-hit: damage per target (per dart).
    /// </summary>
    public IReadOnlyList<DamageRoll> Damage { get; init; } = [];

    /// <summary>Attack riders applied on a hit, after <see cref="Damage"/>.</summary>
    public IReadOnlyList<ActionEffect> OnHit { get; init; } = [];

    /// <summary>Save actions: the save. Attacks carry their saves in <see cref="OnHit"/>.</summary>
    public SaveSpec? Save { get; init; }

    /// <summary>Save actions: the area, or null for a save against <see cref="Targets"/> chosen creatures.</summary>
    public AreaSpec? Area { get; init; }

    /// <summary>Save actions: the condition a failed save imposes, or null.</summary>
    public ConditionEffect? Condition { get; init; }

    /// <summary>
    /// Save actions: further conditions the same failed save imposes alongside <see cref="Condition"/> (the kraken's
    /// Toxic Ink: Blinded AND Poisoned; a swallow: Blinded AND Restrained). Added by the normalizer (Phase 5).
    /// <see cref="Condition"/> is always the most severe of them (paralyzed before poisoned), so a consumer that reads
    /// only it keeps the effect that matters most; reading both is exact. Empty for nearly every action.
    /// </summary>
    public IReadOnlyList<ConditionEffect> ExtraConditions { get; init; } = [];

    /// <summary>Heal actions: HP restored per target.</summary>
    public DamageFormula? Healing { get; init; }

    /// <summary>
    /// Heal actions that restore only the creature itself (2014 unicorn Heal Self). A policy that heals "an ally" must
    /// not spend it on another creature. Added by the normalizer (Phase 5).
    /// </summary>
    public bool SelfOnly { get; init; }

    /// <summary>Parry reactions: the AC added against one attack.</summary>
    public int? AcBonus { get; init; }

    /// <summary>Use-actions kind: what it uses, by resolved name (an entry of the stat block's actions or spells).</summary>
    public IReadOnlyList<ActionUse> Uses { get; init; } = [];

    /// <summary>A spell (its damage is magical; Magic Resistance applies to its saves; it can be counterspelled — not modelled).</summary>
    public bool IsSpell { get; init; }

    /// <summary>Spells: the level it is cast at (2024 data gives the cast level; 2014 the spell's own level). 0 for cantrips.</summary>
    public int? SpellLevel { get; init; }

    /// <summary>Spells that require concentration (the caster concentrates on it; damage may end it).</summary>
    public bool Concentration { get; init; }

    /// <summary>Its damage counts as magical: every spell, and weapon attacks of a creature with Magic Weapons or an attack the text calls magical.</summary>
    public bool Magical { get; init; }

    public UsageSpec Usage { get; init; } = UsageSpec.AtWill;

    /// <summary>Legendary actions: uses it costs (2014 "(Costs 2 Actions)"; 2024 always 1).</summary>
    public int LegendaryCost { get; init; } = 1;

    /// <summary>Legendary actions (2024): "can't take this action again until the start of its next turn".</summary>
    public bool OncePerRound { get; init; }

    /// <summary>
    /// "If a creature's saving throw is successful or the effect ends for it, the creature is immune to the X's Y for the
    /// next 24 hours" (Frightful Presence, Horrifying Visage, Moan, Charm…): after a creature succeeds on this action's
    /// save, or its condition from this action ends, this creature's same action can no longer affect it for the rest
    /// of the fight. Without it the simulator would re-roll the save every turn on creatures that already shook it off.
    /// Added in the Phase 5 fix round.
    /// </summary>
    public bool ImmuneAfterSuccess { get; init; }

    /// <summary>
    /// An outright kill by hit points (2024 Power Word Kill: "If the target has 100 Hit Points or fewer, it dies.
    /// Otherwise, it takes 12d12 Psychic damage"): a target with at most this many hit points dies (it gets no death
    /// saves); otherwise <see cref="Damage"/> applies as usual. Null for everything else. Added in the Phase 5 fix round.
    /// </summary>
    public int? KillAtOrBelowHp { get; init; }

    /// <summary>The data's text, for the combatant view.</summary>
    public required string Text { get; init; }

    /// <summary>What the normalizer decided about this action (an override applied, a default used).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>A damage roll and its type. The type is null only for the rare untyped damage in the data.</summary>
public sealed record DamageRoll(DamageFormula Dice, string? DamageType);

/// <summary>A saving throw: the ability key, its DC and what a success does (<see cref="StatBlockValues.OnSuccess"/>).</summary>
public sealed record SaveSpec(string Ability, int Dc, string OnSuccess);

/// <summary>An area (<see cref="StatBlockValues.Shapes"/>) and its size in feet: a cone's length, a sphere's radius, a line's length.</summary>
public sealed record AreaSpec(string Shape, int Size);

/// <summary>A reference to another action by name, used <see cref="Count"/> times.</summary>
public sealed record ActionUse(string ActionName, int Count);

/// <summary>How often an action can be used; see <see cref="StatBlockValues.UsageKinds"/>.</summary>
/// <param name="RechargeMin">Recharge: the lowest d6 roll that recharges it (5 for "Recharge 5–6").</param>
/// <param name="Uses">Per day: uses per day (a fight is within one day).</param>
/// <param name="Pool">
/// Pool: the key of a shared pool, e.g. <c>slot:3</c> (<see cref="StatBlock.SpellSlots"/>). Recharge: actions with the
/// same key share ONE recharge (a 2014 dragon's Breath Weapons choice becomes one save action per breath, keyed
/// <c>recharge:Breath Weapons</c>): using one spends them all until the recharge roll succeeds. Null for an action
/// with its own recharge.
/// </param>
public sealed record UsageSpec(string Kind, int? RechargeMin = null, int? Uses = null, string? Pool = null)
{
    public static UsageSpec AtWill { get; } = new(StatBlockValues.UsageKinds.AtWill);
}

/// <summary>An attack rider (<see cref="StatBlockValues.EffectKinds"/>).</summary>
public sealed record ActionEffect
{
    public required string Kind { get; init; }

    /// <summary>Damage kind: added on every hit. Save kind: damage on a failed save (half or none on a success per <see cref="Save"/>).</summary>
    public IReadOnlyList<DamageRoll> Damage { get; init; } = [];

    /// <summary>Save kind only.</summary>
    public SaveSpec? Save { get; init; }

    /// <summary>Save kind: imposed on a failed save. Condition kind: imposed on a hit.</summary>
    public ConditionEffect? Condition { get; init; }

    /// <summary>
    /// Further conditions imposed together with <see cref="Condition"/> by the same hit or failed save (a grapple that
    /// also restrains: Grappled, then Restrained "until this grapple ends"). <see cref="Condition"/> is the most severe;
    /// see <see cref="StatBlockAction.ExtraConditions"/>. Added by the normalizer (Phase 5).
    /// </summary>
    public IReadOnlyList<ConditionEffect> ExtraConditions { get; init; } = [];

    /// <summary>The largest size it works on ("Large or smaller" → <c>Large</c>), or null.</summary>
    public string? MaxSize { get; init; }

    /// <summary>
    /// Save kind: a failed save kills a target with at most this many hit points outright (2014 solar Slaying Longbow:
    /// "If the target is a creature that has 100 hit points or fewer, it must succeed on a DC 15 Constitution saving
    /// throw or die"). Null for everything else. Added in the Phase 5 fix round.
    /// </summary>
    public int? KillAtOrBelowHp { get; init; }

    /// <summary>
    /// Save kind: after the target succeeds, or the condition ends, this creature's same rider cannot affect it again
    /// this fight (see <see cref="StatBlockAction.ImmuneAfterSuccess"/>). Added in the Phase 5 fix round.
    /// </summary>
    public bool ImmuneAfterSuccess { get; init; }
}

/// <summary>
/// A condition an action imposes and how it ends (<see cref="StatBlockValues.Durations"/>). A condition imposed
/// through <see cref="SaveEnds"/> ends when the target succeeds on that save at the end of one of its turns.
/// </summary>
public sealed record ConditionEffect
{
    /// <summary><see cref="StatBlockValues.Conditions"/>.</summary>
    public required string Condition { get; init; }

    public required string Duration { get; init; }

    /// <summary>Rounds duration, or a cap on a save-ends condition.</summary>
    public int? Rounds { get; init; }

    /// <summary>Until-escape duration: the escape DC (Athletics or Acrobatics, the target's better).</summary>
    public int? EscapeDc { get; init; }

    /// <summary>Save-ends duration: the repeated save (usually the imposing save's ability and DC).</summary>
    public SaveSpec? SaveEnds { get; init; }

    /// <summary>Ongoing damage at the start of each of the target's turns while it lasts ("burning", swallowed).</summary>
    public IReadOnlyList<DamageRoll> OngoingDamage { get; init; } = [];
}

/// <summary>
/// One Multiattack routine: the fixed <see cref="Steps"/>, then <see cref="Choose"/> more uses picked from
/// <see cref="Options"/> (each pick one use of one option). "Three Rend attacks, one replaceable by Scorching Ray" is
/// Steps [Rend ×2], Choose 1 of [Rend, Scorching Ray]. "Two attacks, Claw or Bite in any combination" is Steps [],
/// Choose 2 of [Claw, Bite].
/// </summary>
public sealed record MultiattackRoutine
{
    /// <summary>"Multiattack", or "Multiattack (option 2)" when the data offers several routines.</summary>
    public required string Label { get; init; }

    public required IReadOnlyList<ActionUse> Steps { get; init; }

    public int Choose { get; init; }

    public IReadOnlyList<ActionUse> Options { get; init; } = [];

    /// <summary>The routine's total uses: the steps' counts plus <see cref="Choose"/>.</summary>
    public int TotalUses => Steps.Sum(s => s.Count) + Choose;
}

/// <summary>Legendary actions: uses per round (reset at the start of its turn) and the actions, each with its cost.</summary>
public sealed record LegendaryActions(int Uses, int? UsesInLair, IReadOnlyList<StatBlockAction> Actions);

/// <summary>
/// Something the simulator leaves out or simplifies for one creature (<see cref="StatBlockValues.WarningCodes"/>).
/// </summary>
/// <param name="Where">What it is about, in the vocabulary of the combatant view: "Fire Breath", "trait Shapechanger", "legendary action Pounce".</param>
/// <param name="Message">One plain sentence: what the text says and what the simulator does instead.</param>
public sealed record NormalizationWarning(string Code, string Where, string Message);
