using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Characters;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tests.CampaignRead;
using DndMcp.Tests.CampaignScenarios;
using Xunit;

namespace DndMcp.Tests.CampaignCharacters;

/// <summary>
/// The leak sweeps of the sheet reads (contract §7.4, FIX §5, A-L4) over the exit-criteria fixtures: every non-author
/// view of both campaigns reads every character through <c>campaign_get include sheet</c> and <c>campaign_character
/// get</c>, and the whole serialized result carries none of the strings that view must not see: never the player
/// ("Cole"), the Contingency or any resource, spell, feat, slot, XP, inventory, sim_profile, notes or sheet_source; an NPC's
/// sheet, a disguised NPC's sheet and a hidden one read as no sheet; a subclass, species or condition holding a word only
/// hidden text holds ("Void-touched", in the Nester's secret; "Keras-born"; "fleet curse"; "reclaim") is left out of every
/// non-author line (or reads "an effect") and kept in the author's. Every sweep runs on the STOCK fixture worlds: nothing is
/// added to the world to make a leak findable.
/// </summary>
public sealed class SheetLeakTests
{
    /// <summary>What no non-author view of fixture A may read from a sheet (besides <see cref="BelmakorScenario.ForbiddenFor"/>).</summary>
    private static readonly IReadOnlyList<string> BelmakorSheetSecrets =
    [
        "Cole", "Contingency", "Bladesong", "Arcane Recovery", "Polymorph", "Circle of Power", "War Caster", "Tough", "Noble", "Elvish",
        "Scimitar", "Hold Monster", "fixture", "SpellSlots", "spell_slots", "Lineage", "Lich", "heist",
    ];

    /// <summary>The One Piece sweep list (ScenarioOnePieceLeakTests.Forbidden, FIX §5.2) plus fixture B's NPC and the sheets' author fields.</summary>
    private static readonly IReadOnlyList<string> OnePieceForbidden =
    [
        "simulacrum", "founders", "fleet", "en masse", "seal", "Baal", "Keras", "Cage", "fragment", "Protector", "Peaceful", "Mistaken",
        "Dutiful", "lineage", "Sky-world", "return visit", "effective_level_offset", "status withheld", "Nester", "unmourned", "Void-touched",
        "Rage", "Focus", "Potion", "34000", "Amethyst rider", "Draconic Strike", "fixture",
    ];

    [Fact]
    public void Belmakor_EveryNonAuthorView_ReadsNoAuthorTextFromAnySheet()
    {
        using var world = new BelmakorScenario();
        var writer = FixtureSheets.Belmakor(world);
        writer.Update(world.Campaign, "character:old-king", FixtureSheets.Spec("""{ "level": 20, "max_hp": 300, "species": "Lich", "notes": "Keras, run by Cole" }"""), null, WriteContext.Default);
        var characters = new[] { "belmakor", "vars", "ignis", "serif", "torch", "aiden-ironstar", "tristan", "old-king" }
            .Select(slug => world.F.Entity(world.Campaign, "character:" + slug))
            .ToList();
        var reader = new SheetReader(world.F.Db.Database);

        foreach (var perspective in BelmakorScenario.NonAuthorPerspectives)
        {
            var forbidden = BelmakorScenario.ForbiddenFor(perspective).Concat(BelmakorSheetSecrets).ToList();
            var reads = new List<object>();
            foreach (var character in characters)
            {
                var get = world.Reads.TryGet(world.Campaign, perspective, character.SeqHandle, new EntityIncludes(Sheet: true));
                if (get is not null)
                {
                    reads.Add(get);
                    if (character.Slug is "old-king" or "tristan")
                    {
                        Assert.Null(get.Entities.Single().Sheet);
                    }
                }

                try
                {
                    reads.Add(reader.Get(world.Campaign, character.SeqHandle, Perspective.Parse(perspective)));
                }
                catch (DndInputException ex)
                {
                    LeakAssert.CleanMessage(ex.Message, character.SeqHandle, forbidden, $"{perspective}: the refusal for {character.SeqHandle}");
                }
            }

            LeakAssert.Clean(reads, forbidden, $"{perspective} reads the sheets");
        }
    }

