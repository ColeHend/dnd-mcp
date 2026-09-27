# dnd-mcp

A local [MCP](https://modelcontextprotocol.io) server for D&D 5e, **2014 and 2024 rules**, written in C# on .NET 10.
Claude Code and Claude Desktop launch it over stdio.

| Area | Status |
|---|---|
| Dice rolling (cryptographic RNG) | Done (Phase 1): keep/drop, rerolls, exploding dice, min/max, success counts, `adv`/`dis`/`ea`, labels, pass/fail checks, optional `seed` |
| Exact dice odds | Done (Phase 1): exact fractions, floating point for large pools, seeded Monte Carlo when no exact form exists |
| Rules lookup (SRD 5.1 / 5.2.1 via the dnd5eapi dataset, offline) | Done (Phase 2): `rules_search` (full text, either edition or both) and `rules_get` (by ref or name; `edition: "both"` puts 2014 and 2024 side by side), plus the `rules://attribution` resource |
| Encounter difficulty (2014 + 2024) | Done (Phase 3): `encounter_difficulty` (2014 DMG thresholds and multipliers, 2024 XP budget, or both side by side; SRD monsters by name or ref, any other by CR; effective-level offset), plus the `rules://tables/*` resources (XP by CR, both editions' encounter tables, DMG monster statistics by CR), also served by `rules_get` |
| DPR maths + feature deltas for homebrew | Phase 4 |
| Monte Carlo combat simulation | Phase 5 |
| Campaign tracking (SQLite, knowledge/provenance, markdown export) | Phases 6–8 |

The design, decisions and phase plan are in [PLAN.md](PLAN.md).

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
Glossary and the licences). The server reads `content/` from beside the executable, so copy or move the whole
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

Curated corrections: 306 of the served records (256 from 2024, 50 from 2014) had damaged upstream text or data (text
spliced from another entry, words run together, rows missing, text cut short or back-translated, XP that contradicts
the stat block's challenge rating). Their text is replaced with the SRD's own words, copied from the SRD 5.2 markdown for 2024 and the SRD 5.1 markdown for 2014, as listed
in `content/srd-corrections.json` and explained in `content/srd-corrections.md`. Every corrected entry says so under
its title ("Corrected from the upstream data: …"), and `rules://attribution` gives the file's sha256.

Not SRD text: the 2014 encounter-building tables (XP thresholds, encounter multipliers, adventuring-day XP) and the
Monster Statistics by Challenge Rating table come from the Dungeon Master's Guide (2014), the first three also from the
free 2014 Basic Rules, not from SRD 5.1, so the CC-BY licence above does not cover them. They are included for personal
use (PLAN.md, open question 1), and every result that uses them names that source.

This project is unofficial Fan Content, compatible with fifth edition.
