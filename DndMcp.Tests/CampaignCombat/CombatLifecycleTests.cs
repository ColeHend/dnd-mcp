using DndMcp.Domain.Combat;
using DndMcp.Domain.Core;
using DndMcp.Domain.Rules;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Combat;
using Microsoft.Data.Sqlite;
using Xunit;
using ES = DndMcp.Domain.Campaign.CampaignValues.EncounterStatuses;
using L = DndMcp.Domain.Campaign.CampaignValues.CombatLogKinds;

namespace DndMcp.Tests.CampaignCombat;

/// <summary>
/// Invariant: an encounter's lifecycle and addressing (contract §6.1, D13): <c>prepare</c> makes a planned fight with no
/// party; <c>start</c> makes a new active fight filed under the live session, adds the current party (D8) in name order and
/// refuses while another fight is active (checked in the transaction; the unique index's own refusal mapped to the same
/// words, never SQLITE_CONSTRAINT); <c>start</c> naming a planned fight re-seeds its sheet-seeded combatants from their
/// sheets as they are NOW; an encounter is addressed by "current", "last" or a name unique among the not-ended fights.
/// </summary>
public sealed class CombatLifecycleTests
{
    internal const string HeroJson = """{ "classes": [{ "class": "fighter", "level": 5 }], "abilities": { "con": 14, "dex": 12 }, "ac": 16, "max_hp": 44 }""";

    internal const string SidekickJson = """{ "classes": [{ "class": "rogue", "level": 5 }], "abilities": { "con": 12, "dex": 16 }, "ac": 14, "max_hp": 33 }""";

    [Fact]
    public void Prepare_MakesAPlannedFight_WithNoParty_AndItsCombatants()
    {
        using var w = CombatWorld.Dm();

        var prepared = w.Combat.Prepare(w.Campaign, new PrepareRequest("Goblin ambush") { Combatants = [CombatWorld.Monster("2024", "goblin-warrior", 3)] });

        Assert.True(prepared.Created);
        var row = w.Encounter("Goblin ambush");
        Assert.Equal((ES.Planned, 0L, "2024", (string?)null), (row.Status, row.Round, row.Ruleset, row.SessionId));
        Assert.Equal(["Goblin Warrior", "Goblin Warrior 2", "Goblin Warrior 3"], w.State("Goblin ambush").Combatants.Select(c => c.Name));
        Assert.Null(w.Reader.State(w.Campaign).Encounter);
    }

    [Fact]
    public void Start_NewFight_ActiveRound0_FiledUnderTheLiveSession_PartyInNameOrder_DeadLeftOutAndNamed()
    {
        using var w = CombatWorld.Dm();
        w.Sheet("character:hero", HeroJson);
        w.StartSession(1);
        w.Reload();

        var started = w.Combat.Start(w.Campaign, new StartRequest { Name = "Bridge fight" });

        var row = w.Encounter("Bridge fight");
        Assert.Equal((ES.Active, 0L), (row.Status, row.Round));
        Assert.Equal(w.Id("session:1"), row.SessionId);
        Assert.Equal(["Hero Prime", "Sidekick"], w.State("Bridge fight").Combatants.Select(c => c.Name));
        Assert.True(w.Combatant("Bridge fight", "Hero Prime").IsSheetSeeded);
        Assert.False(w.Combatant("Bridge fight", "Sidekick").HpKnown);
        Assert.Contains(started.Lines, l => l.Contains("Fallen (dead)", StringComparison.Ordinal));
        Assert.Contains(started.Lines, l => l.Contains("No sheet, so no hit points: Sidekick", StringComparison.Ordinal));
        Assert.Equal([L.Start, L.Add, L.Add], w.Log("Bridge fight").Select(r => r.Kind));
    }

