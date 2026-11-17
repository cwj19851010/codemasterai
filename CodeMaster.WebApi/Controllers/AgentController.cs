using System.Text.Json;
using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Serialization;
using CodeMaster.Agent.Services;
using CodeMaster.Agent.Tools;
using CodeMaster.Core.Common;
using CodeMaster.WebApi.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CodeMaster.WebApi.Controllers;

[ApiController]
[Authorize]
[Route("api/agent")]
public sealed class AgentController : ControllerBase
{
    private readonly IAiProviderService _providerService;
    private readonly IAiConversationService _conversationService;
    private readonly IAiProjectWorkPlanService _workPlanService;
    private readonly ICodeMasterAgentToolFactory _toolFactory;

    public AgentController(
        IAiProviderService providerService,
        IAiConversationService conversationService,
        IAiProjectWorkPlanService workPlanService,
        ICodeMasterAgentToolFactory toolFactory)
    {
        _providerService = providerService;
        _conversationService = conversationService;
        _workPlanService = workPlanService;
        _toolFactory = toolFactory;
    }

    [HttpGet("providers")]
    public Task<IActionResult> GetProviders([FromQuery] string? executionMode = null)
        => ExecuteAsync(async () => await _providerService.GetListAsync(executionMode));

    [HttpPost("providers")]
    public Task<IActionResult> CreateProvider([FromBody] SaveAiProviderRequest input)
        => ExecuteAsync(async () => await _providerService.CreateAsync(input));

    [HttpPut("providers/{id:long}")]
    public Task<IActionResult> UpdateProvider(long id, [FromBody] SaveAiProviderRequest input)
        => ExecuteAsync(async () => await _providerService.UpdateAsync(id, input));

    [HttpDelete("providers/{id:long}")]
    public Task<IActionResult> DeleteProvider(long id)
        => ExecuteAsync(async () => await _providerService.DeleteAsync(id));

    [HttpPost("providers/{id:long}/test")]
    public Task<IActionResult> TestProvider(long id)
        => ExecuteAsync(async () => await _providerService.TestAsync(id));

    [HttpGet("conversations")]
    public Task<IActionResult> GetConversations(
        [FromQuery] long projectId,
        [FromQuery] string? executionMode = null)
        => ExecuteAsync(async () => await _conversationService.GetListAsync(projectId, executionMode));

    [HttpPost("conversations")]
    public Task<IActionResult> CreateConversation([FromBody] CreateAiConversationRequest input)
        => ExecuteAsync(async () => await _conversationService.CreateAsync(input));

    [HttpPut("conversations/{id:long}/preferences")]
    public Task<IActionResult> UpdateConversationPreferences(
        long id,
        [FromBody] UpdateAiConversationPreferencesRequest input)
        => ExecuteAsync(async () => await _conversationService.UpdatePreferencesAsync(id, input));

    /// <summary>
    /// The single explicit authorization point: moves the conversation between the
    /// requirements page and the code page. Nothing generates code before this is called
    /// with AgentMode=Code.
    /// </summary>
    [HttpPost("conversations/{id:long}/mode")]
    public Task<IActionResult> SwitchAgentMode(
        long id,
        [FromBody] SwitchAiAgentModeRequest input)
        => ExecuteAsync(async () => await _conversationService.SwitchAgentModeAsync(
            new SwitchAiAgentModeRequest
            {
                ConversationId = id,
                AgentMode = input.AgentMode,
                Force = input.Force
            }));

    [HttpPut("conversations/{id:long}/provider")]
    public Task<IActionResult> UpdateConversationProvider(
        long id,
        [FromBody] UpdateAiConversationProviderRequest input)
        => ExecuteAsync(async () => await _conversationService.UpdateProviderAsync(id, input));

    [HttpDelete("conversations/{id:long}")]
    public Task<IActionResult> ArchiveConversation(long id)
        => ExecuteAsync(async () => await _conversationService.ArchiveAsync(id));

    [HttpGet("conversations/{id:long}/messages")]
    public Task<IActionResult> GetMessages(long id)
        => ExecuteAsync(async () => await _conversationService.GetMessagesAsync(id));

