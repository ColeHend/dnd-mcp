using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tests.CampaignScenarios;
using DndMcp.Tests.CampaignWrite;
using Xunit;

namespace DndMcp.Tests.CampaignCharacters;

/// <summary>
/// <see cref="ViewTextCheck"/> (contract §6.12, D20b): a free text passes for a view exactly when the knowledge check finds
/// no name the view does not use (in any case), no partial name and no forbidden word in it, and it holds no hidden word (a
/// distinctive word, in any case, that only text the view cannot see holds); one check per render, findings mapped back to
/// their texts by offset; the author view passes everything. Run on the STOCK fixture worlds (fixture B with its own
/// FixtureSheets entities): nothing is added to make a leak visible.
/// </summary>
public sealed class ViewTextCheckTests : IClassFixture<ViewTextCheckTests.Worlds>
{
    private readonly Worlds _worlds;

    public ViewTextCheckTests(Worlds worlds)
    {
        _worlds = worlds;
    }

    /// <summary>Both stock scenario worlds (fixture B with FixtureSheets' entities and sheets), built once (the check only reads).</summary>
    public sealed class Worlds : IDisposable
    {
        public Worlds()
        {
            Belmakor = new BelmakorScenario();
            OnePiece = OnePieceScenario.Build();
            FixtureSheets.OnePiece(OnePiece);
        }

        public BelmakorScenario Belmakor { get; }

        public OnePieceScenario OnePiece { get; }

        public void Dispose()
        {
            Belmakor.Dispose();
            OnePiece.Dispose();
        }
    }

    private ViewTextResult Belmakor(string view, params string?[] texts) =>
        ViewTextCheck.Check(_worlds.Belmakor.F.Db.Database, _worlds.Belmakor.Campaign, Perspective.Parse(view), texts);

    private ViewTextResult OnePiece(string view, params string?[] texts) =>
        ViewTextCheck.Check(_worlds.OnePiece.F.Db.Database, _worlds.OnePiece.Campaign, Perspective.Parse(view), texts);

    [Theory]
    [InlineData("party", "Keras", false)]
    [InlineData("party", "The Old King", true)]
    [InlineData("party", "the sorcerer king", true)]
    [InlineData("party", "Axiom Cage", false)]
    [InlineData("party", "haul the Cage home", false)]
    [InlineData("party", "Cage-bound", false)]
    [InlineData("party", "Torch", true)]
    [InlineData("party", "Lich", true)]
    [InlineData("party", "a 900-year-old lich", false)]
    [InlineData("public", "Torch", false)]
    [InlineData("character:belmakor", "Lieutenant James Torch", true)]
    [InlineData("author", "Keras", true)]
    [InlineData("author", "Axiom Cage", true)]
    [InlineData("party", "keras", false)]
    [InlineData("party", "keras's knife", false)]
    [InlineData("party", "third silence", false)]
    [InlineData("party", "the cage's mark", false)]
    [InlineData("party", "axiom-forged", false)]
    [InlineData("table", "Keras-born", false)]
    [InlineData("dm", "Keras", false)]
    [InlineData("dm", "keras", false)]
    [InlineData("dm", "Axiom Cage", false)]
    [InlineData("character:vars", "Oath of the Axiom Cage", false)]
    [InlineData("party", "reclaim", false)]
    [InlineData("party", "Blighted Ward", false)]
    [InlineData("character:vars", "reclaim", false)]
    [InlineData("character:belmakor", "reclaim", true)]
    [InlineData("dm", "reclaim", true)]
    [InlineData("party", "Battle Smith", true)]
    [InlineData("party", "Bladesong", true)]
    [InlineData("party", "Void-controlled", true)]
    [InlineData("party", "silence-bound", false)]
    [InlineData("party", "Kerasian sentinel", false)]
    [InlineData("party", "Kerasspawn", false)]
    [InlineData("party", "Kerasian lich", false)]
    [InlineData("party", "Kerasian hex", false)]
    [InlineData("party", "Kerasian Ward", false)]
    [InlineData("party", "Kerasian Pact", false)]
    [InlineData("party", "Kerasborn", false)]
    [InlineData("table", "kerasian", false)]
    [InlineData("character:belmakor", "Kerasborn", false)]
    [InlineData("author", "Kerasborn", true)]
    [InlineData("party", "Cagebreaker's mark", false)]
    [InlineData("character:vars", "Axiomatic", false)]
    [InlineData("party", "Baalite cultist", true)]
    public void Check_BelmakorTexts_PassExactlyWhenTheViewUsesEveryNameInThem(string view, string text, bool passes)
    {
        Assert.Equal(passes, Belmakor(view, text).Passes(0));
    }

