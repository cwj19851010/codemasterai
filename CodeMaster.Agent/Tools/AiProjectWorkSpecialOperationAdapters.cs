using System.Text.Json;
using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Serialization;
using CodeMaster.Agent.Services;
using CodeMaster.Application.Services.CodeGen;

namespace CodeMaster.Agent.Tools;

internal sealed class ProjectConsistencyCheckBatchOperationAdapter : ICodeMasterAgentBatchOperationAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = AgentJsonSerializer.Options;
    private readonly ICodeMasterProjectBlueprintService _blueprintService;

    public ProjectConsistencyCheckBatchOperationAdapter(ICodeMasterProjectBlueprintService blueprintService)
        => _blueprintService = blueprintService;

    public string OperationKind => "project_consistency_check";

    public async Task<AiProjectWorkBatchOperationDisclosure> ValidateAsync(long projectId, AiProjectWorkBatchOperation operation)
    {
        var input = Deserialize(operation);
        var blueprint = await _blueprintService.GetProjectAsync(projectId);
        var check = BuildCheck(projectId, blueprint, input);

        return new AiProjectWorkBatchOperationDisclosure
        {
            OperationKind = OperationKind,
            Summary = input.Summary,
            EntityCount = check.EntityCount,
            FieldCount = check.FieldCount,
            RelationCount = check.RelationCount,
            Warnings = check.Errors.Concat(check.Warnings).ToList()
        };
    }

    public Task DemandPermissionAsync(long projectId, AiProjectWorkBatchOperation operation)
        => Task.CompletedTask;

    public async Task<object> ExecuteAsync(long projectId, AiProjectWorkBatchOperation operation)
    {
        var input = Deserialize(operation);
        var blueprint = await _blueprintService.GetProjectAsync(projectId);
        var result = BuildCheck(projectId, blueprint, input);
        if (!result.IsValid)
            throw new InvalidOperationException($"Task '{operation.TaskKey}' consistency check failed: {string.Join(" ", result.Errors)}");
        return result;
    }

    private static AiProjectConsistencyCheckRequest Deserialize(AiProjectWorkBatchOperation operation)
        => string.IsNullOrWhiteSpace(operation.DefinitionJson)
            ? throw new InvalidOperationException($"Task '{operation.TaskKey}' has no consistency-check definition.")
            : JsonSerializer.Deserialize<AiProjectConsistencyCheckRequest>(operation.DefinitionJson, JsonOptions)
              ?? throw new InvalidOperationException($"Task '{operation.TaskKey}' has an invalid consistency-check definition.");

    private static AiProjectConsistencyCheckResult BuildCheck(
        long projectId,
        AiProjectBlueprintDto blueprint,
        AiProjectConsistencyCheckRequest input)
    {
        var result = new AiProjectConsistencyCheckResult
        {
            ProjectId = projectId,
            ModuleCount = blueprint.Modules.Count,
            EntityCount = blueprint.Modules.Sum(item => item.Entities.Count),
            FieldCount = blueprint.Modules.SelectMany(item => item.Entities).Sum(item => item.Fields.Count),
            RelationCount = blueprint.Modules.SelectMany(item => item.Entities).Sum(item => item.Relations.Count)
        };

        if (input.RequireSevenStages)
            result.Warnings.Add("The stage structure is validated by the parent work plan dependencies; this task validates the current project metadata snapshot.");

        var duplicateEntities = blueprint.Modules
            .SelectMany(item => item.Entities)
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);
        foreach (var duplicate in duplicateEntities)
            result.Errors.Add($"Duplicate entity name '{duplicate}'.");

        var entities = blueprint.Modules.SelectMany(item => item.Entities).ToList();
        var entitiesById = entities.ToDictionary(item => item.Id);
        var entitiesByName = entities
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var entity in entities)
        {
            var duplicateFields = entity.Fields
                .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key);
            foreach (var duplicate in duplicateFields)
                result.Errors.Add($"Entity '{entity.Name}' contains duplicate field '{duplicate}'.");

            var fieldNames = entity.Fields.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var relation in entity.Relations)
            {
                if (!fieldNames.Contains(relation.SourceField) && !string.IsNullOrWhiteSpace(relation.SourceField))
                    result.Errors.Add($"Relation '{entity.Name}.{relation.RelationName}' references missing source field '{relation.SourceField}'.");
                if (!entitiesById.TryGetValue(relation.TargetEntityId, out var targetEntity))
                {
                    result.Errors.Add($"Relation '{entity.Name}.{relation.RelationName}' references missing target entity '{relation.TargetEntityId}'.");
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(relation.TargetField) &&
                    !targetEntity.Fields.Any(field => string.Equals(field.Name, relation.TargetField, StringComparison.OrdinalIgnoreCase)) &&
                    !string.Equals(relation.TargetField, "Id", StringComparison.OrdinalIgnoreCase))
                    result.Errors.Add($"Relation '{entity.Name}.{relation.RelationName}' references missing target field '{targetEntity.Name}.{relation.TargetField}'.");
            }

            foreach (var field in entity.Fields.Where(item => !string.IsNullOrWhiteSpace(item.RelatedEntityName)))
            {
                if (SystemReferenceEntityCatalog.TryGet(field.RelatedEntityName!, out var builtIn))
                {
                    var builtInValueField = string.IsNullOrWhiteSpace(field.RelatedEntityIdField) ? "Id" : field.RelatedEntityIdField;
                    if (!builtIn.Fields.Any(item => string.Equals(item.Name, builtInValueField, StringComparison.OrdinalIgnoreCase)))
                        result.Errors.Add($"Field '{entity.Name}.{field.Name}' references missing built-in value field '{builtIn.Name}.{builtInValueField}'.");
                    continue;
                }
                if (!entitiesByName.TryGetValue(field.RelatedEntityName!, out var relatedEntity))
                {
                    result.Errors.Add($"Field '{entity.Name}.{field.Name}' references missing related entity '{field.RelatedEntityName}'.");
                    continue;
                }
                var valueField = string.IsNullOrWhiteSpace(field.RelatedEntityIdField) ? "Id" : field.RelatedEntityIdField;
                if (!relatedEntity.Fields.Any(item => string.Equals(item.Name, valueField, StringComparison.OrdinalIgnoreCase)) &&
                    !string.Equals(valueField, "Id", StringComparison.OrdinalIgnoreCase))
                    result.Errors.Add($"Field '{entity.Name}.{field.Name}' references missing related value field '{relatedEntity.Name}.{valueField}'.");
            }
        }

        return result;
    }
}

