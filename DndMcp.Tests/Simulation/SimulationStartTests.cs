using System.Text.Json;
using DndMcp.Domain.Core;
using DndMcp.Domain.Rng;
using DndMcp.Domain.Simulation;
using DndMcp.Tests.Srd.Combatants;
using Xunit;
using K = DndMcp.Domain.Simulation.StatBlockValues;

namespace DndMcp.Tests.Simulation;

/// <summary>
/// A creature seeded from a live state (<see cref="CombatantStart"/>, contract §11.3): every field lands on the fight's
/// creature without a die, the engine's own rules settle what the seed implies (incapacitation ends concentration, a
/// sixth exhaustion level kills, the downed lie prone), whichever creature comes first, and the statistics count from
/// where the fight was picked up. Scripted through the engine's own methods, as the rules tests are.
/// </summary>
public sealed class SimulationStartTests
{
    private static readonly SimulationCombatant Fighter = SimKit.Pc(SimKit.Fighter2024, hp: 40, ac: 18, name: "Fighter");
    private static readonly SimulationCombatant Ogre = SimKit.Monster(TestStatBlocks.Ogre);

    /// <summary>A harmless, unkillable target: AC 40 and 5,000 HP, a poke that cannot hit.</summary>
    private static SimulationCombatant Sandbag(string name = "Sandbag") => SimKit.Monster(TestStatBlocks.Sandbag(name), name: name);

    private static readonly SimulationCombatant Warden = SimKit.Pc("""
        { "name": "Warden", "edition": "2014", "level": 6, "abilities": {"str": 16, "con": 16},
          "attacks": [{ "name": "Longsword", "count": 2, "damage": "1d8", "damage_type": "slashing", "properties": ["melee"] }],
          "modifiers": [{ "kind": "temp_hp", "name": "Armor of Agathys", "amount": 10 }] }
        """, hp: 52, ac: 17, name: "Warden");

    private static readonly SimulationCombatant Cleric = SimKit.Pc("""
        { "name": "Cleric", "edition": "2024", "level": 9, "abilities": {"wis": 18},
          "attacks": [{ "name": "Mace", "damage": "1d6", "damage_type": "bludgeoning", "properties": ["melee"] }],
          "modifiers": [{ "kind": "heal", "name": "Healing Word", "dice": "2d4", "amount": "wis", "action_cost": "bonus_action", "resource": {"uses": 3, "per": "long_rest"} },
                        { "kind": "save_effect", "name": "Hold Monster", "ability": "wis", "dc": 16, "condition": "paralyzed", "concentration": true, "resource": {"uses": 1, "per": "long_rest"} }] }
        """, hp: 60, ac: 18, name: "Cleric");

    private static SimulationCombatant Seed(SimulationCombatant combatant, CombatantStart start) => combatant with { Start = start };

    private static Fight Begin(IReadOnlyList<SimulationCombatant> party, IReadOnlyList<SimulationCombatant> enemies, FightResume? resume = null,
                               string? enemyHp = null, ulong seed = 1)
    {
        var spec = SimKit.Spec(party, enemies, enemyHp: enemyHp);
        spec = new SimulationSpec
        {
            Party = spec.Party,
            Enemies = spec.Enemies,
            Iterations = spec.Iterations,
            EnemyHp = spec.EnemyHp,
            Resume = resume,
        };
        return Scripted.Begin(spec, seed);
    }

    private static string Refusal(SimulationSpec spec) => Assert.Throws<DndInputException>(() => Simulator.Run(spec, 1)).Message;

    // ------------------------------------------------------------------------------------------------------------------
    // Hit points: the seed is where the fight starts; the maximum stays the real one.
    // ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Begin_SeededHp_StartsThereAndKeepsTheRealMaximum()
    {
        var fighter = Begin([Seed(Fighter, new CombatantStart { Hp = 3 })], [Ogre]).Named("Fighter");
        Assert.Equal((3, 40, 3), (fighter.Hp, fighter.MaxHp, fighter.StartHp));
    }

    [Theory]
    [InlineData(42, false)] // 3 HP + 39 left over: under the maximum of 40, so it drops and is dying.
    [InlineData(43, true)] // 40 left over: the maximum, massive damage kills (reading the current 3 HP would kill at 6).
    public void ApplyDamage_SeededAt3Of40_MassiveDamageReadsTheMaximum(int damage, bool dies)
    {
        var fight = Begin([Seed(Fighter, new CombatantStart { Hp = 3 })], [Ogre]);
        var fighter = fight.Named("Fighter");
        fight.ApplyDamage(null, fighter, Scripted.Damage(damage), false, false, false, false, false);
        Assert.Equal(dies, fighter.Dead);
        Assert.Equal(!dies, fighter.Down);
    }

    [Fact]
    public void ApplyDamage_SeededAtZero_DamageAtZeroReadsTheMaximum()
    {
        // At 0 HP, damage of at least the maximum (40) kills; 39 is one death save failure.
        var fight = Begin([Seed(Fighter, new CombatantStart { Hp = 0 })], [Ogre]);
        var fighter = fight.Named("Fighter");
        fight.ApplyDamage(null, fighter, Scripted.Damage(39), false, false, false, false, false);
        Assert.Equal((false, 1), (fighter.Dead, fighter.DeathFailures));
        fight.ApplyDamage(null, fighter, Scripted.Damage(40), false, false, false, false, false);
        Assert.True(fighter.Dead);
    }

    [Fact]
    public void HpLost_SeededCreatureThatDies_LosesWhatItStartedWith()
    {
        var fight = Begin([Seed(Fighter, new CombatantStart { Hp = 5 })], [Ogre]);
        var fighter = fight.Named("Fighter");
        fight.ApplyDamage(null, fighter, Scripted.Damage(200), false, false, false, false, false);
        Assert.True(fighter.Dead);
        Assert.Equal(5, CreatureTally.HpLost(fighter));
    }

