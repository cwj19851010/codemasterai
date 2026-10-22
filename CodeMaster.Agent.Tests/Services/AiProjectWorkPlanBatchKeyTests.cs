using CodeMaster.Agent.Services;

namespace CodeMaster.Agent.Tests.Services;

/// <summary>
/// Regression guard for the batch key used when a confirmed work plan starts a run.
/// <para>
/// The key used to be clamped with <c>Substring(0, Math.Min(64, 24 + 20))</c> — a hardcoded 44.
/// The natural key is only 40 characters (a 19-digit snowflake id is longer than the constant
/// assumed), so the clamp was <em>longer</em> than the string and every call threw
/// "Index and length must refer to a location within the string". The result was that a confirmed
/// plan could never advance past its first batch.
/// </para>
/// </summary>
public sealed class AiProjectWorkPlanBatchKeyTests
{
    private static readonly DateTime RunAt = new(2026, 10, 14, 16, 46, 29, DateTimeKind.Utc);

    [Fact]
    public void Builds_the_expected_key_for_a_real_snowflake_id()
    {
        // "batch-" (6) + id (19) + "-" (1) + yyyyMMddHHmmss (14) = 40 chars.
        var key = AiProjectWorkPlanService.BuildBatchKey(2110411933080489984, RunAt);

        Assert.Equal("batch-2110411933080489984-20261014164629", key);
        Assert.Equal(40, key.Length);
    }

    [Fact]
    public void Does_not_throw_for_the_current_snowflake_id_range()
    {
        // The old hardcoded clamp of 44 blew up on exactly this shape of input.
        var key = AiProjectWorkPlanService.BuildBatchKey(2110386171900203008, RunAt);

        Assert.StartsWith("batch-2110386171900203008-", key, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(2110411933080489984L)]
    [InlineData(long.MaxValue)]
    public void Stays_within_the_batch_key_column_width(long planId)
    {
        var key = AiProjectWorkPlanService.BuildBatchKey(planId, RunAt);

        Assert.True(key.Length <= 64, $"batch_key must fit the 64-char column, got {key.Length}.");
    }

    [Fact]
    public void Keys_differ_between_runs_of_the_same_plan()
    {
        // The key doubles as the run identity, so two runs must not collide.
        var first = AiProjectWorkPlanService.BuildBatchKey(42, RunAt);
        var second = AiProjectWorkPlanService.BuildBatchKey(42, RunAt.AddSeconds(1));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Keys_differ_between_plans_in_the_same_second()
    {
        var first = AiProjectWorkPlanService.BuildBatchKey(42, RunAt);
        var second = AiProjectWorkPlanService.BuildBatchKey(43, RunAt);

        Assert.NotEqual(first, second);
    }
}
