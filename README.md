# dnd-mcp

A local [MCP](https://modelcontextprotocol.io) server for D&D 5e, **2014 and 2024 rules**, written in C# on .NET 10.
Claude Code and Claude Desktop launch it over stdio.

| Area | Status |
|---|---|
| Dice rolling (cryptographic RNG) | Phase 0 stub: sums of `NdM` and integers |
| Exact dice odds | Phase 1 |
| Rules lookup (SRD 5.1 / 5.2.1 via the dnd5eapi dataset, offline) | Phase 2 |
| Encounter difficulty (2014 + 2024) | Phase 3 |
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
For development against a local build:

```bash
dotnet build DndMcp.sln
claude mcp add --scope local dnd-dev -- dotnet run --project DndMcp --no-build --no-launch-profile
```

Server logs go to stderr; `claude --debug=mcp` captures them.

## Build and test

```bash
dotnet build DndMcp.sln
dotnet test  DndMcp.sln
```

## Attribution

This work includes material taken from the System Reference Document 5.1 ("SRD 5.1") by Wizards of the Coast LLC
and available at https://dnd.wizards.com/resources/systems-reference-document. The SRD 5.1 is licensed under the
Creative Commons Attribution 4.0 International License available at
https://creativecommons.org/licenses/by/4.0/legalcode.

This work includes material from the System Reference Document 5.2.1 ("SRD 5.2.1") by Wizards of the Coast LLC,
available at https://www.dndbeyond.com/srd. The SRD 5.2.1 is licensed under the Creative Commons Attribution 4.0
International License, available at https://creativecommons.org/licenses/by/4.0/legalcode.

SRD content is taken from the [5e-bits 5e-database](https://github.com/5e-bits/5e-srd-api/tree/main/packages/5e-database)
dataset (MIT licensed; see `content/LICENSES/`). This project is unofficial Fan Content, compatible with fifth edition.
