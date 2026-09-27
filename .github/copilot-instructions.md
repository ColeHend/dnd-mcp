# dnd-mcp — agent guide

A local stdio MCP server (C#, .NET 10, ModelContextProtocol 2.2.0) for D&D 5e, 2014 + 2024 rules. The design and
phase plan are in `PLAN.md`; read its Decisions table before changing structure.

## 1. Architecture

```
DndMcp (Exe: MCP host — tools, resources, prompts, CLI)
├── DndMcp.Repository (SQLite, SRD content, markdown transfer)
│   └── DndMcp.Domain
└── DndMcp.Domain (pure maths and rules; ZERO project references, no MCP dependency)
```

`DndMcp.Tests` references Domain + Repository, **not** the host. `DndMcp.IntegrationTests` is the only project that
references the host; it drives the server through a real MCP client.

## 2. The things that fail silently

1. **stdout is the protocol.** Never `Console.Write*` anywhere. Logs go to stderr (Program.cs). `StdoutPurityTests`
   runs the built binary and fails on any non-JSON-RPC stdout line.
2. **Only `McpException` messages reach the model.** Domain throws `DndInputException` for caller-fixable input; the
   call-tool filter in `DndMcpServerRegistration` translates it. Any other exception reaches the model as a bare
   "An error occurred invoking '<tool>'." — treat that as a bug, not an error message.
3. **Tool annotations must be set explicitly.** Unset hints mean destructive=true / openWorld=true under the MCP spec.
   `ServerSurfaceTests` checks every tool.
4. **Vendored content is pinned by sha256.** Never hand-edit files under `content/5e-database/`; re-vendor with
   `scripts/fetch-5e-database.sh`. `.gitattributes` keeps git from rewriting their line endings.
5. **Wrong upstream records are fixed in `content/srd-corrections.json`, never in the vendored files.** Each entry
   replaces (or adds) whole top-level properties with text copied verbatim from the SRD markdown, and carries a reason
   and a source; the reason is shown on the corrected document. Paraphrased or invented rules text is worse than the
   damage it replaces, because every answer is labelled as SRD text.
6. **srd.db is a cache keyed on its inputs.** Its staleness key covers `SrdIndexSchema.Version`, the manifest
   fingerprint and the sha256 of the glossary and the corrections file — not the importer's code. Any change to the
   schema, import, pairing, aliases or search text must bump `SrdIndexSchema.Version`, or every existing install keeps
   serving the old index. `SrdIndexSchemaVersionTests` pins a digest of the hand-written tables to the version.
7. **`content/` ships beside the executable.** The published binary alone has no rules data; copy the whole publish
   directory, and publish upgrades into an empty one (`dotnet publish` never deletes stale files).
8. **Formatter output is bounded and shaped.** A body uses `###` headings or deeper (it sits under `## 2014` in a
   comparison) and every real record must render under `SrdMarkdown.MaxChars`; `SrdMarkdownRenderAllTests` renders all
   ~4,600 records to hold both.
9. **Build MCP servers one at a time in tests.** In ModelContextProtocol 2.2.0, building the same tool on two threads
   at once can leave an injected `IProgress<>` parameter in the input schema as a phantom argument. `McpServerHarness`
   serialises server construction; keep it that way.

## 3. Conventions

- File-scoped namespaces. `public sealed class` for services and tool classes (static classes cannot be
  `WithTools<T>()` arguments).
- Register tools with generic `WithTools<T>(McpJson.Options)` in `DndMcpServerRegistration` — not
  `WithToolsFromAssembly` (reflection-only, not trim-safe) — so Program.cs and the integration tests share one list.
- Tool names are snake_case with no dots (the Claude API tool-name regex has no `.`).
- Tool parameters the model sees are snake_case too: single-word C# names need nothing; multi-word ones get
  `[AIParameterName("dry_run")]` (MEAI001 is suppressed in DndMcp.csproj, with the reason). `ToolArgumentGuard`
  keys its checks by the schema name, so renamed parameters stay validated.
- Every tool validates its own inputs and throws `DndInputException` with what was wrong and what is accepted.
- `private readonly ILogger<X> _logger;`, 4-space indent, Allman braces.
- **XML doc `<summary>` blocks explain WHY and WHAT BREAKS, not what the code does.**
- Wire and discriminator values are string constants, not enums (stored data outlives enum renumbering).
- FluentValidation, validators in Domain. No data annotations for validation (the SDK does not enforce them).

## 4. Testing

xUnit 2.9.3, plain `Assert`. **No mocking library, no FluentAssertions** — real objects, hand-written fakes.

- `Method_Scenario_Expectation` naming, heavy `[Theory]` / `[InlineData]`.
- Golden values for maths come from exact arithmetic (see PLAN.md → Verification); assert them to a stated tolerance.
- Prefer a test that fails on a real mutation over one that merely exercises a path.

```bash
dotnet build DndMcp.sln
dotnet test  DndMcp.sln
```