    [HttpGet("conversations/{id:long}/tools")]
    public Task<IActionResult> GetToolExecutions(long id)
        => ExecuteAsync(async () => await _conversationService.GetToolExecutionsAsync(id));

    /// <summary>
    /// Uploads one Markdown / plain-text document and returns its descriptor. The descriptor is
    /// then sent back with the chat message in <see cref="SendAiMessageRequest.Attachments"/>,
    /// which is when the text is actually inlined into the model input.
    /// </summary>
    [HttpPost("attachments")]
    [RequestSizeLimit(4 * 1024 * 1024)]
    public Task<IActionResult> UploadAttachment([FromQuery] long conversationId, IFormFile? file)
        => ExecuteAsync(async () =>
        {
            if (file == null || file.Length == 0)
                throw new ArgumentException("请选择要上传的文档。");

            await using var stream = file.OpenReadStream();
            return await _conversationService.UploadAttachmentAsync(
                conversationId,
                file.FileName,
                stream,
                file.Length);
        });

    [HttpGet("work-plans/active")]
    public Task<IActionResult> GetActiveWorkPlan([FromQuery] long projectId)
        => ExecuteAsync(async () => await _workPlanService.GetActiveAsync(projectId));

    [HttpGet("work-plans/{id:long}")]
    public Task<IActionResult> GetWorkPlan(long id)
        => ExecuteAsync(async () => await _workPlanService.GetAsync(id));

    [HttpPost("work-plans")]
    public Task<IActionResult> UpsertWorkPlan([FromBody] UpsertAiProjectWorkPlanRequest input, CancellationToken cancellationToken)
        => ExecuteAsync(async () => await _workPlanService.UpsertAsync(input, cancellationToken));

