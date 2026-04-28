# SQL Repo Analyzer (C# + Node)

Portable command-line tool to scan a cloned TypeScript repository, inventory SQL usage (including TypeORM), and generate JSONL reports for later optimization analysis against SQL Server.

## Status

This repo currently contains **Phase 1 (inventory)** + **Phase 2 (static suggestions)** + **Phase 3 (SHOWPLAN_XML, gated `plan`)**:

- CLI commands: `scan`, `doctor`, `suggest`, `plan` (working), `report` (still minimal)
- Structured logging to console + file
- Output folder conventions (`.sqltool/`)
- Bundled Node extractor placeholder under `assets/ts-extractor/`

## Requirements

- .NET 10 SDK/runtime
- Node.js (for TypeScript extraction; validated by `doctor`)

## Backend profile (`manifest.backend`)

Repos differ: some are **.NET-heavy**, some **Node-heavy**. `scan` records that choice in **`.sqltool/manifest.json`** so later steps (and future extractors) know what they’re dealing with.

| Flag | Meaning |
|------|---------|
| `--backend csharp` | Primarily C# / .NET backend SQL patterns |
| `--backend node` | Primarily Node / TypeScript backend SQL patterns |
| `--backend mixed` | **Default** — both ecosystems matter (current `scan` still crawls `.sql` + TS/JS) |

Example:

```powershell
dotnet run --project .\src\SqlRepoAnalyzer.Cli\SqlRepoAnalyzer.Cli.csproj -- scan --root . --out .sqltool --backend node
```

`suggest` and `plan` **rewrite** `manifest.json` but **keep** `backend` from the previous manifest when present.

## Run

```powershell
dotnet build .\SqlRepoAnalyzer.sln
npm --prefix .\assets\ts-extractor\ install
dotnet run --project .\src\SqlRepoAnalyzer.Cli\SqlRepoAnalyzer.Cli.csproj -- doctor --out .sqltool --verbose
dotnet run --project .\src\SqlRepoAnalyzer.Cli\SqlRepoAnalyzer.Cli.csproj -- scan --root . --out .sqltool
dotnet run --project .\src\SqlRepoAnalyzer.Cli\SqlRepoAnalyzer.Cli.csproj -- scan --root . --out .sqltool --backend mixed
dotnet run --project .\src\SqlRepoAnalyzer.Cli\SqlRepoAnalyzer.Cli.csproj -- suggest --root . --out .sqltool
dotnet run --project .\src\SqlRepoAnalyzer.Cli\SqlRepoAnalyzer.Cli.csproj -- suggest --root . --out .sqltool --schema .\.sqltool\schema-snapshot.json

# Phase 3: estimated plans (requires --enable-showplan; connection via env or --connection)
$env:SQLTOOL_CONNECTION_STRING = "Server=localhost;Database=YourDb;Integrated Security=true;TrustServerCertificate=true"
dotnet run --project .\src\SqlRepoAnalyzer.Cli\SqlRepoAnalyzer.Cli.csproj -- plan --root . --out .sqltool --enable-showplan --max-queries 20 --timeout-seconds 30
# Preview which inventory rows would be sent (no DB round-trips for captures):
dotnet run --project .\src\SqlRepoAnalyzer.Cli\SqlRepoAnalyzer.Cli.csproj -- plan --root . --out .sqltool --enable-showplan --dry-run
```

Outputs:

- `.sqltool/manifest.json`
- `.sqltool/queries.jsonl`
- `.sqltool/suggestions.jsonl`
- `.sqltool/plans.jsonl` and `.sqltool/showplan-xml/*.xml` (after `plan`)