# spells.2024.json

Combat facts for SRD 5.2.1 (2024) spells. No 2024 spell record has a saving throw, an area or a healing table, and its
damage is one entry at the base slot (Flame Strike loses its radiant half; Magic Missile and Cure Wounds have no dice),
so for 2024 each entry here is the whole combat profile. Read by `SpellOverlay` (format in `spells.2014.md`).

## Source

Every number is verbatim from the SRD 5.2 markdown `07_Spells.md`
(`serving-solid-characters/Docs/dndsrd5.2_markdown-main/src/07_Spells.md`), which is the SRD 5.2.1 text, CC-BY-4.0:
"This work includes material from the System Reference Document 5.2.1 ("SRD 5.2.1") by Wizards of the Coast LLC,
available at https://www.dndbeyond.com/srd. The SRD 5.2.1 is licensed under the Creative Commons Attribution 4.0
International License, available at https://creativecommons.org/licenses/by/4.0/legalcode."
`SpellOverlayTests.Entries_2024_EveryValueIsInTheSrdMarkdown` re-reads each spell's section where the sibling
repository is checked out and fails on any dice, damage type, save ability, half-damage rule, area size or shape,
count, condition, healing or AC value the text does not state.

## What is here

Every combat spell a 2024 monster casts (Acid Arrow, Chain Lightning, Cone of Cold, Cure Wounds, Entangle, Finger of
Death, Fireball, Flame Strike, Guiding Bolt, Harm, Healing Word, Hold Monster, Hold Person, Ice Knife, Ice Storm,
Insect Plague, Lightning Bolt, Magic Missile, Mind Spike, Moonbeam, Phantasmal Killer, Power Word Kill, Ray of
Sickness, Scorching Ray, Shatter, Shield, Spirit Guardians, Spiritual Weapon, Thunderwave, Vitriolic Sphere, Wall of
Fire, Wall of Ice, Web), plus the common PC combat spells no monster casts (Fire Bolt, Sacred Flame, Eldritch Blast,
Inflict Wounds) for party archetypes and builds.

Judgments, marked as such: `approximation` on the spells whose effect lingers or branches (a zone's damage dealt once;
Power Word Kill's outright kill of a creature at 100 Hit Points or fewer simulated as its 12d12 alternative; Ice Knife's
explosion left out), and `note` where a detail is not simulated (Guiding Bolt's Advantage, Harm's maximum reduction).
Cast levels come from the monster data (a 2024 stat block lists the level it casts at); upcasting adds the `upcast`
dice or `upcast_targets` per level above the spell's own.
