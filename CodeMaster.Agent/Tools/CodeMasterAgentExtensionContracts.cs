using CodeMaster.Core.Extensibility;
using CodeMaster.Domain.Entities.Ai;
using Microsoft.Extensions.AI;

namespace CodeMaster.Agent.Tools;

/// <summary>
/// Context offered to optional feature modules that contribute Agent tools.
/// Extensions can validate a structured proposal and create an approval record,
/// but they do not receive a filesystem write primitive.
/// </summary>
public sealed class CodeMasterAgentExtensionContext
{
    public required long ConversationId { get; init; }
    public required long ProjectId { get; init; }
    public required string RequestId { get; init; }
    public required Func<string, object, Task<long>> CreatePendingApprovalAsync { get; init; }
}

public interface ICodeMasterAgentToolExtension : ICodeMasterExtension
{
    Task<IReadOnlyCollection<AITool>> CreateToolsAsync(CodeMasterAgentExtensionContext context);
    bool CanExecute(string toolName);
    Task DemandPermissionAsync(AiToolExecution execution);
    Task<object> ExecuteAsync(AiToolExecution execution);
}

/// <summary>
/// Marker for approved tool results that require Web/Tauri client actions.
/// </summary>
public interface IAgentClientActionResult
{
    bool HasClientActions { get; }
}
