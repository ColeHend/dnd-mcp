using DndMcp.Domain.Features;
using static DndMcp.Domain.Simulation.Archetypes.ArchetypeParts;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Simulation.Archetypes;

/// <summary>
/// The weapon-using archetypes: fighter, barbarian, paladin, ranger, rogue, monk. Each is one subclass-free build per
/// edition, following that edition's class table (checked against the SRD with the rules tools: Extra Attack levels,
/// Sneak Attack dice, Rage damage, Martial Arts dice, smite wording, slot counts). Shared rules — the ability plan, HP,
/// armour tiers, the +1 weapon at 11 — are stated on <see cref="AbilityTrack"/>, <see cref="ArchetypeHitPoints"/>,
/// <see cref="ArmorPlan"/> and <see cref="ArchetypeParts"/>.
/// </summary>
internal static class MartialArchetypes
{
    /// <summary>
    /// <b>Fighter</b>: a greatsword (2d6 slashing, heavy, two-handed) with Great Weapon Fighting (the edition's), one attack
    /// per Attack action, two at 5, three at 11, four at 20; Action Surge from 2 (one use per short rest, two from 17: a
    /// second Attack action's worth of attacks); Second Wind (1d10 + level, Bonus Action, self): 2014 once per short
    /// rest, 2024 two uses (three at 4, four at 10) per long rest. 2024: the greatsword's mastery is Graze. Str 17, Con 15
    /// → Str 20 by 6; ASIs 4, 6, 8, 12, 14, 16 (+19 in 2014). Heavy armour, no shield: chain mail 16, splint 17 from 5,
    /// plate 18 from 11. Not modelled: Indomitable (a save reroll), 2024 Tactical Mind/Shift/Master and Studied Attacks.
    /// </summary>
    internal static ArchetypeDefinition Fighter(string edition)
    {
        var e2024 = edition == V.Editions.E2024;
        static int Attacks(int level) => level switch { < 5 => 1, < 11 => 2, < 20 => 3, _ => 4 };
        var weapon = WeaponWithPlusOne(
            "Greatsword", "2d6", "slashing", V.Abilities.Str, [V.Properties.Melee, V.Properties.Heavy, V.Properties.TwoHanded], Attacks,
            mastery: e2024 ? V.Masteries.Graze : null);
        var actionSurge = PerWeapon("Greatsword", 2, (attack, from, until) => new ModifierSpec
        {
            Kind = V.Kinds.ExtraAttack,
            Name = "Action Surge",
            Attack = attack,
            Count = Lv.ByLevel(from, until, Attacks),
            Action = V.ExtraAttackActions.Action,
            Resource = Resource(Lv.ByLevel(from, until, l => l >= 17 ? 2 : 1), V.Rests.ShortRest),
            FromLevel = from,
            UntilLevel = until,
        });

        return new ArchetypeDefinition
        {
            Name = "fighter",
            Title = "Fighter",
            Edition = edition,
            HitDie = 10,
            Position = ArchetypeCatalog.Front,
            Abilities = AbilityTrack.Standard(
                [V.Abilities.Str, V.Abilities.Con, V.Abilities.Dex, V.Abilities.Wis, V.Abilities.Cha, V.Abilities.Int],
                e2024 ? [4, 6, 8, 12, 14, 16] : [4, 6, 8, 12, 14, 16, 19]),
            Armor = new ArmorPlan(Armor.ChainMail, ArmorPlan.Any, Shield: false),
            SaveProficiencies = [V.Abilities.Str, V.Abilities.Con],
            FightingStyle = V.FightingStyles.Gwf,
            Attacks = weapon,
            Modifiers =
            [
                .. actionSurge,
                new ModifierSpec
                {
                    Kind = V.Kinds.Heal,
                    Name = "Second Wind",
                    Dice = Lv.Of("1d10"),
                    Amount = Lv.ByLevel(1, 20, l => l),
                    ActionCost = V.ActionCosts.BonusAction,
                    SelfOnly = true,
                    Resource = e2024
                        ? Resource(Lv.Steps((1, 2), (4, 3), (10, 4)), V.Rests.LongRest)
                        : Resource(Lv.Of(1), V.Rests.ShortRest),
                },
            ],
            Routine = $"greatsword 2d6 with Great Weapon Fighting{(e2024 ? " and Graze" : string.Empty)}, 1/2/3/4 attacks at 1/5/11/20, " +
                      "Action Surge from 2, Second Wind",
            Notes =
            [
                e2024
                    ? "Second Wind: 1d10 + level as a Bonus Action, 2 uses (3 at 4, 4 at 10) per long rest."
                    : "Second Wind: 1d10 + level as a Bonus Action, once per short rest.",
                "Action Surge: one extra Attack action per short rest (two uses from 17).",
                e2024
                    ? "Not modelled: Indomitable, Tactical Mind, Tactical Shift, Tactical Master, Studied Attacks."
                    : "Not modelled: Indomitable.",
            ],
        };
    }

