using System.Globalization;

namespace DndMcp.Domain.Features;

/// <summary>
/// A build at one level with every level-dependent value concrete: the input the DPR engine (and later the simulator)
/// consumes. Produced only by <see cref="BuildResolver"/>, after validation, so every field is already checked and
/// canonical (wire values are <see cref="DslValues"/> constants; attack references are the attacks' own names).
///
/// <para>
/// <b>What is folded into the attacks and what stays a list.</b> Modifiers that simply change an attack's numbers are
/// folded into each <see cref="ResolvedAttack"/> they apply to: to_hit amounts and dice, crit_range, lucky,
/// elven_accuracy, ignore_cover, bonus_damage, the fighting-style sugar and the weapon remaps. Everything the engine has
/// to DECIDE or TRACK during a turn stays a typed list with its attack filter resolved to attack names: riders (spent
/// by policy, once per turn, with resources), extra attacks, save effects, conditions on hit, advantage sources (sampled
/// per turn), power attacks (toggled per turn), damage rerolls (Savage Attacker), setup costs and remaps (which the
/// engine applies to rider dice under a ruling). A filter lists only attacks active at this level, in build order.
/// </para>
/// </summary>
public sealed record ResolvedBuild
{
    public required string Name { get; init; }

    /// <summary><see cref="DslValues.Editions"/>: "2014" or "2024".</summary>
    public required string Edition { get; init; }

    /// <summary>The level this resolution is for (1–20), not necessarily the spec's own level.</summary>
    public required int Level { get; init; }

    public required int ProficiencyBonus { get; init; }

    public required ResolvedAbilities Abilities { get; init; }

    /// <summary>The fighting style as given (already expanded into the attacks and remaps), for echoing.</summary>
    public string? FightingStyle { get; init; }

    /// <summary>Attacks active at this level, in build order.</summary>
    public required IReadOnlyList<ResolvedAttack> Attacks { get; init; }

    /// <summary>extra_damage modifiers.</summary>
    public required IReadOnlyList<ResolvedRider> Riders { get; init; }

    public required IReadOnlyList<ResolvedExtraAttack> ExtraAttacks { get; init; }

    public required IReadOnlyList<ResolvedSaveEffect> SaveEffects { get; init; }

    public required IReadOnlyList<ResolvedConditionOnHit> ConditionsOnHit { get; init; }

    public required IReadOnlyList<ResolvedAdvantageSource> AdvantageSources { get; init; }

    public required IReadOnlyList<ResolvedPowerAttack> PowerAttacks { get; init; }

    /// <summary>reroll_damage_take_best modifiers (Savage Attacker).</summary>
    public required IReadOnlyList<ResolvedDamageReroll> DamageRerolls { get; init; }

    /// <summary>
    /// Every die remap, including the fighting style's. Also folded into <see cref="ResolvedAttack.WeaponRemap"/> and
    /// <see cref="ResolvedAttack.ElementalAdeptTypes"/>; listed here for rider dice (ruling <c>gwf_on_riders</c>) and for
    /// save effects (Elemental Adept applies to any die of its type).
    /// </summary>
    public required IReadOnlyList<ResolvedRemap> Remaps { get; init; }

    /// <summary>First-round costs (casting Hex as a Bonus Action), in build order.</summary>
    public required IReadOnlyList<ResolvedSetupCost> SetupCosts { get; init; }

    /// <summary>ac, resistance and temp_hp modifiers: reported, never part of damage dealt.</summary>
    public required IReadOnlyList<ResolvedDefensive> Defensive { get; init; }

    /// <summary>
    /// heal modifiers (Healing Word, Cure Wounds, Second Wind): the simulator's healing. Like <see cref="Defensive"/>
    /// they never change damage dealt, so the DPR engine reports them and otherwise ignores them; they are not in
    /// <see cref="BonusActionConsumers"/>, since in the closed form a heal never competes with damage for the Bonus
    /// Action (the simulator weighs it each turn).
    /// </summary>
    public IReadOnlyList<ResolvedHeal> Heals { get; init; } = [];

    public required ResolvedRulings Rulings { get; init; }

    /// <summary>
    /// Whether the spec changes with level (any step value with two or more steps, any from_level/until_level, any cantrip
    /// scaling). A build that does not, evaluated at several levels, changes only by proficiency bonus, and the engine
    /// warns so.
    /// </summary>
    public required bool ScalesWithLevel { get; init; }

