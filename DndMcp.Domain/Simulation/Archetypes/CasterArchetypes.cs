using DndMcp.Domain.Features;
using static DndMcp.Domain.Simulation.Archetypes.ArchetypeParts;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Simulation.Archetypes;

/// <summary>
/// The spellcasting archetypes: cleric, druid, wizard, sorcerer, warlock, bard. Each is a damage cantrip (scaling ×1–×4
/// at 1/5/11/17), one limited area or signature spell from the level it is gained, and a heal for the healers (clerics
/// and bards Healing Word, druids Cure Wounds, each with the edition's dice).
///
/// <para>
/// <b>Spell slots.</b> The DSL has no shared slot pool, so each spell gets its own uses from the class table without
/// counting a slot twice: heals use the 1st-level slots, the area spell every slot of its level or higher, and a spell
/// cast once a fight and kept up (Spiritual Weapon, Spirit Guardians, Call Lightning, Hex) is a setup cost or an
/// every-turn effect with no uses. <b>No upcasting</b> and no spells above the area spell: a level 17 wizard still casts
/// 8d6 Fireballs, so high-level casters are understated (said in every such member's assumptions).
/// </para>
/// </summary>
internal static class CasterArchetypes
{
    private const string NoUpcasting = ArchetypeParts.NoUpcasting;

    /// <summary>
    /// <b>Cleric</b>: Sacred Flame (Dex save, 1d8 radiant, nothing on a success, cantrip scaling; 2024 Potent Spellcasting
    /// adds the Wis modifier from 7 via Blessed Strikes), Spiritual Weapon from 3 (a Bonus Action melee spell attack every
    /// turn, 1d8 + Wis force; one 2nd-level slot a fight), Spirit Guardians from 5 (cast with the Action in round 1 — a
    /// setup cost — then 3d8 radiant, Wis save for half, to the 3 creatures a 15-foot emanation covers by the DMG table,
    /// every round; Concentration), Healing Word (2014 1d4 + Wis, 2024 2d4 + Wis, Bonus Action; the 1st-level slots).
    /// 2024 Spiritual Weapon needs Concentration, so it stops at 4 when Spirit Guardians takes over. Wis 17, Con 15, Dex 13.
    /// Armour: 2014 scale mail and shield (medium; 17, half plate 18 at 11); 2024 Divine Order: Protector (heavy armour:
    /// chain shirt and shield 16, splint 19 at 5, plate 20 at 11). Front line. Not modelled: other spells, Channel
    /// Divinity, Divine Intervention, 2024 Improved Blessed Strikes' temporary hit points.
    /// </summary>
    internal static ArchetypeDefinition Cleric(string edition)
    {
        var e2024 = edition == V.Editions.E2024;
        var modifiers = new List<ModifierSpec>
        {
            SacredFlame(untilLevel: e2024 ? 6 : null, potent: false),
        };
        if (e2024)
        {
            modifiers.Add(SacredFlame(untilLevel: null, potent: true));
        }

        modifiers.Add(new ModifierSpec
        {
            Kind = V.Kinds.SaveEffect,
            Name = "Spirit Guardians",
            Ability = V.Abilities.Wis,
            DcAbility = V.Abilities.Wis,
            Dice = Lv.Of("3d8"),
            Type = "radiant",
            OnSuccess = V.OnSuccess.Half,
            Shape = V.Shapes.Sphere,
            Size = 15,
            ActionCost = V.ActionCosts.None,
            Concentration = true,
            Setup = V.Setup.Action,
            FromLevel = 5,
        });
        modifiers.Add(HealingWord(e2024, V.Abilities.Wis));

        return new ArchetypeDefinition
        {
            Name = "cleric",
            Title = "Cleric",
            Edition = edition,
            HitDie = 8,
            Position = SimulationValues.Positions.Front,
            Abilities = AbilityTrack.Standard(
                [V.Abilities.Wis, V.Abilities.Con, V.Abilities.Dex, V.Abilities.Str, V.Abilities.Cha, V.Abilities.Int],
                AbilityTrack.StandardAsiLevels(edition)),
            Armor = e2024
                ? new ArmorPlan(Armor.ChainShirt, ArmorPlan.Any, Shield: true)
                : new ArmorPlan(Armor.ScaleMail, ArmorPlan.LightOrMedium, Shield: true),
            SaveProficiencies = [V.Abilities.Wis, V.Abilities.Cha],
            Attacks =
            [
                SpellAttack("Spiritual Weapon", V.Abilities.Wis, "1d8", "force", ranged: false, addsModifier: true,
                    action: V.AttackActions.BonusAction, fromLevel: 3, untilLevel: e2024 ? 4 : null),
            ],
            Modifiers = modifiers,
            Routine = $"Sacred Flame{(e2024 ? " (+Wis from 7)" : string.Empty)}, Spiritual Weapon from 3{(e2024 ? " to 4" : string.Empty)}, " +
                      $"Spirit Guardians from 5, Healing Word ({(e2024 ? "2d4" : "1d4")} + Wis)",
            Notes =
            [
                "Spirit Guardians: cast with the Action in round 1, then 3d8 radiant to 3 creatures (15-ft emanation, DMG table) each round.",
                e2024
                    ? "Divine Order: Protector (heavy armour). Blessed Strikes: Potent Spellcasting (Wis on Sacred Flame from 7). Spiritual Weapon needs Concentration in 2024, so it ends at 4."
                    : "Spiritual Weapon (no Concentration in 2014) and Spirit Guardians run together from 5.",
                "Healing Word uses the 1st-level slots; Spiritual Weapon and Spirit Guardians are one cast per fight.",
                NoUpcasting,
                "Not modelled: other spells, Channel Divinity, Divine Intervention.",
            ],
        };

        ModifierSpec SacredFlame(int? untilLevel, bool potent) => new()
        {
            Kind = V.Kinds.SaveEffect,
            Name = potent ? "Sacred Flame (Potent Spellcasting)" : "Sacred Flame",
            Ability = V.Abilities.Dex,
            DcAbility = V.Abilities.Wis,
            Dice = Lv.Of("1d8"),
            Amount = potent ? Lv.Of(V.Abilities.Wis) : null,
            Type = "radiant",
            OnSuccess = V.OnSuccess.None,
            Cantrip = true,
            FromLevel = potent ? 7 : null,
            UntilLevel = untilLevel,
        };
    }