    /// <summary>
    /// Fixture B's texts. The derived forms of hidden NAMES fail (L02): "Nesterling" and "Nesterborn" with the Nester's
    /// name; "Sealbreaker" with the one-word forbidden term "seal". Words that only start with a word of hidden prose or of
    /// a title pass (F2, reviews UR01/LR01; F3, reviews F2R01/F2R02): "Fighter" (q22's lower-case "fight"), "Phantasmal
    /// Killer" and "Plane Shift" ("kill", "plan" in Nadar's plan), "Builder" (G.O.D.S. Co.'s secret); "Voidtouched" ("Void"
    /// only opens the Nester's secret); and "Baalite cultist", "Baalward", "Path of the Baalborn": this world records Baal
    /// in no name, only in fact statements ("thins Baal's seal") and the author-only question q22's TITLE, which gives no
    /// stem any more; "Baal" there is a possible name, so they pass with a D20b warning
    /// (<see cref="Check_APossibleNameOfHiddenProse_PassesWithAWarning_AHiddenNameFails"/>). The words themselves still fail
    /// ("baal", "Keras-born").
    /// </summary>
    [Theory]
    [InlineData("party", "The Nester", false)]
    [InlineData("party", "sealed by the Nester", false)]
    [InlineData("party", "sealed", false)]
    [InlineData("party", "The Protector", false)]
    [InlineData("party", "the advisor in Serret", true)]
    [InlineData("party", "Void-touched", false)]
    [InlineData("party", "void-touched", false)]
    [InlineData("party", "VOID-TOUCHED", false)]
    [InlineData("table", "Void-touched", false)]
    [InlineData("public", "Void-touched", false)]
    [InlineData("character:fishman-monk", "Void-touched", false)]
    [InlineData("character:dragon-slayer", "touched by the void", false)]
    [InlineData("party", "keras", false)]
    [InlineData("party", "Keras-born", false)]
    [InlineData("party", "fleet curse", false)]
    [InlineData("party", "protector's ward", false)]
    [InlineData("party", "unmourned", false)]
    [InlineData("party", "baal", false)]
    [InlineData("party", "Order of the Deep Sea", true)]
    [InlineData("party", "Path of the Totem Warrior", true)]
    [InlineData("party", "Amethyst", true)]
    [InlineData("party", "cursed (Mucus Cloud)", true)]
    [InlineData("party", "peaceful-hearted", false)]
    [InlineData("party", "the advisor in Serret's gift", true)]
    [InlineData("public", "the advisor in Serret's gift", false)]
    [InlineData("party", "Goblin Boss", true)]
    [InlineData("party", "wraith-blooded", false)]
    [InlineData("party", "rope-bound", true)]
    [InlineData("public", "rope-bound", false)]
    [InlineData("party", "Aboleth", true)]
    [InlineData("character:bjorn-mountainfell", "The Nester", false)]
    [InlineData("dm", "The Nester", true)]
    [InlineData("dm", "Void-touched", true)]
    [InlineData("party", "Baalite cultist", true)]
    [InlineData("party", "Baalward", true)]
    [InlineData("party", "Path of the Baalborn", true)]
    [InlineData("party", "Nesterling", false)]
    [InlineData("party", "Voidtouched", true)]
    [InlineData("party", "Voidtouched goliath", true)]
    [InlineData("party", "Nesterborn goliath", false)]
    [InlineData("party", "Fighter", true)]
    [InlineData("party", "Phantasmal Killer", true)]
    [InlineData("party", "Builder", true)]
    [InlineData("party", "Longstrider", true)]
    [InlineData("party", "Counterspell", true)]
    [InlineData("party", "Plane Shift", true)]
    [InlineData("party", "Speak with Plants", true)]
    [InlineData("party", "Sealbreaker", false)]
    [InlineData("character:nadar", "Sealbreaker", false)]
    [InlineData("character:fishman-monk", "Nesterling", false)]
    [InlineData("dm", "Baalite cultist", true)]
    public void Check_OnePieceTexts_PassExactlyWhenTheViewUsesEveryNameInThem(string view, string text, bool passes)
    {
        Assert.Equal(passes, OnePiece(view, text).Passes(0));
    }

    [Fact]
    public void Check_OneRender_MapsEachFindingToItsOwnText()
    {
        var result = Belmakor("party", "Torch", "Keras", null, "", "Mummy 2", "Axiom", "Ignis");

        Assert.Equal([true, false, true, true, true, false, true], result.Texts.Select(t => t.Passes));
        Assert.False(result.AllPass);
        var keras = Assert.Single(result.Texts[1].Findings, f => f.Kind == ViewTextFindingKinds.Name);
        Assert.Equal("Keras", keras.Matched);
        Assert.Contains("character:old-king", keras.Sources);
        Assert.Contains(result.Texts[1].Findings, f => f.Kind == ViewTextFindingKinds.Forbidden);
        Assert.Equal([ViewTextFindingKinds.PartialName, ViewTextFindingKinds.HiddenWord], result.Texts[5].Findings.Select(f => f.Kind));
        Assert.All(result.Texts.Where(t => t.Passes), t => Assert.Empty(t.Findings));
    }