    /// <summary>
    /// C04: a member that start added without a sheet (no hit points) and that has one now is re-seeded from it by
    /// add {character}, the call start's note prints: the same combatant, no second one, its add row says reseeded, the
    /// encounter simulation includes it, and its fight reaches its sheet at end. One that left re-joins re-seeded.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Add_AMemberThatJoinedWithoutASheet_HasOneNow_IsReseededFromIt_TheCallStartsNotePrints(bool leftFirst)
    {
        using var w = CombatWorld.Dm();
        w.Sheet("character:hero", HeroJson);
        var started = w.Combat.Start(w.Campaign, new StartRequest { Name = "Bridge fight", Combatants = [CombatWorld.Monster("2024", "ogre", hp: HpChoice.Avg)] });
        var note = Assert.Single(started.Lines, l => l.StartsWith("No sheet, so no hit points: Sidekick.", StringComparison.Ordinal));
        var call = System.Text.RegularExpressions.Regex.Match(note, @"with combat (\{.*\});").Groups[1].Value;
        Assert.Equal("{\"action\": \"add\", \"combatants\": [{\"character\": \"character:sidekick\"}], \"campaign\": \"sea\"}", call);
        var id = w.Combatant("Bridge fight", "Sidekick").Id;
        if (leftFirst)
        {
            w.Combat.Leave(w.Campaign, null, new LeaveOp(["sidekick"]));
        }

        w.Sheet("character:sidekick", SidekickJson);
        var added = w.Combat.Add(w.Campaign, null, [new CombatantRequest { Character = "character:sidekick" }]);

        var sidekick = w.Combatant("Bridge fight", "Sidekick");
        Assert.Equal((id, true, 33, false), (sidekick.Id, sidekick.IsSheetSeeded, sidekick.Hp, sidekick.Removed));
        Assert.Equal(3, w.State("Bridge fight").Combatants.Count);
        Assert.Contains("Sidekick: re-seeded from its sheet as it is now.", added.Lines);
        Assert.Contains(w.Log("Bridge fight"), r => r.Kind == L.Add && r.TargetId == id && r.Detail!.Contains("\"reseeded\":true", StringComparison.Ordinal));
        Assert.DoesNotContain(w.Loader.Load(w.Campaign).Notes, n => n.Contains("Sidekick is left out", StringComparison.Ordinal));

        w.Combat.Damage(w.Campaign, null, new DamageOp(["sidekick"]) { Amount = 5 });
        w.Combat.End(w.Campaign, null, new EndRequest { Xp = 0 });
        Assert.Equal(28, w.SheetOf("character:sidekick").Hp);
    }