    /// <summary>
    /// <b>Druid</b>: Produce Flame (ranged spell attack, 1d8 fire, cantrip scaling; 2024 Elemental Fury: Potent
    /// Spellcasting adds the Wis modifier from 7), Call Lightning from 5 (3d10 lightning, Dex save for half, on the
    /// creatures within 5 feet of a point — 1 by the DMG table; each later turn the Action calls another bolt, so one
    /// 3rd-level slot a fight and no uses; Concentration), Cure Wounds (2014 1d8 + Wis, 2024 2d8 + Wis, Action; the
    /// 1st-level slots). Wis 17, Con 15, Dex 13. Armour: leather and shield (14); from 5, 2014 hide (non-metal medium) and
    /// 2024 studded leather (Primal Order: Magician, light armour only): 15. Back line. Not modelled: Wild Shape, other
    /// spells, Wild Companion, Archdruid.
    /// </summary>
    internal static ArchetypeDefinition Druid(string edition)
    {
        var e2024 = edition == V.Editions.E2024;
        var modifiers = new List<ModifierSpec>();
        if (e2024)
        {
            modifiers.Add(new ModifierSpec
            {
                Kind = V.Kinds.BonusDamage,
                Name = "Potent Spellcasting",
                Amount = Lv.Of(V.Abilities.Wis),
                Attacks = ["Produce Flame"],
                FromLevel = 7,
            });
        }

        modifiers.Add(new ModifierSpec
        {
            Kind = V.Kinds.SaveEffect,
            Name = "Call Lightning",
            Ability = V.Abilities.Dex,
            DcAbility = V.Abilities.Wis,
            Dice = Lv.Of("3d10"),
            Type = "lightning",
            OnSuccess = V.OnSuccess.Half,
            Shape = V.Shapes.Cylinder,
            Size = 5,
            Concentration = true,
            FromLevel = 5,
        });
        modifiers.Add(new ModifierSpec
        {
            Kind = V.Kinds.Heal,
            Name = "Cure Wounds",
            Dice = Lv.Of(e2024 ? "2d8" : "1d8"),
            Amount = Lv.Of(V.Abilities.Wis),
            ActionCost = V.ActionCosts.Action,
            Resource = FirstLevelSlots(),
        });

        return new ArchetypeDefinition
        {
            Name = "druid",
            Title = "Druid",
            Edition = edition,
            HitDie = 8,
            Position = SimulationValues.Positions.Back,
            Abilities = AbilityTrack.Standard(
                [V.Abilities.Wis, V.Abilities.Con, V.Abilities.Dex, V.Abilities.Int, V.Abilities.Cha, V.Abilities.Str],
                AbilityTrack.StandardAsiLevels(edition)),
            Armor = e2024
                ? new ArmorPlan(Armor.Leather, ArmorPlan.LightOnly, Shield: true)
                : new ArmorPlan(Armor.Leather, a => !a.Metal && ArmorPlan.LightOrMedium(a), Shield: true),
            SaveProficiencies = [V.Abilities.Int, V.Abilities.Wis],
            Attacks = [SpellAttack("Produce Flame", V.Abilities.Wis, "1d8", "fire", ranged: true, cantrip: V.Cantrips.Dice)],
            Modifiers = modifiers,
            Routine = $"Produce Flame{(e2024 ? " (+Wis from 7)" : string.Empty)}, Call Lightning from 5, Cure Wounds ({(e2024 ? "2d8" : "1d8")} + Wis)",
            Notes =
            [
                "Call Lightning: one 3rd-level slot a fight, then a bolt with the Action every turn (3d10, 1 creature by the DMG table).",
                e2024
                    ? "Primal Order: Magician (light armour). Elemental Fury: Potent Spellcasting (Wis on Produce Flame from 7)."
                    : "Druids do not wear metal: leather, then hide, with a wooden shield.",
                "Cure Wounds uses the 1st-level slots.",
                NoUpcasting,
                "Not modelled: Wild Shape, other spells, Archdruid.",
            ],
        };
    }

