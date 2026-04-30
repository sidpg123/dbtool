using System.Text.RegularExpressions;

namespace SqlRepoAnalyzer.Core.SqlFiles;

/// <summary>Shared heuristics for deciding whether embedded text looks like T-SQL.</summary>
public static class SqlTextHeuristics
{
    private static readonly Regex SqlishLeadingToken = new(
        @"^\s*(?:/\*[\s\S]*?\*/|--[^\n]*\n|--[^\n]*$|\s)*" +
        @"(create|alter|drop|select|insert|update|delete|merge|with|if|begin|end|declare|throw|print)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    public static bool LooksLikeSql(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var trimmed = text.Trim();
        if (trimmed.Length < 6)
            return false;
        return SqlishLeadingToken.IsMatch(trimmed);
    }
}
