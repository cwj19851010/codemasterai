using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeMaster.Domain.Entities.Ai;

namespace CodeMaster.Agent.Services;

public sealed class AiProjectFailureEvidenceInput
{
    public string? ExecutionMode { get; set; }
    public string? ActionName { get; set; }
    public string? InputHash { get; set; }
    public string? ErrorMessage { get; set; }
    public string? DiagnosticOutput { get; set; }
    public string? FailedAction { get; set; }
    public string? ArtifactManifestSummary { get; set; }
    public List<string> PredecessorResults { get; set; } = [];
    public bool ReproducedInCodeMasterOwnedPath { get; set; }
}

internal sealed class AiProjectFailureEvidenceDocument
{
    public string Category { get; set; } = string.Empty;
    public string FailureSignature { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string ExecutionMode { get; set; } = string.Empty;
    public string ActionName { get; set; } = string.Empty;
    public string? FailedAction { get; set; }
    public string? InputHash { get; set; }
    public string CodeMasterVersion { get; set; } = string.Empty;
    public string? DiagnosticOutput { get; set; }
    public string? ArtifactManifestSummary { get; set; }
    public List<string> PredecessorResults { get; set; } = [];
    public List<string> SupportingEvidence { get; set; } = [];
}

internal static class AiProjectFailureEvidenceBuilder
{
    private const int MaximumDiagnosticLength = 6000;
    private static readonly Regex SecretAssignment = new(
        @"(?i)\b(password|pwd|secret|token|api[-_]?key|connectionstring)\b\s*[:=]\s*[\""']?[^\s,;\""']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BearerToken = new(
        @"(?i)\bbearer\s+[A-Za-z0-9._~+/=-]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex JwtToken = new(
        @"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VariableData = new(
        @"(?i)([a-f0-9]{8}-[a-f0-9-]{27,}|\b\d+\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static AiProjectFailureEvidenceDocument Build(AiProjectFailureEvidenceInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var error = Redact(input.ErrorMessage);
        var diagnostic = Truncate(Redact(input.DiagnosticOutput), MaximumDiagnosticLength);
        var combined = string.Join('\n', new[] { error, diagnostic, input.FailedAction }
            .Where(item => !string.IsNullOrWhiteSpace(item)));
        var reproducedInOwnedPath = input.ReproducedInCodeMasterOwnedPath || ContainsCodeMasterOwnedPath(combined);
        var category = Classify(combined, reproducedInOwnedPath, out var supportingEvidence);
        var summary = BuildSummary(category, input.FailedAction, error, diagnostic);
        var signatureSource = VariableData.Replace(
            $"{category}|{input.ActionName}|{input.FailedAction}|{combined}".ToLowerInvariant(), "#");

        return new AiProjectFailureEvidenceDocument
        {
            Category = category.ToString(),
            FailureSignature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signatureSource))),
            Summary = summary,
            ExecutionMode = Truncate(Redact(input.ExecutionMode), 30) ?? "Unknown",
            ActionName = Truncate(Redact(input.ActionName), 100) ?? "unknown",
            FailedAction = Truncate(Redact(input.FailedAction), 100),
            InputHash = Truncate(Redact(input.InputHash), 64),
            CodeMasterVersion = typeof(AiProjectFailureEvidenceBuilder).Assembly.GetName().Version?.ToString() ?? "unknown",
            DiagnosticOutput = diagnostic,
            ArtifactManifestSummary = Truncate(Redact(input.ArtifactManifestSummary), 1000),
            PredecessorResults = input.PredecessorResults
                .Take(20)
                .Select(item => Truncate(Redact(item), 500))
                .Where(item => item != null)
                .Cast<string>()
                .ToList(),
            SupportingEvidence = supportingEvidence
        };
    }

    public static string Serialize(AiProjectFailureEvidenceDocument document)
        => JsonSerializer.Serialize(document);

    public static AiProjectFailureEvidenceDocument? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<AiProjectFailureEvidenceDocument>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string? RedactForTest(string? value) => Redact(value);

    private static AiProjectFailureCategory Classify(
        string evidence,
        bool reproducedInCodeMasterOwnedPath,
        out List<string> supportingEvidence)
    {
        supportingEvidence = [];
        var normalized = evidence.ToLowerInvariant();

        if (ContainsAny(normalized, "missing required", "invalid metadata", "semantic reference", "relation target", "field configuration", "validation failed"))
        {
            supportingEvidence.Add("Structured metadata or plan validation failed before a valid generation input was established.");
            return AiProjectFailureCategory.PlanOrMetadata;
        }

        if (ContainsAny(normalized, "econnrefused", "connection refused", "timed out", "timeout", "command not found", "is not recognized", "permission denied", "disk full", "network unreachable"))
        {
            supportingEvidence.Add("The evidence contains an environment, process, permission, timeout, or connectivity failure.");
            return AiProjectFailureCategory.ExternalEnvironment;
        }

        if (reproducedInCodeMasterOwnedPath && ContainsAny(normalized, "template", "scriban", "marker", "codemaster.application", "codemaster.infrastructure", "templates/", "templates\\"))
        {
            supportingEvidence.Add("The failure was reproduced in an explicitly identified CodeMaster-owned generator or template path.");
            return AiProjectFailureCategory.GeneratorOrTemplate;
        }

        if (ContainsAny(normalized, "vite", "npm", "dotnet build", "compiler error", "build failed", ".vue", ".csproj", "generated project"))
        {
            supportingEvidence.Add("The generated project failed an integration or build checkpoint without sufficient CodeMaster source evidence.");
            return AiProjectFailureCategory.GeneratedProjectIntegration;
        }

        supportingEvidence.Add("The available evidence is insufficient for a deterministic ownership classification.");
        return AiProjectFailureCategory.Unknown;
    }

    private static string BuildSummary(
        AiProjectFailureCategory category,
        string? failedAction,
        string? error,
        string? diagnostic)
    {
        var detail = error ?? diagnostic ?? "No diagnostic output was supplied.";
        var action = string.IsNullOrWhiteSpace(failedAction) ? string.Empty : $" at '{failedAction}'";
        return Truncate($"{category}{action}: {detail}", 2000)!;
    }

    private static bool ContainsAny(string value, params string[] needles)
        => needles.Any(value.Contains);

    private static bool ContainsCodeMasterOwnedPath(string value)
    {
        var normalized = value.Replace('\\', '/').ToLowerInvariant();
        return ContainsAny(
            normalized,
            "codemaster.application/services/codegen/",
            "codemaster.application/scriptbuilder/",
            "codemaster.application/vuebuilder/",
            "codemaster.migrator/seeddata/system/templatemodule.cs",
            "codemaster.agent/services/",
            "codemaster.agent/tools/",
            "/templates/codemaster.");
    }

    private static string? Redact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var redacted = SecretAssignment.Replace(value, match => $"{match.Groups[1].Value}=[REDACTED]");
        redacted = BearerToken.Replace(redacted, "Bearer [REDACTED]");
        return JwtToken.Replace(redacted, "[REDACTED_JWT]");
    }

    private static string? Truncate(string? value, int maximumLength)
        => string.IsNullOrEmpty(value) || value.Length <= maximumLength
            ? value
            : value[..maximumLength];
}
