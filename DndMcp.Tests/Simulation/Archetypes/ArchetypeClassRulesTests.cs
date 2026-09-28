using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation.Archetypes;
using Xunit;
using static DndMcp.Tests.Simulation.Archetypes.ArchetypeTestKit;

namespace DndMcp.Tests.Simulation.Archetypes;

/// <summary>
/// Invariant: each archetype, resolved at a level, carries the class-table numbers of its edition (checked against the
/// SRD 5.1 / 5.2.1 class and spell entries): Extra Attack levels, Sneak Attack dice, Rage damage, Martial Arts dice,
/// cantrip scaling, the heal dice that changed in 2024, the 2014 vs 2024 Divine Smite, slot-count uses and the +1 weapon
/// from 11. The values are written out here, not derived from the definitions, so a slip in a step map shows.
/// </summary>
public sealed class ArchetypeClassRulesTests
{
    private static int ActionAttacks(ResolvedBuild build) =>
        build.Attacks.Where(a => a.Action == DslValues.AttackActions.Action && !a.Offhand).Sum(a => a.Count);

    [Theory]
    // Fighter Extra Attack: 2 at 5, 3 at 11, 4 at 20 (both editions); Action Surge from 2, a second use at 17.
    [InlineData("2014", 1, 1, 0, 0)]
    [InlineData("2014", 2, 1, 1, 1)]
    [InlineData("2014", 4, 1, 1, 1)]
    [InlineData("2014", 5, 2, 2, 1)]
    [InlineData("2014", 10, 2, 2, 1)]
    [InlineData("2024", 11, 3, 3, 1)]
    [InlineData("2024", 16, 3, 3, 1)]
    [InlineData("2024", 17, 3, 3, 2)]
    [InlineData("2014", 19, 3, 3, 2)]
    [InlineData("2024", 20, 4, 4, 2)]
    public void Fighter_ExtraAttackAndActionSurge_FollowTheClassTable(string edition, int level, int attacks, int surgeAttacks, int surgeUses)
    {
        var build = Resolve("fighter", edition, level);

        Assert.Equal(attacks, ActionAttacks(build));
        var surge = build.ExtraAttacks.SingleOrDefault(e => e.Source.Name == "Action Surge");
        Assert.Equal(surgeAttacks, surge?.Count ?? 0);
        Assert.Equal(surgeUses, surge?.Resource?.Uses ?? 0);
        Assert.Equal(surgeUses == 0 ? null : DslValues.Rests.ShortRest, surge?.Resource?.Per);
    }