    /// <summary>
    /// CR01 (F2, contract §6.2): re-seeding a member already in the RUNNING fight takes from its new sheet only what the
    /// unseeded combatant lacked (HP, maximum, temporary HP, AC, death saves, exhaustion, resources, slots, the snapshot)
    /// and keeps everything the fight gave it: its initiative, the poison a goblin put on it, its concentration on Hold
    /// Person (which wins over the sheet's Bless) and the paralysis that concentration holds on the goblin. The sheet's
    /// own persisting conditions join only where the combatant lacks them (blinded joins; a second poisoned does not).
    /// Before, the re-seed replaced the whole combatant: the hold stayed on the goblin, orphaned, so dropping the
    /// concentration was refused, damage prompted no save, leaving did not end it, and the from_state simulation was refused.
    /// </summary>
    [Theory]
    [InlineData("drop")]
    [InlineData("damage")]
    [InlineData("leave")]
    [InlineData("from_state")]
    public void Add_ReseedInARunningFight_KeepsWhatTheFightGaveIt_TheHoldStillWorks(string then)
    {
        using var w = CombatWorld.Dm();
        w.Sheet("character:hero", HeroJson);
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Bridge fight", Combatants = [CombatWorld.Monster("2024", "goblin-warrior", hp: HpChoice.Avg)] });
        w.Combat.Initiative(w.Campaign, null, new InitiativeOp { Rolls = [new("hero", Total: 15), new("sidekick", Total: 12), new("goblin-warrior", Total: 10)] });
        w.Combat.Set(w.Campaign, null, new SetOp([new SetEntry("sidekick") { Hp = 30 }]));
        w.Combat.Concentration(w.Campaign, null, new ConcentrationOp(["sidekick"]) { Spell = "Hold Person", Duration = "1 minute" });
        w.Combat.Condition(w.Campaign, null, new ConditionOp(["goblin-warrior"]) { Add = ["paralyzed"], Source = "sidekick", Duration = "concentration", Dc = 13, Ability = "wis" });
        w.Combat.Condition(w.Campaign, null, new ConditionOp(["sidekick"]) { Add = ["poisoned"], Source = "goblin-warrior", Duration = "1 minute" });
        w.Sheet("character:sidekick", SidekickJson);
        w.Characters.Condition(w.Campaign, "character:sidekick", ["poisoned", "blinded"], null, null, Repository.Campaign.Write.WriteContext.Default);
        using (var connection = w.Open())
        {
            Dapper.SqlMapper.Execute(connection, "UPDATE character_sheet SET concentration = '{\"spell\":\"Bless\",\"level\":1}' WHERE entity_id = @id",
                new { id = w.Id("character:sidekick") });
        }

        var added = w.Combat.Add(w.Campaign, null, [new CombatantRequest { Character = "character:sidekick" }]);

        var sidekick = w.Combatant("Bridge fight", "Sidekick");
        Assert.Contains("Sidekick: re-seeded from its sheet as it is now; kept the fight's HP 30 (the sheet says 33/33).", added.Lines);
        Assert.Equal((true, 30, 33, 14, 12d), (sidekick.IsSheetSeeded, sidekick.Hp, sidekick.MaxHp, sidekick.Ac, sidekick.Initiative));
        Assert.Equal("Hold Person", sidekick.Concentration?.Spell);
        Assert.Equal([("poisoned", "Goblin Warrior"), ("blinded", null)], sidekick.Conditions.Select(c => (c.Name, Source(w, c))));
        Assert.Equal(["poisoned"], sidekick.SheetSnapshot!.Shadowed);
        Assert.True(Paralyzed(w));
        switch (then)
        {
            case "drop":
                w.Combat.Concentration(w.Campaign, null, new ConcentrationOp(["sidekick"]) { Drop = true });
                Assert.False(Paralyzed(w));
                break;
            case "damage":
                var hit = w.Combat.Damage(w.Campaign, null, new DamageOp(["sidekick"]) { Amount = 12 });
                Assert.Contains(hit.Reminders, r => r.Kind == CombatValues.ReminderKinds.ConcentrationSave && r.CombatantId == sidekick.Id);
                break;
            case "leave":
                w.Combat.Leave(w.Campaign, null, new LeaveOp(["sidekick"]));
                Assert.False(Paralyzed(w));
                break;
            default:
                var sim = w.Loader.Load(w.Campaign, fromState: true);
                var report = DndMcp.Domain.Simulation.Simulator.Run(new DndMcp.Domain.Simulation.SimulationSpec
                {
                    Party = sim.Party, Enemies = sim.Enemies, Resume = sim.Resume, Lair = sim.Lair, Edition = "2024", Iterations = 40,
                }, 7);
                Assert.True(report.Resumed);
                break;
        }

        static bool Paralyzed(CombatWorld w) => w.Combatant("Bridge fight", "Goblin Warrior").Has("paralyzed");

        static string? Source(CombatWorld w, CombatCondition c) =>
            c.Source is { } id ? w.State("Bridge fight").Find(id)?.Name : c.SourceNote;
    }

    // The F2R03 world: Sidekick in a running fight with no sheet, the goblin to act second; then its sheet (33/33).
    private static CombatWorld UnseededSidekick(int? hp = null)
    {
        var w = CombatWorld.Dm();
        w.Sheet("character:hero", HeroJson);
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Bridge fight", Combatants = [CombatWorld.Monster("2024", "goblin-warrior", hp: HpChoice.Avg)] });
        w.Combat.Initiative(w.Campaign, null, new InitiativeOp { Rolls = [new("sidekick", Total: 15), new("goblin-warrior", Total: 12), new("hero", Total: 10)] });
        if (hp is { } given)
        {
            w.Combat.Set(w.Campaign, null, new SetOp([new SetEntry("sidekick") { Hp = given }]));
        }

        return w;
    }

