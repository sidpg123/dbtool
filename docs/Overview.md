# SQL Repo Analyzer — overview

Short description of what the CLI does and where to read more.

## `scan`

- Scans entire target repo and finds sql queries. 
- Writes `queries.json` with each distinct query and **occurrences** (repo paths and line/column ranges).
- Also writes `queries.incremental.json` and `scan-state.json` for incremental `suggest`

## `suggest` (no database)

- Runs **static rules** (~23) on the queries from `queries.json`.
- These rules are given by Peter. 
- Examples:
  - Avoid `SELECT *` where policy expects explicit columns.
  - Flag **cursors** and `MERGE`where alternatives are preferred.
  - `TRUNCATE` usage (caution).
  - **Two-part** table/view names: `[schema].[object]`.
  - **Snake_case** for identifiers where applicable.
  - **Tab** indentation and **UPPERCASE** SQL keywords.
  - Prefer **CTEs** over deeply nested derived subqueries where practical.

## `suggest --incremental`

- Re-runs rules **only on new or changed** queries (by fingerprint).

## `plan` (database required)

Gives suggestion based on live database. 

- Examples:
  - **Unknown tables** (reference not in connected DB).
  - **Index coverage / fit** and missing-index hints (advisory).
  - **Duplicate / redundant** and **unused** indexes 
  - **Heavy triggers** 
  - Other catalog/DMV checks  like (implicit conversion risk, stats freshness, FK index support, etc.).

## Outputs

- JSON under `**.sqltool/`** (or `**--out`**); Markdown under `**.sqltool/markdown/**`.
- Schema: `**docs/report-schema.md**`.

