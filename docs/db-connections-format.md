# DB connections config format (Phase 3)

Phase 3 (`plan`) reads DB connections from JSON. Connection strings are not passed in CLI args.

## Default path

- `<outDir>/db-connections.json`  
  Example: `.sqltool/db-connections.json`
- Sample template in repo: `.sqltool/db-connections.example.json`

Use `--db-config <path>` to override.

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

