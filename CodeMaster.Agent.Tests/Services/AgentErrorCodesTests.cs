using CodeMaster.Agent.Contracts;
using Xunit;

namespace CodeMaster.Agent.Tests.Services;

public class AgentErrorCodesTests
{
    [Fact]
    public void Constants_AreStableAndDistinct()
    {
        // These strings are part of the public protocol between Agent tools,
        // UI, and persistence. Changing them breaks grep + i18n mappings.
        Assert.Equal("ModelRequestFailed", AgentErrorCodes.ModelRequestFailed);
        Assert.Equal("ProviderRateLimited", AgentErrorCodes.ProviderRateLimited);
        Assert.Equal("ProviderTimeout", AgentErrorCodes.ProviderTimeout);
        Assert.Equal("ModelEmptyOutput", AgentErrorCodes.ModelEmptyOutput);
        Assert.Equal("ModelOutputInvalid", AgentErrorCodes.ModelOutputInvalid);
        Assert.Equal("ToolArgumentsInvalid", AgentErrorCodes.ToolArgumentsInvalid);
        Assert.Equal("ToolExecutionFailed", AgentErrorCodes.ToolExecutionFailed);
        Assert.Equal("PlanCompileFailed", AgentErrorCodes.PlanCompileFailed);
        Assert.Equal("PlanVersionConflict", AgentErrorCodes.PlanVersionConflict);
        Assert.Equal("MetadataValidationFailed", AgentErrorCodes.MetadataValidationFailed);
        Assert.Equal("TaskBlocked", AgentErrorCodes.TaskBlocked);
        Assert.Equal("UserActionRequired", AgentErrorCodes.UserActionRequired);
        Assert.Equal("GenerationFailed", AgentErrorCodes.GenerationFailed);
        Assert.Equal("Cancelled", AgentErrorCodes.Cancelled);
    }

    [Fact]
    public void ToolResult_DefaultsAreSafeForSuccess()
    {
        var result = new AgentToolResult();
        Assert.True(result.Success);
        Assert.Equal(string.Empty, result.Output);
        Assert.Null(result.ErrorCode);
        Assert.Null(result.ErrorMessage);
        Assert.Equal(AgentToolFailureKind.None, result.FailureKind);
    }

    [Fact]
    public void ToolResult_FailureShapeCarriesCodeAndKind()
    {
        var result = new AgentToolResult
        {
            Success = false,
            Output = "{}",
            ErrorCode = AgentErrorCodes.ToolArgumentsInvalid,
            ErrorMessage = "missing field 'name'",
            FailureKind = AgentToolFailureKind.ArgumentsInvalid,
        };
        Assert.False(result.Success);
        Assert.Equal(AgentErrorCodes.ToolArgumentsInvalid, result.ErrorCode);
        Assert.Equal(AgentToolFailureKind.ArgumentsInvalid, result.FailureKind);
    }

    [Fact]
    public void FailureKind_HasExpectedValues()
    {
        Assert.Equal(0, (int)AgentToolFailureKind.None);
        Assert.Equal(1, (int)AgentToolFailureKind.ArgumentsInvalid);
        Assert.Equal(2, (int)AgentToolFailureKind.ExecutionFailed);
        Assert.Equal(3, (int)AgentToolFailureKind.RetryLimitReached);
        Assert.Equal(4, (int)AgentToolFailureKind.Unauthorized);
    }
}
