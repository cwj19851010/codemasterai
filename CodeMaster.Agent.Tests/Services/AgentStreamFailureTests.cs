using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Services;
using CodeMaster.Agent.Tools;
using CodeMaster.Application.Services.Monitor;
using CodeMaster.Domain.Entities.Monitor;
using CodeMaster.WebApi.Controllers;
using CodeMaster.WebApi.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeMaster.Agent.Tests.Services;

public sealed class AgentStreamFailureTests
{
    [Fact]
    public async Task Streaming_error_is_sent_to_browser_and_logged_as_failed_despite_http_200()
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/agent/chat/stream";
        http.Request.Method = "POST";
        http.Response.Body = new MemoryStream();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "test")], "test"));
        var conversations = Stub<IAiConversationService>((method, _) => method.Name == "SendStreamingAsync"
            ? Task.FromException<AiChatResult>(new InvalidOperationException("模型请求失败; api_key=private-token"))
            : throw new InvalidOperationException(method.Name));
        var controller = new AgentController(null!, conversations, null!, null!)
        { ControllerContext = new ControllerContext { HttpContext = http } };
        SysOperLog? recorded = null;
        var logs = Stub<ISysOperLogService>((method, args) =>
        {
            Assert.Equal("InsertOperLogAsync", method.Name);
            recorded = (SysOperLog)args![0]!;
            return Task.FromResult(1L);
        });
        var filter = new GlobalOperationLogFilter(logs, NullLogger<GlobalOperationLogFilter>.Instance);
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var context = new ActionExecutingContext(action, [], new Dictionary<string, object?>(), controller);
        await filter.OnActionExecutionAsync(context, async () =>
        {
            await controller.StreamChat(new SendAiMessageRequest
            { ConversationId = 1, RequestId = "stream-failure", Content = "你好" }, CancellationToken.None);
            return new ActionExecutedContext(action, [], controller);
        });
        Assert.Equal(200, http.Response.StatusCode);
        Assert.NotNull(recorded);
        Assert.Equal(1, recorded.Status);
        Assert.Contains("模型请求失败", recorded.ErrorMsg);
        Assert.DoesNotContain("private-token", recorded.ErrorMsg);
        var events = Encoding.UTF8.GetString(((MemoryStream)http.Response.Body).ToArray());
        Assert.Contains("\"event\":\"error\"", events);
        Assert.DoesNotContain("\"event\":\"final\"", events);
        Assert.DoesNotContain("private-token", events);
    }

    [Fact]
    public void Failure_payload_carries_stage_and_suggested_action()
    {
        var payload = AiRequestFailure.SanitizeWithContext(
            "FieldControls 失败:字段类型不匹配",
            stage: "FieldControls",
            suggestedAction: "重新调用 get_entity_blueprint",
            attempt: 2);

        Assert.Equal("FieldControls", payload.Stage);
        Assert.Equal("重新调用 get_entity_blueprint", payload.SuggestedAction);
        Assert.Equal(2, payload.Attempt);
        Assert.Contains("FieldControls", payload.Diagnostic);
    }

    [Fact]
    public void Tool_stage_resolver_maps_plan_tools()
    {
        Assert.Equal("plan", AiToolStageResolver.Resolve(CodeMasterAgentToolCatalog.UpsertProjectWorkPlan));
        Assert.Equal("plan", AiToolStageResolver.Resolve(CodeMasterAgentToolCatalog.SupersedeProjectWorkPlan));
        Assert.Equal("EntityFields", AiToolStageResolver.Resolve(CodeMasterAgentToolCatalog.GetEntityBlueprint));
        Assert.Equal("Workflow", AiToolStageResolver.Resolve("unknown_tool"));
    }

    [Fact]
    public void Failure_inspector_classifies_infrastructure_exception_with_stack()
    {
        var payload = SafeInspect(() => throw new HttpRequestException("network is unreachable"));

        Assert.Equal("Infrastructure", payload.FailureKind);
        Assert.Equal("HttpRequestException", payload.ExceptionType);
        Assert.Contains("Model channel failed", payload.NextAction);
    }

    [Fact]
    public void Failure_inspector_classifies_null_reference_from_own_assembly()
    {
        Exception caught;
        try
        {
            Thrower.Boom();
            throw new InvalidOperationException("unreachable");
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        var payload = AiToolFailureInspector.Inspect(
            toolName: CodeMasterAgentToolCatalog.GetEntityBlueprint,
            toolStage: "EntityFields",
            exception: caught,
            correlationId: "req-123",
            nextAction: null,
            attempt: 1);

        Assert.Equal("Unknown", payload.FailureKind);
        Assert.Equal("NullReferenceException", payload.ExceptionType);
        Assert.Equal("EntityFields", payload.ToolStage);
        Assert.Equal("req-123", payload.CorrelationId);
        Assert.Equal(1, payload.Attempt);
        Assert.NotNull(payload.ExceptionSource);
        Assert.True(payload.ExceptionSource!.Contains("Thrower", StringComparison.Ordinal));
        Assert.NotNull(payload.StackSnippet);
        Assert.NotEmpty(payload.StackSnippet!);
        Assert.Contains(payload.ToolName, payload.NextAction);
        Assert.Contains(payload.ExceptionType!, payload.NextAction);
    }

    [Fact]
    public void Failure_inspector_classifies_unauthorized()
    {
        var payload = SafeInspect(() => throw new UnauthorizedAccessException("not allowed"));

        Assert.Equal("Authorization", payload.FailureKind);
        Assert.Equal("UnauthorizedAccessException", payload.ExceptionType);
    }

    private static AiToolFailurePayload SafeInspect(Action action)
    {
        Exception caught;
        try
        {
            action();
            throw new InvalidOperationException("Action did not throw.");
        }
        catch (Exception ex)
        {
            caught = ex;
        }
        return AiToolFailureInspector.Inspect(
            toolName: CodeMasterAgentToolCatalog.GetEntityBlueprint,
            toolStage: "EntityFields",
            exception: caught,
            correlationId: "req-1",
            nextAction: null,
            attempt: 1);
    }

    private static class Thrower
    {
        public static void Boom()
        {
            string? value = null;
            _ = value!.Length;
        }
    }

    private static T Stub<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var instance = DispatchProxy.Create<T, ServiceProxy>();
        ((ServiceProxy)(object)instance).Handler = handler;
        return instance;
    }

    public class ServiceProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }
}