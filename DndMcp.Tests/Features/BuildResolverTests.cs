using DndMcp.Domain.Features;
using Xunit;
using static DndMcp.Tests.Features.FeatureTestBuilds;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Tests.Features;

/// <summary>
/// Invariant: a resolved build has every level-dependent value concrete and every sugar expanded by edition, and the
/// golden fighters of contract §8 resolve to exactly the numbers the DPR maths starts from (+7 to hit, 2d6 + 4, GWF of
/// the right edition, +PB only on Attack-action hits).
/// </summary>
public sealed class BuildResolverTests
{
    [Fact]
    public void Resolve_2014FighterGolden_IsPlus7Greatsword2d6Plus4WithGwf2014()
    {
        var build = BuildResolver.Resolve(Build(Fighter2014GwmJson), 5);

        Assert.Equal("2014", build.Edition);
        Assert.Equal(3, build.ProficiencyBonus);
        var sword = Assert.Single(build.Attacks);
        Assert.Equal(2, sword.Count);
        Assert.Equal(7, sword.AttackBonus);
        Assert.Equal([new NamedValue("Str", 4), new NamedValue("proficiency", 3)], sword.ToHitParts);
        Assert.Equal("2d6", sword.Damage.Text);
        Assert.Equal(4, sword.FlatDamage(partOfAttackAction: true));
        Assert.Equal(4, sword.FlatDamage(partOfAttackAction: false));
        Assert.Equal(V.Remaps.Gwf2014, sword.WeaponRemap);
        Assert.Equal("Great Weapon Fighting (fighting style)", sword.WeaponRemapSource);
        Assert.Equal(20, sword.CritMin);
        Assert.Equal("slashing", sword.DamageType);
        Assert.True(sword.IsMelee);
        Assert.True(sword.IsWeapon);
        Assert.True(sword.IsPartOfAttackAction);

        var power = Assert.Single(build.PowerAttacks);
        Assert.Equal((5, 10, V.PowerAttackPolicies.Auto), (power.Penalty, power.Bonus, power.Policy));
        Assert.Equal(["Greatsword"], power.Attacks);
        var bonusAttack = Assert.Single(build.ExtraAttacks);
        Assert.Equal(("Greatsword", 1, V.ExtraAttackActions.BonusAction, V.Triggers.Crit), (bonusAttack.Attack, bonusAttack.Count, bonusAttack.Action, bonusAttack.Trigger));
        Assert.Null(bonusAttack.TriggerProbability);
        Assert.Empty(build.Riders);
        Assert.Empty(build.Notes);
        Assert.Same(sword, build.FindAttack("GREATSWORD"));
        Assert.Null(build.FindAttack("Longbow"));
        Assert.True(sword.HasProperty(V.Properties.Heavy));
        Assert.False(sword.IsRanged);
    }

    [Fact]
    public void Resolve_2024FighterGolden_IsPlus7WithPbBonusDamageOnAttackActionOnlyAndGraze()
    {
        var build = BuildResolver.Resolve(Build(Fighter2024GwmJson), 5);

        var sword = Assert.Single(build.Attacks);
        Assert.Equal(7, sword.AttackBonus);
        Assert.Equal(19, build.Abilities.Str);
        Assert.Equal(4, sword.AbilityModifier);
        Assert.Equal(V.Remaps.Gwf2024, sword.WeaponRemap);
        Assert.Equal(V.Masteries.Graze, sword.Mastery);
        Assert.Equal(
            [
                new DamagePart("Str", 4, DamagePartSources.Ability),
                new DamagePart("Great Weapon Master", 3, DamagePartSources.BonusDamage, AttackActionOnly: true),
            ],
            sword.DamageParts);
        Assert.Equal(7, sword.FlatDamage(partOfAttackAction: true));
        Assert.Equal(4, sword.FlatDamage(partOfAttackAction: false));
        Assert.Equal(4, sword.AbilityDamage);
        Assert.Equal(15, sword.MasterySaveDc);

        var hew = Assert.Single(build.ExtraAttacks);
        Assert.Equal(("Hew", V.Triggers.Crit), (hew.Source.Label, hew.Trigger));
        Assert.Empty(build.PowerAttacks);
        Assert.Empty(build.Notes);
    }

    [Theory]
    [InlineData(4, 1, 2)]
    [InlineData(5, 2, 3)]
    [InlineData(9, 2, 4)]
    [InlineData(17, 2, 6)]
    public void Resolve_2024FighterAtOtherLevels_StepCountAndPbFollowTheLevel(int level, int count, int pb)
    {
        var build = BuildResolver.Resolve(Build(Fighter2024GwmJson), level);

        var sword = Assert.Single(build.Attacks);
        Assert.Equal(count, sword.Count);
        Assert.Equal(pb, build.ProficiencyBonus);
        Assert.Equal(4 + pb, sword.AttackBonus);
        Assert.Equal(4 + pb, sword.FlatDamage(partOfAttackAction: true));
        Assert.True(build.ScalesWithLevel);
    }

