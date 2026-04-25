namespace SqlRepoAnalyzer.Core.Crawl;

public sealed record CrawlOptions(
    string RepoRoot,
    long MaxFileSizeBytes,
    IReadOnlyList<string> IncludeExtensions,
    IReadOnlyList<string> ExcludeDirNames
);