    /// <summary>
    /// <b>Barbarian</b>: a greataxe (1d12 slashing), Extra Attack at 5, always raging: Rage is entered with the Bonus Action
    /// in round 1 (a setup cost) and lasts the fight (a Rage lasts 10 rounds, and even 2 uses a day cover a day's fights
    /// one at a time), giving Rage Damage +2/+3/+4 at 1/9/16 on every Strength attack and Resistance to bludgeoning,
    /// piercing and slashing (three resistance modifiers). 2014: Brutal Critical adds 1/2/3 greataxe dice to a crit at
    /// 9/13/17. 2024: the greataxe's mastery is Cleave. Unarmored Defense (10 + Dex + Con) until medium armour is better
    /// (scale mail 15 at 5, half plate 16 at 11), no shield. Primal Champion at 20: Str and Con +4. <b>Reckless Attack
    /// is left out</b>: its cost (attacks against the barbarian have Advantage) cannot be written in the DSL, and giving
    /// the Advantage without the cost would flatter the party. Also not modelled: Danger Sense, Relentless Rage, Feral
    /// Instinct, Rage's Strength-save Advantage, 2024 Brutal Strike (it needs Reckless Attack).
    /// </summary>
    internal static ArchetypeDefinition Barbarian(string edition)
    {
        var e2024 = edition == V.Editions.E2024;
        static int Attacks(int level) => level < 5 ? 1 : 2;
        var modifiers = new List<ModifierSpec>
        {
            new()
            {
                Kind = V.Kinds.BonusDamage,
                Name = "Rage",
                Amount = Lv.Steps((1, 2), (9, 3), (16, 4)),
                Setup = V.Setup.BonusAction,
            },
        };
        modifiers.AddRange(new[] { "bludgeoning", "piercing", "slashing" }.Select(type => new ModifierSpec
        {
            Kind = V.Kinds.Resistance,
            Name = $"Rage ({type})",
            Type = type,
        }));
        if (!e2024)
        {
            modifiers.Add(new ModifierSpec
            {
                Kind = V.Kinds.ExtraDamage,
                Name = "Brutal Critical",
                Dice = Lv.Steps((9, "1d12"), (13, "2d12"), (17, "3d12")),
                When = V.When.OnCrit,
                FromLevel = 9,
            });
        }

        return new ArchetypeDefinition
        {
            Name = "barbarian",
            Title = "Barbarian",
            Edition = edition,
            HitDie = 12,
            Position = ArchetypeCatalog.Front,
            Abilities = AbilityTrack.Standard(
                [V.Abilities.Str, V.Abilities.Con, V.Abilities.Dex, V.Abilities.Wis, V.Abilities.Cha, V.Abilities.Int],
                AbilityTrack.StandardAsiLevels(edition), [V.Abilities.Str, V.Abilities.Con], e2024 ? 25 : 24),
            Armor = new ArmorPlan(null, ArmorPlan.LightOrMedium, Shield: false, ArmorPlan.UnarmoredDefense(V.Abilities.Con)),
            SaveProficiencies = [V.Abilities.Str, V.Abilities.Con],
            Attacks = WeaponWithPlusOne(
                "Greataxe", "1d12", "slashing", V.Abilities.Str, [V.Properties.Melee, V.Properties.Heavy, V.Properties.TwoHanded], Attacks,
                mastery: e2024 ? V.Masteries.Cleave : null),
            Modifiers = modifiers,
            Routine = $"greataxe 1d12{(e2024 ? " with Cleave" : string.Empty)}, Extra Attack at 5, raging every fight " +
                      $"(+2/+3/+4 damage at 1/9/16, resistance to bludgeoning, piercing, slashing){(e2024 ? string.Empty : ", Brutal Critical from 9")}",
            Notes =
            [
                "Rage: entered with the Bonus Action in round 1 and kept all fight (one Rage per fight; uses are not tracked).",
                "Reckless Attack is left out: the DSL cannot give its cost (enemies' Advantage against the barbarian).",
                e2024
                    ? "Not modelled: Danger Sense, Relentless Rage, Feral Instinct, Rage's Strength-save Advantage, Brutal Strike."
                    : "Not modelled: Danger Sense, Relentless Rage, Feral Instinct, Rage's Strength-save Advantage.",
            ],
        };
    }

