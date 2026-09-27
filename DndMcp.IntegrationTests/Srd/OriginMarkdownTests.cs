using DndMcp.Formatting.Srd;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.IntegrationTests.Srd;

/// <summary>
/// Invariant: race, species, subrace, subspecies, trait, background and feat bodies show the vendored data faithfully
/// (every value pinned here was read off the JSON by hand), and a race-like body carries the text of every trait it
/// links, under the trait's ref.
///
/// <para>
/// The two editions share a formatter keyed on field names, so each pin below exists in the edition whose shape it
/// exercises: 2014 run-in prose and subrace ability bonuses, 2024 creature type, size options and trait levels.
/// </para>
/// </summary>
public sealed class OriginMarkdownTests
{
    private static readonly VendoredSrdLookup Lookup = VendoredSrdLookup.Instance;

    public static TheoryData<string> TraitOwners()
    {
        var data = new TheoryData<string>();
        foreach (var kind in new[] { SrdKinds.Race, SrdKinds.Subrace, SrdKinds.Species, SrdKinds.Subspecies })
        {
            foreach (var edition in SrdEdition.All.Where(e => SrdKinds.ExistsIn(kind, e)))
            {
                foreach (var doc in Lookup.OfKind(edition, kind))
                {
                    data.Add(doc.Ref.ToString());
                }
            }
        }

        return data;
    }

    [Theory]
    [InlineData("2014/race/elf", "**Size** Medium")]
    [InlineData("2014/race/elf", "**Speed** 30 ft.")]
    [InlineData("2014/race/dwarf", "**Speed** 25 ft.")]
    [InlineData("2014/race/elf", "**Ability Score Increases** DEX +2")]
    [InlineData("2014/race/dragonborn", "**Ability Score Increases** STR +2, CHA +1")]
    [InlineData("2014/race/human", "**Ability Score Increases** STR +1, DEX +1, CON +1, INT +1, WIS +1, CHA +1")]
    [InlineData("2014/race/elf", "**Languages** Common (`2014/language/common`), Elvish (`2014/language/elvish`)")]
    [InlineData("2014/race/half-elf", "**Ability Score Increase Options** Choose 2 from: STR +1, DEX +1, CON +1, INT +1, WIS +1")]
    [InlineData("2014/race/half-elf",
        "**Language Options** Choose 1 from: Dwarvish, Giant, Gnomish, Goblin, Halfling, Orc, Abyssal, Celestial, Draconic, Deep Speech, Infernal, Primordial, Sylvan, Undercommon")]
    [InlineData("2014/race/elf",
        "***Age.*** Although elves reach physical maturity at about the same age as humans, the elven understanding of adulthood goes beyond physical growth to encompass worldly experience. An elf typically claims adulthood and an adult name around the age of 100 and can live to be 750 years old.")]
    [InlineData("2014/race/elf", "***Size.*** Elves range from under 5 to over 6 feet tall and have slender builds. Your size is Medium.")]
    [InlineData("2014/race/elf", "#### Darkvision (`2014/trait/darkvision`)")]
    [InlineData("2014/race/elf", "High Elf (`2014/subrace/high-elf`)")]
    [InlineData("2024/species/elf", "**Creature Type** Humanoid")]
    [InlineData("2024/species/elf", "#### Elven Lineage (`2024/trait/elven-lineage`)")]
    [InlineData("2024/species/elf",
        "- Elven Lineage: High Elf (`2024/subspecies/elven-lineage-high-elf`) — High Elf: Cantrip Versatility; Detect Magic (level 3); Misty Step (level 5)")]
    [InlineData("2024/species/goliath", "**Speed** 35 ft.")]
    [InlineData("2024/species/tiefling", "**Size** Choose a size when you select this species: Small, Medium")]
    public void Body_RaceOrSpecies_PinsFieldsProseAndLinks(string reference, string expectedLine)
    {
        Assert.Contains(expectedLine, BodyLines(reference));
    }

    [Theory]
    [InlineData("2014/race/elf", "### Subraces")]
    [InlineData("2024/species/elf", "### Subspecies")]
    public void Body_RaceOrSpecies_NamesItsChildrenInItsOwnEditionsWords(string reference, string heading)
    {
        var lines = BodyLines(reference);

        Assert.Contains(heading, lines);
        Assert.DoesNotContain(heading == "### Subraces" ? "### Subspecies" : "### Subraces", lines);
    }

