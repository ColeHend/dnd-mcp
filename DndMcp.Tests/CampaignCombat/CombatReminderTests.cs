using DndMcp.Domain.Combat;
using DndMcp.Repository.Campaign.Combat;
using DndMcp.Repository.Campaign.Write;
using Xunit;
using K = DndMcp.Domain.Combat.CombatValues.ReminderKinds;

namespace DndMcp.Tests.CampaignCombat;

/// <summary>
/// Invariant: a combat call's result says ONE dying line per dying creature (contract §6.3, "one dying line per OTHER
/// combatant at 0 HP that is dying"). A hit on a dying creature makes the tracker's step say why its tallies moved ("Hero
/// Prime: 0 HP, 0 successes, 2 failures (a critical hit: two death save failures)"), and the turn's context, recomputed
/// after every step, says the same creature is dying ("… 2 failures"). <see cref="CombatRun.DistinctReminders"/> keeps the
/// step's own, more informative line and drops the context's for that creature, for a <c>combat</c> call and for a
/// <c>campaign_character</c> call routed into the fight alike; a dying creature the step said nothing of keeps the
/// context's line. What breaks without it: FIX A23 and B20 print Torch's and the monk's tallies twice, and a table reads
/// two dying lines as two dying creatures.
/// </summary>
public sealed class CombatReminderTests
{
    private const string Fight = "Dying";
    private const string HeroJson = """{ "classes": [{ "class": "wizard", "level": 5 }], "abilities": { "con": 14, "dex": 12, "int": 18 }, "ac": 12, "max_hp": 32 }""";

    // Hero Prime at 0 HP and dying (0 successes, 0 failures) during the Sidekick's turn in round 1.
    private static CombatWorld HeroDyingInTheSidekicksTurn(string ruleset)
    {
        var w = CombatWorld.Dm(ruleset);
        try
        {
            w.Sheet("character:hero", HeroJson);
            w.Sheet("character:sidekick", CombatLifecycleTests.SidekickJson);
            var c = w.Campaign;
            w.Combat.Start(c, new StartRequest { Name = Fight });
            w.Combat.Add(c, null, [CombatWorld.Monster(ruleset, "ogre", hp: HpChoice.Avg)]);
            w.Combat.Initiative(c, null, new InitiativeOp { Rolls = [new("hero", Total: 18), new("sidekick", Total: 15), new("ogre", Total: 12)] });
            w.Combat.Next(c, null, "hero");
            var dropped = w.Combat.Damage(c, null, new DamageOp(["hero"]) { Amount = 40, DamageType = "bludgeoning", Source = "ogre" });
            Assert.Equal("Hero Prime: 0 HP, 0 successes, 0 failures", Assert.Single(dropped.Reminders, r => r.Kind == K.Dying).Text);
            return w;
        }
        catch
        {
            w.Dispose();
            throw;
        }
    }

    [Theory]
    [InlineData("2024", true, "Hero Prime: 0 HP, 0 successes, 2 failures (a critical hit: two death save failures)", 2)]
    [InlineData("2024", false, "Hero Prime: 0 HP, 0 successes, 1 failure (one death save failure)", 1)]
    [InlineData("2014", true, "Hero Prime: 0 HP, 0 successes, 2 failures (a critical hit: two death save failures)", 2)]
    [InlineData("2014", false, "Hero Prime: 0 HP, 0 successes, 1 failure (one death save failure)", 1)]
    public void Damage_OnADyingCreatureInAnotherCreaturesTurn_OneDyingLine_TheStepsOwn(string ruleset, bool critical, string line, int failures)
    {
        using var w = HeroDyingInTheSidekicksTurn(ruleset);

        var outcome = w.Combat.Damage(w.Campaign, null, new DamageOp(["hero", "ogre"]) { Amount = 5, DamageType = "slashing", Critical = critical, Source = "sidekick" });

        var hero = w.Combatant(Fight, "Hero Prime");
        Assert.Equal((0, failures), (hero.Hp, hero.DeathSaves.Failures));
        var dying = Assert.Single(outcome.Reminders, r => r.Kind == K.Dying);
        Assert.Equal((hero.Id, line), (dying.CombatantId, dying.Text));
    }

    [Theory]
    [InlineData("2024")]
    [InlineData("2014")]
    public void AStepThatSaysNothingOfTheDying_KeepsTheTurnContextsLine(string ruleset)
    {
        using var w = HeroDyingInTheSidekicksTurn(ruleset);

        var outcome = w.Combat.Damage(w.Campaign, null, new DamageOp(["ogre"]) { Amount = 5, DamageType = "slashing" });

        var dying = Assert.Single(outcome.Reminders, r => r.Kind == K.Dying);
        Assert.Equal((w.Combatant(Fight, "Hero Prime").Id, "Hero Prime: 0 HP, 0 successes, 0 failures"), (dying.CombatantId, dying.Text));
    }

    [Fact]
    public void RoutedDamage_OnADyingCharacter_OneDyingLine_TheStepsOwn()
    {
        using var w = HeroDyingInTheSidekicksTurn("2024");

        var result = w.Characters.Damage(w.Campaign, "character:hero", 5, "slashing", WriteContext.Default);

        Assert.True(result.Routed!.Applied);
        var dying = Assert.Single(result.Routed.Reminders, r => r.Kind == K.Dying);
        Assert.Equal("Hero Prime: 0 HP, 0 successes, 1 failure (one death save failure)", dying.Text);
    }
}
