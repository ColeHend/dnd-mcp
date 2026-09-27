using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.Tests.Srd.Index;

/// <summary>
/// The 2014 ↔ 2024 pairing behind <c>edition: both</c>, the other-edition hints and the renamed-entry aliases.
///
/// <para>
/// Every manual pair is pinned by this file's OWN copy of the table (<see cref="PinnedManualPairs"/>), not by reading
/// <see cref="SrdCounterparts.Manual"/> back: deleting or retargeting an entry in code fails here, and so does adding one
/// without adding it here, so every change to the hand-verified list is a reviewed diff. Each pinned pair must resolve
/// to two real documents that list each other as counterparts.
/// </para>
/// <para>
/// The automatic rules are pinned by example, by coverage and by count: slug pairs (level records included), unique-name
/// pairs (per class for features), feature grades ("Wild Shape (CR 1 or below)") pairing with the one 2024 feature,
/// 2014 variant magic items pairing with their parent's 2024 entry, renamed subclasses' level records, the uniqueness
/// guards that keep ambiguous names unpaired, and the totals per source, which move only when the data or the rules do.
/// The coverage tests are the point of all this: <c>edition: both</c> must never be left to say "no 2014 equivalent"
/// for an entry the other edition has under the same slug or the same feature name.
/// </para>
/// </summary>
public sealed class SrdCounterpartTests : IClassFixture<SrdIndexFixture>
{
    private readonly SrdIndexFixture _fixture;
    private readonly SrdIndex _index;

    public SrdCounterpartTests(SrdIndexFixture fixture)
    {
        _fixture = fixture;
        _index = fixture.Index;
    }

