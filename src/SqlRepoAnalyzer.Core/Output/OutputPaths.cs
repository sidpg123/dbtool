namespace SqlRepoAnalyzer.Core.Output;

public sealed record OutputPaths(string OutDir)
{
    public string ManifestPath => Path.Combine(OutDir, "manifest.json");
    public string QueriesPath => Path.Combine(OutDir, "queries.json");
    public string SuggestionsPath => Path.Combine(OutDir, "suggestions.json");
    public string PlansPath => Path.Combine(OutDir, "plans.json");
    public string PlanSuggestionsPath => Path.Combine(OutDir, "plan-suggestions.json");
    public string ShowPlanXmlDirectory => Path.Combine(OutDir, "showplan-xml");
    public string LogPath => Path.Combine(OutDir, "sqltool.log");
}

