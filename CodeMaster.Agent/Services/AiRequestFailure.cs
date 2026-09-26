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

    [GeneratedRegex(@"(?i)Bearer\s+[A-Za-z0-9._~+/=-]+")]
    private static partial Regex BearerToken();

    [GeneratedRegex("""(?i)(?<prefix>["']?(?:access[_-]?token|token|api[_-]?key|password|pwd|secret|authorization|connection[_-]?string)["']?\s*[:=]\s*["']?)[^"'\s,;}]+["']?""")]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"://[^/@\s:]+:[^/@\s]+@")]
    private static partial Regex UriCredential();
}

public sealed class AiFailurePayload
{
    public string Diagnostic { get; set; } = string.Empty;
    public string? Stage { get; set; }
    public string? SuggestedAction { get; set; }
    public int Attempt { get; set; }
}