    /// <summary>
    /// <b>Paladin</b>: a longsword (1d8 slashing) and a shield with the Dueling style (+2 damage from 2, when the fighting
    /// style is gained), Extra Attack at 5. Divine Smite from 2 as a 2d8 radiant rider using a 1st-level slot's dice (no
    /// upcasting): 2014 on any melee hit (every_hit, spent while slots last), 2024 the spell — once per turn, cast with the
    /// Bonus Action right after a hit — with one free cast a day (Paladin's Smite). Its uses are every spell slot (the
    /// archetype casts nothing else). Improved Divine Smite / Radiant Strikes (+1d8 radiant per melee hit) at 11. Lay on
    /// Hands: the whole pool (5 × level) on one creature, 2014 an Action, 2024 a Bonus Action, once a day. 2024: Sap
    /// mastery. Heavy armour and shield: 18, 19 at 5, 20 at 11. Aura of Protection (from 6) adds the Cha modifier (min 1)
    /// to the paladin's OWN saves; the allies' share is not modelled. Str 17, Con 15, Cha 13.
    /// </summary>
    internal static ArchetypeDefinition Paladin(string edition)
    {
        var e2024 = edition == V.Editions.E2024;
        static int Attacks(int level) => level < 5 ? 1 : 2;
        var smite = new ModifierSpec
        {
            Kind = V.Kinds.ExtraDamage,
            Name = "Divine Smite",
            Dice = Lv.Of("2d8"),
            Type = "radiant",
            When = e2024 ? V.When.FirstHitPerTurn : V.When.EveryHit,
            ActionCost = e2024 ? V.ActionCosts.BonusAction : null,
            Resource = Resource(2, l => SpellSlots.HalfTotal(l, edition) + (e2024 ? 1 : 0), V.Rests.LongRest),
            FromLevel = 2,
        };

        return new ArchetypeDefinition
        {
            Name = "paladin",
            Title = "Paladin",
            Edition = edition,
            HitDie = 10,
            Position = ArchetypeCatalog.Front,
            Abilities = AbilityTrack.Standard(
                [V.Abilities.Str, V.Abilities.Con, V.Abilities.Cha, V.Abilities.Wis, V.Abilities.Dex, V.Abilities.Int], AbilityTrack.StandardAsiLevels(edition)),
            Armor = new ArmorPlan(Armor.ChainMail, ArmorPlan.Any, Shield: true),
            SaveProficiencies = [V.Abilities.Wis, V.Abilities.Cha],
            SaveBonus = (level, a) => level >= 6 ? Math.Max(1, a.Modifier(V.Abilities.Cha, level)) : 0,
            Attacks = WeaponWithPlusOne(
                "Longsword", "1d8", "slashing", V.Abilities.Str, [V.Properties.Melee, V.Properties.Versatile], Attacks,
                mastery: e2024 ? V.Masteries.Sap : null),
            Modifiers =
            [
                new ModifierSpec { Kind = V.Kinds.BonusDamage, Name = "Dueling", Amount = Lv.Of(2), FromLevel = 2 },
                smite,
                new ModifierSpec
                {
                    Kind = V.Kinds.ExtraDamage,
                    Name = e2024 ? "Radiant Strikes" : "Improved Divine Smite",
                    Dice = Lv.Of("1d8"),
                    Type = "radiant",
                    FromLevel = 11,
                },
                new ModifierSpec
                {
                    Kind = V.Kinds.Heal,
                    Name = "Lay on Hands",
                    Amount = Lv.ByLevel(1, 20, l => 5 * l),
                    ActionCost = e2024 ? V.ActionCosts.BonusAction : V.ActionCosts.Action,
                    Resource = Resource(Lv.Of(1), V.Rests.LongRest),
                },
            ],
            Routine = "longsword 1d8 and shield with Dueling from 2, Extra Attack at 5, Divine Smite 2d8 from 2 " +
                      $"({(e2024 ? "once per turn with the Bonus Action" : "on any hit")}), +1d8 radiant per hit from 11, Lay on Hands",
            Notes =
            [
                e2024
                    ? "Divine Smite: the 2024 spell (Bonus Action after a hit, once per turn), 2d8 per cast (no upcasting); uses = every spell slot + the free cast."
                    : "Divine Smite: 2d8 radiant on any melee hit while slots last (no upcasting); uses = every spell slot.",
                "Lay on Hands: the whole pool (5 x level) in one use.",
                "Aura of Protection: the Cha modifier (min 1) on the paladin's own saves from 6; allies' aura bonus not modelled.",
                "Not modelled: other spells, Channel Divinity, Aura of Courage, Divine Health.",
            ],
        };
    }

