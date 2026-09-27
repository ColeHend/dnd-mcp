using System.Text.RegularExpressions;

namespace DndMcp.Repository.Srd.Index;

/// <summary>
/// Which 2014 document is "the same entry" as which 2024 document: what <c>edition: both</c> compares, what the
/// other-edition hints point at, and where the renamed-entry aliases come from (2024 <c>tough</c> answers to "Thug").
///
/// <para>
/// The rules, in order; a document paired by an earlier rule is not offered to the derived rules after it:
/// <list type="number">
/// <item>Same kind and same slug, level records included. Upstream kept most indexes across editions (294 monsters,
/// 317 spells, 273 class and subclass levels).</item>
/// <item>Same kind and the same <see cref="SrdNames.Key"/> name, when that name is unique in its kind in BOTH editions
/// (for features, unique within the same class). This catches re-indexed records such as 2014 <c>rage</c> / 2024
/// <c>barbarian-rage</c>. Uniqueness is what keeps it safe; an ambiguous name pairs nothing rather than guessing.
/// Level names are generated ("Fighter 5"), so levels take no part.</item>
/// <item><see cref="Manual"/>: renames no rule can see (2014 "Quipper" is 2024 "Piranha"), including across kinds
/// (the 2014 paladin feature Divine Smite is a 2024 spell).</item>
/// <item><see cref="Sections"/>: 2024 glossary entries and poisons the 2014 SRD covers inside a longer rules section.</item>
/// <item>Feature grades: within one class, a 2014 feature whose base name (<see cref="FeatureBaseKey"/>: trailing
/// "(qualifier)", ": Class" and trailing number stripped) is a 2024 feature's pairs with it, every grade with the one
/// feature ("Action Surge (1 use)" and "(2 uses)" are 2024 Action Surge; the seven fighter "Ability Score Improvement"
/// records, which share one name and are numbered only in their slugs, are the one 2024 feature). Deliberately
/// many-to-one: 2014 split what 2024 kept whole. Only documents no earlier rule paired take part, on both sides.</item>
/// <item>Variant items: a 2014 magic item listed in another's <c>variants</c> (Belt of Hill Giant Strength) pairs with
/// its parent's 2024 magic items: 2024 folded the variants into the parent's table.</item>
/// <item>Subclass levels: a 2014 subclass level pairs with the 2024 level of the same number of the subclass its own
/// subclass pairs with (Draconic 6 → Draconic Sorcery 6), where the subclass was renamed and the slugs differ.</item>
/// </list>
/// 2014 races and subraces pair with 2024 species and subspecies (<see cref="KindIn2024"/>).
/// </para>
/// <para>
/// Why pairing must be this thorough: when <c>edition: both</c> finds no counterpart, the tool can only guess by name
/// and hedge, and before it did, it told the model "No 2014 equivalent in the SRD" for Wild Shape, Grappling and every
/// class level, which a model relays as fact. The coverage tests in <c>SrdCounterpartTests</c> pin that no same-slug
/// document and no same-class feature name is left unpaired.
/// Changing any of this changes the index, so it needs a <see cref="SrdIndexSchema.Version"/> bump.
/// </para>
/// </summary>
public static partial class SrdCounterparts
{
    /// <summary>
    /// Hand-verified 2014 → 2024 renames. An entry is here only when the SRD clearly renamed (or split, or moved to
    /// another kind) the same thing: same role, same numbers or the same opening text, checked against the vendored
    /// data. Each pair also makes each side answer to the other's name (a counterpart alias). Variant magic items pair
    /// by rule (<see cref="Match"/>); the four 2014 insect swarms are listed here because monsters have no variant list.
    /// <c>SrdCounterpartTests</c> pins every entry, so a re-vendor that drops a side fails a test; the builder skips such
    /// an entry and reports it as a warning instead of failing the whole index.
    /// </summary>
    public static IReadOnlyList<SrdManualCounterpart> Manual { get; } =
    [
        // Subclasses: 2024 spells out the full subclass name in the index.
        Same(SrdKinds.Subclass, "berserker", "path-of-the-berserker"),       // Path of the Berserker
        Same(SrdKinds.Subclass, "devotion", "oath-of-devotion"),             // Oath of Devotion
        Same(SrdKinds.Subclass, "draconic", "draconic-sorcery"),             // Draconic Bloodline → Draconic Sorcery
        Same(SrdKinds.Subclass, "evocation", "evoker"),                      // School of Evocation → Evoker
        Same(SrdKinds.Subclass, "fiend", "fiend-patron"),                    // The Fiend → Fiend Patron
        Same(SrdKinds.Subclass, "land", "circle-of-the-land"),               // Circle of the Land
        Same(SrdKinds.Subclass, "life", "life-domain"),                      // Life Domain
        Same(SrdKinds.Subclass, "lore", "college-of-lore"),                  // College of Lore
        Same(SrdKinds.Subclass, "open-hand", "warrior-of-the-open-hand"),    // Way of the Open Hand → Warrior of the Open Hand

        // Monsters: SRD 5.2.1 renames; CR, size and role match in every pair.
        Same(SrdKinds.Monster, "acolyte", "priest-acolyte"),                 // Acolyte → Priest Acolyte (CR 1/4)
        Same(SrdKinds.Monster, "androsphinx", "sphinx-of-valor"),            // CR 17, 199 HP both
        Same(SrdKinds.Monster, "azer", "azer-sentinel"),                     // Azer → Azer Sentinel
        Same(SrdKinds.Monster, "bugbear", "bugbear-warrior"),                // Bugbear → Bugbear Warrior (not the Stalker)
        Same(SrdKinds.Monster, "centaur", "centaur-trooper"),                // Centaur → Centaur Trooper
        Same(SrdKinds.Monster, "cult-fanatic", "cultist-fanatic"),           // Cult Fanatic → Cultist Fanatic
        Same(SrdKinds.Monster, "flying-sword", "animated-flying-sword"),     // animated objects gained "Animated"
        Same(SrdKinds.Monster, "giant-poisonous-snake", "giant-venomous-snake"), // "poisonous" → "venomous"
        Same(SrdKinds.Monster, "giant-sea-horse", "giant-seahorse"),         // spelling only; CR 1/2, 16 HP both
        Same(SrdKinds.Monster, "gnoll", "gnoll-warrior"),                    // Gnoll → Gnoll Warrior
        Same(SrdKinds.Monster, "goblin", "goblin-warrior"),                  // Goblin → Goblin Warrior (not Minion or Boss)
        Same(SrdKinds.Monster, "gynosphinx", "sphinx-of-lore"),              // CR 11 both
        Same(SrdKinds.Monster, "half-red-dragon-veteran", "half-dragon"),    // CR 5 both; 2024 generalises the colour
        Same(SrdKinds.Monster, "hobgoblin", "hobgoblin-warrior"),            // Hobgoblin → Hobgoblin Warrior (not Captain)
        Same(SrdKinds.Monster, "kobold", "kobold-warrior"),                  // Kobold → Kobold Warrior
        Same(SrdKinds.Monster, "merfolk", "merfolk-skirmisher"),             // Merfolk → Merfolk Skirmisher
        Same(SrdKinds.Monster, "minotaur", "minotaur-of-baphomet"),          // CR 3 both
        Same(SrdKinds.Monster, "poisonous-snake", "venomous-snake"),         // "poisonous" → "venomous"
        Same(SrdKinds.Monster, "quipper", "piranha"),                        // CR 0, 1 HP, AC 13 both
        Same(SrdKinds.Monster, "rug-of-smothering", "animated-rug-of-smothering"), // animated objects gained "Animated"
        Same(SrdKinds.Monster, "sahuagin", "sahuagin-warrior"),              // Sahuagin → Sahuagin Warrior
        Same(SrdKinds.Monster, "sea-horse", "seahorse"),                     // spelling only; CR 0, 1 HP both
        Same(SrdKinds.Monster, "shrieker", "shrieker-fungus"),               // CR 0, 13 HP, AC 5 both
        Same(SrdKinds.Monster, "succubus-incubus", "incubus"),               // one 2014 block split into two
        Same(SrdKinds.Monster, "succubus-incubus", "succubus"),              // one 2014 block split into two
        Same(SrdKinds.Monster, "swarm-of-poisonous-snakes", "swarm-of-venomous-snakes"), // "poisonous" → "venomous"
        Same(SrdKinds.Monster, "swarm-of-quippers", "swarm-of-piranhas"),    // quipper → piranha
        Same(SrdKinds.Monster, "thug", "tough"),                             // Thug → Tough (CR 1/2)
        Same(SrdKinds.Monster, "tribal-warrior", "warrior-infantry"),        // Tribal Warrior → Warrior Infantry (CR 1/8)
        Same(SrdKinds.Monster, "veteran", "warrior-veteran"),                // Veteran → Warrior Veteran

        // The four insect-swarm variants of 2014's Swarm of Insects (CR 1/2 each) are rows of 2024's one Swarm of Insects.
        Same(SrdKinds.Monster, "swarm-of-beetles", "swarm-of-insects"),
        Same(SrdKinds.Monster, "swarm-of-centipedes", "swarm-of-insects"),
        Same(SrdKinds.Monster, "swarm-of-spiders", "swarm-of-insects"),
        Same(SrdKinds.Monster, "swarm-of-wasps", "swarm-of-insects"),

        // Spells: the 2024 rules renamed two SRD spells; level, range and opening text match.
        Same(SrdKinds.Spell, "branding-smite", "shining-smite"),             // 2nd level, +2d6 radiant, can't be invisible
        Same(SrdKinds.Spell, "feeblemind", "befuddlement"),                  // 8th level, 150 ft, "You blast the mind of a creature…"

        // Races and subraces became species and subspecies; the lineages carry the old subrace.
        new(SrdKinds.Subrace, "high-elf", SrdKinds.Subspecies, "elven-lineage-high-elf"), // High Elf → Elven Lineage: High Elf
        new(SrdKinds.Subrace, "rock-gnome", SrdKinds.Subspecies, "gnomish-lineage-rock-gnome"), // Rock Gnome → Gnomish Lineage

        // Traits.
        Same(SrdKinds.Trait, "breath-weapon", "draconic-breath-weapon-acid"),        // 2024 splits Breath Weapon by damage type
        Same(SrdKinds.Trait, "breath-weapon", "draconic-breath-weapon-cold"),        // (same split)
        Same(SrdKinds.Trait, "breath-weapon", "draconic-breath-weapon-fire"),        // (same split)
        Same(SrdKinds.Trait, "breath-weapon", "draconic-breath-weapon-lightning"),   // (same split)
        Same(SrdKinds.Trait, "breath-weapon", "draconic-breath-weapon-poison"),      // (same split)
        Same(SrdKinds.Trait, "damage-resistance", "draconic-damage-resistance-acid"), // dragonborn resistance, split by type
        Same(SrdKinds.Trait, "damage-resistance", "draconic-damage-resistance-cold"), // (same split)
        Same(SrdKinds.Trait, "damage-resistance", "draconic-damage-resistance-fire"), // (same split)
        Same(SrdKinds.Trait, "damage-resistance", "draconic-damage-resistance-lightning"), // (same split)
        Same(SrdKinds.Trait, "damage-resistance", "draconic-damage-resistance-poison"), // (same split)
        Same(SrdKinds.Trait, "darkvision", "darkvision-60"),                 // 60 ft both; 2024's 120-ft trait is new
        Same(SrdKinds.Trait, "gnome-cunning", "gnomish-cunning"),            // Gnome Cunning → Gnomish Cunning
        Same(SrdKinds.Trait, "hellish-resistance", "lineage-resistance-fire"), // tiefling fire resistance, same sentence
        Same(SrdKinds.Trait, "high-elf-cantrip", "high-elf-cantrip-versatility"), // the high elf's wizard cantrip
        Same(SrdKinds.Trait, "infernal-legacy", "fiendish-legacy"),          // the tiefling's spell legacy, generalised
        Same(SrdKinds.Trait, "lucky", "luck"),                               // halfling reroll-a-1; "Lucky" is now a feat

        // Class features renamed within their class: same class, same level, same effect.
        Same(SrdKinds.Feature, "aura-improvements", "paladin-aura-expansion"),        // 18th: auras reach 30 feet
        Same(SrdKinds.Feature, "channel-divinity-preserve-life", "life-preserve-life"), // 5 × cleric level HP, within 30 ft
        Same(SrdKinds.Feature, "channel-divinity-sacred-weapon", "devotion-sacred-weapon"), // + Cha to attack rolls
        Same(SrdKinds.Feature, "deflect-missiles", "monk-deflect-attacks"),           // 3rd: reduce by 1d10 + Dex + monk level
        Same(SrdKinds.Feature, "diamond-soul", "monk-disciplined-survivor"),          // 14th: all saves, reroll for a point
        Same(SrdKinds.Feature, "extra-attack-2", "fighter-two-extra-attacks"),        // 11th: three attacks
        Same(SrdKinds.Feature, "extra-attack-3", "fighter-three-extra-attacks"),      // 20th: four attacks
        Same(SrdKinds.Feature, "improved-divine-smite", "paladin-radiant-strikes"),   // 11th: +1d8 radiant on melee hits
        Same(SrdKinds.Feature, "ki", "monk-monks-focus"),                             // 2nd: ki points became Focus Points
        Same(SrdKinds.Feature, "ki-empowered-strikes", "monk-empowered-strikes"),     // 6th: unarmed strikes beat resistance
        Same(SrdKinds.Feature, "signature-spell", "wizard-signature-spells"),         // 20th: two 3rd-level spells
        Same(SrdKinds.Feature, "arcane-tradition", "wizard-subclass"),                // the choose-a-subclass feature,
        Same(SrdKinds.Feature, "bard-college", "bard-subclass"),                      // which 2024 names "<Class> Subclass"
        Same(SrdKinds.Feature, "divine-domain", "cleric-subclass"),
        Same(SrdKinds.Feature, "druid-circle", "druid-subclass"),
        Same(SrdKinds.Feature, "martial-archetype", "fighter-subclass"),
        Same(SrdKinds.Feature, "monastic-tradition", "monk-subclass"),
        Same(SrdKinds.Feature, "otherworldly-patron", "warlock-subclass"),
        Same(SrdKinds.Feature, "primal-path", "barbarian-subclass"),
        Same(SrdKinds.Feature, "ranger-archetype", "ranger-subclass"),
        Same(SrdKinds.Feature, "roguish-archetype", "rogue-subclass"),
        Same(SrdKinds.Feature, "sacred-oath", "paladin-subclass"),
        Same(SrdKinds.Feature, "sorcerous-origin", "sorcerer-subclass"),

        // Across kinds: 2024 moved these to another kind. The paladin's Divine Smite is a spell; the fighting styles are
        // feats (2024 has no Dueling or Protection feat in the SRD); the healing potion and the two cheapest spell
        // scrolls are also sold as adventuring gear.
        new(SrdKinds.Feature, "divine-smite", SrdKinds.Spell, "divine-smite"),                      // 2d8 radiant, +1d8 vs fiends and undead
        new(SrdKinds.Feature, "fighter-fighting-style-archery", SrdKinds.Feat, "archery"),           // +2 to ranged attack rolls
        new(SrdKinds.Feature, "ranger-fighting-style-archery", SrdKinds.Feat, "archery"),
        new(SrdKinds.Feature, "fighter-fighting-style-defense", SrdKinds.Feat, "defense"),           // +1 AC in armor
        new(SrdKinds.Feature, "fighting-style-defense", SrdKinds.Feat, "defense"),                   // (the paladin's)
        new(SrdKinds.Feature, "ranger-fighting-style-defense", SrdKinds.Feat, "defense"),
        new(SrdKinds.Feature, "fighter-fighting-style-great-weapon-fighting", SrdKinds.Feat, "great-weapon-fighting"),
        new(SrdKinds.Feature, "fighting-style-great-weapon-fighting", SrdKinds.Feat, "great-weapon-fighting"), // (the paladin's)
        new(SrdKinds.Feature, "fighter-fighting-style-two-weapon-fighting", SrdKinds.Feat, "two-weapon-fighting"),
        new(SrdKinds.Feature, "ranger-fighting-style-two-weapon-fighting", SrdKinds.Feat, "two-weapon-fighting"),
        new(SrdKinds.MagicItem, "potion-of-healing", SrdKinds.Equipment, "potion-of-healing"),      // 2d4 + 2, 50 gp gear
        new(SrdKinds.MagicItem, "spell-scroll-cantrip", SrdKinds.Equipment, "spell-scroll-cantrip"), // "Spell Scroll, Cantrip"
        new(SrdKinds.MagicItem, "spell-scroll-1st", SrdKinds.Equipment, "spell-scroll-level-1"),    // "Spell Scroll, Level 1"

        // The 2024 Rules Glossary repeats each condition as an entry of its own (2024/rule/prone beside
        // 2024/condition/prone, same text); 2014 has only the condition record. Same rule, so the glossary entry pairs
        // with it too (the 2014 condition compares against the 2024 condition first: counterparts sort by kind).
        new(SrdKinds.Condition, "blinded", SrdKinds.Rule, "blinded"),
        new(SrdKinds.Condition, "charmed", SrdKinds.Rule, "charmed"),
        new(SrdKinds.Condition, "deafened", SrdKinds.Rule, "deafened"),
        new(SrdKinds.Condition, "exhaustion", SrdKinds.Rule, "exhaustion"),
        new(SrdKinds.Condition, "frightened", SrdKinds.Rule, "frightened"),
        new(SrdKinds.Condition, "grappled", SrdKinds.Rule, "grappled"),
        new(SrdKinds.Condition, "incapacitated", SrdKinds.Rule, "incapacitated"),
        new(SrdKinds.Condition, "invisible", SrdKinds.Rule, "invisible"),
        new(SrdKinds.Condition, "paralyzed", SrdKinds.Rule, "paralyzed"),
        new(SrdKinds.Condition, "petrified", SrdKinds.Rule, "petrified"),
        new(SrdKinds.Condition, "poisoned", SrdKinds.Rule, "poisoned"),
        new(SrdKinds.Condition, "prone", SrdKinds.Rule, "prone"),
        new(SrdKinds.Condition, "restrained", SrdKinds.Rule, "restrained"),
        new(SrdKinds.Condition, "stunned", SrdKinds.Rule, "stunned"),
        new(SrdKinds.Condition, "unconscious", SrdKinds.Rule, "unconscious"),

        // Magic items.
        Same(SrdKinds.MagicItem, "arrow-of-slaying", "ammunition-of-slaying"),         // arrow → any ammunition
        Same(SrdKinds.MagicItem, "deck-of-many-things", "mysterious-deck"),            // same deck and cards
        Same(SrdKinds.MagicItem, "glamoured-studded-leather-armor", "glamoured-studded-leather"), // "Armor" dropped
        Same(SrdKinds.MagicItem, "iron-bands-of-binding", "iron-bands"),               // same sphere, same text
        Same(SrdKinds.MagicItem, "orb-of-dragonkind", "dragon-orb"),                   // same artifact
        Same(SrdKinds.MagicItem, "potion-of-healing", "potions-of-healing"),           // the rarity-table parent entry

        // Equipment: 2024 drops units and packaging from names ("Oil (flask)" → "Oil"); price and weight match.
        Same(SrdKinds.Equipment, "acid-vial", "acid"),
        Same(SrdKinds.Equipment, "alchemists-fire-flask", "alchemists-fire"),
        Same(SrdKinds.Equipment, "antitoxin-vial", "antitoxin"),
        Same(SrdKinds.Equipment, "arrow", "arrows"),                          // 20 for 1 gp both
        Same(SrdKinds.Equipment, "ball-bearings-bag-of-1000", "ball-bearings"),
        Same(SrdKinds.Equipment, "blowgun-needle", "needles"),                // 50 for 1 gp both
        Same(SrdKinds.Equipment, "chain-10-feet", "chain"),
        Same(SrdKinds.Equipment, "clothes-costume", "costume"),
        Same(SrdKinds.Equipment, "crossbow-bolt", "bolts"),                   // 20 for 1 gp both
        Same(SrdKinds.Equipment, "crossbow-hand", "hand-crossbow"),           // "Crossbow, hand" → "Hand Crossbow"
        Same(SrdKinds.Equipment, "crossbow-heavy", "heavy-crossbow"),
        Same(SrdKinds.Equipment, "crossbow-light", "light-crossbow"),
        Same(SrdKinds.Equipment, "dice-set", "dice"),
        Same(SrdKinds.Equipment, "flask-or-tankard", "flask"),
        Same(SrdKinds.Equipment, "holy-water-flask", "holy-water"),
        Same(SrdKinds.Equipment, "ink-1-ounce-bottle", "ink"),
        Same(SrdKinds.Equipment, "jug-or-pitcher", "jug"),
        Same(SrdKinds.Equipment, "ladder-10-foot", "ladder"),
        Same(SrdKinds.Equipment, "mirror-steel", "mirror"),
        Same(SrdKinds.Equipment, "oil-flask", "oil"),
        Same(SrdKinds.Equipment, "paper-one-sheet", "paper"),
        Same(SrdKinds.Equipment, "parchment-one-sheet", "parchment"),
        Same(SrdKinds.Equipment, "perfume-vial", "perfume"),
        Same(SrdKinds.Equipment, "playing-card-set", "playing-cards"),
        Same(SrdKinds.Equipment, "poison-basic-vial", "poison-basic"),
        Same(SrdKinds.Equipment, "pole-10-foot", "pole"),
        Same(SrdKinds.Equipment, "rations-1-day", "rations"),
        Same(SrdKinds.Equipment, "robes", "robe"),
        Same(SrdKinds.Equipment, "rope-hempen-50-feet", "rope"),              // 1 gp both; silk rope has no 2024 entry
        Same(SrdKinds.Equipment, "sling-bullet", "bullets-sling"),            // 20 for 4 cp both
        Same(SrdKinds.Equipment, "spike-iron", "spikes-iron"),
        Same(SrdKinds.Equipment, "string-10-feet", "string"),
        Same(SrdKinds.Equipment, "tent-two-person", "tent"),

        // Equipment categories: the magic-item categories became plural.
        Same(SrdKinds.EquipmentCategory, "potion", "potions"),
        Same(SrdKinds.EquipmentCategory, "ring", "rings"),
        Same(SrdKinds.EquipmentCategory, "staff", "staffs"),
        Same(SrdKinds.EquipmentCategory, "wand", "wands"),
        Same(SrdKinds.EquipmentCategory, "weapon", "weapons"),

        // Proficiencies: 2024 prefixes tool proficiencies with "Tool:" (and names the item as it now appears).
        Same(SrdKinds.Proficiency, "calligraphers-supplies", "tool-calligraphers-supplies"),
        Same(SrdKinds.Proficiency, "dice-set", "tool-dice"),
        Same(SrdKinds.Proficiency, "navigators-tools", "tool-navigators-tools"),
        Same(SrdKinds.Proficiency, "playing-card-set", "tool-playing-cards"),
        Same(SrdKinds.Proficiency, "poisoners-kit", "tool-poisoners-kit"),
        Same(SrdKinds.Proficiency, "thieves-tools", "tool-thieves-tools"),

        // Rules: 2014 section headings → 2024 Rules Glossary entries covering the same rule.
        Same(SrdKinds.Rule, "ability-checks", "ability-check"),
        Same(SrdKinds.Rule, "ability-scores-and-modifiers", "ability-score-and-modifier"),
        Same(SrdKinds.Rule, "advantage-and-disadvantage", "advantage"),        // one 2014 section, two glossary entries
        Same(SrdKinds.Rule, "advantage-and-disadvantage", "disadvantage"),     // (same split)
        Same(SrdKinds.Rule, "areas-of-effect", "area-of-effect"),
        Same(SrdKinds.Rule, "attack-rolls", "attack-roll"),
        Same(SrdKinds.Rule, "cantrips", "cantrip"),
        Same(SrdKinds.Rule, "cast-a-spell", "magic"),                          // the Cast a Spell action is now Magic
        Same(SrdKinds.Rule, "creature-size", "size"),
        Same(SrdKinds.Rule, "damage-resistance-and-vulnerability", "resistance"), // one 2014 section, two entries
        Same(SrdKinds.Rule, "damage-resistance-and-vulnerability", "vulnerability"), // (same split)
        Same(SrdKinds.Rule, "damage-rolls", "damage-roll"),
        Same(SrdKinds.Rule, "flying-movement", "flying"),
        Same(SrdKinds.Rule, "food-and-water", "dehydration"),                  // water needs, now a hazard entry
        Same(SrdKinds.Rule, "food-and-water", "malnutrition"),                 // food needs, now a hazard entry
        Same(SrdKinds.Rule, "knocking-a-creature-out", "knocking-out-a-creature"),
        Same(SrdKinds.Rule, "reactions", "reaction"),
        Same(SrdKinds.Rule, "rituals", "ritual"),
        Same(SrdKinds.Rule, "saving-throws", "saving-throw"),
        Same(SrdKinds.Rule, "skills", "skill"),
        Same(SrdKinds.Rule, "spell-attack-rolls", "spell-attack"),
        Same(SrdKinds.Rule, "suffocating", "suffocation"),
        Same(SrdKinds.Rule, "targets", "target"),
        Same(SrdKinds.Rule, "use-an-object", "utilize"),                      // the Use an Object action is now Utilize
        Same(SrdKinds.Rule, "what-is-a-spell", "spell"),
    ];