    /// <summary>
    /// <b>Wizard</b>: Fire Bolt (ranged spell attack, 1d10 fire, cantrip scaling) and Fireball from 5 (8d6 fire, Dex save
    /// for half, a 20-foot sphere: 4 creatures by the DMG table; every 3rd-level-or-higher slot is a use), with Acid Splash
    /// (Dex save, 1d6 acid) as a second cantrip so a fire-immune foe (a brass or gold dragon, a balor) still takes damage —
    /// the simulator casts it only when it beats Fire Bolt. Int 17, Con 15,
    /// Dex 13. Mage Armor (13 + Dex) cast before the day's first fight. Back line. Not modelled: other spells (Shield,
    /// Magic Missile …), Arcane Recovery, Spell Mastery, Signature Spells.
    /// </summary>
    internal static ArchetypeDefinition Wizard(string edition) => Blaster(
        edition, "wizard", "Wizard", V.Abilities.Int,
        new ModifierSpec
        {
            Kind = V.Kinds.SaveEffect,
            Name = "Fireball",
            Ability = V.Abilities.Dex,
            DcAbility = V.Abilities.Int,
            Dice = Lv.Of("8d6"),
            Type = "fire",
            OnSuccess = V.OnSuccess.Half,
            Shape = V.Shapes.Sphere,
            Size = 20,
            Resource = Resource(5, l => SpellSlots.FullAtOrAbove(l, 3), V.Rests.LongRest),
            FromLevel = 5,
        },
        [V.Abilities.Int, V.Abilities.Wis],
        "Fire Bolt (Acid Splash against fire-immune foes), Fireball from 5",
        ["Fireball: 8d6 fire to 4 creatures (20-ft sphere, DMG table); uses = every slot of 3rd level or higher.",
         "Not modelled: other spells (Shield, Magic Missile, …), Arcane Recovery, Spell Mastery, Signature Spells."]);

