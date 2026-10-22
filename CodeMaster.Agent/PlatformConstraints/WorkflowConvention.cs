namespace CodeMaster.Agent.PlatformConstraints;

/// <summary>
/// Hard rules for the metadata-only plan pipeline. The plan ends at ConsistencyCheck:
/// code generation is a manual user action, so the agent never schedules it.
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
        "ConsistencyCheck"
    };

    public string GenerateCodeHardRule =>
        "The agent never generates code and never schedules a GenerateCode stage. It only writes metadata rows (sys_*). Code generation is a manual action the user triggers from the UI; the generator then diffs the metadata and edits the existing source in place. Do not call generation, build, EF migration, or startup tools.";

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
        - Six metadata stages, fixed order: {string.Join(" -> ", StageOrder)}
        - Every stage uses GenerationMode={StagesSixGenerationMode}; no stage requests generation or build
        - {GenerateCodeHardRule}
        - {DependencyRule}
        - {ConfirmationRule}
        - {RepairRule}
        - {MinimalModeRule}
        """;
}