    [Fact]
    public void Belmakor_HomebrewTextsNamingTheSecret_AreLeftOutOfEveryNonAuthorLine()
    {
        using var world = new BelmakorScenario();
        var writer = FixtureSheets.Belmakor(world);
        writer.Update(world.Campaign, "character:vars", FixtureSheets.Spec("""
            { "classes": [{ "class": "ranger", "subclass": "Oath of the Axiom Cage", "level": 6 }, { "class": "rogue", "subclass": "keras's knife", "level": 6 }],
              "species": "Keras-born" }
            """), null, WriteContext.Default);
        writer.Update(world.Campaign, "character:ignis", FixtureSheets.Spec("""{ "species": "axiom-forged" }"""), null, WriteContext.Default);
        writer.Condition(world.Campaign, "character:vars", ["cursed by Keras", "third silence", "reclaim", "the cage's mark", "poisoned"], null, null, WriteContext.Default);
        var reader = new SheetReader(world.F.Db.Database);
        var vars = world.F.Entity(world.Campaign, "character:vars").SeqHandle;
        var ignisHandle = world.F.Entity(world.Campaign, "character:ignis").SeqHandle;
        var lines = 0;

        foreach (var perspective in BelmakorScenario.NonAuthorPerspectives)
        {
            var forbidden = BelmakorScenario.ForbiddenFor(perspective).ToList();
            var view = Perspective.Parse(perspective);
            var reads = new List<object> { reader.Party(world.Campaign, view) };
            foreach (var handle in new[] { vars, ignisHandle })
            {
                if (world.Reads.TryGet(world.Campaign, perspective, handle, new EntityIncludes(Sheet: true)) is { } get)
                {
                    reads.Add(get);
                    reads.Add(reader.Get(world.Campaign, handle, view));
                }
            }

            LeakAssert.Clean(reads, forbidden, $"{perspective} reads Vars's and Ignis's homebrew");
            if (world.Reads.TryGet(world.Campaign, perspective, vars, new EntityIncludes(Sheet: true))?.Entities.Single().Sheet?.Line is not { } line)
            {
                continue;
            }

            lines++;
            Assert.Equal([new PublicClassLine("Ranger", 6, null), new PublicClassLine("Rogue", 6, null)], line.Classes);
            Assert.Null(line.Species);
            Assert.Null(reader.Get(world.Campaign, ignisHandle, view).Characters.Single().Line!.Species);
            var reclaim = perspective is "dm" or "character:belmakor" ? "reclaim" : "an effect";
            Assert.Equal(["an effect", "an effect", reclaim, "an effect", "poisoned"], line.Conditions);
        }

        // party, table, dm and every member who sees Vars got a line to check.
        Assert.True(lines >= 5, $"only {lines} views got Vars's line");

        var author = reader.Get(world.Campaign, "character:vars").Characters.Single().Author!;
        Assert.Equal("Keras-born", author.Sheet.Species);
        Assert.Equal(["Oath of the Axiom Cage", "keras's knife"], author.Sheet.Classes.Select(c => c.Subclass));
    }

    /// <summary>
    /// L02: a subclass or species that is a derived form of a hidden name ("Kerasian Pact", "Kerasborn", "Nesterborn
    /// goliath") is left out of every non-author line, as the name itself is. F3 (review F2R02): One Piece records Baal in
    /// no name, only in fact statements, so "Path of the Baalborn" starts with a possible name only and is shown (prose is
    /// warn-only; the author's combat steps warn about such words, a sheet line has no step to warn in).
    /// </summary>
    [Fact]
    public void BothWorlds_DerivedFormsOfHiddenNames_AreLeftOutOfThePublicLine()
    {
        using var belmakor = new BelmakorScenario();
        FixtureSheets.Belmakor(belmakor).Update(belmakor.Campaign, "character:torch", FixtureSheets.Spec("""
            { "classes": [{ "class": "wizard", "subclass": "Kerasian Pact", "level": 12 }], "species": "Kerasborn", "max_hp": 74, "ac": 12 }
            """), null, WriteContext.Default);
        using var onePiece = OnePieceScenario.Build();
        FixtureSheets.OnePiece(onePiece).Update(onePiece.Campaign, "character:bjorn-mountainfell", FixtureSheets.Spec("""
            { "classes": [{ "class": "barbarian", "subclass": "Path of the Baalborn", "level": 8 }], "species": "Nesterborn goliath" }
            """), null, WriteContext.Default);

        var torch = new SheetReader(belmakor.F.Db.Database).Get(belmakor.Campaign, "character:torch", Perspective.Parse("party")).Characters.Single().Line!;
        var bjorn = new SheetReader(onePiece.F.Db.Database).Get(onePiece.Campaign, "character:bjorn-mountainfell", Perspective.Parse("party")).Characters.Single().Line!;

        Assert.Equal([new PublicClassLine("Wizard", 12, null)], torch.Classes);
        Assert.Null(torch.Species);
        Assert.Equal([new PublicClassLine("Barbarian", 8, "Path of the Baalborn")], bjorn.Classes);
        Assert.Null(bjorn.Species);
    }