    /// <summary>Plain-language notes for the result: edition mismatches, sugar that found nothing to apply to, ….</summary>
    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>The active attack with this name (case is ignored), or null.</summary>
    public ResolvedAttack? FindAttack(string name) =>
        Attacks.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Attacks per turn at most: every attack's count plus every extra attack's (what the validator bounds).</summary>
    public int MaxAttacksPerTurn => Attacks.Sum(a => a.Count) + ExtraAttacks.Sum(e => e.Count);

    /// <summary>
    /// Everything that can spend the Bonus Action, labelled for results: bonus_action attacks, bonus_action extra attacks,
    /// riders and save effects that cost it, and bonus_action setups. <c>balance_compare</c> reports a collision when a
    /// variant adds one where the baseline had one, because the engine then picks the better use each turn and the delta
    /// is net of what the Bonus Action did before.
    /// </summary>
    public IReadOnlyList<string> BonusActionConsumers =>
    [
        .. Attacks.Where(a => a.Action == DslValues.AttackActions.BonusAction).Select(a => a.Name),
        .. ExtraAttacks.Where(e => e.Action == DslValues.ExtraAttackActions.BonusAction).Select(e => e.Source.Label),
        .. Riders.Where(r => r.ActionCost == DslValues.ActionCosts.BonusAction).Select(r => r.Source.Label),
        .. SaveEffects.Where(s => s.ActionCost == DslValues.ActionCosts.BonusAction).Select(s => s.Source.Label),
        .. SetupCosts.Where(c => c.Cost == DslValues.Setup.BonusAction).Select(c => $"{c.Source.Label} (setup)"),
    ];

    /// <summary>Everything that spends the Reaction (reaction extra attacks), labelled for results.</summary>
    public IReadOnlyList<string> ReactionConsumers =>
        ExtraAttacks.Where(e => e.Action == DslValues.ExtraAttackActions.Reaction).Select(e => e.Source.Label).ToList();

    /// <summary>
    /// The note <c>balance_dpr</c> and <c>balance_simulate</c> both give when the build has Nick but does not model it, or
    /// null. One rule and one wording for both tools, so they never disagree about the same build.
    ///
    /// <para>
    /// Nick ("When you make the extra attack of the Light property, you can make it as part of the Attack action instead
    /// of as a Bonus Action") changes only the action economy, and neither engine moves an attack between actions: a build
    /// models it by making the Light weapon's extra attack an action attack. So the note is due while the build has a
    /// Nick weapon AND still makes the Light extra attack with the Bonus Action: a bonus_action weapon attack that is
    /// offhand, Light, or the Nick weapon itself. The rules do not say which of the two Light weapons must carry Nick, so
    /// a Nick dagger in the Attack action beside a bonus_action offhand shortsword is noted too, naming the Nick weapon.
    /// </para>
    /// <para>
    /// A build whose Attack action already holds the Light extra attack has modelled Nick and gets no note, since one
    /// would tell it to do what it already did. The build does not say which attack is the extra one, so the action's
    /// attacks are read for it: two different Light weapon attacks in the action (the 2024 rogue archetype's shortsword
    /// and scimitar), or an offhand action weapon attack other than the Nick one (the extra attack, even with its light
    /// property left out, since the Nick weapon earns it: every SRD Nick weapon, the dagger, light hammer, scimitar and
    /// sickle, is Light). An offhand Nick attack alone does not count: the extra attack is made with a different Light
    /// weapon from the one that earned it, so while the Nick weapon is the action's only Light attack, the extra attack
    /// is the bonus_action one. A bonus_action Light attack beside a build that has modelled Nick comes from another
    /// source (the Dual Wielder feat's) and is not noted. Reading the shape misses a case: two Light weapons that each
    /// make an ordinary action attack (Extra Attack split between them) beside a bonus_action Light extra attack look
    /// the same, and are not noted either; the note changes no number.
    /// </para>
    /// </summary>
    public string? UnmodelledNickNote()
    {
        var nick = Attacks.Where(a => a.Mastery == DslValues.Masteries.Nick).ToList();
        var bonusExtra = Attacks
            .Where(a => a.Action == DslValues.AttackActions.BonusAction && a.IsWeapon &&
                        (a.Offhand || a.HasProperty(DslValues.Properties.Light) || a.Mastery == DslValues.Masteries.Nick))
            .ToList();
        if (nick.Count == 0 || bonusExtra.Count == 0)
        {
            return null;
        }

        var inTheAction = Attacks.Where(a => a.IsPartOfAttackAction).ToList();
        var extraAttackInTheAction =
            inTheAction.Any(a => a.Offhand && a.Mastery != DslValues.Masteries.Nick) ||
            inTheAction.Count(a => a.HasProperty(DslValues.Properties.Light)) >= 2;
        if (extraAttackInTheAction)
        {
            return null;
        }

        // "Scimitar: Nick …" when the bonus_action attack carries Nick itself; "Shortsword: Nick (on Dagger) …" otherwise.
        var carriers = bonusExtra.Any(a => a.Mastery == DslValues.Masteries.Nick)
            ? string.Empty
            : $" (on {string.Join(", ", nick.Select(a => a.Name))})";
        return $"{string.Join(", ", bonusExtra.Select(a => a.Name))}: Nick{carriers} changes only the action economy; model it by making the " +
               "Light weapon's extra attack an action attack (count) instead of a bonus_action one.";
    }
}