    /// <summary>
    /// <b>Ranger</b>: a longbow (1d8 piercing, Dex) with Archery (+2 to hit from 2), Extra Attack at 5, and Hunter's Mark
    /// (+1d6 on every hit, Concentration) cast with the Bonus Action in round 1 (a setup cost; one cast per fight): 2014
    /// from 2 (a spell slot), the weapon's damage type; 2024 from 1 (Favored Enemy's free casts), Force, a d10 at 20 (Foe
    /// Slayer), and Advantage on every attack from 17 (Precise Hunter: the mark is up from the first attack). 2024: the
    /// longbow's mastery is Slow (no effect without a grid). Dex 17, Con 15, Wis 13. Armour: 2014 scale mail (16), 2024
    /// studded leather; then the best light or medium armour of the tier, no shield. Not modelled: other spells, Favored
    /// Enemy's 2014 benefits, Vanish, Feral Senses, 2014 Foe Slayer.
    /// </summary>
    internal static ArchetypeDefinition Ranger(string edition)
    {
        var e2024 = edition == V.Editions.E2024;
        static int Attacks(int level) => level < 5 ? 1 : 2;
        var modifiers = new List<ModifierSpec>
        {
            new() { Kind = V.Kinds.ToHit, Name = "Archery", Amount = Lv.Of(2), FromLevel = 2 },
            new()
            {
                Kind = V.Kinds.ExtraDamage,
                Name = "Hunter's Mark",
                Dice = e2024 ? Lv.Steps((1, "1d6"), (20, "1d10")) : Lv.Of("1d6"),
                Type = e2024 ? "force" : null,
                Concentration = true,
                Setup = V.Setup.BonusAction,
                FromLevel = e2024 ? 1 : 2,
            },
        };
        if (e2024)
        {
            modifiers.Add(new ModifierSpec { Kind = V.Kinds.Advantage, Name = "Precise Hunter", FromLevel = 17 });
        }

        return new ArchetypeDefinition
        {
            Name = "ranger",
            Title = "Ranger",
            Edition = edition,
            HitDie = 10,
            Position = ArchetypeCatalog.Back,
            Abilities = AbilityTrack.Standard(
                [V.Abilities.Dex, V.Abilities.Con, V.Abilities.Wis, V.Abilities.Str, V.Abilities.Int, V.Abilities.Cha], AbilityTrack.StandardAsiLevels(edition)),
            Armor = new ArmorPlan(e2024 ? Armor.StuddedLeather : Armor.ScaleMail, ArmorPlan.LightOrMedium, Shield: false),
            SaveProficiencies = [V.Abilities.Str, V.Abilities.Dex],
            Attacks = WeaponWithPlusOne(
                "Longbow", "1d8", "piercing", V.Abilities.Dex, [V.Properties.Ranged, V.Properties.Heavy, V.Properties.TwoHanded], Attacks,
                mastery: e2024 ? V.Masteries.Slow : null),
            Modifiers = modifiers,
            Routine = $"longbow 1d8 with Archery from 2, Extra Attack at 5, Hunter's Mark from {(e2024 ? "1 (d10 at 20, Advantage from 17)" : "2")}",
            Notes =
            [
                "Hunter's Mark: cast with the Bonus Action in round 1, kept all fight (Concentration).",
                e2024
                    ? "Not modelled: other spells, Deft Explorer, Roving, Tireless, Relentless Hunter, Nature's Veil, Feral Senses."
                    : "Not modelled: other spells, Favored Enemy, Natural Explorer, Primeval Awareness, Vanish, Feral Senses, Foe Slayer.",
            ],
        };
    }

