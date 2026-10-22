using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Serialization;
using CodeMaster.Agent.Tools;
using Microsoft.Extensions.AI;

namespace CodeMaster.Agent.Tests.Services;

public sealed class WorkPlanUpsertFunctionTests
{
    [Theory]
    [InlineData("\"metadata-structure-hash-not-a-revision\"")]
    [InlineData("\"9223372036854775808\"")]
    [InlineData("9223372036854775808")]
    [InlineData("1.5")]
    [InlineData("1e3")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task Creation_discards_only_irrelevant_version_and_preserves_all_task_definitions(string expected)
    {
        UpsertAiProjectWorkPlanRequest? captured = null;
        var function = Tool(input => { captured = input; return Task.FromResult("saved"); });
        var raw = JsonNode.Parse("""
            {"projectId":"2100250881390940160","conversationId":"2101308012084989952",
             "title":"工程管理软件","summary":"工程、材料、人工、财务收付及报表",
             "stages":[{"stageKey":"entities","kind":"EntityFields","title":"完整业务建模","tasks":[
               {"taskKey":"business","title":"保存业务对象","operationKind":"project_change_set",
                "definition":{"generationMode":"None","entities":[{"name":"EngineeringProject","fields":[{"name":"Amount","dataType":"decimal"}]},{"name":"Payment"}]}}
             ]}]}
            """)!.AsObject();
        raw["expectedStructureVersion"] = JsonNode.Parse(expected);
        var originalDefinition = raw["stages"]![0]!["tasks"]![0]!["definition"]!.ToJsonString();
        var result = await function.InvokeAsync(Arguments(raw.ToJsonString()));
        Assert.Equal("saved", result?.ToString());
        Assert.NotNull(captured);
        Assert.Null(captured.ExpectedStructureVersion);
        Assert.Null(captured.PlanId);
        Assert.Equal(2100250881390940160, captured.ProjectId);
        Assert.Equal(2101308012084989952, captured.ConversationId);
        var task = Assert.Single(Assert.Single(captured.Stages).Tasks);
        Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(originalDefinition).RootElement, task.Definition!.Value));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"hash-value\"")]
    [InlineData("\"9223372036854775808\"")]
    [InlineData("9223372036854775808")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("\"\"")]
    [InlineData("1.5")]
    [InlineData("1.0")]
    [InlineData("1e3")]
    [InlineData("\"1e3\"")]
    [InlineData("{}")]
    public async Task Invalid_update_version_returns_recoverable_feedback_without_invoking_write(string version)
    {
        var function = Tool(_ => throw new InvalidOperationException("Must not write"));
        var result = await function.InvokeAsync(Arguments($"{{\"planId\":\"2101308012084989952\",\"expectedStructureVersion\":{version}}}"));
        using var json = JsonDocument.Parse(result!.ToString()!);
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("invalid_plan_version", json.RootElement.GetProperty("code").GetString());
        Assert.Equal("$.expectedStructureVersion", json.RootElement.GetProperty("path").GetString());
        Assert.Equal("get_project_work_plan", json.RootElement.GetProperty("recovery").GetProperty("action").GetString());
    }

    [Theory]
    [InlineData("1", 1L)]
    [InlineData("\"2\"", 2L)]
    [InlineData("\"9223372036854775807\"", long.MaxValue)]
    public async Task Updates_keep_exact_version_and_snowflake_id(string version, long expected)
    {
        UpsertAiProjectWorkPlanRequest? captured = null;
        var result = await Tool(input => { captured = input; return Task.FromResult("updated"); })
            .InvokeAsync(Arguments($"{{\"planId\":\"2101308012084989952\",\"expectedStructureVersion\":{version},\"preserveUnspecified\":true}}"));
        Assert.Equal("updated", result?.ToString());
        Assert.Equal(expected, captured!.ExpectedStructureVersion);
        Assert.Equal(2101308012084989952, captured.PlanId);
        Assert.True(captured.PreserveUnspecified);
    }

    [Theory]
    [InlineData("{\"planId\":\"wrong-id\",\"expectedStructureVersion\":1}", "invalid_plan_id")]
    [InlineData("{\"planId\":0,\"expectedStructureVersion\":1}", "invalid_plan_id")]
    [InlineData("{\"planId\":2101308012084989952.0,\"expectedStructureVersion\":1}", "invalid_plan_id")]
    [InlineData("{\"planId\":123}", "invalid_plan_version")]
    [InlineData("{\"planId\":null,\"PlanId\":123}", "duplicate_plan_property")]
    [InlineData("[]", "invalid_plan_input")]
    public async Task Invalid_identity_never_falls_back_to_create(string input, string code)
    {
        var result = await Tool(_ => throw new InvalidOperationException("Must not write")).InvokeAsync(Arguments(input));
        using var json = JsonDocument.Parse(result!.ToString()!);
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Required_payload_returns_guidance_and_typed_local_input_stays_compatible()
    {
        var function = Tool(_ => Task.FromResult("saved"));
        Assert.Contains(function.JsonSchema.GetProperty("required").EnumerateArray(), value => value.GetString() == "input");
        var result = await function.InvokeAsync(new AIFunctionArguments());
        Assert.Contains("missing_plan_input", result!.ToString());
        result = await function.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["input"] = new UpsertAiProjectWorkPlanRequest { Title = "Typed local plan", ProjectId = 9, ConversationId = 3 }
        }));
        Assert.Equal("saved", result?.ToString());
    }

    [Fact]
    public async Task Nested_numeric_error_keeps_json_path_and_never_becomes_a_generic_FormatException()
    {
        var result = await Tool(_ => throw new InvalidOperationException("Must not write"))
            .InvokeAsync(Arguments("{\"projectId\":1e3}"));
        using var json = JsonDocument.Parse(result!.ToString()!);
        Assert.Equal("$.projectId", json.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Cancellation_and_service_conflicts_are_not_swallowed_or_retried()
    {
        using var token = new CancellationTokenSource();
        token.Cancel();
        var function = Tool(_ => throw new InvalidOperationException("stale plan version"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => function.InvokeAsync(Arguments("{}"), token.Token).AsTask());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => function.InvokeAsync(
            Arguments("{\"planId\":123,\"expectedStructureVersion\":1}")).AsTask());
        Assert.Equal("stale plan version", error.Message);
    }

    private static AIFunction Tool(Func<UpsertAiProjectWorkPlanRequest, Task<string>> execute) =>
        CodeMasterAgentToolCatalog.Create(false, () => Task.FromResult(""), false, () => Task.FromResult(""),
            false, () => Task.FromResult(""), false, _ => Task.FromResult(""), false, _ => Task.FromResult(""),
            false, _ => Task.FromResult(""), false, _ => Task.FromResult(""), _ => Task.FromResult(""),
            false, _ => Task.FromResult(""), includeWorkPlan: true, getProjectWorkPlan: () => Task.FromResult("null"),
            upsertProjectWorkPlan: input => execute(input))
            .OfType<AIFunction>().Single(tool => tool.Name == CodeMasterAgentToolCatalog.UpsertProjectWorkPlan);

    private static AIFunctionArguments Arguments(string input)
    {
        using var document = JsonDocument.Parse(input);
        return new(new Dictionary<string, object?> { ["input"] = document.RootElement.Clone() });
    }
}
