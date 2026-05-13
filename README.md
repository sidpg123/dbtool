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
```

**Scan notes:** `.sql` text is split on **semicolons** (script `GO` is also honored). Statements not separated by `;` stay in **one** inventory blob. **`--query-scope select`** keeps fragments that parse as read-oriented batches: `SELECT`, `DECLARE` variables, `SET` options, and nested `BEGIN…END` blocks containing only those forms (still rejects `INSERT`/`UPDATE`/`DELETE`/`MERGE`/etc.). Use **`--query-scope all`** when you want every extracted string. **C#:** text is pulled from verbatim literals, `"…"`-led `+` chains that look like SQL, and `SqlHelper.ExecuteDataset` / `ExecuteScalar` / `ExecuteNonQuery` / `ExecuteReader` command text (including permissive stitching of `+` with parameters).

```powershell
# 3) suggestions
dotnet run -- suggest --root "c:\Users\NinadBandiwadekarIND\Repos\DB Optimization Tool\dbtool" --out "c:\Users\NinadBandiwadekarIND\Repos\DB Optimization Tool\dbtool\.sqltool"

# 3b) suggestions (incremental — reuse Phase 2 rows for queries whose fingerprint did not change since last scan)
dotnet run -- suggest --root "c:\Users\NinadBandiwadekarIND\Repos\DB Optimization Tool\dbtool" --out "c:\Users\NinadBandiwadekarIND\Repos\DB Optimization Tool\dbtool\.sqltool" --incremental
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
- `scan`: creates `.sqltool/queries.json`, `.sqltool/queries.incremental.json` (new/changed vs last scan), `.sqltool/scan-state.json` (baseline fingerprints), and `.sqltool/markdown/queries.md`
- `suggest`: creates `.sqltool/suggestions.json` and `.sqltool/markdown/suggestions.md`. With `--incremental`, re-runs static rules only for queries listed in `queries.incremental.json` (next to `queries.json`) and merges into the existing `suggestions.json`; omit the flag after rule/tool upgrades to refresh everything.
- `plan`: creates `.sqltool/plans.json` and `.sqltool/markdown/plans.md` (DB-connected rule checks)
- `report`: placeholder/stub

## Output files

- `.sqltool/manifest.json`
- `.sqltool/queries.json`, `.sqltool/queries.incremental.json`, `.sqltool/scan-state.json`, `.sqltool/suggestions.json`, `.sqltool/plans.json` (after `plan`)
- `.sqltool/markdown/*.md` — human-readable copies: `queries.md`, `suggestions.md`, `plans.md`

**Incremental scan:** each `scan` overwrites `queries.json` with the full inventory. It compares to `scan-state.json` from the previous run, writes only new/changed rows to `queries.incremental.json`, then updates `scan-state.json`. First run (or missing state) treats every query as incremental. **`plan`** still uses full `queries.json` only.

## Recent changes (scan, C# extraction, Phase 3 `plan`)

### `scan` and `--query-scope select`

- **`--query-scope select`** classification moved to **`SelectScopeSqlClassifier`** in Core. It now treats batches as “read inventory” when they contain only **`SELECT`**, **`DECLARE` variable** statements, common **`SET`** forms (`SET` on/off, transaction isolation, variable assignment, text size, error level), and nested **`BEGIN…END`** whose inner statements also pass the same rules. Anything else (for example **`INSERT` / `UPDATE` / `DELETE` / `MERGE`**) still fails the filter. Empty batches are skipped instead of failing the whole script.
- **C# embedded SQL:** besides verbatim `@"…"` / `$@"…"` chains, **`CSharpEmbeddedSqlExtractor`** also collects **`"…"`-first** string concatenations that merge with `+` (verbatim or regular fragments) when the merged text passes **`SqlTextHeuristics.LooksLikeSql`** (typical DAL `"...'" + param + "'"` style).
- **Classic DAAB `SqlHelper`:** **`CSharpSqlHelperExecuteDatasetExtractor`** (Roslyn, syntax-only) pulls command text from **`SqlHelper.ExecuteDataset`**, **`ExecuteScalar`**, **`ExecuteNonQuery`**, and **`ExecuteReader`** for the usual `(…, CommandType, commandText, …)` overload (third argument is the SQL string). It resolves **`const`** and **`static readonly string`** fields file-wide, **`string` / `var`** locals declared **before** the call in the same method/accessor body, and uses **permissive `+` stitching** (unknown expression fragments become empty text; candidate marked **`partial`** in `queries.json`). New **`SourceKind`**: **`CSharpSqlHelperExecuteDataset`**. Core references **`Microsoft.CodeAnalysis.CSharp`**.