    /// <summary>
    /// F2R03 (contract §6.2, F3): hit points the fight tracks for a combatant re-seeded in a RUNNING fight (given by set,
    /// and the damage since) are the fight's, at most the sheet's effective maximum, and the line says what the sheet said;
    /// they used to be replaced by the sheet's with only "re-seeded from its sheet as it is now". The fight's damage then
    /// reaches the sheet at end. With no hit points tracked, the sheet's are taken, as before.
    /// </summary>
    [Theory]
    [InlineData(30, 7, false, 23, "kept the fight's HP 23 (the sheet says 33/33)")]
    [InlineData(40, 0, false, 33, "kept the fight's HP 33, cut from 40 to the sheet's maximum (the sheet says 33/33)")]
    [InlineData(30, 7, true, 23, "kept the fight's HP 23 (the sheet says 0/33)")]
    [InlineData(null, 7, false, 33, null)]
    public void Add_ReseedInARunningFight_TheFightsHitPointsAreKept_WithinTheSheetsMaximum(int? set, int damage, bool sheetDown, int hp, string? kept)
    {
        // A sheet at 0 HP seeds the tracker's down state; with the fight's 23 hit points kept, it does not come in with them.
        using var w = UnseededSidekick(set);
        if (damage > 0)
        {
            w.Combat.Damage(w.Campaign, null, new DamageOp(["sidekick"]) { Amount = damage, Source = "goblin-warrior" });
        }

        w.Sheet("character:sidekick", sheetDown ? SidekickJson.Replace("\"max_hp\": 33", "\"max_hp\": 33, \"hp\": 0", StringComparison.Ordinal) : SidekickJson);
        var added = w.Combat.Add(w.Campaign, null, [new CombatantRequest { Character = "character:sidekick" }]);

        var sidekick = w.Combatant("Bridge fight", "Sidekick");
        Assert.Equal((hp, 33), (sidekick.Hp!.Value, sidekick.MaxHp!.Value));
        Assert.Contains("Sidekick: re-seeded from its sheet as it is now" + (kept is null ? "." : $"; {kept}."), added.Lines);
        Assert.Equal((false, false, false), (sidekick.Has("unconscious"), sidekick.Has("prone"), sidekick.Dying));
        w.Combat.End(w.Campaign, null, new EndRequest { Xp = 0 });
        Assert.Equal(hp, w.SheetOf("character:sidekick").Hp);
    }

    /// <summary>
    /// F2R03: a member that went down in the fight before it had a sheet stays down when it is re-seeded: 0 HP, dying, its
    /// death-save tallies and the tracker's down state (Unconscious until it regains hit points, Prone until it stands),
    /// so a heal wakes it. It used to come back at 44/44 still unconscious and prone with its tallies reset: a creature
    /// that never acts, that no heal could wake (0 regained), simulated as dealing nothing.
    /// </summary>
    [Fact]
    public void Add_ReseedInARunningFight_ADownMemberStaysDown_WithItsTallies_AHealWakesIt()
    {
        using var w = UnseededSidekick(20);
        w.Combat.Damage(w.Campaign, null, new DamageOp(["sidekick"]) { Amount = 20, Source = "goblin-warrior" });
        w.Combat.DeathSave(w.Campaign, null, new DeathSaveOp(["sidekick"]) { Face = 4 });
        w.Sheet("character:sidekick", SidekickJson);

        var added = w.Combat.Add(w.Campaign, null, [new CombatantRequest { Character = "character:sidekick" }]);

        var sidekick = w.Combatant("Bridge fight", "Sidekick");
        Assert.Contains("Sidekick: re-seeded from its sheet as it is now; kept the fight's HP 0 (the sheet says 33/33): it stays down (0 successes, 1 failure).", added.Lines);
        Assert.Equal((0, 33, true, new DeathSaveTally(0, 1, false)), (sidekick.Hp!.Value, sidekick.MaxHp!.Value, sidekick.Dying, sidekick.DeathSaves));
        Assert.Equal([("unconscious", CombatValues.Durations.ZeroHp), ("prone", CombatValues.Durations.UntilStands)], sidekick.Conditions.Select(c => (c.Name, c.Duration)));

        w.Combat.Heal(w.Campaign, null, new HealOp(["sidekick"]) { Amount = 3 });

        var healed = w.Combatant("Bridge fight", "Sidekick");
        Assert.Equal((3, false, true), (healed.Hp!.Value, healed.Has("unconscious"), healed.Has("prone")));
    }