    public static TheoryData<string, string, string, string> PinnedManualPairs => new()
    {
        { "condition", "blinded", "rule", "blinded" },                     // the glossary repeats each condition
        { "condition", "charmed", "rule", "charmed" },
        { "condition", "deafened", "rule", "deafened" },
        { "condition", "exhaustion", "rule", "exhaustion" },
        { "condition", "frightened", "rule", "frightened" },
        { "condition", "grappled", "rule", "grappled" },
        { "condition", "incapacitated", "rule", "incapacitated" },
        { "condition", "invisible", "rule", "invisible" },
        { "condition", "paralyzed", "rule", "paralyzed" },
        { "condition", "petrified", "rule", "petrified" },
        { "condition", "poisoned", "rule", "poisoned" },
        { "condition", "prone", "rule", "prone" },
        { "condition", "restrained", "rule", "restrained" },
        { "condition", "stunned", "rule", "stunned" },
        { "condition", "unconscious", "rule", "unconscious" },
        { "equipment", "acid-vial", "equipment", "acid" },
        { "equipment", "alchemists-fire-flask", "equipment", "alchemists-fire" },
        { "equipment", "antitoxin-vial", "equipment", "antitoxin" },
        { "equipment", "arrow", "equipment", "arrows" },
        { "equipment", "ball-bearings-bag-of-1000", "equipment", "ball-bearings" },
        { "equipment", "blowgun-needle", "equipment", "needles" },
        { "equipment", "chain-10-feet", "equipment", "chain" },
        { "equipment", "clothes-costume", "equipment", "costume" },
        { "equipment", "crossbow-bolt", "equipment", "bolts" },
        { "equipment", "crossbow-hand", "equipment", "hand-crossbow" },
        { "equipment", "crossbow-heavy", "equipment", "heavy-crossbow" },
        { "equipment", "crossbow-light", "equipment", "light-crossbow" },
        { "equipment", "dice-set", "equipment", "dice" },
        { "equipment", "flask-or-tankard", "equipment", "flask" },
        { "equipment", "holy-water-flask", "equipment", "holy-water" },
        { "equipment", "ink-1-ounce-bottle", "equipment", "ink" },
        { "equipment", "jug-or-pitcher", "equipment", "jug" },
        { "equipment", "ladder-10-foot", "equipment", "ladder" },
        { "equipment", "mirror-steel", "equipment", "mirror" },
        { "equipment", "oil-flask", "equipment", "oil" },
        { "equipment", "paper-one-sheet", "equipment", "paper" },
        { "equipment", "parchment-one-sheet", "equipment", "parchment" },
        { "equipment", "perfume-vial", "equipment", "perfume" },
        { "equipment", "playing-card-set", "equipment", "playing-cards" },
        { "equipment", "poison-basic-vial", "equipment", "poison-basic" },
        { "equipment", "pole-10-foot", "equipment", "pole" },
        { "equipment", "rations-1-day", "equipment", "rations" },
        { "equipment", "robes", "equipment", "robe" },
        { "equipment", "rope-hempen-50-feet", "equipment", "rope" },
        { "equipment", "sling-bullet", "equipment", "bullets-sling" },
        { "equipment", "spike-iron", "equipment", "spikes-iron" },
        { "equipment", "string-10-feet", "equipment", "string" },
        { "equipment", "tent-two-person", "equipment", "tent" },
        { "equipment-category", "potion", "equipment-category", "potions" },
        { "equipment-category", "ring", "equipment-category", "rings" },
        { "equipment-category", "staff", "equipment-category", "staffs" },
        { "equipment-category", "wand", "equipment-category", "wands" },
        { "equipment-category", "weapon", "equipment-category", "weapons" },
        { "feature", "arcane-tradition", "feature", "wizard-subclass" },                 // choose-your-subclass feature
        { "feature", "aura-improvements", "feature", "paladin-aura-expansion" },
        { "feature", "bard-college", "feature", "bard-subclass" },
        { "feature", "channel-divinity-preserve-life", "feature", "life-preserve-life" },
        { "feature", "channel-divinity-sacred-weapon", "feature", "devotion-sacred-weapon" },
        { "feature", "deflect-missiles", "feature", "monk-deflect-attacks" },
        { "feature", "diamond-soul", "feature", "monk-disciplined-survivor" },
        { "feature", "divine-domain", "feature", "cleric-subclass" },
        { "feature", "divine-smite", "spell", "divine-smite" },                          // a paladin feature became a spell
        { "feature", "druid-circle", "feature", "druid-subclass" },
        { "feature", "extra-attack-2", "feature", "fighter-two-extra-attacks" },
        { "feature", "extra-attack-3", "feature", "fighter-three-extra-attacks" },
        { "feature", "fighter-fighting-style-archery", "feat", "archery" },              // fighting styles became feats
        { "feature", "fighter-fighting-style-defense", "feat", "defense" },
        { "feature", "fighter-fighting-style-great-weapon-fighting", "feat", "great-weapon-fighting" },
        { "feature", "fighter-fighting-style-two-weapon-fighting", "feat", "two-weapon-fighting" },
        { "feature", "fighting-style-defense", "feat", "defense" },                      // the paladin's
        { "feature", "fighting-style-great-weapon-fighting", "feat", "great-weapon-fighting" },
        { "feature", "improved-divine-smite", "feature", "paladin-radiant-strikes" },
        { "feature", "ki", "feature", "monk-monks-focus" },
        { "feature", "ki-empowered-strikes", "feature", "monk-empowered-strikes" },
        { "feature", "martial-archetype", "feature", "fighter-subclass" },
        { "feature", "monastic-tradition", "feature", "monk-subclass" },
        { "feature", "otherworldly-patron", "feature", "warlock-subclass" },
        { "feature", "primal-path", "feature", "barbarian-subclass" },
        { "feature", "ranger-archetype", "feature", "ranger-subclass" },
        { "feature", "ranger-fighting-style-archery", "feat", "archery" },
        { "feature", "ranger-fighting-style-defense", "feat", "defense" },
        { "feature", "ranger-fighting-style-two-weapon-fighting", "feat", "two-weapon-fighting" },
        { "feature", "roguish-archetype", "feature", "rogue-subclass" },
        { "feature", "sacred-oath", "feature", "paladin-subclass" },
        { "feature", "signature-spell", "feature", "wizard-signature-spells" },
        { "feature", "sorcerous-origin", "feature", "sorcerer-subclass" },
        { "magic-item", "arrow-of-slaying", "magic-item", "ammunition-of-slaying" },
        { "magic-item", "deck-of-many-things", "magic-item", "mysterious-deck" },
        { "magic-item", "glamoured-studded-leather-armor", "magic-item", "glamoured-studded-leather" },
        { "magic-item", "iron-bands-of-binding", "magic-item", "iron-bands" },
        { "magic-item", "orb-of-dragonkind", "magic-item", "dragon-orb" },
        { "magic-item", "potion-of-healing", "equipment", "potion-of-healing" },         // 2024 also sells it as gear
        { "magic-item", "potion-of-healing", "magic-item", "potions-of-healing" },
        { "magic-item", "spell-scroll-1st", "equipment", "spell-scroll-level-1" },
        { "magic-item", "spell-scroll-cantrip", "equipment", "spell-scroll-cantrip" },
        { "monster", "acolyte", "monster", "priest-acolyte" },
        { "monster", "androsphinx", "monster", "sphinx-of-valor" },
        { "monster", "azer", "monster", "azer-sentinel" },
        { "monster", "bugbear", "monster", "bugbear-warrior" },
        { "monster", "centaur", "monster", "centaur-trooper" },
        { "monster", "cult-fanatic", "monster", "cultist-fanatic" },
        { "monster", "flying-sword", "monster", "animated-flying-sword" },
        { "monster", "giant-poisonous-snake", "monster", "giant-venomous-snake" },
        { "monster", "giant-sea-horse", "monster", "giant-seahorse" },
        { "monster", "gnoll", "monster", "gnoll-warrior" },
        { "monster", "goblin", "monster", "goblin-warrior" },
        { "monster", "gynosphinx", "monster", "sphinx-of-lore" },
        { "monster", "half-red-dragon-veteran", "monster", "half-dragon" },
        { "monster", "hobgoblin", "monster", "hobgoblin-warrior" },
        { "monster", "kobold", "monster", "kobold-warrior" },
        { "monster", "merfolk", "monster", "merfolk-skirmisher" },
        { "monster", "minotaur", "monster", "minotaur-of-baphomet" },
        { "monster", "poisonous-snake", "monster", "venomous-snake" },
        { "monster", "quipper", "monster", "piranha" },
        { "monster", "rug-of-smothering", "monster", "animated-rug-of-smothering" },
        { "monster", "sahuagin", "monster", "sahuagin-warrior" },
        { "monster", "sea-horse", "monster", "seahorse" },
        { "monster", "shrieker", "monster", "shrieker-fungus" },
        { "monster", "succubus-incubus", "monster", "incubus" },
        { "monster", "succubus-incubus", "monster", "succubus" },
        { "monster", "swarm-of-beetles", "monster", "swarm-of-insects" },               // variants 2024 folded into one
        { "monster", "swarm-of-centipedes", "monster", "swarm-of-insects" },
        { "monster", "swarm-of-poisonous-snakes", "monster", "swarm-of-venomous-snakes" },
        { "monster", "swarm-of-quippers", "monster", "swarm-of-piranhas" },
        { "monster", "swarm-of-spiders", "monster", "swarm-of-insects" },
        { "monster", "swarm-of-wasps", "monster", "swarm-of-insects" },
        { "monster", "thug", "monster", "tough" },
        { "monster", "tribal-warrior", "monster", "warrior-infantry" },
        { "monster", "veteran", "monster", "warrior-veteran" },
        { "proficiency", "calligraphers-supplies", "proficiency", "tool-calligraphers-supplies" },
        { "proficiency", "dice-set", "proficiency", "tool-dice" },
        { "proficiency", "navigators-tools", "proficiency", "tool-navigators-tools" },
        { "proficiency", "playing-card-set", "proficiency", "tool-playing-cards" },
        { "proficiency", "poisoners-kit", "proficiency", "tool-poisoners-kit" },
        { "proficiency", "thieves-tools", "proficiency", "tool-thieves-tools" },
        { "rule", "ability-checks", "rule", "ability-check" },
        { "rule", "ability-scores-and-modifiers", "rule", "ability-score-and-modifier" },
        { "rule", "advantage-and-disadvantage", "rule", "advantage" },
        { "rule", "advantage-and-disadvantage", "rule", "disadvantage" },
        { "rule", "areas-of-effect", "rule", "area-of-effect" },
        { "rule", "attack-rolls", "rule", "attack-roll" },
        { "rule", "cantrips", "rule", "cantrip" },
        { "rule", "cast-a-spell", "rule", "magic" },
        { "rule", "creature-size", "rule", "size" },
        { "rule", "damage-resistance-and-vulnerability", "rule", "resistance" },
        { "rule", "damage-resistance-and-vulnerability", "rule", "vulnerability" },
        { "rule", "damage-rolls", "rule", "damage-roll" },
        { "rule", "flying-movement", "rule", "flying" },
        { "rule", "food-and-water", "rule", "dehydration" },
        { "rule", "food-and-water", "rule", "malnutrition" },
        { "rule", "knocking-a-creature-out", "rule", "knocking-out-a-creature" },
        { "rule", "reactions", "rule", "reaction" },
        { "rule", "rituals", "rule", "ritual" },
        { "rule", "saving-throws", "rule", "saving-throw" },
        { "rule", "skills", "rule", "skill" },
        { "rule", "spell-attack-rolls", "rule", "spell-attack" },
        { "rule", "suffocating", "rule", "suffocation" },
        { "rule", "targets", "rule", "target" },
        { "rule", "use-an-object", "rule", "utilize" },
        { "rule", "what-is-a-spell", "rule", "spell" },
        { "spell", "branding-smite", "spell", "shining-smite" },
        { "spell", "feeblemind", "spell", "befuddlement" },
        { "subclass", "berserker", "subclass", "path-of-the-berserker" },
        { "subclass", "devotion", "subclass", "oath-of-devotion" },
        { "subclass", "draconic", "subclass", "draconic-sorcery" },
        { "subclass", "evocation", "subclass", "evoker" },
        { "subclass", "fiend", "subclass", "fiend-patron" },
        { "subclass", "land", "subclass", "circle-of-the-land" },
        { "subclass", "life", "subclass", "life-domain" },
        { "subclass", "lore", "subclass", "college-of-lore" },
        { "subclass", "open-hand", "subclass", "warrior-of-the-open-hand" },
        { "subrace", "high-elf", "subspecies", "elven-lineage-high-elf" },
        { "subrace", "rock-gnome", "subspecies", "gnomish-lineage-rock-gnome" },
        { "trait", "breath-weapon", "trait", "draconic-breath-weapon-acid" },
        { "trait", "breath-weapon", "trait", "draconic-breath-weapon-cold" },
        { "trait", "breath-weapon", "trait", "draconic-breath-weapon-fire" },
        { "trait", "breath-weapon", "trait", "draconic-breath-weapon-lightning" },
        { "trait", "breath-weapon", "trait", "draconic-breath-weapon-poison" },
        { "trait", "damage-resistance", "trait", "draconic-damage-resistance-acid" },
        { "trait", "damage-resistance", "trait", "draconic-damage-resistance-cold" },
        { "trait", "damage-resistance", "trait", "draconic-damage-resistance-fire" },
        { "trait", "damage-resistance", "trait", "draconic-damage-resistance-lightning" },
        { "trait", "damage-resistance", "trait", "draconic-damage-resistance-poison" },
        { "trait", "darkvision", "trait", "darkvision-60" },
        { "trait", "gnome-cunning", "trait", "gnomish-cunning" },
        { "trait", "hellish-resistance", "trait", "lineage-resistance-fire" },
        { "trait", "high-elf-cantrip", "trait", "high-elf-cantrip-versatility" },
        { "trait", "infernal-legacy", "trait", "fiendish-legacy" },
        { "trait", "lucky", "trait", "luck" },
    };

