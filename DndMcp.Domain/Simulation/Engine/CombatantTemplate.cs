using DndMcp.Domain.Dice;
using DndMcp.Domain.Features;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Simulation;

/// <summary>
/// One creature of a run, compiled ONCE from its stat block or build and shared read-only by every fight on every thread;
/// what changes during a fight lives in <see cref="Creature"/>, which is reset cheaply per fight.
///
/// <para>
/// Monsters and builds share this shape so the attack, save and damage pipeline is one: a stat block fills the monster
/// fields (<see cref="Actions"/>, <see cref="Multiattacks"/>, traits), a build fills <see cref="Pc"/>. Everything a
/// policy needs to rank choices quickly (expected damage per round for "threat") is computed here too.
/// </para>
/// </summary>
internal sealed class CombatantTemplate
{
    /// <summary>The creature's index in every fight's creature array.</summary>
    public required int Id { get; init; }

    /// <summary>0 = party, 1 = enemies.</summary>
    public required int Side { get; init; }

    /// <summary>A unique label: "Ogre", "Ogre 2".</summary>
    public required string Label { get; init; }

    public required string Edition { get; init; }

    /// <summary>Makes death saves at 0 HP (party builds, or <see cref="CombatantSpec.DeathSaves"/>); otherwise dies at 0 HP.</summary>
    public required bool PcLike { get; init; }

    public required int AverageHp { get; init; }

    /// <summary>Rolled per fight when set (monsters with enemy_hp "roll").</summary>
    public DamageFormula? RolledHp { get; init; }

    public required int ArmorClass { get; init; }

    /// <summary>Save bonus per ability, in <see cref="DslValues.Abilities.All"/> order.</summary>
    public required int[] Saves { get; init; }

    public required int InitiativeBonus { get; init; }

    public required bool Front { get; init; }

    /// <summary>A flying creature's melee attacks reach the back line.</summary>
    public bool Flies { get; init; }

    /// <summary>Walking speed above 0: it can stand up from Prone.</summary>
    public bool CanStand { get; init; } = true;

    /// <summary>
    /// Per damage type: 0 none, else a <see cref="Qualifier"/> code for the resistance, immunity and vulnerability that
    /// apply (the most general entry wins when the data lists a type twice).
    /// </summary>
    public required int[] Resist { get; init; }

    public required int[] Immune { get; init; }

    public required int[] Vulnerable { get; init; }

    /// <summary>Condition immunities as a bit mask over <see cref="Cond"/>.</summary>
    public int ConditionImmunities { get; init; }

    public string Size { get; init; } = "Medium";

    /// <summary>Ability modifiers the escape check reads (the better of Str and Dex).</summary>
    public int StrMod { get; init; }

    public int DexMod { get; init; }

    // Traits (monsters; a build has none of these).
    public bool MagicResistance { get; init; }

    public bool PackTactics { get; init; }

    public bool Evasion { get; init; }

    public bool BloodFrenzy { get; init; }

    public bool AdvantageWhileBloodied { get; init; }

    public bool Reckless { get; init; }

    public int RegenerationAmount { get; init; }

    /// <summary>Damage types (bits over <see cref="DamageTypes"/>) that stop regeneration for a turn.</summary>
    public int RegenerationStops { get; init; }

    /// <summary>
    /// The troll's rule ("dies only if it starts its turn with 0 hit points and doesn't regenerate"): at 0 HP it is down,
    /// not dead, until its next turn starts. Read from the trait's text; other regeneration needs at least 1 HP.
    /// </summary>
    public bool RegeneratesFromZero { get; init; }

    public bool UndeadFortitude { get; init; }

    public int RelentlessAmount { get; init; }

    public RollPart[] SneakAttack { get; init; } = [];

    public RollPart[] MartialAdvantage { get; init; } = [];

    public TraitEffect? Retaliation { get; init; }

    public TraitEffect? Aura { get; init; }

    public TraitEffect? DeathBurst { get; init; }

    public int LegendaryResistance { get; init; }

    public int LegendaryUses { get; init; }

    public MonsterAction[] LegendaryActions { get; init; } = [];

    /// <summary>What the creature can do with its Action (actions and action spells).</summary>
    public MonsterAction[] Actions { get; init; } = [];

    public MonsterAction[] BonusActions { get; init; } = [];

