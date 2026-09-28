# monsters.2014.json

Normalization facts about SRD 5.1 (2014) monsters that the vendored 5e-database record states wrongly in a way the
normalizer (`DndMcp.Repository/Srd/Combatants/MonsterNormalizer`) cannot read around. Read by `MonsterOverrides`,
applied on top of the **corrected** record (`srd-corrections.json` first, then these).

**Facts, never text.** An override changes how a record is *read* (this save halves the damage); it never replaces
rules text. Wrong text belongs in `content/srd-corrections.json`, where lookup, search and the normalizer all see the
fix; an override would leave `rules_get` quoting the broken text while the simulator ran the repaired one.

## Format

A JSON object keyed `"2014/<monster index>"`. Each value may hold:

- `initiative`, `legendary_uses`, `legendary_uses_in_lair` (integers) with a `note` saying why and where from;
- `actions`: an object keyed by action name (legendary names without "(Costs N Actions)"), each with any of
  `on_success` (`half` | `none`), `area` (`{"shape": "cone|cube|cylinder|line|sphere|emanation", "size": feet}`),
  `targets` (1–20), and a required `note`.

Unknown fields are refused at load. `MonsterOverridesTests` pins that every entry names a real monster and action,
and that removing any one override changes the normalized stat block (an override the data no longer needs fails).

## What is here (5 entries)

The five save-for-half actions whose data labels the save `"none"` while the SRD 5.1 text says "or half as much damage
on a successful one" (research `01-dnd5eapi.md` §7.4): adult red dragon Fire Breath, ancient white dragon Cold Breath,
green dragon wyrmling Poison Breath, lich Disrupt Life (legendary), winter wolf Cold Breath. Source: each action's own
text in the SRD 5.1 (the record's `desc`, identical to `dnd.srd.5.1-main/10_Monsters`). Without these, the normalizer
keeps the data's "none" and raises a `data_conflict` warning, which is how the five were found and how a sixth would be.

No 2014 monster needs an initiative override: 2014 initiative is the Dexterity modifier. SRD 5.1 gives every legendary
monster 3 legendary actions, the normalizer's default.