    /// <summary>
    /// <b>Rogue</b>: two-weapon melee with Sneak Attack (1d6 per two rogue levels, rounded up; once per turn on the first
    /// hit — the archetype assumes an ally beside the target every turn, which the DSL cannot check). 2014: a shortsword
    /// with the Attack action and an offhand shortsword with the Bonus Action (no ability modifier on its damage). 2024: a
    /// shortsword (Vex) and a scimitar made as part of the Attack action through Nick (so the offhand attack costs no
    /// Bonus Action), and Steady Aim from 3 (Bonus Action: Advantage on the turn's first attack; the rogue does not move).
    /// Dex 17, Con 15, Wis 13; ASIs 4, 8, 10, 12, 16 (+19 in 2014). Leather armour, studded leather from 5. Slippery
    /// Mind at 15 adds Wis saves (2024: Wis and Cha). Not modelled: Cunning Action, Uncanny Dodge, Evasion (the DSL
    /// has no defensive reactions or Evasion), 2024 Cunning Strike.
    /// </summary>
    internal static ArchetypeDefinition Rogue(string edition)
    {
        var e2024 = edition == V.Editions.E2024;
        IReadOnlyList<string> finesse = [V.Properties.Melee, V.Properties.Finesse, V.Properties.Light];
        var attacks = new List<AttackSpec>(WeaponWithPlusOne(
            "Shortsword", "1d6", "piercing", V.Abilities.Dex, finesse, _ => 1, mastery: e2024 ? V.Masteries.Vex : null));
        attacks.Add(new AttackSpec
        {
            Name = e2024 ? "Scimitar" : "Offhand Shortsword",
            Action = e2024 ? V.AttackActions.Action : V.AttackActions.BonusAction,
            ToHit = new ToHitSpec { Ability = V.Abilities.Dex },
            Damage = Lv.Of("1d6"),
            DamageType = e2024 ? "slashing" : "piercing",
            Properties = finesse,
            Offhand = true,
            Mastery = e2024 ? V.Masteries.Nick : null,
        });
        var modifiers = new List<ModifierSpec>
        {
            new()
            {
                Kind = V.Kinds.ExtraDamage,
                Name = "Sneak Attack",
                Dice = Lv.ByLevel(1, 20, l => $"{(l + 1) / 2}d6"),
                When = V.When.FirstHitPerTurn,
            },
        };
        if (e2024)
        {
            modifiers.Add(new ModifierSpec { Kind = V.Kinds.Advantage, Name = "Steady Aim", Attacks = Both("Shortsword"), FromLevel = 3 });
        }

        return new ArchetypeDefinition
        {
            Name = "rogue",
            Title = "Rogue",
            Edition = edition,
            HitDie = 8,
            Position = ArchetypeCatalog.Front,
            Abilities = AbilityTrack.Standard(
                [V.Abilities.Dex, V.Abilities.Con, V.Abilities.Wis, V.Abilities.Int, V.Abilities.Cha, V.Abilities.Str],
                e2024 ? [4, 8, 10, 12, 16] : [4, 8, 10, 12, 16, 19]),
            Armor = new ArmorPlan(Armor.Leather, ArmorPlan.LightOnly, Shield: false),
            SaveProficiencies = [V.Abilities.Dex, V.Abilities.Int],
            LaterSaveProficiencies = level => level < 15 ? [] : e2024 ? [V.Abilities.Wis, V.Abilities.Cha] : [V.Abilities.Wis],
            Attacks = attacks,
            Modifiers = modifiers,
            Routine = e2024
                ? "shortsword (Vex) and scimitar (Nick) in the Attack action, Sneak Attack 1d6 per two levels, Steady Aim from 3"
                : "shortsword and offhand shortsword (Bonus Action), Sneak Attack 1d6 per two levels",
            Notes =
            [
                "Sneak Attack: on the first hit each turn, assuming an ally is always beside the target.",
                e2024
                    ? "Steady Aim: Advantage on the first attack of every turn from 3 (the rogue stays put)."
                    : "No Advantage source: hiding in combat is left to the table.",
                e2024
                    ? "Not modelled: Cunning Action, Cunning Strike, Uncanny Dodge, Evasion, Elusive, Stroke of Luck."
                    : "Not modelled: Cunning Action, Uncanny Dodge, Evasion, Elusive, Stroke of Luck.",
            ],
        };
    }