    /// <summary>
    /// 2024 entries the 2014 SRD covers inside a longer rules section: the 2024 Rules Glossary has Grappling, Critical
    /// Hit and Concentration, while 2014 has them only as "#### Grappling" in Melee Attacks, "#### Critical Hits" in
    /// Damage Rolls and "#### Concentration" in Duration; the 2024 poisons are paragraphs of 2014's Sample Poisons.
    /// Each pair was checked by reading both texts. Not renames: neither name becomes a counterpart alias ("Melee
    /// Attacks" is not another name for Grappling). The 2014 section answers to the 2024 name instead, as a heading
    /// alias, where its text uses that name (it calls Spider's Sting "Drow Poison", so that one is not added).
    /// </summary>
    public static IReadOnlyList<SrdManualCounterpart> Sections { get; } =
    [
        Same(SrdKinds.Rule, "areas-of-effect", "cone"),                       // #### Cone, Cube, Cylinder, Line, Sphere
        Same(SrdKinds.Rule, "areas-of-effect", "cube"),
        Same(SrdKinds.Rule, "areas-of-effect", "cylinder"),
        Same(SrdKinds.Rule, "areas-of-effect", "line"),
        Same(SrdKinds.Rule, "areas-of-effect", "sphere"),
        Same(SrdKinds.Rule, "damage-rolls", "critical-hit"),                  // #### Critical Hits
        Same(SrdKinds.Rule, "damage-rolls", "damage-types"),                  // #### Damage Types
        Same(SrdKinds.Rule, "dropping-to-0-hit-points", "death-saving-throw"), // #### Death Saving Throws
        Same(SrdKinds.Rule, "duration", "concentration"),                     // #### Concentration
        Same(SrdKinds.Rule, "melee-attacks", "grappling"),                    // #### Grappling
        Same(SrdKinds.Rule, "melee-attacks", "opportunity-attacks"),          // #### Opportunity Attacks
        Same(SrdKinds.Rule, "melee-attacks", "unarmed-strike"),               // the unarmed strike paragraph
        Same(SrdKinds.Rule, "special-types-of-movement", "climbing"),         // #### Climbing, Swimming, and Crawling
        Same(SrdKinds.Rule, "special-types-of-movement", "crawling"),
        Same(SrdKinds.Rule, "special-types-of-movement", "swimming"),
        Same(SrdKinds.Rule, "special-types-of-movement", "jumping"),          // #### Jumping
        Same(SrdKinds.Rule, "special-types-of-movement", "high-jump"),        // ***High Jump.*** under Jumping
        Same(SrdKinds.Rule, "special-types-of-movement", "long-jump"),        // ***Long Jump.*** under Jumping
        Same(SrdKinds.Rule, "vision-and-light", "blindsight"),                // #### Blindsight
        Same(SrdKinds.Rule, "vision-and-light", "darkvision"),                // #### Darkvision
        Same(SrdKinds.Rule, "vision-and-light", "truesight"),                 // #### Truesight
        Same(SrdKinds.Rule, "your-turn", "bonus-action"),                     // #### Bonus Actions

        // 2014 Sample Poisons: one paragraph per poison, same DC, damage and price as the 2024 poison records.
        new(SrdKinds.Rule, "sample-poisons", SrdKinds.Poison, "assassins-blood"),
        new(SrdKinds.Rule, "sample-poisons", SrdKinds.Poison, "burnt-othur-fumes"),
        new(SrdKinds.Rule, "sample-poisons", SrdKinds.Poison, "crawler-mucus"),
        new(SrdKinds.Rule, "sample-poisons", SrdKinds.Poison, "essence-of-ether"),
        new(SrdKinds.Rule, "sample-poisons", SrdKinds.Poison, "malice"),
        new(SrdKinds.Rule, "sample-poisons", SrdKinds.Poison, "midnight-tears"),
        new(SrdKinds.Rule, "sample-poisons", SrdKinds.Poison, "oil-of-taggit"),
        new(SrdKinds.Rule, "sample-poisons", SrdKinds.Poison, "pale-tincture"),
        new(SrdKinds.Rule, "sample-poisons", SrdKinds.Poison, "purple-worm-poison"),
        new(SrdKinds.Rule, "sample-poisons", SrdKinds.Poison, "serpent-venom"),
        new(SrdKinds.Rule, "sample-poisons", SrdKinds.Poison, "spiders-sting"),     // 2014's Drow Poison: DC 13, 1 hour, 200 gp
        new(SrdKinds.Rule, "sample-poisons", SrdKinds.Poison, "torpor"),
        new(SrdKinds.Rule, "sample-poisons", SrdKinds.Poison, "truth-serum"),
        new(SrdKinds.Rule, "sample-poisons", SrdKinds.Poison, "wyvern-poison"),
    ];