    /// <summary>
    /// Reactions of kind parry (the only monster reactions used in v1). A spell parry (Shield, <see cref="MonsterAction.IsSpell"/>)
    /// raises the AC until the start of the caster's next turn (<see cref="Creature.ShieldAc"/>); a true Parry covers the
    /// one attack that triggered it.
    /// </summary>
    public MonsterAction[] Parries { get; init; } = [];

    public MultiattackPlan[] Multiattacks { get; init; } = [];

    /// <summary>
    /// The limited-use slots, indexed by <see cref="MonsterAction.UsageSlot"/>: one per recharge or per-day action, and one
    /// per SHARED recharge (a 2014 dragon's breath weapons, <see cref="UsageSpec.Pool"/> "recharge:…"), whose actions all
    /// point at the slot of the first — one d6 roll recharges them, and using one spends them all. Each entry is the
    /// slot's first action (its usage is the slot's).
    /// </summary>
    public MonsterAction[] Limited { get; init; } = [];

    /// <summary>Per limited slot: its name in "resources used" (the action's, or the shared pool's).</summary>
    public string[] LimitedNames { get; init; } = [];

    /// <summary>Spell-slot pools ("slot:3") and their sizes, indexed by <see cref="MonsterAction.PoolSlot"/>.</summary>
    public string[] PoolNames { get; init; } = [];

    public int[] PoolSizes { get; init; } = [];

    /// <summary>The build, for a DSL combatant.</summary>
    public PcBuild? Pc { get; init; }

    /// <summary>A build's temp_hp modifiers: gained at the start of the fight.</summary>
    public int TempHpAtStart { get; init; }

    /// <summary>Expected damage per round against the other side (for the "threat" policy), set after compiling both sides.</summary>
    public double Threat { get; set; }

    /// <summary>Has a heal it can use (for "healer_first").</summary>
    public bool HasHeal { get; init; }

    public StatBlock? StatBlock { get; init; }

    // The dummy harness only.
    public bool InfiniteHp { get; init; }

    /// <summary>Conditions (bits over <see cref="Cond"/>) it has for the whole fight: the harness target's starting condition.</summary>
    public int PermanentConditions { get; init; }

    public bool PermanentDodging { get; init; }

    /// <summary>+AC against attacks that do not ignore cover, and + to Dex saves (the harness target's cover).</summary>
    public int CoverBonus { get; init; }

    /// <summary>Signed dice on its saves (the harness target's save dice, e.g. Bane).</summary>
    public DiceTerm[] SaveDice { get; init; } = [];

    /// <summary>Takes turns but never acts (the harness's dummies).</summary>
    public bool Inert { get; init; }

    /// <summary>Starts the fight at 0 HP and dying (the death-save test's creature left alone).</summary>
    public bool StartsDown { get; set; }

    public static int AbilityIndex(string ability) => ability switch
    {
        V.Abilities.Str => 0,
        V.Abilities.Dex => 1,
        V.Abilities.Con => 2,
        V.Abilities.Int => 3,
        V.Abilities.Wis => 4,
        V.Abilities.Cha => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(ability), ability, "Not an ability key."),
    };

    public bool ImmuneTo(int condition) => (ConditionImmunities & (1 << condition)) != 0;
}

/// <summary>Damage-adjustment codes: which attacks a resistance, immunity or vulnerability applies to.</summary>
internal static class Qualifier
{
    public const int None = 0;
    public const int Always = 1;
    public const int Nonmagical = 2;
    public const int NonmagicalNotSilvered = 3;
    public const int NonmagicalNotAdamantine = 4;

    public static int Of(string? qualifier) => qualifier switch
    {
        null => Always,
        StatBlockValues.DamageQualifiers.Nonmagical => Nonmagical,
        StatBlockValues.DamageQualifiers.NonmagicalNotSilvered => NonmagicalNotSilvered,
        StatBlockValues.DamageQualifiers.NonmagicalNotAdamantine => NonmagicalNotAdamantine,
        _ => Always,
    };

    /// <summary>Whether an adjustment with this code applies to damage with these properties (<see cref="DamageAdjustment.AppliesTo"/>).</summary>
    public static bool Applies(int code, bool magical, bool silvered, bool adamantine) => code switch
    {
        None => false,
        Nonmagical => !magical,
        NonmagicalNotSilvered => !magical && !silvered,
        NonmagicalNotAdamantine => !magical && !adamantine,
        _ => true,
    };

