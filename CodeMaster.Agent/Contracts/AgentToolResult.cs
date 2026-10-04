namespace CodeMaster.Agent.Contracts;

/// <summary>
/// Unified result envelope for every tool invocation. Tools return a structured
/// result instead of raw JSON so the runner can route success, validation,
/// and execution failures through the same code path. Tools that historically
/// returned a JSON string keep returning Output for backwards compatibility.
/// </summary>
public sealed class AgentToolResult
{
    public bool Success { get; set; } = true;

    /// <summary>Serialized output payload. Always populated, even on failure.</summary>
    public string Output { get; set; } = string.Empty;

    /// <summary>Machine-readable error code (see AgentErrorCodes).</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Human-readable error description.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>How the runner should interpret this failure.</summary>
    public AgentToolFailureKind FailureKind { get; set; } = AgentToolFailureKind.None;
}

public enum AgentToolFailureKind
{
    None = 0,
    /// <summary>Tool name or arguments failed pre-execution validation.</summary>
    ArgumentsInvalid = 1,
    /// <summary>Tool ran but threw or returned an error.</summary>
    ExecutionFailed = 2,
    /// <summary>Same tool failure has repeated too many times; stop retrying.</summary>
    RetryLimitReached = 3,
    /// <summary>Authorization / permissions failed.</summary>
    Unauthorized = 4,
}