    [Fact]
    public void Body_Dragonborn2014_ShowsTheDraconicAncestryTableUnderTheTraitWhoseTextCitesIt()
    {
        // The trait's text says "Choose one type of dragon from the Draconic Ancestry table", so the race page shows the
        // table (each ancestry's damage type and breath shape), not only a ref to where it is.
        var lines = BodyLines("2014/race/dragonborn");
        var ancestry = Array.IndexOf(lines, "#### Draconic Ancestry (`2014/trait/draconic-ancestry`)");
        var next = Array.FindIndex(lines, ancestry + 1, l => l.StartsWith("#### ", StringComparison.Ordinal));
        Assert.True(ancestry >= 0 && next > ancestry, "Draconic Ancestry is not followed by another trait");
        var trait = lines[ancestry..next];

        Assert.Contains("**Options** (choose 1)", trait);
        Assert.Contains(
            "- Draconic Ancestry (Blue) (`2014/trait/draconic-ancestry-blue`): *Lightning damage; breath weapon 30-foot line, DEX save.*",
            trait);
        Assert.Contains(
            "- Draconic Ancestry (Gold) (`2014/trait/draconic-ancestry-gold`): *Fire damage; breath weapon 15-foot cone, DEX save.*",
            trait);
        Assert.Equal(10, trait.Count(l => l.StartsWith("- Draconic Ancestry (", StringComparison.Ordinal)));
        // The options' own breath-weapon details stay on their trait records.
        Assert.DoesNotContain(lines, l => l.StartsWith("**Area**", StringComparison.Ordinal));
    }

