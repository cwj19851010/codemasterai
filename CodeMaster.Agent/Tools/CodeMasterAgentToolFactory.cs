using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.PlatformConstraints;
using CodeMaster.Agent.Serialization;
using CodeMaster.Agent.Services;
using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Application.Services.CodeGen;
using CodeMaster.Core.Repositories;
using CodeMaster.Domain.Entities.Ai;
using Microsoft.Extensions.AI;
using SqlSugar;

namespace CodeMaster.Agent.Tools;

public interface ICodeMasterAgentToolFactory
{
    Task<IList<AITool>> CreateToolsAsync(long conversationId, long projectId, string requestId, Func<AiChatStreamEvent, CancellationToken, ValueTask>? publishAsync = null);
    Task<AgentToolInvocationResult> InvokeAsync(
        long conversationId,
        long projectId,
        string requestId,
        string toolName,
        JsonElement arguments,
        string? knownProjectStructureVersion = null);
    Task<AiToolExecutionDto> AcceptAsync(long executionId);
    Task<AiToolExecutionDto> ApproveAsync(long executionId);
    Task<AiToolExecutionDto> ExecuteApprovedAsync(long executionId);
    Task<AiToolExecutionDto> RejectAsync(long executionId);
    Task<AiToolExecutionDto> CompleteClientActionsAsync(long executionId, CompleteAiClientActionsRequest request);
    Task<AiToolExecutionDto?> ExecuteNextWorkPlanBatchAsync(
        long conversationId,
        long projectId,
        long planId,
        string requestId);
}

public sealed class AgentToolInvocationResult
{
    public string Output { get; init; } = string.Empty;
    public string? ProjectStructureVersion { get; init; }
    public bool ProjectStructureNotModified { get; init; }
}

