using DndMcp.Domain.Rules;
using DndMcp.Domain.Simulation;
using Xunit;
using static DndMcp.Tests.Rules.RulesKit;
using K = DndMcp.Domain.Simulation.StatBlockValues;

namespace DndMcp.Tests.Rules;

/// <summary>
/// Invariant: one damage instance goes through ONE pipeline for the tracker and the sheet — per type: half on a save,
/// immune, resistant (or Petrified) once, vulnerable once, rounding down; temporary hit points first; then the drop to 0
/// per edition (a knock-out instead of massive damage or a monster's death; a death interceptor keeps a monster at 0,
/// defeated, not dead), damage at 0 HP on the pre-temp total, concentration and Bloodied. The goldens are the SRD's own
/// worked examples (RULES §1) and the arithmetic of the exit-criterion fixtures (FIX A8, A14, A18, A21, A23, B8, B13,
/// B15, B18, B20, B26).
/// </summary>
public sealed class DamageRulesTests
{
    // ------------------------------------------------------------------------------------------------------------------
    // The SRD's worked examples.
    // ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Damage_2014ExampleReducedBy5ThenResisted_TakesTen()
    {
        // SRD 5.1: "The 25 damage is first reduced by 5 and then halved, so the creature takes 10 damage." The flat
        // reduction is the caller's: it gives 25 − 5.
        var outcome = CombatRules.Damage(State(50, 50, "2014", pc: false), Hit(25 - 5, "bludgeoning"),
            DamageAdjustments.FromTypes(["bludgeoning"], null, null));

        Assert.Equal(10, outcome.Total);
        Assert.Equal(40, outcome.After.Hp);
    }

    [Fact]
    public void Damage_2024ExampleReducedResistedThenDoubled_TakesTwentyTwo()
    {
        // SRD 5.2: "28 Fire damage … first reduced by 5 (to 23), then halved for the creature's Resistance (and rounded
        // down to 11), then doubled for its Vulnerability (to 22)."
        var outcome = CombatRules.Damage(State(50, 50, pc: false), Hit(28 - 5, "fire"), DamageAdjustments.FromTypes(["fire"], null, ["fire"]));

        Assert.Equal(22, outcome.Total);
        var part = Assert.Single(outcome.Parts);
        Assert.Equal([DamageStepKinds.Resistant, DamageStepKinds.Vulnerable], part.Steps.Select(s => s.Kind));
        Assert.Equal([23, 11], part.Steps.Select(s => s.Before));
        Assert.Equal("23 fire ½ (resistant) ×2 (vulnerable) = 22; 50 → 28", outcome.Arithmetic);
    }

    [Theory]
    [InlineData("2014")]
    [InlineData("2024")]
    public void Damage_Maximum12At6Taking18_DiesOfMassiveDamage(string edition)
    {
        // SRD 5.1 "Instant Death": max 12, at 6, takes 18 → 12 remain past 0, at least the maximum.
        var outcome = CombatRules.Damage(State(6, 12, edition), Hit(18));

        Assert.True(outcome.Died);
        Assert.Equal(DeathCauses.MassiveDamage, outcome.DeathCause);
        Assert.Equal(12, outcome.Leftover);
        Assert.True(outcome.After.Dead);
        Assert.Equal(new DeathSaveTally(0, 3, false), outcome.After.DeathSaves);
    }