    [Fact]
    public void Check_HiddenWord_NamesWhereTheWordIsHiddenForTheAuthor()
    {
        var result = OnePiece("party", "Void-touched", "fleet curse", "Keras-born", "Order of the Deep Sea");

        var voidWord = Assert.Single(result.Texts[0].Findings, f => f.Matched == "Void");
        Assert.Equal((ViewTextFindingKinds.HiddenWord, ViewTextFindingKinds.HiddenWord), (voidWord.Kind, voidWord.Classification));
        Assert.Equal(["character:the-nester"], voidWord.Sources);
        Assert.Contains(result.Texts[0].Findings, f => f.Matched == "touched");
        var fleet = Assert.Single(result.Texts[1].Findings).Sources;
        Assert.Equal(["faction:gods-co", "question:q7"], fleet.Take(2));
        Assert.StartsWith("f:", Assert.Single(fleet.Skip(2)), StringComparison.Ordinal);
        var keras = Assert.Single(result.Texts[2].Findings);
        Assert.Equal("Keras", keras.Matched);
        Assert.Contains("character:protector", keras.Sources);
        Assert.True(result.Passes(3));
        Assert.Empty(result.Texts[3].Findings);
    }

    /// <summary>
    /// F3, review F2R02 (bullet: "a prose-only capitalised word gives a warning but is shown"): a word that STARTS with a
    /// word capitalised mid-sentence in hidden prose (a possible name: "Ashveil" in "swore the oath to the Ashveil") passes
    /// and carries the possible name for the author's D20b warning; the word itself still fails (the whole-word rule), and
    /// so does a derived form of a hidden NAME ("Ashenborn"). A capital that only opens a sentence ("Mirewood is lost") is no
    /// possible name, and an ordinary lower-case word of the prose gives nothing.
    /// </summary>
    [Fact]
    public void Check_APossibleNameOfHiddenProse_PassesWithAWarning_AHiddenNameFails()
    {
        using var world = SheetWorld.Dm();
        world.F.Apply(world.Campaign, new CampaignOpSpec
        {
            Op = "upsert", Kind = "faction", Name = "The Ashen Court", Visibility = "author",
            SecretMd = "They swore the oath to the Ashveil at the old mill. Mirewood is lost; the gloomtide rises.",
        });

        var result = ViewTextCheck.Check(world.Database, world.Campaign, Perspective.Parse("party"),
            ["Ashveiler", "Ashveilborn's banner", "Ashveil", "Ashenborn", "Mirewoodkin", "Gloomtider"]);

        Assert.Equal([true, true, false, false, true, true], result.Texts.Select(t => t.Passes));
        var possible = Assert.Single(result.Texts[0].PossibleNames);
        Assert.Equal((ViewTextFindingKinds.PossibleName, "Ashveiler", "Ashveil"), (possible.Kind, possible.Matched, possible.Classification));
        Assert.Equal(["faction:the-ashen-court"], possible.Sources);
        Assert.Equal("Ashveil", Assert.Single(result.Texts[1].PossibleNames).Classification);
        Assert.All(result.Texts.Skip(2), t => Assert.Empty(t.PossibleNames));
        Assert.All(result.Texts.Where(t => t.Passes), t => Assert.Empty(t.Findings));
    }

    /// <summary>
    /// F3, review F2R01: a title gives the prefix rule no stem. An author-only quest and question titled in Title Case
    /// ("Slay the Red Dragon before the Hunt", "Who poisoned the Half-Blood Heir?") hid Hunter, Halfling and the ally
    /// "Huntsman Joss" from the PC's own party line and the board. Each passes now, with no warning either (a title is no
    /// prose); the title's own words are still hidden whole.
    /// </summary>
    [Fact]
    public void Check_TitleCaseAuthorOnlyTitles_HideNoWordTheirWordsOnlyStart()
    {
        using var world = SheetWorld.Dm();
        world.F.Apply(world.Campaign,
            new CampaignOpSpec { Op = "upsert", Kind = "quest", Name = "Slay the Red Dragon before the Hunt", Visibility = "author" },
            new CampaignOpSpec { Op = "upsert", Kind = "question", Name = "Who poisoned the Half-Blood Heir?", Visibility = "author" });

        var result = ViewTextCheck.Check(world.Database, world.Campaign, Perspective.Parse("party"),
            ["Hunter", "Halfling", "Huntsman Joss", "Dragonborn", "Hunter's Mark", "Bloodhound", "Hunt", "half-blood"]);

        Assert.Equal([true, true, true, true, true, true, false, false], result.Texts.Select(t => t.Passes));
        Assert.All(result.Texts, t => Assert.Empty(t.PossibleNames));
    }

