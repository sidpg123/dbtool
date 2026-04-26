using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlRepoAnalyzer.Core.Schema;

public static class SchemaSnapshotFingerprinter
{
    public static string Sha256Hex(SchemaSnapshot snapshot)
    {
        // Stable fingerprint: canonical JSON of the snapshot object graph.
        // (Good enough for cache invalidation; not a security hash.)
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
