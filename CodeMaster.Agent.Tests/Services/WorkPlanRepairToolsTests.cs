using CodeMaster.Agent.Services;

namespace CodeMaster.Agent.Tests.Services;

public sealed class WorkPlanRepairToolsTests
{
    [Theory]
    [InlineData("请帮我修复当前任务计划,任务 4.3 出错了")]
    [InlineData("missing an executable definition for stage 4.3")]
    public void Repair_request_includes_supersede_tool(string content)
    {
        Assert.True(AiTurnToolSelector.IsWorkPlanRepairRequest(content));
    }
}