    /// <summary>Fills a per-type code table; the most general entry wins (Always &lt; the qualified codes).</summary>
    public static int[] Table(IEnumerable<DamageAdjustment> adjustments)
    {
        var table = new int[DamageTypes.Count];
        foreach (var adjustment in adjustments)
        {
            var type = DamageTypes.Of(adjustment.DamageType);
            if (type == DamageTypes.Typeless)
            {
                continue;
            }

            var code = Of(adjustment.Qualifier);
            table[type] = table[type] == None ? code : Math.Min(table[type], code);
        }

        return table;
    }
}

/// <summary>A retaliation, aura or death-burst trait: damage (with a save when it has one), and a condition.</summary>
internal sealed class TraitEffect
{
    public required string Name { get; init; }

    public required RollPart[] Damage { get; init; }

    public SaveSpec? Save { get; init; }

    public ConditionTemplate? Condition { get; init; }

    /// <summary>Creatures a death burst reaches (the DMG area count), 0 for the others.</summary>
    public int AreaCount { get; init; }

    /// <summary>
    /// An aura that works as a creature STARTS ITS TURN next to its owner ("any creature that starts its turn within 5
    /// feet": Stench, Fear Aura), rather than on the owner's own turn ("at the start of each of the balor's turns"). Read
    /// from the trait's text, which is the only place the data says it.
    /// </summary>
    public bool StartOfTargetTurn { get; init; }
}

/// <summary>
/// One monster action, bonus action, legendary action or combat spell, compiled from its <see cref="StatBlockAction"/>:
/// the parts the simulator reads, with the damage as <see cref="RollPart"/>s and the usage mapped to state slots.
/// </summary>
internal sealed class MonsterAction
{
    public required StatBlockAction Source { get; init; }

    public string Name => Source.Name;

    public string Kind => Source.Kind;

    public bool IsAttack => Kind == StatBlockValues.ActionKinds.Attack;

    public bool IsSave => Kind == StatBlockValues.ActionKinds.Save;

    public bool IsAutoHit => Kind == StatBlockValues.ActionKinds.AutoHit;

    public bool IsHeal => Kind == StatBlockValues.ActionKinds.Heal;

    public bool IsUseActions => Kind == StatBlockValues.ActionKinds.UseActions;

    /// <summary>A melee attack (one usable both ways counts as melee, and is thrown from the back line).</summary>
    public bool Melee { get; init; }

    public bool AlsoRanged { get; init; }

    public int AttackBonus { get; init; }

    public int Targets { get; init; } = 1;

    public RollPart[] Damage { get; init; } = [];

    public MonsterOnHit[] OnHit { get; init; } = [];

    public SaveSpec? Save { get; init; }

    /// <summary>Creatures an area covers by the DMG's count (0: not an area; the save targets <see cref="Targets"/> creatures).</summary>
    public int AreaCount { get; init; }

    public ConditionTemplate? Condition { get; init; }

    /// <summary>Further conditions the same failed save imposes (<see cref="StatBlockAction.ExtraConditions"/>).</summary>
    public ConditionTemplate[] ExtraConditions { get; init; } = [];

    /// <summary>Attack rolls per use (Scorching Ray's three rays), each at a target the policy picks.</summary>
    public int AttackRolls { get; init; } = 1;

    /// <summary>A heal on the creature itself only (Heal Self).</summary>
    public bool SelfOnly { get; init; }

    public DamageFormula? Healing { get; init; }

    public int AcBonus { get; init; }

    /// <summary>A parry that works only against melee attacks ("one melee attack that would hit it").</summary>
    public bool ParryMeleeOnly { get; init; }

    /// <summary>For a use-actions legendary action: the actions it uses, resolved.</summary>
    public (MonsterAction Action, int Count)[] Uses { get; set; } = [];

    /// <summary>Its damage counts as magical (spells; Magic Weapons) and Magic Resistance applies to its saves when it is a spell or magical.</summary>
    public bool Magical { get; init; }

    public bool IsSpell => Source.IsSpell;

    public bool Concentration => Source.Concentration;

