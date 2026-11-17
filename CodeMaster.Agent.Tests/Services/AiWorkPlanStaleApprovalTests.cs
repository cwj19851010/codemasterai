using CodeMaster.Agent.Services;
using CodeMaster.Agent.Tools;
using CodeMaster.Domain.Entities.Ai;

namespace CodeMaster.Agent.Tests.Services;

/// <summary>
/// Regression cover for the stale-snapshot bug.
///
/// A work plan was superseded by a newer one, but its PendingApproval batch survived the
/// supersede. The client's automatic approval loop then picked that batch up and replayed a change
/// set whose every item already existed — one stale snapshot, a dozen "already exists" errors.
/// The fix has two halves, and both are pinned here:
///
/// 1. Retiring a plan must cancel the batches still hanging off it
///    (<see cref="AiProjectWorkPlanService.ShouldCancelBatchWithPlan"/>).
/// 2. An approval belonging to a retired plan must never execute, no matter which path reaches it
///    (<see cref="CodeMasterAgentToolFactory.IsTerminalPlanStatus"/>). The resume path always had
///    this check; direct approval — including the automatic loop — did not.
/// </summary>
public sealed class AiWorkPlanStaleApprovalTests
{
    [Theory]
    [InlineData(AiProjectWorkBatchStatus.Draft)]
    [InlineData(AiProjectWorkBatchStatus.PendingApproval)]
    [InlineData(AiProjectWorkBatchStatus.Approved)]
    [InlineData(AiProjectWorkBatchStatus.WaitingClientExecution)]
    [InlineData(AiProjectWorkBatchStatus.Executing)]
    [InlineData(AiProjectWorkBatchStatus.Failed)]
    [InlineData(AiProjectWorkBatchStatus.Rejected)]
    [InlineData(AiProjectWorkBatchStatus.Stale)]
    [InlineData(AiProjectWorkBatchStatus.Cancelled)]
    public void Unfinished_batches_are_cancelled_with_their_plan(AiProjectWorkBatchStatus status)
    {
        Assert.True(AiProjectWorkPlanService.ShouldCancelBatchWithPlan(status));
    }

    [Fact]
    public void Completed_batches_survive_their_plan()
    {
        // A completed batch already produced its result; cancelling it would rewrite history and
        // hide the work it did.
        Assert.False(AiProjectWorkPlanService.ShouldCancelBatchWithPlan(AiProjectWorkBatchStatus.Completed));
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Cancelled")]
    [InlineData("Superseded")]
    public void Retired_plan_statuses_block_execution(string status)
    {
        Assert.True(CodeMasterAgentToolFactory.IsTerminalPlanStatus(status));
    }

    [Theory]
    [InlineData("Active")]
    [InlineData("AwaitingConfirmation")]
    [InlineData("Blocked")]
    [InlineData("Draft")]
    [InlineData("")]
    [InlineData(null)]
    public void Live_plan_statuses_allow_execution(string? status)
    {
        Assert.False(CodeMasterAgentToolFactory.IsTerminalPlanStatus(status));
    }
}
