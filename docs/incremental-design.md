# Incremental design (Phase 0 placeholder)

This document will be **locked** in Phase 0.

## Decisions to lock

- How `queryId` is generated (recommended: hash of normalized SQL when SQL text exists; separate stable IDs for QueryBuilder sites).
- What counts as a change: raw SQL text vs normalized SQL fingerprint.
- How removed queries are represented: `status: removed` retained vs purged.
- What triggers full re-analysis: rules version, schema fingerprint, config fingerprint.