    /// <summary>
    /// 2024 Rules Glossary entries (and 2024 poisons) paired with the 2014 rules section whose text covers them: 2014 has
    /// no Grappling or Concentration entry, only the "#### Grappling" subsection of Melee Attacks and the "####
    /// Concentration" subsection of Duration. Every pair checked by reading both texts.
    /// </summary>
    public static TheoryData<string, string, string, string> PinnedSectionPairs => new()
    {
        { "rule", "areas-of-effect", "rule", "cone" },
        { "rule", "areas-of-effect", "rule", "cube" },
        { "rule", "areas-of-effect", "rule", "cylinder" },
        { "rule", "areas-of-effect", "rule", "line" },
        { "rule", "areas-of-effect", "rule", "sphere" },
        { "rule", "damage-rolls", "rule", "critical-hit" },
        { "rule", "damage-rolls", "rule", "damage-types" },
        { "rule", "dropping-to-0-hit-points", "rule", "death-saving-throw" },
        { "rule", "duration", "rule", "concentration" },
        { "rule", "melee-attacks", "rule", "grappling" },
        { "rule", "melee-attacks", "rule", "opportunity-attacks" },
        { "rule", "melee-attacks", "rule", "unarmed-strike" },
        { "rule", "sample-poisons", "poison", "assassins-blood" },
        { "rule", "sample-poisons", "poison", "burnt-othur-fumes" },
        { "rule", "sample-poisons", "poison", "crawler-mucus" },
        { "rule", "sample-poisons", "poison", "essence-of-ether" },
        { "rule", "sample-poisons", "poison", "malice" },
        { "rule", "sample-poisons", "poison", "midnight-tears" },
        { "rule", "sample-poisons", "poison", "oil-of-taggit" },
        { "rule", "sample-poisons", "poison", "pale-tincture" },
        { "rule", "sample-poisons", "poison", "purple-worm-poison" },
        { "rule", "sample-poisons", "poison", "serpent-venom" },
        { "rule", "sample-poisons", "poison", "spiders-sting" },               // 2014's Drow Poison, renamed
        { "rule", "sample-poisons", "poison", "torpor" },
        { "rule", "sample-poisons", "poison", "truth-serum" },
        { "rule", "sample-poisons", "poison", "wyvern-poison" },
        { "rule", "special-types-of-movement", "rule", "climbing" },
        { "rule", "special-types-of-movement", "rule", "crawling" },
        { "rule", "special-types-of-movement", "rule", "high-jump" },
        { "rule", "special-types-of-movement", "rule", "jumping" },
        { "rule", "special-types-of-movement", "rule", "long-jump" },
        { "rule", "special-types-of-movement", "rule", "swimming" },
        { "rule", "vision-and-light", "rule", "blindsight" },
        { "rule", "vision-and-light", "rule", "darkvision" },
        { "rule", "vision-and-light", "rule", "truesight" },
        { "rule", "your-turn", "rule", "bonus-action" },
    };

    [Fact]
    public void Manual_Table_IsExactlyThePinnedPairs()
    {
        var pinned = PinnedManualPairs.Select(row => $"2014/{row[0]}/{row[1]} → 2024/{row[2]}/{row[3]}").Order(StringComparer.Ordinal);
        var code = SrdCounterparts.Manual.Select(m => m.ToString()).Order(StringComparer.Ordinal);

        Assert.Equal(pinned, code);
    }

    [Fact]
    public void Sections_Table_IsExactlyThePinnedPairs()
    {
        var pinned = PinnedSectionPairs.Select(row => $"2014/{row[0]}/{row[1]} → 2024/{row[2]}/{row[3]}").Order(StringComparer.Ordinal);
        var code = SrdCounterparts.Sections.Select(m => m.ToString()).Order(StringComparer.Ordinal);

        Assert.Equal(pinned, code);
    }

    [Theory]
    [MemberData(nameof(PinnedManualPairs))]
    [MemberData(nameof(PinnedSectionPairs))]
    public void Manual_Pair_ResolvesAndListsEachOtherAsCounterparts(string kind2014, string slug2014, string kind2024, string slug2024)
    {
        var old = _index.Get("2014", kind2014, slug2014);
        var current = _index.Get("2024", kind2024, slug2024);

        Assert.NotNull(old);
        Assert.NotNull(current);
        Assert.Contains(current.Ref, _index.Counterparts(old).Select(d => d.Ref));
        Assert.Contains(old.Ref, _index.Counterparts(current).Select(d => d.Ref));
    }

    // A manual pair the content no longer has is skipped with a warning, never silently; today there are none.
    [Fact]
    public void Build_RealContent_SkipsNoManualPair()
    {
        Assert.Empty(_fixture.Summary.Warnings);
        Assert.Equal(189, _fixture.Scalar("SELECT COUNT(*) FROM counterpart WHERE source = 'manual';"));
        Assert.Equal(36, _fixture.Scalar("SELECT COUNT(*) FROM counterpart WHERE source = 'section';"));
    }

    // The totals move only when the data or the pairing rules change; either deserves a look at this file (and, for a
    // rule change, a SrdIndexSchema.Version bump).
    [Theory]
    [InlineData("slug", 1519)]       // 1,246 entries + 273 level records
    [InlineData("name", 93)]
    [InlineData("manual", 189)]
    [InlineData("section", 36)]
    [InlineData("feature", 104)]     // 31 2024 features: Ability Score Improvement in 12 classes, Spellcasting in 7, …
    [InlineData("variant", 102)]
    [InlineData("level", 9)]         // Draconic, Evocation and Fiend levels 6 and up
    public void Counterparts_PerSource_HavePinnedTotals(string source, long expected)
    {
        Assert.Equal(expected, _fixture.Scalar($"SELECT COUNT(*) FROM counterpart WHERE source = '{source}';"));
    }