    [Fact]
    public void Damage_LeftoverOneShortOfTheMaximum_FallsUnconsciousAndDying()
    {
        var outcome = CombatRules.Damage(State(6, 12, "2014", saves: new DeathSaveTally(2, 1, false)), Hit(17));

        Assert.False(outcome.Died);
        Assert.True(outcome.Dropped);
        Assert.True(outcome.FellUnconscious);
        Assert.Equal(11, outcome.Leftover);
        Assert.True(outcome.After.Dying);
        Assert.Equal(DeathSaveTally.Zero, outcome.After.DeathSaves);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // The pipeline.
    // ------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(9, false, 4)]
    [InlineData(9, true, 2)]
    [InlineData(1, false, 0)]
    [InlineData(0, false, 0)]
    public void Damage_Resistance_HalvesOnceRoundingDown(int amount, bool half, int expected)
    {
        var adjustments = DamageAdjustments.FromTypes(["fire"], null, null).Plus(DamageAdjustments.FromTypes(["fire", "all"], null, null));
        var outcome = CombatRules.Damage(State(50, 50, pc: false), Hit(amount, "fire") with { Half = half }, adjustments);

        Assert.Equal(expected, outcome.Total);
    }

    [Fact]
    public void Damage_PartsOfOneType_AreSummedBeforeTheyAreHalved()
    {
        // 3 + 3 fire resisted is ⌊6/2⌋ = 3, as the simulator's per-type arrays and "damage of that type is halved" read;
        // halving each part would give 2.
        var outcome = CombatRules.Damage(State(50, 50, pc: false), Hit((3, "fire"), (3, "fire"), (4, "cold")), DamageAdjustments.FromTypes(["fire"], null, null));

        Assert.Equal(7, outcome.Total);
        Assert.Equal(["fire", "cold"], outcome.Parts.Select(p => p.DamageType));
        Assert.Equal(6, outcome.Parts[0].Given);
        Assert.Equal(3, outcome.Parts[0].Amount);
    }

    [Fact]
    public void Damage_NegativePart_CountsAsZeroAndCancelsNothing()
    {
        var outcome = CombatRules.Damage(State(50, 50), Hit((-5, "fire"), (6, "fire")));

        Assert.Equal(6, outcome.Total);
        Assert.Equal(44, outcome.After.Hp);
    }

    [Theory]
    [InlineData(28, false, false, 56)]
    [InlineData(28, true, false, 28)]
    [InlineData(28, false, true, 28)]
    [InlineData(28, true, true, 14)]
    public void Damage_HalfThenVulnerability_RawSkipsTheAdjustmentButNotTheSave(int amount, bool half, bool raw, int expected)
    {
        var outcome = CombatRules.Damage(State(100, 100, pc: false), Hit(amount, "fire") with { Half = half, Raw = raw },
            DamageAdjustments.FromTypes(null, null, ["fire"]));

        Assert.Equal(expected, outcome.Total);
    }

    [Theory]
    [InlineData(false, 9)]
    [InlineData(true, 4)]
    public void Damage_RawWhilePetrified_TakesTheAmountGivenAndOnlyTheSaveHalves(bool half, int expected)
    {
        // Contract §5.1: "raw skips every adjustment", Petrified's resistance included; the half on a save is the target's
        // save, not an adjustment.
        var adjustments = new DamageAdjustments { Petrified = true }.Plus(DamageAdjustments.FromTypes(["fire"], null, null));
        var outcome = CombatRules.Damage(State(50, 50, pc: false), Hit(9, "fire") with { Raw = true, Half = half }, adjustments);

        Assert.Equal(expected, outcome.Total);
        Assert.DoesNotContain(Assert.Single(outcome.Parts).Steps, s => s.Kind == DamageStepKinds.Resistant);
    }

    [Fact]
    public void Damage_Petrified_TheBreakdownNamesTheCondition()
    {
        var outcome = CombatRules.Damage(State(50, 50, pc: false), Hit(9, "fire"), new DamageAdjustments { Petrified = true });

        Assert.Equal(DamageAdjustments.PetrifiedSource, Assert.Single(Assert.Single(outcome.Parts).Steps).Source);
        Assert.Equal("9 fire ½ (resistant: petrified) = 4; 50 → 46", outcome.Arithmetic);
    }

    [Fact]
    public void Damage_Immunity_TakesNothingAndNothingElseHappens()
    {
        var target = State(30, 30, concentrating: true);
        var outcome = CombatRules.Damage(target, Hit(28, "poison"), DamageAdjustments.FromTypes(["poison"], ["poison"], ["poison"]));

        Assert.Equal(0, outcome.Total);
        Assert.Same(target, outcome.After);
        Assert.Null(outcome.ConcentrationDc);
        Assert.Equal([DamageStepKinds.Immune], Assert.Single(outcome.Parts).Steps.Select(s => s.Kind));
        Assert.Equal("28 poison ×0 (immune) = 0; 30 → 30", outcome.Arithmetic);
    }

    [Theory]
    [InlineData("slashing", 9, 4)]
    [InlineData(null, 9, 4)]
    [InlineData("psychic", 9, 4)]
    public void Damage_Petrified_ResistsEveryTypeAndUntypedOnce(string? type, int amount, int expected)
    {
        var adjustments = new DamageAdjustments { Petrified = true }.Plus(DamageAdjustments.FromTypes(["slashing"], null, null));
        var outcome = CombatRules.Damage(State(50, 50, pc: false), Hit(amount, type), adjustments);

        Assert.Equal(expected, outcome.Total);
    }

    [Fact]
    public void Damage_UntypedDamage_IsNotTouchedByATypedEntry()
    {
        var outcome = CombatRules.Damage(State(50, 50, pc: false), Hit(9, null), DamageAdjustments.FromTypes(["slashing"], ["fire"], ["cold"]));

        Assert.Equal(9, outcome.Total);
        Assert.Equal("9 untyped; 50 → 41", outcome.Arithmetic);
    }

    [Theory]
    [InlineData("psychic", 10)]
    [InlineData("fire", 5)]
    [InlineData("bludgeoning", 6)]
    [InlineData(null, 5)]
    public void Damage_ResistAllExceptPsychic_CoversEveryOtherTypeAndUntyped(string? type, int expected)
    {
        var rage = DamageAdjustments.FromTypes(["all"], null, null, except: ["psychic"], source: "Rage");
        var outcome = CombatRules.Damage(State(85, 85), Hit(type == "bludgeoning" ? 12 : 10, type), rage);

        Assert.Equal(expected, outcome.Total);
    }

    [Theory]
    [InlineData(false, false, false, 5)]
    [InlineData(true, false, false, 10)]
    [InlineData(false, true, false, 10)]
    [InlineData(false, false, true, 5)]
    public void Damage_NonmagicalNotSilveredResistance_AppliesOnlyToNonmagicalUnsilveredDamage(bool magical, bool silvered, bool adamantine, int expected)
    {
        var werewolf = new DamageAdjustments { Resistances = [new DamageAdjustmentEntry("slashing", K.DamageQualifiers.NonmagicalNotSilvered)] };
        var outcome = CombatRules.Damage(State(50, 50, pc: false), Hit(10) with { Magical = magical, Silvered = silvered, Adamantine = adamantine }, werewolf);

        Assert.Equal(expected, outcome.Total);
        if (expected == 5)
        {
            Assert.Contains("½ (resistant: nonmagical, not silvered)", outcome.Arithmetic, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Damage_AnUnqualifiedAndAQualifiedEntry_TheUnqualifiedOneDecides()
    {
        // The simulator's code table: the most general entry of a type decides, so magical damage is still resisted.
        var both = new DamageAdjustments
        {
            Resistances = [new DamageAdjustmentEntry("slashing", K.DamageQualifiers.Nonmagical), new DamageAdjustmentEntry("slashing")],
        };

        Assert.Equal(5, CombatRules.Damage(State(50, 50, pc: false), Hit(10) with { Magical = true }, both).Total);
    }

    [Theory]
    [InlineData(true, false, 10)]
    [InlineData(false, true, 5)]
    [InlineData(false, false, 5)]
    public void Damage_TwoQualifiedEntries_TheMoreGeneralOneDecidesAsInTheEngine(bool silvered, bool adamantine, int expected)
    {
        // "Not silvered" is more general than "not adamantine" in the simulator's code table, so silvered damage is not
        // resisted even though the adamantine entry alone would have applied to it.
        var both = new DamageAdjustments
        {
            Resistances =
            [
                new DamageAdjustmentEntry("slashing", K.DamageQualifiers.NonmagicalNotAdamantine),
                new DamageAdjustmentEntry("slashing", K.DamageQualifiers.NonmagicalNotSilvered),
            ],
        };

        Assert.Equal(expected, CombatRules.Damage(State(50, 50, pc: false), Hit(10) with { Silvered = silvered, Adamantine = adamantine }, both).Total);
    }

    [Fact]
    public void Damage_OtherQualifier_AppliesAndTheBreakdownQuotesIt()
    {
        var block = new DamageAdjustment("piercing", K.DamageQualifiers.Other, "piercing from magic weapons wielded by good creatures");
        var adjustments = DamageAdjustments.FromStatBlock(StatBlockWith(resistances: [block]));
        var outcome = CombatRules.Damage(State(50, 50, pc: false), Hit(10, "piercing") with { Magical = true }, adjustments);

        Assert.Equal(5, outcome.Total);
        Assert.Contains("½ (resistant: a qualifier to check, piercing from magic weapons wielded by good creatures)", outcome.Arithmetic, StringComparison.Ordinal);
    }

    [Fact]
    public void DamageAdjustments_SnapshotOrSheetPlusEffects_CombineAndTheBreakdownNamesEachSource()
    {
        var sheet = DamageAdjustments.FromSheetDefenses(["fire"], ["poison"], ["cold"]);
        var rage = DamageAdjustments.FromEffect("Rage", ["all"], null, null, except: ["psychic"]);
        var stoneskin = DamageAdjustments.FromEffect("Stoneskin", ["bludgeoning"], null, null);
        var all = DamageAdjustments.Combine(sheet, rage, null, stoneskin, new DamageAdjustments { Petrified = false });

        Assert.Equal(5, CombatRules.Damage(State(50, 50), Hit(10, "fire"), all).Total);
        Assert.Equal(0, CombatRules.Damage(State(50, 50), Hit(10, "poison"), all).Total);
        Assert.Equal(10, CombatRules.Damage(State(50, 50), Hit(10, "cold"), all).Total);
        Assert.Equal(10, CombatRules.Damage(State(50, 50), Hit(10, "psychic"), all).Total);
        Assert.Equal("10 fire ½ (resistant: sheet) = 5; 50 → 45", CombatRules.Damage(State(50, 50), Hit(10, "fire"), all).Arithmetic);
        Assert.Equal("10 slashing ½ (resistant: Rage) = 5; 50 → 45", CombatRules.Damage(State(50, 50), Hit(10), all).Arithmetic);
        Assert.Equal(["fire", "all", "bludgeoning"], all.Resistances.Select(r => r.DamageType));
        Assert.False(all.Petrified);
        Assert.True(DamageAdjustments.Combine(sheet, new DamageAdjustments { Petrified = true }).Petrified);
    }

    [Fact]
    public void DamageAdjustments_FromAStatBlock_KeepsTheQualifiers()
    {
        var block = SrdBlock("2014", "mummy");
        var adjustments = DamageAdjustments.FromStatBlock(block);

        Assert.Equal(block.Resistances.Select(r => (r.DamageType, r.Qualifier)), adjustments.Resistances.Select(r => (r.DamageType, r.Qualifier)));
        Assert.Contains(adjustments.Resistances, r => r.Qualifier == K.DamageQualifiers.Nonmagical);
        Assert.Equal(["fire"], adjustments.Vulnerabilities.Select(v => v.DamageType));
        Assert.Throws<ArgumentException>(() => DamageAdjustments.FromEffect(" ", ["fire"], null, null));
    }

    [Fact]
    public void Damage_TempHp_AbsorbFirstAndTheRestComesOffHitPoints()
    {
        var outcome = CombatRules.Damage(State(20, 20, temp: 8), Hit(13));

        Assert.Equal(8, outcome.TempAbsorbed);
        Assert.Equal(0, outcome.After.TempHp);
        Assert.Equal(15, outcome.After.Hp);
        Assert.Equal(5, outcome.HpLost);
        Assert.Equal("13 slashing; temporary HP 8 → 0; 20 → 15", outcome.Arithmetic);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // The fixtures' arithmetic.
    // ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Damage_FixtureA8_RottingFistOnBelmakor_TempAbsorbsSevenAndTheDcReadsThePreTempTotal()
    {
        var belmakor = State(110, 110, "2014", temp: 7, concentrating: true);
        var outcome = CombatRules.Damage(belmakor, Hit((14, "bludgeoning"), (21, "necrotic")));

        Assert.Equal(35, outcome.Total);
        Assert.Equal(7, outcome.TempAbsorbed);
        Assert.Equal(82, outcome.After.Hp);
        Assert.Equal(0, outcome.After.TempHp);
        Assert.Equal(17, outcome.ConcentrationDc);
        Assert.True(outcome.After.Concentrating);
        Assert.Equal([RulingFlags.ConcentrationOnPreTempDamage], outcome.Rulings);
        Assert.Equal("14 bludgeoning + 21 necrotic = 35; temporary HP 7 → 0; 110 → 82", outcome.Arithmetic);
    }

    [Theory]
    [InlineData("mummy-lord", 41)]
    [InlineData("mummy", 2)]
    public void Damage_FixtureA14_FireballOnTheMummies_IsDoubledByTheirVulnerability(string slug, int left)
    {
        var block = SrdBlock("2014", slug);
        var outcome = CombatRules.Damage(Monster(block), Hit(28, "fire"), DamageAdjustments.FromStatBlock(block));

        Assert.Equal(56, outcome.Total);
        Assert.Equal(left, outcome.After.Hp);
        Assert.Equal($"28 fire ×2 (vulnerable) = 56; {block.HitPoints} → {left}", outcome.Arithmetic);
    }

    [Theory]
    [InlineData(false, 0, "×0 (immune: nonmagical)")]
    [InlineData(true, 10, null)]
    public void Damage_MummyLordImmunityToNonmagicalBludgeoning_DoesNotStopMagicalDamage(bool magical, int expected, string? step)
    {
        var block = SrdBlock("2014", "mummy-lord");
        var outcome = CombatRules.Damage(Monster(block), Hit(10, "bludgeoning") with { Magical = magical }, DamageAdjustments.FromStatBlock(block));

        Assert.Equal(expected, outcome.Total);
        if (step is not null)
        {
            Assert.Contains(step, outcome.Arithmetic, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(true, 8)]
    [InlineData(false, 4)]
    public void Damage_FixtureA21_ScimitarOnMummy2_KillsAMonsterAtZero(bool magical, int total)
    {
        var block = SrdBlock("2014", "mummy");
        var outcome = CombatRules.Damage(Monster(block, 2), Hit(8) with { Magical = magical }, DamageAdjustments.FromStatBlock(block),
            StatBlockFacts.DeathInterceptors(block));

        Assert.Equal(total, outcome.Total);
        Assert.True(outcome.Died);
        Assert.True(outcome.Dropped);
        Assert.Equal(DeathCauses.ZeroHp, outcome.DeathCause);
        Assert.False(outcome.FellUnconscious);
    }

    [Fact]
    public void Damage_FixtureA18_TorchDropsWithOneLeftOver_IsUnconsciousAndDyingNotDead()
    {
        var outcome = CombatRules.Damage(State(19, 74, "2014"), Hit((10, "bludgeoning"), (10, "necrotic")));

        Assert.Equal(0, outcome.After.Hp);
        Assert.Equal(1, outcome.Leftover);
        Assert.False(outcome.Died);
        Assert.True(outcome.FellUnconscious);
        Assert.True(outcome.After.Dying);
        Assert.Equal(DeathSaveTally.Zero, outcome.After.DeathSaves);
    }

    [Fact]
    public void Damage_FixtureA23_CriticalHitOnDyingTorch_IsTwoFailures()
    {
        var outcome = CombatRules.Damage(Dying(74, "2014"), Hit((25, "bludgeoning"), (42, "necrotic")) with { Critical = true });

        Assert.Equal(67, outcome.Total);
        Assert.False(outcome.Died);
        Assert.Equal(2, outcome.DeathSaveFailures);
        Assert.Equal(new DeathSaveTally(0, 2, false), outcome.After.DeathSaves);
    }

    [Fact]
    public void Damage_FixtureB8_MonkFrom35To25_BecomesBloodied()
    {
        var outcome = CombatRules.Damage(State(35, 59), Hit(10, "psychic"));

        Assert.Equal(25, outcome.After.Hp);
        Assert.True(outcome.BecameBloodied);
        Assert.True(outcome.After.Bloodied);
    }

    [Fact]
    public void Damage_FixtureB13AndB18_RageHalvesTheLashButNotPsychic()
    {
        var rage = DamageAdjustments.FromTypes(["all"], null, null, except: ["psychic"], source: "Rage");
        var lash = CombatRules.Damage(State(73, 85, exhaustion: 1), Hit(12, "bludgeoning"), rage);
        var drain = CombatRules.Damage(lash.After, Hit(10, "psychic"), rage);

        Assert.Equal(67, lash.After.Hp);
        Assert.Equal("12 bludgeoning ½ (resistant: Rage) = 6; 73 → 67", lash.Arithmetic);
        Assert.Equal(57, drain.After.Hp);
    }

    [Fact]
    public void Damage_FixtureB15_AbolethFrom82To62_BecomesBloodiedAtTheFirstCrossing()
    {
        var block = SrdBlock("2024", "aboleth");
        var outcome = CombatRules.Damage(Monster(block, 82), Hit(20, "bludgeoning"), DamageAdjustments.FromStatBlock(block));

        Assert.Equal(62, outcome.After.Hp);
        Assert.True(outcome.BecameBloodied);
    }

    [Fact]
    public void Damage_FixtureB18_SecondTentacleDropsTheMonk_DyingWithNineLeftOver()
    {
        var outcome = CombatRules.Damage(State(3, 59), Hit(12, "bludgeoning"));

        Assert.Equal(9, outcome.Leftover);
        Assert.True(outcome.After.Dying);
        Assert.False(outcome.BecameBloodied);
    }

    [Fact]
    public void Damage_FixtureB20_CriticalLashOnTheUnconsciousMonk_IsTwoFailures()
    {
        var outcome = CombatRules.Damage(Dying(59), Hit(19, "bludgeoning") with { Critical = true });

        Assert.Equal(new DeathSaveTally(0, 2, false), outcome.After.DeathSaves);
        Assert.False(outcome.Died);
    }

    [Fact]
    public void Damage_FixtureB26_AbolethDropsToZero_DiesAndItsRestorationIsListed()
    {
        var block = SrdBlock("2024", "aboleth");
        var outcome = CombatRules.Damage(Monster(block, 13), Hit(18, "bludgeoning"), DamageAdjustments.FromStatBlock(block), StatBlockFacts.DeathInterceptors(block));

        Assert.True(outcome.Died);
        Assert.Equal(DeathCauses.ZeroHp, outcome.DeathCause);
        Assert.Equal("Eldritch Restoration", Assert.Single(StatBlockFacts.RevivalTraits(block)).Name);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Knocking out.
    // ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Damage_KnockOut2014_LeavesItAtZeroUnconsciousAndStable()
    {
        var outcome = CombatRules.Damage(State(5, 30, "2014", concentrating: true), Hit(10) with { KnockOut = true });

        Assert.True(outcome.KnockedOut);
        Assert.True(outcome.FellUnconscious);
        Assert.True(outcome.Dropped);
        Assert.Equal(0, outcome.After.Hp);
        Assert.Equal(DeathSaveTally.Stabilized, outcome.After.DeathSaves);
        Assert.False(outcome.After.Dying);
        Assert.False(outcome.After.KnockedOut);
        Assert.True(outcome.ConcentrationBroken);
        Assert.False(outcome.After.Concentrating);
    }

    [Fact]
    public void Damage_KnockOut2024_LeavesItAtOneHitPointUnconsciousNotDying()
    {
        var outcome = CombatRules.Damage(State(5, 30), Hit(10) with { KnockOut = true });

        Assert.True(outcome.KnockedOut);
        Assert.True(outcome.FellUnconscious);
        Assert.False(outcome.Dropped);
        Assert.Equal(1, outcome.After.Hp);
        Assert.True(outcome.After.KnockedOut);
        Assert.False(outcome.After.Dying);
        Assert.Equal(5, outcome.Leftover);
    }

    [Theory]
    [InlineData("2014", 0)]
    [InlineData("2024", 1)]
    public void Damage_KnockOut_AppliesInsteadOfMassiveDamage(string edition, int hp)
    {
        var outcome = CombatRules.Damage(State(6, 12, edition), Hit(40) with { KnockOut = true });

        Assert.False(outcome.Died);
        Assert.Equal(hp, outcome.After.Hp);
    }

    [Theory]
    [InlineData("2014", 0)]
    [InlineData("2024", 1)]
    public void Damage_KnockOutAMonster_AppliesInsteadOfItsDeath(string edition, int hp)
    {
        var outcome = CombatRules.Damage(State(6, 12, edition, pc: false), Hit(40) with { KnockOut = true });

        Assert.False(outcome.Died);
        Assert.Equal(hp, outcome.After.Hp);
    }

    [Fact]
    public void Damage_KnockOutOfACreatureAlreadyKnockedOut_DoesNotFallUnconsciousAgain()
    {
        var outcome = CombatRules.Damage(State(1, 30, knockedOut: true), Hit(5) with { KnockOut = true });

        Assert.True(outcome.KnockedOut);
        Assert.False(outcome.FellUnconscious);
        Assert.Equal(1, outcome.After.Hp);
        Assert.True(outcome.After.KnockedOut);
    }

    [Fact]
    public void Damage_DroppingACreatureAlreadyKnockedOut_EndsTheKnockOutAndItIsDying()
    {
        // Its knock-out Unconscious stays and now lasts until it regains hit points; it does not fall a second time.
        var outcome = CombatRules.Damage(State(1, 30, knockedOut: true), Hit(5));

        Assert.True(outcome.Dropped);
        Assert.False(outcome.FellUnconscious);
        Assert.False(outcome.KnockedOut);
        Assert.False(outcome.After.KnockedOut);
        Assert.True(outcome.After.Dying);
    }

    [Fact]
    public void Damage_KnockOutThatDoesNotDrop_IsJustDamage()
    {
        var outcome = CombatRules.Damage(State(30, 30), Hit(10) with { KnockOut = true });

        Assert.False(outcome.KnockedOut);
        Assert.False(outcome.FellUnconscious);
        Assert.Equal(20, outcome.After.Hp);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Damage at 0 HP.
    // ------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, false, 1, false)]
    [InlineData(0, true, 2, false)]
    [InlineData(1, true, 3, true)]
    [InlineData(2, false, 3, true)]
    public void Damage_AtZero_AddsFailuresAndThreeKill(int failures, bool critical, int after, bool dead)
    {
        var outcome = CombatRules.Damage(Dying(40, failures: failures), Hit(3) with { Critical = critical });

        Assert.Equal(dead, outcome.Died);
        Assert.Equal(dead ? new DeathSaveTally(0, 3, false) : new DeathSaveTally(0, after, false), outcome.After.DeathSaves);
        Assert.False(outcome.Dropped);
        Assert.False(outcome.FellUnconscious);
        Assert.Equal(0, outcome.Leftover);
        if (dead)
        {
            Assert.Equal(DeathCauses.DeathSaveFailures, outcome.DeathCause);
        }
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public void Damage_AtZero_KeepsTheSuccesses(bool critical, int failures)
    {
        // SRD 5.1 "keep track of both until you collect three of a kind": damage at 0 adds failures and leaves the
        // successes; only regaining hit points or becoming stable resets them.
        var outcome = CombatRules.Damage(Dying(40, successes: 2), Hit(3) with { Critical = critical });

        Assert.Equal(new DeathSaveTally(2, failures, false), outcome.After.DeathSaves);
        Assert.True(outcome.After.Dying);
    }

    [Theory]
    [InlineData(39, false)]
    [InlineData(40, true)]
    public void Damage_AtZeroOfAtLeastTheEffectiveMaximum_Kills(int amount, bool dead)
    {
        var outcome = CombatRules.Damage(Dying(40), Hit(amount));

        Assert.Equal(dead, outcome.Died);
        Assert.Equal(dead ? DeathCauses.DamageAtZero : null, outcome.DeathCause);
    }

    [Theory]
    [InlineData("2024", 0, 10, 30, true)]
    [InlineData("2024", 0, 10, 29, false)]
    [InlineData("2014", 4, 0, 20, true)]
    [InlineData("2014", 4, 0, 19, false)]
    [InlineData("2024", 4, 0, 39, false)]
    public void Damage_AtZeroOfAtLeastTheReducedOrHalvedMaximum_Kills(string edition, int exhaustion, int reduction, int amount, bool dead)
    {
        // Contract §5.5 "damage ≥ effective max → dead" with §5.17: a reduction of 10 leaves 30 of 40; 2014 Exhaustion 4
        // halves 40 to 20; 2024 Exhaustion leaves the maximum alone.
        var outcome = CombatRules.Damage(State(0, 40, edition, exhaustion: exhaustion, reduction: reduction), Hit(amount));

        Assert.Equal(dead, outcome.Died);
        Assert.Equal(dead ? DeathCauses.DamageAtZero : null, outcome.DeathCause);
        Assert.Equal(dead ? 0 : 1, outcome.DeathSaveFailures);
    }

    [Fact]
    public void Damage_AtZeroAbsorbedByTempHp_StillCostsAFailureOnThePreTempRuling()
    {
        var outcome = CombatRules.Damage(Dying(40, temp: 5), Hit(3));

        Assert.Equal(3, outcome.TempAbsorbed);
        Assert.Equal(2, outcome.After.TempHp);
        Assert.Equal(1, outcome.DeathSaveFailures);
        Assert.Equal([RulingFlags.ConcentrationOnPreTempDamage], outcome.Rulings);
        Assert.Equal(0, outcome.After.Hp);
    }

    [Fact]
    public void Damage_ToAStableCreature_StartsItDyingAgain()
    {
        var outcome = CombatRules.Damage(State(0, 40, saves: DeathSaveTally.Stabilized), Hit(2));

        Assert.True(outcome.StableLost);
        Assert.Equal(new DeathSaveTally(0, 1, false), outcome.After.DeathSaves);
        Assert.True(outcome.After.Dying);
        Assert.False(outcome.Dropped);
        Assert.False(outcome.FellUnconscious);
    }

    [Theory]
    [InlineData(2, 5, 3, DeathCauses.DeathSaveFailures)]
    [InlineData(0, 50, 45, DeathCauses.DamageAtZero)]
    public void Damage_DeathWithTemporaryHitPointsLeft_ClearsThem(int failures, int temp, int amount, string cause)
    {
        // Death clears what is left of the temporary hit points, as the simulator's Die does: a dead record carries none.
        var outcome = CombatRules.Damage(Dying(40, failures: failures, temp: temp), Hit(amount));

        Assert.True(outcome.Died);
        Assert.Equal(cause, outcome.DeathCause);
        Assert.Equal(amount, outcome.TempAbsorbed);
        Assert.Equal(0, outcome.After.TempHp);
    }

    [Fact]
    public void Damage_ToAKnockedOut2014MonsterAtZero_KillsIt()
    {
        var outcome = CombatRules.Damage(State(0, 30, "2014", pc: false, saves: DeathSaveTally.Stabilized), Hit(1));

        Assert.True(outcome.Died);
        Assert.Equal(DeathCauses.DamageAtZero, outcome.DeathCause);
    }

    [Fact]
    public void Damage_AlreadyDead_HasNoEffect()
    {
        var dead = State(0, 30, dead: true);
        var outcome = CombatRules.Damage(dead, Hit(10));

        Assert.True(outcome.NoEffect);
        Assert.Same(dead, outcome.After);
        Assert.Equal("10 slashing; no effect: dead", outcome.Arithmetic);
    }

    [Theory]
    [InlineData("2014", 4, 15, true)]
    [InlineData("2024", 4, 15, false)]
    [InlineData("2014", 3, 15, false)]
    public void Damage_MassiveDamage_ComparesWithTheEffectiveMaximum(string edition, int exhaustion, int amount, bool dead)
    {
        // Max 20 at 5 HP: 15 leaves 10 past 0, at least the 2014 Exhaustion-4 maximum of 10, short of 20.
        var outcome = CombatRules.Damage(State(5, 20, edition, exhaustion: exhaustion), Hit(amount));

        Assert.Equal(dead, outcome.Died);
    }

    [Fact]
    public void Damage_MassiveDamage_ComparesWithTheReducedMaximum()
    {
        var outcome = CombatRules.Damage(State(5, 20, reduction: 8), Hit(17));

        Assert.True(outcome.Died);
        Assert.Equal(DeathCauses.MassiveDamage, outcome.DeathCause);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Death interceptors.
    // ------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("slashing", false, true)]
    [InlineData("radiant", false, false)]
    [InlineData("slashing", true, false)]
    public void Damage_ZombieDropped_IsInterceptedUnlessRadiantOrCritical(string type, bool critical, bool intercepted)
    {
        var block = SrdBlock("2014", "zombie");
        var outcome = CombatRules.Damage(Monster(block, 5), Hit(10, type) with { Critical = critical }, DamageAdjustments.FromStatBlock(block),
            StatBlockFacts.DeathInterceptors(block));

        Assert.Equal(intercepted, outcome.Intercepted);
        Assert.Equal(!intercepted, outcome.Died);
        Assert.Equal(0, outcome.After.Hp);
        if (intercepted)
        {
            var fortitude = Assert.Single(outcome.Interceptors);
            Assert.Equal(K.TraitKinds.UndeadFortitude, fortitude.Kind);
            Assert.Equal(15, fortitude.SaveDc(outcome.Total));
            Assert.False(outcome.After.Dead);
        }
    }

    [Theory]
    [InlineData(7, true)]
    [InlineData(8, false)]
    public void Damage_BoarRelentless_InterceptsOnlyWithinItsThreshold(int amount, bool intercepted)
    {
        var block = SrdBlock("2014", "boar");
        var outcome = CombatRules.Damage(Monster(block, 2), Hit(amount, "piercing"), DamageAdjustments.FromStatBlock(block), StatBlockFacts.DeathInterceptors(block));

        Assert.Equal(intercepted, outcome.Intercepted);
        Assert.Equal(!intercepted, outcome.Died);
    }

    [Fact]
    public void Damage_TrollAtZero_IsInterceptedAndFurtherDamageWaitsForItsTurn()
    {
        var block = SrdBlock("2024", "troll");
        var interceptors = StatBlockFacts.DeathInterceptors(block);
        var dropped = CombatRules.Damage(Monster(block, 5), Hit(30, "fire"), DamageAdjustments.FromStatBlock(block), interceptors);
        var again = CombatRules.Damage(dropped.After, Hit(30, "fire"), DamageAdjustments.FromStatBlock(block), interceptors);

        Assert.True(dropped.Intercepted);
        Assert.False(again.Died);
        Assert.Equal(0, again.After.Hp);
    }

    [Theory]
    [InlineData("slashing", false, true)]
    [InlineData("radiant", false, false)]
    [InlineData("slashing", true, false)]
    public void Damage_AZombieThatMakesDeathSaves_KeepsItsUndeadFortitude(string type, bool critical, bool intercepted)
    {
        // The simulator reads Undead Fortitude for every creature that drops, death_saves: true included, before it falls
        // unconscious: held at 0 for the table, and dying only if the save fails.
        var block = SrdBlock("2014", "zombie");
        var outcome = CombatRules.Damage(State(5, 22, "2014", saves: new DeathSaveTally(1, 1, false)), Hit(10, type) with { Critical = critical },
            DamageAdjustments.FromStatBlock(block), StatBlockFacts.DeathInterceptors(block));

        Assert.Equal(intercepted, outcome.Intercepted);
        Assert.False(outcome.Died);
        Assert.True(outcome.Dropped);
        Assert.Equal(0, outcome.After.Hp);
        Assert.Equal(DeathSaveTally.Zero, outcome.After.DeathSaves);
        Assert.Equal(!intercepted, outcome.FellUnconscious);
        if (intercepted)
        {
            Assert.Equal(K.TraitKinds.UndeadFortitude, Assert.Single(outcome.Interceptors).Kind);
            var fails = Assert.IsType<DamageOutcome>(outcome.IfTraitFails);
            Assert.True(fails.FellUnconscious);
            Assert.True(fails.After.Dying);
            Assert.False(fails.Intercepted);
        }
        else
        {
            Assert.Null(outcome.IfTraitFails);
        }
    }

    [Fact]
    public void Damage_ATraitIsReadBeforeMassiveDamage_AndMassiveDamageIsWhatAFailedTraitLeaves()
    {
        // 30 at 5 HP leaves 25 past 0, at least the zombie's 22: the engine still rolls Undead Fortitude first.
        var block = SrdBlock("2014", "zombie");
        var outcome = CombatRules.Damage(State(5, 22, "2014"), Hit(30), DamageAdjustments.FromStatBlock(block), StatBlockFacts.DeathInterceptors(block));

        Assert.True(outcome.Intercepted);
        Assert.False(outcome.Died);
        Assert.Equal(25, outcome.Leftover);
        Assert.Equal(35, outcome.Interceptors[0].SaveDc(outcome.Total));
        var fails = Assert.IsType<DamageOutcome>(outcome.IfTraitFails);
        Assert.True(fails.Died);
        Assert.Equal(DeathCauses.MassiveDamage, fails.DeathCause);
    }

    [Theory]
    [InlineData(7, true)]
    [InlineData(8, false)]
    public void Damage_ABoarThatMakesDeathSaves_KeepsItsRelentless(int amount, bool intercepted)
    {
        var block = SrdBlock("2014", "boar");
        var outcome = CombatRules.Damage(State(2, block.HitPoints, "2014"), Hit(amount, "piercing"), DamageAdjustments.FromStatBlock(block),
            StatBlockFacts.DeathInterceptors(block));

        Assert.Equal(intercepted, outcome.Intercepted);
        Assert.False(outcome.Died);
        Assert.Equal(!intercepted, outcome.After.Dying && outcome.FellUnconscious);
    }

    [Fact]
    public void Damage_ATrollThatMakesDeathSaves_FallsDyingAsTheEngineDoes()
    {
        // The engine's DropToZero sends a death-save maker to dying before it reads Regeneration at 0.
        var block = SrdBlock("2024", "troll");
        var outcome = CombatRules.Damage(State(5, block.HitPoints), Hit(30, "fire"), DamageAdjustments.FromStatBlock(block),
            StatBlockFacts.DeathInterceptors(block));

        Assert.False(outcome.Intercepted);
        Assert.True(outcome.FellUnconscious);
        Assert.True(outcome.After.Dying);
    }

    [Theory]
    [InlineData(7, false)]
    [InlineData(9, true)]
    public void Damage_BoarRelentlessWithTemporaryHitPoints_ReadsThePreTempTotal(int amount, bool died)
    {
        // 9 with 3 absorbed leaves 6 for its 2 HP, but Relentless reads the 9 (Fight.ApplyDamage: total ≤ 7).
        var block = SrdBlock("2014", "boar");
        var outcome = CombatRules.Damage(State(2, block.HitPoints, "2014", temp: 3, pc: false), Hit(amount, "piercing"),
            DamageAdjustments.FromStatBlock(block), StatBlockFacts.DeathInterceptors(block));

        Assert.Equal(died, outcome.Died);
        Assert.Equal(!died, outcome.Intercepted);
        Assert.Equal(3, outcome.TempAbsorbed);
    }

    [Fact]
    public void Damage_ARelentlessAlreadyUsed_NoLongerIntercepts()
    {
        var block = SrdBlock("2014", "boar");
        var outcome = CombatRules.Damage(Monster(block, 2), Hit(7, "piercing"), DamageAdjustments.FromStatBlock(block),
            StatBlockFacts.DeathInterceptors(block, relentlessUsed: true));

        Assert.False(outcome.Intercepted);
        Assert.True(outcome.Died);
    }

    [Fact]
    public void Damage_ARadiantPartHalvedToNothing_DoesNotStopUndeadFortitude()
    {
        // The engine counts a hit as radiant only when some radiant damage got through: ⌊1/2⌋ = 0.
        var block = SrdBlock("2014", "zombie");
        var outcome = CombatRules.Damage(Monster(block, 5), Hit((10, "slashing"), (1, "radiant")) with { Half = true },
            DamageAdjustments.FromStatBlock(block), StatBlockFacts.DeathInterceptors(block));

        Assert.Equal(5, outcome.Total);
        Assert.False(outcome.Radiant);
        Assert.True(outcome.Intercepted);
    }

    [Fact]
    public void Damage_AMonsterHeldByATrait_IfTheTraitFails_Dies()
    {
        var block = SrdBlock("2014", "zombie");
        var outcome = CombatRules.Damage(Monster(block, 5), Hit(10), DamageAdjustments.FromStatBlock(block), StatBlockFacts.DeathInterceptors(block));

        Assert.True(outcome.Intercepted);
        Assert.False(outcome.FellUnconscious);
        var fails = Assert.IsType<DamageOutcome>(outcome.IfTraitFails);
        Assert.True(fails.Died);
        Assert.Equal(DeathCauses.ZeroHp, fails.DeathCause);
        Assert.Equal(outcome.Arithmetic, fails.Arithmetic);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Concentration and Bloodied.
    // ------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("2014", 70, 35)]
    [InlineData("2024", 70, 30)]
    [InlineData("2024", 12, 10)]
    public void Damage_Concentrating_OwesASaveAgainstTheDc(string edition, int amount, int dc)
    {
        var outcome = CombatRules.Damage(State(200, 200, edition, concentrating: true), Hit(amount));

        Assert.Equal(dc, outcome.ConcentrationDc);
        Assert.False(outcome.ConcentrationBroken);
        Assert.Empty(outcome.Rulings);
    }

    [Theory]
    [InlineData(10, true, false)]
    [InlineData(100, true, true)]
    public void Damage_ConcentratorDroppedOrKilled_LosesItWithoutASave(int amount, bool broken, bool died)
    {
        var outcome = CombatRules.Damage(State(10, 40, concentrating: true), Hit(amount));

        Assert.Equal(broken, outcome.ConcentrationBroken);
        Assert.Equal(died, outcome.Died);
        Assert.Null(outcome.ConcentrationDc);
        Assert.False(outcome.After.Concentrating);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Damage_ConcentratorHeldByUndeadFortitudeOrRelentless_KeepsItAndOwesTheSave(bool makesDeathSaves)
    {
        // The engine leaves it at 1 HP and then rolls the concentration save on the total (Fight.ApplyDamage); the table
        // resolves the trait, so the save is owed and the concentration ends only if the trait fails.
        var block = SrdBlock("2014", "boar");
        var outcome = CombatRules.Damage(State(2, block.HitPoints, "2014", pc: makesDeathSaves, concentrating: true), Hit(7, "piercing"),
            DamageAdjustments.FromStatBlock(block), StatBlockFacts.DeathInterceptors(block));

        Assert.True(outcome.Intercepted);
        Assert.False(outcome.ConcentrationBroken);
        Assert.True(outcome.After.Concentrating);
        Assert.Equal(10, outcome.ConcentrationDc);
        var fails = Assert.IsType<DamageOutcome>(outcome.IfTraitFails);
        Assert.True(fails.ConcentrationBroken);
        Assert.False(fails.After.Concentrating);
    }

    [Fact]
    public void Damage_ConcentratorHeldOnlyByRegeneration_LosesIt()
    {
        // A troll at 0 is down until the start of its turn: the engine's OnIncapacitated ends its concentration.
        var block = SrdBlock("2024", "troll");
        var outcome = CombatRules.Damage(State(5, block.HitPoints, pc: false, concentrating: true), Hit(30, "fire"),
            DamageAdjustments.FromStatBlock(block), StatBlockFacts.DeathInterceptors(block));

        Assert.True(outcome.Intercepted);
        Assert.True(outcome.ConcentrationBroken);
        Assert.Null(outcome.ConcentrationDc);
    }

    [Fact]
    public void Damage_NotConcentrating_OwesNoSave()
    {
        Assert.Null(CombatRules.Damage(State(30, 30), Hit(10)).ConcentrationDc);
    }

    [Fact]
    public void Damage_Bloodied_IsReportedAtEachCrossingFromAboveHalf()
    {
        var first = CombatRules.Damage(State(31, 60), Hit(1));
        var lower = CombatRules.Damage(first.After, Hit(5));
        var healed = CombatRules.Heal(lower.After, 20);
        var again = CombatRules.Damage(healed.After, Hit(20));

        Assert.True(first.BecameBloodied);
        Assert.Equal(30, first.After.Hp);
        Assert.False(lower.BecameBloodied);
        Assert.False(healed.After.Bloodied);
        Assert.True(again.BecameBloodied);
    }

    [Theory]
    [InlineData("2014", 31, 1, true, false)]
    [InlineData("2024", 40, 40, true, true)]
    [InlineData("2024", 31, 31, true, true)]
    [InlineData("2024", 31, 31, false, false)]
    [InlineData("2024", 30, 30, true, false)]
    [InlineData("2024", 12, 200, true, false)]
    public void Damage_BloodiedCrossing_IncludesADropToZeroButNotADeathOr2014(string edition, int hp, int amount, bool pc, bool bloodied)
    {
        // Contract §5.10: a reminder at EACH crossing from above half; a creature dying at 0 is Bloodied (its state says
        // so), a dead one is not, and one already at half or less does not cross.
        var outcome = CombatRules.Damage(State(hp, 60, edition, pc: pc), Hit(amount));

        Assert.Equal(bloodied, outcome.BecameBloodied);
        Assert.Equal(edition == "2024" && !outcome.Died, outcome.After.Bloodied);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Damage_BloodiedCrossingOnAKnockOutOrAnInterception_IsReported(bool knockOut)
    {
        var block = SrdBlock("2024", "zombie");
        var outcome = knockOut
            ? CombatRules.Damage(State(20, 30), Hit(25) with { KnockOut = true })
            : CombatRules.Damage(Monster(block, 12), Hit(12), DamageAdjustments.FromStatBlock(block), StatBlockFacts.DeathInterceptors(block));

        Assert.Equal(!knockOut, outcome.Intercepted);
        Assert.True(outcome.BecameBloodied);
    }

    [Fact]
    public void Damage_Bloodied_UsesTheEffectiveMaximum()
    {
        // Max 60 reduced by 20 is 40: 25 → 20 crosses ⌊40/2⌋ = 20; against the recorded 60 it was already at half.
        var outcome = CombatRules.Damage(State(25, 60, reduction: 20), Hit(5));

        Assert.True(outcome.BecameBloodied);
        Assert.False(CombatRules.Damage(State(25, 60), Hit(5)).BecameBloodied);
    }

    [Fact]
    public void HitPointState_TheDead_AreNotBloodied()
    {
        Assert.True(State(10, 60).Bloodied);
        Assert.False(State(10, 60, dead: true).Bloodied);
        Assert.False(State(0, 60, pc: false, dead: true).Bloodied);
    }

    [Theory]
    [InlineData(30, 60, "2024", true)]
    [InlineData(31, 60, "2024", false)]
    [InlineData(29, 59, "2024", true)]
    [InlineData(30, 59, "2024", false)]
    [InlineData(0, 60, "2024", true)]
    [InlineData(30, 60, "2014", false)]
    [InlineData(0, 0, "2024", false)]
    public void IsBloodied_HalfTheEffectiveMaximumRoundedDown(int hp, int max, string edition, bool expected)
    {
        Assert.Equal(expected, CombatRules.IsBloodied(hp, max, edition));
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Host bugs.
    // ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Damage_UnknownDamageType_IsAHostBug()
    {
        Assert.Throws<ArgumentException>(() => CombatRules.Damage(State(10, 10), Hit(3, "sonic")));
        Assert.Throws<ArgumentException>(() => DamageAdjustments.FromTypes(["sonic"], null, null));
        Assert.Throws<ArgumentException>(() => DamageAdjustments.FromTypes(null, null, null, except: ["all"]));
    }

    [Fact]
    public void Damage_ForgivingTypeSpelling_IsCanonicalised()
    {
        var outcome = CombatRules.Damage(State(10, 10), Hit(3, "Fire"), DamageAdjustments.FromTypes(["FIRE"], null, null));

        Assert.Equal("fire", Assert.Single(outcome.Parts).DamageType);
        Assert.Equal(1, outcome.Total);
    }

    [Theory]
    [InlineData(-1, 10, 0, 0, "2024")]
    [InlineData(5, -1, 0, 0, "2024")]
    [InlineData(5, 10, -1, 0, "2024")]
    [InlineData(5, 10, 0, 7, "2024")]
    [InlineData(5, 10, 0, 0, "5e")]
    public void Damage_BadState_IsAHostBug(int hp, int max, int temp, int exhaustion, string edition)
    {
        Assert.ThrowsAny<ArgumentException>(() => CombatRules.Damage(State(hp, max, edition, temp, exhaustion: exhaustion), Hit(1)));
    }

    [Fact]
    public void Damage_AnAmountBeyondTheGuard_IsAHostBug()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CombatRules.Damage(State(10, 10), Hit(CombatRules.MaxDamagePart + 1)));
    }

    private static StatBlock StatBlockWith(IReadOnlyList<DamageAdjustment> resistances) =>
        Simulation.TestStatBlocks.Create("Test", 12, 50, "10d8", (10, 10, 10, 10, 10, 10), [], resistances: resistances);
}
