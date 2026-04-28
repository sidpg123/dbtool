# Report schema (Phase 2 + Phase 3)

This describes the JSON artifacts the CLI writes today. Field names use **camelCase**.

## `manifest.json`

Written by `scan` (`reportSchemaVersion: 1`), overwritten by `suggest` (`2`), and overwritten by `plan` (`3`).

- `reportSchemaVersion` (number)
- `toolVersion` (string)
- `generatedAtUtc` (string, ISO-8601)
- `repoRoot` (string)
- `outDir` (string)
- `gitSha` (string \| null) — reserved; not populated yet
- `rulesVersion` (string \| null) — set by `suggest` (`--rules-version`)
- `schemaFingerprint` (string \| null) — SHA-256 hex of a canonical JSON snapshot; set by `suggest` when `--schema` loads successfully
- `backend` (string \| null) — primary stack hint for extractors/heuristics: `csharp` \| `node` \| `mixed`. Set by `scan --backend` (defaults to `mixed` when omitted). Preserved when `suggest` or `plan` overwrites the manifest so downstream tooling still knows the repo profile.
- `config` (object \| null) — command-specific counters/metadata (Phase 1 `config` also echoes `backend` where applicable)

## `queries.jsonl`

One JSON object per line (inventory).

- `queryId` (string)
- `fingerprint` (string)
- `sqlText` (string \| null)
- `sourceKind` (string enum name, for example `SqlFile`, `TypeOrmRawQuery`)
- `completeness` (string \| null)
- `occurrences` (array)
  - `filePath` (string, repo-relative when possible)
  - `startLine`, `startCol`, `endLine`, `endCol` (numbers)

## `suggestions.jsonl`

One JSON object per line (Phase 2 static analysis). `queryId` matches `queries.jsonl`.

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

## `plans.jsonl` (Phase 3)

Emitted by `plan`. One JSON object per inventory row (same `queryId` order as processed from `queries.jsonl`).

- `queryId` (string)
- `fingerprint` (string)
- `sourceKind` (string enum name)
- `completeness` (string \| null)
- `status` (string) — `ok` \| `skipped` \| `error` \| `dry_run`
- `skipReason` (string \| null) — e.g. `no_sql_text`, `not_select_only`, `max_queries_cap`, `would_capture_showplan`
- `error` (string \| null) — capture / server message when `status` is `error`
- `planXmlRelativePath` (string \| null) — e.g. `showplan-xml/q_abcd1234.xml` when `status` is `ok`
- `findings` (array) — same shape as `suggestions.jsonl` findings (table scan, missing-index hint XML, etc.)

## `plan-suggestions.jsonl` (Phase 3)

Emitted by `plan`. One JSON object per inventory row, in the same shape as `suggestions.jsonl`, but populated from SHOWPLAN_XML analysis.

- `queryId` (string)
- `fingerprint` (string)
- `sourceKind` (string enum name)
- `completeness` (string \| null)
- `analysisStatus` (string) — `planned` \| `skipped` \| `error` \| `dry_run` \| `no_sql_text`
- `analysisWarning` (string \| null)
- `parseOk` (boolean \| null) — `true` for `planned` records (plan captured), `null` otherwise
- `parseErrors` (array of strings \| null) — not populated in Phase 3
- `findings` (array) — showplan-based findings (table scan, key lookup, missing-index hint, optional index metadata enrichment, etc.)
