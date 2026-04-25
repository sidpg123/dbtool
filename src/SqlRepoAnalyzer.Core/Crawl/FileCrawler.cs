namespace SqlRepoAnalyzer.Core.Crawl;

public static class FileCrawler
{
    public static IEnumerable<string> EnumerateFiles(CrawlOptions options)
    {
        var repoRoot = Path.GetFullPath(options.RepoRoot);
        var excluded = new HashSet<string>(options.ExcludeDirNames, StringComparer.OrdinalIgnoreCase);
        var includeExts = new HashSet<string>(
            options.IncludeExtensions.Select(e => e.StartsWith('.') ? e : "." + e),
            StringComparer.OrdinalIgnoreCase);

        return Enumerate(repoRoot);

        IEnumerable<string> Enumerate(string dir)
        {
            foreach (var subDir in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(subDir);
                if (excluded.Contains(name)) continue;
                foreach (var f in Enumerate(subDir)) yield return f;
            }

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var ext = Path.GetExtension(file);
                if (!includeExts.Contains(ext)) continue;

                var info = new FileInfo(file);
                if (info.Length > options.MaxFileSizeBytes) continue;

                yield return file;
            }
        }
    }
}

