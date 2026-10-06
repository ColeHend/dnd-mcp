using System.Text.Json.Nodes;
using DndMcp.Domain.Combat;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Combat;
using Xunit;
using L = DndMcp.Domain.Campaign.CampaignValues.CombatLogKinds;

namespace DndMcp.Tests.CampaignCombat;

/// <summary>
/// Invariant: the server's combat rolls (contract D11, §6.10) are rolled with the caller's roller and logged as
/// <c>dice_roll</c> rows inside the step's transaction: the encounter set, filed under the live session (else the fight's,
/// else none), the expression the dice actually rolled ("4d6+5" for a critical "2d6+5"), the detail's source "combat",
/// a label that names the subject only by its party-safe name (never a typed or true name: A-L2/A-L3), secret by §6.10's
/// rules (B-L1), and cited by the combat_log rows that used them (<c>roll_id</c>). A value the caller gives makes no row.
/// </summary>
public sealed class CombatDiceTests
{
    [Fact]
    public void FixtureB_ThreeServerRolls_LabelsSecrecyExpressionsAndTheRowsThatCiteThem()
    {
        using var w = CombatWorld.OnePiece();
        CombatScripts.FixtureB(w);

        var dice = w.Dice();
        var encounter = w.Encounter(CombatScripts.StationName);
        Assert.Equal(
            [("1d10", "Aboleth: heal", 5L, 1L), ("4d6+5", "Aboleth: damage (critical)", 19L, 1L), ("2d4+2", "Björn Mountainfell: heal", 7L, 0L)],
            dice.Select(d => (d.Expression, d.Label!, d.Total, d.Secret)));
        Assert.All(dice, d => Assert.Equal(encounter.Id, d.EncounterId));
        Assert.All(dice, d => Assert.Equal(w.Id("session:13"), d.SessionId));
        Assert.All(dice, d => Assert.Equal(CombatDice.Source, JsonNode.Parse(d.Detail)!["source"]!.GetValue<string>()));
        var log = w.Log(CombatScripts.StationName);
        Assert.Equal(dice.Select(d => d.Id).ToHashSet(), log.Where(r => r.RollId is not null).Select(r => r.RollId!).ToHashSet());
        Assert.Contains(log, r => r.RollId == dice[0].Id && r.Kind == L.Heal);
        Assert.Contains(log, r => r.RollId == dice[1].Id && r.Kind == L.Damage);
        Assert.Contains(log, r => r.RollId == dice[2].Id && r.Kind == L.Heal);
        Assert.DoesNotContain(dice, d => d.Label!.Contains("Nester", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FixtureA_EveryValueGiven_NoDiceRow_GivenFacesInTheDetail()
    {
        using var w = CombatWorld.Belmakor();

        CombatScripts.FixtureA(w);

        Assert.Empty(w.Dice());
        var log = w.Log(CombatScripts.CryptName);
        Assert.All(log, r => Assert.Null(r.RollId));
        Assert.Contains(log, r => r.Kind == L.Initiative && r.Detail!.Contains("\"given\":true", StringComparison.Ordinal));
    }

    [Fact]
    public void TypedNameOverAStatBlock_ItsRollsAreLabelledByTheMonster_NeverTheTypedName()
    {
        using var w = CombatWorld.Belmakor();
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Rematch (probe)", AddParty = false, Combatants = [CombatWorld.Monster("2014", "lich", name: "Keras", hp: HpChoice.Avg)] });
        w.Roller.Push(11);

        var outcome = w.Combat.Initiative(w.Campaign, null, new InitiativeOp());

        var roll = Assert.Single(w.Dice());
        Assert.Equal("Lich: initiative", roll.Label);
        Assert.Equal(0L, roll.Secret);
        Assert.Null(roll.SessionId);
        Assert.Equal("Lich: initiative", Assert.Single(outcome.Rolls).Label);
        Assert.Contains(outcome.Lines.Concat(outcome.Reminders.Select(r => r.Text)), l => l.Contains(CombatTracker.RolledHereNote, StringComparison.Ordinal));
    }

    [Fact]
    public void PlayerCampaign_AHiddenCombatantsRoll_IsSecret_AnEnemysHitPoints_AreSecret()
    {
        using var w = CombatWorld.Player();
        w.Roller.Push(4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4);
        w.Combat.Start(w.Campaign, new StartRequest
        {
            Name = "Ambush",
            AddParty = false,
            Combatants =
            [
                CombatWorld.Monster("2014", "ogre", hp: HpChoice.Roll),
                new CombatantRequest { Name = "Lurker", Hp = HpChoice.Of(10), Side = "ally", Hidden = true },
            ],
        });
        var hp = Assert.Single(w.Dice());
        Assert.Equal(("7d10+21", "Ogre: hit points", 1L), (hp.Expression, hp.Label!, hp.Secret));
        w.Roller.Push(9, 9);

        w.Combat.Initiative(w.Campaign, null, new InitiativeOp());

        var rolls = w.Dice().Skip(1).ToList();
        Assert.Equal(2, rolls.Count);
        Assert.Contains(rolls, r => r.Label == "Ogre: initiative" && r.Secret == 0);
        Assert.Contains(rolls, r => r.Label!.EndsWith(": initiative", StringComparison.Ordinal) && r.Label != "Ogre: initiative" && r.Secret == 1);
        Assert.Equal(49, w.Combatant("Ambush", "Ogre").MaxHp);
    }

    [Fact]
    public void DmCampaign_EnemyRollsAreSecret_TheCallsSecretOverrides()
    {
        using var w = CombatWorld.Dm();
        w.Sheet("character:hero", CombatLifecycleTests.HeroJson);
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Duel", Combatants = [CombatWorld.Monster("2024", "ogre", hp: HpChoice.Avg)] });
        w.Combat.Initiative(w.Campaign, null, new InitiativeOp { Rolls = [new("hero", Total: 15), new("ogre", Total: 10), new("sidekick", Total: 5)] });

        w.Roller.Push(3, 3);
        w.Combat.Damage(w.Campaign, null, new DamageOp(["hero"]) { Dice = "2d8", DamageType = "bludgeoning", Source = "ogre" });
        w.Roller.Push(5, 5);
        w.Combat.Damage(w.Campaign, null, new DamageOp(["hero"]) { Dice = "2d8", DamageType = "bludgeoning", Source = "ogre" }, secret: false);
        w.Roller.Push(6);
        w.Combat.Damage(w.Campaign, null, new DamageOp(["ogre"]) { Dice = "1d8+3", DamageType = "slashing", Source = "hero" });
        w.Roller.Push(2);
        w.Combat.Damage(w.Campaign, null, new DamageOp(["ogre"]) { Dice = "1d8+3", DamageType = "slashing", Source = "hero" }, secret: true);

        Assert.Equal(
            [("Ogre: damage", 1L), ("Ogre: damage", 0L), ("Hero Prime: damage", 0L), ("Hero Prime: damage", 1L)],
            w.Dice().Select(d => (d.Label!, d.Secret)));
    }

    [Fact]
    public void NoSourceDamage_IsTheTurnHoldersRoll_AnEnemysOwnTurnSecret_APcsOwnTurnOpen_AHealStaysTheTargets()
    {
        // L01 (§6.10 as amended by F1): with no source, a damage roll is the actor's (the turn-holder rolls it), never the
        // target's: the target default logged the ogre's own dice open under the hero's name, and the hero's secret.
        using var w = CombatWorld.Dm();
        w.Sheet("character:hero", CombatLifecycleTests.HeroJson);
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Duel", Combatants = [CombatWorld.Monster("2024", "ogre", hp: HpChoice.Avg)] });
        w.Combat.Initiative(w.Campaign, null, new InitiativeOp { Rolls = [new("ogre", Total: 15), new("hero", Total: 10), new("sidekick", Total: 5)] });

        w.Roller.Push(3, 3);
        w.Combat.Damage(w.Campaign, null, new DamageOp(["hero"]) { Dice = "2d8", DamageType = "bludgeoning" });
        w.Combat.Next(w.Campaign, null);
        w.Roller.Push(6);
        w.Combat.Damage(w.Campaign, null, new DamageOp(["ogre"]) { Dice = "1d8+3", DamageType = "slashing" });
        w.Roller.Push(2);
        w.Combat.Heal(w.Campaign, null, new HealOp(["ogre"]) { Dice = "1d4" });

        Assert.Equal([("Ogre: damage", 1L), ("Hero Prime: damage", 0L), ("Ogre: heal", 1L)], w.Dice().Select(d => (d.Label!, d.Secret)));
    }

    [Fact]
    public void PlayerCampaign_AHiddenTurnHoldersNoSourceDamage_IsSecret_NeverThePartyTargetsOpenRoll()
    {
        using var w = CombatWorld.Player();
        w.Combat.Start(w.Campaign, new StartRequest
        {
            Name = "Ambush",
            AddParty = false,
            Combatants = [new CombatantRequest { Character = "character:aria-vale" }, new CombatantRequest { Name = "Lurker", Hp = HpChoice.Of(10), Hidden = true }],
        });
        w.Combat.Initiative(w.Campaign, null, new InitiativeOp { Rolls = [new("lurker", Total: 20), new("aria-vale", Total: 10)] });
        w.Roller.Push(4, 5);

        w.Combat.Damage(w.Campaign, null, new DamageOp(["aria-vale"]) { Dice = "2d6+3", DamageType = "piercing" });

        var roll = Assert.Single(w.Dice());
        Assert.Equal(1L, roll.Secret);
        Assert.DoesNotContain("Aria", roll.Label!, StringComparison.Ordinal);
    }

    [Fact]
    public void SeveralTargetsAndNoSource_TheLabelIsThePurpose_SecretWhenAnyTargetWouldBe()
    {
        using var w = CombatWorld.Dm();
        w.Sheet("character:hero", CombatLifecycleTests.HeroJson);
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Fireball", Combatants = [CombatWorld.Monster("2024", "ogre", hp: HpChoice.Avg)] });
        w.Roller.Push(1, 2, 3, 4, 5, 6, 1, 2);

        w.Combat.Damage(w.Campaign, null, new DamageOp(["hero", "ogre"]) { Dice = "8d6", DamageType = "fire" });

        var roll = Assert.Single(w.Dice());
        Assert.Equal(("damage", 1L, "8d6", 24L), (roll.Label!, roll.Secret, roll.Expression, roll.Total));
    }

    [Fact]
    public void DamageInTwoDiceParts_EachRollIsLogged_AndEachIsCitedByARow()
    {
        using var w = CombatWorld.Dm();
        w.Sheet("character:hero", CombatLifecycleTests.HeroJson);
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Parts", Combatants = [CombatWorld.Monster("2024", "ogre", hp: HpChoice.Avg)] });
        w.Roller.Push(4, 5, 6);

        w.Combat.Damage(w.Campaign, null, new DamageOp(["ogre"]) { Parts = [new(Dice: "2d6", Type: "slashing"), new(Dice: "1d6", Type: "fire")], Source = "hero" });

        var dice = w.Dice();
        Assert.Equal(["2d6", "1d6"], dice.Select(d => d.Expression));
        var cited = w.Log("Parts").Where(r => r.RollId is not null).Select(r => r.RollId).ToHashSet();
        Assert.All(dice, d => Assert.Contains(d.Id, cited));
    }

    [Fact]
    public void CustomPartyNameThePartyDoesNotUse_IsLabelledACombatant()
    {
        using var w = CombatWorld.Dm();
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Masks", AddParty = false, Combatants = [new CombatantRequest { Name = "The Villain", Side = "party", Hp = HpChoice.Of(20) }] });
        w.Roller.Push(12);

        w.Combat.Initiative(w.Campaign, null, new InitiativeOp());

        Assert.Equal("a combatant: initiative", Assert.Single(w.Dice()).Label);
    }

    [Fact]
    public void Rolls_AreFiledUnderTheLiveSession_ElseTheFightsSession_ElseNone()
    {
        using var w = CombatWorld.Dm();
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Long fight", AddParty = false, Combatants = [new CombatantRequest { Name = "Rat", Hp = HpChoice.Of(4), Side = "ally" }] });
        w.Roller.Push(1);
        w.Combat.Heal(w.Campaign, null, new HealOp(["rat"]) { Dice = "1d4" });
        w.Combat.End(w.Campaign, null, new EndRequest());
        w.StartSession(1);
        w.Reload();
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Session fight", AddParty = false, Combatants = [new CombatantRequest { Name = "Rat", Hp = HpChoice.Of(4), Side = "ally" }] });
        w.Roller.Push(2);
        w.Combat.Heal(w.Campaign, null, new HealOp(["rat"]) { Dice = "1d4" });
        w.F.Sessions.End(w.Campaign, "The rats were fought.");
        w.Reload();
        w.Roller.Push(3);

        w.Combat.Heal(w.Campaign, null, new HealOp(["rat"]) { Dice = "1d4" });

        var session = w.Id("session:1");
        Assert.Equal([null, session, session], w.Dice().Select(d => d.SessionId));
        Assert.Equal(w.Encounter("Session fight").Id, w.Dice()[^1].EncounterId);
    }

    [Fact]
    public void ARollInALaterSession_IsFiledUnderTheLiveSession_NotTheFightsEarlierOne()
    {
        using var w = CombatWorld.Dm();
        w.StartSession(1);
        w.Reload();
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Long fight", AddParty = false, Combatants = [new CombatantRequest { Name = "Rat", Hp = HpChoice.Of(4), Side = "ally" }] });
        w.F.Sessions.End(w.Campaign, "Stopped mid-fight.");
        w.Reload();
        w.StartSession(2);
        w.Reload();
        w.Roller.Push(1);

        w.Combat.Heal(w.Campaign, null, new HealOp(["rat"]) { Dice = "1d4" });

        Assert.Equal(w.Id("session:1"), w.Encounter("Long fight").SessionId);
        Assert.Equal(w.Id("session:2"), Assert.Single(w.Dice()).SessionId);
    }

    [Fact]
    public void DisguisedAlly_ItsRollIsSecret_LabelledByTheStandIn_NeverItsName()
    {
        using var w = CombatWorld.OnePiece();
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Rooftop (probe)", AddParty = false, Combatants = [new CombatantRequest { Character = "character:protector", Side = "ally", Hp = HpChoice.Of(30) }] });
        w.Roller.Push(2);

        w.Combat.Heal(w.Campaign, null, new HealOp(["protector"]) { Dice = "1d4" });

        var roll = Assert.Single(w.Dice());
        Assert.Equal(("a combatant: heal", 1L), (roll.Label!, roll.Secret));
    }

    [Fact]
    public void PartySideLinkedEntityThePartyCannotSee_ItsRollStaysOpen_UnderTheStandIn()
    {
        using var w = CombatWorld.Dm();
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Turncoat", AddParty = false, Combatants = [new CombatantRequest { Character = "character:villain", Side = "party" }] });
        w.Roller.Push(12);

        w.Combat.Initiative(w.Campaign, null, new InitiativeOp());

        var roll = Assert.Single(w.Dice());
        Assert.Equal(("a combatant: initiative", 0L), (roll.Label!, roll.Secret));
    }

    [Theory]
    [InlineData("player")]
    [InlineData("dm")]
    public void AnAllysHitPointRoll_IsSecret_InEitherRole(string role)
    {
        using var w = role == "dm" ? CombatWorld.Dm() : CombatWorld.Player();
        w.Roller.Push(Enumerable.Repeat(4, 20).ToArray());

        w.Combat.Start(w.Campaign, new StartRequest { Name = "Hired", AddParty = false, Combatants = [CombatWorld.Monster(w.Campaign.Ruleset, "ogre", hp: HpChoice.Roll, side: "ally")] });

        var roll = Assert.Single(w.Dice());
        Assert.Equal(("Ogre: hit points", 1L), (roll.Label!, roll.Secret));
    }

    [Theory]
    [InlineData("neutral", 1L)]
    [InlineData("enemy", 1L)]
    [InlineData("ally", 0L)]
    public void DmCampaign_EnemyAndNeutralRollsAreSecret_AnAllysOpen(string side, long secret)
    {
        using var w = CombatWorld.Dm();
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Bystander", AddParty = false, Combatants = [CombatWorld.Monster("2024", "ogre", hp: HpChoice.Avg, side: side)] });
        w.Combat.Damage(w.Campaign, null, new DamageOp(["ogre"]) { Amount = 10 });
        w.Roller.Push(3);

        w.Combat.Heal(w.Campaign, null, new HealOp(["ogre"]) { Dice = "1d4" });

        var roll = Assert.Single(w.Dice());
        Assert.Equal(("Ogre: heal", secret), (roll.Label!, roll.Secret));
    }

    [Theory]
    [InlineData(true, 1L)]
    [InlineData(false, 0L)]
    public void PlayerCampaign_APartySideEntrysHitPointRoll_IsOpen_UnlessItIsHidden(bool hidden, long secret)
    {
        using var w = CombatWorld.Player();
        w.Roller.Push(Enumerable.Repeat(4, 20).ToArray());

        w.Combat.Start(w.Campaign, new StartRequest { Name = "Unseen", AddParty = false, Combatants = [CombatWorld.Monster("2014", "ogre", hp: HpChoice.Roll, side: "party", hidden: hidden)] });

        var roll = Assert.Single(w.Dice());
        Assert.Equal(("Ogre: hit points", secret), (roll.Label!, roll.Secret));
    }
}