    /// <summary>
    /// <b>Sorcerer</b>: Fire Bolt, Acid Splash as the fallback cantrip (as the wizard's), and Lightning Bolt from 5 (8d6
    /// lightning, Dex save for half, a 100-foot line: 4 creatures by the DMG table; every 3rd-level-or-higher slot is a use).
    /// 2024: Innate Sorcery, used with the Bonus
    /// Action in round 1 (a setup cost; it lasts the fight, two uses a day), gives Advantage on Fire Bolt and +1 to the
    /// spell save DC. Cha 17, Con 15, Dex 13. Mage Armor. Back line. Not modelled: other spells, Metamagic and Sorcery
    /// Points, Sorcery Incarnate, Arcane Apotheosis.
    /// </summary>
    internal static ArchetypeDefinition Sorcerer(string edition)
    {
        var e2024 = edition == V.Editions.E2024;
        var extra = new List<ModifierSpec>();
        if (e2024)
        {
            extra.Add(new ModifierSpec
            {
                Kind = V.Kinds.Advantage,
                Name = "Innate Sorcery",
                Attacks = ["Fire Bolt"],
                Setup = V.Setup.BonusAction,
            });
        }

        return Blaster(
            edition, "sorcerer", "Sorcerer", V.Abilities.Cha,
            new ModifierSpec
            {
                Kind = V.Kinds.SaveEffect,
                Name = "Lightning Bolt",
                Ability = V.Abilities.Dex,
                DcAbility = V.Abilities.Cha,
                DcBonus = e2024 ? 1 : null,
                Dice = Lv.Of("8d6"),
                Type = "lightning",
                OnSuccess = V.OnSuccess.Half,
                Shape = V.Shapes.Line,
                Size = 100,
                Resource = Resource(5, l => SpellSlots.FullAtOrAbove(l, 3), V.Rests.LongRest),
                FromLevel = 5,
            },
            [V.Abilities.Con, V.Abilities.Cha],
            e2024 ? "Fire Bolt with Innate Sorcery's Advantage (Acid Splash against fire-immune foes), Lightning Bolt from 5 (DC +1)" : "Fire Bolt (Acid Splash against fire-immune foes), Lightning Bolt from 5",
            [
                "Lightning Bolt: 8d6 lightning to 4 creatures (100-ft line, DMG table); uses = every slot of 3rd level or higher.",
                .. e2024
                    ? (string[])["Innate Sorcery: Bonus Action in round 1, kept all fight: Advantage on Fire Bolt, spell save DC +1."]
                    : [],
                e2024
                    ? "Not modelled: other spells, Metamagic and Sorcery Points, Sorcery Incarnate, Arcane Apotheosis."
                    : "Not modelled: other spells, Metamagic and Sorcery Points.",
            ],
            extra);
    }