internal sealed class GenerateProjectBatchOperationAdapter : ICodeMasterAgentBatchOperationAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = AgentJsonSerializer.Options;
    private readonly IAiAuthorizationService _authorizationService;

    public GenerateProjectBatchOperationAdapter(IAiAuthorizationService authorizationService)
        => _authorizationService = authorizationService;

    public string OperationKind => "generate_project";

    public Task<AiProjectWorkBatchOperationDisclosure> ValidateAsync(long projectId, AiProjectWorkBatchOperation operation)
    {
        var input = Deserialize(operation);
        if (!string.Equals(input.GenerationMode, "Full", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(input.GenerationMode, "Incremental", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(input.GenerationMode, "Smart", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Task '{operation.TaskKey}' GenerationMode must be Full, Incremental, or Smart.");

        return Task.FromResult(new AiProjectWorkBatchOperationDisclosure
        {
            OperationKind = OperationKind,
            Summary = input.Summary,
            Warnings = ["The agent only writes metadata. This legacy generation task performs no code generation, build, or test; the user triggers those manually from the project page."]
        });
    }

    public Task DemandPermissionAsync(long projectId, AiProjectWorkBatchOperation operation)
    {
        // No codegen or build permission is required any more: this adapter only records that
        // the metadata is ready, and never queues a generation, build, or test action.
        return Task.CompletedTask;
    }

    public Task<object> ExecuteAsync(long projectId, AiProjectWorkBatchOperation operation)
    {
        var input = Deserialize(operation);
        // The agent never generates, builds, or tests the project. Generating the source code is
        // a manual action the user triggers from the project page (full or incremental), so this
        // legacy task is now a metadata-only no-op that just reports readiness.
        return Task.FromResult<object>(new GenerateProjectWorkResult
        {
            ProjectId = projectId,
            Summary = string.IsNullOrWhiteSpace(input.Summary)
                ? "元数据已就绪，请在项目页面手动生成代码"
                : input.Summary
        });
    }

    private static GenerateProjectWorkRequest Deserialize(AiProjectWorkBatchOperation operation)
        => string.IsNullOrWhiteSpace(operation.DefinitionJson)
            ? throw new InvalidOperationException($"Task '{operation.TaskKey}' has no generate-project definition.")
            : JsonSerializer.Deserialize<GenerateProjectWorkRequest>(operation.DefinitionJson, JsonOptions)
              ?? throw new InvalidOperationException($"Task '{operation.TaskKey}' has an invalid generate-project definition.");
}
