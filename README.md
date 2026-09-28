# dnd-mcp

A local [MCP](https://modelcontextprotocol.io) server for D&D 5e, **2014 and 2024 rules**, written in C# on .NET 10.
Claude Code and Claude Desktop launch it over stdio.

| Area | Status |
|---|---|
| Dice rolling (cryptographic RNG) | Done (Phase 1): keep/drop, rerolls, exploding dice, min/max, success counts, `adv`/`dis`/`ea`, labels, pass/fail checks, optional `seed` |
| Exact dice odds | Done (Phase 1): exact fractions, floating point for large pools, seeded Monte Carlo when no exact form exists |
| Rules lookup (SRD 5.1 / 5.2.1 via the dnd5eapi dataset, offline) | Done (Phase 2): `rules_search` (full text, either edition or both) and `rules_get` (by ref or name; `edition: "both"` puts 2014 and 2024 side by side), plus the `rules://attribution` resource |
| Encounter difficulty (2014 + 2024) | Done (Phase 3): `encounter_difficulty` (2014 DMG thresholds and multipliers, 2024 XP budget, or both side by side; SRD monsters by name or ref, any other by CR; effective-level offset), plus the `rules://tables/*` resources (XP by CR, both editions' encounter tables, DMG monster statistics by CR), also served by `rules_get` |
| DPR maths + feature deltas for homebrew | Done (Phase 4): `balance_dpr` (exact damage per round of a build in the feature DSL: per-attack and per-rider breakdown, round-1 damage percentiles, power-attack choices, save effects with kill chances; round 1, fight or adventuring-day horizon; level curves and level × AC grids) and `balance_compare` (a feature's ΔDPR against a baseline, its level-equivalent and balance band, Bonus Action and Reaction collisions), plus the `rules://tables/{dpr-targets-by-level, gwf-expected-values, aoe-targets}` resources |
| Monte Carlo combat simulation | Done (Phase 5): `balance_simulate` (a party of class archetypes, feature-DSL builds or SRD monsters against SRD monsters or builds, fought 10,000 times with the dice rolled: win, defeat, draw and death odds with 95% intervals, rounds, per-combatant damage and resources, a replay of any fight, a paired comparison with and without a feature, reproducible by seed), `rules_get` format `combatant` (a monster as the simulator reads it), plus the `rules://tables/monster-stats-by-cr-empirical` resource |
| Campaign tracking (SQLite, knowledge/provenance, markdown export) | Phases 6–8 |

The design, decisions and phase plan are in [PLAN.md](PLAN.md).

## Tools

| Tool | What it answers |
|---|---|
| `rules_search` | Which SRD entries mention these words (2014 SRD 5.1, 2024 SRD 5.2.1, or both). |
| `rules_get` | One SRD entry in full by ref or name, or both editions side by side; format `combatant` shows a monster as the simulator reads it; also the rules tables and the attribution. |
| `dice_roll` | A roll made for the user, with every die shown. |
| `dice_odds` | Exact probabilities for a dice expression. |
| `encounter_difficulty` | How hard a fight is for a party, by the 2014 DMG method, the 2024 XP budget, or both. |
| `balance_dpr` | A build's damage per round, computed exactly over every die outcome (not simulated). |
| `balance_compare` | What a homebrew feature adds to a baseline build, in damage and in character levels. |
| `balance_simulate` | Who wins a whole fight, how often, and at what cost: a Monte Carlo simulation of a party against enemies. |

**The balance tools.** A build is written in a small JSON feature DSL: attacks (dice, damage type, to-hit, properties,
weapon mastery, cantrip scaling) and modifiers (`to_hit`, `extra_damage` for smites and Sneak Attack, `bonus_damage`,
`crit_range`, `advantage`, `lucky`, `elven_accuracy`, `damage_die_remap` for Great Weapon Fighting, `reroll_damage_take_best`
for Savage Attacker, `extra_attack` for Action Surge and bonus or reaction attacks, `power_attack`, `save_effect` for
Fireball-style effects, `condition_on_hit`, `ignore_cover`, and the defensive `ac`, `resistance` and `temp_hp`). Any
number can change with level through a step map such as `{"1": 1, "5": 2}`. `balance_dpr` reports:

- the damage per round on three horizons: round 1 (the nova), a fight of R rounds, and an adventuring day (the 2014 DMG's
  6–8 encounters and 2 short rests, a "light day" that is labelled unofficial, or a custom one);
- per attack and rider: hit and crit chances, uses per round and damage per use;
- the exact round-1 damage distribution;
- the power-attack decision;
- save effects over a shared damage roll (Fireball on four goblins: raw 89.2, effective 28.00, all four die 99.93%).

`balance_compare` measures ΔDPR under identical assumptions. It turns the delta into a level-equivalent (Δ ÷ the damage
per round the baseline gains per level in that tier, or RPGBOT's reference slope when the baseline does not scale) and a
band: Under, On budget, Creeping, Over or Breaking.

The default target at level L is the 2014 DMG's monster row for CR = L, with a typical save bonus from The Finished
Book. Table rulings the rules text does not settle (Hew and +PB, Cleave and the Attack action, GWF on rider dice, Savage
Attacker on crit dice) are flags, echoed in every result.

**Choosing the target.** Every target field is optional (`ac`, `cr`, `saves`, `hp`, resistances, a starting condition,
cover, …), and two fields change where the rest comes from:

- `target.monster`: an SRD monster by name (`"ogre"`) or ref (`"2014/monster/ogre"`), resolved like `encounter_difficulty`'s
  monsters. A name is looked up in the build's edition (the baseline's for `balance_compare`), so a 2014 build fights the
  2014 ogre; a ref is used as given. Its stat block supplies the AC, all six saves, its hit points (so kill chances
  appear), its resistances and immunities with their qualifiers, Magic Resistance, Evasion, Legendary Resistance (the
  count outside its lair) and its condition immunities. "From nonmagical attacks" applies unless the attack is a spell
  or has the property `magical` (`silvered` and `adamantine` likewise), and the notes say which attacks it applied to; a
  condition it is immune to is never attempted. Any other target field overrides the stat block, with a note, e.g.
  `{"monster": "ogre", "ac": 13}`.
- `target.profile`: the table behind the default AC and saves. `"dmg2014"` (default) is the DMG row above; `"mm2024"`
  and `"mm2014"` are the medians of that edition's SRD monsters of the CR (`rules_get` ref
  `rules://tables/monster-stats-by-cr-empirical`), rounded half up, with the warlock reference curve measured against
  the same profile.

`monster` takes neither `cr` nor `profile`: a stat block has its own numbers.

**Simulating a fight.** `balance_simulate` fights a party against enemies thousands of times with every die rolled
and reports what the averages hide: how often the party wins, loses everyone or loses someone, with 95% intervals; how
many rounds a fight takes; and per combatant how often it drops, the hit points it loses, the damage it deals and takes,
and the limited resources it spends. Each side is a list of entries, each exactly one of:

- `archetype` with `level` (and `edition`): a simple, subclass-free fighter, barbarian, paladin, ranger, rogue, monk,
  cleric, druid, wizard, sorcerer, warlock or bard, so "four level 5 characters" needs no builds;
- `monster`: any SRD monster by name or ref, normalized from its stat block (`rules_get` with `format: "combatant"`
  shows exactly what the simulator reads, and every result lists what a stat block's simulation leaves out);
- `build` with `hp` and `ac`: any feature-DSL build, for a real character sheet or a homebrew creature.

```json
{"party": [{"archetype": "fighter", "level": 5, "count": 2}, {"archetype": "cleric", "level": 5},
           {"archetype": "wizard", "level": 5}],
 "enemies": [{"monster": "ogre", "count": 3}], "seed": 42}
```

Monsters use greedy expected-damage tactics; there is no grid (a front line and a back line per side), no lair, no
fleeing and no morale, and every result lists its assumptions and the targeting and resource policies in force. A seed
reproduces a result exactly (without one, a random seed is drawn and shown); `replay` shows one fight turn by turn;
`precision` runs until P(win) is known to a chosen half-width; `compare` runs the same fights with and without a feature
and reports the paired difference, which resolves far smaller changes than two separate runs.

## Install for Claude Code

```bash
dotnet publish DndMcp -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o ~/.local/share/dnd-mcp/bin
claude mcp add --transport stdio --scope user dnd -- ~/.local/share/dnd-mcp/bin/DndMcp
claude mcp list        # dnd should show as connected
```

Register a **published binary**, not `dotnet run`: build output on stdout corrupts the MCP stream.

The publish directory holds the executable **and a `content/` directory** (the vendored SRD data, the 2024 Rules
Glossary, the licences and the monster and spell overrides the simulator reads). The server reads `content/` from beside the executable, so copy or move the whole
directory, never the binary alone. To check an install, and to build the rules index before the first session:

```bash
~/.local/share/dnd-mcp/bin/DndMcp srd-build     # exit 0 and a summary; the reason on stderr and exit 1 if broken
```

To upgrade, publish into an empty directory (delete `~/.local/share/dnd-mcp/bin` first): `dotnet publish` never
removes files an older version left behind.

The rules index (`srd.db`) is a disposable cache in `$DND_MCP_CACHE_DIR`, else `$XDG_CACHE_HOME/dnd-mcp`, else
`~/.cache/dnd-mcp`. Set `DND_MCP_CACHE_DIR` to an absolute path: MCP configs are JSON, which expands nothing, so the
server expands a leading `~/` itself and ignores any other relative value. The server rebuilds the index by itself
(about a second) whenever the content, the curated corrections or the importer changes; `srd-build --force` rebuilds it
on demand. Deleting it is always safe, even while a session is running: the next rules call reopens or rebuilds it.

For development against a local build, give the dev server its own cache, so it and the installed server never replace
each other's `srd.db` when their content differs:

```bash
dotnet build DndMcp.sln
claude mcp add --transport stdio --scope local --env DND_MCP_CACHE_DIR="$HOME/.cache/dnd-mcp-dev" dnd-dev \
  -- dotnet run --project DndMcp --no-build --no-launch-profile
```

Server logs go to stderr; `claude --debug=mcp` captures them.

## Build and test

```bash
dotnet build DndMcp.sln
dotnet test  DndMcp.sln
```

## Attribution

The statements below are also served to MCP clients as the `rules://attribution` resource (and by `rules_get` with
ref `rules://attribution`), with the exact data versions and the 5e-database licence, and every rules result names the
SRD it comes from.

This work includes material taken from the System Reference Document 5.1 ("SRD 5.1") by Wizards of the Coast LLC
and available at https://dnd.wizards.com/resources/systems-reference-document. The SRD 5.1 is licensed under the
Creative Commons Attribution 4.0 International License available at
https://creativecommons.org/licenses/by/4.0/legalcode.

This work includes material from the System Reference Document 5.2.1 ("SRD 5.2.1") by Wizards of the Coast LLC,
available at https://www.dndbeyond.com/srd. The SRD 5.2.1 is licensed under the Creative Commons Attribution 4.0
International License, available at https://creativecommons.org/licenses/by/4.0/legalcode.

SRD content is taken from the [5e-bits 5e-database](https://github.com/5e-bits/5e-srd-api/tree/main/packages/5e-database)
dataset (MIT licensed; see `content/LICENSES/`), and the 2024 rules from the SRD 5.2.1 Rules Glossary as structured JSON
from the [serving-solid-characters](https://github.com/ColeHend/serving-solid-characters) project.

Curated corrections: 307 of the served records (257 from 2024, 50 from 2014) had damaged upstream text or data (text
spliced from another entry, words run together, rows missing, text cut short or back-translated, XP that contradicts
the stat block's challenge rating). Their text is replaced with the SRD's own words, copied from the SRD 5.2 markdown for 2024 and the SRD 5.1 markdown for 2014, as listed
in `content/srd-corrections.json` and explained in `content/srd-corrections.md`. Every corrected entry says so under
its title ("Corrected from the upstream data: …"), and `rules://attribution` gives the file's sha256.

Not SRD text: the 2014 encounter-building tables (XP thresholds, encounter multipliers, adventuring-day XP) and the
Monster Statistics by Challenge Rating table come from the Dungeon Master's Guide (2014), the first three also from the
free 2014 Basic Rules, not from SRD 5.1, so the CC-BY licence above does not cover them. The DPR tools add the DMG
2014's Targets in Areas of Effect (p. 249), a typical monster save bonus by CR from The Finished Book (tomedunn), and two
community reference curves: RPGBOT's DPR target and the Warlock Baseline (Form of Dread). They are included for personal
use (PLAN.md, open question 1), and every result that uses them names that source.

This project is unofficial Fan Content, compatible with fifth edition.
