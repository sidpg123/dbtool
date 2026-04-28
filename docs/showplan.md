# SHOWPLAN_XML (`plan` command)

Phase 3 connects to **SQL Server** and, for eligible rows in `queries.json`, runs `SET SHOWPLAN_XML ON` and returns **estimated** showplan XML (result rows are not executed; the batch is still **compiled** on the server).

## Safety / gating

- The CLI **refuses** to run unless you pass **`--enable-showplan`** (explicit opt-in).
- Use **`--dry-run`** to classify inventory rows without opening a connection for captures (connection string not required).
- For real captures, provide **`SQLTOOL_CONNECTION_STRING`** or **`--connection "..."`**. Logs only a short summary (`DataSource` + `InitialCatalog`), never the password.

## Eligibility (client-side)

By default, only batches that parse as `TSqlScript` and contain **at least one `SELECT`** are eligible. Other statement kinds are rejected except a small allowlist of harmless `SET` forms (`SET ANSI_NULLS ON`, isolation level, etc.).

If you pass **`--allow-dml`**, then `INSERT`, `UPDATE`, `DELETE`, and `MERGE` statements are also eligible for SHOWPLAN_XML capture (still compile-only; still gated by `--enable-showplan`).

## Limits

- **`--max-queries`** (default `50`, max `10000`): caps how many **eligible** rows are processed (including `--dry-run` rows).
- **`--timeout-seconds`** (default `30`, max `600`): command timeout for each round trip.

## Outputs

- **`plans.json`**: one record per `queries.json` row processed (see `docs/report-schema.md`).
- **`showplan-xml/<queryId>.xml`**: raw SHOWPLAN_XML for `status: ok` rows.

## Manifest

`plan` writes `manifest.json` with `reportSchemaVersion: 3` and counters under `config` (`planOkCount`, etc.).