    [Theory]
    [InlineData("barbarian")]
    [InlineData("paladin")]
    [InlineData("ranger")]
    [InlineData("monk")]
    public void ExtraAttack_OtherMartialClasses_OneExtraAttackAtFive(string name)
    {
        foreach (var edition in Editions)
        {
            Assert.Equal(1, ActionAttacks(Resolve(name, edition, 4)));
            Assert.Equal(2, ActionAttacks(Resolve(name, edition, 5)));
            Assert.Equal(2, ActionAttacks(Resolve(name, edition, 20)));
        }
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void Rogue_SneakAttack_IsOneD6PerTwoLevelsRoundedUp(int level)
    {
        foreach (var edition in Editions)
        {
            var sneak = Resolve("rogue", edition, level).Riders.Single(r => r.Source.Name == "Sneak Attack");

            Assert.Equal($"{(level + 1) / 2}d6", sneak.Damage.Text);
            Assert.Equal(DslValues.When.FirstHitPerTurn, sneak.When);
            Assert.Null(sneak.DamageType); // the weapon's type
        }
    }

    public static IEnumerable<object[]> Levels() => Enumerable.Range(1, 20).Select(l => new object[] { l });

    [Theory]
    // Rage Damage +2 at 1-8, +3 at 9-15, +4 at 16-20 (both editions); Brutal Critical (2014 only) 1/2/3 dice at 9/13/17.
    [InlineData("2014", 1, 2, null)]
    [InlineData("2014", 8, 2, null)]
    [InlineData("2014", 9, 3, "1d12")]
    [InlineData("2014", 13, 3, "2d12")]
    [InlineData("2014", 16, 4, "2d12")]
    [InlineData("2014", 17, 4, "3d12")]
    [InlineData("2024", 9, 3, null)]
    [InlineData("2024", 20, 4, null)]
    public void Barbarian_RageDamageAndBrutalCritical_FollowTheClassTable(string edition, int level, int rage, string? brutal)
    {
        var build = Resolve("barbarian", edition, level);
        var axe = build.Attacks.Single();

        Assert.Equal(rage, axe.DamageParts.Single(p => p.Label == "Rage").Value);
        Assert.Equal(["bludgeoning", "piercing", "slashing"], build.Defensive.Where(d => d.Kind == DslValues.Kinds.Resistance).Select(d => d.DamageType));
        Assert.Contains(build.SetupCosts, s => s.Source.Name == "Rage" && s.Cost == DslValues.Setup.BonusAction);
        var critical = build.Riders.SingleOrDefault(r => r.Source.Name == "Brutal Critical");
        Assert.Equal(brutal, critical?.Damage.Text);
        if (critical is not null)
        {
            Assert.Equal(DslValues.When.OnCrit, critical.When);
        }

        Assert.Equal(edition == "2024" ? DslValues.Masteries.Cleave : null, axe.Mastery);
    }

    [Theory]
    // Martial Arts die: 2014 d4/d6/d8/d10, 2024 d6/d8/d10/d12 at 1/5/11/17. Flurry: 2 strikes (2024: 3 from 10).
    [InlineData("2014", 1, "1d4", 0)]
    [InlineData("2014", 2, "1d4", 2)]
    [InlineData("2014", 5, "1d6", 2)]
    [InlineData("2014", 11, "1d8", 2)]
    [InlineData("2014", 17, "1d10", 2)]
    [InlineData("2024", 1, "1d6", 0)]
    [InlineData("2024", 5, "1d8", 2)]
    [InlineData("2024", 10, "1d8", 3)]
    [InlineData("2024", 11, "1d10", 3)]
    [InlineData("2024", 17, "1d12", 3)]
    public void Monk_MartialArtsDieAndFlurry_FollowTheClassTable(string edition, int level, string die, int flurry)
    {
        var build = Resolve("monk", edition, level);
        var strike = build.Attacks.Single();

        Assert.Equal(die, strike.Damage.Text);
        Assert.Equal(DslValues.Abilities.Dex, strike.Ability);
        Assert.Equal(flurry, build.ExtraAttacks.SingleOrDefault(e => e.Source.Name == "Flurry of Blows")?.Count ?? 0);
        Assert.Equal(1, build.ExtraAttacks.Single(e => e.Source.Name == "Martial Arts strike").Count);
    }

    [Theory]
    // Ki/Focus points = monk level from 2, split: from 5, floor(level / 2) to Stunning Strike, the rest to Flurry.
    [InlineData(2, 2, 0)]
    [InlineData(4, 4, 0)]
    [InlineData(5, 3, 2)]
    [InlineData(10, 5, 5)]
    [InlineData(17, 9, 8)]
    [InlineData(20, 10, 10)]
    public void Monk_Points_AreSplitBetweenFlurryAndStunningStrike(int level, int flurry, int stun)
    {
        foreach (var edition in Editions)
        {
            var build = Resolve("monk", edition, level);

            Assert.Equal(flurry, build.ExtraAttacks.Single(e => e.Source.Name == "Flurry of Blows").Resource!.Uses);
            var stunning = build.ConditionsOnHit.SingleOrDefault();
            Assert.Equal(stun, stunning?.Resource?.Uses ?? 0);
            if (stunning is not null)
            {
                Assert.Equal(DslValues.Conditions.Stunned, stunning.Condition);
                Assert.Equal(DslValues.Abilities.Con, stunning.Ability);
                // 8 + PB + Wis: Wis 16 at 4-11, 18 at 12-15, 20 from 16 (24 at 20 in 2024).
                var wis = edition == "2024" && level == 20 ? 7 : level >= 16 ? 5 : level >= 12 ? 4 : 3;
                Assert.Equal(8 + DslLimits.ProficiencyBonus(level) + wis, stunning.Dcs.Single().Dc);
                Assert.Null(stunning.Duration); // the edition default: 2014 end of next turn, 2024 start of next turn
            }
        }
    }

    [Fact]
    public void Monk_StrikesBecomeMagicalOrForceAtSix()
    {
        Assert.False(Resolve("monk", "2014", 5).Attacks.Single().IsMagical);
        Assert.True(Resolve("monk", "2014", 6).Attacks.Single().IsMagical);
        Assert.Equal("bludgeoning", Resolve("monk", "2024", 5).Attacks.Single().DamageType);
        Assert.Equal("force", Resolve("monk", "2024", 6).Attacks.Single().DamageType);
    }

    [Theory]
    [InlineData("fighter", "Greatsword", "2d6")]
    [InlineData("barbarian", "Greataxe", "1d12")]
    [InlineData("paladin", "Longsword", "1d8")]
    [InlineData("ranger", "Longbow", "1d8")]
    [InlineData("rogue", "Shortsword", "1d6")]
    public void Weapon_BecomesAPlusOneMagicWeaponAtEleven(string name, string weapon, string dice)
    {
        foreach (var edition in Editions)
        {
            var before = Resolve(name, edition, 10).Attacks.First();
            var after = Resolve(name, edition, 11).Attacks.First();

            Assert.Equal(weapon, before.Name);
            Assert.False(before.IsMagical);
            Assert.Equal(dice, before.Damage.Text);
            Assert.Equal($"+1 {weapon}", after.Name);
            Assert.True(after.IsMagical);
            Assert.Equal($"{dice}+1", after.Damage.Text);
            Assert.Equal(before.AttackBonus + 1, after.AttackBonus); // PB is +4 at both 10 and 11
        }
    }

    [Theory]
    // Divine Smite uses = every spell slot (2024: + the free Paladin's Smite cast). Half-caster table: 2 at 2, 3 at 3,
    // 6 at 5, 7 at 7, 9 at 9, 10 at 11, 11 at 13, 12 at 15, 14 at 17, 15 at 19.
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(5, 6)]
    [InlineData(9, 9)]
    [InlineData(13, 11)]
    [InlineData(17, 14)]
    [InlineData(20, 15)]
    public void Paladin_DivineSmite_FollowsEachEditionsRule(int level, int slots)
    {
        var smite2014 = Resolve("paladin", "2014", level).Riders.Single(r => r.Source.Name == "Divine Smite");
        var smite2024 = Resolve("paladin", "2024", level).Riders.Single(r => r.Source.Name == "Divine Smite");

        // 2014: on any melee weapon hit, spending a slot; 2024: a Bonus Action spell right after a hit, so once a turn.
        Assert.Equal(("2d8", "radiant", DslValues.When.EveryHit, (string?)null, slots),
            (smite2014.Damage.Text, smite2014.DamageType, smite2014.When, smite2014.ActionCost, smite2014.Resource!.Uses));
        Assert.Equal(("2d8", "radiant", DslValues.When.FirstHitPerTurn, (string?)DslValues.ActionCosts.BonusAction, slots + 1),
            (smite2024.Damage.Text, smite2024.DamageType, smite2024.When, smite2024.ActionCost, smite2024.Resource!.Uses));
        Assert.Empty(Resolve("paladin", "2014", 1).Riders);
    }

