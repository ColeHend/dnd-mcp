using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation;
using DndMcp.Repository.Srd;
using Xunit;
using V = DndMcp.Domain.Simulation.StatBlockValues;

namespace DndMcp.Tests.Srd.Combatants;

/// <summary>
/// Hand-verified stat blocks: each expected value was read off the monster's text in the SRD (5.1 for 2014, 5.2.1 for
/// 2024), not off the normalizer's output. Together they cover every trap the normalizer exists for: multiattack
/// choices, breath recharges, legendary costs and uses, Legendary Resistance in and out of a lair, spell slots and cast
/// levels, variable multiattack counts, qualified resistances, regeneration, Undead Fortitude, save riders, grapple
/// escapes, swallowing, shapechanger forms, versatile weapons and fuzzy references.
/// </summary>
public sealed class MonsterSpotCheckTests
{
    private static StatBlock Block(string edition, string slug) => CorrectedSrd.Shipped.StatBlock(edition, slug);

    private static StatBlockAction Action(StatBlock block, string name) =>
        block.FindAction(name) ?? throw new Xunit.Sdk.XunitException($"{block.Ref} has no action {name}.");

    private static string Dice(IEnumerable<DamageRoll> rolls) => string.Join(" + ", rolls.Select(r => $"{r.Dice} {r.DamageType}"));

    private static string Routine(MultiattackRoutine r) =>
        string.Join(", ", r.Steps.Select(s => $"{s.ActionName} x{s.Count}")) +
        (r.Choose > 0 ? $" + {r.Choose} of [{string.Join(", ", r.Options.Select(o => o.ActionName))}]" : string.Empty);

    [Fact]
    public void Goblin_2014_IsTwoAttacksAndAWarnedNimbleEscape()
    {
        var goblin = Block("2014", "goblin");

        Assert.Equal((15, 7, "2d6", 2), (goblin.ArmorClass, goblin.HitPoints, goblin.HitDice.Text, goblin.InitiativeBonus));
        Assert.Equal((4, V.AttackRanges.Melee, "1d6+2 slashing"), (Action(goblin, "Scimitar").AttackBonus, Action(goblin, "Scimitar").Range, Dice(Action(goblin, "Scimitar").Damage)));
        Assert.Equal((4, V.AttackRanges.Ranged, "1d6+2 piercing"), (Action(goblin, "Shortbow").AttackBonus, Action(goblin, "Shortbow").Range, Dice(Action(goblin, "Shortbow").Damage)));
        Assert.Contains(goblin.Warnings, w => w.Where == "trait Nimble Escape" && w.Code == V.WarningCodes.NotModelled);
        Assert.Empty(goblin.Multiattacks);
    }

