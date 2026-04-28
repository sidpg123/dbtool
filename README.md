# SQL Repo Analyzer

Simple CLI tool to:

1. Find SQL used in your codebase (`scan`)
2. Generate static findings (`suggest`)
3. Optionally capture SQL Server estimated plans (`plan`)

Outputs are written to `.sqltool/`.

## Requirements

- .NET SDK
- Node.js

## Use it the easy way

Run from CLI project folder once:

```powershell
Set-Location "c:\Users\NinadBandiwadekarIND\Repos\DB Optimization Tool\dbtool\src\SqlRepoAnalyzer.Cli"
```

Then you can run `dotnet run -- ...` without `--project`.

## Quick start

```powershell
# one-time setup
dotnet build "c:\Users\NinadBandiwadekarIND\Repos\DB Optimization Tool\dbtool\SqlRepoAnalyzer.sln"
npm --prefix "c:\Users\NinadBandiwadekarIND\Repos\DB Optimization Tool\dbtool\assets\ts-extractor" install

# 1) check environment
dotnet run -- doctor --out "c:\Users\NinadBandiwadekarIND\Repos\DB Optimization Tool\dbtool\.sqltool" --verbose

# 2) inventory SQL
dotnet run -- scan --root "c:\Users\NinadBandiwadekarIND\Repos\DB Optimization Tool\dbtool" --out "c:\Users\NinadBandiwadekarIND\Repos\DB Optimization Tool\dbtool\.sqltool"

# 2b) inventory only static SELECT queries (exclude dynamic/no-SQL entries)
dotnet run -- scan --root "c:\Users\NinadBandiwadekarIND\Repos\DB Optimization Tool\dbtool" --out "c:\Users\NinadBandiwadekarIND\Repos\DB Optimization Tool\dbtool\.sqltool" --query-scope select

# 3) suggestions
dotnet run -- suggest --root "c:\Users\NinadBandiwadekarIND\Repos\DB Optimization Tool\dbtool" --out "c:\Users\NinadBandiwadekarIND\Repos\DB Optimization Tool\dbtool\.sqltool"
```

## Optional plan capture

`plan` needs `--enable-showplan` and a SQL Server connection string.

```powershell
$env:SQLTOOL_CONNECTION_STRING = "Server=localhost;Database=YourDb;Integrated Security=true;TrustServerCertificate=true"
dotnet run -- plan --root "c:\Users\NinadBandiwadekarIND\Repos\DB Optimization Tool\dbtool" --out "c:\Users\NinadBandiwadekarIND\Repos\DB Optimization Tool\dbtool\.sqltool" --enable-showplan --dry-run
```

Use `--dry-run` first to validate selection safely.

## What each command does

- `doctor`: checks output folder + Node installation
- `scan`: creates `.sqltool/queries.json`
- `suggest`: creates `.sqltool/suggestions.json`
- `plan`: creates `.sqltool/plans.json` + `.sqltool/showplan-xml/*.xml`
- `report`: placeholder/stub

## Output files

- `.sqltool/manifest.json`
- `.sqltool/queries.json`
- `.sqltool/suggestions.json`
- `.sqltool/plans.json` (after `plan`)
- `.sqltool/plan-suggestions.json` (after `plan`)

## Notes

- `--verbose` = more detailed logs in terminal.
- `--query-scope all|select` (scan only): `all` is default; `select` keeps only static SELECT-shaped SQL and excludes dynamic/no-SQL queries.
- VS Code/Cursor tasks are available in `.vscode/tasks.json` (`doctor`, `scan`, `suggest`, `custom args`).