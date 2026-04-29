# Rules reference

This document lists the rules currently implemented by the rule engine used by the `suggest` command.

Notes:
- Most rules run on **parsed T‑SQL AST** (ScriptDom). If parsing fails, AST-based rules typically do not run.
- Some rules are **text heuristics** (they scan raw `sqlText`), which can produce false positives (for example inside string literals).

---

## Baseline rule

### `tsql.parse_error`
- **Purpose**: Report when `sqlText` could not be parsed as T‑SQL.
- **Type**: Text + parse result.
- **Schema required**: No.
- **Output**: Warn with first ScriptDom parse error location/message.

---

## Core query optimization rules (Phase 2)

These rules directly affect whether SQL can use indexes effectively (sargability, shape of reads/writes). Review them alongside DB-connected checks in **`plan`** (see **`docs/rules-requiring-db-connection.md`**).

| Rule ID | Focus |
|---------|--------|
| `sql.std.non_sargable_predicate` | Predicates that often block seeks |
| `sql.like_leading_wildcard` | `LIKE` patterns that usually cannot use indexes on the leading column |
| `sql.select_star` | Over-fetching columns (more I/O, harder covering indexes) |
| `sql.std.cursor_avoid` | Row-by-row processing instead of set-based SQL |
| `sql.std.merge_prohibited` | `MERGE` complexity and locking behavior (standards / safety) |

### `sql.std.non_sargable_predicate`
- **Purpose**: Heuristically flag functions, casts, and similar constructs inside `WHERE`, `HAVING`, or join `ON` predicates that often prevent or limit index seeks (non-sargable patterns).
- **Type**: AST (heuristic).
- **Schema required**: No.
- **Why it matters for optimization**: Even with a perfect index, wrapping a column in a function (e.g. `YEAR(col) = …`) can force scans or residual filters instead of efficient seeks.
- **Output**: Warning finding with location when matched.
- **Limitations**: Intentionally conservative; may miss real issues or flag rare false positives. Does not know which columns are indexed or actual execution plans.

### `sql.like_leading_wildcard`
- **Purpose**: Detect `LIKE` patterns that start with `%` or `_`, which generally prevent use of a normal B-tree index on that column’s leading edge.
- **Type**: AST.
- **Schema required**: No.
- **Why it matters for optimization**: Leading wildcards usually imply scans or heavy filtering unless specialized indexes (e.g. full-text) apply.
- **Output**: Warning finding.

### `sql.select_star`
- **Purpose**: Detect `SELECT *` (and trivial variants exposed in AST).
- **Type**: AST.
- **Schema required**: No.
- **Why it matters for optimization**: Pulls every column instead of projection needed by the caller; increases I/O and makes narrow covering indexes ineffective.
- **Output**: Warning finding.

### `sql.std.cursor_avoid`
- **Purpose**: Flag T-SQL cursor usage (`DECLARE CURSOR`, `OPEN`, `FETCH`, `CLOSE`, `DEALLOCATE`, and related patterns the rule detects).
- **Type**: AST.
- **Schema required**: No.
- **Why it matters for optimization**: Row-by-row processing scales poorly versus set-based joins/aggregates and can multiply round-trips or locks under load.
- **Output**: Warning finding.

### `sql.std.merge_prohibited`
- **Purpose**: Flag `MERGE` statements when your coding standard prohibits them (complex semantics, concurrency, and tooling concerns).
- **Type**: AST.
- **Schema required**: No.
- **Why it matters for optimization / operations**: Merge plans can differ from INSERT/UPDATE/DELETE equivalents; banning `MERGE` is often a consistency and reviewability choice rather than purely one seek vs scan.
- **Output**: Warning finding.

---

## Other SQL coding-standard rules

### `sql.std.truncate_caution`
- **Purpose**: Warn on `TRUNCATE TABLE` usage (caution for large data / truncate-reload patterns).
- **Type**: AST.
- **Schema required**: No.

### `sql.std.schema_qualified_object`
- **Purpose**: Prefer two-part naming for table/view references (`[schema].[object]`).
- **Type**: AST.
- **Schema required**: No.
- **Limitations**: Skips temp tables (`#...`). Does not validate quoting/brackets.