    /// <summary>
    /// The 2024 kind a 2014 kind's documents pair with: races became species and subraces subspecies; every other kind
    /// keeps its name. A 2024-only kind (poison) has no 2014 documents to pair.
    /// </summary>
    public static string KindIn2024(string kind2014) => kind2014 switch
    {
        SrdKinds.Race => SrdKinds.Species,
        SrdKinds.Subrace => SrdKinds.Subspecies,
        _ => kind2014,
    };

    /// <summary>The inverse of <see cref="KindIn2024"/>.</summary>
    public static string KindIn2014(string kind2024) => kind2024 switch
    {
        SrdKinds.Species => SrdKinds.Race,
        SrdKinds.Subspecies => SrdKinds.Subrace,
        _ => kind2024,
    };

    /// <summary>
    /// Every pair among <paramref name="documents"/>, by the rules in the type summary, in that order. Manual and section
    /// entries whose documents are missing are left out and described in <paramref name="warnings"/>.
    /// </summary>
    internal static IReadOnlyList<CounterpartPair> Match(IReadOnlyList<IndexedDocument> documents, List<string> warnings)
    {
        var pairs = new List<CounterpartPair>();
        var seen = new HashSet<(long, long)>();
        var paired2014 = new HashSet<long>();
        var paired2024 = new HashSet<long>();

        void Add(IndexedDocument a, IndexedDocument b, string source)
        {
            if (seen.Add((a.Id, b.Id)))
            {
                pairs.Add(new CounterpartPair(a, b, source));
                paired2014.Add(a.Id);
                paired2024.Add(b.Id);
            }
        }

        var byEditionKind = documents
            .GroupBy(d => (d.Edition, d.Kind))
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var kind in SrdKinds.All.Select(k => k.Name))
        {
            if (!byEditionKind.TryGetValue((SrdEdition.Edition2014, kind), out var old) ||
                !byEditionKind.TryGetValue((SrdEdition.Edition2024, KindIn2024(kind)), out var current))
            {
                continue;
            }

            var currentBySlug = current.ToDictionary(d => d.Slug, StringComparer.Ordinal);
            foreach (var doc in old)
            {
                if (currentBySlug.TryGetValue(doc.Slug, out var match))
                {
                    Add(doc, match, SrdIndexSchema.CounterpartBySlug);
                }
            }

            if (kind == SrdKinds.Level)
            {
                continue;
            }

            var oldKeys = old.GroupBy(NameGroup).ToDictionary(g => g.Key, g => g.Count());
            var currentByKey = current.GroupBy(NameGroup).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var doc in old)
            {
                var key = NameGroup(doc);
                if (paired2014.Contains(doc.Id) || oldKeys[key] != 1 ||
                    !currentByKey.TryGetValue(key, out var candidates) || candidates.Count != 1 ||
                    paired2024.Contains(candidates[0].Id))
                {
                    continue;
                }

                Add(doc, candidates[0], SrdIndexSchema.CounterpartByName);
            }
        }

        var byRef = documents.ToDictionary(d => (d.Edition, d.Kind, d.Slug));
        foreach (var (list, source) in new[] { (Manual, SrdIndexSchema.CounterpartManual), (Sections, SrdIndexSchema.CounterpartBySection) })
        {
            foreach (var manual in list)
            {
                var found2014 = byRef.TryGetValue((SrdEdition.Edition2014, manual.Kind2014, manual.Slug2014), out var a);
                var found2024 = byRef.TryGetValue((SrdEdition.Edition2024, manual.Kind2024, manual.Slug2024), out var b);
                if (found2014 && found2024)
                {
                    Add(a!, b!, source);
                }
                else
                {
                    warnings.Add(
                        $"Manual counterpart {manual} skipped: {(found2014 ? "" : $"2014/{manual.Kind2014}/{manual.Slug2014} ")}" +
                        $"{(found2024 ? "" : $"2024/{manual.Kind2024}/{manual.Slug2024} ")}not in the content.");
                }
            }
        }

        MatchFeatureGrades(byEditionKind, paired2014, paired2024, Add);
        MatchVariants(byEditionKind, pairs, paired2014, Add);
        MatchSubclassLevels(byEditionKind, pairs, paired2014, paired2024, Add);
        return pairs;
    }

    /// <summary>
    /// The base name a feature is compared by within its class: the name without a trailing parenthetical qualifier
    /// ("Wild Shape (CR 1 or below)"), without a ": &lt;its class&gt;" suffix ("Spellcasting: Wizard") and without a trailing
    /// number ("Unarmored Movement 2", a form no vendored name has today: upstream numbers repeats in the slug only), as
    /// a name key. A colon naming anything but the class is an option of a feature ("Metamagic: Careful Spell"), not a
    /// grade of it, and is kept.
    /// </summary>
    internal static string FeatureBaseKey(IndexedDocument feature)
    {
        var name = BareName(feature.Name) ?? feature.Name;
        var colon = name.LastIndexOf(':');
        if (colon > 0 && feature.ClassGroup is { } classSlug && SrdNames.Key(name[(colon + 1)..]) == SrdNames.Key(classSlug))
        {
            name = name[..colon];
        }

        return SrdNames.Key(TrailingNumber().Replace(name, string.Empty));
    }

    /// <summary>
    /// <paramref name="name"/> without a trailing parenthetical qualifier ("Finesse (Weapon Property)" → "Finesse",
    /// "Wild Shape (CR 1 or below)" → "Wild Shape"), or null when it has none. Only a trailing one counts.
    /// </summary>
    internal static string? BareName(string name) =>
        QualifiedName().Match(name) is { Success: true } match ? match.Groups["bare"].Value : null;

    // Feature grades: every unpaired 2014 feature with an unpaired same-class 2024 feature of the same base name. The
    // unpaired sets are taken before any of these pairs are added, so all grades reach the one 2024 feature.
    private static void MatchFeatureGrades(
        Dictionary<(string, string), List<IndexedDocument>> byEditionKind,
        HashSet<long> paired2014,
        HashSet<long> paired2024,
        Action<IndexedDocument, IndexedDocument, string> add)
    {
        if (!byEditionKind.TryGetValue((SrdEdition.Edition2014, SrdKinds.Feature), out var old) ||
            !byEditionKind.TryGetValue((SrdEdition.Edition2024, SrdKinds.Feature), out var current))
        {
            return;
        }

        var open2024 = current
            .Where(d => d.ClassGroup is not null && !paired2024.Contains(d.Id))
            .ToLookup(d => (d.ClassGroup, FeatureBaseKey(d)));
        foreach (var doc in old.Where(d => d.ClassGroup is not null && !paired2014.Contains(d.Id)).ToList())
        {
            foreach (var match in open2024[(doc.ClassGroup, FeatureBaseKey(doc))])
            {
                add(doc, match, SrdIndexSchema.CounterpartByFeature);
            }
        }
    }

    // Variant items: an unpaired 2014 magic item another 2014 item lists as a variant pairs with that parent's 2024
    // magic items (the same kind only: the 2014 Potion of Healing also pairs with 2024's gear entry, which is the plain
    // potion, not the Greater one).
    private static void MatchVariants(
        Dictionary<(string, string), List<IndexedDocument>> byEditionKind,
        List<CounterpartPair> pairs,
        HashSet<long> paired2014,
        Action<IndexedDocument, IndexedDocument, string> add)
    {
        if (!byEditionKind.TryGetValue((SrdEdition.Edition2014, SrdKinds.MagicItem), out var old))
        {
            return;
        }

        var bySlug = old.ToDictionary(d => d.Slug, StringComparer.Ordinal);
        var parentCounterparts = pairs
            .Where(p => p.Doc2014.Kind == SrdKinds.MagicItem && p.Doc2024.Kind == SrdKinds.MagicItem)
            .ToLookup(p => p.Doc2014.Id, p => p.Doc2024);
        foreach (var parent in old)
        {
            foreach (var variantSlug in parent.Variants)
            {
                if (bySlug.TryGetValue(variantSlug, out var variant) && variant.Id != parent.Id && !paired2014.Contains(variant.Id))
                {
                    foreach (var target in parentCounterparts[parent.Id].ToList())
                    {
                        add(variant, target, SrdIndexSchema.CounterpartByVariant);
                    }
                }
            }
        }
    }

    // Subclass levels whose slugs differ because the subclass was renamed: same level number, paired subclasses.
    private static void MatchSubclassLevels(
        Dictionary<(string, string), List<IndexedDocument>> byEditionKind,
        List<CounterpartPair> pairs,
        HashSet<long> paired2014,
        HashSet<long> paired2024,
        Action<IndexedDocument, IndexedDocument, string> add)
    {
        if (!byEditionKind.TryGetValue((SrdEdition.Edition2014, SrdKinds.Level), out var old) ||
            !byEditionKind.TryGetValue((SrdEdition.Edition2024, SrdKinds.Level), out var current))
        {
            return;
        }

        var subclassPairs = pairs
            .Where(p => p.Doc2014.Kind == SrdKinds.Subclass && p.Doc2024.Kind == SrdKinds.Subclass)
            .ToLookup(p => p.Doc2014.Slug, p => p.Doc2024.Slug, StringComparer.Ordinal);
        var open2024 = current
            .Where(d => d.SubclassSlug is not null && d.Level is not null && !paired2024.Contains(d.Id))
            .ToLookup(d => (d.SubclassSlug!, d.Level!.Value));
        foreach (var doc in old.Where(d => d.SubclassSlug is not null && d.Level is not null && !paired2014.Contains(d.Id)).ToList())
        {
            foreach (var subclass in subclassPairs[doc.SubclassSlug!])
            {
                foreach (var match in open2024[(subclass, doc.Level!.Value)])
                {
                    add(doc, match, SrdIndexSchema.CounterpartByLevel);
                }
            }
        }
    }

    // Features are compared within their class; every other kind across the whole kind.
    private static (string? Group, string Key) NameGroup(IndexedDocument doc) => (doc.ClassGroup, doc.NameKey);

    private static SrdManualCounterpart Same(string kind, string slug2014, string slug2024) => new(kind, slug2014, kind, slug2024);

    // "Finesse (Weapon Property)" → bare "Finesse". Only a trailing parenthetical counts.
    [GeneratedRegex(@"^(?<bare>.*\S)\s*\([^()]*\)\s*$")]
    private static partial Regex QualifiedName();

    [GeneratedRegex(@"\s+\d+\s*$")]
    private static partial Regex TrailingNumber();
}

