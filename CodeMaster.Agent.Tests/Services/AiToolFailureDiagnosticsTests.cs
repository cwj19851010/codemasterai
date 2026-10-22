using CodeMaster.Agent.Services;
using CodeMaster.Agent.Tools;

namespace CodeMaster.Agent.Tests.Services;

/// <summary>
/// 工具失败诊断的回归测试。
/// <para>
/// 红色横条要回答两个问题：哪个阶段失败、下一步怎么办。而 <c>propose_project_change_set</c>
/// 一个工具承载实体/关系/控件三个阶段，光看工具名只能落到 "Workflow"，所以校验类失败必须
/// 从报错文案再定位一次；同时真正的工具异常不能被误判成校验失败，否则建议会指向错误的修复方向。
/// </para>
/// </summary>
public sealed class AiToolFailureDiagnosticsTests
{
    private const string ChangeSetTool = CodeMasterAgentToolCatalog.ProposeProjectChangeSet;

    [Fact]
    public void Control_type_mismatch_is_classified_as_validation()
    {
        var failure = Inspect("Field 'Amount' uses number and must have a numeric data type.");

        Assert.Equal("Validation", failure.FailureKind);
        Assert.Contains("Amount", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Control_type_mismatch_is_attributed_to_field_controls_stage()
    {
        var failure = Inspect("Field 'Amount' uses number and must have a numeric data type.");

        var stage = AiToolStageResolver.RefineFromMessage(failure.ToolStage, failure.Message);

        Assert.Equal("FieldControls", stage);
    }

    [Fact]
    public void Relation_type_mismatch_is_attributed_to_relations_stage()
    {
        var failure = Inspect("Relation field types must match: CustomerId and Id.");

        var stage = AiToolStageResolver.RefineFromMessage(failure.ToolStage, failure.Message);

        Assert.Equal("Relations", stage);
    }

    [Fact]
    public void Validation_failure_suggests_fixing_the_named_metadata()
    {
        var failure = Inspect("Field 'Amount' uses number and must have a numeric data type.");

        Assert.Contains("data type", failure.NextAction!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Genuine_tool_exception_is_not_misread_as_validation()
    {
        var failure = Inspect("Object reference not set to an instance of an object.");

        Assert.NotEqual("Validation", failure.FailureKind);
    }

    [Fact]
    public void Unknown_failure_keeps_the_workflow_stage()
    {
        var failure = Inspect("Object reference not set to an instance of an object.");

        var stage = AiToolStageResolver.RefineFromMessage(failure.ToolStage, failure.Message);

        Assert.Equal("Workflow", stage);
    }

    [Fact]
    public void Refinement_never_overrides_a_stage_already_resolved_from_the_tool_name()
    {
        // upsert_project_work_plan 已经明确是 plan 阶段，报错文案不该把它改成 FieldControls。
        var stage = AiToolStageResolver.RefineFromMessage("plan", "must have a numeric data type");

        Assert.Equal("plan", stage);
    }

    private static AiToolFailurePayload Inspect(string message) => AiToolFailureInspector.Inspect(
        toolName: ChangeSetTool,
        toolStage: AiToolStageResolver.Resolve(ChangeSetTool),
        exception: new InvalidOperationException(message),
        correlationId: "req-test",
        nextAction: null,
        attempt: 1);
}
