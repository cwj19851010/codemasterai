namespace CodeMaster.Agent.Services;

public sealed class AiProjectRepairOptions
{
    public bool SourceRepairEnabled { get; set; }
    public string[] AllowedWorkspaceRoots { get; set; } = [];
    public string[] ValidationProfiles { get; set; } = ["agent-regression"];
    public int MaxAttemptsPerSignature { get; set; } = 2;
    public int StaleExecutionTimeoutMinutes { get; set; } = 15;
}
