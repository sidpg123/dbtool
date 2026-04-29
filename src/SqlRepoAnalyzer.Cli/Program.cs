using SqlRepoAnalyzer.Core.Logging;
using SqlRepoAnalyzer.Core.Manifest;
using SqlRepoAnalyzer.Core.Output;
using SqlRepoAnalyzer.Core.Crawl;
using SqlRepoAnalyzer.Core.Queries;
using SqlRepoAnalyzer.Core.Reports;
using SqlRepoAnalyzer.Core.SqlFiles;
using SqlRepoAnalyzer.Core.Phase3;
using SqlRepoAnalyzer.Core.Tsql;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlRepoAnalyzer.TypeScript.Node;
using SqlRepoAnalyzer.TypeScript.Extractor;
using SqlRepoAnalyzer.Suggestions;

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
  SqlRepoAnalyzer scan --root <repoRoot> [--out <dir>] [--backend csharp|node|mixed] [--query-scope all|select]
  SqlRepoAnalyzer suggest --root <repoRoot> [--out <dir>] [--queries <path>] [--rules-version <string>]
  SqlRepoAnalyzer plan --root <repoRoot> [--out <dir>] [--queries <path>] [--db-config <path>] [--env <name>]
  SqlRepoAnalyzer report --out <dir>                        (stub; richer summaries planned)

Phase 2:
  - doctor runs environment checks (Node presence/version, out dir writable)
  - scan writes manifest + queries.json (SQL inventory) and markdown/queries.md
  - suggest reads queries.json and writes suggestions.json + markdown/suggestions.md

