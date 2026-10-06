using DndMcp.Domain.Combat;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Combat;
using Xunit;
using ES = DndMcp.Domain.Campaign.CampaignValues.EncounterStatuses;
using K = DndMcp.Domain.Combat.CombatValues.ReminderKinds;

namespace DndMcp.Tests.CampaignCombat;

/// <summary>
/// Invariant: the AUTHOR's <c>combat state</c> (contract §6.1, §14.4) and the D20b warnings. With no fight running it
/// succeeds with the listing (the planned and paused fights and the last ended one); a running fight comes with the
/// reminders its state calls for now and the last combat_log row; a name that matches nothing is refused listing the
/// fights (author output). An author step whose typed name, effect or spell a party view would not show says so, with
/// what the party sees instead and, for a name an entity holds, the handle that shows the name the party knows.
/// </summary>
public sealed class CombatStateTests
{
    [Fact]
    public void NoFightRunning_SucceedsWithThePlannedPausedAndLastEnded()
    {
        using var w = CombatWorld.Dm();
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Old fight", AddParty = false, Combatants = [CombatWorld.Monster("2024", "ogre")] });
        w.Combat.End(w.Campaign, null, new EndRequest { Outcome = "Won." });
        w.Combat.Prepare(w.Campaign, new PrepareRequest("Next fight") { Combatants = [CombatWorld.Monster("2024", "ogre", 2)] });
        w.Combat.Prepare(w.Campaign, new PrepareRequest("On hold"));
        w.F.Query<int>("UPDATE encounter SET status = 'paused' WHERE name = 'On hold' RETURNING 1");

        var state = w.Reader.State(w.Campaign);

        Assert.Null(state.Encounter);
        var listing = Assert.IsType<CombatNoFight>(state.NoFight);
        Assert.Equal([("Next fight", ES.Planned, 2)], listing.Planned.Select(e => (e.Name, e.Status, e.Combatants)));
        Assert.Equal(["On hold"], listing.Paused.Select(e => e.Name));
        Assert.Equal(("Old fight", "Won.", (string?)null), (listing.LastEnded!.Name, listing.LastEnded.OutcomeMd, listing.LastEnded.WritebackStatus));
        Assert.Equal(DndMcp.Tests.CampaignRead.LeakAssert.Serialize(listing), DndMcp.Tests.CampaignRead.LeakAssert.Serialize(w.Reader.Encounters(w.Campaign)));
        Assert.Equal("Next fight", w.Reader.State(w.Campaign, "next fight").Encounter!.Name);
    }

    [Fact]
    public void Running_TheStateItsRemindersAndTheLastChange()
    {
        using var w = CombatWorld.Dm();
        w.Sheet("character:hero", CombatLifecycleTests.HeroJson);
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Now", Combatants = [CombatWorld.Monster("2024", "ogre", hp: HpChoice.Avg)] });
        w.Combat.Initiative(w.Campaign, null, new InitiativeOp { Rolls = [new("hero", Total: 15), new("ogre", Total: 10), new("sidekick", Total: 5)] });
        w.Combat.Condition(w.Campaign, null, new ConditionOp(["hero"]) { Add = ["poisoned"] });

        var state = w.Reader.State(w.Campaign);

        var encounter = state.Encounter!;
        Assert.Equal(("Now", ES.Active, 1, "Hero Prime"), (encounter.Name, encounter.Status, encounter.Round, encounter.TurnName));
        Assert.Equal(["Hero Prime", "Ogre", "Sidekick"], encounter.Rows.Select(r => r.Name));
        Assert.Equal([1, 2, 3], encounter.Rows.Select(r => r.Position!.Value));
        Assert.True(encounter.Rows[0].Turn);
        Assert.Contains(state.Reminders, r => r.Kind == K.ConditionEffects && r.Text.Contains("poisoned", StringComparison.Ordinal));
        Assert.Equal(("condition", "Hero Prime"), (state.LastChange!.Kind, state.LastChange.Target));
        Assert.Null(state.NoFight);
    }

    /// <summary>
    /// Deferred (H2's checker): a concentration save the state still owes is listed in the state's reminders, in the very
    /// words and with the call the step that owed it printed, so one lost from that step's output can be recovered; once
    /// resolved it is gone.
    /// </summary>
    [Fact]
    public void Running_AConcentrationSaveStillOwed_IsInTheStatesReminders_UntilItIsResolved()
    {
        using var w = Running();
        w.Combat.Concentration(w.Campaign, null, new ConcentrationOp(["hero"]) { Spell = "Bless" });
        var hit = w.Combat.Damage(w.Campaign, null, new DamageOp(["hero"]) { Amount = 12 });
        var owed = Assert.Single(hit.Reminders, r => r.Kind == K.ConcentrationSave);

        var pending = w.Reader.State(w.Campaign).Reminders;
        w.Combat.Concentration(w.Campaign, null, new ConcentrationOp(["hero"]) { Total = 20 });
        var resolved = w.Reader.State(w.Campaign).Reminders;

        Assert.Contains(owed, pending);
        Assert.Equal(1, pending.Count(r => r.Kind == K.ConcentrationSave));
        Assert.DoesNotContain(resolved, r => r.Kind == K.ConcentrationSave);
    }

