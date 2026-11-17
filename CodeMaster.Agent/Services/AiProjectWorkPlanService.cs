using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodeMaster.Agent.Contracts;
using CodeMaster.Core.Repositories;
using CodeMaster.Domain.Entities.Ai;
using Microsoft.Extensions.Logging;

namespace CodeMaster.Agent.Services;

public interface IAiProjectWorkPlanService
{
    Task<AiProjectWorkPlanDto?> GetActiveAsync(long projectId);
    Task<AiProjectWorkPlanDto?> GetActiveForConversationAsync(long projectId, long conversationId);
    Task<AiProjectWorkPlanDto?> GetForRequestAsync(long projectId, long conversationId, string requestId);
    Task<AiProjectWorkPlanDto> GetAsync(long planId);
    Task<AiProjectWorkPlanDto> UpsertAsync(UpsertAiProjectWorkPlanRequest input, CancellationToken cancellationToken = default);
    Task<AiProjectWorkPlanDto> ConfirmAsync(long planId, ConfirmAiProjectWorkPlanRequest input, CancellationToken cancellationToken = default);
    Task<AiProjectWorkPlanDto> PrepareResumeAsync(long planId, long conversationId, CancellationToken cancellationToken = default);
    Task<AiProjectWorkBatchProposal> CreateBatchAsync(ProposeAiProjectWorkBatchRequest input, CancellationToken cancellationToken = default);
    Task AttachToolExecutionAsync(long batchId, long executionId, CancellationToken cancellationToken = default);
    Task BeginBatchExecutionAsync(long batchId, string inputHash, CancellationToken cancellationToken = default);
    Task WaitForClientExecutionAsync(long batchId, CancellationToken cancellationToken = default);
    Task CompleteBatchAsync(long batchId, bool success, string? errorMessage = null, AiProjectFailureEvidenceInput? evidence = null, IReadOnlyCollection<string>? completedTaskKeys = null, CancellationToken cancellationToken = default);
    Task<AiProjectWorkPlanDto> RetryTaskAsync(long planId, long taskId, RetryAiProjectWorkTaskRequest input, CancellationToken cancellationToken = default);
    Task RecordSourceRepairAsync(long planId, long taskId, AiProjectSourceRepairResult result, CancellationToken cancellationToken = default);
    Task<AiProjectWorkPlanDto> RejectBatchAsync(long batchId, CancellationToken cancellationToken = default);
    Task<AiProjectWorkPlanDto> CancelAsync(long planId, bool superseded, CancellationToken cancellationToken = default);
    Task<AiProjectWorkPlanDto> SupersedeAsync(SupersedeAiProjectWorkPlanRequest input, CancellationToken cancellationToken = default);
}

internal sealed class AiProjectWorkPlanService : IAiProjectWorkPlanService
{
    private static readonly string[] SevenStageOrder =
    [
        "EntityFields", "Views", "Relations", "FieldControls", "ComplexPages", "ConsistencyCheck", "GenerateCode"
    ];

    private readonly IRepository<AiProjectWorkPlan> _planRepository;
    private readonly IRepository<AiProjectWorkStage> _stageRepository;
    private readonly IRepository<AiProjectWorkTask> _taskRepository;
    private readonly IRepository<AiProjectWorkTaskDependency> _dependencyRepository;
    private readonly IRepository<AiProjectWorkBatch> _batchRepository;
    private readonly IRepository<AiProjectWorkBatchTask> _batchTaskRepository;
    private readonly IRepository<AiProjectWorkAttempt> _attemptRepository;
    private readonly IRepository<AiProjectWorkCheckpoint> _checkpointRepository;
    private readonly IRepository<AiMessage> _messageRepository;
    private readonly ICodeMasterProjectBlueprintService? _blueprintService;
    private readonly IAiCurrentUser _currentUser;
    private readonly ILogger<AiProjectWorkPlanService>? _logger;

    public AiProjectWorkPlanService(
        IRepository<AiProjectWorkPlan> planRepository,
        IRepository<AiProjectWorkStage> stageRepository,
        IRepository<AiProjectWorkTask> taskRepository,
        IRepository<AiProjectWorkTaskDependency> dependencyRepository,
        IRepository<AiProjectWorkBatch> batchRepository,
        IRepository<AiProjectWorkBatchTask> batchTaskRepository,
        IRepository<AiProjectWorkAttempt> attemptRepository,
        IRepository<AiProjectWorkCheckpoint> checkpointRepository,
        IRepository<AiMessage> messageRepository,
        ICodeMasterProjectBlueprintService blueprintService,
        IAiCurrentUser currentUser,
        ILogger<AiProjectWorkPlanService>? logger = null)
    {
        _planRepository = planRepository;
        _stageRepository = stageRepository;
        _taskRepository = taskRepository;
        _dependencyRepository = dependencyRepository;
        _batchRepository = batchRepository;
        _batchTaskRepository = batchTaskRepository;
        _attemptRepository = attemptRepository;
        _checkpointRepository = checkpointRepository;
        _messageRepository = messageRepository;
        _blueprintService = blueprintService;
        _currentUser = currentUser;
        _logger = logger;
    }

    // ============================== reads ==============================

    public Task<AiProjectWorkPlanDto?> GetActiveAsync(long projectId)
        => FindActiveAsync(projectId, null);

    public Task<AiProjectWorkPlanDto?> GetActiveForConversationAsync(long projectId, long conversationId)
        => FindActiveAsync(projectId, conversationId);

    private async Task<AiProjectWorkPlanDto?> FindActiveAsync(long projectId, long? conversationId)
    {
        var query = _planRepository.GetQueryable()
            .Where(item => item.ProjectId == projectId &&
                           item.UserId == _currentUser.UserId &&
                           item.ActivitySlot == "active");
        if (conversationId.HasValue)
            query = query.Where(item => item.ConversationId == conversationId.Value);
        var plan = await query.OrderByDescending(item => item.LastActivityAt).FirstAsync();
        if (plan == null)
            return null;
        var hasTasks = await _taskRepository.GetQueryable().AnyAsync(item => item.PlanId == plan.Id);
        if (ShouldRetireEmptyPlanForTest(plan.Status, hasTasks ? 1 : 0))
        {
            await RetireAsync(plan);
            return null;
        }
        return await BuildProjectionAsync(plan);
    }

    public async Task<AiProjectWorkPlanDto?> GetForRequestAsync(long projectId, long conversationId, string requestId)
    {
        var active = await GetActiveForConversationAsync(projectId, conversationId);
        if (active != null)
            return active;

        var message = await _messageRepository.GetQueryable()
            .Where(item => item.ProjectId == projectId && item.ConversationId == conversationId &&
                           item.UserId == _currentUser.UserId && item.RequestId == requestId && item.Role == "user")
            .FirstAsync();
        if (message == null)
            return null;
        var plan = await _planRepository.GetQueryable()
            .Where(item => item.ProjectId == projectId && item.ConversationId == conversationId &&
                           item.UserId == _currentUser.UserId && item.CreateTime >= message.CreateTime &&
                           item.Status == AiProjectWorkPlanStatus.Completed)
            .OrderByDescending(item => item.CreateTime)
            .FirstAsync();
        return plan == null ? null : await BuildProjectionAsync(plan);
    }

    public async Task<AiProjectWorkPlanDto> GetAsync(long planId)
    {
        var plan = await LoadOwnedPlanAsync(planId);
        return await BuildProjectionAsync(plan);
    }

    private async Task<AiProjectWorkPlan> LoadOwnedPlanAsync(long planId)
        => await _planRepository.GetQueryable()
               .Where(item => item.Id == planId && item.UserId == _currentUser.UserId)
               .FirstAsync()
           ?? throw new KeyNotFoundException($"Work plan '{planId}' was not found.");

    // ============================== upsert ==============================

    public async Task<AiProjectWorkPlanDto> UpsertAsync(UpsertAiProjectWorkPlanRequest input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.ProjectId <= 0)
            throw new ArgumentException("Project id is required.");
        if (input.ConversationId <= 0)
            throw new ArgumentException("Conversation id is required.");
        await ValidatePrerequisiteReferencesAsync(input);

