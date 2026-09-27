using System.Text.Json;
using System.Text.RegularExpressions;
using DndMcp.Formatting.Srd;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.IntegrationTests.Srd;

/// <summary>
/// Invariant: class, subclass, level and feature bodies show the vendored data faithfully (every value pinned here was
/// read off the JSON by hand), and every feature a class or subclass names carries a ref the model can fetch next.
///
/// <para>
/// The level table is where a wrong transform hides best: a dropped zero rule turns "—" into "0", a lost
/// <c>_die</c> rule prints "6" for a d6, an unsorted slot column shifts every spell count one level. So whole rows are
/// pinned, not fragments. Choice rendering (<see cref="SrdChoiceMarkdown"/>) is tested here too, against the real choice
/// objects whose quirks it exists for.
/// </para>
/// </summary>
public sealed partial class ClassMarkdownTests
{
    private static readonly VendoredSrdLookup Lookup = VendoredSrdLookup.Instance;

    public static TheoryData<string> Classes() => RefsOf(SrdKinds.Class);

    public static TheoryData<string> Subclasses() => RefsOf(SrdKinds.Subclass);

    public static TheoryData<string> Subclasses2024()
    {
        var data = new TheoryData<string>();
        foreach (var doc in Lookup.OfKind(SrdEdition.Edition2024, SrdKinds.Subclass))
        {
            data.Add(doc.Ref.ToString());
        }

        return data;
    }

    [Theory]
    [InlineData("2014/class/fighter", "**Hit Die** d10")]
    [InlineData("2014/class/wizard", "**Hit Die** d6")]
    [InlineData("2024/class/barbarian", "**Hit Die** d12")]
    [InlineData("2024/class/fighter", "**Primary Ability** Strength or Dexterity")]
    [InlineData("2024/class/monk", "**Primary Ability** Dexterity and Wisdom")]
    [InlineData("2014/class/fighter", "**Saving Throws** STR (`2014/ability-score/str`), CON (`2014/ability-score/con`)")]
    [InlineData("2024/class/wizard",
        "**Proficiencies** Simple Weapons (`2024/proficiency/simple-weapons`), Saving Throw: INT (`2024/proficiency/saving-throw-int`), Saving Throw: WIS (`2024/proficiency/saving-throw-wis`)")]
    [InlineData("2014/class/fighter",
        "**Proficiency Choices** Choose two skills from Acrobatics, Animal Handling, Athletics, History, Insight, Intimidation, Perception, and Survival")]
    [InlineData("2014/class/wizard", "**Starting Equipment** Spellbook (`2014/equipment/spellbook`)")]
    [InlineData("2014/class/barbarian",
        "**Starting Equipment** Explorer's Pack (`2014/equipment/explorers-pack`), 4 × Javelin (`2014/equipment/javelin`)")]
    [InlineData("2014/class/cleric", "- (a) a mace or (b) a warhammer (if proficient)")]
    [InlineData("2014/class/cleric", "- any one of Holy Symbols (`2014/equipment-category/holy-symbols`)")]
    [InlineData("2024/class/wizard",
        "**Starting Equipment Options** (a) 2 Daggers, Arcane Focus (Quarterstaff), Robe, Spellbook, Scholar’s Pack, and 5 GP; or (b) 55 GP")]
    [InlineData("2014/class/fighter", "**Multiclassing Prerequisites** STR 13+ or DEX 13+")]
    [InlineData("2014/class/monk", "**Multiclassing Prerequisites** DEX 13+ and WIS 13+")]
    [InlineData("2024/class/barbarian",
        "**Multiclassing Proficiencies** Shields (`2024/proficiency/shields`), Martial Weapons (`2024/proficiency/martial-weapons`)")]
    [InlineData("2014/class/wizard", "**Spellcasting** INT (`2014/ability-score/int`), from level 1")]
    [InlineData("2014/class/paladin", "**Spellcasting** CHA (`2014/ability-score/cha`), from level 2")]
    [InlineData("2024/class/fighter", "Champion (`2024/subclass/champion`)")]
    [InlineData("2014/class/bard", "Lore (`2014/subclass/lore`)")]
    public void Body_Class_PrintsEachFieldAsOneLine(string reference, string expectedLine)
    {
        Assert.Contains(expectedLine, BodyLines(reference));
    }