    /// <summary>
    /// F3, review F2R02 ("Kerasian and Baalite fail in both worlds through names, with q22 renamed or removed"). Belmakor:
    /// "Keras" is a cross-linked name and an author alias, so Kerasian fails there; "Cage" is an author alias, so does
    /// "Cagebreaker's mark"; "Baal" is only in the cross-linked Keras's text, a possible name. One Piece records Keras, Baal
    /// and the Cage in no name, only in prose (the Protector's secret, the fact statements) and q22's title: with q22
    /// renamed they are possible names, shown with a warning — and once the author records them as names (an author-only
    /// Baal and Keras, the Axiom Cage), their derived forms fail, naming where.
    /// </summary>
    [Fact]
    public void Check_DerivedFormsOfKerasBaalAndCage_FailThroughNames_ProseOnlyTheyWarn()
    {
        string[] texts = ["Kerasian", "Baalite cultist", "Cagebreaker's mark", "Baalward"];
        var belmakor = Belmakor("party", texts);
        using var onePiece = OnePieceScenario.Build();
        onePiece.F.Apply(onePiece.Campaign, new CampaignOpSpec { Op = "upsert", Ref = "question:q22", Name = "How long ago was the old fight, on the corrected timeline?" });
        var prose = ViewTextCheck.Check(onePiece.F.Db.Database, onePiece.Campaign, Perspective.Parse("party"), texts);
        onePiece.F.Apply(onePiece.Campaign,
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Keras", Subtype = "npc", Visibility = "author" },
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Baal", Subtype = "npc", Visibility = "author" },
            new CampaignOpSpec { Op = "upsert", Kind = "item", Name = "The Axiom Cage", Slug = "axiom-cage", Visibility = "author" });
        var names = ViewTextCheck.Check(onePiece.F.Db.Database, onePiece.Campaign, Perspective.Parse("party"), texts);

        Assert.Equal([false, true, false, true], belmakor.Texts.Select(t => t.Passes));
        Assert.Equal(["Baal", "Baal"], belmakor.Texts.Where(t => t.Passes).Select(t => Assert.Single(t.PossibleNames).Classification));
        Assert.Equal(["one-piece/character:keras"], belmakor.Texts[1].PossibleNames[0].Sources);
        Assert.Equal([true, true, true, true], prose.Texts.Select(t => t.Passes));
        Assert.Equal(["Keras", "Baal", "Cage", "Baal"], prose.Texts.Select(t => Assert.Single(t.PossibleNames).Classification));
        Assert.Equal([false, false, false, false], names.Texts.Select(t => t.Passes));
        Assert.Equal(["character:keras"], Assert.Single(names.Texts[0].Findings).Sources);
        Assert.Contains("character:baal", Assert.Single(names.Texts[1].Findings).Sources);
        Assert.Contains("item:axiom-cage", Assert.Single(names.Texts[2].Findings).Sources);
    }

    [Theory]
    [InlineData("Bladesong")]
    [InlineData("Circle of Power")]
    [InlineData("Battle Smith")]
    [InlineData("Bladesinger")]
    [InlineData("High Elf")]
    [InlineData("frightened")]
    public void Check_FixtureAGoldenTexts_PassForEveryPartySideView(string text)
    {
        foreach (var view in new[] { "party", "table", "dm", "character:belmakor", "character:torch" })
        {
            Assert.True(Belmakor(view, text).Passes(0), $"{view}: {text}");
        }
    }

    [Theory]
    [InlineData("Rage")]
    [InlineData("cursed (Mucus Cloud)")]
    [InlineData("Order of the Deep Sea")]
    [InlineData("Path of the Totem Warrior")]
    [InlineData("Amethyst")]
    [InlineData("Dragon Slayer")]
    [InlineData("dragon slayer")]
    [InlineData("grappled")]
    public void Check_FixtureBGoldenTexts_PassForEveryPartySideView(string text)
    {
        foreach (var view in new[] { "party", "table", "character:bjorn-mountainfell", "character:dragon-slayer", "character:fishman-monk" })
        {
            Assert.True(OnePiece(view, text).Passes(0), $"{view}: {text}");
        }
    }

    [Theory]
    [InlineData("Oath of the Axiom Cage", false)]
    [InlineData("cage-born", false)]
    [InlineData("temple-sworn", false)]
    [InlineData("moonlight ward", false)]
    [InlineData("fruit-eater", true)]
    [InlineData("shard-bearer", true)]
    public void Check_WordsOfFactsThePartyDoesNotKnow_AreHiddenUnlessItCanReadThemElsewhere(string text, bool passes)
    {
        // "Cage", "temple" and "moonlight" are only in restricted clues the party has not been told; "fruit" and "shard" are
        // in clues too, but also in "The party holds at least one devil fruit and at least one axe shard.", which it knows.
        Assert.Equal(passes, OnePiece("party", text).Passes(0));
    }

    [Fact]
    public void Check_HiddenWordTheViewCanReadElsewhere_Passes()
    {
        // "advisor" and "Serret" are in the Protector's secret, and in the party's own name for it ("the advisor in
        // Serret"); "Battle" is only in the secret. The public knows the Protector by no name at all.
        var result = OnePiece("party", "Advisor's blessing", "Serret-born", "Battle-born");
        var publicView = OnePiece("public", "Advisor's blessing");

        Assert.Equal([true, true, false], result.Texts.Select(t => t.Passes));
        Assert.False(publicView.Passes(0));
    }

