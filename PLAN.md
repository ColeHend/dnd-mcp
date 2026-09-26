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
| D3 | Two database files | `srd.db` is disposable and rebuilt from the vendored JSON whenever **any** input changes: the 5e-database manifest fingerprint (`ContentManifest.ComputeFingerprint`), the `rules-glossary-2024.json` hash, or the importer/schema version. `campaigns.db` is precious and backed up. They never share a file. |
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
  ├─ Startup                      SrdIndexBootstrapper (background; tools await readiness), CampaignDbMigrator
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
Overrides:    DND_MCP_DATA_DIR, DND_MCP_DB, XDG_DATA_HOME, XDG_CACHE_HOME
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
│  ├─ rules-glossary-2024.json                      copied from serving-solid-characters/.../data/srd/2024/rules.json
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
| `rules_get` | RO, idem, closed | `{ref \| kind+name, edition, format: concise\|full\|combatant}`. `both` returns a side-by-side 2014/2024 comparison. `combatant` returns the **normalized simulator view plus normalization warnings**, so you can see how the sim reads a stat block. |
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
- The label is the highest band whose budget ≤ total XP.
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
- 2024 classification bands (highest band ≤ total)
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
6. **Do 2024 monsters have lower AC than 2014 (your homebrew-balance skill) or +1 AC (Blog of Holding medians)?** *Lean: default to the DMG 2014 table.* Compute the medians from the 341 vendored 2024 monsters in Phase 5 and let the data decide.

## Side findings in your other projects (out of scope, recorded so they aren't lost)

**`dndMath.ts`: five bugs**
1. The missing crit floor.
2. Lucky with disadvantage.
3. Bless is never wired into DPR.
4. The half-damage shortcut.
5. The dice-parser sign.

**homebrew-balance skill**
- "Advantage ≈ +3.5 on the d20 ≈ +20–25%" understates the gain at the canonical 65% hit rate. There it is **+4.55 on the d20 and about +38% damage**; +20–25% only holds at 75–80% base accuracy.

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

## Implementation status (updated 2026-09-26)

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

**Phase 1**
- **Cap dice output.** `1000d1000` × 100 is about 1M characters, against Claude Code's 25k-token limit. Summarise the face list past a threshold, and add an integration test that pins an output ceiling.
- Sum in `long` or checked arithmetic; the stub needed a length cap to avoid wrapping `int`.
- Replace `GetInt32(1, sides + 1)` with `GetInt32(sides) + 1`, so `sides = int.MaxValue` cannot overflow.

**Phase 2**
- **The published binary must ship `content/`.** Copy it next to the executable, or embed it. Today's publish has no content.
- Add a data/cache path override to `AddDndMcpServer` and set it in `McpServerHarness`, so tests never touch `~/.local/share/dnd-mcp` or `~/.cache/dnd-mcp`. `StdoutPurityTests` already isolates `DND_MCP_DATA_DIR` / `XDG_*`.
- In custom converters, call `options.GetConverter(typeof(T)).Read(...)` rather than `JsonSerializer.Deserialize(ref reader)`; the latter loses the absolute error path. Use `TrySkip()` on a reader copy, not `Skip()`.

**Phases 2–6**
- `ToolArgumentGuard` is shallow. When tools gain object parameters (builds, campaign ops), have it test-deserialize each argument with `McpJson.Options` and report `JsonException.Path`. Otherwise a malformed nested object reaches the model as the generic error.
- Domain maths must reject non-finite inputs, because `1e400` binds as infinity for `double` parameters.

**Phase 6**
- Wire `SqliteCapabilities.Probe()` into startup (log to stderr, then `EnsureSupported()`).
- Move `Fts5Query` under `Repository/Campaign` if that layout is preferred.
- Any test class that flips Dapper's global `MatchNamesWithUnderscores` joins the `DapperGlobalStateCollection`.