    /// <summary>
    /// UR01, CR02 (F2): an ordinary secret note ("They hunt the half-blood heirs. Their oath is sworn to the dragon in the
    /// mill…"; "Half the town guard is in its pay; it means to hunt the heirs down.") hides no SRD species or subclass that
    /// merely starts with one of its words: the party's own lines keep Dragonborn, Halfling, Hunter and Oathbreaker, as they
    /// did before F1's prefix rule.
    /// </summary>
    [Theory]
    [InlineData("faction", "The Ashen Court", "A cult that serves the red dragon.",
        "They hunt the half-blood heirs. Their oath is sworn to the dragon in the mill; the blade is poisoned, and the wild hills hide the lair.")]
    [InlineData("character", "Vexthar", "An ancient dragon asleep under the mountain.", "Half the town guard is in its pay; it means to hunt the heirs down.")]
    public void Dm_AnOrdinarySecretNote_LeavesTheSpeciesAndSubclassesItsWordsOnlyStart(string kind, string name, string summary, string secret)
    {
        using var world = SheetWorld.Dm();
        world.F.Apply(world.Campaign, new CampaignOpSpec
        {
            Op = "upsert", Kind = kind, Name = name, Subtype = kind == "character" ? "npc" : null, Visibility = "author", Summary = summary, SecretMd = secret,
        });
        world.Update("character:hero", """{ "classes": [{ "class": "paladin", "subclass": "Oathbreaker", "level": 5 }], "species": "Dragonborn" }""");
        world.Update("character:sidekick", """{ "classes": [{ "class": "ranger", "subclass": "Hunter", "level": 5 }], "species": "Halfling" }""");

        var lines = world.Reader.Party(world.Campaign, Perspective.Parse("party")).Characters.ToDictionary(c => c.Name, c => c.Line!);

        Assert.Equal(("Dragonborn", "Halfling"), (lines["Hero Prime"].Species, lines["Sidekick"].Species));
        Assert.Equal([new PublicClassLine("Paladin", 5, "Oathbreaker")], lines["Hero Prime"].Classes);
        Assert.Equal([new PublicClassLine("Ranger", 5, "Hunter")], lines["Sidekick"].Classes);
    }

    /// <summary>
    /// F2R01 (F3): an author-only quest and question titled in Title Case ("Slay the Red Dragon before the Hunt", "Who
    /// poisoned the Half-Blood Heir?") leave the party line's species and subclass alone: a title gives the prefix rule no
    /// stem. They hid Brak's Hunter and Halfling: "level 5 Ranger" with no subclass and no species.
    /// </summary>
    [Fact]
    public void Dm_TitleCaseAuthorOnlyTitles_LeaveTheSpeciesAndSubclassesTheirWordsOnlyStart()
    {
        using var world = SheetWorld.Dm();
        world.F.Apply(world.Campaign,
            new CampaignOpSpec { Op = "upsert", Kind = "quest", Name = "Slay the Red Dragon before the Hunt", Visibility = "author" },
            new CampaignOpSpec { Op = "upsert", Kind = "question", Name = "Who poisoned the Half-Blood Heir?", Visibility = "author" });
        world.Update("character:hero", """{ "classes": [{ "class": "paladin", "subclass": "Oathbreaker", "level": 5 }], "species": "Dragonborn" }""");
        world.Update("character:sidekick", """{ "classes": [{ "class": "ranger", "subclass": "Hunter", "level": 5 }], "species": "Halfling" }""");

        var lines = world.Reader.Party(world.Campaign, Perspective.Parse("party")).Characters.ToDictionary(c => c.Name, c => c.Line!);

        Assert.Equal(("Dragonborn", "Halfling"), (lines["Hero Prime"].Species, lines["Sidekick"].Species));
        Assert.Equal([new PublicClassLine("Ranger", 5, "Hunter")], lines["Sidekick"].Classes);
    }

