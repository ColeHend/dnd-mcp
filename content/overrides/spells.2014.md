# spells.2014.json

Combat facts for SRD 5.1 (2014) spells that the spell record's structure leaves out or states in a way a fight cannot
use. Read by `SpellOverlay`; `SpellNormalizer` derives each 2014 spell's profile from the record (attack type, save,
area, damage and healing tables) and the entry's fields then win. Every value is from the spell's own SRD 5.1 text (the
record's `desc` and `higher_level`, which ship in the data); `SpellOverlayTests.Entries_2014_EveryValueIsInTheSpellsOwnText`
holds each entry to that text.

## Format

An object keyed by spell index. Fields (all optional): `kind` (`attack` | `save` | `auto_hit` | `heal` | `parry`),
`attack` (`melee` | `ranged`), `save` (ability key), `on_success` (`half` | `none`), `damage` and `upcast`
(`[{"dice", "type"}]`, at the spell's level and per slot level above), `damage_adds_modifier`, `cantrip`
(`dice` | `beams`), `targets` / `upcast_targets`, `area` / `no_area`, `condition`
(`{"condition", "duration", "rounds"?}` in the stat block vocabulary), `heal` / `heal_modifier` / `upcast_heal`,
`ac_bonus`, `approximation` (how a lingering effect is simplified: an `approximated` warning on every caster) and `note`.

## What is here

- Scorching Ray (three rays, one attack each), Magic Missile (three auto-hitting darts, not one 3d4+3 roll), Eldritch
  Blast (beams by caster level): counts the record's tables cannot express.
- Hold Person, Hold Monster (paralyzed, save ends, 1 minute), Blindness/Deafness (blinded), Entangle and Web
  (restrained until a Strength check escapes): the record has the save but no condition.
- Acid Arrow's delayed 2d4, Flame Strike's area (the record's 40 is the cylinder's height, the radius is 10),
  Disintegrate (one creature: the recorded cube is for objects), Spirit Guardians (no damage in the record), Shield
  (+5 AC as a parry).
- Approximations for areas that linger (Cloudkill, Insect Plague, Wall of Fire, Blade Barrier): their damage is dealt
  once, when cast.

Which spells have no combat effect or are not modelled is a judgment, not a fact of the text: that catalogue is code
(`SpellNormalizer.NoCombatEffect` / `NotModelled`), pinned by `SpellCatalogueTests`.