    [Theory]
    // 2014 fighter: its own three columns; indomitable uses 0 at level 5 is "—", not "0".
    [InlineData("2014/class/fighter", "| Level | PB | Features | Action Surges | Indomitable Uses | Extra Attacks |")]
    [InlineData("2014/class/fighter", "| 5 | +3 | Extra Attack (`2014/feature/extra-attack-1`) | 1 | — | 1 |")]
    [InlineData("2024/class/fighter", "| Level | PB | Features | Second Wind Uses | Weapon Mastery |")]
    [InlineData("2024/class/fighter",
        "| 5 | +3 | Extra Attack (`2024/feature/fighter-extra-attack`), Tactical Shift (`2024/feature/fighter-tactical-shift`) | 3 | 4 |")]
    // Sneak attack is {dice_count: 3, dice_value: 6} at level 5 in both editions.
    [InlineData("2014/class/rogue", "| 5 | +3 | Uncanny Dodge (`2014/feature/uncanny-dodge`) | 3d6 |")]
    [InlineData("2024/class/rogue",
        "| 5 | +3 | Cunning Strike (`2024/feature/rogue-cunning-strike`), Uncanny Dodge (`2024/feature/rogue-uncanny-dodge`) | 3d6 |")]
    // Wizard 5: 4/3/2 slots. 2014 has an arcane recovery column and no prepared count; 2024 the reverse.
    [InlineData("2014/class/wizard",
        "| Level | PB | Features | Arcane Recovery Levels | Cantrips Known | 1st | 2nd | 3rd | 4th | 5th | 6th | 7th | 8th | 9th |")]
    [InlineData("2014/class/wizard", "| 5 | +3 | — | 3 | 4 | 4 | 3 | 2 | — | — | — | — | — | — |")]
    [InlineData("2024/class/wizard",
        "| Level | PB | Features | Cantrips Known | Prepared Spells | 1st | 2nd | 3rd | 4th | 5th | 6th | 7th | 8th | 9th |")]
    [InlineData("2024/class/wizard", "| 5 | +3 | Memorize Spell (`2024/feature/wizard-memorize-spell`) | 4 | 9 | 4 | 3 | 2 | — | — | — | — | — | — |")]
    // Warlock pact slots are all of one level: at 5, two 3rd-level slots and none below. Only slot levels 1–5 exist.
    [InlineData("2014/class/warlock",
        "| Level | PB | Features | Invocations Known | Mystic Arcanum Level 6 | Mystic Arcanum Level 7 | Mystic Arcanum Level 8 | Mystic Arcanum Level 9 | Cantrips Known | Spells Known | 1st | 2nd | 3rd | 4th | 5th |")]
    [InlineData("2014/class/warlock", "| 5 | +3 | — | 3 | — | — | — | — | 3 | 6 | — | — | 2 | — | — |")]
    [InlineData("2014/class/warlock",
        "| 20 | +6 | Eldritch Master (`2014/feature/eldritch-master`) | 8 | 1 | 1 | 1 | 1 | 4 | 15 | — | — | — | — | 4 |")]
    [InlineData("2024/class/warlock",
        "| Level | PB | Features | Eldritch Invocations | Cantrips Known | Prepared Spells | 1st | 2nd | 3rd | 4th | 5th |")]
    [InlineData("2024/class/warlock", "| 5 | +3 | — | 5 | 3 | 6 | — | — | 2 | — | — |")]
    // 9999 is upstream's "Unlimited"; rage damage is a bonus, so signed.
    [InlineData("2014/class/barbarian",
        "| 20 | +6 | Primal Champion (`2014/feature/primal-champion`) | Unlimited | +4 | 3 |")]
    // *_die sizes are dice; a die the class does not have yet (song of rest at 1) is "—".
    [InlineData("2014/class/bard",
        "| 1 | +2 | Spellcasting: Bard (`2014/feature/spellcasting-bard`), Bardic Inspiration (d6) (`2014/feature/bardic-inspiration-d6`) | d6 | — | — | — | — | 2 | 4 | 2 | — | — | — | — | — | — | — | — |")]
    [InlineData("2024/class/monk",
        "| 5 | +3 | Extra Attack (`2024/feature/monk-extra-attack`), Stunning Strike (`2024/feature/monk-stunning-strike`) | d8 | 5 | +10 ft. |")]
    [InlineData("2014/class/monk",
        "| 5 | +3 | Extra Attack (`2014/feature/monk-extra-attack`), Stunning Strike (`2014/feature/stunning-strike`) | 1d6 | 5 | +10 ft. |")]
    // Unarmored Movement is a speed increase in both editions (the SRD's column reads "+10 ft."), whatever upstream's key.
    [InlineData("2014/class/monk",
        "| 2 | +2 | Ki (`2014/feature/ki`), Flurry of Blows (`2014/feature/flurry-of-blows`), Patient Defense (`2014/feature/patient-defense`), Step of the Wind (`2014/feature/step-of-the-wind`), Unarmored Movement (`2014/feature/unarmored-movement-1`) | 1d4 | 2 | +10 ft. |")]
    [InlineData("2014/class/monk", "| 18 | +6 | Empty Body (`2014/feature/empty-body`) | 1d10 | 18 | +30 ft. |")]
    [InlineData("2014/class/monk", "| 1 | +2 | Unarmored Defense (`2014/feature/monk-unarmored-defense`), Martial Arts (`2014/feature/martial-arts`) | 1d4 | — | — |")]
    // Fractional CRs print as the SRD prints them; booleans as Yes/No.
    [InlineData("2014/class/druid",
        "| 2 | +2 | Wild Shape (CR 1/4 or below, no flying or swim speed) (`2014/feature/wild-shape-cr-1-4-or-below-no-flying-or-swim-speed`), Druid Circle (`2014/feature/druid-circle`) | 1/4 | No | No | 2 | 3 | — | — | — | — | — | — | — | — |")]
    [InlineData("2014/class/cleric",
        "| 5 | +3 | Domain Spells (`2014/feature/domain-spells-3`), Destroy Undead (CR 1/2 or below) (`2014/feature/destroy-undead-cr-1-2-or-below`) | 1 | 1/2 | 4 | 4 | 3 | 2 | — | — | — | — | — | — |")]
    // Half-caster: no cantrips and no slot above 5th, ever, so those columns are absent.
    [InlineData("2014/class/paladin", "| Level | PB | Features | Aura Range | 1st | 2nd | 3rd | 4th | 5th |")]
    public void Body_ClassLevelTable_PinsHeaderAndRowFromTheLevelRecords(string reference, string expectedRow)
    {
        Assert.Contains(expectedRow, BodyLines(reference));
    }

    [Theory]
    [InlineData("2014/class/fighter")]
    [InlineData("2024/class/fighter")]
    [InlineData("2024/class/rogue")]
    public void Body_ClassWithoutSpellcasting_HasNoSpellColumns(string reference)
    {
        var header = BodyLines(reference).Single(l => l.StartsWith("| Level |", StringComparison.Ordinal));

        Assert.DoesNotContain("1st", header, StringComparison.Ordinal);
        Assert.DoesNotContain("Cantrips", header, StringComparison.Ordinal);
    }

    [Fact]
    public void Body_Sorcerer2014_KeepsTheListValuedColumnOutOfTheTableButShowsItOnTheLevel()
    {
        var header = BodyLines("2014/class/sorcerer").Single(l => l.StartsWith("| Level |", StringComparison.Ordinal));

        Assert.Equal(
            "| Level | PB | Features | Sorcery Points | Metamagic Known | Cantrips Known | Spells Known | 1st | 2nd | 3rd | 4th | 5th | 6th | 7th | 8th | 9th |",
            header);
        Assert.Contains(
            "**Creating Spell Slots** Spell Slot Level 1, Sorcery Point Cost 2; Spell Slot Level 2, Sorcery Point Cost 3; Spell Slot Level 3, Sorcery Point Cost 5; Spell Slot Level 4, Sorcery Point Cost 6; Spell Slot Level 5, Sorcery Point Cost 7",
            BodyLines("2014/level/sorcerer-5"));
    }

