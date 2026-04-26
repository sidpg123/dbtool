# Schema snapshot format (SQL Server)

Optional input to `suggest --schema <path>`. The file is JSON; property names are **case-insensitive** on read, but the tool’s `schemaFingerprint` is computed from **canonical camelCase** JSON (see `SchemaSnapshotFingerprinter`).

## Top-level

- `engine` (string, optional) — informational; expected `mssql`
- `database` (string, optional)
- `capturedAtUtc` (string, optional) — ISO-8601 recommended
- `tables` (array)

## `tables[]`

- `schema` (string, optional) — defaults to `dbo` when omitted in rules (`UnknownTableReferenceRule`)
- `name` (string, required)
- `columns` (array, optional)
  - `name` (string)
  - `type` (string, optional)
  - `nullable` (boolean, optional)
- `indexes` (array, optional) — reserved for future rules
  - `name` (string)
  - `isUnique` (boolean, optional)
  - `keys` (array)
    - `column` (string)
    - `descending` (boolean, optional)
  - `includes` (array of strings)

## Example

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
          "keys": [{ "column": "customer_id", "descending": false }],
          "includes": ["id"]
        }
      ]
    }
  ]
}
```

## Notes for rule authors

- `UnknownTableReferenceRule` resolves `NamedTableReference` to `schema.name` using **two-part** names (`dbo` default). Three-part / four-part names are not modeled yet.
