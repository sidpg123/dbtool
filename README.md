# SQL Repo Analyzer

Simple CLI tool to:

1. Find SQL used in your codebase (`scan`)
2. Generate static findings (`suggest`)
3. Run Phase 3 DB-connected checks (`plan`)

Outputs are written to `.sqltool/` (JSON at the repo root of that folder; human-readable **Markdown** copies under `.sqltool/markdown/`).

## Requirements

- .NET SDK
- Node.js

## Use it the easy way

Run from CLI project folder once:

```powershell
Set-Location "c:\Users\NinadBandiwadekarIND\Repos\DB Optimization Tool\dbtool\src\SqlRepoAnalyzer.Cli"
```

Then you can run `dotnet run -- ...` without `--project`.

## Quick start

```powershell
# one-time setup
dotnet build "c:\Users\NinadBandiwadekarIND\Repos\DB Optimization Tool\dbtool\SqlRepoAnalyzer.sln"
npm --prefix "c:\Users\NinadBandiwadekarIND\Repos\DB Optimization Tool\dbtool\assets\ts-extractor" install

# 1) check environment
dotnet run -- doctor --out "c:\Users\NinadBandiwadekarIND\Repos\DB Optimization Tool\dbtool\.sqltool" --verbose

# 2) inventory SQL
dotnet run -- scan --root "c:\Users\NinadBandiwadekarIND\Repos\DB Optimization Tool\dbtool" --out "c:\Users\NinadBandiwadekarIND\Repos\DB Optimization Tool\dbtool\.sqltool"

# 2b) inventory only static SELECT queries (exclude dynamic/no-SQL entries)
dotnet run -- scan --root "c:\Users\NinadBandiwadekarIND\Repos\DB Optimization Tool\dbtool" --out "c:\Users\NinadBandiwadekarIND\Repos\DB Optimization Tool\dbtool\.sqltool" --query-scope select

**Scan notes:** `.sql` text is split on **semicolons** (script `GO` is also honored). Statements not separated by `;` stay in **one** inventory blob. **`--query-scope select`** keeps only fragments that parse as *only* static `SELECT`s; if a blob mixes `SELECT` + `MERGE` / `INSERT` / `DELETE` / etc., or you use `select` scope with almost no qualifying batches, `queries.json` can be **`[]`** even when the file obviously contains `SELECT`. Use default **`--query-scope all`** (or omit the flag) to inventory everything.

**C# backends:** **`scan`** crawls **`*.cs`** and pulls T-SQL from **verbatim** strings (`@"…"`, `$@"…"`, `@$"…"`) whose text looks like SQL (starts with keywords such as `SELECT`, `MERGE`, `CREATE`, …). Put raw SQL in a verbatim literal for best results; normal `"…"` strings are not mined. `**bin`** / *`*obj`** folders are skipped.

# 3) suggestions
dotnet run -- suggest --root "c:\Users\NinadBandiwadekarIND\Repos\DB Optimization Tool\dbtool" --out "c:\Users\NinadBandiwadekarIND\Repos\DB Optimization Tool\dbtool\.sqltool"
```

## Phase 3 (`plan`)

`plan` runs DB-connected checks using environment-based connection config from JSON.

Command:

```powershell
dotnet run -- plan --root "c:\path\to\target-repo" --out "c:\path\to\target-repo\.sqltool" --env dev
```

Optional flags:

- `--env <name>`: selects environment in DB config
- `--db-config <path>`: override config path (default is `<outDir>\db-connections.json`)

### DB connection config JSON

`plan` resolves `db-connections.json` in this order: `**--db-config` path** → `**<outDir>/db-connections.json`** (e.g. per-repo under `.sqltool/`) → **template bundled with the tool** next to `SqlRepoAnalyzer.dll` (no file in the scanned repo required). Edit the bundled copy in your build output, use `--db-config`, or add `.sqltool/db-connections.json` for environment-specific secrets.

Example (either location):

```json
{
  "defaultEnvironment": "dev",
  "environments": {
    "dev": {
      "connectionString": "Server=localhost;Database=YourDb;Integrated Security=true;TrustServerCertificate=true"
    },
    "qa": {
      "connectionString": "Server=qa-sql;Database=YourDb;User Id=app;Password=***;TrustServerCertificate=true"
    }
  }
}
```

Detailed format reference: `docs/db-connections-format.md`.
Checked-in sample template: `.sqltool/db-connections.example.json` (copy to `.sqltool/db-connections.json` and replace placeholder values).

## What each command does

- `doctor`: checks output folder + Node installation
- `scan`: creates `.sqltool/queries.json` and `.sqltool/markdown/queries.md`
- `suggest`: creates `.sqltool/suggestions.json` and `.sqltool/markdown/suggestions.md`
- `plan`: creates `.sqltool/plans.json` and `.sqltool/markdown/plans.md` (DB-connected rule checks)
- `report`: placeholder/stub

## Output files

- `.sqltool/manifest.json`
- `.sqltool/queries.json`, `.sqltool/suggestions.json`, `.sqltool/plans.json` (after `plan`)
- `.sqltool/markdown/*.md` — human-readable copies: `queries.md`, `suggestions.md`, `plans.md`

## Recent fixes (this branch)

Phase 2 `**suggest**` rule behavior:

1. `**sql.std.snake_case**` — The rule used to emit one finding per AST occurrence of the same column or identifier, so repeated names in one query produced duplicate rows. It now emits **one finding per distinct** failing schema-object segment, table alias, or column name. `**evidence`** includes `**occurrenceCount**` and `**occurrences**` (`{ line, column }`, 1-based positions in the analyzed SQL text). See `docs/rules.md` for details.
2. `**sql.std.schema_qualified_object**` — Single-part `FROM` / join targets that are **CTEs** (names declared in the same statement’s `WITH` clause) were incorrectly flagged as needing `[schema].[object]`. The rule now tracks **CTE scope** for `SELECT`, `INSERT`, `UPDATE`, `DELETE`, and `MERGE` and **skips** those references. Unqualified **tables/views** are aggregated **one finding per name** with `**occurrences`** locations. Edge case: an unqualified base table that shares a name with an in-scope CTE is also skipped (documented in `docs/rules.md`).
3. `**sql.std.bracket_quoted_identifiers**` — Same aggregation + `**occurrences**` as schema/snake_case for repeated identifiers.
4. `**markdown/suggestions.md**` — Rule summary table includes a **Count** column; detail sections list **Locations** (`L_line:C_col`) and repeat headings only once per finding with **×n** when count > 1.

Re-run `**suggest`** after pulling to refresh `suggestions.json` / `markdown/suggestions.md`.

## Notes

- `--verbose` = more detailed logs in terminal.
- `--query-scope all|select` (scan only): default `all` inventories all extracted SQL. `select` keeps only fragments whose parse tree is purely static SELECT statements—mixed DDL/DML/multiple verbs in one **unsplit** fragment are dropped; omit the flag when you see an empty inventory but know SQL exists (see Quick start note).
- VS Code/Cursor tasks are available in `.vscode/tasks.json` (`doctor`, `scan`, `suggest`, `custom args`).

## Rules which are yet to create

- 3.7
- 3.8
- 3.9

