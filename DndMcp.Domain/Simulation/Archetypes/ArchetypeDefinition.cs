using DndMcp.Domain.Features;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Simulation.Archetypes;

/// <summary>
/// One class archetype in one edition, level-independent: the DSL parts (with step values), the ability plan, the
/// armour plan, the hit die, the saves and the position. <see cref="ArchetypeCatalog"/> reads it at a level to make an
/// <see cref="ArchetypeMember"/>. Everything level-dependent is DERIVED here (HP, AC, saves) or resolved from step values
/// (the build), never written per level, so a class's rules live in one place.
///
/// <para>
/// A class, not a record: its members include delegates and an <see cref="AbilityTrack"/>, so a record's value equality
/// would compare references anyway. <see cref="AbilitiesSpec"/> is derived when <see cref="Abilities"/> is set (not
/// cached on first read), so it can never disagree with the plan, and it is complete before the shared catalogue hands
/// the definition to any thread.
/// </para>
/// </summary>
internal sealed class ArchetypeDefinition
{
    private readonly AbilityTrack _abilities = null!;

    /// <summary>The wire name, e.g. "fighter".</summary>
    public required string Name { get; init; }

    /// <summary>"Fighter": how results name it.</summary>
    public required string Title { get; init; }

    /// <summary>"2014" or "2024": which class table, ASI levels and feature wording the definition follows.</summary>
    public required string Edition { get; init; }

    /// <summary>The class's hit die (6, 8, 10 or 12): HP is its maximum at level 1, then its average plus Con per level.</summary>
    public required int HitDie { get; init; }

    /// <summary><see cref="SimulationValues.Positions.Front"/> or <see cref="SimulationValues.Positions.Back"/>.</summary>
    public required string Position { get; init; }

    /// <summary>
    /// The ability plan: the standard array in the class's order, the ASIs in a fixed order and any capstone. Setting it
    /// also derives <see cref="AbilitiesSpec"/>.
    /// </summary>
    public required AbilityTrack Abilities
    {
        get => _abilities;
        init
        {
            _abilities = value;
            AbilitiesSpec = value.ToSpec();
        }
    }

    /// <summary>The ability plan as the DSL's abilities (step maps by level), derived from <see cref="Abilities"/>.</summary>
    public AbilitiesSpec AbilitiesSpec { get; private init; } = null!;

    /// <summary>
    /// The starting armour, the armour the class is trained in (bought by wealth tier), whether a shield is carried and
    /// any armourless formula (Unarmored Defense, Mage Armor): what <see cref="ArmorPlan.At"/> turns into the AC at a level.
    /// </summary>
    public required ArmorPlan Armor { get; init; }

    /// <summary>The class's two saving throw proficiencies.</summary>
    public required IReadOnlyList<string> SaveProficiencies { get; init; }

    /// <summary>Saves gained later (Slippery Mind, Diamond Soul / Disciplined Survivor): level → abilities, or null.</summary>
    public Func<int, IReadOnlyList<string>>? LaterSaveProficiencies { get; init; }

    /// <summary>A bonus to every save of its own at a level (a paladin's Aura of Protection), or null.</summary>
    public Func<int, AbilityTrack, int>? SaveBonus { get; init; }

    /// <summary>A <see cref="V.FightingStyles"/> value the build takes (gwf, archery, dueling, twf), or null.</summary>
    public string? FightingStyle { get; init; }

    /// <summary>The build's attacks with step values and level ranges, the same at every level (the member picks the level).</summary>
    public required IReadOnlyList<AttackSpec> Attacks { get; init; }

    /// <summary>The build's modifiers with step values and level ranges, the same at every level.</summary>
    public required IReadOnlyList<ModifierSpec> Modifiers { get; init; }

    /// <summary>The routine in one line: "Greatsword (2d6, GWF, Graze), Extra Attack 5/11/20, Action Surge, Second Wind".</summary>
    public required string Routine { get; init; }

    /// <summary>Class-specific assumptions: what is modelled beyond the routine, and what is left out and why.</summary>
    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>The save proficiencies at a level, in str … cha order.</summary>
    public IReadOnlyList<string> SavesAt(int level)
    {
        var all = SaveProficiencies.Concat(LaterSaveProficiencies?.Invoke(level) ?? []).ToHashSet();
        return V.Abilities.All.Where(all.Contains).ToList();
    }
}