    [Fact]
    public void GoblinWarrior_2024_KeepsItsBaseDamageAndWarnsAboutTheAdvantageBonus()
    {
        var goblin = Block("2024", "goblin-warrior");

        Assert.Equal("1d6+2 slashing", Dice(Action(goblin, "Scimitar").Damage));
        Assert.Equal(2, goblin.InitiativeBonus);
        Assert.Contains(goblin.Warnings, w => w.Where == "Scimitar" && w.Message.Contains("if the attack roll had Advantage", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("2014", 59, "7d10+21")]
    [InlineData("2024", 68, "8d10+24")]
    public void Ogre_BothEditions_HasAGreatclubAndAThrownJavelin(string edition, int hp, string dice)
    {
        var ogre = Block(edition, "ogre");

        Assert.Equal((11, hp, dice), (ogre.ArmorClass, ogre.HitPoints, ogre.HitDice.Text));
        Assert.Equal((6, "2d8+4 bludgeoning"), (Action(ogre, "Greatclub").AttackBonus, Dice(Action(ogre, "Greatclub").Damage)));
        var javelin = Action(ogre, "Javelin");
        Assert.Equal((V.AttackRanges.Melee, true, "2d6+4 piercing"), (javelin.Range, javelin.AlsoRanged, Dice(javelin.Damage)));
        Assert.Empty(ogre.Warnings);
    }

    [Fact]
    public void Owlbear_BothEditions_AttacksTwice()
    {
        Assert.Equal("Beak x1, Claws x1", Routine(Assert.Single(Block("2014", "owlbear").Multiattacks)));
        Assert.Equal("Rend x2", Routine(Assert.Single(Block("2024", "owlbear").Multiattacks)));
        Assert.Equal("2d8+5 slashing", Dice(Action(Block("2024", "owlbear"), "Rend").Damage));
    }

    [Fact]
    public void AdultRedDragon_2014_ReadsItsBreathLegendaryCostsAndResistance()
    {
        var dragon = Block("2014", "adult-red-dragon");

        Assert.Equal("Frightful Presence x1, Bite x1, Claw x2", Routine(Assert.Single(dragon.Multiattacks)));
        Assert.Equal("2d10+8 piercing + 2d6 fire", Dice(Action(dragon, "Bite").Damage));
        var breath = Action(dragon, "Fire Breath");
        Assert.Equal((V.UsageKinds.Recharge, 5), (breath.Usage.Kind, breath.Usage.RechargeMin));
        Assert.Equal(new SaveSpec("dex", 21, V.OnSuccess.Half), breath.Save);
        Assert.Equal(new AreaSpec(V.Shapes.Cone, 60), breath.Area);
        Assert.Equal("18d6 fire", Dice(breath.Damage));
        var presence = Action(dragon, "Frightful Presence");
        Assert.Equal((V.Conditions.Frightened, V.Durations.SaveEnds, 10), (presence.Condition!.Condition, presence.Condition.Duration, presence.Condition.Rounds));
        Assert.Equal((3, (int?)null), (dragon.Legendary!.Uses, dragon.Legendary.UsesInLair));
        var wing = Action(dragon, "Wing Attack");
        Assert.Equal((2, V.ActionKinds.Save, "dex", 22), (wing.LegendaryCost, wing.Kind, wing.Save!.Ability, wing.Save.Dc));
        Assert.Equal((V.Conditions.Prone, new AreaSpec(V.Shapes.Emanation, 10)), (wing.Condition!.Condition, wing.Area));
        Assert.Equal("Tail", Assert.Single(Action(dragon, "Tail Attack").Uses).ActionName);
        Assert.Equal((3, (int?)null), (dragon.LegendaryResistance, dragon.LegendaryResistanceInLair));
    }

    [Fact]
    public void AdultRedDragon_2024_ChoosesScorchingRayAndHasFourLegendaryUsesInItsLair()
    {
        var dragon = Block("2024", "adult-red-dragon");

        Assert.Equal("Rend x2 + 1 of [Rend, Scorching Ray]", Routine(Assert.Single(dragon.Multiattacks)));
        var ray = Action(dragon, "Scorching Ray");
        Assert.Equal((V.ActionKinds.Attack, 12, 3, 2, "2d6 fire", true), (ray.Kind, ray.AttackBonus, ray.AttackRolls, ray.SpellLevel, Dice(ray.Damage), ray.IsSpell));
        Assert.Equal((V.UsageKinds.PerDay, 1, "8d6 fire", 20), (Action(dragon, "Fireball").Usage.Kind, Action(dragon, "Fireball").Usage.Uses, Dice(Action(dragon, "Fireball").Damage), Action(dragon, "Fireball").Save!.Dc));
        Assert.Equal(4, dragon.InitiativeBonus);
        Assert.Equal((3, (int?)4), (dragon.Legendary!.Uses, dragon.Legendary.UsesInLair));
        Assert.Equal((3, (int?)4), (dragon.LegendaryResistance, dragon.LegendaryResistanceInLair));
        var rays = Action(dragon, "Fiery Rays");
        Assert.Equal((V.ActionKinds.UseActions, true, "Scorching Ray"), (rays.Kind, rays.OncePerRound, Assert.Single(rays.Uses).ActionName));
        Assert.Equal((false, "Rend"), (Action(dragon, "Pounce").OncePerRound, Assert.Single(Action(dragon, "Pounce").Uses).ActionName));
        Assert.Equal(V.ActionKinds.NotModelled, Action(dragon, "Commanding Presence").Kind);
        Assert.Equal(V.OnSuccess.Half, Action(dragon, "Fire Breath").Save!.OnSuccess);
    }

    [Fact]
    public void Lich_2014_CastsFromSlotsAndHalvesDisruptLifeByOverride()
    {
        var lich = Block("2014", "lich");

        Assert.Equal(new Dictionary<int, int> { [1] = 4, [2] = 3, [3] = 3, [4] = 3, [5] = 3, [6] = 1, [7] = 1, [8] = 1, [9] = 1 }, lich.SpellSlots);
        var fireball = Action(lich, "Fireball");
        Assert.Equal(("slot:3", 3, 20, "8d6 fire"), (fireball.Usage.Pool, fireball.SpellLevel, fireball.Save!.Dc, Dice(fireball.Damage)));
        var frost = Action(lich, "Ray of Frost");
        Assert.Equal((V.UsageKinds.AtWill, 12, "4d8 cold"), (frost.Usage.Kind, frost.AttackBonus, Dice(frost.Damage)));
        var missile = Action(lich, "Magic Missile");
        Assert.Equal((V.ActionKinds.AutoHit, 3, "1d4+1 force"), (missile.Kind, missile.Targets, Dice(missile.Damage)));
        Assert.Equal((V.ActionKinds.Parry, 5, V.ActionSlots.Reaction), (Action(lich, "Shield").Kind, Action(lich, "Shield").AcBonus, Action(lich, "Shield").Slot));
        var disrupt = Action(lich, "Disrupt Life");
        Assert.Equal((3, V.OnSuccess.Half, "6d6 necrotic"), (disrupt.LegendaryCost, disrupt.Save!.OnSuccess, Dice(disrupt.Damage)));
        Assert.Equal("Ray of Frost", Assert.Single(Action(lich, "Cantrip").Uses).ActionName);
        Assert.Contains(lich.Warnings, w => w.Where == "Spellcasting" && w.Message.Contains("Counterspell", StringComparison.Ordinal));
        Assert.DoesNotContain(lich.Warnings, w => w.Code == V.WarningCodes.DataConflict);
        var touch = Action(lich, "Paralyzing Touch");
        Assert.Equal((V.Conditions.Paralyzed, V.Durations.SaveEnds, 18), (touch.OnHit[0].Condition!.Condition, touch.OnHit[0].Condition!.Duration, touch.OnHit[0].Save!.Dc));
    }

    [Fact]
    public void Lich_2024_CastsAtItsDataLevelsAndChoosesThreeAttacks()
    {
        var lich = Block("2024", "lich");

        Assert.Equal(" + 3 of [Eldritch Burst, Paralyzing Touch]", Routine(Assert.Single(lich.Multiattacks)));
        Assert.Equal((5, "10d6 fire", V.UsageKinds.AtWill), (Action(lich, "Fireball").SpellLevel, Dice(Action(lich, "Fireball").Damage), Action(lich, "Fireball").Usage.Kind));
        Assert.Equal((4, "10d8 lightning"), (Action(lich, "Chain Lightning").Targets, Dice(Action(lich, "Chain Lightning").Damage)));
        Assert.Equal((V.Conditions.Paralyzed, V.Durations.UntilStartOfSourceTurn), (Action(lich, "Paralyzing Touch").OnHit[0].Condition!.Condition, Action(lich, "Paralyzing Touch").OnHit[0].Condition!.Duration));
        Assert.Equal((4, (int?)5), (lich.LegendaryResistance, lich.LegendaryResistanceInLair));
        Assert.Equal(7, lich.InitiativeBonus);
    }

    [Theory]
    [InlineData("2014")]
    [InlineData("2024")]
    public void Hydra_BothEditions_BitesOncePerStartingHeadWithAWarning(string edition)
    {
        var hydra = Block(edition, "hydra");

        Assert.Equal("Bite x5", Routine(Assert.Single(hydra.Multiattacks)));
        Assert.Contains(hydra.Warnings, w => w.Code == V.WarningCodes.Approximated && w.Where == "Multiattack" && w.Message.Contains("5 heads", StringComparison.Ordinal));
    }

    [Fact]
    public void VioletFungus_2014_Rolls1d4AsTwoWithAWarning_2024_MakesTwo()
    {
        var old = Block("2014", "violet-fungus");
        Assert.Equal("Rotting Touch x2", Routine(Assert.Single(old.Multiattacks)));
        Assert.Contains(old.Warnings, w => w.Code == V.WarningCodes.Approximated && w.Message.Contains("1d4", StringComparison.Ordinal));

        var current = Block("2024", "violet-fungus");
        Assert.Equal("Rotting Touch x2", Routine(Assert.Single(current.Multiattacks)));
        Assert.DoesNotContain(current.Warnings, w => w.Where == "Multiattack");
    }

    [Fact]
    public void Werewolf_2014_IsImmuneOnlyToNonmagicalUnsilveredWeapons()
    {
        var werewolf = Block("2014", "werewolf-hybrid");

        Assert.Equal(["bludgeoning", "piercing", "slashing"], werewolf.Immunities.Select(i => i.DamageType));
        Assert.All(werewolf.Immunities, i =>
        {
            Assert.Equal(V.DamageQualifiers.NonmagicalNotSilvered, i.Qualifier);
            Assert.True(i.AppliesTo(magical: false, silvered: false, adamantine: false));
            Assert.False(i.AppliesTo(magical: true, silvered: false, adamantine: false));
            Assert.False(i.AppliesTo(magical: false, silvered: true, adamantine: false));
        });
        Assert.Equal(["werewolf-human", "werewolf-wolf"], werewolf.Forms.Select(f => f[(f.LastIndexOf('/') + 1)..]).Order());
    }

    [Fact]
    public void Werewolf_2024_ExpandsItsCappedBiteIntoExactRoutines()
    {
        var werewolf = Block("2024", "werewolf-hybrid");

        Assert.Equal(
            ["Longbow x1, Bite x1", "Longbow x2", "Scratch x1, Bite x1", "Scratch x1, Longbow x1", "Scratch x2"],
            werewolf.Multiattacks.Select(Routine).Order());
        Assert.Empty(werewolf.Immunities);
    }

    [Theory]
    [InlineData("2014", 10)]
    [InlineData("2024", 15)]
    public void Troll_BothEditions_RegeneratesUnlessBurnedOrMelted(string edition, int amount)
    {
        var regeneration = Block(edition, "troll").Trait(V.TraitKinds.Regeneration)!;

        Assert.Equal(amount, regeneration.Amount);
        Assert.Equal(["acid", "fire"], regeneration.DamageTypes.Order());
    }

    [Theory]
    [InlineData("2014")]
    [InlineData("2024")]
    public void Zombie_BothEditions_HasUndeadFortitude(string edition)
    {
        var zombie = Block(edition, "zombie");

        Assert.True(zombie.Has(V.TraitKinds.UndeadFortitude));
        Assert.DoesNotContain(zombie.Warnings, w => w.Where.Contains("Undead Fortitude", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("2014", "2d6+3 bludgeoning + 3d6 necrotic")]
    [InlineData("2024", "1d10+3 bludgeoning + 3d6 necrotic")]
    public void Mummy_RottingFist_DealsBothDamagesAndWarnsThatTheRotIsNotSimulated(string edition, string damage)
    {
        var mummy = Block(edition, "mummy");
        var fist = Action(mummy, "Rotting Fist");

        Assert.Equal(damage, Dice(fist.Damage));
        Assert.Empty(fist.OnHit);
        Assert.Contains(mummy.Warnings, w => w.Where == "Rotting Fist" && w.Message.Contains("curse", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GiantSpider_2014_WebRestrainsUntilAStrengthCheckEscapes()
    {
        var spider = Block("2014", "giant-spider");
        var web = Action(spider, "Web");

        Assert.Equal((V.AttackRanges.Ranged, 5, V.UsageKinds.Recharge, 5), (web.Range, web.AttackBonus, web.Usage.Kind, web.Usage.RechargeMin));
        var restrained = Assert.Single(web.OnHit).Condition!;
        Assert.Equal((V.Conditions.Restrained, V.Durations.UntilEscape, 12), (restrained.Condition, restrained.Duration, restrained.EscapeDc));
        var bite = Assert.Single(Action(spider, "Bite").OnHit);
        Assert.Equal((new SaveSpec("con", 11, V.OnSuccess.Half), "2d8 poison"), (bite.Save, Dice(bite.Damage)));
    }

    [Fact]
    public void GiantSpider_2024_WebIsASaveThatRestrainsUntilTheWebIsDestroyed()
    {
        var web = Action(Block("2024", "giant-spider"), "Web");

        Assert.Equal((V.ActionKinds.Save, "dex", 13, V.Conditions.Restrained, V.Durations.Fight), (web.Kind, web.Save!.Ability, web.Save.Dc, web.Condition!.Condition, web.Condition.Duration));
        Assert.Contains(Block("2024", "giant-spider").Warnings, w => w.Where == "Web" && w.Code == V.WarningCodes.Approximated);
    }

    [Fact]
    public void Kraken_2014_GrapplesWithTentaclesAndItsSwallowIsWarned()
    {
        var kraken = Block("2014", "kraken");

        var tentacle = Assert.Single(Action(kraken, "Tentacle").OnHit);
        Assert.Equal((V.Conditions.Restrained, 18), (tentacle.Condition!.Condition, tentacle.Condition.EscapeDc));
        Assert.Contains(tentacle.ExtraConditions, c => c.Condition == V.Conditions.Grappled && c.EscapeDc == 18);
        var bite = Action(kraken, "Bite");
        Assert.Equal(("3d8+10 piercing", 17), (Dice(bite.Damage), bite.AttackBonus));
        Assert.Empty(bite.OnHit);
        Assert.Contains(kraken.Warnings, w => w.Where == "Bite" && w.Message.Contains("swallowed", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, Action(kraken, "Lightning Storm").Targets);
        Assert.Equal(["Tentacle Attack or Fling (Fling)", "Tentacle Attack or Fling (Tentacle)"],
            kraken.Legendary!.Actions.Where(a => a.Name.StartsWith("Tentacle Attack", StringComparison.Ordinal)).Select(a => a.Name).Order());
    }

    [Fact]
    public void Kraken_2024_SwallowIsApproximatedAsRestrainedWithAcidEachTurn()
    {
        var kraken = Block("2024", "kraken");
        var swallow = Action(kraken, "Swallow");

        Assert.Equal((1, "3d8+10 piercing", V.Conditions.Restrained, "7d6 acid"),
            (swallow.Targets, Dice(swallow.Damage), swallow.Condition!.Condition, Dice(swallow.Condition.OngoingDamage)));
        Assert.Contains(kraken.Warnings, w => w.Where == "Swallow" && w.Code == V.WarningCodes.Approximated);
        Assert.Equal("Tentacle x2 + 1 of [Fling, Lightning Strike, Swallow]", Routine(Assert.Single(kraken.Multiattacks)));
    }

    [Fact]
    public void Vampire_2014_FormsKeepTheirOwnActionsAndWarnAboutTheOthers()
    {
        var vampire = Block("2014", "vampire-vampire");
        Assert.Equal(["Unarmed Strike x1, Bite x1", "Unarmed Strike x2"], vampire.Multiattacks.Select(Routine).Order());
        var strike = vampire.Legendary!.Actions.Single(a => a.Name == "Unarmed Strike");
        Assert.Equal((V.ActionKinds.UseActions, "Unarmed Strike"), (strike.Kind, Assert.Single(strike.Uses).ActionName));
        Assert.Equal(2, vampire.Legendary.Actions.Single(a => a.Name == "Bite").LegendaryCost);
        Assert.Contains(vampire.Warnings, w => w.Where == "Bite" && w.Message.Contains("willing creature", StringComparison.Ordinal));

        var bat = Block("2014", "vampire-bat");
        Assert.DoesNotContain(bat.Actions, a => a.Name == "Unarmed Strike");
        Assert.Contains(bat.Warnings, w => w.Code == V.WarningCodes.UnresolvedReference && w.Message.Contains("unarmed strike", StringComparison.Ordinal));
        Assert.Equal(2, vampire.Forms.Count);
    }

    [Fact]
    public void Mage_2014_HasSlotsAndCantripsAtItsCasterLevel()
    {
        var mage = Block("2014", "mage");

        Assert.Equal(new Dictionary<int, int> { [1] = 4, [2] = 3, [3] = 3, [4] = 3, [5] = 1 }, mage.SpellSlots);
        Assert.Equal(("2d10 fire", 6), (Dice(Action(mage, "Fire Bolt").Damage), Action(mage, "Fire Bolt").AttackBonus));
        Assert.Equal(("slot:5", "8d8 cold", new AreaSpec(V.Shapes.Cone, 60)), (Action(mage, "Cone of Cold").Usage.Pool, Dice(Action(mage, "Cone of Cold").Damage), Action(mage, "Cone of Cold").Area));
        Assert.Contains(mage.Notes, n => n.Contains("Mage Armor", StringComparison.Ordinal) || n.Contains("mage armor", StringComparison.Ordinal));
        Assert.Equal((12, "15 with Mage Armor"), (mage.ArmorClass, mage.ArmorClassNote));
    }

    [Fact]
    public void Mage_2024_UpcastsFireballToItsCastLevel()
    {
        var mage = Block("2024", "mage");

        var fireball = Action(mage, "Fireball");
        Assert.Equal((4, "9d6 fire", 2, 14), (fireball.SpellLevel, Dice(fireball.Damage), fireball.Usage.Uses, fireball.Save!.Dc));
        Assert.Equal((V.UsageKinds.PerDay, 3), (Action(mage, "Shield").Usage.Kind, Action(mage, "Shield").Usage.Uses));
        Assert.Equal("Arcane Burst x3", Routine(Assert.Single(mage.Multiattacks)));
    }

    [Fact]
    public void Erinyes_2014_UsesTheTwoHandedLongswordAndMagicalWeapons()
    {
        var erinyes = Block("2014", "erinyes");
        var sword = Action(erinyes, "Longsword");

        Assert.Equal(("1d10+4 slashing + 3d8 poison", true), (Dice(sword.Damage), sword.Magical));
        Assert.Contains(sword.Notes, n => n.StartsWith("Versatile", StringComparison.Ordinal));
        var poison = Assert.Single(Action(erinyes, "Longbow").OnHit);
        Assert.Equal((V.Conditions.Poisoned, V.Durations.Fight, 14), (poison.Condition!.Condition, poison.Condition.Duration, poison.Save!.Dc));
        Assert.Equal((V.ActionKinds.Parry, 4), (Action(erinyes, "Parry").Kind, Action(erinyes, "Parry").AcBonus));
        Assert.Equal(4, erinyes.Multiattacks.Count);
    }

    [Fact]
    public void Erinyes_2024_ResolvesItsRopeFuzzily()
    {
        var erinyes = Block("2024", "erinyes");

        Assert.Equal("Withering Sword x3, Entangling Rope (Requires Magic Rope) x1", Routine(Assert.Single(erinyes.Multiattacks)));
        var rope = Action(erinyes, "Entangling Rope (Requires Magic Rope)");
        Assert.Equal((V.ActionKinds.Save, "str", 16, "4d6 force", V.Conditions.Restrained), (rope.Kind, rope.Save!.Ability, rope.Save.Dc, Dice(rope.Damage), rope.Condition!.Condition));
    }

    [Fact]
    public void Aboleth_2024_TakesItsPrintedInitiativeNotItsDexterity()
    {
        var aboleth = Block("2024", "aboleth");

        Assert.Equal((-1, 3), (aboleth.Modifier(DslValues.Abilities.Dex), aboleth.InitiativeBonus));
        Assert.Contains(aboleth.Notes, n => n.StartsWith("Initiative +3", StringComparison.Ordinal));
    }

    [Fact]
    public void AdultSilverDragon_2014_BreathsShareOneRecharge()
    {
        var dragon = Block("2014", "adult-silver-dragon");
        var cold = Action(dragon, "Cold Breath");
        var paralyzing = Action(dragon, "Paralyzing Breath");

        Assert.Equal("recharge:Breath Weapons", cold.Usage.Pool);
        Assert.Equal(cold.Usage, paralyzing.Usage);
        Assert.Equal((V.Conditions.Paralyzed, V.Durations.SaveEnds), (paralyzing.Condition!.Condition, paralyzing.Condition.Duration));
        Assert.Equal(("13d8 cold", V.OnSuccess.Half), (Dice(cold.Damage), cold.Save!.OnSuccess));
    }
}
