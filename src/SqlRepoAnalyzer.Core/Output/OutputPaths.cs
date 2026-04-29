namespace SqlRepoAnalyzer.Core.Output;

public sealed record OutputPaths(string OutDir)
{
    public string ManifestPath => Path.Combine(OutDir, "manifest.json");
    public string QueriesPath => Path.Combine(OutDir, "queries.json");
    public string SuggestionsPath => Path.Combine(OutDir, "suggestions.json");

    /// <summary>Readable Phase 2 report parallel to <see cref="SuggestionsPath"/>.</summary>
    public string SuggestionsMarkdownPath => Path.Combine(MarkdownDir, "suggestions.md");
    public string PlansPath => Path.Combine(OutDir, "plans.json");

    /// <summary>Folder for human-readable Markdown exports. JSON artifacts remain in <see cref="OutDir"/> root.</summary>
    public string MarkdownDir => Path.Combine(OutDir, "markdown");

    /// <summary>Readable inventory parallel to <see cref="QueriesPath"/>.</summary>
    public string QueriesMarkdownPath => Path.Combine(MarkdownDir, "queries.md");

    /// <summary>DBA-readable Phase 3 report parallel to <see cref="PlansPath"/>.</summary>
    public string PlansMarkdownPath => Path.Combine(MarkdownDir, "plans.md");
    public string DbConnectionsPath => Path.Combine(OutDir, "db-connections.json");
    public string LogPath => Path.Combine(OutDir, "sqltool.log");
}

