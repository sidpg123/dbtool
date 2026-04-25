namespace SqlRepoAnalyzer.Core.Output;

public sealed record OutputPaths(string OutDir)
{
    public string ManifestPath => Path.Combine(OutDir, "manifest.json");
    public string QueriesPath => Path.Combine(OutDir, "queries.jsonl");
    public string SuggestionsPath => Path.Combine(OutDir, "suggestions.jsonl");
    public string LogPath => Path.Combine(OutDir, "sqltool.log");
}