    /// <summary>
    /// F2R03: a condition the sheet stores (poisoned, until removed) that the fight's condition of the same name shadows at
    /// the re-seed (poisoned by the goblin, 1 minute) survives the write-back exactly as stored: its until_removed
    /// persistence wins, and the summary says the fight's ended while the sheet's stays. It used to be dropped from the
    /// sheet at end. A condition remove of that name in the fight ends the sheet's too.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Add_ReseedInARunningFight_AShadowedSheetCondition_SurvivesTheEnd_UnlessTheFightRemovesIt(bool removed)
    {
        using var w = UnseededSidekick();
        w.Combat.Condition(w.Campaign, null, new ConditionOp(["sidekick"]) { Add = ["poisoned"], Source = "goblin-warrior", Duration = "1 minute" });
        w.Sheet("character:sidekick", SidekickJson);
        w.Characters.Condition(w.Campaign, "character:sidekick", ["poisoned", "blinded"], null, null, Repository.Campaign.Write.WriteContext.Default);
        var stored = w.SheetOf("character:sidekick").Conditions;
        w.Combat.Add(w.Campaign, null, [new CombatantRequest { Character = "character:sidekick" }]);
        Assert.Equal(["poisoned", "blinded"], w.Combatant("Bridge fight", "Sidekick").Conditions.Select(c => c.Name));
        if (removed)
        {
            w.Combat.Condition(w.Campaign, null, new ConditionOp(["sidekick"]) { Remove = ["poisoned"] });
            Assert.Empty(w.Combatant("Bridge fight", "Sidekick").SheetSnapshot!.Shadowed);
        }

        var end = w.Combat.End(w.Campaign, null, new EndRequest { Xp = 0 });

        var after = w.SheetOf("character:sidekick").Conditions;
        Assert.Equal(removed ? stored.Skip(1).ToList() : stored.ToList(), after.ToList());
        Assert.Equal(!removed, end.Summary.Contains("Ended with the fight: poisoned (Sidekick; the sheet's own poisoned stays)."));
    }

    /// <summary>
    /// CR05 (C02's class, for encounter names): a name with no letter or digit has no key, so a planned fight named "!!!"
    /// could never be started, ended or discarded by name, and blocked every other keyless name. prepare and start refuse it
    /// with C02's shared phrase, writing nothing.
    /// </summary>
    [Theory]
    [InlineData("!!!")]
    [InlineData("???")]
    [InlineData("—")]
    [InlineData("… ·")]
    public void PrepareOrStart_ANameWithNoLetterOrDigit_IsRefused_WritingNothing(string name)
    {
        using var w = CombatWorld.Dm();
        var encounters = w.F.Count("SELECT count(*) FROM encounter");

        var prepare = Assert.Throws<DndInputException>(() => w.Combat.Prepare(w.Campaign, new PrepareRequest(name) { Combatants = [CombatWorld.Monster("2024", "goblin-warrior")] }));
        var start = Assert.Throws<DndInputException>(() => w.Combat.Start(w.Campaign, new StartRequest { Name = name }));

        Assert.Equal($"name \"{name}\": give name a name with a letter or digit.", prepare.Message);
        Assert.Equal(prepare.Message, start.Message);
        Assert.Equal(encounters, w.F.Count("SELECT count(*) FROM encounter"));
    }

    [Fact]
    public void Add_AReseedThatAlsoGivesOtherFields_IsRefused_WritingNothing()
    {
        using var w = CombatWorld.Dm();
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Bridge fight" });
        w.Sheet("character:sidekick", SidekickJson);
        var log = w.Log("Bridge fight").Count;

        var refused = Assert.Throws<DndInputException>(() => w.Combat.Add(w.Campaign, null, [new CombatantRequest { Character = "character:sidekick", Ac = 20 }]));

        Assert.Equal(
            "combatants item 1: character:sidekick is in the fight as Sidekick with no sheet; adding it again re-seeds it from the sheet it has now, " +
            "which gives all of it: give only character (change the rest with set afterwards).", refused.Message);
        Assert.Equal(log, w.Log("Bridge fight").Count);
        Assert.False(w.Combatant("Bridge fight", "Sidekick").IsSheetSeeded);
    }

    [Fact]
    public void Start_WhileAnotherFightIsActive_IsRefusedNamingIt_WritingNothing()
    {
        using var w = CombatWorld.Dm();
        w.Combat.Start(w.Campaign, new StartRequest { Name = "First", AddParty = false });
        var encounters = w.F.Count("SELECT count(*) FROM encounter");

        var refused = Assert.Throws<DndInputException>(() => w.Combat.Start(w.Campaign, new StartRequest { Name = "Second", AddParty = false }));

        Assert.Equal("\"First\" is already running in sea (one fight at a time): end it first with combat {\"action\": \"end\", \"encounter\": \"First\", \"campaign\": \"sea\"}, then start this one.", refused.Message);
        Assert.Equal(encounters, w.F.Count("SELECT count(*) FROM encounter"));
    }

