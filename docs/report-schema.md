# Report schema (Phase 2)

This describes the JSON artifacts the CLI writes today. Field names use **camelCase**.

## `manifest.json`

Written by `scan` (`reportSchemaVersion: 1`) and overwritten by `suggest` (`reportSchemaVersion: 2`).

- `reportSchemaVersion` (number)
- `toolVersion` (string)
- `generatedAtUtc` (string, ISO-8601)
- `repoRoot` (string)
- `outDir` (string)
- `gitSha` (string \| null) — reserved; not populated yet
- `rulesVersion` (string \| null) — set by `suggest` (`--rules-version`)
- `schemaFingerprint` (string \| null) — SHA-256 hex of a canonical JSON snapshot; set by `suggest` when `--schema` loads successfully
- `config` (object \| null) — command-specific counters/metadata

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
