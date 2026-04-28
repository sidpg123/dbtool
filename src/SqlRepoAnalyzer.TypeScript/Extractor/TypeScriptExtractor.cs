using System.Diagnostics;
using System.Text.Json;
using SqlRepoAnalyzer.Core.Logging;
using SqlRepoAnalyzer.Core.Queries;

namespace SqlRepoAnalyzer.TypeScript.Extractor;

public sealed class TypeScriptExtractor
{
    private readonly Logger _log;

    public TypeScriptExtractor(Logger log)
    {
        _log = log;
    }

    public async Task<IReadOnlyList<QueryCandidate>> ExtractAsync(
        string repoRoot,
        string outDir,
        IReadOnlyList<string> files,
        CancellationToken ct)
    {
        if (files.Count == 0) return Array.Empty<QueryCandidate>();

        var listPath = Path.Combine(outDir, "ts-files.txt");
        Directory.CreateDirectory(outDir);
        await File.WriteAllLinesAsync(listPath, files, ct);

        var extractorPath = ResolveExtractorPath();
        if (extractorPath is null)
        {
            _log.Error("TS extractor script not found", new Dictionary<string, object?>
            {
                ["baseDirectory"] = AppContext.BaseDirectory,
                ["currentDirectory"] = Directory.GetCurrentDirectory()
            });
            return Array.Empty<QueryCandidate>();
        }

        var psi = new ProcessStartInfo
        {
            FileName = "node",
            Arguments = $"\"{extractorPath}\" --files \"{listPath}\" --root \"{repoRoot}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _log.Info("Running TS extractor", new Dictionary<string, object?>
        {
            ["fileCount"] = files.Count,
            ["extractorPath"] = extractorPath
        });

        using var p = new Process { StartInfo = psi };
        p.Start();

        var candidates = new List<QueryCandidate>();
        while (true)
        {
            var line = await p.StandardOutput.ReadLineAsync(ct);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var dto = JsonSerializer.Deserialize<TsCandidateDto>(line, JsonOptions);
                if (dto is null) continue;

                if (!TryMapSourceKind(dto.SourceKind, out var sk)) continue;
                candidates.Add(new QueryCandidate(
                    sk,
                    dto.File,
                    dto.StartLine,
                    dto.StartCol,
                    dto.EndLine,
                    dto.EndCol,
                    dto.SqlText,
                    dto.Completeness
                ));
            }
            catch (Exception ex)
            {
                _log.Warn("Failed to parse extractor JSONL line", new Dictionary<string, object?> { ["line"] = line }, ex);
            }
        }

        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
        {
            _log.Error("TS extractor failed", new Dictionary<string, object?> { ["exitCode"] = p.ExitCode, ["stderr"] = stderr });
        }
        else if (!string.IsNullOrWhiteSpace(stderr))
        {
            _log.Warn("TS extractor stderr", new Dictionary<string, object?> { ["stderr"] = stderr.Trim() });
        }

        return candidates;
    }

    private static bool TryMapSourceKind(string sourceKind, out SourceKind sk)
    {
        sk = default;
        return sourceKind switch
        {
            "embedded_raw_sql" => (sk = SourceKind.EmbeddedRawSql) == SourceKind.EmbeddedRawSql,
            "typeorm_raw_query" => (sk = SourceKind.TypeOrmRawQuery) == SourceKind.TypeOrmRawQuery,
            "typeorm_query_dynamic" => (sk = SourceKind.TypeOrmQueryDynamic) == SourceKind.TypeOrmQueryDynamic,
            "typeorm_query_builder_site" => (sk = SourceKind.TypeOrmQueryBuilderSite) == SourceKind.TypeOrmQueryBuilderSite,
            _ => false
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static string? ResolveExtractorPath()
    {
        static string Candidate(string basePath) =>
            Path.Combine(basePath, "assets", "ts-extractor", "extract.mjs");

        var direct = Candidate(AppContext.BaseDirectory);
        if (File.Exists(direct)) return direct;

        var cwdDirect = Candidate(Directory.GetCurrentDirectory());
        if (File.Exists(cwdDirect)) return cwdDirect;

        foreach (var root in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var probe = new DirectoryInfo(root);
            while (probe is not null)
            {
                var candidate = Candidate(probe.FullName);
                if (File.Exists(candidate)) return candidate;
                probe = probe.Parent;
            }
        }

        return null;
    }
}