        var now = DateTime.UtcNow;
        AiProjectWorkPlan? plan = null;
        if (input.PlanId is > 0)
        {
            plan = await LoadOwnedPlanAsync(input.PlanId.Value);
            if (plan.ProjectId != input.ProjectId || plan.ConversationId != input.ConversationId)
                throw new InvalidOperationException("An existing work plan cannot be moved to another project or conversation.");
            if (!input.ExpectedStructureVersion.HasValue)
                throw new ArgumentException("Expected structure version is required when updating a work plan.");
            if (plan.StructureVersion != input.ExpectedStructureVersion.Value)
                throw new InvalidOperationException($"The work plan is stale. Expected version {input.ExpectedStructureVersion.Value}, current version is {plan.StructureVersion}.");
            if (IsTerminal(plan.Status))
                throw new InvalidOperationException("A terminal work plan cannot be changed.");
            plan.StructureVersion += 1;
        }
        else
        {
            var actives = await _planRepository.GetQueryable()
                .Where(item => item.ProjectId == input.ProjectId && item.ConversationId == input.ConversationId &&
                               item.UserId == _currentUser.UserId && item.ActivitySlot == "active")
                .OrderByDescending(item => item.LastActivityAt)
                .ToListAsync();

            AiProjectWorkPlan? adopted = null;
            if (actives.Count > 0)
            {
                var (decision, completedTaskCount, unfinishedTaskCount) = await EvaluateReuseAsync(actives[0]);
                if (decision == AiProjectWorkPlanReuseDecision.Refuse)
                {
                    throw AiProjectWorkPlanInProgressException.ForReplacement(
                        actives[0].Id, actives[0].Status, unfinishedTaskCount, completedTaskCount);
                }

                if (decision == AiProjectWorkPlanReuseDecision.Adopt)
                {
                    // Nothing of this plan has executed, so replacing its task rows destroys no
                    // committed work. Upgrading in place is what makes a second
                    // submit_business_plan call in the same turn idempotent instead of a second
                    // plan that silently supersedes the first one.
                    adopted = actives[0];
                    plan = adopted;
                    plan.StructureVersion += 1;
                }
            }

            foreach (var stale in actives)
            {
                if (adopted != null && stale.Id == adopted.Id)
                    continue;
                // Retiring a plan has to retire its batches too: a PendingApproval batch of a
                // superseded plan is still an executable snapshot and would replay stale metadata.
                await RetirePlanAsync(stale, AiProjectWorkPlanStatus.Superseded, now);
            }

            if (plan == null)
            {
                plan = new AiProjectWorkPlan
                {
                    TenantId = _currentUser.TenantId,
                    UserId = _currentUser.UserId,
                    ProjectId = input.ProjectId,
                    ConversationId = input.ConversationId,
                    PlanKey = string.IsNullOrWhiteSpace(input.PlanKey)
                        ? Truncate($"plan-{now:yyyyMMddHHmmss}-{Guid.NewGuid():N}", 60)
                        : input.PlanKey!.Substring(0, Math.Min(60, input.PlanKey.Length)),
                    ActivitySlot = "active",
                    StructureVersion = 1,
                    CreateBy = _currentUser.UserName,
                    CreateUserId = _currentUser.UserId,
                    CreateTime = now
                };
            }
        }

        plan.Title = string.IsNullOrWhiteSpace(input.Title) ? plan.Title : input.Title.Substring(0, Math.Min(200, input.Title.Length));
        plan.Summary = input.Summary;
        plan.RepairMode = Enum.TryParse<AiProjectRepairMode>(input.RepairMode, true, out var repairMode) ? repairMode : AiProjectRepairMode.MetadataOnly;
        plan.Status = input.Stages.Any(stage => stage.Tasks.Count > 0)
            ? AiProjectWorkPlanStatus.AwaitingConfirmation
            : AiProjectWorkPlanStatus.Draft;
        plan.LastActivityAt = now;
        plan.UpdateBy = _currentUser.UserName;
        plan.UpdateUserId = _currentUser.UserId;
        plan.UpdateTime = now;

        if (plan.Id == 0)
            plan.Id = await _planRepository.InsertAsync(plan);
        else
            await _planRepository.UpdateAsync(plan);

        // Replace (or partially preserve) stage/task/dependency rows.
        if (!input.PreserveUnspecified)
        {
            await _dependencyRepository.DeleteAsync(item => item.PlanId == plan.Id);
            await _taskRepository.DeleteAsync(item => item.PlanId == plan.Id);
            await _stageRepository.DeleteAsync(item => item.PlanId == plan.Id);
        }

        if (input.CancelTaskKeys.Count > 0)
        {
            var cancelKeys = input.CancelTaskKeys.Select(item => item.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var cancellable = await _taskRepository.GetQueryable()
                .Where(item => item.PlanId == plan.Id && cancelKeys.Contains(item.TaskKey))
                .ToListAsync();
            foreach (var task in cancellable.Where(item => item.Status is not AiProjectWorkTaskStatus.Completed))
            {
                task.Status = AiProjectWorkTaskStatus.Cancelled;
                task.UpdateTime = now;
                await _taskRepository.UpdateAsync(task);
            }
        }

        var taskKeyToId = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var insertedDependencies = new List<(string TaskKey, string DependsOnKey)>();
        var order = 0;
        foreach (var stageInput in input.Stages)
        {
            if (input.PreserveUnspecified)
            {
                var existingStage = await _stageRepository.GetQueryable()
                    .Where(item => item.PlanId == plan.Id && item.StageKey == stageInput.StageKey)
                    .FirstAsync();
                if (existingStage != null)
                {
                    await _dependencyRepository.DeleteAsync(item => item.PlanId == plan.Id);
                    var oldTasks = await _taskRepository.GetQueryable().Where(item => item.StageId == existingStage.Id).ToListAsync();
                    foreach (var old in oldTasks)
                    {
                        taskKeyToId.Remove(old.TaskKey);
                        await _taskRepository.DeleteAsync(old.Id);
                    }
                }
            }

            var stage = new AiProjectWorkStage
            {
                TenantId = _currentUser.TenantId,
                PlanId = plan.Id,
                StageKey = stageInput.StageKey,
                Kind = Enum.TryParse<AiProjectWorkStageKind>(stageInput.Kind, true, out var kind) ? kind : AiProjectWorkStageKind.Custom,
                Title = stageInput.Title,
                Summary = stageInput.Summary,
                Status = AiProjectWorkStageStatus.Pending,
                SortOrder = stageInput.SortOrder ?? ++order * 100,
                GenerationMode = stageInput.GenerationMode,
                BuildAfterStage = stageInput.BuildAfterStage,
                CreateBy = _currentUser.UserName,
                CreateUserId = _currentUser.UserId,
                CreateTime = now,
                UpdateBy = _currentUser.UserName,
                UpdateUserId = _currentUser.UserId,
                UpdateTime = now
            };
            stage.Id = await _stageRepository.InsertAsync(stage);

            var taskOrder = 0;
            foreach (var taskInput in stageInput.Tasks)
            {
                var task = new AiProjectWorkTask
                {
                    TenantId = _currentUser.TenantId,
                    PlanId = plan.Id,
                    StageId = stage.Id,
                    TaskKey = taskInput.TaskKey,
                    Title = taskInput.Title,
                    Summary = taskInput.Summary,
                    OperationKind = taskInput.OperationKind,
                    Status = AiProjectWorkTaskStatus.Pending,
                    ExecutionLocation = Enum.TryParse<AiProjectWorkExecutionLocation>(taskInput.ExecutionLocation, true, out var location) ? location : AiProjectWorkExecutionLocation.Client,
                    SafetyClass = Enum.TryParse<AiProjectWorkSafetyClass>(taskInput.SafetyClass, true, out var safety) ? safety : AiProjectWorkSafetyClass.Metadata,
                    DefinitionJson = taskInput.Definition.HasValue ? taskInput.Definition.Value.GetRawText() : null,
                    SemanticReferencesJson = taskInput.SemanticReferences.Count > 0
                        ? JsonSerializer.Serialize(taskInput.SemanticReferences)
                        : null,
                    SortOrder = taskInput.SortOrder ?? ++taskOrder * 100,
                    RetryLimit = taskInput.RetryLimit is > 0 ? taskInput.RetryLimit : 3,
                    AttemptCount = 0,
                    LastFailureCategory = AiProjectFailureCategory.None,
                    RequiresGeneration = taskInput.RequiresGeneration,
                    RequiresBuild = taskInput.RequiresBuild,
                    CreateBy = _currentUser.UserName,
                    CreateUserId = _currentUser.UserId,
                    CreateTime = now,
                    UpdateBy = _currentUser.UserName,
                    UpdateUserId = _currentUser.UserId,
                    UpdateTime = now
                };
                task.Id = await _taskRepository.InsertAsync(task);
                if (!string.IsNullOrWhiteSpace(task.TaskKey))
                    taskKeyToId[task.TaskKey] = task.Id;
                foreach (var dependsOn in taskInput.DependsOnTaskKeys ?? [])
                    insertedDependencies.Add((task.TaskKey, dependsOn));
            }
        }

        foreach (var (taskKey, dependsOnKey) in insertedDependencies)
        {
            if (!taskKeyToId.TryGetValue(taskKey, out var taskId) || !taskKeyToId.TryGetValue(dependsOnKey, out var dependsOnId))
                continue;
            await _dependencyRepository.InsertAsync(new AiProjectWorkTaskDependency
            {
                TenantId = _currentUser.TenantId,
                PlanId = plan.Id,
                TaskId = taskId,
                DependsOnTaskId = dependsOnId,
                CreateBy = _currentUser.UserName,
                CreateUserId = _currentUser.UserId,
                CreateTime = now
            });
        }

        await RecalculateReadinessAsync(plan.Id, now);
        plan.LastActivityAt = now;
        await _planRepository.UpdateAsync(plan);
        return await BuildProjectionAsync(plan);
    }