    /// <summary>Index into the creature's limited-use state (recharge flag or uses left), −1 when at will.</summary>
    public int UsageSlot { get; set; } = -1;

    /// <summary>Index into the creature's spell-slot pools, −1 for none.</summary>
    public int PoolSlot { get; set; } = -1;

    public int LegendaryCost => Source.LegendaryCost;

    public bool OncePerRound => Source.OncePerRound;

    /// <summary>Index in <see cref="CombatantTemplate.LegendaryActions"/> (for once-per-round flags), −1 otherwise.</summary>
    public int LegendaryIndex { get; set; } = -1;

    /// <summary>
    /// For an action with <see cref="StatBlockAction.ImmuneAfterSuccess"/>: its number among the creature's
    /// immunity-granting effects (actions and riders, numbered by the compiler), which a target that succeeded against it
    /// records (<see cref="Creature.BecomeImmuneToEffect"/>); −1 for every other action. One number per compiled action,
    /// so the same Frightful Presence used as an action or through a legendary use_actions is one immunity.
    /// </summary>
    public int ImmunityIndex { get; set; } = -1;

    /// <summary>An outright kill at or below this many hit points (<see cref="StatBlockAction.KillAtOrBelowHp"/>: Power Word Kill), or null.</summary>
    public int? KillAtOrBelowHp => Source.KillAtOrBelowHp;

    /// <summary>What the simulator can do with it: attack, save, auto_hit, heal or use_actions (parry is a reaction).</summary>
    public bool Usable => IsAttack || IsSave || IsAutoHit || IsHeal || IsUseActions;

    /// <summary>E[damage per target] on a normal hit / a failed save / per dart, before the target's adjustments (policies only).</summary>
    public double MeanDamage { get; init; }

    /// <summary>E[the extra dice of a crit] (policies only).</summary>
    public double MeanCritExtra { get; init; }
}

/// <summary>A monster attack's rider (<see cref="ActionEffect"/>), compiled.</summary>
internal sealed class MonsterOnHit
{
    public required string Kind { get; init; }

    public RollPart[] Damage { get; init; } = [];

    public SaveSpec? Save { get; init; }

    public ConditionTemplate? Condition { get; init; }

    /// <summary>Further conditions imposed with <see cref="Condition"/> (a grapple that also restrains).</summary>
    public ConditionTemplate[] ExtraConditions { get; init; } = [];

    /// <summary>The largest size it works on, or null.</summary>
    public string? MaxSize { get; init; }

    /// <summary>
    /// A save rider that kills on a failure a target with at most this many hit points (the 2014 solar's Slaying Longbow,
    /// <see cref="ActionEffect.KillAtOrBelowHp"/>); a target above it is not affected by the rider at all. Null otherwise.
    /// </summary>
    public int? KillAtOrBelowHp { get; init; }

    /// <summary>For a rider with <see cref="ActionEffect.ImmuneAfterSuccess"/>, its immunity number (<see cref="MonsterAction.ImmunityIndex"/>); −1 otherwise.</summary>
    public int ImmunityIndex { get; set; } = -1;
}

/// <summary>A multiattack routine with its steps resolved to actions.</summary>
internal sealed class MultiattackPlan
{
    public required string Label { get; init; }

    public required (MonsterAction Action, int Count)[] Steps { get; init; }

    public int Choose { get; init; }

    public MonsterAction[] Options { get; init; } = [];
}

/// <summary>One resource-limited modifier of a build: its label (for "resources used") and uses per fight.</summary>
internal sealed record PcResource(string Label, int Uses);

/// <summary>
/// A build compiled for the simulator: the resolved build's lists with the per-attack indexes the turn needs, the resource
/// slots, which modifiers wait for a setup, and the build's concentration modifier.
/// </summary>
internal sealed class PcBuild
{
    public required ResolvedBuild Build { get; init; }

    public required PcAttack[] Attacks { get; init; }

    public required ResolvedRider[] Riders { get; init; }

    public required int[] RiderSlot { get; init; }

    public required ResolvedExtraAttack[] Extras { get; init; }

    public required int[] ExtraSlot { get; init; }

    public required int[] ExtraAttack { get; init; }

    public required ResolvedSaveEffect[] SaveEffects { get; init; }

    public required int[] SaveSlot { get; init; }

    /// <summary>Per save effect: the condition it imposes (compiled with its duration), or null.</summary>
    public required ConditionTemplate?[] SaveCondition { get; init; }

