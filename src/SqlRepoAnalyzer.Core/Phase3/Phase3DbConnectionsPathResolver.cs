namespace SqlRepoAnalyzer.Core.Phase3;

/// <summary>
/// Finds <c>db-connections.json</c> for <c>plan</c>: explicit <c>--db-config</c>, then
/// output-directory file (legacy / per-repo secrets), then the template bundled with the CLI executable.
/// </summary>
public static class Phase3DbConnectionsPathResolver
{
    public const string BundledFileName = "db-connections.json";

    /// <returns>Resolved full path and a short label for logging: <c>explicit</c>, <c>outDir</c>, or <c>tool</c>.</returns>
    public static (string Path, string Source) Resolve(string? explicitPathFromUser, string outDirDefaultPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPathFromUser))
        {
            var p = Path.GetFullPath(explicitPathFromUser);
            if (!File.Exists(p))
                throw new FileNotFoundException($"DB config file not found (--db-config): {p}", p);
            return (p, "explicit");
        }

        var outDirFull = Path.GetFullPath(outDirDefaultPath);
        if (File.Exists(outDirFull))
            return (outDirFull, "outDir");

        var bundled = Path.Combine(AppContext.BaseDirectory, BundledFileName);
        if (File.Exists(bundled))
            return (Path.GetFullPath(bundled), "tool");

        throw new FileNotFoundException(
            "No db-connections.json found. Use --db-config <path>, add db-connections.json under your --out folder, or ensure the tool shipped db-connections.json next to SqlRepoAnalyzer.dll.",
            outDirFull);
    }
}
