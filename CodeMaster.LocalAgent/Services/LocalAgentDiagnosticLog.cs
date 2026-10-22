using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeMaster.LocalAgent.Services;

public sealed partial class LocalAgentDiagnosticLog
{
    public const string LogDirectoryEnvironmentVariable = "CODEMASTER_LOCAL_AGENT_LOG_DIR";

    private const int RetentionDays = 14;
    private const int MaximumMessageLength = 12000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] SensitiveKeyFragments =
    [
        "token",
        "password",
        "secret",
        "apikey",
        "api_key",
        "authorization",
        "cookie",
        "connectionstring",
        "connection_string"
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _logDirectory;
    private int _cleanupCompleted;

    public LocalAgentDiagnosticLog(string logDirectory)
    {
        if (string.IsNullOrWhiteSpace(logDirectory))
            throw new ArgumentException("Diagnostic log directory is required.", nameof(logDirectory));

        _logDirectory = Path.GetFullPath(logDirectory);
    }

    public string LogDirectory => _logDirectory;

    public string CurrentLogPath => Path.Combine(
        _logDirectory,
        $"local-agent-{DateTime.UtcNow:yyyyMMdd}.jsonl");

    public async Task WriteAsync(
        string level,
        string eventName,
        IReadOnlyDictionary<string, object?>? fields = null,
        Exception? exception = null)
    {
        var entry = new Dictionary<string, object?>
        {
            ["timestamp"] = DateTimeOffset.UtcNow,
            ["level"] = NormalizeLevel(level),
            ["source"] = "local-agent",
            ["event"] = string.IsNullOrWhiteSpace(eventName) ? "unknown" : eventName.Trim(),
            ["processId"] = Environment.ProcessId
        };

        if (fields is { Count: > 0 })
            entry["fields"] = SanitizeFields(fields);

        if (exception != null)
        {
            var baseException = exception.GetBaseException();
            entry["exception"] = new Dictionary<string, object?>
            {
                ["type"] = baseException.GetType().FullName,
                ["message"] = SanitizeText(baseException.Message),
                ["stackTrace"] = Truncate(exception.StackTrace ?? string.Empty, MaximumMessageLength)
            };
        }

        var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_logDirectory);
            await File.AppendAllTextAsync(CurrentLogPath, line);
            CleanupExpiredLogsOnce();
        }
        catch
        {
            // Diagnostics must never break the execution being observed.
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static string SanitizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var sanitized = BearerTokenRegex().Replace(value, "Bearer [REDACTED]");
        sanitized = SensitiveAssignmentRegex().Replace(
            sanitized,
            match => $"{match.Groups["prefix"].Value}[REDACTED]");
        sanitized = UriCredentialRegex().Replace(sanitized, "://[REDACTED]@");
        return Truncate(sanitized, MaximumMessageLength);
    }

    private static Dictionary<string, object?> SanitizeFields(IReadOnlyDictionary<string, object?> fields)
    {
        return fields.ToDictionary(
            pair => pair.Key,
            pair => IsSensitiveKey(pair.Key)
                ? "[REDACTED]"
                : SanitizeValue(pair.Value),
            StringComparer.OrdinalIgnoreCase);
    }

    private static object? SanitizeValue(object? value)
    {
        return value switch
        {
            null => null,
            string text => SanitizeText(text),
            Uri uri => uri.Host,
            DateTime or DateTimeOffset or TimeSpan or bool or byte or short or int or long or float or double or decimal => value,
            Enum enumValue => enumValue.ToString(),
            _ => SanitizeText(value.ToString())
        };
    }

    private static bool IsSensitiveKey(string key)
    {
        var normalized = key.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);
        return SensitiveKeyFragments.Any(fragment =>
            normalized.Contains(
                fragment.Replace("_", string.Empty, StringComparison.Ordinal),
                StringComparison.OrdinalIgnoreCase));
    }

    private void CleanupExpiredLogsOnce()
    {
        if (Interlocked.Exchange(ref _cleanupCompleted, 1) != 0) return;

        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        foreach (var path in Directory.EnumerateFiles(_logDirectory, "local-agent-*.jsonl"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                    File.Delete(path);
            }
            catch
            {
                // A locked or inaccessible old log can be retried on the next process start.
            }
        }
    }

    private static string NormalizeLevel(string level)
    {
        return level.Trim().ToLowerInvariant() switch
        {
            "debug" => "debug",
            "warning" or "warn" => "warning",
            "error" => "error",
            _ => "information"
        };
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    [GeneratedRegex(@"(?i)Bearer\s+[A-Za-z0-9._~+/=-]+")]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex("""(?i)(?<prefix>["']?(?:access[_-]?token|token|api[_-]?key|password|pwd|secret|authorization|connection[_-]?string)["']?\s*[:=]\s*["']?)[^"'\s,;}]+["']?""")]
    private static partial Regex SensitiveAssignmentRegex();

    [GeneratedRegex(@"://[^/@\s:]+:[^/@\s]+@", RegexOptions.IgnoreCase)]
    private static partial Regex UriCredentialRegex();
}