    /// <summary>Per save effect: its damage dice and flat part (null when it deals none).</summary>
    public required RollPart?[] SaveDamage { get; init; }

    public required ResolvedConditionOnHit[] ConditionsOnHit { get; init; }

    /// <summary>Per condition on hit: the condition with its duration (the edition default when none is given).</summary>
    public required ConditionTemplate[] OnHitCondition { get; init; }

    /// <summary>Per rider: E[its dice] (policies only).</summary>
    public required double[] RiderDiceMean { get; init; }

    public required int[] ConditionSlot { get; init; }

    public required ResolvedAdvantageSource[] Advantage { get; init; }

    public required ResolvedPowerAttack[] PowerAttacks { get; init; }

    public required ResolvedDamageReroll[] Rerolls { get; init; }

    public required ResolvedHeal[] Heals { get; init; }

    public required int[] HealSlot { get; init; }

    public required PcResource[] Resources { get; init; }

    /// <summary>Per modifier number: whether it waits for its setup (a first-round cost) before it applies.</summary>
    public required bool[] Gated { get; init; }

    public required ResolvedSetupCost[] Setups { get; init; }

    /// <summary>The build's concentration modifier number (the validator allows one per level), or 0.</summary>
    public required int ConcentrationNumber { get; init; }

    public required string? ConcentrationLabel { get; init; }

    /// <summary>The Attack action's attacks in list order, each count times.</summary>
    public required int[] ActionQueue { get; init; }

    /// <summary>The bonus_action attacks, each count times.</summary>
    public required int[] BonusQueue { get; init; }

    public required int[] SurgeExtras { get; init; }

    public required int[] BonusExtras { get; init; }

    public required int[] ReactionExtras { get; init; }

    public required int[] ActionSaves { get; init; }

    public required int[] BonusSaves { get; init; }

    public required int[] FreeSaves { get; init; }

    public ResolvedRulings Rulings => Build.Rulings;
}

/// <summary>One attack of a build, compiled: its dice, flat parts per way of making it, and which modifiers apply.</summary>
internal sealed class PcAttack
{
    public required ResolvedAttack A { get; init; }

    public required int Index { get; init; }

    public required DiceTerm[] Dice { get; init; }

    public required int Type { get; init; }

    public bool Melee => A.IsMelee;

    public bool Weapon => A.IsWeapon;

    public bool MeleeWeapon => A.IsMelee && A.IsWeapon;

    /// <summary>Spell attacks and weapons with the <c>magical</c> property overcome "nonmagical" resistance.</summary>
    public required bool Magical { get; init; }

    public required bool Silvered { get; init; }

    public required bool Adamantine { get; init; }

    public required DiceTerm[] ToHitDice { get; init; }

    /// <summary>Per damage type: whether Elemental Adept turns its 1s into 2s on this attack.</summary>
    public required bool[] ElementalAdept { get; init; }

    /// <summary>Indices of riders whose filter includes it (attack_action_only is checked per way of making it).</summary>
    public required int[] Riders { get; init; }

    public required int[] Advantage { get; init; }

    public required int[] PowerAttacks { get; init; }

    public required int[] Rerolls { get; init; }

    public required int[] Conditions { get; init; }

    public required double DiceMean { get; init; }

    /// <summary>The flat damage per way of making it (<see cref="LineKind"/>): attack_action_only parts, Cleave dropping a positive ability modifier.</summary>
    public required int[] Flat { get; init; }

    /// <summary>The bonus dice on the attack roll as a distribution (Bless), or null; for the policies' odds.</summary>
    public Pmf<double>? ToHitPmf { get; init; }

    /// <summary>A key unique to this attack of this creature, for the per-thread odds cache.</summary>
    public int OddsKey { get; set; }

    /// <summary>Savage Attacker's expected gain on a hit (and on a crit, ruling off): E[better of two rolls] − E[one roll] of the weapon dice.</summary>
    public double SavageGain { get; init; }

    /// <summary>Savage Attacker's gain on a crit under savage_attacker_on_crit_dice: the whole doubled set rolled twice.</summary>
    public double SavageGainCritAll { get; init; }

    public int AttackBonus => A.AttackBonus;

    public int CritMin => A.CritMin;
}
