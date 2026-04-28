using SqlRepoAnalyzer.Core.Logging;
using SqlRepoAnalyzer.Core.Manifest;
using SqlRepoAnalyzer.Core.Output;
using SqlRepoAnalyzer.Core.Crawl;
using SqlRepoAnalyzer.Core.Queries;
using SqlRepoAnalyzer.Core.Reports;
using SqlRepoAnalyzer.Core.SqlFiles;
using SqlRepoAnalyzer.Core.Schema;
using SqlRepoAnalyzer.TypeScript.Node;
using SqlRepoAnalyzer.TypeScript.Extractor;
using SqlRepoAnalyzer.Suggestions;
using SqlRepoAnalyzer.ShowPlan;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();

        return command switch
        {
            "doctor" => RunDoctor(rest).GetAwaiter().GetResult(),
            "scan" => RunScan(rest).GetAwaiter().GetResult(),
            "suggest" => RunSuggest(rest),
            "plan" => RunPlan(rest).GetAwaiter().GetResult(),
            "report" => RunReport(rest),
            _ => Unknown(command),
        };
    }

    static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"Unknown command: {cmd}");
        PrintHelp();
        return 2;
    }

    static void PrintHelp()
    {
        Console.WriteLine("""
SqlRepoAnalyzer - SQL inventory & suggestions tool

Usage:
  SqlRepoAnalyzer doctor --out <dir>
  SqlRepoAnalyzer scan --root <repoRoot> [--out <dir>] [--backend csharp|node|mixed]
  SqlRepoAnalyzer suggest --root <repoRoot> [--out <dir>] [--queries <path>] [--schema <path>] [--rules-version <string>]
  SqlRepoAnalyzer plan --root <repoRoot> [--out <dir>] --enable-showplan [--allow-dml] [--connection <cs>] [--queries <path>] [--timeout-seconds <n>] [--max-queries <n>] [--dry-run]
  SqlRepoAnalyzer report --out <dir>                        (stub; richer summaries planned)

Phase 2:
  - doctor runs environment checks (Node presence/version, out dir writable)
  - scan writes manifest + queries.jsonl (SQL inventory)
  - suggest reads queries.jsonl and writes suggestions.jsonl (static rules + ScriptDom parse)

Phase 3:
  - plan captures SHOWPLAN_XML for SELECT-only inventory rows by default (gated; requires --enable-showplan; connection via --connection or SQLTOOL_CONNECTION_STRING). Use --allow-dml to also allow INSERT/UPDATE/DELETE/MERGE.
""");
    }

    static (string repoRoot, string outDir, bool verbose) ParseCommon(string[] args)
    {
        string repoRoot = ".";
        string? outDir = null;
        bool verbose = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--root":
                    repoRoot = args[++i];
                    break;
                case "--out":
                    outDir = args[++i];
                    break;
                case "--verbose":
                    verbose = true;
                    break;
            }
        }

        outDir ??= Path.Combine(repoRoot, ".sqltool");
        return (Path.GetFullPath(repoRoot), Path.GetFullPath(outDir), verbose);
    }

    /// <summary>
    /// Parses <c>--backend csharp|node|mixed</c>. Defaults to <c>mixed</c> when omitted. Last flag wins.
    /// </summary>
    static bool TryParseBackend(string[] args, out string backend, out string? error)
    {
        backend = "mixed";
        error = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], "--backend", StringComparison.OrdinalIgnoreCase))
                continue;
            if (i + 1 >= args.Length)
            {
                error = "Missing value for --backend (csharp, node, or mixed).";
                return false;
            }

            var v = args[++i].Trim().ToLowerInvariant();
            switch (v)
            {
                case "csharp":
                case "node":
                case "mixed":
                    backend = v;
                    break;
                default:
                    error = $"Invalid --backend '{v}'. Use csharp, node, or mixed.";
                    return false;
            }
        }

        return true;
    }

    static async Task<int> RunDoctor(string[] args)
    {
        var (_, outDir, verbose) = ParseCommon(args);
        var paths = new OutputPaths(outDir);
        var log = new Logger(
            minConsoleLevel: verbose ? LogLevel.Debug : LogLevel.Info,
            logFilePath: paths.LogPath,
            minFileLevel: LogLevel.Debug);

        log.Info("Doctor start", new Dictionary<string, object?> { ["outDir"] = outDir });

        try
        {
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, ".write-test"), "ok");
            File.Delete(Path.Combine(outDir, ".write-test"));
            log.Info("Output directory writable");
        }
        catch (Exception ex)
        {
            log.Error("Output directory not writable", new Dictionary<string, object?> { ["outDir"] = outDir }, ex);
            return 1;
        }

        var node = await NodeTooling.CheckNodeAsync(log, CancellationToken.None);
        if (!node.Ok)
        {
            log.Error("Node check failed", new Dictionary<string, object?> { ["error"] = node.Error });
            return 1;
        }

        log.Info("Doctor ok");
        return 0;
    }

    static async Task<int> RunScan(string[] args)
    {
        var (repoRoot, outDir, verbose) = ParseCommon(args);
        if (!TryParseBackend(args, out var backend, out var backendError))
        {
            Console.Error.WriteLine(backendError);
            return 2;
        }

        var paths = new OutputPaths(outDir);
        var log = new Logger(
            minConsoleLevel: verbose ? LogLevel.Debug : LogLevel.Info,
            logFilePath: paths.LogPath,
            minFileLevel: LogLevel.Debug);

        log.Info("Scan start", new Dictionary<string, object?>
        {
            ["repoRoot"] = repoRoot,
            ["outDir"] = outDir,
            ["backend"] = backend
        });
        Directory.CreateDirectory(outDir);

        var node = await NodeTooling.CheckNodeAsync(log, CancellationToken.None);
        if (!node.Ok)
        {
            log.Error("Node check failed (required for TS extraction). Run doctor for details.", new Dictionary<string, object?> { ["error"] = node.Error });
            return 1;
        }

        var crawlOptions = new CrawlOptions(
            RepoRoot: repoRoot,
            MaxFileSizeBytes: 500 * 1024,
            IncludeExtensions: new[] { ".ts", ".tsx", ".js", ".jsx", ".sql" },
            ExcludeDirNames: new[] { "node_modules", "dist", "build", ".git", ".sqltool" }
        );
        var allFiles = FileCrawler.EnumerateFiles(crawlOptions).ToList();
        var sqlFiles = allFiles.Where(f => Path.GetExtension(f).Equals(".sql", StringComparison.OrdinalIgnoreCase)).ToList();
        var tsFiles = allFiles.Where(f =>
            Path.GetExtension(f).Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(f).Equals(".tsx", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(f).Equals(".js", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(f).Equals(".jsx", StringComparison.OrdinalIgnoreCase)).ToList();

        var candidates = new List<QueryCandidate>();

        foreach (var sf in sqlFiles)
        {
            try
            {
                candidates.AddRange(SqlFileExtractor.ExtractFromFile(sf));
            }
            catch (Exception ex)
            {
                log.Warn("Failed to extract from .sql file", new Dictionary<string, object?> { ["file"] = sf }, ex);
            }
        }

        var tsExtractor = new TypeScriptExtractor(log);
        var tsCandidates = await tsExtractor.ExtractAsync(repoRoot, outDir, tsFiles, CancellationToken.None);
        candidates.AddRange(tsCandidates);

        var records = QueryMerger.MergeAndFingerprint(repoRoot, candidates);
        JsonlWriter.WriteJsonLines(paths.QueriesPath, records);

        var counts = new Dictionary<string, object?>
        {
            ["phase"] = 1,
            ["maxFileSizeBytes"] = crawlOptions.MaxFileSizeBytes,
            ["includeExtensions"] = crawlOptions.IncludeExtensions,
            ["excludedDirNames"] = crawlOptions.ExcludeDirNames,
            ["crawledFileCount"] = allFiles.Count,
            ["sqlFileCount"] = sqlFiles.Count,
            ["tsFileCount"] = tsFiles.Count,
            ["candidateCount"] = candidates.Count,
            ["queryRecordCount"] = records.Count,
            ["backend"] = backend
        };

        ManifestWriter.WriteManifest(paths.ManifestPath, new ManifestRecord(
            ReportSchemaVersion: 1,
            ToolVersion: "0.1.0-phase1",
            GeneratedAtUtc: DateTimeOffset.UtcNow.ToString("o"),
            RepoRoot: repoRoot,
            OutDir: outDir,
            GitSha: null,
            RulesVersion: null,
            SchemaFingerprint: null,
            Backend: backend,
            Config: counts
        ));

        log.Info("Scan complete", new Dictionary<string, object?> { ["manifest"] = paths.ManifestPath, ["queries"] = paths.QueriesPath });
        return await Task.FromResult(0);
    }

    static int RunSuggest(string[] args)
    {
        var (repoRoot, outDir, verbose) = ParseCommon(args);
        var paths = new OutputPaths(outDir);
        var log = new Logger(
            minConsoleLevel: verbose ? LogLevel.Debug : LogLevel.Info,
            logFilePath: paths.LogPath,
            minFileLevel: LogLevel.Debug);

        var (queriesPath, schemaPath, rulesVersion) = ParseSuggestArgs(args, paths);

        log.Info("Suggest start", new Dictionary<string, object?>
        {
            ["repoRoot"] = repoRoot,
            ["outDir"] = outDir,
            ["queries"] = queriesPath,
            ["schema"] = schemaPath ?? "(none)",
            ["rulesVersion"] = rulesVersion
        });

        Directory.CreateDirectory(outDir);

        if (!File.Exists(queriesPath))
        {
            log.Error("queries.jsonl not found", new Dictionary<string, object?> { ["queries"] = queriesPath });
            return 1;
        }

        SchemaSnapshot? schema = null;
        string? schemaFingerprint = null;
        if (!string.IsNullOrWhiteSpace(schemaPath))
        {
            try
            {
                schema = SchemaSnapshotLoader.Load(schemaPath);
                schemaFingerprint = SchemaSnapshotFingerprinter.Sha256Hex(schema);
                log.Info("Schema snapshot loaded", new Dictionary<string, object?>
                {
                    ["schemaPath"] = schemaPath,
                    ["schemaFingerprint"] = schemaFingerprint
                });
            }
            catch (Exception ex)
            {
                log.Error("Failed to load schema snapshot", new Dictionary<string, object?> { ["schemaPath"] = schemaPath! }, ex);
                return 1;
            }
        }

        var queries = SuggestionService.ReadQueriesJsonl(queriesPath);
        var suggestions = SuggestionService.BuildSuggestions(queries, schema);
        SuggestionService.WriteSuggestionsJsonl(paths.SuggestionsPath, suggestions);

        var preservedBackend = ManifestReader.TryReadBackend(paths.ManifestPath);

        ManifestWriter.WriteManifest(paths.ManifestPath, new ManifestRecord(
            ReportSchemaVersion: 2,
            ToolVersion: "0.2.0-phase2",
            GeneratedAtUtc: DateTimeOffset.UtcNow.ToString("o"),
            RepoRoot: repoRoot,
            OutDir: outDir,
            GitSha: null,
            RulesVersion: rulesVersion,
            SchemaFingerprint: schemaFingerprint,
            Backend: preservedBackend,
            Config: new Dictionary<string, object?>
            {
                ["phase"] = 2,
                ["rulesVersion"] = rulesVersion,
                ["schemaPath"] = schemaPath,
                ["schemaFingerprint"] = schemaFingerprint,
                ["queryCount"] = queries.Count,
                ["suggestionCount"] = suggestions.Count
            }
        ));

        log.Info("Suggest complete", new Dictionary<string, object?> { ["suggestions"] = paths.SuggestionsPath });
        return 0;
    }

    static (string queriesPath, string? schemaPath, string rulesVersion) ParseSuggestArgs(string[] args, OutputPaths defaults)
    {
        string queriesPath = defaults.QueriesPath;
        string? schemaPath = null;
        var rulesVersion = "0.2.0";

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--queries":
                    queriesPath = Path.GetFullPath(args[++i]);
                    break;
                case "--schema":
                    schemaPath = Path.GetFullPath(args[++i]);
                    break;
                case "--rules-version":
                    rulesVersion = args[++i];
                    break;
            }
        }

        return (queriesPath, schemaPath, rulesVersion);
    }

    static async Task<int> RunPlan(string[] args)
    {
        var (repoRoot, outDir, verbose) = ParseCommon(args);
        var paths = new OutputPaths(outDir);
        var log = new Logger(
            minConsoleLevel: verbose ? LogLevel.Debug : LogLevel.Info,
            logFilePath: paths.LogPath,
            minFileLevel: LogLevel.Debug);

        var options = ParsePlanArgs(args, paths);

        if (!options.EnableShowplanAcknowledged)
        {
            Console.Error.WriteLine(
                "Refusing to run: `plan` touches a live SQL Server (compile-only SHOWPLAN_XML for SELECT-shaped text). " +
                "Re-run with --enable-showplan after you intend to run this against the given connection.");
            return 2;
        }

        log.Info("Plan start", new Dictionary<string, object?>
        {
            ["repoRoot"] = repoRoot,
            ["outDir"] = outDir,
            ["queries"] = options.QueriesPath,
            ["dryRun"] = options.DryRun,
            ["maxQueries"] = options.MaxQueries,
            ["timeoutSeconds"] = options.CommandTimeoutSeconds,
            ["connection"] = string.IsNullOrWhiteSpace(options.ConnectionString)
                ? "(none)"
                : ShowPlanConnection.Describe(options.ConnectionString)
        });

        Directory.CreateDirectory(outDir);

        if (!File.Exists(options.QueriesPath))
        {
            log.Error("queries.jsonl not found", new Dictionary<string, object?> { ["queries"] = options.QueriesPath });
            return 1;
        }

        if (!options.DryRun && string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            log.Error("Missing connection string for plan. Use --connection \"...\" or set environment variable SQLTOOL_CONNECTION_STRING.");
            return 1;
        }

        var records = await PlanRunService.RunAsync(options, CancellationToken.None).ConfigureAwait(false);
        var ok = records.Count(r => string.Equals(r.Status, "ok", StringComparison.OrdinalIgnoreCase));

        var planPreservedBackend = ManifestReader.TryReadBackend(paths.ManifestPath);

        ManifestWriter.WriteManifest(paths.ManifestPath, new ManifestRecord(
            ReportSchemaVersion: 3,
            ToolVersion: "0.3.0-phase3",
            GeneratedAtUtc: DateTimeOffset.UtcNow.ToString("o"),
            RepoRoot: repoRoot,
            OutDir: outDir,
            GitSha: null,
            RulesVersion: null,
            SchemaFingerprint: null,
            Backend: planPreservedBackend,
            Config: new Dictionary<string, object?>
            {
                ["phase"] = 3,
                ["planDryRun"] = options.DryRun,
                ["planRecordCount"] = records.Count,
                ["planOkCount"] = ok,
                ["queriesPath"] = options.QueriesPath,
                ["connectionSummary"] = string.IsNullOrWhiteSpace(options.ConnectionString)
                    ? null
                    : ShowPlanConnection.Describe(options.ConnectionString)
            }
        ));

        log.Info("Plan complete", new Dictionary<string, object?>
        {
            ["plans"] = paths.PlansJsonlPath,
            ["showplanXmlDir"] = paths.ShowPlanXmlDirectory,
            ["okCount"] = ok
        });

        return 0;
    }

    static PlanRunOptions ParsePlanArgs(string[] args, OutputPaths defaults)
    {
        var queriesPath = defaults.QueriesPath;
        var connection = Environment.GetEnvironmentVariable("SQLTOOL_CONNECTION_STRING");
        var timeout = 30;
        var maxQueries = 50;
        var enable = false;
        var allowDml = false;
        var dryRun = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--queries":
                    queriesPath = Path.GetFullPath(args[++i]);
                    break;
                case "--connection":
                    connection = args[++i];
                    break;
                case "--timeout-seconds":
                    timeout = int.TryParse(args[++i], out var t) ? t : 30;
                    break;
                case "--max-queries":
                    maxQueries = int.TryParse(args[++i], out var m) ? m : 50;
                    break;
                case "--enable-showplan":
                    enable = true;
                    break;
                case "--allow-dml":
                    allowDml = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
            }
        }

        timeout = Math.Clamp(timeout, 1, 600);
        maxQueries = Math.Clamp(maxQueries, 1, 10_000);

        return new PlanRunOptions(
            QueriesPath: queriesPath,
            OutDir: defaults.OutDir,
            ConnectionString: string.IsNullOrWhiteSpace(connection) ? null : connection,
            CommandTimeoutSeconds: timeout,
            MaxQueries: maxQueries,
            EnableShowplanAcknowledged: enable,
            AllowDml: allowDml,
            DryRun: dryRun);
    }

    static int RunReport(string[] args)
    {
        var outDir = ".sqltool";
        var verbose = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out":
                    outDir = args[++i];
                    break;
                case "--verbose":
                    verbose = true;
                    break;
            }
        }

        var paths = new OutputPaths(Path.GetFullPath(outDir));
        var log = new Logger(
            minConsoleLevel: verbose ? LogLevel.Debug : LogLevel.Info,
            logFilePath: paths.LogPath,
            minFileLevel: LogLevel.Debug);

        log.Warn("Report is a stub (summaries/formatting planned)");
        Console.WriteLine($"outDir={paths.OutDir}");
        Console.WriteLine($"manifest={paths.ManifestPath}");
        Console.WriteLine($"queries={paths.QueriesPath}");
        Console.WriteLine($"suggestions={paths.SuggestionsPath}");
        Console.WriteLine($"plans={paths.PlansJsonlPath}");
        Console.WriteLine($"showplanXml={paths.ShowPlanXmlDirectory}");
        return 0;
    }
}
