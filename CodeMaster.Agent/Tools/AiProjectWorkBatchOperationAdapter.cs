using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Services;
using CodeMaster.Core.Extensibility;
using CodeMaster.Domain.Entities.Ai;

namespace CodeMaster.Agent.Tools;

public sealed class AiProjectWorkBatchOperationDisclosure
{
    public string OperationKind { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public int EntityCount { get; set; }
    public int FieldCount { get; set; }
    public int RelationCount { get; set; }
    public List<string> Warnings { get; set; } = [];
}

public interface ICodeMasterAgentBatchOperationAdapter : ICodeMasterExtension
{
    string OperationKind { get; }
    Task<AiProjectWorkBatchOperationDisclosure> ValidateAsync(long projectId, AiProjectWorkBatchOperation operation);
    Task DemandPermissionAsync(long projectId, AiProjectWorkBatchOperation operation);
    Task<object> ExecuteAsync(long projectId, AiProjectWorkBatchOperation operation);
}

internal sealed class ProjectChangeSetBatchOperationAdapter : ICodeMasterAgentBatchOperationAdapter
{
    private readonly ICodeMasterProjectChangeSetService _changeSetService;
    private readonly IProjectChangeSetDefinitionNormalizer _definitionNormalizer;
    private readonly IAiAuthorizationService _authorizationService;

    public ProjectChangeSetBatchOperationAdapter(
        ICodeMasterProjectChangeSetService changeSetService,
        IProjectChangeSetDefinitionNormalizer definitionNormalizer,
        IAiAuthorizationService authorizationService)
    {
        _changeSetService = changeSetService;
        _definitionNormalizer = definitionNormalizer;
        _authorizationService = authorizationService;
    }

    public string OperationKind => "project_change_set";

    public async Task<AiProjectWorkBatchOperationDisclosure> ValidateAsync(
        long projectId,
        AiProjectWorkBatchOperation operation)
    {
        var proposal = await DeserializeAsync(projectId, operation);
        var validation = await _changeSetService.ValidateAsync(projectId, proposal);
        if (!validation.IsValid)
            throw new InvalidOperationException($"Task '{operation.TaskKey}' is invalid: {string.Join(" ", validation.Errors)}");
        return new AiProjectWorkBatchOperationDisclosure
        {
            OperationKind = OperationKind,
            Summary = proposal.Summary,
            EntityCount = validation.EntityCount + validation.UpdatedEntityCount + validation.DeletedEntityCount,
            FieldCount = validation.FieldCount,
            RelationCount = validation.RelationCount,
            Warnings = validation.Warnings
        };
    }

    public async Task DemandPermissionAsync(long projectId, AiProjectWorkBatchOperation operation)
    {
        var proposal = await DeserializeAsync(projectId, operation);
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
    }

    public async Task<object> ExecuteAsync(long projectId, AiProjectWorkBatchOperation operation)
    {
        var proposal = await DeserializeAsync(projectId, operation);
        // Planned tasks reach a change set through this adapter, so the known-issue repairs have to
        // run here as well — running them only in the standalone approval tool leaves every planned
        // task hitting the raw model output.
        var repairs = await _changeSetService.RepairKnownIssuesAsync(projectId, proposal);
        var result = await _changeSetService.ExecuteAsync(projectId, proposal);
        result.AutomaticRepairs.AddRange(repairs);
        return result;
    }

    private async Task<ProjectChangeSetProposal> DeserializeAsync(
        long projectId,
        AiProjectWorkBatchOperation operation)
    {
        if (string.IsNullOrWhiteSpace(operation.DefinitionJson))
            throw new InvalidOperationException($"Task '{operation.TaskKey}' has no project change-set definition.");
        return await _definitionNormalizer.NormalizeAsync(projectId, operation.TaskKey, operation.DefinitionJson);
    }
}
