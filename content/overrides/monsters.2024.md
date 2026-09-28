# monsters.2024.json

Normalization facts about SRD 5.2.1 (2024) monsters that the vendored 5e-database record lacks. Read by
`MonsterOverrides` (format in `monsters.2014.md`), applied on top of the corrected record.

## What is here (341 entries)

**Initiative, every 2024 monster.** A 2024 stat block prints "Initiative +N (M)", and N is not always the Dexterity
modifier (118 of 341 add proficiency or more); the data has no initiative field. Each entry is the modifier printed in
the SRD 5.2 markdown (`serving-solid-characters/Docs/dndsrd5.2_markdown-main/src/12_MonstersA-Z.md` and
`13_Animals.md`, CC-BY-4.0), extracted by stat block heading: the heading's slug is the monster's index, or the prefix
of its forms' indexes (the markdown's one Werewolf block gives werewolf-human, -hybrid and -wolf). Each `note` repeats
the printed line. `MonsterOverridesTests.Initiative_2024_IsTheSrdMarkdownsValue` re-reads the markdown where the
sibling repository is checked out and fails on any difference, any unmapped heading and any monster without one.

**No legendary-use overrides.** The data stores no legendary action uses. The normalizer uses 3, and 4 in the lair
when the record has `xp_in_lair` (29 monsters), which matches SRD 5.2.1 ("3 (4 in Lair)" for those 29, "3" for the
solar, tarrasque and unicorn); the markdown drops the "Legendary Action Uses" line, so there is nothing further to
extract. An override would pin an exception if one ever appears.
