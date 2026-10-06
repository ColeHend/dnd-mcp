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
| Campaign tracking (SQLite: who knows what, sessions, undo) | Done (Phase 6): `campaign`, `campaign_search`, `campaign_get`, `campaign_write`, `campaign_knowledge`, `campaign_session` and `campaign_history` over one local `campaigns.db` (people, places, quests, secrets and facts with their provenance; what each character, the party and the table know, and under which names; reads from a character's perspective that show only what they know; reveal gates and forbidden words; sessions with a live log and an end-of-session checklist; every write one undoable batch; entries as they stood after any session), six prompts, the `campaign://` resources, `dice_roll` logging to a live session, the rules, encounter and balance tools defaulting to the active campaign's ruleset, and the `backup` and `restore` commands |
| Character sheets and live combat tracking | Done (Phase 7): `campaign_character` (a campaign character's sheet: HP and temporary HP, spell slots and Pact Magic, resources, conditions and exhaustion, death saves, short and long rests with Hit Dice, level-ups, XP, inventory and coins, and the build the simulator fights it with; every change out of a fight one undoable batch) and `combat` (a live fight: initiative, turns and rounds, damage with resistances, healing, conditions and their durations, concentration saves, legendary actions, death saves, reminders that carry the call resolving them, a board for the players; `end` writes the sheets back as one undoable batch, with XP, loot and coins), `balance_simulate` fighting a stored fight or resuming the live one and playing characters from their sheets, `encounter_difficulty` with the campaign's party, the `campaign://<slug>/party` and `/combat/current` resources, and the `rules://tables/character-advancement` table |
| Markdown export and import (an Obsidian vault, a player-safe export, the PWA's campaign bundles) | Phase 8 |

The design, decisions and phase plan are in [PLAN.md](PLAN.md).

## Tools

| Tool | What it answers |
|---|---|
| `rules_search` | Which SRD entries mention these words (2014 SRD 5.1, 2024 SRD 5.2.1, or both). |
| `rules_get` | One SRD entry in full by ref or name, or both editions side by side; format `combatant` shows a monster as the simulator reads it; also the rules tables and the attribution. |
| `dice_roll` | A roll made for the user, with every die shown. While a campaign session is live the roll is logged to it; `secret` marks a roll behind the DM's screen. |
| `dice_odds` | Exact probabilities for a dice expression. |
| `encounter_difficulty` | How hard a fight is for a party, by the 2014 DMG method, the 2024 XP budget, or both; `party: "campaign"` takes the levels from the campaign's current party sheets. |
| `balance_dpr` | A build's damage per round, computed exactly over every die outcome (not simulated). |
| `balance_compare` | What a homebrew feature adds to a baseline build, in damage and in character levels. |
| `balance_simulate` | Who wins a whole fight, how often, and at what cost: a Monte Carlo simulation of a party against enemies, of a fight stored in a campaign (`encounter`), or of the live fight from where it stands (`from_state`); a party member can be a campaign character played from its sheet. Read-only: it writes nothing. |
| `campaign` | Your campaigns: create one (you play in it or you run it; 2014, 2024 or mixed rules), choose the one the other tools use, change its record, or see where things stand. |
| `campaign_search` | What a campaign holds, found by words or listed by kind, status and tag, as one perspective sees it: a character's view finds only what they know, under the names they know. |
| `campaign_get` | Up to ten entries in full (people, places, quests, secrets, facts, sessions), with their relations, facts, who knows them and their history. |
| `campaign_write` | Any change to a campaign, as one batch of ops applied together or not at all: entities, links, facts (who knows them, what they rest on, reveal gates), status, objectives, clocks, answers. `dry_run` previews it; invented material gets a register code (`F7`) to accept or strike. |
| `campaign_knowledge` | Who knows what: record it, reveal facts at the table (a reveal before its gate is met warns and is applied), check a draft (a lyric, a journal, a line of dialogue) against what its speaker knows, and the ledger of who knows each fact. |
| `campaign_session` | Sessions: plan one, start it (writes then belong to it), log notes, end it with the recap and a checklist of loose ends, record one played earlier, read them back. |
| `campaign_history` | What changed and when, entries as they stood after a given session, and undo of one batch (an undo is itself a batch, so it can be undone). |
| `campaign_character` | A character's sheet: read it (another perspective sees only a public line), create or patch it, and the table's changes to it: damage, healing, temporary HP, slots and resources spent, rests, conditions, level-ups, XP, inventory and coins, each one undoable batch (while the character is in a live fight from its sheet, its damage, healing, temporary HP, uses and conditions go to the fight instead, with no batch, and reach the sheet at the fight's `end`). |
| `combat` | A live fight at the table, step by step: initiative, turns, damage, healing, conditions, concentration, legendary actions and death saves, each step answered with the initiative table and the reminders it raised; `state` with a perspective is the players' board; `end` writes the fight back to the sheets as one undoable batch. |

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
- `build` with `hp` and `ac`: any feature-DSL build, for a real character sheet or a homebrew creature;
- `character`: a campaign character with a sheet (`"character:torch"`), fought with its sheet's `sim_profile` build (the
  sheet then needs a level, AC and maximum HP), else the archetype of its main class at its total level; a sheet with no
  classes, or whose class has no archetype (an artificer), needs a `sim_profile`, and is refused as an entry (an
  encounter run leaves that member out and says so). It starts at the sheet's maximum HP (less any reduction), not its
  current HP, with the sheet's AC and save proficiencies.

```json
{"party": [{"archetype": "fighter", "level": 5, "count": 2}, {"archetype": "cleric", "level": 5},
           {"archetype": "wizard", "level": 5}],
 "enemies": [{"monster": "ogre", "count": 3}], "seed": 42}
```

With `encounter` a fight stored in a campaign is the two sides (`"current"`, the one running now; `"last"`, the last one
ended; or a stored fight's name, planned, running or ended; a planned fight with no party in it yet fights the campaign's
current party), and `from_state: true` (once initiative is rolled) resumes the running fight from its hit points, slots,
conditions, concentration and turn, so "who wins from here?" has an answer mid-fight. Neither writes anything.

Monsters use greedy expected-damage tactics; there is no grid (a front line and a back line per side), no lair actions
(a stored fight in a lair uses the in-lair legendary action and Legendary Resistance counts), no fleeing and no morale,
and every result lists its assumptions and the targeting and resource policies in force. A seed reproduces a result
exactly (without one, a random seed is drawn and shown); `replay` shows one fight turn by turn; `precision` runs until
P(win) is known to a chosen half-width; `compare` runs the same fights with and without a feature and reports the paired
difference, which resolves far smaller changes than two separate runs.

**Campaigns.** One `campaigns.db` holds every campaign, each a player campaign (you play one character in it) or a DM
campaign (you run it). Every campaign tool takes `campaign` (a slug); left out, it is the one chosen in this session
(with `campaign` `use` or `create`, or a `campaign_session` `start` or `end` that names its campaign), else the last one
chosen with `use` or `create`, else the only one. `campaign` `list` shows each player campaign's character handle (the
`character:<slug>` its perspective takes). A campaign holds entities (characters,
locations, factions, items, quests, threads, questions, secrets, beats, clocks, sessions and more), the relations between
them, and facts, each with who knows it and how (knows, suspects, believes, heard, met, unaware, …), the session they
learned it in and the name they know a thing by.

- **Perspectives.** `campaign_search`, `campaign_get` and the knowledge resource read as `author` (the default:
  everything), `dm`, `table`, `party`, `public` or `character:<slug>`. Every view but the author's sees only what it
  knows, under the names it knows: no secret text, no author-only names, not even a count of what is hidden. A party
  that knows a villain only as "the old king" gets "the old king" and a neutral ref (`e:12`), never his true name, even
  by searching for it.
- **Reveal gates and forbidden words.** A fact can be gated (not before these facts, together with those, or once enough
  clues of a route are known) and carry words no player-facing text may use until it is revealed. `campaign_knowledge`
  `check` runs a draft (a song, a journal entry, an NPC's line) against its speaker's knowledge, those words and its
  audience. A write is applied with a warning, never refused, when it reveals a fact before its gate is met or when text
  players can read uses a forbidden word or a name they do not use: what happened at the table stands.
- **Sessions.** `campaign_session start` makes a session live: until `end`, writes without a session belong to it and
  `dice_roll` logs every roll to it. A `start` that names its campaign makes that campaign the current one, so the
  night's rolls and writes go there; a roll made while another campaign is current says which campaign's session is
  live. `end` stores the recap and returns a checklist of loose ends (names in the recap that match nothing, clocks not
  ticked, facts nobody learned, inventions to accept or strike). An open roll's label is shown with it in the players'
  views of the session; roll `secret` for one they must not see.
- **History and undo.** Every call that writes the campaign's record is one batch, and its id is printed with the call
  that reverses it (a fight's steps, and the sheet changes `campaign_character` sends to a live fight, write none: the
  fight's `end` is the batch): `campaign_history` `undo` reverses exactly one batch, and refuses (naming the later
  batches in the way) when later changes build on it. `as_of` shows entries as they stood at the end of a session.
- **Defaults for the other tools.** With a campaign chosen, `rules_search`, `rules_get`, `encounter_difficulty` and the
  balance tools use its ruleset when a call names no edition (and say so), and `encounter_difficulty` takes its
  `effective_level_offset` setting. `encounter_difficulty` with `party: "campaign"` takes the levels from the current
  party's sheets (and says who it counted); a member without a sheet or a level is refused with the call that gives one.

**Character sheets and combat.** Any campaign character can have a sheet, made and patched with `campaign_character`
`update` (classes and level, abilities, AC, maximum and current HP, slots, resources with their recharge, defenses,
features, notes, and a `sim_profile` build for the simulator). Every sheet change out of a fight is one batch, undone
like any other. Read from another perspective (`perspective`, or `campaign_get` with `include: ["sheet"]`), a sheet is
one public line: a current party member's name as that view knows it, level, classes, species, HP, AC, exhaustion and
conditions, nothing more; other characters read as having no sheet.

`combat` runs a fight at the table. `start` adds the current party, played from their sheets, and `add` brings in SRD
monsters by name (their stat blocks snapshotted), campaign characters and anything else by name with its HP and AC. Each
step (`initiative`, `next`, `damage`, `heal`, `condition`, `concentration`, `use`, `legendary`, `death_save`, …) answers
with what changed, the initiative table and the reminders it raised: a concentration save and its DC, a condition
ending, a save to repeat, a legendary action reset, a creature dropping or dying, each with the call that resolves it.

The server rolls only what a call does not give (initiative, damage and healing dice, concentration and death saves,
rolled hit points) and logs those rolls with the fight. A roll belongs to the creature it is rolled for: a damage roll
to its `source`, else to the creature whose turn it is (before the first turn, to its one target); a healing or
temporary-HP roll to its `source`, else to its one target, the creature healed. It is secret, left out of the players'
views of the session, when that creature is hidden; when it rolls the hit points of anything not on the party's side
(allies included); when the creature is not on the party's side and is a campaign character the party does not know as
itself (unseen, or known only in disguise); and, in a DM campaign, whenever the creature is an enemy or neutral. A roll
that belongs to no one (several targets, no source, and for damage no turn running) is secret when any one target's
would be. `damage`, `heal`, `initiative`, `concentration` and `death_save` take `secret`, which overrides all of this
either way.

The fight's hit points change turn by turn outside the campaign's history: no batch per hit, nothing for
`campaign_history` to list or undo. `end` writes the fight back as one batch: each sheet's HP, slots, resources,
conditions, exhaustion and items used, and any XP, loot and coins awarded. That batch is undone like any other (the undo
is refused, naming the later batch in the way, when a later sheet edit builds on it); `dry_run` previews it. While a
character is in the fight from its sheet (until the fight ends, even after it leaves), `campaign_character`'s damage,
healing, temporary HP, use and conditions go to the fight instead (the sheet gets them at `end`), and a rest is refused
until the fight is over. `state` with a `perspective` is the players' board: the order, the party's numbers, every
other creature's state as one word (unhurt, hurt, bloodied, down), conditions by name, and no hidden creature, secret
name or roll. A name you type that only starts like a word your hidden notes capitalise ("Baalite cultist" where only a
secret says Baal) is shown, and the step that typed it warns you, so you decide.

**Prompts.** In Claude Code each is a command, `/mcp__dnd__<name>` for the server registered as `dnd`; its arguments
are single words, and the draft or notes it works on come from the conversation.

| Command | What it does |
|---|---|
| `/mcp__dnd__session_recap [campaign] [session]` | Records a played session from your account of it: facts and who learned them, progress, new people and places, inventions to accept or strike, as dry runs you approve. |
| `/mcp__dnd__session_prep [campaign] [session]` | Prepares the next session as a run-sheet: reachable beats, clocks, what the NPCs want, open questions and gates, each fight's difficulty; saved as the session's prep once you approve. |
| `/mcp__dnd__knowledge_check <character> [campaign]` | Checks the latest draft against what that character knows: names they would not use, things they cannot know, forbidden words, secrets a song would reveal. |
| `/mcp__dnd__continuity_check [campaign]` | The DM's five-step continuity pass over a draft: its canon objects, what was played, who could know it, gates and forbidden words. Flags, never fixes. |
| `/mcp__dnd__in_character <character> [campaign]` | Writes the piece you ask for in a character's voice from only what they know, and checks it before showing it. |
| `/mcp__dnd__homebrew_review [campaign]` | Measures homebrew with `balance_compare` against the official option it replaces and reports its balance band, with the smallest fix. |

**Resources.** `campaign://list` (every campaign and its resources) and, per campaign, `campaign://<slug>/summary`,
`campaign://<slug>/threads` and `campaign://<slug>/party` (every current party member's sheet line): in Claude Code,
`@dnd:` mentions. Readable by URI though not listed: `campaign://<slug>/entity/<ref>`, `campaign://<slug>/session/<n>`
(or `live`, `last`), `campaign://<slug>/knowledge/<perspective>`, everything one view knows (e.g.
`knowledge/character:belmakor`), and `campaign://<slug>/combat/current`, the fight running now as `combat` `state`
shows it. The rules tables include `rules://tables/character-advancement` (XP and proficiency bonus by level, the same
in both editions). The tools reach everything the resources show.

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
removes files an older version left behind. Then restart every Claude session that was running: a new version may
migrate `campaigns.db` the first time it opens it (0.7.0 adds the tables for sheets and fights, after a pre-migration
backup), and an older server still running refuses the migrated file (a 0.6.0 process says a newer version of dnd-mcp
wrote it, and changes nothing) until it is restarted.

The rules index (`srd.db`) is a disposable cache in `$DND_MCP_CACHE_DIR`, else `$XDG_CACHE_HOME/dnd-mcp`, else
`~/.cache/dnd-mcp`. Set `DND_MCP_CACHE_DIR` to an absolute path: MCP configs are JSON, which expands nothing, so the
server expands a leading `~/` itself and ignores any other relative value. The server rebuilds the index by itself
(about a second) whenever the content, the curated corrections or the importer changes; `srd-build --force` rebuilds it
on demand. Deleting it is always safe, even while a session is running: the next rules call reopens or rebuilds it.

The campaigns (`campaigns.db`) are your data, not a cache. The file is `$DND_MCP_DB` when that is set (the file itself,
absolute or starting `~/`), else `campaigns.db` in the data directory: `$DND_MCP_DATA_DIR`, else `$XDG_DATA_HOME/dnd-mcp`,
else `~/.local/share/dnd-mcp`. The first campaign write creates it, never the server's start, so a server used only for
dice and rules never makes one. Several Claude sessions can use it at once, each with its own current campaign. The
server backs it up by itself into `backups/` beside it: before the first write of each UTC day, at the end of every
session, and before every schema migration and every restore. It keeps every pre-migration backup, the newest 10 of the
others, and the newest of each day for the last 30 days. By hand:

```bash
~/.local/share/dnd-mcp/bin/DndMcp backup                 # a backup now; prints its path (--reason names it: manual by default)
~/.local/share/dnd-mcp/bin/DndMcp restore <backup file>  # replace campaigns.db with a backup
```

`restore` is a command only, never a tool: other Claude sessions' servers may have campaigns.db open, so no model can
replace it in the middle of a conversation. It checks the backup first, saves the current campaigns.db as a
`pre-restore` backup (restore that file to undo the restore) and prints where; restart any running dnd-mcp servers
afterwards so they read the restored file.

For development against a local build, give the dev server its own cache and data, so it and the installed server never
replace each other's `srd.db` when their content differs, and a development build's schema migration never reaches the
campaigns the installed server uses (an older build refuses a campaigns.db a newer one has migrated). Set `DND_MCP_DB`
for it as well: it outranks the data directory, so a `DND_MCP_DB` you export for daily use would otherwise point the dev
server at your real campaigns.db:

```bash
dotnet build DndMcp.sln
claude mcp add --transport stdio --scope local --env DND_MCP_CACHE_DIR="$HOME/.cache/dnd-mcp-dev" \
  --env DND_MCP_DATA_DIR="$HOME/.local/share/dnd-mcp-dev" \
  --env DND_MCP_DB="$HOME/.local/share/dnd-mcp-dev/campaigns.db" dnd-dev \
  -- dotnet run --project DndMcp --no-build --no-launch-profile
```

The dev server's prompts are `/mcp__dnd-dev__<name>`. A prompt names its tools without a server prefix (the server
cannot know the name it was registered under) and tells the model they are the tools of the server the prompt came from,
so `/mcp__dnd-dev__session_recap` asks for the dev server's tools. With both servers connected the bare names match both,
and only that sentence steers the model: check which server's tools it calls, or connect one of the two at a time.

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

Curated corrections: 308 of the served records (257 from 2024, 51 from 2014) had damaged upstream text or data (text
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