    [Theory]
    [MemberData(nameof(Classes))]
    public void Body_EveryClass_ShowsTheRefOfEveryFeatureItsLevelsName(string reference)
    {
        var doc = Lookup.Require(reference);
        var body = Body(doc);
        var levels = Lookup.ClassLevels(doc.Edition, doc.Slug);

        Assert.Equal(20, levels.Count);
        foreach (var feature in levels.SelectMany(l => l.Root.Arr("features")))
        {
            var target = SrdRef.FromApiUrl(feature.Str("url"));
            Assert.NotNull(target);
            Assert.Contains($"{feature.Str("name")} (`{target}`)", body, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Classes))]
    public void Format_EveryClass_StaysUnderTheTypicalRecordBudget(string reference)
    {
        var text = SrdMarkdown.Format(Lookup.Require(reference), SrdMarkdown.Concise, Lookup);

        // The brief's target for a full 20-level caster; the hard cap is 32,000.
        Assert.True(text.Length < 8_000, $"{reference}: {text.Length} characters.");
    }

    [Theory]
    [InlineData("2014/subclass/champion", "**Class** Fighter (`2014/class/fighter`)")]
    [InlineData("2014/subclass/champion", "**Subclass Type** Martial Archetype")]
    [InlineData("2014/subclass/champion", "#### Level 3: Improved Critical (`2014/feature/improved-critical`)")]
    [InlineData("2014/subclass/champion",
        "Beginning when you choose this archetype at 3rd level, your weapon attacks score a critical hit on a roll of 19 or 20.")]
    [InlineData("2024/subclass/champion", "*Pursue Physical Excellence in Combat*")]
    [InlineData("2024/subclass/champion", "#### Level 3: Improved Critical (`2024/feature/champion-improved-critical`)")]
    [InlineData("2024/subclass/champion",
        "Your attack rolls with weapons and Unarmed Strikes can score a Critical Hit on a roll of 19 or 20 on the d20.")]
    [InlineData("2014/subclass/lore", "**Subclass Type** Bard College")]
    [InlineData("2014/subclass/lore", "#### Level 6: Additional Magical Secrets (`2014/feature/additional-magical-secrets`)")]
    [InlineData("2014/subclass/lore", "| Level | Additional Magical Secrets Max Lvl |")]
    [InlineData("2014/subclass/lore", "| 6 | 3 |")]
    [InlineData("2024/subclass/college-of-lore", "#### Level 3: Bonus Proficiencies (`2024/feature/lore-bonus-proficiencies`)")]
    [InlineData("2024/subclass/college-of-lore", "#### Level 6: Magical Discoveries (`2024/feature/lore-magical-discoveries`)")]
    // 2014 domain spells as Level | Spells rows, each level and spell linked.
    [InlineData("2014/subclass/life", "| Cleric 1 (`2014/level/cleric-1`) | Bless (`2014/spell/bless`), Cure Wounds (`2014/spell/cure-wounds`) |")]
    [InlineData("2014/subclass/life", "| Cleric 7 (`2014/level/cleric-7`) | Death Ward (`2014/spell/death-ward`) |")]
    [InlineData("2014/subclass/devotion",
        "| Paladin 17 (`2014/level/paladin-17`) | Commune (`2014/spell/commune`), Flame Strike (`2014/spell/flame-strike`) |")]
    // Devotion's aura range lives in subclass_specific; level 18 has no feature but still a value.
    [InlineData("2014/subclass/devotion", "| Level | Aura Range |")]
    [InlineData("2014/subclass/devotion", "| 18 | 30 ft. |")]
    // 2014 Land: one spell table per terrain, grouped under the terrain feature the spells also require.
    [InlineData("2014/subclass/land", "**Circle of the Land: Arctic (`2014/feature/circle-of-the-land-arctic`)**")]
    [InlineData("2014/subclass/land",
        "| Druid 3 (`2014/level/druid-3`) | Hold Person (`2014/spell/hold-person`), Spike Growth (`2014/spell/spike-growth`) |")]
    // A 2014 sub-feature choice inside a subclass keeps each option's own text.
    [InlineData("2014/subclass/hunter",
        "- Hunter's Prey: Colossus Slayer (`2014/feature/hunters-prey-colossus-slayer`): Your tenacity can wear down the most potent foes. When you hit a creature with a weapon attack, the creature takes an extra 1d8 damage if it's below its hit point maximum. You can deal this extra damage only once per turn.")]
    public void Body_Subclass_PinsFieldsFeaturesAndSpells(string reference, string expectedLine)
    {
        Assert.Contains(expectedLine, BodyLines(reference));
    }

    [Fact]
    public void Body_Land2014_GroupsSpellsUnderEachOfTheSevenTerrains()
    {
        var headings = BodyLines("2014/subclass/land").Where(l => l.StartsWith("**Circle of the Land: ", StringComparison.Ordinal)).ToList();

        Assert.Equal(
            new[] { "Arctic", "Coast", "Desert", "Forest", "Grassland", "Mountain", "Swamp" },
            headings.Select(h => h["**Circle of the Land: ".Length..h.IndexOf(" (", StringComparison.Ordinal)]));
    }

