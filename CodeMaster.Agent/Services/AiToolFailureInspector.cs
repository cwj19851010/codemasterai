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

    /// <summary>
    /// 元数据校验失败的固定文案片段。这些异常来自 <c>ValidateFieldControlsAsync</c> /
    /// <c>EntityRelationGraph</c>，属于"模型填错了、改一下就能过"，和真正的工具异常不是一类问题，
    /// 所以单独归为 <c>Validation</c>，好让前端给出可执行的下一步。
    /// </summary>
    private static readonly string[] ValidationMessageMarkers =
    [
        "must have bool data type",
        "must have a numeric data type",
        "must have DateTime, DateOnly, or DateTimeOffset",
        "must have string or text data type",
        "must enable multiple selection",
        "cannot enable multiple selection",
        "must store its comma-separated values",
        "Relation field types must match",
        "references missing target field",
        "references missing source field"
    ];

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
        var message = AiRequestFailure.Sanitize(AiRequestFailure.DescribeExceptionChain(exception));
        var kind = Classify(baseException, toolName, message);
        var stack = ExtractStackSnippet(baseException);

        return new AiToolFailurePayload(
            ToolName: toolName,
            ToolStage: toolStage,
            Message: message,
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

    private static string Classify(Exception? exception, string toolName, string? message)
    {
        if (exception == null) return "Unknown";
        var typeName = exception.GetType().Name;
        if (InfrastructureExceptionTypes.Contains(typeName))
            return "Infrastructure";
        // 校验失败先判：它也是 InvalidOperationException，但归到"工具异常"会丢掉可执行的修复建议。
        if (IsMetadataValidationFailure(message))
            return "Validation";
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

    /// <summary>判断失败是否属于"元数据填写不合法"，可据此推断出错的阶段并给出针对性建议。</summary>
    internal static bool IsMetadataValidationFailure(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        foreach (var marker in ValidationMessageMarkers)
        {
            if (message.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string SuggestNextAction(string kind, string toolName, string type)
    {
        return kind switch
        {
            "Infrastructure" => $"Model channel failed ({type}). Retry the call; if it repeats, check the AI provider configuration.",
            "Authorization" => "Current user lacks permission for this operation. Re-authorize and retry.",
            "NotFound" => $"Target entity or relation was not found. Re-read the entity blueprint with {CodeMasterAgentToolCatalog.GetEntityBlueprint}.",
            "Validation" => $"The metadata named in the message is inconsistent. Fix the field data type or the relation field pair, re-read {CodeMasterAgentToolCatalog.GetEntityBlueprint}, then resubmit the change set.",
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