    [Fact]
    public void Body_Dragonborn2024_ShowsTheDraconicAncestryTraitAndEachAncestorsBreathWeaponAndResistance()
    {
        // Upstream linked only Darkvision and Draconic Flight, and the per-colour Breath Weapon and Damage Resistance live
        // on the ten subspecies: the species page read as a 2024 dragonborn with no breath weapon, and beside 2014's
        // traits as a rules change. The corrected record adds Draconic Ancestry; each subspecies line names its traits.
        var lines = BodyLines("2024/species/dragonborn");

        Assert.Contains("#### Draconic Ancestry (`2024/trait/draconic-ancestry`)", lines);
        Assert.Contains(
            "- Draconic Ancestor: Blue (`2024/subspecies/draconic-ancestor-blue`) — Breath Weapon: Lightning; Damage Resistance: Lightning",
            lines);
        Assert.Equal(10, lines.Count(l => l.StartsWith("- Draconic Ancestor: ", StringComparison.Ordinal) && l.Contains(" — Breath Weapon: ", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("2014/race/elf", "2014/trait/darkvision", "#### Darkvision (`2014/trait/darkvision`)")]
    [InlineData("2024/species/elf", "2024/trait/darkvision-60", "#### Darkvision (60 ft.) (`2024/trait/darkvision-60`)")]
    [InlineData("2024/subspecies/elven-lineage-high-elf", "2024/trait/lineage-spell-misty-step",
        "#### Level 5: Misty Step (`2024/trait/lineage-spell-misty-step`)")]
    public void Body_TraitWhoseRecordIsCorrected_SaysSoUnderTheTraitHeading(string owner, string trait, string heading)
    {
        // The trait's own page says its text was corrected (the meta line); the race page inlines the same text and must
        // not pass it off as the untouched upstream record.
        var lookup = new AlteredSrdLookup(Lookup) { Corrected = (trait, "Test reason.") };
        var lines = SrdMarkdown.Body(Lookup.Require(owner), lookup).Split('\n');
        var at = Array.IndexOf(lines, heading);

        Assert.True(at >= 0, "heading missing");
        Assert.Equal("*Corrected from the upstream data: Test reason.*", lines[at + 2]);
        Assert.Equal(SrdProse.Join(Lookup.Require(trait).Root.Description()).Split('\n')[0], lines[at + 4]);
    }

    [Theory]
    [InlineData("2014/subrace/high-elf", "**Race** Elf (`2014/race/elf`)")]
    [InlineData("2014/subrace/high-elf", "**Ability Score Increases** INT +1")]
    [InlineData("2014/subrace/high-elf", "#### High Elf Cantrip (`2014/trait/high-elf-cantrip`)")]
    [InlineData("2014/subrace/high-elf",
        "You know one cantrip of your choice form the wizard spell list. Intelligence is your spellcasting ability for it.")]
    [InlineData("2024/subspecies/elven-lineage-high-elf", "**Species** Elf (`2024/species/elf`)")]
    [InlineData("2024/subspecies/elven-lineage-high-elf",
        "#### Level 1: High Elf: Cantrip Versatility (`2024/trait/high-elf-cantrip-versatility`)")]
    [InlineData("2024/subspecies/elven-lineage-high-elf", "#### Level 5: Misty Step (`2024/trait/lineage-spell-misty-step`)")]
    [InlineData("2024/subspecies/elven-lineage-high-elf", "You know the spell Misty Step.")]
    [InlineData("2024/subspecies/draconic-ancestor-red", "**Damage Type** Fire (`2024/damage-type/fire`)")]
    [InlineData("2024/subspecies/draconic-ancestor-red", "#### Level 1: Breath Weapon: Fire (`2024/trait/draconic-breath-weapon-fire`)")]
    public void Body_SubraceOrSubspecies_PinsParentBonusesAndLeveledTraits(string reference, string expectedLine)
    {
        Assert.Contains(expectedLine, BodyLines(reference));
    }

    [Theory]
    [MemberData(nameof(TraitOwners))]
    public void Body_EveryRaceLikeRecord_InlinesTheTextOfEveryTraitUnderItsRef(string reference)
    {
        var doc = Lookup.Require(reference);
        var body = SrdMarkdown.Body(doc, Lookup);

        foreach (var link in doc.Root.Arr("traits").Concat(doc.Root.Arr("racial_traits")))
        {
            var target = SrdRef.FromApiUrl(link.Str("url"));
            Assert.NotNull(target);
            Assert.Contains($"{link.Str("name")} (`{target}`)", body, StringComparison.Ordinal);

            var trait = Lookup.Get(target.Value.Edition, target.Value.Kind, target.Value.Slug);
            Assert.NotNull(trait);
            // The whole text, as SrdProse lays it out (2024 single newlines become paragraph breaks; words unchanged).
            Assert.Contains(SrdProse.Join(trait.Root.Description()), body, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("2014/trait/darkvision",
        "**Races** Dwarf (`2014/race/dwarf`), Elf (`2014/race/elf`), Gnome (`2014/race/gnome`), Half-Elf (`2014/race/half-elf`), Half-Orc (`2014/race/half-orc`), Tiefling (`2014/race/tiefling`)")]
    [InlineData("2014/trait/high-elf-cantrip", "**Subraces** High Elf (`2014/subrace/high-elf`)")]
    [InlineData("2014/trait/dwarven-combat-training",
        "**Proficiencies** Battleaxes (`2014/proficiency/battleaxes`), Handaxes (`2014/proficiency/handaxes`), Light hammers (`2014/proficiency/light-hammers`), Warhammers (`2014/proficiency/warhammers`)")]
    [InlineData("2014/trait/tool-proficiency", "**Proficiency Choices** Choose 1 from: Smith's Tools, Brewer's Supplies, Mason's Tools")]
    // Spell options are linked: what the cantrip does is the reader's next question.
    [InlineData("2014/trait/high-elf-cantrip",
        "**Spell Options** Choose 1 from: Light (`2014/spell/light`), Mage Hand (`2014/spell/mage-hand`), Mending (`2014/spell/mending`), Message (`2014/spell/message`), Minor Illusion (`2014/spell/minor-illusion`), Acid Splash (`2014/spell/acid-splash`), Prestidigitation (`2014/spell/prestidigitation`), Ray of Frost (`2014/spell/ray-of-frost`), Shocking Grasp (`2014/spell/shocking-grasp`), True Strike (`2014/spell/true-strike`), Chill Touch (`2014/spell/chill-touch`), Dancing Lights (`2014/spell/dancing-lights`), Fire Bolt (`2014/spell/fire-bolt`), Poison Spray (`2014/spell/poison-spray`)")]
    // Dragonborn breath weapon: area, save, uses and damage by character level, from trait_specific.
    [InlineData("2014/trait/draconic-ancestry-red", "**Parent** Draconic Ancestry (`2014/trait/draconic-ancestry`)")]
    [InlineData("2014/trait/draconic-ancestry-red", "**Damage Type** Fire (`2014/damage-type/fire`)")]
    [InlineData("2014/trait/draconic-ancestry-red", "### Breath Weapon")]
    [InlineData("2014/trait/draconic-ancestry-red", "**Area** 15-foot cone")]
    [InlineData("2014/trait/draconic-ancestry-red", "**Save** DEX, half damage on a success")]
    [InlineData("2014/trait/draconic-ancestry-red", "**Uses** 1 per rest")]
    [InlineData("2014/trait/draconic-ancestry-red", "**Damage** Fire (`2014/damage-type/fire`): 2d6 (level 1), 3d6 (6), 4d6 (11), 5d6 (16)")]
    [InlineData("2014/trait/draconic-ancestry-black", "**Area** 30-foot line")]
    [InlineData("2014/trait/draconic-ancestry-green", "**Save** CON, half damage on a success")]
    // The parent's options reproduce the SRD's Draconic Ancestry table.
    [InlineData("2014/trait/draconic-ancestry",
        "- Draconic Ancestry (Black) (`2014/trait/draconic-ancestry-black`): *Acid damage; breath weapon 30-foot line, DEX save.*")]
    [InlineData("2014/trait/draconic-ancestry",
        "- Draconic Ancestry (Silver) (`2014/trait/draconic-ancestry-silver`): *Cold damage; breath weapon 15-foot cone, CON save.*")]
    [InlineData("2024/trait/keen-senses",
        "**Proficiency Choices** Choose one of the following skills: Insight, Perception, or Survival.")]
    [InlineData("2024/trait/lineage-spell-misty-step", "**Spells** Misty Step (`2024/spell/misty-step`)")]
    [InlineData("2024/trait/lineage-spell-misty-step", "**Subspecies** Elven Lineage: High Elf (`2024/subspecies/elven-lineage-high-elf`)")]
    [InlineData("2024/trait/wood-elf-speed-increase", "**Speed** 35 ft.")]
    public void Body_Trait_PinsOwnersChoicesAndTraitSpecificData(string reference, string expectedLine)
    {
        Assert.Contains(expectedLine, BodyLines(reference));
    }

    [Fact]
    public void Body_BreathWeaponWithLevelsOutOfOrder_ListsDamageByNumericCharacterLevel()
    {
        // Upstream happens to list levels in order; a string sort (or none, on a reordered file) would print
        // "4d6 (11)" before "3d6 (6)". Shaped like draconic-ancestry-red's trait_specific.
        var doc = new SrdDocument
        {
            Edition = SrdEdition.Edition2014,
            Kind = SrdKinds.Trait,
            Slug = "test-breath",
            Name = "Test Breath",
            Json = """
                {"index": "test-breath", "name": "Test Breath", "desc": ["Breathes."],
                 "trait_specific": {"breath_weapon": {"name": "Breath Weapon",
                   "damage": [{"damage_type": {"index": "fire", "name": "Fire", "url": "/api/2014/damage-types/fire"},
                               "damage_at_character_level": {"11": "4d6", "1": "2d6", "16": "5d6", "6": "3d6"}}]}}}
                """,
        };

        Assert.Contains(
            "**Damage** Fire (`2014/damage-type/fire`): 2d6 (level 1), 3d6 (6), 4d6 (11), 5d6 (16)",
            SrdMarkdown.Body(doc, Lookup).Split('\n'));
    }

    [Fact]
    public void Body_SkillfulTrait2024_ListsTheSkillsItsSentenceDoesNotName()
    {
        var line = BodyLines("2024/trait/skillful").Single(l => l.StartsWith("**Proficiency Choices**", StringComparison.Ordinal));

        Assert.StartsWith("**Proficiency Choices** Choose any skill: Acrobatics, Animal Handling, Arcana, ", line, StringComparison.Ordinal);
        Assert.EndsWith(", Stealth, Survival", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Body_DraconicAncestry2014_ShowsTheTextThatEveryOptionCopiesOnlyOnce()
    {
        var body = SrdMarkdown.Body(Lookup.Require("2014/trait/draconic-ancestry"), Lookup);

        Assert.Single(body.Split('\n'), l => l.Contains("Choose one type of dragon from the Draconic Ancestry table", StringComparison.Ordinal));
        Assert.Equal(10, body.Split('\n').Count(l => l.StartsWith("- Draconic Ancestry (", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("2014/background/acolyte",
        "**Proficiencies** Skill: Insight (`2014/proficiency/skill-insight`), Skill: Religion (`2014/proficiency/skill-religion`)")]
    [InlineData("2014/background/acolyte", "**Languages** any 2 languages")]
    [InlineData("2014/background/acolyte",
        "**Equipment** Clothes, common (`2014/equipment/clothes-common`), Pouch (`2014/equipment/pouch`), any one of Holy Symbols (`2014/equipment-category/holy-symbols`), 15 gp")]
    [InlineData("2014/background/acolyte", "### Feature: Shelter of the Faithful")]
    [InlineData("2014/background/acolyte", "### Personality Traits (choose 2)")]
    [InlineData("2014/background/acolyte",
        "1. I idolize a particular hero of my faith, and constantly refer to that person's deeds and example.")]
    [InlineData("2014/background/acolyte",
        "8. I've spent so long in the temple that I have little practical experience dealing with people in the outside world.")]
    [InlineData("2014/background/acolyte", "### Ideals (choose 1)")]
    [InlineData("2014/background/acolyte",
        "1. Tradition. The ancient traditions of worship and sacrifice must be preserved and upheld. (Lawful Good, Lawful Neutral, Lawful Evil)")]
    [InlineData("2014/background/acolyte", "### Bonds (choose 1)")]
    [InlineData("2014/background/acolyte", "6. Once I pick a goal, I become obsessed with it to the detriment of everything else in my life.")]
    [InlineData("2024/background/acolyte",
        "**Ability Scores** INT (`2024/ability-score/int`), WIS (`2024/ability-score/wis`), CHA (`2024/ability-score/cha`)")]
    // The reference's note narrows the feat: an acolyte gets the Cleric version of Magic Initiate.
    [InlineData("2024/background/acolyte", "**Feat** Magic Initiate (Cleric) (`2024/feat/magic-initiate`)")]
    [InlineData("2024/background/sage", "**Feat** Magic Initiate (`2024/feat/magic-initiate`)")]
    [InlineData("2024/background/acolyte",
        "**Equipment** Choose A or B: (A) Calligrapher's Supplies, Book (prayers), Holy Symbol, Parchment (10 sheets), Robe, 8 GP; or (B) 50 GP")]
    [InlineData("2024/background/soldier",
        "**Proficiency Choices** Choose one kind of Gaming Set (see \"Equipment\"): Tool: Dice, Tool: Dragonchess, Tool: Playing Cards, Tool: Three-Dragon Ante")]
    public void Body_Background_PinsProficienciesEquipmentAndCharacteristics(string reference, string expectedLine)
    {
        Assert.Contains(expectedLine, BodyLines(reference));
    }

    [Theory]
    [InlineData("2014/feat/grappler", "**Prerequisite** STR 13+")]
    [InlineData("2014/feat/grappler", "- You have advantage on Attack Rolls against a creature you are Grappling.")]
    [InlineData("2024/feat/alert", "**Type** Origin Feat")]
    [InlineData("2024/feat/grappler", "**Type** General Feat")]
    [InlineData("2024/feat/grappler", "**Prerequisite** Level 4+, Strength or Dexterity 13+")]
    [InlineData("2024/feat/archery", "**Type** Fighting Style Feat")]
    [InlineData("2024/feat/archery", "**Prerequisite** Fighting Style Feature")]
    [InlineData("2024/feat/boon-of-spell-recall", "**Type** Epic Boon Feat")]
    [InlineData("2024/feat/boon-of-spell-recall", "**Prerequisite** Level 19+, Spellcasting Feature")]
    [InlineData("2024/feat/magic-initiate",
        "**Repeatable** You can take this feat more than once, but you must choose a different spell list each time.")]
    [InlineData("2024/feat/ability-score-improvement", "**Repeatable** You can take this feat more than once.")]
    [InlineData("2024/feat/archery", "You gain a +2 bonus to attack rolls you make with Ranged weapons.")]
    public void Body_Feat_PinsTypePrerequisitesRepeatableAndText(string reference, string expectedLine)
    {
        Assert.Contains(expectedLine, BodyLines(reference));
    }

    [Theory]
    [InlineData("2024/feat/alert")]
    [InlineData("2024/feat/savage-attacker")]
    public void Body_OriginFeatWithoutPrerequisites_HasNoPrerequisiteOrRepeatableLine(string reference)
    {
        var lines = BodyLines(reference);

        Assert.DoesNotContain(lines, l => l.StartsWith("**Prerequisite**", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("**Repeatable**", StringComparison.Ordinal));
    }

    private static string[] BodyLines(string reference) =>
        SrdMarkdown.Body(Lookup.Require(reference), Lookup).Split('\n');
}