    /// <summary>
    /// <b>Monk</b>: Unarmed Strikes with the Martial Arts die (2014 d4/d6/d8/d10, 2024 d6/d8/d10/d12 at 1/5/11/17, Dex),
    /// Extra Attack at 5, a Bonus Action strike every turn (Martial Arts) or Flurry of Blows (two strikes, three from 10
    /// in 2024) for 1 ki/Focus Point, and Stunning Strike from 5 (Con save vs 8 + PB + Wis; once per turn, on the first
    /// hit, 1 point; 2014 stunned until the end of the monk's next turn, 2024 until the start of it). The points (= monk
    /// level from 2) are split, since the DSL has no shared pool: from 5 half (rounded down) go to Stunning Strike, the
    /// rest to Flurry. At 6 the strikes become magical (2014 Ki-Empowered Strikes) or Force (2024 Empowered Strikes), so
    /// the strike is two attacks split at 6 (as the +1 weapons split at 11). Unarmored Defense (10 + Dex + Wis); Dex 17,
    /// Wis 15, Con 13; 2024 Body and Mind at 20: Dex and Wis +4. All saves proficient from 14 (Diamond Soul /
    /// Disciplined Survivor). Not modelled: Deflect Missiles/Attacks, Evasion, Patient Defense, Step of the Wind, 2014
    /// every-hit Stunning Strike attempts (the archetype tries once a turn), 2024 Stunning Strike's rider on a success.
    /// </summary>
    internal static ArchetypeDefinition Monk(string edition)
    {
        var e2024 = edition == V.Editions.E2024;
        const int EmpoweredLevel = 6;
        const string Strike = "Unarmed Strike";
        var empowered = e2024 ? "Empowered Strike" : "Ki-Empowered Strike";
        string MartialArts(int level) => (e2024, level) switch
        {
            (true, < 5) => "1d6",
            (true, < 11) => "1d8",
            (true, < 17) => "1d10",
            (true, _) => "1d12",
            (false, < 5) => "1d4",
            (false, < 11) => "1d6",
            (false, < 17) => "1d8",
            _ => "1d10",
        };
        static int Stun(int level) => level < 5 ? 0 : level / 2;
        static int Flurry(int level) => level - Stun(level);

        IReadOnlyList<AttackSpec> attacks =
        [
            new AttackSpec
            {
                Name = Strike,
                Count = Lv.Steps((1, 1), (5, 2)),
                ToHit = new ToHitSpec { Ability = V.Abilities.Dex },
                Damage = Lv.ByLevel(1, EmpoweredLevel - 1, MartialArts),
                DamageType = "bludgeoning",
                Properties = [V.Properties.Melee],
                UntilLevel = EmpoweredLevel - 1,
            },
            new AttackSpec
            {
                Name = empowered,
                Count = Lv.Of(2),
                ToHit = new ToHitSpec { Ability = V.Abilities.Dex },
                Damage = Lv.ByLevel(EmpoweredLevel, 20, MartialArts),
                DamageType = e2024 ? "force" : "bludgeoning",
                Properties = e2024 ? [V.Properties.Melee] : [V.Properties.Melee, V.Properties.Magical],
                FromLevel = EmpoweredLevel,
            },
        ];

        var modifiers = new List<ModifierSpec>();
        foreach (var (attack, from, until) in new[] { (Strike, 1, EmpoweredLevel - 1), (empowered, EmpoweredLevel, 20) })
        {
            modifiers.Add(new ModifierSpec
            {
                Kind = V.Kinds.ExtraAttack,
                Name = "Martial Arts strike",
                Attack = attack,
                Action = V.ExtraAttackActions.BonusAction,
                FromLevel = from,
                UntilLevel = until,
            });
            modifiers.Add(new ModifierSpec
            {
                Kind = V.Kinds.ExtraAttack,
                Name = "Flurry of Blows",
                Attack = attack,
                Count = Lv.ByLevel(Math.Max(2, from), until, l => e2024 && l >= 10 ? 3 : 2),
                Action = V.ExtraAttackActions.BonusAction,
                Resource = Resource(Lv.ByLevel(Math.Max(2, from), until, Flurry), V.Rests.ShortRest),
                FromLevel = Math.Max(2, from),
                UntilLevel = until,
            });
        }

        modifiers.Add(new ModifierSpec
        {
            Kind = V.Kinds.ConditionOnHit,
            Name = "Stunning Strike",
            Condition = V.Conditions.Stunned,
            Ability = V.Abilities.Con,
            DcAbility = V.Abilities.Wis,
            When = V.When.FirstHitPerTurn,
            Resource = Resource(5, Stun, V.Rests.ShortRest),
            FromLevel = 5,
        });

        return new ArchetypeDefinition
        {
            Name = "monk",
            Title = "Monk",
            Edition = edition,
            HitDie = 8,
            Position = ArchetypeCatalog.Front,
            Abilities = AbilityTrack.Standard(
                [V.Abilities.Dex, V.Abilities.Wis, V.Abilities.Con, V.Abilities.Str, V.Abilities.Int, V.Abilities.Cha],
                AbilityTrack.StandardAsiLevels(edition), e2024 ? [V.Abilities.Dex, V.Abilities.Wis] : null, 25),
            Armor = new ArmorPlan(null, _ => false, Shield: false, ArmorPlan.UnarmoredDefense(V.Abilities.Wis)),
            SaveProficiencies = [V.Abilities.Str, V.Abilities.Dex],
            LaterSaveProficiencies = level => level >= 14 ? V.Abilities.All : [],
            Attacks = attacks,
            Modifiers = modifiers,
            Routine = $"Unarmed Strikes with the Martial Arts die ({(e2024 ? "d6-d12" : "d4-d10")}), Extra Attack at 5, a Bonus Action " +
                      $"strike or Flurry of Blows, Stunning Strike from 5, {(e2024 ? "Force" : "magical")} strikes from 6",
            Notes =
            [
                "Ki/Focus Points (= level from 2) are split: from 5 half go to Stunning Strike (once a turn), the rest to Flurry of Blows.",
                e2024
                    ? "Stunning Strike: stunned until the start of the monk's next turn; the rider on a successful save is not modelled."
                    : "Stunning Strike: tried once a turn (the rules allow every hit), stunned until the end of the monk's next turn.",
                e2024
                    ? "Not modelled: Deflect Attacks/Energy, Evasion, Patient Defense, Step of the Wind, Uncanny Metabolism, Self-Restoration, Superior Defense."
                    : "Not modelled: Deflect Missiles, Evasion, Patient Defense, Step of the Wind, Stillness of Mind, Empty Body.",
            ],
        };
    }
}