    [Fact]
    public void Body_Devotion2024_ShowsTheOathSpellsTableOfTheFeatureRecordNotTheGarbledInlineCopy()
    {
        // Upstream's inline copy of this feature is a PDF extraction of the wrong table: it drops the level 3, 9 and 17
        // oath spells and adds rows of the Paladin spell list. The feature record the heading links has the SRD's table.
        var section = FeatureSection(Body(Lookup.Require("2024/subclass/oath-of-devotion")),
            "#### Level 3: Oath of Devotion Spells (`2024/feature/devotion-oath-of-devotion-spells`)");

        Assert.Contains("Protection from Evil and Good", section, StringComparison.Ordinal);
        Assert.Contains("Aid, Zone of Truth", section, StringComparison.Ordinal);
        Assert.Contains("Beacon of Hope, Dispel Magic", section, StringComparison.Ordinal);
        Assert.Contains("Freedom of Movement, Guardian of Faith", section, StringComparison.Ordinal);
        Assert.Contains("Commune, Flame Strike", section, StringComparison.Ordinal);
        Assert.DoesNotContain("Magic Circle", section, StringComparison.Ordinal);
        Assert.DoesNotContain("Spell School Special", section, StringComparison.Ordinal);
    }

    [Theory]
    // The subclass body and rules_get on the feature's ref say the same thing, so both must list the level-3 spells.
    // Upstream's feature record drops these three; content/srd-corrections.json restores them (CHAR-1, data side).
    [InlineData("2024/subclass/circle-of-the-land", "Ray of Frost")]
    [InlineData("2024/subclass/circle-of-the-land", "Shocking Grasp")]
    [InlineData("2024/subclass/circle-of-the-land", "Sleep")]
    [InlineData("2024/feature/land-circle-of-the-land-spells", "Ray of Frost")]
    [InlineData("2024/feature/land-circle-of-the-land-spells", "Shocking Grasp")]
    [InlineData("2024/feature/land-circle-of-the-land-spells", "Sleep")]
    public void Body_Land2024CircleSpells_ListEveryLevel3Spell(string reference, string spell)
    {
        var body = Body(Lookup.Require(reference));
        var text = reference.Contains("/subclass/", StringComparison.Ordinal)
            ? FeatureSection(body, "#### Level 3: Circle of the Land Spells (`2024/feature/land-circle-of-the-land-spells`)")
            : body;

        Assert.True(text.Contains(spell, StringComparison.Ordinal),
            $"{reference} does not list {spell}; the land-circle-of-the-land-spells record needs its correction in content/srd-corrections.json.");
    }

    [Fact]
    public void Body_Land2024_ShowsTheSentenceUpstreamRanIntoTheInlineNameExactlyOnce()
    {
        // Upstream's inline name is "Circle of the Land Spells Whenever you finish a Long Rest, …". The heading keeps the
        // real name and ref, and the body shows the feature record, whose text starts with that sentence: rescuing it
        // from the name as well would print it twice.
        var body = Body(Lookup.Require("2024/subclass/circle-of-the-land"));
        var text = FeatureSection(body, "#### Level 3: Circle of the Land Spells (`2024/feature/land-circle-of-the-land-spells`)")
            .Split("\n\n")
            .SkipWhile(p => p.StartsWith("*Corrected from the upstream data: ", StringComparison.Ordinal))
            .First();

        Assert.StartsWith("Whenever you finish a Long Rest, choose one type of land: arid, polar, temperate, or tropical.",
            text, StringComparison.Ordinal);
        Assert.Single(body.Split('\n'),
            l => l.Contains("Whenever you finish a Long Rest, choose one type of land", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Subclasses2024))]
    public void Body_Subclass2024_ShowsTheTextOfTheFeatureRecordEachHeadingLinks(string reference)
    {
        // What rules_get returns for the feature's ref is what the subclass body says under that ref: one source, so a
        // model never sees two versions of the same feature.
        var doc = Lookup.Require(reference);
        var body = Body(doc);

        foreach (var level in Lookup.SubclassLevels(doc.Edition, doc.Slug))
        {
            foreach (var link in level.Root.Arr("features"))
            {
                var target = SrdRef.FromApiUrl(link.Str("url"))!.Value;
                var record = Lookup.Get(target.Edition, target.Kind, target.Slug);
                Assert.NotNull(record);

                var heading = $"#### Level {level.Root.Int("level")}: {link.Str("name")} (`{target}`)";
                var expected = record.Corrections.Select(reason => $"*Corrected from the upstream data: {reason.Trim()}*")
                    .Append(SrdProse.Join(record.Root.Description()));
                Assert.Equal(string.Join("\n\n", expected), FeatureSection(body, heading));
            }
        }
    }

    /// <summary>
    /// Word differences between a 2024 subclass's inline feature text and the feature record its level records link,
    /// in the vendored bytes (before any curated correction). Upstream keeps these twins in separate files, and a data
    /// bump that changes one and not the other must be looked at: the body renders the record, so a record that loses
    /// words the inline copy keeps is a silent regression. Each entry says which side is right.
    /// </summary>
    private static readonly Dictionary<string, string> KnownInlineRecordDifferences = new(StringComparer.Ordinal)
    {
        ["circle-of-the-land/land-circle-of-the-land-spells"] =
            "record wrong: drops Ray of Frost (Polar) and Shocking Grasp and Sleep (Temperate) from the level-3 rows",
        ["circle-of-the-land/land-natures-ward"] =
            "inline wrong: repeats the table header (\"Land Type Resistance\") where the SRD's two-column table wrapped",
        ["college-of-lore/lore-cutting-words"] =
            "record wrong: keeps two line-break hyphenations from the PDF (\"com- petence\", \"suc- cess\")",
        ["oath-of-devotion/devotion-oath-of-devotion-spells"] =
            "inline wrong: the wrong table, extracted from the PDF (no level 3, 9 or 17 spells; Paladin spell-list rows added); " +
            "the record has the SRD's table, with the typo \"Shielf of Faith\"",
    };