    /// <summary>
    /// <b>Warlock</b>: Eldritch Blast (1d10 force per beam, beams 1/2/3/4 at 1/5/11/17) with Agonizing Blast (+Cha per
    /// beam from 2, both editions' prerequisite) and Hex (+1d6 necrotic per hit, Concentration) cast with the Bonus Action
    /// in round 1 — a setup cost, unlike the warlock_baseline preset, which assumes Hex is already up. One Pact Magic slot a
    /// fight goes to Hex. Cha 17, Con 15, Dex 13 (the baseline's Cha +3/+4/+5 at 1/4/8). Armor of Shadows (Mage Armor at
    /// will) from 2 (2014) / 1 (2024) over leather. Back line. Not modelled: other spells, Mystic Arcanum, Pact Boons,
    /// other invocations, Magical Cunning / Eldritch Master.
    /// </summary>
    internal static ArchetypeDefinition Warlock(string edition)
    {
        var e2024 = edition == V.Editions.E2024;
        return new ArchetypeDefinition
        {
            Name = "warlock",
            Title = "Warlock",
            Edition = edition,
            HitDie = 8,
            Position = SimulationValues.Positions.Back,
            Abilities = AbilityTrack.Standard(
                [V.Abilities.Cha, V.Abilities.Con, V.Abilities.Dex, V.Abilities.Wis, V.Abilities.Int, V.Abilities.Str],
                AbilityTrack.StandardAsiLevels(edition)),
            Armor = new ArmorPlan(Armor.Leather, ArmorPlan.LightOnly, Shield: false, ArmorPlan.MageArmor(e2024 ? 1 : 2, "Armor of Shadows")),
            SaveProficiencies = [V.Abilities.Wis, V.Abilities.Cha],
            Attacks = [SpellAttack("Eldritch Blast", V.Abilities.Cha, "1d10", "force", ranged: true, cantrip: V.Cantrips.Beams)],
            Modifiers =
            [
                new ModifierSpec
                {
                    Kind = V.Kinds.BonusDamage,
                    Name = "Agonizing Blast",
                    Amount = Lv.Of(V.Abilities.Cha),
                    Attacks = ["Eldritch Blast"],
                    FromLevel = 2,
                },
                new ModifierSpec
                {
                    Kind = V.Kinds.ExtraDamage,
                    Name = "Hex",
                    Dice = Lv.Of("1d6"),
                    Type = "necrotic",
                    Concentration = true,
                    Setup = V.Setup.BonusAction,
                },
            ],
            Routine = "Eldritch Blast (beams 1/2/3/4 at 1/5/11/17) with Agonizing Blast from 2 and Hex (Bonus Action in round 1)",
            Notes =
            [
                "Hex: cast with the Bonus Action in round 1 (one Pact Magic slot a fight), kept all fight (Concentration).",
                $"Invocations: Agonizing Blast from 2 and Armor of Shadows from {(e2024 ? 1 : 2)} (Mage Armor at will).",
                "Not modelled: other spells, Mystic Arcanum, Pact Boon, other invocations.",
            ],
        };
    }

    /// <summary>
    /// <b>Bard</b>: 2014 Vicious Mockery (Wis save, 1d4 psychic, nothing on a success, cantrip scaling; the SRD bard's only
    /// damage cantrip); 2024 Starry Wisp (ranged spell attack, 1d8 radiant, cantrip scaling). Shatter from 3 (3d8 thunder,
    /// Con save for half, a 10-foot sphere: 2 creatures by the DMG table; every 2nd-level-or-higher slot is a use) and
    /// Healing Word (2014 1d4 + Cha, 2024 2d4 + Cha, Bonus Action; the 1st-level slots). Cha 17, Con 15, Dex 13. Leather,
    /// studded leather from 5. Back line. Not modelled: Bardic Inspiration (it helps allies, which a build cannot say),
    /// Vicious Mockery's Disadvantage rider, other spells, Magical Secrets, Countercharm.
    /// </summary>
    internal static ArchetypeDefinition Bard(string edition)
    {
        var e2024 = edition == V.Editions.E2024;
        var modifiers = new List<ModifierSpec>();
        if (!e2024)
        {
            modifiers.Add(new ModifierSpec
            {
                Kind = V.Kinds.SaveEffect,
                Name = "Vicious Mockery",
                Ability = V.Abilities.Wis,
                DcAbility = V.Abilities.Cha,
                Dice = Lv.Of("1d4"),
                Type = "psychic",
                OnSuccess = V.OnSuccess.None,
                Cantrip = true,
            });
        }

        modifiers.Add(new ModifierSpec
        {
            Kind = V.Kinds.SaveEffect,
            Name = "Shatter",
            Ability = V.Abilities.Con,
            DcAbility = V.Abilities.Cha,
            Dice = Lv.Of("3d8"),
            Type = "thunder",
            OnSuccess = V.OnSuccess.Half,
            Shape = V.Shapes.Sphere,
            Size = 10,
            Resource = Resource(3, l => SpellSlots.FullAtOrAbove(l, 2), V.Rests.LongRest),
            FromLevel = 3,
        });
        modifiers.Add(HealingWord(e2024, V.Abilities.Cha));

        return new ArchetypeDefinition
        {
            Name = "bard",
            Title = "Bard",
            Edition = edition,
            HitDie = 8,
            Position = SimulationValues.Positions.Back,
            Abilities = AbilityTrack.Standard(
                [V.Abilities.Cha, V.Abilities.Con, V.Abilities.Dex, V.Abilities.Wis, V.Abilities.Int, V.Abilities.Str],
                AbilityTrack.StandardAsiLevels(edition)),
            Armor = new ArmorPlan(Armor.Leather, ArmorPlan.LightOnly, Shield: false),
            SaveProficiencies = [V.Abilities.Dex, V.Abilities.Cha],
            Attacks = e2024 ? [SpellAttack("Starry Wisp", V.Abilities.Cha, "1d8", "radiant", ranged: true, cantrip: V.Cantrips.Dice)] : [],
            Modifiers = modifiers,
            Routine = $"{(e2024 ? "Starry Wisp" : "Vicious Mockery")}, Shatter from 3, Healing Word ({(e2024 ? "2d4" : "1d4")} + Cha)",
            Notes =
            [
                "Shatter: 3d8 thunder to 2 creatures (10-ft sphere, DMG table); uses = every slot of 2nd level or higher. Healing Word uses the 1st-level slots.",
                e2024
                    ? "Starry Wisp is the 2024 bard's damage cantrip."
                    : "Vicious Mockery's Disadvantage rider is not modelled (the DSL has no such condition).",
                NoUpcasting,
                "Not modelled: Bardic Inspiration (an ally's die), other spells, Magical Secrets, Countercharm.",
            ],
        };
    }

