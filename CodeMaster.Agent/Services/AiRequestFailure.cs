using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeMaster.Agent.Services;

public static partial class AiRequestFailure
{
    public const string Role = "error";
    public const int MaximumDiagnosticLength = 1800;

    public static string Sanitize(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "Agent 请求失败，未返回具体诊断。" : value.Trim();
        text = BearerToken().Replace(text, "Bearer [REDACTED]");
        text = SecretAssignment().Replace(text, match => match.Groups["prefix"].Value + "[REDACTED]");
        text = UriCredential().Replace(text, "://[REDACTED]@");
        return text.Length <= MaximumDiagnosticLength ? text : text[..MaximumDiagnosticLength] + "…";
    }

    public static AiFailurePayload SanitizeWithContext(string? value, string? stage, string? suggestedAction, int attempt)
    {
        return new AiFailurePayload
        {
            Diagnostic = Sanitize(value),
            Stage = stage,
            SuggestedAction = suggestedAction,
            Attempt = attempt
        };
    }

    public static string Metadata(string state) => JsonSerializer.Serialize(new
    {
        kind = "request_failure",
        status = state,
        updatedAt = DateTime.UtcNow,
        hasStage = true,
        hasSuggestedAction = true,
        hasAttempt = true
    });

    /// <summary>
    /// Flattens the whole exception chain into one line, outermost first.
    /// <para>
    /// The outermost message alone is not diagnosable: the OpenAI SDK reports
    /// "Retry failed after 3 tries." and the transport layer reports
    /// "The SSL connection could not be established, see inner exception.", while the actual
    /// cause (an untrusted certificate, a refused handshake, a proxy that never answered)
    /// only exists on an inner exception. Reporting the chain keeps that cause visible.
    /// </para>
    /// <para>
    /// Some failures carry no message at all — a key-ring <c>CryptographicException</c>, or a
    /// transport error raised before any response exists, both surface with an empty
    /// <see cref="Exception.Message"/>. Dropping those levels used to leave the whole chain
    /// empty, so the caller only saw "未返回具体诊断" and lost the one fact that identifies the
    /// failure. Such a level is now described by its type, HResult and first stack frame.
    /// </para>
    /// </summary>
    public static string DescribeExceptionChain(Exception? exception)
    {
        if (exception == null)
            return string.Empty;

        const int maxLevels = 6;
        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var current = exception; current != null && parts.Count < maxLevels; current = current.InnerException)
        {
            var typeName = current.GetType().Name;
            var message = current.Message?.Trim();
            var entry = string.IsNullOrWhiteSpace(message)
                ? $"{typeName}: (无消息, HResult=0x{current.HResult:X8}){FirstFrame(current)}"
                : $"{typeName}: {message}";

            if (!seen.Add(entry))
                break;

            parts.Add(entry);
        }

        return parts.Count == 0 ? string.Empty : string.Join("  <-  ", parts);
    }

    /// <summary>First stack line, used only when an exception has no message to report.</summary>
    private static string FirstFrame(Exception exception)
    {
        var stack = exception.StackTrace;
        if (string.IsNullOrWhiteSpace(stack))
            return string.Empty;

        var line = stack.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return line == null ? string.Empty : " @ " + line;
    }

    [GeneratedRegex(@"(?i)Bearer\s+[A-Za-z0-9._~+/=-]+")]
    private static partial Regex BearerToken();

    [GeneratedRegex("""(?i)(?<prefix>["']?(?:access[_-]?token|token|api[_-]?key|password|pwd|secret|authorization|connection[_-]?string)["']?\s*[:=]\s*["']?)[^"'\s,;}]+["']?""")]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"://[^/@\s:]+:[^/@\s]+@")]
    private static partial Regex UriCredential();

    /// <summary>
    /// True when the failure is the endpoint refusing image content rather than a transport or
    /// credential problem.
    /// <para>
    /// There is no standard error code for "this model has no vision", so the check looks at
    /// the vocabulary every OpenAI-compatible implementation shares. Both the server-side
    /// capability probe and the local execution client use it: the probe to decide whether to
    /// send images at all, the local client to turn the rejection into an explanation instead
    /// of an error.
    /// </para>
    /// </summary>
    public static bool LooksLikeVisionRejection(Exception? exception)
    {
        var text = DescribeExceptionChain(exception);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string[] markers =
        [
            "image",
            "vision",
            "multimodal",
            "image_url",
            "invalid_image",
            "unsupported content",
            "content type"
        ];

        return markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class AiFailurePayload
{
    public string Diagnostic { get; set; } = string.Empty;
    public string? Stage { get; set; }
    public string? SuggestedAction { get; set; }
    public int Attempt { get; set; }
}
