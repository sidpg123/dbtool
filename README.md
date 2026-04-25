# SQL Repo Analyzer (C# + Node)

Portable command-line tool to scan a cloned TypeScript repository, inventory SQL usage (including TypeORM), and generate JSONL reports for later optimization analysis against SQL Server.

## Phase 0 status

This repo currently contains Phase 1 (inventory) implementation:

- CLI commands: `scan`, `doctor` (working), `suggest`/`report` (stubs until Phase 2)
- Structured logging to console + file
- Output folder conventions (`.sqltool/`)
- Bundled Node extractor placeholder under `assets/ts-extractor/`

## Requirements

- .NET 10 SDK/runtime
- Node.js (for TypeScript extraction; validated by `doctor`)

## Run

```powershell
dotnet build .\SqlRepoAnalyzer.sln
npm --prefix .\assets\ts-extractor\ install
dotnet run --project .\src\SqlRepoAnalyzer.Cli\SqlRepoAnalyzer.Cli.csproj -- doctor --out .sqltool --verbose
dotnet run --project .\src\SqlRepoAnalyzer.Cli\SqlRepoAnalyzer.Cli.csproj -- scan --root . --out .sqltool
```

Outputs:

- `.sqltool/manifest.json`
- `.sqltool/queries.jsonl`