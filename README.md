# SQL Repo Analyzer (C# + Node)

Portable command-line tool to scan a cloned TypeScript repository, inventory SQL usage (including TypeORM), and generate JSONL reports for later optimization analysis against SQL Server.

## Phase 0 status

This repo currently contains the Phase 0 skeleton:

- CLI command stubs: `scan`, `suggest`, `report`, `doctor`
- Structured logging to console + file
- Output folder conventions (`.sqltool/`)
- Bundled Node extractor placeholder under `assets/ts-extractor/`

## Requirements

- .NET 8 SDK (for building) / .NET 8 runtime (for running framework-dependent)
- Node.js (for TypeScript extraction; validated by `doctor`)