    [Fact]
    public void Belmakor_PartyLineOfBelmakor_IsTheWhitelistWithHisNumbers()
    {
        using var world = new BelmakorScenario();
        FixtureSheets.Belmakor(world);

        var line = new SheetReader(world.F.Db.Database).Get(world.Campaign, "character:belmakor", Perspective.Parse("party")).Characters.Single().Line!;

        Assert.Equal(("character:belmakor", "Belmakor Silverwind", 12, "High Elf"), (line.Ref, line.Name, line.Level, line.Species));
        Assert.Equal([new PublicClassLine("Wizard", 12, "Bladesinger")], line.Classes);
        Assert.Equal((110, 110, 7, 17, 0), (line.Hp, line.EffectiveMaxHp, line.TempHp, line.Ac, line.Exhaustion));
    }

    [Fact]
    public void OnePiece_EveryPlayerView_ReadsNoAuthorTextAndNoNpcSheet()
    {
        using var world = OnePieceScenario.Build();
        var writer = FixtureSheets.OnePiece(world);
        writer.Update(world.Campaign, "character:fishman-monk", FixtureSheets.Spec("""{ "classes": [{ "class": "monk", "subclass": "Void-touched", "level": 8 }] }"""), null, WriteContext.Default);
        writer.Condition(world.Campaign, "character:bjorn-mountainfell", ["fleet curse", "sealed by the Protector", "Mistaken mark", "keras's brand"], null, null, WriteContext.Default);
        writer.Update(world.Campaign, "character:dragon-slayer", FixtureSheets.Spec("""{ "species": "unmourned-kin" }"""), null, WriteContext.Default);
        writer.Update(world.Campaign, "character:the-nester", FixtureSheets.Spec("""{ "level": 10, "max_hp": 150, "species": "Aboleth", "notes": "Void-touched" }"""), null, WriteContext.Default);
        writer.Update(world.Campaign, "character:protector", FixtureSheets.Spec("""{ "level": 9, "max_hp": 80, "species": "Keras fragment" }"""), null, WriteContext.Default);
        var reader = new SheetReader(world.F.Db.Database);
        var characters = new[] { "bjorn-mountainfell", "dragon-slayer", "fishman-monk", "the-nester", "protector" }
            .Select(slug => world.F.Entity(world.Campaign, "character:" + slug))
            .ToList();

        foreach (var perspective in OnePieceScenario.PlayerViews.Append("character:fishman-monk"))
        {
            var reads = new List<object> { reader.Get(world.Campaign, null, Perspective.Parse(perspective)) };
            foreach (var character in characters)
            {
                if (world.Reads.TryGet(world.Campaign, perspective, character.SeqHandle, new EntityIncludes(Sheet: true)) is { } get)
                {
                    reads.Add(get);
                    if (character.Slug is "the-nester" or "protector")
                    {
                        Assert.Null(get.Entities.Single().Sheet);
                    }
                }
            }

            LeakAssert.Clean(reads, OnePieceForbidden, $"{perspective} reads the sheets");
        }

        var author = reader.Get(world.Campaign, "character:fishman-monk").Characters.Single().Author!;
        Assert.Equal("Void-touched", author.Sheet.Classes.Single().Subclass);
        var party = reader.Get(world.Campaign, "character:fishman-monk", Perspective.Parse("party")).Characters.Single().Line!;
        Assert.Equal([new PublicClassLine("Monk", 8, null)], party.Classes);
        var bjorn = reader.Get(world.Campaign, "character:bjorn-mountainfell", Perspective.Parse("party")).Characters.Single().Line!;
        Assert.Equal(["an effect", "an effect", "an effect", "an effect"], bjorn.Conditions);
    }

