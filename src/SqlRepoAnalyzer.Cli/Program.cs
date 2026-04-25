using SqlRepoAnalyzer.Core.Logging;
using SqlRepoAnalyzer.Core.Manifest;
using SqlRepoAnalyzer.Core.Output;
using SqlRepoAnalyzer.Core.Crawl;
using SqlRepoAnalyzer.Core.Queries;
using SqlRepoAnalyzer.Core.Reports;
using SqlRepoAnalyzer.Core.SqlFiles;
using SqlRepoAnalyzer.TypeScript.Node;
using SqlRepoAnalyzer.TypeScript.Extractor;

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
  SqlRepoAnalyzer scan --root <repoRoot> [--out <dir>]
  SqlRepoAnalyzer suggest --root <repoRoot> [--out <dir>]   (stub, Phase 1)
  SqlRepoAnalyzer report --out <dir>                        (stub, Phase 1)

Phase 1:
  - doctor runs environment checks (Node presence/version, out dir writable)
  - scan writes manifest + queries.jsonl (SQL inventory)
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
        var paths = new OutputPaths(outDir);
        var log = new Logger(
            minConsoleLevel: verbose ? LogLevel.Debug : LogLevel.Info,
            logFilePath: paths.LogPath,
            minFileLevel: LogLevel.Debug);

        log.Info("Scan start", new Dictionary<string, object?> { ["repoRoot"] = repoRoot, ["outDir"] = outDir });
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
            ["queryRecordCount"] = records.Count
        };

        ManifestWriter.WriteManifest(paths.ManifestPath, new ManifestRecord(
            ReportSchemaVersion: 1,
            ToolVersion: "0.1.0-phase1",
            GeneratedAtUtc: DateTimeOffset.UtcNow.ToString("o"),
            RepoRoot: repoRoot,
            OutDir: outDir,
            GitSha: null,
            Config: counts
        ));

        log.Info("Scan complete", new Dictionary<string, object?> { ["manifest"] = paths.ManifestPath, ["queries"] = paths.QueriesPath });
        return await Task.FromResult(0);
    }

    static int RunSuggest(string[] args)
    {
        var (_, outDir, verbose) = ParseCommon(args);
        var paths = new OutputPaths(outDir);
        var log = new Logger(
            minConsoleLevel: verbose ? LogLevel.Debug : LogLevel.Info,
            logFilePath: paths.LogPath,
            minFileLevel: LogLevel.Debug);

        log.Warn("Suggest is a stub (Phase 2)");
        Directory.CreateDirectory(outDir);
        if (!File.Exists(paths.SuggestionsPath))
        {
            File.WriteAllText(paths.SuggestionsPath, "");
        }
        return 0;
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

        log.Warn("Report is a stub (Phase 2)");
        Console.WriteLine($"outDir={paths.OutDir}");
        Console.WriteLine($"manifest={paths.ManifestPath}");
        Console.WriteLine($"queries={paths.QueriesPath}");
        Console.WriteLine($"suggestions={paths.SuggestionsPath}");
        return 0;
    }
}