    [Fact]
    public void Paladin_DuelingFromTwo_RadiantStrikesFromEleven_LayOnHandsFiveTimesLevel()
    {
        foreach (var edition in Editions)
        {
            Assert.DoesNotContain(Resolve("paladin", edition, 1).Attacks.Single().DamageParts, p => p.Label == "Dueling");
            Assert.Equal(2, Resolve("paladin", edition, 2).Attacks.Single().DamageParts.Single(p => p.Label == "Dueling").Value);
            Assert.DoesNotContain(Resolve("paladin", edition, 10).Riders, r => r.Damage.Text == "1d8");
            Assert.Contains(Resolve("paladin", edition, 11).Riders, r => r.Damage.Text == "1d8" && r.When == DslValues.When.EveryHit && r.Resource is null);
            foreach (var level in new[] { 1, 7, 20 })
            {
                var lay = Resolve("paladin", edition, level).Heals.Single();
                Assert.Equal((5 * level).ToString(System.Globalization.CultureInfo.InvariantCulture), lay.Healing.Text);
                Assert.Equal(edition == "2024" ? DslValues.ActionCosts.BonusAction : DslValues.ActionCosts.Action, lay.ActionCost);
            }
        }
    }

    [Theory]
    // Second Wind 1d10 + fighter level, Bonus Action, self; 2014 once per short rest, 2024 2/3/4 uses at 1/4/10.
    [InlineData("2014", 1, "1d10+1", 1)]
    [InlineData("2014", 20, "1d10+20", 1)]
    [InlineData("2024", 3, "1d10+3", 2)]
    [InlineData("2024", 4, "1d10+4", 3)]
    [InlineData("2024", 10, "1d10+10", 4)]
    public void Fighter_SecondWind_FollowsEachEdition(string edition, int level, string healing, int uses)
    {
        var heal = Resolve("fighter", edition, level).Heals.Single();

        Assert.Equal((healing, DslValues.ActionCosts.BonusAction, true, uses), (heal.Healing.Text, heal.ActionCost, heal.SelfOnly, heal.Resource!.Uses));
    }

