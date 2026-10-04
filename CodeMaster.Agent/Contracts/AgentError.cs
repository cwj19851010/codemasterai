namespace CodeMaster.Agent.Contracts;

/// <summary>
/// Canonical error codes used across Agent execution. Tools, runner, and
/// streaming events should use these instead of free-form messages so the UI
/// can map them to localized text and the team can grep for failure modes.
/// </summary>
public static class AgentErrorCodes
{
    public const string ModelRequestFailed = "ModelRequestFailed";
    public const string ProviderRateLimited = "ProviderRateLimited";
    public const string ProviderTimeout = "ProviderTimeout";
    public const string ModelEmptyOutput = "ModelEmptyOutput";
    public const string ModelOutputInvalid = "ModelOutputInvalid";
    public const string ToolArgumentsInvalid = "ToolArgumentsInvalid";
    public const string ToolExecutionFailed = "ToolExecutionFailed";
    public const string PlanCompileFailed = "PlanCompileFailed";
    public const string PlanVersionConflict = "PlanVersionConflict";
    public const string MetadataValidationFailed = "MetadataValidationFailed";
    public const string TaskBlocked = "TaskBlocked";
    public const string UserActionRequired = "UserActionRequired";
    public const string GenerationFailed = "GenerationFailed";
    public const string Cancelled = "Cancelled";
}
