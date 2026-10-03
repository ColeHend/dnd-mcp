# Build `dnd-mcp`: a local C# (.NET 10) MCP server for D&D 5e (2014 + 2024)

> **Summary.** This is a stdio MCP server that Claude Code and Claude Desktop launch as a local process. It does five jobs:
> - **Rules lookup** for both editions, served from the dnd5eapi's own dataset pinned locally (no runtime API calls).
> - **Balance math:** exact closed-form DPR plus a seeded Monte Carlo combat simulator, both driven by one declarative "feature DSL". "This feature adds about 3.2 DPR at level 5" becomes a computed number.
> - **Dice rolling** from the OS cryptographic RNG, plus exact probability queries.
> - **Encounter difficulty** under both the 2014 and 2024 rules.
> - **Campaign tracking** in SQLite for campaigns you play in and campaigns you DM. It is built around *who knows what, since which session*, and exports to markdown so it works alongside your campaign skills.

---

## Context

`/home/coleh/Projects/Real/dnd-mcp` is empty. Research changed the task in four ways, and each one shapes the design below.

1. **The dnd5eapi has the right data, but the live API shouldn't be a runtime dependency.**
   - Measured live: 100 requests per second per IP (the docs' "10,000/s" is stale). A 429 comes back as `text/html`. There is no `Cache-Control`.
   - `/api/2024/monsters?challenge_rating=` is silently ignored. `/api/2024/rules` returns 404: **the 2024 API has no rules at all**.
   - 2024 monsters landed on 2026-09-22 and 2024 spells on 2026-09-12, so the 2024 data is days old and still changing.
   - The API's own dataset (5e-database) can be downloaded at a pinned tag. The server imports that locally, which gives the same data offline and deterministically.
   - The old `5e-bits/5e-database` repo is **archived**. The data now lives in the monorepo `5e-bits/5e-srd-api` under `packages/5e-database`, at tag `5e-database-v7.0.0`.
2. **Much of this already exists in `serving-solid-characters`.**
   - An 801-line TypeScript DPR library, `SolidCharacters/client/src/shared/customHooks/utility/tools/dndMath.ts` (based on LudicSavant's DPR Calculator v2.0). It is the port target, but it has **5 bugs** and only computes means.
   - SRD JSON for both editions. Its derived attack bonuses and save DCs match the stat-block prose 100%, so it becomes a **test oracle**. Its `2024/rules.json` (174 Rules Glossary entries) is the only structured source of 2024 rules anywhere.
   - The PWA's DM model (arcs, beats with allOf/anyOf edges, NPC secrets, sessions with loot, combatants). It can be imported.
3. **For campaign tracking, the hard requirement is knowledge, not notes.** Your skills already track this by hand:
   - Belmakor's three tiers: what Belmakor knows, what Cole knows, what the party knows.
   - Different names for the same thing per audience: Cole knows "the Axiom Cage", but in Belmakor's voice it is "the old king".
   - One Piece reveal gates and forbidden vocabulary: no NPC says "seal" until the axe is assembled.
   - **RULED** markers and supersession ("Corrected by Cole, 2026-08-30 … those figures are superseded").
   - The files have already drifted: `party-and-band.md` says Belmakor is level 11, while `SKILL.md` and `belmakor-build.md` say 12.

   So the database models facts, who knows each one, since which session, and which facts depend on which. The skills' reference files become exports of it.
4. **The 2014 encounter math is not in SRD 5.1.**
   - The thresholds, group multipliers and Adventuring Day XP are in the free 2014 Basic Rules and the DMG. They are not CC-BY.
   - "Monster Statistics by Challenge Rating" is DMG-only.
   - That's fine for a personal local server, but it becomes a licensing decision if you ever publish this.
   - Your local SRD 5.1 CR→XP table is also missing **CR 9–13 and CR 26–30**.

---

## Decisions

| # | Decision | Choice |
|---|---|---|
| Q1 | Transport | **Local stdio only** *(locked with you)*. Register a published self-contained binary at user scope in Claude Code. HTTP is deferred; `ModelContextProtocol.AspNetCore` exists if it's ever wanted. |
| Q2 | Dice RNG | **`RandomNumberGenerator.GetInt32`** (the OS CSPRNG, unbiased mask-rejection) for every table roll *(locked)*. The simulator uses a **seeded xoshiro256\*\*** for reproducibility. Never use `new Random(seed)`: in .NET 10 that is the legacy Knuth `CompatSeedImpl`. |
| Q3 | Campaign storage | **SQLite + markdown export/import** *(locked)* |
| Q4 | Simulator depth | **Core + extensible** *(locked)*: attacks, saves, AoE, crits, resistances, HP/death saves, multiattack, recharge, legendary actions/resistance, and common conditions. No grid. |
| D1 | Content source | 5e-database JSON **vendored in the repo at `5e-database-v7.0.0`**, English only, with a sha256 manifest. Add your `2024/rules.json` as the 2024 Rules Glossary. No HTTP at runtime. |
| D2 | Data access | **Microsoft.Data.Sqlite + Dapper + hand-written SQL migrations.** Not EF Core: the design depends on FTS5, triggers, STRICT tables, partial and expression indexes, `json_patch`, and recursive CTEs, and EF would fight all of them. |
| D3 | Two database files | `srd.db` is disposable and rebuilt from the vendored JSON whenever **any** input changes: the 5e-database manifest fingerprint (`ContentManifest.ComputeFingerprint`), the `rules-glossary-2024.json` hash, the `srd-corrections.json` hash, or the importer/schema version (`SrdIndexSchema.Version`, bumped by hand: the key covers inputs, not code). `campaigns.db` is precious and backed up. They never share a file. |
| D4 | Tool surface | **18 coarse tools**, each with an `action` parameter. Claude Code's tool search is on by default, so definitions load on demand. |
| D5 | Homebrew input | One declarative **feature DSL** (JSON "modifiers") consumed by both the closed-form DPR engine and the simulator. Claude translates a homebrew feature into modifiers; adding a feature needs no code. |
| D6 | Assembly prefix | `DndMcp.*`, distinct from `SolidCharacters.*` and `SavingCharacters.*` (the sibling precedent against assembly-identity clashes) |
| D7 | Ambiguous rules | Points where RAW is unsettled become **ruling flags** with stated defaults, and every result echoes the flags it used. The list is in the DPR section below. |
| D8 | Edition default | Every rules, balance and encounter tool takes `edition`. It defaults to the active campaign's ruleset, otherwise 2024. |

---

## Architecture

```
Claude Code / Claude Desktop
  │  spawns the process; JSON-RPC over stdin/stdout
  │  (stdio uses the legacy `initialize` handshake → protocol 2025-11-25)
  ▼
DndMcp  (Exe; ModelContextProtocol 2.2.0; all logs → stderr)
  ├─ Tools/ Resources/ Prompts/   thin: validate → call Domain/Repository → concise markdown
  ├─ Cli/                         `restore | srd-build | backup | export` — ops needing exclusive DB access, never MCP tools
  ├─ Startup                      SrdIndexService + SrdIndexWarmup (background; tools await readiness), CampaignDbMigrator
  │
  ├─ DndMcp.Domain   (zero project refs; pure and deterministic)
  │    Dice/         parser → AST, CryptoRoller, exact Distribution (PMFs), keep-highest DP
  │    Probability/  d20 face PMF (adv/dis/Elven Accuracy/Lucky/Bless), saves, Wilson CIs
  │    Features/     feature DSL model + FluentValidation validators + ruling flags
  │    Dpr/          per-turn DP (riders, policies, power-attack toggle), curves, deltas, LE bands
  │    Simulation/   Combatant model, engine, hooks, policies, conditions, Xoshiro256**, stats
  │    Encounters/   2014 thresholds + multipliers, 2024 budget, CR tables
  │    Campaign/     knowledge-verdict rules, beat reachability, perspective filter, op validators
  │
  └─ DndMcp.Repository  (refs Domain)
       Srd/          5e-database models + converters, importer → srd.db (FTS5), MonsterNormalizer + overrides
       Campaign/     Dapper stores, SQL migrations, change_log writer, FTS, backups
       Transfer/     markdown vault, skill export profiles, PWA bundle import

Files (XDG):  ~/.local/share/dnd-mcp/campaigns.db  (+ backups/, exports/)
              ~/.cache/dnd-mcp/srd.db              (rebuildable)
Overrides:    DND_MCP_DATA_DIR, DND_MCP_CACHE_DIR, DND_MCP_DB, XDG_DATA_HOME, XDG_CACHE_HOME (absolute paths or ~/ only)
```

Invariants worth stating once:
- **stdout carries only JSON-RPC.** One `Console.WriteLine` corrupts the session. Logging goes to stderr through `LogToStandardErrorThreshold = Trace`, and a test enforces it (see Verification).
- **The MCP handshake never waits on the SRD index build.** `MCP_TIMEOUT` is 30 s. The index builds in the background, and rules tools await a readiness `Task` with a timeout plus progress notifications.
- **Only `McpException` messages reach the model.** Any other exception becomes the generic "An error occurred invoking '<tool>'." A `ToolGuard` wrapper maps domain exceptions (dice syntax, unknown ref, validation) to `McpException` with an actionable message that lists the valid values.
- **Several processes may open `campaigns.db` at once**, one per Claude session. Connection string `Foreign Keys=True;Default Timeout=5`, then `PRAGMA busy_timeout = 5000` after every Open (`Default Timeout` only sets the command timeout; busy_timeout stays 0). Write with `conn.BeginTransaction()`, whose default is `BEGIN IMMEDIATE`: a deferred read-then-write transaction fails with `SQLITE_BUSY_SNAPSHOT` (517) however long it waits. Add an in-process write semaphore. All pinned by `DndMcp.Tests/Sqlite/WalConcurrencyTests`.

---

## Repository layout

```
dnd-mcp/
├─ global.json                  sdk 10.0.112, rollForward latestFeature, allowPrerelease false  (copy saving's)
├─ DndMcp.sln                   `dotnet new sln --format sln` (.NET 10 defaults to .slnx)
├─ README.md                    setup, registration, CC-BY 4.0 statements (verbatim) + MIT notice
├─ .github/copilot-instructions.md   conventions, as in the saving repo
├─ content/
│  ├─ 5e-database/v7.0.0/{2014,2024}/5e-SRD-*.json   vendored from packages/5e-database/src/{year}/en/
│  ├─ 5e-database/manifest.json                     tag, source URL, sha256 per file
│  ├─ rules-glossary-2024.json (+ .md provenance)   copied from serving-solid-characters/.../data/srd/2024/rules.json
│  ├─ srd-corrections.json (+ .md)                  curated fixes to provably wrong upstream records, verbatim SRD text
│  ├─ overrides/monsters.{2014,2024}.json           normalization fixes keyed {edition}/{index}
│  ├─ overrides/spells.2024.json                    DC / AoE / upcast overlay for combat spells
│  └─ LICENSES/                                     SRD-5.1 CC-BY, SRD-5.2.1 CC-BY, 5e-database MIT
├─ scripts/fetch-5e-database.sh  re-vendors at a given tag and regenerates the manifest
├─ DndMcp/                       Exe: Program.cs, Tools/, Resources/, Prompts/, Cli/, .mcp/server.json
├─ DndMcp.Domain/
├─ DndMcp.Repository/            Migrations/NNNN_*.sql (embedded)
├─ DndMcp.Tests/                 xUnit; refs Domain + Repository, NOT the host (sibling rule)
└─ DndMcp.IntegrationTests/      refs the host; in-memory MCP client over pipes
```

The saving plan's "known gaps" concluded that an integration-test project is the right home for host-level tests. This repo creates one on day one, so the tool layer is covered without breaking the "tests don't reference the host" rule.

### Conventions to match (verified in the sibling repos)

**Project settings**
- `net10.0` with `Nullable` and `ImplicitUsings` on.
- No `TreatWarningsAsErrors`. That matters here because the SDK emits **MCP9005** obsolete warnings on the logging, sampling and roots APIs, and we avoid those APIs anyway.

**Code style**
- File-scoped namespaces, `public sealed class` for services, `private readonly ILogger<X> _logger;`, 4-space Allman braces.
- All DI registration inline in `Program.cs`, with a comment justifying each lifetime.
- `IRunOnStartup` + `StartupRunnerHostedService` for startup work.
- FluentValidation validators live in Domain.
- Wire and discriminator values are **string constants, not enums**: stored data outlives enum renumbering.
- **XML `<summary>` docs explain *why* and *what breaks*.** The siblings call this the single most important convention.

**Tests**
- xUnit 2.9.3 with plain `Assert` only: no mocking library, no FluentAssertions. Use hand-written fakes and static factories.
- Name tests `Method_Scenario_Expectation` and use `[Theory]` heavily.
- Environment-dependent tests use a custom `FactAttribute` with `Skip`, because xUnit 2.9 has no `Assert.Skip`.

**SDK-specific rules (from the v2.2.0 source)**
- Register tools with `WithTools<T>()` on **sealed** classes. Static classes are illegal as type arguments (CS0718). The generic overloads are also AOT/trim-safe, unlike `WithToolsFromAssembly`.
- Set tool annotations explicitly on every tool. If they're unset the spec means destructive = true and openWorld = true.
- Tool names are snake_case with **no dots**: the SDK allows `.`, but the Claude API's name regex doesn't.
- The server name `dnd` makes the tools `mcp__dnd__<tool>`.
- Serializer options:
  - Copy `McpJsonUtilities.DefaultOptions`, which keeps `JsonStringEnumConverter`; without it enums serialize as integers.
  - Add `Encoder = UnsafeRelaxedJsonEscaping`, or `'` and non-ASCII characters come out `\uXXXX`-escaped.
  - Add `PropertyNamingPolicy = SnakeCaseLower`, so complex parameter objects (builds, ops) are snake_case like the DSL and the 5e data.
- DataAnnotations shape the input schema but are **not enforced**, so validate inside each tool.
- Tool results are one concise markdown text block. Skip structured output in v1: on the 2025-11-25 handshake Claude Code uses, non-object values get wrapped, and text is what the model reads anyway.

### Packages (latest stable, verified on nuget.org 2026-09-26)

| Package | Version | Where |
|---|---|---|
| `ModelContextProtocol` | 2.2.0 | host (the bundled `mcpserver` template pins 2.1.0, so bump it) |
| `Microsoft.Extensions.Hosting` | 10.0.12 | host |
| `Microsoft.Data.Sqlite` | 10.0.12 | Repository (the bundled e_sqlite3 includes FTS5 and JSON1) |
| `Dapper` | 2.1.89 | Repository |
| `FluentValidation` | 12.1.1 | Domain |
| `YamlDotNet` | 18.1.0 | Repository (Phase 8 only) |
| `Markdig` | 1.4.0 | Repository (Phase 8 only) |
| `xunit` / `xunit.runner.visualstudio` | 2.9.3 / 4.0.0 | tests (runner 4.x still runs v2 tests) |
| `Microsoft.NET.Test.Sdk` / `coverlet.collector` | 18.10.1 / 10.0.1 | tests |

**Not used:**
- Polly: no runtime HTTP.
- EF Core: see D2.
- `ModelContextProtocol.AspNetCore`: stdio only.
- The deprecated logging-to-client APIs (MCP9005). Log to stderr instead; `claude --debug=mcp` captures stderr.

---

## MCP surface

**Server instructions** (≤ 2,048 characters; longer text is truncated) name the task families and when to reach for each tool. With tool search on, the instructions carry more weight than individual descriptions. Each tool description lists the required arguments *per action* and gives one example, since MCP has no `input_examples` field.

### Tools (18)

| Tool | Hints | What it does |
|---|---|---|
| `rules_search` | RO, idem, closed | FTS over all SRD content. `{query, edition: 2014\|2024\|both, kinds?[], limit=10}` returns ranked refs with snippets. |
| `rules_get` | RO, idem, closed | `{ref \| kind+name, edition, format: concise\|full}` (`combatant` arrives with the Phase 5 normalizer). `both` returns a side-by-side 2014/2024 comparison. `combatant` returns the **normalized simulator view plus normalization warnings**, so you can see how the sim reads a stat block. |
| `dice_roll` | RO, closed | `{expression, times=1, label?, secret?, seed?}` returns totals and **every die face**. Rolls are logged to the active campaign; `secret` hides DM rolls from player-safe exports. |
| `dice_odds` | RO, idem, closed | `{expression}` such as `8d6>=30` or `4d6kh3`. Returns mean, SD, P(=/≥/≤), percentiles, and an exact fraction when the space is small. |
| `encounter_difficulty` | RO, idem, closed | `{edition, party: levels[] \| "campaign", monsters[{ref \| cr, count, exclude?}], effective_level_offset?}` returns budget, label, per-edition math and warnings. It can show both editions. |
| `balance_dpr` | RO, idem, closed | `{build, target{ac \| cr \| "level", save_bonus?, resist?}, levels?, ac_range?, rulings?}` returns DPR with a per-attack and per-rider breakdown, distribution percentiles, and the assumptions used. |
| `balance_compare` | RO, idem, closed | `{baseline, variant \| feature, levels=1..20, target, horizon: round1\|fight\|day, rest_preset}` returns a ΔDPR table, %Δ, **LE band on your homebrew-balance scale**, and Bonus Action / Reaction collision warnings. |
| `balance_simulate` | RO, closed | `{party[builds \| campaign character refs], enemies[{ref, count} \| statblock], encounter?, iterations=10000, seed?, policies, round_cap, replay?}` returns win / TPK / downed / death probabilities with CIs, rounds p50/p90, and damage per combatant. Sends progress notifications. |
| `campaign` | write | `list \| get \| create \| update \| use \| summary`. `create{name, role: player\|dm, ruleset, dm_name?, settings?}` |
| `campaign_search` | RO | `{query?, kinds?, status?, tags?, perspective, include_facts, as_of_session?, limit, cursor}` |
| `campaign_get` | RO | `{refs[≤10], include[relations\|facts\|knowledge\|sheet\|children\|sessions\|history], perspective, as_of_session?}` |
| `campaign_write` | write (delete → destructive) | Batched `ops[]`: `upsert \| delete \| link \| unlink \| fact \| progress` (status, objective, clock tick, beat met, answer question), plus `dry_run`, `session`, `reason`. Returns `batch_id` and warnings. |
| `campaign_knowledge` | write | `record \| reveal \| check \| ledger \| retract`. `check{perspective, text, diegetic?}` is the "does Belmakor even know that?" pass. |
| `campaign_session` | write | `list \| get \| plan \| start \| log \| end \| recap \| record_past`. `end` returns a follow-up checklist, e.g. names mentioned but not in the DB, or clocks not ticked. |
| `campaign_character` | write | `get \| update \| damage \| heal \| temp_hp \| spend_slot \| restore_slot \| use_resource \| rest \| condition \| level_up \| xp \| inventory_* \| currency` |
| `combat` | write | `start \| add \| roll_initiative \| next \| prev \| damage \| heal \| condition \| concentration \| death_save \| legendary \| state \| end`. Each step returns the initiative table plus reminders. |
| `campaign_history` | RO (undo → destructive) | `since{date \| session} \| entity \| as_of \| batch \| undo{batch_id}` |
| `campaign_transfer` | write, destructive | `export_markdown \| import_markdown \| import_pwa \| backup \| list_backups`. `dry_run` defaults to **true** and returns per-file diffs. |

**Output discipline**
- Concise by default; `detail: full` opts in to more.
- Paginate with `limit` and `cursor`.
- Keep results well under Claude Code's **10k-token warning** (the hard cap is 25k; beyond that the result is saved to a file). Serve long bodies as resources.

### Resources

**Rules**
- `rules://attribution`
- `rules://tables/{name}`: encounter tables for 2014 and 2024, CR→XP, monster-stats-by-CR, GWF expected values, AoE target counts.

**Campaigns**
- `campaign://list`
- `campaign://{slug}/summary`, `/party`, `/threads`, `/session/{n}`, `/entity/{ref}`
- `/knowledge/{perspective}`: for example, everything Belmakor may say.
- `/combat/current`
- `/export/{file}`

In Claude Code these are `@dnd:` mentions. Claude Desktop only attaches resources by hand, so **everything is also reachable through a tool**. Skip subscriptions.

### Prompts (`/mcp__dnd__<name>`; arguments are split on whitespace, so keep them single-token)

| Prompt | What it does |
|---|---|
| `session_recap` | Record, react, connect to threads. One `campaign_write` dry-run batch (facts with knowers and provenance, status changes, loot/XP). Apply after approval, then propose the skill-reference diff. |
| `session_prep` | Reachable beats, locked beats, clocks, NPC wants, open questions. Emits your run-sheet format, runs difficulty plus an optional simulation, and lists **Inventions (accept / strike)**. |
| `knowledge_check` | Belmakor's three-tier pass (`diegetic` for songs). |
| `continuity_check` | The DM's 5-step pass, including reveal gates and forbidden terms. |
| `in_character` | Loads only `knowledge/{character}` before drafting journals or lyrics. |
| `homebrew_review` | Runs `balance_compare` and reports on your homebrew-balance scale. |
| `bootstrap_from_skill` | One-time migration from a campaign skill (`source=skill:<file>`, keeping the files' own confidence caveats). |

---

## Subsystem designs

Full tables, formulas and example payloads are in `~/.claude/plans/dnd-mcp-research/` (see Design basis). This section keeps only what an implementer must not get wrong.

### 1. SRD content and rules lookup

**Vendoring**
- Download `packages/5e-database/src/{2014,2024}/en/*.json` at tag `5e-database-v7.0.0`. Pinned raw.githubusercontent and jsDelivr URLs both return HTTP 200.
- Record the tag and each file's sha256 in `manifest.json`.
- 2014 has 24 files. 2024 has the same set minus Races, Subraces and Rules, plus Poisons, Species, Subspecies and Weapon-Mastery-Properties.

**Models**
- Hand-written System.Text.Json models; keep the OpenAPI spec only as a reference.
- Every monster and spell Choice is typed (`Choice<T>` / `OptionSet<T>`, Phase 0). Only class and background Choices are still untyped.
- Converters are needed for:
  - multiattack `count`: a **string everywhere** in v7.0.0 (`"2"`, `"Number of Heads"`, `"1d4"`), including inside `action_options`. The live API still serializes `action_options` counts as ints, so `MultiattackCountConverter` accepts both;
  - damage entries that are either `Damage` or a `Choice`;
  - `desc` / `description`: mostly `string[]` `desc` in 2014 and `string` `description` in 2024, with exceptions: 2014 `desc` is a plain string in Alignments, Languages, Magic-Schools, Rules, Subraces and monster top level, and 2024 Magic-Items keep `desc` as a string (`StringOrStringArrayConverter` handles all of them);
  - `higher_level`: array in 2014, string in 2024.

**Index (`srd.db`)**
- `doc(edition, kind, index, name, json, text)` plus FTS5 over (name, aliases, text), porter tokenizer.
- An `alias` table maps 2014↔2024 renames for `edition: both`. Subclass indexes differ (`lore` ↔ `college-of-lore`, `fiend` ↔ `fiend-patron`), and many monsters were renamed. Build the map by name match plus a small manual list.
- Rules: 2014 comes from `5e-SRD-Rules.json`, which v7 turned into a 137-entry heading tree. **2024 comes from your rules glossary.**

**Formatters**
- Per-kind concise markdown: stat block, spell, class level table (`/levels` is a **bare array**), condition, and so on.
- `full` mode adds the raw fields.

**Attribution**
- Ship the SRD 5.1 and SRD 5.2.1 CC-BY statements verbatim, plus the MIT notice for 5e-database.
- Add no other Wizards attribution; both PDFs ask for none.

**Monster normalization** (used by `format: combatant` and the simulator). Every stat block becomes a `Combatant` plus a list of `NormalizationWarning`. Overrides are keyed `{edition}/{index}`. The quirks to handle:

**Multiattack**
- `actions[]` is a fixed routine.
- `actions[]` together with `action_options` (41 in 2024) means "replace one attack with X". Example: the red dragon makes 2 Rend attacks and chooses 1 of {Rend, Scorching Ray}.
- 47 multiattacks in 2024 read "Any combination".
- 21 multiattack references in 2024 match no action name. 20 name a spell inside Spellcasting; the 21st is erinyes "Entangling Rope", whose action is named "Entangling Rope (Requires Magic Rope)".
- All 41 combined `actions[]` + `action_options` multiattacks are typed `multiattack_type: "actions"`. A normalizer that switches on that field and reads only the matching list drops the options.
- Hydra ("Number of Heads") and violet fungus ("1d4") need special handling.

**Damage arrays**
- Only the first entry is guaranteed to apply on hit. Later entries may be riders, conditional or ongoing damage: 64 attacks in 2014 and 148 in 2024 have several entries. **Summing them all overestimates DPR.** Default to the first entry and use overrides for the rest.
- 2014 has 16 Choice damage entries.
- 2024 has **18 attacks with an `attack_bonus` and no `damage` key at all** (there are no empty arrays). 17 deal flat damage stated only in prose ("Hit: 1 Piercing damage"), so parse it; the 18th, roper Tentacle, only grapples. 2014 also has 18 attack_bonus actions without `damage` (non-damaging attacks such as Web).
- 2024 zombie and ogre-zombie Undead Fortitude have a `dc` with **no `dc_value`** ("DC 5 plus the damage taken"), so `DcValue` is nullable. Treating null as 0 would make every such save succeed.

**DCs**
- 28 of the 2024 `attack_bonus` + `dc` actions are **grapple escape DCs, not saves**.
- **5 mislabeled 2014 half-damage saves** need overrides: adult red dragon Fire Breath, ancient white Cold Breath, green wyrmling Poison Breath, lich Disrupt Life, winter wolf Cold Breath.

**Legendary actions**
- Neither edition stores the uses count. The default is 3; in 2024 it's 4 in a lair, and `xp_in_lair` works as the lair heuristic.
- 2014 costs appear only in names ("(Costs 2 Actions)" ×35, "(Costs 3 Actions)" ×6).
- 2024 has a once-per-turn restriction in prose (×44).

**Other quirks**
- **Recharge:** use `usage` only. The API strips "(Recharge 5–6)" from names, and `usage` has no misses in either edition.
- **Spellcasting:** 2014 puts it in `special_abilities`; 2024 puts it in `actions`. In 2024 the per-spell `level` is the **cast level**, not the spell's base level.
- **Resistances:** 2014 uses free-text qualifiers ("…from nonmagical attacks"). Parse them into conditional resistances rather than splitting on commas; your SRD JSON's comma split made werewolves immune to bludgeoning and piercing unconditionally.
- **Initiative:** 2024 monster JSON has no initiative field. Compute it from DEX, with an override.

**Spells for the simulator**
- 2014 is structured: `damage_at_slot_level`, `damage_at_character_level`, `dc`, `area_of_effect`.
- 2024 is thin: **0 of 339 spells have `dc` or `area_of_effect`**, `damage` is a single object, and there are no upcast tables.
- Build `overrides/spells.2024.json` by parsing `description` and `higher_level`: save ability, "half as much", dice, "increases by XdY for each slot level above N", and the Cantrip Upgrade at levels 5, 11 and 17. **Cantrip scaling is at the end of `description`, never in `higher_level`** (0 of 27 cantrips have `higher_level`). Hand-verify the top combat spells.

### 2. Dice

**Grammar**
```
expr   := term (('+'|'-') term)* [cmp INT]            ; trailing cmp ⇒ probability query ("8d6>=30")
term   := factor (('*'|'/') INT)*                     ; '/' floors
factor := dice | INT | '(' expr ')' | 'adv' | 'dis' | 'ea'   ; 2d20kh1 / 2d20kl1 / 3d20kh1
dice   := [INT] 'd' (INT|'%') mod* ['[' label ']']
mod    := (kh|kl|k|dh|dl)[INT] | r[cmp]INT | ro[cmp]INT | ![cmp INT] | !![cmp INT] | !p[cmp INT]
        | min INT | max INT | cs cmp INT | cf cmp INT
```

**Evaluation**
- Order per die: roll → reroll (`r` repeats until no match; `ro` rerolls once) → explode → clamp. Then, on the pool: keep/drop → success count, otherwise sum.
- Guards: N and M ≤ 1000, explosion depth ≤ 100, and a reroll that matches every face is rejected.
- Keep the **sign on dice terms**. `dndMath.ts` drops it (`1d8-1d4` is read as +1d4).

**Rolling**
- `RandomNumberGenerator.GetInt32(1, sides + 1)`: static, thread-safe, unbiased. Every face is logged.
- `seed` switches to xoshiro and labels the result "pseudo-random, reproducible".
- No external TRNG service: the OS CSPRNG already gives true-random-quality dice without quotas or network.

**Exact distributions** (sparse `double` PMFs, with an optional exact `BigInteger` fraction path for small spaces)
- Sums use convolution, with repeated squaring for large N.
- **Keep-highest / keep-lowest:** a DP over face values from high to low with state (dice assigned, kept, sum). It also works for remapped PMFs.
- Explode with a depth cap reports the truncated probability mass.
- Plain `!` combined with keep has a random pool size, so it falls back to a Monte Carlo estimate with a CI.

### 3. Encounter difficulty

**Tables** (hard-coded, each with a source comment)
- 2014: XP thresholds (Easy/Medium/Hard/Deadly, levels 1–20), group multipliers with the party-size shift (fewer than 3 PCs and 6+ PCs move one column), and Adventuring Day XP.
- 2024: XP Budget per Character (Low/Moderate/High) — **no multipliers**.
- CR→XP for CR 0–30, identical in both editions.
- Every value was cross-checked against at least two sources.
- **Do not use the `dndR` R package as a source.** Its 2014 thresholds are wrong at 17 of 20 levels (L3 Deadly is 400, not 300), and its 15+ monster multiplier is wrong.

**2014 algorithm**
1. Sum each character's thresholds.
2. Multiply the monsters' total XP by the group multiplier.
3. Exclude monsters with a "significantly lower" CR. The DMG gives no number, so this is a per-monster `exclude` flag, default off.
4. Label = the highest threshold reached; below Easy is labelled "trivial" (not an official term).

**2024 algorithm**
- The budget is the sum of each character's value.
- ~~The label is the highest band whose budget ≤ total XP.~~ **Superseded in Phase 3:** the label is the lowest band whose budget the total fits (≥ total), because the SRD's own worked examples classify that way; see Implementation status → Phase 3.
- Both rules are **interpretations** (mixed-level parties, classifying an existing encounter), so flag them in the output.
- Emit the SRD 5.2 troubleshooting warnings: more than 2 creatures per PC, a creature CR above the party level, CR 0 overuse, more than 2–3 stat blocks.

**Effective level**
- `effective_level_offset` comes from campaign settings (One Piece: about +1).
- Report both the book label and the effective-level label.
- When comparing across editions, note that "Low = old Medium" holds only at L1–7.

### 4. DPR engine and feature DSL

**d20 model**
- A **face PMF** per (mode, Lucky, Elven Accuracy), with at most 20³ = 8,000 cases, cached. Hit and crit are sums over faces.
- Hit faces are `{f ≥ need, f ≥ 2} ∪ {f ≥ critMin}`. **Crit faces always hit.** This is the crit floor `dndMath.ts` lacks.
- Bless and Bane are exact: `Σ_b ¼·T(p_{bonus±b})`. Crit chance is unchanged by either.

**Damage model**
- Integer PMFs, not just means. Crits double every damage die, including riders; flat modifiers are not doubled.
- Resistance is applied per damage type, after other adjustments, and floored.
- Half on a successful save is computed from the PMF. Don't use the `x/2 − 0.25` shortcut: it breaks for flat-only damage and for remapped dice like GWF or Elemental Adept.

**Per-turn dynamic program**
- State per attack: rider unused, Vex advantage pending, target prone, Bonus Action available. The result is the **exact per-turn damage distribution**.
- Policies:
  - rider on the first hit;
  - **hold for crit** (backward induction);
  - smite with a slot price λ, reporting damage per slot.
- 2014 Divine Smite can go on multiple hits per turn; 2024 Divine Smite is a Bonus Action spell, so once per turn.

**Power attack**
- 2014 GWM/SS −5/+10 is on iff `P'/P > D/(D+10)`, decided automatically per AC.
- 2024 GWM adds +PB on hits "as part of the Attack action", plus **Hew**.
- 2024 Sharpshooter has no −5/+10. Its value comes from scenarios: cover, firing in melee, long range.

**2024 specifics**
- GWF treats 1–2 as 3; 2014 GWF rerolls 1s and 2s once. Both have exact per-die expected values.
- Savage Attacker is `E[max(X1, X2)]` over the weapon dice.
- Masteries:
  - **Graze:** `(1−P)·mod` on a miss.
  - **Vex:** Markov steady state `x* = P_N / (1 − P_A + P_N)`.
  - **Topple:** P(prone) feeds advantage to later attacks.
  - **Sap:** defensive value.
  - **Cleave:** needs an engagement-density input.
  - **Nick:** action economy only.
  - **Push, Slow:** no DPR in v1.

**Saves and AoE**
- `F = clamp((DC − bonus − 1)/20, 0, 1)`.
- Magic Resistance: F². Automatic Str/Dex save failure while Paralyzed, Stunned or Unconscious. Evasion.
- Legendary Resistance: expected casts to land an effect = `(L + 1)/F`.
- **AoE damage is rolled once for all targets**, so kills are correlated.
- Always report both raw and effective (HP-capped) damage, plus the kill-count distribution.
- DMG target counts per shape: cone size÷10, cube size÷5, sphere radius÷5, line length÷30.

**Horizons** (every result echoes the assumption it used)
- **Round-1 nova.**
- **R-round fight**, default R = 3.
- **Day average**, with presets:
  - "2014 DMG (6–8 encounters, 2 short rests)";
  - "light day (3–4, 1 SR)", **labelled unofficial**, because 2024 removed the adventuring-day guidance.

**Targets**
- Level L uses the AC and attack/DC of the CR = L row in the DMG table. Target save bonus is an explicit input; the default is the tomedunn baseline.
- An optional "2024 MM empirical" profile can be computed in Phase 5 from the 341 vendored 2024 monsters. That also settles the AC-trend disagreement (Open question 6).

**The delta method**
- ΔDPR = DPR(build + feature) − DPR(build), under identical assumptions.
- **LE ≈ ΔDPR ÷ tier-averaged per-level slope** of the baseline curve, banded on your scale:

  | Band | LE |
  |---|---|
  | Under | ≤ −0.25 |
  | On budget | ±0.2 |
  | Creeping | +0.25 to +0.5 |
  | Over | +0.5 to +1.0 |
  | Breaking | ≥ +1.0 |

- Report opportunity cost when a feature consumes the Bonus Action or Reaction.
- Presets: the RPGBOT target (top of CR-L HP ÷ 12) and the Warlock EB + Agonizing + Hex baseline.

**Ruling flags** (defaults in parentheses; results echo the ones used)
- Hew gets +PB (no)
- The Cleave attack is part of the Attack action (no)
- GWF applies to rider dice (no)
- Savage Attacker works on doubled crit dice (no)
- 2024 mixed-level budget (sum per character)
- 2024 classification bands (lowest band whose budget ≥ total; Phase 3 replaced "highest band ≤ total")
- 2014 "significantly lower CR" exclusion (off)
- Initiative ties (higher modifier, then seeded roll-off; "PCs win ties" optional)

**Feature DSL** (D5). The same shape is stored as `character_sheet.sim_profile`:
```json
{ "name": "Ember Edge (homebrew)", "edition": "2024", "level": 5,
  "abilities": {"str": 18}, "fighting_style": "gwf",
  "attacks": [{ "name": "Greatsword", "count": 2, "to_hit": {"ability": "str", "proficient": true},
                "damage": "2d6", "ability_to_damage": true, "properties": ["heavy","two-handed"], "mastery": "graze" }],
  "modifiers": [
    { "kind": "extra_damage", "dice": "1d6", "type": "fire", "when": "first_hit_per_turn", "crit_doubles": true,
      "resource": { "uses": 3, "per": "long_rest" } } ] }
```
v1 modifier kinds:
- `to_hit`
- `extra_damage`, with `when`: every_hit | first_hit_per_turn | on_crit | on_miss
- `bonus_damage`
- `crit_range`
- `advantage` (a rate, or a source)
- `damage_die_remap`: gwf2014 | gwf2024 | elemental_adept
- `reroll_damage_take_best`
- `extra_attack`: action | bonus_action | reaction with a trigger probability
- `power_attack`
- `save_effect`: dc, ability, dice, on_success, targets or shape+size
- `condition_on_hit`
- defensive, sim-only: `ac`, `resistance`, `temp_hp`
- `resource`, `concentration`, `action_cost`

An unknown kind raises an `McpException` that lists the valid kinds.

**Porting `dndMath.ts`: fix its five bugs**
1. The missing crit floor. Example: +5 vs AC 30, crit on 19, 1d8+3 gives 0.825 in the TS; the correct value is **1.2**.
2. Lucky with disadvantage: it computes 1.0975·p², but the correct value is 1.1·p².
3. Bless is never wired into DPR.
4. The `x/2 − 0.25` half-damage shortcut, and the missing resistance handling.
5. The dice-parser sign bug.

### 5. Monte Carlo simulator

**Engine**
- Round-based. Initiative is a group roll for identical creatures. Surprise follows each edition: 2014 skips the first turn; 2024 gives Disadvantage on initiative.
- **Start of turn:** recharge roll (`(7−X)/6`), Legendary Action reset, death save, ongoing damage, "until start of turn" expiry.
- **Turn:** the policy picks an action, a Bonus Action and movement.
- **End of turn:** save-ends conditions roll.
- **Legendary actions:** each legendary creature may act after every other creature's turn.
- **No grid.** Melee combatants belong to **engagement groups**; joining another group costs the turn's movement, with an optional opportunity-attack probability. AoE hits k creatures of a group.

**Rules**
- Monsters die at 0 HP; a flag lets named NPCs make death saves instead.
- PCs roll death saves: a natural 1 counts as 2 failures, a natural 20 restores 1 HP. Damage at 0 HP adds failures. Massive damage kills instantly.
- Temp HP doesn't stack.
- Concentration takes one save per damage source, DC `max(10, ⌊dmg/2⌋)`, capped at 30 in 2024 only.
- Conditions are data-driven flags with durations: N rounds, start/end of the source's turn, save-ends, or concentration-linked.
- Exhaustion follows each edition.

**Policies**
- focus-fire (lowest HP / highest kill chance)
- spread
- threat (highest DPR)
- break concentration
- healer-first
- AoE-maximize
- Legendary Resistance spent by severity threshold

**Extensibility (the "+ extensible" in Q4)**
- An `ICombatHook` pipeline: `BeforeAttackRoll`, `OnHit`, `OnMiss`, `OnDamageTaken`, `OnSaveFailed`, `OnTurnStart`, `OnTurnEnd`.
- Built-in features and DSL modifiers compile to hooks.
- A new mechanic means a new hook class plus a new DSL kind; the engine doesn't change.

**RNG and determinism**
- Use a `struct Xoshiro256StarStar` (public-domain Blackman/Vigna) with Lemire bounded integers.
- **Seed per iteration:** `SplitMix64(master ⊕ i·φ)`.
- `Parallel.For` over fixed 1,024-iteration chunks, with thread-local **integer** accumulators (Σx and Σx² in `long`).
- **Results are identical for any thread count.** `replay: i` re-runs one iteration with a full combat log.
- Always echo the master seed.
- `balance_compare` in sim mode uses **common random numbers**, i.e. the same seeds for both builds, and reports the paired Δ with a CI.

**Statistics**
- Wilson intervals for proportions; CLT for means.
- 10,000 iterations by default gives a ±1% half-width at 95% (n = 9,604 at the worst case p = 0.5). Optionally run in batches until a requested half-width is reached.
- Report progress, which resets Claude Code's idle timeout, and honour the `CancellationToken`.

### 6. Campaign tracking

The schema is taken from the campaign research report (`research/04 §4`, full DDL drafted there).

**Principles**
1. **One polymorphic `entity` table** for everything that is graphed, tagged, searched or known about. The kinds are:

   | Group | Kinds |
   |---|---|
   | World | character, location, faction, item, lore, rule, homebrew |
   | Plot | quest, thread, question, secret |
   | Play structure | arc, beat, scene, session, event, handout |
   | Other | work (songs), clock, front, note |

   Typed side tables exist only where there are real mechanics or queries: `character_sheet`, `session`, `objective`, `clock`, `timeline_event`, `encounter`/`combatant`, `holding`, `currency_txn`, `award`.
2. **Facts are the unit of continuity.**
   - A `fact` is an atomic claim with `fact_type` (canon / ruling / secret / rumor / belief / clue / theory), `truth`, `canon_status`, `confidence`, a `gate` (reveal preconditions and forbidden terms), and `superseded_by`.
   - `fact_dependency` returns the downstream facts to re-check when one is superseded.
3. **`knowledge` rows** record who knows a fact:
   - The knower is a character, the party, the table, the author (you), the DM, or the public.
   - Each row has a `state`: knows / suspects / believes / misbelieves / heard / met / aware / unrecognized / unaware.
   - Each row also records `known_as` ("the old king"), `learned_session_id`, `via_entity_id` and `how`.
   - **Verdict precedence:**
     1. An explicit character row wins.
     2. Otherwise a party row counts if the character was present (from `session_attendance`).
     3. A party row where the character was absent gives "uncertain: absent in S-n".
     4. A public knower means known.
     5. Anything else means not known.
4. **Visibility.**
   - Rows have a `visibility` of public / party / restricted / author, and the strictest endpoint wins (Kanka-style chaining).
   - Every read takes a `perspective`.
   - **FTS column filters exclude `secret` for non-author perspectives**, so secrets never leak through search. **Build the query with `Fts5Query.ColumnFiltered(userText, columns)`** (player perspectives) or `Fts5Query.Terms(userText)` (author), both in `DndMcp.Repository/Sqlite`. The research/04 form `'{name aliases summary body tags} : ' || :q` **leaks**: FTS5 binds a column filter only to the next phrase, so "keras hoard" matches `hoard` in `secret`, and parentheses are escaped by an unbalanced `)`. Search semantics are therefore: words ANDed, trailing `*` for prefix, no user-facing OR/NOT/NEAR (confirm in Phase 6). Pinned by `PerspectiveSafeSearchTests`.
   - **Alias visibility.** The research `entity_fts_au` trigger puts ALL aliases into the player-visible `aliases` column, so a party search for an author-only alias ("the Axiom Cage") finds the entity. Fix: add a `hidden_aliases` FTS column (last, so bm25 weights become `10, 8, 4, 1, 1, 2, 8`). Only public/party aliases go into `aliases`; restricted/author aliases go into `hidden_aliases`, which player filters leave out. Also add `entity_alias_au AFTER UPDATE OF alias, visibility`, which research lacks. Pinned by `AliasVisibilitySearchTests`. Still open: matching a restricted alias for the specific characters who know it needs per-knower resolution after the join.
   - `cross_link` records the same being across campaigns (Keras). It is **never rendered for non-author perspectives**, which is the Belmakor skill's cross-campaign firewall.
5. **Canon status and confidence are first-class.**
   - `canon_status`: canon / played / ruled / planned / proposed / accepted / struck / superseded.
   - `confidence`: confirmed / approximate / reconstructed / unverified.
   - `proposed` auto-numbers F-codes, which gives your **Inventions (accept / strike)** register for free.
6. **Handles.** `kind:slug` for tools, your register codes (Q22, F36, C13), and `f:<n>` for facts. UUIDv7 stays internal.
7. **An append-only `change_log`** written by the repository layer, recording actor, tool, `batch_id` (one tool call = one undo unit), `session_id`, field path, old and new values, and reason.
   - Triggers make it immutable.
   - This is what makes tracking *accurate*: `campaign_history since` is the where-to-stand "sync from play" feed, and `as_of_session` replays history backwards.
8. Every FTS-indexed table has an **explicit `INTEGER PRIMARY KEY`**; `VACUUM` may renumber implicit rowids and silently break FTS links.
9. **The append-only triggers are not enough on their own.** `INSERT OR REPLACE` with an existing `seq` overwrites a `change_log` row even with both BEFORE UPDATE / BEFORE DELETE triggers, because REPLACE's implicit delete fires triggers only when `recursive_triggers` is on (off by default). Add `CREATE TRIGGER change_log_no_replace BEFORE INSERT ON change_log WHEN EXISTS (SELECT 1 FROM change_log WHERE seq = NEW.seq) BEGIN SELECT RAISE(ABORT,'change_log is append-only'); END;`. RAISE(ABORT) undoes only the failing statement, so the batch writer must roll back on any exception. Pinned by `AppendOnlyTriggerTests`.
10. **JSON columns that must hold an object** need `CHECK (json_valid(x) AND json_type(x) = 'object')`; `json_valid` alone accepts arrays, numbers, strings and null. Expression indexes are used only when the query repeats the exact indexed expression (`json_extract(data,'$.attitude')`, not `data->>'$.attitude'`).
11. **Dapper:** set `DefaultTypeMap.MatchNamesWithUnderscores = true` once at startup, before any query. Otherwise snake_case columns leave class properties silently null. Positional records need exact CLR types (INTEGER is `long`) and columns in constructor order, so never `SELECT *` into a record.

**Operations**
- Migrations are embedded `NNNN_name.sql` files checked via `PRAGMA user_version`. Each one runs as `VACUUM INTO backups/pre-migrate-vN-<utc timestamp>.db`, then `BEGIN IMMEDIATE`, apply, commit. The timestamp matters: `VACUUM INTO` refuses an existing file, so a fixed name makes every retry of a failed migration fail until someone deletes the backup by hand. `PRAGMA foreign_keys` is a silent no-op inside a transaction, so table rebuilds must turn it off before `BEGIN`.
- **Backups** use `VACUUM INTO` (a raw file copy can miss committed WAL data). They run on the first write of the day, before migrations and imports, and at session end. Keep the last 10, dailies for 30 days, and every pre-migration copy.
- **Restore is a CLI subcommand, never a tool**, because other processes may hold the DB open.

### 7. Live combat tracker

- State lives in `encounter`, `combatant` and `combat_log`, with each log row linked to the `dice_roll` that caused it.
- `add{srd: "bandit captain"}` resolves against the campaign's ruleset and **snapshots the stat block**, so prepped fights stay stable if the content is re-vendored. HP can be `avg`, `roll` or a number.
- Every step returns the initiative table plus server-computed reminders:
  - concentration DC due;
  - durations expiring at start or end of a turn;
  - save-ends prompts;
  - legendary action resets;
  - death saves;
  - lair actions (2014 acts on initiative count 20).
- **HP ticks stay out of `change_log`** (the PWA's lesson); the combat log is the audit trail. On `end`, one summarized batch writes PC HP, slots and conditions back to `character_sheet`, proposes status changes for defeated named NPCs, and records XP and loot.
- `balance_simulate {encounter}` simulates a fight before you run it, from the party's `sim_profile`s plus the enemy snapshots. `from_state` simulates the rest of an in-progress fight. Each run stores a `sim_run` and can append a "Tuning" line to the scene's prep, matching your run-sheet format.

### 8. Markdown export/import and PWA import

**Exports** go to `~/.local/share/dnd-mcp/exports/<campaign>/`:
- **`vault/`**: Obsidian-friendly and round-trippable.
  - One file per entity, with YAML frontmatter, `[[wikilinks]]`, a `## Facts` list, and author-only text in `> [!secret]` callouts.
  - Also `_campaign.md`, `_knowledge.md` and `_manifest.json`.
- **`player_safe/`**: rendered with `perspective=party`, so it can be shared with the table.
- **`skill/references/*.md`**: generated through per-campaign **export profiles**.
  - Only regions between `<!-- dnd-mcp:begin id=… -->` and `<!-- dnd-mcp:end -->` are regenerated; hand-written guidance (tier-check callouts, ordering cautions) is untouched.
  - Output goes to a writable directory, because the installed skill path is read-only.
  - A first-run "adopt" mode proposes where the markers go.

**Markdown import** uses YamlDotNet and Markdig.
- Matching is by `id`, then by `slug`.
- It shows a dry-run diff and applies everything as one batch (`actor=import:markdown`).
- It never deletes anything without `prune`.

**PWA import** reads `DmCampaignBundle` inside the `TradeEnvelope`.
- Accept `format: solid-characters/trade`, `version: 1`; refuse `dmSchemaVersion > 5`.
- Port the `sanitizeBundle` rules.
- Record PWA ids in `external_ref`, so a re-import **updates rather than duplicates**.
- Audio is not imported, only counted in the import report.
- Exporting back to the PWA is deferred.

---

## Phasing

Each phase ends with working MCP tools and passing tests. **Phase 6 depends only on Phase 0** and can run in parallel with Phases 1–5.

| # | Phase | Depends on | Heart of it | Exit criteria |
|---|---|---|---|---|
| 0 | Scaffold and spikes | — | Adapt `dotnet new mcpserver` (bundled template 10.0.12; bump to 2.2.0), four projects plus the integration project, `ToolGuard`, JSON options, stderr logging, the pipe-based test harness. Publish, `claude mcp add`, and see it in `/mcp`. **Spike A:** vendor v7.0.0 and deserialize *every* file for both editions with zero exceptions. **Spike B:** log `sqlite_version()`, then create an FTS5 table and run `json_patch`. | A stub `dice_roll` answers inside Claude Code. Both spikes are green. The stdout-purity test passes. |
| 1 | Dice | 0 | Parser, CryptoRoller, exact distributions, keep-highest DP | `dice_roll` and `dice_odds` pass the golden values below. |
| 2 | Rules lookup | 0 | Importer → `srd.db`, FTS5, aliases, formatters, 2024 rules glossary, background bootstrap, attribution resource | `rules_search` and `rules_get` (both editions and `both`) work. Import counts match. |
| 3 | Encounter difficulty | 0 | Tables, both algorithms, effective level | Table parity tests pass, and so do the worked examples. |
| 4 | DPR engine | 1 | d20 face PMF, damage PMFs, per-turn DP, policies, DSL + validators, horizons, deltas, LE bands, presets (port `dndMath.ts` with its 5 fixes) | `balance_dpr` and `balance_compare` reproduce every worked example. |
| 5 | Normalization + simulator | 2, 4 | MonsterNormalizer + overrides, 2024 spell overlay, engine, hooks, policies, xoshiro, parallel stats, replay; compute the "2024 MM empirical" profile from the data | `balance_simulate` matches the closed form within CI. The death-save absorption test passes. Every monster normalizes or warns. |
| 6 | Campaign core | 0 | Migrations, entity/fact/knowledge/change_log, FTS, perspective filter, sessions, backups; tools `campaign`, `_search`, `_get`, `_write`, `_knowledge`, `_session`, `_history`; recap/prep/knowledge/continuity prompts | Belmakor's "Axiom Cage vs the old king" and the One Piece reveal-gate scenarios pass as tests. Undo works. |
| 7 | Characters + combat | 1, 2, 6 (5 for sim links) | `character_sheet`, `campaign_character`, `combat` with reminders and write-back, `sim_profile`, encounter→sim and `from_state` | A full scripted combat round-trips to the sheet correctly. |
| 8 | Transfer | 6 | Vault, player_safe, skill export profiles with markers + adopt mode, markdown import, PWA import, `bootstrap_from_skill` | Export → import is idempotent (no diff on the second run). A player_safe export contains no author-only text. A PWA re-import updates rather than duplicates. |

**Scope guard for v1.** No grid, positioning or movement geometry. No scripting of arbitrary spells beyond what the DSL and the 2024 overlay express. No HTTP transport. No PWA export. Anything outside that goes on a list rather than into a phase.

---

## Verification

```bash
dotnet build DndMcp.sln
dotnet test DndMcp.sln                                      # unit + integration
dotnet publish DndMcp -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ~/.local/share/dnd-mcp/bin
claude mcp add --transport stdio --scope user dnd -- ~/.local/share/dnd-mcp/bin/DndMcp
claude --debug=mcp                                          # server stderr → ~/.claude/debug/<session>.txt
npx @modelcontextprotocol/inspector ~/.local/share/dnd-mcp/bin/DndMcp   # optional manual poking
```

For development, run `dotnet build` and then register `dotnet run --project DndMcp --no-build --no-launch-profile` at `--scope local`. `dotnet run` must never build inside the session, because build output on stdout corrupts the protocol (csharp-sdk #791).

### Tests that carry real weight (golden values computed with exact arithmetic during research)

**Dice**
- `4d6kh3` mean = **15869/1296** (≈ 12.2446); P(18) = 7/432.
- `2d20kh1` mean = 13.825; `3d20kh1` mean = 15.4875.
- **P(8d6 ≥ 30) = 638543/1679616** (≈ 0.38017).
- Exploding d6 mean 4.2 (depth cap 1 → 4.0833).
- Every PMF sums to 1 within 1e-12.
- Roller statistics: a chi-square test on 10⁶ d20 rolls is marked `[Trait("Category","Slow")]`.

**d20 math**
- At p = 0.65: advantage 0.8775; Elven Accuracy 0.957125; Lucky 0.6825; Lucky + advantage 0.898625; Lucky + disadvantage 0.46475.
- Bless at +7 vs AC 15: normal 0.775, advantage 0.94625, disadvantage 0.60375.
- The crit-floor regression case (+5 vs AC 30, crit on 19, 1d8+3) = **1.2**.

**DPR**
- 2014 L5 Fighter (+7, greatsword, GWF 2014, GWM, 2 attacks) vs AC 15 = **19.61**. Without GWM (GWF only) = 16.867. The power-attack toggle flips at **AC 17**.
- 2024 L5 Fighter (GWF 2024, Graze, GWM + Hew) vs AC 15 = **24.04**, or 24.23 with the Hew-gets-PB flag. Savage Attacker adds +0.825.
- The full AC 13–19 comparison table from `research/03 §A11`.
- GWF expected-value table; Savage Attacker table; Vex steady state 0.841 at P = 0.65.
- Smite policies: 8.505 / 6.885 / 1.755 / 12.6 damage per round.

**AoE and saves**
- Fireball (DC 15, 8d6) vs 4 goblins (2014): raw **89.2**, effective **27.9986**, P(all 4 die) = **0.99933**.
- Rolling separately per target (the wrong model) gives 0.99877. A test pins that the shared-roll model is the one used.
- Legendary Resistance: F = 0.6 with 3 uses → 6.67 expected casts.

**Simulator**
- One attacker vs an infinite-HP dummy matches closed-form DPR within the 95% CI.
- Unassisted death saves: P(death) = **0.404875**, P(stable) = 0.41375, P(natural-20 revive) = 0.181375, within CI.
- The same seed gives byte-identical results at 1, 4 and 16 threads.

**Encounters**
- 2024 budget: parity with your SRD 5.2 markdown (`09_GameplayToolbox.md` ~lines 589–632), read as text.
- 2014: spot checks including **L3 Deadly = 400** (the value dndR gets wrong) and the 15+ multiplier (×5 / ×4 / ×3).
- CR→XP includes CR 9–13 and CR 26–30.

**SRD import**
- Counts match the manifest. Examples: monsters 334 / 341, spells 319 / 339, feats 1 / 17, 2014 rules 137.
- The 5 known mislabeled 2014 half-saves are corrected by overrides, and a test pins each one.
- **Oracle test:** for monsters in both datasets, normalized attack bonuses and save DCs equal your SRD JSON's derived values. Those matched the prose 100%.

**MCP layer (integration, in-memory client on protocol `2025-11-25`)**
- Exactly 18 tools.
- Every name matches `^[a-zA-Z0-9_-]{1,64}$`, every description is ≤ 2,048 characters, and **every tool has explicit annotations**.
- Each tool has one happy-path call and one bad-input call. The bad-input call asserts `IsError` and that the message starts with `An error occurred invoking '<tool>': ` followed by an actionable hint.
- **stdout purity:** launch the built host as a real process (`dotnet DndMcp.dll` by default; set `DND_MCP_TEST_HOST_EXE` to the published binary for the Phase 0 exit check), send `initialize` and `tools/list`, and assert that every stdout line parses as JSON-RPC.

**Campaign**
- Migrations run against fixture DBs.
- `change_log` rejects UPDATE and DELETE.
- The knowledge verdict precedence is a table-driven `[Theory]`.
- `campaign_search` with `perspective=party` never matches `secret_md` text.
- Superseding a fact returns its dependents.
- Undo reverses exactly one batch.
- Export → import → export is byte-identical.

### Manual end-to-end in Claude Code

Try each of these:
- "Roll 4d6 drop lowest six times"
- "Compare grappled in 2014 and 2024"
- "How much DPR does GWM add to a level 5 fighter in 2024, and what's that in level-equivalents?"
- "Is 3 ogres Deadly for four level-5s in 2014, and what is it in 2024?"
- "Simulate that fight 10,000 times"
- "Create my Belmakor campaign as a player, 2014 rules", then "Does Belmakor know about the Axiom Cage?"

---

## Open risks

1. **2024 data is days old.** Mitigation: pin the tag, re-vendor with a script, and pin import counts in tests so a bump is a deliberate, reviewed diff.
2. **Monster-normalization accuracy drives simulator credibility.** Mitigation: overrides, warnings surfaced in `format: combatant` and in simulation output, and the oracle test against your SRD JSON.
3. **Simulator scope creep.** Mitigation: the hook architecture plus the v1 scope guard. New mechanics arrive as DSL kinds, not engine rewrites.
4. **Tool-selection accuracy with `action`-discriminated tools.** Mitigation: per-action argument lists and examples in descriptions, actionable validation errors, and a short prompt-eval list run manually each phase.
5. **Result size.** Mitigation: concise defaults, pagination, and resources for long bodies. Integration tests assert typical outputs stay under ~8k tokens (approximated as characters ÷ 4).
6. **Multi-process writes to `campaigns.db`.** Mitigation: WAL, `PRAGMA busy_timeout` per connection, short `BEGIN IMMEDIATE` transactions, and restore only through the CLI.
7. **Licensing.** The 2014 encounter tables and the DMG CR table are not CC-BY. Fine for a personal local server; review before any publishing (see Open question 1).
8. **Your Claude Code is 2.1.198.** Long-call backgrounding needs ≥ 2.1.212, and the description-length setting needs ≥ 2.1.280. Run `claude update` before Phase 0.

## Open questions (with leans)

1. **Should the 2014 DMG / Basic Rules encounter tables and the DMG Monster-Stats-by-CR table be included?** *Lean: yes, with source comments.* It's a personal local server, and the tables are needed for your 2014 Belmakor campaign. Revisit before publishing.
2. **One global campaigns DB, or one per project via `CLAUDE_PROJECT_DIR`?** *Lean: one global DB.* Campaigns aren't tied to code repos. `DND_MCP_DB` stays as an override.
3. **Should the Belmakor and One Piece campaigns be bootstrapped from their skills in Phases 6–8?** *Lean: yes*, through `bootstrap_from_skill` with dry-run first, keeping each file's confidence caveats.
4. **Should the skill reference files be generated from the DB?** *Lean: generate marked regions only.* SKILL.md files and hand-written guidance are never generated.
5. **Should the server export back to the PWA?** *Lean: defer* until you actually run sessions from its Run screen.
6. **Do 2024 monsters have lower AC than 2014 (your homebrew-balance skill) or +1 AC (Blog of Holding medians)?** *Settled in Phase 5 by the data:* the 2024 SRD monsters' median AC is 0.42 higher than 2014's and the DMG table's on average over CR 1–20; the 2014 monsters match the DMG table exactly on average. `target.profile` defaults to `dmg2014`; `mm2014` and `mm2024` measure against the data.
7. **Should the level-equivalent bands or the slope be recalibrated?** Even net of the ASI it replaces (`balance_compare`'s ASI yardstick), the official 2024 GWM reads LE 1.1–1.6, inside the Breaking band (≥ +1.0), so an official feat is called Breaking (Phase 4 review). *Lean: measure before changing either.* Run the other official 2024 damage feats that replace an ASI (Sharpshooter, Polearm Master) net of the ASI the same way. If they also read Over or Breaking, the slope (the baseline's tier-averaged per-level DPR gain) is too shallow for feats and should change; the bands are your homebrew-balance scale and stay. If only GWM does, keep both and let the yardstick line say that the official GWM itself reads Breaking.

## Side findings in your other projects (out of scope, recorded so they aren't lost)

**`dndMath.ts`: five bugs**
1. The missing crit floor.
2. Lucky with disadvantage.
3. Bless is never wired into DPR.
4. The half-damage shortcut.
5. The dice-parser sign.

**homebrew-balance skill**
- "Advantage ≈ +3.5 on the d20 ≈ +20–25%" understates the gain at the canonical 65% hit rate. There it is **+4.55 on the d20 and about +38% damage**; +20–25% only holds at 75–80% base accuracy.
- It says 2024 monsters have lower AC; the SRD data says 0.42 higher on average (Phase 5, `MonsterStatsEmpirical`).

**belmakor-campaign skill**
- `references/party-and-band.md` line 20 says level 11, while `SKILL.md` and `belmakor-build.md` say level 12.

**serving-solid-characters SRD JSON**
- 22 wrong 2014 save types: every dragon Wing Attack is marked "half".
- `legendary_action_count` is hard-coded to 3 (`scripts/srd-gen/parsers/2024/monsters.ts` ~line 118), and legendary costs are stripped.
- Resistance qualifiers are mangled by a comma split: werewolves come out unconditionally immune to bludgeoning and piercing.

**Local SRD 5.1 markdown**
- The CR→XP table skips CR 9–13 and CR 26–30.

---

*Design basis.*
- The completed user-conventions survey, plus four follow-up research agents that finished the dnd5eapi, C# MCP SDK (v2.2.0 source), balance-math and campaign-tracking dimensions of a stopped 5-agent workflow.
- Live API and burst tests, a monster-data audit across both editions, table values cross-checked against ≥ 2 sources, and golden values computed with exact rational arithmetic.
- Full reports, with every table, formula, example payload and the draft DDL, are in `~/.claude/plans/dnd-mcp-research/`:
  - `00-user-conventions.md`
  - `01-dnd5eapi.md`
  - `02-csharp-mcp-sdk.md`
  - `03-balance-math.md`
  - `04-campaign-tracking.md`

---

## Implementation status (updated 2026-10-02)

**Phase 6 — done.** Both exit criteria are met: Belmakor's "Axiom Cage vs the old king" and the One Piece reveal-gate scenarios pass as tests, through the repository and through the MCP tools, and undo works (it reverses exactly one batch, refuses a conflicting one, and can itself be undone). PLAN's campaign test list passes too: migrations run against fixture databases; change_log rejects UPDATE, DELETE and a REPLACE onto an existing row; the knowledge-verdict precedence is a table-driven `[Theory]`; a player-perspective search never matches secret text (a property test over every non-author perspective); superseding a fact returns its dependents with their depth. The phase is uncommitted on top of 18e1e30 "phase 5.1" (238 new files, about 72,000 lines with the tests; 37 changed files besides this PLAN.md, +3,361/−178).
- **The Belmakor scenario** (`DndMcp.Tests/CampaignScenarios/Belmakor*`, `DndMcp.IntegrationTests/Campaign/ScenarioBelmakor*`): all 46 expected results of the scenario report, by row. "The old king" (Keras) and "the thing he wants" (the Axiom Cage) are two entities; the party and Belmakor know them by those names, and Keras, Axiom, Cage, Baal, Third Silence and the other campaign's slug appear in no output for any non-author view (dm, table, party, public, Belmakor, absent Serif, Aiden with no attendance row), through search, get, the summary, the knowledge resource and the ledger. "Old King, Come Down" passes the knowledge check; "we'll haul the Axiom Cage back" is flagged as a name Belmakor does not use and as revealing it to the audience, and since the review "haul your Cage back" is flagged too (a distinctive word of a name he does not use); his ambition sung in a lyric is listed as a secret at risk; "a nine-hundred-year-old sorcerer" trips the no-timespan reveal rule.
- **The One Piece scenario** (`…/OnePiece*`, `ScenarioOnePiece*`): all 49 expected results. "seal" (and seals, Sealed, SEALING; not sealskin) is flagged in NPC dialogue until the axe is assembled, and not after; revealing the secret early writes the knowledge and warns which `after`, `with` and route conditions are unmet (warn and apply, never refuse), and since the review also that the seal's statement now reaches the party with "seal" in it while the vocabulary rule holds (give the party's phrasing as `known_as`); the secret's status moves hidden → seeded → partial → revealed as the routes land, and replays that way as of each session; superseding the 2026-08-30 timeline returns the ~500–900-year and ~400-year figures (depth 1) and the lineage claim (depth 2, via the first) with the questions and NPCs to recheck; undoing the T5 reveal puts the party back to not knowing and the status back to partial.
- **Tests: 19,782 passing** (14,397 unit + 5,385 integration, none skipped: the sibling-repo tests ran), 0 build warnings (clean `--no-incremental` build); the integration suite was run twice. After stage 3b the suite was 13,862 + 5,032; the review fixes added 888.
- **How it was built.** One contract (`~/.claude/plans/dnd-mcp-research/phase6/phase6-contract.md`) from six research reports (the Belmakor and One Piece scenario fixtures with numbered expected results, the host, SQLite, SDK and change-site surveys), then staged agents, each in its own copy with a disjoint set of files it owns and a patch for anything outside them: stage 0 (the schema and the Domain vocabularies, by hand); stage 1, Domain logic ∥ Repository infrastructure; stage 2, the write path ∥ the read path; stage 3, the host (read tools and resources ∥ write tools and prompts ∥ carry-forwards, CLI and probe ∥ repository-level scenarios), then shared surfaces, tool-level scenarios and cross-area fixes. Every builder's work went to an adversarial checker before it was finalized. Spend-limit stops (stages 3a and 3b, a fix stage) and session ends were recovered from each agent's copy.
- **Review.** Five lenses, each in its own copy of the merged tree, and an independent skeptic per lens who reproduced every finding: **leak red team (L)** over every surface × every non-author view, with two sweeps (Belmakor and a "Veil" world built to break disguises); **correctness through the tools (C)**, including every description Example sent verbatim and fuzzing for the SDK's generic error; **durability and concurrency (R)** with the real binary (two processes writing, kill -9 mid-batch, restore under a running server, damaged/read-only/newer-schema files); **model usability (U)**, seven headless `claude -p` sessions on a scratch publish with an isolated data directory (create Belmakor and record a session, "does Belmakor know about the Axiom Cage", check a chorus, set up One Piece with its gate, reveal it early then "undo that", 3 ogres) plus 20 scripted probes; and **mutation (M)**, 26 surviving mutants with killer tests. 78 findings: 68 confirmed or partly (after the skeptics' regrading 12 medium, 38 low, 18 nits), 10 refuted. The models used the tools well (the right tool first time in every run, dry_run before every write and the undo, every MCP error recovered in one call, nothing author-only written into a party-visible field); the defects were in what the tools told them. The worst:
  - **Leaks** (L): a planned session's title became visible to every player view at `start`, with no way to retitle a live session; a party-visibility default ignored when the party learned it, so a member absent from session 1 read its recap and a former member read everything after he left; as_of reads fell through to the visibility default past a row learned later (as of session 2 the party saw the true name of a woman it met in session 3 as "the veiled woman"); a party alias found by a public search; not-found suggestions naming entities the view had not met; a disguised entity's `same_as` relation; hints in non-author results pointing at author pages; and the write path never warned when it put a forbidden word or a hidden true name into party-visible text.
  - **Usability** (U): the check passed "haul your Cage back" (only the model's own judgement caught it); a disguised entity's party aliases were flagged as names the party does not use, so the run-1 model deleted the correct alias "the old king"; after `start` in a campaign that was not the current one, dice were silently not logged; a fact marked canon during a session was never in play for gates; prompts hard-coded `mcp__dnd__`; `start` filed an evening game under tomorrow's UTC date; sentence-opening words ("Afterwards", "That's") reported as unknown names.
  - **Correctness** (C): a state change with no session context cleared the learned session; a party member added mid-session joined "always"; `record_past` attendance could only grow; undo of a batch that created a session orphaned its rolls; as_of labels and "did not exist then"; beats showed no edges or reachability; one long line was cut whole by the output cap.
  - **Durability** (R): a restore could lose a write another process committed during it; a newer schema was refused only once per process; a backup interrupted mid-write counted as today's; read-path SQLite failures reached the model as the generic error; a restore's retention pass could delete its own source.
- **Fixes.** A fix spec (`phase6/fix-spec.md`) gave the decision per finding, then two fix stages, each fixer in its own copy with a checker: F1, Domain ∥ Repository infrastructure; F2, the write path ∥ the read path ∥ the host (which also merged the mutation lens's killer tests into the house test classes). Every fix that changes behaviour has a test that fails without it. Merging F2 needed hand resolution in three formatters and a reconciliation of 38 integration tests where the three fixers' wording met (the `title` argument, the advisory names line, the bare tool names in prompts, the new search footer, and the new forbidden-word warnings in the One Piece scenario).
- **Reverification.** Five agents re-ran every confirmed finding's reproduction against the merged fixes, each in its own copy (the reviewers' probe tests adapted to the new APIs, the R lens's scripts on the real binary, all 51 of the M lens's mutants re-applied with four more, and 43 new ones on the fixes' code), and a sixth re-ran the usability sessions on a scratch publish; a skeptic reproduced what they reported. All 68 were fixed or left by decision, except the undo path of U09 (an undo could unmask a disguised name under a gate with no warning). The models were steered right at every point the original runs went wrong (the chorus check now flags "Cage" without the model overriding it). The pass also found 20 new items, all low or nit, and the skeptic reproduced every one: as_of reads printed the character's name of today in the banner, dropped the session from their same-view hints, and replayed a known_as edited with the knower's own session as timeless; campaign_history's as_of hint named an argument the tool does not take, and its Example could not run as written; the check listed "Hail Belmakor" and "Sing of the Crumbling Statue" as possible inventions, and titled a lyric "pass … every name is one the speaker uses" while listing a partial name; nothing said how to end a disguise (the new warning even advised the opposite) or how to refer to a fact made earlier in a batch, and no description said writes warn about forbidden words; a server that was not ready yet answered concurrent calls one busy wait apart under another process's lock (a 39-request burst took 322 s, dice_roll 221 s); a locked backup was blamed on campaigns.db; and six mutants of the new code survived.
- **Fix round F3** (`phase6/fix-f3-spec.md`): three fixers (write path ∥ read path ∥ host text and infrastructure), each checked by an adversarial checker (80 more mutants) and finalized; no file outside an owner's set changed and no scenario test broke. The leak sweeps (Sky and Veil, every non-author view, as_of 1–3) give the same token findings on the merged tree as before it, apart from the added `"as_of_session"` in hints; the burst above now ends in 20 s with dice_roll at 10 s.
- Published as **0.6.0** (`DndMcp.csproj` and `.mcp/server.json`), single-file to a scratch directory, and installed at `~/.local/share/dnd-mcp/bin` (binary and pdbs replaced by rename; `content/` replaced whole; byte-identical to the publish output). The published binary passes the 7 stdout-purity tests (`DND_MCP_TEST_HOST_EXE`) and a smoke session with an isolated data directory (create, a dry-run write, a dice roll, the per-campaign resources). `DndMcp srd-build` exits 0 (4,602 documents: 2,415 for 2014, 2,187 for 2024; the index was current); `claude mcp list` shows ✔ Connected. `~/.local/share/dnd-mcp` still holds only `bin/`: campaigns.db is created by the first campaign call that needs it, never at startup.
- Manual end-to-end check: five headless `claude -p` runs on the installed 0.6.0, through an MCP config naming the server `dnd` with an isolated data directory (so the real campaigns.db was never touched; `~/.local/share/dnd-mcp` still holds only `bin/`). The tools were found from the server instructions alone, every write was previewed with dry_run first, and each MCP error was recovered in one call (the other errors were Claude Code refusing reads of the skill's files outside the working directory).
  - "Create my Belmakor campaign … record last session … it's the Axiom Cage and his name is Keras, but the party never heard either name": a player campaign, session 1 by `record_past`, and one 15-op batch putting Keras and the Axiom Cage behind the party's names ("the sorcerer king", "what the sorcerer king sent us for") with reveal rules forbidding both words. The model then checked Belmakor's view itself: a search for "Keras" finds nothing, and a test lyric gets 6 hard flags. The one MCP error was its first get by author handles in Belmakor's view (refused by design; it went on by `e:<n>`). The end checklist listed the participle "Beaten", and the model reworded its own recap to clear it. 20 turns, 12 MCP calls.
  - "Check this chorus: 'Old king, come down / we'll haul your Cage back up the mountain'": `partial_name` "Cage", a word of "Axiom Cage", hard-flagged by the check itself (in the review the check passed it, and only the model caught it); the model also caught the mountain nobody had established and offered a line built on what Belmakor saw. 14 turns, 6 MCP calls, 0 MCP errors.
  - "Set up my One Piece campaign … the big secret … then: session 7 is tonight and the party worked out the seal thing early": the gate as asked (after the axe, with Nadar's plan, "seal" forbidden until the axe), tying `forbidden_until` to the axe because the default lifts it when the party learns the fact. Its first batch used the codes "AXE"/"SEAL" and was refused (codes need digits); the knowledge dry run's author-visibility warnings made it set the facts restricted first; the reveal was applied with the gate's two warnings (the axe not in play, Nadar's plan not told) and forbidden-word warnings for the secret's own name and summary, and the model asked whether to keep the "land together" rule. 19 turns, 12 MCP calls.
  - A new session, "Actually undo that last change in my One Piece campaign": `since`, the undo as a dry run, then the undo; the party's rows are gone and the secret is hidden again, and the model said how to redo it. 6 turns, 4 MCP calls, 0 MCP errors.
  - "Is 3 ogres deadly for my four level-6 players?": `encounter_difficulty` in both editions with the campaign's +1 offset applied and named (2024 Low, 1,350 of 2,400 and 3,000 at +1; 2014 Medium, Easy at +1), then `balance_simulate`: the party wins all 10,000 fights in 2.01 rounds. 4 turns, 2 MCP calls.

**What exists now**
- **campaigns.db** (`DndMcp.Repository/Campaign/Migrations/0001_init.sql`, embedded): campaign, entity (+ alias, tag), relation, cross_link, fact (+ link, dependency), knowledge, session (+ attendance), objective, clock, beat_edge, dice_roll, an append-only change_log, and FTS5 `entity_fts` (with `hidden_aliases`) and `fact_fts`. STRICT tables, JSON-object CHECKs, AUTOINCREMENT sequence numbers (so `e:<n>`/`f:<n>` are never reused), partial unique indexes for knowledge, `change_log_no_replace`. Phase 7/8 tables are left to later migrations. The file: `DND_MCP_DB`, else `<data dir>/campaigns.db`; backups in `backups/` beside it.
- **Domain/Campaign**: the vocabularies (`CampaignValues`), handles (`CampaignHandle`: `kind:slug`, slug, `e:<n>`, `f:<n>`, codes, `session:<n>|live|last`, `campaign/<handle>`), slugs, name keys (`CampaignText`, `CampaignWords`), `Perspective`, the verdict rules (`KnowledgeVerdicts`, `KnowledgeModel`, `PartyMembership`), `Audience`, `EntityViews` (visibility, the disguised view, its `UsedNames`, perspective-safe refs), gates (`GateSpec`, `FactGates`: after/with/prefer/seeds/routes/min_routes/forbidden terms and patterns/forbidden_until/preferred terms), `SecretStatuses`, `RevealRuleData`, `ForbiddenVocabulary`, `NameScanner`, `ProperNouns` and `KnownNames` (one rule for "a capitalised word matches no name", shared by the check and the end checklist), `BeatReachability` (a port of the PWA's, with its tests), `Supersession`, `RegisterCodes`, and the write-op specs and validation (`Ops/`: `CampaignOpSpec`, `KnowerSpec`, `AttendanceSpec`, `CampaignOpFields`, `CampaignOpValidation`, `CampaignLimits`).
- **Repository/Campaign**: `CampaignDatabase` (lazy create, probe, migrate; Pooling=false, Recursive Triggers, busy_timeout 5000, BEGIN IMMEDIATE, an in-process write semaphore, the daily backup, a newer schema refused on every use, a failed open shared with the calls that waited for it; user-fixable failures become `CampaignStoreUnavailableException`, and `TryMapUnavailable` maps a read's), `CampaignDbMigrator`, `CampaignBackups` (VACUUM INTO a `.partial` then rename, retention, restore through the online backup API holding the write lock throughout), `ChangeRecorder` (every write and its change_log rows), `UndoEngine`, `ChangeReplay` (as-of rows, knowledge rows included), `HandleResolver`, `KnowledgeLoader`, `DiceRollLog`, and the row records.
  - `Write/`: `CampaignStore` (create/update/list/use/resolve), `CampaignWriter` (the ten ops), `KnowledgeWriter` (record/reveal/retract), `RevealChecks` (the gate warnings and secret-status derivation shared by both write paths), `PlayerTextChecks` (the write-time check of text a batch makes readable to player views), `SessionWriter` (plan/start/log/end/record_past and the end checklist), `HistoryWriter` (undo), `DiceLogWriter`.
  - `Read/`: `CampaignSearch`, `EntityReader`, `KnowledgeCheck`, `KnowledgeLedger`, `CampaignSummary`, `HistoryReader`, `SessionReader`, `CampaignDefaults` (never creates the database; ReadWrite without create, a 250 ms busy timeout, and "unreadable" told apart from "no campaign"), with `ReadScope`/`ReadGates` holding the perspective rules every reader applies.
- **Host**: seven tools — `campaign`, `campaign_search`, `campaign_get`, `campaign_write`, `campaign_knowledge`, `campaign_session`, `campaign_history` (15 in all); six prompts — `session_recap`, `session_prep`, `knowledge_check`, `continuity_check`, `in_character`, `homebrew_review`; resources `campaign://list`, per campaign `campaign://<slug>/summary` and `/threads` (listed), and `/session/<n>`, `/entity/<ref>`, `/knowledge/<perspective>` (readable, not listed); `CampaignService` (the lazy database, the process's current campaign, the ambient defaults); `SqliteCapabilityCheck` (startup probe); the CLI `backup` and `restore`; the formatters in `Formatting/Campaign/`. Version 0.6.0.
- **Carry-forwards done**: `dice_roll` `secret` and logging to a live session; edition defaults from the active campaign in rules_search, rules_get, encounter_difficulty, balance_dpr, balance_compare and balance_simulate, and the level offset in encounter_difficulty, each with a note saying the campaign decided (or, when campaigns.db cannot be read, that its settings could not be read and what was used); the SQLite probe wired into startup; `DND_MCP_DB`; the server instructions rewritten for 15 tools.

**Decisions made while implementing** (the plan is silent or differs; the contract's §13, and the review's fix spec)
- **campaign_progress folded into campaign_write** as flat ops (`status`, `objective`, `tick`, `answer`) beside `upsert`, `delete`, `restore`, `link`, `unlink` and `fact`: one batch, one undo unit, one flat op spec (the `ModifierSpec` precedent), with a per-op field table that refuses fields an op does not take.
- **Perspectives and the disguised view.** Every read takes `author` (default), `dm`, `table`, `party`, `public` or `character:<handle>`; `dm` in a DM campaign is the author. A non-author view sees an entity or fact only when the verdict says it knows it, and `author` visibility is absolute (knowledge rows never expose it). An entity the view knows under another name (`known_as` not equal to its own name, even when it equals an alias) or does not recognise is *disguised*: only that name, its kind, an `e:<n>` ref, the party aliases it may see (none when unrecognised; never a public alias, which can name the true identity, nor one containing the true name) and the facts the view knows; never its summary, body, tags, relations, slug or a `same_as` relation. A non-author view never sees a count of what it cannot see, secret text, a cross-link, history, prep or a planned session; a `withheld` question reads `open`. Alias visibility is per view (a party alias is not the public's). FTS column filters alone did not close these (the slug and the name column spell true names), so search re-checks hits against the view's visible text. A `character:<slug>` that names no character but begins exactly one character's slug (`character:belmakor` for `belmakor-silverwind`) is that character, and the banner says so. Hints in a non-author result point to the same view, never to author pages.
- **Verdict precedence** (table-driven): a character's own row; then the party row if the character is a member — present at the learned session → knows, absent or unlisted when attendance was recorded → uncertain, joined later or left before → uncertain, attendance not recorded → knows with a note; then a public row; then the row's visibility default, which for `party` visibility is dated like an implicit party row learned at the target's own session (an entity's introduced session, else the session its creation was written in; a fact's established session; a session's number), so a member who missed session 1 does not read its recap. A member who has left (until set, or status former) stops seeing party-visible relations and objectives. `table` and `dm` read party/public rows only when they say the group knows (an unaware party row gives "no record", never the visibility default). "No record" is never worded "does not know".
- **Point-in-time reads** replay entity, fact, alias, tag, session and knowledge rows from change_log (a knowledge edit made outside a session but dated by the knower's own `session` counts as made then; a bare re-dating is a correction and stays timeless); the perspective itself (a character's name for himself, the slug completion) is resolved as of the session, and same-view hints keep `as_of_session`; a row learned after the session asked for means "not yet", never the visibility default; a fact established later grants nothing by visibility; the author's as_of read of something made later says it did not exist then (other views keep the plain not-found, so hidden and missing stay indistinguishable).
- **Gates warn and apply; they are never refused.** The table is the source of truth, a refusal would sink a whole recap batch, and the skill's rule is "flag, don't fix". Gates are richer than research's: `after`, `with` (same knower, same session), `prefer` (advisory), `seeds`, `routes` with `min_clues` and `min_routes` ("any two of three"), `forbidden_terms`/`forbidden_patterns` with `forbidden_until` (the axe lifts "seal", not the reveal), `preferred_terms`. `rule`/`reveal_rule` entities carry the same vocabulary for rules not tied to one fact (Belmakor's no-name, no-timespan rule). A secret's status is derived from its gated facts and written in the same batch (so undo reverts it); knowing or suspecting any gated fact makes it partial, and a suspicion is never knowledge. A fact set to canon, ruled or accepted with no established session, that a gate or rule waits for, warns that it is not in play.
- **The write-time player-text check** (review L12/U09): when a batch ends (an undo or redo too, dry run included), the text it made readable to a player-side view (a statement told without `known_as`, a party/public name, summary, body or alias, a played or live session's title and recap, a `known_as`) is scanned for words an active gate or reveal rule forbids and for names that view does not use (author/restricted aliases, the true name of an entity it knows under a disguise). Each hit is a warning naming the field, the word and what to say instead; the write is applied. A gated fact's own statement counts too (the vocabulary rule keeps the word out of every non-author text, as `check` does). A name must be written as a name (capitalised where the stored name is), unlike forbidden words, which match in any case. A true-name warning also prints the `campaign_knowledge record` call that sets the reader's `known_as` to the true name, for when the party has learned it: giving the true name as `known_as` is how a disguise ends.
- **change_log** is mechanical (`create`/`update`/`delete`, one row per changed column or `data` key) with a separate `action` label, `other_entity_id` and `undo_of`. Undo is generic over it, refuses when a later batch touched the same row or field or references an id the batch created (UUID substring test, including inside JSON and cross-campaign links), refuses to undo a session's creation once rolls or later batches reference that session, and is itself undoable. The live log and dice rolls are not logged (undo never un-rolls dice). Point-in-time reads reverse rows attributed to later sessions; rows written outside a session are timeless.
- **Sessions**: one live session per campaign; `start` and `record_past` with no number take the next session to play (the lowest planned above the last played, else max + 1), `plan` max + 1; a session write is filed under the session it writes; unplayed sessions are invisible to non-author views. `start` dates the session by the local date. `start` and `end` take `title`, and starting a planned session that keeps its planned title warns that players now see it. `start` or `end` naming a campaign makes it this process's current campaign and says so. Attendance given to `end` or `record_past` replaces the list (removals are logged, so undo restores them). A character linked `member_of` the party during a session joins as of that session. A state change with no session context keeps the row's learned session. With no visibility given, a `secret` is restricted and a `rule`, `note` or `question` author-only; other kinds take the role default.
- **F-codes**: one register per campaign per letter, shared by entities and facts, 1 + the largest number including struck and deleted rows (F56a counts as 56; F56A parses as F56a), computed inside the transaction so a dry run shows the real code; `proposed` gets the next `F`.
- **The knowledge check** hard-flags a capitalised word that is a distinctive word (four letters or more, not common English) of a name, alias or forbidden term the speaker does not use, unless it is itself a name the speaker uses ("haul your Cage back"). Facts are headed "has no record of knowing"; an author-speaker check says there is nothing to flag from the author view and how to check a character's. A capitalised word made only of a name the speaker uses, connectors, titles and a sentence-opening word ("Hail Belmakor") is no possible invention, by the same rule as the end checklist; a line-opening word of a secret name is listed for review, and the title says so rather than a bare "pass".
- **Defaults from the active campaign** (D8 and the carry-forwards): the process's current campaign (set by `use`/`create`, and by `start`/`end` naming one), else the persisted active one — never "the only campaign", and never by creating campaigns.db; `mixed` gives 2024; an explicit argument always wins; the output says when the campaign decided, and when campaigns.db exists but cannot be read, says so and what was used. Each Claude session keeps its own current campaign. `encounter_difficulty`'s `party: "campaign"` waits for Phase 7 (levels live on character sheets).
- **dice_roll** logs only while the resolved campaign has a live session and the roll is not seeded; one row per roll with the expression as typed and the faces (up to 1,000 per roll); a failed log write still returns the dice with "Not logged: …", and when another campaign has the live session the note names it and the `use` call; `secret` keeps a roll out of non-author views, and an open roll's label is shown to player views of the session (the description says so). It is no longer read-only (ReadOnly hint false).
- **Resources**: concrete per-campaign entries in `resources/list` (the model's resource listing never shows templates; Claude Code 2.1.283 offers templates only in the user's `@` menu), deep URIs readable without being listed, a list handler that never creates the database or fails the list (a damaged, locked or newer-schema file gives the static part and a logged warning), no templates. `campaign://list` marks the campaign calls without `campaign` use, by the same rule as `campaign list`.
- **Prompts**: PLAN's four plus `in_character` and `homebrew_review` (they had no phase); arguments are single tokens (Claude Code splits on whitespace by position and drops the rest), free text comes from the conversation, and each prompt returns instructions that drive the tools (dry-run first) rather than pasted database content. Prompts name tools bare (`campaign_knowledge`), since a prompt drives the tools of the server it comes from, whatever name the user registered it under. `bootstrap_from_skill` stays in Phase 8.
- **Errors**: the call-tool, get-prompt and read-resource filters turn a user-fixable SQLite failure on campaigns.db (locked past the busy timeout, damaged, not a database, I/O, full, read-only) into a message naming the file and what to do, never SQL; any other code stays the SDK's generic error.
- **Output**: every campaign result is capped at 24,000 characters on a line boundary, or inside the line when the last boundary is before half the room; the end checklist and session pages budget their long parts.
- **Schema**: knowledge has two partial unique indexes (research's coalesce index served neither upserts nor lookups); relations and objectives take no `restricted` (they have no knowledge rows); `lean` joins canon_status; `data` keys are plain identifiers; Phase 7/8 tables wait for their migrations.
- **CLI**: `backup [--reason]` takes a reason name (it is part of the file name retention parses); `restore <file>` validates, takes a pre-restore backup and copies through SQLite's online backup API while holding the write lock, so a running server keeps working and a write another process attempts meanwhile waits and lands after it (or fails with the store message), never vanishes; it runs no retention, and refuses an interrupted or locked backup naming the backup. Backups are written to `.partial` and renamed; stale `.partial` files and orphaned journals are cleared. The startup probe treats a missing WAL as a warning and clears its own stale scratch directories.
- **Not built**: per-knower restricted aliases (a knower's name is its one `known_as`), so the `dm` does not find an entity by an author alias even when it knows the fact naming it (Belmakor row 27, pinned).

**Corrections found while implementing**
- **"The old king" is not "the Axiom Cage".** PLAN's Context and research §1a reduced them to one entity; the skill has two (Keras, the old king; the Axiom Cage, "the thing he wants"), and the fixtures follow the skill. The level-11 drift in `party-and-band.md` is at line 19, not 20.
- **research/04's DDL** had six more traps beyond the four Phase 0 found: `ux_knowledge` served neither upserts nor lookups; an alias moved to another entity and a renamed tag were not re-indexed; a foreign key to a table that does not exist yet fails every INSERT and DELETE on the child table even when the column is NULL (so Phase 7/8 columns wait for their tables); `REPLACE` on an FTS-backed table leaves a stale index row (never used; Recursive Triggers on as well); bm25 needs all seven weights (six silently weight `hidden_aliases` 1); and a restored backup is in DELETE journal mode, so WAL is set on every open, not once at creation.
- **PLAN's verdict precedence** ("anything else means not known") would read the party-visibility default as knowledge regardless of when or whether a member was there; it is dated by the target's session (above), and "no record" is not "not known".
- **SDK and Claude Code facts.** A C# default value is published as the schema's `"default"`, so an edition that defaults from the campaign must default to null; prompts and resource reads bypass the call-tool filter (two more filters translate their errors); Claude Code splits prompt arguments on whitespace by position and discards message roles; a prompt that names `mcp__dnd__…` tools breaks when the server is registered under another name.
- **SQLite facts.** In WAL mode `sqlite3_backup_step(0)` takes the destination's write lock, which is what lets a restore hold it from before its pre-restore copy to the end of the copy-in; in rollback-journal mode the same needs `BEGIN IMMEDIATE`, then `locking_mode=EXCLUSIVE`, then `ROLLBACK`. `VACUUM INTO` writes in place, so an interrupted backup is a valid-looking file with today's name unless it is written under another name and renamed.
- **One Piece fixtures**: the gated facts must be `restricted`, not `author` — author visibility is absolute, so an author-only secret could never show to the party after its reveal (row 40).

**Known gaps left open in Phase 6**
- `encounter_difficulty`'s `party: "campaign"` (Phase 7, with character levels).
- Per-knower restricted aliases; tripwires on knowledge ("the moment he learns…"), scoped public knowers ("local to the areas around the fragments"), ordering constraints between arcs ("must land before"), per-character level offsets, a `promoted` invention status, and `ruled_by` on rulings were found in the skills and not built.
- As-of reads search today's text (hits whose text changed are re-checked in C#, not by porter stemming); a public-visibility entity introduced later is shown as of an earlier session (public visibility is not dated; party visibility is); an entity whose creation was undone does not reappear as of an earlier session.
- Stale-figure detection is word-based: "four hundred" does not match "400". The possible-invention heuristic flags some capitalised ordinary words (sentence-opening adverbs and connectives are now stoplisted).
- `e:<n>`/`f:<n>` numbers are global sequences (handles resolve within a campaign).
- A party row on a planned or struck clue still counts toward a route.
- The write-time player-text check does not cover objectives, tags or relation labels, nor other text that merely mentions a name whose disguise the batch changed; its warnings are in the result but not in the batch's change_log reason (the gate warnings are). A dry-run undo's warnings are headed "Applied anyway", as a write's dry run is.
- The shared unknown-name rule reads a sentence-opening "Rolf of the Crumbling Statue sang." as known (the first word of a sentence may be an ordinary word), and lists a mid-sentence "we cried Hail Belmakor".
- Left as the review found them, by decision: an alias with no visibility defaults to party (documented); knowledge recorded into an unplayed session applies (a session can be played without being marked played); a secret's status and the party's awareness of the secret entity are separate; `since` lists oldest first (the date and session filters reach the newest); a write queued behind another process's lock is not cancelled when the client goes (exit waits for it, about 5 s per queued write; `WriteAsync` has no production caller yet). dice_roll's log lookup has no shorter busy timeout: under another process's lock it waits one busy wait (about 10 s) before rolling with "Not logged".
- The server instructions are at 2,043 of 2,048 characters, `balance_dpr`'s description at 2,040, `balance_simulate`'s at 2,038 and `campaign_write`'s at 2,031; `balance_compare`'s input schema is 31,881 of 32,000. Phase 7's tools need a rewrite.

**Phase 5 — done.** All three exit criteria are met: `balance_simulate` matches the closed form within its confidence interval, the death-save absorption test passes, and every SRD monster normalizes or warns. The phase was committed as 2a5124a "phase 5" before its review was fixed; the review fixes are uncommitted on top of it (82 files changed besides this PLAN.md, +5,891/−525).
- **Closed-form agreement.** `DummyDpr` runs a build against an infinite-HP dummy with the target's AC, saves, adjustments and condition, and 55 builds are compared with `DprEngine`'s fight horizon (R = 3) and its round 1: 200,000 fights × 3 rounds per build, a fixed seed per case, |sim − exact| ≤ 4 SE on both. The builds cover the Phase 4 goldens (2014 GWF + GWM at AC 13 and 19 and forced on and off; the 2024 fighter with Hew and both Savage Attacker rulings; the four smite policies; Vex; Topple; Cleave), Bless, Lucky, Elven Accuracy, crit range 19, resistant, vulnerable and immune targets, typed and untyped riders, Action Surge, a reaction attack, offhand, Fireball against 4 with and without Evasion, Sacred Flame against Magic Resistance, Stunning Strike and the warlock baseline. 42 were written with the simulator; reviewer M added 12 and reviewer C 1 (Spirit Guardians set up with the Action, which the cleric bug below failed by 184 SE).
  - 2014 Stunning Strike is the one expected difference: the simulator keeps the stun into the monk's next turn, so over the fight it deals 13.12 against the closed form's 12.27. That case compares round 1 and checks the direction.
  - Round-1 p50 and p90 are within ±1 of the exact percentiles for five builds.
- **Death saves.** Enumerating the Markov chain of `DeathSaves.Step` gives P(death) 0.404875, P(stable) 0.41375 and P(natural-20 revive) 0.181375 to 1e-12. 400,000 dying creatures left alone in the engine (seed 20260927) give 0.404670, 0.414035 and 0.181295, each golden inside its 99.9% Wilson interval.
- **Every monster normalizes or warns.** All 334 + 341 monsters normalize without an exception. Every trait, action, reaction, legendary action and spell is simulated, marked `no_combat_effect` with a note, or carried with a warning. The warnings are pinned by edition and code: 2014 not_modelled 215, approximated 96, unresolved_reference 3; 2024 191 / 116 / 2; data_conflict and unparsed 0 in both (the fix-up round moved 2014 not_modelled from 214, 2024 not_modelled from 188 and 2024 approximated from 117). Every normalized monster also fights, as an enemy and as a party ally, without an exception.
  - The criterion did not hold at first. Reviewer C compared every "N (XdY)" in action text with the normalized dice and found five per-turn riders dropped silently (fixed). The fix round's normalizer verifier found three more: the 2014 water elemental's Whelm, the 2024 fire elemental's Fire Aura (Burning) and the 2024 sea hag's Death Glare (a 20-HP branch). The fix-up round warns all three as not_modelled and moves the 2024 horned devil's infernal wound from approximated to not_modelled. A whole-SRD dump before and after shows only warnings changed, no stat-block field.
  - The 3 + 2 unresolved references are correct: the vampire's bat and mist forms have legendary actions naming attacks those forms lack.
- **Oracle against your SRD JSON.** All 645 of its monsters match a normalized stat block by name and form. 922 attack bonuses and 214 save DCs are equal, with 0 mismatches and no exclusions; 1 attack and 44 saves belong to effects the simulator does not run (pinned). The kraken's Fling moved from compared to not compared in the fix round (215 → 214).
- **The other goldens.** Fireball (2014, DC 15, 8d6) against 4 goblins over 400,000 casts: the 99.9% interval of P(all four die) contains 0.999333 and lies above the independent-rolls value 0.998775; raw 89.2 and effective 27.998608 per cast, within 4 SE. Recharge 4–6, 5–6 and 6 within 4 SE; legendary action counts exact; concentration DC max(10, ⌊damage/2⌋), capped at 30 in 2024 only; massive damage.
- **Determinism.** The same seed gives byte-identical serialized reports at 1, 4 and 16 threads (a 3,000-fight run with rolled enemy HP; a fix-round verifier repeated it with two specs that use every mechanic the fixes added, replay included; a fix-up verifier with a lich + trolls run with `compare`, a 2024 solar run and a 2014 lich + adult red dragon run). Since chunks are sized by work, a test also pins byte-identical reports at chunk sizes 16, 14 and 6. `replay` reproduces a fight's outcome and rounds from the full run.
- **Open question 6, settled by the data.** Median AC over CR 1–20 (19 CRs; no SRD monster is CR 18):
  - The 2014 SRD monsters track the DMG 2014 table exactly on average (mean difference 0.00: below it at 6 CRs, equal at 6, above at 7).
  - The 2024 medians are 0.42 higher than both on average (against 2014: higher at 8 CRs, equal at 9, lower at 2).
  - So 2024 monsters are slightly harder to hit, not easier. The homebrew-balance skill's "lower AC" is contradicted; Blog of Holding's "+1" has the right direction at under half the size.
  - Also from the table: attack bonuses are nearly equal between the editions and above the DMG table at every CR but one (2014 CR 4, equal), by 2 or more at most CRs from 9 up (CR 17: +13.5 against +10); 2024 mean save bonuses are lower at high CRs (fewer proficient saves); 2024 median HP is higher than 2014's at 12 of the 19 CRs (equal at 5, lower at CR 4 and 17).
- **Speed.** A 4-versus-3 fight run 10,000 times takes 0.05–0.09 s on the 16-thread machine (about 0.5 s on a loaded machine). The build measured the worst shape at 65.5 million creature-turns in 16.9 s and capped the work at 60 million, documented as about 15 s. The fix-up round re-measured it: 40 creatures that all stay standing to a 100-round cap took 43 s at 60 million, 1.0–1.4 million creature-turns a second (a monster values its actions against every candidate; 3 against 3 runs 4.8 million a second). The cap is now 20 million, where the worst cases measured took 15.8 s (fighter walls), 19.0 s (power-attack builds with spread targeting) and 20.5 s (level 20 wizard archetypes against adult red dragons); the contract asks for about 20 s. A run uses one thread fewer than the machine has.
- Sanity fights at build (before the review fixes): four 2024 level-5 fighters beat 3 ogres in all 10,000 fights, in 3.24 rounds (2014: 2.99); four level-5 archetypes (fighter, rogue, cleric, wizard) win 100% in 2.06 rounds (2014: 1.88). A level-15 party loses every fight to an Adult Red Dragon plus a Lich; the review checked it line by line and found it genuine (51,000 XP is 1.63 × the 2024 High budget of 31,200).
- Published as **0.5.0** (`DndMcp.csproj` and `.mcp/server.json`), single-file to a scratch directory, and installed at `~/.local/share/dnd-mcp/bin` (binary and pdbs replaced by rename; `content/`, now with `content/overrides/`, replaced whole; byte-identical to the publish output). `DndMcp srd-build` exits 0 (4,602 documents: 2,415 for 2014, 2,187 for 2024); `claude mcp list` shows ✔ Connected. The published single-file binary passes the 7 stdout-purity tests (`DND_MCP_TEST_HOST_EXE`), whose session now includes a simulate call and a combatant call.
- Manual end-to-end check: three headless `claude -p` runs on the installed 0.5.0, all answered correctly, the tools found from the server instructions alone.
  - "Using the 2024 rules, simulate four level-5 characters (fighter, rogue, cleric, wizard) against 3 ogres": `balance_simulate` with the four archetypes against `2024/monster/ogre` × 3 (20,000 fights, seed 42): the party wins 100% in 2.03 rounds on average, and a party member dies in 0.01%. The model cross-checked `encounter_difficulty` (2024 Low: 1,350 XP of a 2,000 Low budget), then on its own re-ran a harsh variant (the party surprised, rolled enemy HP, enemies `focus_fire` + `finish_downed`): still 100%, 2.08 rounds, a party member dies in 1.1% (the rogue) and is left dying in 4.7%. 5 turns.
  - How the simulator sees the 2024 Lich, Power Word Kill and Paralyzing Touch: `rules_get` format combatant, then the SRD text, Power Word Kill and Paralyzed. Correct on Power Word Kill (dies at 100 HP or fewer, else 12d12), Paralyzing Touch (paralyzed until the start of the lich's next turn), Shield (+5 until its next turn) and Legendary Resistance 4 (never in a lair). The model noted that the combatant view does not say which of the Paralyzed condition's effects the engine applies (Advantage against it, a critical hit within 5 ft), so it could not confirm them from the view. 6 turns.
  - A 2014 level-5 fighter (greatsword, Str 18, GWF, Extra Attack) against a werewolf, mundane against +1: `rules_get` "Werewolf" (kind monster) was refused with the three form refs listed; `balance_dpr` with `target.monster` "werewolf" resolved to the Human Form with a note. Mundane 0.00 DPR (immune to nonmagical bludgeoning, piercing and slashing that isn't silvered), +1 24.83 against the human form and 23.50 against the hybrid form, silvered mundane 20.57. 8 turns.

**Tests: 12,821 passing** (10,384 unit + 2,437 integration, none skipped: the sibling-repo tests ran), 0 build warnings, on the uncommitted fixes; the integration suite was run twice. At commit 2a5124a: 12,500 (10,092 unit, 4 of them skipped because the sibling repo was not beside the copy, + 2,408 integration).
- Built by six agents from one contract (`phase5-contract.md`), each in its own copy: F (the Phase 4 review fixes and the DSL additions), N (normalizer, overlays, empirical statistics) and S (simulator); then T (targets from stat blocks and profiles), A (party archetypes) and H (host).
- **History.** The build session ended before the review was fixed, and its scratchpad was wiped. The next session recovered both reviewers' work from their transcripts: reviewer C's report and failing tests, and reviewer M's killing tests. M had run 185 of its 270 mutants and was writing killing tests when a spend limit stopped it; three agents finished the campaign by area (engine; normalizer and empirical statistics; host, DPR and archetypes).
- Reviewed through two independent lenses, each in its own copy:
  - **Correctness/data truth (C):** 1 high, 4 medium and 3 low findings, each with a failing test, plus 6 observations. It spot-checked about 40 stat blocks in both editions (the adult red dragon and the lich line by line against `rules_get`), all 37 entries of `spells.2024.json` against the SRD markdown, the death and dying rules, concentration, initiative and surprise, legendary actions and Legendary Resistance, the statistics and the seeding.
  - **Conventions + mutation (M, then the area agents):** 271 mutants: 154 killed by the original suite, 96 by the recovered tests, 7 by new tests; 12 equivalent; 2 real bugs; 0 unkilled. M's review found 4 real bugs (an advantage setup skipped in round 1, a concentration modifier without a setup that outlived its concentration, PC melee hits ignoring retaliation traits, and a DPR crash); the area agents added dead members, test-only seams, literals, duplicate regexes, missing summaries and unchecked overlay ranges.
  - Merging the reviewers' tests found two more bugs, which got tests of their own: `balance_simulate` starved the thread pool (a cancel was read 12.6 s late, and the first progress came at about 80% of a 100,000-fight run), and a `use_actions` routine over recharge-pooled spells cast nothing. All the tests went into one patch (19 files, +1,655 lines); on the phase-5 code 30 of them failed (28 unit, 2 integration), every one a real bug.
- **Fix round.** Four fixers in their own copies (engine; simulate host; normalizer; DPR, archetypes and the rest of the host), a merge, then five verifiers (one per area and one on a fresh copy) who reverted each fix to watch its test fail. All 30 tests pass; the merged suite was 10,317 unit + 2,424 integration, 0 skipped. What changed:
  - **Engine.** A concentration save effect set up with the Action (Spirit Guardians) was cast again every turn, so every cleric archetype from level 5 stopped attacking after round 1 (−110 to −243 SE against the closed form); now within 0.05 SE. An advantage source with a setup applies from round 1 (Innate Sorcery's round 1: 7.70 → 10.73, exact 10.725). A concentration modifier without a setup (Hex in `warlock_baseline`) stops when concentration breaks. PC melee hits trigger retaliation traits. Areas catch creatures at 0 HP after the standing ones, so a dying PC in a breath weapon takes a death-save failure. Shield lasts until the caster's next turn. "Immune for 24 hours after a success" is honoured, kill thresholds kill (Power Word Kill, the solar's bows), the monster AI values the conditions an attack imposes (the 2024 lich now uses Paralyzing Touch), and a routine sharing its spells' recharge spends it once (the 2024 pit fiend's Hellfire Spellcasting casts Fireball twice).
  - **Simulate host.** Runs use ProcessorCount − 1 threads with the pool minimum raised, so a cancel is read in about 10 ms. Progress goes out in order. "Left dying at the end" is reported apart from deaths. Per-entry means count fights, not copies. Wilson bounds are exact at 0 and n. An archetype's source line uses the catalogue's name.
  - **Normalizer.** The 24-hour immunity clause is flagged on 37 save actions (the review's list said 33; it missed 4). Power Word Kill kills at 100 HP or fewer in both editions (the 2014 lich now casts it). The 2024 pit fiend's Hellfire Spellcasting is one recharge shared by its spells and routines. Swallow warnings appear only where a swallow is built. The five per-turn riders are warned. The 2014 kraken's Fling is not modelled (its save belongs to the creature the target is thrown at). A true minus in notes. Overlay ranges are checked.
  - **DPR, archetypes, host.** `balance_dpr` crashed with the SDK's generic error on a Vex build against a target immune to the weapon's damage; it now answers 0. The paladin archetype takes Str, Cha, Con (Aura of Protection +3/+4/+5 from level 6/12/16; it was +1). The combatant view shows kill thresholds, the immunity and Shield's duration.
  - **Outside the areas** (the normalizer fixer and the merge). The 2014 violet fungus correction (below); `MultiattackOptionTypes`; the Shield notes in both spell overlays.
  - The verifiers raised 27 further problems: 1 high (a condition timed on its source's turns never ends once the source dies, now common because monsters pick Paralyzing Touch), 3 medium (two concurrent `balance_simulate` calls starve the pool again; heavy runs of up to 1,024 fights send no progress for 25–50 s; Shield's end had no test) and the rest low or nits. All 27 are fixed in the fix-up round below.
- **Fix-up round.** Four fixers in their own copies (engine; simulate host; normalizer; DPR and host), a merge, then two verifiers on fresh copies (engine and simulate host; normalizer, DPR and host) who reverted each fix to watch its test fail. Every fix that changes behaviour has a test that fails without it. The merge found one gap between areas and closed it: the report did not say that a single-target kill is aimed at a creature it kills, and now does when a monster has one. After the merge: 10,375 unit + 2,434 integration, 0 skipped. What changed:
  - **Engine.** When a dead creature's place in the initiative order comes round, what it imposed still ends or ticks there. The verifiers' probe (the 2024 lich + 3 trolls against 3 level-17 fighters, 200 seeds) had 18 of 62 lich deaths leave a fighter paralyzed on 2 or more later turns; now 0. The 2024 solar's Slaying Bow kills instead of dealing its damage (before, each kill also added about 60 to the damage dealt and taken). A single-target kill is aimed and valued at a creature it can kill: the 2014 Power Word Kill against a 200-HP and an 80-HP PC had wasted every cast under `threat` and 99 of 200 under `spread`, now none. An area that could reach only creatures already immune to it is not used (the 2014 adult red dragon's Frightful Presence: 129 → 115 uses in 30 fights). Shield's end has its test, every hunk a verifier's mutant survived is pinned, and a shortcut that could never fire is gone. Ordinary fights (fighters against ogres, a level-5 party against bugbears and goblins, a 2014 level-10 party against 2 trolls) draw and report exactly as before, apart from two reworded assumption lines; in the verifier's lich fight the party's wins rose from 17.7% to 19.7%.
  - **Simulate host.** One call's fights run at a time, chunks are sized by work, the work cap fell from 60 million to 20 million (see Speed and Decisions), and a run stops when the client closes stdin. The Compare section pairs "a party member is left dying" beside the deaths, with a caveat. The assumption lines now match the engine (a Parry whose text says melee attack covers only a melee attack; a condition counts with the chance it lands, an attack's hit and a failed save where there is one; exhaustion a tenth, deafened nothing). The Kills column has a note.
  - **Normalizer.** The three silent drops are warned (above). A run warning now quotes its triggering sentence whole, so the rug of smothering's and the kraken's warnings include the damage they report (41 warnings gained their full sentence; no count changed). The rule that a 24-hour immunity names a word of the action's name has a test; the werewolf test said to pin it could not fail.
  - **DPR and host.** The combatant view words a kill threshold by action kind, as the engine applies it: a save action "On a failure, a target with 100 HP or fewer dies (no death saves); above that, it takes the damage.", an attack "On a hit, …"; the 2014 Power Word Kill no longer says "no damage each". A melee-only Parry says "one melee attack". The 2024 pit fiend's Hellfire Spellcasting routine no longer shares a recharge with itself, and the 2024 lich's Deathly Teleport shows its area (2 creatures by the DMG's count), as the engine hits it. The Nick note is one rule in both tools (see Decisions).
  - The two verifiers found 2 low problems and 3 nits more, all fixed in a last round. Under the new cap, precision mode was refused for ordinary requests (a 4v2 with `compare`, or 11 combatants, at round cap 20), because it was charged 100,000 fights; it is now charged its first batch. The Nick fix noted the common 2024 build that already makes the Light extra attack in the Attack action, beside a Dual Wielder bonus attack; the note now skips a build whose Attack action already holds the Light extra attack. The Kills note left out outright kills and exhaustion. A session end was logged as a failed tool call with a stack trace; now it is one information line, and the doc of what ends a session no longer says running requests are waited for. The final suite is above.

**What exists now**
- **Domain/Simulation/StatBlocks:** `StatBlock` and its records, the normalized stat block every consumer reads (numbers, all six `SaveBonuses`, a final `InitiativeBonus`, adjustments with qualifiers, `Actions`, `Multiattacks`, `Spells`, bonus actions, reactions, `Legendary`, traits by kind, `Forms`, `Notes`, `Warnings`), and `StatBlockValues` (the vocabularies: action and trait kinds, slots, durations, damage qualifiers, warning codes). The fix round added `KillAtOrBelowHp` and `ImmuneAfterSuccess` on actions and on riders.
- **Repository/Srd/Combatants**
  - `MonsterNormalizer.Normalize(SrdDocument, ISrdLookup)`: a corrected srd.db document → `StatBlock`; it never throws for vendored data. It is split into `MonsterReading` (numbers, traits), `.Actions`, `.Spells` and `.Routines` (multiattack, legendary).
  - `MonsterRecord` (one view over both editions' typed models), `ProseText` (the prose readers: areas, durations, conditions, DCs), `DamageAdjustmentReader` (qualifiers, never split on commas), `TraitCatalogue` (one name → kind table; a test lists every distinct trait name in both editions), `NormalizationLog`.
  - `SpellNormalizer` / `SpellProfile`: a spell document plus its overlay → attack, save, auto_hit, heal, parry, `no_combat_effect` or `not_modelled`. A catalogue test covers every spell a monster casts.
  - `MonsterOverrides` and `SpellOverlay` load `content/overrides/` with range checks. A test removes each override in turn and fails if the stat block does not change.
  - `Srd/Models/MultiattackOptionTypes`.
- **content/overrides/**, each file with a provenance `.md`:
  - `monsters.2014.json`: the 5 mislabeled half-saves. Without them the normalizer reports `data_conflict`, which is how it found them.
  - `monsters.2024.json`: Initiative for all 341 monsters, from the SRD 5.2 markdown (the data has none; 118 differ from the Dex modifier). A sibling-repo test re-reads the markdown.
  - `spells.2024.json`: 37 spells, every combat spell a 2024 monster casts plus Fire Bolt, Sacred Flame, Eldritch Blast and Inflict Wounds, checked against `07_Spells.md`.
  - `spells.2014.json`: 20 supplements to the records' own data (ray, dart and beam counts; Hold Person/Monster, Blindness, Entangle and Web conditions; Flame Strike's radius; Disintegrate single-target; lingering spells once; Power Word Kill's threshold).
- **Domain/Encounters/MonsterStatsEmpirical:** per edition and CR row, the count, median AC, median HP, median best attack bonus, median best save DC (with its count) and median mean save bonus. `MonsterStats(edition, cr)` interpolates a CR no SRD monster has and flags it. A test recomputes every cell from the normalized data.
- **Domain/Simulation**
  - `Simulator.Prepare(spec)` → `PreparedSimulation` (validation, archetype expansion and the work check, so bad input is refused before any wait) and `Simulator.Run(spec or prepared, seed, ct, progress, maxThreads)` → `SimulationReport`. `SimulationSpec` with `CombatantSpec` (party and enemies share it), `PolicySpec`, `CompareSpec` and `SimulationCombatant(spec, StatBlock?)`; `SimulationValues` (wire values, matched forgivingly) and `SimulationLimits`; `SimulationPreparation` (validation with up to five problems, archetype expansion, the assumptions list); `RunTally` (integer accumulators); `SimulationStatistics` (Wilson, CLT means, percentiles); `DummyDpr` (the public agreement harness).
  - `Engine/`: `CombatantCompiler` → `CombatantTemplate` (compiled once per run), `Creature` (per-fight state), `FightSetup`, `DeathSaves` (a pure step function), `CombatLog` (the replay), and `Fight`, one engine per thread, in partials: `Rolls`, `Damage` (damage, death, healing, concentration, conditions), `Targeting`, `Pc` (a DSL build's turn) and `Monster` (a stat block's turn).
  - `PartyArchetypes.Expand(name, level, edition)` over `Archetypes/` (`ArchetypeCatalog`, `ArchetypeDefinition`, `ArchetypeKit`, `ArchetypeMember`, `MartialArchetypes`, `CasterArchetypes`): 12 classes × levels 1–20 × both editions. All 480 simulate in a test.
- **Domain/Features**
  - `TargetResolver.Resolve(spec, level, StatBlock?)` takes AC, saves, HP, qualified adjustments (`DamageProperties`), Magic Resistance, Evasion, non-lair Legendary Resistance, condition immunities and CR from a stat block.
  - `TargetProfiles.Row(profile, cr)`: `dmg2014` | `mm2014` | `mm2024`.
  - DSL additions: `heal`, `duration`, the `crit_or_kill` trigger, the `magical` / `silvered` / `adamantine` properties, `BuildUse.Simulation`.
- **Host**
  - `balance_simulate{party[], enemies[], iterations, seed, round_cap, edition, surprise, enemy_hp, precision, replay, policies, compare{member, feature}, rulings}` (`SimulateTools`, rendered by `SimulationMarkdown`): read-only, not idempotent without a seed, titled "Simulate a fight". An entry is `archetype` + `level`, `monster`, or `build` + `hp` + `ac`, with optional `name`, `edition`, `count`, `position`, `saves`, `save_proficiencies`, `initiative_bonus` and `death_saves`. One call's fights run at a time (`FightGate`), and a run stops when the client closes stdin.
  - `rules_get` `format: "combatant"` (`CombatantMarkdown`): a monster as the simulator reads it (numbers, qualified adjustments, traits by kind, actions with to-hit, damage and averages, riders, saves, areas, usage, routines, spells, legendary actions, notes, warnings). Other kinds are refused with the formats listed.
  - `balance_dpr` and `balance_compare` take `target.monster` and `target.profile`; both are now async.
  - `StatBlockService`: a DI singleton over `SrdIndexService` that builds the normalizer lazily from the content root's overrides and caches stat blocks by ref; several lookups share one index wait.
  - `SrdMonsterLookup`: `encounter_difficulty`'s resolution (refs, names, forms, the other edition, close names), now shared by the encounter, simulate and DPR tools with per-tool wording.
  - `CheckedAsAttribute` (publish a parameter untyped, validate it as a type) beside `SameShapeAs`; `ToolArgumentGuard` checks array-shaped parameters item by item.
  - `rules://tables/monster-stats-by-cr-empirical` (both editions) and its attribution note.
  - Server instructions name `balance_simulate` and the combatant format; the last line now names only campaign tracking. 8 tools. Version 0.5.0 in `DndMcp.csproj` and `.mcp/server.json`.

**Decisions made while implementing** (the plan is silent or differs; the first seven are the contract's §10)
- **Engagement** is a front line and a back line per side, not engagement groups. Melee attacks reach a standing enemy front-liner (any enemy when none stands); flying melee creatures reach the back line; a front-liner's ranged attack has Disadvantage while an enemy front-liner stands. "An ally within 5 feet" (Pack Tactics, Sneak Attack, Martial Advantage) means another standing ally on the front line with a front-line target. No movement and no opportunity attacks except the DSL's reaction attacks.
- **`balance_compare` has no sim mode.** The common-random-numbers comparison is `balance_simulate`'s `compare` (one party entry plus a feature, the same seeds, paired differences with intervals): it has the party context, and `balance_compare`'s schema had no room.
- **Custom enemies are DSL builds** (`build` + `hp` + `ac`), not a free-form stat block; SRD enemies are named by ref or name.
- **Party archetypes are new**, because the plan's `party[builds]` would make every "simulate this fight" start with the model writing four builds. 12 subclass-free classes (fighter, barbarian, paladin, ranger, rogue, monk, cleric, druid, wizard, sorcerer, warlock, bard), levels 1–20, both editions: the standard array plus ASIs at the class's levels, HP at the maximum for level 1 plus the average per level, a typical AC, the class's save proficiencies and a signature routine (Extra Attack, Sneak Attack, Rage, smites with slots, scaling cantrips, a levelled area spell from level 5 for full casters, Healing Word / Cure Wounds, Second Wind). The paladin raises Str, Cha, Con in that order (the review moved Cha ahead of Con). Wizards and sorcerers carry Acid Splash beside Fire Bolt, because fire-immune dragons and balors otherwise took no damage. An entry's own fields override the archetype's: `saves` per ability, `save_proficiencies` replacing the archetype's (and a paladin's aura). Campaign character refs wait for Phase 7.
- **The DSL gains** `heal` (dice and/or amount, action or bonus action, 1–6 targets, `self_only`), `duration` on `condition_on_hit` and on a `save_effect` condition, the `crit_or_kill` trigger (the closed form treats it as `crit`, with a note), the properties `magical`, `silvered` and `adamantine` (for qualified resistances), and a simulation validation mode that drops only the "one turn's routine" rule, so a build may hold several Action routines and the simulator chooses per turn.
- **The empirical profile covers both editions** (`mm2014`, `mm2024`) and is a `target.profile` value; `dmg2014` stays the default. An empirical row's AC and save bonus are medians rounded half up (toward the harder target), and the save bonus is the median of each monster's mean save bonus, used for every ability. The table has no expected-damage column: it is not defensible without a policy for saves, areas and riders.
- **Never in a lair** in v1: no lair actions, and the non-lair legendary and Legendary Resistance counts. Every result says so.
- **No `ICombatHook` pipeline.** The engine is one `Fight` class in partial files. A new mechanic is a `StatBlock` member or a DSL kind that `CombatantCompiler` compiles and the engine reads (the fix round added kill thresholds and immunity after a success that way).
- **PC turns are written independently of `TurnEvaluator`**, so the simulator cross-checks the closed form rather than repeating it. It knowingly differs in five places: `optimal` riders are decided myopically over the turn's remaining attacks; power attack `auto` is chosen per turn from an estimate; each turn the Action is the Attack action or an Action save effect, whichever is estimated higher; conditions keep their real durations; Cleave hits a real second front-liner and `crit_or_kill` fires on kills. Reaction attacks are drawn once per round with their trigger probability and made after the next enemy turn. A concentration modifier lost to damage stays lost unless it has a setup cost; then it is set up again when its value over two turns is at least what that action would otherwise deal.
- **Monsters play greedily** by expected damage against the target their side's policy picks. An area counts its damage per creature × the creatures it would catch; a condition adds a share of the target's damage per round (all of it for one that takes its turns, a quarter for one that hampers it, a tenth for exhaustion) × the chance it lands; a kill is worth the target's HP (or the damage, if higher), and a single-target kill the HP of the creature it is aimed at (see Kill thresholds). Legendary actions go to the best value per use after each other creature's turn. Monster reactions are only Parry (one attack; only melee when its text says "melee attack") and Shield (+5 AC until the start of the caster's next turn). No upcasting; no new concentration spell while concentrating; an ally at 25% HP or less is healed when a heal is available; a monster with nothing usable Dodges.
- **Policies:** party targeting `focus_fire` (default) | `spread` | `threat`; enemies `spread` (default) | `focus_fire` | `threat` | `healer_first` | `break_concentration`; Legendary Resistance `conditions` (default: a failed save that would impose a condition or drop the creature to 0 HP) | `always` | `never`; healing `downed` (default) | `below_half` | `never`, with self-only heals (Second Wind) at half HP or less; `finish_downed` (default false); `pcs_win_ties`. The plan's AoE-maximize policy was not built.
- **Rules readings**
  - PC-like creatures (party builds and archetypes, or any entry with `death_saves: true`) make death saves; SRD monsters on either side die at 0 HP.
  - The fight ends when a side has no creature above 0 HP (a dying PC counts as down), also for a regenerating troll at 0 HP. Keeping the fight going while a troll could regenerate made four level-5 fighters lose every fight to the real trolls.
  - Areas catch the DMG's count (no ±1d3) and never an ally: standing creatures first, front line before back line, random within a line, then creatures at 0 HP while the count has room, who take damage by the 0-HP rules. The damage is rolled once for all of them.
  - The fight's `edition` (default: the party's, else 2024) sets surprise, exhaustion, the concentration cap and 2024 Grappled; a creature's own features (Evasion, DSL default durations) follow its own edition. 2014 surprise: the creature skips its first turn and takes no reactions or legendary actions until it ends; 2024: Disadvantage on initiative.
  - A creature restrained with an escape DC always spends its Action trying to escape.
  - Aura timing is read from the trait text ("starts its turn"); Undead Fortitude gets no save against radiant damage or a critical hit; Death Burst hits enemies only.
  - A dead creature's place in the initiative order still comes round for what it imposed: conditions lasting until the start or end of its next turn end there, round and save-ends counts tick, and its Sap ends. Nothing else of a turn happens, and no dice are drawn.
  - **Immunity after a success** ("immune … for 24 hours") lasts the rest of the fight, per source creature, and starts on a successful save (Legendary Resistance included) or when the condition ends. Where the text grants it only on a success, or covers "all mummies", an approximated warning says so. An area that could reach only creatures already immune to it is not used (no use spent, no dice drawn).
  - **Kill thresholds** (Power Word Kill in both editions, the 2014 solar's Slaying Longbow as a save rider after the hit's damage, the 2024 solar's Slaying Bow as a save action): at or below 100 HP the creature dies outright, with no death saves, counted as a death and a kill. A failed save's kill replaces the save's damage (Slaying Bow: "Failure: If the creature has 100 Hit Points or fewer, it dies. It otherwise takes …"); an attack's kill comes on the hit, before damage. A single-target kill is aimed at a standing creature it can kill, the side's policy choosing among those; with none, the policy picks as usual, and a dying PC under `finish_downed` is never the reason to aim it. Legendary Resistance treats a save that would kill as one that matters. The report's assumptions state the aiming when a monster has a single-target save or auto-hit kill (not for the 2014 solar's rider), and the combatant view words each kind this way.
- **Reporting**
  - The fight ends at a party wipe while members may still be dying. Their death saves are not rolled on: "left dying at the end" is reported separately (in the headline, with a caveat, and per combatant) rather than guessed.
  - The Compare section pairs "a party member is left dying" beside the deaths whenever either run left someone dying, with a caveat: a feature that ends fights sooner leaves the dying fewer rounds to fail, so fewer deaths beside more left dying is not lives saved.
  - Per-entry shares (dropped to 0, dead, dying at the end) print no interval, because copies in one fight are not independent; per-entry means treat each fight as one sample. The contract asked for an interval on every figure.
  - Kills count the other side's deaths a creature caused: by damage, an outright kill, or a sixth level of exhaustion. A death from failed death saves (or from regeneration stopped at 0 HP) is credited to no one, and the per-combatant note says so.
  - One markdown result, typical under 8,000 characters and worst case under 24,000 (both pinned; the largest measured was 23,389 at build and 23,764 in a fix-round verifier's 7-versus-14 SRD fight, both with replay and compare). Warnings are condensed (4 per stat block, 6 stat blocks, then "Also warned: …"), resources to 6 per combatant, notes to 8, assumptions to 6,000 characters. A probability prints 0% or 100% only when it is exact.
- **Run controls**
  - Limits: iterations 1–100,000 (default 10,000); round cap 1–100 (20); up to 20 copies per entry and 40 combatants; HP up to 5,000; `precision` 0.001–0.5, in batches of 10,000 up to 100,000 fights. A run whose fights × combatants × round cap (× 2 with `compare`) exceeds 20,000,000 is refused up front with what to reduce (60,000,000 until the fix-up round; see Speed). Ordinary requests pass: 100,000 fights of a 4v3 at round cap 20 is 14 million, and the default 10,000 fights allow 40 combatants to round 50.
  - Precision mode is charged its first batch of 10,000, which always reaches ±1% or wider (the widest 95% half-width at 10,000 fights is ±0.98%). Later batches stop where the next would pass the cap, and the report says the precision was not reached at the work limit (`SimulationReport.PrecisionMaxFights`), so the worst case is the same 20 million.
  - `replay: i` runs the whole simulation, then fight i alone with a log (initiative, every roll with its faces, damage per type after adjustments, conditions, saves, death saves, legendary actions, Legendary Resistance), capped at 20,000 characters with the summary always kept. With `compare` it shows the baseline's fight. In precision mode i may be up to 100,000: fight i is a pure function of the seed and i, so a fight past the ones precision ran is still replayed.
  - With no `seed`, the host draws one from the OS RNG and prints how to pass it back. Seeds are 0 to 2^64 − 1, taken as a number or a decimal string and echoed as a string (JavaScript clients round integers above 2^53).
  - A bare monster name is looked up in its entry's `edition`, else the fight's, else the first party entry's, else 2024. An archetype with no `edition` follows the fight's.
- **Progress, cancellation and threads.** A run uses ProcessorCount − 1 threads, and the host raises the thread-pool minimum to ProcessorCount + 4 once per process, so the stdio reader and the progress sender always get a thread.
  - One call's fights run at a time, process-wide (`FightGate`), so the cap covers every call together: two calls at once, each on ProcessorCount − 1 threads, starved the pool again (a ping took 24.94 s behind two runs). A waiting call reports every second that it is waiting for another simulation, and input the Domain refuses is refused before the wait (`Simulator.Prepare`).
  - Chunks are sized by work (`SimulationLimits.ChunkWork`: 65,536 of the cap's units, at most 1,024 fights, and at least as many chunks as threads). The heaviest runs report within about a second (first after 0.5–0.73 s, then at most 0.45 s apart, where fixed 1,024-fight chunks gave a 600-fight 40-creature run one report at 14–19 s), and a run of a few heavy fights uses every thread. The report does not depend on the chunk size or the thread count (integer tallies).
  - Progress goes out through the request's progress token: the first value at once, then at most every 250 ms, one awaited send at a time, only increasing values; the rules-index wait and the gate wait report through the same pump. Cancellation is checked between fights.
  - A run is cancelled when the client closes stdin (see the SDK corrections); the call then returns "The session ended; the simulation was stopped." and logs it once at information level.
- **`enemies` and `compare` are published untyped** and validated as `party`'s item type (`SameShapeAs`) and as `CompareSpec` (`CheckedAs`): typed, the schema was 31.8 KB of the 32 KB budget; now it is 19,490 characters.
- **Normalizer readings**, each carried as a warning or a note:
  - Spellcasting traits are not listed in Traits; their spells are in `Spells` at their cast level, with slots as pools. A spellcasting action with its own recharge shares it among its spells, and "casts X twice … can replace one X with Y or Z" becomes `use_actions` routines (the 2024 pit fiend).
  - Rejuvenation-style traits are `no_combat_effect` (they cannot change one fight).
  - The 2014 violet fungus's "1d4" attacks are 2 (as in 2024) and the hydra has 5 heads (both approximated).
  - Versatile weapons deal their two-handed damage unless the monster carries a shield.
  - Condition-only auras (Stench, Fear Aura) are auras with a condition and no damage.
  - "X or Y" legendary actions become one entry each; the 2024 sphinx's Roar becomes its three roars, once each; capped multiattack options ("only one of which can be a bite") expand into exact routines.
  - A swallow is blinded + restrained + the acid each turn; two-stage petrification models its first stage (restrained); lingering spell zones deal their damage once.
  - The rakshasa's conditional vulnerability (an `other` qualifier) always applies, and the 2014 archmage's "damage from spells" resistance does not (both warned).
  - Legendary uses are 3, and 4 in a lair exactly for the 29 2024 monsters with in-lair XP (inferred from the XP; no override was needed).
  - "It drops to 0 Hit Points" (the 2024 sea hag's Death Glare) is not dying: the damage is dealt and the branch is warned. Per-turn damage with no condition to carry it (the 2014 water elemental's Whelm, the 2024 horned devil's infernal wound, Burning) is not_modelled, with its dice named.
  - A warning about a conditional run quotes its triggering sentence whole, up to 240 characters; a longer one keeps about its first 100 and last 120, because a conditional sentence names its effect last.
- **Targets from stat blocks** (`balance_dpr`, `balance_compare`): a bare name is looked up in the build's edition (the baseline's for `balance_compare`), else 2024; a ref is used as given, with a note. Explicit target fields override the stat block's, with a note, and a given list replaces the stat block's whole list. `monster` with `cr` or `profile` is refused. An `other` qualifier always applies. A condition the target is immune to is never attempted (no save, no use spent); a save effect's condition never lands but its damage counts. `TargetSpec` has no `condition_immunities` field (schema budget).
- **Nick** is noted by one rule and one sentence in both `balance_dpr` and `balance_simulate` (`ResolvedBuild.UnmodelledNickNote`): when the build has a Nick-mastery attack and a `bonus_action` weapon attack that is offhand, Light or the Nick weapon, unless the Attack action already holds the Light extra attack (an offhand action attack other than the Nick weapon, or two Light weapon attacks in the action, as in the 2024 rogue archetype). The rules do not say which of the two Light weapons must carry Nick, so a Nick dagger in the action beside a `bonus_action` offhand shortsword is noted, naming both ("Shortsword: Nick (on Dagger) changes only the action economy; …"). A `bonus_action` Light attack beside a build that has modelled Nick is read as another source's (the Dual Wielder feat's) and not noted.

**Corrections found while implementing**
- **Data**
  - The 2024 Ancient Copper Dragon had a fourth legendary action, "Spike (level 5 version)", a fragment split off Mind Jolt; the 2014 violet fungus listed the condition immunity "blinded" twice and dropped "deafened". Both are now in `srd-corrections.json` (308 entries: 257 in 2024, 51 in 2014). `SrdIndexSchema.Version` did not change: the corrections' sha256 is already in the staleness key, and srd.db stores no stat blocks.
  - The five mislabeled 2014 half-saves were found again, independently, by the normalizer's `data_conflict` check.
- **Plan vs practice**
  - PLAN's seed `SplitMix64(master ⊕ i·φ)` must be one SplitMix64 output handed to the generator. Handing master ⊕ i·φ straight to `Xoshiro256StarStar` (which seeds itself from a SplitMix64 stream) gives fights i and i+1 three of their four state words in common; the agreement battery caught round 1 running 2–3 SE low.
  - The 2024 High budget for four level-15 characters is 31,200, not the 52,000 the build assumed when a 0% win looked like a bug.
- **SDK (ModelContextProtocol 2.2.0)**
  - Reports through an injected `IProgress<ProgressNotificationValue>` are sent fire-and-forget, so they can arrive out of order (1,024 after 8,736; 3,976 after 5,000), which breaks MCP's rule that progress increases. An injected `RequestContext<CallToolRequestParams>` (bound by the SDK and kept out of the schema) gives the progress token; awaiting `NotifyProgressAsync` one send at a time keeps the order.
  - `Parallel.For` with the default degree of parallelism takes every pool thread, and the stdio reader then cannot read `notifications/cancelled`: a cancel was read 12.6 s late and progress stopped. A thread cap alone and `SetMinThreads` alone each failed the tests; both together pass.
  - The request's token is not cancelled when the client disconnects (stdin EOF), and the SDK's shutdown waits for running requests, so an abandoned run kept 15 cores busy for 30 s before the process could exit. The SDK registers the session's `ITransport` as a DI singleton, and its `MessageReader.Completion` completes on stdin EOF once every message is off the queue, without waiting for requests still running; `SimulateTools` takes the transport and cancels the run then. Stopped that way, the call's `OperationCanceledException` was logged by the SDK as a failed tool call with a stack trace (the request's own token was never cancelled), so the call catches it and returns a short text instead. Without a transport (a future HTTP host) only the request's token stops a run.
- **Host bug (predates Phase 5):** in `SrdIndexService.GetIndexAsync`, an index build finishing at the moment a wait timed out let a `TimeoutException` escape as the SDK's generic error; the stdout-purity session hit it. Fixed; it is a race, so no deterministic test.
- **Tooling**
  - "Internal CLR error (0x80131506)" kept recurring: it aborted `dotnet build` several times, made the solution-level `dotnet test` crash (run the two projects separately), and killed the built server mid-run in about 2 of 75 heavy stdio test runs. It struck again in the fix-up round (a build in the normalizer's copy, two `--no-incremental` rebuilds in the merge, and one run of `SimulateToolTests.CallTool_CancelledMidRun_TheFightsStop`, which passed twice alone). Rerun before suspecting the code.
  - These tests fail under load and pass alone: `SimulationStatisticsTests.Speed_Typical4v3Times10000_TakesUnderTwoSeconds`, both `WalConcurrencyTests` busy-timeout tests, `SrdIndexServiceTests.GetIndexAsync_TimeoutShorterThanTheProgressInterval_GivesUpAtTheTimeout`, and (before the progress fix) `SimulateToolTests.CallTool_Progress_IsReportedWhileFightingAndAlwaysIncreases`.

**Known gaps left open in Phase 5** (recorded so they aren't lost)
- The DPR engine still does not spend a target's Legendary Resistance in the fight chain (Phase 4 fix, partly done); the simulator does.
- Not modelled: monster upcasting; monster reactions other than Parry and Shield; movement traits (Charge, Pounce, Trampling Charge, Rampage, Aggressive); Burning and other ongoing damage with no condition to carry it (warned); 2014 exhaustion levels 2, 4 and 5; the 2014 water elemental's Whelm grapple (warned; its "If it is Large or smaller" reads as conditional, and the 2024 Whelm is modelled).
- Lair actions, `LegendaryActions.UsesInLair` (4 for the 29 2024 monsters with in-lair XP) and in-lair Legendary Resistance never apply: v1 has no lair fights.
- Party archetypes are simplified: casters have no Shield or Counterspell, rogues no Uncanny Dodge or Cunning Action, barbarians no Reckless Attack, and no one upcasts.
- The combatant view does not say which of a condition's effects the engine applies (for Paralyzed: Advantage against it, a critical hit within 5 ft), so a model cannot confirm them from the view (the lich headless check).
- `rules_get` does not resolve a split-form name the way `encounter_difficulty`, `balance_simulate` and `target.monster` do through `SrdMonsterLookup`: "Werewolf" (2014) is refused with its three form refs listed (the werewolf headless check). This predates Phase 5.
- In precision mode a replayed fight past the fights precision ran is shown, but the result does not say that it lies outside the fights counted.
- The Nick note misses one shape: Extra Attack split between two Light weapons beside a `bonus_action` Light extra attack reads as Nick already modelled and gets no note (documented in `ResolvedBuild`; the note changes no number).

**Phase 4 — done.** The exit criterion is met: `balance_dpr` and `balance_compare` reproduce every worked example, through the Domain and through the tools. The phase was committed as 0b09153 "phase 4" before its review finished; the review's fixes were made in Phase 5 (below).
- **Goldens** (re-derived with exact arithmetic before the build; `phase4-contract.md` §8): the crit floor (1.2); the 2014 L5 fighter 19.611625 (GWF only 253/15, the power-attack switch at AC 17, the full AC 13–19 columns); the 2024 L5 fighter 24.036 (24.226125 with `hew_gets_pb`); Savage Attacker; the GWF and Savage Attacker per-die tables; Vex 0.841424; the four smite policies (8.505 / 6.885 / 1.755 / 12.6) and optimal's exact λ boundaries; Fireball against 4 goblins (raw 89.2, effective 27.998608, P(all 4) 0.999333059 against 0.998775 for independent rolls); Legendary Resistance 6.6667 casts; the advantage calibration; the warlock baseline and RPGBOT curves for levels 1–20; Action Surge on the day horizon.
- **An independent Python oracle** (`DndMcp.Tests/Dpr/Fixtures/oracle/`: exact fractions, explicit outcome trees, exhaustive decision search) computes 261 cases, and the C# engine matches every one to 1e-9 (DPR) or 1e-12 (probabilities, PMFs). Its README lists 29 readings the contract left open.
- Not published at the time: the installed server stayed 0.3.0 until Phase 5's 0.5.0.

**Tests: 8,655 passing** (6,425 unit + 2,230 integration) at commit 0b09153, 0 build warnings.
- Built by six agents from one contract: probability, DSL and oracle; then the turn engine; then horizons and comparison; then the host.
- **Review.** Four lenses were started, each finding checked by an adversarial verifier: maths, rules fidelity, conventions + mutation, and model usability with headless sessions. The session ended while they ran, and "phase 4" was committed then. The rules and usability lenses finished (23 findings: 20 verified, 1 refuted, 2 left unverified); the maths and mutation lenses did not. Phase 5's closed-form agreement battery is an independent check of the maths the unfinished lens would have covered.

**What exists now**
- **Domain/Probability:** `D20` (8 exact face tables: normal, advantage, disadvantage and Elven Accuracy, each with and without Lucky; tails by one integer division), `AttackRoll.Odds` (the crit floor; bonus dice summed as Crit + Σ P(b)·(P(hit at b) − Crit)), `SavingThrow` (no natural 1 or 20; advantage as the average of squares; `ExpectedCastsToLand` = (L + 1)/F), `BonusDice.Parse`.
- **Domain/Features (the DSL):** spec classes that are the tool schema (`BuildSpec`, `AttackSpec`, `ModifierSpec`, `TargetSpec`, `FeatureSpec`, `RulingsSpec`); wire constants with forgiving matching (`DslValues`, `DslValueSet`); step values (`LevelValue`); `DamageFormula`; two-stage validation (FluentValidation for structure, then per-level rules) with one message of up to five problems; `BuildResolver` → `ResolvedBuild` (everything level-dependent made concrete; what the engine decides stays as typed lists); `FeatureMerge`; `TargetResolver` (the DMG 2014 row for CR = level, and the typical save bonus from The Finished Book); the `warlock_baseline` preset.
- **Domain/Dpr:** `TurnPlan` (attack lines per kind: Action, BonusAction, Surge, Hew, Cleave, Reaction); `TurnEvaluator` (a memoised backward expected-value pass that makes every decision, then a distribution pass that follows them); `DprEvaluation` (advantage-rate samples, power attack `auto`, fights as a Markov chain over carried state); `DamageModel` / `DamageAdjustment` / `SaveEffectDamage`; `HorizonEvaluator` (round1, fight, day); `DprAnalysis` (levels, AC grid, marks); `DprComparison` (Δ, level-equivalents, bands, collisions, signature effects); `ReferenceCurves`; `DprTables`. One `WorkMeter` of 2e8 units per call; at most 100,000 states per evaluator.
- **Host:** `balance_dpr` and `balance_compare` (`BalanceTools`; `variant` published untyped and validated as `baseline` through `SameShapeAsAttribute`, which kept the schema at 26.8 KB instead of 38 KB); `BalanceDprMarkdown`, `BalanceCompareMarkdown`, `BalanceMarkdownText` (decimal rounding, so exact goldens print as their fractions do: 8.51 / 6.89 / 1.76); three tables (`rules://tables/dpr-targets-by-level`, `gwf-expected-values`, `aoe-targets`); "Not SRD text" attribution entries (DMG p. 249, RPGBOT, Form of Dread, The Finished Book); rewritten server instructions; version 0.4.0 (never published).

**Decisions made while implementing** (the plan is silent or differs)
- The DSL gains step values (`{"1": 1, "5": 2}`), `from_level` / `until_level`, `cantrip` (dice | beams), `offhand`, `setup`, and fields instead of kinds for `resource`, `concentration` and `action_cost`; the kinds `lucky`, `elven_accuracy` and `ignore_cover` are added. `resource` is taken only by kinds spent per use (riders, extra attacks, save effects, conditions on hit) and the defensive kinds. The `sap`, `nick`, `push` and `slow` masteries report notes only.
- Per-level rules beyond the contract: at most one Action save effect; one Bonus Action and one Action setup; GWF may not reach one attack twice; at most 3 advantage sources with a rate below 1, and 3 power attacks; an extra attack's attack must be active wherever it is; Dueling is not applied while the build makes an offhand attack.
- Readings that change numbers, each recorded in the oracle README: fight decisions are per turn, not backward induction over the whole fight; Cleave's second-target rate is drawn once per turn, at the first eligible hit; round 1 includes the round's reaction attack; Unconscious implies Prone.
- An `extra_damage` rider or flat `bonus_damage` with no `type` deals the attack's damage type (RAW 2024 Sneak Attack: "the same as the weapon's type"); save effects without a type stay typeless. At commit 0b09153 untyped riders were typeless; the Phase 5 fixes changed that.
- An every-hit rider with a Bonus Action cost and no resource is optional (at most once per turn). Save effects with a resource are cast only while uses remain; `action_cost: none` effects are cast every turn.
- Level-equivalents: the band is read from the printed two-significant-figure value. The RPGBOT slope is the fallback when the baseline does not scale, when a tier's slope is below 0.05, or when the baseline cannot be resolved at a tier edge.
- `target: {monster}` waited for the Phase 5 normalizer, and `balance_simulate` and the sim-only effects for Phase 5 (all done there).

**Corrections found while implementing**
- **Two plan values were wrong** (contract §9). Savage Attacker's +0.825 holds only with the `savage_attacker_on_crit_dice` ruling; the default is **+0.799**. Action Surge amortises the exact marginal turn (4 attacks against 2, with the larger chance of a crit-triggered bonus attack): **+3.25 / +2.44**, not research's +3.1 / +2.3.
- **Two numbers in the review verifiers' fix guidance were wrong**; hand arithmetic and the oracle agree on the values now pinned. The prone-on-save turn is 59.840625 (12.895875 + 46.94475), not 58.261875. "Evasion while stunned equals no Evasion" is false (the attack still misses on a natural 1, 5%); the exact differences are pinned instead (−0.7025 in 2024, −14.24 in 2014).

**Phase 4 review fixes** (made in Phase 5 by agent F, following each verifier's fix guidance). All 22 fix-list entries (every finding but the refuted one) are done, 20 fully and 2 partly, and each fix has a test that failed before it.
- **Rules**
  - An untyped rider takes the attack's damage type, in both engine sites, the warnings and the echo ("(the attack's damage type)").
  - Archery and Dueling apply to thrown weapons: a thrown weapon is a melee weapon. Dueling on a thrown javelin adds its +2 (flat damage 3 → 5; integration goldens 10.10 and 12.70).
  - "Part of the Attack action" means a weapon attack made with the Action or Action Surge (`ResolvedAttack.IsPartOfAttackAction`). An offhand attack needs the Attack action that turn: gated, with a note, for builds that have one.
  - While incapacitated, 2024 Evasion does not work and Dodge ends (engine and oracle).
  - A condition on the main target no longer makes every creature in an area fail its save.
  - In the closed form, imposed conditions still end at the end of the attacker's turn (the simulator carries real durations). Each result now says so per source, with a 2014 sentence for Stunning Strike, and labels the figure "for the rest of a turn".
  - Legendary Resistance (partly): notes, "ignoring Legendary Resistance" labels, `ConditionReport.ExpectedAttemptsToLand` and a compare footnote. The engine still does not spend Legendary Resistance in the fight chain; that is a contract-level change to the engine and the oracle.
- **Usability**
  - The level-equivalent bands called official options Breaking. `balance_compare` now prints an ASI yardstick (`AsiYardstick`: the ASI a feat replaces, on the same slope), and its description says a verdict needs the official option as the baseline. The bands are unchanged. Still open (Open question 7): even net of the ASI, the official GWM reads LE 1.1–1.6.
  - A limited-use feature's fight headline now carries the day Δ and its level-equivalent on a day-horizon slope (`LimitedUsesReport`).
  - Validation errors name the list an item came from (`ItemNames`: the baseline, the feature, or the preset's items) instead of calling everything "variant". Merged-only limits read "the baseline and the feature together have …".
  - New notes: ranged, finesse and spell attacks that default to Str (the default stays); a spell attack adding its ability to damage; a save effect with no resource (`IsCantrip`); a feature's ability scores replacing a stepped baseline; a band that applies to the whole feature.
  - Damage parse errors give a hint per field instead of suggesting forms the parser refuses. The `action_cost: "none"` hint was wrong and is now conditional.
  - Wording: edition-aware spell action labels ("Magic action" in 2024, "Cast a Spell action" in 2014); Eldritch Blast's extra beams are "Cantrip Upgrade" marks, not Extra Attack; a line says the damage already includes riders. The GWF table no longer overstates the dice it covers (partly: its test sits in `BalanceDprToolTests`).
- **Numbers.** No §8 golden moved, and all 261 oracle cases regenerated byte-identical. The oracle gained 31 `fix-*` cases (292 in all; README readings 30–37). Moved: a rogue's untyped Sneak Attack with a piercing rapier against piercing resistance, 10.06 → 6.39 (exactly 6.3875); Dueling on a thrown javelin, as above; pinned run counts, +1 each for the ASI yardstick run.
- Tests in F's copy: 6,795 unit (6,794 passed, 1 skipped) + 2,255 integration. After the fixes, `balance_dpr`'s description was 2,040 of 2,048 characters and `balance_compare`'s input schema 30,167 of 32,000.

**Phase 3 — done.** The exit criterion is met: the table parity tests pass, and so do the worked examples.
- **Parity, read as text.** The 2024 XP Budget per Character equals the SRD 5.2 markdown table cell for cell, and XP and proficiency bonus by CR equal both SRD markdown tables (the SRD 5.1 one where it has rows). The tests read verbatim excerpts committed under `DndMcp.Tests/Encounters/Fixtures/`, and `SrdTableFixtureTests` re-checks each excerpt byte for byte against serving-solid-characters when it is checked out beside this repo.
- **2014 tables** (DMG-only) are pinned as independently typed columns, plus the two dndR errors: level 3 Deadly is 400, and only levels 1, 2 and 4 fit "Easy × 2 / × 3 / × 4". The 15+ multiplier is × 5 / × 4 / × 3. CR→XP includes CR 9–13 and 26–30.
- **Worked examples**: SRD 5.2.1's three examples (six encounters) through the Domain and through the tool; the DMG's multiplier example (four monsters, 500 XP → 1,000); PLAN's "3 ogres vs four level-5s" (2014 Medium at 2,700 adjusted XP; 2024 Low).
- **Data truth.** Every monster in srd.db, both editions, has the XP its CR is worth, and every 2024 in-lair XP is the next CR's (29 monsters). Five upstream records did not, and are now corrected (see Corrections below).
- Published as **0.3.0** and installed at `~/.local/share/dnd-mcp/bin` (binary and `content/` replaced by rename; `DndMcp srd-build` passes; `claude mcp list` shows ✔ Connected). The published single-file binary passes the stdout-purity test.
- Manual end-to-end check: two headless `claude -p` runs on the installed server, both correct, the tools found from the server instructions alone.
  - "Is 3 ogres Deadly for four level-5s in 2014, and what is it in 2024?": 2014 Medium (2,700 adjusted XP), 2024 Low (1,350 of a 2,000 budget), in 3 turns.
  - 2014 thresholds for a level 7 character, then a beholder and 4 goblins against four level 10s in 2024: the table by ref; the beholder refused by name, then given by CR from memory (the model said so); High at 10,200 XP with the powerful-creature warning.

**Tests: 4,767 passing** (2,761 unit + 2,006 integration), 0 build warnings.
- Reviewed through two independent lenses as in Phases 0–1, each in its own copy of the repo.
  - **Correctness/data truth:** re-checked every table cell, the five corrections, and all 675 monsters' CR, XP and in-lair XP against the SRD markdown (no other errors). It agreed with the 2024 classification reading. It found 1 bug (two CR-only items with the same label shared a stat-block key, hiding the troubleshooting warnings) and 6 misleading texts: "same XP total" beside differing totals; no mention that the editions classify in opposite directions; lair with cr asking for a CR that does not exist; "Vampire" and the lycanthropes reported as not in the SRD; not-found hints ignoring the other edition; 2024 CR 0 shown as a flat 10. Plus nits. All fixed and pinned.
  - **Conventions + mutation:** 126 mutants, 73 killed by the first tests, 53 survived. The review's killing tests (merged into the existing classes) kill 46; 5 are equivalent and 2 lived in dead code (`AlsoNamed`, removed). Convention fixes: one validation path and one vocabulary ("monsters item 2") for the tool, the Domain and the guard; the formatter no longer depends on the Tools layer; text derived from constants; one level-bounds constant; stale guard docs; the instructions' "Gameplay Toolbox not in the data" now excepts the encounter budget. One mutant that survived the fixes themselves (a label-only stat-block key, masked by judging each group's highest CR) got its own killing test.

**What exists now**
- **Domain/Encounters**
  - `ChallengeRating`: the 34 CRs as eighths; parses "1/8", 0.125, "½", "CR 1/8"; refuses anything between rows rather than rounding onto one.
  - `ChallengeRatingTables`: XP by CR (CR 0 = 10 by default, "0 or 10" by stat block), PB by CR, and the DMG 2014 Monster Statistics by Challenge Rating (every row; CR 0's AC, attack and DC are ceilings).
  - `EncounterTables2014`: thresholds, the multiplier ladder with the party-size shift, Adventuring Day XP. `EncounterTables2024`: the budget.
  - `Encounter2014` / `Encounter2024`: the two methods, plus `Encounter2024.Troubleshoot` (the SRD's troubleshooting advice). `EncounterLimits` holds the input checks, `EffectiveParty` the offset.
- **Repository:** `SrdMonsterChallenge.Read(SrdDocument)` reads CR, XP and in-lair XP from srd.db (corrections applied), never from the vendored files.
- **Host**
  - `encounter_difficulty{party, monsters[{ref | name | cr (+ name as label), count, exclude, lair}], edition, effective_level_offset}` (`EncounterTools`, rendered by `EncounterMarkdown`).
  - `rules://tables/{cr-xp, xp-budget-2024, xp-thresholds-2014, encounter-multipliers-2014, adventuring-day-xp-2014, monster-stats-by-cr-2014}`: six static resources (`RulesTableResources`, from the `RulesTables` catalog), rendered from the Domain tables, each naming its source. `rules_get` serves them by URI, lists them for `rules://tables`, and finds them by name ("XP Budget per Character").
  - `rules://attribution` gains a "Not SRD text" section naming the four DMG tables.
  - `SrdIndexService.QueryAsync`: the reopen-once retry, moved from `RulesTools` so every index-reading tool shares it.
  - `ToolArgumentGuard` now checks inside object arguments and object items: unknown fields (refused, because System.Text.Json would silently ignore `"qty": 3`), field types, integer ranges of fields and of array items, recursively, capped at five problems per argument; then a test-deserialize backstop that names the failing field from `JsonException.Path`.

**Decisions made while implementing** (the plan is silent or differs)
- **2024 classification is "the lowest difficulty whose budget the XP fits"**, not the plan's "highest band ≤ total". The SRD only describes building to a budget ("spend as much of your XP budget as you can without going over"), and its own worked examples call 150 XP against a 200 budget Low and 1,100 XP (Low 750, Moderate 1,125) Moderate; the plan's reading calls both one band lower and leaves everything under the Low budget unnamed. Above High the label is "Beyond High", flagged as not an SRD term. The output states the reading.
- **2014 exclusion** (`exclude: true`): the monster leaves the count that picks the multiplier; its XP still counts (the cautious reading of "don't count any monsters whose challenge rating is significantly below…"). Never automatic. When CRs differ and nothing is excluded, the 2014 section mentions the rule.
- **2014 below Easy is "Trivial"**, flagged as not a DMG term. The 2014 section also gives the adventuring-day share (adjusted XP ÷ the party's Adventuring Day XP) and the XP earned; the multiplier never changes the award.
- **Mixed levels** sum each character's thresholds or budget in both editions (the DMG does; the 2024 SRD multiplies one party level, which is the same for a one-level party), flagged for 2024.
- **Troubleshooting** is judged at printed levels (an offset does not raise a level-1 character's hit points). Limits: more than 2 creatures per character; CR above a character's level (fractional CRs compared as values); more than 2 CR 0 creatures, or any worth 0 XP; more than 3 stat blocks (two lines with one ref count once). "Adjustments" and "Unusual Features" need judgement and are not checked.
- **Monsters** (messages count them as "monsters item N", like the argument guard): `ref` (a ref; one without a slash is read as a name), `name` (looked up in both editions), or `cr` (any monster the SRD lacks, with `name` as its label). One edition uses a ref as given, even the other edition's (a 2014 stat block in a 2024 game); `both` pairs each side with its recorded counterpart, then falls back to the same stat block with a note. A name missing from the asked edition uses the other edition's counterpart or stat block, with a note. `lair` uses the 2024 in-lair XP; 2014 stat blocks have none (said in a note); with `cr` it is the next CR's XP in 2024 (as every SRD 5.2.1 in-lair XP is), the CR unchanged. `cr` is untyped in the schema so "1/2" and 0.5 both work; a number is read by its JSON text, so 1e1 is refused like "1e1".
- **Split stat blocks**: a name the data splits into forms ("Vampire" is "Vampire, Vampire Form", "…, Bat Form", "…, Mist Form"; each lycanthrope likewise) resolves to the form named after the creature, else the first, when every form shares CR and XP (they all do), with a note.
- **2024 CR 0**: 26 of the 28 SRD 5.2.1 CR 0 stat blocks print "XP 0 or 10" and upstream stores 10; the output says so for those.
- **Not found**: the error lists close SRD names from both editions (resolution falls back to the other one) and says to pass one's ref, before offering the CR route, so a model does not guess a CR for a monster the SRD has. A ref missing from its edition names the same slug in the other edition.
- **Output wording**: every label comes with the numbers either side of it; percentages round down ("under 1%" for a sliver), and an uneven share of XP says "about N each". "The editions compared" computes the band equalities for the party's levels, states the opposite classification directions, and names the monsters whose XP differs between editions.
- `effective_level_offset` is a whole number from −10 to +10; each character's effective level is held to 1–20 and the output says how many were.
- `party` is levels only: `"campaign"` waits for Phase 6, and `edition` defaults to 2024 until then (as for the rules tools).
- **Tables are static resources**, one per table, not a `rules://tables/{name}` template: a template moves to resources/templates/list, where Claude Code may not offer it (`ServerSurfaceTests` still pins that there are no templates). The DMG's Monster Statistics by CR is included now (Phase 4 needs it for targets); GWF expected values and AoE target counts wait for Phase 4's maths.
- Table names are checked before the SRD index, so no table name may equal an SRD name; `RulesTablesTests` pins that none does, in either edition.
- No FluentValidation yet: the inputs are simple and the checks throw `DndInputException` directly, as in Phases 1–2. Phase 4's build DSL is where validators arrive.

**Corrections found while implementing**
- **Five upstream XP values contradicted their stat blocks**, now corrected in `srd-corrections.json` (306 entries: 256 in 2024, 50 in 2014): 2014 Brass Dragon Wyrmling 100 → 200, Deep Gnome 50 → 100, Dretch 25 → 50, Riding Horse 25 → 50; 2024 Archmage 8,000 → 8,400. Every other monster's CR and XP was checked against its SRD markdown stat block (the shape-changer forms against their shared block, the 2014 insect swarms against Swarm of Insects).
- CR 0 is "0 or 10" XP. Four stat blocks are worth 0 (2014 Frog and Sea Horse, 2024 Seahorse and Shrieker Fungus); the SRD 5.2 markdown prints the 2024 Frog as "XP 0 or 10" and upstream gives 10.
- 2024 "XP N, or M in lair" is always the next CR's XP (29 monsters); 2014 records carry no lair XP.
- **Tooling:** `dotnet test DndMcp.sln` and once `dotnet build` aborted with "Internal CLR error (0x80131506)" (a crash in the CLI process, twice while a reviewer's builds ran concurrently); the same command passed on the next run. Check the build log's "Build succeeded" before trusting `--no-build` test results.

**Phase 2 — done.** The exit criterion is met: `rules_search` and `rules_get` work in both editions and with `edition: "both"`, and the import counts match the vendored data.
- srd.db holds **4,602 documents**: 2,415 for 2014 and 2,187 for 2024 (174 of them the Rules Glossary). Every (edition, kind) count is pinned: monsters 334 / 341, spells 319 / 339, feats 1 / 17, rules 137 / 174, levels 290 / 287, and the rest. Every stored record is checked value for value against the vendored JSON (or its correction).
- A cold build takes about 0.6 s and a reopen about 0.1 s. srd.db is 13.8 MB.
- Checked against the published single-file binary: `content/` ships beside it, `DndMcp srd-build` works, and the stdout-purity test passes, including a rules call that builds the index.
- Published as **0.2.0** and installed at `~/.local/share/dnd-mcp/bin` with its `content/`. `claude mcp list` shows ✔ Connected.
- Manual end-to-end check: three headless `claude -p` runs on the installed server, all answered correctly. The model found the tools from the server instructions alone.
  - "Compare the grappled condition in the 2014 and 2024 rules": `rules_get` edition both, then the 2014 Melee Attacks section and 2024 Unarmed Strike.
  - The Vex mastery.
  - The 2024 Oath of Devotion spells: the corrected table, including the tiers upstream's garbled text dropped.

**Tests: 4,307 passing** (2,493 unit + 1,814 integration), 0 build warnings.
- The work went through three review rounds. First, eight independent lenses, each finding checked by an adversarial verifier: 55 findings, 54 confirmed. Then a fix round and a re-verification of every finding (49 fixed, 6 partly), plus 57 new issues, mostly side effects of the first fixes. A third fix round followed.
- The data corrections were audited word by word against the SRD markdown.
- Mutation campaigns ran over the index, tools and formatters, and each surviving mutant got a killing test.

**What exists now**
- **Repository/Srd/Index**
  - `SrdIndexBuilder` verifies the manifest, reads every file `SrdKinds` lists plus the glossary, applies the corrections overlay, and writes STRICT tables plus FTS5. It builds into a temp file and renames it into place. It uses journal_mode DELETE and deletes abandoned temp files after 10 minutes.
  - `SrdIndexContent` computes the staleness key: schema version, manifest fingerprint, glossary sha256 and corrections sha256.
  - `SrdIndexOpener.OpenOrBuild` reuses or rebuilds the index, and retries when another version replaces the file at the same moment.
  - `SrdIndex` answers `Search`, `FindByName`, `Get`, `Counterparts`, `ClassLevels`, `SubclassLevels` and `Counts`. Its 4 read-only connections are opened together and validated up front, so deleting or replacing srd.db mid-session changes nothing for a running server.
  - `SrdCounterparts` pairs the two editions (2,052 pairs):
    - by slug;
    - by unique name (features within their class);
    - graded features ("Wild Shape (CR 1 or below)" with 2024 Wild Shape, lowest level first);
    - subclass levels;
    - 2014 variant magic items with their 2024 parent;
    - 2014 rule sections with the glossary entries they contain (Grappling with Melee Attacks);
    - a manual table, with one comment per pair.
  - `SrdCuratedAliases` holds the 85 2014 subsection headings that name a rule, and 8 extra names ("Shove", "Damage Resistance").
  - Also: `SrdNames` (name keys: NFKC, diacritics, apostrophes, format characters), `SrdKindNames` (plurals, API names, race⇄species), `SrdRefParser` (`kind/slug`, `edition/kind/slug`, API URLs), `SrdSearchText`, and `SrdIndexUnavailableException`, whose messages are written for the user.
- **Repository/Srd**
  - `SrdKinds`: 28 kinds, their API segments and their per-edition files.
  - `SrdCorrections`: the overlay loader. It sets or adds top-level properties, validated both ways.
  - `DndMcpPaths`: cache and data directories, with warnings for ignored overrides.
- **content**
  - `rules-glossary-2024.json`, sha-pinned, with its provenance in a .md file.
  - `srd-corrections.json`: 301 entries (255 for 2024, 46 for 2014). Replacement text is copied verbatim from the SRD 5.2 and 5.1 markdown, and every corrected document says "*Corrected from the upstream data: …*".
- **Host**
  - `rules_search` and `rules_get` (`RulesTools`).
  - `SrdIndexService`: lazy, plus a warm-up hosted service, so the handshake never waits on a build. Tools wait up to 25 s and send progress. It falls back to a private temp directory when the cache can't be written, and it reopens after an unavailable-index error.
  - `rules://attribution`, also reachable through `rules_get`.
  - `DndMcp srd-build [--force]`: exit 0 = ok, 1 = failure, 2 = usage.
  - `DndMcpServerOptions`: content root, cache and data directories.
  - `ToolArgumentGuard` checks array item types.
  - `Formatting/Srd`: the `SrdMarkdown` dispatcher (title, meta line, cap at 32,000 characters, comparison layout with an at-a-glance table for spells and monsters) and per-kind formatters (monster stat blocks, spells, class level tables, subclasses, origins, equipment, magic items, 2014 rule tree, 2024 glossary). `SrdProse` changes whitespace only; `SrdChoiceMarkdown` renders choice trees.

**Decisions made while implementing** (the plan is silent or differs)
- `format` is `concise | full`. `combatant` waits for the Phase 5 normalizer, because a value that does nothing would mislead.
- `edition` defaults to 2024 until Phase 6 supplies the active campaign's ruleset.
- **Refs** are `{edition}/{kind}/{slug}` (`2024/spell/fireball`). Results always print them with the edition, so a copied ref fetches exactly that record.
- **Level records** are stored and reachable by ref, and they feed the class tables. They are not full-text searchable: 577 "Fighter N" rows would bury real results.
- **Search**
  - Words are ANDed; a trailing `*` is a prefix; every word is quoted, so user text never becomes FTS syntax.
  - If no document has every word, search matches any word and says so.
  - Exact name and alias matches come first, then bm25 with weights 10 / 5 / 1. Loose forms ("lich stat block", "Healing Word spell", plurals) count as exact.
  - Inputs are capped (query length, distinct words) with actionable messages.
- **Name matching tiers**, used by lookup, by search's exact tier and by the ambiguity footer:
  - own name in a content kind;
  - alias in a content kind;
  - own name in a reference kind (language, proficiency, skill, …);
  - alias in a reference kind;
  - heading or section alias.
  - Within a tier: kind priority, then lower feature level, then slug.
  - So 2024 "Goblin" gives the Goblin Warrior monster, not the language. "Acolyte" gives the background, not the Priest Acolyte monster. "Bardic Inspiration" gives the level-1 grade.
- **Edition "both" never claims something does not exist.** The other side comes from recorded counterparts, preferring one that answers to the typed name. Failing that it tries the same kind and slug, then the same name ("matched by name only"). Only then does it print a hedged "no entry matched … try rules_search".
- **Aliases are typed** (`qualifier`, `counterpart`, `heading`, `section`, `curated`), and the meta line shows each the right way:
  - "2014 name: Thug", not "also known as";
  - "Covers: Grappling; Opportunity Attacks; …".
  - A lookup that lands through a heading says the rule is covered by that subsection.
- **Upstream data is not trusted blindly.** Provably wrong records are fixed through the corrections overlay, never in the vendored files. Anything left as upstream wrote it is listed in `srd-corrections.md` and pinned by `SrdDataQualityTests`. Examples of what was found:
  - 2024 potions had their text shifted between records (Potion of Heroism carried Gaseous Form's effect);
  - about 30 flattened tables;
  - 2024 category lists contradicted their items (Hide Armor filed under Light);
  - the Oath of Devotion spell table was garbled;
  - 2014 spells were machine back-translated (Hold Monster, Dominate Beast's upcast rule);
  - 2014 to-hit bonuses contradicted their own stat blocks (Kraken, Purple Worm and others);
  - the Mule carried the Octopus's actions;
  - Pirate Captain and Unicorn were missing their bonus actions.
- **Paths**
  - `DND_MCP_CACHE_DIR` and `DND_MCP_DATA_DIR` must be absolute or start with `~/`. MCP configs are JSON and expand nothing, so other relative values are ignored with a logged warning.
  - The host's content root is the executable's directory, so a project's appsettings.json is never loaded.
- **Refusals the brief did not spell out:** `kinds: ["level"]` in search, a conflicting ref edition, and `kind` with a ref of another kind. Each gets a message with the fix.

**Corrections found while implementing**
- **SDK (ModelContextProtocol 2.2.0):** building the same tool on two threads at once can leave the injected `IProgress<ProgressNotificationValue>` parameter in the input schema as a phantom `progress` argument. Production builds one server on one thread. `McpServerHarness` serialises server construction, and `ServerSurfaceTests` fails if the lock is removed.
- **SQLite:** an unwritable directory raises SQLITE_CANTOPEN, not an IOException. `File.GetLastWriteTimeUtc` on a vanished file returns 1601, not an exception, which made an early temp-file cleanup delete a live build's journal.
- **Data vs plan**
  - The 2024 Rules Glossary is the only 2024 rules text. SRD 5.2.1's core chapters are not in the data: Playing the Game, the Spells chapter's casting rules, Character Creation, the Equipment prose and the Gameplay Toolbox. The server instructions say so, so a model reports "not in this server's data" rather than guessing.
  - Glossary entries that cite those chapters carry a note saying the chapter is not included.

**Phase 2 carry-forwards:** all done. The published binary ships `content/`. `AddDndMcpServer` takes a content root plus cache and data directories, and the harness isolates them. The converter note needed no new converters, because the index stores raw JSON.

**Phase 1 — done.** The exit criterion is met: `dice_roll` and `dice_odds` pass every golden value above, as exact fractions where one exists.
- 4d6kh3 has mean 15869/1296 and P(18) = 7/432.
- 2d20kh1 is 553/40 and 3d20kh1 is 1239/80.
- P(8d6 ≥ 30) = 638543/1679616.
- An exploding d6 has mean 4.2, or 49/12 at depth cap 1.
- Every PMF sums to 1 within 1e-12.
- The χ² tests on 10⁶ rolls are marked `Category=Slow`.

All of these were re-derived by brute force in Python for this phase.

**Tests: 1297 passing** (1105 unit + 192 integration).
- The work was reviewed through the same two lenses as Phase 0: correctness, and conventions with 61 mutation probes.
- The correctness review found one real semantic bug and eight smaller ones; all are fixed and pinned. The bug: keep/drop on a `!`/`!p` pool was measured against the dice typed, not the exploded pool.
- The mutation review left 19 real survivors. Each now has a test that kills it, and a re-run confirmed 9 of them including the fixes.

**What exists now**
- **Domain/Dice**
  - `DiceExpression` is the parser. It covers the full grammar plus labels on constants, a leading unary sign, and `!N` meaning `!=N`. Static bounds are checked against ±10^12, so all runtime arithmetic is plain `long`.
  - `DiceEvaluator` rolls with a record of every face. Its lean mode is also the Monte Carlo sampler, so an estimate and a roll cannot disagree about meaning.
  - `Pmf<T>` is generic over `BigInteger` (exact fractions) and `double` (normalised).
  - `KeepDistribution` is the keep-highest DP. It uses unconditional counts for `BigInteger` and conditional binomials for `double`, which stay stable at 1000 dice.
  - `DistributionCompiler` builds explosion chains to the full cap and applies reroll weights.
  - `DiceDistribution` tries exact → floating point → seeded Monte Carlo, each under a `WorkMeter` budget with a support cap. `DiceOdds` carries the result.
  - Also here: `Fraction`, and `DiceCaps`/`DiceLimits`.
- **Domain/Rng:** `Xoshiro256StarStar` (with Lemire bounded draws) and `SplitMix64`, each pinned to reference vectors, ready for Phase 5.
- **Rollers:** `SeededDiceRoller` is new. `CryptoDiceRoller` now uses `GetInt32(sides) + 1`.
- **Host**
  - `dice_roll{expression, times, label, seed}` and the new `dice_odds{expression}`, rendered by `Formatting/DiceRollMarkdown` and `DiceOddsMarkdown`.
  - `ToolArgumentGuard` now calls an integer beyond its CLR type (even beyond decimal) "too large" rather than "should be integer".
- **Tests**
  - `RollPathEnumerator` runs the real evaluator over every face sequence (with lowered caps). `DiceSemanticsAgreementTests` requires the exact PMF to equal it fraction for fraction for 40+ modifier combinations.
  - The oracle shares the evaluator's reading of the grammar, so semantics such as keep/drop on exploding pools are also pinned by hand-computed scripted rolls.

**Decisions made while implementing** (the plan is silent or differs)
- `dice_roll`'s `secret` parameter and campaign roll logging wait for Phase 6. There is no campaign to log to yet, and a parameter that does nothing would mislead.
- **Per-die order** is roll → reroll → explode → clamp, then keep/drop → count or sum on the pool.
  - `!!` clamps the compounded total.
  - `!`/`!p` clamp each die.
  - `!p` subtracts 1 from every die an explosion adds.
  - Success counting uses the clamped value.
- **Keep/drop on `!`/`!p` pools** acts on the exploded pool. `4d6!dl1` drops one die of however many were rolled, and `2d6!kh3` keeps up to 3. On fixed pools, drops normalise to keeps (4d6dl1 = 4d6kh3).
- **Caps**
  - `r` rerolls physically up to 100 times, then draws directly from the non-matching faces (same distribution, bounded work).
  - Explosions stop at 100 per die in rolling and in both exact paths. `dice_odds` reports the cap's effect when it exceeds 1e-12 (for 1d6!>=2 it is about 1e-8).
  - One `dice_roll` call may make 1,000,000 physical rolls.
- **Refusals the grammar does not spell out**
  - A reroll matching no face, or (for `r`/`ro`) every face.
  - An explosion that no face a die can end on matches, or that every one does. This is judged after `r`, so `1d6r<6!` and `1d6r6!` are refused.
  - `8d6!>=30` is refused with a hint to write `8d6! >= 30`.
  - A sign straight after kh/kl/dh/dl.
  - Labels outside letters, digits, spaces and `'-.`. Labels render in italics, so they never read as arithmetic.
- **`dice_odds` budgets**
  - Exact: 1e6 work units and a 1024-bit total.
  - Floating point: 2e8 units.
  - Distinct totals: at most 2M.
  - Monte Carlo: a 20M-roll budget, 200 to 1M samples, a hard stop at 1.5× the budget (20 samples minimum), and fixed seed 20260926 so the tool is idempotent.
  - Measured in Release, the slowest inputs found take about 2 s: 1000d1000!>=2kh1 and 100d100kh50. Typical questions take well under 100 ms.
  - 100d6! and 50d6!! now go to Monte Carlo, because building chains to the full cap is what keeps ranges and 0% claims truthful.
- **What `dice_odds` prints as certain**
  - A probability prints as 0% or 100% only when no possible total, or every one, meets the condition.
  - For exact results that is judged on the support, which keeps underflowed values (weight 0.0). For Monte Carlo it is judged on the expression's static bounds.
  - Anything else prints as "< 0.0000000001%", "> 99.9999999999%" or "≈ 0%".
- **Output ceilings**
  - `dice_roll` breakdowns share 8,000 characters. A roll summarises a group ("…+980 more = S"), then every group, then omits its dice with a closing note. The total can always be recomputed from what is shown.
  - The worst case measured is under 16,000 characters.
  - `dice_odds` shows a table only when there are at most 60 totals.

**Phase 0 — done.** All three exit criteria are met:
1. **A stub `dice_roll` answers inside Claude Code.** The server is registered at user scope (`claude mcp add --scope user dnd -- ~/.local/share/dnd-mcp/bin/DndMcp`), shows `✔ Connected`, and a headless `claude -p` session called `mcp__dnd__dice_roll` and got real cryptographic rolls back.
2. **Both spikes are green.**
   - **Spike A:** the 5e-database is vendored at `5e-database-v7.0.0` (49 files, 7 MB) through `scripts/fetch-5e-database.sh`. The script cross-checks each file's sha256 between jsDelivr and GitHub raw, stages before replacing, and re-runs byte-identically. Every file for both editions deserializes, and monsters and spells deserialize **strictly** (unmapped fields fail) and round-trip losslessly.
   - **Spike B:** SQLite **3.53.3** (e_sqlite3 from SQLitePCLRaw 2.1.12) has all 12 features the design needs. `SqliteCapabilities.Probe()` / `EnsureSupported()` are ready for host wiring.
3. **The stdout-purity test passes against the installed single-file binary**: `DND_MCP_TEST_HOST_EXE=$HOME/.local/share/dnd-mcp/bin/DndMcp dotnet test DndMcp.IntegrationTests --filter FullyQualifiedName~StdoutPurityTests`.

**Tests: 802 passing** (714 unit + 88 integration).
- Each of the three work items was built in an isolated copy, reviewed independently through two lenses (correctness/data-truth and conventions/test-strength), fixed, then merged.
- The mutation checks recorded in the workflow all failed the intended tests and were reverted. Examples: dropping the `DndInputException` translation, removing the argument guard, adding a `Console.Write`, dropping a model field, a one-byte edit to vendored JSON, a lossy count converter, removing the alias-update trigger, bm25 sorted the wrong way.

**What exists now**
- **Host:** `DndMcpServerRegistration.AddDndMcpServer()` is shared by `Program.cs` (stdio) and the integration harness (in-memory pipes), so the tests run the real registrations.
  - A call-tool filter translates `DndInputException` into `McpException`.
  - `ToolArgumentGuard` validates required arguments, JSON types and integer ranges against the tool's own schema. The model gets an actionable message instead of the SDK's bare "An error occurred invoking '<tool>'.". The guard is held to exactly what the SDK binder accepts, which `ArgumentBindingAgreementTests` pins.
- **Domain:** `DndInputException`, `IDiceRoller` + `CryptoDiceRoller`, and `StubDiceExpression` (to be replaced in Phase 1).
- **Repository:**
  - `Srd/`: manifest + verifier + fingerprint, per-edition `Monster2014/2024` and `Spell2014/2024` models, and the converters.
  - `Sqlite/`: `SqliteCapabilities` and `Fts5Query`.
- **Tests:**
  - `Srd/`: manifest integrity, a per-file count table, strict typed reads, and 26 data-quirk counters × 2 editions pinned so a re-vendor is a deliberate diff.
  - `Sqlite/`: one test per design dependency.
  - `Dice/`.
  - Integration: `ServerSurfaceTests`, `ToolErrorTests`, `DiceToolTests`, `StdoutPurityTests`, `ArgumentBindingAgreementTests`, `UnexpectedExceptionTests`.

### Corrections found while implementing

Everything below is fixed inline above, and each item is pinned by a test.

**Data vs research**
- Multiattack counts are strings everywhere in v7.0.0.
- 20 (not 21) unresolved 2024 multiattack references are spells.
- The 18 2024 "empty damage" attacks have no `damage` key at all.
- 2024 recharge-on-roll is 88 (72 actions + 15 bonus actions + 1 reaction), not 87.
- 2014 `action_options` use only the `action` and `multiple` option types. Breath options live in the separate `options` Choice.
- Zombie Undead Fortitude has a DC with no value.
- Combined multiattacks are typed `actions`.
- Cantrip scaling lives in `description`.

**SQLite design bugs** (the plan and research/04 were wrong)
- The research FTS query leaks secrets on any multi-word search.
- The alias trigger leaks author-only aliases to the party.
- `INSERT OR REPLACE` bypasses the append-only `change_log` triggers.
- A fixed pre-migration backup name blocks every retry.
- `Default Timeout` does not set `busy_timeout`.
- Deferred transactions fail with `BUSY_SNAPSHOT`.
- Foreign keys are ON by default in this SQLite bundle, unlike stock SQLite. Keep setting `Foreign Keys=True` explicitly anyway.

**SDK facts** (v2.2.0)
- `McpServerTool.Metadata[0]` is the tool's `MethodInfo`.
- An unknown tool is a JSON-RPC InvalidParams error ("Unknown tool: '<name>'"), not an `IsError` result.
- The SDK logs a translated `McpException` at **Error** level as "threw an unhandled exception", the same as a real bug. Domain errors and bugs can't be told apart in stderr logs.
- `[AIParameterName]` is experimental (MEAI001). It is suppressed in `DndMcp.csproj` with the reason, and multi-word tool parameters use it for snake_case names.
- Closing stdin makes the host exit 0 in about 20 ms.

### Carry-forward notes for later phases

**Phase 1** (all three done)
- Cap dice output (character budget, pinned by `CallTool_LargestRolls_StayUnderTheOutputCeilingAndRemainCheckable`).
- Sum in `long` (static bounds ≤ 10^12).
- Use `GetInt32(sides) + 1`.

**Phase 4 (from Phase 1)** (all three done: damage PMFs are `Pmf<double>` under one `WorkMeter` of 2e8 units per call; `D20` builds its own exact face tables rather than using `KeepDistribution`)
- Reuse `Pmf<T>` (`Convolve`, `Map`, `Power`) for damage PMFs, and `KeepDistribution` for d20 modes (adv/ea already exist as 2d20kh1 and 3d20kh1).
- A PMF's `Values` is its exact support. A weight of 0.0 means an underflowed double, not an impossible value, so never filter on weight.
- Pass a `WorkMeter` with a budget; `WorkMeter.Unlimited` is for tests.

**Phase 5 (from Phase 1)** (both done: fight i's seed is SplitMix64(master ⊕ i·φ), one hashed output, see the Phase 5 corrections; cancellation is checked between fights)
- `Xoshiro256StarStar` and `SplitMix64` are in `DndMcp.Domain.Rng`. Seed each iteration as the plan says.
- `DiceEvaluator.CreateSampler` is the model for an allocation-free sampling loop. Check the budget and cancellation on every sample, never every Nth: one sample can cost 10^6 rolls.

**Phase 6 (from Phase 1)** (all three done: `dice_roll` takes `secret` and logs to the resolved campaign's live session; its rows store the expression as typed and every face)
- Add `secret` to `dice_roll` and log rolls to the active campaign.
- `DiceRoll` holds every face for the `dice_roll` table.
- Store the expression text, not the in-process enums (`DiceComparison`, `ExplodeKind`, `DiceOddsMethod`).

**Phase 2** (all done; see the Phase 2 status above)
- The publish ships `content/`, `AddDndMcpServer` takes content, cache and data directories, and the harness isolates them.
- The converter note still applies to any future converter. Phase 2 added none: the index stores each record's raw JSON.

**Phase 3 (from Phase 2)** (both done)
- `rules://tables/{name}`: six static resources, each also served by `rules_get` (by URI and by name).
- Monster CR and XP come from srd.db documents (`SrdMonsterChallenge`), which include the corrections.

**Phase 4 (from Phase 3)** (all done: `TargetResolver` reads `ChallengeRatingTables.MonsterStats`, and the typical save bonus from The Finished Book is `TypicalSaveBonus`, not a table; the GWF and AoE tables are `rules://tables/gwf-expected-values` and `aoe-targets`; FluentValidation arrived with the build DSL; the instructions were rewritten)
- `ChallengeRatingTables.MonsterStats` (DMG 2014, every row) is ready for "target the CR = L row" (AC, attack bonus, save DC). The tomedunn save-bonus column is not in it; add it as its own table if used.
- GWF expected values and AoE target counts are still to come as `rules://tables/*`: add a `RulesTables` catalog entry (resources and `rules_get` pick it up) and give it names no SRD entry has (`RulesTablesTests` checks).
- `ToolArgumentGuard` already checks object arguments and nested objects: unknown fields refused, field types, integer ranges, required fields, recursion into nested arrays, and a test-deserialize backstop that names the failing field. The test-only `echo_shape` tool pins those paths. Build objects get all of this; FluentValidation is still needed for semantic rules.
- The server instructions are at 2,002 of 2,048 characters. Adding the balance tools needs a rewrite (the rules_get paragraph is the longest), and `ServerSurfaceTests` pins the limit.

**Phase 6 (from Phase 3)** (done but `party: "campaign"`, which waits for Phase 7's character levels: the edition and the level offset default from the campaign, and the description says so)
- `encounter_difficulty`: accept `party: "campaign"`, default `edition` to the campaign's ruleset, and default `effective_level_offset` from the campaign's balance profile. Update the description, which says 2024 is the default.

**Phase 7 (from Phase 3)**
- `combat add{srd}` can reuse the encounter tool's monster resolution: ref, name, forms, counterparts, the other edition's fallback, and cr for anything else.

**Phase 5 (from Phase 2)** (all done: stat blocks are normalized from srd.db documents through `StatBlockService`; normalization facts live in `content/overrides/` and text fixes in `srd-corrections.json`; `format: "combatant"` is in `SrdMarkdown.Formats`; `SrdIndexSchema.Version` needed no bump, because srd.db stores no stat blocks)
- **Read corrected records.** `srd-corrections.json` is applied when srd.db is built. The typed models (`SrdJson.ReadArray<Monster2024>` …) read the vendored files unchanged, so a normalizer built on them would simulate the uncorrected 2024 Mule (the Octopus's actions) and a Pirate Captain without Captain's Charm. Build combatants from srd.db documents (`SrdDocument.Json`), or run records through `SrdCorrections.Apply` before deserialising. `SrdCorrectedTypedModelTests` pins that every corrected monster and spell still deserialises strictly into its typed model.
- **Two overlays, two jobs.** `srd-corrections.json` restores SRD text. The planned `overrides/monsters.*.json` and `overrides/spells.2024.json` carry normalisation facts: the 5 mislabeled half-damage saves, riders, DC/AoE for 2024 spells. Overrides layer on the corrected record, and a text fix never belongs in an override.
- Add `format: "combatant"` to `rules_get` together with the normalizer. `SrdMarkdown.Formats` and the description list the formats in one place.
- Any change to what srd.db stores bumps `SrdIndexSchema.Version`. `SrdIndexSchemaVersionTests` pins a digest of the hand-written tables to it.

**Phase 6 (from Phase 2)** (both done: the rules tools default to the campaign's ruleset, else 2024, and their schemas advertise `"default": null`; campaigns.db lives in `DataDirectory`)
- The rules tools' `edition` default is fixed at 2024, and `rules_search`'s schema advertises `"default":"2024"`. Switch it to the active campaign's ruleset (D8) and update the descriptions.
- `DndMcpServerOptions.DataDirectory` exists for campaigns.db; `McpServerHarness` points it at the test output.

**Known gaps left open in Phase 2** (low severity, recorded so they aren't lost)
- The 2024 SRD core chapters (Playing the Game, spellcasting rules, Character Creation, Equipment prose, Gameplay Toolbox) are absent from the data. The SRD 5.2 markdown (CC-BY) has them. Importing them as a 2024 rule tree, as 2014's is, would be the biggest remaining quality gain for 2024 rules questions; it is a scope decision for later.
- Some 2014 wording differences that look like PHB errata are left as upstream wrote them, with the list in `srd-corrections.md`. Where the SRD markdown itself is damaged (Animal Friendship's higher level, Compulsion), nothing verbatim exists to copy.

**Phases 2–6** (the first bullet was done in Phase 3; the non-finite rule is applied in Phases 4–5, e.g. `DslProblems` and `precision`, and still applies to Phase 6)
- `ToolArgumentGuard` is shallow. When tools gain object parameters (builds, campaign ops), have it test-deserialize each argument with `McpJson.Options` and report `JsonException.Path`. Otherwise a malformed nested object reaches the model as the generic error.
- Domain maths must reject non-finite inputs, because `1e400` binds as infinity for `double` parameters.

**Phase 6** (all done: the probe runs at startup; `Fts5Query` stays in `Repository/Sqlite`, where the campaign code uses it; `CampaignDatabase`'s static constructor sets Dapper's global, and only `DapperMappingTests` flips it)
- Wire `SqliteCapabilities.Probe()` into startup (log to stderr, then `EnsureSupported()`).
- Move `Fts5Query` under `Repository/Campaign` if that layout is preferred.
- Any test class that flips Dapper's global `MatchNamesWithUnderscores` joins the `DapperGlobalStateCollection`.

**Phase 6 (from Phase 5)** (the first two done: every edition default reads the campaign, and the instructions were rewritten for 15 tools; `session_prep` points at `balance_simulate` for the dangerous fights. The pool and stop-on-close notes still apply: the campaign tools keep blocking work to one SQLite transaction under a 5 s busy timeout, and none runs long enough to need the stop)
- Defaults to switch to the active campaign's ruleset: `balance_simulate`'s `edition` (the party's, else 2024) and its monster lookup's last fallback (2024); `target.monster`'s lookup in `balance_dpr` / `balance_compare` (the build's edition, else 2024). Update the descriptions.
- The server instructions are at 2,024 of 2,048 characters and end "More tools arrive in later builds: campaign tracking."; `balance_dpr`'s description is at 2,042, `balance_simulate`'s and `rules_get`'s at 2,017 (of 2,048). The largest input schemas are `balance_compare` 30,193, `balance_simulate` 19,490 and `balance_dpr` 18,174 (of 32,000). Adding the campaign tools needs another rewrite. `ServerSurfaceTests` pins 8 tools.
- One `balance_simulate` call's fights run at a time, process-wide (`FightGate`, static, so in-memory integration servers share it too), on ProcessorCount − 1 pool threads, and `SimulateTools` raises the pool minimum to ProcessorCount + 4 once per process. A campaign tool that blocks a pool thread (a SQLite `busy_timeout` wait) competes with a running simulation; keep blocking work short, as planned.
- A run stops when the session ends: `SimulateTools` takes the SDK's `ITransport` (a DI singleton the SDK registers for the stdio transport) and watches its `MessageReader.Completion`. A later tool with long work can stop the same way when the client closes stdin.
- `session_prep`'s optional simulation can pass an encounter's monsters straight to `enemies`: both tools resolve monsters through `SrdMonsterLookup`.

**Phase 7 (from Phase 5)**
- `sim_profile` is the DSL `BuildSpec`, the shape `balance_dpr` and `balance_simulate`'s `build` already take. `CombatantSpec` gains the campaign character ref here (Phase 5 left it out). A character without a `sim_profile` can fall back to `PartyArchetypes.Expand(class, level, edition)`.
- `from_state`: every simulated fight starts at full HP (`hp` sets the maximum), fresh resources, no conditions and no concentration. Resuming a live combat needs current HP apart from the maximum (massive damage and healing caps read the maximum), spent resources and slots, active conditions with their remaining durations and sources, and concentration. Add them to `CombatantSpec` rather than overloading `hp`.
- `combat add{srd}`: take AC, HP, hit dice, initiative bonus (2024 initiative comes from `content/overrides/monsters.2024.json`, because the data has none), legendary uses and Legendary Resistance from `StatBlockService.ResolveAsync`, not from the SRD JSON again. Its reminders can use the stat block's parsed parts (recharge, legendary costs, `ImmuneAfterSuccess`, `KillAtOrBelowHp`, conditions with durations), which the combatant view already renders.
- The tracker's death-save and concentration reminders should agree with the simulator: `DeathSaves` and `Fight.ConcentrationDc` are internal to `DndMcp.Domain.Simulation`; make them public (or move them to a shared rules class) rather than re-deriving them.

**Phase 8 (from Phase 5)**
- Export a `sim_profile` as `BuildSpec` wire JSON with canonical spellings. DSL input is matched forgivingly (`DslValueSet.TryMatch` returns the canonical value), so writing back what the user typed would make export → import differ on the second run.

**Phase 7 (from Phase 6)**
- **Schema.** Add `character_sheet`, `encounter`, `combatant`, `combat_log` (and the others PLAN lists) in a new embedded migration `0002_*.sql`. A foreign key to a table that does not exist yet fails every INSERT and DELETE on the child table, even when the column is NULL, so any Phase 6 table that should point at a Phase 7 one gains that column in the same migration that creates the target. `CampaignDbMigrator` takes the timestamped pre-migrate backup and applies it under `BEGIN IMMEDIATE`; a table rebuild needs `foreign_keys` off before `BEGIN`.
- **Writes** go through `CampaignDatabase.Write` (BEGIN IMMEDIATE, the in-process semaphore, the newer-schema refusal) and `ChangeRecorder`, so undo, as_of replay and history see them. HP ticks stay out of change_log as planned; the end-of-combat write-back is one batch (one undo unit), and `UndoEngine`'s conflict rules (a later batch touched the same row or field, or references an id the batch created) then protect it. `combat_log` rows link to `dice_roll` rows: `DiceLogWriter` already writes one row per roll with the expression and faces, filed under the live session; a combat roll should go through it with the encounter's id.
- **Store errors.** The call-tool filter maps a user-fixable SQLite failure to the store-unavailable message only for tools named `campaign` or `campaign_*` (`DndMcpServerRegistration.IsCampaignTool`). A `combat` tool is neither: add it there (or name it `campaign_combat`), or a locked campaigns.db reaches the model as the generic error.
- **Which campaign.** Resolve through `CampaignService.Resolve` (the call's `campaign`, else this process's current one, else the persisted active one, else the only one); `start`/`end` naming a campaign make it current. `encounter_difficulty`'s `party: "campaign"` takes the current party (members whose `PartyMembership.HasLeft` is false: no `until` session and not `former`) and their sheets' levels.
- **Perspectives.** Anything the combat tools show from the campaign (a combatant that is a campaign character, a named NPC's status proposal) goes through `ReadScope` when it can reach a non-author view, so disguised names stay disguised. The DM-facing tracker is the author view.
- **Limits.** The server instructions are at 2,043 of 2,048 characters, `balance_dpr`'s description at 2,040, `balance_simulate`'s at 2,038 and `campaign_write`'s at 2,031; `balance_compare`'s input schema is 31,881 of 32,000 (as `ServerSurfaceTests` measures it, the raw schema text), so adding a campaign character ref to `CombatantSpec`/`BuildSpec` may push it over (`ServerSurfaceTests` pins all of these). The instructions need a rewrite for `campaign_character` and `combat`.

**Phase 8 (from Phase 6)**
- **player_safe export** reads through `ReadScope` with `perspective=party` (every reader does; never re-derive visibility): dated verdicts, the disguised view and its shared aliases, per-view alias visibility, `same_as` hidden for disguised ends, unplayed sessions and not-in-play canon statuses hidden. `PlayerTextChecks`' rules (forbidden words of active gates and rules; names the view does not use) can vet the exported text as a last check.
- **Identity across export and import.** `e:<n>`/`f:<n>` are global AUTOINCREMENT sequences, not stable across databases: match by `id` (UUIDv7), then slug, as planned. Register codes are data (F56a), so an import keeps them; `RegisterCodes` gives the next code from the largest number including struck and deleted rows. `known_as` is one name per knower row; restricted aliases per knower are not modelled.
- **Imports** are one batch through `ChangeRecorder` (actor `import:markdown`), so one undo reverts them. `UndoEngine` looks for the batch's created ids in every later change_log row (a substring test, JSON included): measure it on an import of thousands of rows before relying on undo there.
- **Prompts.** `bootstrap_from_skill` follows the Phase 6 prompt rules: single-token arguments, free text from the conversation, tools named bare, and instructions that drive the tools (dry run first) rather than pasted content. The Belmakor and One Piece fixtures (`DndMcp.Tests/CampaignScenarios`) are ready-made inputs for the skill export's adopt mode.
