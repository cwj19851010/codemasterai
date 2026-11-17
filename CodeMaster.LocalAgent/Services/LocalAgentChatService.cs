using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Serialization;
using CodeMaster.Agent.Services;
using CodeMaster.Agent.Tools;
using CodeMaster.LocalAgent.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace CodeMaster.LocalAgent.Services;

public sealed class LocalAgentChatService
{
    private const string LocalCompatibilityInstructions = """
        Local client capability: CreateEntityProposal supports CopyFieldsFromEntityName or CopyFieldsFromEntityId. When the user asks for another entity with the same fields, every supported control, or a repeated full-control test entity, use one copy source and keep Fields empty except for new or overridden foreign-key fields. The local client expands the copied business fields and control bindings before sending the standard proposal to the server. Do not enumerate the copied fields, and do not call get_codegen_catalog or get_entity_blueprint merely to reproduce existing controls. Relations are not copied: create the requested OneToMany or Owned OneToOne relation explicitly. An Owned OneToOne entity is appropriate for an optional extension/detail record that shares the owner's lifecycle and should contain at most one row per owner.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly CodeMasterServerClient _serverClient;
    private readonly LocalAiProviderStore _providerStore;
    private readonly LocalAgentDiagnosticLog _diagnostics;
    private readonly LocalAgentConversationSessionStore _sessions;

    public LocalAgentChatService(
        CodeMasterServerClient serverClient,
        LocalAiProviderStore providerStore,
        LocalAgentDiagnosticLog diagnostics,
        LocalAgentConversationSessionStore sessions)
    {
        _serverClient = serverClient;
        _providerStore = providerStore;
        _diagnostics = diagnostics;
        _sessions = sessions;
    }

    public async Task<LocalExecutionResult> SaveProviderAsync(LocalExecutionRequest request)
    {
        var input = DeserializePayload<SaveLocalAiProviderRequest>(request.Payload);
        await _providerStore.SaveAsync(request.ServerBaseUrl, input);
        _sessions.InvalidateProvider(request.ServerBaseUrl, input.ProviderId);
        return LocalExecutionResult.Ok("Local AI provider saved.");
    }

    public async Task<LocalExecutionResult> DeleteProviderAsync(LocalExecutionRequest request)
    {
        var providerId = GetRequiredLong(request.Payload, "providerId", "id");
        await _providerStore.DeleteAsync(request.ServerBaseUrl, providerId);
        _sessions.InvalidateProvider(request.ServerBaseUrl, providerId);
        return LocalExecutionResult.Ok("Local AI provider deleted.");
    }

    public async Task<LocalExecutionResult> TestProviderAsync(LocalExecutionRequest request)
    {
        var providerId = GetRequiredLong(request.Payload, "providerId", "id");
        var settings = await _providerStore.GetAsync(request.ServerBaseUrl, providerId);
        using var client = AiChatClientFactory.Create(settings);
        var response = await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "Reply with OK.") },
            new ChatOptions { MaxOutputTokens = 16 });
        var text = response.Text?.Trim();
        return LocalExecutionResult.OkData(new AiProviderTestResult
        {
            Success = true,
            SupportsTools = true,
            SupportsStreaming = true,
            Message = "Local model connection succeeded.",
            ResponseText = text
        });
    }

    public Task<LocalExecutionResult> InvalidateConversationAsync(LocalExecutionRequest request)
    {
        var conversationId = GetRequiredLong(request.Payload, "conversationId");
        var removed = _sessions.Invalidate(request.ServerBaseUrl, conversationId);
        return Task.FromResult(LocalExecutionResult.Ok(
            removed ? "Local Agent conversation context invalidated." : "Local Agent conversation context was already empty.",
            new { conversationId, invalidated = removed }));
    }

    public async Task<LocalExecutionResult> ChatAsync(
        LocalExecutionRequest request,
        Func<LocalExecutionProgress, Task>? reportProgress = null)
    {
        if (string.IsNullOrWhiteSpace(request.AccessToken))
            throw new InvalidOperationException("The CodeMaster login token is required for local Agent execution.");

        var input = DeserializePayload<SendAiMessageRequest>(request.Payload);
        var sessionState = _sessions.GetOrCreate(request.ServerBaseUrl, input.ConversationId);
        var acquired = await sessionState.Gate.WaitAsync(TimeSpan.FromMinutes(5));
        if (!acquired)
            throw new TimeoutException("The local Agent conversation is still processing another request. Retry after it finishes.");
        var totalStopwatch = Stopwatch.StartNew();
        long? conversationId = input.ConversationId > 0 ? input.ConversationId : null;
        var baseFields = new Dictionary<string, object?>
        {
            ["conversationId"] = conversationId,
            ["requestId"] = input.RequestId
        };
        await _diagnostics.WriteAsync("information", "agent.chat.started", baseFields);
        await ReportProgressAsync(reportProgress, new LocalExecutionProgress
        {
            Event = "status",
            Phase = "loading_context"
        });

        try
        {
            var beginStopwatch = Stopwatch.StartNew();
            var begin = await _serverClient.BeginLocalAiChatAsync(
                request.ServerBaseUrl,
                input,
                request.AccessToken);
            beginStopwatch.Stop();
            conversationId = begin.ConversationId;
            baseFields["conversationId"] = conversationId;
            await _diagnostics.WriteAsync(
                "information",
                "agent.chat.context.loaded",
                new Dictionary<string, object?>(baseFields)
                {
                    ["durationMs"] = beginStopwatch.ElapsedMilliseconds,
                    ["providerId"] = begin.ProviderId,
                    ["messageCount"] = begin.Messages.Count,
                    ["availableToolCount"] = begin.AvailableTools.Count,
                    ["alreadyCompleted"] = begin.AlreadyCompleted
                });
            if (begin.AlreadyCompleted && begin.CompletedResult != null)
            {
                totalStopwatch.Stop();
                await _diagnostics.WriteAsync(
                    "information",
                    "agent.chat.completed.cached",
                    new Dictionary<string, object?>(baseFields)
                    {
                        ["durationMs"] = totalStopwatch.ElapsedMilliseconds
                    });
                await ReportProgressAsync(reportProgress, new LocalExecutionProgress
                {
                    Event = "status",
                    Phase = "completed"
                });
                return LocalExecutionResult.OkData(begin.CompletedResult);
            }

            var settings = await _providerStore.GetAsync(request.ServerBaseUrl, begin.ProviderId);
            using var chatClient = new AiResponseTraceChatClient(AiChatClientFactory.Create(settings));
            var availableTools = new HashSet<string>(begin.AvailableTools, StringComparer.Ordinal);
            var currentUserContent = GetCurrentUserContent(begin);
            // The server owns the mode; the sidecar must not re-derive it from keywords.
            var isCodeMode = AiAgentModes.IsCode(begin.AgentMode);
            var isWorkPlanRepair = isCodeMode && AiTurnToolSelector.IsWorkPlanRepairRequest(currentUserContent);
            var requirementsOnly = !isCodeMode;
            var sessionReused = !isWorkPlanRepair && sessionState.CanReuse(begin.ProviderId, begin.Messages.Count);
            if (!sessionReused)
                sessionState.Reset();
            await _diagnostics.WriteAsync(
                "information",
                sessionReused ? "agent.session.reused" : "agent.session.created",
                new Dictionary<string, object?>(baseFields)
                {
                    ["providerId"] = begin.ProviderId,
                    ["serverMessageCount"] = begin.Messages.Count,
                    ["projectStructureVersion"] = sessionState.ProjectStructureVersion
                });

            Task<string> InvokeAsync(string toolName)
            {
                var element = JsonSerializer.SerializeToElement(new { }, AgentJsonSerializer.Options);
                return InvokeRemoteToolAsync(request, begin, toolName, element, sessionState, reportProgress);
            }

            Task<string> InvokeWithArgumentAsync(string toolName, string parameterName, object value)
            {
                var element = JsonSerializer.SerializeToElement(
                    new Dictionary<string, object?> { [parameterName] = value },
                    AgentJsonSerializer.Options);
                return InvokeRemoteToolAsync(request, begin, toolName, element, sessionState, reportProgress);
            }

            string? completedProposalToolName = null;

            async Task<string> InvokeProposalAsync(string toolName, string parameterName, object value)
            {
                var output = await InvokeWithArgumentAsync(toolName, parameterName, value);
                completedProposalToolName = toolName;
                return output;
            }

            async Task<string> ProposeProjectChangeSetAsync(ProjectChangeSetProposal proposal)
            {
                await ProjectChangeSetFieldCopyExpander.ExpandAsync(
                    proposal,
                    async query =>
                    {
                        if (!availableTools.Contains(CodeMasterAgentToolCatalog.GetEntityBlueprint))
                        {
                            throw new InvalidOperationException(
                                "The connected CodeMaster server does not expose get_entity_blueprint, " +
                                "so copied fields cannot be expanded by this client.");
                        }

                        var output = await InvokeWithArgumentAsync(
                            CodeMasterAgentToolCatalog.GetEntityBlueprint,
                            "query",
                            query);
                        return JsonSerializer.Deserialize<AiEntityBlueprintDto>(output, JsonOptions)
                               ?? throw new InvalidOperationException(
                                   "The copied-field source returned an invalid entity blueprint.");
                    });
                return await InvokeProposalAsync(
                    CodeMasterAgentToolCatalog.ProposeProjectChangeSet,
                    "proposal",
                    proposal);
            }

            var tools = CodeMasterAgentToolCatalog.Create(
                availableTools.Contains(CodeMasterAgentToolCatalog.GetProjectSummary),
                () => InvokeAsync(CodeMasterAgentToolCatalog.GetProjectSummary),
                availableTools.Contains(CodeMasterAgentToolCatalog.GetProjectStructure),
                () => InvokeAsync(CodeMasterAgentToolCatalog.GetProjectStructure),
                availableTools.Contains(CodeMasterAgentToolCatalog.GetCodegenCatalog),
                () => InvokeAsync(CodeMasterAgentToolCatalog.GetCodegenCatalog),
                availableTools.Contains(CodeMasterAgentToolCatalog.GetEntityBlueprint),
                query => InvokeWithArgumentAsync(CodeMasterAgentToolCatalog.GetEntityBlueprint, "query", query),
                availableTools.Contains(CodeMasterAgentToolCatalog.GetUiPageBlueprint),
                query => InvokeWithArgumentAsync(CodeMasterAgentToolCatalog.GetUiPageBlueprint, "query", query),
                availableTools.Contains(CodeMasterAgentToolCatalog.GetProjectDictionaries),
                query => InvokeWithArgumentAsync(CodeMasterAgentToolCatalog.GetProjectDictionaries, "query", query ?? new ProjectDictionaryQuery()),
                availableTools.Contains(CodeMasterAgentToolCatalog.ProposeProjectChangeSet),
                ProposeProjectChangeSetAsync,
                proposal => InvokeProposalAsync(CodeMasterAgentToolCatalog.ProposeUiPageEnhancement, "proposal", proposal),
                availableTools.Contains(CodeMasterAgentToolCatalog.ProposeDictionaryChange),
                proposal => InvokeProposalAsync(CodeMasterAgentToolCatalog.ProposeDictionaryChange, "proposal", proposal),
                includeWorkPlan: availableTools.Contains(CodeMasterAgentToolCatalog.GetProjectWorkPlan) &&
                                 availableTools.Contains(CodeMasterAgentToolCatalog.UpsertProjectWorkPlan),
                getProjectWorkPlan: () => InvokeAsync(CodeMasterAgentToolCatalog.GetProjectWorkPlan),
                getProjectCompletionStatus: availableTools.Contains(CodeMasterAgentToolCatalog.GetProjectCompletionStatus)
                    ? () => InvokeAsync(CodeMasterAgentToolCatalog.GetProjectCompletionStatus)
                    : null,
                getProjectWorkTask: availableTools.Contains(CodeMasterAgentToolCatalog.GetProjectWorkTask)
                    ? query => InvokeWithArgumentAsync(
                        CodeMasterAgentToolCatalog.GetProjectWorkTask,
                        "query",
                        query ?? new AiProjectWorkTaskQuery())
                    : null,
                upsertProjectWorkPlan: input => InvokeWithArgumentAsync(
                    CodeMasterAgentToolCatalog.UpsertProjectWorkPlan,
                    "input",
                    input),
                includeWorkBatch: availableTools.Contains(CodeMasterAgentToolCatalog.ProposeProjectWorkBatch),
                proposeProjectWorkBatch: input => InvokeProposalAsync(
                    CodeMasterAgentToolCatalog.ProposeProjectWorkBatch,
                    "input",
                    input),
                includeSubmitBusinessPlan: availableTools.Contains(CodeMasterAgentToolCatalog.SubmitBusinessPlan),
                submitBusinessPlan: availableTools.Contains(CodeMasterAgentToolCatalog.SubmitBusinessPlan)
                    ? input => InvokeWithArgumentAsync(
                        CodeMasterAgentToolCatalog.SubmitBusinessPlan,
                        "input",
                        input)
                    : null);
            tools = AiTurnToolSelector.SelectForMode(tools, begin.AgentMode);
            if (begin.RequiresWorkPlan)
                AiProjectPlanTurn.EnsureCapability(tools.Select(tool => tool.Name));
            var runOptions = begin.RequiresWorkPlan
                ? AiChatClientFactory.CreateRunOptions(settings.CompatibilityMode, CodeMasterAgentToolCatalog.GetProjectWorkPlan)
                : AiChatClientFactory.CreateRunOptions(settings.CompatibilityMode);

            var runtimeInstructions = (requirementsOnly
                ? begin.Instructions
                : isWorkPlanRepair
                ? $"You are the built-in CodeMaster task-plan repair agent for the fixed conversation. Call get_project_completion_status, then get_project_work_task for the exact blocked task. Read only necessary entity blueprints. Update the existing plan with PreserveUnspecified=true and only that task. Preserve completed and unspecified tasks, never recreate completed entities, and do not ask for confirmation. Reply briefly with the actual result."
                : begin.Instructions + "\n\n" + LocalCompatibilityInstructions) + (sessionReused
                ? "\n\nRuntime session state: the prior Agent session and its tool results were restored. Reuse them for follow-up reasoning. Do not repeat get_project_summary or get_project_structure for an ordinary follow-up when the prior tool result is sufficient. Call get_project_summary only when current metadata must be checked; supported servers will perform a structure-version comparison."
                : "\n\nRuntime session state: this is a fresh Agent session. For a project-dependent request, load get_project_summary before making metadata claims or proposals.");
            var agent = AiChatClientFactory.CreateAgent(
                chatClient,
                runtimeInstructions,
                "CodeMasterLocalAgent",
                "CodeMaster project modeling and code generation assistant running on the user's computer.",
                tools,
                settings.CompatibilityMode);
            var messages = isWorkPlanRepair
                ? [GetCurrentUserMessage(begin)]
                : AiConversationHistory.BuildBounded(begin.Messages);
            var session = sessionReused
                ? sessionState.Session!
                : await agent.CreateSessionAsync();
            var modelStopwatch = Stopwatch.StartNew();
            await _diagnostics.WriteAsync(
                "information",
                "agent.model.started",
                new Dictionary<string, object?>(baseFields)
                {
                    ["providerId"] = begin.ProviderId,
                    ["providerType"] = settings.ProviderType,
                    ["modelName"] = settings.ModelName,
                    ["networkTimeoutSeconds"] = settings.ProviderType == AiProviderTypes.OpenAiCompatible
                        ? AiChatClientFactory.OpenAiCompatibleNetworkTimeout.TotalSeconds
                        : null,
                    ["maxRetries"] = settings.ProviderType == AiProviderTypes.OpenAiCompatible
                        ? AiChatClientFactory.OpenAiCompatibleMaxRetries
                        : null,
                    ["messageCount"] = sessionReused ? 1 : messages.Count,
                    ["sessionReused"] = sessionReused,
                    ["projectStructureVersion"] = sessionState.ProjectStructureVersion,
                    ["toolCount"] = tools.Count,
                    ["availableTools"] = tools.Select(tool => tool.Name).ToArray(),
                    ["requiresWorkPlan"] = begin.RequiresWorkPlan
                });
            await ReportProgressAsync(reportProgress, new LocalExecutionProgress
            {
                Event = "status",
                Phase = "generating"
            });
            var recoveredAfterProposal = false;
            var streamingFallback = false;
            var streamedCharacters = 0;
            string responseText;
            try
            {
                var responseBuilder = new StringBuilder();
                var streamingResponse = sessionReused
                    ? agent.RunStreamingAsync(GetCurrentUserMessage(begin), session, options: runOptions)
                    : agent.RunStreamingAsync(messages, session, options: runOptions);
                await foreach (var update in streamingResponse)
                {
                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        responseBuilder.Append(update.Text);
                        streamedCharacters = responseBuilder.Length;
                        await ReportProgressAsync(reportProgress, new LocalExecutionProgress
                        {
                            Event = "text_delta",
                            Phase = "generating",
                            Text = update.Text
                        });
                    }
                }
                modelStopwatch.Stop();
                responseText = responseBuilder.ToString();
                await _diagnostics.WriteAsync(
                    "information",
                    "agent.model.completed",
                    new Dictionary<string, object?>(baseFields)
                    {
                        ["durationMs"] = modelStopwatch.ElapsedMilliseconds,
                        ["responseLength"] = responseText.Length,
                        ["streamingFallback"] = streamingFallback
                    });
            }
            catch (Exception ex) when (!string.IsNullOrWhiteSpace(completedProposalToolName))
            {
                modelStopwatch.Stop();
                recoveredAfterProposal = true;
                responseText = BuildRecoveredProposalResponse(completedProposalToolName);
                await ReportProgressAsync(reportProgress, new LocalExecutionProgress
                {
                    Event = "text_replace",
                    Phase = "generating",
                    Text = responseText
                });
                await _diagnostics.WriteAsync(
                    "warning",
                    "agent.model.recovered_after_proposal",
                    new Dictionary<string, object?>(baseFields)
                    {
                        ["durationMs"] = modelStopwatch.ElapsedMilliseconds,
                        ["proposalToolName"] = completedProposalToolName
                    },
                    ex);
            }
            catch (Exception ex) when (streamedCharacters == 0 &&
                                       begin.Messages.Any(item => item.Images.Count > 0) &&
                                       AiRequestFailure.LooksLikeVisionRejection(ex))
            {
                // The local model refused the image. Answer with an explanation instead of an
                // error: retry the same turn without the images so the text part still works,
                // and say plainly why the pictures were not used.
                modelStopwatch.Stop();
                await _diagnostics.WriteAsync(
                    "warning",
                    "agent.model.vision_unsupported",
                    new Dictionary<string, object?>(baseFields)
                    {
                        ["durationMs"] = modelStopwatch.ElapsedMilliseconds,
                        ["fallback"] = "text_only"
                    },
                    ex);

                await ReportProgressAsync(reportProgress, new LocalExecutionProgress
                {
                    Event = "status",
                    Phase = "generating"
                });

                var textOnly = begin.Messages
                    .Select(item => new AiMessageDto
                    {
                        Id = item.Id,
                        RequestId = item.RequestId,
                        Role = item.Role,
                        Content = item.Content,
                        MetadataJson = item.MetadataJson,
                        Sequence = item.Sequence,
                        CreateTime = item.CreateTime,
                        Attachments = item.Attachments
                    })
                    .ToList();

                session = await agent.CreateSessionAsync();
                var retryStopwatch = Stopwatch.StartNew();
                var retryResponse = await agent.RunAsync(
                    AiConversationHistory.BuildBounded(textOnly),
                    session,
                    options: runOptions);
                retryStopwatch.Stop();
                var retryText = retryResponse.ToString();
                responseText = $"{AiVisionCapabilityProbe.UnsupportedMessage}{AiVisionCapabilityProbe.UnsupportedSuggestion}" +
                               (string.IsNullOrWhiteSpace(retryText)
                                   ? string.Empty
                                   : $"{Environment.NewLine}{Environment.NewLine}{retryText}");

                await _diagnostics.WriteAsync(
                    "information",
                    "agent.model.completed",
                    new Dictionary<string, object?>(baseFields)
                    {
                        ["durationMs"] = modelStopwatch.ElapsedMilliseconds + retryStopwatch.ElapsedMilliseconds,
                        ["fallbackDurationMs"] = retryStopwatch.ElapsedMilliseconds,
                        ["responseLength"] = responseText.Length,
                        ["visionFallback"] = true
                    });
            }
            catch (Exception ex) when (IsStreamingTransportFailure(ex))
            {
                modelStopwatch.Stop();
                streamingFallback = true;
                await _diagnostics.WriteAsync(
                    "warning",
                    "agent.model.streaming_failed",
                    new Dictionary<string, object?>(baseFields)
                    {
                        ["durationMs"] = modelStopwatch.ElapsedMilliseconds,
                        ["fallback"] = "non_streaming"
                    },
                    ex);
                await ReportProgressAsync(reportProgress, new LocalExecutionProgress
                {
                    Event = "status",
                    Phase = "stream_fallback"
                });

                var fallbackStopwatch = Stopwatch.StartNew();
                session = await agent.CreateSessionAsync();
                var fallbackResponse = await agent.RunAsync(messages, session, options: runOptions);
                fallbackStopwatch.Stop();
                responseText = fallbackResponse.ToString();
                if (string.IsNullOrWhiteSpace(responseText))
                    responseText = "The model returned an empty response.";
                await _diagnostics.WriteAsync(
                    "information",
                    "agent.model.completed",
                    new Dictionary<string, object?>(baseFields)
                    {
                        ["durationMs"] = modelStopwatch.ElapsedMilliseconds + fallbackStopwatch.ElapsedMilliseconds,
                        ["fallbackDurationMs"] = fallbackStopwatch.ElapsedMilliseconds,
                        ["responseLength"] = responseText.Length,
                        ["streamingFallback"] = true
                    });
            }

            AiProjectPlanTurnResult? planResult = null;
            if (!begin.RequiresWorkPlan && !recoveredAfterProposal)
            {
                var answer = await AiResponseCompletion.EnsureAsync(
                    currentUserContent, requirementsOnly, () => chatClient.LastReply, responseText,
                    async (prompt, token) =>
                    {
                        await agent.RunAsync(prompt, session,
                            options: AiChatClientFactory.CreateVisibleResponseRunOptions(settings.CompatibilityMode),
                            cancellationToken: token);
                    },
                    async _ => await ReportProgressAsync(reportProgress, new LocalExecutionProgress
                    {
                        Event = "status", Phase = "completing_answer", Text = "正在完成需求正文。"
                    }));
                responseText = answer.Text;
                await _diagnostics.WriteAsync("information", "agent.answer.completed", new Dictionary<string, object?>(baseFields)
                {
                    ["answerComplete"] = answer.Complete,
                    ["answerContinuationAttempts"] = answer.Attempts,
                    ["modelCalls"] = chatClient.BuildDiagnostics()
                });
                await ReportProgressAsync(reportProgress, new LocalExecutionProgress
                {
                    Event = "text_replace", Phase = "generating", Text = responseText
                });
            }
            if (begin.RequiresWorkPlan)
            {
                planResult = await AiProjectPlanTurn.EnsureSavedAsync(
                    async _ => JsonSerializer.Deserialize<AiProjectWorkPlanDto?>(
                        await InvokeAsync(CodeMasterAgentToolCatalog.GetProjectWorkPlan), JsonOptions),
                    async (prompt, token) =>
                    {
                        await agent.RunAsync(prompt, session,
                            options: AiChatClientFactory.CreateRunOptions(settings.CompatibilityMode, CodeMasterAgentToolCatalog.SubmitBusinessPlan),
                            cancellationToken: token);
                    },
                    async (_, _) => await ReportProgressAsync(reportProgress, new LocalExecutionProgress
                    {
                        Event = "status",
                        Phase = "generating",
                        Text = "正在保存已确认需求的任务计划。"
                    }));
                responseText = AiProjectPlanTurn.BuildOutcomeMessage(planResult.Plan);
                await ReportProgressAsync(reportProgress, new LocalExecutionProgress
                {
                    Event = "text_replace",
                    Phase = "generating",
                    Text = responseText
                });
            }

            await ReportProgressAsync(reportProgress, new LocalExecutionProgress
            {
                Event = "status",
                Phase = "saving"
            });
            var completeStopwatch = Stopwatch.StartNew();
            var result = await _serverClient.CompleteLocalAiChatAsync(
                request.ServerBaseUrl,
                new CompleteLocalAiChatRequest
                {
                    ConversationId = begin.ConversationId,
                    RequestId = begin.RequestId,
                    ResponseText = responseText,
                    PlanContinuationAttempts = planResult?.ContinuationAttempts ?? 0
                },
                request.AccessToken);
            completeStopwatch.Stop();
            if (recoveredAfterProposal)
                _sessions.Invalidate(request.ServerBaseUrl, begin.ConversationId);
            else
                sessionState.Commit(session, begin.ProviderId, begin.Messages.Count + 1);
            totalStopwatch.Stop();
            await _diagnostics.WriteAsync(
                "information",
                "agent.chat.completed",
                new Dictionary<string, object?>(baseFields)
                {
                    ["durationMs"] = totalStopwatch.ElapsedMilliseconds,
                    ["serverCompletionDurationMs"] = completeStopwatch.ElapsedMilliseconds,
                    ["sessionReused"] = sessionReused,
                    ["recoveredAfterProposal"] = recoveredAfterProposal,
                    ["projectStructureVersion"] = sessionState.ProjectStructureVersion,
                    ["pendingApprovalCount"] = result.PendingApprovals.Count
                });
            await ReportProgressAsync(reportProgress, new LocalExecutionProgress
            {
                Event = "status",
                Phase = "completed"
            });
            return LocalExecutionResult.OkData(result);
        }
        catch (Exception ex)
        {
            _sessions.Invalidate(request.ServerBaseUrl, input.ConversationId);
            try
            {
                await _serverClient.FailLocalAiChatAsync(request.ServerBaseUrl, new FailLocalAiChatRequest
                {
                    ConversationId = input.ConversationId,
                    RequestId = input.RequestId ?? string.Empty,
                    ErrorMessage = AiRequestFailure.Sanitize(ex.GetBaseException().Message)
                }, request.AccessToken);
            }
            catch (Exception persistenceError)
            {
                await _diagnostics.WriteAsync("warning", "agent.failure.persistence_failed", baseFields, persistenceError);
            }
            totalStopwatch.Stop();
            await _diagnostics.WriteAsync(
                "error",
                "agent.chat.failed",
                new Dictionary<string, object?>(baseFields)
                {
                    ["durationMs"] = totalStopwatch.ElapsedMilliseconds
                },
                ex);
            if (IsModelConnectionTimeout(ex))
            {
                throw new TimeoutException(
                    "Agent model request timed out or the model connection was interrupted. " +
                    "Check the saved conversation and task status before retrying.",
                    ex);
            }
            throw;
        }
        finally
        {
            sessionState.Gate.Release();
        }
    }

    private async Task<string> InvokeRemoteToolAsync(
        LocalExecutionRequest request,
        BeginLocalAiChatResult begin,
        string toolName,
        JsonElement arguments,
        LocalAgentConversationSessionState sessionState,
        Func<LocalExecutionProgress, Task>? reportProgress)
    {
        var isProjectSummary = string.Equals(
            toolName,
            CodeMasterAgentToolCatalog.GetProjectSummary,
            StringComparison.Ordinal);
        var stopwatch = Stopwatch.StartNew();
        var fields = new Dictionary<string, object?>
        {
            ["conversationId"] = begin.ConversationId,
            ["requestId"] = begin.RequestId,
            ["toolName"] = toolName,
            ["knownProjectStructureVersion"] = isProjectSummary
                ? sessionState.ProjectStructureVersion
                : null
        };
        await _diagnostics.WriteAsync("information", "agent.tool.started", fields);
        await ReportProgressAsync(reportProgress, new LocalExecutionProgress
        {
            Event = "tool_started",
            Phase = "tool",
            ToolName = toolName
        });
        try
        {
            var result = await _serverClient.InvokeLocalAiToolAsync(
                request.ServerBaseUrl,
                new InvokeLocalAiToolRequest
                {
                    ConversationId = begin.ConversationId,
                    RequestId = begin.RequestId,
                    ToolName = toolName,
                    KnownProjectStructureVersion = isProjectSummary
                        ? sessionState.ProjectStructureVersion
                        : null,
                    Arguments = arguments
                },
                request.AccessToken);
            stopwatch.Stop();
            if (isProjectSummary && !string.IsNullOrWhiteSpace(result.ProjectStructureVersion))
                sessionState.ProjectStructureVersion = result.ProjectStructureVersion;
            await _diagnostics.WriteAsync(
                "information",
                "agent.tool.completed",
                new Dictionary<string, object?>(fields)
                {
                    ["durationMs"] = stopwatch.ElapsedMilliseconds,
                    ["outputLength"] = result.Output?.Length ?? 0,
                    ["projectStructureVersion"] = result.ProjectStructureVersion,
                    ["projectStructureNotModified"] = result.ProjectStructureNotModified
                });
            await ReportProgressAsync(reportProgress, new LocalExecutionProgress
            {
                Event = "tool_completed",
                Phase = "tool",
                ToolName = toolName
            });
            return result.Output ?? string.Empty;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            await _diagnostics.WriteAsync(
                "error",
                "agent.tool.failed",
                new Dictionary<string, object?>(fields)
                {
                    ["durationMs"] = stopwatch.ElapsedMilliseconds
                },
                ex);
            await ReportProgressAsync(reportProgress, new LocalExecutionProgress
            {
                Event = "tool_failed",
                Phase = "tool",
                ToolName = toolName
            });
            throw;
        }
    }

    private static async Task ReportProgressAsync(
        Func<LocalExecutionProgress, Task>? reportProgress,
        LocalExecutionProgress progress)
    {
        if (reportProgress == null)
            return;

        try
        {
            await reportProgress(progress);
        }
        catch
        {
            // A disconnected UI must not interrupt server-side message completion.
        }
    }

    private static AiMessageDto GetCurrentUserDto(BeginLocalAiChatResult begin)
    {
        return begin.Messages.LastOrDefault(item =>
                   string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(item.RequestId, begin.RequestId, StringComparison.Ordinal))
               ?? begin.Messages.LastOrDefault(item =>
                   string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException("The current user message is missing from the local Agent context.");
    }

    private static string GetCurrentUserContent(BeginLocalAiChatResult begin)
        => GetCurrentUserDto(begin).Content;

    /// <summary>
    /// The current turn as a model message. Uses the shared converter so images attached to the
    /// message become multimodal content exactly like they do on the server path.
    /// </summary>
    private static ChatMessage GetCurrentUserMessage(BeginLocalAiChatResult begin)
        => AiConversationHistory.ToChatMessage(GetCurrentUserDto(begin));

    private static ChatMessage ToChatMessage(AiMessageDto message)
    {
        var role = message.Role switch
        {
            "assistant" => ChatRole.Assistant,
            "system" => ChatRole.System,
            _ => ChatRole.User
        };
        return new ChatMessage(role, message.Content);
    }

    private static T DeserializePayload<T>(JsonElement payload)
    {
        return payload.Deserialize<T>(JsonOptions)
            ?? throw new ArgumentException("Local Agent request payload is invalid.");
    }

    private static long GetRequiredLong(JsonElement payload, params string[] names)
    {
        foreach (var name in names)
        {
            if (!payload.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number)) return number;
        }
        throw new ArgumentException($"Missing required value: {string.Join("/", names)}");
    }

    internal static bool IsModelConnectionTimeout(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is TimeoutException or OperationCanceledException or SocketException or
                HttpRequestException or HttpIOException)
                return true;
        }
        return false;
    }

    internal static bool IsStreamingTransportFailure(Exception exception)
    {
        if (IsModelConnectionTimeout(exception))
            return true;

        for (var current = exception; current != null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains("Status: 504", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("ResponseEnded", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    internal static string BuildRecoveredProposalResponse(string proposalToolName)
    {
        var proposalName = proposalToolName switch
        {
            CodeMasterAgentToolCatalog.ProposeUiPageEnhancement => "页面调整方案",
            CodeMasterAgentToolCatalog.ProposeDictionaryChange => "数据字典方案",
            _ => "项目变更方案"
        };
        return $"{proposalName}已准备，请在下方确认后应用到 CodeMaster。";
    }
}
