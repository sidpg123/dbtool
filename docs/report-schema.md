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
- `sourceKind` (string enum name, for example `SqlFile`, `TypeOrmRawQuery`)
- `completeness` (string \| null)
- `occurrences` (array)
  - `filePath` (string, repo-relative when possible)
  - `startLine`, `startCol`, `endLine`, `endCol` (numbers)

## `suggestions.json`

JSON array (Phase 2 static analysis). `queryId` matches `queries.json`. A readable copy is also emitted as **`markdown/suggestions.md`** after **`suggest`**.

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
  - `evidence` (object \| null)

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
