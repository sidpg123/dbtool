# DB connections config format (Phase 3)

Phase 3 (`plan`) reads DB connections from JSON. Connection strings are not passed in CLI args.

## Where `plan` loads the file

Resolution order:

1. **`--db-config <path>`** — use that file (must exist).
2. **`<outDir>/db-connections.json`** — e.g. `.sqltool/db-connections.json` next to `queries.json` (typical for **per-repo secrets** committed or gitignored).
3. **Bundled template** — `db-connections.json` copied next to `SqlRepoAnalyzer.dll` when you build or publish the tool (placeholder `localhost`-style connection only).

If you do not need a separate file in the scanned repo, omit (2) and the tool uses (3). Override with (1) for CI secrets or non-default locations.

## Environment selection

Resolution order:

1. `--env <name>` (if provided)
2. `defaultEnvironment` in config
3. fail if unresolved

## JSON schema

```json
{
  "defaultEnvironment": "dev",
  "environments": {
    "dev": {
      "connectionString": "Server=localhost;Database=AppDb;Integrated Security=true;TrustServerCertificate=true"
    },
    "qa": {
      "connectionString": "Server=qa-sql;Database=AppDb;User Id=app;Password=***;TrustServerCertificate=true"
    }
  }
}
```

## Security notes

- Treat this file as sensitive when it contains passwords.
- Do not commit real credentials.
- Prefer environment-specific secure secret handling in CI/CD where possible.