/// <summary>
/// DSL parts the archetypes share: weapons that become +1 weapons at level 11, spell attacks, and the wire constants
/// spelled once.
///
/// <para>
/// <b>The +1 weapon at 11.</b> The DMG 2014 "Starting at Higher Levels" table gives a standard campaign's level 11
/// character magic items (uncommon); a +1 weapon is the archetype's. It matters beyond +1: many 2014 monsters resist
/// "bludgeoning, piercing, and slashing from nonmagical attacks", and a tier 3 party without a magic weapon would be
/// simulated against resistances real tables rarely face. A weapon's properties are not a step value, so the weapon is two
/// attacks: the mundane one until level 10 and "+1 &lt;name&gt;" (magical, +1 to hit and damage) from 11. Modifiers that name
/// attacks name both; an extra attack (Action Surge, Flurry of Blows) is written once per weapon with the same level
/// split.
/// </para>
/// </summary>
internal static class ArchetypeParts
{
    /// <summary>The casters' note on slots; a report states it once (<see cref="ArchetypeCatalog.SharedRules"/>).</summary>
    public const string NoUpcasting =
        "Spells are cast at their own level (no upcasting) and none above the area spell are modelled, so high-level casting is understated.";

    public const int MagicWeaponLevel = 11;
    public const int LastMundaneLevel = MagicWeaponLevel - 1;

    public static string PlusOne(string weapon) => $"+1 {weapon}";

    /// <summary>The weapon's two names: mundane and +1.</summary>
    public static IReadOnlyList<string> Both(string weapon) => [weapon, PlusOne(weapon)];

    /// <summary>
    /// The weapon as two attacks split at <see cref="MagicWeaponLevel"/>. <paramref name="count"/> is attacks per Attack
    /// action by level (Extra Attack); each half gets the part of it that falls in its levels.
    /// </summary>
    public static IReadOnlyList<AttackSpec> WeaponWithPlusOne(
        string name, string dice, string damageType, string ability, IReadOnlyList<string> properties, Func<int, int> count,
        string? mastery = null, string action = V.AttackActions.Action, bool? offhand = null) =>
    [
        new AttackSpec
        {
            Name = name,
            Count = Lv.ByLevel(DslLimits.MinLevel, LastMundaneLevel, count),
            Action = action,
            ToHit = new ToHitSpec { Ability = ability },
            Damage = Lv.Of(dice),
            DamageType = damageType,
            Properties = properties,
            Mastery = mastery,
            Offhand = offhand,
            UntilLevel = LastMundaneLevel,
        },
        new AttackSpec
        {
            Name = PlusOne(name),
            Count = Lv.ByLevel(MagicWeaponLevel, DslLimits.MaxLevel, count),
            Action = action,
            ToHit = new ToHitSpec { Ability = ability, Bonus = 1 },
            Damage = Lv.Of($"{dice}+1"),
            DamageType = damageType,
            Properties = [.. properties, V.Properties.Magical],
            Mastery = mastery,
            Offhand = offhand,
            FromLevel = MagicWeaponLevel,
        },
    ];

    /// <summary>
    /// One modifier per half of a split weapon: <paramref name="make"/> gets the attack's name and the level range it
    /// covers (intersected with [<paramref name="from"/>, 20]); halves outside the range are skipped.
    /// </summary>
    public static IReadOnlyList<ModifierSpec> PerWeapon(string weapon, int from, Func<string, int, int, ModifierSpec> make)
    {
        var list = new List<ModifierSpec>();
        if (from <= LastMundaneLevel)
        {
            list.Add(make(weapon, from, LastMundaneLevel));
        }

        list.Add(make(PlusOne(weapon), Math.Max(from, MagicWeaponLevel), DslLimits.MaxLevel));
        return list;
    }

    /// <summary>
    /// A spell attack: no ability modifier on damage unless <paramref name="addsModifier"/> (Spiritual Weapon),
    /// <paramref name="cantrip"/> scaling if given.
    /// </summary>
    public static AttackSpec SpellAttack(
        string name, string ability, string dice, string damageType, bool ranged, string? cantrip = null, bool addsModifier = false,
        string action = V.AttackActions.Action, int? fromLevel = null, int? untilLevel = null) => new()
    {
        Name = name,
        Action = action,
        ToHit = new ToHitSpec { Ability = ability },
        Damage = Lv.Of(dice),
        DamageType = damageType,
        AbilityToDamage = addsModifier,
        Properties = [ranged ? V.Properties.Ranged : V.Properties.Melee, V.Properties.Spell],
        Cantrip = cantrip,
        FromLevel = fromLevel,
        UntilLevel = untilLevel,
    };

    /// <summary>A resource of <paramref name="uses"/> (a number or step map) per rest.</summary>
    public static ResourceSpec Resource(object uses, string per) => new() { Uses = uses, Per = per };

    /// <summary>Uses by level over [from, 20] from a per-level count.</summary>
    public static ResourceSpec Resource(int from, Func<int, int> uses, string per) => Resource(Lv.ByLevel(from, DslLimits.MaxLevel, uses), per);

    public static string Ordinal(int level) => level.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
