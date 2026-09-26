using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using CodeMaster.Agent.Tools;

namespace CodeMaster.Agent.Services;

/// <summary>
/// Structured representation of a tool execution failure. The agent emits this
/// as the payload of <c>tool_failed</c> SSE events so the front end can render a
/// precise red banner instead of a generic error toast.
/// </summary>
public sealed record AiToolFailurePayload(
    string ToolName,
    string? ToolStage,
    string? Message,
    string? ExceptionType,
    string? ExceptionSource,
    string FailureKind,
    string? CorrelationId,
    string? StackSnippet,
    string? NextAction,
    int Attempt);

public static class AiToolFailureInspector
{
    private const int MaximumStackFrames = 6;
    private const int MaximumStackSnippetLength = 1200;

    private static readonly HashSet<string> OwnAssemblyPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "CodeMaster.Agent.",
        "CodeMaster.WebApi.",
        "CodeMaster.Application.",
        "CodeMaster.Infrastructure.",
        "CodeMaster.Core.",
        "CodeMaster.Domain.",
        "CodeMaster.Migrator."
    };

    private static readonly HashSet<string> InfrastructureExceptionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "HttpRequestException",
        "TaskCanceledException",
        "OperationCanceledException",
        "TimeoutException",
        "IOException",
        "SocketException",
        "WebException"
    };

    public static AiToolFailurePayload Inspect(
        string toolName,
        string? toolStage,
        Exception? exception,
        string? correlationId,
        string? nextAction,
        int attempt)
    {
        var baseException = exception?.GetBaseException();
        var type = baseException?.GetType().Name ?? "Unknown";
        var source = ResolveSource(baseException);
        var kind = Classify(baseException, toolName);
        var stack = ExtractStackSnippet(baseException);

        return new AiToolFailurePayload(
            ToolName: toolName,
            ToolStage: toolStage,
            Message: AiRequestFailure.Sanitize(baseException?.Message ?? exception?.Message),
            ExceptionType: type,
            ExceptionSource: source,
            FailureKind: kind,
            CorrelationId: correlationId,
            StackSnippet: stack,
            NextAction: nextAction ?? SuggestNextAction(kind, toolName, type),
            Attempt: attempt);
    }

    private static string? ResolveSource(Exception? exception)
    {
        if (exception == null) return null;
        var trace = new StackTrace(exception, false);
        for (var i = 0; i < trace.FrameCount; i++)
        {
            var frame = trace.GetFrame(i);
            var method = frame?.GetMethod();
            if (method == null) continue;
            var declaringType = method.DeclaringType;
            if (declaringType == null) continue;
            var assemblyName = declaringType.Assembly.GetName().Name ?? string.Empty;
            foreach (var prefix in OwnAssemblyPrefixes)
            {
                if (assemblyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return $"{declaringType.FullName}.{method.Name}";
                }
            }
        }

        return exception.TargetSite?.DeclaringType?.FullName;
    }

    private static string Classify(Exception? exception, string toolName)
    {
        if (exception == null) return "Unknown";
        var typeName = exception.GetType().Name;
        if (InfrastructureExceptionTypes.Contains(typeName))
            return "Infrastructure";
        if (typeName == "JsonException" || typeName == "InvalidOperationException" || typeName == "ArgumentException")
            return "ToolExecution";
        if (typeName == "UnauthorizedAccessException")
            return "Authorization";
        if (typeName == "KeyNotFoundException")
            return "NotFound";
        if (typeName.Contains("Provider", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("Http", StringComparison.OrdinalIgnoreCase))
            return "Infrastructure";
        return "Unknown";
    }

    private static string SuggestNextAction(string kind, string toolName, string type)
    {
        return kind switch
        {
            "Infrastructure" => $"Model channel failed ({type}). Retry the call; if it repeats, check the AI provider configuration.",
            "Authorization" => "Current user lacks permission for this operation. Re-authorize and retry.",
            "NotFound" => $"Target entity or relation was not found. Re-read the entity blueprint with {CodeMasterAgentToolCatalog.GetEntityBlueprint}.",
            "ToolExecution" => $"Tool {toolName} threw {type}. Inspect the generated JSON for the call; check {CodeMasterAgentToolCatalog.GetEntityBlueprint} before resubmitting.",
            _ =>
                $"Tool {toolName} failed with {type}. Re-read the entity blueprint and retry the call."
        };
    }

    private static string? ExtractStackSnippet(Exception? exception)
    {
        if (exception == null) return null;
        var trace = new StackTrace(exception, false);
        var collected = new List<string>();
        for (var i = 0; i < trace.FrameCount && collected.Count < MaximumStackFrames; i++)
        {
            var frame = trace.GetFrame(i);
            var method = frame?.GetMethod();
            var declaringType = method?.DeclaringType?.FullName;
            if (declaringType == null) continue;
            if (!IsOwnFrame(declaringType)) continue;
            var file = frame?.GetFileName();
            var line = frame?.GetFileLineNumber();
            collected.Add(file is null || line is null || line == 0
                ? $"  at {declaringType}.{method!.Name}"
                : $"  at {declaringType}.{method!.Name} ({file}:{line})");
        }
        if (collected.Count == 0) return null;
        var joined = string.Join("\n", collected);
        return joined.Length <= MaximumStackSnippetLength ? joined : joined[..MaximumStackSnippetLength] + "…";
    }

    private static bool IsOwnFrame(string declaringType)
    {
        foreach (var prefix in OwnAssemblyPrefixes)
        {
            if (declaringType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