    [HttpPost("work-plans/{id:long}/confirm")]
    public Task<IActionResult> ConfirmWorkPlan(
        long id,
        [FromBody] ConfirmAiProjectWorkPlanRequest input,
        CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var plan = await _workPlanService.ConfirmAsync(id, input, cancellationToken);
            var execution = await _toolFactory.ExecuteNextWorkPlanBatchAsync(
                plan.ConversationId,
                plan.ProjectId,
                plan.Id,
                $"plan-confirm-{plan.Id}-{DateTime.UtcNow.Ticks}");
            plan = await _workPlanService.GetAsync(plan.Id);
            return new ConfirmAiProjectWorkPlanResult
            {
                Plan = plan,
                Execution = execution,
                Message = execution == null
                    ? "工作计划已确认，当前没有可执行任务。"
                    : "工作计划已确认，正在按任务顺序执行。"
            };
        });

    [HttpPost("work-plans/{id:long}/cancel")]
    public Task<IActionResult> CancelWorkPlan(long id, [FromQuery] bool superseded, CancellationToken cancellationToken)
        => ExecuteAsync(async () => await _workPlanService.CancelAsync(id, superseded, cancellationToken));

    [HttpPost("work-plans/{id:long}/resume")]
    public Task<IActionResult> ResumeWorkPlan(
        long id,
        [FromQuery] long? conversationId,
        CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var plan = await _workPlanService.GetAsync(id);
            var resumeConversationId = conversationId ?? plan.ConversationId;
            plan = await _workPlanService.PrepareResumeAsync(id, resumeConversationId, cancellationToken);
            var execution = await _toolFactory.ExecuteNextWorkPlanBatchAsync(
                resumeConversationId,
                plan.ProjectId,
                plan.Id,
                $"plan-resume-{plan.Id}-{DateTime.UtcNow.Ticks}");
            plan = await _workPlanService.GetAsync(plan.Id);
            return new ResumeAiProjectWorkPlanResult
            {
                Plan = plan,
                Execution = execution,
                Message = execution == null ? "当前没有可继续执行的任务。" : "已继续执行下一个就绪批次。"
            };
        });

    [HttpPost("work-plans/{planId:long}/tasks/{taskId:long}/retry")]
    public Task<IActionResult> RetryWorkPlanTask(
        long planId,
        long taskId,
        [FromBody] RetryAiProjectWorkTaskRequest input,
        CancellationToken cancellationToken)
        => ExecuteAsync(async () => await _workPlanService.RetryTaskAsync(planId, taskId, input, cancellationToken));

    [HttpPost("chat")]
    public Task<IActionResult> Chat([FromBody] SendAiMessageRequest input, CancellationToken cancellationToken)
        => ExecuteAsync(async () => await _conversationService.SendAsync(input, cancellationToken));

    [HttpPost("chat/cancel")]
    public Task<IActionResult> CancelChat([FromBody] CancelAiChatRequest input)
        => ExecuteAsync(async () => await _conversationService.CancelAsync(input));

    [HttpPost("chat/stream")]
    public async Task StreamChat([FromBody] SendAiMessageRequest input, CancellationToken cancellationToken)
    {
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers.CacheControl = "no-cache, no-transform";
        Response.Headers.Append("X-Accel-Buffering", "no");
        await Response.StartAsync(cancellationToken);

        var sequence = 0;
        var writeGate = new SemaphoreSlim(1, 1);
        var clientDisconnected = 0;

        async ValueTask WriteEventAsync(AiChatStreamEvent streamEvent, CancellationToken _)
        {
            if (Volatile.Read(ref clientDisconnected) == 1)
                return;

            var acquired = false;
            try
            {
                await writeGate.WaitAsync();
                acquired = true;
                if (Volatile.Read(ref clientDisconnected) == 1)
                    return;

                streamEvent.Sequence = ++sequence;
                streamEvent.Timestamp = DateTime.UtcNow;
                // Push an SSE comment frame first so any reverse proxy or SSE
                // client (notably the Codex desktop app) sees fresh socket bytes
                // during long upstream thinking pauses. SSE comments start with
                // ":" and are ignored by event consumers.
                await Response.WriteAsync($": keep-alive-{streamEvent.Sequence}-{streamEvent.Timestamp:O}\n\n", HttpContext.RequestAborted);
                var json = JsonSerializer.Serialize(streamEvent, AgentJsonSerializer.Options);
                await Response.WriteAsync($"data: {json}\n\n", HttpContext.RequestAborted);
                await Response.Body.FlushAsync(HttpContext.RequestAborted);
            }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
            {
                Interlocked.Exchange(ref clientDisconnected, 1);
            }
            catch (IOException)
            {
                Interlocked.Exchange(ref clientDisconnected, 1);
            }
            catch (Exception) when (HttpContext.RequestAborted.IsCancellationRequested)
            {
                Interlocked.Exchange(ref clientDisconnected, 1);
            }
            finally
            {
                if (acquired)
                    writeGate.Release();
            }
        }

        using var heartbeatCancellation = new CancellationTokenSource();
        var heartbeatTask = RunHeartbeatAsync(input.RequestId ?? string.Empty, WriteEventAsync, heartbeatCancellation.Token);

        async Task StopHeartbeatAsync()
        {
            if (!heartbeatCancellation.IsCancellationRequested)
                await heartbeatCancellation.CancelAsync();
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when the request completes before the next heartbeat.
            }
        }

        try
        {
            await WriteEventAsync(new AiChatStreamEvent
            {
                Event = "connected",
                RequestId = input.RequestId ?? string.Empty,
                Phase = "connected"
            }, CancellationToken.None);

            var result = await _conversationService.SendStreamingAsync(input, WriteEventAsync, cancellationToken);
            await StopHeartbeatAsync();
            await WriteEventAsync(new AiChatStreamEvent
            {
                Event = "final",
                RequestId = result.Message.RequestId ?? input.RequestId ?? string.Empty,
                Phase = "completed",
                Result = result
            }, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            HttpContext.Items[GlobalOperationLogFilter.HandledFailureKey] = "Agent request cancelled.";
            await StopHeartbeatAsync();
            await WriteEventAsync(new AiChatStreamEvent
            {
                Event = "cancelled",
                RequestId = input.RequestId ?? string.Empty,
                Phase = "cancelled"
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            var failure = AiToolFailureInspector.Inspect(
                toolName: "stream-loop",
                toolStage: "Workflow",
                exception: ex,
                correlationId: input.RequestId,
                nextAction: null,
                attempt: 1);
            var diagnostic = AiRequestFailure.Sanitize(ex.GetBaseException().Message);
            HttpContext.Items[GlobalOperationLogFilter.HandledFailureKey] = diagnostic;
            await StopHeartbeatAsync();
            await WriteEventAsync(new AiChatStreamEvent
            {
                Event = "error",
                RequestId = input.RequestId ?? string.Empty,
                Message = diagnostic,
                ToolStage = failure.ToolStage,
                NextAction = failure.NextAction,
                Attempt = failure.Attempt,
                ExceptionType = failure.ExceptionType,
                ExceptionSource = failure.ExceptionSource,
                FailureKind = failure.FailureKind,
                CorrelationId = failure.CorrelationId,
                StackSnippet = failure.StackSnippet
            }, CancellationToken.None);
        }
        finally
        {
            await StopHeartbeatAsync();
            writeGate.Dispose();
        }
    }

    [HttpPost("local/chat/begin")]
    public Task<IActionResult> BeginLocalChat([FromBody] SendAiMessageRequest input)
        => ExecuteAsync(async () => await _conversationService.BeginLocalAsync(input));

    [HttpPost("local/chat/invoke-tool")]
    public Task<IActionResult> InvokeLocalTool([FromBody] InvokeLocalAiToolRequest input)
        => ExecuteAsync(async () => await _conversationService.InvokeLocalToolAsync(input));

    [HttpPost("local/chat/complete")]
    public Task<IActionResult> CompleteLocalChat([FromBody] CompleteLocalAiChatRequest input)
        => ExecuteAsync(async () => await _conversationService.CompleteLocalAsync(input));

    [HttpPost("tools/{id:long}/approve")]
    public Task<IActionResult> ApproveTool(long id)
        => ExecuteAsync(async () => await _conversationService.ApproveAsync(id));

    [HttpPost("local/chat/fail")]
    public Task<IActionResult> FailLocalChat([FromBody] FailLocalAiChatRequest input)
        => ExecuteAsync(async () => await _conversationService.FailLocalAsync(input));

    [HttpPost("tools/{id:long}/execute")]
    public Task<IActionResult> ExecuteApprovedTool(long id)
        => ExecuteAsync(async () => await _conversationService.ExecuteApprovedAsync(id));

    [HttpPost("tools/{id:long}/reject")]
    public Task<IActionResult> RejectTool(long id)
        => ExecuteAsync(async () => await _conversationService.RejectAsync(id));

    [HttpPost("tools/{id:long}/complete-client-actions")]
    public Task<IActionResult> CompleteClientActions(long id, [FromBody] CompleteAiClientActionsRequest input)
        => ExecuteAsync(async () => await _conversationService.CompleteClientActionsAsync(id, input));

    private async Task<IActionResult> ExecuteAsync<T>(Func<Task<T>> action)
    {
        try
        {
            var result = await action();
            return Ok(ApiResponse<T>.Success(result));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(ApiResponse<object>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    private static async Task RunHeartbeatAsync(
        string requestId,
        Func<AiChatStreamEvent, CancellationToken, ValueTask> writeAsync,
        CancellationToken cancellationToken)
    {
        // Emit an SSE comment line before each heartbeat. SSE comments start with
        // ":" and are ignored by event consumers, but they keep the socket alive
        // for upstream proxies/clients (notably the Codex desktop SSE reader)
        // that time out on idle "data:" frames while the upstream LLM thinks.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        var tick = 0;
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                var pingToken = cancellationToken;
                await writeAsync(new AiChatStreamEvent
                {
                    Event = "heartbeat",
                    RequestId = requestId,
                    Sequence = ++tick,
                    Timestamp = DateTime.UtcNow
                }, pingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