/// <summary>Ability scores at one level.</summary>
public sealed record ResolvedAbilities(int Str, int Dex, int Con, int Int, int Wis, int Cha)
{
    /// <summary>The score for an ability key.</summary>
    public int Score(string ability) => ability switch
    {
        DslValues.Abilities.Str => Str,
        DslValues.Abilities.Dex => Dex,
        DslValues.Abilities.Con => Con,
        DslValues.Abilities.Int => Int,
        DslValues.Abilities.Wis => Wis,
        DslValues.Abilities.Cha => Cha,
        _ => throw new ArgumentOutOfRangeException(nameof(ability), ability, "Not an ability key."),
    };

    /// <summary>The modifier for an ability key; <see cref="DslValues.Abilities.None"/> is 0.</summary>
    public int Modifier(string ability) => ability == DslValues.Abilities.None ? 0 : DslLimits.AbilityModifier(Score(ability));
}

/// <summary>One named part of a sum: ("Str", 4), ("proficiency", 3), ("Archery", 2).</summary>
public sealed record NamedValue(string Label, int Value)
{
    /// <summary>"Str +4", "Bane -1".</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Label} {Value:+0;-0;+0}");
}

/// <summary>Named bonus dice on a d20 roll: ("Bless", 1d4). Dice only; flat parts are <see cref="NamedValue"/>s.</summary>
public sealed record NamedDice(string Label, DamageFormula Dice);

/// <summary>Where a flat damage part comes from; the engine treats some sources specially.</summary>
public static class DamagePartSources
{
    /// <summary>The to_hit ability's modifier. Cleave drops it unless negative.</summary>
    public const string Ability = "ability";

    /// <summary>A bonus_damage modifier (Agonizing Blast, 2024 GWM's +PB).</summary>
    public const string BonusDamage = "bonus_damage";

    /// <summary>The Dueling fighting style's +2.</summary>
    public const string Dueling = "dueling";
}

/// <summary>
/// A flat damage part of an attack (never doubled on a crit). <see cref="AttackActionOnly"/> parts count only when the
/// attack is made as part of the Attack action: not on Hew or a reaction attack (unless ruling <c>hew_gets_pb</c>).
/// </summary>
public sealed record DamagePart(string Label, int Value, string Source, bool AttackActionOnly = false);

/// <summary>A modifier's identity in results and messages: its 1-based position in the build's list, kind and name.</summary>
/// <param name="Name">The name as given, or null when it has none.</param>
public sealed record ModifierRef(int Number, string Kind, string? Name)
{
    /// <summary>How results label it: its name, or "extra_damage #3" when it has none.</summary>
    public string Label => Name ?? $"{Kind} #{DslText.Number(Number)}";

    /// <summary>"modifiers item 3 (extra_damage \"Hex\")", the vocabulary of every DSL message.</summary>
    public string Where => ModifierItem.WhereOf(Number, Kind, Name);
}

/// <summary>Uses at this level and when they return (<see cref="DslValues.Rests"/>).</summary>
public sealed record ResolvedResource(int Uses, string Per);

/// <summary>Table rulings in force (see <see cref="RulingsSpec"/>).</summary>
public sealed record ResolvedRulings(bool HewGetsPb, bool CleavePartOfAttackAction, bool GwfOnRiders, bool SavageAttackerOnCritDice)
{
    public static ResolvedRulings Default { get; } = new(false, false, false, false);