    private async Task ValidatePrerequisiteReferencesAsync(UpsertAiProjectWorkPlanRequest input)
    {
        if (_blueprintService == null)
            return;
        var blueprint = await _blueprintService.GetProjectAsync(input.ProjectId);
        var moduleNames = blueprint.Modules.Select(item => item.ModuleName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entityNames = blueprint.Modules.SelectMany(item => item.Entities).Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A single business plan may create the module/entity and reference it
        // from later tasks. Include names declared in executable definitions so
        // validation does not reject valid same-plan dependencies as missing.
        foreach (var task in input.Stages.SelectMany(stage => stage.Tasks))
        {
            if (task.Definition is not { ValueKind: JsonValueKind.Object } definition)
                continue;
            AddDeclaredNames(definition, "modules", "moduleName", moduleNames);
            AddDeclaredNames(definition, "modules", "name", moduleNames);
            AddDeclaredNames(definition, "entities", "name", entityNames);
            AddDeclaredNames(definition, "entities", "entityName", entityNames);
        }
        foreach (var stage in input.Stages)
        {
            foreach (var task in stage.Tasks)
            {
                foreach (var reference in task.SemanticReferences ?? [])
                {
                    var value = reference?.Trim() ?? string.Empty;
                    if (value.Length == 0 || string.Equals(value, "project", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var separator = value.IndexOf(':');
                    if (separator <= 0)
                        continue;
                    var kind = value[..separator];
                    var name = value[(separator + 1)..];
                    if (string.Equals(kind, "module", StringComparison.OrdinalIgnoreCase) && !moduleNames.Contains(name))
                        throw new ArgumentException($"Task '{task.TaskKey}' references semantic reference '{value}' that does not exist in the current project metadata.");
                    if (string.Equals(kind, "entity", StringComparison.OrdinalIgnoreCase) && !entityNames.Contains(name))
                        throw new ArgumentException($"Task '{task.TaskKey}' references semantic reference '{value}' that does not exist in the current project metadata.");
                }
            }
        }

        static void AddDeclaredNames(
            JsonElement root,
            string arrayName,
            string propertyName,
            HashSet<string> target)
        {
            if (!root.TryGetProperty(arrayName, out var array) || array.ValueKind != JsonValueKind.Array)
                return;
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty(propertyName, out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    target.Add(value.GetString()!.Trim());
                }
            }
        }
    }

    // ============================== confirm / resume ==============================

    public async Task<AiProjectWorkPlanDto> ConfirmAsync(long planId, ConfirmAiProjectWorkPlanRequest input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var plan = await LoadOwnedPlanAsync(planId);
        var projection = await BuildProjectionAsync(plan);
        var canRetryStart = plan.Status == AiProjectWorkPlanStatus.Active &&
                            !plan.ActiveBatchId.HasValue &&
                            projection.CompletedTasks == 0;
        if (plan.Status != AiProjectWorkPlanStatus.AwaitingConfirmation && !canRetryStart)
            return projection;
        if (input.ExpectedStructureVersion != plan.StructureVersion)
            throw new InvalidOperationException($"The work plan version changed. Expected {input.ExpectedStructureVersion}, current {plan.StructureVersion}.");
        if (string.IsNullOrWhiteSpace(input.ContentHash) ||
            !string.Equals(input.ContentHash.Trim(), projection.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The work plan content changed. Reload the plan and confirm the current revision.");

        var now = DateTime.UtcNow;
        plan.Status = AiProjectWorkPlanStatus.Active;
        plan.StartedAt ??= now;
        plan.LastActivityAt = now;
        plan.UpdateTime = now;
        await _planRepository.UpdateAsync(plan);
        await RecalculateReadinessAsync(plan.Id, now);
        return await BuildProjectionAsync(plan);
    }

    public async Task<AiProjectWorkPlanDto> PrepareResumeAsync(long planId, long conversationId, CancellationToken cancellationToken = default)
    {
        var plan = await LoadOwnedPlanAsync(planId);
        if (plan.ConversationId != conversationId)
            throw new InvalidOperationException("The work plan belongs to another conversation.");
        if (IsTerminal(plan.Status))
            throw new InvalidOperationException("A terminal work plan cannot be resumed.");
        return await BuildProjectionAsync(plan);
    }

    // ============================== batches ==============================

    public async Task<AiProjectWorkBatchProposal> CreateBatchAsync(ProposeAiProjectWorkBatchRequest input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var plan = await LoadOwnedPlanAsync(input.PlanId);
        if (IsTerminal(plan.Status))
            throw new InvalidOperationException("A terminal work plan cannot create a batch.");
        if (plan.Status == AiProjectWorkPlanStatus.AwaitingConfirmation)
            throw new InvalidOperationException("The work plan has not been confirmed yet. Confirm the complete plan first.");
        if (plan.ActiveBatchId.HasValue)
            throw new InvalidOperationException("The work plan already has an active batch. Complete or reject it first.");
        if (input.ExpectedStructureVersion.HasValue && input.ExpectedStructureVersion.Value != plan.StructureVersion)
            throw new InvalidOperationException($"The work plan is stale. Expected version {input.ExpectedStructureVersion.Value}, current version is {plan.StructureVersion}.");

        var readyQuery = _taskRepository.GetQueryable()
            .Where(item => item.PlanId == plan.Id && item.Status == AiProjectWorkTaskStatus.Ready);
        if (input.TaskKeys.Count > 0)
        {
            var keys = input.TaskKeys.Select(item => item.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            readyQuery = readyQuery.Where(item => keys.Contains(item.TaskKey));
        }
        var readyTasks = (await readyQuery.ToListAsync())
            .OrderBy(item => item.SortOrder)
            .Take(input.MaxTasks is > 0 ? input.MaxTasks : 20)
            .ToList();
        if (readyTasks.Count == 0)
            throw new InvalidOperationException("No ready work plan tasks are available for a batch.");

        var now = DateTime.UtcNow;
        var stages = await _stageRepository.GetQueryable().Where(item => item.PlanId == plan.Id).ToListAsync();
        var firstStage = stages.FirstOrDefault(item => item.Id == readyTasks[0].StageId);
        var inputHash = ComputeHash(string.Join("|", readyTasks.Select(item => $"{item.TaskKey}:{item.PayloadHash ?? item.DefinitionJson ?? string.Empty}")));
        var batchKey = BuildBatchKey(plan.Id, now);
        var batch = new AiProjectWorkBatch
        {
            TenantId = _currentUser.TenantId,
            PlanId = plan.Id,
            StageId = firstStage?.Id,
            BatchKey = batchKey,
            Title = readyTasks.Count == 1 ? readyTasks[0].Title : $"{readyTasks[0].Title} +{readyTasks.Count - 1}",
            Summary = $"Batch of {readyTasks.Count} ready task(s).",
            Status = AiProjectWorkBatchStatus.PendingApproval,
            StructureVersion = plan.StructureVersion,
            InputHash = inputHash,
            ExecutionLocation = readyTasks.Any(item => item.ExecutionLocation == AiProjectWorkExecutionLocation.Client)
                ? AiProjectWorkExecutionLocation.Client
                : AiProjectWorkExecutionLocation.Server,
            SafetyClass = AiProjectWorkSafetyClass.Metadata,
            AttemptCount = 0,
            CreateBy = _currentUser.UserName,
            CreateUserId = _currentUser.UserId,
            CreateTime = now,
            UpdateBy = _currentUser.UserName,
            UpdateUserId = _currentUser.UserId,
            UpdateTime = now
        };
        batch.Id = await _batchRepository.InsertAsync(batch);
        var sortOrder = 0;
        foreach (var task in readyTasks)
        {
            await _batchTaskRepository.InsertAsync(new AiProjectWorkBatchTask
            {
                TenantId = _currentUser.TenantId,
                PlanId = plan.Id,
                BatchId = batch.Id,
                TaskId = task.Id,
                SortOrder = ++sortOrder,
                CreateBy = _currentUser.UserName,
                CreateUserId = _currentUser.UserId,
                CreateTime = now
            });
        }
        plan.ActiveBatchId = batch.Id;
        plan.LastActivityAt = now;
        await _planRepository.UpdateAsync(plan);

        return new AiProjectWorkBatchProposal
        {
            PlanId = plan.Id,
            StructureVersion = plan.StructureVersion,
            InputHash = inputHash,
            Summary = batch.Summary ?? string.Empty,
            Batch = await MapBatchAsync(batch),
            Operations = readyTasks.Select(item => new AiProjectWorkBatchOperation
            {
                TaskId = item.Id,
                TaskKey = item.TaskKey,
                OperationKind = item.OperationKind,
                DefinitionJson = item.DefinitionJson,
                PayloadHash = item.PayloadHash ?? string.Empty
            }).ToList()
        };
    }

    public async Task AttachToolExecutionAsync(long batchId, long executionId, CancellationToken cancellationToken = default)
    {
        var batch = await _batchRepository.GetQueryable().Where(item => item.Id == batchId).FirstAsync()
            ?? throw new KeyNotFoundException($"Work batch '{batchId}' was not found.");
        batch.ToolExecutionId = executionId;
        batch.UpdateTime = DateTime.UtcNow;
        await _batchRepository.UpdateAsync(batch);
    }

    public async Task BeginBatchExecutionAsync(long batchId, string inputHash, CancellationToken cancellationToken = default)
    {
        var batch = await LoadOwnedBatchAsync(batchId);
        var now = DateTime.UtcNow;
        batch.Status = AiProjectWorkBatchStatus.Executing;
        batch.InputHash = string.IsNullOrWhiteSpace(inputHash) ? batch.InputHash : inputHash;
        batch.ApprovedAt ??= now;
        batch.UpdateTime = now;
        await _batchRepository.UpdateAsync(batch);
        foreach (var task in await BatchTasksAsync(batch.Id))
        {
            task.Status = AiProjectWorkTaskStatus.Applying;
            task.UpdateTime = now;
            await _taskRepository.UpdateAsync(task);
        }
    }

    public async Task WaitForClientExecutionAsync(long batchId, CancellationToken cancellationToken = default)
    {
        var batch = await LoadOwnedBatchAsync(batchId);
        if (batch.ExecutionLocation != AiProjectWorkExecutionLocation.Client)
            return;
        batch.Status = AiProjectWorkBatchStatus.WaitingClientExecution;
        batch.UpdateTime = DateTime.UtcNow;
        await _batchRepository.UpdateAsync(batch);
    }

    public async Task CompleteBatchAsync(long batchId, bool success, string? errorMessage = null, AiProjectFailureEvidenceInput? evidence = null, IReadOnlyCollection<string>? completedTaskKeys = null, CancellationToken cancellationToken = default)
    {
        var batch = await LoadOwnedBatchAsync(batchId);
        var now = DateTime.UtcNow;
        batch.Status = success ? AiProjectWorkBatchStatus.Completed : AiProjectWorkBatchStatus.Failed;
        batch.ErrorMessage = success ? null : errorMessage;
        batch.CompletedAt = now;
        batch.UpdateTime = now;
        await _batchRepository.UpdateAsync(batch);

        foreach (var task in await BatchTasksAsync(batch.Id))
        {
            if (success)
            {
                task.Status = AiProjectWorkTaskStatus.Completed;
                task.LastDiagnosis = null;
            }
            else if (ShouldMarkTaskCompletedOnBatchFailure(completedTaskKeys, task.TaskKey))
            {
                // Its operation already committed. Marking it Failed anyway is what forced the
                // repair flow to re-execute work that had succeeded, which replays create-intent
                // change sets against rows that now exist ("... already exists", once per entity).
                task.Status = AiProjectWorkTaskStatus.Completed;
                task.LastDiagnosis = null;
            }
            else
            {
                task.Status = AiProjectWorkTaskStatus.Failed;
                task.LastDiagnosis = errorMessage;
                task.LastFailureCategory = AiProjectFailureCategory.PlanOrMetadata;
                task.LastFailureSignature = ComputeHash(errorMessage ?? string.Empty);
                task.AttemptCount += 1;
            }
            task.UpdateTime = now;
            await _taskRepository.UpdateAsync(task);
        }

        var plan = await LoadOwnedPlanAsync(batch.PlanId);
        plan.ActiveBatchId = null;
        plan.LastActivityAt = now;
        await RecalculateReadinessAsync(plan.Id, now);
        var tasks = await _taskRepository.GetQueryable().Where(item => item.PlanId == plan.Id).ToListAsync();
        if (tasks.Count > 0 && tasks.All(item => item.Status is AiProjectWorkTaskStatus.Completed or AiProjectWorkTaskStatus.Cancelled))
        {
            plan.Status = AiProjectWorkPlanStatus.Completed;
            plan.CompletedAt = now;
        }
        else if (!success && plan.Status == AiProjectWorkPlanStatus.Active)
        {
            plan.Status = AiProjectWorkPlanStatus.Failed;
        }
        await _planRepository.UpdateAsync(plan);
    }

    public async Task<AiProjectWorkPlanDto> RejectBatchAsync(long batchId, CancellationToken cancellationToken = default)
    {
        var batch = await LoadOwnedBatchAsync(batchId);
        var now = DateTime.UtcNow;
        batch.Status = AiProjectWorkBatchStatus.Rejected;
        batch.UpdateTime = now;
        await _batchRepository.UpdateAsync(batch);
        foreach (var task in await BatchTasksAsync(batch.Id))
        {
            if (task.Status is AiProjectWorkTaskStatus.Applying or AiProjectWorkTaskStatus.WaitingApproval)
            {
                task.Status = AiProjectWorkTaskStatus.Ready;
                task.UpdateTime = now;
                await _taskRepository.UpdateAsync(task);
            }
        }
        var plan = await LoadOwnedPlanAsync(batch.PlanId);
        plan.ActiveBatchId = null;
        plan.LastActivityAt = now;
        await _planRepository.UpdateAsync(plan);
        return await BuildProjectionAsync(plan);
    }

    public async Task<AiProjectWorkPlanDto> RetryTaskAsync(long planId, long taskId, RetryAiProjectWorkTaskRequest input, CancellationToken cancellationToken = default)
    {
        var plan = await LoadOwnedPlanAsync(planId);
        var task = await _taskRepository.GetQueryable().Where(item => item.Id == taskId && item.PlanId == planId).FirstAsync()
            ?? throw new KeyNotFoundException($"Work task '{taskId}' was not found.");
        if (task.Status != AiProjectWorkTaskStatus.Failed)
            throw new InvalidOperationException("Only failed tasks can be retried.");
        if (!string.IsNullOrWhiteSpace(input?.FailureSignature) &&
            !string.Equals(input.FailureSignature.Trim(), task.LastFailureSignature, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The failure signature changed. Reload the task list before retrying.");
        var now = DateTime.UtcNow;
        task.Status = AiProjectWorkTaskStatus.Ready;
        task.LastDiagnosis = null;
        task.UpdateTime = now;
        await _taskRepository.UpdateAsync(task);
        plan.Status = AiProjectWorkPlanStatus.Active;
        plan.LastActivityAt = now;
        await _planRepository.UpdateAsync(plan);
        return await BuildProjectionAsync(plan);
    }

    public async Task RecordSourceRepairAsync(long planId, long taskId, AiProjectSourceRepairResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var plan = await LoadOwnedPlanAsync(planId);
        var task = await _taskRepository.GetQueryable()
            .Where(item => item.Id == taskId && item.PlanId == planId)
            .FirstAsync()
            ?? throw new KeyNotFoundException($"Work task '{taskId}' was not found.");

        var batchLink = await _batchTaskRepository.GetQueryable()
            .Where(item => item.PlanId == planId && item.TaskId == taskId)
            .OrderByDescending(item => item.CreateTime)
            .FirstAsync();
        var batchId = batchLink?.BatchId ?? 0;
        var attemptNumber = Math.Max(1, task.AttemptCount + 1);
        var now = DateTime.UtcNow;
        var success = result.Applied && result.ValidationPassed;
        var repairJson = JsonSerializer.Serialize(result);
        var attempt = new AiProjectWorkAttempt
        {
            TenantId = _currentUser.TenantId,
            PlanId = planId,
            BatchId = batchId,
            TaskId = taskId,
            AttemptNumber = attemptNumber,
            Status = success ? AiProjectWorkAttemptStatus.Completed : AiProjectWorkAttemptStatus.Failed,
            ExecutorMode = "CodeMasterSourceRepair",
            FailureCategory = AiProjectFailureCategory.GeneratorOrTemplate,
            FailureSignature = string.IsNullOrWhiteSpace(result.FailureSignature) ? task.LastFailureSignature : result.FailureSignature,
            RepairJson = repairJson,
            ResultJson = repairJson,
            ErrorMessage = success ? null : result.Failure,
            StartedAt = now,
            CompletedAt = now,
            CreateBy = _currentUser.UserName,
            CreateUserId = _currentUser.UserId,
            CreateTime = now
        };
        attempt.Id = await _attemptRepository.InsertAsync(attempt);

        var checkpoint = new AiProjectWorkCheckpoint
        {
            TenantId = _currentUser.TenantId,
            PlanId = planId,
            BatchId = batchId,
            TaskId = taskId,
            AttemptId = attempt.Id,
            Kind = AiProjectWorkCheckpointKind.Repair,
            CheckpointKey = string.IsNullOrWhiteSpace(result.RepairId) ? $"source-repair-{attempt.Id}" : result.RepairId,
            ActionName = "apply_test_source_repair", // retired action name, kept for audit history
            Status = success ? AiProjectWorkCheckpointStatus.Completed : AiProjectWorkCheckpointStatus.Failed,
            SortOrder = attemptNumber,
            InputHash = task.PayloadHash,
            ResultJson = repairJson,
            DiagnosticOutput = result.ValidationOutput ?? result.Failure,
            StartedAt = now,
            CompletedAt = now,
            CreateBy = _currentUser.UserName,
            CreateUserId = _currentUser.UserId,
            CreateTime = now
        };
        checkpoint.Id = await _checkpointRepository.InsertAsync(checkpoint);

        task.AttemptCount = attemptNumber;
        task.LastFailureSignature = result.FailureSignature;
        task.LastFailureCategory = AiProjectFailureCategory.GeneratorOrTemplate;
        task.LastDiagnosis = success ? null : result.Failure ?? result.ValidationOutput;
        task.Status = success ? AiProjectWorkTaskStatus.Ready : AiProjectWorkTaskStatus.Failed;
        task.UpdateTime = now;
        await _taskRepository.UpdateAsync(task);

        plan.Status = success ? AiProjectWorkPlanStatus.Active : AiProjectWorkPlanStatus.Failed;
        plan.LastActivityAt = now;
        plan.UpdateTime = now;
        await _planRepository.UpdateAsync(plan);
    }

    public async Task<AiProjectWorkPlanDto> CancelAsync(long planId, bool superseded, CancellationToken cancellationToken = default)
    {
        var plan = await LoadOwnedPlanAsync(planId);

        // Cancelling is the escape hatch, so it must stay available — except while an operation
        // is literally running. A batch mid-execution has already handed work to the change-set
        // service; marking it Cancelled underneath would leave the result unattributable and the
        // next approval would replay the same snapshot. Everything else (PendingApproval,
        // Approved, WaitingClientExecution) has written nothing yet and can be dropped safely.
        var running = await _batchRepository.GetQueryable()
            .Where(item => item.PlanId == planId)
            .ToListAsync();
        if (running.Any(item => item.Status == AiProjectWorkBatchStatus.Executing))
            throw AiProjectWorkPlanInProgressException.ForRunningBatch(plan.Id, plan.Status);

        await RetirePlanAsync(
            plan,
            superseded ? AiProjectWorkPlanStatus.Superseded : AiProjectWorkPlanStatus.Cancelled,
            DateTime.UtcNow);
        return await BuildProjectionAsync(plan);
    }

    public Task<AiProjectWorkPlanDto> SupersedeAsync(SupersedeAiProjectWorkPlanRequest input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return CancelAsync(input.PlanId, superseded: true, cancellationToken);
    }

    // ============================== internals ==============================

    private static bool IsTerminal(AiProjectWorkPlanStatus status)
        => status is AiProjectWorkPlanStatus.Completed or AiProjectWorkPlanStatus.Cancelled or AiProjectWorkPlanStatus.Superseded;

    /// <summary>
    /// A batch must die with its plan unless it already produced a result. Kept as a named rule
    /// (instead of an inline filter) so the "retire the plan but leave a PendingApproval batch
    /// behind" regression stays directly testable.
    /// </summary>
    internal static bool ShouldCancelBatchWithPlan(AiProjectWorkBatchStatus status)
        => status != AiProjectWorkBatchStatus.Completed;

    /// <summary>
    /// A batch is "in flight" while it can still write metadata: created but not yet decided,
    /// approved but not started, or actually executing. Failed/Rejected/Stale/Cancelled batches
    /// are history and never block a re-plan.
    /// </summary>
    internal static bool IsBatchInFlight(AiProjectWorkBatchStatus status)
        => status is AiProjectWorkBatchStatus.Draft
            or AiProjectWorkBatchStatus.PendingApproval
            or AiProjectWorkBatchStatus.Approved
            or AiProjectWorkBatchStatus.WaitingClientExecution
            or AiProjectWorkBatchStatus.Executing;

    /// <summary>
    /// Decides what "create a plan" means when another plan is already active in the same
    /// conversation. Kept as a pure rule so the three outcomes stay directly testable.
    /// </summary>
    /// <remarks>
    /// <para><b>Refuse</b> — an in-flight batch exists, or some tasks already committed while
    /// others did not. Both cases lose real work: the batch is an executable snapshot compiled
    /// against an older blueprint (approving it later replays stale metadata and produces a wall
    /// of "already exists"), and deleting committed task rows makes the repair flow re-run
    /// create-intent change sets against rows that exist.</para>
    /// <para><b>Adopt</b> — nothing has executed yet, so replacing the task rows is lossless.
    /// This is what makes a second plan submission in one turn an in-place upgrade instead of a
    /// second plan that silently supersedes the first.</para>
    /// <para><b>Replace</b> — the plan is empty or fully finished; a new round of work is
    /// legitimate and the old rows carry no unfinished state.</para>
    /// </remarks>
    internal static AiProjectWorkPlanReuseDecision DecideReuseForNewPlan(
        bool hasInFlightBatch, int completedTaskCount, int unfinishedTaskCount)
    {
        if (hasInFlightBatch)
            return AiProjectWorkPlanReuseDecision.Refuse;

        if (completedTaskCount > 0)
        {
            return unfinishedTaskCount > 0
                ? AiProjectWorkPlanReuseDecision.Refuse
                : AiProjectWorkPlanReuseDecision.Replace;
        }

        return unfinishedTaskCount > 0
            ? AiProjectWorkPlanReuseDecision.Adopt
            : AiProjectWorkPlanReuseDecision.Replace;
    }

    private async Task<(AiProjectWorkPlanReuseDecision Decision, int CompletedTasks, int UnfinishedTasks)> EvaluateReuseAsync(AiProjectWorkPlan plan)
    {
        var batches = await _batchRepository.GetQueryable()
            .Where(item => item.PlanId == plan.Id)
            .ToListAsync();
        var tasks = await _taskRepository.GetQueryable()
            .Where(item => item.PlanId == plan.Id)
            .ToListAsync();
        var completed = tasks.Count(item => item.Status == AiProjectWorkTaskStatus.Completed);
        var unfinished = tasks.Count(item => item.Status is not (AiProjectWorkTaskStatus.Completed or AiProjectWorkTaskStatus.Cancelled));
        var decision = DecideReuseForNewPlan(
            batches.Any(item => IsBatchInFlight(item.Status)),
            completed,
            unfinished);
        return (decision, completed, unfinished);
    }

    /// <summary>
    /// True when this task's operation already committed before a later task in the same batch
    /// failed. A batch is not one transaction — every operation commits on its own — so the tasks
    /// that succeeded must stay Completed. Reporting them as Failed makes the repair flow re-run
    /// them, and re-running a create-intent change set against rows that already exist is exactly
    /// what produced a dozen "already exists" errors from one failed batch.
    /// </summary>
    internal static bool ShouldMarkTaskCompletedOnBatchFailure(
        IReadOnlyCollection<string>? completedTaskKeys,
        string? taskKey)
    {
        if (completedTaskKeys == null || completedTaskKeys.Count == 0 || string.IsNullOrWhiteSpace(taskKey))
            return false;
        return completedTaskKeys.Contains(taskKey, StringComparer.OrdinalIgnoreCase);
    }

    private async Task RetireAsync(AiProjectWorkPlan plan)
        => await RetirePlanAsync(plan, AiProjectWorkPlanStatus.Superseded, DateTime.UtcNow);

    /// <summary>
    /// Terminates a plan together with everything still hanging off it.
    ///
    /// Batches belong to the plan, not to the conversation. A batch left in PendingApproval after
    /// its plan was retired is still a fully executable change-set snapshot: it keeps showing up
    /// in the pending-approval list, and approving it replays metadata compiled against an older
    /// blueprint — which is exactly how one superseded batch produced a dozen "already exists"
    /// errors at once. Retiring a plan therefore has to take its batches with it, and that is the
    /// only reason this is more than a single UpdateAsync on the plan row.
    /// </summary>
    private async Task RetirePlanAsync(AiProjectWorkPlan plan, AiProjectWorkPlanStatus status, DateTime now)
    {
        plan.Status = status;
        plan.ActivitySlot = $"terminal:{plan.PlanKey}";
        plan.CompletedAt = now;
        plan.LastActivityAt = now;
        plan.UpdateTime = now;
        await _planRepository.UpdateAsync(plan);

        var batches = await _batchRepository.GetQueryable()
            .Where(item => item.PlanId == plan.Id)
            .ToListAsync();
        foreach (var batch in batches.Where(item => ShouldCancelBatchWithPlan(item.Status)))
        {
            batch.Status = AiProjectWorkBatchStatus.Cancelled;
            batch.UpdateTime = now;
            await _batchRepository.UpdateAsync(batch);
        }

        var tasks = await _taskRepository.GetQueryable().Where(item => item.PlanId == plan.Id).ToListAsync();
        foreach (var task in tasks.Where(item => item.Status is not (AiProjectWorkTaskStatus.Completed or AiProjectWorkTaskStatus.Cancelled)))
        {
            task.Status = AiProjectWorkTaskStatus.Cancelled;
            task.UpdateTime = now;
            await _taskRepository.UpdateAsync(task);
        }
    }

    private async Task<List<AiProjectWorkTask>> BatchTasksAsync(long batchId)
    {
        var links = await _batchTaskRepository.GetQueryable().Where(item => item.BatchId == batchId).OrderBy(item => item.SortOrder).ToListAsync();
        if (links.Count == 0)
            return [];
        var ids = links.Select(item => item.TaskId).ToList();
        return await _taskRepository.GetQueryable().Where(item => ids.Contains(item.Id)).ToListAsync();
    }

    private async Task<AiProjectWorkBatch> LoadOwnedBatchAsync(long batchId)
        => await _batchRepository.GetQueryable().Where(item => item.Id == batchId).FirstAsync()
           ?? throw new KeyNotFoundException($"Work batch '{batchId}' was not found.");

    private async Task RecalculateReadinessAsync(long planId, DateTime now)
    {
        var tasks = await _taskRepository.GetQueryable().Where(item => item.PlanId == planId).ToListAsync();
        var dependencies = await _dependencyRepository.GetQueryable().Where(item => item.PlanId == planId).ToListAsync();
        var byId = tasks.ToDictionary(item => item.Id);
        foreach (var task in tasks)
        {
            if (task.Status is AiProjectWorkTaskStatus.Completed or AiProjectWorkTaskStatus.Cancelled
                or AiProjectWorkTaskStatus.Failed or AiProjectWorkTaskStatus.Rejected
                or AiProjectWorkTaskStatus.Applying or AiProjectWorkTaskStatus.WaitingApproval)
                continue;
            var unmet = dependencies
                .Where(item => item.TaskId == task.Id)
                .Select(item => byId.TryGetValue(item.DependsOnTaskId, out var dep) ? dep : null)
                .Where(item => item != null && item.Status != AiProjectWorkTaskStatus.Completed)
                .ToList();
            var next = unmet.Count == 0 ? AiProjectWorkTaskStatus.Ready : AiProjectWorkTaskStatus.Blocked;
            if (task.Status != next)
            {
                task.Status = next;
                task.UpdateTime = now;
                await _taskRepository.UpdateAsync(task);
            }
        }
        var stages = await _stageRepository.GetQueryable().Where(item => item.PlanId == planId).ToListAsync();
        foreach (var stage in stages)
        {
            var stageTasks = tasks.Where(item => item.StageId == stage.Id).ToList();
            var nextStatus = stageTasks.Count > 0 && stageTasks.All(item => item.Status == AiProjectWorkTaskStatus.Completed)
                ? AiProjectWorkStageStatus.Completed
                : stageTasks.Any(item => item.Status == AiProjectWorkTaskStatus.Failed)
                    ? AiProjectWorkStageStatus.Failed
                    : stageTasks.Any(item => item.Status is AiProjectWorkTaskStatus.Applying or AiProjectWorkTaskStatus.WaitingApproval)
                        ? AiProjectWorkStageStatus.InProgress
                        : AiProjectWorkStageStatus.Pending;
            if (stage.Status != nextStatus)
            {
                stage.Status = nextStatus;
                stage.UpdateTime = now;
                await _stageRepository.UpdateAsync(stage);
            }
        }
    }

    private async Task<AiProjectWorkPlanDto> BuildProjectionAsync(AiProjectWorkPlan plan)
    {
        var stages = await _stageRepository.GetQueryable().Where(item => item.PlanId == plan.Id).OrderBy(item => item.SortOrder).ToListAsync();
        var tasks = await _taskRepository.GetQueryable().Where(item => item.PlanId == plan.Id).OrderBy(item => item.SortOrder).ToListAsync();
        var dependencies = await _dependencyRepository.GetQueryable().Where(item => item.PlanId == plan.Id).ToListAsync();
        var taskById = tasks.ToDictionary(item => item.Id);
        AiProjectWorkBatchDto? activeBatch = null;
        if (plan.ActiveBatchId.HasValue)
        {
            var batch = await _batchRepository.GetQueryable().Where(item => item.Id == plan.ActiveBatchId.Value).FirstAsync();
            if (batch != null)
                activeBatch = await MapBatchAsync(batch);
        }

        var stageDtos = stages.Select(stage => new AiProjectWorkStageDto
        {
            Id = stage.Id,
            StageKey = stage.StageKey,
            Kind = stage.Kind.ToString(),
            Title = stage.Title,
            Summary = stage.Summary,
            Status = stage.Status.ToString(),
            SortOrder = stage.SortOrder,
            GenerationMode = stage.GenerationMode,
            BuildAfterStage = stage.BuildAfterStage,
            CompletedTasks = tasks.Count(item => item.StageId == stage.Id && item.Status == AiProjectWorkTaskStatus.Completed),
            TotalTasks = tasks.Count(item => item.StageId == stage.Id),
            Tasks = tasks.Where(item => item.StageId == stage.Id).Select(task => MapTask(task, tasks, dependencies, taskById)).ToList()
        }).ToList();

        var actions = new List<string> { "cancel", "update" };
        if (plan.Status == AiProjectWorkPlanStatus.AwaitingConfirmation)
            actions.Add("confirm");
        if (!plan.ActiveBatchId.HasValue && tasks.Any(item => item.Status == AiProjectWorkTaskStatus.Ready))
            actions.Add("proposeReadyBatch");

        return new AiProjectWorkPlanDto
        {
            Id = plan.Id,
            ConversationId = plan.ConversationId,
            ProjectId = plan.ProjectId,
            PlanKey = plan.PlanKey,
            Title = plan.Title,
            Summary = plan.Summary,
            Status = plan.Status.ToString(),
            StructureVersion = plan.StructureVersion,
            RepairMode = plan.RepairMode.ToString(),
            ActiveBatchId = plan.ActiveBatchId,
            StartedAt = plan.StartedAt,
            CompletedAt = plan.CompletedAt,
            LastActivityAt = plan.LastActivityAt,
            CompletedTasks = tasks.Count(item => item.Status == AiProjectWorkTaskStatus.Completed),
            TotalTasks = tasks.Count,
            WorkflowSchemaVersion = "1.0",
            ConfirmationStatus = plan.Status == AiProjectWorkPlanStatus.AwaitingConfirmation ? "Pending" : "Confirmed",
            RequiresConfirmation = plan.Status == AiProjectWorkPlanStatus.AwaitingConfirmation,
            ContentHash = ComputeContentHash(stages, tasks, dependencies),
            Stages = stageDtos,
            ActiveBatch = activeBatch,
            AvailableActions = actions,
            RepairPolicy = new AiProjectRepairPolicyDto
            {
                Mode = plan.RepairMode.ToString(),
                // Disabled 2026-10-20: the agent never touches CodeMaster source code.
                SourceRepairEnabled = false,
                SourceRepairAuthorized = false,
                Message = "Source repair is disabled: the agent never inspects or patches CodeMaster source code."
            }
        };
    }

    private static AiProjectWorkTaskDto MapTask(
        AiProjectWorkTask task,
        List<AiProjectWorkTask> allTasks,
        List<AiProjectWorkTaskDependency> dependencies,
        Dictionary<long, AiProjectWorkTask> taskById)
    {
        var dependsOnIds = dependencies.Where(item => item.TaskId == task.Id).Select(item => item.DependsOnTaskId).ToList();
        var dependsOnKeys = dependsOnIds
            .Select(id => taskById.TryGetValue(id, out var dep) ? dep.TaskKey : string.Empty)
            .Where(key => key.Length > 0)
            .ToList();
        var unmet = dependsOnIds
            .Select(id => taskById.TryGetValue(id, out var dep) ? dep : null)
            .Where(dep => dep != null && dep.Status != AiProjectWorkTaskStatus.Completed)
            .Select(dep => dep!.TaskKey)
            .ToList();
        JsonElement? standardJson = null;
        if (!string.IsNullOrWhiteSpace(task.DefinitionJson))
        {
            try
            {
                using var document = JsonDocument.Parse(task.DefinitionJson);
                standardJson = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                standardJson = null;
            }
        }
        var actions = new List<string>();
        if (task.Status == AiProjectWorkTaskStatus.Failed)
            actions.Add("retry");
        return new AiProjectWorkTaskDto
        {
            Id = task.Id,
            StageId = task.StageId,
            TaskKey = task.TaskKey,
            Title = task.Title,
            Summary = task.Summary,
            OperationKind = task.OperationKind,
            Status = task.Status.ToString(),
            ExecutionLocation = task.ExecutionLocation.ToString(),
            SafetyClass = task.SafetyClass.ToString(),
            PayloadHash = task.PayloadHash,
            SemanticReferences = DeserializeReferences(task.SemanticReferencesJson),
            DependsOnTaskIds = dependsOnIds,
            DependsOnTaskKeys = dependsOnKeys,
            UnmetDependencyTaskKeys = unmet,
            SortOrder = task.SortOrder,
            RetryLimit = task.RetryLimit,
            AttemptCount = task.AttemptCount,
            LastFailureCategory = task.LastFailureCategory == AiProjectFailureCategory.None ? null : task.LastFailureCategory.ToString(),
            LastDiagnosis = task.LastDiagnosis,
            RequiresGeneration = task.RequiresGeneration,
            RequiresBuild = task.RequiresBuild,
            StandardJson = standardJson,
            AvailableActions = actions
        };
    }

    private async Task<AiProjectWorkBatchDto> MapBatchAsync(AiProjectWorkBatch batch)
    {
        var links = await _batchTaskRepository.GetQueryable().Where(item => item.BatchId == batch.Id).OrderBy(item => item.SortOrder).ToListAsync();
        return new AiProjectWorkBatchDto
        {
            Id = batch.Id,
            PlanId = batch.PlanId,
            StageId = batch.StageId,
            ToolExecutionId = batch.ToolExecutionId,
            BatchKey = batch.BatchKey,
            Title = batch.Title,
            Summary = batch.Summary,
            Status = batch.Status.ToString(),
            StructureVersion = batch.StructureVersion,
            InputHash = batch.InputHash,
            ExecutionLocation = batch.ExecutionLocation.ToString(),
            SafetyClass = batch.SafetyClass.ToString(),
            AttemptCount = batch.AttemptCount,
            ApprovedAt = batch.ApprovedAt,
            CompletedAt = batch.CompletedAt,
            ErrorMessage = batch.ErrorMessage,
            TaskIds = links.Select(item => item.TaskId).ToList()
        };
    }

    private static List<string> DeserializeReferences(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string ComputeContentHash(
        List<AiProjectWorkStage> stages,
        List<AiProjectWorkTask> tasks,
        List<AiProjectWorkTaskDependency> dependencies)
    {
        var builder = new StringBuilder();
        foreach (var stage in stages.OrderBy(item => item.SortOrder))
            builder.Append(stage.StageKey).Append(':').Append(stage.Kind).Append(':').Append(stage.SortOrder).Append(';');
        var taskById = tasks.ToDictionary(item => item.Id);
        foreach (var task in tasks.OrderBy(item => item.SortOrder))
        {
            builder.Append(task.TaskKey).Append(':').Append(task.OperationKind).Append(':')
                .Append(task.DefinitionJson ?? string.Empty).Append(':')
                .Append(task.RequiresGeneration ? 1 : 0).Append(task.RequiresBuild ? 1 : 0).Append(':');
            foreach (var dependency in dependencies.Where(item => item.TaskId == task.Id).OrderBy(item => item.DependsOnTaskId))
                builder.Append(taskById.TryGetValue(dependency.DependsOnTaskId, out var dep) ? dep.TaskKey : dependency.DependsOnTaskId.ToString()).Append(',');
            builder.Append(';');
        }
        return ComputeHash(builder.ToString());
    }

    private static string ComputeHash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty)));

    /// <summary>
    /// Batch key for one plan run, clamped to the <c>batch_key</c> column width.
    /// <para>
    /// The clamp must be driven by the key's own length: with a 19-digit snowflake id the natural
    /// key is 40 characters — <em>shorter</em> than a plausible-looking constant cap. An earlier
    /// version used <c>Substring(0, Math.Min(64, 24 + 20))</c>, i.e. 44, which threw
    /// "Index and length must refer to a location within the string" on every batch and left
    /// every confirmed plan unable to advance.
    /// </para>
    /// </summary>
    internal static string BuildBatchKey(long planId, DateTime nowUtc)
        => Truncate($"batch-{planId}-{nowUtc:yyyyMMddHHmmss}", 64);

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    // ============================== test-visible helpers ==============================

    internal static bool ShouldRetireEmptyPlanForTest(AiProjectWorkPlanStatus status, int taskCount)
        => taskCount == 0 &&
           status is AiProjectWorkPlanStatus.Draft or AiProjectWorkPlanStatus.AwaitingConfirmation;

    internal static bool IsStaleExecutionForTest(DateTime now, DateTime? lastActivity, int timeoutMinutes)
    {
        if (lastActivity == null)
            return false;
        return (now - lastActivity.Value).TotalMinutes >= timeoutMinutes;
    }

    internal static void EnsureAcyclic(IDictionary<string, List<string>> dependencies)
    {
        if (dependencies == null)
            return;
        var path = new List<string>();
        void Visit(string node)
        {
            var existing = path.FindIndex(item => string.Equals(item, node, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                var cycle = string.Join(" -> ", path.Skip(existing).Concat([node]));
                throw new ArgumentException($"Dependency cycle detected: {cycle}");
            }
            path.Add(node);
            if (dependencies.TryGetValue(node, out var deps))
            {
                foreach (var next in deps ?? [])
                    Visit(next);
            }
            path.RemoveAt(path.Count - 1);
        }
        foreach (var node in dependencies.Keys.ToList())
            Visit(node);
    }

    internal sealed record SemanticResolutionResult(
        List<AiProjectSemanticReferenceDto> Resolved,
        List<string> Errors);

    internal static SemanticResolutionResult ResolveSemanticReferencesForTest(
        AiProjectOverviewDto blueprint,
        IEnumerable<string> references)
    {
        var resolved = new List<AiProjectSemanticReferenceDto>();
        var errors = new List<string>();
        foreach (var reference in references ?? [])
        {
            var value = reference?.Trim() ?? string.Empty;
            if (string.Equals(value, "project", StringComparison.OrdinalIgnoreCase))
            {
                resolved.Add(new AiProjectSemanticReferenceDto
                {
                    Reference = value,
                    Kind = "project",
                    ObjectId = blueprint.Project.Id,
                    ObjectName = blueprint.Project.ProjectName
                });
                continue;
            }
            var separator = value.IndexOf(':');
            var kind = separator > 0 ? value[..separator] : string.Empty;
            var name = separator > 0 ? value[(separator + 1)..] : value;
            AiProjectSemanticReferenceDto? hit = kind switch
            {
                var k when string.Equals(k, "module", StringComparison.OrdinalIgnoreCase) =>
                    blueprint.Modules.FirstOrDefault(item => string.Equals(item.ModuleName, name, StringComparison.OrdinalIgnoreCase)) is { } module
                        ? new AiProjectSemanticReferenceDto { Reference = value, Kind = "module", ObjectId = module.Id, ObjectName = module.ModuleName }
                        : null,
                var k when string.Equals(k, "entity", StringComparison.OrdinalIgnoreCase) =>
                    blueprint.Modules.SelectMany(item => item.Entities).FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)) is { } entity
                        ? new AiProjectSemanticReferenceDto { Reference = value, Kind = "entity", ObjectId = entity.Id, ObjectName = entity.Name }
                        : null,
                var k when string.Equals(k, "field", StringComparison.OrdinalIgnoreCase) => ResolveMember(blueprint, name, isField: true, reference: value),
                var k when string.Equals(k, "relation", StringComparison.OrdinalIgnoreCase) => ResolveMember(blueprint, name, isField: false, reference: value),
                _ => null
            };
            if (hit == null)
                errors.Add(value);
            else
                resolved.Add(hit);
        }
        return new SemanticResolutionResult(resolved, errors);
    }

    private static AiProjectSemanticReferenceDto? ResolveMember(AiProjectOverviewDto blueprint, string qualifiedName, bool isField, string reference)
    {
        var separator = qualifiedName.IndexOf('.');
        if (separator <= 0)
            return null;
        var entityName = qualifiedName[..separator];
        var memberName = qualifiedName[(separator + 1)..];
        var entity = blueprint.Modules.SelectMany(item => item.Entities)
            .FirstOrDefault(item => string.Equals(item.Name, entityName, StringComparison.OrdinalIgnoreCase));
        if (entity == null)
            return null;
        if (isField)
        {
            var field = entity.Fields.FirstOrDefault(item => string.Equals(item.Name, memberName, StringComparison.OrdinalIgnoreCase));
            return field == null ? null : new AiProjectSemanticReferenceDto { Reference = reference, Kind = "field", ObjectId = field.Id, ObjectName = field.Name };
        }
        var relation = entity.Relations.FirstOrDefault(item => string.Equals(item.RelationName, memberName, StringComparison.OrdinalIgnoreCase));
        return relation == null ? null : new AiProjectSemanticReferenceDto { Reference = reference, Kind = "relation", ObjectId = relation.Id, ObjectName = relation.RelationName };
    }

    internal static string? GetDefinitionValidationErrorForTest(AiProjectWorkTask task)
    {
        if (task.DefinitionPayload is { Length: > 0 })
        {
            try
            {
                var json = AiToolPayloadCodec.DecodeBinary(task.DefinitionPayload);
                using var document = JsonDocument.Parse(json);
                return null;
            }
            catch (Exception)
            {
                return "The task stores an invalid stored definition payload.";
            }
        }
        if (!string.IsNullOrWhiteSpace(task.DefinitionJson))
        {
            try
            {
                var json = AiToolPayloadCodec.Decode(task.DefinitionJson!);
                using var document = JsonDocument.Parse(json);
                return null;
            }
            catch (Exception)
            {
                return "The task stores an invalid stored definition payload.";
            }
        }
        return "The task is missing an executable definition.";
    }

    internal static List<AiProjectWorkChangeDto> ExtractNormalizedChangesForTest(
        JsonElement document,
        string operationKind,
        AiProjectBlueprintDto blueprint,
        string stageKey)
        => ExtractNormalizedChanges(document, operationKind, blueprint);

    internal static List<AiProjectWorkChangeDto> ExtractNormalizedChanges(
        JsonElement? definition,
        string operationKind,
        AiProjectBlueprintDto? blueprint)
    {
        var changes = new List<AiProjectWorkChangeDto>();
        if (definition is not { ValueKind: JsonValueKind.Object } element)
            return changes;

        if (element.TryGetProperty("changes", out var declared) && declared.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in declared.EnumerateArray())
            {
                changes.Add(new AiProjectWorkChangeDto
                {
                    ChangeId = GetString(item, "changeId") ?? string.Empty,
                    Operation = GetString(item, "operation") ?? string.Empty,
                    ResourceType = GetString(item, "resourceType") ?? string.Empty,
                    Target = CloneObject(item, "target"),
                    Values = CloneObject(item, "values"),
                    Differences = CloneObject(item, "differences"),
                    Reason = GetString(item, "reason")
                });
            }
            return changes;
        }

        if (!string.Equals(operationKind, "project_change_set", StringComparison.OrdinalIgnoreCase) || blueprint == null)
            return changes;

        var entities = blueprint.Modules.SelectMany(item => item.Entities).ToList();
        if (element.TryGetProperty("entityUpdates", out var updates) && updates.ValueKind == JsonValueKind.Array)
        {
            foreach (var update in updates.EnumerateArray())
            {
                if (!update.TryGetProperty("entityId", out var entityIdElement))
                    continue;
                var entity = entities.FirstOrDefault(item => item.Id == entityIdElement.GetInt64());
                if (entity == null)
                    continue;
                if (update.TryGetProperty("newFields", out var newFields) && newFields.ValueKind == JsonValueKind.Array)
                {
                    foreach (var field in newFields.EnumerateArray())
                    {
                        var fieldName = GetString(field, "name") ?? string.Empty;
                        changes.Add(new AiProjectWorkChangeDto
                        {
                            ChangeId = $"field:add:{entity.Name}.{fieldName}",
                            Operation = "add",
                            ResourceType = "field",
                            Target = JsonSerializer.SerializeToElement(new { entityName = entity.Name, fieldName }),
                            Values = field.Clone()
                        });
                    }
                }
                if (update.TryGetProperty("updatedFields", out var updatedFields) && updatedFields.ValueKind == JsonValueKind.Array)
                {
                    foreach (var field in updatedFields.EnumerateArray())
                    {
                        if (!field.TryGetProperty("fieldId", out var fieldIdElement))
                            continue;
                        var fieldId = fieldIdElement.GetInt64();
                        var existing = entity.Fields.FirstOrDefault(item => item.Id == fieldId);
                        var differences = new List<object>();
                        if (existing != null)
                        {
                            AddDifference(differences, "description", existing.Description, GetString(field, "description"));
                            AddDifference(differences, "businessDescription", existing.BusinessDescription, GetString(field, "businessDescription"));
                            AddDifference(differences, "formControlType", existing.FormControlType, GetString(field, "formControlType"));
                            AddDifference(differences, "dataType", existing.DataType, GetString(field, "dataType"));
                        }
                        changes.Add(new AiProjectWorkChangeDto
                        {
                            ChangeId = $"field:update:{entity.Name}.{fieldId}",
                            Operation = "update",
                            ResourceType = "field",
                            Target = JsonSerializer.SerializeToElement(new
                            {
                                entityName = entity.Name,
                                fieldName = existing?.Name ?? string.Empty,
                                displayName = existing?.Description ?? string.Empty,
                                businessDescription = existing?.BusinessDescription
                            }),
                            Values = field.Clone(),
                            Differences = JsonSerializer.SerializeToElement(differences)
                        });
                    }
                }
                if (update.TryGetProperty("deletedFieldIds", out var deletedIds) && deletedIds.ValueKind == JsonValueKind.Array)
                {
                    foreach (var fieldIdElement in deletedIds.EnumerateArray())
                    {
                        var fieldId = fieldIdElement.GetInt64();
                        var existing = entity.Fields.FirstOrDefault(item => item.Id == fieldId);
                        changes.Add(new AiProjectWorkChangeDto
                        {
                            ChangeId = $"field:delete:{entity.Name}.{fieldId}",
                            Operation = "delete",
                            ResourceType = "field",
                            Target = JsonSerializer.SerializeToElement(new
                            {
                                entityName = entity.Name,
                                fieldName = existing?.Name ?? string.Empty,
                                displayName = existing?.Description ?? string.Empty,
                                businessDescription = existing?.BusinessDescription
                            })
                        });
                    }
                }
            }
        }
        return changes;
    }

    private static void AddDifference(List<object> differences, string property, string? before, string? after)
    {
        if (after == null || string.Equals(before, after, StringComparison.Ordinal))
            return;
        differences.Add(new { property, before = before ?? string.Empty, after });
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static JsonElement? CloneObject(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) ? value.Clone() : null;

    internal static void ValidateSevenStageWorkflowForTest(UpsertAiProjectWorkPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stages = request.Stages.OrderBy(item => item.SortOrder ?? 0).ToList();
        var known = stages.Where(item => SevenStageOrder.Contains(item.Kind, StringComparer.OrdinalIgnoreCase)).ToList();
        var orderIndices = known.Select(item => Array.FindIndex(SevenStageOrder, name => string.Equals(name, item.Kind, StringComparison.OrdinalIgnoreCase))).ToList();
        for (var i = 1; i < orderIndices.Count; i++)
        {
            if (orderIndices[i] <= orderIndices[i - 1])
                throw new ArgumentException("The standard JSON workflow stages must be ordered as EntityFields, Views, Relations, FieldControls, ComplexPages, ConsistencyCheck, GenerateCode.");
        }
        foreach (var stage in stages)
        {
            var isGenerateStage = string.Equals(stage.Kind, "GenerateCode", StringComparison.OrdinalIgnoreCase);
            foreach (var task in stage.Tasks)
            {
                if (!isGenerateStage && (task.RequiresGeneration || task.RequiresBuild))
                    throw new ArgumentException($"Stage '{stage.Kind}' cannot generate or build; only the final GenerateCode stage may request generation or build.");
                if (string.Equals(stage.Kind, "ConsistencyCheck", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(task.OperationKind, "project_consistency_check", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("The ConsistencyCheck stage must use the project_consistency_check operation adapter.");
                if (isGenerateStage && !string.Equals(task.OperationKind, "generate_project", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("The GenerateCode stage must use the generate_project operation adapter.");
            }
        }
        var generateIndex = stages.FindIndex(item => string.Equals(item.Kind, "GenerateCode", StringComparison.OrdinalIgnoreCase));
        if (generateIndex >= 0 && generateIndex != stages.Count - 1)
            throw new ArgumentException("The GenerateCode stage must be the final stage of the workflow.");
    }
}