    [Theory]
    [InlineData("2014/spell/fireball", "2024/spell/fireball", "slug")]
    [InlineData("2014/monster/adult-red-dragon", "2024/monster/adult-red-dragon", "slug")]
    [InlineData("2014/race/elf", "2024/species/elf", "slug")]                          // race ⇄ species
    [InlineData("2014/rule/cover", "2024/rule/cover", "slug")]                          // 2014 rule tree ⇄ glossary
    [InlineData("2014/level/fighter-5", "2024/level/fighter-5", "slug")]                // a class level, same row both
    [InlineData("2014/level/berserker-3", "2024/level/berserker-3", "slug")]            // a subclass level
    [InlineData("2014/feature/rage", "2024/feature/barbarian-rage", "name")]            // re-indexed, same unique name
    [InlineData("2014/feature/extra-attack-1", "2024/feature/fighter-extra-attack", "name")] // unique within the fighter
    [InlineData("2014/equipment/diplomats-pack", "2024/equipment/diplomat-pack", "name")]
    [InlineData("2014/magic-item/horn-of-valhalla-silver", "2024/magic-item/horn-of-valhalla-1", "name")]
    [InlineData("2014/monster/thug", "2024/monster/tough", "manual")]
    [InlineData("2014/subrace/high-elf", "2024/subspecies/elven-lineage-high-elf", "manual")]
    [InlineData("2014/feature/divine-smite", "2024/spell/divine-smite", "manual")]       // across kinds
    [InlineData("2014/rule/melee-attacks", "2024/rule/grappling", "section")]
    [InlineData("2014/rule/sample-poisons", "2024/poison/serpent-venom", "section")]
    [InlineData("2014/feature/wild-shape-cr-1-or-below", "2024/feature/druid-wild-shape", "feature")]
    [InlineData("2014/feature/spellcasting-wizard", "2024/feature/wizard-spellcasting", "feature")]
    [InlineData("2014/feature/fighter-ability-score-improvement-7", "2024/feature/fighter-ability-score-improvement", "feature")]
    [InlineData("2014/magic-item/belt-of-giant-strength-hill", "2024/magic-item/belt-of-giant-strength", "variant")]
    [InlineData("2014/magic-item/potion-of-healing-greater", "2024/magic-item/potions-of-healing", "variant")]
    [InlineData("2014/level/draconic-6", "2024/level/draconic-sorcery-6", "level")]      // Draconic Bloodline → Draconic Sorcery
    public void Counterparts_Example_IsPairedByTheExpectedRule(string ref2014, string ref2024, string source)
    {
        var row = _fixture.Column(
            $"""
            SELECT c.source FROM counterpart c
            JOIN doc a ON a.id = c.doc_2014 JOIN doc b ON b.id = c.doc_2024
            WHERE a.edition || '/' || a.kind || '/' || a.slug = '{ref2014}'
              AND b.edition || '/' || b.kind || '/' || b.slug = '{ref2024}';
            """);

        Assert.Equal([source], row);
    }

    /// <summary>
    /// 2014 splits a class feature into graded records ("Action Surge (1 use)", "Action Surge (2 uses)"), repeats some
    /// under one name (the seven fighter "Ability Score Improvement" records, numbered only in their slugs) and qualifies
    /// spellcasting by class; 2024 has one record. Every 2014 grade is the same feature, so every one pairs with it, in
    /// the order a class gains them (lowest level first): edition "both" compares against the first and names the rest.
    /// Slug order put "bardic-inspiration-d10" (level 10) before "-d6" (level 1), and the comparison showed "Level 10".
    /// </summary>
    [Theory]
    [InlineData("2024/feature/druid-wild-shape", "2014/feature/wild-shape-cr-1-4-or-below-no-flying-or-swim-speed,2014/feature/wild-shape-cr-1-2-or-below-no-flying-speed,2014/feature/wild-shape-cr-1-or-below")]
    [InlineData("2024/feature/fighter-action-surge", "2014/feature/action-surge-1-use,2014/feature/action-surge-2-uses")]
    [InlineData("2024/feature/bard-bardic-inspiration", "2014/feature/bardic-inspiration-d6,2014/feature/bardic-inspiration-d8,2014/feature/bardic-inspiration-d10,2014/feature/bardic-inspiration-d12")]
    [InlineData("2024/feature/cleric-channel-divinity", "2014/feature/channel-divinity-1-rest,2014/feature/channel-divinity-2-rest,2014/feature/channel-divinity-3-rest")]
    [InlineData("2024/feature/ranger-favored-enemy", "2014/feature/favored-enemy-1-type,2014/feature/favored-enemy-2-types,2014/feature/favored-enemy-3-enemies")]
    [InlineData("2024/feature/sorcerer-metamagic", "2014/feature/metamagic-1,2014/feature/metamagic-2,2014/feature/metamagic-3")]
    [InlineData("2024/feature/warlock-mystic-arcanum", "2014/feature/mystic-arcanum-6th-level,2014/feature/mystic-arcanum-7th-level,2014/feature/mystic-arcanum-8th-level,2014/feature/mystic-arcanum-9th-level")]
    [InlineData("2024/feature/wizard-spellcasting", "2014/feature/spellcasting-wizard")]
    [InlineData("2024/feature/fighter-indomitable", "2014/feature/indomitable-1-use,2014/feature/indomitable-2-uses,2014/feature/indomitable-3-uses")]
    [InlineData("2024/feature/monk-unarmored-movement", "2014/feature/unarmored-movement-1,2014/feature/unarmored-movement-2")]
    [InlineData("2024/feature/rogue-expertise", "2014/feature/rogue-expertise-1,2014/feature/rogue-expertise-2")]
    [InlineData("2024/feature/fighter-extra-attack", "2014/feature/extra-attack-1")]    // not the (2)/(3) grades: 2024 renamed those
    [InlineData("2024/feature/fighter-two-extra-attacks", "2014/feature/extra-attack-2")]
    public void Counterparts_FeatureGrades_AllPairWithThe2024FeatureLowestLevelFirst(string reference, string expected)
    {
        var parts = reference.Split('/');
        var doc = _index.Get(parts[0], parts[1], parts[2])!;

        Assert.Equal(expected, string.Join(',', _index.Counterparts(doc).Select(d => d.Ref.ToString())));
    }

    /// <summary>
    /// Which counterpart comes first, which is what edition "both" compares when nothing else says which: the other
    /// edition's record with the same slug (2014 Swarm of Insects, not Swarm of Beetles, which slug order put first for
    /// 2024 Swarm of Insects and whose speeds the comparison then reported), then the first grade of a graded feature.
    /// </summary>
    [Theory]
    [InlineData("2024/monster/swarm-of-insects", "2014/monster/swarm-of-insects")]
    [InlineData("2024/feature/bard-bardic-inspiration", "2014/feature/bardic-inspiration-d6")]
    [InlineData("2024/feature/druid-wild-shape", "2014/feature/wild-shape-cr-1-4-or-below-no-flying-or-swim-speed")]
    [InlineData("2024/magic-item/belt-of-giant-strength", "2014/magic-item/belt-of-giant-strength")]
    [InlineData("2014/condition/prone", "2024/condition/prone")]
    public void Counterparts_First_IsTheSameSlugThenTheFirstGrade(string reference, string expected)
    {
        var parts = reference.Split('/');

        Assert.Equal(expected, _index.Counterparts(_index.Get(parts[0], parts[1], parts[2])!)[0].Ref.ToString());
    }

