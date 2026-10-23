using System.Text.Json;
using System.Text.Json.Serialization;
using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Serialization;
using CodeMaster.Agent.Services;
using CodeMaster.Agent.Tools;
using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Application.Services.CodeGen;

namespace CodeMaster.UiComposition.Agent;

/// <summary>
/// Connects the durable ComplexPages work-plan stage to the structured complex-page
/// planner. This keeps page metadata, approval, generation, and work-task completion
/// on the same execution path.
/// </summary>
public sealed class ComplexPageBatchOperationAdapter : ICodeMasterAgentBatchOperationAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly IComplexPageAgentPlanningService _planningService;
    private readonly IAiAuthorizationService _authorizationService;
    private readonly IProjectModuleService _moduleService;
    private readonly IModuleEntityService _entityService;

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(AgentJsonSerializer.Options);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public ComplexPageBatchOperationAdapter(
        IComplexPageAgentPlanningService planningService,
        IAiAuthorizationService authorizationService,
        IProjectModuleService moduleService,
        IModuleEntityService entityService)
    {
        _planningService = planningService;
        _authorizationService = authorizationService;
        _moduleService = moduleService;
        _entityService = entityService;
    }

    public string OperationKind => "complex_page";

    public async Task<AiProjectWorkBatchOperationDisclosure> ValidateAsync(
        long projectId,
        AiProjectWorkBatchOperation operation)
    {
        var plan = await DeserializeAsync(projectId, operation);
        var validation = await _planningService.ValidateAsync(projectId, plan);
        if (!validation.IsValid)
            throw new InvalidOperationException(
                $"Task '{operation.TaskKey}' is invalid: {string.Join(" ", validation.Errors)}");

        var definition = plan.Definition;
        return new AiProjectWorkBatchOperationDisclosure
        {
            OperationKind = OperationKind,
            Summary = plan.Summary.Trim(),
            EntityCount = definition.DataSources.Count(source => source.EntityId.HasValue),
            FieldCount = definition.Bindings.Count(binding => binding.FieldId.HasValue),
            Warnings = validation.Warnings
        };
    }

    public async Task DemandPermissionAsync(long projectId, AiProjectWorkBatchOperation operation)
    {
        var plan = await DeserializeAsync(projectId, operation);
        await _authorizationService.DemandAnyAsync(
            plan.ExistingPageId.HasValue ? "codegen:projectpage:update" : "codegen:projectpage:create");
    }

    public async Task<object> ExecuteAsync(long projectId, AiProjectWorkBatchOperation operation)
    {
        var plan = await DeserializeAsync(projectId, operation);
        var applied = await _planningService.ApplyAsync(projectId, plan);
        var result = new ComplexPageBatchExecutionResult
        {
            ProjectId = applied.ProjectId,
            PageId = applied.PageId,
            Created = applied.Created,
            Summary = applied.Summary
        };
        return result;
    }

    private async Task<ComplexPageAgentPlanDto> DeserializeAsync(long projectId, AiProjectWorkBatchOperation operation)
    {
        if (string.IsNullOrWhiteSpace(operation.DefinitionJson))
            throw new InvalidOperationException($"Task '{operation.TaskKey}' has no complex-page payload.");
        var plan = JsonSerializer.Deserialize<ComplexPageAgentPlanDto>(operation.DefinitionJson, JsonOptions)
            ?? throw new InvalidOperationException($"Task '{operation.TaskKey}' has an invalid complex-page payload.");

        if (plan.Definition.ProjectId <= 0)
            plan.Definition.ProjectId = projectId;
        else if (plan.Definition.ProjectId != projectId)
            throw new InvalidOperationException("Complex page definition escapes the current project boundary.");

        if (plan.Definition.ModuleId <= 0 && !string.IsNullOrWhiteSpace(plan.Definition.ModuleName))
        {
            var module = (await _moduleService.GetByProjectIdAsync(projectId))
                .FirstOrDefault(item => string.Equals(item.ModuleName, plan.Definition.ModuleName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (module != null)
                plan.Definition.ModuleId = module.Id;
        }

        if (plan.Definition.ModuleId > 0)
        {
            var entities = await _entityService.GetByModuleIdAsync(plan.Definition.ModuleId);
            foreach (var source in plan.Definition.DataSources)
            {
                if (source.EntityId.HasValue || string.IsNullOrWhiteSpace(source.EntityName))
                    continue;
                var entity = entities.FirstOrDefault(item => string.Equals(item.Name, source.EntityName.Trim(), StringComparison.OrdinalIgnoreCase));
                if (entity == null)
                    throw new InvalidOperationException($"Complex page data source '{source.SourceKey}' references unknown entity '{source.EntityName}'.");
                source.EntityId = entity.Id;
                source.ServiceName = string.IsNullOrWhiteSpace(source.ServiceName) ? $"{entity.Name}Service" : source.ServiceName;
                source.MethodName = string.IsNullOrWhiteSpace(source.MethodName) ? "GetListAsync" : source.MethodName;
                source.DtoName = string.IsNullOrWhiteSpace(source.DtoName) ? $"{entity.Name}Dto" : source.DtoName;
            }
        }

        return plan;
    }
}

public sealed class ComplexPageBatchExecutionResult : IAgentClientActionResult
{
    public long ProjectId { get; set; }
    public long PageId { get; set; }
    public bool Created { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<AiClientActionDto> ClientActions { get; set; } = [];
    public bool HasClientActions => ClientActions.Count > 0;
}
