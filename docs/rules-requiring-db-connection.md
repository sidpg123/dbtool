# Rules requiring DB connection (separate list)

This file tracks coding standards that cannot be validated reliably from SQL text/AST alone and should use live database metadata or runtime plan inspection.

## Requires DB connection and/or runtime plan data

- **`schema.unknown_table`**  
  Moved out of the default static `suggest` ruleset. This check should run in the DB-dependent set because reliable table existence validation depends on live schema context (or a DB-synced snapshot).

- **Every query has a fully covering index**  
  Needs index metadata plus actual/estimated execution plan behavior to validate key lookup trade-offs.

- **Validate index suitability for predicates/join order/selectivity**  
  Requires cardinality + optimizer choices from plan analysis; static text is insufficient.

- **Confirm minimal dataset extraction in production context**  
  Static rules can warn on `SELECT *`, but proving "minimal" requires workload and plan statistics.

- **Heavy trigger impact verification**  
  Trigger body complexity can be linted statically, but runtime impact/row-by-row cost needs plan/runtime data.

## Recommendation

- Keep **static style/convention rules** in `suggest` (Phase 2).
- Keep **runtime/index effectiveness rules** in `plan` (Phase 3) where SHOWPLAN data is available.