    /// <summary>
    /// R03/R04: a stored value no step can run (a total that is not a finite number, a side, ruleset or status outside its
    /// vocabulary, a negative round: values that bypassed a CHECK, or that STRICT REAL takes) is the store's unreadable-row
    /// message on every read and step, never the generic error; it names the column, the combatant, the fight and the
    /// campaign, never the stored value.
    /// </summary>
    [Theory]
    [InlineData("UPDATE combatant SET initiative = 9e999 WHERE name = 'Ogre'", "Ogre", "combatant column initiative is not a finite number.")]
    [InlineData("UPDATE combatant SET order_key = -9e999 WHERE name = 'Ogre'", "Ogre", "combatant column order_key is not a finite number.")]
    [InlineData("UPDATE combatant SET side = 'martian' WHERE name = 'Hero Prime'", "Hero Prime", "combatant column side is not a side (party, ally, enemy or neutral).")]
    [InlineData("UPDATE encounter SET ruleset = '1999'", null, "encounter column ruleset is not an edition (2014 or 2024).")]
    [InlineData("UPDATE encounter SET round = -1", null, "encounter column round is negative.")]
    [InlineData("UPDATE encounter SET status = 'weird'", null, null)]
    public void StoredValueNoStepCanRun_EveryReadAndStepIsTheStoreMessage_NamingWhoAndWhere(string damage, string? combatant, string? column)
    {
        using var w = Running();
        w.F.Db.WriteBehindTheServer(damage);
        if (column is null)
        {
            // An unknown status is no fight the campaign runs: nothing is found, and nothing refuses.
            Assert.Null(w.Reader.State(w.Campaign).Encounter);
            Assert.Throws<CampaignStoreUnavailableException>(() => w.Reader.State(w.Campaign, "Now"));
            return;
        }

        var refusals = new Func<object>[]
        {
            () => w.Reader.State(w.Campaign),
            () => w.Combat.Damage(w.Campaign, null, new DamageOp(["hero"]) { Amount = 2 }),
            () => w.Combat.Next(w.Campaign, null),
            () => w.Combat.End(w.Campaign, null, new EndRequest { DryRun = true }),
            () => w.Loader.Load(w.Campaign),
            () => w.Characters.Damage(w.Campaign, "character:hero", 1, null, Repository.Campaign.Write.WriteContext.Default),
        }.Select(call => Assert.Throws<CampaignStoreUnavailableException>(() => call()).Message).ToList();

        var who = combatant is null ? "The fight \"Now\"" : $"Combatant \"{combatant}\" of the fight \"Now\"";
        Assert.All(refusals, m => Assert.StartsWith($"{who} in campaign sea cannot be read (", m, StringComparison.Ordinal));
        Assert.All(refusals, m => Assert.Contains($"): {column} Nothing was changed.", m, StringComparison.Ordinal));
        Assert.All(refusals, m => Assert.DoesNotContain("martian", WithoutThePath(w, m), StringComparison.Ordinal));
        Assert.All(refusals, m => Assert.DoesNotContain("1999", WithoutThePath(w, m), StringComparison.Ordinal));
    }