    [Theory]
    // Healing Word 2014 1d4 + mod, 2024 2d4 + mod (Bonus Action); Cure Wounds 2014 1d8 + mod, 2024 2d8 + mod (Action).
    // Uses: the 1st-level slots, 2 / 3 / 4 at levels 1 / 2 / 3+. Mod: Wis or Cha 17 (+3), 18 at 4 (+4), 20 at 8 (+5).
    [InlineData("cleric", "2014", 1, "Healing Word", "1d4+3", "bonus_action", 2)]
    [InlineData("cleric", "2024", 1, "Healing Word", "2d4+3", "bonus_action", 2)]
    [InlineData("bard", "2014", 2, "Healing Word", "1d4+3", "bonus_action", 3)]
    [InlineData("bard", "2024", 8, "Healing Word", "2d4+5", "bonus_action", 4)]
    [InlineData("druid", "2014", 4, "Cure Wounds", "1d8+4", "action", 4)]
    [InlineData("druid", "2024", 20, "Cure Wounds", "2d8+5", "action", 4)]
    public void Heals_UseTheEditionsDice(string name, string edition, int level, string spell, string healing, string cost, int uses)
    {
        var heal = Resolve(name, edition, level).Heals.Single();

        Assert.Equal((spell, healing, cost, 1, false, uses), (heal.Source.Name, heal.Healing.Text, heal.ActionCost, heal.Targets, heal.SelfOnly, heal.Resource!.Uses));
    }