    [Fact]
    public void InlineFeatures2024_WordDiffedAgainstTheirVendoredFeatureRecords_DifferOnlyWhereKnown()
    {
        var subclasses = VendoredRecords(SrdEdition.Edition2024, SrdKinds.Subclass);
        var features = VendoredRecords(SrdEdition.Edition2024, SrdKinds.Feature);
        var pairs = 0;
        var differing = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var (slug, subclass) in subclasses)
        {
            var links = Lookup.SubclassLevels(SrdEdition.Edition2024, slug)
                .SelectMany(level => level.Root.Arr("features").Select(link => (Level: level.Root.Int("level"), Link: link)))
                .ToList();

            foreach (var inline in subclass.Arr("features"))
            {
                var name = inline.Str("name")!.Trim();
                var match = links.FindIndex(l => l.Level == inline.Int("level") && l.Link.Str("name") is { } n &&
                                                 (name == n || name.StartsWith(n + " ", StringComparison.Ordinal)));
                Assert.True(match >= 0, $"{slug}: inline feature '{name}' has no level-record feature of the same level and name.");

                var link = links[match].Link;
                links.RemoveAt(match);
                var recordSlug = SrdRef.FromApiUrl(link.Str("url"))!.Value.Slug;
                var inlineWords = Words(name[link.Str("name")!.Length..] + " " + string.Join(" ", inline.Description()));
                var recordWords = Words(string.Join(" ", features[recordSlug].Description()));
                pairs++;

                if (!inlineWords.SequenceEqual(recordWords))
                {
                    differing[$"{slug}/{recordSlug}"] = WordDiff(inlineWords, recordWords);
                }
            }
        }

        var unexpected = differing.Where(d => !KnownInlineRecordDifferences.ContainsKey(d.Key))
            .Select(d => $"{d.Key}: {d.Value}").ToList();
        var stale = KnownInlineRecordDifferences.Keys.Where(k => !differing.ContainsKey(k)).ToList();

