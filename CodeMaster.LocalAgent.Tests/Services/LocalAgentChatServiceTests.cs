using CodeMaster.Agent.Tools;
using CodeMaster.LocalAgent.Services;

namespace CodeMaster.LocalAgent.Tests.Services;

public class LocalAgentChatServiceTests
{
    [Theory]
    [MemberData(nameof(ModelConnectionExceptions))]
    public void IsModelConnectionTimeout_RecognizesInterruptedHttpConnections(Exception exception)
    {
        Assert.True(LocalAgentChatService.IsModelConnectionTimeout(exception));
    }

    [Theory]
    [InlineData("Service request failed. Status: 504 (Gateway Timeout)")]
    [InlineData("The response ended prematurely. (ResponseEnded)")]
    public void IsStreamingTransportFailure_RecognizesProviderStreamFailures(string message)
    {
        Assert.True(LocalAgentChatService.IsStreamingTransportFailure(new InvalidOperationException(message)));
    }

    [Fact]
    public void IsStreamingTransportFailure_DoesNotTreatUnauthorizedAsStreamFailure()
    {
        Assert.False(LocalAgentChatService.IsStreamingTransportFailure(
            new InvalidOperationException("Service request failed. Status: 401 (Unauthorized)")));
    }

    public static TheoryData<Exception> ModelConnectionExceptions => new()
    {
        new HttpRequestException("SSL connection failed."),
        new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely."),
        new InvalidOperationException("outer", new TimeoutException("timed out"))
    };

    [Theory]
    [InlineData(CodeMasterAgentToolCatalog.ProposeProjectChangeSet, "项目变更方案")]
    [InlineData(CodeMasterAgentToolCatalog.ProposeUiPageEnhancement, "页面调整方案")]
    [InlineData(CodeMasterAgentToolCatalog.ProposeDictionaryChange, "数据字典方案")]
    public void BuildRecoveredProposalResponse_UsesConciseBusinessMessage(
        string toolName,
        string expectedProposalName)
    {
        var response = LocalAgentChatService.BuildRecoveredProposalResponse(toolName);

        Assert.Contains(expectedProposalName, response);
        Assert.Contains("请在下方确认后应用到 CodeMaster", response);
        Assert.DoesNotContain("待审批", response);
        Assert.DoesNotContain("模型连接", response);
    }
}
