# Report schema (Phase 0)

This document will be finalized in Phase 0 before Phase 1 extraction expands.

## `manifest.json`

- `reportSchemaVersion` (int)
- `toolVersion` (string)
- `generatedAtUtc` (ISO-8601 string)
- `repoRoot` (string)
- `outDir` (string)
- `gitSha` (string|null)
- `config` (object|null)

## `queries.jsonl`

Phase 0: file may be empty. Phase 1 will emit one JSON object per line.

## `suggestions.jsonl`

Phase 0: file may be empty. Phase 2 will emit one JSON object per line keyed by `queryId`.