/// <summary>One hand-verified rename: the 2014 document and the 2024 document that are the same entry.</summary>
public sealed record SrdManualCounterpart(string Kind2014, string Slug2014, string Kind2024, string Slug2024)
{
    public override string ToString() => $"2014/{Kind2014}/{Slug2014} → 2024/{Kind2024}/{Slug2024}";
}

/// <summary>
/// A document as the builder holds it while pairing and aliasing: identity, name and the few fields the rules read,
/// not the JSON. <see cref="ClassGroup"/> is a feature's class; <see cref="SubclassSlug"/> and <see cref="Level"/> a
/// level record's; <see cref="Variants"/> the slugs a magic item lists as its variants; <see cref="Headings"/> the
/// <c>####</c>/<c>#####</c> subsection headings of a 2014 rule; <see cref="TextKey"/> a 2014 rule's text as a name key,
/// for checking which names a section actually uses.
/// </summary>
internal sealed record IndexedDocument(long Id, string Edition, string Kind, string Slug, string Name, string NameKey, string? ClassGroup)
{
    public string? SubclassSlug { get; init; }

    public int? Level { get; init; }

    public IReadOnlyList<string> Variants { get; init; } = [];

    public IReadOnlyList<string> Headings { get; init; } = [];

    public string TextKey { get; init; } = string.Empty;
}

/// <summary>One 2014↔2024 pair and the rule that made it (an <c>SrdIndexSchema.Counterpart…</c> source).</summary>
internal sealed record CounterpartPair(IndexedDocument Doc2014, IndexedDocument Doc2024, string Source);
