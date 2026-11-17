using CodeMaster.Agent.Services;

namespace CodeMaster.Agent.Tests.Services;

/// <summary>
/// Regression cover for the second source of "already exists" floods.
///
/// A batch is not one transaction: every operation commits on its own. When task 4 of 5 failed,
/// the batch was closed by marking <em>all</em> tasks Failed — including the three whose operations
/// had already committed. The repair flow then re-ran them, and a create-intent change set replayed
/// against rows that already exist reports one "already exists" per item.
///
/// The rule pinned here: a task whose operation committed stays Completed even when a later task in
/// the same batch fails.
/// </summary>
public sealed class AiWorkPlanBatchFailureBookkeepingTests
{
    [Fact]
    public void Committed_task_stays_completed_when_a_later_task_fails()
    {
        string[] committed = ["entity-fields.orders", "entity-fields.suppliers"];
        Assert.True(AiProjectWorkPlanService.ShouldMarkTaskCompletedOnBatchFailure(committed, "entity-fields.orders"));
    }

    [Fact]
    public void Committed_task_keys_match_case_insensitively()
    {
        string[] committed = ["EntityFields.Orders"];
        Assert.True(AiProjectWorkPlanService.ShouldMarkTaskCompletedOnBatchFailure(committed, "entityfields.orders"));
    }

    [Fact]
    public void Task_that_never_committed_is_marked_failed()
    {
        string[] committed = ["entity-fields.orders"];
        Assert.False(AiProjectWorkPlanService.ShouldMarkTaskCompletedOnBatchFailure(committed, "entity-fields.payments"));
    }

    [Fact]
    public void First_task_failure_marks_every_task_failed()
    {
        // Nothing committed yet, so the whole batch is genuinely failed.
        Assert.False(AiProjectWorkPlanService.ShouldMarkTaskCompletedOnBatchFailure(null, "entity-fields.orders"));
        Assert.False(AiProjectWorkPlanService.ShouldMarkTaskCompletedOnBatchFailure([], "entity-fields.orders"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_task_key_is_never_treated_as_committed(string? taskKey)
    {
        string[] committed = ["entity-fields.orders"];
        Assert.False(AiProjectWorkPlanService.ShouldMarkTaskCompletedOnBatchFailure(committed, taskKey));
    }
}