    public static ResolvedRulings From(RulingsSpec? spec) => spec is null
        ? Default
        : new ResolvedRulings(spec.HewGetsPb, spec.CleavePartOfAttackAction, spec.GwfOnRiders, spec.SavageAttackerOnCritDice);
}

/// <summary>Anything that applies to a set of attacks, named by their canonical names.</summary>
public interface IAttackFilter
{
    /// <summary>Names of the active attacks it applies to, in build order.</summary>
    IReadOnlyList<string> Attacks { get; }
}

public static class AttackFilterExtensions
{
    /// <summary>Whether the filter includes <paramref name="attack"/>.</summary>
    public static bool AppliesTo(this IAttackFilter filter, ResolvedAttack attack) =>
        filter.Attacks.Contains(attack.Name, StringComparer.Ordinal);
}

/// <summary>
/// One attack at one level. The to-hit and damage numbers are complete: <see cref="AttackBonus"/> is the sum of
/// <see cref="ToHitParts"/>, and damage is <see cref="Damage"/> plus the <see cref="DamageParts"/> that apply
/// (<see cref="FlatDamage"/>). What the engine varies per turn (advantage, power attack, riders) is not in here.
/// </summary>
public sealed record ResolvedAttack
{
    /// <summary>1-based position in the build's attacks list: "attacks item N".</summary>
    public required int Number { get; init; }

    public required string Name { get; init; }

    /// <summary>Attacks per use at this level, cantrip beams included.</summary>
    public required int Count { get; init; }

    /// <summary><see cref="DslValues.AttackActions"/>: action or bonus_action.</summary>
    public required string Action { get; init; }

    /// <summary>
    /// Made as part of the Attack action (for attack_action_only bonuses, and what lets the Light weapon's offhand attack
    /// follow): an action WEAPON attack. A spell attack made with the Action is the spell's casting action (2024 Magic
    /// action, 2014 Cast a Spell), not the Attack action: 2024 "Attack [Action]: an attack roll with a weapon or an Unarmed
    /// Strike".
    /// </summary>
    public bool IsPartOfAttackAction => Action == DslValues.AttackActions.Action && IsWeapon;

    /// <summary>The to_hit ability key, or <see cref="DslValues.Abilities.None"/>.</summary>
    public required string Ability { get; init; }

    /// <summary>That ability's modifier (0 for none): what Graze deals, and what Topple's DC uses.</summary>
    public required int AbilityModifier { get; init; }

    /// <summary>The flat attack bonus: the sum of <see cref="ToHitParts"/>.</summary>
    public int AttackBonus => ToHitParts.Sum(p => p.Value);

    /// <summary>("Str", 4), ("proficiency", 3), ("Archery", 2), (a to_hit modifier's label, its amount) …</summary>
    public required IReadOnlyList<NamedValue> ToHitParts { get; init; }

    /// <summary>Bonus dice on the attack roll (Bless 1d4, Bane −1d4), each named by its modifier.</summary>
    public required IReadOnlyList<NamedDice> ToHitDice { get; init; }

    /// <summary>Crits on this d20 roll or higher (20 unless a crit_range applies; the lowest wins).</summary>
    public required int CritMin { get; init; }

    /// <summary>Lucky / Luck: reroll a natural 1 on the attack roll (one die).</summary>
    public required bool Lucky { get; init; }

    /// <summary>Elven Accuracy: with Advantage, roll three dice.</summary>
    public required bool ElvenAccuracy { get; init; }

    /// <summary>The weapon's (or spell's) own damage at this level, cantrip dice included; its flat part is in here too.</summary>
    public required DamageFormula Damage { get; init; }

    /// <summary>A <see cref="DslValues.DamageTypes"/> value, or null for typeless damage (never resisted).</summary>
    public string? DamageType { get; init; }

    /// <summary>Flat parts added to damage: the ability modifier, Dueling, bonus_damage modifiers.</summary>
    public required IReadOnlyList<DamagePart> DamageParts { get; init; }

    /// <summary>
    /// The flat damage on a hit: <see cref="Damage"/>'s flat part plus every applicable <see cref="DamageParts"/> entry.
    /// </summary>
    /// <param name="partOfAttackAction">Whether this attack is made as part of the Attack action (attack_action_only parts).</param>
    public int FlatDamage(bool partOfAttackAction) =>
        Damage.Flat + DamageParts.Where(p => partOfAttackAction || !p.AttackActionOnly).Sum(p => p.Value);

