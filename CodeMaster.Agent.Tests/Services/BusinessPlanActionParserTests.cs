using CodeMaster.Agent.Services;

namespace CodeMaster.Agent.Tests.Services;

public sealed class BusinessPlanActionParserTests
{
    [Fact]
    public void Parses_direct_business_plan_json_without_a_tool_call()
    {
        const string text = """
        {
          "modules": [{ "key": "engineering", "technicalName": "Engineering", "displayName": "工程管理" }],
          "entities": [{ "key": "project", "moduleKey": "engineering", "technicalName": "Project", "displayName": "工程项目" }]
        }
        """;

        Assert.True(BusinessPlanActionParser.TryParse(text, out var definition));
        Assert.Equal("Engineering", Assert.Single(definition.Modules).TechnicalName);
        Assert.Equal("Project", Assert.Single(definition.Entities).TechnicalName);
    }

    [Fact]
    public void Parses_wrapped_json_and_markdown_fences()
    {
        const string text = """
        下面是计划：
        ```json
        {
          "action": "submit_business_plan",
          "businessPlan": {
            "modules": [{ "key": "engineering", "technicalName": "Engineering", "displayName": "工程管理" }],
            "entities": []
          }
        }
        ```
        """;

        Assert.True(BusinessPlanActionParser.TryParse(text, out var definition));
        Assert.Equal("Engineering", Assert.Single(definition.Modules).TechnicalName);
    }

    [Fact]
    public void Rejects_plain_prose_and_wrong_actions()
    {
        Assert.False(BusinessPlanActionParser.TryParse("请开始创建工程管理系统", out _));
        Assert.False(BusinessPlanActionParser.TryParse(
            "{\"action\":\"upsert_project_work_plan\",\"modules\":[{\"key\":\"x\"}]}",
            out _));
    }
}