Phase 3:
  - plan runs DB-connected checks using db-connections.json and writes plans.json + markdown/plans.md
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

    /// <summary>
    /// Parses <c>--query-scope all|select</c> for scan. Defaults to <c>all</c> when omitted. Last flag wins.
    /// </summary>
    static bool TryParseQueryScope(string[] args, out string queryScope, out string? error)
    {
        queryScope = "all";
        error = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], "--query-scope", StringComparison.OrdinalIgnoreCase))
                continue;
            if (i + 1 >= args.Length)
            {
                error = "Missing value for --query-scope (all or select).";
                return false;
            }

            var v = args[++i].Trim().ToLowerInvariant();
            switch (v)
            {
                case "all":
                case "select":
                    queryScope = v;
                    break;
                default:
                    error = $"Invalid --query-scope '{v}'. Use all or select.";
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
        if (!TryParseQueryScope(args, out var queryScope, out var queryScopeError))
        {
            Console.Error.WriteLine(queryScopeError);
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
            ["backend"] = backend,
            ["queryScope"] = queryScope
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

        var preFilterCandidateCount = candidates.Count;
        if (string.Equals(queryScope, "select", StringComparison.OrdinalIgnoreCase))
        {
            candidates = candidates
                .Where(c => !string.IsNullOrWhiteSpace(c.SqlText))
                .Where(c => c.SourceKind != SourceKind.TypeOrmQueryDynamic)
                .Where(c => IsSelectOnlyCandidate(c.SqlText!))
                .ToList();
        }

        if (string.Equals(queryScope, "select", StringComparison.OrdinalIgnoreCase)
            && preFilterCandidateCount > 0
            && candidates.Count == 0)
        {
            log.Warn(
                "scan: query-scope select removed every candidate. Inventory will be empty. Use default scope (omit flag or use --query-scope all), or terminate statements with semicolons so each fragment is ONLY static SELECTs (mixed MERGE/INSERT/DELETE/select without ';' binds into one blob and fails the filter).",
                new Dictionary<string, object?>
                {
                    ["candidatesBeforeSelectScopeFilter"] = preFilterCandidateCount
                });
        }

        var records = QueryMerger.MergeAndFingerprint(repoRoot, candidates);
        JsonlWriter.WriteJsonArray(paths.QueriesPath, records);
        QueriesMarkdownFormatter.WriteUtf8File(paths.QueriesMarkdownPath, records, DateTimeOffset.UtcNow.ToString("o"));

        var counts = new Dictionary<string, object?>
        {
            ["phase"] = 1,
            ["maxFileSizeBytes"] = crawlOptions.MaxFileSizeBytes,
            ["includeExtensions"] = crawlOptions.IncludeExtensions,
            ["excludedDirNames"] = crawlOptions.ExcludeDirNames,
            ["crawledFileCount"] = allFiles.Count,
            ["sqlFileCount"] = sqlFiles.Count,
            ["tsFileCount"] = tsFiles.Count,
            ["candidateCountBeforeQueryScopeFilter"] = preFilterCandidateCount,
            ["candidateCount"] = candidates.Count,
            ["queryRecordCount"] = records.Count,
            ["backend"] = backend,
            ["queryScope"] = queryScope
        };

        ManifestWriter.WriteManifest(paths.ManifestPath, new ManifestRecord(
            ReportSchemaVersion: 1,
            ToolVersion: "0.1.0-phase1",
            GeneratedAtUtc: DateTimeOffset.UtcNow.ToString("o"),
            RepoRoot: repoRoot,
            OutDir: outDir,
            GitSha: null,
            RulesVersion: null,
            Backend: backend,
            Config: counts
        ));

        log.Info("Scan complete", new Dictionary<string, object?>
        {
            ["manifest"] = paths.ManifestPath,
            ["queries"] = paths.QueriesPath,
            ["queriesMarkdown"] = paths.QueriesMarkdownPath
        });
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

        var (queriesPath, rulesVersion) = ParseSuggestArgs(args, paths);

        log.Info("Suggest start", new Dictionary<string, object?>
        {
            ["repoRoot"] = repoRoot,
            ["outDir"] = outDir,
            ["queries"] = queriesPath,
            ["rulesVersion"] = rulesVersion
        });

        Directory.CreateDirectory(outDir);

        if (!File.Exists(queriesPath))
        {
            log.Error("queries.json not found", new Dictionary<string, object?> { ["queries"] = queriesPath });
            return 1;
        }

        var queries = SuggestionService.ReadQueriesJson(queriesPath);
        var suggestions = SuggestionService.BuildSuggestions(queries);
        SuggestionService.WriteSuggestionsJson(paths.SuggestionsPath, suggestions);
        SuggestionsMarkdownFormatter.WriteUtf8File(paths.SuggestionsMarkdownPath, suggestions, DateTimeOffset.UtcNow.ToString("o"));

        var preservedBackend = ManifestReader.TryReadBackend(paths.ManifestPath);

        ManifestWriter.WriteManifest(paths.ManifestPath, new ManifestRecord(
            ReportSchemaVersion: 2,
            ToolVersion: "0.2.0-phase2",
            GeneratedAtUtc: DateTimeOffset.UtcNow.ToString("o"),
            RepoRoot: repoRoot,
            OutDir: outDir,
            GitSha: null,
            RulesVersion: rulesVersion,
            Backend: preservedBackend,
            Config: new Dictionary<string, object?>
            {
                ["phase"] = 2,
                ["rulesVersion"] = rulesVersion,
                ["queryCount"] = queries.Count,
                ["suggestionCount"] = suggestions.Count
            }
        ));

        log.Info("Suggest complete", new Dictionary<string, object?>
        {
            ["suggestions"] = paths.SuggestionsPath,
            ["suggestionsMarkdown"] = paths.SuggestionsMarkdownPath
        });
        return 0;
    }

    static (string queriesPath, string rulesVersion) ParseSuggestArgs(string[] args, OutputPaths defaults)
    {
        string queriesPath = defaults.QueriesPath;
        var rulesVersion = "0.2.0";

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--queries":
                    queriesPath = Path.GetFullPath(args[++i]);
                    break;
                case "--rules-version":
                    rulesVersion = args[++i];
                    break;
            }
        }

        return (queriesPath, rulesVersion);
    }

    static async Task<int> RunPlan(string[] args)
    {
        var (repoRoot, outDir, verbose) = ParseCommon(args);
        var paths = new OutputPaths(outDir);
        var log = new Logger(
            minConsoleLevel: verbose ? LogLevel.Debug : LogLevel.Info,
            logFilePath: paths.LogPath,
            minFileLevel: LogLevel.Debug);

        var (queriesPath, dbConfigPath, envName) = ParsePlanArgs(args, paths);

        log.Info("Plan start", new Dictionary<string, object?>
        {
            ["repoRoot"] = repoRoot,
            ["outDir"] = outDir,
            ["queries"] = queriesPath,
            ["dbConfig"] = dbConfigPath,
            ["env"] = envName ?? "(default)"
        });

        Directory.CreateDirectory(outDir);

        if (!File.Exists(queriesPath))
        {
            log.Error("queries.json not found", new Dictionary<string, object?> { ["queries"] = queriesPath });
            return 1;
        }

        if (!File.Exists(dbConfigPath))
        {
            log.Error("DB config file not found", new Dictionary<string, object?> { ["dbConfig"] = dbConfigPath });
            return 1;
        }

        Phase3DbConfig config;
        string resolvedEnv;
        string connectionString;
        try
        {
            config = Phase3DbConfigLoader.Load(dbConfigPath);
            (resolvedEnv, connectionString) = Phase3DbConfigLoader.ResolveConnection(config, envName);
        }
        catch (Exception ex)
        {
            log.Error("Failed to resolve DB connection from config", new Dictionary<string, object?>
            {
                ["dbConfig"] = dbConfigPath,
                ["env"] = envName ?? "(default)"
            }, ex);
            return 1;
        }

        var queries = SuggestionService.ReadQueriesJson(queriesPath);
        Phase3PlansReport report;
        try
        {
            report = await Phase3DbRuleService.RunAsync(resolvedEnv, connectionString, queries, log, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Error("Phase3 DB checks failed", new Dictionary<string, object?>
            {
                ["environment"] = resolvedEnv,
                ["dbConfig"] = dbConfigPath
            }, ex);
            return 1;
        }
        JsonlWriter.WriteJsonObject(paths.PlansPath, report);
        Phase3PlansMarkdownFormatter.WriteUtf8File(paths.PlansMarkdownPath, report);

        var planPreservedBackend = ManifestReader.TryReadBackend(paths.ManifestPath);

        ManifestWriter.WriteManifest(paths.ManifestPath, new ManifestRecord(
            ReportSchemaVersion: 3,
            ToolVersion: "0.3.0-phase3-dbchecks",
            GeneratedAtUtc: DateTimeOffset.UtcNow.ToString("o"),
            RepoRoot: repoRoot,
            OutDir: outDir,
            GitSha: null,
            RulesVersion: null,
            Backend: planPreservedBackend,
            Config: new Dictionary<string, object?>
            {
                ["phase"] = 3,
                ["queriesPath"] = queriesPath,
                ["dbConfigPath"] = dbConfigPath,
                ["environment"] = report.Environment,
                ["connectionSummary"] = report.ConnectionSummary,
                ["queryCount"] = report.QueryCount,
                ["totalRules"] = report.TotalRules,
                ["totalFindings"] = report.TotalFindings,
                ["ruleCounts"] = report.ByRule.Select(r => new Dictionary<string, object?>
                {
                    ["ruleId"] = r.RuleId,
                    ["pass"] = r.Pass,
                    ["warn"] = r.Warn,
                    ["fail"] = r.Fail,
                    ["error"] = r.Error
                }).ToList()
            }
        ));

        log.Info("Plan complete", new Dictionary<string, object?>
        {
            ["plans"] = paths.PlansPath,
            ["plansMarkdown"] = paths.PlansMarkdownPath,
            ["environment"] = report.Environment,
            ["totalFindings"] = report.TotalFindings
        });

        return await Task.FromResult(0);
    }

    static (string queriesPath, string dbConfigPath, string? envName) ParsePlanArgs(string[] args, OutputPaths defaults)
    {
        var queriesPath = defaults.QueriesPath;
        var dbConfigPath = defaults.DbConnectionsPath;
        string? envName = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--queries":
                    queriesPath = Path.GetFullPath(args[++i]);
                    break;
                case "--db-config":
                    dbConfigPath = Path.GetFullPath(args[++i]);
                    break;
                case "--env":
                    envName = args[++i];
                    break;
            }
        }

        return (queriesPath, dbConfigPath, envName);
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
        Console.WriteLine($"markdownDir={paths.MarkdownDir}");
        Console.WriteLine($"manifest={paths.ManifestPath}");
        Console.WriteLine($"queries={paths.QueriesPath}");
        Console.WriteLine($"queriesMd={paths.QueriesMarkdownPath}");
        Console.WriteLine($"suggestions={paths.SuggestionsPath}");
        Console.WriteLine($"suggestionsMd={paths.SuggestionsMarkdownPath}");
        Console.WriteLine($"plans={paths.PlansPath}");
        Console.WriteLine($"plansMd={paths.PlansMarkdownPath}");
        return 0;
    }

    static bool IsSelectOnlyCandidate(string sql)
    {
        var parse = TsqlParser.Parse(sql);
        if (!parse.Success || parse.Fragment is not TSqlScript script) return false;
        if (script.Batches is null || script.Batches.Count == 0) return false;

        var hasSelect = false;
        foreach (var batch in script.Batches)
        {
            foreach (var stmt in batch.Statements)
            {
                switch (stmt)
                {
                    case SelectStatement:
                        hasSelect = true;
                        break;
                    case SetOnOffStatement:
                    case SetTransactionIsolationLevelStatement:
                        break;
                    default:
                        return false;
                }
            }
        }

        return hasSelect;
    }
}
