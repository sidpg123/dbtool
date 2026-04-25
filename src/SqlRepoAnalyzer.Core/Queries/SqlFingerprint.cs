using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SqlRepoAnalyzer.Core.Queries;

public static class SqlFingerprint
{
    // Very conservative MVP normalization:
    // - remove /* */ and -- comments
    // - trim and collapse whitespace
    private static readonly Regex BlockComment = new(@"/\*[\s\S]*?\*/", RegexOptions.Compiled);
    private static readonly Regex LineComment = new(@"--.*?$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex Ws = new(@"\s+", RegexOptions.Compiled);

    public static string Normalize(string sql)
    {
        var s = BlockComment.Replace(sql, " ");
        s = LineComment.Replace(s, " ");
        s = Ws.Replace(s, " ").Trim();
        return s;
    }

    public static string Sha256Hex(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

