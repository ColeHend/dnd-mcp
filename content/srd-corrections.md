# srd-corrections.json

Curated corrections to vendored SRD records that are provably wrong upstream: text spliced from another entry,
tables flattened into one run-on word, category lists that contradict their own items, 2014 spell text back-translated
from another language, stat-block numbers that contradict the stat block's own ability scores. The vendored files under
`5e-database/` are never edited (they are pinned by sha256 and re-vendored by script); the index applies these entries
on top when it builds `srd.db`, and every corrected document says so when it is shown
("*Corrected from the upstream data: <reason>*").

Format: `{"corrections": [{"ref": "2024/magic-item/potion-of-heroism", "set": {"desc": "…"}, "reason": "…", "source": "…"}]}`

- `ref`: `edition/kind/slug` of the record (a Rules Glossary entry is `2024/rule/<slug of its name>`).
- `set`: top-level properties to replace, each with its complete new value. Only properties the record already has.
- `add`: top-level properties to add, for a section upstream dropped (the Pirate Captain's and Unicorn's
  `bonus_actions`, the Entertainer's Pack's `weight`, the Glassblower's Tools' `utilize`, 2014 Heroism's
  `higher_level`). Only properties the record does NOT have; added properties follow the record's own. An entry has
  `set`, `add` or both, never the same property in both.
- `reason`: what upstream got wrong, one sentence starting "Upstream". It is printed on the document, so every string
  it quotes is upstream's text or the replacement's (a test checks each quote).
- `source`: where the replacement came from, as `SRD 5.2 markdown <file> › <heading>` for a 2024 record or
  `SRD 5.1 markdown <file> › <heading>` for a 2014 record, plus any SRD 5.2.1 difference applied (see below). The
  SRD 5.2 markdown is `serving-solid-characters/Docs/dndsrd5.2_markdown-main/src/*.md`; the SRD 5.1 markdown is
  `serving-solid-characters/Docs/dnd.srd.5.1-main/**.md` (both CC-BY-4.0).

`SrdCorrectionsTests` pins that every entry applies to an existing record, sets only properties it has and adds only
properties it lacks, and changes what it sets; that the file's rules refuse a broken entry; and the corrected facts
themselves. `SrdDataQualityTests` scans every record of both editions (corrections applied) for the damage signatures
below, lists with reasons what is deliberately left as upstream wrote it, and pins the "Left as upstream wrote it" list
here as still true. The file's sha256 is part of `srd.db`'s staleness key, so editing it rebuilds the index on next
start.

## What is corrected (306 entries: 256 in 2024, 50 in 2014)

### 2024 (SRD 5.2.1 data, text from the SRD 5.2 markdown)

