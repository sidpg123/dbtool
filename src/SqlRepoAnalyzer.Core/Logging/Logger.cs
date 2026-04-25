using System.Globalization;

namespace SqlRepoAnalyzer.Core.Logging;

public sealed class Logger
{
    private readonly object _lock = new();
    private readonly LogLevel _minConsoleLevel;
    private readonly string? _logFilePath;
    private readonly LogLevel _minFileLevel;

    public Logger(LogLevel minConsoleLevel, string? logFilePath, LogLevel minFileLevel)
    {
        _minConsoleLevel = minConsoleLevel;
        _logFilePath = logFilePath;
        _minFileLevel = minFileLevel;
    }

    public void Debug(string message, IReadOnlyDictionary<string, object?>? data = null) =>
        Write(new LogEvent(DateTimeOffset.UtcNow, LogLevel.Debug, message, data));

    public void Info(string message, IReadOnlyDictionary<string, object?>? data = null) =>
        Write(new LogEvent(DateTimeOffset.UtcNow, LogLevel.Info, message, data));

    public void Warn(string message, IReadOnlyDictionary<string, object?>? data = null, Exception? ex = null) =>
        Write(new LogEvent(DateTimeOffset.UtcNow, LogLevel.Warn, message, data, ex));

    public void Error(string message, IReadOnlyDictionary<string, object?>? data = null, Exception? ex = null) =>
        Write(new LogEvent(DateTimeOffset.UtcNow, LogLevel.Error, message, data, ex));

    private void Write(LogEvent evt)
    {
        var line = Format(evt);

        lock (_lock)
        {
            if (evt.Level >= _minConsoleLevel)
            {
                Console.Error.WriteLine(line);
                if (evt.Exception is not null)
                {
                    Console.Error.WriteLine(evt.Exception);
                }
            }

            if (_logFilePath is not null && evt.Level >= _minFileLevel)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logFilePath)!);
                File.AppendAllText(_logFilePath, line + Environment.NewLine);
                if (evt.Exception is not null)
                {
                    File.AppendAllText(_logFilePath, evt.Exception + Environment.NewLine);
                }
            }
        }
    }

    private static string Format(LogEvent evt)
    {
        // Grep-friendly key=value pairs
        var ts = evt.Timestamp.ToString("o", CultureInfo.InvariantCulture);
        var parts = new List<string>
        {
            $"ts={ts}",
            $"level={evt.Level.ToString().ToUpperInvariant()}",
            $"msg={Escape(evt.Message)}",
        };

        if (evt.Data is not null)
        {
            foreach (var (k, v) in evt.Data)
            {
                if (string.IsNullOrWhiteSpace(k)) continue;
                parts.Add($"{k}={Escape(v)}");
            }
        }

        return string.Join(" ", parts);
    }

    private static string Escape(object? value)
    {
        if (value is null) return "null";
        var s = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
        if (s.Length == 0) return "\"\"";
        // Quote when whitespace or equals present
        return s.Any(c => char.IsWhiteSpace(c) || c == '=')
            ? $"\"{s.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : s;
    }
}