    /// <summary>The ability part of <see cref="DamageParts"/> (0 when none is added).</summary>
    public int AbilityDamage => DamageParts.Where(p => p.Source == DamagePartSources.Ability).Sum(p => p.Value);

    /// <summary><see cref="DslValues.Properties"/> values, canonical, in the order given.</summary>
    public required IReadOnlyList<string> Properties { get; init; }

    public bool HasProperty(string property) => Properties.Contains(property, StringComparer.Ordinal);

    /// <summary>Melee (also when neither melee nor ranged was given).</summary>
    public bool IsMelee => !IsRanged;

    public bool IsRanged => HasProperty(DslValues.Properties.Ranged);

    /// <summary>A spell attack, not a weapon.</summary>
    public bool IsSpell => HasProperty(DslValues.Properties.Spell);

    public bool IsWeapon => !IsSpell;

    /// <summary>
    /// Magical damage for a qualified resistance ("from nonmagical attacks"): a spell attack, or an attack given the
    /// <see cref="DslValues.Properties.Magical"/> property. Read through <see cref="DamageProperties.Of(ResolvedAttack)"/>
    /// by the DPR engine against a stat block target, for the attack and every rider on it, and by the simulator.
    /// </summary>
    public bool IsMagical => IsSpell || HasProperty(DslValues.Properties.Magical);

    /// <summary>Overcomes "nonmagical attacks that aren't silvered".</summary>
    public bool IsSilvered => HasProperty(DslValues.Properties.Silvered);

    /// <summary>Overcomes "nonmagical attacks that aren't adamantine".</summary>
    public bool IsAdamantine => HasProperty(DslValues.Properties.Adamantine);

    public required bool Offhand { get; init; }

    /// <summary>A <see cref="DslValues.Masteries"/> value, or null.</summary>
    public string? Mastery { get; init; }

    /// <summary>The Save DC of this attack's mastery (Topple): 8 + proficiency bonus + <see cref="AbilityModifier"/>.</summary>
    public required int MasterySaveDc { get; init; }

    /// <summary>gwf2014 or gwf2024 on this attack's own dice, or null.</summary>
    public string? WeaponRemap { get; init; }

    /// <summary>Where <see cref="WeaponRemap"/> comes from: "Great Weapon Fighting (fighting style)" or a modifier's label.</summary>
    public string? WeaponRemapSource { get; init; }

    /// <summary>Damage types whose dice on this attack (its own and its riders') count a 1 as a 2 (Elemental Adept).</summary>
    public required IReadOnlyList<string> ElementalAdeptTypes { get; init; }

    /// <summary>Ignores half and three-quarters cover (2024 Sharpshooter).</summary>
    public required bool IgnoresCover { get; init; }

    /// <summary><see cref="DslValues.Cantrips"/> mode, or null.</summary>
    public string? Cantrip { get; init; }

    /// <summary>1–4: the cantrip multiplier applied to the dice or the count (1 when not a cantrip).</summary>
    public required int CantripMultiplier { get; init; }
}

/// <summary>An extra_damage rider.</summary>
public sealed record ResolvedRider : IAttackFilter
{
    public required ModifierRef Source { get; init; }

    /// <summary>Dice plus flat amount at this level. Dice double on a crit when <see cref="CritDoubles"/>; flat never does.</summary>
    public required DamageFormula Damage { get; init; }

    /// <summary>
    /// The rider's damage type, or null for none given: then it deals the damage type of the attack it lands with (2024
    /// Sneak Attack: "The extra damage's type is the same as the weapon's type"; 2014 Hex and Hunter's Mark are read the
    /// same way), resolved per attack line by the engine, so one rider on a piercing and a fire attack is each.
    /// </summary>
    public string? DamageType { get; init; }

    /// <summary><see cref="DslValues.When"/>: every_hit, first_hit_per_turn, on_crit or on_miss.</summary>
    public required string When { get; init; }

    /// <summary><see cref="DslValues.Policies"/>; meaningful only when <see cref="IsOptional"/>.</summary>
    public required string Policy { get; init; }

    /// <summary>With policy optimal: the damage one use is worth elsewhere (λ).</summary>
    public required double UseValue { get; init; }

    public required bool CritDoubles { get; init; }

    public required bool AttackActionOnly { get; init; }