    [Theory]
    // Cantrips scale x1/x2/x3/x4 at 1/5/11/17 (dice, or Eldritch Blast's beams).
    [InlineData(1, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(10, 2)]
    [InlineData(11, 3)]
    [InlineData(17, 4)]
    [InlineData(20, 4)]
    public void Cantrips_ScaleAtFiveElevenSeventeen(int level, int multiplier)
    {
        foreach (var edition in Editions)
        {
            Assert.Equal($"{multiplier}d10", Resolve("wizard", edition, level).FindAttack("Fire Bolt")!.Damage.Text);
            Assert.Equal($"{multiplier}d10", Resolve("sorcerer", edition, level).FindAttack("Fire Bolt")!.Damage.Text);
            Assert.Equal($"{multiplier}d8", Resolve("druid", edition, level).FindAttack("Produce Flame")!.Damage.Text);
            Assert.Equal(multiplier, Resolve("warlock", edition, level).FindAttack("Eldritch Blast")!.Count);
            Assert.Equal(multiplier, Resolve("cleric", edition, level).SaveEffects.First(s => s.Source.Name!.StartsWith("Sacred Flame", StringComparison.Ordinal)).CantripMultiplier);
        }

        foreach (var name in new[] { "wizard", "sorcerer" })
        {
            foreach (var edition in Editions)
            {
                var splash = Resolve(name, edition, level).SaveEffects.Single(s => s.Source.Name == "Acid Splash");
                Assert.Equal(($"{multiplier}d6", "acid", DslValues.OnSuccess.None, 1), (splash.Damage!.Text, splash.DamageType, splash.OnSuccess, splash.Targets));
            }
        }

        Assert.Equal($"{multiplier}d4", Resolve("bard", "2014", level).SaveEffects.Single(s => s.Source.Name == "Vicious Mockery").Damage!.Text);
        Assert.Equal($"{multiplier}d8", Resolve("bard", "2024", level).FindAttack("Starry Wisp")!.Damage.Text);
    }

    [Theory]
    // The area spells: dice, save, targets by the DMG table, uses = slots of the spell's level or higher (full caster).
    // archetype, level, spell, dice, save, targets, uses (null: one cast a fight, no uses)
    [InlineData("wizard", 5, "Fireball", "8d6", "dex", 4, 2)]
    [InlineData("wizard", 9, "Fireball", "8d6", "dex", 4, 7)]
    [InlineData("wizard", 20, "Fireball", "8d6", "dex", 4, 15)]
    [InlineData("sorcerer", 5, "Lightning Bolt", "8d6", "dex", 4, 2)]
    [InlineData("bard", 3, "Shatter", "3d8", "con", 2, 2)]
    [InlineData("bard", 5, "Shatter", "3d8", "con", 2, 5)]
    [InlineData("cleric", 5, "Spirit Guardians", "3d8", "wis", 3, null)]
    [InlineData("druid", 5, "Call Lightning", "3d10", "dex", 1, null)]
    public void AreaSpells_FromTheirLevel_WithTheClassSlots(string name, int level, string spell, string dice, string save, int targets, int? uses)
    {
        foreach (var edition in Editions)
        {
            var effect = Resolve(name, edition, level).SaveEffects.Single(s => s.Source.Name == spell);

            Assert.Equal((dice, save, targets, DslValues.OnSuccess.Half), (effect.Damage!.Text, effect.Ability, effect.Targets, effect.OnSuccess));
            Assert.Equal(uses, effect.Resource?.Uses);
        }
    }

    [Theory]
    // Each area spell arrives with the class's first slot of its level: 3rd at 5, Shatter's 2nd at 3.
    [InlineData("wizard", "Fireball", 5)]
    [InlineData("sorcerer", "Lightning Bolt", 5)]
    [InlineData("cleric", "Spirit Guardians", 5)]
    [InlineData("druid", "Call Lightning", 5)]
    [InlineData("bard", "Shatter", 3)]
    public void AreaSpells_ArriveWithTheirSlotLevel(string name, string spell, int firstLevel)
    {
        foreach (var edition in Editions)
        {
            Assert.DoesNotContain(Resolve(name, edition, firstLevel - 1).SaveEffects, s => s.Source.Name == spell);
            Assert.Contains(Resolve(name, edition, firstLevel).SaveEffects, s => s.Source.Name == spell);
        }
    }

    [Fact]
    public void Cleric_SpiritGuardians_IsSetUpWithTheActionAndCostsNoActionAfter()
    {
        foreach (var edition in Editions)
        {
            var build = Resolve("cleric", edition, 5);
            var guardians = build.SaveEffects.Single(s => s.Source.Name == "Spirit Guardians");

            Assert.Equal(DslValues.ActionCosts.None, guardians.ActionCost);
            Assert.True(guardians.Concentration);
            Assert.Contains(build.SetupCosts, s => s.Source.Name == "Spirit Guardians" && s.Cost == DslValues.Setup.Action);
            Assert.Equal(15, guardians.Dc); // 8 + 3 + Wis 18
        }
    }

    [Fact]
    public void Cleric_SpiritualWeapon_2014KeepsIt_2024EndsItForSpiritGuardians()
    {
        Assert.Null(Resolve("cleric", "2014", 2).FindAttack("Spiritual Weapon"));
        foreach (var edition in Editions)
        {
            var weapon = Resolve("cleric", edition, 3).FindAttack("Spiritual Weapon")!;
            Assert.Equal((DslValues.AttackActions.BonusAction, "1d8+3", "force"), (weapon.Action, $"{weapon.Damage.Text}+{weapon.AbilityDamage}", weapon.DamageType));
        }

        Assert.NotNull(Resolve("cleric", "2014", 20).FindAttack("Spiritual Weapon"));
        Assert.NotNull(Resolve("cleric", "2024", 4).FindAttack("Spiritual Weapon"));
        Assert.Null(Resolve("cleric", "2024", 5).FindAttack("Spiritual Weapon"));
    }

    [Fact]
    public void PotentSpellcasting_2024CasterCantripsAddWisFromSeven()
    {
        // Blessed Strikes / Elemental Fury (2024 only): the Wis modifier (18 at 7, +4) on the damage cantrip.
        string SacredFlame(string edition, int level) =>
            Resolve("cleric", edition, level).SaveEffects.Single(s => s.Source.Name!.StartsWith("Sacred Flame", StringComparison.Ordinal)).Damage!.Text;

        Assert.Equal("2d8", SacredFlame("2024", 6));
        Assert.Equal("2d8+4", SacredFlame("2024", 7));
        Assert.Equal("4d8", SacredFlame("2014", 20));
        Assert.Equal(0, Resolve("druid", "2024", 6).FindAttack("Produce Flame")!.FlatDamage(false));
        Assert.Equal(4, Resolve("druid", "2024", 7).FindAttack("Produce Flame")!.FlatDamage(false));
        Assert.Equal(0, Resolve("druid", "2014", 20).FindAttack("Produce Flame")!.FlatDamage(false));
    }

    [Theory]
    [InlineData("2014", 2, false, "1d6", null)]
    [InlineData("2014", 20, false, "1d6", null)]
    [InlineData("2024", 1, false, "1d6", "force")]
    [InlineData("2024", 17, true, "1d6", "force")]
    [InlineData("2024", 20, true, "1d10", "force")]
    public void Ranger_HuntersMarkArcheryAndPreciseHunter_FollowEachEdition(string edition, int level, bool advantage, string mark, string? type)
    {
        var build = Resolve("ranger", edition, level);
        var hex = build.Riders.Single(r => r.Source.Name == "Hunter's Mark");

        Assert.Equal((mark, type, true), (hex.Damage.Text, hex.DamageType, hex.Concentration));
        Assert.Contains(build.SetupCosts, s => s.Source.Name == "Hunter's Mark" && s.Cost == DslValues.Setup.BonusAction);
        Assert.Equal(advantage, build.AdvantageSources.Any());
        Assert.Equal(level >= 2, build.Attacks.First().ToHitParts.Any(p => p.Label == "Archery" && p.Value == 2));
        Assert.Empty(Resolve("ranger", "2014", 1).Riders);
    }

    [Fact]
    public void Rogue_2014BonusActionOffhand_2024NickInTheAttackActionWithSteadyAim()
    {
        var old = Resolve("rogue", "2014", 3);
        Assert.Equal((DslValues.AttackActions.BonusAction, true, 0), (old.Attacks[1].Action, old.Attacks[1].Offhand, old.Attacks[1].AbilityDamage));
        Assert.Empty(old.AdvantageSources);

        var now = Resolve("rogue", "2024", 3);
        Assert.Equal((DslValues.AttackActions.Action, true, DslValues.Masteries.Nick), (now.Attacks[1].Action, now.Attacks[1].Offhand, now.Attacks[1].Mastery));
        Assert.Equal(DslValues.Masteries.Vex, now.Attacks[0].Mastery);
        Assert.Equal(["Shortsword"], now.AdvantageSources.Single().Attacks);
        Assert.Empty(Resolve("rogue", "2024", 2).AdvantageSources);
    }

    [Fact]
    public void Warlock_HexIsASetupCostUnlikeThePreset_AgonizingBlastFromTwo()
    {
        foreach (var edition in Editions)
        {
            Assert.Contains(Resolve("warlock", edition, 1).SetupCosts, s => s.Source.Name == "Hex" && s.Cost == DslValues.Setup.BonusAction);
            Assert.Equal(0, Resolve("warlock", edition, 1).FindAttack("Eldritch Blast")!.FlatDamage(false));
            Assert.Equal(3, Resolve("warlock", edition, 2).FindAttack("Eldritch Blast")!.FlatDamage(false));
            Assert.Equal(5, Resolve("warlock", edition, 8).FindAttack("Eldritch Blast")!.FlatDamage(false));
        }
    }

    [Fact]
    public void Sorcerer_2024InnateSorcery_AdvantageOnFireBoltAndDcPlusOne()
    {
        var now = Resolve("sorcerer", "2024", 5);
        var old = Resolve("sorcerer", "2014", 5);

        Assert.Equal(["Fire Bolt"], now.AdvantageSources.Single().Attacks);
        Assert.Contains(now.SetupCosts, s => s.Source.Name == "Innate Sorcery");
        Assert.Equal(old.SaveEffects.Single(s => s.Source.Name == "Lightning Bolt").Dc + 1, now.SaveEffects.Single(s => s.Source.Name == "Lightning Bolt").Dc);
        Assert.Empty(old.AdvantageSources);
    }

    [Theory]
    [MemberData(nameof(ArchetypeTestKit.AllArchetypes), MemberType = typeof(ArchetypeTestKit))]
    public void Resources_AtMostThreePerLevel_AndOneConcentrationEffect(string name, string edition)
    {
        // The DSL's limits hold with room: the validator enforces them, this says which archetypes sit at the edge.
        for (var level = 1; level <= 20; level++)
        {
            var build = Resolve(name, edition, level);
            var limited = build.Riders.Count(r => r.Resource is not null) + build.ExtraAttacks.Count(e => e.Resource is not null) +
                          build.SaveEffects.Count(s => s.Resource is not null) + build.ConditionsOnHit.Count(c => c.Resource is not null) +
                          build.Heals.Count(h => h.Resource is not null);
            var concentration = build.Riders.Count(r => r.Concentration) + build.SaveEffects.Count(s => s.Concentration);

            Assert.InRange(limited, 0, 3);
            Assert.InRange(concentration, 0, 1);
        }
    }
}
