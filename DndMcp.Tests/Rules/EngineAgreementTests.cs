using DndMcp.Domain.Rules;
using DndMcp.Domain.Simulation;
using DndMcp.Tests.Simulation;
using DndMcp.Tests.Srd.Combatants;
using Xunit;
using static DndMcp.Tests.Rules.RulesKit;

namespace DndMcp.Tests.Rules;

/// <summary>
/// Invariant: the shared rules and the simulator's engine (which keeps its own optimised arithmetic and is not edited
/// in Phase 7) give the same numbers on the same inputs — every SRD stat block's damage adjustments for every type and
/// every magical/silvered/adamantine/half/Petrified combination, massive damage, damage at 0 HP, healing, the
/// temporary-HP keep-higher rule, and the death interceptors for monsters with and without death saves. A tracker fight
/// seeded into a simulation therefore continues by the rules it was played by.
/// </summary>
public sealed class EngineAgreementTests
{
    private static readonly SimulationCombatant Party = SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "Fighter");

    public static TheoryData<string> Editions => new(["2014", "2024"]);

    private static readonly string?[] Types = [.. Domain.Features.DslValues.DamageTypes.Set.Values, null];

    [Theory]
    [MemberData(nameof(Editions))]
    public void Damage_EverySrdStatBlockTypeAndFlag_TotalsAsTheEngineDeals(string edition)
    {
        var compared = 0;
        foreach (var block in CorrectedSrd.Shipped.StatBlocks(edition))
        {
            var fight = Scripted.Begin([Party], [SimKit.Monster(block, name: "M")]);
            var target = fight.Named("M");
            var adjustments = DamageAdjustments.FromStatBlock(block);
            for (var petrified = 0; petrified < 2; petrified++)
            {
                if (petrified == 1 && !fight.AddCondition(null, target, new ConditionTemplate { Condition = Cond.Petrified, Duration = DurationKind.Fight }, 0))
                {
                    continue; // immune to Petrified
                }

                var withPetrified = adjustments with { Petrified = petrified == 1 };
                foreach (var type in Types)
                {
                    for (var flags = 0; flags < 16; flags++)
                    {
                        bool magical = (flags & 1) != 0, silvered = (flags & 2) != 0, adamantine = (flags & 4) != 0, half = (flags & 8) != 0;
                        target.Hp = 1_000_000;
                        var dealt = fight.ApplyDamage(null, target, Scripted.Damage(9, type), magical, silvered, adamantine, false, half).Dealt;
                        var request = Hit(9, type) with { Magical = magical, Silvered = silvered, Adamantine = adamantine, Half = half };
                        var total = CombatRules.Damage(State(1_000_000, 1_000_000, edition, pc: false), request, withPetrified).Total;
                        Assert.True(dealt == total, $"{block.Ref} {type ?? "untyped"} flags {flags} petrified {petrified}: engine {dealt}, rules {total}");
                        compared++;
                    }
                }
            }
        }

        Assert.True(compared > 300 * 14 * 16);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(12)]
    public void Damage_DropAndMassiveDamage_AgreeWithTheEngine(int hp)
    {
        for (var temp = 0; temp <= 3; temp += 3)
        {
            for (var damage = 0; damage <= 30; damage++)
            {
                var fight = Scripted.Begin([SimKit.Pc(SimKit.Fighter2024, hp: 12, ac: 18, name: "P")], [SimKit.Monster(TestStatBlocks.Ogre)]);
                var pc = fight.Named("P");
                pc.Hp = hp;
                pc.TempHp = temp;
                fight.ApplyDamage(null, pc, Scripted.Damage(damage), false, false, false, false, false);
                var outcome = CombatRules.Damage(State(hp, 12, "2024", temp), Hit(damage));

                var where = $"hp {hp} temp {temp} damage {damage}";
                Assert.True(pc.Dead == outcome.After.Dead, where);
                Assert.True(pc.Down == outcome.After.Dying, where);
                Assert.True(pc.Hp == outcome.After.Hp, where);
                Assert.True(pc.TempHp == outcome.After.TempHp, where);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Damage_AtZeroHp_AgreesWithTheEngine(bool critical)
    {
        foreach (var (successes, failures) in new[] { (0, 0), (0, 1), (0, 2), (2, 0), (1, 2) })
        {
            foreach (var stable in new[] { false, true })
            {
                foreach (var temp in new[] { 0, 5, 50 })
                {
                    foreach (var damage in new[] { 0, 1, 3, 43, 44, 50 })
                    {
                        var fight = Scripted.Begin([SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "P")], [SimKit.Monster(TestStatBlocks.Ogre)]);
                        var pc = fight.Named("P");
                        fight.ApplyDamage(null, pc, Scripted.Damage(44), false, false, false, false, false);
                        pc.DeathSuccesses = stable ? 0 : successes;
                        pc.DeathFailures = stable ? 0 : failures;
                        pc.Stable = stable;
                        pc.TempHp = temp;
                        fight.ApplyDamage(null, pc, Scripted.Damage(damage), false, false, false, critical, false);
                        var tally = stable ? DeathSaveTally.Stabilized : new DeathSaveTally(successes, failures, false);
                        var outcome = CombatRules.Damage(State(0, 44, temp: temp, saves: tally), Hit(damage) with { Critical = critical });

                        var where = $"successes {successes} failures {failures} stable {stable} temp {temp} damage {damage}";
                        Assert.True(pc.Dead == outcome.After.Dead, where);
                        Assert.True(pc.TempHp == outcome.After.TempHp, where);
                        if (!pc.Dead)
                        {
                            Assert.True(pc.DeathSuccesses == outcome.After.DeathSaves.Successes, where);
                            Assert.True(pc.DeathFailures == outcome.After.DeathSaves.Failures, where);
                            Assert.True(pc.Stable == outcome.After.DeathSaves.Stable, where);
                        }
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(0, 7)]
    [InlineData(0, 100)]
    [InlineData(20, 7)]
    [InlineData(40, 7)]
    public void Heal_AgreesWithTheEngine(int hp, int amount)
    {
        var fight = Scripted.Begin([SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "P")], [SimKit.Monster(TestStatBlocks.Ogre)]);
        var pc = fight.Named("P");
        if (hp == 0)
        {
            fight.ApplyDamage(null, pc, Scripted.Damage(44), false, false, false, false, false);
            fight.ApplyDamage(null, pc, Scripted.Damage(1), false, false, false, false, false);
        }
        else
        {
            pc.Hp = hp;
        }

        fight.Heal(pc, pc, amount, "test");
        var state = hp == 0 ? Dying(44, failures: 1) : State(hp, 44);
        var outcome = CombatRules.Heal(state, amount);

        Assert.Equal(pc.Hp, outcome.After.Hp);
        Assert.Equal(pc.DeathFailures, outcome.After.DeathSaves.Failures);
        Assert.Equal(pc.Down, outcome.After.Dying);
    }

    [Fact]
    public void GrantTempHp_KeepsTheHigherAsTheEngine()
    {
        var fight = Scripted.Begin([Party], [SimKit.Monster(TestStatBlocks.Ogre)]);
        var pc = fight.Named("Fighter");
        for (var current = 0; current <= 12; current += 3)
        {
            for (var gained = 0; gained <= 12; gained += 2)
            {
                pc.TempHp = current;
                fight.GainTempHp(pc, gained);
                Assert.Equal(pc.TempHp, CombatRules.GrantTempHp(current, gained));
            }
        }
    }

    [Fact]
    public void Damage_MonsterDeathAndInterceptors_AgreeWithTheEngine()
    {
        // Deterministic engine paths: a monster with nothing dies at 0; a zombie dies to radiant or a critical hit; a
        // boar's Relentless leaves it at 1 HP; a troll's Regeneration leaves it down, not dead.
        var cases = new (string Edition, string Slug, int Hp, int Damage, string Type, bool Critical, bool EngineDead)[]
        {
            ("2014", "ogre", 5, 10, "slashing", false, true),
            ("2014", "zombie", 5, 10, "radiant", false, true),
            ("2014", "zombie", 5, 10, "slashing", true, true),
            ("2014", "boar", 2, 7, "piercing", false, false),
            ("2014", "boar", 2, 8, "piercing", false, true),
            ("2014", "troll", 5, 30, "fire", false, false),
            ("2024", "troll", 5, 30, "slashing", false, false),
        };
        foreach (var (edition, slug, hp, damage, type, critical, engineDead) in cases)
        {
            var block = SrdBlock(edition, slug);
            var fight = Scripted.Begin([Party], [SimKit.Monster(block, name: "M")]);
            var target = fight.Named("M");
            target.Hp = hp;
            fight.ApplyDamage(null, target, Scripted.Damage(damage, type), false, false, false, critical, false);
            var outcome = CombatRules.Damage(Monster(block, hp), Hit(damage, type) with { Critical = critical }, DamageAdjustments.FromStatBlock(block),
                StatBlockFacts.DeathInterceptors(block));

            Assert.True(engineDead == target.Dead, $"{slug}: engine");
            Assert.True(engineDead == outcome.Died, $"{slug}: rules");
            Assert.Equal(!engineDead, outcome.Intercepted);
        }
    }

    [Theory]
    [InlineData(7, 0, false, false)]
    [InlineData(7, 3, false, false)]
    [InlineData(9, 3, false, true)]
    [InlineData(8, 0, false, true)]
    [InlineData(7, 0, true, true)]
    public void Damage_RelentlessThresholdTemporaryHpAndUse_AgreeWithTheEngine(int damage, int temp, bool used, bool dead)
    {
        // The engine reads Relentless on the total before temporary hit points (Fight.ApplyDamage: total ≤ 7), and never
        // once RelentlessUsed is set.
        var block = SrdBlock("2014", "boar");
        var fight = Scripted.Begin([Party], [SimKit.Monster(block, name: "M")]);
        var target = fight.Named("M");
        target.Hp = 2;
        target.TempHp = temp;
        target.RelentlessUsed = used;
        fight.ApplyDamage(null, target, Scripted.Damage(damage, "piercing"), false, false, false, false, false);
        var outcome = CombatRules.Damage(State(2, block.HitPoints, "2014", temp, pc: false), Hit(damage, "piercing"),
            DamageAdjustments.FromStatBlock(block), StatBlockFacts.DeathInterceptors(block, relentlessUsed: used));

        Assert.Equal(dead, target.Dead);
        Assert.Equal(dead, outcome.Died);
        Assert.Equal(!dead, outcome.Intercepted);
        Assert.Equal(target.TempHp, outcome.After.TempHp);
    }

    [Theory]
    [InlineData("boar", 2, 7, "piercing", false)]
    [InlineData("boar", 2, 8, "piercing", false)]
    [InlineData("zombie", 5, 10, "radiant", false)]
    [InlineData("zombie", 5, 10, "slashing", true)]
    [InlineData("zombie", 5, 6, "slashing", false)]
    [InlineData("troll", 5, 30, "fire", false)]
    public void Damage_ADeathSaveMakersInterceptors_AgreeWithTheEngine(string slug, int hp, int damage, string type, bool critical)
    {
        // A death_saves: true monster is PcLike in the engine, which still reads Relentless and Undead Fortitude before it
        // drops (Fight.ApplyDamage) and sends it to dying, never to Regeneration, when nothing holds it (DropToZero). Over
        // many seeds the engine's Undead Fortitude save both holds (1 HP: the rules' Intercepted) and fails (the rules'
        // IfTraitFails, dying).
        var block = SrdBlock("2014", slug);
        var outcome = CombatRules.Damage(State(hp, block.HitPoints, "2014"), Hit(damage, type) with { Critical = critical },
            DamageAdjustments.FromStatBlock(block), StatBlockFacts.DeathInterceptors(block));
        var held = 0;
        var fell = 0;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var fight = Scripted.Begin([Party], [SimKit.Monster(block, name: "M", deathSaves: true)], seed);
            var target = fight.Named("M");
            target.Hp = hp;
            fight.ApplyDamage(null, target, Scripted.Damage(damage, type), false, false, false, critical, false);
            var where = $"{slug} {damage} {type} seed {seed}";
            Assert.False(target.Dead, where);
            Assert.False(outcome.Died, where);
            if (target.Hp == 1)
            {
                held++;
                Assert.True(outcome.Intercepted, where);
                Assert.Contains(outcome.Interceptors, i => i.Kind != Domain.Simulation.StatBlockValues.TraitKinds.Regeneration);
            }
            else
            {
                fell++;
                var fallen = outcome.Intercepted ? outcome.IfTraitFails! : outcome;
                Assert.True(target.Down, where);
                Assert.True(fallen.After.Dying, where);
                Assert.True(fallen.FellUnconscious, where);
                Assert.Equal(target.Hp, fallen.After.Hp);
                Assert.Equal(target.DeathFailures, fallen.After.DeathSaves.Failures);
            }
        }

        Assert.Equal(outcome.Intercepted, held > 0);
        Assert.Equal(!outcome.Intercepted || slug == "zombie", fell > 0);
    }
}