| Kind | Entries | Damage |
|---|---|---|
| magic-item | 123 | Text spliced between records (Potion of Heroism had Gaseous Form's effect; Giant Strength had Heroism's; Staff of Fire had the Sphere of Annihilation's movement rules; Staff of Frost and Staff of Swarming Insects had other staffs' spell rows), tables run into one word or dropped rows (Potions of Healing lacked the Supreme row; Wand of Wonder held 9 of its 20 rows), tables and lists flattened into one line, stat blocks flattened, words run together ("expendthe") or split, paragraphs glued at a period or broken mid-sentence. |
| spell | 49 | Control Weather's text and tables spliced into Control Water (and cut from Control Weather), dropped or flattened tables (Confusion, Prismatic Spray/Wall, Scrying, Divine Word, Creation, Augury, Reincarnate, Teleport), flattened stat blocks (Summon Dragon, Giant Insect, Animate Objects, Find Steed), flattened bullet lists, run-together words, and 23 material texts with PDF comma noise ("worth, 5,000+ GP", "1,500+ GP,, which", a leading "(" shown as "M ((…", hyphens for dashes). |
| equipment | 44 | Wrong costs and weights (Longbow 5 GP for 50 GP, Dart 5 GP for 5 CP, Chain Shirt 14 lb. for 20 lb., …), Sling damage type, Trident damage (2014's 1d6/1d8), Brewer's Supplies and Tinker's Tools DCs, pack contents ("7 flasks of Oil" parsed as 7 Flasks *and* 7 Oil; the Entertainer's Pack had the 2014 pack's contents; the Scholar's Pack a Blanket), four damaged descriptions and the Scholar's Pack's, category claims (Hide Armor is Medium, not Light; Explorer's and Entertainer's Packs are Equipment Packs; the Disguise Kit is a Tool), and two dropped properties added: the Entertainer's Pack's weight (58½ lb.) and the Glassblower's Tools' Utilize line. |
| feature | 24 | Circle of the Land Spells lacked Ray of Frost, Shocking Grasp and Sleep; Oath of Devotion Spells had "Shielf of Faith"; Draconic Spells ran its level 5 and 7 rows together; Draconic Spells, Fiend Spells and Font of Magic had their table captions glued to the header row; Wizard Spellcasting dropped half a sentence; Divine Intervention broke mid-sentence before "Reaction"; words split at old line breaks ("Ar- mor"). |
| equipment-category | 6 | Martial Melee Weapons lacked the Longsword, Tools the Forgery Kit, Adventuring Gear the Entertainer's and Explorer's Packs, Equipment Packs the Entertainer's Pack; Armor and Weapons lacked the magic armor and weapons that claim them (the 2014 lists hold both). |
| monster | 4 | The Mule carried the Octopus's traits, action and reaction (upstream has no Octopus record); the Pirate Captain's Captain's Charm and the Unicorn's Unicorn's Blessing (3/Day) bonus actions were missing (added); the Archmage was worth 8,000 XP, not CR 12's 8,400. |
| feat | 2 | Boon of Irresistible Offense and Boon of Spell Recall let the ability increase go to any ability. |
| trait | 2 | Fiendish Legacy's "spell-casting"; the Rock Gnome lineage said only "You know the Prestidigitation cantrip." (upstream moved Mending to a separate trait). |
| species | 1 | Dragonborn linked only Darkvision and Draconic Flight; its Draconic Ancestry trait (the table behind Breath Weapon and Damage Resistance) was linked from nothing. |
| rule | 1 | Breaking Objects had a stray line holding only "k". |

### 2014 (SRD 5.1 data, text from the SRD 5.1 markdown)