    // Wizard and sorcerer: a cantrip, one area spell, Mage Armor, d6 hit die, back line.
    private static ArchetypeDefinition Blaster(
        string edition, string name, string title, string ability, ModifierSpec area, IReadOnlyList<string> saves, string routine,
        IReadOnlyList<string> notes, IReadOnlyList<ModifierSpec>? extra = null) => new()
    {
        Name = name,
        Title = title,
        Edition = edition,
        HitDie = 6,
        Position = SimulationValues.Positions.Back,
        Abilities = AbilityTrack.Standard(
            [ability, V.Abilities.Con, V.Abilities.Dex, V.Abilities.Wis, ability == V.Abilities.Int ? V.Abilities.Cha : V.Abilities.Int, V.Abilities.Str],
            AbilityTrack.StandardAsiLevels(edition)),
        Armor = new ArmorPlan(null, _ => false, Shield: false, ArmorPlan.MageArmor()),
        SaveProficiencies = saves,
        Attacks = [SpellAttack("Fire Bolt", ability, "1d10", "fire", ranged: true, cantrip: V.Cantrips.Dice)],
        Modifiers =
        [
            area,
            new ModifierSpec
            {
                Kind = V.Kinds.SaveEffect,
                Name = "Acid Splash",
                Ability = V.Abilities.Dex,
                DcAbility = ability,
                Dice = Lv.Of("1d6"),
                Type = "acid",
                OnSuccess = V.OnSuccess.None,
                Cantrip = true,
            },
            .. extra ?? [],
        ],
        Routine = routine,
        Notes =
        [
            .. notes,
            "Acid Splash: Dex save, 1d6 acid (cantrip scaling), 1 creature; cast instead of Fire Bolt when it does more.",
            "Mage Armor (13 + Dex) is cast before the day's first fight.",
            NoUpcasting,
        ],
    };

    private static ModifierSpec HealingWord(bool e2024, string ability) => new()
    {
        Kind = V.Kinds.Heal,
        Name = "Healing Word",
        Dice = Lv.Of(e2024 ? "2d4" : "1d4"),
        Amount = Lv.Of(ability),
        ActionCost = V.ActionCosts.BonusAction,
        Resource = FirstLevelSlots(),
    };

    // The 1st-level slots of a full caster: 2, 3 at 2, 4 from 3.
    private static ResourceSpec FirstLevelSlots() => Resource(1, l => SpellSlots.FullExactly(l, 1), V.Rests.LongRest);
}
