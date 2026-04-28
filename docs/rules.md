# Rules reference

This document lists the rules currently implemented by the rule engine used by the `suggest` command.

Notes:
- Most rules run on **parsed T‑SQL AST** (ScriptDom). If parsing fails, AST-based rules typically do not run.
- Some rules are **text heuristics** (they scan raw `sqlText`), which can produce false positives (for example inside string literals).
- Only the rules labeled **Schema required** need `suggest --schema ...`.

## Baseline rules (already present)

### `tsql.parse_error`
- **Purpose**: Report when `sqlText` could not be parsed as T‑SQL.
- **Type**: Text + parse result.
- **Schema required**: No.
- **Output**: Warn with first ScriptDom parse error location/message.

### `sql.select_star`
- **Purpose**: Detect `SELECT *`.
- **Type**: AST.
- **Schema required**: No.

### `sql.like_leading_wildcard`
- **Purpose**: Detect `LIKE` patterns starting with `%` or `_` (often non-sargable).
- **Type**: AST.
- **Schema required**: No.

## SQL coding-standard rules (added)

### `sql.std.merge_prohibited`
- **Purpose**: Flag `MERGE` statements (prohibited by standard).
- **Type**: AST.
- **Schema required**: No.

### `sql.std.cursor_avoid`
- **Purpose**: Flag cursor usage (DECLARE/OPEN/FETCH/CLOSE/DEALLOCATE).
- **Type**: AST.
- **Schema required**: No.

### `sql.std.truncate_caution`
- **Purpose**: Warn on `TRUNCATE TABLE` usage (caution for large data / truncate-reload patterns).
- **Type**: AST.
- **Schema required**: No.

### `sql.std.non_sargable_predicate`
- **Purpose**: Heuristically flag functions/casts inside `WHERE`, `HAVING`, or join `ON` predicates that often reduce sargability.
- **Type**: AST (heuristic).
- **Schema required**: No.
- **Limitations**: This is intentionally conservative and may produce false positives/negatives; it does not know which columns are indexed.

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

## Where these rules run

- **Phase 2 (`suggest`)**: runs all rules in `SqlRepoAnalyzer.Rules.RulesRegistry.DefaultRules`.
- **Phase 3 (`plan`)**: does not run these rules directly; it analyzes `SHOWPLAN_XML` and emits plan findings separately.
- **DB-connection-dependent standards**: tracked separately in `docs/rules-requiring-db-connection.md` (includes `schema.unknown_table`).