internal sealed class CodeMasterAgentToolFactory : ICodeMasterAgentToolFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new(AgentJsonSerializer.Options)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IProjectService _projectService;
    private readonly IProjectModuleService _moduleService;
    private readonly IModuleEntityService _entityService;
    private readonly ICodeMasterProjectBlueprintService _blueprintService;
    private readonly ICodeMasterProjectChangeSetService _changeSetService;
    private readonly IProjectChangeSetDefinitionNormalizer _definitionNormalizer;
    private readonly IAiProjectWorkPlanService _workPlanService;
    private readonly IRepository<AiToolExecution> _executionRepository;
    private readonly IRepository<AiConversation> _conversationRepository;
    private readonly ISqlSugarClient _db;
    private readonly IAiCurrentUser _currentUser;
    private readonly IAiAuthorizationService _authorizationService;
    private readonly IAiProjectSourceRepairExecutor? _sourceRepairExecutor;
    private readonly IReadOnlyCollection<ICodeMasterAgentToolExtension> _toolExtensions;
    private readonly IReadOnlyDictionary<string, ICodeMasterAgentBatchOperationAdapter> _batchAdapters;
    private readonly IPlatformConstraintsProvider? _platformConstraints;
    private Func<AiChatStreamEvent, CancellationToken, ValueTask>? _publishAsync;
    private AiConversationReadCacheManager? _cacheManager;

    public CodeMasterAgentToolFactory(
        IProjectService projectService,
        IProjectModuleService moduleService,
        IModuleEntityService entityService,
        ICodeMasterProjectBlueprintService blueprintService,
        ICodeMasterProjectChangeSetService changeSetService,
        IProjectChangeSetDefinitionNormalizer definitionNormalizer,
        IAiProjectWorkPlanService workPlanService,
        IRepository<AiToolExecution> executionRepository,
        IRepository<AiConversation> conversationRepository,
        ISqlSugarClient db,
        IAiCurrentUser currentUser,
        IAiAuthorizationService authorizationService,
        IEnumerable<ICodeMasterAgentToolExtension>? toolExtensions = null,
        IEnumerable<ICodeMasterAgentBatchOperationAdapter>? batchAdapters = null,
        IAiProjectSourceRepairExecutor? sourceRepairExecutor = null,
        IPlatformConstraintsProvider? platformConstraints = null,
        AiConversationReadCacheManager? cacheManager = null)
    {
        _cacheManager = cacheManager;
        _projectService = projectService;
        _moduleService = moduleService;
        _entityService = entityService;
        _blueprintService = blueprintService;
        _changeSetService = changeSetService;
        _definitionNormalizer = definitionNormalizer;
        _workPlanService = workPlanService;
        _executionRepository = executionRepository;
        _conversationRepository = conversationRepository;
        _db = db;
        _currentUser = currentUser;
        _authorizationService = authorizationService;
        _sourceRepairExecutor = sourceRepairExecutor;
        _toolExtensions = toolExtensions?.ToArray() ?? Array.Empty<ICodeMasterAgentToolExtension>();
        _batchAdapters = (batchAdapters ?? []).ToDictionary(item => item.OperationKind, StringComparer.OrdinalIgnoreCase);
        _platformConstraints = platformConstraints;
    }

    public async Task<IList<AITool>> CreateToolsAsync(long conversationId, long projectId, string requestId, Func<AiChatStreamEvent, CancellationToken, ValueTask>? publishAsync = null)
    {
        _publishAsync = publishAsync;
        if (_cacheManager == null)
            return Array.Empty<AITool>();
        var readCache = _cacheManager.Acquire(conversationId, _blueprintService);
        var dictionaryTasks = new Dictionary<string, Task<string>>(StringComparer.Ordinal);

        void InvalidateReadCaches()
        {
            readCache.InvalidateMetadata();
            dictionaryTasks.Clear();
        }

        async Task<string> GetProjectSummaryAsync()
        {
            var overview = await readCache.GetOverviewAsync(projectId);
            return BuildProjectSummaryInvocation(overview, null).Output;
        }

        async Task<string> GetProjectStructureAsync()
        {
            return JsonSerializer.Serialize(await readCache.GetProjectAsync(projectId), JsonOptions);
        }

        async Task<string> GetCodegenCatalogAsync()
        {
            var catalog = await readCache.GetCatalogAsync();
            catalog.PlatformConstraints = BuildPlatformConstraintsDto();
            return JsonSerializer.Serialize(catalog, JsonOptions);
        }

        AiPlatformConstraintsDto BuildPlatformConstraintsDto()
        {
            var doc = _platformConstraints?.Current;
            if (doc == null) return new AiPlatformConstraintsDto();
            return new AiPlatformConstraintsDto
            {
                Frontend = doc.Frontend.RenderForPrompt(),
                Backend = doc.Backend.RenderForPrompt(),
                Relations = doc.Relations.RenderForPrompt(),
                Controls = doc.Controls.RenderForPrompt(),
                Workflow = doc.Workflow.RenderForPrompt(),
                Generation = doc.Generation.RenderForPrompt()
            };
        }

        async Task<string> GetEntityBlueprintAsync(EntityBlueprintQuery query)
        {
            var entity = await readCache.GetEntityAsync(projectId, query.EntityId, query.EntityName);
            return JsonSerializer.Serialize(entity, JsonOptions);
        }

        async Task<string> GetUiPageBlueprintAsync(UiPageBlueprintQuery query)
        {
            var page = await readCache.GetUiPageAsync(
                projectId,
                query.EntityId,
                query.EntityName,
                query.PageType);
            return JsonSerializer.Serialize(page, JsonOptions);
        }

        async Task<string> GetProjectDictionariesAsync(ProjectDictionaryQuery? query = null)
        {
            // Reading all dictionaries is a valid project inspection operation. Keep the
            // argument optional so an assistant call with an empty arguments object does not
            // fail function binding before the tool can apply its safe defaults.
            query ??= new ProjectDictionaryQuery();
            var key = JsonSerializer.Serialize(query, JsonOptions);
            if (dictionaryTasks.TryGetValue(key, out var cached))
                return await cached;

            var task = LoadProjectDictionariesAsync(query);
            dictionaryTasks[key] = task;
            return await task;
        }

        async Task<string> LoadProjectDictionariesAsync(ProjectDictionaryQuery query)
        {
            var dictionaries = await _projectService.GetProjectDictionariesAsync(projectId, new ProjectDictionaryQueryDto
            {
                TenantId = query.TenantId,
                DictType = query.DictType,
                Keyword = query.Keyword,
                IncludeItems = query.IncludeItems,
                IncludeDisabled = query.IncludeDisabled
            });
            return JsonSerializer.Serialize(dictionaries, JsonOptions);
        }

        async Task<string> GetProjectWorkPlanAsync()
        {
            var plan = await _workPlanService.GetForRequestAsync(projectId, conversationId, requestId);
            return JsonSerializer.Serialize(plan, JsonOptions);
        }

        async Task<string> GetProjectCompletionStatusAsync()
        {
            var overview = await readCache.GetOverviewAsync(projectId);
            var plan = await _workPlanService.GetActiveAsync(projectId);
            var entities = overview.Modules.SelectMany(module => module.Entities).ToList();
            return JsonSerializer.Serialize(new
            {
                project = overview.Project,
                projectStructureHash = overview.StructureVersion,
                overview.Totals,
                modules = overview.Modules.Select(module => new
                {
                    module.Id,
                    module.ModuleName,
                    module.ModuleDescription,
                    entityCount = module.Entities.Count,
                    generatedEntityCount = module.Entities.Count(entity => entity.IsGenerated),
                    pendingGeneration = module.Entities.Where(entity => !entity.IsGenerated).Select(entity => entity.Name)
                }),
                generation = new
                {
                    generatedEntities = entities.Count(entity => entity.IsGenerated),
                    pendingEntities = entities.Where(entity => !entity.IsGenerated).Select(entity => entity.Name)
                },
                workPlan = plan == null ? null : new
                {
                    plan.Id,
                    plan.ConversationId,
                    plan.Title,
                    plan.Status,
                    plan.StructureVersion,
                    plan.ConfirmationStatus,
                    plan.RequiresConfirmation,
                    plan.CompletedTasks,
                    plan.TotalTasks,
                    activeBatch = plan.ActiveBatch == null ? null : new
                    {
                        plan.ActiveBatch.Id,
                        plan.ActiveBatch.BatchKey,
                        plan.ActiveBatch.Status,
                        plan.ActiveBatch.ToolExecutionId,
                        plan.ActiveBatch.ErrorMessage,
                        plan.ActiveBatch.TaskIds
                    },
                    stages = plan.Stages.Select(stage => new
                    {
                        stage.Id,
                        stage.StageKey,
                        stage.Kind,
                        stage.Title,
                        stage.Status,
                        stage.CompletedTasks,
                        stage.TotalTasks,
                        tasks = stage.Tasks.Select(task => new
                        {
                            task.Id,
                            task.TaskKey,
                            task.Title,
                            task.OperationKind,
                            task.Status,
                            task.DependsOnTaskKeys,
                            task.UnmetDependencyTaskKeys,
                            task.LastDiagnosis,
                            task.LastFailureCategory,
                            hasExecutableDefinition = task.StandardJson.HasValue,
                            task.AvailableActions
                        })
                    })
                }
            }, JsonOptions);
        }

        async Task<string> GetProjectWorkTaskAsync(AiProjectWorkTaskQuery query)
        {
            if (string.IsNullOrWhiteSpace(query.TaskKey))
                throw new ArgumentException("Task key is required.");
            var plan = await _workPlanService.GetActiveAsync(projectId)
                ?? throw new InvalidOperationException("No active project work plan is available.");
            var stage = plan.Stages.FirstOrDefault(item => item.Tasks.Any(task =>
                string.Equals(task.TaskKey, query.TaskKey.Trim(), StringComparison.OrdinalIgnoreCase)))
                ?? throw new KeyNotFoundException($"Work plan task '{query.TaskKey}' was not found.");
            var task = stage.Tasks.First(item =>
                string.Equals(item.TaskKey, query.TaskKey.Trim(), StringComparison.OrdinalIgnoreCase));
            return JsonSerializer.Serialize(new
            {
                planId = plan.Id,
                plan.StructureVersion,
                plan.Status,
                plan.ConfirmationStatus,
                stage = new { stage.Id, stage.StageKey, stage.Kind, stage.Title, stage.Status },
                task
            }, JsonOptions);
        }

        async Task<AiProjectWorkPlanDto> RequireTestSourceRepairPlanAsync()
        {
            if (_sourceRepairExecutor == null)
                throw new InvalidOperationException("CodeMaster source repair is not installed.");
            var plan = await _workPlanService.GetActiveAsync(projectId)
                ?? throw new InvalidOperationException("No active project work plan is available for source repair.");
            if (!string.Equals(plan.RepairMode, "TestSourceRepair", StringComparison.OrdinalIgnoreCase) ||
                !plan.RepairPolicy.CanInspectCodeMasterSource)
                throw new InvalidOperationException("CodeMaster source inspection is available only in an authorized TestSourceRepair plan.");
            return plan;
        }

        async Task<string> InspectCodeMasterSourceAsync(AiProjectSourceInspectionRequest input)
        {
            _ = await RequireTestSourceRepairPlanAsync();
            var result = await _sourceRepairExecutor!.InspectAsync(input);
            return JsonSerializer.Serialize(result, JsonOptions);
        }

        async Task<string> ApplyTestSourceRepairAsync(AiProjectSourceRepairRequest input)
        {
            var plan = await RequireTestSourceRepairPlanAsync();
            if (input.PlanId != plan.Id)
                throw new InvalidOperationException("The source repair plan id is stale.");
            var task = plan.Stages.SelectMany(item => item.Tasks)
                .FirstOrDefault(item => item.Id == input.TaskId);
            var technicalFailure = task != null &&
                (string.Equals(task.LastFailureCategory, "GeneratorOrTemplate", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(task.LastFailureCategory, "GeneratedProjectIntegration", StringComparison.OrdinalIgnoreCase));
            if (!technicalFailure || !string.Equals(task!.Status, "Failed", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(task.LastAttempt?.FailureSignature, input.FailureSignature?.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Source repair requires the current failed generator/integration signature from the task list.");
            if (task.LastAttempt?.Repair != null)
                throw new InvalidOperationException("This task attempt already contains one CodeMaster source repair. Retry or replan before another patch.");
            input.ProjectId = projectId;
            var result = await _sourceRepairExecutor!.ApplyAsync(input);
            await _workPlanService.RecordSourceRepairAsync(input.PlanId, input.TaskId, result);
            return JsonSerializer.Serialize(result, JsonOptions);
        }


        /// <summary>
        /// Local helper: after a plan is saved, optionally confirm it and start
        /// executing the first ready batch. Both upsert_project_work_plan and
        /// submit_business_plan paths call this so the auto-continue behavior
        /// stays in lock-step (doc section 5.3).
        /// </summary>
        async Task<(AiProjectWorkPlanDto Plan, AiToolExecutionDto? Execution)> AutoApproveAndExecuteAsync(
            AiProjectWorkPlanDto plan)
        {
            if (plan.ActiveBatchId.HasValue)
                return (await _workPlanService.GetAsync(plan.Id), null);

            var conv = await _conversationRepository.GetQueryable()
                .Where(item => item.Id == conversationId && item.UserId == _currentUser.UserId)
                .FirstAsync();
            var preferences = AiWorkflowPreferencesStore.Read(conv?.ContextSummary);
            if (!preferences.ShouldAutoApprove)
                return (plan, null);

            if (plan.RequiresConfirmation)
            {
                plan = await _workPlanService.ConfirmAsync(
                    plan.Id,
                    new ConfirmAiProjectWorkPlanRequest
                    {
                        ExpectedStructureVersion = plan.StructureVersion,
                        ContentHash = plan.ContentHash ?? string.Empty
                    });
            }

            plan = await _workPlanService.GetAsync(plan.Id);
            var hasReadyTask = plan.Stages
                .SelectMany(stage => stage.Tasks)
                .Any(task => string.Equals(task.Status, "Ready", StringComparison.OrdinalIgnoreCase));
            AiToolExecutionDto? execution = null;
            if (!plan.ActiveBatchId.HasValue && hasReadyTask)
            {
                execution = await ExecuteNextWorkPlanBatchAsync(
                    conversationId,
                    projectId,
                    plan.Id,
                    $"plan-auto-{plan.Id}-{DateTime.UtcNow.Ticks}");
            }
            plan = await _workPlanService.GetAsync(plan.Id);
            InvalidateReadCaches();
            return (plan, execution);
        }

        async Task<string> UpsertProjectWorkPlanAsync(UpsertAiProjectWorkPlanRequest input)
        {
            try
            {
                input.ProjectId = projectId;
                input.ConversationId = conversationId;
                var plan = await _workPlanService.UpsertAsync(input);
                var (finalPlan, execution) = await AutoApproveAndExecuteAsync(plan);
                plan = finalPlan;
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    plan,
                    execution,
                    message = execution != null
                        ? "项目方案已保存，已开始执行就绪任务。"
                        : plan.Status is "Blocked" or "Failed"
                            ? "项目方案已保存，但当前仍有阻塞任务，未开始执行。"
                            : plan.Stages.SelectMany(stage => stage.Tasks).Any(task =>
                                string.Equals(task.Status, "Ready", StringComparison.OrdinalIgnoreCase))
                                ? "项目方案已保存，存在就绪任务但本轮未能启动执行。"
                                : "项目方案已保存，当前没有可执行任务。"
                }, JsonOptions);
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    projectId,
                    planId = input.PlanId,
                    diagnostic = ex.GetBaseException().Message,
                    errorType = ex.GetBaseException().GetType().Name,
                    message = "项目方案未保存，请根据提示修正后重试。"
                }, JsonOptions);
            }
        }

        async Task<string> SupersedeProjectWorkPlanAsync(SupersedeAiProjectWorkPlanRequest input)
        {
            try
            {
                if (input.PlanId <= 0)
                    throw new ArgumentException("Plan id is required.");
                var current = await _workPlanService.GetAsync(input.PlanId);
                if (current.ConversationId != conversationId || current.ProjectId != projectId)
                    throw new InvalidOperationException("The work plan is not owned by this Agent conversation and project.");

                var plan = await _workPlanService.CancelAsync(input.PlanId, superseded: true);
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    plan,
                    message = "旧项目方案已撤销，可以重新提交。"
                }, JsonOptions);
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    planId = input.PlanId,
                    projectId,
                    diagnostic = ex.GetBaseException().Message,
                    errorType = ex.GetBaseException().GetType().Name,
                    message = "The work plan was not superseded. Correct the diagnostic and retry only after resolving any active batch."
                }, JsonOptions);
            }
        }

        async Task<string> SubmitBusinessPlanAsync(BusinessPlanDefinition input)
        {
            try
            {
                if (input == null)
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        projectId,
                        diagnostic = "BusinessPlanDefinition payload is required.",
                        errorType = nameof(ArgumentNullException),
                        message = "业务计划为空，请提交完整的 modules/entities/relations/views/pages。"
                    }, JsonOptions);

                Func<BusinessPlanCompileEvent, Task>? progressAsync = evt =>
                {
                    if (_publishAsync == null) return Task.CompletedTask;
                    return _publishAsync(new AiChatStreamEvent
                    {
                        Event = "compile_progress",
                        RequestId = requestId,
                        Phase = evt.Phase,
                        Stage = evt.Stage,
                        Percent = evt.Percent,
                        AddedEntities = evt.AddedEntities,
                        AddedViews = evt.AddedViews,
                        TotalTasks = evt.TotalTasks,
                        Message = evt.Message
                    }, CancellationToken.None).AsTask();
                };

                var compile = await BusinessPlanCompiler.CompileAsync(
                    input, projectId, conversationId, _blueprintService, progressAsync);

                var saved = await _workPlanService.UpsertAsync(compile.CompiledPlan);

                AiToolExecutionDto? execution = null;
                var (finalSaved, execFromHelper) = await AutoApproveAndExecuteAsync(saved);
                saved = finalSaved;
                if (execution == null) execution = execFromHelper;

                return JsonSerializer.Serialize(new
                {
                    success = true,
                    projectId,
                    planId = saved.Id,
                    plan = saved,
                    execution,
                    report = compile.Report,
                    warnings = compile.Warnings,
                    message = execution != null
                        ? $"业务计划已编译并保存，已自动开始执行就绪任务，共 {compile.Report.Tasks} 项。"
                        : saved.Status is "Blocked" or "Failed"
                            ? $"业务计划已编译保存，但当前仍有阻塞任务，请查看任务列表。"
                            : saved.Stages.SelectMany(s => s.Tasks).Any(t =>
                                string.Equals(t.Status, "Ready", StringComparison.OrdinalIgnoreCase))
                                ? $"业务计划已编译保存，存在就绪任务但本轮未能启动执行。"
                                : $"业务计划已编译保存，当前没有可执行任务。"
                }, JsonOptions);
            }
            catch (ArgumentException ex)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    projectId,
                    diagnostic = ex.GetBaseException().Message,
                    errorType = nameof(ArgumentException),
                    message = "业务计划校验未通过，请根据提示修正 modules/entities/fields/relations 之间的引用后重试。"
                }, JsonOptions);
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    projectId,
                    diagnostic = ex.GetBaseException().Message,
                    errorType = ex.GetBaseException().GetType().Name,
                    message = "业务计划编译或保存失败，请根据提示修正后重试。"
                }, JsonOptions);
            }
        }

        async Task<string> ProposeProjectWorkBatchAsync(ProposeAiProjectWorkBatchRequest input)
        {
            var pending = await _executionRepository.GetQueryable()
                .Where(item => item.ConversationId == conversationId &&
                               item.UserId == _currentUser.UserId &&
                               item.Status == "PendingApproval")
                .OrderBy(item => item.CreateTime)
                .FirstAsync();
            if (pending != null)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    code = "pending_approval_exists",
                    pendingApprovalId = pending.Id,
                    projectId,
                    canContinueMcp = true,
                    message = "当前变更方案尚未处理，请先完成当前方案后再提交新的变更方案。"
                }, JsonOptions);
            }

            try
            {
                var proposal = await _workPlanService.CreateBatchAsync(input);
                var execution = await CreatePendingExecutionAsync(
                    conversationId,
                    projectId,
                    requestId,
                    "project_work_batch",
                    proposal);
                await _workPlanService.AttachToolExecutionAsync(proposal.Batch.Id, execution.Id);
                proposal.Batch.ToolExecutionId = execution.Id;
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    pendingApprovalId = execution.Id,
                    projectId,
                    planId = proposal.PlanId,
                    batch = proposal.Batch,
                    taskCount = proposal.Operations.Count,
                    message = "当前任务已准备。"
                }, JsonOptions);
            }
            catch (Exception ex)
            {
                // Batch proposals are agent-facing validation. Preserve the exact
                // reason so the model can repair the plan instead of retrying blindly.
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    projectId,
                    planId = input.PlanId,
                    diagnostic = ex.Message,
                    errorType = ex.GetType().Name,
                    message = "变更方案未应用，请根据提示修正后重试。"
                }, JsonOptions);
            }
        }

        async Task<string> ProposeProjectChangeSetAsync(ProjectChangeSetProposal proposal)
        {
            _ = await GetProjectAsync(projectId);
            var userMessage = await _db.Queryable<AiMessage>()
                .Where(item => item.ConversationId == conversationId &&
                               item.RequestId == requestId &&
                               item.Role == "user")
                .FirstAsync();
            if (AiConversationService.RequiresProjectWorkPlan(userMessage?.Content))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    projectId,
                    code = "project_work_plan_required",
                    message = "这是跨模块完整项目需求，请先保存完整项目方案，再统一应用到 CodeMaster。"
                }, JsonOptions);
            }
            var validation = await _changeSetService.ValidateAsync(projectId, proposal);
            foreach (var dictionary in proposal.Dictionaries)
                ValidateDictionaryProposal(dictionary);
            if (!validation.IsValid)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    projectId,
                    validation.Errors,
                    validation.Warnings,
                    message = "The proposal is invalid. Correct the errors and submit a new change set."
                }, JsonOptions);
            }

            var execution = await CreatePendingExecutionAsync(conversationId, projectId, requestId, "project_change_set", proposal);
            return JsonSerializer.Serialize(new
            {
                success = true,
                pendingApprovalId = execution.Id,
                projectId,
                action = "project_change_set",
                proposal.Summary,
                validation.ModuleCount,
                validation.EntityCount,
                validation.FieldCount,
                validation.RelationCount,
                dictionaryCount = proposal.Dictionaries.Count,
                validation.UpdatedModuleCount,
                validation.UpdatedEntityCount,
                validation.DeletedModuleCount,
                validation.DeletedEntityCount,
                proposal.GenerationMode,
                validation.Warnings,
                message = "变更已准备。"
            }, JsonOptions);
        }

        async Task<string> ProposeUiPageEnhancementAsync(UiPageEnhancementProposal proposal)
        {
            _ = await GetProjectAsync(projectId);
            await ValidateUiPageEnhancementAsync(projectId, proposal);
            var execution = await CreatePendingExecutionAsync(
                conversationId,
                projectId,
                requestId,
                "ui_page_enhancement",
                proposal);

            return JsonSerializer.Serialize(new
            {
                success = true,
                pendingApprovalId = execution.Id,
                projectId,
                action = "ui_page_enhancement",
                proposal.Summary,
                proposal.TargetKind,
                proposal.Page,
                proposal.EntityId,
                proposal.EntityName,
                proposal.PageType,
                operationCount = proposal.Operations.Count,
                proposal.BuildAfterApply,
                message = "The UI design change is waiting for user approval. Stable genId values and business bindings will be preserved."
            }, JsonOptions);
        }

        async Task<string> ProposeDictionaryChangeAsync(ProjectDictionaryChangeProposal proposal)
        {
            _ = await GetProjectAsync(projectId);
            ValidateDictionaryProposal(proposal);
            var execution = await CreatePendingExecutionAsync(
                conversationId,
                projectId,
                requestId,
                "dictionary_change",
                proposal);
            return JsonSerializer.Serialize(new
            {
                success = true,
                pendingApprovalId = execution.Id,
                projectId,
                action = "dictionary_change",
                proposal.Summary,
                proposal.TenantId,
                proposal.DictType,
                proposal.DictName,
                itemCount = proposal.Items.Count,
                message = "The dictionary change is waiting for user approval. No target-project dictionary data has been changed yet."
            }, JsonOptions);
        }

        var canRead = await _authorizationService.HasAnyAsync("codegen:project:list", "codegen:project:view");
        var canChange = await _authorizationService.HasAnyAsync(
                "codegen:projectModule:create",
                "codegen:projectModule:update",
                "codegen:projectModule:delete",
                "codegen:moduleEntity:create",
                "codegen:moduleEntity:update",
                "codegen:moduleEntity:delete",
                "codegen:project:build");
        var canChangeDictionary = await _authorizationService.HasAnyAsync("codegen:project:update");
        var sourceRepairPlan = _sourceRepairExecutor == null
            ? null
            : await _workPlanService.GetActiveAsync(projectId);
        var canSourceRepair = sourceRepairPlan != null &&
            string.Equals(sourceRepairPlan.RepairMode, "TestSourceRepair", StringComparison.OrdinalIgnoreCase) &&
            sourceRepairPlan.RepairPolicy.CanInspectCodeMasterSource;

        var tools = CodeMasterAgentToolCatalog.Create(
            canRead,
            GetProjectSummaryAsync,
            canRead,
            GetProjectStructureAsync,
            canRead,
            GetCodegenCatalogAsync,
            canRead,
            GetEntityBlueprintAsync,
            canRead,
            GetUiPageBlueprintAsync,
            canRead,
            GetProjectDictionariesAsync,
            canChange,
            ProposeProjectChangeSetAsync,
            ProposeUiPageEnhancementAsync,
            canChangeDictionary,
            ProposeDictionaryChangeAsync,
            includeWorkPlan: canRead,
            getProjectWorkPlan: GetProjectWorkPlanAsync,
            getProjectCompletionStatus: GetProjectCompletionStatusAsync,
            getProjectWorkTask: GetProjectWorkTaskAsync,
            upsertProjectWorkPlan: UpsertProjectWorkPlanAsync,
            includeWorkBatch: canChange,
            proposeProjectWorkBatch: ProposeProjectWorkBatchAsync,
            includeSourceRepair: canSourceRepair,
            inspectCodeMasterSource: InspectCodeMasterSourceAsync,
            applyTestSourceRepair: ApplyTestSourceRepairAsync,
            supersedeProjectWorkPlan: SupersedeProjectWorkPlanAsync,
            includeSubmitBusinessPlan: canChange,
            submitBusinessPlan: SubmitBusinessPlanAsync);

        if (_toolExtensions.Count == 0)
            return tools;

        var context = new CodeMasterAgentExtensionContext
        {
            ConversationId = conversationId,
            ProjectId = projectId,
            RequestId = requestId,
            CreatePendingApprovalAsync = async (toolName, input) =>
                (await CreatePendingExtensionExecutionAsync(
                    conversationId,
                    projectId,
                    requestId,
                    toolName,
                    input)).Id
        };
        foreach (var extension in _toolExtensions)
        {
            foreach (var tool in await extension.CreateToolsAsync(context))
            {
                if (tool is AIFunction function && tools.OfType<AIFunction>().Any(existing => existing.Name == function.Name))
                    throw new InvalidOperationException($"Duplicate Agent tool name: {function.Name}");
                tools.Add(tool);
            }
        }
        return tools;
    }

    public async Task<AgentToolInvocationResult> InvokeAsync(
        long conversationId,
        long projectId,
        string requestId,
        string toolName,
        JsonElement arguments,
        string? knownProjectStructureVersion = null)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            throw new ArgumentException("Tool name is required.");

        var function = (await CreateToolsAsync(conversationId, projectId, requestId))
            .OfType<AIFunction>()
            .FirstOrDefault(item => string.Equals(item.Name, toolName.Trim(), StringComparison.Ordinal));
        if (function == null)
            throw new UnauthorizedAccessException($"Tool '{toolName}' is unavailable for the current user.");

        if (string.Equals(toolName, CodeMasterAgentToolCatalog.GetProjectSummary, StringComparison.Ordinal))
            return await BuildProjectSummaryInvocationAsync(projectId, knownProjectStructureVersion);

        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in arguments.EnumerateObject())
                values[property.Name] = property.Value.Clone();
        }
        else if (arguments.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            throw new ArgumentException("Tool arguments must be a JSON object.");
        }

        var result = await function.InvokeAsync(new AIFunctionArguments(values));
        var output = result switch
        {
            null => string.Empty,
            string text => text,
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonElement element => element.GetRawText(),
            _ => JsonSerializer.Serialize(result, JsonOptions)
        };
        return new AgentToolInvocationResult { Output = output };
    }

    private async Task<AgentToolInvocationResult> BuildProjectSummaryInvocationAsync(
        long projectId,
        string? knownProjectStructureVersion)
    {
        var overview = await _blueprintService.GetProjectOverviewAsync(projectId);
        return BuildProjectSummaryInvocation(overview, knownProjectStructureVersion);
    }

    internal static AgentToolInvocationResult BuildProjectSummaryInvocation(
        AiProjectOverviewDto overview,
        string? knownProjectStructureVersion)
    {
        var notModified = !string.IsNullOrWhiteSpace(knownProjectStructureVersion) &&
            string.Equals(
                knownProjectStructureVersion.Trim(),
                overview.StructureVersion,
                StringComparison.OrdinalIgnoreCase);
        var output = notModified
            ? JsonSerializer.Serialize(new
            {
                schemaVersion = overview.SchemaVersion,
                projectStructureHash = overview.StructureVersion,
                notModified = true,
                message = "Project metadata is unchanged. Reuse the project summary already present in this Agent session."
            }, JsonOptions)
            : JsonSerializer.Serialize(new
            {
                overview.SchemaVersion,
                projectStructureHash = overview.StructureVersion,
                overview.Project,
                overview.Totals,
                overview.Modules
            }, JsonOptions);

        return new AgentToolInvocationResult
        {
            Output = output,
            ProjectStructureVersion = overview.StructureVersion,
            ProjectStructureNotModified = notModified
        };
    }

    public async Task<AiToolExecutionDto> AcceptAsync(long executionId)
    {
        var execution = await GetOwnedExecutionAsync(executionId);
        if (execution.Status is "Approved" or "Executing" or "Completed" or "AwaitingClientExecution")
            return ToDto(execution);
        if (!string.Equals(execution.Status, "PendingApproval", StringComparison.Ordinal))
            return ToDto(execution);

        await SupersedeEarlierStandaloneApprovalsAsync(execution);

        // Legacy conversations may have multiple active cards. Preserve the
        // oldest executable record so the UI can show one deterministic card.
        var earlierActive = execution.WorkPlanId.HasValue
            ? null
            : await _executionRepository.GetQueryable()
                .Where(item => item.ConversationId == execution.ConversationId &&
                               item.UserId == _currentUser.UserId &&
                               (item.Status == "PendingApproval" ||
                                item.Status == "Approved" ||
                                item.Status == "AwaitingClientExecution" ||
                                item.Status == "Executing") &&
                               item.Id < execution.Id)
                .OrderBy(item => item.Id)
                .FirstAsync();
        if (earlierActive != null)
        {
            execution.ErrorMessage = Truncate(
                $"Finish the earlier active change first (#{earlierActive.Id}) so dependent MCP operations execute in order.",
                2000);
            execution.UpdateTime = DateTime.UtcNow;
            await _executionRepository.UpdateAsync(execution);
            return ToDto(execution);
        }

        await DemandToolPermissionAsync(execution);
        var approvedAt = DateTime.UtcNow;
        var claimed = await _db.Updateable<AiToolExecution>()
            .SetColumns(item => item.Status == "Approved")
            .SetColumns(item => item.ApprovedAt == approvedAt)
            .SetColumns(item => item.UpdateUserId == _currentUser.UserId)
            .SetColumns(item => item.UpdateBy == _currentUser.UserName)
            .SetColumns(item => item.UpdateTime == approvedAt)
            .Where(item => item.Id == executionId &&
                           item.UserId == _currentUser.UserId &&
                           item.Status == "PendingApproval")
            .ExecuteCommandAsync();
        if (claimed == 0)
        {
            execution = await GetOwnedExecutionAsync(executionId);
            return ToDto(execution);
        }

        return ToDto(await GetOwnedExecutionAsync(executionId));
    }

    private async Task SupersedeEarlierStandaloneApprovalsAsync(AiToolExecution execution)
    {
        if (!execution.WorkPlanId.HasValue)
            return;

        var plan = await _workPlanService.GetAsync(execution.WorkPlanId.Value);
        if (!string.Equals(plan.Status, "Active", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(plan.ConfirmationStatus, "Confirmed", StringComparison.OrdinalIgnoreCase) ||
            !plan.StartedAt.HasValue)
        {
            return;
        }

        var earlierApprovals = await _executionRepository.GetQueryable()
            .Where(item => item.ConversationId == execution.ConversationId &&
                           item.UserId == _currentUser.UserId &&
                           !item.WorkPlanId.HasValue &&
                           item.CreateTime < plan.StartedAt.Value &&
                           (item.Status == "PendingApproval" ||
                            item.Status == "Approved"))
            .ToListAsync();
        if (earlierApprovals.Count == 0)
            return;

        var now = DateTime.UtcNow;
        foreach (var earlier in earlierApprovals)
        {
            earlier.Status = "Rejected";
            earlier.CompletedAt = now;
            earlier.ErrorMessage = "已纳入当前项目任务计划，系统将按计划继续执行。";
            earlier.UpdateUserId = _currentUser.UserId;
            earlier.UpdateBy = _currentUser.UserName;
            earlier.UpdateTime = now;
            await _executionRepository.UpdateAsync(earlier);
        }
    }

    public async Task<AiToolExecutionDto> ApproveAsync(long executionId)
    {
        var accepted = await AcceptAsync(executionId);
        return string.Equals(accepted.Status, "Approved", StringComparison.Ordinal)
            ? await ExecuteApprovedAsync(executionId)
            : accepted;
    }

    public async Task<AiToolExecutionDto> ExecuteApprovedAsync(long executionId)
    {
        var execution = await GetOwnedExecutionAsync(executionId);
        if (execution.Status is "Completed" or "AwaitingClientExecution" or "Failed" or "Rejected")
            return ToDto(execution);
        if (string.Equals(execution.Status, "PendingApproval", StringComparison.Ordinal))
        {
            var accepted = await AcceptAsync(executionId);
            if (!string.Equals(accepted.Status, "Approved", StringComparison.Ordinal))
                return accepted;
            execution = await GetOwnedExecutionAsync(executionId);
        }
        if (string.Equals(execution.Status, "Executing", StringComparison.Ordinal))
            return ToDto(execution);
        if (!string.Equals(execution.Status, "Approved", StringComparison.Ordinal))
            return ToDto(execution);

        var startedAt = DateTime.UtcNow;
        var claimed = await _db.Updateable<AiToolExecution>()
            .SetColumns(item => item.Status == "Executing")
            .SetColumns(item => item.UpdateUserId == _currentUser.UserId)
            .SetColumns(item => item.UpdateBy == _currentUser.UserName)
            .SetColumns(item => item.UpdateTime == startedAt)
            .Where(item => item.Id == executionId &&
                           item.UserId == _currentUser.UserId &&
                           item.Status == "Approved")
            .ExecuteCommandAsync();
        if (claimed == 0)
            return ToDto(await GetOwnedExecutionAsync(executionId));

        execution = await GetOwnedExecutionAsync(executionId);

        try
        {
            object result = execution.ToolName switch
            {
                "create_module" => await ExecuteCreateModuleAsync(execution),
                "create_entity" => await ExecuteCreateEntityAsync(execution),
                "project_change_set" => await ExecuteProjectChangeSetAsync(execution),
                "ui_page_enhancement" => await ExecuteUiPageEnhancementAsync(execution),
                "dictionary_change" => await ExecuteDictionaryChangeAsync(execution),
                "project_work_batch" => await ExecuteProjectWorkBatchAsync(execution),
                _ => await ExecuteExtensionAsync(execution)
            };

            var hasDeferredMaterialization = result switch
            {
                ProjectChangeSetExecutionResult { ClientActions.Count: > 0 } => true,
                UiPageEnhancementExecutionResult { ClientActions.Count: > 0 } => true,
                AiProjectWorkBatchExecutionResult { ClientActions.Count: > 0 } => true,
                IAgentClientActionResult { HasClientActions: true } => true,
                _ => false
            };
            var awaitsClientExecution = execution.WorkBatchId.HasValue && hasDeferredMaterialization;
            execution.Status = ResolveStatusAfterMetadataApplication(
                hasDeferredMaterialization,
                execution.WorkBatchId.HasValue);
            execution.ApprovedAt = DateTime.UtcNow;
            execution.CompletedAt = awaitsClientExecution ? null : DateTime.UtcNow;
            SetOutputPayload(execution, JsonSerializer.Serialize(result, JsonOptions));
            execution.UpdateUserId = _currentUser.UserId;
            execution.UpdateBy = _currentUser.UserName;
            execution.UpdateTime = DateTime.UtcNow;
            await _executionRepository.UpdateAsync(execution);
            if (execution.WorkBatchId.HasValue)
            {
                if (awaitsClientExecution)
                    await _workPlanService.WaitForClientExecutionAsync(execution.WorkBatchId.Value);
                else
                    await _workPlanService.CompleteBatchAsync(execution.WorkBatchId.Value, true, null);
            }
        }
        catch (Exception ex)
        {
            execution.Status = "Failed";
            execution.ApprovedAt = DateTime.UtcNow;
            execution.CompletedAt = DateTime.UtcNow;
            execution.ErrorMessage = Truncate(ex.Message, 2000);
            execution.UpdateTime = DateTime.UtcNow;
            await _executionRepository.UpdateAsync(execution);
            if (execution.WorkBatchId.HasValue)
                await _workPlanService.CompleteBatchAsync(
                    execution.WorkBatchId.Value,
                    false,
                    execution.ErrorMessage,
                    new AiProjectFailureEvidenceInput
                    {
                        ExecutionMode = "Server",
                        ActionName = execution.ToolName,
                        InputHash = execution.InputHash,
                        ErrorMessage = execution.ErrorMessage,
                        DiagnosticOutput = ex.ToString(),
                        ReproducedInCodeMasterOwnedPath = IsCodeMasterOwnedFailure(ex)
                    });
            // Return the handled failure so the conversation can render the
            // diagnostic and continue with a repair turn instead of producing
            // an empty/400 response from the approval endpoint.
            return ToDto(execution);
        }

        return ToDto(execution);
    }

    internal static string ResolveStatusAfterMetadataApplication(
        bool hasDeferredMaterialization,
        bool belongsToWorkBatch = false)
    {
        return belongsToWorkBatch && hasDeferredMaterialization
            ? "AwaitingClientExecution"
            : "Completed";
    }

    public async Task<AiToolExecutionDto?> ExecuteNextWorkPlanBatchAsync(
        long conversationId,
        long projectId,
        long planId,
        string requestId)
    {
        var plan = await _workPlanService.GetAsync(planId);
        if (plan.ConversationId != conversationId || plan.ProjectId != projectId)
            throw new InvalidOperationException("The work plan is not owned by this conversation and project.");
        if (plan.RequiresConfirmation)
            throw new InvalidOperationException("The work plan must be confirmed before automatic execution starts.");
        if (plan.Status is "Completed" or "Cancelled" or "Superseded")
            return null;

        if (plan.ActiveBatchId.HasValue)
        {
            var existing = await _executionRepository.GetQueryable()
                .Where(item => item.ConversationId == conversationId &&
                               item.ProjectId == projectId &&
                               item.UserId == _currentUser.UserId &&
                               item.WorkPlanId == planId &&
                               item.WorkBatchId == plan.ActiveBatchId.Value)
                .OrderByDescending(item => item.CreateTime)
                .FirstAsync()
                ?? throw new InvalidOperationException("The active work batch has no linked execution record.");
            return string.Equals(existing.Status, "PendingApproval", StringComparison.Ordinal)
                ? await ApproveAsync(existing.Id)
                : ToDto(existing);
        }

        AiProjectWorkBatchProposal proposal;
        try
        {
            proposal = await _workPlanService.CreateBatchAsync(new ProposeAiProjectWorkBatchRequest
            {
                PlanId = planId,
                ExpectedStructureVersion = plan.StructureVersion,
                MaxTasks = 50
            });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("No ready task", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var execution = await CreatePendingExecutionAsync(
            conversationId,
            projectId,
            NormalizeAutomaticRequestId(requestId, planId, proposal.Batch.Id),
            "project_work_batch",
            proposal);
        await _workPlanService.AttachToolExecutionAsync(proposal.Batch.Id, execution.Id);
        return await ApproveAsync(execution.Id);
    }

    internal static object BuildClientActionResultFallback(object result)
    {
        var actions = (result switch
        {
            ProjectChangeSetExecutionResult value => value.ClientActions,
            UiPageEnhancementExecutionResult value => value.ClientActions,
            AiProjectWorkBatchExecutionResult value => value.ClientActions,
            _ => result.GetType().GetProperty("ClientActions")?.GetValue(result) as IEnumerable<AiClientActionDto>
        })?.ToList() ?? new List<AiClientActionDto>();

        if (actions.Count == 0)
            throw new InvalidOperationException("The execution result was too large and its required client actions could not be preserved.");

        var resultType = result.GetType();
        return new
        {
            resultTruncated = true,
            projectId = resultType.GetProperty("ProjectId")?.GetValue(result),
            summary = resultType.GetProperty("Summary")?.GetValue(result)?.ToString(),
            clientActions = actions
        };
    }

    public async Task<AiToolExecutionDto> CompleteClientActionsAsync(
        long executionId,
        CompleteAiClientActionsRequest request)
    {
        var execution = await GetOwnedExecutionAsync(executionId);
        if (string.Equals(execution.Status, "Completed", StringComparison.Ordinal) && request.Success)
            return ToDto(execution);

        if (!string.Equals(execution.Status, "AwaitingClientExecution", StringComparison.Ordinal))
            throw new InvalidOperationException("This tool execution is not waiting for client-side work.");

        var clientOutput = string.IsNullOrWhiteSpace(request.DiagnosticOutput)
            ? request.Output
            : request.DiagnosticOutput;
        var storedOutput = ReadOutputPayload(execution);
        if (!string.IsNullOrWhiteSpace(storedOutput) && execution.ToolName == "project_change_set")
        {
            var result = JsonSerializer.Deserialize<ProjectChangeSetExecutionResult>(
                storedOutput,
                JsonOptions);
            if (result != null)
            {
                result.ClientExecutionOutput = string.IsNullOrWhiteSpace(clientOutput)
                    ? null
                    : Truncate(clientOutput, 6000);
                result.ClientExecutionFailedAction = string.IsNullOrWhiteSpace(request.FailedAction)
                    ? null
                    : Truncate(request.FailedAction, 100);
                result.ClientExecutionError = string.IsNullOrWhiteSpace(request.ErrorMessage)
                    ? null
                    : Truncate(request.ErrorMessage, 2000);
                SetOutputPayload(execution, JsonSerializer.Serialize(result, JsonOptions));
            }
        }
        else if (!string.IsNullOrWhiteSpace(storedOutput) && execution.ToolName == "ui_page_enhancement")
        {
            var result = JsonSerializer.Deserialize<UiPageEnhancementExecutionResult>(
                storedOutput,
                JsonOptions);
            if (result != null)
            {
            result.ClientExecutionOutput = string.IsNullOrWhiteSpace(clientOutput)
                ? null
                : Truncate(clientOutput, 6000);
            result.ClientExecutionFailedAction = string.IsNullOrWhiteSpace(request.FailedAction)
                ? null
                : Truncate(request.FailedAction, 100);
            result.ClientExecutionError = string.IsNullOrWhiteSpace(request.ErrorMessage)
                ? null
                : Truncate(request.ErrorMessage, 2000);
            SetOutputPayload(execution, JsonSerializer.Serialize(result, JsonOptions));
            }
        }

        var finalStatus = request.Success ? "Completed" : "Failed";
        var completedAt = DateTime.UtcNow;
        var errorMessage = request.Success
            ? null
            : Truncate(request.ErrorMessage ?? "Client-side execution failed.", 2000);
        execution.Status = finalStatus;
        execution.CompletedAt = completedAt;
        execution.ErrorMessage = errorMessage;
        execution.UpdateUserId = _currentUser.UserId;
        execution.UpdateBy = _currentUser.UserName;
        execution.UpdateTime = completedAt;
        var claimed = await _db.Updateable<AiToolExecution>()
            .SetColumns(item => new AiToolExecution
            {
                Status = finalStatus,
                CompletedAt = completedAt,
                ErrorMessage = errorMessage,
                OutputJson = execution.OutputJson,
                OutputPayload = execution.OutputPayload,
                UpdateUserId = _currentUser.UserId,
                UpdateBy = _currentUser.UserName,
                UpdateTime = completedAt
            })
            .Where(item => item.Id == executionId &&
                           item.UserId == _currentUser.UserId &&
                           item.Status == "AwaitingClientExecution")
            .ExecuteCommandAsync();
        if (claimed == 0)
        {
            var current = await GetOwnedExecutionAsync(executionId);
            if (current.Status is "Completed" or "Failed")
                return ToDto(current);
            throw new InvalidOperationException("The client action completion was already claimed by another request.");
        }

        execution = await GetOwnedExecutionAsync(executionId);
        if (execution.WorkBatchId.HasValue)
            await _workPlanService.CompleteBatchAsync(
                execution.WorkBatchId.Value,
                request.Success,
                errorMessage,
                request.Success ? null : new AiProjectFailureEvidenceInput
                {
                    ExecutionMode = "Client",
                    ActionName = execution.ToolName,
                    FailedAction = request.FailedAction,
                    InputHash = execution.InputHash,
                    ErrorMessage = request.ErrorMessage,
                    DiagnosticOutput = clientOutput
                });
        return ToDto(execution);
    }

    public async Task<AiToolExecutionDto> RejectAsync(long executionId)
    {
        var execution = await GetOwnedExecutionAsync(executionId);
        if (!string.Equals(execution.Status, "PendingApproval", StringComparison.Ordinal))
            return ToDto(execution);

        var completedAt = DateTime.UtcNow;
        var updated = await _db.Updateable<AiToolExecution>()
            .SetColumns(item => item.Status == "Rejected")
            .SetColumns(item => item.CompletedAt == completedAt)
            .SetColumns(item => item.UpdateUserId == _currentUser.UserId)
            .SetColumns(item => item.UpdateBy == _currentUser.UserName)
            .SetColumns(item => item.UpdateTime == completedAt)
            .Where(item => item.Id == executionId &&
                           item.UserId == _currentUser.UserId &&
                           item.Status == "PendingApproval")
            .ExecuteCommandAsync();
        if (updated == 0)
            return ToDto(await GetOwnedExecutionAsync(executionId));
        execution = await GetOwnedExecutionAsync(executionId);
        if (execution.WorkBatchId.HasValue)
            await _workPlanService.RejectBatchAsync(execution.WorkBatchId.Value);
        return ToDto(execution);
    }

    private async Task<object> ExecuteCreateModuleAsync(AiToolExecution execution)
    {
        var proposal = DeserializeInput<CreateModuleProposal>(execution);
        ValidateModuleProposal(proposal);
        var id = await _moduleService.CreateAsync(new CreateProjectModuleDto
        {
            ProjectId = execution.ProjectId,
            ModuleName = proposal.ModuleName.Trim(),
            ModuleDescription = proposal.ModuleDescription.Trim(),
            Icon = NormalizeOptional(proposal.Icon),
            OrderNum = proposal.OrderNum,
            RoutePath = NormalizeOptional(proposal.RoutePath),
            Remark = NormalizeOptional(proposal.Remark)
        });

        return new { id, projectId = execution.ProjectId, proposal.ModuleName };
    }

    private async Task DemandToolPermissionAsync(AiToolExecution execution)
    {
        switch (execution.ToolName)
        {
            case "create_module":
                await _authorizationService.DemandAnyAsync("codegen:projectModule:create");
                break;
            case "create_entity":
                await _authorizationService.DemandAnyAsync("codegen:moduleEntity:create");
                break;
            case "project_change_set":
            {
                var proposal = await NormalizeProjectChangeSetAsync(execution);
                if (proposal.Modules.Count > 0)
                    await _authorizationService.DemandAnyAsync("codegen:projectModule:create");
                if (proposal.Entities.Count > 0)
                    await _authorizationService.DemandAnyAsync("codegen:moduleEntity:create");
                if (proposal.ModuleUpdates.Count > 0)
                    await _authorizationService.DemandAnyAsync("codegen:projectModule:update");
                if (proposal.EntityUpdates.Count > 0)
                    await _authorizationService.DemandAnyAsync("codegen:moduleEntity:update");
                if (proposal.DeleteModuleIds.Count > 0)
                    await _authorizationService.DemandAnyAsync("codegen:projectModule:delete", "codegen:projectModule:update");
                if (proposal.DeleteEntityIds.Count > 0)
                    await _authorizationService.DemandAnyAsync("codegen:moduleEntity:delete", "codegen:moduleEntity:update");
                if (proposal.Dictionaries.Count > 0)
                    await _authorizationService.DemandAnyAsync("codegen:project:update");
                if (!string.Equals(proposal.GenerationMode, "None", StringComparison.OrdinalIgnoreCase))
                    await _authorizationService.DemandAnyAsync("codegen:moduleEntity:update");
                if (CodeMasterProjectChangeSetService.RequiresBuildPermission(proposal))
                    await _authorizationService.DemandAnyAsync("codegen:project:build");
                break;
            }
            case "ui_page_enhancement":
                await _authorizationService.DemandAnyAsync("codegen:project:build", "codegen:moduleEntity:update");
                break;
            case "dictionary_change":
                await _authorizationService.DemandAnyAsync("codegen:project:update");
                break;
            case "project_work_batch":
            {
                var proposal = DeserializeInput<AiProjectWorkBatchProposal>(execution);
                foreach (var operation in proposal.Operations)
                {
                    await DemandBatchOperationPermissionAsync(execution.ProjectId, operation);
                }
                break;
            }
            default:
            {
                var extension = _toolExtensions.FirstOrDefault(item => item.CanExecute(execution.ToolName))
                    ?? throw new InvalidOperationException($"Unsupported approved tool: {execution.ToolName}");
                var storedInput = execution.InputJson;
                execution.InputJson = ReadInputPayload(execution);
                try
                {
                    await extension.DemandPermissionAsync(execution);
                }
                finally
                {
                    execution.InputJson = storedInput;
                }
                break;
            }
        }
    }

    private async Task<object> ExecuteExtensionAsync(AiToolExecution execution)
    {
        var extension = _toolExtensions.FirstOrDefault(item => item.CanExecute(execution.ToolName))
            ?? throw new InvalidOperationException($"Unsupported approved tool: {execution.ToolName}");
        var storedInput = execution.InputJson;
        execution.InputJson = ReadInputPayload(execution);
        try
        {
            return await extension.ExecuteAsync(execution);
        }
        finally
        {
            execution.InputJson = storedInput;
        }
    }

    private async Task<object> ExecuteProjectChangeSetAsync(AiToolExecution execution)
    {
        var proposal = await NormalizeProjectChangeSetAsync(execution);
        var repairs = await RepairKnownProjectChangeSetIssuesAsync(execution.ProjectId, proposal);
        var result = await _changeSetService.ExecuteAsync(execution.ProjectId, proposal);
        result.AutomaticRepairs.AddRange(repairs);
        return result;
    }

    private async Task<ProjectChangeSetProposal> NormalizeProjectChangeSetAsync(AiToolExecution execution)
    {
        var definition = DeserializeInput<JsonElement>(execution);
        return await _definitionNormalizer.NormalizeAsync(
            execution.ProjectId,
            $"approval-{execution.Id}",
            definition.GetRawText());
    }

    private async Task<object> ExecuteProjectWorkBatchAsync(AiToolExecution execution)
    {
        var requestId = execution.RequestId ?? string.Empty;
        var proposal = DeserializeInput<AiProjectWorkBatchProposal>(execution);
        if (execution.WorkPlanId != proposal.PlanId || execution.WorkBatchId != proposal.Batch.Id)
            throw new InvalidDataException("The approval record is not linked to the proposed work batch.");
        await _workPlanService.BeginBatchExecutionAsync(proposal.Batch.Id, proposal.InputHash);
        var batchStage = await ResolveBatchStageAsync(proposal);
        await PublishStageEventAsync(proposal, batchStage, "stage_started", requestId);

        var result = new AiProjectWorkBatchExecutionResult
        {
            ProjectId = execution.ProjectId,
            PlanId = proposal.PlanId,
            BatchId = proposal.Batch.Id,
            Summary = proposal.Summary
        };
        foreach (var operation in proposal.Operations)
        {
            await PublishTaskEventAsync(proposal, operation, "task_started", requestId);
            object operationResult;
            try
            {
                operationResult = await ExecuteBatchOperationAsync(execution.ProjectId, operation);
            }
            catch (Exception ex)
            {
                await PublishTaskEventAsync(proposal, operation, "task_completed", requestId, success: false, errorMessage: ex.GetBaseException().Message);
                await PublishStageEventAsync(proposal, batchStage, "stage_completed", requestId, success: false, errorMessage: ex.GetBaseException().Message);
                throw;
            }
            await VerifyWorkBatchOperationAsync(execution.ProjectId, operation, operationResult);
            result.OperationResults.Add(operationResult);
            var actions = operationResult switch
            {
                ProjectChangeSetExecutionResult value => value.ClientActions,
                UiPageEnhancementExecutionResult value => value.ClientActions,
                _ => operationResult.GetType().GetProperty("ClientActions")?.GetValue(operationResult) as List<AiClientActionDto> ?? []
            };
            foreach (var action in actions)
            {
                action.WorkPlanId = proposal.PlanId;
                action.WorkBatchId = proposal.Batch.Id;
                action.WorkTaskId = operation.TaskId;
                action.InputHash = operation.PayloadHash;
                action.IdempotencyKey = $"work-batch:{proposal.Batch.Id}:task:{operation.TaskId}:action:{result.ClientActions.Count}";
                result.ClientActions.Add(action);
            }
            await PublishTaskEventAsync(proposal, operation, "task_completed", requestId, success: true);
        }
        await PublishStageEventAsync(proposal, batchStage, "stage_completed", requestId);
        return result;
    }

    private async Task PublishTaskEventAsync(
        AiProjectWorkBatchProposal proposal,
        AiProjectWorkBatchOperation operation,
        string eventName,
        string requestId,
        bool success = false,
        string? errorMessage = null)
    {
        if (_publishAsync == null) return;
        var taskIndex = proposal.Operations.FindIndex(item => item.TaskId == operation.TaskId);
        var total = proposal.Operations.Count;
        await _publishAsync(new AiChatStreamEvent
        {
            Event = eventName,
            RequestId = requestId,
            ToolName = "project_work_batch",
            ToolStage = operation.TaskKey,
            TaskId = operation.TaskId.ToString(),
            TaskKey = operation.TaskKey,
            TaskIndex = taskIndex >= 0 ? taskIndex + 1 : null,
            TaskSuccess = success,
            ErrorMessage = errorMessage,
            PlanId = proposal.PlanId.ToString(),
            Message = $"{operation.TaskKey} {(eventName == "task_started" ? "started" : success ? "completed" : "failed")}"
        }, CancellationToken.None);
    }

    private async Task<(string Key, string Title)?> ResolveBatchStageAsync(AiProjectWorkBatchProposal proposal)
    {
        if (!proposal.Batch.StageId.HasValue)
            return null;
        var plan = await _workPlanService.GetAsync(proposal.PlanId);
        var stage = plan.Stages.FirstOrDefault(item => item.Id == proposal.Batch.StageId.Value);
        return stage == null ? null : (stage.StageKey, stage.Title);
    }

    private async Task PublishStageEventAsync(
        AiProjectWorkBatchProposal proposal,
        (string Key, string Title)? stage,
        string eventName,
        string requestId,
        bool success = true,
        string? errorMessage = null)
    {
        if (_publishAsync == null || stage == null)
            return;
        await _publishAsync(new AiChatStreamEvent
        {
            Event = eventName,
            RequestId = requestId,
            ToolName = "project_work_batch",
            ToolStage = stage.Value.Key,
            Stage = stage.Value.Title,
            PlanId = proposal.PlanId.ToString(),
            TaskSuccess = success,
            ErrorMessage = errorMessage,
            Message = $"{stage.Value.Title} {(eventName == "stage_started" ? "started" : success ? "completed" : "failed")}"
        }, CancellationToken.None);
    }

    private async Task VerifyWorkBatchOperationAsync(
        long projectId,
        AiProjectWorkBatchOperation operation,
        object operationResult)
    {
        if (!string.Equals(operation.OperationKind, "project_change_set", StringComparison.OrdinalIgnoreCase))
            return;
        if (string.IsNullOrWhiteSpace(operation.DefinitionJson))
            throw new InvalidOperationException($"Task '{operation.TaskKey}' completed without a project change-set payload.");

        var proposal = await _definitionNormalizer.NormalizeAsync(
            projectId,
            operation.TaskKey,
            operation.DefinitionJson);
        if (operationResult is not ProjectChangeSetExecutionResult result)
            throw new InvalidOperationException($"Task '{operation.TaskKey}' returned an unexpected execution result.");

        var expectedEntities = proposal.Entities.Select(item => item.Name.Trim())
            .Where(item => item.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (expectedEntities.Count > 0)
        {
            var blueprint = await _blueprintService.GetProjectAsync(projectId);
            var actualEntities = blueprint.Modules.SelectMany(item => item.Entities)
                .Select(item => item.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = expectedEntities.Where(item => !actualEntities.Contains(item)).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"Task '{operation.TaskKey}' reported success but metadata verification found missing entities: {string.Join(", ", missing)}.");
            if (result.CreatedEntities.Count < expectedEntities.Count)
                throw new InvalidOperationException(
                    $"Task '{operation.TaskKey}' reported only {result.CreatedEntities.Count} created entities for {expectedEntities.Count} requested entities.");
        }

        if (proposal.Dictionaries.Count > 0)
        {
            foreach (var group in proposal.Dictionaries.GroupBy(item => item.TenantId))
            {
                var dictionaries = await _projectService.GetProjectDictionariesAsync(projectId, new ProjectDictionaryQueryDto
                {
                    TenantId = group.Key,
                    IncludeItems = false,
                    IncludeDisabled = true
                });
                var actualTypes = dictionaries.Select(item => item.DictType)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var missing = group.Select(item => item.DictType.Trim())
                    .Where(item => !actualTypes.Contains(item))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (missing.Count > 0)
                    throw new InvalidOperationException(
                        $"Task '{operation.TaskKey}' reported success but dictionary verification found missing types for tenant {group.Key}: {string.Join(", ", missing)}.");
            }
            if (result.CreatedDictionaryCount < proposal.Dictionaries.Count)
                throw new InvalidOperationException(
                    $"Task '{operation.TaskKey}' reported only {result.CreatedDictionaryCount} saved dictionaries for {proposal.Dictionaries.Count} requested dictionaries.");
        }
    }

    private ICodeMasterAgentBatchOperationAdapter GetBatchAdapter(string operationKind)
        => _batchAdapters.TryGetValue(operationKind, out var adapter)
            ? adapter
            : throw new InvalidOperationException($"No batch operation adapter is registered for '{operationKind}'.");

    private async Task DemandBatchOperationPermissionAsync(long projectId, AiProjectWorkBatchOperation operation)
    {
        if (_batchAdapters.TryGetValue(operation.OperationKind, out var adapter))
        {
            await adapter.DemandPermissionAsync(projectId, operation);
            return;
        }

        var extension = _toolExtensions.FirstOrDefault(item => item.CanExecute(operation.OperationKind))
            ?? throw new InvalidOperationException($"No batch operation adapter or installed Agent extension is registered for '{operation.OperationKind}'.");
        var execution = CreateExtensionBatchExecution(projectId, operation);
        await extension.DemandPermissionAsync(execution);
    }

    private async Task<object> ExecuteBatchOperationAsync(long projectId, AiProjectWorkBatchOperation operation)
    {
        if (_batchAdapters.TryGetValue(operation.OperationKind, out var adapter))
            return await adapter.ExecuteAsync(projectId, operation);

        var extension = _toolExtensions.FirstOrDefault(item => item.CanExecute(operation.OperationKind))
            ?? throw new InvalidOperationException($"No batch operation adapter or installed Agent extension is registered for '{operation.OperationKind}'.");
        return await extension.ExecuteAsync(CreateExtensionBatchExecution(projectId, operation));
    }

    private AiToolExecution CreateExtensionBatchExecution(long projectId, AiProjectWorkBatchOperation operation)
    {
        if (string.IsNullOrWhiteSpace(operation.DefinitionJson))
            throw new InvalidOperationException($"Task '{operation.TaskKey}' has no extension operation definition.");
        var definitionJson = NormalizeExtensionDefinition(operation.TaskKey, operation.DefinitionJson);
        return new AiToolExecution
        {
            UserId = _currentUser.UserId,
            ProjectId = projectId,
            ToolName = operation.OperationKind,
            InputHash = HashText(definitionJson),
            InputJson = definitionJson,
            InputPayload = AiToolPayloadCodec.EncodeBinary(definitionJson),
            Status = "Executing"
        };
    }

    internal static string NormalizeExtensionDefinitionForTest(string taskKey, string definitionJson)
        => NormalizeExtensionDefinition(taskKey, definitionJson);

    private static string NormalizeExtensionDefinition(string taskKey, string definitionJson)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(definitionJson) as JsonObject
                ?? throw new InvalidOperationException($"Task '{taskKey}' extension definition must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Task '{taskKey}' has invalid extension JSON: {ex.Message}", ex);
        }

        if (root["summary"] is JsonValue summaryValue &&
            summaryValue.TryGetValue<string>(out var summary) &&
            !string.IsNullOrWhiteSpace(summary))
            return root.ToJsonString(JsonOptions);

        foreach (var propertyName in new[] { "description", "displayName", "pageName", "viewName", "name", "action" })
        {
            if (root[propertyName] is not JsonValue value ||
                !value.TryGetValue<string>(out var candidate) ||
                string.IsNullOrWhiteSpace(candidate))
                continue;
            root["summary"] = candidate.Trim();
            return root.ToJsonString(JsonOptions);
        }

        root["summary"] = taskKey;
        return root.ToJsonString(JsonOptions);
    }

    private static string NormalizeAutomaticRequestId(string requestId, long planId, long batchId)
    {
        var prefix = string.IsNullOrWhiteSpace(requestId) ? "plan-confirm" : requestId.Trim();
        var value = $"{prefix}-{planId}-{batchId}";
        return value.Length <= 64 ? value : value[^64..];
    }

    private static string HashText(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private async Task<List<string>> RepairKnownProjectChangeSetIssuesAsync(
        long projectId,
        ProjectChangeSetProposal proposal)
    {
        var repairs = new List<string>();

        foreach (var entity in proposal.Entities)
        {
            var removed = entity.Relations.RemoveAll(relation =>
                IsSelfRelation(entity.Name, 0, relation));
            if (removed == 0)
                continue;

            entity.IsTree = true;
            repairs.Add(
                $"Converted {entity.Name} hierarchy from an unsupported explicit self relation to IsTree=true with built-in ParentId/Ancestors fields.");
        }

        if (proposal.EntityUpdates.Count == 0)
            return repairs;

        var blueprint = await _blueprintService.GetProjectAsync(projectId);
        var existingById = blueprint.Modules
            .SelectMany(module => module.Entities)
            .ToDictionary(entity => entity.Id);
        foreach (var update in proposal.EntityUpdates)
        {
            if (!existingById.TryGetValue(update.EntityId, out var source))
                continue;

            var removed = update.NewRelations.RemoveAll(relation =>
                IsSelfRelation(source.Name, source.Id, relation));
            if (removed == 0)
                continue;

            update.IsTree = true;
            repairs.Add(
                $"Converted {source.Name} hierarchy from an unsupported explicit self relation to IsTree=true with built-in ParentId/Ancestors fields.");
        }

        return repairs;
    }

    private static bool IsSelfRelation(
        string sourceEntityName,
        long sourceEntityId,
        CreateEntityRelationProposal relation)
    {
        return (!string.IsNullOrWhiteSpace(relation.TargetEntityName) &&
                string.Equals(
                    sourceEntityName.Trim(),
                    relation.TargetEntityName.Trim(),
                    StringComparison.OrdinalIgnoreCase)) ||
               (sourceEntityId > 0 && relation.TargetEntityId == sourceEntityId);
    }

    private async Task<object> ExecuteDictionaryChangeAsync(AiToolExecution execution)
    {
        var proposal = DeserializeInput<ProjectDictionaryChangeProposal>(execution);
        ValidateDictionaryProposal(proposal);
        return await _projectService.SaveProjectDictionaryAsync(execution.ProjectId, new SaveProjectDictionaryDto
        {
            TenantId = proposal.TenantId,
            DictType = proposal.DictType,
            DictName = proposal.DictName,
            LangKey = proposal.LangKey,
            Status = proposal.Status,
            Sort = proposal.Sort,
            Remark = proposal.Remark,
            Items = proposal.Items.Select(item => new SaveProjectDictionaryItemDto
            {
                Label = item.Label,
                Value = item.Value,
                LangKey = item.LangKey,
                IsDefault = item.IsDefault,
                Status = item.Status,
                Sort = item.Sort,
                Remark = item.Remark
            }).ToList()
        });
    }

    private async Task<object> ExecuteUiPageEnhancementAsync(AiToolExecution execution)
    {
        var proposal = DeserializeInput<UiPageEnhancementProposal>(execution);
        await ValidateUiPageEnhancementAsync(execution.ProjectId, proposal);

        var action = new AiClientActionDto
        {
            Action = "enhanceUiPage",
            ProjectId = execution.ProjectId,
            EntityId = proposal.EntityId,
            TargetKind = proposal.TargetKind,
            Page = proposal.Page,
            PageType = proposal.PageType,
            Style = proposal.Style,
            Headline = proposal.Headline,
            Subtitle = proposal.Subtitle,
            Highlights = proposal.Highlights,
            PrimaryColor = proposal.PrimaryColor,
            SecondaryColor = proposal.SecondaryColor,
            ReplaceExistingDesign = proposal.ReplaceExistingDesign,
            Operations = proposal.Operations
        };
        var result = new UiPageEnhancementExecutionResult
        {
            ProjectId = execution.ProjectId,
            Summary = proposal.Summary,
            TargetKind = proposal.TargetKind,
            Page = string.Equals(proposal.TargetKind, "EntityPage", StringComparison.OrdinalIgnoreCase)
                ? proposal.PageType ?? string.Empty
                : proposal.Page ?? string.Empty,
            ClientActions = { action }
        };
        if (proposal.BuildAfterApply)
        {
            result.ClientActions.Add(new AiClientActionDto
            {
                Action = "buildProject",
                ProjectId = execution.ProjectId
            });
        }

        return result;
    }

    private async Task<object> ExecuteCreateEntityAsync(AiToolExecution execution)
    {
        var proposal = DeserializeInput<CreateEntityProposal>(execution);
        await ValidateEntityProposalAsync(execution.ProjectId, proposal);
        var id = await _entityService.CreateAsync(new CreateModuleEntityDto
        {
            ProjectId = execution.ProjectId,
            ModuleId = proposal.ModuleId,
            Name = proposal.Name.Trim(),
            Description = proposal.Description.Trim(),
            BusinessDescription = NormalizeOptional(proposal.BusinessDescription),
            HasPrimaryKey = proposal.HasPrimaryKey,
            TableName = NormalizeOptional(proposal.TableName),
            IsTree = proposal.IsTree,
            IsReadOnly = proposal.IsReadOnly,
            HasTenant = proposal.HasTenant,
            HasDataPermission = proposal.HasDataPermission,
            HasAudit = proposal.HasAudit,
            HasSoftDelete = proposal.HasSoftDelete,
            GenerateFrontend = proposal.GenerateFrontend,
            FrontendRoute = NormalizeOptional(proposal.FrontendRoute),
            MenuIcon = NormalizeOptional(proposal.MenuIcon),
            OrderNum = proposal.OrderNum,
            Fields = CodeMasterProjectChangeSetService.MapFields(proposal)
        });

        return new { id, projectId = execution.ProjectId, proposal.ModuleId, proposal.Name };
    }

    private async Task<AiToolExecution> CreatePendingExecutionAsync<T>(
        long conversationId,
        long projectId,
        string requestId,
        string toolName,
        T input)
    {
        var conversation = await _conversationRepository.GetQueryable()
            .Where(x => x.Id == conversationId &&
                        x.UserId == _currentUser.UserId &&
                        x.ProjectId == projectId &&
                        x.Status == "Active")
            .FirstAsync();

        if (conversation == null)
        {
            throw new UnauthorizedAccessException("Conversation context is invalid.");
        }

        var rawInputJson = JsonSerializer.Serialize(input, JsonOptions);
        var inputHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawInputJson)));
        var existing = await _executionRepository.GetQueryable()
            .Where(item => item.ConversationId == conversationId &&
                           item.UserId == _currentUser.UserId &&
                           item.RequestId == requestId &&
                           item.ToolName == toolName &&
                           item.InputHash == inputHash)
            .FirstAsync();
        if (existing != null)
            return existing;

        await EnsureSinglePendingApprovalAsync(conversationId, requestId, inputHash);

        var inputPayload = AiToolPayloadCodec.EncodeBinary(rawInputJson);

        var execution = new AiToolExecution
        {
            ConversationId = conversationId,
            UserId = _currentUser.UserId,
            ProjectId = projectId,
            RequestId = requestId,
            InputHash = inputHash,
            ToolName = toolName,
            InputJson = BuildStoredPayloadSummary(toolName, rawInputJson.Length, inputPayload.Length),
            InputPayload = inputPayload,
            Status = "PendingApproval",
            CreateUserId = _currentUser.UserId,
            CreateBy = _currentUser.UserName,
            CreateTime = DateTime.UtcNow
        };
        execution.Id = await _executionRepository.InsertAsync(execution);
        return execution;
    }

    private async Task<AiToolExecution> CreatePendingExtensionExecutionAsync(
        long conversationId,
        long projectId,
        string requestId,
        string toolName,
        object input)
    {
        var conversation = await _conversationRepository.GetQueryable()
            .Where(x => x.Id == conversationId &&
                        x.UserId == _currentUser.UserId &&
                        x.ProjectId == projectId &&
                        x.Status == "Active")
            .FirstAsync();
        if (conversation == null)
            throw new UnauthorizedAccessException("Conversation context is invalid.");

        var rawInputJson = JsonSerializer.Serialize(input, input.GetType(), JsonOptions);
        var inputHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawInputJson)));
        var existing = await _executionRepository.GetQueryable()
            .Where(item => item.ConversationId == conversationId &&
                           item.UserId == _currentUser.UserId &&
                           item.RequestId == requestId &&
                           item.ToolName == toolName &&
                           item.InputHash == inputHash)
            .FirstAsync();
        if (existing != null)
            return existing;

        await EnsureSinglePendingApprovalAsync(conversationId, requestId, inputHash);

        var inputPayload = AiToolPayloadCodec.EncodeBinary(rawInputJson);
        var execution = new AiToolExecution
        {
            ConversationId = conversationId,
            UserId = _currentUser.UserId,
            ProjectId = projectId,
            RequestId = requestId,
            InputHash = inputHash,
            ToolName = toolName,
            InputJson = BuildStoredPayloadSummary(toolName, rawInputJson.Length, inputPayload.Length),
            InputPayload = inputPayload,
            Status = "PendingApproval",
            CreateUserId = _currentUser.UserId,
            CreateBy = _currentUser.UserName,
            CreateTime = DateTime.UtcNow
        };
        execution.Id = await _executionRepository.InsertAsync(execution);
        return execution;
    }

    private async Task EnsureSinglePendingApprovalAsync(long conversationId, string requestId, string inputHash)
    {
        var pending = await _executionRepository.GetQueryable()
            .Where(item => item.ConversationId == conversationId &&
                           item.UserId == _currentUser.UserId &&
                           item.RequestId == requestId &&
                           item.Status == "PendingApproval")
            .OrderBy(item => item.CreateTime)
            .FirstAsync();
        if (pending == null || string.Equals(pending.InputHash, inputHash, StringComparison.OrdinalIgnoreCase))
            return;

        throw new InvalidOperationException(
            "当前变更方案尚未处理，请先完成当前方案后再提交新的变更方案。");
    }

    private async Task<AiToolExecution> GetOwnedExecutionAsync(long executionId)
    {
        var execution = await _executionRepository.GetQueryable()
            .Where(x => x.Id == executionId && x.UserId == _currentUser.UserId)
            .FirstAsync();
        if (execution == null)
        {
            throw new KeyNotFoundException("Tool execution was not found.");
        }

        var conversation = await _conversationRepository.GetQueryable()
            .Where(x => x.Id == execution.ConversationId && x.UserId == _currentUser.UserId && x.ProjectId == execution.ProjectId)
            .FirstAsync();
        if (conversation == null)
        {
            throw new UnauthorizedAccessException("Conversation context is invalid.");
        }

        return execution;
    }

    private async Task<ProjectDto> GetProjectAsync(long projectId)
    {
        var project = await _projectService.GetByIdAsync(projectId);
        return project ?? throw new KeyNotFoundException("The project bound to this conversation was not found.");
    }

    private async Task ValidateEntityProposalAsync(long projectId, CreateEntityProposal proposal)
    {
        if (string.IsNullOrWhiteSpace(proposal.Name) || string.IsNullOrWhiteSpace(proposal.Description))
        {
            throw new ArgumentException("Entity name and description are required.");
        }
        if (proposal.Description.Trim().Length > MetadataDescriptionNormalizer.DisplayNameMaxLength)
            throw new ArgumentException($"Entity description must be at most {MetadataDescriptionNormalizer.DisplayNameMaxLength} characters. Put detailed semantics in BusinessDescription.");
        if (proposal.BusinessDescription?.Trim().Length > MetadataDescriptionNormalizer.BusinessDescriptionMaxLength)
            throw new ArgumentException($"Entity BusinessDescription must be at most {MetadataDescriptionNormalizer.BusinessDescriptionMaxLength} characters.");

        var module = await _moduleService.GetByIdAsync(proposal.ModuleId);
        if (module == null || module.ProjectId != projectId)
        {
            throw new ArgumentException("The selected module does not belong to the conversation project.");
        }

        if (proposal.Fields.Count == 0)
        {
            throw new ArgumentException("At least one business field is required.");
        }

        foreach (var field in proposal.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name) || string.IsNullOrWhiteSpace(field.Description) || string.IsNullOrWhiteSpace(field.DataType))
            {
                throw new ArgumentException("Every field requires a name, description, and data type.");
            }
            if (field.Description.Trim().Length > MetadataDescriptionNormalizer.DisplayNameMaxLength)
                throw new ArgumentException($"Field '{field.Name}' description must be at most {MetadataDescriptionNormalizer.DisplayNameMaxLength} characters. Put detailed semantics in BusinessDescription.");
            if (field.BusinessDescription?.Trim().Length > MetadataDescriptionNormalizer.BusinessDescriptionMaxLength)
                throw new ArgumentException($"Field '{field.Name}' BusinessDescription must be at most {MetadataDescriptionNormalizer.BusinessDescriptionMaxLength} characters.");
        }
    }

    private async Task ValidateUiPageEnhancementAsync(long projectId, UiPageEnhancementProposal proposal)
    {
        if (string.IsNullOrWhiteSpace(proposal.Summary))
            throw new ArgumentException("UI enhancement summary is required.");
        if (!IsValidColor(proposal.PrimaryColor) || !IsValidColor(proposal.SecondaryColor))
            throw new ArgumentException("UI colors must use six-digit hex format, for example #2563eb.");

        if (string.Equals(proposal.TargetKind, "Scaffold", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(proposal.Page, "Login", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(proposal.Page, "Dashboard", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Scaffold page must be Login or Dashboard.");
            proposal.Page = string.Equals(proposal.Page, "Login", StringComparison.OrdinalIgnoreCase)
                ? "Login"
                : "Dashboard";
            proposal.TargetKind = "Scaffold";
            return;
        }

        if (!string.Equals(proposal.TargetKind, "EntityPage", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("TargetKind must be Scaffold or EntityPage.");
        proposal.TargetKind = "EntityPage";

        var entity = await _blueprintService.GetEntityAsync(projectId, proposal.EntityId, proposal.EntityName);
        proposal.EntityId = entity.Id;
        proposal.EntityName = entity.Name;
        var pageType = proposal.PageType?.Trim().ToLowerInvariant();
        if (pageType is not ("index" or "add" or "edit" or "detail"))
            throw new ArgumentException("Entity page type must be index, add, edit, or detail.");
        proposal.PageType = pageType;
        if (proposal.Operations.Count == 0)
            throw new ArgumentException("EntityPage enhancement requires at least one structured operation.");

        var page = await _blueprintService.GetUiPageAsync(projectId, entity.Id, null, pageType);
        var validGenIds = page.StableNodes.Select(item => item.GenId)
            .Concat(page.Fields.SelectMany(item => item.GenIds))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var operationTypes = new HashSet<string>(page.SupportedOperations, StringComparer.OrdinalIgnoreCase);

        foreach (var operation in proposal.Operations)
        {
            if (string.IsNullOrWhiteSpace(operation.Type) || !operationTypes.Contains(operation.Type))
                throw new ArgumentException($"Unsupported UI operation: {operation.Type}");

            if (!string.IsNullOrWhiteSpace(operation.TargetGenId) &&
                !validGenIds.Contains(operation.TargetGenId) &&
                !IsRelationGenId(operation.TargetGenId))
            {
                throw new ArgumentException($"Target genId is not part of the page blueprint: {operation.TargetGenId}");
            }

            if (operation.Type.Equals("Group", StringComparison.OrdinalIgnoreCase))
            {
                if (operation.MemberGenIds.Count == 0)
                    throw new ArgumentException("Group operation requires member genIds.");
                var invalidMember = operation.MemberGenIds.FirstOrDefault(item =>
                    !validGenIds.Contains(item) && !IsRelationGenId(item));
                if (invalidMember != null)
                    throw new ArgumentException($"Group member genId is not part of the page blueprint: {invalidMember}");
            }
        }
    }

    private static bool IsValidColor(string? value)
    {
        return string.IsNullOrWhiteSpace(value) || Regex.IsMatch(value.Trim(), "^#[0-9a-fA-F]{6}$");
    }

    private static bool IsRelationGenId(string value)
    {
        return value.StartsWith("gen_child_", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("gen_owned_", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateModuleProposal(CreateModuleProposal proposal)
    {
        if (string.IsNullOrWhiteSpace(proposal.ModuleName) || string.IsNullOrWhiteSpace(proposal.ModuleDescription))
        {
            throw new ArgumentException("Module name and description are required.");
        }

        CSharpModuleNameValidator.RequireValid(proposal.ModuleName);
    }

    private static void ValidateDictionaryProposal(ProjectDictionaryChangeProposal proposal)
    {
        if (string.IsNullOrWhiteSpace(proposal.Summary))
            throw new ArgumentException("Dictionary change summary is required.");
        if (proposal.TenantId < 0)
            throw new ArgumentException("Dictionary tenant id cannot be negative.");
        if (string.IsNullOrWhiteSpace(proposal.DictType))
            throw new ArgumentException("Dictionary code is required.");
        if (string.IsNullOrWhiteSpace(proposal.DictName))
            throw new ArgumentException("Dictionary name is required.");

        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in proposal.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Value) || string.IsNullOrWhiteSpace(item.Label))
                throw new ArgumentException("Every dictionary entry requires a value and label.");
            if (!values.Add(item.Value.Trim()))
                throw new ArgumentException($"Dictionary entry value '{item.Value.Trim()}' is duplicated.");
        }
    }

    private static T DeserializeInput<T>(AiToolExecution execution)
    {
        var json = ReadInputPayload(execution);
        if (!string.IsNullOrWhiteSpace(execution.InputHash))
        {
            var actualHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            if (!string.Equals(actualHash, execution.InputHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Agent tool input integrity validation failed.");
        }

        return DeserializeRequired<T>(json);
    }

    private static T DeserializeRequired<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("Tool input is missing.");
        }

        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new InvalidOperationException("Tool input is invalid.");
    }

    private static string ReadInputPayload(AiToolExecution execution) =>
        ReadStoredPayload(execution.InputPayload, execution.InputJson)
        ?? throw new InvalidOperationException("Tool input is missing.");

    private static string? ReadOutputPayload(AiToolExecution execution) =>
        ReadStoredPayload(execution.OutputPayload, execution.OutputJson);

    private static string? ReadStoredPayload(byte[]? binaryPayload, string? legacyPayload)
    {
        if (binaryPayload is { Length: > 0 })
            return AiToolPayloadCodec.DecodeBinary(binaryPayload);
        return string.IsNullOrWhiteSpace(legacyPayload)
            ? legacyPayload
            : AiToolPayloadCodec.Decode(legacyPayload);
    }

    private static void SetOutputPayload(AiToolExecution execution, string json)
    {
        var payload = AiToolPayloadCodec.EncodeBinary(json);
        execution.OutputPayload = payload;
        execution.OutputJson = BuildStoredPayloadSummary(execution.ToolName, json.Length, payload.Length);
    }

    private static string BuildStoredPayloadSummary(string toolName, int rawLength, int payloadLength) =>
        JsonSerializer.Serialize(new
        {
            storage = "gzip-binary-v1",
            toolName,
            rawLength,
            payloadLength
        }, JsonOptions);

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    private static bool IsCodeMasterOwnedFailure(Exception exception)
    {
        var normalized = exception.ToString().Replace('\\', '/');
        return normalized.Contains("CodeMaster.Application/Services/CodeGen/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("CodeMaster.Application/ScriptBuilder/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("CodeMaster.Application/VueBuilder/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("CodeMaster.Migrator/SeedData/System/TemplateModule.cs", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("CodeMaster.Agent/Services/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("CodeMaster.Agent/Tools/", StringComparison.OrdinalIgnoreCase);
    }

    internal static AiToolExecutionDto ToDto(AiToolExecution entity)
    {
        return new AiToolExecutionDto
        {
            Id = entity.Id,
            ConversationId = entity.ConversationId,
            ProjectId = entity.ProjectId,
            WorkPlanId = entity.WorkPlanId,
            WorkBatchId = entity.WorkBatchId,
            WorkTaskId = entity.WorkTaskId,
            RequestId = entity.RequestId,
            ToolName = entity.ToolName,
            InputJson = ReadStoredPayload(entity.InputPayload, entity.InputJson),
            OutputJson = ReadStoredPayload(entity.OutputPayload, entity.OutputJson),
            Status = entity.Status,
            ErrorMessage = entity.ErrorMessage,
            CreateTime = entity.CreateTime
        };
    }
}
