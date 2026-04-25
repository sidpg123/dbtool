# Schema snapshot format (Phase 0 placeholder)

This format will be **locked** in Phase 0 before any schema-dependent rule is written.

## Goals

- Represent SQL Server schema needed for rules: tables, columns, types, PK/FK, indexes (key + include), computed columns.
- Provide a stable `schemaFingerprint` derivation (hash of canonicalized snapshot).

## Proposed top-level (draft)

```json
{
  "engine": "mssql",
  "capturedAtUtc": "2026-04-25T00:00:00Z",
  "database": "MyDb",
  "tables": [
    {
      "schema": "dbo",
      "name": "Orders",
      "columns": [
        { "name": "id", "type": "bigint", "nullable": false },
        { "name": "customer_id", "type": "bigint", "nullable": false }
      ],
      "indexes": [
        {
          "name": "IX_Orders_customer_id",
          "isUnique": false,
          "keys": [ { "column": "customer_id", "descending": false } ],
          "includes": [ "id" ]
        }
      ]
    }
  ]
}
```