    /// <summary>"bonus_action" when spending it uses the Bonus Action (2024 Divine Smite), else null.</summary>
    public string? ActionCost { get; init; }

    public ResolvedResource? Resource { get; init; }

    public required bool Concentration { get; init; }

    public required IReadOnlyList<string> Attacks { get; init; }

    /// <summary>
    /// Whether spending it is a choice the policy makes: a first_hit_per_turn rider, or an every-hit rider with a resource
    /// or an action cost. Every other rider is applied whenever it can be.
    /// </summary>
    public bool IsOptional =>
        When == DslValues.When.FirstHitPerTurn ||
        (When == DslValues.When.EveryHit && (Resource is not null || ActionCost is not null));
}

/// <summary>An extra_attack: more attacks with a named attack.</summary>
public sealed record ResolvedExtraAttack
{
    public required ModifierRef Source { get; init; }

    /// <summary>The attack's canonical name (active at this level: validated).</summary>
    public required string Attack { get; init; }

    public required int Count { get; init; }

    /// <summary><see cref="DslValues.ExtraAttackActions"/>: action (Action Surge), bonus_action or reaction.</summary>
    public required string Action { get; init; }

    /// <summary><see cref="DslValues.Triggers"/>; only bonus_action extra attacks have one other than always.</summary>
    public required string Trigger { get; init; }

    /// <summary>For reaction extra attacks only: the chance per round it happens.</summary>
    public double? TriggerProbability { get; init; }

    public ResolvedResource? Resource { get; init; }

    public required bool Concentration { get; init; }
}

/// <summary>A save_effect with its DC, damage and target count concrete.</summary>
public sealed record ResolvedSaveEffect
{
    public required ModifierRef Source { get; init; }

    /// <summary>The saving throw's ability key.</summary>
    public required string Ability { get; init; }

    public required int Dc { get; init; }

    /// <summary>("given", 15) or ("base", 8), ("proficiency", 3), ("Cha", 4), ("dc_bonus", 1).</summary>
    public required IReadOnlyList<NamedValue> DcParts { get; init; }

    /// <summary>The ability the DC comes from (dc_ability), or null when the DC was given: what an ASI would raise.</summary>
    public string? DcAbility { get; init; }

    /// <summary>Damage on a failed save (dice + amount, cantrip-scaled), or null for an effect with only a condition.</summary>
    public DamageFormula? Damage { get; init; }

    public string? DamageType { get; init; }

    /// <summary><see cref="DslValues.OnSuccess"/>: half or none.</summary>
    public required string OnSuccess { get; init; }

    /// <summary>Creatures affected: given, from the area (<see cref="DslValues.Shapes.Targets"/>), or 1.</summary>
    public required int Targets { get; init; }

    public string? Shape { get; init; }

    public int? Size { get; init; }

    /// <summary>Whether <see cref="Targets"/> came from shape and size (results add the DMG's "±1d3" caveat).</summary>
    public bool TargetsFromArea => Shape is not null;

    /// <summary>Magic Resistance applies.</summary>
    public required bool Magical { get; init; }

    public string? Condition { get; init; }

    /// <summary><see cref="DslValues.Durations"/> of <see cref="Condition"/> for the simulator, or null for the default (save_ends).</summary>
    public string? Duration { get; init; }

    /// <summary>Whether <see cref="Condition"/> changes the maths, or is a label only.</summary>
    public bool ConditionIsMechanical => Condition is not null && DslValues.Conditions.IsMechanical(Condition);

    /// <summary><see cref="DslValues.ActionCosts"/>: action, bonus_action or none.</summary>
    public required string ActionCost { get; init; }

    public ResolvedResource? Resource { get; init; }

    public required bool Concentration { get; init; }

    /// <summary>1–4 when the dice scale like a cantrip's, else 1.</summary>
    public required int CantripMultiplier { get; init; }

    /// <summary>
    /// Given as a cantrip (cantrip: true). <see cref="CantripMultiplier"/> cannot say it (it is 1 for a cantrip at levels
    /// 1–4); a save effect that is neither a cantrip nor limited by a resource is used every round of every fight, which
    /// the result notes.
    /// </summary>
    public bool IsCantrip { get; init; }

    /// <summary>Damage types whose dice here count a 1 as a 2 (Elemental Adept of <see cref="DamageType"/>).</summary>
    public required IReadOnlyList<string> ElementalAdeptTypes { get; init; }
}