    [Fact]
    public void Check_GatedFactAndForbiddenTerm_HideTheirWordsUntilTheViewKnowsThem()
    {
        // "Eating a devil fruit breaks part of Baal's seal." is gated and unknown to the party; Nadar knows it, but the gate's
        // forbidden term "seal" is still active for every non-author view.
        var party = OnePiece("party", "the eating curse", "breaks things", "seal-bound");
        var nadar = OnePiece("character:nadar", "the eating curse", "seal-bound");

        Assert.Equal([false, false, false], party.Texts.Select(t => t.Passes));
        Assert.Contains(party.Texts[0].Findings, f => f.Kind == ViewTextFindingKinds.HiddenWord && f.Sources.Single().StartsWith("f:", StringComparison.Ordinal));
        Assert.True(nadar.Passes(0));
        Assert.False(nadar.Passes(1));
    }

    [Fact]
    public void Check_UnknownProperNoun_PassesButIsAPossibleInvention()
    {
        var result = Belmakor("party", "Wraith Blade", "Torch");
        var onePiece = OnePiece("party", "Goblin Boss");

        Assert.True(result.Passes(0));
        Assert.True(result.Texts[0].HasPossibleInventions);
        Assert.False(result.Texts[1].HasPossibleInventions);
        Assert.True(onePiece.Passes(0));
        Assert.True(onePiece.Texts[0].HasPossibleInventions);
    }

    [Fact]
    public void Check_EachHiddenSource_HidesItsWordsAndAUsedAliasShowsThem()
    {
        // One small world, one source per text, all lower case (so neither the name scanner's whole names nor the partial-
        // name capitals decide): "storm" is in Vexmoor's secret but also in the party alias the party uses; "drowned" only in
        // an author alias; "hollow" and "crown" only in an active reveal rule's forbidden term; "lighthouse" and "tides" only
        // in the body and summary of an NPC the party has not met. "dawnfire" is only in a PUBLIC fact not yet in play: the
        // contract counts the statements of NON-public facts a view does not know, so it passes.
        using var world = SheetWorld.Dm();
        world.F.Apply(world.Campaign,
            new CampaignOpSpec
            {
                Op = "upsert", Kind = "character", Name = "Captain Vexmoor", Subtype = "npc", Visibility = "party",
                SecretMd = "The Storm Admiral answers to nobody.",
                Aliases = [new AliasSpec { Alias = "the Storm Admiral", Visibility = "party" }, new AliasSpec { Alias = "the Drowned Prince", Visibility = "author" }],
            },
            new CampaignOpSpec
            {
                Op = "upsert", Kind = "rule", Subtype = "reveal_rule", Name = "Reveal pacing", Visibility = "author",
                Data = Op.Data("{\"forbidden_terms\": [\"Hollow Crown\"]}"),
            },
            new CampaignOpSpec
            {
                Op = "upsert", Kind = "character", Name = "Quiet Neighbour", Subtype = "npc", Visibility = "restricted",
                Summary = "Counts the tides.", BodyMd = "Keeps the lighthouse on the point.",
            },
            new CampaignOpSpec { Op = "fact", Statement = "The dawnfire festival is called off.", CanonStatus = "planned", Visibility = "public" });

        var result = ViewTextCheck.Check(world.Database, world.Campaign, Perspective.Parse("party"),
            ["storm-touched", "drowned-kin", "crown-forged", "hollow-eyed", "lighthouse-keeper", "tides-born", "dawnfire-blessed"]);

        Assert.Equal([true, false, false, false, false, false, true], result.Texts.Select(t => t.Passes));
        Assert.Equal(["character:quiet-neighbour"], Assert.Single(result.Texts[4].Findings).Sources);
        Assert.Equal(["character:captain-vexmoor"], Assert.Single(result.Texts[1].Findings).Sources);
        Assert.Equal(["rule:reveal-pacing"], Assert.Single(result.Texts[2].Findings).Sources);
        Assert.All(result.Texts.Skip(1).Take(5), t => Assert.Equal(ViewTextFindingKinds.HiddenWord, Assert.Single(t.Findings).Kind));
    }

