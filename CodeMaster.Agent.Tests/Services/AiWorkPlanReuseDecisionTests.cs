using CodeMaster.Agent.Services;
using CodeMaster.Domain.Entities.Ai;

namespace CodeMaster.Agent.Tests.Services;

/// <summary>
/// Regression cover for "one turn created two plans".
///
/// <c>UpsertAsync</c> with no PlanId used to retire the active plan unconditionally. A single
/// model turn could therefore write a hand-made plan, then the compiled one, and the second
/// silently superseded the first — leaving the first plan's PendingApproval batch orphaned. When
/// that batch was approved later it replayed metadata compiled against an older blueprint and
/// produced a wall of "already exists" errors.
///
/// The rule pinned here: creating a plan while another plan is active is only allowed when the
/// active plan has no work left (Replace) or has not executed anything yet (Adopt). Anything else
/// is refused, and discarding it becomes an explicit <c>supersede_project_work_plan</c> call.
/// </summary>
public sealed class AiWorkPlanReuseDecisionTests
{
    [Fact]
    public void In_flight_batch_blocks_a_replacement_plan()
    {
        var decision = AiProjectWorkPlanService.DecideReuseForNewPlan(
            hasInFlightBatch: true,
            completedTaskCount: 0,
            unfinishedTaskCount: 11);

        Assert.Equal(AiProjectWorkPlanReuseDecision.Refuse, decision);
    }

    [Fact]
    public void In_flight_batch_blocks_a_replacement_plan_even_when_every_task_completed()
    {
        var decision = AiProjectWorkPlanService.DecideReuseForNewPlan(
            hasInFlightBatch: true,
            completedTaskCount: 11,
            unfinishedTaskCount: 0);

        Assert.Equal(AiProjectWorkPlanReuseDecision.Refuse, decision);
    }

    [Fact]
    public void Partly_committed_plan_is_not_replaced()
    {
        var decision = AiProjectWorkPlanService.DecideReuseForNewPlan(
            hasInFlightBatch: false,
            completedTaskCount: 3,
            unfinishedTaskCount: 2);

        Assert.Equal(AiProjectWorkPlanReuseDecision.Refuse, decision);
    }

    [Fact]
    public void Plan_that_never_executed_is_upgraded_in_place()
    {
        var decision = AiProjectWorkPlanService.DecideReuseForNewPlan(
            hasInFlightBatch: false,
            completedTaskCount: 0,
            unfinishedTaskCount: 11);

        Assert.Equal(AiProjectWorkPlanReuseDecision.Adopt, decision);
    }

    [Fact]
    public void Fully_finished_plan_is_replaced_for_the_next_round()
    {
        var decision = AiProjectWorkPlanService.DecideReuseForNewPlan(
            hasInFlightBatch: false,
            completedTaskCount: 42,
            unfinishedTaskCount: 0);

        Assert.Equal(AiProjectWorkPlanReuseDecision.Replace, decision);
    }

    [Fact]
    public void Empty_plan_is_replaced()
    {
        var decision = AiProjectWorkPlanService.DecideReuseForNewPlan(
            hasInFlightBatch: false,
            completedTaskCount: 0,
            unfinishedTaskCount: 0);

        Assert.Equal(AiProjectWorkPlanReuseDecision.Replace, decision);
    }

    [Theory]
    [InlineData(AiProjectWorkBatchStatus.Draft, true)]
    [InlineData(AiProjectWorkBatchStatus.PendingApproval, true)]
    [InlineData(AiProjectWorkBatchStatus.Approved, true)]
    [InlineData(AiProjectWorkBatchStatus.WaitingClientExecution, true)]
    [InlineData(AiProjectWorkBatchStatus.Executing, true)]
    [InlineData(AiProjectWorkBatchStatus.Completed, false)]
    [InlineData(AiProjectWorkBatchStatus.Failed, false)]
    [InlineData(AiProjectWorkBatchStatus.Rejected, false)]
    [InlineData(AiProjectWorkBatchStatus.Stale, false)]
    [InlineData(AiProjectWorkBatchStatus.Cancelled, false)]
    public void Batch_is_in_flight_only_while_it_can_still_write_metadata(AiProjectWorkBatchStatus status, bool expected)
    {
        Assert.Equal(expected, AiProjectWorkPlanService.IsBatchInFlight(status));
    }
}