    /// <summary>
    /// The counterparts that answer to the name the caller typed come first, the rest keep their order: edition "both"
    /// for "Swarm of Wasps" reaches 2024 Swarm of Insects, and must compare the wasps, not the same-slug Swarm of Insects
    /// or the Swarm of Beetles slug order used to pick; "Shoving a Creature" (a 2014 heading) compares 2024 Unarmed
    /// Strike, which holds the 2024 shove, not Grappling.
    /// </summary>
    [Theory]
    [InlineData("2024/monster/swarm-of-insects", "Swarm of Wasps", "2014/monster/swarm-of-wasps")]
    [InlineData("2024/monster/swarm-of-insects", "Swarm of Spiders", "2014/monster/swarm-of-spiders")]
    [InlineData("2024/monster/swarm-of-insects", "Swarm of Insects", "2014/monster/swarm-of-insects")]
    [InlineData("2024/monster/swarm-of-insects", null, "2014/monster/swarm-of-insects")]
    [InlineData("2024/feature/druid-wild-shape", "Wild Shape (CR 1 or below)", "2014/feature/wild-shape-cr-1-or-below")]
    [InlineData("2024/feature/bard-bardic-inspiration", "Bardic Inspiration", "2014/feature/bardic-inspiration-d6")]
    [InlineData("2014/rule/melee-attacks", "Shoving a Creature", "2024/rule/unarmed-strike")]
    [InlineData("2014/rule/damage-rolls", "Damage Types", "2024/rule/damage-types")]
    [InlineData("2014/rule/damage-rolls", "Nothing Like It", "2024/rule/critical-hit")]
    public void Counterparts_PreferredName_PutsTheOneAnsweringToItFirst(string reference, string? preferName, string expected)
    {
        var parts = reference.Split('/');
        var doc = _index.Get(parts[0], parts[1], parts[2])!;

        var counterparts = _index.Counterparts(doc, preferName);

        Assert.Equal(expected, counterparts[0].Ref.ToString());
        Assert.Equal(
            _index.Counterparts(doc).Select(d => d.Ref.ToString()).Order(StringComparer.Ordinal),
            counterparts.Select(d => d.Ref.ToString()).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Coverage over every feature, with the base-name rule written out again here: a 2024 feature whose class has a
    /// 2014 feature of the same base name (qualifier, ": Class" and trailing number stripped) always has a counterpart,
    /// and so does that 2014 feature. Without it, <c>edition: both</c> printed "No 2014 equivalent" for Wild Shape,
    /// Metamagic, Channel Divinity and 28 more core features.
    /// </summary>
    [Fact]
    public void Counterparts_EveryFeatureWithASameClassBaseName_IsPaired()
    {
        var features = _fixture.Column(
                """
                SELECT d.edition || char(9) || d.slug || char(9) || d.name || char(9) || ifnull(json_extract(d.json, '$.class.index'), '') || char(9) ||
                       (SELECT COUNT(*) FROM counterpart c WHERE c.doc_2014 = d.id OR c.doc_2024 = d.id)
                FROM doc d WHERE d.kind = 'feature';
                """)
            .Select(row => row.Split('\t'))
            .Select(f => (Edition: f[0], Slug: f[1], Class: f[3], Base: BaseKey(f[2], f[3]), Paired: f[4] != "0"))
            .ToList();
        var bases = features.Select(f => (f.Edition, f.Class, f.Base)).ToHashSet();

        var unpaired = features
            .Where(f => !f.Paired && bases.Contains((f.Edition == "2014" ? "2024" : "2014", f.Class, f.Base)))
            .Select(f => $"{f.Edition}/feature/{f.Slug}")
            .ToList();

        Assert.Empty(unpaired);
        Assert.Contains(("2014", "druid", "wild shape"), bases);

        static string BaseKey(string name, string classSlug)
        {
            var bare = System.Text.RegularExpressions.Regex.Replace(name, @"\s*\([^()]*\)\s*$", string.Empty);
            var colon = bare.LastIndexOf(':');
            if (colon > 0 && SrdNames.Key(bare[(colon + 1)..]) == SrdNames.Key(classSlug))
            {
                bare = bare[..colon];
            }

            return SrdNames.Key(System.Text.RegularExpressions.Regex.Replace(bare, @"\s+\d+$", string.Empty));
        }
    }

    /// <summary>
    /// A level record is a row of the class table, and both editions key it the same way (<c>fighter-5</c>): comparing
    /// what a Fighter gets at 5th level across editions must find the other row, never "no equivalent".
    /// </summary>
    [Fact]
    public void Counterparts_LevelRecords_PairBySlug()
    {
        Assert.Equal(["2024/level/fighter-5"], _index.Counterparts(_index.Get("2014", "level", "fighter-5")!).Select(d => d.Ref.ToString()));
        Assert.Equal(["2014/level/wizard-3"], _index.Counterparts(_index.Get("2024", "level", "wizard-3")!).Select(d => d.Ref.ToString()));
        Assert.Equal(273, _fixture.Scalar(
            "SELECT COUNT(*) FROM counterpart c JOIN doc a ON a.id = c.doc_2014 JOIN doc b ON b.id = c.doc_2024 WHERE a.kind = 'level' AND b.kind = 'level' AND a.slug = b.slug;"));
    }

    // The coverage the tools' "no equivalent" wording relies on: nothing with the same kind (races as species) and slug
    // in the other edition is ever left unpaired.
    [Fact]
    public void Counterparts_SameKindAndSlugInTheOtherEdition_AreAlwaysPaired()
    {
        Assert.Equal(0, _fixture.Scalar(
            """
            SELECT COUNT(*) FROM doc a JOIN doc b
              ON a.edition = '2014' AND b.edition = '2024' AND a.slug = b.slug
             AND b.kind = CASE a.kind WHEN 'race' THEN 'species' WHEN 'subrace' THEN 'subspecies' ELSE a.kind END
            WHERE NOT EXISTS (SELECT 1 FROM counterpart c WHERE c.doc_2014 = a.id AND c.doc_2024 = b.id);
            """));
    }

    // Many-to-many where the SRD split an entry, ordered by slug on the split side.
    [Theory]
    [InlineData("2014/monster/succubus-incubus", "2024/monster/incubus,2024/monster/succubus")]
    [InlineData("2024/monster/succubus", "2014/monster/succubus-incubus")]
    [InlineData("2014/rule/advantage-and-disadvantage", "2024/rule/advantage,2024/rule/disadvantage")]
    [InlineData("2014/trait/breath-weapon", "2024/trait/draconic-breath-weapon-acid,2024/trait/draconic-breath-weapon-cold,2024/trait/draconic-breath-weapon-fire,2024/trait/draconic-breath-weapon-lightning,2024/trait/draconic-breath-weapon-poison")]
    [InlineData("2014/subclass/lore", "2024/subclass/college-of-lore")]
    [InlineData("2024/subclass/college-of-lore", "2014/subclass/lore")]
    [InlineData("2024/spell/divine-smite", "2014/feature/divine-smite")]                 // a 2014 feature, a 2024 spell
    [InlineData("2024/equipment/potion-of-healing", "2014/magic-item/potion-of-healing")] // 2024 gear, 2014 magic item
    [InlineData("2024/feat/archery", "2014/feature/fighter-fighting-style-archery,2014/feature/ranger-fighting-style-archery")]
    [InlineData("2024/rule/grappling", "2014/rule/melee-attacks")]
    [InlineData("2024/rule/prone", "2014/condition/prone")]                      // the glossary's copy of the condition
    [InlineData("2014/condition/prone", "2024/condition/prone,2024/rule/prone")]
    [InlineData("2024/rule/concentration", "2014/rule/duration")]
    [InlineData("2024/rule/critical-hit", "2014/rule/damage-rolls")]
    [InlineData("2024/rule/death-saving-throw", "2014/rule/dropping-to-0-hit-points")]
    [InlineData("2014/rule/melee-attacks", "2024/rule/grappling,2024/rule/opportunity-attacks,2024/rule/unarmed-strike")]
    [InlineData("2014/monster/swarm-of-wasps", "2024/monster/swarm-of-insects")]        // a variant 2024 folded into Swarm of Insects
    [InlineData("2014/monster/duergar", "")]
    [InlineData("2024/monster/pirate", "")]
    [InlineData("2014/equipment/rope-silk-50-feet", "")]                 // rejected: 2024 has one rope, priced as hempen
    [InlineData("2014/monster/giant-rat-diseased", "")]                  // rejected: 2024's Giant Rat carries no disease
    [InlineData("2024/feat/ability-score-improvement", "")]              // the class ASI features pair with 2024's class features
    public void Counterparts_ForDocument_AreTheOtherEditionsEntries(string reference, string expected)
    {
        var parts = reference.Split('/');
        var doc = _index.Get(parts[0], parts[1], parts[2])!;

        Assert.Equal(expected, string.Join(',', _index.Counterparts(doc).Select(d => d.Ref.ToString()).Order(StringComparer.Ordinal)));
    }

    /// <summary>
    /// 2014 magic-item variants 2024 folded into their parent (Belt of Hill Giant Strength is a row of 2024's Belt of
    /// Giant Strength table) pair with whatever their 2014 parent pairs with, of the same kind: the Potion of Greater
    /// Healing pairs with 2024's Potions of Healing, not with the 2024 gear entry the plain Potion of Healing also has.
    /// </summary>
    [Theory]
    [InlineData("2024/magic-item/belt-of-giant-strength", 7)]            // the parent + hill, stone, frost, fire, cloud, storm
    [InlineData("2024/magic-item/potions-of-healing", 5)]                // the parent + common, greater, superior, supreme
    [InlineData("2024/magic-item/ioun-stone", 15)]
    [InlineData("2024/equipment/potion-of-healing", 1)]
    public void Counterparts_VariantItems_PairWithTheirParentsEntry(string reference, int expected)
    {
        var parts = reference.Split('/');

        Assert.Equal(expected, _index.Counterparts(_index.Get(parts[0], parts[1], parts[2])!).Count);
    }

    // Aliases: the other edition's name where it differs, a name's bare form without its trailing qualifier, a 2014
    // rule's curated subsection headings and the 2024 names it covers, and curated names; never the document's own name.
    [Theory]
    [InlineData("2024/monster/tough", "Thug")]
    [InlineData("2014/monster/thug", "Tough")]
    [InlineData("2014/subclass/lore", "College of Lore")]
    [InlineData("2024/rule/finesse-weapon-property", "Finesse")]
    [InlineData("2024/rule/cleave-mastery-property", "Cleave")]
    [InlineData("2014/monster/succubus-incubus", "Incubus|Succubus")]
    [InlineData("2024/rule/magic", "Cast a Spell")]
    [InlineData("2014/spell/fireball", "")]                       // same name in both editions: no alias
    [InlineData("2024/monster/will-o-wisp", "")]                  // "Will-o'-Wisp" vs "Will-o’-Wisp": same key
    [InlineData("2024/rule/cover", "")]
    [InlineData("2014/feature/wild-shape-cr-1-or-below", "Wild Shape")]            // bare name; also the 2024 name
    [InlineData("2014/feature/spellcasting-wizard", "Spellcasting")]
    [InlineData("2014/equipment/oil-flask", "Oil")]
    [InlineData("2014/monster/deep-gnome-svirfneblin", "Deep Gnome")]
    [InlineData("2024/trait/darkvision-120", "Darkvision")]
    [InlineData("2014/rule/melee-attacks", "Opportunity Attacks|Two-Weapon Fighting|Contests in Combat|Grappling|Shoving a Creature|Unarmed Strike|Shove|Shoving|Grapple")]
    [InlineData("2014/rule/damage-rolls", "Critical Hits|Damage Types|Critical Hit|Damage Roll")] // headings, the glossary's singular, the renamed 2024 entry
    [InlineData("2014/rule/duration", "Concentration")]            // not "Instantaneous", a kind of duration
    [InlineData("2014/rule/casting-time", "Longer Casting Times")] // not "Bonus Action" or "Reactions"
    [InlineData("2014/rule/creating-sentient-magic-items", "")]    // none of its sentient-item headings
    [InlineData("2024/rule/grappling", "Grapple")]                // curated; a section pair: "Melee Attacks" is not another name for it
    [InlineData("2024/rule/unarmed-strike", "Shove|Shoving|Shoving a Creature")]
    [InlineData("2024/rule/concentration", "")]
    [InlineData("2024/feat/archery", "Fighting Style: Archery")]
    public void Aliases_ForDocument_AreTheOtherNamesItAnswersTo(string reference, string expected)
    {
        var parts = reference.Split('/');

        Assert.Equal(expected, string.Join('|', AliasNames(reference)));
        Assert.Equal(AliasNames(reference).Order(StringComparer.Ordinal), _index.Get(parts[0], parts[1], parts[2])!.Aliases.Select(a => a.Name).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The other edition's names come in the order the other edition's records are gained: 2024 Bardic Inspiration's
    /// "2014 name:" line read "(d10), (d12), (d6), (d8)" in alphabetical order.
    /// </summary>
    [Fact]
    public void Aliases_OtherEditionsGradedNames_AreInLevelOrder()
    {
        var names = _index.Get("2024", "feature", "bard-bardic-inspiration")!.Aliases
            .Where(a => a.Source == SrdAliasSources.Counterpart)
            .Select(a => a.Name);

        Assert.Equal(["Bardic Inspiration (d6)", "Bardic Inspiration (d8)", "Bardic Inspiration (d10)", "Bardic Inspiration (d12)"], names);
    }

    [Theory]
    [InlineData("2024/magic-item/belt-of-giant-strength", "Belt of Hill Giant Strength")]
    [InlineData("2024/magic-item/potions-of-healing", "Potion of Greater Healing")]
    [InlineData("2024/magic-item/ioun-stone", "Ioun Stone of Protection")]
    [InlineData("2024/monster/swarm-of-insects", "Swarm of Wasps")]
    [InlineData("2014/rule/sample-poisons", "Serpent Venom")]
    public void Aliases_ForDocument_IncludeTheName(string reference, string alias)
    {
        Assert.Contains(alias, AliasNames(reference));
    }

    // A section pair's 2024 name becomes a heading alias of the 2014 section only where the section uses that name:
    // the 2014 poison list calls Spider's Sting "Drow Poison", so "Spider's Sting" would send a reader to a section
    // that never says it.
    [Fact]
    public void Aliases_SectionPairNameTheSectionNeverUses_IsNotAnAlias()
    {
        Assert.DoesNotContain("Spider's Sting", AliasNames("2014/rule/sample-poisons"));
        Assert.Contains("Wyvern Poison", AliasNames("2014/rule/sample-poisons"));
    }

    [Theory]
    [InlineData("2024/rule/finesse-weapon-property", "Finesse", "qualifier")]
    [InlineData("2024/monster/tough", "Thug", "counterpart")]
    [InlineData("2014/feature/action-surge-1-use", "Action Surge", "qualifier")]
    [InlineData("2014/rule/melee-attacks", "Grappling", "heading")]
    [InlineData("2014/rule/damage-rolls", "Critical Hit", "section")]
    [InlineData("2014/rule/melee-attacks", "Unarmed Strike", "section")]
    [InlineData("2024/rule/unarmed-strike", "Shove", "curated")]
    public void AliasTable_Row_RecordsWhereTheAliasCameFrom(string reference, string alias, string source)
    {
        Assert.Contains($"{alias}|{SrdNames.Key(alias)}|{source}", AliasRows(reference));
    }

    [Theory]
    [InlineData("qualifier", 108)]
    [InlineData("counterpart", 555)]
    [InlineData("heading", 85)]       // SrdCuratedAliases.Headings, every one applied
    [InlineData("section", 22)]       // section-pair names the section's text uses, without a heading of that name
    [InlineData("curated", 8)]        // SrdCuratedAliases.Names
    public void AliasTable_PerSource_HasPinnedTotals(string source, long expected)
    {
        Assert.Equal(expected, _fixture.Scalar($"SELECT COUNT(*) FROM alias WHERE source = '{source}';"));
    }

    [Theory]
    [InlineData("race", "species")]
    [InlineData("subrace", "subspecies")]
    [InlineData("spell", "spell")]
    [InlineData("rule", "rule")]
    public void KindIn2024_AndBack_MapsRacesToSpecies(string kind2014, string kind2024)
    {
        Assert.Equal(kind2024, SrdCounterparts.KindIn2024(kind2014));
        Assert.Equal(kind2014, SrdCounterparts.KindIn2014(kind2024));
    }

    /// <summary>The matching rules on hand-made documents, where each rule can be isolated.</summary>
    [Fact]
    public void Match_SlugBeforeName_AndAmbiguousNamesUnpaired()
    {
        IndexedDocument[] docs =
        [
            new(1, "2014", "monster", "orc", "Orc", "orc", null),
            new(2, "2014", "monster", "gnoll", "Gnoll Pack", "gnoll pack", null),
            new(3, "2014", "monster", "twin-a", "Twin", "twin", null),
            new(4, "2014", "monster", "twin-b", "Twin", "twin", null),
            new(10, "2024", "monster", "orc", "Orc Warrior", "orc warrior", null),
            new(11, "2024", "monster", "gnoll-pack", "Gnoll Pack", "gnoll pack", null),
            new(12, "2024", "monster", "twin", "Twin", "twin", null),
        ];
        var warnings = new List<string>();

        var pairs = SrdCounterparts.Match(docs, warnings);

        Assert.Equal(
            ["1-10-slug", "2-11-name"],
            pairs.Where(p => p.Source != "manual").Select(p => $"{p.Doc2014.Id}-{p.Doc2024.Id}-{p.Source}"));
    }

    /// <summary>
    /// The name rule's 2024-side guards: a name two 2024 documents share pairs with neither, and a 2024 document already
    /// paired by slug gains no second partner by name (2014 "Orc Warrior" is not the 2024 orc, which is 2014's Orc).
    /// </summary>
    [Fact]
    public void Match_NameAmbiguousIn2024OrAlreadySlugPaired_PairsNothingMore()
    {
        IndexedDocument[] docs =
        [
            new(1, "2014", "monster", "orc", "Orc", "orc", null),
            new(2, "2014", "monster", "orc-warrior-old", "Orc Warrior", "orc warrior", null),
            new(3, "2014", "monster", "solo", "Solo", "solo", null),
            new(10, "2024", "monster", "orc", "Orc Warrior", "orc warrior", null),
            new(11, "2024", "monster", "solo-a", "Solo", "solo", null),
            new(12, "2024", "monster", "solo-b", "Solo", "solo", null),
        ];

        var pairs = SrdCounterparts.Match(docs, []);

        Assert.Equal(["1-10-slug"], pairs.Where(p => p.Source != "manual").Select(p => $"{p.Doc2014.Id}-{p.Doc2024.Id}-{p.Source}"));
    }

    [Fact]
    public void Match_Features_CompareNamesWithinTheirClassOnly()
    {
        IndexedDocument[] docs =
        [
            new(1, "2014", "feature", "extra-attack-1", "Extra Attack", "extra attack", "fighter"),
            new(2, "2014", "feature", "barbarian-extra", "Extra Attack", "extra attack", "barbarian"),
            new(10, "2024", "feature", "fighter-extra-attack", "Extra Attack", "extra attack", "fighter"),
            new(11, "2024", "feature", "monk-extra-attack", "Extra Attack", "extra attack", "monk"),
        ];

        var pairs = SrdCounterparts.Match(docs, []);

        Assert.Equal(["1-10"], pairs.Select(p => $"{p.Doc2014.Id}-{p.Doc2024.Id}"));
    }

    /// <summary>
    /// The feature base-name rule in isolation: graded, repeated and ": Class" names pair with the one 2024 feature of
    /// their class, many-to-one; a colon that names an option ("Metamagic: Careful Spell") is not a grade, and another
    /// class's feature stays out of it. (Earlier pairs and trailing numbers have tests of their own below.)
    /// </summary>
    [Fact]
    public void Match_FeatureGrades_PairWithinTheirClassByBaseName()
    {
        IndexedDocument[] docs =
        [
            new(1, "2014", "feature", "wild-shape-a", "Wild Shape (CR 1/4 or below)", "wild shape cr 1 4 or below", "druid"),
            new(2, "2014", "feature", "wild-shape-b", "Wild Shape (CR 1 or below)", "wild shape cr 1 or below", "druid"),
            new(3, "2014", "feature", "spellcasting-druid", "Spellcasting: Druid", "spellcasting druid", "druid"),
            new(4, "2014", "feature", "asi-1", "Ability Score Improvement", "ability score improvement", "druid"),
            new(5, "2014", "feature", "asi-2", "Ability Score Improvement", "ability score improvement", "druid"),
            new(6, "2014", "feature", "metamagic-careful", "Metamagic: Careful Spell", "metamagic careful spell", "sorcerer"),
            new(7, "2014", "feature", "timeless-body", "Timeless Body", "timeless body", "monk"),
            new(10, "2024", "feature", "druid-wild-shape", "Wild Shape", "wild shape", "druid"),
            new(11, "2024", "feature", "druid-spellcasting", "Spellcasting", "spellcasting", "druid"),
            new(12, "2024", "feature", "druid-asi", "Ability Score Improvement", "ability score improvement", "druid"),
            new(13, "2024", "feature", "sorcerer-metamagic", "Metamagic", "metamagic", "sorcerer"),
            new(14, "2024", "feature", "druid-timeless-body", "Timeless Body", "timeless body", "druid"),
        ];

        var pairs = SrdCounterparts.Match(docs, []);

        Assert.Equal(
            ["1-10-feature", "2-10-feature", "3-11-feature", "4-12-feature", "5-12-feature"],
            pairs.Where(p => p.Source != "manual").Select(p => $"{p.Doc2014.Id}-{p.Doc2024.Id}-{p.Source}").Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A 2024 feature an earlier rule already paired takes no grades: 2014 "Wild Shape" pairs with 2024 "Wild Shape" by
    /// name, so the 2014 grade "Wild Shape (CR 1/4 or below)" must not become a second pair of the same 2024 feature
    /// through the grade rule. Nothing in the vendored data exercises this today; a re-vendor could.
    /// </summary>
    [Fact]
    public void Match_FeatureGrades_SkipA2024FeatureAnEarlierRulePaired()
    {
        IndexedDocument[] docs =
        [
            new(1, "2014", "feature", "wild-shape", "Wild Shape", "wild shape", "druid"),
            new(2, "2014", "feature", "wild-shape-a", "Wild Shape (CR 1/4 or below)", "wild shape cr 1 4 or below", "druid"),
            new(10, "2024", "feature", "druid-wild-shape", "Wild Shape", "wild shape", "druid"),
        ];

        var pairs = SrdCounterparts.Match(docs, []);

        Assert.Equal(["1-10-name"], pairs.Where(p => p.Source != "manual").Select(p => $"{p.Doc2014.Id}-{p.Doc2024.Id}-{p.Source}"));
    }

    // A trailing number in the NAME is a repeat, not part of the base name ("Unarmored Movement 1" and "2" are 2024's
    // one Unarmored Movement). The vendored data numbers repeats only in slugs; a re-vendor could number the names.
    [Fact]
    public void Match_FeatureGrades_NameWithATrailingNumber_PairsByItsBaseName()
    {
        IndexedDocument[] docs =
        [
            new(1, "2014", "feature", "um-1", "Unarmored Movement 1", "unarmored movement 1", "monk"),
            new(2, "2014", "feature", "um-2", "Unarmored Movement 2", "unarmored movement 2", "monk"),
            new(10, "2024", "feature", "monk-unarmored-movement", "Unarmored Movement", "unarmored movement", "monk"),
        ];

        var pairs = SrdCounterparts.Match(docs, []);

        Assert.Equal(["1-10-feature", "2-10-feature"], pairs.Where(p => p.Source != "manual").Select(p => $"{p.Doc2014.Id}-{p.Doc2024.Id}-{p.Source}"));
    }

    // A variant pairs with EVERY same-kind counterpart of its parent, not only the first.
    [Fact]
    public void Match_VariantItems_PairWithEachOfTheParentsCounterparts()
    {
        IndexedDocument[] docs =
        [
            new(1, "2014", "magic-item", "potion-of-healing", "Potion of Healing", "potion of healing", null) { Variants = ["potion-of-healing-greater"] },
            new(2, "2014", "magic-item", "potion-of-healing-greater", "Potion of Greater Healing", "potion of greater healing", null),
            new(10, "2024", "magic-item", "potion-of-healing", "Potion of Healing", "potion of healing", null),
            new(11, "2024", "magic-item", "potions-of-healing", "Potions of Healing", "potions of healing", null),
        ];

        var pairs = SrdCounterparts.Match(docs, []);

        Assert.Equal(["1-10-slug", "1-11-manual", "2-10-variant", "2-11-variant"], pairs.Select(p => $"{p.Doc2014.Id}-{p.Doc2024.Id}-{p.Source}").Order(StringComparer.Ordinal));
    }

    // A 2024 subclass level some earlier rule paired (here by slug) is not paired again by the subclass-level rule.
    [Fact]
    public void Match_SubclassLevels_SkipA2024LevelAlreadyPaired()
    {
        IndexedDocument[] docs =
        [
            new(1, "2014", "subclass", "draconic", "Draconic", "draconic", null),
            new(2, "2014", "level", "draconic-sorcery-6", "Draconic 6", "draconic 6", null) { SubclassSlug = "draconic", Level = 6 },
            new(3, "2014", "level", "draconic-6", "Draconic 6", "draconic 6", null) { SubclassSlug = "draconic", Level = 6 },
            new(10, "2024", "subclass", "draconic-sorcery", "Draconic Sorcery", "draconic sorcery", null),
            new(11, "2024", "level", "draconic-sorcery-6", "Draconic Sorcery 6", "draconic sorcery 6", null) { SubclassSlug = "draconic-sorcery", Level = 6 },
        ];

        var pairs = SrdCounterparts.Match(docs, []);

        Assert.Equal(["2-11-slug"], pairs.Where(p => p.Doc2014.Kind == "level").Select(p => $"{p.Doc2014.Id}-{p.Doc2024.Id}-{p.Source}"));
    }

    [Fact]
    public void Match_VariantItems_PairWithTheParentsSameKindCounterparts()
    {
        IndexedDocument[] docs =
        [
            new(1, "2014", "magic-item", "belt", "Belt of Giant Strength", "belt of giant strength", null) { Variants = ["belt-hill", "belt-frost"] },
            new(2, "2014", "magic-item", "belt-hill", "Belt of Hill Giant Strength", "belt of hill giant strength", null),
            new(3, "2014", "magic-item", "belt-frost", "Belt of Frost Giant Strength", "belt of frost giant strength", null),
            new(4, "2014", "magic-item", "orphan", "Orphan Charm", "orphan charm", null) { Variants = ["orphan-red"] },
            new(5, "2014", "magic-item", "orphan-red", "Red Orphan Charm", "red orphan charm", null),
            new(10, "2024", "magic-item", "belt", "Belt of Giant Strength", "belt of giant strength", null),
        ];

        var pairs = SrdCounterparts.Match(docs, []);

        Assert.Equal(
            ["1-10-slug", "2-10-variant", "3-10-variant"],
            pairs.Where(p => p.Source != "manual").Select(p => $"{p.Doc2014.Id}-{p.Doc2024.Id}-{p.Source}").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Match_SubclassLevels_PairThroughTheSubclassPairAtTheSameLevel()
    {
        IndexedDocument[] docs =
        [
            new(1, "2014", "subclass", "draconic", "Draconic", "draconic", null),
            new(2, "2014", "level", "draconic-6", "Draconic 6", "draconic 6", null) { SubclassSlug = "draconic", Level = 6 },
            new(3, "2014", "level", "draconic-1", "Draconic 1", "draconic 1", null) { SubclassSlug = "draconic", Level = 1 },
            new(4, "2014", "level", "fighter-5", "Fighter 5", "fighter 5", null) { Level = 5 },
            new(10, "2024", "subclass", "draconic-sorcery", "Draconic Sorcery", "draconic sorcery", null),
            new(11, "2024", "level", "draconic-sorcery-6", "Draconic Sorcery 6", "draconic sorcery 6", null) { SubclassSlug = "draconic-sorcery", Level = 6 },
            new(12, "2024", "level", "draconic-sorcery-3", "Draconic Sorcery 3", "draconic sorcery 3", null) { SubclassSlug = "draconic-sorcery", Level = 3 },
            new(13, "2024", "level", "fighter-5", "Fighter 5", "fighter 5", null) { Level = 5 },
        ];

        var pairs = SrdCounterparts.Match(docs, []);

        Assert.Contains("2-11-level", pairs.Select(p => $"{p.Doc2014.Id}-{p.Doc2024.Id}-{p.Source}"));
        Assert.Contains("4-13-slug", pairs.Select(p => $"{p.Doc2014.Id}-{p.Doc2024.Id}-{p.Source}"));
        Assert.DoesNotContain(pairs, p => p.Doc2014.Id == 3 || p.Doc2024.Id == 12);
    }

    [Fact]
    public void Match_ManualPairWithAMissingSide_IsSkippedWithAWarning()
    {
        IndexedDocument[] docs = [new(1, "2014", "monster", "thug", "Thug", "thug", null)];
        var warnings = new List<string>();

        var pairs = SrdCounterparts.Match(docs, warnings);

        Assert.Empty(pairs);
        Assert.Contains("Manual counterpart 2014/monster/thug → 2024/monster/tough skipped: 2024/monster/tough not in the content.", warnings);
        Assert.Equal(SrdCounterparts.Manual.Count + SrdCounterparts.Sections.Count, warnings.Count);
    }

    // The alias names of one document in the order the builder added them (doc.aliases, the FTS column).
    private IReadOnlyList<string> AliasNames(string reference)
    {
        var column = _fixture.Column($"SELECT aliases FROM doc WHERE edition || '/' || kind || '/' || slug = '{reference}';").Single();
        return column.Length == 0 ? [] : column.Split('\n');
    }

    // "alias|alias_key|source" rows of the alias table for one document, in no particular order.
    private IReadOnlyList<string> AliasRows(string reference) =>
        _fixture.Column(
            $"""
            SELECT a.alias || '|' || a.alias_key || '|' || a.source FROM alias a JOIN doc d ON d.id = a.doc_id
            WHERE d.edition || '/' || d.kind || '/' || d.slug = '{reference}';
            """);
}