/// <summary>The DC a condition_on_hit uses with one attack (the default DC follows the attack's ability).</summary>
public sealed record AttackDc(string Attack, int Dc, IReadOnlyList<NamedValue> Parts);

/// <summary>A condition_on_hit (Stunning Strike, trip riders).</summary>
public sealed record ResolvedConditionOnHit : IAttackFilter
{
    public required ModifierRef Source { get; init; }

    /// <summary>One of the mechanical <see cref="DslValues.Conditions"/>.</summary>
    public required string Condition { get; init; }

    /// <summary>The saving throw's ability key.</summary>
    public required string Ability { get; init; }

    /// <summary>The DC per attack it applies to, in the order of <see cref="Attacks"/>.</summary>
    public required IReadOnlyList<AttackDc> Dcs { get; init; }

    public IReadOnlyList<string> Attacks => Dcs.Select(d => d.Attack).ToList();

    /// <summary>every_hit or first_hit_per_turn.</summary>
    public required string When { get; init; }

    /// <summary><see cref="DslValues.Durations"/> for the simulator, or null for the edition's default (see there).</summary>
    public string? Duration { get; init; }

    /// <summary><see cref="DslValues.Policies"/>: a condition on hit is always optional.</summary>
    public required string Policy { get; init; }

    public required double UseValue { get; init; }

    public required bool Magical { get; init; }

    public ResolvedResource? Resource { get; init; }

    public required bool Concentration { get; init; }

    /// <summary>The DC against <paramref name="attack"/>.</summary>
    public int DcFor(string attack) => Dcs.First(d => d.Attack == attack).Dc;
}

/// <summary>An advantage (or disadvantage) source present on a turn with probability <see cref="Rate"/>.</summary>
public sealed record ResolvedAdvantageSource(ModifierRef Source, string Mode, double Rate, IReadOnlyList<string> Attacks) : IAttackFilter;

/// <summary>A −penalty/+bonus power attack (2014 GWM/Sharpshooter) on the filtered attacks.</summary>
public sealed record ResolvedPowerAttack(ModifierRef Source, int Penalty, int Bonus, string Policy, IReadOnlyList<string> Attacks) : IAttackFilter;

/// <summary>reroll_damage_take_best (Savage Attacker): once per turn, roll the weapon's damage dice twice, use either.</summary>
public sealed record ResolvedDamageReroll(ModifierRef Source, string Policy, double UseValue, IReadOnlyList<string> Attacks) : IAttackFilter;

/// <summary>
/// A die remap. <see cref="Source"/> is null for the fighting-style sugar. Elemental Adept applies to any die of
/// <see cref="DamageType"/> on the filtered attacks (their riders included) and on every save effect of that type.
/// </summary>
public sealed record ResolvedRemap(string Label, ModifierRef? Source, string Remap, string? DamageType, IReadOnlyList<string> Attacks) : IAttackFilter;

/// <summary>A first-round cost: <see cref="DslValues.Setup"/> bonus_action or action.</summary>
public sealed record ResolvedSetupCost(ModifierRef Source, string Cost);

/// <summary>A defensive modifier (ac, resistance, temp_hp): kept for the simulator; no effect on damage dealt.</summary>
public sealed record ResolvedDefensive(ModifierRef Source, string Kind, int? Amount, string? DamageType, ResolvedResource? Resource);

/// <summary>
/// A heal modifier at one level: what the simulator restores and what it costs. Healing is capped at the target's
/// hit point maximum, brings a creature at 0 HP back up and resets its death saves (engine rules, not this record's).
/// </summary>
public sealed record ResolvedHeal
{
    public required ModifierRef Source { get; init; }

    /// <summary>
    /// HP restored per target at this level: dice plus flat parts (an ability modifier, the level for Second Wind). The
    /// flat part may be negative with a negative ability modifier; healing never goes below 0 in the rules.
    /// </summary>
    public required DamageFormula Healing { get; init; }

    /// <summary><see cref="DslValues.ActionCosts"/>: action or bonus_action.</summary>
    public required string ActionCost { get; init; }

    /// <summary>Creatures healed per use (Mass Healing Word: up to 6).</summary>
    public required int Targets { get; init; }

    /// <summary>Heals only the creature itself (Second Wind), never an ally.</summary>
    public required bool SelfOnly { get; init; }

    public ResolvedResource? Resource { get; init; }
}