| Kind | Entries | Damage |
|---|---|---|
| spell | 25 | Text back-translated from another language (Hold Monster's "a saving throw of Wisdom" and "a level 6 or higher location", Fire Shield's "depending on the model", Water Breathing's "until the end of its term", See Invisibility's "see through Ethereal", Shatter's "for each level of higher spell slot 2", Magic Mouth's whole description); wrong or truncated higher-level rules (Dominate Beast's "9th level spell slot", Conjure Animals missing the 9th-level slot, Heroism's missing entirely, added); Dominate Beast targeting "a creature"; dropped sentences (True Resurrection's undead clause, Create Water's "extinguishing exposed flames"); wrong words ("action modifier" for attack modifier, "ores" for orcs, "can make" for must make, "order of skunk", "starting form", "had advantage", "whenever" for wherever, "If can move", "5th level of higher", "each spell slot above 1st"); dice typos ("1dl0", "10d 10", "o f"); a repeated paragraph (Wall of Fire); and the materials of the back-translated spells, Magic Mouth's "10 inches" and Enlarge/Reduce's "A pinch iron powder". |
| monster | 18 | To-hit bonuses the stat block's own Strength and proficiency contradict (Kraken +7 for +17, Purple Worm +9 for +14, Gynosphinx +9 for +8, Black Bear +3 for +4, Brown Bear +5 for +6; `attack_bonus` corrected with the text); Assassin Sneak Attack "13 (4d6)"; Solar Slaying Longbow "190 hit points"; Efreeti Elemental Demise naming the djinni; Ettercap webbing missing its poison and psychic immunity; Lich Disrupt Life hitting "each living creature" for non-undead; Harpy Luring Song "the must move"; the Adult Bronze, Gold and Silver Dragons missing Change Shape; XP that contradicts the stat block's own Challenge (Brass Dragon Wyrmling 100 for Challenge 1's 200, Deep Gnome 50 for 100, Dretch and Riding Horse 25 for 50). |
| magic-item | 7 | Belt of Dwarvenkind missing its darkvision benefit; Mithral Armor missing "but not hide"; Rod of Lordly Might missing "(you choose the type of sword)"; Staff of the Python (uncommon) and Staff of Swarming Insects (rare) given as very rare; Bracers of Defense's "Wondous item"; Spell Scroll saying "If the casting is uninterrupted, the scroll is not lost." after the scroll crumbles to dust. |

## How the replacement text was produced

The text is the SRD markdown section for the entry, copied, with **whitespace and markup changes only**:

- one paragraph per line (the upstream 2024 convention) or one paragraph per array element (the upstream 2014
  convention for spell and magic-item `desc`/`higher_level`); blank lines dropped;
- table cells trimmed of alignment padding, separator rows written `| --- |`, the SRD 5.1 markdown's trailing empty
  table row dropped;
- a `Table: <title>` caption written as the plain title line the printed SRD shows;
- blockquote markers removed (a stat block quoted inside a spell or item keeps its lines and table);
- a paragraph the markdown broke mid-sentence rejoined (Dust of Sneezing and Choking's "30-foot" / "Emanation"), and
  runs of spaces inside a line collapsed;
- a 2024 magic item's first `desc` line (its item type, from which the formatter builds "Potion, Rare") and a
  variant's second line ("Rare (Silver)") are kept from upstream; a 2014 magic item's first `desc` line is the SRD 5.1
  markdown's italic type line, without the asterisks (it is what some of these corrections fix);
- spells keep upstream's split between `description`/`desc` and `higher_level`; a 2014 material is the SRD's
  parenthetical after "M", with a capital letter and a closing period, as every 2014 record stores it;
- monster action and trait text drops the markdown's italics ("*Melee Weapon Attack:*"), as every upstream monster
  record does; a monster correction replaces the whole array but only the damaged entries' text (and `attack_bonus`),
  keeping upstream's structure for the rest;
- the 2024 bonus actions and the Glassblower's Tools' Utilize line are added in upstream's structure for those sections
  (`dc`, `usage`), with the SRD's text.

2024 corrections were then checked against the text of the SRD 5.2.1 PDF (the edition the data is labelled with): each
sentence and each table cell of the replacement appears in 5.2.1, and every word the correction removes from upstream
is damage (a glued token, a duplicated two-column header, or another entry's text). The two bonus actions added this
round (Pirate Captain, Unicorn) are the SRD 5.2 markdown's text; no 5.2.1 PDF was on hand for them. The Archmage's
8,400 XP is the SRD 5.2 markdown's, and CR 12's row of the XP table in both SRDs; no 5.2.1 PDF was on hand for it
either. 2014 replacements are the SRD 5.1 markdown's text as it stands, including its hyphenation where it differs from
upstream's ("10-foot deep pit", "60-foot radius sphere"); no SRD 5.1 PDF was on hand to check them against.

### Where the printed SRD 5.2.1 differs from the 5.2 markdown (applied, and named in each entry's source)

- Figurine of Wondrous Power: the Giant Fly has **HP 19** (3d10 + 3) in SRD 5.2.1; the 5.2 markdown prints 15. Upstream
  already had 19.
- Hyphens the markdown drops: "40 foot radius" is "40-foot radius", "50-footlong"/"120-foothigh"/"10-footsquare" are
  "50-foot-long"/"120-foot-high"/"10-foot-square" (the hyphen was lost at a line break in the PDF's text layer).
- "con forms" is "conforms"; "1 1/2 pints" is "1½ pints"; a stat block's **CR** line starts its own line; the markdown
  escape `\*` is the plain asterisk the SRD prints; the Figurine's stat block reads "**Speed** 30 ft." (the markdown
  repeats "Speed"); the Mule's Hooves keep the "Hit:" label the markdown drops.

### SRD 5.2 → 5.2.1 errata noticed elsewhere (upstream already matches 5.2.1; nothing to correct)

The monster chapter of the 5.2 markdown predates several 5.2.1 fixes that upstream (5.2.1) has: Awakened Tree Slam
14 (3d6 + 4) (5.2: 13 (2d8 + 4)); Axe Beak Beak 6 (1d8 + 2) (5.2: 5 (1d6 + 2)); Ancient Copper Dragon Mind Jolt casts
Mind Spike (level 4 version) (5.2: level 5); Priest Spellcasting "(spell save DC 13)"; Adult Black Dragon Pounce "moves"
(5.2: "can move"); Owl Flyby / Rat Agile "an Opportunity Attack"; Gold dragon and Shadow "D20 Tests"; Succubus and
Incubus shape-shift wording; and "DM" → "GM" throughout (5.2 has eight "DM"s, 5.2.1 none). Anyone copying monster text
from the 5.2 markdown must check it against 5.2.1 first.

## Scope of the checks

- 2024: every magic item, spell, class feature, feat, trait, species and glossary entry was aligned against its SRD 5.2
  section, every equipment cost, weight, damage and tool DC against the SRD 5.2 tables, and every monster's trait and
  action names against the SRD; `SrdDataQualityTests` scans all 2024 kinds for the damage signatures.
- Both editions: every monster's Challenge Rating and XP against its SRD markdown stat block, and against the XP by
  Challenge Rating table (`SrdMonsterChallengeTests` holds every stat block's XP to the table, corrections applied).
- 2014: every spell's `desc`, `higher_level` and `material`, every condition, every magic item that has its own SRD 5.1
  section, and every monster's traits, actions, reactions and legendary actions were word-diffed against the SRD 5.1
  markdown (case, punctuation and "ft."/"feet" ignored). 2014 classes, features, races, backgrounds, rules and equipment
  were not. `SrdDataQualityTests` scans all 2014 kinds for the signatures of what that diff found: back-translated
  phrases, words split into letters, mistyped dice and ordinals, a truncated "7th-level.", higher-level text that names
  no slot level or starts at the wrong slot, plus the string-level signatures shared with 2024.

## Left as upstream wrote it

### 2024

- Twenty-two monsters with one lost hyphen in action or trait text ("5-footwide Line", "30foot radius"): fixing one would
  copy the monster's whole `actions` or `special_abilities` array into this file for a cosmetic slip.
- The three vampire forms' "If it can't use ShapeShift" (SRD: "Shape-Shift") and the Cleric class's "for which oyu have
  spell slots" (SRD: "you"), each one word inside a nested structure (`special_abilities`, `spellcasting.info`), for the
  same reason.
- The six rods and the Spell Scroll claim the Wondrous Items category, although their type lines say "Rod" and "Scroll"
  and SRD 5.2 files Rods and Scrolls as categories of their own (so the Wondrous Items list counts 133 where the SRD has
  126). Upstream 2024 has no rods or scrolls category record, and a correction cannot create one.
- Telekinesis ends mid-sentence ("…such as manipulating a simple tool,") in the SRD itself, 5.2 and 5.2.1 alike.
- Mid-sentence line wraps with every word intact where the next line starts in lower case (five class features, the
  Oath of Devotion subclass text, Long Jump): SrdProse rejoins them when rendering.
- Nine 2024 spells store their components inside `range` ("Touch Component: V, S"); SpellMarkdown moves them back on
  display and its tests use these records.
- The Spellbook (not an SRD 5.2 item) claims Adventuring Gear, which does not list it.
- Oil's description lacks only its final period.
- Druid Wild Shape: its text is intact (list items each on their own line, the Beast Shapes table in the slash-row form
  the formatters read); an earlier entry only reformatted it and was removed.
- The 5.2 markdown's monster chapter lacks many reactions upstream has; those are upstream's (5.2.1) and stay.

### 2014

- **Differences shaped like PHB errata.** In these the SRD 5.1 markdown and upstream each state a coherent rule, one
  reading like the other's later clarification, and no SRD 5.1 PDF is on hand to say which the SRD printed (the
  markdown carries at least one errata note of its own, in Prismatic Wall). Spells: Acid Splash ("you can see"),
  Contagion (poisoned first), Sleet Storm (concentration check), Polymorph and True Polymorph (0 hit points, "lasts
  until it is dispelled"), Disintegrate ("leaves it with 0 hit points"), Phantasmal Killer and Weird (start/end of
  turn), Color Spray ("until the end of your next turn"), Call Lightning (cloud position), Sanctuary ("or deals damage
  to another creature"), Glyph of Warding (moving the object), Simulacrum ("except that it is a construct"), Prismatic
  Wall (rod of cancellation), Moonbeam ("up to 60 feet"), Levitate ("loose object"), Unseen Servant ("Medium"),
  Heroes' Feast ("twelve other creatures"), Find Steed (closed list of forms; telepathy "with each other"), Slow ("each
  of its turns"). Magic items: Bag of Tricks ("The creature vanishes at the next dawn…"). Monsters: Doppelganger Ambusher
  ("In the first round of combat"), Shadow Shadow Stealth ("Its stealth bonus is also improved to +6."). Conditions:
  Exhaustion lacks "Also, being raised from the dead reduces a creature's exhaustion level by 1."
- **Same rule, other words.** Mending ("such as a broken key"), Spiritual Weapon ("above the 2nd"), Speak with Animals
  ("at a minimum"), Shillelagh ("a club or a quarterstaff"), Goodberry ("for a day"), Zone of Truth ("remain evasive"),
  Storm of Vengeance ("additional effects"), Blight's description ("the spell has no effect"); the materials of
  Confusion ("Three walnut shells."), Detect Thoughts ("A copper coin."), Feather Fall ("or a piece of down"), Illusory
  Script ("10gp, which this spell consumes") and Stone Shape; Magmin Touch ("until a target takes an action"), Marilith
  Multiattack ("can make seven attacks").
- **Cosmetic.** Lower-case ability names ("a wisdom saving throw"), "25gp" for "25 gp" in about twenty materials, "ft."
  for "feet" in monster text, numbered for bulleted lists, missing table captions, "1stlevel" (Candle of Invocation),
  "hit points maximum" (Berserker Axe), "varies" for "rarity varies" in three type lines, "motion less" (Gargoyle),
  "isn t" (Mimic), "spell casting" (Efreeti, Solar), "sun light" and "bit attack" (Vampire), "fast- talk" (Deception,
  Charisma), "two- handed" (Martial Arts), "weapons- that" (Marvelous Pigments), Reincarnate's "a d 100" before its race
  table.
- **The SRD 5.1 markdown is itself damaged, so there is no verbatim text to copy**: Animal Friendship has no
  higher-level text upstream and the markdown's reads "you can affect one additional beast t level above 1st";
  Compulsion's markdown reads "another Wisdom saving to try"; Animal Messenger's "3nd level" is the SRD's own typo.
- **Upstream is right and the markdown is not** (nothing to correct): Guardian of Faith ("delat" in the markdown), the
  White Dragons' Ice Walk ("moment"), Giant Sea Horse Charge ("it the target"), Scout Longbow ("ranged 150/600"),
  Gladiator Spear ("and range"), and the Adult Silver Dragon's Wing Attack DC 22, Bandit Captain and Mage Dagger and
  Merrow Harpoon damage, where upstream's numbers follow the stat block's ability scores and the markdown's do not.
- **Missing, not correctable here**: the Deck of Many Things lacks the Avatar of Death stat block the Skull card summons
  (the SRD 5.1 markdown has it inside the item, with footnote markup no upstream 2014 record uses).

## Re-vendoring

After a re-vendor, `SrdCorrectionsTests` fails for any entry whose record or property is gone, whose value upstream now
matches (the correction no longer changes anything: delete it), or whose added property upstream now has (move it to
`set` or delete it). `SrdDataQualityTests` fails for new damage and for any item in "Left as upstream wrote it" that
upstream changed. Replacement text comes from the SRD 5.2 markdown (2024) or the SRD 5.1 markdown (2014) with only the
changes listed above, 2024 text checked against SRD 5.2.1.