        Assert.Equal(58, pairs);
        Assert.True(unexpected.Count == 0,
            "Inline and record text now differ (decide which is right; correct the record in content/srd-corrections.json " +
            "or list the pair here):\n" + string.Join("\n", unexpected));
        Assert.True(stale.Count == 0, "These pairs now agree; remove them from the known differences: " + string.Join(", ", stale));
    }

    /// <summary>
    /// 2024 subclass features whose rendered record (corrections applied) has fewer table rows keyed by a class level
    /// than the subclass's inline copy, and why that is right. The word diff above cannot see line structure: the Draconic
    /// Spells record ran rows 5 and 7 together ("5 | Fear, Fly 7 | Arcane Eye, Charm Monster") with the same words as the
    /// inline copy's two rows, and the switch to rendering the record shipped that merged row.
    /// </summary>
    private static readonly Dictionary<string, string> KnownLevelRowDifferences = new(StringComparer.Ordinal)
    {
    };

    [Fact]
    public void InlineFeatures2024_LevelRowsOfTheRenderedRecord_AreAtLeastTheInlineCopys()
    {
        var subclasses = VendoredRecords(SrdEdition.Edition2024, SrdKinds.Subclass);
        var differing = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var rowsSeen = 0;

        foreach (var (slug, subclass) in subclasses)
        {
            var links = Lookup.SubclassLevels(SrdEdition.Edition2024, slug)
                .SelectMany(level => level.Root.Arr("features").Select(link => (Level: level.Root.Int("level"), Link: link)))
                .ToList();

            foreach (var inline in subclass.Arr("features"))
            {
                var name = inline.Str("name")!.Trim();
                var match = links.FindIndex(l => l.Level == inline.Int("level") && l.Link.Str("name") is { } n &&
                                                 (name == n || name.StartsWith(n + " ", StringComparison.Ordinal)));
                var link = links[match].Link;
                links.RemoveAt(match);
                var record = Lookup.Require(SrdRef.FromApiUrl(link.Str("url"))!.Value.ToString());

                var inlineRows = LevelRows(inline.Description());
                var recordRows = LevelRows(record.Root.Description());
                rowsSeen += inlineRows;
                if (recordRows < inlineRows)
                {
                    differing[$"{slug}/{record.Slug}"] = $"inline {inlineRows} level rows, rendered record {recordRows}";
                }
            }
        }

        var unexpected = differing.Where(d => !KnownLevelRowDifferences.ContainsKey(d.Key)).Select(d => $"{d.Key}: {d.Value}").ToList();
        var stale = KnownLevelRowDifferences.Keys.Where(k => !differing.ContainsKey(k)).ToList();

        Assert.True(rowsSeen > 20, $"Only {rowsSeen} level rows found in the inline copies; the row pattern no longer matches them.");
        Assert.True(unexpected.Count == 0,
            "A rendered feature record has fewer level rows than its inline copy (a merged or dropped row; correct it in " +
            "content/srd-corrections.json or list it here):\n" + string.Join("\n", unexpected));
        Assert.True(stale.Count == 0, "These now agree; remove them from the known differences: " + string.Join(", ", stale));
    }

    // Lines that open with a class level, in any of the shapes upstream and the corrections use: "5 Fear, Fly",
    // "5 | Fear, Fly", "| 5 | Fear, Fly |", "5 / Fear".
    private static int LevelRows(IEnumerable<string> paragraphs) =>
        paragraphs.SelectMany(p => p.Split('\n')).Count(l => LevelRow().IsMatch(l.Trim()));

    [GeneratedRegex(@"^\|?\s*\d{1,2}\s*(?:\||/|\s+\S)")]
    private static partial Regex LevelRow();

    [Fact]
    public void Body_Subclass2024WhoseFeatureRecordIsMissing_FallsBackToTheInlineTextUnderTheSameHeading()
    {
        // The inline copy is all there is then; the heading still carries the ref the level record names.
        var lookup = new AlteredSrdLookup(Lookup) { HiddenRef = "2024/feature/devotion-oath-of-devotion-spells" };
        var section = FeatureSection(SrdMarkdown.Body(Lookup.Require("2024/subclass/oath-of-devotion"), lookup),
            "#### Level 3: Oath of Devotion Spells (`2024/feature/devotion-oath-of-devotion-spells`)");

        Assert.StartsWith("The magic of your oath ensures you always have certain spells ready;", section, StringComparison.Ordinal);
        Assert.Contains("Magic Circle Abjuration M", section, StringComparison.Ordinal);
    }

    [Fact]
    public void Body_Land2024WhoseFeatureRecordIsMissing_SplitsTheSentenceUpstreamRanIntoTheInlineName()
    {
        var lookup = new AlteredSrdLookup(Lookup) { HiddenRef = "2024/feature/land-circle-of-the-land-spells" };
        var lines = SrdMarkdown.Body(Lookup.Require("2024/subclass/circle-of-the-land"), lookup).Split('\n');
        var heading = Array.IndexOf(lines, "#### Level 3: Circle of the Land Spells (`2024/feature/land-circle-of-the-land-spells`)");

        Assert.True(heading >= 0, "heading missing");
        Assert.Equal(
            "Whenever you finish a Long Rest, choose one type of land: arid, polar, temperate, or tropical. Consult the table below that corresponds to the chosen type; you have the spells listed for your Druid level and lower prepared.",
            lines[heading + 2]);
        Assert.Equal("Arid Land", lines[heading + 4]);
        Assert.Contains("3 Fog Cloud, Hold Person, Ray of Frost", lines);
    }

    [Theory]
    [InlineData("2024/subclass/champion", "2024/feature/champion-improved-critical",
        "Your attack rolls with weapons and Unarmed Strikes can score a Critical Hit on a roll of 19 or 20 on the d20.")]
    [InlineData("2014/subclass/champion", "2014/feature/improved-critical",
        "Beginning when you choose this archetype at 3rd level, your weapon attacks score a critical hit on a roll of 19 or 20.")]
    public void Body_SubclassFeatureWhoseRecordIsCorrected_SaysSoUnderTheFeatureHeading(string subclass, string feature, string text)
    {
        // The feature's own page says its text was corrected (the meta line); the subclass page shows the same text and
        // must not pass it off as the untouched upstream record.
        var lookup = new AlteredSrdLookup(Lookup) { Corrected = (feature, "Test reason.") };
        var section = FeatureSection(SrdMarkdown.Body(Lookup.Require(subclass), lookup),
            $"#### Level 3: Improved Critical (`{feature}`)");

        Assert.Equal($"*Corrected from the upstream data: Test reason.*\n\n{text}", section);
    }

    [Fact]
    public void Body_Subclass2024WithoutLevelRecords_HeadsEachInlineFeatureByItsOwnNameAndText()
    {
        var lookup = new AlteredSrdLookup(Lookup) { HideSubclassLevels = true };
        var lines = SrdMarkdown.Body(Lookup.Require("2024/subclass/champion"), lookup).Split('\n');
        var heading = Array.IndexOf(lines, "#### Level 3: Improved Critical");

        Assert.True(heading >= 0, "heading missing");
        Assert.Equal(
            "Your attack rolls with weapons and Unarmed Strikes can score a Critical Hit on a roll of 19 or 20 on the d20.",
            lines[heading + 2]);
    }

    [Theory]
    [MemberData(nameof(Subclasses))]
    public void Body_EverySubclass_ShowsEveryFeatureItsLevelRecordsLink(string reference)
    {
        var doc = Lookup.Require(reference);
        var body = Body(doc);
        var features = Lookup.SubclassLevels(doc.Edition, doc.Slug).SelectMany(l => l.Root.Arr("features")).ToList();

        Assert.NotEmpty(features);
        foreach (var feature in features)
        {
            Assert.Contains($"(`{SrdRef.FromApiUrl(feature.Str("url"))}`)", body, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("**Other features at these levels**", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2014/level/fighter-5", "**Class** Fighter (`2014/class/fighter`)")]
    [InlineData("2014/level/fighter-5", "**Proficiency Bonus** +3")]
    [InlineData("2014/level/fighter-5", "**Ability Score Improvements** 1 gained by this level")]
    [InlineData("2014/level/fighter-5", "**Features** Extra Attack (`2014/feature/extra-attack-1`)")]
    [InlineData("2014/level/fighter-5", "**Indomitable Uses** —")]
    [InlineData("2024/level/fighter-5",
        "**Features** Extra Attack (`2024/feature/fighter-extra-attack`), Tactical Shift (`2024/feature/fighter-tactical-shift`)")]
    [InlineData("2024/level/fighter-5", "**Weapon Mastery** 4")]
    [InlineData("2024/level/rogue-5", "**Sneak Attack** 3d6")]
    [InlineData("2024/level/wizard-5", "**Prepared Spells** 9")]
    [InlineData("2024/level/wizard-5", "**Spell Slots** 1st 4 · 2nd 3 · 3rd 2")]
    [InlineData("2014/level/warlock-5", "**Spell Slots** 3rd 2")]
    [InlineData("2014/level/sorcerer-5", "**Features** none gained at this level")]
    [InlineData("2014/level/champion-3", "**Subclass** Champion (`2014/subclass/champion`)")]
    [InlineData("2014/level/devotion-18", "**Aura Range** 30 ft.")]
    [InlineData("2014/level/monk-2", "**Unarmored Movement** +10 ft.")]
    [InlineData("2024/level/monk-2", "**Unarmored Movement Bonus** +10 ft.")]
    [InlineData("2024/level/berserker-3", "**Subclass** Path of the Berserker (`2024/subclass/path-of-the-berserker`)")]
    public void Body_Level_PinsItsValues(string reference, string expectedLine)
    {
        Assert.Contains(expectedLine, BodyLines(reference));
    }

    [Fact]
    public void Body_LevelWithEmptySpellSlots_OmitsTheSlotsLine()
    {
        // 2014 paladin 1 has a spellcasting object whose every slot is 0: "Spell Slots" with nothing after it would mislead.
        Assert.DoesNotContain(BodyLines("2014/level/paladin-1"), l => l.StartsWith("**Spell Slots**", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("2014/feature/extra-attack-1", "**Class** Fighter (`2014/class/fighter`)")]
    [InlineData("2014/feature/extra-attack-1", "**Level** 5")]
    [InlineData("2024/feature/champion-improved-critical", "**Subclass** Champion (`2024/subclass/champion`)")]
    [InlineData("2024/feature/champion-improved-critical", "**Level** Fighter 3 (`2024/level/fighter-3`)")]
    [InlineData("2014/feature/fighter-fighting-style-archery", "**Parent** Fighting Style (`2014/feature/fighter-fighting-style`)")]
    [InlineData("2014/feature/eldritch-invocation-thirsting-blade",
        "**Prerequisites** level 5, Pact of the Blade (`2014/feature/pact-of-the-blade`)")]
    [InlineData("2014/feature/fighter-fighting-style", "**Options** (choose 1)")]
    [InlineData("2014/feature/fighter-fighting-style",
        "- Fighting Style: Archery (`2014/feature/fighter-fighting-style-archery`): You gain a +2 bonus to attack rolls you make with ranged weapons.")]
    [InlineData("2014/feature/eldritch-invocations", "**Invocations**")]
    [InlineData("2014/feature/eldritch-invocations",
        "- Eldritch Invocation: Agonizing Blast (`2014/feature/eldritch-invocation-agonizing-blast`): *Prerequisite: Eldritch Blast (`2014/spell/eldritch-blast`).* When you cast eldritch blast, add your Charisma modifier to the damage it deals on a hit.")]
    // Later paragraphs of an option stay inside its bullet, indented.
    [InlineData("2014/feature/pact-boon",
        "  When you cast the spell, you can choose one of the normal forms for your familiar or one of the following special forms: imp, pseudodragon, quasit, or sprite.")]
    [InlineData("2014/feature/favored-enemy-1-type",
        "**Enemy Type Options** Choose one enemy type: aberrations, beasts, celestials, constructs, dragons, elementals, fey, fiends, giants, monstrosities, oozes, plants, undead, humanoids")]
    [InlineData("2014/feature/elemental-affinity", "**Reference** Draconic (`2014/subclass/draconic`)")]
    // A class-spellcasting reference renders that class's spellcasting rules, which no record of its own holds.
    [InlineData("2014/feature/spellcasting-wizard", "**Spellcasting Ability** INT (`2014/ability-score/int`)")]
    [InlineData("2014/feature/spellcasting-wizard",
        "***Cantrips.*** At 1st level, you know three cantrips of your choice from the wizard spell list. You learn additional wizard cantrips of your choice at higher levels, as shown in the Cantrips Known column of the Wizard table.")]
    [InlineData("2014/feature/pact-magic", "**Spellcasting Ability** CHA (`2014/ability-score/cha`)")]
    public void Body_Feature_PinsHeaderTextAndOptions(string reference, string expectedLine)
    {
        Assert.Contains(expectedLine, BodyLines(reference));
    }

    [Fact]
    public void Body_OptionsThatCopyTheirParentsText_AreListedWithoutRepeatingIt()
    {
        // Every 2014 Dragon Ancestor option's desc is the parent's desc, word for word.
        var lines = BodyLines("2014/feature/dragon-ancestor");

        Assert.Contains("- Dragon Ancestor: Red - Fire Damage (`2014/feature/dragon-ancestor-red---fire-damage`)", lines);
        Assert.Single(lines, l => l.StartsWith("At 1st level, you choose one type of dragon as your ancestor.", StringComparison.Ordinal));
        Assert.Equal(10, lines.Count(l => l.StartsWith("- Dragon Ancestor: ", StringComparison.Ordinal)));
    }

    [Theory]
    // Desc names every option: the SRD's sentence alone.
    [InlineData("2014/class/monk", "proficiency_choices", 1, "Choose one type of artisan’s tools or one musical instrument")]
    // Desc names none: the options follow it.
    [InlineData("2014/class/bard", "proficiency_choices", 1,
        "Three musical instruments of your choice: Bagpipes, Drum, Dulcimer, Flute, Lute, Lyre, Horn, Pan flute, Shawm, Viol")]
    [InlineData("2014/class/bard", "proficiency_choices", 0,
        "Choose any three: Skill: Acrobatics, Skill: Animal Handling, Skill: Arcana, Skill: Athletics, Skill: Deception, Skill: History, Skill: Insight, Skill: Intimidation, Skill: Investigation, Skill: Medicine, Skill: Nature, Skill: Perception, Skill: Performance, Skill: Persuasion, Skill: Religion, Skill: Sleight of Hand, Skill: Stealth, Skill: Survival")]
    // A bare noun phrase gets "Choose N".
    [InlineData("2014/class/bard", "multi_classing.proficiency_choices", 1,
        "Choose 1 musical instrument: Bagpipes, Drum, Dulcimer, Flute, Lute, Lyre, Horn, Pan flute, Shawm, Viol")]
    // No desc: the tree is walked.
    [InlineData("2014/class/rogue", "multi_classing.proficiency_choices", 0,
        "Choose 1 from: Skill: Acrobatics, Skill: Athletics, Skill: Deception, Skill: Insight, Skill: Intimidation, Skill: Investigation, Skill: Perception, Skill: Performance, Skill: Persuasion, Skill: Sleight of Hand, Skill: Stealth")]
    // Upstream's mojibake keeps the desc from naming "artisan's tools", so the nested choices are listed, lettered.
    [InlineData("2024/class/monk", "proficiency_choices", 1,
        "Choose one type of artisanâ€™s tools or one musical instrument: (a) Choose 1 artisan's tools: Alchemist's Supplies, Brewer's Supplies, Tool: Calligrapher's Supplies, Carpenter's Tools, Cartographer's Tools, Cobbler's Tools, Cook's Utensils, Glassblower's Tools, Jeweler's Tools, Leatherworker's Tools, Mason's Tools, Painter's Supplies, Potter's Tools, Smith's Tools, Tinker's Tools, Weaver's Tools, Woodcarver's Tools, Disguise Kit, Forgery Kit; (b) Choose 1 musical instrument: Bagpipes, Drum, Dulcimer, Flute, Lute, Lyre, Horn, Pan flute, Shawm, Viol")]
    public void Describe_RealClassChoice_ReadsAsTheSrdSentenceOrListsTheOptions(string reference, string path, int index, string expected)
    {
        var choices = path.Split('.').Aggregate(Lookup.Require(reference).Root,
            (element, property) => element.GetProperty(property));

        Assert.Equal(expected, SrdChoiceMarkdown.Describe(choices[index]));
    }

    [Fact]
    public void Body_RogueExpertise2014_BracketsTheNestedChoiceInsideTheThievesToolsBundle()
    {
        var line = BodyLines("2014/feature/rogue-expertise-1").Single(l => l.StartsWith("**Expertise Options**", StringComparison.Ordinal));

        Assert.StartsWith("**Expertise Options** Choose 1 from: (a) Choose 2 from: Skill: Acrobatics, ", line, StringComparison.Ordinal);
        Assert.EndsWith("; (b) [Choose 1 from: Skill: Acrobatics, Skill: Animal Handling, Skill: Arcana, Skill: Athletics, Skill: Deception, Skill: History, Skill: Insight, Skill: Intimidation, Skill: Investigation, Skill: Medicine, Skill: Nature, Skill: Perception, Skill: Performance, Skill: Persuasion, Skill: Religion, Skill: Sleight of Hand, Skill: Stealth, Skill: Survival], Thieves' Tools",
            line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("rage_count", "9999", "Unlimited")]
    [InlineData("rage_count", "3", "3")]
    [InlineData("indomitable_uses", "0", "—")]
    [InlineData("bardic_inspiration_die", "6", "d6")]
    [InlineData("rage_damage_bonus", "2", "+2")]
    // Speed increases and ranges are feet: a bare "10" beside "Unarmored Movement" reads as a speed of 10 feet.
    [InlineData("unarmored_movement", "10", "+10 ft.")]
    [InlineData("unarmored_movement_bonus", "30", "+30 ft.")]
    [InlineData("unarmored_movement", "0", "—")]
    [InlineData("aura_range", "10", "10 ft.")]
    [InlineData("wild_shape_max_cr", "0.25", "1/4")]
    [InlineData("destroy_undead_cr", "0.5", "1/2")]
    [InlineData("wild_shape_fly", "true", "Yes")]
    [InlineData("wild_shape_swim", "false", "No")]
    [InlineData("sneak_attack", """{"dice_count": 3, "dice_value": 6}""", "3d6")]
    [InlineData("creating_spell_slots", "[1, 2]", null)]
    [InlineData("anything", """{"other": 1}""", null)]
    public void Value_ClassSpecificValue_PrintsAsTheSrdTablesDo(string key, string json, string? expected)
    {
        using var parsed = JsonDocument.Parse(json);

        Assert.Equal(expected, ClassMarkdown.Value(key, parsed.RootElement));
    }

    [Theory]
    [InlineData("song_of_rest_die", "Song of Rest Die")]
    [InlineData("wild_shape_max_cr", "Wild Shape Max CR")]
    [InlineData("spell_slots_level_3", "3rd")]
    [InlineData("spell_slots_level_1", "1st")]
    [InlineData("spell_slots_level_9", "9th")]
    [InlineData("cantrips_known", "Cantrips Known")]
    public void ColumnName_UpstreamKey_BecomesAReadableHeading(string key, string expected)
    {
        Assert.Equal(expected, ClassMarkdown.ColumnName(key));
    }

    private static TheoryData<string> RefsOf(string kind)
    {
        var data = new TheoryData<string>();
        foreach (var edition in SrdEdition.All)
        {
            foreach (var doc in VendoredSrdLookup.Instance.OfKind(edition, kind))
            {
                data.Add(doc.Ref.ToString());
            }
        }

        return data;
    }

    private static string Body(SrdDocument doc) => SrdMarkdown.Body(doc, Lookup);

    private static string[] BodyLines(string reference) => Body(Lookup.Require(reference)).Split('\n');

    // The text under a "#### Level N: …" heading, up to the next heading or the end of the section.
    private static string FeatureSection(string body, string heading)
    {
        var start = body.IndexOf(heading + "\n\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"heading missing: {heading}");
        start += heading.Length + 2;

        var ends = new[] { "\n\n#### ", "\n\n### ", "\n\n**Other features at these levels**" }
            .Select(marker => body.IndexOf(marker, start, StringComparison.Ordinal))
            .Where(i => i >= 0)
            .DefaultIfEmpty(body.Length);
        return body[start..ends.Min()];
    }

    // Lower-case words and numbers; apostrophes dropped, every other mark a separator ("Nature’s" = "Nature's").
    private static string[] Words(string text) =>
        Regex.Matches(text.Replace("’", string.Empty, StringComparison.Ordinal).Replace("'", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant(), "[a-z0-9]+")
            .Select(m => m.Value)
            .ToArray();

    // The words between the longest common prefix and suffix, both sides, cut to a readable length.
    private static string WordDiff(string[] inline, string[] record)
    {
        var prefix = 0;
        while (prefix < inline.Length && prefix < record.Length && inline[prefix] == record[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < inline.Length - prefix && suffix < record.Length - prefix &&
               inline[^(suffix + 1)] == record[^(suffix + 1)])
        {
            suffix++;
        }

        static string Cut(IEnumerable<string> words) => string.Join(" ", words.Take(25));
        return $"inline \"{Cut(inline[prefix..^suffix])}\" vs record \"{Cut(record[prefix..^suffix])}\"";
    }

    // The vendored records of one kind as upstream wrote them, keyed by index: no curated correction applied.
    private static Dictionary<string, JsonElement> VendoredRecords(string edition, string kind)
    {
        var root = Path.Combine(VendoredSrdLookup.ContentRoot, "5e-database");
        var manifest = ContentManifest.Load(Path.Combine(root, ContentManifest.FileName));
        var path = Path.Combine(root, manifest.Tag["5e-database-".Length..], edition, SrdKinds.Find(kind)!.FileFor(edition)!);
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.EnumerateArray()
            .ToDictionary(r => r.GetProperty("index").GetString()!, r => r.Clone(), StringComparer.Ordinal);
    }
}