    [Fact]
    public void PrintedCalls_NameTheFightWhole_AQuoteOrALongNameStillParses()
    {
        using var w = CombatWorld.Dm();
        const string name = "The \"long\" night at the harbor, when the bells rang for an hour and nobody came";

        var prepared = w.Combat.Prepare(w.Campaign, new PrepareRequest(name));
        w.Combat.Start(w.Campaign, new StartRequest { Encounter = name, AddParty = false });
        var refused = Assert.Throws<DndInputException>(() => w.Combat.Start(w.Campaign, new StartRequest { Name = "Another", AddParty = false }));

        foreach (var text in new[] { prepared.Lines[0], refused.Message })
        {
            var call = System.Text.Json.Nodes.JsonNode.Parse(System.Text.RegularExpressions.Regex.Match(text, @"combat (\{.*\})").Groups[1].Value)!;
            Assert.Equal((name, "sea"), (call["encounter"]!.GetValue<string>(), call["campaign"]!.GetValue<string>()));
        }
    }

    [Fact]
    public void Start_TheUniqueIndexRefusesASecondActiveFight_MappedToTheSameRefusal_NeverSqliteConstraint()
    {
        using var w = CombatWorld.Dm();
        w.Combat.BeforeActivate = (connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO encounter (id, campaign_id, name, ruleset, status, created_at, updated_at) VALUES ('race', $c, 'Raced in', '2024', 'active', 'x', 'x')";
            command.Parameters.AddWithValue("$c", w.Campaign.Id);
            command.ExecuteNonQuery();
        };

        var refused = Assert.Throws<DndInputException>(() => w.Combat.Start(w.Campaign, new StartRequest { Name = "Mine", AddParty = false }));

        Assert.StartsWith("\"Raced in\" is already running in sea (one fight at a time)", refused.Message, StringComparison.Ordinal);
        Assert.IsNotType<SqliteException>(refused.InnerException);
        Assert.Equal(0L, w.F.Count("SELECT count(*) FROM encounter"));
    }

    [Fact]
    public void Start_APlannedFight_ReseedsItsSheetSeededCombatantsFromTheSheetsAsTheyAreNow()
    {
        using var w = CombatWorld.Dm();
        w.Sheet("character:hero", HeroJson);
        w.Combat.Prepare(w.Campaign, new PrepareRequest("Later") { Combatants = [new CombatantRequest { Character = "character:hero" }] });
        Assert.Equal(44, w.Combatant("Later", "Hero Prime").Hp);
        w.Characters.Damage(w.Campaign, "character:hero", 10, null, Repository.Campaign.Write.WriteContext.Default);

        w.Combat.Start(w.Campaign, new StartRequest { Encounter = "Later", AddParty = false });

        var hero = w.Combatant("Later", "Hero Prime");
        Assert.Equal(34, hero.Hp);
        Assert.Equal(34L, hero.SheetSnapshot!.Column("hp"));
        Assert.Equal(ES.Active, w.Encounter("Later").Status);
        Assert.Contains(w.Log("Later"), r => r.Kind == L.Add && r.TargetId == hero.Id && r.Detail!.Contains("\"reseeded\":true", StringComparison.Ordinal));
    }