### `sql.std.join_requires_alias`
- **Purpose**: In multi-table queries, require aliases on base tables.
- **Type**: AST.
- **Schema required**: No.
- **Limitations**: Focuses on `NamedTableReference` in `FROM` and common join shapes.

### `sql.std.snake_case`
- **Purpose**: Enforce snake_case naming for identifiers (tables/columns/aliases).
- **Type**: AST (heuristic on identifier tokens).
- **Schema required**: No.
- **Limitations**:
  - Skips `#temp` and `@variables`.
  - Does not validate/require brackets (`[]`).
  - Does not validate object prefixes (e.g. `sp_`, `vw_`) because DDL is not modeled here.

### `sql.std.indent_tabs`
- **Purpose**: Enforce tab-based indentation (flags leading whitespace that is spaces-only).
- **Type**: Text heuristic.
- **Schema required**: No.
- **Limitations**: Ignores blank lines and comment-only lines; does not auto-fix.

### `sql.std.keyword_uppercase`
- **Purpose**: Prefer UPPERCASE SQL keywords.
- **Type**: Text heuristic.
- **Schema required**: No.
- **Limitations**: May match inside string literals/comments; keyword list is intentionally small.

### `sql.std.xact_abort`
- **Purpose**: If explicit transactions are detected, recommend `SET XACT_ABORT ON`.
- **Type**: Text heuristic.
- **Schema required**: No.
- **Limitations**: Looks for common transaction tokens; not a full control-flow analysis.

### `sql.std.column_alias_qualified`
- **Purpose**: In multi-table queries, selected columns should use `alias.column`.
- **Type**: AST.
- **Schema required**: No.

### `sql.std.bracket_quoted_identifiers`
- **Purpose**: Prefer bracket-quoted identifiers (`[schema].[object]`, etc.) per standard.
- **Type**: AST (identifier quote metadata).
- **Schema required**: No.

### `sql.std.select_column_separate_line`
- **Purpose**: Ensure select-list expressions are on separate lines.
- **Type**: AST (line metadata).
- **Schema required**: No.

### `sql.std.select_modifier_same_line`
- **Purpose**: Keep `TOP`/`DISTINCT` on same line as `SELECT`.
- **Type**: Text heuristic.
- **Schema required**: No.

### `sql.std.predicate_separate_line`
- **Purpose**: Encourage one predicate per line in `WHERE`/`ON`/`HAVING`.
- **Type**: Text heuristic.
- **Schema required**: No.

### `sql.std.prefer_cte_over_nested_query`
- **Purpose**: Prefer CTEs over nested derived subqueries where practical.
- **Type**: Text heuristic.
- **Schema required**: No.

### `sql.std.prefer_temp_table_over_table_variable`
- **Purpose**: Warn on table-variable usage where temp tables are preferred.
- **Type**: Text heuristic.
- **Schema required**: No.

### `sql.std.object_prefix_convention`
- **Purpose**: Validate DDL object-name prefixes (`sp_`, `vw_`, `fn_`, `tvf_`) heuristically.
- **Type**: Text heuristic.
- **Schema required**: No.

### `sql.std.named_constraint_required`
- **Purpose**: Warn when constraints may be unnamed in `CREATE TABLE`.
- **Type**: Text heuristic.
- **Schema required**: No.

### `sql.std.complex_join_comment`
- **Purpose**: For complex multi-join queries, encourage intent comments.
- **Type**: Text heuristic.
- **Schema required**: No.

---

## Where these rules run

- **Phase 2 (`suggest`)**: Runs all rules in **`SqlRepoAnalyzer.Rules.RulesRegistry.DefaultRules`**.
- **Phase 3 (`plan`)**: DB-connected checks only (index/schema/trigger/metadata); outputs **`plans.json`** (automation) and **`markdown/plans.md`** (DBA-readable summary). Combine with **`suggest`** when tuning queries.
- **DB-connection-dependent standards**: Tracked separately in **`docs/rules-requiring-db-connection.md`** (Phase 3 rules such as covering index suitability and indexed objects).
