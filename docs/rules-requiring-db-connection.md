# Rules requiring DB connection (separate list)

This file tracks coding standards that cannot be validated reliably from SQL text/AST alone and should use live database metadata or runtime plan inspection.

## Requires DB connection and/or catalog/DMV access

Implemented by **`plan`** (`Phase3PlansReport` in **`plans.json`**) using **`db-connections.json`**. Scoped to tables referenced by scanned queries (**`queries.json`**).

### Core schema and index fit

- **`schema.unknown_table`**  
  Static references are resolved against **`sys.tables`**, **`sys.views`**, and **`sys.synonyms`** for the chosen environment (synonyms are followed to their **`base_object_id`**, including chained synonyms, and must resolve to a user table or view); unmatched references produce fail findings.

- **`db.covering_index`**  
  Live index metadata counts usable indexes vs heap heuristic for referenced tables.

- **`db.index_suitability`**  
  Predicate/select-list columns matched against index key/includes; missing-index DMV snippets when available.

- **`db.minimal_dataset_extraction`**  
  `SELECT *` combined with **`sys.dm_db_partition_stats`** row counts for referenced user tables and views (including via synonym).

- **`db.heavy_trigger_impact`**  
  Trigger bodies from **`sys.triggers`** / **`sys.sql_modules`** scoped to referenced tables (`parent_id`). Findings include the **parent table** (`OBJECT_SCHEMA_NAME`/`OBJECT_NAME` on `parent_id`) in **`message`**, **`affectedObjects`**, and **`evidence.parentTable`**.

### Implicit conversion / type mismatch

- **`db.implicit_conversion_risk`**  
  Column-to-column equality (join / `WHERE` **col = col**) compares **`sys.columns`**/`sys.types` for both sides (`system_type_id`, `user_type_id`, collation for string kinds). Warns when types or collation differ in ways that commonly force implicit conversions at runtime.

### Statistics freshness

- **`db.stats_freshness`**  
  Uses **`sys.stats`** plus **`sys.dm_db_stats_properties`** for referenced user tables and views (`last_updated`, **`modification_counter`**, **`rows`**). Warns when stats look stale versus simple heuristics (e.g. old `last_updated` with non-trivial **`rows`**, or high **`modification_counter`** relative to **`rows`**). Permission-sensitive; fails soft if DMV access is denied.

### Redundant / unused indexes

- **`db.redundant_indexes`**  
  Compares NC index key/Include lists among indexes on the same referenced table flags likely redundant pairs (narrower index covered by a wider one with compatible keys/includes).

- **`db.unused_indexes`**  
  **`sys.dm_db_index_usage_stats`** versus **`sys.indexes`** for referenced objects (`user_seeks`/`user_scans`/`user_lookups`/`user_updates`). Warns when an index appears unused for reads yet still incurs writes. Reset after service restart affects DMVs—treat as advisory.

### Foreign key without supporting index

- **`db.fk_missing_index`**  
  **`sys.foreign_keys`** / **`sys.foreign_key_columns`** on referenced parent tables checks for a nonclustered index whose leading key columns match FK column order; warns when missing (common OLTP hotspot for child-table lookups/deletes).

## Recommendation

- Keep **static style/convention rules** in **`suggest`** (Phase 2) — see **`docs/rules.md`**.
- Combine **`suggest`** with **`plan`** when tuning predicates and physical design.
- **`rules-requiring-db-connection`** rules do **not** replace execution-plan analysis for cardinality and spills.