    [Fact]
    public void Run_SeededHp_ReportCountsFromTheStartHp()
    {
        // A fighter at 20 of 40 who is never hit: HP at start 20, nothing lost (counted from the maximum: 20 lost).
        var report = Simulator.Run(SimKit.Spec([Seed(Fighter, new CombatantStart { Hp = 20 })], [Sandbag()], iterations: 200, roundCap: 2), 3);
        var fighter = report.Combatants[0];
        Assert.Equal(20, fighter.MaxHp);
        Assert.True(fighter.HpLost.Mean < 0.5, $"HP lost {fighter.HpLost.Mean}");
        Assert.True(report.Resumed);
    }

    [Theory]
    [InlineData(null, 10)] // the fresh grant
    [InlineData(3, 3)] // what is left replaces it
    [InlineData(0, 0)]
    public void Begin_SeededTempHp_ReplacesTheBuildsGrant(int? seeded, int expected)
    {
        var warden = Begin([Seed(Warden, new CombatantStart { TempHp = seeded })], [Ogre]).Named("Warden");
        Assert.Equal(expected, warden.TempHp);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // 0 HP, death saves, exhaustion.
    // ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Begin_PcSeededAtZero_IsDownProneAndDyingWithItsTallies()
    {
        var fighter = Begin([Seed(Fighter, new CombatantStart { Hp = 0, DeathSuccesses = 1, DeathFailures = 2 }), Warden], [Ogre]).Named("Fighter");
        Assert.True(fighter.Down && fighter.Unconscious && fighter.Has(Cond.Prone) && fighter.Dropped);
        Assert.False(fighter.Dead || fighter.Stable);
        Assert.Equal((1, 2), (fighter.DeathSuccesses, fighter.DeathFailures));
        Assert.True(CreatureTally.IsDying(fighter));
    }

    [Fact]
    public void Begin_PcSeededAtZeroAndStable_IsStable()
    {
        var fighter = Begin([Seed(Fighter, new CombatantStart { Hp = 0, Stable = true }), Warden], [Ogre]).Named("Fighter");
        Assert.True(fighter.Down && fighter.Stable);
        Assert.False(CreatureTally.IsDying(fighter));
    }

    [Fact]
    public void Begin_TalliesAndStableAboveZero_AreIgnored()
    {
        var fighter = Begin([Seed(Fighter, new CombatantStart { Hp = 9, Stable = true, DeathFailures = 2 })], [Ogre]).Named("Fighter");
        Assert.Equal((false, false, 0), (fighter.Down, fighter.Stable, fighter.DeathFailures));
    }

    [Fact]
    public void Prepare_MonsterSeededAtZero_IsRefused()
    {
        var message = Refusal(SimKit.Spec([Fighter], [Seed(Ogre, new CombatantStart { Hp = 0 })]));
        Assert.Contains("enemies item 1 (Ogre): start hp is 0, but it makes no death saves and dies at 0 HP; leave it out, or keep it as a placeholder", message);
    }

    [Fact]
    public void Begin_TrollSeededAtZero_IsDownAndRegeneratesAtItsTurn()
    {
        var fight = Begin([Fighter], [Seed(SimKit.Monster(TestStatBlocks.Troll), new CombatantStart { Hp = 0 })]);
        var troll = fight.Named("Troll");
        Assert.True(troll.Down && !troll.Dead && !troll.Has(Cond.Prone));
        fight.StartOfTurn(troll);
        Assert.Equal(10, troll.Hp);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    public void Begin_SeededExhaustion_AddsItsLevels(int level)
    {
        var fighter = Begin([Seed(Fighter, new CombatantStart { Exhaustion = level })], [Ogre]).Named("Fighter");
        Assert.Equal(level, fighter.Exhaustion);
        Assert.False(fighter.Dead);
    }

    [Fact]
    public void Begin_SeededExhaustionSix_KillsWithoutRollingItsDeathBurst()
    {
        // A sixth level kills on the seed as it kills in a fight; the death happened in the live fight, so its burst is not
        // rolled again: the fight's generator is untouched after a seeded, fixed-order start.
        var magmin = TestStatBlocks.Create("Magmin", 14, 9, "2d6+2", (7, 15, 12, 8, 11, 10), [TestStatBlocks.Attack("Touch", 4, "2d6", "fire")]) with
        {
            Traits =
            [
                new StatBlockTrait
                {
                    Name = "Death Burst", Kind = K.TraitKinds.DeathBurst, Text = "Death Burst",
                    Damage = [TestStatBlocks.Roll("2d6", "fire")], Save = new SaveSpec("dex", 11, K.OnSuccess.Half), Area = new AreaSpec(K.Shapes.Sphere, 10),
                },
            ],
        };
        var fight = Begin([Seed(Fighter, new CombatantStart())], [Seed(SimKit.Monster(magmin), new CombatantStart { Exhaustion = 6 })],
            new FightResume([0, 1], 0, 2), seed: 9);
        var burst = fight.Named("Magmin");
        Assert.True(burst.Dead);
        Assert.Equal(40, fight.Named("Fighter").Hp);
        Assert.Equal(new Xoshiro256StarStar(9).NextUInt64(), fight.PeekNextDraw());
        Assert.True(fight.Over);
        Assert.Equal(SimulationValues.Outcomes.PartyWins, fight.Outcome);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Concentration and the conditions it holds.
    // ------------------------------------------------------------------------------------------------------------------

    private static readonly SimulationCombatant Caster = SimKit.Monster(TestStatBlocks.Ogre, name: "Caster");

    public static IEnumerable<object[]> BothOrders => [[true], [false]];

    [Theory]
    [MemberData(nameof(BothOrders))]
    public void Begin_SeededConcentration_HoldsItsConditions(bool casterFirst)
    {
        var fight = HeldParalysis(casterFirst, stunned: false);
        var caster = fight.Named("Caster");
        Assert.NotEqual(0, caster.ConcentrationToken);
        Assert.Equal("Hold Person", caster.ConcentrationLabel);
        var target = fight.Named("Target");
        Assert.True(target.Has(Cond.Paralyzed));

        // The held condition goes with the concentration: incapacitating the caster ends both.
        fight.AddCondition(null, caster, new ConditionTemplate { Condition = Cond.Stunned, Duration = DurationKind.Fight }, 0);
        Assert.Equal(0, caster.ConcentrationToken);
        Assert.False(target.Has(Cond.Paralyzed));
    }

    [Theory]
    [MemberData(nameof(BothOrders))]
    public void Begin_SeededConcentrationWithASeededStunned_EndsWhicheverCreatureComesFirst(bool casterFirst)
    {
        var fight = HeldParalysis(casterFirst, stunned: true);
        Assert.Equal(0, fight.Named("Caster").ConcentrationToken);
        Assert.True(fight.Named("Caster").Has(Cond.Stunned));
        Assert.False(fight.Named("Target").Has(Cond.Paralyzed));
    }

    /// <summary>A monster concentrating on Hold Person (a spell no build models), paralyzing the other side's creature.</summary>
    private static Fight HeldParalysis(bool casterFirst, bool stunned)
    {
        var casterStart = new CombatantStart
        {
            Concentration = "Hold Person",
            Conditions = stunned ? [new StartCondition("stunned", K.Durations.Fight)] : [],
        };
        var caster = Seed(Caster, casterStart);
        var target = SimKit.Monster(TestStatBlocks.Ogre, name: "Target");
        return casterFirst
            ? Begin([caster], [Seed(target, new CombatantStart { Conditions = [new StartCondition("paralyzed", K.Durations.Fight, SourceEntry: 0, HeldBySourceConcentration: true)] })])
            : Begin([Seed(target, new CombatantStart { Conditions = [new StartCondition("paralyzed", K.Durations.Fight, SourceEntry: 1, HeldBySourceConcentration: true)] })], [caster]);
    }

    [Fact]
    public void Begin_SeededConcentrationOfADownedCreature_IsNotStartedAndHoldsNothing()
    {
        var fight = Begin(
            [Seed(Cleric, new CombatantStart { Hp = 0, Concentration = "Hold Monster" }), Fighter],
            [Seed(Ogre, new CombatantStart { Conditions = [new StartCondition("paralyzed", K.Durations.Fight, SourceEntry: 0, HeldBySourceConcentration: true)] })]);
        Assert.Equal(0, fight.Named("Cleric").ConcentrationToken);
        Assert.False(fight.Named("Ogre").Has(Cond.Paralyzed));
    }

    [Fact]
    public void Begin_HexSeeded_IsUpAndHeld_AndUnseededItIsLostForGood()
    {
        var warlock = SimKit.Pc("""{ "name": "Warlock", "preset": "warlock_baseline", "level": 5 }""", hp: 38, ac: 13, name: "Warlock");
        var fresh = Begin([warlock], [Ogre]).Named("Warlock");
        var number = fresh.Pc!.Build.ConcentrationNumber;
        Assert.True(fresh.ConcentrationToken != 0 && fresh.Pc.Active[number]);

        var held = Begin([Seed(warlock, new CombatantStart { Concentration = "  HEX " })], [Ogre]).Named("Warlock");
        Assert.True(held.ConcentrationToken != 0 && held.Pc!.Active[number] && held.ConcentrationDeactivates);
        Assert.Equal("Hex", held.ConcentrationLabel);

        // Not concentrating at the resume: the Hex was lost, and with no setup to pay it stays lost (unlike a fresh fight).
        var lost = Begin([Seed(warlock, new CombatantStart())], [Ogre]).Named("Warlock");
        Assert.True(lost.ConcentrationToken == 0 && !lost.Pc!.Active[number]);
    }

    [Fact]
    public void Begin_SpiritGuardiansSeeded_ItsSetupIsPaid()
    {
        var cleric = new SimulationCombatant(new CombatantSpec { Archetype = "cleric", Level = 5, Edition = "2024" });
        var fresh = Begin([cleric], [Ogre]).Named("Cleric");
        var number = fresh.Pc!.Build.ConcentrationNumber;
        Assert.Equal("Spirit Guardians", fresh.Pc.Build.ConcentrationLabel);
        Assert.False(fresh.Pc.Active[number]);

        var held = Begin([Seed(cleric, new CombatantStart { Concentration = "spirit guardians" })], [Ogre]).Named("Cleric");
        Assert.True(held.Pc!.Active[number] && held.ConcentrationToken != 0 && held.ConcentrationDeactivates);
    }

    [Fact]
    public void Begin_ASaveEffectConcentration_StaysCastableWhenNotHeld()
    {
        var notHeld = Begin([Seed(Cleric, new CombatantStart())], [Ogre]).Named("Cleric");
        var number = notHeld.Pc!.Build.ConcentrationNumber;
        Assert.True(notHeld.Pc.Active[number]);
        Assert.Equal(0, notHeld.ConcentrationToken);

        var held = Begin([Seed(Cleric, new CombatantStart { Concentration = "Hold Monster" })], [Ogre]).Named("Cleric");
        Assert.True(held.ConcentrationToken != 0 && held.Pc!.Active[number] && !held.ConcentrationDeactivates);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Durations: the seeded condition ends where the engine ends it.
    // ------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)] // imposed during the Fighter's resumed turn: that turn's end does not count
    public void Run_SeededUntilEndOfSourceTurn_EndsAtTheSourcesTurnEnd(bool imposedDuringResumedTurn, int sourceTurns)
    {
        var fight = Begin([Fighter],
            [Seed(Sandbag(), new CombatantStart { Conditions = [new StartCondition("frightened", K.Durations.UntilEndOfSourceTurn, SourceEntry: 0, ImposedDuringResumedTurn: imposedDuringResumedTurn)] })],
            new FightResume([0, 1], 0, 2));
        var sandbag = fight.Named("Sandbag");
        var fighter = fight.Named("Fighter");
        for (var turn = 1; turn <= sourceTurns; turn++)
        {
            Assert.True(sandbag.Has(Cond.Frightened));
            fight.TakeTurn(sandbag); // its own turn ends nothing it has from another source
            Assert.True(sandbag.Has(Cond.Frightened));
            fight.TakeTurn(fighter);
        }

        Assert.False(sandbag.Has(Cond.Frightened));
    }

    [Fact]
    public void Run_SeededRounds_TickAtTheSourcesTurnEnds()
    {
        var fight = Begin([Fighter],
            [Seed(Sandbag(), new CombatantStart { Conditions = [new StartCondition("restrained", K.Durations.Rounds, SourceEntry: 0, RoundsLeft: 2)] })]);
        var sandbag = fight.Named("Sandbag");
        fight.TakeTurn(fight.Named("Fighter"));
        Assert.True(sandbag.Has(Cond.Restrained));
        fight.TakeTurn(fight.Named("Fighter"));
        Assert.False(sandbag.Has(Cond.Restrained));
    }

    [Theory]
    [InlineData(K.Durations.Rounds, null, true)]
    [InlineData(K.Durations.SaveEnds, 40, true)] // a save it cannot make: it lasts until its source's turn starts
    [InlineData(K.Durations.SaveEnds, 1, false)] // a save it always makes: its own turn's end ends it first
    public void Run_SeededRoundsLeftZero_EndsAtTheSourcesNextTurnStart(string duration, int? dc, bool outlivesItsOwnTurn)
    {
        // rounds_left 0 (a "1 minute" resumed in its last round after its anchor acted): no counted turn end is left, and
        // RAW ends it at the START of the source's next turn, so it is that duration exactly; a save-ends keeps its save.
        var fight = Begin([Fighter],
            [Seed(Sandbag(), new CombatantStart { Conditions = [new StartCondition("frightened", duration, SourceEntry: 0, RoundsLeft: 0, SaveAbility: dc is null ? null : "wis", SaveDc: dc)] })]);
        var sandbag = fight.Named("Sandbag");
        Assert.Equal(DurationKind.UntilStartOfSourceTurn, sandbag.Conditions.Single(c => c.Condition == Cond.Frightened).Duration);
        fight.TakeTurn(sandbag);
        Assert.Equal(outlivesItsOwnTurn, sandbag.Has(Cond.Frightened));
        fight.StartOfTurn(fight.Named("Fighter"));
        Assert.False(sandbag.Has(Cond.Frightened));
    }

    /// <summary>
    /// The resumed turn's own conditions (contract §6.11): [Fighter] against [Sandbag, Bag ×2], in the order Bag ×2, Sandbag,
    /// Fighter, resumed at the Fighter's turn (entry position 2, creature position 3), whose start is played again.
    /// </summary>
    public static IEnumerable<object[]> ResumedTurnRows()
    {
        foreach (var duration in new[] { K.Durations.UntilStartOfSourceTurn, K.Durations.UntilStartOfTargetTurn, K.Durations.UntilEndOfSourceTurn, K.Durations.UntilEndOfTargetTurn, K.Durations.Rounds })
        {
            yield return [duration, true, true, 1]; // its anchor is the turn-holder: that turn does not count
            yield return [duration, true, false, 0]; // not imposed this turn: the replayed start (or the turn's end) ends it
            if (duration != K.Durations.Rounds)
            {
                yield return [duration, false, true, 0]; // imposed this turn, but its anchor's next turn is still ahead
            }
        }
    }

    [Theory]
    [MemberData(nameof(ResumedTurnRows))]
    public void Run_ConditionImposedDuringTheResumedTurn_OutlivesThatTurnOnlyForItsAnchor(string duration, bool anchorIsTurnHolder, bool imposedDuringResumedTurn,
                                                                                           int anchorTurnsSurvived)
    {
        // Source kinds (and rounds_left 0) are anchored on the source, target kinds on the target. The turn-holder (the
        // Fighter, entry 0) anchors it when it is the source of a source kind or the target of a target kind.
        var targetKind = duration is K.Durations.UntilStartOfTargetTurn or K.Durations.UntilEndOfTargetTurn;
        var onFighter = anchorIsTurnHolder == targetKind;
        var condition = new StartCondition("frightened", duration, SourceEntry: onFighter ? 1 : 0, RoundsLeft: duration == K.Durations.Rounds ? 0 : null,
            ImposedDuringResumedTurn: imposedDuringResumedTurn);
        var withIt = new CombatantStart { Conditions = [condition] };
        var bags = SimKit.Monster(TestStatBlocks.Sandbag("Bag"), count: 2, name: "Bag");
        var fight = Begin([onFighter ? Seed(Fighter, withIt) : Fighter], [onFighter ? Sandbag() : Seed(Sandbag(), withIt), bags], new FightResume([2, 1, 0], 2, 3));
        var target = fight.Named(onFighter ? "Fighter" : "Sandbag");
        var anchor = fight.Named(anchorIsTurnHolder ? "Fighter" : "Sandbag");
        Assert.Equal(fight.Named("Fighter").Id, fight.Order[3]);

        var survived = 0;
        for (var turn = 0; turn < 3 && target.Has(Cond.Frightened); turn++)
        {
            fight.TakeTurn(anchor);
            survived += target.Has(Cond.Frightened) ? 1 : 0;
        }

        Assert.Equal(anchorTurnsSurvived, survived);
        Assert.False(target.Has(Cond.Frightened));
    }

    [Fact]
    public void Run_StunnedUntilTheTurnHoldersNextTurnStart_OutlivesTheReplayedStart()
    {
        // The Fighter's Stunning Strike landed before the DM asked: the replayed start of its turn does not end it; the
        // start of its next turn, a round later, does.
        var stunned = new StartCondition("stunned", K.Durations.UntilStartOfSourceTurn, SourceEntry: 0, ImposedDuringResumedTurn: true);
        var spec = new SimulationSpec
        {
            Party = [Fighter],
            Enemies = [Seed(Sandbag(), new CombatantStart { Conditions = [stunned] })],
            Iterations = 2,
            RoundCap = 2,
            Replay = 1,
            Resume = new FightResume([0, 1], 0, 5),
        };
        var log = Simulator.Run(spec, 1).ReplayLog!;
        var released = log.IndexOf("Sandbag is no longer stunned (its source's turn starts)", StringComparison.Ordinal);
        Assert.True(released > log.IndexOf("Round 6", StringComparison.Ordinal), log);
        Assert.Contains("incapacitated: no action", log[..log.IndexOf("Round 6", StringComparison.Ordinal)]);
    }

    [Fact]
    public void Begin_SeededProneWithAnyDuration_LastsUntilItStands()
    {
        var fight = Begin([Seed(Fighter, new CombatantStart { Conditions = [new StartCondition("Prone", K.Durations.Fight)] })], [Sandbag()]);
        var fighter = fight.Named("Fighter");
        Assert.True(fighter.Has(Cond.Prone));
        fight.StartOfTurn(fighter);
        Assert.False(fighter.Has(Cond.Prone));
    }

    [Theory]
    [MemberData(nameof(BothOrders))]
    public void Begin_SeededGrappleBySourceThatIsStunned_IsReleasedWhicheverComesFirst(bool grapplerFirst)
    {
        var grapple = new StartCondition("grappled", K.Durations.UntilEscape, SourceEntry: grapplerFirst ? 0 : 1, EscapeDc: 13);
        var grappler = Seed(SimKit.Monster(TestStatBlocks.Ogre, name: "Grappler"), new CombatantStart { Conditions = [new StartCondition("stunned", K.Durations.Fight)] });
        var held = Seed(SimKit.Monster(TestStatBlocks.Ogre, name: "Held"), new CombatantStart { Conditions = [grapple] });
        var fight = grapplerFirst ? Begin([grappler], [held]) : Begin([held], [grappler]);
        Assert.False(fight.Named("Held").Has(Cond.Grappled));
    }

    [Fact]
    public void Begin_SeededGrapple_HoldsWhileItsSourceCan()
    {
        var fight = Begin([Fighter],
            [Seed(Ogre, new CombatantStart { Conditions = [new StartCondition("grappled", K.Durations.UntilEscape, SourceEntry: 0, EscapeDc: 14)] })]);
        Assert.True(fight.Named("Ogre").Has(Cond.Grappled));
    }

    [Fact]
    public void Begin_SeededConditionTheTargetIsImmuneTo_IsNotApplied()
    {
        var fight = Begin([Fighter], [Seed(SimKit.Monster(TestStatBlocks.Zombie), new CombatantStart { Conditions = [new StartCondition("poisoned", K.Durations.Fight)] })]);
        Assert.False(fight.Named("Zombie").Has(Cond.Poisoned));
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Uses, recharge, pools, legendary counts, setups, flags.
    // ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Begin_SeededUsesLeft_MatchTheBuildsResourcesForgivingly()
    {
        var cleric = Begin([Seed(Cleric, new CombatantStart { UsesLeft = new Dictionary<string, int> { ["healing-word"] = 1, ["HOLD MONSTER"] = 0, ["Bardic Inspiration"] = 2 } })], [Ogre])
            .Named("Cleric");
        var resources = cleric.Pc!.Build.Resources.Select(r => r.Label).ToList();
        Assert.Equal(1, cleric.Pc.UsesLeft[resources.IndexOf("Healing Word")]);
        Assert.Equal(0, cleric.Pc.UsesLeft[resources.IndexOf("Hold Monster")]);
    }

    [Fact]
    public void Begin_SeededUsesAboveTheFreshCount_AreCapped()
    {
        var cleric = Begin([Seed(Cleric, new CombatantStart { UsesLeft = new Dictionary<string, int> { ["Healing Word"] = 9 } })], [Ogre]).Named("Cleric");
        Assert.Equal(3, cleric.Pc!.UsesLeft[cleric.Pc.Build.Resources.Select(r => r.Label).ToList().IndexOf("Healing Word")]);
    }

    private static StatBlock LairDragon => TestStatBlocks.Create("Lair Dragon", 18, 200, "16d12+96", (23, 10, 21, 14, 11, 19),
        [TestStatBlocks.Attack("Rend", 11, "2d8+6", "slashing")], edition: "2024", cr: "15",
        legendary: new LegendaryActions(3, 4, [TestStatBlocks.Attack("Tail", 11, "2d8+6", "bludgeoning")]), legendaryResistance: 3) with { LegendaryResistanceInLair = 4 };

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 4)] // in a lair the cap is the in-lair count
    public void Begin_SeededLegendaryCountsAboveTheFresh_AreCapped(bool lair, int expected)
    {
        // Uncapped, a dragon seeded with 9 legendary actions would take 9 after the next turn.
        var spec = new SimulationSpec
        {
            Party = [Fighter],
            Enemies = [Seed(SimKit.Monster(LairDragon), new CombatantStart { LegendaryActionsLeft = 9, LegendaryResistanceLeft = 9 })],
            Lair = lair,
        };
        var dragon = Scripted.Begin(spec).Named("Lair Dragon");
        Assert.Equal((expected, expected), (dragon.LegendaryUsesLeft, dragon.LegendaryResistanceLeft));
    }

    [Fact]
    public void Begin_SeededPerDayAndPoolUsesAboveTheFresh_AreCapped()
    {
        var (block, slot) = PerDayBlock();
        var perDay = Begin([Fighter], [Seed(SimKit.Monster(block), new CombatantStart { UsesLeft = new Dictionary<string, int> { [PerDayName(block, slot)] = 99 } })])
            .Named(block.Name);
        Assert.Equal(perDay.T.Limited[slot].Source.Usage.Uses, perDay.UsesLeft[slot]);

        var mage = PoolMage();
        var fresh = Begin([Fighter], [SimKit.Monster(mage)]).Named(mage.Name);
        var pools = Begin([Fighter], [Seed(SimKit.Monster(mage), new CombatantStart { UsesLeft = fresh.T.PoolNames.ToDictionary(p => p, _ => 99) })]).Named(mage.Name);
        Assert.Equal(fresh.T.PoolSizes, pools.PoolLeft);
    }

    [Theory]
    [InlineData(0, false)] // a recharge named in uses_left with 0 is spent (the tracker's limited:<Name> {ready: false})
    [InlineData(1, true)]
    public void Begin_SeededRechargeInUsesLeft_ZeroStartsSpent(int uses, bool ready)
    {
        var dragon = Begin([Fighter], [Seed(SimKit.Monster(TestStatBlocks.AdultRedDragon), new CombatantStart { UsesLeft = new Dictionary<string, int> { ["fire breath"] = uses } })])
            .Named("Adult Red Dragon");
        Assert.Equal(ready, dragon.RechargeReady[dragon.T.LimitedNames.ToList().IndexOf("Fire Breath")]);
    }

    [Fact]
    public void Begin_ActiveSetupNamingTheConcentrationModifier_DoesNotRaiseIt()
    {
        // Spirit Guardians up with no concentration holding it could never be broken: the concentration modifier follows
        // Concentration, never ActiveSetups.
        var cleric = new SimulationCombatant(new CombatantSpec { Archetype = "cleric", Level = 5, Edition = "2024" });
        var seeded = Begin([Seed(cleric, new CombatantStart { ActiveSetups = ["Spirit Guardians"] })], [Ogre]).Named("Cleric");
        Assert.False(seeded.Pc!.Active[seeded.Pc.Build.ConcentrationNumber]);
        Assert.Equal(0, seeded.ConcentrationToken);
    }

    [Fact]
    public void Run_SeededPerDayAndPool_ReportWhatWasLeft()
    {
        var (block, slot) = PerDayBlock();
        var name = PerDayName(block, slot);
        var perDay = Simulator.Run(SimKit.Spec([Fighter], [Seed(SimKit.Monster(block), new CombatantStart { UsesLeft = new Dictionary<string, int> { [name] = 1 } })], iterations: 10), 1);
        Assert.Equal(1, perDay.Combatants[1].Resources.Single(r => r.Name == name).Available);

        var mage = PoolMage();
        var pool = Begin([Fighter], [SimKit.Monster(mage)]).Named(mage.Name).T.PoolNames[0];
        var pools = Simulator.Run(SimKit.Spec([Fighter], [Seed(SimKit.Monster(mage), new CombatantStart { UsesLeft = new Dictionary<string, int> { [pool] = 1 } })], iterations: 10), 1);
        Assert.Equal(1, pools.Combatants[1].Resources.Single(r => r.Name == $"spell slots ({pool})").Available);
    }

    [Fact]
    public void Run_OneSeededNameThatMatchesNothing_IsSaidInTheSingular()
    {
        var report = Simulator.Run(SimKit.Spec([Seed(Cleric, new CombatantStart { ActiveSetups = ["Rage"] })], [Ogre], iterations: 10), 1);
        Assert.Contains("Cleric: the live state's \"Rage\" matches none of its uses, recharges, slot pools or setups here, so it starts as in a fresh fight.", report.Assumptions);
    }

    /// <summary>A shipped 2024 stat block with a per-day action of more than one use, and that action's limited slot.</summary>
    private static (StatBlock Block, int Slot) PerDayBlock()
    {
        var block = CorrectedSrd.Shipped.StatBlocks("2024").First(b => b.Actions.Concat(b.Spells).Any(a => a.Usage.Kind == K.UsageKinds.PerDay && (a.Usage.Uses ?? 1) > 1));
        var fresh = Begin([Fighter], [SimKit.Monster(block)]).Named(block.Name);
        return (block, Enumerable.Range(0, fresh.T.Limited.Length).First(i => fresh.T.Limited[i].Source.Usage.Kind == K.UsageKinds.PerDay && (fresh.T.Limited[i].Source.Usage.Uses ?? 1) > 1));
    }

    private static string PerDayName(StatBlock block, int slot) => Begin([Fighter], [SimKit.Monster(block)]).Named(block.Name).T.LimitedNames[slot];

    /// <summary>A shipped 2014 caster with more than one spell slot pool.</summary>
    private static StatBlock PoolMage() =>
        CorrectedSrd.Shipped.StatBlocks("2014").First(b => b.SpellSlots.Count > 1 && b.Spells.Select(s => s.Usage.Pool).Distinct().Count() > 2);

    [Fact]
    public void Run_SeededNamesThatMatchNothing_StartFreshAndAreSaid()
    {
        var report = Simulator.Run(SimKit.Spec(
            [Seed(Cleric, new CombatantStart { UsesLeft = new Dictionary<string, int> { ["Bardic Inspiration"] = 2, ["Healing Word"] = 1 }, ActiveSetups = ["Rage"], Spent = ["Fire Breath"] })],
            [Ogre], iterations: 20), 1);
        Assert.Contains("Cleric: the live state's \"Bardic Inspiration\", \"Fire Breath\", \"Rage\" match none of its uses, recharges, slot pools or setups here, so they start as in a fresh fight.",
            report.Assumptions);
        Assert.Equal(1, report.Combatants[0].Resources.Single(r => r.Name == "Healing Word").Available);
    }

    [Fact]
    public void Begin_SeededDragon_RechargeLegendaryAndResistance()
    {
        var dragon = Begin([Fighter],
            [Seed(SimKit.Monster(TestStatBlocks.AdultRedDragon), new CombatantStart { Spent = ["fire breath"], LegendaryActionsLeft = 1, LegendaryResistanceLeft = 9, RelentlessUsed = true, ReactionUsed = true })])
            .Named("Adult Red Dragon");
        Assert.False(dragon.RechargeReady[dragon.T.LimitedNames.ToList().IndexOf("Fire Breath")]);
        Assert.Equal((1, 3), (dragon.LegendaryUsesLeft, dragon.LegendaryResistanceLeft));
        Assert.True(dragon.RelentlessUsed);
        Assert.False(dragon.ReactionAvailable);
    }

    [Fact]
    public void Begin_SeededSlotPools_AreSetByName()
    {
        var mage = CorrectedSrd.Shipped.StatBlocks("2014").First(b => b.SpellSlots.Count > 1 && b.Spells.Select(s => s.Usage.Pool).Distinct().Count() > 2);
        var fresh = Begin([Fighter], [SimKit.Monster(mage)]).Named(mage.Name);
        Assert.True(fresh.T.PoolNames.Length > 1);

        var seeded = Begin([Fighter], [Seed(SimKit.Monster(mage), new CombatantStart { UsesLeft = new Dictionary<string, int> { [fresh.T.PoolNames[0].ToUpperInvariant()] = 1 } })])
            .Named(mage.Name);
        Assert.Equal(1, seeded.PoolLeft[0]);
        Assert.Equal(fresh.T.PoolSizes.Skip(1), seeded.PoolLeft.Skip(1));
    }

    [Fact]
    public void Begin_SeededPerDayUses_AreSetByName()
    {
        var block = CorrectedSrd.Shipped.StatBlocks("2024").First(b => b.Actions.Concat(b.Spells).Any(a => a.Usage.Kind == K.UsageKinds.PerDay && (a.Usage.Uses ?? 1) > 1));
        var fresh = Begin([Fighter], [SimKit.Monster(block)]).Named(block.Name);
        var slot = Enumerable.Range(0, fresh.T.Limited.Length).First(i => fresh.T.Limited[i].Source.Usage.Kind == K.UsageKinds.PerDay && (fresh.T.Limited[i].Source.Usage.Uses ?? 1) > 1);
        Assert.True(fresh.UsesLeft[slot] > 1);

        var seeded = Begin([Fighter], [Seed(SimKit.Monster(block), new CombatantStart { UsesLeft = new Dictionary<string, int> { [fresh.T.LimitedNames[slot]] = 1 } })])
            .Named(block.Name);
        Assert.Equal(1, seeded.UsesLeft[slot]);
        var spent = Begin([Fighter], [Seed(SimKit.Monster(block), new CombatantStart { Spent = [fresh.T.LimitedNames[slot]] })]).Named(block.Name);
        Assert.Equal(0, spent.UsesLeft[slot]);
    }

    [Fact]
    public void Begin_SeededActiveSetups_RageIsUp()
    {
        var barbarian = new SimulationCombatant(new CombatantSpec { Archetype = "barbarian", Level = 5, Edition = "2024" });
        var fresh = Begin([barbarian], [Ogre]).Named("Barbarian");
        var rage = fresh.Pc!.Build.Setups.Single(s => s.Source.Label == "Rage").Source.Number;
        Assert.False(fresh.Pc.Active[rage]);

        var raging = Begin([Seed(barbarian, new CombatantStart { ActiveSetups = ["rage"], HasActed = true })], [Ogre]).Named("Barbarian");
        Assert.True(raging.Pc!.Active[rage]);
        Assert.True(raging.Pc.Acted);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Placeholders.
    // ------------------------------------------------------------------------------------------------------------------

    private static readonly SimulationCombatant Fallen = new(new CombatantSpec { Name = "Fallen" }, Start: new CombatantStart { Placeholder = true });

    [Fact]
    public void Begin_Placeholder_IsDeadAndWhatItImposedStillEndsAtItsPlace()
    {
        // The fighter is frightened until the start of the fallen creature's next turn; it is dead, but at its place in the
        // order the condition still ends.
        var fight = Begin(
            [Seed(Fighter, new CombatantStart { Conditions = [new StartCondition("frightened", K.Durations.UntilStartOfSourceTurn, SourceEntry: 1)] })],
            [Fallen, Sandbag()]);
        var fallen = fight.Named("Fallen");
        Assert.True(fallen.Dead);
        Assert.True(fight.Named("Fighter").Has(Cond.Frightened));
        fight.TakeTurn(fallen);
        Assert.False(fight.Named("Fighter").Has(Cond.Frightened));
    }

    [Fact]
    public void Run_Placeholder_ItsImposedConditionExpiresInTheReplay()
    {
        var spec = SimKit.Spec(
            [Seed(Fighter, new CombatantStart { Conditions = [new StartCondition("frightened", K.Durations.UntilStartOfSourceTurn, SourceEntry: 1)] })],
            [Fallen, Sandbag()], iterations: 10, roundCap: 2, replay: 1);
        var log = Simulator.Run(spec, 4).ReplayLog!;
        Assert.Contains("Start: Fallen (dead; a placeholder holding its place in the order)", log);
        Assert.Contains("Fighter is no longer frightened (its source is dead; its turn would start)", log);
        var summary = log[log.IndexOf("Summary of fight 1:", StringComparison.Ordinal)..];
        Assert.Contains("- Fighter (party)", summary);
        Assert.DoesNotContain("Fallen", summary);
    }

    [Fact]
    public void Run_Placeholders_AreLeftOutOfTheOutcomesAndTheReport()
    {
        var fallenPc = new SimulationCombatant(new CombatantSpec { Name = "Fallen Hero", Build = SimKit.Build(SimKit.Fighter2024), Hp = 40, Ac = 18 },
            Start: new CombatantStart { Placeholder = true });
        var report = Simulator.Run(SimKit.Spec([Fighter, fallenPc], [Sandbag(), Fallen], iterations: 100, roundCap: 2), 2);
        Assert.Equal(0, report.AnyPartyDeath.Count);
        Assert.Equal(["Fighter", "Sandbag"], report.Combatants.Select(c => c.Name));
        Assert.Contains("Placeholders (dead; they only keep their place in the order, so what they imposed still ends on their turns, and are left out of the results): Fallen Hero, Fallen.",
            report.Assumptions);
    }

    [Fact]
    public void Run_APlaceholderFirstInTheParty_TheEditionFollowsTheFirstWhoFights()
    {
        // No edition given: the fight's is the first fighting party member's (the 2014 Warden), not the placeholder's default.
        var report = Simulator.Run(SimKit.Spec([new SimulationCombatant(new CombatantSpec { Name = "Fallen Hero" }, Start: new CombatantStart { Placeholder = true }), Warden],
            [Sandbag()], iterations: 10, roundCap: 2), 1);
        Assert.Equal("2014", report.Edition);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // No die is drawn; the report says it was resumed.
    // ------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("roll")]
    public void Begin_EverySeededCreatureInAFixedOrder_DrawsNoDie(string? enemyHp)
    {
        const ulong seed = 77;
        var fight = Begin(
            [Seed(Fighter, new CombatantStart { Hp = 12, Exhaustion = 2, Concentration = "Bless", Conditions = [new StartCondition("poisoned", K.Durations.SaveEnds, SaveAbility: "con", SaveDc: 12)] })],
            [Seed(SimKit.Monster(TestStatBlocks.Troll, count: 1), new CombatantStart { Hp = 30, Conditions = [new StartCondition("restrained", K.Durations.Rounds, SourceEntry: 0, RoundsLeft: 3)] }),
             Seed(SimKit.Monster(TestStatBlocks.AdultRedDragon), new CombatantStart { Spent = ["Fire Breath"] })],
            new FightResume([2, 0, 1], 1, 3), enemyHp, seed);
        Assert.Equal(new Xoshiro256StarStar(seed).NextUInt64(), fight.PeekNextDraw());

        var fresh = Begin([Fighter], [SimKit.Monster(TestStatBlocks.Troll)], seed: seed);
        Assert.NotEqual(new Xoshiro256StarStar(seed).NextUInt64(), fresh.PeekNextDraw());
    }

    [Fact]
    public void Run_AnEmptyStartWithoutConcentration_FightsTheSameFightsAsNoStart()
    {
        // A start that changes nothing (no concentration modifier to lose) draws nothing either: same outcomes, same rounds.
        var plain = Simulator.Run(SimKit.Spec([Fighter], [Ogre], iterations: 500), 6);
        var seeded = Simulator.Run(SimKit.Spec([Seed(Fighter, new CombatantStart())], [Seed(Ogre, new CombatantStart())], iterations: 500), 6);
        Assert.Equal(JsonSerializer.Serialize(plain.Combatants), JsonSerializer.Serialize(seeded.Combatants));
        Assert.Equal(JsonSerializer.Serialize(plain.Rounds), JsonSerializer.Serialize(seeded.Rounds));
        Assert.False(plain.Resumed);
        Assert.True(seeded.Resumed);
    }

    [Fact]
    public void Report_Resumed_IsLeftOutOfTheJsonWhenFalse()
    {
        Assert.DoesNotContain("Resumed", JsonSerializer.Serialize(Simulator.Run(SimKit.Spec([Fighter], [Ogre], iterations: 10), 1)), StringComparison.Ordinal);
        Assert.Contains("\"Resumed\":true", JsonSerializer.Serialize(Simulator.Run(SimKit.Spec([Seed(Fighter, new CombatantStart { Hp = 30 })], [Ogre], iterations: 10), 1)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Run_CompareWithAResumedTurnsCondition_TheVariantKeepsItToo()
    {
        // The Fighter, resumed at its own turn, is frightened of the Ogre until the start of its next turn, imposed during
        // this one (a reaction): it attacks with Disadvantage this turn in both fights, or the variant's dice would differ.
        var feature = SimKit.Feature("""{ "name": "Nothing", "modifiers": [{ "kind": "to_hit", "name": "Zero", "amount": 0 }] }""");
        var frightened = new StartCondition("frightened", K.Durations.UntilStartOfTargetTurn, SourceEntry: 1, ImposedDuringResumedTurn: true);
        var spec = new SimulationSpec
        {
            Party = [Seed(Fighter, new CombatantStart { Conditions = [frightened] })],
            Enemies = [Ogre],
            Iterations = 1_000,
            Resume = new FightResume([0, 1], 0, 2),
            Compare = new CompareSpec { Member = 1, Feature = feature },
        };
        var compare = Simulator.Run(spec, 5).Compare!;
        Assert.Equal(compare.BaselineWins.Count, compare.VariantWins.Count);
        Assert.Equal(0, compare.RoundsDifference.Mean);
    }

    [Fact]
    public void Run_CompareWithASeededMember_TheVariantStartsFromTheSameState()
    {
        var feature = SimKit.Feature("""{ "name": "Nothing", "modifiers": [{ "kind": "to_hit", "name": "Zero", "amount": 0 }] }""");
        var spec = SimKit.Spec([Seed(Fighter, new CombatantStart { Hp = 6, Conditions = [new StartCondition("poisoned", K.Durations.Fight)] })], [Ogre], iterations: 1_000,
            compare: new CompareSpec { Member = 1, Feature = feature });
        var compare = Simulator.Run(spec, 5).Compare!;
        // A feature that changes nothing fights identical fights only if the variant starts where the baseline does.
        Assert.Equal(compare.BaselineWins.Count, compare.VariantWins.Count);
        Assert.Equal(0, compare.WinDifference.Mean);
        Assert.Equal(0, compare.RoundsDifference.Mean);
    }
}