    [Fact]
    public void Resolve_Levels_ValidatesOnceAndResolvesEachInTheOrderGiven()
    {
        var builds = BuildResolver.Resolve(Build(Fighter2014GwmJson), [5, 1, 20]);

        Assert.Equal([5, 1, 20], builds.Select(b => b.Level));
        Assert.Equal([2, 1, 2], builds.Select(b => b.Attacks[0].Count));
    }

    [Fact]
    public void Resolve_NoLevels_UsesTheBuildsOwnLevel()
    {
        var build = Assert.Single(BuildResolver.Resolve(Build(Fighter2014GwmJson), levels: null));

        Assert.Equal(5, build.Level);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void Resolve_LevelOutsideTheTables_IsRefused(int level)
    {
        var ex = Assert.Throws<Domain.Core.DndInputException>(() => BuildResolver.Resolve(Build(Fighter2014GwmJson), level));

        Assert.Equal($"levels: {level} is not a character level; levels are 1 to 20.", ex.Message);
    }

    [Theory]
    [InlineData("2014", V.Remaps.Gwf2014)]
    [InlineData("2024", V.Remaps.Gwf2024)]
    public void Resolve_GwfSugar_RemapsTwoHandedAndVersatileMeleeWeaponsByEdition(string edition, string remap)
    {
        var build = BuildResolver.Resolve(With(
            """
            [{ "name": "Greatsword", "damage": "2d6", "properties": ["melee", "two-handed"] },
             { "name": "Longsword", "damage": "1d8", "properties": ["versatile"] },
             { "name": "Rapier", "damage": "1d8", "properties": ["melee", "finesse"] },
             { "name": "Longbow", "damage": "1d8", "properties": ["ranged", "two-handed"] },
             { "name": "Spell blade", "damage": "1d8", "properties": ["melee", "two-handed", "spell"] }]
            """,
            extra: $"\"edition\": \"{edition}\", \"fighting_style\": \"gwf\","), 5);

        Assert.Equal([remap, remap, null, null, null], build.Attacks.Select(a => a.WeaponRemap));
        var sugar = Assert.Single(build.Remaps);
        Assert.Null(sugar.Source);
        Assert.Equal(["Greatsword", "Longsword"], sugar.Attacks);
    }

    [Fact]
    public void Resolve_ArcherySugar_AddsTwoToRangedWeaponAttacksOnly()
    {
        var build = BuildResolver.Resolve(With(
            """
            [{ "name": "Longbow", "to_hit": {"ability": "dex"}, "damage": "1d8", "properties": ["ranged"] },
             { "name": "Dagger", "to_hit": {"ability": "dex"}, "damage": "1d4", "properties": ["melee", "thrown"] },
             { "name": "Fire Bolt", "to_hit": {"ability": "int"}, "damage": "1d10", "properties": ["ranged", "spell"] }]
            """,
            extra: "\"abilities\": {\"dex\": 16}, \"fighting_style\": \"archery\","), 5);

        Assert.Equal([8, 6, 3], build.Attacks.Select(a => a.AttackBonus));
        Assert.Contains(new NamedValue("Archery", 2), build.Attacks[0].ToHitParts);
    }

    [Fact]
    public void Resolve_ArcheryWithTotal_IsNotAddedAndSaysSo()
    {
        var build = BuildResolver.Resolve(With(
            """[{ "name": "Longbow", "to_hit": {"ability": "dex", "total": 9}, "damage": "1d8", "properties": ["ranged"] }]""",
            extra: "\"fighting_style\": \"archery\","), 5);

        Assert.Equal(9, build.Attacks[0].AttackBonus);
        Assert.Contains(build.Notes, n => n.Contains("archery is not added to attacks with to_hit total", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_DuelingSugar_AddsTwoDamageToOneHandedMeleeWeaponsWithANote()
    {
        var build = BuildResolver.Resolve(With(
            """
            [{ "name": "Longsword", "damage": "1d8", "properties": ["melee", "versatile"] },
             { "name": "Greatsword", "damage": "2d6", "properties": ["melee", "two-handed"] },
             { "name": "Javelin", "damage": "1d6", "properties": ["ranged", "thrown"] }]
            """,
            extra: "\"edition\": \"2014\", \"abilities\": {\"str\": 16}, \"fighting_style\": \"dueling\","), 5);

        Assert.Equal([5, 3, 3], build.Attacks.Select(a => a.FlatDamage(partOfAttackAction: true)));
        Assert.Contains(new DamagePart("Dueling", 2, DamagePartSources.Dueling), build.Attacks[0].DamageParts);
        Assert.Contains("Dueling's +2 assumes the weapon is held in one hand and no other weapon is wielded.", build.Notes);
    }

    [Fact]
    public void Resolve_DuelingWithAnOffhandAttack_IsNotApplied()
    {
        var build = BuildResolver.Resolve(With(
            """
            [{ "name": "Shortsword", "damage": "1d6", "properties": ["melee", "light"] },
             { "name": "Offhand shortsword", "action": "bonus_action", "offhand": true, "damage": "1d6", "properties": ["melee", "light"] }]
            """,
            extra: "\"fighting_style\": \"dueling\","), 5);

        Assert.DoesNotContain(build.Attacks.SelectMany(a => a.DamageParts), p => p.Source == DamagePartSources.Dueling);
        Assert.Contains(build.Notes, n => n.Contains("Dueling needs no other weapon", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(16, null, 0)]
    [InlineData(16, "twf", 3)]
    [InlineData(8, null, -1)]
    [InlineData(8, "twf", -1)]
    public void Resolve_OffhandAttack_AddsTheModifierOnlyWithTwfOrWhenNegative(int str, string? style, int expected)
    {
        var styleJson = style is null ? string.Empty : $"\"fighting_style\": \"{style}\",";
        var build = BuildResolver.Resolve(With(
            """
            [{ "name": "Shortsword", "damage": "1d6", "properties": ["melee", "light"] },
             { "name": "Offhand", "action": "bonus_action", "offhand": true, "damage": "1d6", "properties": ["melee", "light"] }]
            """,
            extra: $"\"abilities\": {{\"str\": {str}}}, {styleJson}"), 5);

        Assert.Equal(expected, build.Attacks[1].AbilityDamage);
        Assert.Equal(DslLimits.AbilityModifier(str), build.Attacks[0].AbilityDamage);
    }

    [Fact]
    public void Resolve_TwfWithoutOffhand_ChangesNothingAndSaysSo()
    {
        var build = BuildResolver.Resolve(With(extra: "\"fighting_style\": \"twf\","), 5);

        Assert.Contains("fighting_style twf changes nothing at level 5: no attack is offhand.", build.Notes);
    }

    [Fact]
    public void Resolve_GwfWithNoEligibleAttack_SaysSo()
    {
        var build = BuildResolver.Resolve(With("""[{ "name": "Rapier", "damage": "1d8", "properties": ["finesse"] }]""", extra: "\"fighting_style\": \"gwf\","), 5);

        Assert.Null(build.Attacks[0].WeaponRemap);
        Assert.Contains(build.Notes, n => n.StartsWith("fighting_style gwf applies to no attack at level 5", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_ExplicitGwfRemapOfTheOtherEdition_IsUsedWithANote()
    {
        var build = BuildResolver.Resolve(WithModifier("""{ "kind": "damage_die_remap", "remap": "gwf2014" }""", "\"edition\": \"2024\","), 5);

        Assert.Equal([V.Remaps.Gwf2014, null], build.Attacks.Select(a => a.WeaponRemap));
        Assert.Equal("damage_die_remap #1", build.Attacks[0].WeaponRemapSource);
        Assert.Contains(build.Notes, n => n.Contains("gwf2014 is the other edition's Great Weapon Fighting", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_ToHitModifiers_AreNamedPartsAndBonusDice()
    {
        var build = BuildResolver.Resolve(With(
            modifiersJson: """
                [{ "kind": "to_hit", "name": "+1 weapon", "amount": 1, "attacks": ["Greatsword"] },
                 { "kind": "to_hit", "name": "Bless", "dice": "1d4", "concentration": true },
                 { "kind": "to_hit", "name": "Bane", "dice": "-1d4" }]
                """), 5);

        var sword = build.Attacks[0];
        Assert.Equal(4, sword.AttackBonus);
        Assert.Contains(new NamedValue("+1 weapon", 1), sword.ToHitParts);
        Assert.Equal(["Bless:1d4", "Bane:-1d4"], sword.ToHitDice.Select(d => $"{d.Label}:{d.Dice}"));
        Assert.Equal(3, build.Attacks[1].AttackBonus);
        Assert.Equal(2, build.Attacks[1].ToHitDice.Count);
    }

    [Fact]
    public void Resolve_ToHitTotal_ReplacesTheComputedBonusButKeepsTheAbilityForDamage()
    {
        var build = BuildResolver.Resolve(With(
            """[{ "name": "Claw", "to_hit": {"ability": "str", "total": 9, "bonus": 2}, "damage": "2d6", "properties": ["melee"] }]""",
            """[{ "kind": "to_hit", "amount": 1 }]""",
            "\"abilities\": {\"str\": 18},"), 5);

        var claw = build.Attacks[0];
        Assert.Equal(10, claw.AttackBonus);
        Assert.Equal([new NamedValue("to_hit total", 9), new NamedValue("to_hit #1", 1)], claw.ToHitParts);
        Assert.Equal(4, claw.AbilityDamage);
        Assert.Equal(15, claw.MasterySaveDc);
        Assert.Contains(build.Notes, n => n.Contains("its proficient and bonus are ignored", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_ToHitAbilityNone_HasNoAbilityPartAndNoAbilityDamage()
    {
        var build = BuildResolver.Resolve(With(
            """[{ "name": "Trap dart", "to_hit": {"ability": "none", "proficient": false, "bonus": 5}, "damage": "1d4", "properties": ["ranged"] }]"""), 5);

        var dart = build.Attacks[0];
        Assert.Equal([new NamedValue("bonus", 5)], dart.ToHitParts);
        Assert.Empty(dart.DamageParts);
        Assert.Equal(0, dart.AbilityModifier);
    }

    [Fact]
    public void Resolve_CritRange_TheLowestApplicableMinWins()
    {
        var build = BuildResolver.Resolve(With(
            modifiersJson: """
                [{ "kind": "crit_range", "name": "Improved Critical", "min": {"3": 19, "15": 18} },
                 { "kind": "crit_range", "min": 20, "attacks": ["Longbow"] }]
                """), 15);

        Assert.Equal([18, 18], build.Attacks.Select(a => a.CritMin));
        Assert.Equal([19, 19], BuildResolver.Resolve(With(modifiersJson: """[{ "kind": "crit_range", "min": {"3": 19, "15": 18} }]"""), 5).Attacks.Select(a => a.CritMin));
    }

    [Fact]
    public void Resolve_ElvenAccuracyByDefault_AppliesOnlyToDexIntWisChaAttacks()
    {
        var build = BuildResolver.Resolve(WithModifier("""{ "kind": "elven_accuracy" }"""), 5);

        Assert.Equal([false, true], build.Attacks.Select(a => a.ElvenAccuracy));
    }

    [Fact]
    public void Resolve_LuckyAndIgnoreCover_FoldIntoTheFilteredAttacks()
    {
        var build = BuildResolver.Resolve(With(modifiersJson: """
            [{ "kind": "lucky" },
             { "kind": "ignore_cover", "name": "Sharpshooter", "attacks": ["longbow"] }]
            """), 5);

        Assert.Equal([true, true], build.Attacks.Select(a => a.Lucky));
        Assert.Equal([false, true], build.Attacks.Select(a => a.IgnoresCover));
    }

    [Fact]
    public void Resolve_Rider_KeepsItsPolicyTimingAndResourceWithFilterNames()
    {
        var build = BuildResolver.Resolve(Build(PaladinSmiteJson), 5);

        var smite = Assert.Single(build.Riders);
        Assert.Equal("2d8", smite.Damage.Text);
        Assert.Equal("radiant", smite.DamageType);
        Assert.Equal(V.When.FirstHitPerTurn, smite.When);
        Assert.Equal(V.Policies.CritOrLast, smite.Policy);
        Assert.Equal(V.ActionCosts.BonusAction, smite.ActionCost);
        Assert.Equal(new ResolvedResource(4, V.Rests.LongRest), smite.Resource);
        Assert.True(smite.CritDoubles);
        Assert.True(smite.IsOptional);
        Assert.Equal(["Longsword"], smite.Attacks);
        Assert.True(smite.AppliesTo(build.Attacks[0]));
        Assert.Equal(new ModifierRef(1, V.Kinds.ExtraDamage, "Divine Smite"), smite.Source);
        Assert.Equal("modifiers item 1 (extra_damage \"Divine Smite\")", smite.Source.Where);
        Assert.Equal("Divine Smite", smite.Source.Label);
        Assert.Equal(["Divine Smite"], build.BonusActionConsumers);
        Assert.Empty(build.ReactionConsumers);
    }

    [Theory]
    [InlineData("""{ "kind": "extra_damage", "dice": "1d6" }""", false)]
    [InlineData("""{ "kind": "extra_damage", "dice": "1d6", "when": "first_hit_per_turn" }""", true)]
    [InlineData("""{ "kind": "extra_damage", "dice": "2d8", "resource": {"uses": 2, "per": "long_rest"} }""", true)]
    [InlineData("""{ "kind": "extra_damage", "dice": "2d8", "action_cost": "bonus_action" }""", true)]
    [InlineData("""{ "kind": "extra_damage", "dice": "1d8", "when": "on_crit" }""", false)]
    public void Resolve_Rider_IsOptionalOnlyWhenSpendingItIsAChoice(string modifier, bool optional)
    {
        var rider = Assert.Single(BuildResolver.Resolve(WithModifier(modifier), 5).Riders);

        Assert.Equal(optional, rider.IsOptional);
        Assert.Equal(V.Policies.AnyHit, rider.Policy);
    }

    [Fact]
    public void Resolve_RiderWithAmount_AddsTheResolvedAmountAsFlat()
    {
        var build = BuildResolver.Resolve(WithModifier("""{ "kind": "extra_damage", "name": "Divine Favor", "dice": "1d4", "amount": "pb", "type": "radiant" }"""), 9);

        Assert.Equal("1d4+4", build.Riders[0].Damage.Text);
    }

    [Theory]
    [InlineData("on_crit")]
    [InlineData("on_miss")]
    public void Resolve_OnCritOrMissRider_DiceAreNotDoubled(string when)
    {
        var rider = BuildResolver.Resolve(WithModifier($$"""{ "kind": "extra_damage", "dice": "1d8", "when": "{{when}}" }"""), 5).Riders[0];

        Assert.False(rider.CritDoubles);
    }

    [Fact]
    public void Resolve_SaveEffect_ShapeGivesTheDmgTargetCountAndDcIsGiven()
    {
        var build = BuildResolver.Resolve(Build(FireballJson), 5);

        Assert.Empty(build.Attacks);
        var fireball = Assert.Single(build.SaveEffects);
        Assert.Equal((V.Abilities.Dex, 15, 4, "8d6", "fire"), (fireball.Ability, fireball.Dc, fireball.Targets, fireball.Damage!.Text, fireball.DamageType));
        Assert.True(fireball.TargetsFromArea);
        Assert.Equal([new NamedValue("given", 15)], fireball.DcParts);
        Assert.Equal(V.OnSuccess.Half, fireball.OnSuccess);
        Assert.Equal(V.ActionCosts.Action, fireball.ActionCost);
        Assert.True(fireball.Magical);
        Assert.Equal(new ResolvedResource(2, V.Rests.LongRest), fireball.Resource);
    }

    [Fact]
    public void Resolve_SaveEffectWithDcAbility_Is8PlusPbPlusModPlusBonusAndCantripScales()
    {
        var build = BuildResolver.Resolve(Build("""
            { "name": "Cleric", "level": 11, "abilities": {"wis": 18},
              "modifiers": [{ "kind": "save_effect", "name": "Sacred Flame", "ability": "dex", "dc_ability": "wis", "dc_bonus": 1,
                              "dice": "1d8", "type": "radiant", "on_success": "none", "cantrip": true }] }
            """), 11);

        var flame = Assert.Single(build.SaveEffects);
        Assert.Equal(17, flame.Dc);
        Assert.Equal([new NamedValue("base", 8), new NamedValue("proficiency", 4), new NamedValue("Wis", 4), new NamedValue("dc_bonus", 1)], flame.DcParts);
        Assert.Equal("3d8", flame.Damage!.Text);
        Assert.Equal(3, flame.CantripMultiplier);
        Assert.Equal(1, flame.Targets);
        Assert.False(flame.TargetsFromArea);
    }

    [Fact]
    public void Resolve_SaveEffectConditionOnly_HasNoDamageAndLabelConditionsGetANote()
    {
        var build = BuildResolver.Resolve(Build("""
            { "name": "Caster", "level": 5,
              "modifiers": [{ "kind": "save_effect", "name": "Hold Person", "ability": "wis", "dc": 14, "condition": "paralyzed" },
                            { "kind": "save_effect", "name": "Cause Fear", "ability": "wis", "dc": 14, "condition": "frightened", "action_cost": "none" }] }
            """), 5);

        Assert.Null(build.SaveEffects[0].Damage);
        Assert.True(build.SaveEffects[0].ConditionIsMechanical);
        Assert.False(build.SaveEffects[1].ConditionIsMechanical);
        Assert.Contains("Cause Fear: frightened is shown as a label; nothing here models its effect.", build.Notes);
    }

    [Fact]
    public void Resolve_ConditionOnHit_DefaultDcFollowsEachAttacksAbility()
    {
        var build = BuildResolver.Resolve(WithModifier(
            """{ "kind": "condition_on_hit", "name": "Trip", "condition": "prone", "ability": "str" }""",
            "\"abilities\": {\"str\": 18, \"dex\": 14},"), 5);

        var trip = Assert.Single(build.ConditionsOnHit);
        Assert.Equal(["Greatsword", "Longbow"], trip.Attacks);
        Assert.Equal([new NamedValue("base", 8), new NamedValue("proficiency", 3), new NamedValue("Str", 4)], trip.Dcs[0].Parts);
        Assert.Equal([new NamedValue("base", 8), new NamedValue("proficiency", 3), new NamedValue("Dex", 2)], trip.Dcs[1].Parts);
        Assert.Equal(15, trip.DcFor("Greatsword"));
        Assert.Equal(13, trip.DcFor("Longbow"));
        Assert.Equal(V.When.EveryHit, trip.When);
        Assert.False(trip.Magical);
    }

    [Fact]
    public void Resolve_ConditionOnHitWithDcAbility_UsesItForEveryAttack()
    {
        var build = BuildResolver.Resolve(WithModifier(
            """{ "kind": "condition_on_hit", "name": "Stunning Strike", "condition": "stunned", "ability": "con", "dc_ability": "wis", "when": "first_hit_per_turn", "resource": {"uses": 5, "per": "short_rest"} }""",
            "\"abilities\": {\"wis\": 16},"), 5);

        Assert.Equal([14, 14], build.ConditionsOnHit[0].Dcs.Select(d => d.Dc));
        Assert.Equal(V.When.FirstHitPerTurn, build.ConditionsOnHit[0].When);
    }

    [Fact]
    public void Resolve_AdvantagePowerAttackRerollAndSetup_AreTypedWithDefaults()
    {
        var build = BuildResolver.Resolve(With(
            """
            [{ "name": "Greatsword", "count": 2, "damage": "2d6", "properties": ["melee", "two-handed", "heavy"] },
             { "name": "Eldritch Blast", "to_hit": {"ability": "cha"}, "damage": "1d10", "properties": ["ranged", "spell"] }]
            """,
            """
            [{ "kind": "advantage", "name": "Reckless Attack", "rate": 0.5, "attacks": ["Greatsword"] },
             { "kind": "advantage", "mode": "disadvantage" },
             { "kind": "power_attack", "policy": "always" },
             { "kind": "reroll_damage_take_best", "name": "Savage Attacker" },
             { "kind": "extra_damage", "name": "Hex", "dice": "1d6", "type": "necrotic", "setup": "bonus_action", "concentration": true }]
            """), 5);

        Assert.Equal(
            [("Reckless Attack", V.AdvantageModes.Advantage, 0.5, "Greatsword"), ("advantage #2", V.AdvantageModes.Disadvantage, 1.0, "Greatsword,Eldritch Blast")],
            build.AdvantageSources.Select(a => (a.Source.Label, a.Mode, a.Rate, string.Join(",", a.Attacks))));
        Assert.Equal(["Greatsword"], build.PowerAttacks[0].Attacks);
        Assert.Equal(V.PowerAttackPolicies.Always, build.PowerAttacks[0].Policy);
        Assert.Equal(["Greatsword"], build.DamageRerolls[0].Attacks);
        Assert.Equal(V.Policies.AnyHit, build.DamageRerolls[0].Policy);
        var setup = Assert.Single(build.SetupCosts);
        Assert.Equal(("Hex", V.Setup.BonusAction), (setup.Source.Label, setup.Cost));
        Assert.Equal(["Hex (setup)"], build.BonusActionConsumers);
        Assert.Equal(new ModifierRef(2, V.Kinds.Advantage, null), build.AdvantageSources[1].Source);
        Assert.Equal("modifiers item 2 (advantage)", build.AdvantageSources[1].Source.Where);
        Assert.Contains(build.Notes, n => n.Contains("2024 has no -5/+10 power attack", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_ElementalAdept_AppliesToAttacksAndSaveEffectsOfItsTypeWithANote()
    {
        var build = BuildResolver.Resolve(Build("""
            { "name": "Evoker", "level": 5, "abilities": {"int": 18},
              "attacks": [{ "name": "Fire Bolt", "to_hit": {"ability": "int"}, "ability_to_damage": false, "damage": "1d10", "damage_type": "fire", "properties": ["ranged", "spell"], "cantrip": "dice" }],
              "modifiers": [{ "kind": "damage_die_remap", "name": "Elemental Adept", "remap": "elemental_adept", "type": "fire" },
                            { "kind": "save_effect", "name": "Fireball", "ability": "dex", "dc_ability": "int", "dice": "8d6", "type": "fire", "shape": "sphere", "size": 20, "action_cost": "bonus_action" }] }
            """), 5);

        Assert.Equal(["fire"], build.Attacks[0].ElementalAdeptTypes);
        Assert.Equal("2d10", build.Attacks[0].Damage.Text);
        Assert.Empty(build.Attacks[0].DamageParts);
        Assert.Equal(["fire"], build.SaveEffects[0].ElementalAdeptTypes);
        Assert.Equal(15, build.SaveEffects[0].Dc);
        Assert.Contains(build.Remaps, r => r.Remap == V.Remaps.ElementalAdept && r.DamageType == "fire");
        Assert.Contains(build.Notes, n => n.Contains("\"ignore resistance\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_ExtraAttackForms_CarryCountActionTriggerAndProbability()
    {
        var build = BuildResolver.Resolve(With(modifiersJson: """
            [{ "kind": "extra_attack", "name": "Action Surge", "attack": "greatsword", "count": {"1": 2, "11": 3}, "action": "action", "resource": {"uses": {"1": 1, "17": 2}, "per": "short_rest"} },
             { "kind": "extra_attack", "name": "Sentinel", "attack": "Greatsword", "action": "reaction", "trigger_probability": 0.25 }]
            """), 17);

        Assert.Equal(("Greatsword", 3, V.ExtraAttackActions.Action, V.Triggers.Always), (build.ExtraAttacks[0].Attack, build.ExtraAttacks[0].Count, build.ExtraAttacks[0].Action, build.ExtraAttacks[0].Trigger));
        Assert.Equal(new ResolvedResource(2, V.Rests.ShortRest), build.ExtraAttacks[0].Resource);
        Assert.Equal(0.25, build.ExtraAttacks[1].TriggerProbability);
        Assert.Equal(["Sentinel"], build.ReactionConsumers);
        Assert.Empty(build.BonusActionConsumers);
        Assert.Equal(2 + 1 + 3 + 1, build.MaxAttacksPerTurn);
    }

    [Fact]
    public void Resolve_Defensive_IsKeptWithANoteAndNoEffectOnAttacks()
    {
        var build = BuildResolver.Resolve(With(modifiersJson: """
            [{ "kind": "ac", "name": "Shield of Faith", "amount": 2, "concentration": true },
             { "kind": "resistance", "name": "Rage", "type": "slashing" },
             { "kind": "temp_hp", "amount": "cha" }]
            """), 5);

        Assert.Equal([("ac", (int?)2, (string?)null), ("resistance", null, "slashing"), ("temp_hp", 0, null)], build.Defensive.Select(d => (d.Kind, d.Amount, d.DamageType)));
        Assert.Equal(3, build.Notes.Count(n => n.Contains("is defensive: kept for the simulator", StringComparison.Ordinal)));
        Assert.Equal(3, build.Attacks[0].AttackBonus);
    }

    [Fact]
    public void Resolve_FromAndUntilLevel_DropInactiveAttacksAndModifiers()
    {
        var spec = With(
            """
            [{ "name": "Dagger", "damage": "1d4", "until_level": 4 },
             { "name": "Flame Blade", "damage": "3d6", "from_level": 5 }]
            """,
            """[{ "kind": "to_hit", "amount": 1, "from_level": 11 }]""");

        Assert.Equal(["Dagger"], BuildResolver.Resolve(spec, 4).Attacks.Select(a => a.Name));
        Assert.Equal(["Flame Blade"], BuildResolver.Resolve(spec, 5).Attacks.Select(a => a.Name));
        Assert.Equal(4, BuildResolver.Resolve(spec, 10).Attacks[0].AttackBonus);
        Assert.Equal(5, BuildResolver.Resolve(spec, 11).Attacks[0].AttackBonus);
    }

    [Fact]
    public void Resolve_ExplicitFilterWithNoActiveAttack_SaysSo()
    {
        var build = BuildResolver.Resolve(With(
            """
            [{ "name": "Dagger", "damage": "1d4" },
             { "name": "Flame Blade", "damage": "3d6", "from_level": 5 }]
            """,
            """[{ "kind": "bonus_damage", "name": "Blade ward", "amount": 2, "attacks": ["Flame Blade"] }]"""), 3);

        Assert.Contains("Blade ward (bonus_damage) applies to no attack active at level 3.", build.Notes);
    }

    [Fact]
    public void Resolve_MasteryOn2014Build_IsUsedWithANote()
    {
        var build = BuildResolver.Resolve(With(
            """[{ "name": "Maul", "damage": "2d6", "properties": ["melee", "heavy", "two-handed"], "mastery": "topple" }]""",
            extra: "\"edition\": \"2014\","), 5);

        Assert.Equal(V.Masteries.Topple, build.Attacks[0].Mastery);
        Assert.Contains("Weapon masteries are a 2024 rule; Maul's topple is used as given on this 2014 build.", build.Notes);
    }

    [Fact]
    public void Resolve_DamageStepMapAndCantripDice_AreReadAtTheLevel()
    {
        var spec = With(
            """
            [{ "name": "Unarmed", "damage": {"1": "1d6", "5": "1d8", "11": "1d10", "17": "1d12"}, "properties": ["melee"] },
             { "name": "Toll the Dead", "to_hit": {"ability": "wis"}, "ability_to_damage": false, "damage": "1d12", "properties": ["ranged", "spell"], "cantrip": "dice" }]
            """);

        Assert.Equal(["1d6", "1d12"], BuildResolver.Resolve(spec, 4).Attacks.Select(a => a.Damage.Text));
        Assert.Equal(["1d8", "2d12"], BuildResolver.Resolve(spec, 5).Attacks.Select(a => a.Damage.Text));
        Assert.Equal(["1d12", "4d12"], BuildResolver.Resolve(spec, 20).Attacks.Select(a => a.Damage.Text));
    }

    [Fact]
    public void Resolve_ProficiencyBonusOverride_AppliesAtEveryLevel()
    {
        var builds = BuildResolver.Resolve(With(extra: "\"proficiency_bonus\": 4,"), [1, 20]);

        Assert.Equal([4, 4], builds.Select(b => b.ProficiencyBonus));
    }

    [Fact]
    public void Resolve_Rulings_AreEchoed()
    {
        var build = BuildResolver.Resolve(Build(Fighter2024GwmJson), 5, new RulingsSpec { HewGetsPb = true, GwfOnRiders = true });

        Assert.Equal(new ResolvedRulings(HewGetsPb: true, CleavePartOfAttackAction: false, GwfOnRiders: true, SavageAttackerOnCritDice: false), build.Rulings);
        Assert.Equal(ResolvedRulings.Default, BuildResolver.Resolve(Build(Fighter2024GwmJson), 5).Rulings);
    }

    [Fact]
    public void Resolve_EmberEdgePlanExample_Resolves()
    {
        var build = BuildResolver.Resolve(Build(EmberEdgeJson), 5);

        Assert.Equal(V.Remaps.Gwf2024, build.Attacks[0].WeaponRemap);
        Assert.True(build.Attacks[0].IsMelee);
        var rider = Assert.Single(build.Riders);
        Assert.Equal(("1d6", "fire", V.When.FirstHitPerTurn), (rider.Damage.Text, rider.DamageType, rider.When));
        Assert.Equal("extra_damage #1", rider.Source.Label);
    }

    [Theory]
    [InlineData(Fighter2014GwmJson, true)]
    [InlineData(PaladinSmiteJson, false)]
    [InlineData(FireballJson, false)]
    [InlineData(EmberEdgeJson, false)]
    public void ScalesWithLevel_StepMapsLevelRangesAndCantrips_AreWhatScale(string json, bool scales)
    {
        Assert.Equal(scales, BuildResolver.ScalesWithLevel(Build(json)));
    }

    [Theory]
    [InlineData("""[{ "name": "A", "damage": "1d8", "from_level": 3 }, { "name": "B", "damage": "1d8" }]""", "[]", "")]
    [InlineData("""[{ "name": "A", "damage": "1d8", "until_level": 10 }, { "name": "B", "damage": "1d8" }]""", "[]", "")]
    [InlineData("""[{ "name": "A", "damage": "1d10", "cantrip": "dice" }]""", "[]", "")]
    [InlineData("""[{ "name": "A", "damage": "1d10" }]""", """[{ "kind": "to_hit", "amount": {"1": 0, "10": 1} }]""", "")]
    [InlineData("""[{ "name": "A", "damage": "1d10" }]""", """[{ "kind": "to_hit", "amount": 1, "from_level": 4 }]""", "")]
    [InlineData("""[{ "name": "A", "damage": "1d10" }]""", "[]", "\"abilities\": {\"str\": {\"1\": 16, \"4\": 18}},")]
    [InlineData("""[{ "name": "A", "damage": "1d10" }]""", """[{ "kind": "save_effect", "ability": "dex", "dc": 13, "dice": "1d6", "cantrip": true, "action_cost": "bonus_action" }]""", "")]
    [InlineData("""[{ "name": "A", "damage": "1d10" }]""", """[{ "kind": "extra_attack", "attack": "A", "action": "action", "resource": {"uses": {"1": 1, "17": 2}, "per": "short_rest"} }]""", "")]
    public void ScalesWithLevel_AnyScalingField_Scales(string attacks, string modifiers, string extra)
    {
        Assert.True(BuildResolver.ScalesWithLevel(With(attacks, modifiers, extra)));
    }

    [Fact]
    public void ScalesWithLevel_OneStepStepMap_DoesNotScale()
    {
        Assert.False(BuildResolver.ScalesWithLevel(With("""[{ "name": "A", "damage": {"1": "1d10"}, "count": {"1": 2} }]""")));
    }
}
