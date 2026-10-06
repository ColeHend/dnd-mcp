using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign.Characters;
using DndMcp.Repository.Campaign.Write;
using Xunit;

namespace DndMcp.Tests.CampaignCharacters;

/// <summary>
/// Damage, healing and temporary hit points on a sheet out of combat (contract §7.1, §5.1-§5.8): R's rules on the
/// sheet's state, its defenses and persisted effects, dropping to 0 and dying, concentration, and the refusals.
/// </summary>
public sealed class CharacterWriterHitPointTests
{
    private const string Fighter = """{ "classes": [{ "class": "fighter", "level": 5 }], "abilities": { "con": 14 }, "ac": 18 }""";

    private static SheetWorld World(string json = Fighter, string ruleset = "2024")
    {
        var world = SheetWorld.Dm(ruleset);
        world.Update("character:hero", json);
        return world;
    }

    [Fact]
    public void Damage_Untyped_LowersHpInOneLoggedBatch()
    {
        using var world = World();
        var max = world.Required("character:hero").MaxHp!.Value;

        var result = world.Writer.Damage(world.Campaign, "character:hero", 10, null, WriteContext.Default);

        Assert.Equal(max - 10, world.Required("character:hero").Hp);
        var row = Assert.Single(world.Log(result.BatchId));
        Assert.Equal("hp", row.FieldPath);
        Assert.Equal("campaign_character/damage", row.Tool);
        Assert.Contains(result.Notes, n => n.Contains("→", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("resist", "fire", 28, 14)]
    [InlineData("immune", "fire", 28, 0)]
    [InlineData("vulnerable", "fire", 13, 26)]
    [InlineData("resist", "cold", 28, 28)]
    public void Damage_SheetDefenses_Apply(string kind, string type, int amount, int expectedLoss)
    {
        using var world = World($$"""{ "level": 20, "max_hp": 200, "defenses": { "{{kind}}": ["fire"] } }""");

        world.Writer.Damage(world.Campaign, "character:hero", amount, type, WriteContext.Default);

        Assert.Equal(200 - expectedLoss, world.Required("character:hero").Hp);
    }

    [Fact]
    public void Damage_PersistedEffectResistsAllExceptPsychic_HalvesFireNotPsychic()
    {
        using var world = World("""{ "level": 8, "max_hp": 85 }""");
        using (var connection = world.Open())
        {
            Dapper.SqlMapper.Execute(connection,
                "UPDATE character_sheet SET conditions = '[{\"name\":\"Rage\",\"duration\":\"until_removed\",\"effect\":{\"resist\":[\"all\"],\"except\":[\"psychic\"]}}]' WHERE entity_id = @id",
                new { id = world.Id("character:hero") });
        }

        world.Writer.Damage(world.Campaign, "character:hero", 10, "fire", WriteContext.Default);
        Assert.Equal(80, world.Required("character:hero").Hp);
        world.Writer.Damage(world.Campaign, "character:hero", 10, "psychic", WriteContext.Default);
        Assert.Equal(70, world.Required("character:hero").Hp);
    }

    [Fact]
    public void Damage_TemporaryHitPointsFirst_ThenHp()
    {
        using var world = World("""{ "level": 5, "max_hp": 40, "temp_hp": 7 }""");

        var result = world.Writer.Damage(world.Campaign, "character:hero", 10, null, WriteContext.Default);

        var sheet = world.Required("character:hero");
        Assert.Equal(0, sheet.TempHp);
        Assert.Equal(37, sheet.Hp);
        Assert.Equal(["hp", "temp_hp"], world.Log(result.BatchId).Select(r => r.FieldPath).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A drop to 0 adds Unconscious (until removed, which a heal ends) and never Prone (F2, review UR03): nothing on a sheet
    /// would ever end a stored Prone, so a character who stood up hours ago entered the next fight prone. A fight seeds the
    /// sheet's Unconscious at 0 HP as the tracker's down state, Prone until it stands included (§6.2).
    /// </summary>
    [Fact]
    public void Damage_DropToZero_AddsUnconsciousNeverProne_AndEndsConcentration()
    {
        using var world = World("""{ "level": 5, "max_hp": 30 }""");
        using (var connection = world.Open())
        {
            Dapper.SqlMapper.Execute(connection, "UPDATE character_sheet SET concentration = '{\"spell\":\"Bless\",\"level\":1}' WHERE entity_id = @id",
                new { id = world.Id("character:hero") });
        }

        var result = world.Writer.Damage(world.Campaign, "character:hero", 35, null, WriteContext.Default);

        var sheet = world.Required("character:hero");
        Assert.Equal(0, sheet.Hp);
        Assert.Equal([("unconscious", "until_removed")], sheet.Conditions.Select(c => (c.Name, c.Duration)));
        Assert.Contains("Added unconscious (until removed).", result.Notes);
        Assert.DoesNotContain(result.Notes, n => n.Contains("prone", StringComparison.OrdinalIgnoreCase));
        Assert.Null(sheet.Concentration);
        Assert.Contains("Concentration on Bless ended.", result.Notes);
        Assert.Contains(result.Notes, n => n.Contains("is dying at 0 HP", StringComparison.Ordinal));
        Assert.True(sheet.IsDying("2024"));
    }

    [Fact]
    public void Damage_MassiveDamage_KillsWithTheStatusProposal()
    {
        using var world = World("""{ "level": 5, "max_hp": 30 }""");

        var result = world.Writer.Damage(world.Campaign, "character:hero", 60, null, WriteContext.Default);

        var sheet = world.Required("character:hero");
        Assert.Equal(3, sheet.DeathSaves.Failures);
        Assert.True(sheet.IsDead("2024"));
        var died = Assert.Single(result.Reminders, r => r.Kind == "died");
        Assert.Equal("campaign_write {\"ops\": [{\"op\": \"status\", \"ref\": \"character:hero\", \"status\": \"dead\"}], \"dry_run\": true, \"campaign\": \"sea\"}", died.Call);
        Assert.Equal("alive", world.F.Entity(world.Campaign, "character:hero").Status ?? "alive");
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public void Damage_AtZeroHp_AddsOneDeathSaveFailurePerHit(int hits, int failures)
    {
        using var world = World("""{ "level": 5, "max_hp": 30 }""");
        world.Writer.Damage(world.Campaign, "character:hero", 30, null, WriteContext.Default);

        for (var i = 0; i < hits; i++)
        {
            world.Writer.Damage(world.Campaign, "character:hero", 3, null, WriteContext.Default);
        }

        Assert.Equal(failures, world.Required("character:hero").DeathSaves.Failures);
    }

    [Fact]
    public void Damage_Concentrating_GivesTheSaveDcAndTheCallThatEndsIt()
    {
        using var world = World("""{ "classes": [{ "class": "wizard", "level": 5 }], "abilities": { "con": 14 }, "max_hp": 40 }""");
        using (var connection = world.Open())
        {
            Dapper.SqlMapper.Execute(connection, "UPDATE character_sheet SET concentration = '{\"spell\":\"Fly\",\"level\":3}' WHERE entity_id = @id",
                new { id = world.Id("character:hero") });
        }

        var result = world.Writer.Damage(world.Campaign, "character:hero", 30, null, WriteContext.Default);

        var save = Assert.Single(result.Reminders, r => r.Kind == "concentration_save");
        Assert.Contains("DC 15", save.Text, StringComparison.Ordinal);
        Assert.Contains("save bonus +2", save.Text, StringComparison.Ordinal);
        Assert.Equal("campaign_character {\"action\": \"condition\", \"character\": \"character:hero\", \"remove\": [\"concentration\"], \"campaign\": \"sea\"}", save.Call);
        Assert.NotNull(world.Required("character:hero").Concentration);

        world.Writer.Condition(world.Campaign, "character:hero", null, ["concentration"], null, WriteContext.Default);
        Assert.Null(world.Required("character:hero").Concentration);
    }

    [Theory]
    [InlineData("2024", 0, "roll 1d20+2 (save bonus +2)")]
    [InlineData("2024", 2, "roll 1d20-2 (save bonus +2, −4 for Exhaustion 2)")]
    [InlineData("2014", 2, "roll 1d20+2 (save bonus +2)")]
    [InlineData("2014", 3, "roll 2d20kl1+2 (save bonus +2, Disadvantage for Exhaustion 3)")]
    public void Damage_ConcentratingWhileExhausted_GivesTheRollTheCombatRulesMake(string ruleset, int exhaustion, string roll)
    {
        // §5.8: the save is face + the Con save bonus − 2 × Exhaustion in 2024; 2014 Exhaustion 3+ gives Disadvantage.
        using var world = World("""{ "classes": [{ "class": "wizard", "level": 5 }], "abilities": { "con": 14 }, "max_hp": 60 }""", ruleset);
        if (exhaustion > 0)
        {
            world.Writer.Condition(world.Campaign, "character:hero", ["exhaustion"], null, exhaustion, WriteContext.Default);
        }

        using (var connection = world.Open())
        {
            Dapper.SqlMapper.Execute(connection, "UPDATE character_sheet SET concentration = '{\"spell\":\"Fly\",\"level\":3}' WHERE entity_id = @id",
                new { id = world.Id("character:hero") });
        }

        var result = world.Writer.Damage(world.Campaign, "character:hero", 30, null, WriteContext.Default);

        var save = Assert.Single(result.Reminders, r => r.Kind == CharacterReminderKinds.ConcentrationSave);
        Assert.Equal($"Concentration on Fly: Constitution save DC 15, {roll}. If it fails, end it:", save.Text);
    }

    [Theory]
    [InlineData("2014", "fire", 20, 10)]
    [InlineData("2024", "psychic", 21, 10)]
    [InlineData("2024", null, 9, 4)]
    public void Damage_Petrified_ResistsEveryType(string ruleset, string? type, int amount, int expectedLoss)
    {
        using var world = World("""{ "level": 8, "max_hp": 85 }""", ruleset);
        world.Writer.Condition(world.Campaign, "character:hero", ["petrified"], null, null, WriteContext.Default);
        var before = world.Required("character:hero").Hp!.Value;

        world.Writer.Damage(world.Campaign, "character:hero", amount, type, WriteContext.Default);

        Assert.Equal(before - expectedLoss, world.Required("character:hero").Hp);
    }

    /// <summary>
    /// A heal from 0 wakes the character and resets its death saves; the drop stored no Prone (UR03), so none is left. A
    /// Prone the author stored stays as stored (they may have set it on purpose), and the note says so.
    /// </summary>
    [Theory]
    [InlineData(false, "Unconscious ended.")]
    [InlineData(true, "Unconscious ended (prone stays).")]
    public void Heal_FromZero_WakesAndResetsDeathSaves_ProneOnlyWhereTheAuthorStoredIt(bool authorsProne, string note)
    {
        using var world = World("""{ "level": 5, "max_hp": 30 }""");
        world.Writer.Damage(world.Campaign, "character:hero", 30, null, WriteContext.Default);
        world.Writer.Damage(world.Campaign, "character:hero", 2, null, WriteContext.Default);
        if (authorsProne)
        {
            world.Writer.Condition(world.Campaign, "character:hero", ["prone"], null, null, WriteContext.Default);
        }

        var result = world.Writer.Heal(world.Campaign, "character:hero", 8, WriteContext.Default);

        var sheet = world.Required("character:hero");
        Assert.Equal(8, sheet.Hp);
        Assert.True(sheet.DeathSaves.IsReset);
        Assert.Equal(authorsProne ? new[] { "prone" } : [], sheet.Conditions.Select(c => c.Name));
        Assert.Contains(note, result.Notes);
    }

    /// <summary>
    /// UR03: a trap drops a character to 0 out of combat; a heal and a long rest later nothing is left lying on the sheet
    /// (no Prone, no "prone stays" rest reminder), so the next fight starts with the character standing.
    /// </summary>
    [Fact]
    public void Damage_TrapDropToZero_ThenHealAndALongRest_LeavesNoCondition()
    {
        using var world = World("""{ "level": 5, "max_hp": 30 }""");
        world.Writer.Damage(world.Campaign, "character:hero", 34, null, WriteContext.Default);
        world.Writer.Heal(world.Campaign, "character:hero", 5, WriteContext.Default);

        var rest = world.Writer.Rest(world.Campaign, "character:hero", "long", null, null, DndMcp.Tests.Dice.ScriptedDiceRoller.Sequence(), WriteContext.Default);

        var sheet = world.Required("character:hero");
        Assert.Equal((30, 0), (sheet.Hp, sheet.Conditions.Count));
        Assert.DoesNotContain(rest.Reminders, r => r.Text.Contains("prone", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// CR06 (F2): a stored Unconscious on a sheet ABOVE 0 HP (a 2024 knock-out written back at 1 HP: Unconscious until it
    /// regains any hit points, SRD 5.2.1) ends with a heal that regains hit points, as one from 0 does.
    /// </summary>
    [Fact]
    public void Heal_AboveZeroWithAStoredUnconscious_EndsIt()
    {
        using var world = World("""{ "level": 5, "max_hp": 30 }""");
        world.Writer.Damage(world.Campaign, "character:hero", 29, null, WriteContext.Default);
        world.Writer.Condition(world.Campaign, "character:hero", ["unconscious"], null, null, WriteContext.Default);

        var result = world.Writer.Heal(world.Campaign, "character:hero", 4, WriteContext.Default);

        var sheet = world.Required("character:hero");
        Assert.Equal((5, 0), (sheet.Hp, sheet.Conditions.Count));
        Assert.Contains("Unconscious ended.", result.Notes);
        Assert.Contains(world.Log(result.BatchId), r => r.FieldPath == "conditions");
    }

    /// <summary>
    /// F2R04: a heal that regains no hit point (the sheet at its maximum) wakes nobody: the sheet said "HP 49 → 49 (+0).
    /// Unconscious ended." for a sleeper at full hit points.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("knocked out")]
    public void Heal_AtTheMaximum_RegainsNothing_AStoredUnconsciousStays(string? note)
    {
        using var world = World("""{ "level": 5, "max_hp": 30 }""");
        world.Writer.Condition(world.Campaign, "character:hero", ["unconscious"], null, null, WriteContext.Default);
        if (note is not null)
        {
            using var connection = world.Open();
            Dapper.SqlMapper.Execute(connection, "UPDATE character_sheet SET conditions = json_set(conditions, '$[0].note', @note) WHERE entity_id = @id",
                new { note, id = world.F.Entity(world.Campaign, "character:hero").Id });
        }

        var result = world.Writer.Heal(world.Campaign, "character:hero", 4, WriteContext.Default);

        var sheet = world.Required("character:hero");
        Assert.Equal((30, "unconscious"), (sheet.Hp, Assert.Single(sheet.Conditions).Name));
        Assert.Equal(["HP 30 → 30 (+0).", "Nothing changed."], result.Notes);
    }

    [Fact]
    public void Heal_CappedAtTheEffectiveMaximum()
    {
        using var world = World("""{ "level": 5, "max_hp": 30, "hp": 20, "max_hp_reduction": 5 }""");

        world.Writer.Heal(world.Campaign, "character:hero", 50, WriteContext.Default);

        Assert.Equal(25, world.Required("character:hero").Hp);
    }

    /// <summary>
    /// C10: healing the dead is refused, naming the cause only when the sheet shows it. Three failures are how every death
    /// at 0 is stored, massive damage's too, so they name none: never "three failed death saves" for a character killed
    /// outright.
    /// </summary>
    [Theory]
    [InlineData("massive damage", "Hero Prime is dead: ")]
    [InlineData("three failures", "Hero Prime is dead: ")]
    [InlineData("exhaustion 6", "Hero Prime is dead (exhaustion 6): ")]
    public void Heal_TheDead_IsRefusedNamingOnlyACauseTheSheetShows_NothingIsLogged(string death, string expected)
    {
        using var world = World("""{ "level": 5, "max_hp": 30 }""");
        switch (death)
        {
            case "massive damage":
                world.Writer.Damage(world.Campaign, "character:hero", 70, null, WriteContext.Default);
                break;
            case "three failures":
                foreach (var amount in new[] { 30, 1, 1, 1 })
                {
                    world.Writer.Damage(world.Campaign, "character:hero", amount, null, WriteContext.Default);
                }

                break;
            default:
                world.Writer.Condition(world.Campaign, "character:hero", ["exhaustion"], null, 6, WriteContext.Default);
                break;
        }

        var rows = world.ChangeRows();

        var heal = Assert.Throws<DndInputException>(() => world.Writer.Heal(world.Campaign, "character:hero", 5, WriteContext.Default));
        var temp = Assert.Throws<DndInputException>(() => world.Writer.TempHp(world.Campaign, "character:hero", 5, WriteContext.Default));

        Assert.Equal(expected + "healing does not bring back the dead.", heal.Message);
        Assert.Equal(expected + "the dead gain no temporary hit points.", temp.Message);
        Assert.Equal(rows, world.ChangeRows());
    }

    [Theory]
    [InlineData(0, 5, 5)]
    [InlineData(8, 5, 8)]
    [InlineData(3, 9, 9)]
    public void TempHp_KeepsTheHigher(int had, int gained, int expected)
    {
        using var world = World($$"""{ "level": 5, "max_hp": 30, "temp_hp": {{had}} }""");

        var result = world.Writer.TempHp(world.Campaign, "character:hero", gained, WriteContext.Default);

        Assert.Equal(expected, world.Required("character:hero").TempHp);
        Assert.Equal(expected == had, result.BatchId is null);
    }

    [Theory]
    [InlineData(null, "amount is required for damage")]
    [InlineData(-1, "amount is -1")]
    public void Damage_BadAmount_IsRefused(int? amount, string expected)
    {
        using var world = World();

        var ex = Assert.Throws<DndInputException>(() => world.Writer.Damage(world.Campaign, "character:hero", amount, null, WriteContext.Default));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Damage_UnknownDamageType_IsRefusedListingTheTypes()
    {
        using var world = World();

        var ex = Assert.Throws<DndInputException>(() => world.Writer.Damage(world.Campaign, "character:hero", 3, "sonic", WriteContext.Default));

        Assert.Contains("\"sonic\" is not a damage type", ex.Message, StringComparison.Ordinal);
        Assert.Contains("thunder", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Damage_SheetWithoutHitPoints_IsRefusedWithTheUpdateCall()
    {
        using var world = World("""{ "classes": [{ "class": "bard", "level": 12 }] }""");

        var ex = Assert.Throws<DndInputException>(() => world.Writer.Damage(world.Campaign, "character:hero", 3, null, WriteContext.Default));

        Assert.Equal(
            "Hero Prime's sheet does not track hit points (no max_hp): give them with campaign_character {\"action\": \"update\", " +
            "\"character\": \"character:hero\", \"sheet\": {\"max_hp\": …}, \"campaign\": \"sea\"}.", ex.Message);
    }

    [Fact]
    public void Damage_NoSheet_IsRefusedWithTheCallThatCreatesOne()
    {
        using var world = SheetWorld.Dm();

        var ex = Assert.Throws<DndInputException>(() => world.Writer.Damage(world.Campaign, "character:sidekick", 3, null, WriteContext.Default));

        Assert.Contains("has no sheet yet", ex.Message, StringComparison.Ordinal);
        Assert.Contains("campaign_character {\"action\": \"update\", \"character\": \"character:sidekick\", \"sheet\": {\"level\": …}, \"campaign\": \"sea\"}.",
            ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Damage_2024BloodiedCrossing_IsNoted()
    {
        using var world = World("""{ "level": 5, "max_hp": 30 }""");

        var result = world.Writer.Damage(world.Campaign, "character:hero", 16, null, WriteContext.Default);

        Assert.Contains("Bloodied.", result.Notes);
    }
}