    /// <summary>
    /// L02: a word that STARTS with a hidden distinctive word of four letters or more taken from a hidden NAME ("Kerasian"
    /// of "Keras") gives it away like the word itself, and names where it is hidden: here an alias the party does not use
    /// ("the Drowned Prince") and an author-only item's name ("Hollow Crown"). A word that is itself visible passes whatever
    /// it starts with ("Hollowmere", a place the party knows), and so does one that starts with a word the view can read
    /// ("Stormborn": "storm" is in the alias the party uses). A one-word forbidden term forbids the words it starts, even
    /// for a view that reads the term elsewhere. A word that only a secret holds starts nothing (F2, review UR01):
    /// "Gloomwaterkin" passes though "gloomwater" fails.
    /// </summary>
    [Fact]
    public void Check_AWordStartingWithAHiddenWord_FailsUnlessTheWordOrItsStartIsVisible()
    {
        using var world = SheetWorld.Dm();
        world.F.Apply(world.Campaign,
            new CampaignOpSpec
            {
                Op = "upsert", Kind = "character", Name = "Captain Vexmoor", Subtype = "npc", Visibility = "party",
                SecretMd = "The Storm Admiral answers to the Drowned Prince across the gloomwater.",
                Aliases = [new AliasSpec { Alias = "the Storm Admiral", Visibility = "party" }, new AliasSpec { Alias = "the Drowned Prince", Visibility = "author" }],
            },
            new CampaignOpSpec { Op = "upsert", Kind = "location", Name = "Hollowmere", Visibility = "party" },
            new CampaignOpSpec { Op = "upsert", Kind = "item", Name = "Hollow Crown", Visibility = "author" },
            new CampaignOpSpec
            {
                Op = "upsert", Kind = "rule", Subtype = "reveal_rule", Name = "Reveal pacing", Visibility = "author",
                Data = Op.Data("{\"forbidden_terms\": [\"Hollow Crown\", \"Tidecaller\"]}"),
            },
            new CampaignOpSpec { Op = "fact", Statement = "The Tidecaller sleeps under Hollowmere.", CanonStatus = "canon", Visibility = "public" });

        var result = ViewTextCheck.Check(world.Database, world.Campaign, Perspective.Parse("party"),
            ["Drownedkin", "Hollowborn", "Hollowmere", "Stormborn", "Tidecallers' cult", "Tidecallerborn", "gloomwater", "Gloomwaterkin"]);

        Assert.Equal([false, false, true, true, false, false, false, true], result.Texts.Select(t => t.Passes));
        Assert.Equal(["character:captain-vexmoor"], Assert.Single(result.Texts[0].Findings).Sources);
        Assert.Equal((ViewTextFindingKinds.HiddenWord, "Hollowborn", "item:hollow-crown"),
            (Assert.Single(result.Texts[1].Findings).Kind, result.Texts[1].Findings[0].Matched, result.Texts[1].Findings[0].Sources.Single()));
        Assert.Contains(result.Texts[5].Findings, f => f.Kind == ViewTextFindingKinds.HiddenWord && f.Sources.Contains("rule:reveal-pacing"));
    }

    // The ordinary DM notes of reviews UR01, RR02 and CR02: author-only text full of common words ("dragon", "half",
    // "hunt", "moon", "spirit", "stone", "guard") that the SRD's own names start with.
    private const string AshenCourtSummary = "A cult that serves the red dragon.";
    private const string AshenCourtSecret = "They hunt the half-blood heirs. Their oath is sworn to the dragon in the mill; the blade is poisoned, and the wild hills hide the lair.";
    private const string SeleneSecret = "Herald of the moon goddess; a storm spirit bound in stone, who speaks in thunder.";
    private const string VextharSummary = "An ancient dragon asleep under the mountain.";
    private const string VextharSecret = "Half the town guard is in its pay; it means to hunt the heirs down.";

    // A DM world (SheetWorld.Dm) with one of those notes on an author-only entity.
    private static SheetWorld NoteWorld(string note)
    {
        var world = SheetWorld.Dm();
        world.F.Apply(world.Campaign, note switch
        {
            "ur01" => new CampaignOpSpec
            {
                Op = "upsert", Kind = "faction", Name = "The Ashen Court", Visibility = "author", Summary = AshenCourtSummary, SecretMd = AshenCourtSecret,
            },
            "rr02" => new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Selene", Subtype = "npc", Visibility = "author", SecretMd = SeleneSecret },
            _ => new CampaignOpSpec
            {
                Op = "upsert", Kind = "character", Name = "Vexthar", Subtype = "npc", Visibility = "author", Summary = VextharSummary, SecretMd = VextharSecret,
            },
        });
        return world;
    }

