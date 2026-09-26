namespace CodeMaster.Agent.PlatformConstraints;

/// <summary>
/// Hard rules for the seven-stage plan pipeline.
/// </summary>
public sealed record WorkflowConvention
{
    public IReadOnlyList<string> StageOrder { get; init; } = new[]
    {
        "EntityFields",
        "Views",
        "Relations",
        "FieldControls",
        "ComplexPages",
        "ConsistencyCheck",
        "GenerateCode"
    };

    public string GenerateCodeHardRule =>
        "GenerationMode=Incremental; do NOT request EF migration, runtime startup, or service initialization from the Agent. The user confirms and the WebApi owns those steps.";

    public string StagesSixGenerationMode => "None";

    public string DependencyRule =>
        "Use explicit DependsOnTaskKeys so every later stage waits for the preceding saved state. Never duplicate an earlier stage just because a later stage discovers a dependency.";

    public string ConfirmationRule =>
        "Give the user one concise confirmation summary for the complete business change; do not discuss internal batches or checkpoints.";

    public string RepairRule =>
        "Repair flows update the existing plan with upsert_project_work_plan using the current PlanId and ExpectedStructureVersion, PreserveUnspecified=true, and only the affected stage/task.";

    public string MinimalModeRule =>
        "In Minimal mode the client confirms and executes the first ready task automatically. Never ask the user to confirm an internal plan.";

    public string RenderForPrompt() =>
        $"""
        Workflow conventions:
        - Seven stages, fixed order: {string.Join(" -> ", StageOrder)}
        - First six stages use GenerationMode={StagesSixGenerationMode}; only the final GenerateCode task uses GenerationMode=Incremental
        - {GenerateCodeHardRule}
        - {DependencyRule}
        - {ConfirmationRule}
        - {RepairRule}
        - {MinimalModeRule}
        """;
}