using System;
using CodeMaster.Domain.Entities.Ai;

namespace CodeMaster.Agent.Services;

/// <summary>
/// Raised when an operation would destroy a work plan that is still in progress.
/// </summary>
/// <remarks>
/// <para>Creating a plan is not a local edit: <c>UpsertAsync</c> retires the active plan, deletes
/// its stages/tasks and cancels its batches. Doing that silently while a batch is in flight leaves
/// an orphaned PendingApproval snapshot that replays metadata compiled against an older blueprint
/// — the "already exists" avalanche. It also throws away task rows that already committed, so the
/// repair flow re-runs them.</para>
/// <para>The same applies in reverse to cancellation: a batch that is literally executing has
/// already handed work to the change-set service, so retiring its plan underneath it leaves the
/// result unattributable.</para>
/// <para>The only supported ways out are explicit: finish the batch, reject it, or wait for it.
/// This exception is what makes that explicit.</para>
/// </remarks>
public sealed class AiProjectWorkPlanInProgressException : InvalidOperationException
{
    private AiProjectWorkPlanInProgressException(string message, long planId, AiProjectWorkPlanStatus planStatus, int unfinishedTaskCount, int completedTaskCount)
        : base(message)
    {
        PlanId = planId;
        PlanStatus = planStatus;
        UnfinishedTaskCount = unfinishedTaskCount;
        CompletedTaskCount = completedTaskCount;
    }

    public long PlanId { get; }

    public AiProjectWorkPlanStatus PlanStatus { get; }

    public int UnfinishedTaskCount { get; }

    public int CompletedTaskCount { get; }

    /// <summary>A plan cannot be replaced while it still holds committed or in-flight work.</summary>
    public static AiProjectWorkPlanInProgressException ForReplacement(
        long planId, AiProjectWorkPlanStatus planStatus, int unfinishedTaskCount, int completedTaskCount)
        => new(
            $"Work plan {planId} is still in progress (status {planStatus}, {completedTaskCount} completed task(s), " +
            $"{unfinishedTaskCount} unfinished task(s) or an in-flight batch). Creating a new plan would retire it, " +
            "discard its committed task records and orphan its batch, so nothing was created. " +
            "Finish or reject the current batch first, or call supersede_project_work_plan to discard the plan explicitly.",
            planId, planStatus, unfinishedTaskCount, completedTaskCount);

    /// <summary>A plan cannot be cancelled while one of its batches is executing right now.</summary>
    public static AiProjectWorkPlanInProgressException ForRunningBatch(long planId, AiProjectWorkPlanStatus planStatus)
        => new(
            $"Work plan {planId} (status {planStatus}) has a batch that is executing right now. " +
            "Cancelling the plan mid-execution would leave that result unattributable, so nothing was cancelled. " +
            "Wait for the running batch to finish, then cancel.",
            planId, planStatus, 0, 0);
}

/// <summary>
/// What <c>UpsertAsync</c> should do when it is asked to create a plan while another plan is
/// already active for the same conversation.
/// </summary>
internal enum AiProjectWorkPlanReuseDecision
{
    /// <summary>Retire the active plan and create a fresh one. Safe: it has no work left.</summary>
    Replace,

    /// <summary>Upgrade the active plan in place. Safe: nothing of it has executed yet.</summary>
    Adopt,

    /// <summary>Refuse. Replacing it would destroy committed work or orphan an in-flight batch.</summary>
    Refuse
}
