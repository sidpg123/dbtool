# Report schema (Phase 2 + Phase 3)

This describes the JSON artifacts the CLI writes today. Field names use **camelCase**.

## Markdown exports (`markdown/`)

Under the output directory (e.g. `.sqltool/`), the **`markdown/`** subfolder holds human-readable **`.md`** files that mirror the JSON data:

- **`markdown/queries.md`** — written by **`scan`** (same content as `queries.json`).
- **`markdown/suggestions.md`** — written by **`suggest`** (same content as `suggestions.json`).
- **`markdown/plans.md`** — written by **`plan`** (same content as `plans.json`).

JSON files stay at the root of the output directory for scripts and tooling; use Markdown for Confluence, GitHub, or email.

## `manifest.json`

Written by `scan` (`reportSchemaVersion: 1`), overwritten by `suggest` (`2`), and overwritten by `plan` (`3`).

- `reportSchemaVersion` (number)
- `toolVersion` (string)
- `generatedAtUtc` (string, ISO-8601)
- `repoRoot` (string)
- `outDir` (string)
- `gitSha` (string \| null) — reserved; not populated yet
- `rulesVersion` (string \| null) — set by `suggest` (`--rules-version`)
- `backend` (string \| null) — primary stack hint for extractors/heuristics: `csharp` \| `node` \| `mixed`. Set by `scan --backend` (defaults to `mixed` when omitted). Preserved when `suggest` or `plan` overwrites the manifest so downstream tooling still knows the repo profile.
- `config` (object \| null) — command-specific counters/metadata (Phase 1 `config` also echoes `backend` where applicable)

## `queries.json`

JSON array of query inventory records. A readable copy is also emitted as **`markdown/queries.md`** after **`scan`**.

- `queryId` (string)
- `fingerprint` (string)
- `sqlText` (string \| null)
- `sourceKind` (string enum name, for example `SqlFile`, `CSharpEmbeddedSql`, `TypeOrmRawQuery`)
- `completeness` (string \| null)
- `occurrences` (array)
  - `filePath` (string, repo-relative when possible)
  - `startLine`, `startCol`, `endLine`, `endCol` (numbers)

## `queries.incremental.json`

Written by **`scan`**. Same array shape as **`queries.json`**, but only rows whose `queryId` is new or whose `fingerprint` changed compared to **`scan-state.json`** from the previous run. On the first scan (or missing/corrupt baseline), this file lists **all** queries (same as `queries.json`). Used by **`suggest --incremental`** to limit static rule work; `suggest` still reads the full **`queries.json`** for ordering and merge.

## `scan-state.json`

Written by **`scan`** after each successful inventory. Baseline for the next incremental diff.

- `version` (number) — schema version (currently `1`)
- `lastScanAtUtc` (string, ISO-8601)
- `fingerprintsByQueryId` (object) — map of `queryId` → `fingerprint` from that scan

## `suggestions.json`

JSON array (Phase 2 static analysis). `queryId` matches `queries.json`. A readable copy is also emitted as **`markdown/suggestions.md`** after **`suggest`**.

With **`suggest --incremental`**, the CLI merges new findings for queries in **`queries.incremental.json`** (sibling of the `queries.json` passed to `--queries`, default `<out>/queries.incremental.json`) with prior rows from the existing **`suggestions.json`** when the `fingerprint` for that `queryId` is unchanged. Run **`suggest` without `--incremental`** after changing rules or the analyzer so every query is re-evaluated.

- `queryId` (string)
- `fingerprint` (string)
- `sourceKind` (string enum name)
- `completeness` (string \| null)
- `analysisStatus` (string) — `analyzed` \| `no_sql_text`
- `analysisWarning` (string \| null)
- `parseOk` (boolean \| null)
- `parseErrors` (array of strings \| null)
- `findings` (array)
  - `ruleId` (string)
  - `severity` (string enum name: `Info`, `Warn`, `Error`)
  - `confidence` (string enum name: `Low`, `Medium`, `High`)
  - `message` (string)
  - `suggestion` (string \| null)
  - `evidence` (object \| null) — optional fields:
    - `occurrenceCount` (number) — how many times this issue appears in the analyzed `sqlText`
    - `occurrences` (array) — 1-based **line** and **column** in the **analyzed `sqlText` string** for that query (ScriptDom start position of the identifier / reported token), not necessarily the on-disk file when SQL is embedded or split by `scan`
    - other rule-specific keys as needed

## `plans.json` (Phase 3)

Emitted by `plan` as a JSON object for DB-connected checks. The same command writes **`markdown/plans.md`**: a DBA-oriented Markdown summary (run metadata, counts by rule, an **Action items** section for FAIL/WARN, full findings with JSON evidence) suitable for Confluence, GitHub, or email.

- `generatedAtUtc` (string, ISO-8601)
- `environment` (string) — selected environment from DB config
- `connectionSummary` (string) — safe connection summary (server/database only)
- `queryCount` (number)
- `startedAtUtc` (string, ISO-8601)
- `durationMs` (number)
- `totalRules` (number)
- `totalFindings` (number)
- `findings` (array)
  - `ruleId` (string)
  - `status` (string) — `pass` \| `warn` \| `fail` \| `error`
  - `severity` (string) — `info` \| `warn` \| `error`
  - `message` (string)
  - `recommendation` (string \| null)
  - `affectedObjects` (array of strings)
  - `queryIds` (array of strings) — which queries the finding relates to where applicable
  - `evidence` (object \| null)
    - For `db.index_suitability` failures: typically `whereJoinColumns` (columns from WHERE/JOIN used as index keys in the heuristic), `selectListColumns` (columns from SELECT needing cover via INCLUDE where applicable), `indexCreationScript` (templated `CREATE INDEX` DDL for review—bracket‑quoted IDs; validate before deploying).
    - For `db.index_suitability` DMV warnings: `equalityColumns`, `inequalityColumns`, `includedColumns`, `impactScore`, plus `indexCreationScript` (plain skeleton `CREATE INDEX` — fill keys/includes from DMV fields after normalization).
- `byRule` (array)
  - `ruleId` (string)
  - `pass` (number)
  - `warn` (number)
  - `fail` (number)
  - `error` (number)