    [Fact]
    public void Start_APlannedFight_AStatBlockNpcWithASheet_IsNotReseeded_ItKeepsItsStatBlock()
    {
        using var w = CombatWorld.Dm();
        w.Sheet("character:hero", HeroJson);
        w.Combat.Prepare(w.Campaign, new PrepareRequest("Later") { Combatants = [CombatWorld.Monster("2024", "ogre", character: "character:hero", hp: HpChoice.Avg, side: "ally")] });
        var prepared = w.Combatant("Later", "Ogre");

        w.Combat.Start(w.Campaign, new StartRequest { Encounter = "Later", AddParty = false });

        var ogre = w.Combatant("Later", "Ogre");
        Assert.False(ogre.IsSheetSeeded);
        Assert.Equal("Ogre", ogre.StatBlock!.Name);
        Assert.Equal((prepared.Hp, prepared.MaxHp, prepared.Ac), (ogre.Hp, ogre.MaxHp, ogre.Ac));
        Assert.DoesNotContain(w.Log("Later"), r => r.Detail?.Contains("\"reseeded\":true", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Start_APlannedFight_IsFiledUnderTheLiveSession()
    {
        using var w = CombatWorld.Dm();
        w.Combat.Prepare(w.Campaign, new PrepareRequest("Later"));
        w.StartSession(1);
        w.Reload();

        w.Combat.Start(w.Campaign, new StartRequest { Encounter = "Later", AddParty = false });

        Assert.Equal((ES.Active, w.Id("session:1")), (w.Encounter("Later").Status, w.Encounter("Later").SessionId));
    }

    [Theory]
    [InlineData(false, true, 4)]
    [InlineData(true, false, 3)]
    [InlineData(true, null, 4)]
    [InlineData(false, null, 3)]
    public void Start_APlannedFight_ALairChangeRecountsTheLegendaryUses(bool preparedInLair, bool? startedInLair, int uses)
    {
        using var w = CombatWorld.Dm();
        w.Combat.Prepare(w.Campaign, new PrepareRequest("Lair") { Lair = preparedInLair, Combatants = [CombatWorld.Monster("2024", "aboleth", hp: HpChoice.Avg)] });
        var prepared = w.Combatant("Lair", "Aboleth").Legendary!;
        Assert.Equal(preparedInLair ? (4, 4) : (3, 3), (prepared.Actions, prepared.Resistance));

        w.Combat.Start(w.Campaign, new StartRequest { Encounter = "Lair", AddParty = false, Lair = startedInLair });

        var started = w.Combatant("Lair", "Aboleth").Legendary!;
        Assert.Equal((uses, uses, 0, 0), (started.Actions, started.Resistance, started.Used, started.ResistanceUsed));
        Assert.Equal(startedInLair ?? preparedInLair, w.Encounter("Lair").Lair != 0);
    }

    [Fact]
    public void Start_NamingAPlannedFightByName_ActivatesIt_NotANewOne()
    {
        using var w = CombatWorld.Dm();
        w.Combat.Prepare(w.Campaign, new PrepareRequest("Goblins"));

        var started = w.Combat.Start(w.Campaign, new StartRequest { Name = "goblins", AddParty = false });

        Assert.False(started.Created);
        Assert.Equal(1L, w.F.Count("SELECT count(*) FROM encounter"));
        Assert.Equal(ES.Active, w.Encounter("Goblins").Status);
    }

    [Theory]
    [InlineData("current")]
    [InlineData("last")]
    [InlineData("  ")]
    public void Prepare_AReservedOrBlankName_IsRefused(string name)
    {
        using var w = CombatWorld.Dm();

        var refused = Assert.Throws<DndInputException>(() => w.Combat.Prepare(w.Campaign, new PrepareRequest(name)));

        Assert.StartsWith("name ", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_ANameANotEndedFightHas_IsRefused_AnEndedOnesNameIsFree()
    {
        using var w = CombatWorld.Dm();
        w.Combat.Prepare(w.Campaign, new PrepareRequest("Goblins"));

        Assert.Contains("already the name of a planned fight", Assert.Throws<DndInputException>(() => w.Combat.Prepare(w.Campaign, new PrepareRequest("GOBLINS"))).Message,
            StringComparison.Ordinal);
        w.Combat.End(w.Campaign, "Goblins", new EndRequest());
        w.Combat.Prepare(w.Campaign, new PrepareRequest("Goblins"));
        Assert.Equal(2L, w.F.Count("SELECT count(*) FROM encounter WHERE name = 'Goblins'"));
    }

    [Fact]
    public void Resolve_CurrentLastAndNames_ANameOfSeveralEndedFightsMeansTheLatest()
    {
        using var w = CombatWorld.Dm();
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Rats", AddParty = false });
        w.Combat.End(w.Campaign, null, new EndRequest());
        w.F.Db.Time.Advance(TimeSpan.FromMinutes(5));
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Rats", AddParty = false, Combatants = [new CombatantRequest { Name = "Big rat", Hp = HpChoice.Of(5) }] });
        w.Combat.End(w.Campaign, null, new EndRequest());
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Now", AddParty = false });

        using var connection = w.Open();
        Assert.Equal("Now", EncounterResolver.Resolve(connection, w.Campaign, null).Name);
        Assert.Equal("Now", EncounterResolver.Resolve(connection, w.Campaign, "Current").Name);
        var last = EncounterResolver.Resolve(connection, w.Campaign, "last");
        Assert.Equal("Rats", last.Name);
        Assert.Equal(last.Id, EncounterResolver.Resolve(connection, w.Campaign, "rats").Id);
        Assert.Single(w.State("Rats").Combatants);
        var refused = Assert.Throws<DndInputException>(() => EncounterResolver.Resolve(connection, w.Campaign, "Wolves"));
        Assert.Equal("encounter \"Wolves\": no encounter by that name in sea. Encounters: \"Now\" (active), \"Rats\" (ended), \"Rats\" (ended).", refused.Message);
    }

    [Fact]
    public void Step_WithNoFightRunning_IsRefusedWithTheStartCall()
    {
        using var w = CombatWorld.Dm();

        var refused = Assert.Throws<DndInputException>(() => w.Combat.Next(w.Campaign, null));

        // C17: the name is the bare placeholder, so the call cannot be sent unfilled (a quoted "…" would start a fight
        // named "…"); the campaign is named last.
        Assert.Equal("No combat is running in sea: start one with combat {\"action\": \"start\", \"name\": …, \"campaign\": \"sea\"}.", refused.Message);
    }

    [Fact]
    public void Start_Surprised_MarksThemAfterTheAdds()
    {
        using var w = CombatWorld.Dm();

        w.Combat.Start(w.Campaign, new StartRequest { Name = "Ambush", AddParty = false, Combatants = [CombatWorld.Monster("2024", "goblin-warrior")], Surprised = ["goblin-warrior"] });

        Assert.True(w.Combatant("Ambush", "Goblin Warrior").Surprised);
    }

    [Fact]
    public void Start_InAMixedCampaign_NeedsAnEdition()
    {
        using var w = CombatWorld.Dm();
        using (var connection = w.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE campaign SET ruleset = 'mixed'";
            command.ExecuteNonQuery();
        }

        w.Reload();

        Assert.StartsWith("edition is required: sea mixes", Assert.Throws<DndInputException>(() => w.Combat.Start(w.Campaign, new StartRequest { AddParty = false })).Message,
            StringComparison.Ordinal);
        w.Combat.Start(w.Campaign, new StartRequest { AddParty = false, Edition = "2014" });
        Assert.Equal("2014", w.Encounter("Fight 1").Ruleset);
    }

    [Fact]
    public void MonsterEdition_TheFightsRulesetForAdd_APlannedFightsForStart_ElseTheNewFightsEdition()
    {
        using var w = CombatWorld.Dm();
        w.Combat.Prepare(w.Campaign, new PrepareRequest("Old rules") { Edition = "2014" });

        Assert.Equal("2014", w.Combat.MonsterEdition(w.Campaign, CombatActions.Start, encounter: "Old rules"));
        Assert.Equal("2014", w.Combat.MonsterEdition(w.Campaign, CombatActions.Start, name: "old rules"));
        Assert.Equal("2024", w.Combat.MonsterEdition(w.Campaign, CombatActions.Start, name: "A new one"));
        Assert.Equal("2014", w.Combat.MonsterEdition(w.Campaign, CombatActions.Prepare, edition: "2014"));
        Assert.Equal("2014", w.Combat.MonsterEdition(w.Campaign, CombatActions.Add, encounter: "Old rules"));
        Assert.Throws<DndInputException>(() => w.Combat.MonsterEdition(w.Campaign, CombatActions.Add));
        Assert.Equal(["prepare", "start", "add"], new[] { CombatActions.Prepare, CombatActions.Start, CombatActions.Add });
        Assert.Equal(17, CombatActions.All.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Start_NamingAnEndedFightAsTheEncounter_IsRefused_ItsNameIsFreeForANewOne()
    {
        using var w = CombatWorld.Dm();
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Rats", AddParty = false });
        w.Combat.End(w.Campaign, null, new EndRequest());

        var refused = Assert.Throws<DndInputException>(() => w.Combat.Start(w.Campaign, new StartRequest { Encounter = "Rats", AddParty = false }));

        Assert.Equal("\"Rats\" has ended and cannot run again; start a new fight with name (an ended fight's name may be used again).", refused.Message);
        Assert.True(w.Combat.Start(w.Campaign, new StartRequest { Name = "Rats", AddParty = false }).Created);
    }
}