    /// <summary>
    /// R03: text in a number column (a table whose STRICT was lost) of a combatant, an encounter or a combat_log row is the
    /// store message too, never Dapper's or T's exception; an encounter row read that way can still be found and discarded.
    /// </summary>
    [Theory]
    [InlineData("combatant", "UPDATE combatant SET hp = 'abc' WHERE name = 'Ogre'", "Combatant \"Ogre\" of the fight \"Now\" in campaign sea cannot be read")]
    [InlineData("encounter", "UPDATE encounter SET round = 'x'", "The fight \"Now\" in campaign sea cannot be read")]
    [InlineData("combat_log", "UPDATE combat_log SET amount = 'many' WHERE seq = (SELECT max(seq) FROM combat_log)", "A combat_log row in ")]
    public void TextInANumberColumn_IsTheStoreMessage_TheFightCanStillBeDiscarded(string table, string damage, string message)
    {
        using var w = Running();
        w.F.Db.DropStrict(table);
        w.F.Db.WriteBehindTheServer(damage);

        var refused = Assert.Throws<CampaignStoreUnavailableException>(() => w.Reader.State(w.Campaign));
        var end = w.Combat.End(w.Campaign, null, new EndRequest { Discard = true });

        Assert.StartsWith(message, refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("abc", WithoutThePath(w, refused.Message), StringComparison.Ordinal);
        Assert.DoesNotContain("many", WithoutThePath(w, refused.Message), StringComparison.Ordinal);
        Assert.True(end.Discarded);
        Assert.Equal(ES.Ended, w.Encounter("Now").Status);
    }

    /// <summary>
    /// A store message with campaigns.db's path taken out (review CR11): the path is a temp directory named by a random
    /// GUID, whose hex holds "abc" in about one run in 140, so a check that the stored value is NOT echoed must never read it.
    /// The path itself must be there.
    /// </summary>
    private static string WithoutThePath(CombatWorld w, string message)
    {
        var path = Path.GetFullPath(w.Database.Path);
        Assert.Contains(path, message, StringComparison.Ordinal);
        return message.Replace(path, "<campaigns.db>", StringComparison.Ordinal);
    }

    private static CombatWorld Running()
    {
        var w = CombatWorld.Dm();
        try
        {
            w.Sheet("character:hero", CombatLifecycleTests.HeroJson);
            w.Combat.Start(w.Campaign, new StartRequest { Name = "Now", AddParty = false, Combatants = [new CombatantRequest { Character = "character:hero" }, CombatWorld.Monster("2024", "ogre", hp: HpChoice.Avg)] });
            w.Combat.Initiative(w.Campaign, null, new InitiativeOp { Rolls = [new("hero", Total: 15), new("ogre", Total: 10)] });
            return w;
        }
        catch
        {
            w.Dispose();
            throw;
        }
    }

    [Fact]
    public void AName_ThatMatchesNothing_IsRefusedListingTheFights_LastWithNoneEndedIsTheListing()
    {
        using var w = CombatWorld.Dm();
        w.Combat.Prepare(w.Campaign, new PrepareRequest("Goblins"));

        Assert.NotNull(w.Reader.State(w.Campaign, "last").NoFight);
        var refused = Assert.Throws<DndInputException>(() => w.Reader.State(w.Campaign, "Wolves"));

        Assert.Equal("encounter \"Wolves\": no encounter by that name in sea. Encounters: \"Goblins\" (planned).", refused.Message);
    }

    [Fact]
    public void D20b_ATypedNameAnEntityHolds_WarnsWithWhatThePartySeesAndTheFix()
    {
        using var w = CombatWorld.OnePiece();
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Probe", AddParty = false });

        var added = w.Combat.Add(w.Campaign, null, [CombatWorld.Monster("2024", "aboleth", name: "The Nester")]);

        Assert.Equal(
            ["'The Nester' is a name the party does not use (character:the-nester): party views show this combatant as 'Aboleth'; add it with character:the-nester to show the name the party knows."],
            added.Warnings);
    }

    [Fact]
    public void D20b_AnEffectOrASpellThePartyCannotRead_WarnsAnEffect_APassingNameIsQuiet()
    {
        using var w = CombatWorld.OnePiece();
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Probe", AddParty = true });

        var effect = w.Combat.Condition(w.Campaign, null, new ConditionOp(["bjorn-mountainfell"]) { Add = ["sealed by the Nester", "Rage"] });
        var spell = w.Combat.Concentration(w.Campaign, null, new ConcentrationOp(["dragon-slayer"]) { Spell = "Nester's Ward" });
        var quiet = w.Combat.Add(w.Campaign, null, [CombatWorld.Monster("2024", "aboleth", name: "Goblin Boss")]);

        var warning = Assert.Single(effect.Warnings);
        Assert.StartsWith("'sealed by the Nester' ", warning, StringComparison.Ordinal);
        Assert.EndsWith("party views show this effect as 'an effect'.", warning, StringComparison.Ordinal);
        Assert.Contains("party views show this concentration as", Assert.Single(spell.Warnings), StringComparison.Ordinal);
        Assert.Empty(quiet.Warnings);
    }

    [Fact]
    public void D20b_RevealingATypedNameWithSet_Warns()
    {
        using var w = CombatWorld.OnePiece();
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Probe", AddParty = false, Combatants = [CombatWorld.Monster("2024", "aboleth", name: "The Nester", hidden: true)] });

        var revealed = w.Combat.Set(w.Campaign, null, new SetOp([new SetEntry("the-nester") { Hidden = false }]));

        Assert.Contains(revealed.Warnings, x => x.StartsWith("'The Nester' is a name the party does not use", StringComparison.Ordinal));
    }
}