    /// <summary>
    /// UR01, RR02, CR02 (F2, amending §6.12's F1 prefix rule): the prefix rule takes its stems only from hidden NAMES (the
    /// true names of entities the view does not see, aliases it does not use, cross-linked names, one-word forbidden terms);
    /// a word of a secret, a summary, a body or a fact statement keeps the whole-word rule. One ordinary DM note used to hide
    /// Dragonborn, Halfling, Hunter, Oathbreaker, Hunter's Mark, Moonbeam, Spiritual Weapon, Stoneskin, Thunderwave, Spirit
    /// Guardians and the allies "Guardsman Pell" and "Huntsman Joss" from every party view (and warn the author at every
    /// step). Each passes now; the note's own words, and the words its entity's NAME starts, still fail.
    /// </summary>
    [Theory]
    [InlineData("ur01", "Dragonborn", true)]
    [InlineData("ur01", "Halfling", true)]
    [InlineData("ur01", "Hunter", true)]
    [InlineData("ur01", "Oathbreaker", true)]
    [InlineData("ur01", "Hunter's Mark", true)]
    [InlineData("ur01", "Wildland Bandit", true)]
    [InlineData("ur01", "Millworker Thug", true)]
    [InlineData("ur01", "Bladeward", true)]
    [InlineData("ur01", "half-blood", false)]
    [InlineData("ur01", "dragon-sworn", false)]
    [InlineData("ur01", "Ashenborn", false)]
    [InlineData("rr02", "Moonbeam", true)]
    [InlineData("rr02", "Spiritual Weapon", true)]
    [InlineData("rr02", "Stoneskin", true)]
    [InlineData("rr02", "Thunderwave", true)]
    [InlineData("rr02", "Thunderwave push", true)]
    [InlineData("rr02", "moon-touched", false)]
    [InlineData("rr02", "Seleneborn", false)]
    [InlineData("cr02", "Dragonborn", true)]
    [InlineData("cr02", "Halfling", true)]
    [InlineData("cr02", "Spirit Guardians", true)]
    [InlineData("cr02", "Hunter's Mark", true)]
    [InlineData("cr02", "Guardsman Pell", true)]
    [InlineData("cr02", "Huntsman Joss", true)]
    [InlineData("cr02", "town guard", false)]
    [InlineData("cr02", "Vextharian", false)]
    public void Check_AnOrdinarySecretNote_HidesNoWordThatOnlyStartsWithOneOfItsWords(string note, string text, bool passes)
    {
        using var world = NoteWorld(note);

        var result = ViewTextCheck.Check(world.Database, world.Campaign, Perspective.Parse("party"), [text]);

        Assert.Equal(passes, result.Passes(0));
    }