### Phase 3 `plan` (DB rules)

- **`schema.unknown_table`:** referenced `schema.object` is resolved against **`sys.tables`**, **`sys.views`**, and **`sys.synonyms`**; synonym chains are walked to a **`sys.objects`** target of type **user table (`U`) or view (`V`)**. Unresolvable or non-table/view targets stay “unknown.”
- **Catalog for synonyms / views:** index, row-count, column, and related maps are keyed by the **logical** reference where needed; **`db.stats_freshness`** includes **views** (`U` and `V`) where applicable.
- **`db.index_suitability`:** missing-index DMV rows stay keyed by the **physical** base object; requirements that use a **synonym** (or other logical name) are matched via a **logical → catalog name** map. Placeholder **`CREATE INDEX`** text targets the base table/view. When the query text used a synonym, the warn message / evidence can include **`queriedTableNames`**.

See **`docs/rules-requiring-db-connection.md`** for the updated rule descriptions.

## Recent fixes (this branch)

Phase 2 `**suggest**` rule behavior:

1. `**sql.std.snake_case**` — The rule used to emit one finding per AST occurrence of the same column or identifier, so repeated names in one query produced duplicate rows. It now emits **one finding per distinct** failing schema-object segment, table alias, or column name. `**evidence`** includes `**occurrenceCount**` and `**occurrences**` (`{ line, column }`, 1-based positions in the analyzed SQL text). See `docs/rules.md` for details.
2. `**sql.std.schema_qualified_object**` — Single-part `FROM` / join targets that are **CTEs** (names declared in the same statement’s `WITH` clause) were incorrectly flagged as needing `[schema].[object]`. The rule now tracks **CTE scope** for `SELECT`, `INSERT`, `UPDATE`, `DELETE`, and `MERGE` and **skips** those references. Unqualified **tables/views** are aggregated **one finding per name** with `**occurrences`** locations. Edge case: an unqualified base table that shares a name with an in-scope CTE is also skipped (documented in `docs/rules.md`).
3. `**sql.std.bracket_quoted_identifiers**` — Same aggregation + `**occurrences**` as schema/snake_case for repeated identifiers.
4. `**markdown/suggestions.md**` — Rule summary table includes a **Count** column; detail sections list **Locations** (`L_line:C_col`) and repeat headings only once per finding with **×n** when count > 1.

Re-run `**suggest`** after pulling to refresh `suggestions.json` / `markdown/suggestions.md`.

## Notes

- `--verbose` = more detailed logs in terminal.
- `--query-scope all|select` (scan only): default `all` inventories all extracted SQL. `select` keeps fragments that parse as read-oriented T-SQL (see Quick start **Scan notes**); mixed DML/DDL in one unsplit fragment is still dropped. Omit the flag when you need full coverage.
- VS Code/Cursor tasks are available in `.vscode/tasks.json` (`doctor`, `scan`, `suggest`, `custom args`).

## Rules which are yet to create

- 3.7
- 3.8
- 3.9


dotnet run -- scan --root "C:\Users\SiddharthPatilINDev\Documents\work\Synchronizer\JiBe.Synchronizer.Office.SharedLogic\Appcode" --out "C:\Users\SiddharthPatilINDev\Documents\DB_Tool\testBackend\.sqltool" --query-scope select

