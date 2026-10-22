using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Tools;

namespace CodeMaster.Agent.Services;

/// <summary>
/// Orchestrates the post-plan lifecycle: confirm (if needed) and execute the next
/// ready batch. Extracted from the legacy chat flow so the same logic is reusable
/// from the new AgentTurnRunner (doc §5.3 "submit_business_plan 成功后直接进入编译和计划执行").
/// </summary>
public interface IWorkPlanAutoExecutor
{
    /// <summary>
    /// Confirms a plan (if it requires confirmation) and starts executing its first
    /// ready batch. Returns the tool execution that was launched, or null if there
    /// was nothing ready.
    /// </summary>
    Task<AiToolExecutionDto?> ConfirmAndExecuteAsync(
        long planId,
        long conversationId,
        long projectId,
        CancellationToken cancellationToken = default);
}