    /// <summary>
    /// UR01's control: a hidden true NAME still hides the words it starts. With an NPC named "Hunt" the party has not met,
    /// "Hunter" and "Hunter's Mark" fail (naming the NPC for the author); once the party is shown the NPC, they pass.
    /// </summary>
    [Theory]
    [InlineData("restricted", false)]
    [InlineData("party", true)]
    public void Check_AHiddenTrueName_StillHidesTheWordsItStarts(string visibility, bool passes)
    {
        using var world = SheetWorld.Dm();
        world.F.Apply(world.Campaign, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Hunt", Subtype = "npc", Visibility = visibility });

        var result = ViewTextCheck.Check(world.Database, world.Campaign, Perspective.Parse("party"), ["Hunter", "Hunter's Mark"]);

        Assert.Equal([passes, passes], result.Texts.Select(t => t.Passes));
        Assert.All(result.Texts.Where(t => !t.Passes), t => Assert.Contains(t.Findings, f => f.Kind == ViewTextFindingKinds.HiddenWord && f.Sources.SequenceEqual(["character:hunt"])));
    }

    /// <summary>
    /// LR01: the plural-stripping of a start never makes a common word a giveaway. "Longstrider" starts with "longs",
    /// which stripped is "long", a common word the rule skips (the common-word test runs AFTER stripping); with a location
    /// "The Long Night" hidden from the party it used to fail. The hidden name's distinctive word still hides its derived
    /// forms ("Nightshade", "Nightborn").
    /// </summary>
    [Fact]
    public void Check_AStartThatStripsToACommonWord_GivesNothingAway()
    {
        using var world = SheetWorld.Dm();
        world.F.Apply(world.Campaign, new CampaignOpSpec { Op = "upsert", Kind = "location", Name = "The Long Night", Visibility = "restricted" });

        var result = ViewTextCheck.Check(world.Database, world.Campaign, Perspective.Parse("party"), ["Longstrider", "Longs", "Nightshade", "Nightborn"]);

        Assert.Equal([true, true, false, false], result.Texts.Select(t => t.Passes));
    }

    /// <summary>
    /// LR01 (review L's sweep): every SRD spell, species, subspecies, subclass and class name of both editions passes for
    /// the party when the hidden text is ordinary prose (the notes of UR01, RR02 and CR02, and LR01's hidden statements: the
    /// fight, kill, plan, counts, building), except a name holding one of that prose's own words, which the whole-word rule
    /// withholds as it always has ("Spirit Guardians" with "a storm spirit" in a secret). The prefix rule adds no failure.
    /// The control: the note's entity's hidden name still hides its derived form.
    /// </summary>
    [Fact]
    public void Check_EverySrdName_OrdinaryHiddenProse_FailsOnlyByAWholeWordOfIt()
    {
        const string statement = "Nadar's plan is to have them kill the fleet's builder; the counts are climbing, and the fight comes after the record " +
                                 "period, near the close of the campaign.";
        using var world = SheetWorld.Dm();
        world.F.Apply(world.Campaign,
            new CampaignOpSpec { Op = "upsert", Kind = "faction", Name = "The Ashen Court", Visibility = "author", Summary = AshenCourtSummary, SecretMd = AshenCourtSecret },
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Selene", Subtype = "npc", Visibility = "author", SecretMd = SeleneSecret },
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Vexthar", Subtype = "npc", Visibility = "author", Summary = VextharSummary, SecretMd = VextharSecret },
            new CampaignOpSpec { Op = "fact", Statement = statement, CanonStatus = "canon", Visibility = "restricted" });
        var names = new[] { "2014", "2024" }
            .SelectMany(edition => (edition == "2014" ? new[] { "Spells", "Races", "Subraces", "Subclasses", "Classes" } : ["Spells", "Species", "Subspecies", "Subclasses", "Classes"])
                .SelectMany(file => DndMcp.Tests.Srd.SrdTestContent.Raw(edition, $"5e-SRD-{file}.json").EnumerateArray().Select(e => e.GetProperty("name").GetString()!)))
            .Distinct(StringComparer.Ordinal)
            .Append("Vextharian")
            .ToList();
        var prose = CampaignWords.Keys(string.Join(' ', AshenCourtSummary, AshenCourtSecret, SeleneSecret, VextharSummary, VextharSecret, statement)).ToHashSet(StringComparer.Ordinal);
        bool HoldsAWordOfTheProse(string name) =>
            CampaignWords.DistinctiveWords(name).Any(w => prose.Contains(w) || prose.Contains(w + "s") || (w.EndsWith('s') && prose.Contains(w[..^1])));

        var result = ViewTextCheck.Check(world.Database, world.Campaign, Perspective.Parse("party"), names.Cast<string?>().ToList());

        var failed = names.Where((_, i) => !result.Passes(i)).ToList();
        Assert.True(names.Count > 400, $"only {names.Count} SRD names");
        Assert.Equal(names.Where(HoldsAWordOfTheProse).Append("Vextharian"), failed);
        Assert.All(new[] { "Dragonborn", "Halfling", "Hunter", "Hunter's Mark", "Moonbeam", "Spiritual Weapon", "Stoneskin", "Thunderwave", "Fighter", "Phantasmal Killer", "Plane Shift", "Longstrider", "Counterspell" },
            name => Assert.True(result.Passes(names.IndexOf(name)), name));
    }

    [Fact]
    public void Check_AsOfASession_HidesWhatTheViewDidNotKnowThen()
    {
        // The party met Captain Vexmoor in session 2: as of session 1 his name was text it could not see. Lower case, so
        // neither the name scanner (the whole name) nor the partial-name flag (a capital) finds "vexmoor": the hidden-word
        // rule alone decides.
        using var world = SheetWorld.Dm();
        world.F.Played(world.Campaign, 1);
        world.F.Played(world.Campaign, 2);
        world.F.Apply(world.Campaign, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Captain Vexmoor", Subtype = "npc", Visibility = "restricted" });
        world.F.Knowledge.Record(world.Campaign, ["character:captain-vexmoor"], [new KnowerSpec { Who = "party", State = "met", Session = 2 }], WriteContext.For(2));

        var then = ViewTextCheck.Check(world.Database, world.Campaign, Perspective.Parse("party"), ["vexmoor's mark"], 1);
        var now = ViewTextCheck.Check(world.Database, world.Campaign, Perspective.Parse("party"), ["vexmoor's mark"]);

        Assert.False(then.Passes(0));
        Assert.Equal(ViewTextFindingKinds.HiddenWord, Assert.Single(then.Texts[0].Findings).Kind);
        Assert.True(now.Passes(0));
    }

    [Fact]
    public void Check_ANameReadAcrossTheJoin_ChecksTheTextsAlone()
    {
        // Joined, "The Old\nKing's" reads as the party's "the old king's"; alone, "King's" is the restricted King's name. A
        // world of its own: the shared ones stay the stock fixtures.
        using var belmakor = new BelmakorScenario();
        belmakor.F.Apply(belmakor.Campaign, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "King", Subtype = "npc", Visibility = "restricted" });
        using var onePiece = OnePieceScenario.Build();
        onePiece.F.Apply(onePiece.Campaign, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "King", Subtype = "npc", Visibility = "restricted" });

        var result = ViewTextCheck.Check(belmakor.F.Db.Database, belmakor.Campaign, Perspective.Parse("party"), ["The Old", "King's"]);
        var other = ViewTextCheck.Check(onePiece.F.Db.Database, onePiece.Campaign, Perspective.Parse("party"), ["The Old", "King's"]);

        Assert.True(result.Passes(0));
        Assert.False(result.Passes(1));
        Assert.False(other.Passes(1));
    }

    [Fact]
    public void Check_TextLongerThanTheChecksCap_FailsUnchecked()
    {
        var result = Belmakor("party", new string('a', CampaignLimits.MaxCheckTextLength + 1), "Torch");

        Assert.False(result.Passes(0));
        Assert.True(result.Passes(1));
    }

    [Fact]
    public void Check_AuthorView_PassesEverythingWithoutFindings()
    {
        var result = OnePiece("author", "The Nester", "Keras", "sealed");

        Assert.True(result.AllPass);
        Assert.All(result.Texts, t => Assert.Empty(t.Findings));
    }

    [Fact]
    public void Check_UnknownCharacterPerspective_IsRefused()
    {
        Assert.Throws<DndMcp.Domain.Core.DndInputException>(() => Belmakor("character:nobody", "Torch"));
    }
}