    [Fact]
    public void OnePiece_StockFixtureProbe_SubclassAndSpeciesNamingHiddenWords_AreLeftOut()
    {
        // The stage-2 checker's probe on the stock fixture B: "Void-touched" is in the Nester's secret, and "Cage" in the
        // restricted clue statements the party does not know ("The Protector kept the Cage engaging"), so both the subclass
        // and the species "Oath of the Axiom Cage" are left out for every player view ("Axiom", written nowhere, would pass
        // alone as a possible invention).
        using var world = OnePieceScenario.Build();
        var writer = FixtureSheets.OnePiece(world);
        writer.Update(world.Campaign, "character:fishman-monk", FixtureSheets.Spec("""
            { "classes": [{ "class": "monk", "subclass": "Void-touched", "level": 8 }], "species": "Oath of the Axiom Cage" }
            """), null, WriteContext.Default);
        var reader = new SheetReader(world.F.Db.Database);

        foreach (var perspective in OnePieceScenario.PlayerViews.Where(p => p != "public").Append("character:fishman-monk"))
        {
            var line = reader.Get(world.Campaign, "character:fishman-monk", Perspective.Parse(perspective)).Characters.Single().Line!;
            var get = world.Reads.Get(world.Campaign, perspective, new EntityIncludes(Sheet: true), null, world.F.Entity(world.Campaign, "character:fishman-monk").SeqHandle);

            Assert.Equal([new PublicClassLine("Monk", 8, null)], line.Classes);
            Assert.Null(line.Species);
            LeakAssert.Clean(new object[] { line, get, reader.Get(world.Campaign, null, Perspective.Parse(perspective)) },
                ["Void", "touched", "Nester", "Axiom", "Cage", "Oath"], $"{perspective} reads the monk");
        }

        var author = reader.Get(world.Campaign, "character:fishman-monk").Characters.Single().Author!;
        Assert.Equal(("Oath of the Axiom Cage", "Void-touched"), (author.Sheet.Species, author.Sheet.Classes.Single().Subclass));
    }

    [Fact]
    public void OnePiece_AfterThePartyHearsOfTheCage_TheSpeciesNamingItPrints()
    {
        // T1: the party is told the Protector's testimony in its own words ("…said the Cage kept engaging"). "Cage" is now
        // in text the party can see, so a word it also holds in hidden text no longer gives anything away.
        using var world = OnePieceScenario.Build();
        var writer = FixtureSheets.OnePiece(world);
        writer.Update(world.Campaign, "character:fishman-monk", FixtureSheets.Spec("""{ "species": "Cage-born" }"""), null, WriteContext.Default);
        var reader = new SheetReader(world.F.Db.Database);
        string? Species() => reader.Get(world.Campaign, "character:fishman-monk", Perspective.Parse("party")).Characters.Single().Line!.Species;

        var before = Species();
        world.T1();

        Assert.Null(before);
        Assert.Equal("Cage-born", Species());
    }

    [Fact]
    public void OnePiece_PartyList_IsTheThreeMembersLinesInNameOrder()
    {
        using var world = OnePieceScenario.Build();
        FixtureSheets.OnePiece(world);

        var result = new SheetReader(world.F.Db.Database).Get(world.Campaign, null, Perspective.Parse("party"));

        Assert.True(result.IsList);
        Assert.Equal(["Björn Mountainfell", "The amethyst Dragon Slayer", "The fishman monk"], result.Characters.Select(c => c.Name));
        var slayer = result.Characters[1].Line!;
        Assert.Equal([new PublicClassLine("dragon slayer", 8, "Amethyst")], slayer.Classes);
        Assert.Equal((68, 68, 16), (slayer.Hp, slayer.EffectiveMaxHp, slayer.Ac));
        Assert.Equal((85, 15), (result.Characters[0].Line!.Hp, result.Characters[0].Line!.Ac));
    }

    [Fact]
    public void OnePiece_AuthorList_CarriesTheWholeSheetsAndTheInventory()
    {
        using var world = OnePieceScenario.Build();
        FixtureSheets.OnePiece(world);

        var result = new SheetReader(world.F.Db.Database).Get(world.Campaign, null, Perspective.Parse("dm"));

        Assert.True(result.AuthorView);
        var bjorn = result.Characters[0].Author!;
        Assert.Equal(34000, bjorn.Sheet.Xp);
        Assert.Equal(48000, bjorn.NextXpThreshold);
        var potion = Assert.Single(bjorn.Inventory);
        Assert.Equal((FixtureSheets.PotionOfHealing, 2.0, FixtureSheets.PotionRef), (potion.Name, potion.Quantity, potion.SrdRef));
        Assert.Equal("Amethyst Dragon Slayer L8 (fixture)", result.Characters[1].Author!.SimProfile!.Name);
    }
}
