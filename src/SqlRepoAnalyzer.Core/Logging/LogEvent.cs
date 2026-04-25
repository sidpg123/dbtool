namespace SqlRepoAnalyzer.Core.Logging;

public sealed record LogEvent(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Message,
    IReadOnlyDictionary<string, object?>? Data = null,
    Exception? Exception = null
);

